using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Npgsql;
using Zentriq.Application.Licensing;
using Zentriq.LicenseServer.Endpoints;
using Zentriq.LicenseServer.Services;

namespace Zentriq.LicenseServer;

public partial class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Commands: (none) | serve  — run the service;  keygen --out <dir> [--key-id <id>];  key-info;  migrate;
        //           create-admin --email <e> --name <n>  (the passphrase is read from ZLS_ADMIN_PASSPHRASE or standard input).
        var command = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : "serve";
        var rest = args.Length > 0 && !args[0].StartsWith('-') ? args[1..] : args;
        if (command == "keygen") return Keygen(rest);

        var app = Build(rest);
        if (command is "serve" or "key-info")
        {
            // Load and self-test the signing key before anything else (wrong passphrase or file: stop here, clearly).
            try
            {
                var key = app.Services.GetRequiredService<SigningKey>();
                app.Logger.LogInformation("Signing key {KeyId} loaded and self-tested. Fingerprint {Fingerprint}", key.KeyId, key.Fingerprint);
                if (command == "key-info") { Console.WriteLine($"Key id:      {key.KeyId}\nFingerprint: {key.Fingerprint}\n{key.PublicPem}"); return 0; }
            }
            catch (InvalidOperationException ex) { Console.Error.WriteLine(ex.Message); return 3; }
        }
        var ds = app.Services.GetRequiredService<NpgsqlDataSource>();
        var applied = await MigrationRunner.MigrateAsync(ds);
        if (applied > 0) app.Logger.LogInformation("Applied {Count} database migration(s).", applied);

        switch (command)
        {
            case "migrate": return 0;
            case "create-admin": return await CreateAdminAsync(app, args);
            case "serve": break;
            default: Console.Error.WriteLine($"Unknown command '{command}'. Use serve, keygen, migrate or create-admin."); return 2;
        }
        await BootstrapAsync(app);
        await app.RunAsync();
        return 0;
    }

    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        var options = builder.Configuration.GetSection("LicenseServer").Get<LicenseServerOptions>() ?? new LicenseServerOptions();
        var cs = builder.Configuration.GetConnectionString("LicenseServer")
                 ?? throw new InvalidOperationException("ConnectionStrings:LicenseServer is not set.");

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(NpgsqlDataSource.Create(cs));
        builder.Services.AddSingleton(sp => SigningKey.Load(options));
        builder.Services.AddSingleton<LicenseIssuer>();
        builder.Services.AddSingleton<LicenseService>();
        builder.Services.AddSingleton<Audit>();
        builder.Services.AddSingleton<AdminStore>();
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNameCaseInsensitive = true);
        builder.Services.AddProblemDetails();

        builder.Services.AddAuthentication(AdminApi.CookieScheme).AddCookie(AdminApi.CookieScheme, o =>
        {
            o.Cookie.Name = "zls_session";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.ExpireTimeSpan = TimeSpan.FromHours(8);
            o.SlidingExpiration = true;
            o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
            o.Events.OnValidatePrincipal = async ctx =>
            {
                // Disabled accounts lose their sessions at once.
                var id = ctx.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                var store = ctx.HttpContext.RequestServices.GetRequiredService<AdminStore>();
                if (!Guid.TryParse(id, out var g) || await store.FindAsync(g, ctx.HttpContext.RequestAborted) is not { Disabled: false })
                    ctx.RejectPrincipal();
            };
        });
        builder.Services.AddAuthorization();

        var perMinute = builder.Configuration.GetValue("LicenseServer:RequestsPerMinute", 30);
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = 429;
            o.AddPolicy(PublicApi.RatePolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });

        builder.Services.Configure<ForwardedHeadersOptions>(o =>
        {
            // Runs behind a TLS-terminating reverse proxy (see docker-compose.yml): trust its X-Forwarded-For / -Proto.
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            if (builder.Configuration.GetValue("LicenseServer:TrustAllProxies", false)) { o.KnownIPNetworks.Clear(); o.KnownProxies.Clear(); }
        });

        var app = builder.Build();
        app.UseForwardedHeaders();
        app.Use(async (ctx, next) =>
        {
            var h = ctx.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            h["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            await next();
        });
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapPublicApi();
        app.MapAdminApi();
        app.MapFallbackToFile("/admin/{*path:nonfile}", "admin/index.html");
        return app;
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int Keygen(string[] args)
    {
        var dir = Arg(args, "--out");
        if (dir is null) { Console.Error.WriteLine("Usage: keygen --out <directory> [--key-id <id>]   (passphrase: ZLS_SIGNING_PASSPHRASE or prompted)"); return 2; }
        var keyId = Arg(args, "--key-id") ?? $"zentriq-prod-{DateTime.UtcNow:yyyy-MM}";
        var pass = Environment.GetEnvironmentVariable(KeyCeremony.PassphraseEnv);
        if (string.IsNullOrEmpty(pass))
        {
            if (Console.IsInputRedirected) { Console.Error.WriteLine($"Set {KeyCeremony.PassphraseEnv} or run keygen interactively."); return 2; }
            pass = ReadHidden($"Passphrase for the signing key ({KeyCeremony.MinPassphraseLength}+ characters): ");
            if (ReadHidden("Repeat the passphrase: ") != pass) { Console.Error.WriteLine("The passphrases do not match."); return 1; }
        }
        try
        {
            var c = KeyCeremony.Generate(dir, keyId, pass);
            Console.WriteLine($"Key id:       {c.KeyId}");
            Console.WriteLine($"Fingerprint:  {c.Fingerprint}");
            Console.WriteLine($"Private key:  {c.PrivateFile}  (encrypted; back it up offline, keep the passphrase separately)");
            Console.WriteLine($"Public key:   {c.EntryFile}  (send ONLY this file to Zentriq, confirm the fingerprint by phone)");
            Console.WriteLine($"Record:       {c.RecordFile}");
            return 0;
        }
        catch (InvalidOperationException ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }

    private static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }

    private static async Task<int> CreateAdminAsync(WebApplication app, string[] args)
    {
        var email = Arg(args, "--email"); var name = Arg(args, "--name") ?? email;
        if (email is null) { Console.Error.WriteLine("Usage: create-admin --email <email> [--name <name>]"); return 2; }
        var pass = Environment.GetEnvironmentVariable("ZLS_ADMIN_PASSPHRASE") ?? Console.In.ReadLine();
        if (AdminStore.CheckPassword(pass) is { } e) { Console.Error.WriteLine(e); return 1; }
        var store = app.Services.GetRequiredService<AdminStore>();
        if (await store.FindByEmailAsync(email, default) is not null) { Console.Error.WriteLine("An account with this email exists."); return 1; }
        await store.CreateAsync(email, name!, pass!, owner: true, default);
        Console.WriteLine($"Owner account {email} created.");
        return 0;
    }

    /// <summary>First start: create the owner account from ZLS_BOOTSTRAP_EMAIL / ZLS_ADMIN_PASSPHRASE when there is none.</summary>
    private static async Task BootstrapAsync(WebApplication app)
    {
        var store = app.Services.GetRequiredService<AdminStore>();
        if (await store.CountAsync(default) > 0) return;
        var email = Environment.GetEnvironmentVariable("ZLS_BOOTSTRAP_EMAIL");
        var pass = Environment.GetEnvironmentVariable("ZLS_ADMIN_PASSPHRASE");
        if (string.IsNullOrWhiteSpace(email) || AdminStore.CheckPassword(pass) is not null)
        {
            app.Logger.LogWarning("No portal account exists. Run 'create-admin --email <email>' or set ZLS_BOOTSTRAP_EMAIL and ZLS_ADMIN_PASSPHRASE.");
            return;
        }
        await store.CreateAsync(email, email, pass!, owner: true, default);
        app.Logger.LogInformation("Created owner account {Email}.", email);
    }
}
