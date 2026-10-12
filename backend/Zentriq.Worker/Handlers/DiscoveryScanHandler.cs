using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Application.Discovery;
using Zentriq.Domain;
using Zentriq.Infrastructure.Inventory;
using Zentriq.Infrastructure.Licensing;
using Zentriq.Infrastructure.Secrets;
using Zentriq.Worker.Agentless;

namespace Zentriq.Worker.Handlers;

/// <summary>
/// Real, authorised network discovery for IP / single-host / list scans.
///  * Targets are re-validated against the approved scopes captured when the scan was created.
///  * Hostnames are resolved and skipped if they resolve outside the approved scopes.
///  * Bounded concurrency, per-probe timeouts, cooperative cancellation.
///  * Resumable: targets that already have a result are skipped, so a retried or recovered job
///    (e.g. after a worker restart) continues where it stopped.
///  * Credentialed (agentless) inventory over WinRM/DCOM, SSH and SNMP when the scan has credentials; a
///    host is "success" only when an inventory was actually collected. Without usable credentials,
///    responding hosts are recorded as reachable_unauthenticated or inventory_unavailable.
///  * Scan type "ad" lists enabled computer objects in a domain/OU and scans them; an AD object alone is
///    never treated as proof that the computer is online or inventoried.
/// </summary>
public sealed class DiscoveryScanHandler(NpgsqlDataSource ds, IHostPinger pinger, LicensingService licensing, AgentlessInventory agentless,
    CredentialProtector protector, InventoryStore inventoryStore, ILogger<DiscoveryScanHandler> logger) : IJobHandler
{
    public string JobType => JobTypes.DiscoveryScan;

    private static readonly int[] ManagementPorts = [22, 135, 445, 5985, 5986];
    private sealed record DirectorySource(string Server, string BaseDn, bool Ldaps, Guid CredentialId, int? InactiveDays);
    private sealed record Config(string ScanType, string TargetsText, string ExcludeText, int Concurrency, int TimeoutMs, int[] Ports, List<string> ApprovedScopes, int MaxTargets,
        Guid[]? CredentialIds = null, DirectorySource? Directory = null);
    private List<ScanCredential> _creds = [];
    private Dictionary<string, DirectoryComputer> _adObjects = new(StringComparer.OrdinalIgnoreCase);

    public async Task<object?> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var scanId = JsonDocument.Parse(context.Job.Payload).RootElement.GetProperty("scanId").GetGuid();
        var (status, configJson) = await LoadScanAsync(scanId, ct) ?? throw new PermanentJobException($"Scan {scanId} no longer exists.");
        if (status is "cancelled" or "completed" or "completed_errors" or "failed") return new { skipped = true, status };

        var cfg = JsonSerializer.Deserialize<Config>(configJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                  ?? throw new PermanentJobException("Scan configuration is unreadable.");
        try { _creds = await Credentials.LoadAsync(ds, protector, cfg.CredentialIds ?? [], ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await FinishAsync(scanId, "failed", "Scan credentials could not be decrypted: " + ex.Message, ct);
            throw new PermanentJobException("Scan credentials could not be decrypted: " + ex.Message);
        }

        List<string> targets;
        if (cfg.ScanType == "ad")
        {
            targets = await DirectoryTargetsAsync(scanId, cfg, context, ct);
        }
        else
        {
            var parsed = TargetParser.Parse(cfg.TargetsText, cfg.ExcludeText, new ScanSafeguards(cfg.ApprovedScopes, cfg.MaxTargets, 0));
            if (!parsed.IsValid)
            {
                await FinishAsync(scanId, "failed", string.Join("; ", parsed.Errors), ct);
                throw new PermanentJobException("Targets failed validation: " + string.Join("; ", parsed.Errors));
            }
            targets = TargetParser.Expand(parsed, cfg.MaxTargets).ToList();
        }
        var done = await ExistingResultsAsync(scanId, ct);
        await MarkRunningAsync(scanId, targets.Count, done.Count, ct);
        await context.LogAsync(done.Count > 0
            ? $"Resuming: {done.Count} of {targets.Count} targets already processed."
            : $"Validated & authorised {targets.Count} target(s); probing ports {string.Join(',', cfg.Ports)} with concurrency {cfg.Concurrency}.", ct: ct);

        var timeout = TimeSpan.FromMilliseconds(cfg.TimeoutMs);
        var processed = done.Count;
        var created = 0; var updated = 0; var errors = 0;
        var counts = new ConcurrentDictionary<string, int>();
        using var gate = new SemaphoreSlim(cfg.Concurrency);
        var lastReport = DateTime.UtcNow;
        var license = await licensing.GetStatusAsync(ct);

        var tasks = new List<Task>();
        try
        {
            foreach (var target in targets)
            {
                if (done.Contains(target)) continue;
                await gate.WaitAsync(ct);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var r = await ProbeTargetAsync(scanId, target, cfg, timeout, license, ct);
                        counts.AddOrUpdate(r.Status, 1, (_, n) => n + 1);
                        if (r.Created) Interlocked.Increment(ref created);
                        if (r.Updated) Interlocked.Increment(ref updated);
                        if (r.Status is ScanResultStatus.InventoryFailed or ScanResultStatus.AuthFailed) Interlocked.Increment(ref errors);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref errors);
                        logger.LogWarning(ex, "Probe of {Target} failed", target);
                        await RecordAsync(scanId, target, null, null, ScanResultStatus.InventoryFailed, [], "Probe error: " + ex.Message, null, false, null, CancellationToken.None);
                    }
                    finally
                    {
                        Interlocked.Increment(ref processed);
                        gate.Release();
                    }
                }, CancellationToken.None));

                if ((DateTime.UtcNow - lastReport).TotalSeconds >= 2)
                {
                    lastReport = DateTime.UtcNow;
                    await ProgressAsync(scanId, processed, created, updated, ct);
                    await context.ProgressAsync(targets.Count == 0 ? 100 : (int)(processed * 100L / targets.Count), $"{processed}/{targets.Count} targets", ct);
                }
            }
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            await Task.WhenAll(tasks.Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default)));
            await ProgressAsync(scanId, processed, created, updated, CancellationToken.None);
            var cancelledNow = await IsCancelRequestedAsync(context.Job.Id);
            if (cancelledNow)
            {
                await RecordCancelledAsync(scanId, targets, CancellationToken.None);
                await FinishAsync(scanId, "cancelled", null, CancellationToken.None);
                await context.LogAsync($"Scan cancelled after {processed} of {targets.Count} targets.", "warning", CancellationToken.None);
            }
            throw;
        }

        await ProgressAsync(scanId, processed, created, updated, ct);
        var final = errors > 0 ? "completed_errors" : "completed";
        await FinishAsync(scanId, final, null, ct);
        var summary = string.Join(", ", counts.OrderBy(k => k.Key).Select(k => $"{k.Key}: {k.Value}"));
        await context.LogAsync($"Finished. {summary}. Assets created {created}, updated {updated}.", ct: ct);
        return new { scanId, status = final, processed, created, updated, counts };
    }

    private sealed record TargetOutcome(string Status, bool Created, bool Updated);

    private async Task<TargetOutcome> ProbeTargetAsync(Guid scanId, string target, Config cfg, TimeSpan timeout, Application.Licensing.LicenseStatus license, CancellationToken ct)
    {
        IPAddress? ip;
        string? hostname = null;
        if (IPAddress.TryParse(target, out var parsedIp)) ip = parsedIp;
        else
        {
            hostname = target;
            ip = await pinger.ResolveAsync(target, timeout * 2, ct);
            if (ip is null)
            {
                await RecordAsync(scanId, target, null, hostname, ScanResultStatus.Unreachable, [], "Name could not be resolved (DNS).", null, false, null, ct);
                return new(ScanResultStatus.Unreachable, false, false);
            }
            if (!TargetParser.AddressInScope(ip, cfg.ApprovedScopes))
            {
                await RecordAsync(scanId, target, ip, hostname, ScanResultStatus.Skipped, [], $"Resolved to {ip}, outside the approved scopes. Not probed.", null, false, null, ct);
                return new(ScanResultStatus.Skipped, false, false);
            }
        }

        var probe = await pinger.ProbeAsync(ip, cfg.Ports, timeout, ct);
        _adObjects.TryGetValue(target, out var adObject);
        var adNote = adObject is null ? "" : $" Active Directory: {adObject.DistinguishedName}{(adObject.LastLogon is { } ll ? $", last logon {ll:yyyy-MM-dd}" : "")}.";
        if (!probe.Alive)
        {
            // Network devices often answer only SNMP (UDP), which the TCP probe cannot see.
            if (_creds.Any(c => c.IsSnmp))
            {
                var snmp = await agentless.TryCollectAsync(ip, hostname, [], false, _creds.Where(c => c.IsSnmp).ToList(), timeout, ct);
                if (snmp.Outcome == CollectionAttempt.Success) return await StoreInventoryAsync(scanId, target, ip, snmp, probe, adObject, license, ct);
            }
            await RecordAsync(scanId, target, ip, hostname, ScanResultStatus.Unreachable, [], "No response on any probed port (host down, filtered or not routed)." + adNote, probe.DurationMs, false, null, ct);
            return new(ScanResultStatus.Unreachable, false, false);
        }

        hostname ??= await pinger.ReverseLookupAsync(ip, timeout, ct);
        CollectionAttempt? attempt = null;
        if (_creds.Count > 0)
        {
            attempt = await agentless.TryCollectAsync(ip, hostname, probe.OpenPorts, true, _creds, TimeSpan.FromSeconds(Math.Max(15, cfg.TimeoutMs / 1000.0 * 10)), ct);
            if (attempt.Outcome == CollectionAttempt.Success) return await StoreInventoryAsync(scanId, target, ip, attempt, probe, adObject, license, ct);
        }
        var mgmt = probe.OpenPorts.Intersect(ManagementPorts).ToArray();
        string status, detail;
        if (attempt is { Outcome: CollectionAttempt.AuthFailed })
        {
            status = ScanResultStatus.AuthFailed;
            detail = attempt.Detail;
        }
        else if (attempt is { Outcome: CollectionAttempt.Failed })
        {
            status = ScanResultStatus.InventoryFailed;
            detail = attempt.Detail;
        }
        else if (mgmt.Length > 0)
        {
            status = ScanResultStatus.ReachableUnauthenticated;
            detail = $"Responded; management ports open ({string.Join(',', mgmt)}). " + (attempt?.Detail ?? "Add credentials to the scan, or install the Zentriq Agent, to collect inventory.");
        }
        else
        {
            status = ScanResultStatus.InventoryUnavailable;
            detail = probe.OpenPorts.Length > 0
                ? $"Responded (open: {string.Join(',', probe.OpenPorts)}); no supported inventory protocol is exposed."
                : probe.Note ?? "Host is up but exposes no probed ports.";
        }

        var (assetId, created, updated, assetNote) = await ReconcileAssetAsync(ip, hostname, probe.OpenPorts, license, ct);
        if (assetNote is not null) detail += " " + assetNote;
        detail += adNote;
        if (assetId is { } aid && adObject is not null) await AddIdentifierAsync(aid, "ad_object_guid", adObject.ObjectGuid.ToString("D"), ct);
        await RecordAsync(scanId, target, ip, hostname, status, probe.OpenPorts, detail, probe.DurationMs, created, assetId, ct);
        return new(status, created, updated);
    }

    private async Task<TargetOutcome> StoreInventoryAsync(Guid scanId, string target, IPAddress ip, CollectionAttempt attempt, ProbeResult probe, DirectoryComputer? ad, Application.Licensing.LicenseStatus license, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        var r = await inventoryStore.ApplyAsync(conn, attempt.Document!, new MatchHints(Ip: ip), license, ct);
        var detail = attempt.Detail + (r.Note is null ? "" : " " + r.Note);
        if (r.AssetId is { } id && ad is not null) await AddIdentifierAsync(id, "ad_object_guid", ad.ObjectGuid.ToString("D"), ct);
        var host = attempt.Document!.Device.Fqdn ?? attempt.Document.Device.Hostname;
        await RecordAsync(scanId, target, ip, host, r.AssetId is null ? ScanResultStatus.InventoryFailed : ScanResultStatus.Success, probe.OpenPorts, detail,
            probe.DurationMs, r.Created, r.AssetId, ct, attempt.Protocol, attempt.CredentialId);
        return new(r.AssetId is null ? ScanResultStatus.InventoryFailed : ScanResultStatus.Success, r.Created, !r.Created && r.AssetId is not null);
    }

    private async Task AddIdentifierAsync(Guid assetId, string kind, string value, CancellationToken ct)
    {
        if (value == Guid.Empty.ToString("D")) return;
        await using var cmd = ds.CreateCommand("INSERT INTO zentriq.asset_identifiers (asset_id, kind, value) VALUES (@a, @k, @v) ON CONFLICT (kind, value) DO NOTHING");
        cmd.Parameters.AddWithValue("a", assetId);
        cmd.Parameters.AddWithValue("k", kind);
        cmd.Parameters.AddWithValue("v", value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Enabled AD computer objects under the base DN become hostname targets.</summary>
    private async Task<List<string>> DirectoryTargetsAsync(Guid scanId, Config cfg, JobContext context, CancellationToken ct)
    {
        var d = cfg.Directory ?? throw new PermanentJobException("Directory settings are missing.");
        await using var limitCmd = ds.CreateCommand("SELECT value::text FROM zentriq.system_settings WHERE key = 'discovery.ad_page_limit'");
        var limit = int.TryParse(((await limitCmd.ExecuteScalarAsync(ct)) as string)?.Trim('"'), out var l) ? l : 20000;
        List<ScanCredential> dirCreds;
        try { dirCreds = await Credentials.LoadAsync(ds, protector, [d.CredentialId], ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw new PermanentJobException("Directory credential could not be decrypted: " + ex.Message); }
        if (dirCreds.Count == 0) throw new PermanentJobException("The directory credential no longer exists.");

        List<DirectoryComputer> computers;
        try
        {
            computers = await Task.Run(() => DirectoryCollector.ListComputers(d.Server, d.BaseDn, d.Ldaps, dirCreds[0], Math.Min(limit, cfg.MaxTargets), TimeSpan.FromSeconds(60)), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var msg = Zentriq.Application.Security.Redactor.Redact(ex.Message);
            await FinishAsync(scanId, "failed", $"Active Directory query failed: {msg}", ct);
            throw new PermanentJobException($"Active Directory query failed ({d.Server}, {d.BaseDn}): {msg}");
        }
        var cutoff = d.InactiveDays is > 0 ? DateTimeOffset.UtcNow.AddDays(-d.InactiveDays.Value) : (DateTimeOffset?)null;
        var targets = new List<string>();
        foreach (var c in computers)
        {
            var name = c.DnsHostName ?? c.Name;
            if (string.IsNullOrWhiteSpace(name)) continue;
            _adObjects[name] = c;
            if (cutoff is not null && c.LastLogon is { } ll && ll < cutoff)
            {
                await RecordAsync(scanId, name, null, name, ScanResultStatus.Skipped, [], $"Not scanned: no logon to the domain since {ll:yyyy-MM-dd} (inactive > {d.InactiveDays} days). {c.DistinguishedName}", null, false, null, ct);
                continue;
            }
            targets.Add(name);
        }
        await context.LogAsync($"Active Directory: {computers.Count} enabled computer object(s) under {d.BaseDn}; {targets.Count} to scan.", ct: ct);
        return targets;
    }

    /// <summary>
    /// Matches by IP then hostname among active assets; creates a new asset when nothing matches,
    /// enforcing the workload limit transactionally.
    /// </summary>
    private async Task<(Guid? Id, bool Created, bool Updated, string? Note)> ReconcileAssetAsync(IPAddress ip, string? hostname, int[] openPorts, Application.Licensing.LicenseStatus license, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        Guid? existing;
        await using (var find = new NpgsqlCommand("""
            SELECT id FROM zentriq.assets
             WHERE status = 'active' AND (primary_ip = @ip OR (@h::text IS NOT NULL AND (lower(hostname) = lower(@h) OR lower(fqdn) = lower(@h))))
             ORDER BY (primary_ip = @ip) DESC, last_seen_at DESC LIMIT 1 FOR UPDATE
            """, conn, tx))
        {
            find.Parameters.Add(new NpgsqlParameter("ip", NpgsqlDbType.Inet) { Value = ip });
            find.Parameters.Add(new NpgsqlParameter("h", NpgsqlDbType.Text) { Value = (object?)hostname ?? DBNull.Value });
            existing = (Guid?)await find.ExecuteScalarAsync(ct);
        }

        if (existing is { } id)
        {
            await using var upd = new NpgsqlCommand("""
                UPDATE zentriq.assets SET last_seen_at = now(), primary_ip = @ip,
                       fqdn = COALESCE(@h, fqdn), hostname = COALESCE(hostname, split_part(@h, '.', 1)),
                       updated_at = now(), row_version = row_version + 1
                 WHERE id = @id
                """, conn, tx);
            upd.Parameters.AddWithValue("id", id);
            upd.Parameters.Add(new NpgsqlParameter("ip", NpgsqlDbType.Inet) { Value = ip });
            upd.Parameters.Add(new NpgsqlParameter("h", NpgsqlDbType.Text) { Value = (object?)hostname ?? DBNull.Value });
            await upd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            return (id, false, true, null);
        }

        var (category, osFamily) = Classify(openPorts);
        try
        {
            await licensing.EnsureCapacityAsync(conn, tx, Workloads.FromAssetCategory(category), 1, license, ct);
        }
        catch (WorkloadLimitExceededException ex)
        {
            await tx.RollbackAsync(ct);
            return (null, false, false, "Asset not added: " + ex.Message);
        }
        await using (var ins = new NpgsqlCommand("""
            INSERT INTO zentriq.assets (id, hostname, fqdn, primary_ip, category, os_family, source, inventory_status, first_seen_at, last_seen_at)
            VALUES (@id, split_part(@h, '.', 1), @h, @ip, @c, @os, 'discovery', 'not_collected', now(), now())
            """, conn, tx))
        {
            var newId = Guid.NewGuid();
            ins.Parameters.AddWithValue("id", newId);
            ins.Parameters.Add(new NpgsqlParameter("h", NpgsqlDbType.Text) { Value = (object?)hostname ?? DBNull.Value });
            ins.Parameters.Add(new NpgsqlParameter("ip", NpgsqlDbType.Inet) { Value = ip });
            ins.Parameters.AddWithValue("c", category);
            ins.Parameters.Add(new NpgsqlParameter("os", NpgsqlDbType.Text) { Value = (object?)osFamily ?? DBNull.Value });
            await ins.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            return (newId, true, false, null);
        }
    }

    /// <summary>Port-based first guess; refined later by credentialed inventory.</summary>
    internal static (string Category, string? OsFamily) Classify(int[] open)
    {
        bool Has(int p) => open.Contains(p);
        if (Has(135) || Has(445) || Has(3389) || Has(5985) || Has(5986)) return ("endpoint", "windows");
        if (Has(22)) return ("server", "linux");
        if (Has(80) || Has(443)) return ("other", null);
        return ("other", null);
    }

    // ---- persistence helpers ----------------------------------------------------------------

    private async Task<(string Status, string Config)?> LoadScanAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT status, config::text FROM zentriq.discovery_scans WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetString(0), r.GetString(1)) : null;
    }

    private async Task<HashSet<string>> ExistingResultsAsync(Guid scanId, CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = ds.CreateCommand("SELECT target FROM zentriq.scan_results WHERE scan_id = @id AND status <> 'cancelled'");
        cmd.Parameters.AddWithValue("id", scanId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) set.Add(r.GetString(0));
        return set;
    }

    private async Task MarkRunningAsync(Guid scanId, int total, int processed, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            UPDATE zentriq.discovery_scans SET status = CASE WHEN status = 'cancelling' THEN status ELSE 'running' END,
                   started_at = COALESCE(started_at, now()), total_targets = @t, processed = @p WHERE id = @id
            """);
        cmd.Parameters.AddWithValue("id", scanId);
        cmd.Parameters.AddWithValue("t", total);
        cmd.Parameters.AddWithValue("p", processed);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task ProgressAsync(Guid scanId, int processed, int created, int updated, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            UPDATE zentriq.discovery_scans SET processed = GREATEST(processed, @p),
                   assets_created = (SELECT count(*) FROM zentriq.scan_results WHERE scan_id = @id AND asset_created),
                   assets_updated = GREATEST(assets_updated, @u)
             WHERE id = @id
            """);
        cmd.Parameters.AddWithValue("id", scanId);
        cmd.Parameters.AddWithValue("p", processed);
        cmd.Parameters.AddWithValue("u", updated);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task FinishAsync(Guid scanId, string status, string? error, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("UPDATE zentriq.discovery_scans SET status = @s, finished_at = now(), error = @e WHERE id = @id");
        cmd.Parameters.AddWithValue("id", scanId);
        cmd.Parameters.AddWithValue("s", status);
        cmd.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.Text) { Value = (object?)error ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<bool> IsCancelRequestedAsync(Guid jobId)
    {
        await using var cmd = ds.CreateCommand("SELECT cancel_requested FROM zentriq.jobs WHERE id = @id");
        cmd.Parameters.AddWithValue("id", jobId);
        return await cmd.ExecuteScalarAsync() is true;
    }

    private async Task RecordCancelledAsync(Guid scanId, List<string> targets, CancellationToken ct)
    {
        var done = await ExistingResultsAsync(scanId, ct);
        foreach (var t in targets.Where(t => !done.Contains(t)).Take(100_000))
            await RecordAsync(scanId, t, null, null, ScanResultStatus.Cancelled, [], "Not probed: scan was cancelled.", null, false, null, ct);
    }

    private async Task RecordAsync(Guid scanId, string target, IPAddress? ip, string? hostname, string status, int[] ports, string detail, int? durationMs, bool created, Guid? assetId, CancellationToken ct,
        string? protocol = null, Guid? credentialId = null)
    {
        await using var cmd = ds.CreateCommand("""
            INSERT INTO zentriq.scan_results (scan_id, target, ip_address, hostname, status, open_ports, detail, asset_id, asset_created, duration_ms, protocol, credential_id)
            VALUES (@s, @t, @ip, @h, @st, @p, @d, @a, @c, @ms, @proto, @cred)
            ON CONFLICT (scan_id, target) DO UPDATE SET status = EXCLUDED.status, ip_address = EXCLUDED.ip_address, hostname = EXCLUDED.hostname,
                open_ports = EXCLUDED.open_ports, detail = EXCLUDED.detail, asset_id = EXCLUDED.asset_id, asset_created = EXCLUDED.asset_created,
                duration_ms = EXCLUDED.duration_ms, protocol = EXCLUDED.protocol, credential_id = EXCLUDED.credential_id, recorded_at = now()
            WHERE zentriq.scan_results.status = 'cancelled'
            """);
        cmd.Parameters.AddWithValue("s", scanId);
        cmd.Parameters.AddWithValue("t", target);
        cmd.Parameters.Add(new NpgsqlParameter("ip", NpgsqlDbType.Inet) { Value = (object?)ip ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("h", NpgsqlDbType.Text) { Value = (object?)hostname ?? DBNull.Value });
        cmd.Parameters.AddWithValue("st", status);
        cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = ports });
        cmd.Parameters.AddWithValue("d", detail);
        cmd.Parameters.Add(new NpgsqlParameter("a", NpgsqlDbType.Uuid) { Value = (object?)assetId ?? DBNull.Value });
        cmd.Parameters.AddWithValue("c", created);
        cmd.Parameters.Add(new NpgsqlParameter("ms", NpgsqlDbType.Integer) { Value = (object?)durationMs ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("proto", NpgsqlDbType.Text) { Value = (object?)protocol ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("cred", NpgsqlDbType.Uuid) { Value = (object?)credentialId ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
