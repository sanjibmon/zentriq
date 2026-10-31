using System.Text;
using Npgsql;
using Renci.SshNet;
using Renci.SshNet.Common;
using Zentriq.Inventory;

namespace Zentriq.Worker.Agentless;

/// <summary>
/// Agentless Linux/macOS inventory over SSH: a fixed list of read-only commands (no sudo, nothing
/// written). Host keys are pinned on first use (ssh_known_hosts); a changed key is refused, so
/// credentials are never sent to an impersonating host.
/// </summary>
public static class SshCollector
{
    public sealed class HostKeyMismatchException(string message) : Exception(message);

    public static async Task<InventoryDocument> CollectAsync(NpgsqlDataSource ds, string host, ScanCredential cred, TimeSpan timeout, string version, CancellationToken ct)
    {
        var port = cred.Int("port", 22);
        var user = cred.Username ?? throw new InvalidOperationException("SSH credential has no user name.");
        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrEmpty(cred.Secret.PrivateKey))
        {
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(cred.Secret.PrivateKey));
            var key = string.IsNullOrEmpty(cred.Secret.Passphrase) ? new PrivateKeyFile(ms) : new PrivateKeyFile(ms, cred.Secret.Passphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(user, key));
        }
        if (!string.IsNullOrEmpty(cred.Secret.Password)) methods.Add(new PasswordAuthenticationMethod(user, cred.Secret.Password));
        if (methods.Count == 0) throw new InvalidOperationException("SSH credential has neither a password nor a private key.");

        var known = await KnownKeyAsync(ds, host, ct);
        string? seenType = null, seenPrint = null;
        var info = new ConnectionInfo(host, port, user, methods.ToArray()) { Timeout = timeout };
        using var client = new SshClient(info);
        client.HostKeyReceived += (_, e) =>
        {
            seenType = e.HostKeyName;
            seenPrint = e.FingerPrintSHA256;
            e.CanTrust = known is null || string.Equals(known, e.FingerPrintSHA256, StringComparison.Ordinal);
        };
        try
        {
            client.Connect();
        }
        catch (SshConnectionException) when (known is not null && seenPrint is not null && seenPrint != known)
        {
            throw new HostKeyMismatchException($"SSH host key of {host} changed (expected {known}, got {seenPrint}). Credentials were not sent. Clear the pinned key in Discovery > Credentials if the change is expected.");
        }
        if (known is null && seenPrint is not null) await PinAsync(ds, host, seenType ?? "", seenPrint, ct);

        string Run(string command)
        {
            using var c = client.CreateCommand(command);
            c.CommandTimeout = TimeSpan.FromSeconds(60);
            try { return c.Execute() ?? ""; } catch (SshOperationTimeoutException) { return ""; }
        }

        var uname = Run(UnixCollector.ProbeCommand);
        var isMac = uname.StartsWith("Darwin", StringComparison.Ordinal);
        var commands = isMac ? UnixCollector.MacCommands : UnixCollector.LinuxCommands.Concat(UnixCollector.LinuxExtraCommands);
        var outputs = new Dictionary<string, string>();
        foreach (var (k, cmd) in commands)
        {
            ct.ThrowIfCancellationRequested();
            outputs[k] = Run(cmd);
        }
        client.Disconnect();
        if (!uname.StartsWith("Linux", StringComparison.Ordinal) && !isMac)
            throw new NotSupportedException($"Unsupported SSH platform '{uname.Split('\n')[0].Trim()}' (Linux and macOS are supported; use SNMP for network devices).");
        return isMac ? UnixCollector.ParseMac(outputs, uname, version) : UnixCollector.ParseLinux(outputs, uname, version);
    }

    public static bool IsAuthFailure(Exception ex) => ex is SshAuthenticationException;

    private static async Task<string?> KnownKeyAsync(NpgsqlDataSource ds, string host, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT fingerprint FROM zentriq.ssh_known_hosts WHERE host = @h");
        cmd.Parameters.AddWithValue("h", host.ToLowerInvariant());
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private static async Task PinAsync(NpgsqlDataSource ds, string host, string type, string print, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("INSERT INTO zentriq.ssh_known_hosts (host, key_type, fingerprint) VALUES (@h, @t, @f) ON CONFLICT (host) DO UPDATE SET last_seen_at = now()");
        cmd.Parameters.AddWithValue("h", host.ToLowerInvariant());
        cmd.Parameters.AddWithValue("t", type);
        cmd.Parameters.AddWithValue("f", print);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
