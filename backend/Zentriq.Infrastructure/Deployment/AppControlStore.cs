using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Inventory;

namespace Zentriq.Infrastructure.Deployment;

/// <summary>
/// Application control on the server: the organisation's settings, device groups, the approved list, the policy each
/// agent receives, and the safety net that checks the software an agent reports.
/// </summary>
public static class AppControlStore
{
    public const string SettingsKey = "appcontrol.settings";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed class Settings
    {
        /// <summary>off | audit | block — for all devices; device groups can set their own.</summary>
        public string Mode { get; set; } = "off";
        public string Message { get; set; } = AppControl.DefaultMessage;
        public bool AllowRequests { get; set; } = true;
        /// <summary>Block mode: software that is not approved and was installed after application control was turned on is removed.</summary>
        public bool AutoRemove { get; set; }
        /// <summary>The remote support agent cannot be removed, stopped or disabled on the device without a code.</summary>
        public bool ProtectRemoteAgent { get; set; } = true;
        public long Revision { get; set; } = 1;
        /// <summary>When application control was turned on (audit or block); software installed before is not removed.</summary>
        public DateTimeOffset? EnabledAt { get; set; }
    }

    public sealed record Group(Guid Id, string Name, string? AppMode, string WdacMode);

    public static async Task<Settings> LoadSettingsAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT value::text FROM zentriq.system_settings WHERE key = @k");
        cmd.Parameters.AddWithValue("k", SettingsKey);
        var s = await cmd.ExecuteScalarAsync(ct) is string json ? JsonSerializer.Deserialize<Settings>(json, Json) ?? new() : new();
        if (!AppControl.Modes.Contains(s.Mode)) s.Mode = "off";
        if (string.IsNullOrWhiteSpace(s.Message)) s.Message = AppControl.DefaultMessage;
        return s;
    }

    public static async Task SaveSettingsAsync(NpgsqlDataSource ds, Settings s, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            INSERT INTO zentriq.system_settings (key, value) VALUES (@k, @v::jsonb)
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now()
            """);
        cmd.Parameters.AddWithValue("k", SettingsKey);
        cmd.Parameters.AddWithValue("v", JsonSerializer.Serialize(s, Json));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The approved list, a group or the settings changed: agents fetch their policy again (at once over the live connection).</summary>
    public static async Task<long> BumpRevisionAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            UPDATE zentriq.system_settings SET value = jsonb_set(value, '{revision}', to_jsonb(COALESCE((value ->> 'revision')::bigint, 0) + 1)), updated_at = now()
             WHERE key = @k RETURNING (value ->> 'revision')::bigint
            """);
        cmd.Parameters.AddWithValue("k", SettingsKey);
        return await cmd.ExecuteScalarAsync(ct) is long r ? r : 0;
    }

    /// <summary>"windows" or "linux" for an agent platform (windows-x64, linux-x64, linux-arm64).</summary>
    public static string Family(string? platform) => platform is not null && platform.StartsWith("linux", StringComparison.OrdinalIgnoreCase) ? "linux" : "windows";

    /// <summary>SQL condition: asset <c>a</c> is a member of group <c>g</c>.</summary>
    public const string MemberSql = """
        (a.id = ANY(g.asset_ids) OR a.department_id = ANY(g.department_ids) OR a.location_id = ANY(g.location_ids))
        AND (g.platform IS NULL OR g.platform = a.os_family)
        """;

    public static async Task<List<Group>> GroupsOfAssetAsync(NpgsqlDataSource ds, Guid assetId, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand($"""
            SELECT g.id, g.name, g.app_mode, g.wdac_mode FROM zentriq.device_groups g, zentriq.assets a
             WHERE a.id = @a AND {MemberSql} ORDER BY lower(g.name)
            """);
        cmd.Parameters.AddWithValue("a", assetId);
        var list = new List<Group>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new Group(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3)));
        return list;
    }

    /// <summary>The policy for one device: the mode and WDAC its groups set (the strictest of them) and the rules for its groups and platform.</summary>
    public static async Task<AppControlPolicy> PolicyForAsync(NpgsqlDataSource ds, Guid? assetId, string platform, CancellationToken ct)
    {
        var s = await LoadSettingsAsync(ds, ct);
        var family = Family(platform);
        var groups = assetId is { } id ? await GroupsOfAssetAsync(ds, id, ct) : [];
        var mode = s.Mode;
        var withMode = groups.Where(g => g.AppMode is not null).ToList();
        if (withMode.Count > 0) mode = withMode.Select(g => g.AppMode!).Aggregate("off", AppControl.Stricter);
        var wdac = family == "windows" ? groups.Select(g => g.WdacMode).Aggregate("off", AppControl.StricterWdac) : "off";
        var policy = new AppControlPolicy
        {
            Revision = s.Revision, Mode = mode, Message = s.Message, AllowRequests = s.AllowRequests,
            AutoRemove = s.AutoRemove && mode == "block", Wdac = wdac, ProtectRemoteAgent = s.ProtectRemoteAgent, GeneratedAt = DateTimeOffset.UtcNow,
        };
        await using var cmd = ds.CreateCommand("""
            SELECT id, action, label, name_pattern, publisher_pattern, version_min, version_max, sha256, platform
              FROM zentriq.app_control_rules
             WHERE ((group_id IS NULL AND asset_id IS NULL) OR group_id = ANY(@g) OR asset_id = @a)
               AND (platform IS NULL OR platform = @p) AND (expires_at IS NULL OR expires_at > now())
             ORDER BY action DESC, created_at
            """);
        cmd.Parameters.AddWithValue("g", groups.Select(g => g.Id).ToArray());
        cmd.Parameters.AddWithValue("p", family);
        cmd.Parameters.Add(new NpgsqlParameter("a", NpgsqlDbType.Uuid) { Value = (object?)assetId ?? DBNull.Value });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            policy.Rules.Add(new AppControlRule
            {
                Id = r.GetGuid(0).ToString(), Action = r.GetString(1), Label = r.GetString(2),
                Name = r.IsDBNull(3) ? null : r.GetString(3), Publisher = r.IsDBNull(4) ? null : r.GetString(4),
                VersionMin = r.IsDBNull(5) ? null : r.GetString(5), VersionMax = r.IsDBNull(6) ? null : r.GetString(6),
                Sha256 = r.IsDBNull(7) ? null : r.GetString(7), Platform = r.IsDBNull(8) ? null : r.GetString(8),
            });
        return policy;
    }

    /// <summary>
    /// The safety net, run when an agent sends inventory: software that is not approved and was installed after application
    /// control was turned on is reported (once) and, in block mode with automatic removal, uninstalled through the agent.
    /// Returns the number of items reported.
    /// </summary>
    public static async Task<int> CheckInventoryAsync(NpgsqlDataSource ds, Guid assetId, Guid agentId, string platform, IReadOnlyList<SoftwareItem> software,
        IEnumerable<string> agentCapabilities, CancellationToken ct)
    {
        var policy = await PolicyForAsync(ds, assetId, platform, ct);
        if (policy.Mode == "off") return 0;
        var settings = await LoadSettingsAsync(ds, ct);
        var since = settings.EnabledAt ?? DateTimeOffset.UtcNow;
        var family = Family(platform);
        var found = new List<(SoftwareItem Item, AppDecision D)>();
        foreach (var item in software)
        {
            // Only what was installed after application control was turned on (earlier software is the starting point: approve or remove it).
            if (item.InstallDate is not { } at || at.Date < since.UtcDateTime.Date) continue;
            if (item.Source == "appx") continue;
            var d = AppControl.Evaluate(policy, new SoftwareCandidate { Name = item.Name, Publisher = item.Publisher, Version = item.Version, Platform = family });
            if (!d.Approved) found.Add((item, d));
        }
        if (found.Count == 0) return 0;
        var remove = policy.AutoRemove && agentCapabilities.Contains("software-uninstall");
        var reported = 0;
        foreach (var (item, d) in found.Take(50))
        {
            await using var ev = ds.CreateCommand("""
                INSERT INTO zentriq.app_control_events (asset_id, agent_id, occurred_at, kind, name, publisher, version, platform, detail)
                SELECT @a, @ag, COALESCE(@at, now()), @k, @n, @p, @v, @pf, @d
                 WHERE NOT EXISTS (SELECT 1 FROM zentriq.app_control_events e WHERE e.asset_id = @a AND lower(e.name) = lower(@n)
                                     AND COALESCE(e.version, '') = COALESCE(@v, '') AND e.kind IN ('detected','removed','remove-failed')
                                     AND e.received_at > now() - interval '30 days')
                RETURNING id
                """);
            ev.Parameters.AddWithValue("a", assetId);
            ev.Parameters.AddWithValue("ag", agentId);
            ev.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = (object?)item.InstallDate ?? DBNull.Value });
            ev.Parameters.AddWithValue("k", "detected");
            ev.Parameters.AddWithValue("n", item.Name);
            ev.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Text) { Value = (object?)item.Publisher ?? DBNull.Value });
            ev.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Text) { Value = (object?)item.Version ?? DBNull.Value });
            ev.Parameters.AddWithValue("pf", family);
            ev.Parameters.AddWithValue("d", d.Reason + (remove ? " It is being removed (automatic removal is on)." : ""));
            if (await ev.ExecuteScalarAsync(ct) is null) continue;
            reported++;
            if (!remove) continue;
            await using var act = ds.CreateCommand("""
                INSERT INTO zentriq.asset_actions (asset_id, agent_id, channel, kind, target, operation, argument, detail)
                SELECT @a, @ag, 'agent', 'uninstall', @n, 'uninstall', @v, 'Removed by application control: not on the approved software list.'
                 WHERE NOT EXISTS (SELECT 1 FROM zentriq.asset_actions x WHERE x.asset_id = @a AND x.kind = 'uninstall' AND lower(x.target) = lower(@n)
                                     AND x.requested_at > now() - interval '24 hours')
                """);
            act.Parameters.AddWithValue("a", assetId);
            act.Parameters.AddWithValue("ag", agentId);
            act.Parameters.AddWithValue("n", item.Name);
            act.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Text) { Value = family == "windows" ? (object?)item.Version ?? DBNull.Value : DBNull.Value });
            await act.ExecuteNonQueryAsync(ct);
        }
        return reported;
    }

    /// <summary>Marks a removal by application control as done (or failed) once the agent reports the uninstall result.</summary>
    public static async Task RecordRemovalAsync(NpgsqlDataSource ds, Guid actionId, bool ok, string? message, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            INSERT INTO zentriq.app_control_events (asset_id, agent_id, occurred_at, kind, name, version, platform, detail)
            SELECT x.asset_id, x.agent_id, now(), CASE WHEN @ok THEN 'removed' ELSE 'remove-failed' END, x.target, x.argument,
                   CASE WHEN COALESCE(r.platform, 'windows-x64') LIKE 'linux%' THEN 'linux' ELSE 'windows' END, @m
              FROM zentriq.asset_actions x LEFT JOIN zentriq.agent_registrations r ON r.id = x.agent_id
             WHERE x.id = @id AND x.kind = 'uninstall' AND x.requested_by IS NULL AND x.detail LIKE 'Removed by application control%'
            """);
        cmd.Parameters.AddWithValue("id", actionId);
        cmd.Parameters.AddWithValue("ok", ok);
        cmd.Parameters.Add(new NpgsqlParameter("m", NpgsqlDbType.Text) { Value = (object?)message ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
