using System.Net;
using System.Security.Cryptography.X509Certificates;
using LettuceEncrypt;
using Microsoft.AspNetCore.DataProtection;

namespace Zentriq.LicenseServer.Services;

/// <summary>
/// An installed license server (<c>serve --data &lt;folder&gt;</c>): Windows service, settings from the setup wizard,
/// the signing key unlocked automatically, daily backups, and the addresses it listens on:
/// always http://localhost:5080 (setup and local use); in public mode also HTTPS on 443 (Let's Encrypt or your
/// certificate) and HTTP on 80 (certificate validation and redirect to HTTPS).
/// </summary>
public static class InstalledServer
{
    public static void Configure(WebApplicationBuilder builder, ServerData data, LicenseServerOptions options)
    {
        var state = data.State;
        builder.Services.AddWindowsService(o => o.ServiceName = "ZentriqLicenseServer");
        builder.Logging.AddProvider(new FileLoggerProvider(data.Logs));
        builder.Services.AddSingleton(data);
        builder.Services.AddHostedService<DailyBackup>();
        // Portal sessions survive restarts (the wizard restarts the service to apply address settings).
        var dp = builder.Services.AddDataProtection().SetApplicationName("Zentriq License Server")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(data.Protected, "session-keys")));
        if (OperatingSystem.IsWindows()) dp.ProtectKeysWithDpapi(protectToLocalMachine: true);

        options.PublicUrl = state.PublicUrl;
        var holder = new SigningKeyHolder();
        if (state.KeyId is { } keyId && state.KeyFile is { } keyFile)
        {
            options.SigningKeyId = keyId;
            try
            {
                var key = KeyCeremony.Load(File.ReadAllText(keyFile), data.GetSecret("signing"), allowUnencrypted: false);
                KeyCeremony.SelfTest(key, keyId);
                holder.Set(new SigningKey(key, keyId));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                holder.LoadError = $"The signing key {keyId} could not be loaded: {ex.Message}";
            }
        }
        builder.Services.AddSingleton(holder);

        var access = state.Access;
        X509Certificate2? pfx = null;
        if (access.IsPublic && access.Certificate == AccessSettings.PfxCertificate)
        {
            // Not an ephemeral key: Windows TLS (SChannel) needs the key in a key container.
            try { pfx = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(data.Certs, "server.pfx"), data.GetSecret("pfx")); }
            catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException) { Console.Error.WriteLine("HTTPS certificate: " + ex.Message); }
        }
        var auto = access.IsPublic && access.Certificate == AccessSettings.AutoCertificate;
        if (auto)
        {
            var acmePassword = data.GetSecret("acme");
            if (acmePassword is null) { acmePassword = ServerData.NewSecret(); data.SetSecret("acme", acmePassword); }
            builder.Services.AddLettuceEncrypt(o =>
            {
                o.AcceptTermsOfService = true;   // accepted by the owner in the setup wizard
                o.DomainNames = [access.Hostname!];
                o.EmailAddress = access.Email ?? "";
            }).PersistDataToDirectory(new DirectoryInfo(Path.Combine(data.Certs, "letsencrypt")), acmePassword);
        }

        // A public port another program holds (IIS, another web server) must not stop the server: it then still runs
        // on localhost:5080, and the setup page shows the problem.
        var status = new AccessStatus();
        builder.Services.AddSingleton(status);
        var httpFree = false; var httpsFree = false;
        if (access.IsPublic)
        {
            httpFree = PortFree(80, status);
            httpsFree = PortFree(443, status);
            if (access.Certificate == AccessSettings.PfxCertificate && pfx is null) status.Problems.Add("The HTTPS certificate (certs\\server.pfx) could not be loaded; choose it again.");
        }
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.ListenLocalhost(state.HttpPort);
            if (!access.IsPublic) return;
            if (httpFree) k.Listen(IPAddress.IPv6Any, 80);
            if (!httpsFree) return;
            if (auto)
            {
                var services = k.ApplicationServices;
                k.Listen(IPAddress.IPv6Any, 443, l => l.UseHttps(h => h.UseLettuceEncrypt(services)));
            }
            else if (pfx is not null)
                k.Listen(IPAddress.IPv6Any, 443, l => l.UseHttps(pfx));
        });
    }

    private static bool PortFree(int port, AccessStatus status)
    {
        try
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.IPv6Any, port);
            probe.Server.DualMode = true;
            probe.Start();
            probe.Stop();
            return true;
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            status.Problems.Add($"Port {port} cannot be used ({ex.SocketErrorCode}): another program, for example IIS or another web server, is using it. Stop that program (or move it to another port) and save the settings again.");
            Console.Error.WriteLine($"Port {port}: {ex.Message}");
            return false;
        }
    }

    public static bool IsLoopback(HttpContext ctx) => ctx.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);

    /// <summary>Before the setup is finished only this computer reaches the server (the wizard); HTTP on 80 redirects to HTTPS.</summary>
    public static void UseGates(WebApplication app, ServerData data)
    {
        app.Use(async (ctx, next) =>
        {
            var state = data.State;
            var path = ctx.Request.Path.Value ?? "/";
            var local = IsLoopback(ctx);

            if (path.StartsWith("/setup", StringComparison.OrdinalIgnoreCase) && !local)
            {
                ctx.Response.StatusCode = 404;
                return;
            }
            if (state.Access.IsPublic && !ctx.Request.IsHttps && ctx.Connection.LocalPort == 80
                && !path.StartsWith("/.well-known/", StringComparison.Ordinal) && path != "/health")
            {
                ctx.Response.Redirect($"https://{state.Access.Hostname}{ctx.Request.PathBase}{ctx.Request.Path}{ctx.Request.QueryString}", permanent: true);
                return;
            }
            if (ctx.Request.IsHttps) ctx.Response.Headers.StrictTransportSecurity = "max-age=31536000";
            if (!state.SetupComplete && !path.StartsWith("/setup", StringComparison.OrdinalIgnoreCase) && path != "/health"
                && !path.StartsWith("/app.css", StringComparison.Ordinal) && !path.StartsWith("/.well-known/", StringComparison.Ordinal))
            {
                if (local && (path == "/" || path.StartsWith("/admin", StringComparison.OrdinalIgnoreCase)) && HttpMethods.IsGet(ctx.Request.Method)
                    && !path.StartsWith("/admin/api", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Response.Redirect("/setup/");
                    return;
                }
                if (!local)
                {
                    ctx.Response.StatusCode = 503;
                    await Results.Problem(statusCode: 503, title: NotSetUpException.Message).ExecuteAsync(ctx);
                    return;
                }
            }
            await next();
        });
    }
}

/// <summary>Problems with the public addresses found at start-up (shown by the setup page).</summary>
public sealed class AccessStatus
{
    public List<string> Problems { get; } = [];
}
