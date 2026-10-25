using System.Text.Json;
using Npgsql;
using Zentriq.Domain;
using Zentriq.Infrastructure.Cloud;
using Zentriq.Infrastructure.Jobs;
using Zentriq.Infrastructure.Secrets;
using Zentriq.Worker.Agentless;

namespace Zentriq.Worker.Cloud;

/// <summary>
/// Reads one cloud or virtualization connection (Azure, AWS, vCenter, Hyper-V hosts) and stores what it found:
/// resources, changes, links to inventoried devices and findings. Read-only towards the platform.
/// </summary>
public sealed class CloudSyncHandler(NpgsqlDataSource ds, CloudStore store, CredentialProtector protector, ILogger<CloudSyncHandler> logger) : IJobHandler
{
    public string JobType => JobTypes.CloudSync;

    public async Task<object?> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var id = JsonDocument.Parse(context.Job.Payload).RootElement.GetProperty("accountId").GetGuid();
        CloudAccountInfo account;
        try { account = await store.LoadAsync(id, ct) ?? throw new PermanentJobException("The connection no longer exists."); }
        catch (Exception ex) when (ex is not (OperationCanceledException or PermanentJobException))
        {
            throw new PermanentJobException("The connection secret could not be decrypted: " + ex.Message);
        }
        await store.SetStatusAsync(id, "running", null, null, null, ct);
        await context.LogAsync($"Reading {CloudProviders.Title(account.Provider)} connection '{account.Name}'.", ct: ct);
        await context.ProgressAsync(5, "Signing in", ct);
        CloudSnapshot snap;
        try
        {
            snap = account.Provider switch
            {
                CloudProviders.Azure => await new AzureCollector().CollectAsync(account.Settings, account.Secret, ct),
                CloudProviders.Aws => await new AwsCollector().CollectAsync(account.Settings, account.Secret, ct),
                CloudProviders.VMware => await new VmwareCollector().CollectAsync(account.Settings, account.Secret, ct),
                CloudProviders.Gcp => await new GcpCollector().CollectAsync(account.Settings, account.Secret, ct),
                CloudProviders.Nutanix => await new NutanixCollector().CollectAsync(account.Settings, account.Secret, ct),
                CloudProviders.Proxmox => await new ProxmoxCollector().CollectAsync(account.Settings, account.Secret, ct),
                CloudProviders.XenServer => await new XenServerCollector().CollectAsync(account.Settings, account.Secret, ct),
                CloudProviders.Ovirt => await new OvirtCollector().CollectAsync(account.Settings, account.Secret, ct),
                CloudProviders.HyperV => await HyperVAsync(account, context, ct),
                _ => throw new CloudException($"Unknown platform '{account.Provider}'."),
            };
        }
        catch (CloudException ex)
        {
            await store.SetStatusAsync(id, "failed", ex.Message, null, null, ct);
            throw new PermanentJobException(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var msg = $"The sync failed: {ex.Message}";
            await store.SetStatusAsync(id, "failed", msg, null, null, ct);
            logger.LogWarning(ex, "Cloud sync {Name} failed", account.Name);
            throw new PermanentJobException(msg);
        }
        if (snap.Identity is not null) await context.LogAsync($"Signed in: {snap.Identity}.", ct: ct);
        foreach (var w in snap.Warnings.Take(20)) await context.LogAsync(w, "warn", ct);
        await context.ProgressAsync(80, $"Saving {snap.Items.Count} resources", ct);

        var result = await store.SaveSnapshotAsync(id, account.Provider, snap, DateTimeOffset.UtcNow, ct);
        var counts = new
        {
            resources = result.Resources, byKind = result.ByKind, created = result.Created, changed = result.Changed, deleted = result.Deleted,
            restored = result.Restored, linked = result.Linked, findings = result.Findings, newFindings = result.NewFindings, identity = snap.Identity,
        };
        var status = snap.Warnings.Count > 0 ? "partial" : "succeeded";
        await store.SetStatusAsync(id, status, null, counts, snap.Warnings, ct);
        await context.LogAsync($"{result.Resources} resource(s): {string.Join(", ", result.ByKind.OrderByDescending(k => k.Value).Select(k => $"{k.Value} {CloudKinds.Labels.GetValueOrDefault(k.Key, k.Key).ToLowerInvariant()}"))}. " +
                               $"{result.Created} new, {result.Changed} changed, {result.Deleted} deleted; {result.Linked} linked to inventoried devices; {result.Findings} finding(s), {result.NewFindings} new.", ct: ct);
        logger.LogInformation("Cloud sync {Name}: {Count} resources, {Findings} findings ({Status})", account.Name, result.Resources, result.Findings, status);
        return counts;
    }

    /// <summary>Each Hyper-V host over WinRM (then DCOM) with the saved Windows credential; one host failing does not stop the others.</summary>
    private async Task<CloudSnapshot> HyperVAsync(CloudAccountInfo account, JobContext context, CancellationToken ct)
    {
        var hosts = account.Settings.Hosts.Select(h => h.Trim()).Where(h => h.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (hosts.Count == 0) throw new CloudException("Add at least one Hyper-V host (name or IP address).");
        if (account.CredentialId is not { } credId) throw new CloudException("Choose the saved Windows credential (an administrator, or a member of Hyper-V Administrators, on the hosts).");
        var cred = (await ScanCredential.LoadAsync(ds, protector, [credId], ct)).FirstOrDefault()
                   ?? throw new CloudException("The saved credential no longer exists. Choose another one.");
        if (!cred.IsWindows) throw new CloudException("Hyper-V needs a Windows credential (domain or local account).");

        var snap = new CloudSnapshot { Identity = $"{cred.Username} on {hosts.Count} host(s)" };
        var failures = new List<string>();
        var n = 0;
        foreach (var host in hosts)
        {
            ct.ThrowIfCancellationRequested();
            await context.ProgressAsync(5 + 70 * n++ / hosts.Count, $"Reading {host}", ct);
            string? error = null;
            foreach (var dcom in new[] { false, true })
            {
                try
                {
                    var vms = await Task.Run(() =>
                    {
                        using var session = WinRmCollector.CreateSession(host, cred, dcom, false, dcom ? 135 : 5985, TimeSpan.FromSeconds(60));
                        var part = new CloudSnapshot();
                        HyperVReader.Read(host, (nsp, wql) => Query(session, nsp, wql), part, DateTimeOffset.UtcNow);
                        lock (snap) snap.Items.AddRange(part.Items);
                        return part.Items.Count(i => i.Kind == "vm");
                    }, ct);
                    error = null;
                    await context.LogAsync($"{host}: {vms} virtual machine(s) (read over {(dcom ? "DCOM" : "WinRM")}).", ct: ct);
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    error = WinRmCollector.IsAccessDenied(ex)
                        ? "access denied — the account must be an administrator or in Hyper-V Administrators on the host"
                        : ex.Message.Contains("Invalid namespace", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("namespace", StringComparison.OrdinalIgnoreCase) && ex.Message.Contains("virtualization", StringComparison.OrdinalIgnoreCase)
                            ? "the Hyper-V role is not installed on this computer"
                            : ex.Message.Split('\n')[0].Trim();
                    if (WinRmCollector.IsAccessDenied(ex) || error.Contains("Hyper-V role")) break;
                }
            }
            if (error is not null) { failures.Add($"{host}: {error}"); snap.Warn($"{host}: not read — {error}."); }
        }
        if (failures.Count == hosts.Count) throw new CloudException(string.Join("; ", failures));
        return snap;
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(Microsoft.Management.Infrastructure.CimSession session, string nsp, string wql)
    {
        var list = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var inst in session.QueryInstances(nsp, "WQL", wql))
            using (inst)
            {
                var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in inst.CimInstanceProperties) d[p.Name] = p.Value;
                list.Add(d);
            }
        return list;
    }
}

/// <summary>Every 10 minutes, queues a sync for each enabled connection whose interval has passed (and none is queued or running).</summary>
public sealed class CloudScheduler(NpgsqlDataSource ds, JobQueue jobs, ILogger<CloudScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var cmd = ds.CreateCommand("""
                    SELECT c.id, c.name FROM zentriq.cloud_accounts c
                     WHERE c.enabled AND (c.last_sync_at IS NULL OR c.last_sync_at < now() - make_interval(hours => c.sync_hours))
                       AND NOT EXISTS (SELECT 1 FROM zentriq.jobs j WHERE j.job_type = 'cloud.sync' AND j.status IN ('queued','running')
                                         AND j.payload->>'accountId' = c.id::text)
                       AND NOT EXISTS (SELECT 1 FROM zentriq.jobs j WHERE j.job_type = 'cloud.sync' AND j.payload->>'accountId' = c.id::text
                                         AND j.created_at > now() - make_interval(hours => c.sync_hours))
                    """);
                var due = new List<(Guid, string)>();
                await using (var r = await cmd.ExecuteReaderAsync(stoppingToken))
                    while (await r.ReadAsync(stoppingToken)) due.Add((r.GetGuid(0), r.GetString(1)));
                foreach (var (id, name) in due)
                {
                    await jobs.EnqueueAsync(new EnqueueRequest(JobTypes.CloudSync, new { accountId = id, scheduled = true }, MaxAttempts: 1, TimeoutSeconds: 3600), ct: stoppingToken);
                    logger.LogInformation("Scheduled cloud sync for {Name}", name);
                }
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
            {
                logger.LogWarning("Cloud sync scheduling failed: {Message}", ex.Message);
            }
            try { await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}
