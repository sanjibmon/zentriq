using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Zentriq.Api.Infrastructure;
using Zentriq.Application.Discovery;
using Zentriq.Domain;
using Zentriq.Infrastructure.Data;
using Zentriq.Infrastructure.Jobs;

namespace Zentriq.Api.Endpoints;

public static class DiscoveryEndpoints
{
    public sealed record ScanRequest(string? Name, string ScanType, string? TargetsText, string? ExcludeText, int? Concurrency, int? TimeoutMs, int[]? Ports,
        Guid[]? CredentialIds = null, DirectorySource? Directory = null);
    /// <summary>Active Directory source for scan type "ad": computers under BaseDn become targets.</summary>
    public sealed record DeleteScansBody(Guid[]? Ids, int? OlderThanDays);
    public sealed record DirectorySource(string? Server, string? BaseDn, bool Ldaps, Guid? CredentialId, int? InactiveDays, Guid? ConnectionId = null);
    public sealed record JobEventDto(DateTimeOffset OccurredAt, string Level, string Message);
    public sealed record ProfileBody(string Name, string? Description, ScanRequest Config, bool Shared);

    /// <summary>Persisted scan configuration consumed by the worker (no secrets).</summary>
    public sealed record ScanConfig(string ScanType, string TargetsText, string ExcludeText, int Concurrency, int TimeoutMs, int[] Ports, IReadOnlyList<string> ApprovedScopes, int MaxTargets,
        Guid[]? CredentialIds = null, DirectorySource? Directory = null);

    public static readonly int[] DefaultPorts = [22, 80, 135, 139, 443, 445, 3389, 5985, 5986, 161];
    private static readonly string[] SupportedTypes = ["ip", "single", "list", "ad"];

    public static void MapDiscoveryEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/discovery").WithTags("Discovery").RequireModule(Modules.Discovery);

        g.MapGet("/safeguards", async (ZentriqDbContext db, CancellationToken ct) => Results.Ok(await LoadSafeguardsAsync(db, ct)))
            .RequireAuthorization(Permissions.AssetsView);

        g.MapPost("/scans/validate", async (ScanRequest body, ZentriqDbContext db, CancellationToken ct) =>
        {
            var sg = await LoadSafeguardsAsync(db, ct);
            var parsed = TargetParser.Parse(body.TargetsText, body.ExcludeText, sg.Safeguards);
            return Results.Ok(new { valid = parsed.IsValid, errors = parsed.Errors, totalAddresses = parsed.TotalAddresses, confirmAbove = sg.ConfirmAbove });
        }).RequireAuthorization(Permissions.ScansRun);

        // Ad hoc scan: no profile or schedule required.
        g.MapPost("/scans", async (ScanRequest body, ZentriqDbContext db, JobQueue jobs, RequestContext rc, CancellationToken ct) =>
        {
            var (scan, error) = await CreateScanAsync(body, null, db, jobs, rc, ct);
            return error ?? Results.Accepted($"/api/v1/discovery/scans/{scan!.Id}", ScanDto(scan, null));
        }).RequireAuthorization(Permissions.ScansRun);

        g.MapGet("/scans", async (ZentriqDbContext db, string? status, int? page, int? pageSize, CancellationToken ct) =>
        {
            var (p, ps) = Paging.Normalize(page, pageSize);
            var q = db.DiscoveryScans.AsNoTracking().AsQueryable();
            if (status == "active") q = q.Where(s => s.Status == "queued" || s.Status == "running" || s.Status == "cancelling");
            else if (!string.IsNullOrEmpty(status) && status != "all") q = q.Where(s => s.Status == status);
            var total = await q.CountAsync(ct);
            var scans = await q.OrderByDescending(s => s.CreatedAt).Skip((p - 1) * ps).Take(ps).ToListAsync(ct);
            var ids = scans.Select(s => s.Id).ToList();
            var counts = await db.ScanResults.AsNoTracking().Where(r => ids.Contains(r.ScanId))
                .GroupBy(r => new { r.ScanId, r.Status }).Select(x => new { x.Key.ScanId, x.Key.Status, n = x.Count() }).ToListAsync(ct);
            var items = scans.Select(s => ScanDto(s, counts.Where(c => c.ScanId == s.Id).ToDictionary(c => c.Status, c => c.n))).ToList();
            return Results.Ok(new PagedResult<object>(items, total, p, ps));
        }).RequireAuthorization(Permissions.AssetsView);

        g.MapGet("/scans/{id:guid}", async (Guid id, ZentriqDbContext db, CancellationToken ct) =>
        {
            var s = await db.DiscoveryScans.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return Results.NotFound();
            var counts = await db.ScanResults.AsNoTracking().Where(r => r.ScanId == id).GroupBy(r => r.Status)
                .Select(x => new { x.Key, n = x.Count() }).ToDictionaryAsync(x => x.Key, x => x.n, ct);
            var events = s.JobId is null ? new List<JobEventDto>() : await db.JobEvents.AsNoTracking().Where(e => e.JobId == s.JobId)
                .OrderBy(e => e.Id).Take(500).Select(e => new JobEventDto(e.OccurredAt, e.Level, e.Message)).ToListAsync(ct);
            return Results.Ok(new { scan = ScanDto(s, counts), config = JsonDocument.Parse(s.Config).RootElement, log = events });
        }).RequireAuthorization(Permissions.AssetsView);

        g.MapGet("/scans/{id:guid}/results", async (Guid id, ZentriqDbContext db, string? status, string? q, int? page, int? pageSize, CancellationToken ct) =>
        {
            var (p, ps) = Paging.Normalize(page, pageSize);
            var query = db.ScanResults.AsNoTracking().Where(r => r.ScanId == id);
            if (!string.IsNullOrEmpty(status)) query = query.Where(r => r.Status == status);
            if (!string.IsNullOrWhiteSpace(q)) { var t = q.Trim().ToLower(); query = query.Where(r => r.Target.ToLower().Contains(t) || (r.Hostname != null && r.Hostname.ToLower().Contains(t)) || (r.Detail != null && r.Detail.ToLower().Contains(t))); }
            var total = await query.CountAsync(ct);
            var rows = await query.OrderBy(r => r.Id).Skip((p - 1) * ps).Take(ps).ToListAsync(ct);
            return Results.Ok(new PagedResult<object>(rows.Select(r => (object)new
            {
                r.Id, r.Target, ip = r.IpAddress?.ToString(), r.Hostname, r.Status, r.OpenPorts, r.Detail, r.AssetId, r.AssetCreated, r.DurationMs, r.RecordedAt, r.Protocol, r.CredentialId,
            }).ToList(), total, p, ps));
        }).RequireAuthorization(Permissions.AssetsView);

        g.MapPost("/scans/{id:guid}/cancel", async (Guid id, ZentriqDbContext db, JobQueue jobs, RequestContext rc, CancellationToken ct) =>
        {
            var s = await db.DiscoveryScans.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return Results.NotFound();
            if (s.Status is not ("queued" or "running")) return Results.Problem(statusCode: 409, title: "Only queued or running scans can be cancelled.");
            var jobStatus = s.JobId is null ? null : await jobs.RequestCancelAsync(s.JobId.Value, ct);
            s.Status = jobStatus == "cancelled" ? "cancelled" : "cancelling";
            if (s.Status == "cancelled") s.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await rc.AuditAsync("scan.cancel", s.Name, detail: new { s.Id });
            return Results.Ok(ScanDto(s, null));
        }).RequireAuthorization(Permissions.ScansRun);

        // Scan history housekeeping. Running scans must be cancelled first; assets found by a scan are kept.
        g.MapDelete("/scans/{id:guid}", async (Guid id, ZentriqDbContext db, RequestContext rc, CancellationToken ct) =>
        {
            var s = await db.DiscoveryScans.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return Results.NotFound();
            if (s.Status is "queued" or "running" or "cancelling") return Results.Problem(statusCode: 409, title: "Cancel the scan before deleting it.");
            db.DiscoveryScans.Remove(s);
            await db.SaveChangesAsync(ct);
            await rc.AuditAsync("scan.delete", s.Name, detail: new { s.Id, s.Number });
            return Results.NoContent();
        }).RequireAuthorization(Permissions.ScansRun);

        g.MapPost("/scans/delete", async (DeleteScansBody body, ZentriqDbContext db, RequestContext rc, CancellationToken ct) =>
        {
            var q = db.DiscoveryScans.Where(x => x.Status != "queued" && x.Status != "running" && x.Status != "cancelling");
            if (body.Ids is { Length: > 0 }) q = q.Where(x => body.Ids.Contains(x.Id));
            else if (body.OlderThanDays is int days and > 0) q = q.Where(x => x.CreatedAt < DateTimeOffset.UtcNow.AddDays(-days));
            else return Validation.Problem("ids", "Select scans to delete, or give an age in days.");
            var victims = await q.ToListAsync(ct);
            db.DiscoveryScans.RemoveRange(victims);
            await db.SaveChangesAsync(ct);
            await rc.AuditAsync("scan.delete", $"{victims.Count} scan(s)", detail: new { ids = victims.Select(v => v.Id), body.OlderThanDays });
            return Results.Ok(new { deleted = victims.Count });
        }).RequireAuthorization(Permissions.ScansRun);

        g.MapPost("/scans/{id:guid}/retry", async (Guid id, ZentriqDbContext db, JobQueue jobs, RequestContext rc, CancellationToken ct) =>
        {
            var s = await db.DiscoveryScans.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return Results.NotFound();
            var failed = await db.ScanResults.AsNoTracking()
                .Where(r => r.ScanId == id && (r.Status == ScanResultStatus.Unreachable || r.Status == ScanResultStatus.AuthFailed || r.Status == ScanResultStatus.InventoryFailed || r.Status == ScanResultStatus.Cancelled))
                .Select(r => r.Target).ToListAsync(ct);
            if (failed.Count == 0) return Results.Problem(statusCode: 409, title: "There are no failed targets to retry.");
            var cfg = JsonSerializer.Deserialize<ScanConfig>(s.Config, JsonOpts)!;
            var req = new ScanRequest($"{s.Name} (retry)", "list", string.Join('\n', failed), null, cfg.Concurrency, cfg.TimeoutMs, cfg.Ports, cfg.CredentialIds);
            var (scan, error) = await CreateScanAsync(req, s.ProfileId, db, jobs, rc, ct);
            return error ?? Results.Accepted($"/api/v1/discovery/scans/{scan!.Id}", ScanDto(scan, null));
        }).RequireAuthorization(Permissions.ScansRun);

        g.MapGet("/profiles", async (ZentriqDbContext db, RequestContext rc, CancellationToken ct) =>
        {
            var uid = rc.UserId;
            var list = await db.DiscoveryProfiles.AsNoTracking().Where(p => p.Shared || p.OwnerId == uid).OrderBy(p => p.Name).ToListAsync(ct);
            return Results.Ok(list.Select(p => new { p.Id, p.Name, p.Description, p.ScanType, config = JsonDocument.Parse(p.Config).RootElement, p.Enabled, p.Shared, p.OwnerId, p.NextRunAt, p.UpdatedAt }));
        }).RequireAuthorization(Permissions.AssetsView);

        g.MapPost("/profiles", async (ProfileBody body, ZentriqDbContext db, RequestContext rc, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 120) return Validation.Problem("name", "Profile name is required (max 120 characters).");
            if (!SupportedTypes.Contains(body.Config.ScanType)) return Validation.Problem("scanType", "Unsupported scan type.");
            var sg = await LoadSafeguardsAsync(db, ct);
            var parsed = TargetParser.Parse(body.Config.TargetsText, body.Config.ExcludeText, sg.Safeguards);
            if (!parsed.IsValid) return Results.ValidationProblem(new Dictionary<string, string[]> { ["targetsText"] = parsed.Errors.ToArray() });
            var now = DateTimeOffset.UtcNow;
            var p = new DiscoveryProfile
            {
                Id = Guid.NewGuid(), Name = body.Name.Trim(), Description = body.Description?.Trim() ?? "", ScanType = body.Config.ScanType,
                Config = JsonSerializer.Serialize(body.Config, JsonOpts), OwnerId = rc.UserId, Shared = body.Shared, CreatedAt = now, UpdatedAt = now,
            };
            db.DiscoveryProfiles.Add(p);
            await db.SaveChangesAsync(ct);
            await rc.AuditAsync("scan_profile.create", p.Name, detail: new { p.Id });
            return Results.Created($"/api/v1/discovery/profiles/{p.Id}", new { p.Id, p.Name });
        }).RequireAuthorization(Permissions.ScansProfiles);

        g.MapPost("/profiles/{id:guid}/run", async (Guid id, ZentriqDbContext db, JobQueue jobs, RequestContext rc, CancellationToken ct) =>
        {
            var p = await db.DiscoveryProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (p is null) return Results.NotFound();
            if (!p.Enabled) return Results.Problem(statusCode: 409, title: "This profile is disabled.");
            var req = JsonSerializer.Deserialize<ScanRequest>(p.Config, JsonOpts)! with { Name = p.Name };
            var (scan, error) = await CreateScanAsync(req, p.Id, db, jobs, rc, ct);
            return error ?? Results.Accepted($"/api/v1/discovery/scans/{scan!.Id}", ScanDto(scan, null));
        }).RequireAuthorization(Permissions.ScansRun);
    }

    public static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static async Task<(DiscoveryScan? Scan, IResult? Error)> CreateScanAsync(ScanRequest body, Guid? profileId, ZentriqDbContext db, JobQueue jobs, RequestContext rc, CancellationToken ct)
    {
        if (!SupportedTypes.Contains(body.ScanType))
            return (null, Validation.Problem("scanType", "Unsupported scan type."));
        var sg = await LoadSafeguardsAsync(db, ct);

        var credIds = (body.CredentialIds ?? []).Distinct().Take(10).ToArray();
        if (credIds.Length > 0)
        {
            var known = await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM zentriq.credentials WHERE id = ANY({credIds})").ToListAsync(ct);
            if (known.Count != credIds.Length) return (null, Validation.Problem("credentialIds", "One or more selected credentials no longer exist."));
        }
        if (body.ScanType == "ad")
        {
            var d = body.Directory;
            if (d?.ConnectionId is { } connId)
            {
                var row = await db.Database.SqlQuery<string>($"SELECT concat_ws(chr(31), coalesce(server_host, ''), coalesce(base_dn, ''), transport, inactive_days::text) AS \"Value\" FROM zentriq.directory_connections WHERE id = {connId}").FirstOrDefaultAsync(ct);
                if (row is null) return (null, Validation.Problem("directory.connectionId", "The Active Directory connection no longer exists."));
                var f = row.Split('\u001f');
                d = new DirectorySource(f[0], f[1], f[2] == "ldaps", null, d.InactiveDays ?? int.Parse(f[3]), connId);
            }
            else
            {
                if (d is null || string.IsNullOrWhiteSpace(d.Server) || string.IsNullOrWhiteSpace(d.BaseDn))
                    return (null, Validation.Problem("directory", "Choose an Active Directory connection (Discovery > Active Directory)."));
                if (!d.BaseDn.Contains("DC=", StringComparison.OrdinalIgnoreCase))
                    return (null, Validation.Problem("directory.baseDn", "Use a distinguished name, e.g. OU=Servers,DC=corp,DC=example,DC=com."));
                if (d.CredentialId is not { } dirCredId)
                    return (null, Validation.Problem("directory.credentialId", "Choose a directory (LDAP bind) or Windows domain credential with read access to the directory."));
                var kind = await db.Database.SqlQuery<string>($"SELECT kind AS \"Value\" FROM zentriq.credentials WHERE id = {dirCredId}").FirstOrDefaultAsync(ct);
                if (kind is not ("ldap_bind" or "windows_domain"))
                    return (null, Validation.Problem("directory.credentialId", "Choose a directory (LDAP bind) or Windows domain credential with read access to the directory."));
                d = d with { Server = d.Server.Trim(), BaseDn = d.BaseDn.Trim() };
            }
            var adConfig = new ScanConfig("ad", "", body.ExcludeText ?? "", Math.Clamp(body.Concurrency ?? sg.DefaultConcurrency, 1, sg.MaxConcurrency),
                Math.Clamp(body.TimeoutMs ?? sg.ProbeTimeoutMs, 200, 10_000), DefaultPorts, sg.Safeguards.ApprovedScopes, sg.Safeguards.MaxTargets, credIds,
                d);
            return await PersistScanAsync(body, adConfig, 0, profileId, db, jobs, rc, ct);
        }

        var parsed = TargetParser.Parse(body.TargetsText, body.ExcludeText, sg.Safeguards);
        if (body.ScanType == "single" && parsed.Items.Count == 1 && parsed.Items[0].Count != 1)
            return (null, Validation.Problem("targetsText", "A single-host scan takes exactly one IP address or hostname."));
        if (!parsed.IsValid)
        {
            await rc.AuditAsync("scan.create", body.Name, "Failure", new { errors = parsed.Errors });
            return (null, Results.ValidationProblem(new Dictionary<string, string[]> { ["targetsText"] = parsed.Errors.ToArray() }));
        }
        var concurrency = Math.Clamp(body.Concurrency ?? sg.DefaultConcurrency, 1, sg.MaxConcurrency);
        var timeout = Math.Clamp(body.TimeoutMs ?? sg.ProbeTimeoutMs, 200, 10_000);
        var ports = (body.Ports is { Length: > 0 } ? body.Ports : DefaultPorts).Where(p => p is > 0 and < 65536).Distinct().Take(32).ToArray();
        var config = new ScanConfig(body.ScanType, body.TargetsText ?? "", body.ExcludeText ?? "", concurrency, timeout, ports, sg.Safeguards.ApprovedScopes, sg.Safeguards.MaxTargets, credIds);
        return await PersistScanAsync(body, config, parsed.TotalAddresses, profileId, db, jobs, rc, ct);
    }

    private static async Task<(DiscoveryScan? Scan, IResult? Error)> PersistScanAsync(ScanRequest body, ScanConfig config, long totalAddresses, Guid? profileId, ZentriqDbContext db, JobQueue jobs, RequestContext rc, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var scan = new DiscoveryScan
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(body.Name) ? $"{(body.ScanType switch { "single" => "Single host", "ad" => "Active Directory", _ => "IP scan" })} {now:yyyy-MM-dd HH:mm} UTC" : body.Name.Trim()[..Math.Min(body.Name.Trim().Length, 200)],
            ScanType = body.ScanType, ProfileId = profileId, Config = JsonSerializer.Serialize(config, JsonOpts), Status = "queued",
            TotalTargets = (int)Math.Min(totalAddresses, int.MaxValue), CreatedBy = rc.UserId, CreatedAt = now,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.DiscoveryScans.Add(scan);
        await db.SaveChangesAsync(ct);
        scan.JobId = await jobs.EnqueueAsync(new EnqueueRequest(JobTypes.DiscoveryScan, new { scanId = scan.Id }, $"scan:{scan.Id}",
                Priority: 50, MaxAttempts: 3, TimeoutSeconds: 6 * 3600, CreatedBy: rc.UserId),
            (NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction)tx.GetDbTransaction(), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await rc.AuditAsync("scan.create", scan.Name, detail: new { scan.Id, scan.ScanType, targets = scan.TotalTargets, profileId, credentials = config.CredentialIds?.Length ?? 0 });
        return (scan, null);
    }

    public sealed record SafeguardSettings(ScanSafeguards Safeguards, int ConfirmAbove, int DefaultConcurrency, int MaxConcurrency, int ProbeTimeoutMs);

    public static async Task<SafeguardSettings> LoadSafeguardsAsync(ZentriqDbContext db, CancellationToken ct)
    {
        var s = await db.SystemSettings.AsNoTracking().Where(x => x.Key.StartsWith("discovery.")).ToDictionaryAsync(x => x.Key, x => x.Value, ct);
        int I(string k, int d) => s.TryGetValue(k, out var v) && int.TryParse(v.Trim('"'), out var n) ? n : d;
        var scopes = s.TryGetValue("discovery.approved_scopes", out var j) ? JsonSerializer.Deserialize<List<string>>(j) ?? [] : [];
        return new SafeguardSettings(new ScanSafeguards(scopes, I("discovery.max_targets", 65536), I("discovery.min_prefix", 16)),
            I("discovery.confirm_above", 4096), I("discovery.default_concurrency", 32), I("discovery.max_concurrency", 128), I("discovery.probe_timeout_ms", 1500));
    }

    private static object ScanDto(DiscoveryScan s, Dictionary<string, int>? counts) => new
    {
        s.Id, s.Number, s.Name, s.ScanType, s.ProfileId, s.Status, s.TotalTargets, s.Processed, s.AssetsCreated, s.AssetsUpdated,
        s.JobId, s.CreatedAt, s.StartedAt, s.FinishedAt, s.Error, counts = counts ?? new Dictionary<string, int>(),
    };
}
