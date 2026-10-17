using System.Security.Cryptography.X509Certificates;
using Zentriq.Api.Infrastructure;
using Zentriq.Infrastructure.Certificates;
using Zentriq.Infrastructure.Configuration;
using Zentriq.Infrastructure.Secrets;

namespace Zentriq.Api.Endpoints;

/// <summary>
/// The server's HTTPS trust: the Zentriq Local CA certificate to install on technician PCs (public, no sign-in, like any
/// CA certificate) and what the current console certificate covers.
/// </summary>
public static class TrustEndpoints
{
    public static void MapTrustEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/trust/ca.cer", (ZentriqOptions options, ZentriqPaths paths, Func<AppSecrets> secrets) =>
        {
            var ca = LoadCaPublic(options, paths, secrets());
            if (ca is null) return Results.NotFound();
            using (ca)
                return Results.File(ca.Export(X509ContentType.Cert), "application/pkix-cert", "Zentriq-Local-CA.cer");
        }).AllowAnonymous().WithTags("Trust");

        api.MapGet("/trust", (ZentriqOptions options, ZentriqPaths paths, Func<AppSecrets> secrets, IServiceProvider sp) =>
        {
            var current = sp.GetService<ConsoleCertificate>()?.Current;
            using var ca = LoadCaPublic(options, paths, secrets());
            var san = current?.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
            return Results.Ok(new
            {
                mode = !string.IsNullOrWhiteSpace(options.Https.CertificateThumbprint) ? "customer" : ca is not null ? "local-ca" : "self-signed",
                ca = ca is null ? null : new { subject = ca.Subject, thumbprint = ca.Thumbprint, expiresAt = ca.NotAfter.ToUniversalTime() },
                certificate = current is null ? null : new
                {
                    subject = current.Subject, issuer = current.Issuer, thumbprint = current.Thumbprint, expiresAt = current.NotAfter.ToUniversalTime(),
                    names = san?.EnumerateDnsNames().ToArray() ?? [],
                    addresses = san?.EnumerateIPAddresses().Select(a => a.ToString()).ToArray() ?? [],
                },
            });
        }).RequireAuthorization().WithTags("Trust");
    }

    private static X509Certificate2? LoadCaPublic(ZentriqOptions options, ZentriqPaths paths, AppSecrets s)
    {
        if (!string.IsNullOrWhiteSpace(options.Https.CertificateThumbprint) || string.IsNullOrEmpty(s.HttpsCaPfxPassword) || !File.Exists(paths.HttpsCaFile)) return null;
        using var withKey = ServerCertificates.Load(paths.HttpsCaFile, s.HttpsCaPfxPassword);
        return X509CertificateLoader.LoadCertificate(withKey.Export(X509ContentType.Cert));
    }
}
