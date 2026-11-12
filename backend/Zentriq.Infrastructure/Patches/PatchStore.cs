using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Infrastructure.Deployment;

namespace Zentriq.Infrastructure.Patches;

public sealed class PatchSettings
{
    /// <summary>Devices are scanned for missing updates automatically.</summary>
    public bool AutoScan { get; set; } = true;
    public int ScanHours { get; set; } = 24;
    /// <summary>Minutes between the restart notice and the restart (when the updates need one).</summary>
    public int RebootDelayMinutes { get; set; } = 10;
    public string RebootMessage { get; set; } = DefaultRebootMessage;

    public const string DefaultRebootMessage = "Updates were installed on this computer. Save your work: it restarts in a few minutes.";
}

/// <summary>One update the agent found missing (Windows Update Agent, or a package upgrade on Linux).</summary>
public sealed record ScanUpdate(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("kb")] string? Kb,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("classification")] string? Classification,
    [property: JsonPropertyName("severity")] string? Severity,
    [property: JsonPropertyName("product")] string? Product,
    [property: JsonPropertyName("package")] string? Package,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("released")] string? Released,
    [property: JsonPropertyName("size")] long? Size,
    [property: JsonPropertyName("reboot")] bool? Reboot,
    [property: JsonPropertyName("cves")] string[]? Cves,
    [property: JsonPropertyName("url")] string? Url);

/// <summary>The outcome of installing one update.</summary>
public sealed record InstallResult(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("reboot")] bool? Reboot,
    [property: JsonPropertyName("message")] string? Message);

/// <summary>What the agent sends after a scan (and after installing: the results, then a new scan).</summary>
public sealed record ScanReport(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("rebootPending")] bool? RebootPending,
    [property: JsonPropertyName("updates")] List<ScanUpdate>? Updates,
    [property: JsonPropertyName("results")] List<InstallResult>? Results = null,
    [property: JsonPropertyName("apps")] List<ScanApp>? Apps = null,
    [property: JsonPropertyName("appsMessage")] string? AppsMessage = null);

/// <summary>An application with a newer version in winget (Windows agent, build 136).</summary>
public sealed record ScanApp(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("installed")] string? Installed,
    [property: JsonPropertyName("available")] string? Available,
    [property: JsonPropertyName("source")] string? Source);

/// <summary>Which missing updates a deployment installs (policies; a manual "install all missing").</summary>
public sealed record PatchFilter(string[]? Classifications, string[]? Severities, bool ApprovedOnly, int DelayDays);

public sealed record PatchJobRequest(string Name, string[]? PatchIds, bool AllMissing, PatchFilter? Filter, string Reboot, TargetSpec Targets, string? Platform, string? Note, Guid? PolicyId);

public sealed record PatchJobResult(Guid Id, int Sent, int Skipped, int Nothing);

/// <summary>
/// Patch management: missing operating system updates reported by the Zentriq Agent, approvals, deployments (device
/// actions <c>patch/install</c> that install the chosen updates and scan again) and automatic deployment policies.
/// </summary>
public static partial class PatchStore
{
    public const string SettingsKey = "patch.settings";
    public const string Capability = "patch-management";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static readonly string[] Classifications = ["critical", "security", "definition", "updates", "update-rollup", "feature-pack", "service-pack", "driver", "tools", "upgrade", "application", "other"];
    public static readonly string[] Kinds = ["os", "microsoft", "thirdparty"];

    /// <summary>
    /// Operating system (Windows itself, .NET Framework, Defender; every Linux package), other Microsoft software (Office,
    /// SQL Server, Edge … from Windows / Microsoft Update or WSUS) or a third-party application (winget).
    /// </summary>
    public static string KindOf(string platform, string id, string? product)
    {
        if (id.StartsWith("winget:", StringComparison.OrdinalIgnoreCase)) return "thirdparty";
        if (platform == "linux" || string.IsNullOrWhiteSpace(product)) return "os";
        return OsProduct().IsMatch(product) || !MicrosoftProduct().IsMatch(product) ? "os" : "microsoft";
    }

    [GeneratedRegex(@"^(windows|microsoft defender|windows defender|\.net framework|microsoft server operating system)", RegexOptions.IgnoreCase)]
    private static partial Regex OsProduct();
    [GeneratedRegex(@"(office|microsoft 365|sql server|visual studio|exchange|sharepoint|skype|teams|edge|silverlight|dynamics|project|visio|system center|azure|onedrive|lync|developer tools|asp\.net|\.net( [0-9]|$| core))", RegexOptions.IgnoreCase)]
    private static partial Regex MicrosoftProduct();

    /// <summary>A winget application as an update row: id winget:&lt;package&gt;:&lt;available version&gt;.</summary>
    public static ScanUpdate? FromApp(ScanApp a)
    {
        var pkg = a.Id?.Trim();
        var ver = a.Available?.Trim();
        if (pkg is null || ver is null || !PackageName().IsMatch(pkg) || pkg.Length > 128 || ver.Length is 0 or > 60 || !PackageName().IsMatch(ver)) return null;
        var name = string.IsNullOrWhiteSpace(a.Name) ? pkg : a.Name!.Trim();
        var title = $"{name} {ver}" + (string.IsNullOrWhiteSpace(a.Installed) ? "" : $" (installed {a.Installed!.Trim()})");
        return new ScanUpdate($"winget:{pkg}:{ver}", null, title, "application", null, name, pkg, ver, null, null, false, null, null);
    }
    public static readonly string[] Severities = ["critical", "important", "moderate", "low", "unspecified"];

    // ---- settings ---------------------------------------------------------------------------------------------------

    public static async Task<PatchSettings> SettingsAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand($"SELECT value::text FROM zentriq.system_settings WHERE key = '{SettingsKey}'");
        var s = await cmd.ExecuteScalarAsync(ct) is string text ? JsonSerializer.Deserialize<PatchSettings>(text, Json) ?? new() : new();
        if (string.IsNullOrWhiteSpace(s.RebootMessage)) s.RebootMessage = PatchSettings.DefaultRebootMessage;
        return s;
    }

    public static async Task SaveSettingsAsync(NpgsqlDataSource ds, PatchSettings s, CancellationToken ct)
    {
        s.ScanHours = Math.Clamp(s.ScanHours, 1, 168);
        s.RebootDelayMinutes = Math.Clamp(s.RebootDelayMinutes, 1, 240);
        s.RebootMessage = string.IsNullOrWhiteSpace(s.RebootMessage) ? PatchSettings.DefaultRebootMessage : s.RebootMessage.Trim()[..Math.Min(500, s.RebootMessage.Trim().Length)];
        await using var cmd = ds.CreateCommand("""
            INSERT INTO zentriq.system_settings (key, value, updated_at) VALUES (@k, @v::jsonb, now())
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now()
            """);
        cmd.Parameters.AddWithValue("k", SettingsKey);
        cmd.Parameters.AddWithValue("v", JsonSerializer.Serialize(s, Json));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---- normalising what agents report ---------------------------------------------------------------------------

    /// <summary>Windows Update classification names (and the agent's Linux names) to the stored classification.</summary>
    public static string Classification(string? name)
    {
        var n = (name ?? "").Trim().ToLowerInvariant();
        return n switch
        {
            "critical updates" or "critical" => "critical",
            "security updates" or "security" => "security",
            "definition updates" or "definition" => "definition",
            "updates" or "update" or "" or "bugfix" or "enhancement" or "recommended" or "optional" => "updates",
            "update rollups" or "update-rollup" => "update-rollup",
            "feature packs" or "feature-pack" => "feature-pack",
            "service packs" or "service-pack" => "service-pack",
            "drivers" or "driver" => "driver",
            "tools" => "tools",
            "upgrades" or "upgrade" => "upgrade",
            "application" or "applications" => "application",
            _ => Classifications.Contains(n) ? n : "other",
        };
    }

    /// <summary>MSRC severity (Critical, Important, Moderate, Low) and Linux advisory severities to the stored severity.</summary>
    public static string Severity(string? name)
    {
        var n = (name ?? "").Trim().ToLowerInvariant();
        return n switch
        {
            "critical" => "critical",
            "important" or "high" => "important",
            "moderate" or "medium" => "moderate",
            "low" => "low",
            _ => "unspecified",
        };
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:+~\-]{0,199}$")]
    private static partial Regex SafeId();

    /// <summary>A Linux package name the agent may pass to the package manager (no options, no shell characters).</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+\-]{0,199}$")]
    public static partial Regex PackageName();

    public static string? CleanKb(string? kb)
    {
        var s = (kb ?? "").Trim();
        if (s.StartsWith("KB", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return s.Length is > 0 and <= 12 && s.All(char.IsAsciiDigit) ? s : null;
    }

    /// <summary>The stored id of an update reported by a device (null: not usable).</summary>
    public static string? IdOf(ScanUpdate u, string platform)
    {
        if (platform == "linux")
        {
            var pkg = u.Package?.Trim();
            var ver = u.Version?.Trim();
            if (pkg is null || ver is null || !PackageName().IsMatch(pkg) || ver.Length is 0 or > 120) return null;
            return $"linux:{pkg}:{ver}";
        }
        var id = u.Id?.Trim();
        if (id is not null && id.StartsWith("winget:", StringComparison.OrdinalIgnoreCase))
            return u.Package is { } p && u.Version is { } v && PackageName().IsMatch(p.Trim()) && PackageName().IsMatch(v.Trim()) ? $"winget:{p.Trim()}:{v.Trim()}" : null;
        id = id?.ToLowerInvariant();
        return id is not null && SafeId().IsMatch(id) ? id : null;
    }

    /// <summary>Stored ids: Windows update ids in lower case; Linux ids keep the package's case (NetworkManager).</summary>
    public static string NormId(string id)
    {
        var s = id.Trim();
        return s.StartsWith("linux:", StringComparison.Ordinal) || s.StartsWith("winget:", StringComparison.OrdinalIgnoreCase) ? s : s.ToLowerInvariant();
    }

    private static string? T(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length > max ? s.Trim()[..max] : s.Trim();

    // ---- scans ------------------------------------------------------------------------------------------------------

    public sealed record IngestResult(int Missing, int Installed, int Failed);

    /// <summary>Stores a scan of a device: the updates, their state on the device and the scan's outcome.</summary>
    public static async Task<IngestResult> IngestAsync(NpgsqlDataSource ds, Guid assetId, string platform, ScanReport report, CancellationToken ct)
    {
        platform = platform == "linux" ? "linux" : "windows";
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var results = new Dictionary<string, InstallResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in report.Results ?? [])
            if (r.Id is { } rid) results[NormId(rid)] = r;
        var rebootPending = report.RebootPending == true;
        int missing = 0, installed = 0, failed = 0;

        if (report.Ok)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var packages = new HashSet<string>(StringComparer.Ordinal);
            // Third-party applications (winget) are reported apart; without a winget answer their rows stay as they were.
            var apps = platform == "windows" && report.Apps is not null ? report.Apps.Select(FromApp).OfType<ScanUpdate>() : Enumerable.Empty<ScanUpdate>();
            foreach (var u in (report.Updates ?? []).Take(5000).Concat(apps.Take(1000)))
            {
                var id = IdOf(u, platform);
                if (id is null || !seen.Add(id)) continue;
                var winget = id.StartsWith("winget:", StringComparison.Ordinal);
                if (platform == "linux" || winget) packages.Add(u.Package!.Trim());
                var title = T(u.Title, 500) ?? (platform == "linux" ? $"{u.Package} {u.Version}" : id);
                DateOnly? released = DateTime.TryParse(u.Released, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var rd) ? DateOnly.FromDateTime(rd) : null;
                await using (var up = new NpgsqlCommand("""
                    INSERT INTO zentriq.patches (id, platform, kb, title, classification, severity, product, package, version, released_at, size_bytes, reboot, cves, support_url, kind)
                    VALUES (@id, @pf, @kb, @t, @c, @s, @p, @pkg, @v, @rel, @size, @rb, @cves, @url, @kind)
                    ON CONFLICT (id) DO UPDATE SET title = EXCLUDED.title, kind = EXCLUDED.kind, kb = COALESCE(EXCLUDED.kb, patches.kb), classification = EXCLUDED.classification,
                        severity = CASE WHEN EXCLUDED.severity = 'unspecified' THEN patches.severity ELSE EXCLUDED.severity END,
                        product = COALESCE(EXCLUDED.product, patches.product), released_at = COALESCE(EXCLUDED.released_at, patches.released_at),
                        size_bytes = COALESCE(EXCLUDED.size_bytes, patches.size_bytes), reboot = COALESCE(EXCLUDED.reboot, patches.reboot),
                        cves = CASE WHEN cardinality(EXCLUDED.cves) > 0 THEN EXCLUDED.cves ELSE patches.cves END,
                        support_url = COALESCE(EXCLUDED.support_url, patches.support_url), last_seen_at = now()
                    """, conn, tx))
                {
                    up.Parameters.AddWithValue("id", id);
                    up.Parameters.AddWithValue("pf", platform);
                    up.Parameters.Add(new NpgsqlParameter("kb", NpgsqlDbType.Text) { Value = (object?)CleanKb(u.Kb) ?? DBNull.Value });
                    up.Parameters.AddWithValue("t", title);
                    up.Parameters.AddWithValue("c", Classification(u.Classification));
                    up.Parameters.AddWithValue("s", Severity(u.Severity));
                    up.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Text) { Value = (object?)T(u.Product, 200) ?? DBNull.Value });
                    up.Parameters.Add(new NpgsqlParameter("pkg", NpgsqlDbType.Text) { Value = platform == "linux" || winget ? u.Package!.Trim() : DBNull.Value });
                    up.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Text) { Value = platform == "linux" || winget ? u.Version!.Trim() : DBNull.Value });
                    up.Parameters.AddWithValue("kind", KindOf(platform, id, u.Product));
                    up.Parameters.Add(new NpgsqlParameter("rel", NpgsqlDbType.Date) { Value = (object?)released ?? DBNull.Value });
                    up.Parameters.Add(new NpgsqlParameter("size", NpgsqlDbType.Bigint) { Value = u.Size is > 0 ? u.Size : DBNull.Value });
                    up.Parameters.Add(new NpgsqlParameter("rb", NpgsqlDbType.Boolean) { Value = (object?)u.Reboot ?? DBNull.Value });
                    up.Parameters.AddWithValue("cves", (u.Cves ?? []).Select(c => c.Trim().ToUpperInvariant()).Where(c => c.StartsWith("CVE-", StringComparison.Ordinal) && c.Length <= 30).Distinct().Take(200).ToArray());
                    up.Parameters.Add(new NpgsqlParameter("url", NpgsqlDbType.Text) { Value = (object?)(u.Url is { } url && url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? T(url, 500) : null) ?? DBNull.Value });
                    await up.ExecuteNonQueryAsync(ct);
                }
                results.TryGetValue(id, out var res);
                // Still missing: failed (this attempt or before), waiting for the restart, or missing.
                var state = res is { Ok: false } ? "failed" : res is { Ok: true } ? (res.Reboot == true || rebootPending ? "pending_reboot" : "missing") : null;
                await using (var ap = new NpgsqlCommand("""
                    INSERT INTO zentriq.asset_patches (asset_id, patch_id, state, message) VALUES (@a, @p, COALESCE(@st, 'missing'), @m)
                    ON CONFLICT (asset_id, patch_id) DO UPDATE SET
                        state = CASE WHEN @st IS NOT NULL THEN @st
                                     WHEN asset_patches.state = 'pending_reboot' AND @rp THEN 'pending_reboot'
                                     WHEN asset_patches.state = 'failed' THEN 'failed'
                                     ELSE 'missing' END,
                        message = COALESCE(@m, CASE WHEN asset_patches.state IN ('failed','pending_reboot') THEN asset_patches.message END),
                        installed_at = NULL, updated_at = now()
                    RETURNING state
                    """, conn, tx))
                {
                    ap.Parameters.AddWithValue("a", assetId);
                    ap.Parameters.AddWithValue("p", id);
                    ap.Parameters.Add(new NpgsqlParameter("st", NpgsqlDbType.Text) { Value = (object?)state ?? DBNull.Value });
                    ap.Parameters.Add(new NpgsqlParameter("m", NpgsqlDbType.Text) { Value = (object?)T(res?.Message, 1000) ?? DBNull.Value });
                    ap.Parameters.AddWithValue("rp", rebootPending);
                    var st = (string?)await ap.ExecuteScalarAsync(ct);
                    if (st == "failed") failed++;
                    missing++;
                }
            }

            // No longer missing: installed (by Zentriq, by Windows Update itself or by hand) — unless still waiting for
            // the restart. A Linux package with a newer version reported replaces the row of the older version.
            await using (var gone = new NpgsqlCommand("""
                WITH old AS (
                    SELECT ap.patch_id, ap.state, p.package FROM zentriq.asset_patches ap JOIN zentriq.patches p ON p.id = ap.patch_id
                     WHERE ap.asset_id = @a AND p.platform = @pf AND ap.state <> 'installed' AND NOT (ap.patch_id = ANY(@seen))
                       AND (p.kind <> 'thirdparty' OR @apps)),
                del AS (
                    DELETE FROM zentriq.asset_patches ap USING old WHERE ap.asset_id = @a AND ap.patch_id = old.patch_id AND old.package = ANY(@pkgs) RETURNING ap.patch_id)
                UPDATE zentriq.asset_patches ap SET state = 'installed', installed_at = now(), updated_at = now(), message = NULL
                  FROM old
                 WHERE ap.asset_id = @a AND ap.patch_id = old.patch_id AND NOT (old.package IS NOT NULL AND old.package = ANY(@pkgs))
                   AND NOT (old.state = 'pending_reboot' AND @rp)
                """, conn, tx))
            {
                gone.Parameters.AddWithValue("a", assetId);
                gone.Parameters.AddWithValue("pf", platform);
                gone.Parameters.AddWithValue("seen", seen.ToArray());
                gone.Parameters.AddWithValue("pkgs", packages.ToArray());
                gone.Parameters.AddWithValue("rp", rebootPending);
                gone.Parameters.AddWithValue("apps", report.Apps is not null);
                installed = await gone.ExecuteNonQueryAsync(ct);
            }
            // Installed by this deployment and waiting for the restart (Windows no longer lists them as missing).
            var waiting = results.Where(kv => kv.Value is { Ok: true, Reboot: true }).Select(kv => kv.Key).ToArray();
            if (waiting.Length > 0 && rebootPending)
            {
                await using var rb = new NpgsqlCommand("""
                    UPDATE zentriq.asset_patches SET state = 'pending_reboot', installed_at = NULL, message = 'Installed; the device needs a restart to finish.', updated_at = now()
                     WHERE asset_id = @a AND patch_id = ANY(@ids) AND state = 'installed'
                    """, conn, tx);
                rb.Parameters.AddWithValue("a", assetId);
                rb.Parameters.AddWithValue("ids", waiting);
                installed -= await rb.ExecuteNonQueryAsync(ct);
            }
        }
        else
        {
            // The scan failed: install results still count (an update installed before the scan failed).
            foreach (var (id, res) in results)
            {
                await using var ap = new NpgsqlCommand("""
                    UPDATE zentriq.asset_patches SET state = @st, message = @m, updated_at = now(), installed_at = CASE WHEN @st = 'installed' THEN now() END
                     WHERE asset_id = @a AND patch_id = @p
                    """, conn, tx);
                ap.Parameters.AddWithValue("a", assetId);
                ap.Parameters.AddWithValue("p", id);
                ap.Parameters.AddWithValue("st", !res.Ok ? "failed" : res.Reboot == true ? "pending_reboot" : "installed");
                ap.Parameters.Add(new NpgsqlParameter("m", NpgsqlDbType.Text) { Value = (object?)T(res.Message, 1000) ?? DBNull.Value });
                await ap.ExecuteNonQueryAsync(ct);
            }
        }

        await using (var sc = new NpgsqlCommand("""
            INSERT INTO zentriq.asset_patch_scans (asset_id, scanned_at, ok, message, source, reboot_pending, missing)
            VALUES (@a, now(), @ok, @m, @src, @rp, @n)
            ON CONFLICT (asset_id) DO UPDATE SET scanned_at = now(), ok = EXCLUDED.ok, message = EXCLUDED.message,
                source = COALESCE(EXCLUDED.source, asset_patch_scans.source), reboot_pending = EXCLUDED.reboot_pending,
                missing = CASE WHEN EXCLUDED.ok THEN EXCLUDED.missing ELSE asset_patch_scans.missing END
            """, conn, tx))
        {
            sc.Parameters.AddWithValue("a", assetId);
            sc.Parameters.AddWithValue("ok", report.Ok);
            sc.Parameters.Add(new NpgsqlParameter("m", NpgsqlDbType.Text) { Value = (object?)T(report.Ok ? report.AppsMessage : report.Message, 1000) ?? DBNull.Value });
            sc.Parameters.Add(new NpgsqlParameter("src", NpgsqlDbType.Text) { Value = (object?)T(report.Source, 200) ?? DBNull.Value });
            sc.Parameters.AddWithValue("rp", rebootPending);
            sc.Parameters.AddWithValue("n", missing);
            await sc.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return new IngestResult(missing, installed, failed);
    }

    /// <summary>Asks devices for a scan now (one open scan per device). Returns how many were asked.</summary>
    public static async Task<int> RequestScanAsync(NpgsqlDataSource ds, Guid[] assetIds, Guid? userId, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand($"""
            INSERT INTO zentriq.asset_actions (asset_id, agent_id, channel, kind, target, operation, requested_by, detail)
            SELECT a.id, r.id, 'agent', 'patch', 'scan', 'scan', @by, 'Scan for missing updates'
              FROM zentriq.assets a
              JOIN LATERAL (SELECT x.id FROM zentriq.agent_registrations x WHERE x.asset_id = a.id AND x.status = 'active' AND '{Capability}' = ANY(x.capabilities)
                             ORDER BY x.last_checkin_at DESC NULLS LAST LIMIT 1) r ON true
             WHERE a.id = ANY(@ids) AND a.status = 'active'
               AND NOT EXISTS (SELECT 1 FROM zentriq.asset_actions o WHERE o.asset_id = a.id AND o.kind = 'patch' AND o.status IN ('queued','sent','running')
                                 AND o.requested_at > now() - interval '24 hours')
            """);
        cmd.Parameters.AddWithValue("ids", assetIds);
        cmd.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Uuid) { Value = (object?)userId ?? DBNull.Value });
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Automatic scans: devices whose last scan is older than the scan interval (at most <paramref name="limit"/> per round).</summary>
    public static async Task<int> QueueDueScansAsync(NpgsqlDataSource ds, int scanHours, int limit, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand($"""
            INSERT INTO zentriq.asset_actions (asset_id, agent_id, channel, kind, target, operation, detail, automatic)
            SELECT a.id, r.id, 'agent', 'patch', 'scan', 'scan', 'Scan for missing updates (automatic)', true
              FROM zentriq.assets a
              JOIN LATERAL (SELECT x.id, x.last_checkin_at FROM zentriq.agent_registrations x WHERE x.asset_id = a.id AND x.status = 'active' AND '{Capability}' = ANY(x.capabilities)
                             ORDER BY x.last_checkin_at DESC NULLS LAST LIMIT 1) r ON true
              LEFT JOIN zentriq.asset_patch_scans s ON s.asset_id = a.id
             WHERE a.status = 'active' AND r.last_checkin_at > now() - interval '7 days'
               AND (s.scanned_at IS NULL OR s.scanned_at < now() - make_interval(hours => @h))
               AND NOT EXISTS (SELECT 1 FROM zentriq.asset_actions o WHERE o.asset_id = a.id AND o.kind = 'patch' AND o.status IN ('queued','sent','running')
                                 AND o.requested_at > now() - interval '24 hours')
             ORDER BY s.scanned_at NULLS FIRST
             LIMIT @lim
            """);
        cmd.Parameters.AddWithValue("h", scanHours);
        cmd.Parameters.AddWithValue("lim", limit);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---- approvals --------------------------------------------------------------------------------------------------

    public static async Task<int> SetApprovalAsync(NpgsqlDataSource ds, string[] ids, string approval, Guid? userId, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            UPDATE zentriq.patches SET approval = @ap, approved_by = CASE WHEN @ap = 'none' THEN NULL ELSE @by END, approved_at = CASE WHEN @ap = 'none' THEN NULL ELSE now() END
             WHERE id = ANY(@ids)
            """);
        cmd.Parameters.AddWithValue("ap", approval);
        cmd.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Uuid) { Value = (object?)userId ?? DBNull.Value });
        cmd.Parameters.AddWithValue("ids", ids);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---- deployments ------------------------------------------------------------------------------------------------

    public static string FilterSql => """
        (cardinality(@cls) = 0 OR p.classification = ANY(@cls))
        AND (cardinality(@sev) = 0 OR p.severity = ANY(@sev))
        AND (NOT @appr OR p.approval = 'approved')
        AND (@delay = 0 OR COALESCE(p.released_at, p.first_seen_at::date) <= current_date - @delay)
        """;

    /// <summary>
    /// Creates a deployment: for every device the updates of the request that it is missing (declined updates are only
    /// installed when chosen by name) and one device action that installs them and scans again.
    /// </summary>
    public static async Task<PatchJobResult> CreateJobAsync(NpgsqlDataSource ds, PatchJobRequest req, Guid? userId, CancellationToken ct)
    {
        var settings = await SettingsAsync(ds, ct);
        var targets = await DeviceTargets.ResolveAsync(ds, req.Targets, Capability, ct);
        if (targets.Count > 10000) throw new InvalidOperationException("Deploy to at most 10 000 devices at a time.");
        var ids = (req.PatchIds ?? []).Select(NormId).Where(x => x.Length > 0).Distinct().ToArray();
        var f = req.Filter ?? new PatchFilter(null, null, false, 0);
        var jobId = Guid.NewGuid();
        int sent = 0, skipped = 0, nothing = 0;
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var j = new NpgsqlCommand("""
            INSERT INTO zentriq.patch_jobs (id, policy_id, name, patch_ids, all_missing, reboot, targets, note, created_by)
            VALUES (@id, @pol, @n, @ids, @all, @rb, @t::jsonb, @note, @by)
            """, conn, tx))
        {
            j.Parameters.AddWithValue("id", jobId);
            j.Parameters.Add(new NpgsqlParameter("pol", NpgsqlDbType.Uuid) { Value = (object?)req.PolicyId ?? DBNull.Value });
            j.Parameters.AddWithValue("n", T(req.Name, 200) ?? "Patch deployment");
            j.Parameters.AddWithValue("ids", ids);
            j.Parameters.AddWithValue("all", req.AllMissing);
            j.Parameters.AddWithValue("rb", req.Reboot == "never" ? "never" : "when-needed");
            j.Parameters.AddWithValue("t", req.Targets.ToJson());
            j.Parameters.Add(new NpgsqlParameter("note", NpgsqlDbType.Text) { Value = (object?)T(req.Note, 500) ?? DBNull.Value });
            j.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Uuid) { Value = (object?)userId ?? DBNull.Value });
            await j.ExecuteNonQueryAsync(ct);
        }
        foreach (var t in targets)
        {
            var why = DeviceTargets.WhyNot(t, req.Platform, "patch management");
            var chosen = new List<(string Id, string? Package, string? Kb, string Title)>();
            if (why is null)
            {
                await using var q = new NpgsqlCommand($"""
                    SELECT p.id, p.package, p.kb, p.title FROM zentriq.asset_patches ap JOIN zentriq.patches p ON p.id = ap.patch_id
                     WHERE ap.asset_id = @a AND ap.state IN ('missing','failed') AND p.platform = @pf
                       AND ((@all AND p.approval <> 'declined' AND {FilterSql}) OR (NOT @all AND p.id = ANY(@ids)))
                     ORDER BY CASE p.classification WHEN 'critical' THEN 0 WHEN 'security' THEN 1 ELSE 2 END, p.released_at NULLS LAST
                     LIMIT 500
                    """, conn, tx);
                q.Parameters.AddWithValue("a", t.AssetId);
                q.Parameters.AddWithValue("pf", t.Family);
                q.Parameters.AddWithValue("all", req.AllMissing);
                q.Parameters.AddWithValue("ids", ids);
                q.Parameters.AddWithValue("cls", f.Classifications ?? []);
                q.Parameters.AddWithValue("sev", f.Severities ?? []);
                q.Parameters.AddWithValue("appr", f.ApprovedOnly);
                q.Parameters.AddWithValue("delay", f.DelayDays);
                await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) chosen.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3)));
                if (chosen.Count == 0) { why = "Nothing to install: the device is not missing these updates (as of its last scan)."; nothing++; }
            }
            Guid? actionId = null;
            if (why is null)
            {
                var argument = JsonSerializer.Serialize(new
                {
                    job = jobId,
                    ids = chosen.Select(c => c.Id).ToArray(),
                    packages = chosen.Where(c => c.Package is not null).Select(c => c.Package).Distinct().ToArray(),
                    kbs = chosen.Where(c => c.Kb is not null).Select(c => c.Kb).Distinct().ToArray(),
                    reboot = req.Reboot == "never" ? "never" : "when-needed",
                    rebootDelayMinutes = settings.RebootDelayMinutes,
                    rebootMessage = settings.RebootMessage,
                }, Json);
                await using var a = new NpgsqlCommand("""
                    INSERT INTO zentriq.asset_actions (asset_id, agent_id, channel, kind, target, operation, argument, requested_by, detail, automatic)
                    VALUES (@as, @ag, 'agent', 'patch', @t, 'install', @arg, @by, @d, @auto) RETURNING id
                    """, conn, tx);
                a.Parameters.AddWithValue("as", t.AssetId);
                a.Parameters.AddWithValue("ag", t.AgentId!.Value);
                a.Parameters.AddWithValue("t", jobId.ToString());
                a.Parameters.AddWithValue("arg", argument);
                a.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Uuid) { Value = (object?)userId ?? DBNull.Value });
                a.Parameters.AddWithValue("d", chosen.Count == 1 ? $"Install {chosen[0].Title}" : $"Install {chosen.Count} updates");
                a.Parameters.AddWithValue("auto", req.PolicyId is not null);
                actionId = (Guid)(await a.ExecuteScalarAsync(ct))!;
                sent++;
            }
            else skipped++;
            await using var d = new NpgsqlCommand("INSERT INTO zentriq.patch_job_devices (job_id, asset_id, action_id, patches, skipped) VALUES (@j, @a, @x, @n, @s)", conn, tx);
            d.Parameters.AddWithValue("j", jobId);
            d.Parameters.AddWithValue("a", t.AssetId);
            d.Parameters.Add(new NpgsqlParameter("x", NpgsqlDbType.Uuid) { Value = (object?)actionId ?? DBNull.Value });
            d.Parameters.AddWithValue("n", chosen.Count);
            d.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Text) { Value = (object?)why ?? DBNull.Value });
            await d.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return new PatchJobResult(jobId, sent, skipped, nothing);
    }

    // ---- policies ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The start of the maintenance window that <paramref name="now"/> (server local time) falls in, or null. A window
    /// starts on one of <paramref name="days"/> (0 = Sunday) at <paramref name="start"/> and lasts <paramref name="hours"/>
    /// (it may run past midnight).
    /// </summary>
    public static DateTime? WindowStart(IReadOnlyCollection<int> days, TimeOnly start, int hours, DateTime now)
    {
        for (var back = 0; back <= 1; back++)
        {
            var day = now.Date.AddDays(-back);
            if (!days.Contains((int)day.DayOfWeek)) continue;
            var s = day + start.ToTimeSpan();
            if (now >= s && now < s.AddHours(hours)) return s;
        }
        return null;
    }

    public sealed record DuePolicy(Guid Id, string Name, string Platform, string[] Classifications, string[] Severities, bool ApprovedOnly, int DelayDays, string Targets, string Reboot);

    /// <summary>Policies whose maintenance window is open and that have not run in it yet: one deployment each.</summary>
    public static async Task<List<(DuePolicy Policy, PatchJobResult Result)>> RunDuePoliciesAsync(NpgsqlDataSource ds, DateTime localNow, CancellationToken ct)
    {
        var due = new List<DuePolicy>();
        await using (var cmd = ds.CreateCommand("""
            SELECT id, name, platform, classifications, severities, approved_only, delay_days, targets::text, reboot, days, start_time, window_hours, last_run_at
              FROM zentriq.patch_policies WHERE enabled
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var ws = WindowStart(r.GetFieldValue<int[]>(9), r.GetFieldValue<TimeOnly>(10), r.GetInt32(11), localNow);
                if (ws is null) continue;
                var last = r.IsDBNull(12) ? (DateTime?)null : r.GetFieldValue<DateTimeOffset>(12).LocalDateTime;
                if (last is not null && last >= ws) continue;
                due.Add(new DuePolicy(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetFieldValue<string[]>(3), r.GetFieldValue<string[]>(4), r.GetBoolean(5), r.GetInt32(6), r.GetString(7), r.GetString(8)));
            }
        var done = new List<(DuePolicy, PatchJobResult)>();
        foreach (var p in due)
        {
            await using (var mark = ds.CreateCommand("UPDATE zentriq.patch_policies SET last_run_at = now() WHERE id = @id"))
            {
                mark.Parameters.AddWithValue("id", p.Id);
                await mark.ExecuteNonQueryAsync(ct);
            }
            var res = await CreateJobAsync(ds, new PatchJobRequest($"Policy: {p.Name}", null, true, new PatchFilter(p.Classifications, p.Severities, p.ApprovedOnly, p.DelayDays),
                p.Reboot, TargetSpec.FromJson(p.Targets), p.Platform, null, p.Id), null, ct);
            done.Add((p, res));
        }
        return done;
    }
}
