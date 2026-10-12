using System.Net;
using Npgsql;

namespace Zentriq.Worker.Agentless;

/// <summary>
/// Chooses protocols by the ports a target answered on and tries the scan's credentials in order:
///   WinRM (5985/5986) → DCOM (135)  with Windows credentials
///   SSH (22)                        with SSH credentials
///   SNMP (UDP 161)                  with SNMP credentials (also for hosts that answered no TCP probe)
/// A credential is only ever sent to targets inside its allowed scope. The first successful collection wins.
/// </summary>
public sealed class AgentlessInventory(NpgsqlDataSource ds, ILogger<AgentlessInventory> logger)
{
    public static string Version => typeof(AgentlessInventory).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public async Task<CollectionAttempt> TryCollectAsync(IPAddress ip, string? hostname, int[] openPorts, bool tcpAlive, IReadOnlyList<ScanCredential> creds, TimeSpan timeout, CancellationToken ct)
    {
        if (creds.Count == 0) return CollectionAttempt.None("No credentials selected for this scan.");
        var target = hostname ?? ip.ToString();
        var allowed = creds.Where(c => c.AllowedFor(ip)).ToList();
        if (allowed.Count == 0) return CollectionAttempt.None("No selected credential is allowed for this address (credential scope).");
        var authFailures = new List<string>();
        var errors = new List<string>();

        async Task<CollectionAttempt?> Try(string protocol, ScanCredential c, Func<Task<Zentriq.Inventory.InventoryDocument?>> run, Func<Exception, bool> isAuth)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMinutes(3));
                var doc = await run().WaitAsync(cts.Token);
                if (doc is null) { errors.Add($"{protocol}: no response"); return null; }
                await ScanCredential.MarkUsedAsync(ds, c.Id);
                return new CollectionAttempt(CollectionAttempt.Success, doc, protocol, c.Id,
                    $"Inventoried over {protocol} with credential '{c.Name}' ({doc.Software.Count} software items{(doc.Errors.Count > 0 ? $"; partial: {string.Join("; ", doc.Errors.Take(3))}" : "")}).");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                var msg = Zentriq.Application.Security.Redactor.Redact(ex.Message);
                if (isAuth(ex)) authFailures.Add($"{protocol} '{c.Name}': {msg}");
                else errors.Add($"{protocol} '{c.Name}': {msg}");
                logger.LogDebug(ex, "{Protocol} collection from {Target} with {Credential} failed", protocol, target, c.Name);
                return null;
            }
        }

        bool Open(int p) => openPorts.Contains(p);
        foreach (var c in allowed.Where(c => c.IsWindows))
        {
            if (Open(5985) || Open(5986))
            {
                var https = Open(5986) && (!Open(5985) || c.Bool("https"));
                var r = await Try(https ? "WinRM (HTTPS)" : "WinRM", c, () => Task.Run(() => (Zentriq.Inventory.InventoryDocument?)WinRmCollector.Collect(target, c, false, https, https ? 5986 : 5985, timeout, Version)), WinRmCollector.IsAccessDenied);
                if (r is not null) return r;
            }
            if (Open(135))
            {
                var r = await Try("DCOM/WMI", c, () => Task.Run(() => (Zentriq.Inventory.InventoryDocument?)WinRmCollector.Collect(target, c, true, false, 135, timeout, Version)), WinRmCollector.IsAccessDenied);
                if (r is not null) return r;
            }
        }
        foreach (var c in allowed.Where(c => c.IsSsh))
        {
            if (!Open(c.Int("port", 22))) continue;
            var r = await Try("SSH", c, async () => await SshCollector.CollectAsync(ds, target, c, timeout, Version, ct), SshCollector.IsAuthFailure);
            if (r is not null) return r;
        }
        foreach (var c in allowed.Where(c => c.IsSnmp))
        {
            var r = await Try(c.Kind == "snmp_v3" ? "SNMPv3" : "SNMPv2c", c, () => Task.Run(() => SnmpCollector.Collect(ip, c, timeout * 2, Version)), SnmpCollector.IsAuthFailure);
            if (r is not null) return r;
        }

        if (authFailures.Count > 0)
            return new CollectionAttempt(CollectionAttempt.AuthFailed, null, null, null, "Credentials rejected: " + string.Join(" | ", authFailures.Take(4)));
        if (errors.Count > 0)
            return new CollectionAttempt(CollectionAttempt.Failed, null, null, null, "Inventory failed: " + string.Join(" | ", errors.Take(4)));
        return CollectionAttempt.None(tcpAlive
            ? "No selected credential matches the protocols this host exposes."
            : "No response.");
    }
}
