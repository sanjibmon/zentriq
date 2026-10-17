using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Zentriq.Infrastructure.Certificates;
using Zentriq.Infrastructure.Configuration;
using Zentriq.Infrastructure.Remote;
using Zentriq.Infrastructure.Secrets;

namespace Zentriq.Api.Infrastructure;

/// <summary>The certificate Kestrel serves on the console and agent ports; it can be replaced while running.</summary>
public sealed class ConsoleCertificate(X509Certificate2 initial)
{
    private X509Certificate2 _current = initial;
    public X509Certificate2 Current => Volatile.Read(ref _current);
    public void Replace(X509Certificate2 next) => Interlocked.Exchange(ref _current, next); // the old one may still serve open handshakes
}

/// <summary>
/// Keeps the console certificate valid for the server's current names and IP addresses: checks at start, every five
/// minutes and when the network changes. When something is not covered (or the certificate is due), it is re-issued
/// from the local CA with the same key (agents pin the key), saved, served immediately and handed to remote support.
/// Does nothing when a customer certificate (thumbprint) is configured.
/// </summary>
public sealed class ConsoleCertificateRenewer(ConsoleCertificate holder, ZentriqOptions options, ZentriqPaths paths, Func<AppSecrets> secrets,
    ILogger<ConsoleCertificateRenewer> logger) : BackgroundService
{
    private readonly SemaphoreSlim _changed = new(0);
    private string? _lastProblem;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!string.IsNullOrWhiteSpace(options.Https.CertificateThumbprint) || !OperatingSystem.IsWindows()) return;
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { Check(); }
                catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    Problem("Could not renew the console certificate: " + ex.Message);
                }
                try
                {
                    // Wait for a network change (then let addresses settle) or five minutes.
                    if (await _changed.WaitAsync(TimeSpan.FromMinutes(5), stoppingToken)) await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                    while (_changed.CurrentCount > 0) await _changed.WaitAsync(stoppingToken);
                }
                catch (OperationCanceledException) { break; }
            }
        }
        finally { NetworkChange.NetworkAddressChanged -= OnNetworkChanged; }
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => _changed.Release();

    private void Problem(string message)
    {
        if (message == _lastProblem) return;
        _lastProblem = message;
        logger.LogWarning("{Message}", message);
    }

    /// <summary>Returns true when a new certificate was issued.</summary>
    public bool Check()
    {
        var s = secrets();
        if (string.IsNullOrEmpty(s.HttpsCaPfxPassword) || !File.Exists(paths.HttpsCaFile))
        {
            Problem("The local certificate authority is not set up yet; run Zentriq Setup (Repair) so the console certificate follows the server's address.");
            return false;
        }
        using var ca = ServerCertificates.Load(paths.HttpsCaFile, s.HttpsCaPfxPassword);
        var names = ServerCertificates.Names(options.Https.HostName);
        var addresses = ServerCertificates.Addresses(options.Https.HostName);
        var current = holder.Current;
        if (ServerCertificates.Covers(current, ca, names, addresses))
        {
            EnsureRemoteCopy(current, s);
            _lastProblem = null;
            return false;
        }

        using var existing = ServerCertificates.Load(paths.HttpsCertificateFile, s.HttpsPfxPassword);
        using var key = existing.GetRSAPrivateKey();
        using var issued = ServerCertificates.Issue(ca, key, names, addresses);
        ServerCertificates.Save(issued, paths.HttpsCertificateFile, s.HttpsPfxPassword);
        holder.Replace(HttpsCertificate.Load(options, paths));
        logger.LogInformation("Issued a new console certificate for {Names} and {Addresses} (the server's names or addresses changed). Browsers that trust the Zentriq Local CA trust it.",
            string.Join(", ", names), string.Join(", ", addresses.Select(a => a.ToString())));
        EnsureRemoteCopy(holder.Current, s, force: true);
        _lastProblem = null;
        return true;
    }

    /// <summary>Writes the certificate in PEM for the remote support engine (ZentriqRemote picks it up within a minute).</summary>
    private void EnsureRemoteCopy(X509Certificate2 current, AppSecrets s, bool force = false)
    {
        var dir = paths.RemoteCertificateDir;
        if (!Directory.Exists(dir)) return; // remote support not installed
        var marker = Path.Combine(dir, "zentriq-web-cert.txt");
        if (!force && File.Exists(marker) && File.ReadAllText(marker).Trim().Equals(current.Thumbprint, StringComparison.OrdinalIgnoreCase)) return;
        using var exportable = ServerCertificates.Load(paths.HttpsCertificateFile, s.HttpsPfxPassword);
        if (!RemoteServerConfig.WriteWebCertificate(exportable, dir, out var reason)) Problem("Could not pass the console certificate to remote support: " + reason);
    }
}
