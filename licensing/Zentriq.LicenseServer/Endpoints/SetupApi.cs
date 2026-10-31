using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Zentriq.LicenseServer.Services;

namespace Zentriq.LicenseServer.Endpoints;

/// <summary>
/// The setup wizard of an installed server (/setup): owner account, signing key (create or import), key backup and
/// product entry, and how the server is reached. Answers only on the server itself (loopback). Once the owner exists,
/// every step needs the owner signed in.
/// </summary>
public static partial class SetupApi
{
    public sealed record OwnerBody(string? Email, string? Name, string? Password);
    public sealed record NewKeyBody(string? KeyId, string? Passphrase);
    public sealed record ImportKeyBody(string? KeyId, string? PrivateKeyPem, string? Passphrase);
    public sealed record AccessBody(string? Mode, string? Hostname, string? Certificate, string? Email, bool AcceptTerms, string? PfxBase64, string? PfxPassword);

    [GeneratedRegex("^(?=.{4,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\\.)+[a-z]{2,63}$")]
    private static partial Regex HostnameRegex();

    private static IResult Bad(string title) => Results.Problem(statusCode: 400, title: title);

    public static void MapSetupApi(this WebApplication app)
    {
        var g = app.MapGroup("/setup/api").DisableAntiforgery();
        g.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            if (!InstalledServer.IsLoopback(http)) return Results.NotFound();
            if (!HttpMethods.IsGet(http.Request.Method) && http.Request.Headers[AdminApi.CsrfHeader] != "1")
                return Results.Problem(statusCode: 403, title: "Missing request header.");
            return await next(ctx);
        });

        g.MapGet("/state", async (HttpContext http, ServerData data, SigningKeyHolder keys, AdminStore admins, CancellationToken ct) =>
        {
            var s = data.State;
            var owners = await admins.CountAsync(ct);
            var lastBackup = Directory.Exists(data.Backups) ? Directory.GetFiles(data.Backups, "*.dump").Select(File.GetLastWriteTimeUtc).DefaultIfEmpty().Max() : default;
            return Results.Ok(new
            {
                complete = s.SetupComplete,
                ownerExists = owners > 0,
                signedIn = http.User.IsInRole("owner"),
                user = http.User.FindFirst(ClaimTypes.Email)?.Value,
                key = s.KeyId is null ? null : new { keyId = s.KeyId, fingerprint = keys.Current?.Fingerprint ?? s.KeyFingerprint, loaded = keys.Current is not null, backedUp = s.KeyBackedUp },
                keyError = keys.LoadError,
                suggestedKeyId = $"zentriq-prod-{DateTime.UtcNow:yyyy-MM}",
                access = s.Access,
                localUrl = $"http://localhost:{s.HttpPort}",
                publicUrl = s.PublicUrl,
                dataFolder = data.Root,
                version = s.InstalledVersion,
                canRestart = Restarter.CanRestart,
                lastBackup = lastBackup == default ? (DateTime?)null : lastBackup,
            });
        });

        g.MapPost("/owner", async (OwnerBody body, HttpContext http, AdminStore admins, Audit audit, CancellationToken ct) =>
        {
            if (await admins.CountAsync(ct) > 0) return Results.Problem(statusCode: 409, title: "The owner account exists. Sign in with it.");
            var email = body.Email?.Trim() ?? "";
            if (!email.Contains('@') || email.Length > 200) return Bad("Enter a valid email address.");
            if (AdminStore.CheckPassword(body.Password) is { } e) return Bad("Passphrase: " + e);
            var a = await admins.CreateAsync(email, string.IsNullOrWhiteSpace(body.Name) ? email : body.Name.Trim(), body.Password!, owner: true, ct);
            await http.SignInAsync(AdminApi.CookieScheme, AdminStore.Principal(a));
            await audit.WriteAsync(a.Email, "setup.owner_created", a.Id.ToString(), PublicApi.Ip(http), null, ct);
            return Results.Ok(a.View());
        });

        g.MapPost("/key/new", async (NewKeyBody body, HttpContext http, ServerData data, SigningKeyHolder keys, LicenseServerOptions options, AdminStore admins, Audit audit, CancellationToken ct) =>
        {
            if (await OwnerOnly(http, admins, ct) is { } denied) return denied;
            if (data.State.KeyId is not null) return Results.Problem(statusCode: 409, title: "A signing key is already set up; a key is never replaced here.");
            if (KeyCeremony.CheckPassphrase(body.Passphrase) is { } e) return Bad(e);
            var keyId = string.IsNullOrWhiteSpace(body.KeyId) ? $"zentriq-prod-{DateTime.UtcNow:yyyy-MM}" : body.KeyId.Trim().ToLowerInvariant();
            KeyCeremony.Ceremony c;
            try { c = KeyCeremony.Generate(data.Keys, keyId, body.Passphrase!); }
            catch (InvalidOperationException ex) { return Bad(ex.Message); }
            Activate(c, body.Passphrase!, data, keys, options);
            await audit.WriteAsync(Actor(http), "setup.key_created", keyId, PublicApi.Ip(http), new { c.Fingerprint }, ct);
            return Results.Ok(new { c.KeyId, c.Fingerprint });
        });

        g.MapPost("/key/import", async (ImportKeyBody body, HttpContext http, ServerData data, SigningKeyHolder keys, LicenseServerOptions options, AdminStore admins, Audit audit, CancellationToken ct) =>
        {
            if (await OwnerOnly(http, admins, ct) is { } denied) return denied;
            if (data.State.KeyId is not null) return Results.Problem(statusCode: 409, title: "A signing key is already set up; a key is never replaced here.");
            if (string.IsNullOrWhiteSpace(body.PrivateKeyPem)) return Bad("Choose the encrypted private key file (<key id>.private.pem).");
            if (string.IsNullOrEmpty(body.Passphrase)) return Bad("Enter the key's passphrase.");
            var keyId = (body.KeyId ?? "").Trim().ToLowerInvariant();
            KeyCeremony.Ceremony c;
            try { c = KeyCeremony.Import(data.Keys, keyId, body.PrivateKeyPem, body.Passphrase); }
            catch (InvalidOperationException ex) { return Bad(ex.Message); }
            Activate(c, body.Passphrase, data, keys, options);
            data.Update(s => s with { KeyBackedUp = true });   // it existed before: its backups exist
            await audit.WriteAsync(Actor(http), "setup.key_imported", keyId, PublicApi.Ip(http), new { c.Fingerprint }, ct);
            return Results.Ok(new { c.KeyId, c.Fingerprint });
        });

        g.MapGet("/key/product-entry", async (HttpContext http, ServerData data, SigningKeyHolder keys, AdminStore admins, CancellationToken ct) =>
        {
            if (await OwnerOnly(http, admins, ct) is { } denied) return denied;
            var k = keys.Require();
            return Results.File(Encoding.UTF8.GetBytes(KeyCeremony.ProductEntry(k.KeyId, k.PublicPem)), "application/json", $"{k.KeyId}.product-entry.json");
        });

        g.MapGet("/key/backup", async (HttpContext http, ServerData data, AdminStore admins, Audit audit, CancellationToken ct) =>
        {
            if (await OwnerOnly(http, admins, ct) is { } denied) return denied;
            var s = data.State;
            if (s.KeyFile is null || !File.Exists(s.KeyFile)) return Results.NotFound();
            await audit.WriteAsync(Actor(http), "setup.key_backup_downloaded", s.KeyId, PublicApi.Ip(http), null, ct);
            return Results.File(await File.ReadAllBytesAsync(s.KeyFile, ct), "application/x-pem-file", Path.GetFileName(s.KeyFile));
        });

        g.MapPost("/key/backed-up", async (HttpContext http, ServerData data, AdminStore admins, CancellationToken ct) =>
        {
            if (await OwnerOnly(http, admins, ct) is { } denied) return denied;
            if (data.State.KeyId is null) return Bad("Create or import the signing key first.");
            data.Update(s => s with { KeyBackedUp = true });
            return Results.NoContent();
        });

        g.MapPost("/access", async (AccessBody body, HttpContext http, ServerData data, LicenseServerOptions options, AdminStore admins, Audit audit, ILoggerFactory lf, CancellationToken ct) =>
        {
            if (await OwnerOnly(http, admins, ct) is { } denied) return denied;
            var before = data.State.Access;
            AccessSettings next;
            if (body.Mode == AccessSettings.Local) next = new AccessSettings();
            else if (body.Mode == AccessSettings.Public)
            {
                var host = (body.Hostname ?? "").Trim().ToLowerInvariant().TrimEnd('.');
                if (host.StartsWith("https://", StringComparison.Ordinal)) host = host[8..].TrimEnd('/');
                if (!HostnameRegex().IsMatch(host)) return Bad("Enter the server's internet name, e.g. license.zentriq.com.");
                if (body.Certificate == AccessSettings.AutoCertificate)
                {
                    if (!body.AcceptTerms) return Bad("Accept the Let's Encrypt subscriber agreement to get a certificate automatically.");
                    if (body.Email is not { } em || !em.Contains('@')) return Bad("Enter an email for certificate notices.");
                    next = new AccessSettings { Mode = AccessSettings.Public, Hostname = host, Certificate = AccessSettings.AutoCertificate, Email = em.Trim() };
                }
                else if (body.Certificate == AccessSettings.PfxCertificate)
                {
                    byte[] pfx;
                    try { pfx = Convert.FromBase64String(body.PfxBase64 ?? ""); } catch (FormatException) { return Bad("Choose the certificate file (.pfx)."); }
                    if (pfx.Length == 0) return Bad("Choose the certificate file (.pfx).");
                    X509Certificate2 cert;
                    try { cert = X509CertificateLoader.LoadPkcs12(pfx, body.PfxPassword, X509KeyStorageFlags.EphemeralKeySet); }
                    catch (CryptographicException) { return Bad("The certificate file could not be opened: check its password."); }
                    using (cert)
                    {
                        if (!cert.HasPrivateKey) return Bad("The certificate file has no private key; export it with the private key (.pfx).");
                        if (!cert.MatchesHostname(host)) return Bad($"The certificate is not for {host}.");
                        if (cert.NotAfter < DateTime.Now) return Bad($"The certificate expired on {cert.NotAfter:yyyy-MM-dd}.");
                        Directory.CreateDirectory(data.Certs);
                        await File.WriteAllBytesAsync(Path.Combine(data.Certs, "server.pfx"), pfx, ct);
                        data.SetSecret("pfx", body.PfxPassword ?? "");
                        next = new AccessSettings { Mode = AccessSettings.Public, Hostname = host, Certificate = AccessSettings.PfxCertificate, PfxExpires = cert.NotAfter };
                    }
                }
                else return Bad("Choose how the server gets its HTTPS certificate.");
            }
            else return Bad("Choose who can reach the server.");

            var s = data.Update(st => st with { Access = next });
            options.PublicUrl = s.PublicUrl;
            var restart = next != before;
            await audit.WriteAsync(Actor(http), "setup.access", next.Mode, PublicApi.Ip(http), new { next.Hostname, next.Certificate }, ct);
            if (restart) Restarter.Schedule(lf.CreateLogger("Setup"));
            return Results.Ok(new { restart, canRestart = Restarter.CanRestart, publicUrl = s.PublicUrl });
        });

        g.MapPost("/finish", async (HttpContext http, ServerData data, SigningKeyHolder keys, AdminStore admins, Audit audit, CancellationToken ct) =>
        {
            if (await OwnerOnly(http, admins, ct) is { } denied) return denied;
            var s = data.State;
            if (keys.Current is null) return Bad("Create or import the signing key first.");
            if (!s.KeyBackedUp) return Bad("Back up the signing key first, and confirm it.");
            data.Update(st => st with { SetupComplete = true, CompletedAt = st.CompletedAt ?? DateTimeOffset.UtcNow });
            await audit.WriteAsync(Actor(http), "setup.complete", null, PublicApi.Ip(http), null, ct);
            return Results.Ok(new { portal = "/admin/" });
        });

        g.MapPost("/backup", async (HttpContext http, ServerData data, AdminStore admins, CancellationToken ct) =>
        {
            if (await OwnerOnly(http, admins, ct) is { } denied) return denied;
            if (!File.Exists(DailyBackup.PgDump)) return Bad("Backups run on the installed server only.");
            try { return Results.Ok(new { file = await Task.Run(() => DailyBackup.Run(data), ct) }); }
            catch (InvalidOperationException ex) { return Results.Problem(statusCode: 500, title: ex.Message); }
        });
    }

    private static void Activate(KeyCeremony.Ceremony c, string passphrase, ServerData data, SigningKeyHolder keys, LicenseServerOptions options)
    {
        data.SetSecret("signing", passphrase);
        data.Update(s => s with { KeyId = c.KeyId, KeyFile = c.PrivateFile, KeyFingerprint = c.Fingerprint });
        var key = KeyCeremony.Load(File.ReadAllText(c.PrivateFile), passphrase, false);
        keys.Set(new SigningKey(key, c.KeyId));
        options.SigningKeyId = c.KeyId;
    }

    private static string Actor(HttpContext http) => http.User.FindFirst(ClaimTypes.Email)?.Value ?? "setup";

    private static async Task<IResult?> OwnerOnly(HttpContext http, AdminStore admins, CancellationToken ct)
    {
        if (await admins.CountAsync(ct) == 0) return Results.Problem(statusCode: 409, title: "Create the owner account first.");
        return http.User.IsInRole("owner") ? null : Results.Problem(statusCode: 401, title: "Sign in with the owner account.");
    }
}
