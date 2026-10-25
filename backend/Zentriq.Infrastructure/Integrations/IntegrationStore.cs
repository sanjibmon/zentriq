using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Infrastructure.Mail;
using Zentriq.Infrastructure.Secrets;

namespace Zentriq.Infrastructure.Integrations;

/// <summary>
/// Integrations & automation: channel storage (secrets encrypted), event detectors (watermark per detector, deduplicated
/// events), routing of events to alert rules (severity, device filters, throttling) and the delivery queue with retries.
/// </summary>
public sealed class IntegrationStore(NpgsqlDataSource ds, CredentialProtector protector, ChannelSender sender, MailService mail)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int MaxAttempts = 5;

    // ---- channels ---------------------------------------------------------------------------------------

    public string ProtectSecrets(Guid channelId, ChannelSecrets s) =>
        protector.Protect(channelId, new CredentialSecret { Password = JsonSerializer.Serialize(s, Json) });

    public ChannelSecrets UnprotectSecrets(Guid channelId, string? value)
    {
        if (string.IsNullOrEmpty(value)) return new ChannelSecrets();
        var json = protector.Unprotect(channelId, value).Password;
        return string.IsNullOrEmpty(json) ? new ChannelSecrets() : JsonSerializer.Deserialize<ChannelSecrets>(json, Json) ?? new ChannelSecrets();
    }

    public async Task<Channel?> ChannelAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT id, kind, name, enabled, settings::text, secret_protected FROM zentriq.int_channels WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new Channel(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetBoolean(3),
            JsonSerializer.Deserialize<ChannelSettings>(r.GetString(4), Json) ?? new ChannelSettings(),
            UnprotectSecrets(r.GetGuid(0), r.IsDBNull(5) ? null : r.GetString(5)));
    }

    public async Task<string?> ConsoleUrlAsync(CancellationToken ct)
    {
        var s = await mail.LoadAsync(ct);
        return string.IsNullOrWhiteSpace(s.ConsoleUrl) ? null : s.ConsoleUrl.Trim().TrimEnd('/');
    }

    // ---- detectors ----------------------------------------------------------------------------------------

    private sealed record Detector(string Name, string Sql);

    // Each detector reads the window (@from, @to] and returns: event_type, severity, dedupe_key, title, body, asset_id, data (json), link.
    // Per-device events group everything found for a device in one window, so a large evaluation gives one message per device.
    private static readonly Detector[] Detectors =
    [
        new("vulnerability.new", """
            SELECT 'vulnerability.new',
                   CASE WHEN bool_or(c.kev OR c.ms_exploited) THEN 'critical' ELSE replace((array_agg(COALESCE(c.severity,'low') ORDER BY array_position(ARRAY['critical','high','medium','low','none'], c.severity)))[1], 'none', 'info') END,
                   'vulnerability.new:' || f.asset_id || ':' || extract(epoch FROM @to)::bigint,
                   count(*) || ' new vulnerabilit' || CASE WHEN count(*) = 1 THEN 'y' ELSE 'ies' END || ' on ' || COALESCE(a.hostname, zentriq.asset_ipv4(a.id), host(a.primary_ip))
                     || CASE WHEN count(*) FILTER (WHERE c.kev OR c.ms_exploited) > 0 THEN ' (' || count(*) FILTER (WHERE c.kev OR c.ms_exploited) || ' known exploited)' ELSE '' END,
                   string_agg(f.cve_id || ' [' || COALESCE(c.severity, '?') || COALESCE(', CVSS ' || c.cvss_score, '') || CASE WHEN c.kev OR c.ms_exploited THEN ', exploited' ELSE '' END || '] '
                              || f.software_name || ' ' || COALESCE(f.installed_version, f.software_version) || COALESCE(' → fixed in ' || f.fixed_version, '') || COALESCE(' (KB' || f.kb || ')', ''),
                              E'\n' ORDER BY f.risk_score DESC),
                   f.asset_id,
                   jsonb_build_object('findingIds', jsonb_agg(f.id), 'cves', jsonb_agg(DISTINCT f.cve_id), 'count', count(*)),
                   '/assets/' || f.asset_id
              FROM zentriq.vuln_findings f JOIN zentriq.vuln_cves c ON c.cve_id = f.cve_id JOIN zentriq.assets a ON a.id = f.asset_id
             WHERE f.first_seen_at > @from AND f.first_seen_at <= @to AND f.status = 'open' AND a.status = 'active'
             GROUP BY f.asset_id, a.id, a.hostname, a.primary_ip
            """),
        new("vulnerability.overdue", """
            SELECT 'vulnerability.overdue',
                   replace((array_agg(COALESCE(c.severity,'low') ORDER BY array_position(ARRAY['critical','high','medium','low','none'], c.severity)))[1], 'none', 'info'),
                   'vulnerability.overdue:' || f.asset_id || ':' || extract(epoch FROM @to)::bigint,
                   count(*) || ' vulnerabilit' || CASE WHEN count(*) = 1 THEN 'y' ELSE 'ies' END || ' past the due date on ' || COALESCE(a.hostname, zentriq.asset_ipv4(a.id), host(a.primary_ip)),
                   string_agg(f.cve_id || ' ' || f.software_name || ' — due ' || to_char(f.due_at AT TIME ZONE 'UTC', 'YYYY-MM-DD') || COALESCE(', ticket ' || f.ticket, ''), E'\n' ORDER BY f.due_at),
                   f.asset_id,
                   jsonb_build_object('findingIds', jsonb_agg(f.id), 'cves', jsonb_agg(DISTINCT f.cve_id), 'count', count(*)),
                   '/vulnerabilities'
              FROM zentriq.vuln_findings f JOIN zentriq.vuln_cves c ON c.cve_id = f.cve_id JOIN zentriq.assets a ON a.id = f.asset_id
             WHERE f.due_at > @from AND f.due_at <= @to AND f.status IN ('open','in_progress') AND a.status = 'active'
             GROUP BY f.asset_id, a.id, a.hostname, a.primary_ip
            """),
        new("security.failed", """
            SELECT 'security.failed',
                   CASE WHEN bool_or(s.severity = 'high') THEN 'high' WHEN bool_or(s.severity = 'medium') THEN 'medium' ELSE 'low' END,
                   'security.failed:' || s.asset_id || ':' || extract(epoch FROM @to)::bigint,
                   count(*) || ' security check' || CASE WHEN count(*) = 1 THEN '' ELSE 's' END || ' failing on ' || COALESCE(a.hostname, zentriq.asset_ipv4(a.id), host(a.primary_ip)),
                   string_agg(s.rule_id || ' ' || COALESCE(r.title, '') || ' [' || s.severity || ']' || COALESCE(' — ' || s.actual, ''), E'\n' ORDER BY s.rule_id),
                   s.asset_id,
                   jsonb_build_object('rules', jsonb_agg(DISTINCT s.rule_id), 'count', count(*)),
                   '/assets/' || s.asset_id
              FROM zentriq.sec_results s JOIN zentriq.assets a ON a.id = s.asset_id LEFT JOIN zentriq.sec_rules r ON r.id = s.rule_id
             WHERE s.status = 'fail' AND s.first_failed_at > @from AND s.first_failed_at <= @to AND a.status = 'active'
             GROUP BY s.asset_id, a.id, a.hostname, a.primary_ip
            """),
        new("inventory.changed", """
            SELECT c.kind || '.changed',
                   CASE WHEN c.kind = 'hardware' THEN 'medium' WHEN bool_or(c.change = 'uninstalled') OR bool_or(c.change = 'installed' AND c.component = 'Application') THEN 'low' ELSE 'info' END,
                   c.kind || '.changed:' || c.asset_id || ':' || extract(epoch FROM @to)::bigint,
                   count(*) || ' ' || c.kind || ' change' || CASE WHEN count(*) = 1 THEN '' ELSE 's' END || ' on ' || COALESCE(a.hostname, zentriq.asset_ipv4(a.id), host(a.primary_ip)),
                   string_agg(initcap(c.change) || ' ' || c.component || ': ' || c.item || COALESCE(' (' || c.old_value || ' → ' || c.new_value || ')', COALESCE(' (' || c.new_value || ')', '')), E'\n' ORDER BY c.id),
                   c.asset_id,
                   jsonb_build_object('count', count(*), 'changes', jsonb_agg(jsonb_build_object('change', c.change, 'component', c.component, 'item', c.item, 'before', c.old_value, 'after', c.new_value) ORDER BY c.id)),
                   '/assets/' || c.asset_id || '?tab=changes'
              FROM zentriq.asset_changes c JOIN zentriq.assets a ON a.id = c.asset_id
             WHERE c.detected_at > @from AND c.detected_at <= @to AND a.status = 'active'
             GROUP BY c.kind, c.asset_id, a.id, a.hostname, a.primary_ip
            """),
        new("asset.discovered", """
            SELECT 'asset.discovered', 'info', 'asset.discovered:' || a.id,
                   'New device: ' || COALESCE(a.hostname, zentriq.asset_ipv4(a.id), host(a.primary_ip), a.id::text),
                   concat_ws(E'\n', 'Category: ' || a.category, 'Operating system: ' || a.os_name, 'IP address: ' || COALESCE(zentriq.asset_ipv4(a.id), host(a.primary_ip)), 'Found by: ' || a.source),
                   a.id, jsonb_build_object('source', a.source), '/assets/' || a.id
              FROM zentriq.assets a
             WHERE a.first_seen_at > @from AND a.first_seen_at <= @to AND a.status = 'active'
            """),
        new("asset.offline", """
            SELECT 'asset.offline', 'medium', 'asset.offline:' || a.id || ':' || extract(epoch FROM x.last)::bigint,
                   COALESCE(a.hostname, zentriq.asset_ipv4(a.id), host(a.primary_ip)) || ' not seen for ' || @hours || ' hours',
                   'Last contact: ' || to_char(x.last AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI') || ' UTC',
                   a.id, jsonb_build_object('lastContact', x.last), '/assets/' || a.id
              FROM zentriq.assets a
              CROSS JOIN LATERAL (SELECT GREATEST(a.last_seen_at, (SELECT max(r.last_checkin_at) FROM zentriq.agent_registrations r WHERE r.asset_id = a.id AND r.status = 'active')) AS last) x
             WHERE a.status = 'active' AND x.last + make_interval(hours => @hours) > @from AND x.last + make_interval(hours => @hours) <= @to
            """),
        new("agent.offline", """
            SELECT 'agent.offline', 'medium', 'agent.offline:' || r.id || ':' || extract(epoch FROM r.last_checkin_at)::bigint,
                   'Agent on ' || COALESCE(r.hostname, a.hostname, 'unknown device') || ' has not checked in for ' || @hours || ' hours',
                   'Last check-in: ' || to_char(r.last_checkin_at AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI') || ' UTC' || COALESCE(E'\nAgent version: ' || r.agent_version, ''),
                   r.asset_id, jsonb_build_object('agentId', r.id, 'lastCheckin', r.last_checkin_at), '/agents'
              FROM zentriq.agent_registrations r LEFT JOIN zentriq.assets a ON a.id = r.asset_id
             WHERE r.status = 'active' AND r.last_checkin_at + make_interval(hours => @hours) > @from AND r.last_checkin_at + make_interval(hours => @hours) <= @to
            """),
        new("scan", """
            SELECT CASE WHEN s.status IN ('failed','cancelled') THEN 'scan.failed' ELSE 'scan.completed' END,
                   CASE WHEN s.status = 'failed' THEN 'medium' WHEN s.status = 'completed_errors' THEN 'low' ELSE 'info' END,
                   'scan:' || s.id,
                   'Discovery scan "' || s.name || '" ' || CASE s.status WHEN 'completed' THEN 'completed' WHEN 'completed_errors' THEN 'completed with errors' WHEN 'cancelled' THEN 'was cancelled' ELSE 'failed' END,
                   concat_ws(E'\n', s.processed || ' of ' || s.total_targets || ' target(s) checked', s.assets_created || ' new device(s), ' || s.assets_updated || ' updated', 'Error: ' || s.error),
                   NULL::uuid, jsonb_build_object('scanId', s.id, 'number', s.number, 'status', s.status, 'newDevices', s.assets_created), '/discovery'
              FROM zentriq.discovery_scans s
             WHERE s.finished_at > @from AND s.finished_at <= @to AND s.status IN ('completed','completed_errors','failed','cancelled')
            """),
        new("accessory.low", """
            SELECT 'accessory.low', CASE WHEN s.quantity = 0 THEN 'medium' ELSE 'low' END,
                   'accessory.low:' || s.item_id || ':' || s.location_id || ':' || extract(epoch FROM s.low_since)::bigint,
                   i.name || CASE WHEN s.quantity = 0 THEN ' is out of stock at ' ELSE ' is low at ' END || lo.name || ' (' || s.quantity || ' left)',
                   concat_ws(E'\n', 'Reorder level: ' || i.reorder_level, 'In stock: ' || s.quantity, 'Faulty: ' || NULLIF(s.faulty, 0), 'Category: ' || i.category),
                   NULL::uuid, jsonb_build_object('itemId', s.item_id, 'locationId', s.location_id, 'quantity', s.quantity, 'reorderLevel', i.reorder_level),
                   '/accessories?tab=stock&low=1'
              FROM zentriq.accessory_stock s JOIN zentriq.accessory_items i ON i.id = s.item_id JOIN zentriq.locations lo ON lo.id = s.location_id
             WHERE s.low_since > @from AND s.low_since <= @to AND i.active
            """),
        new("lifecycle.warranty", """
            SELECT 'lifecycle.warranty', CASE WHEN l.warranty_end <= x.at::date THEN 'medium' ELSE 'low' END,
                   'lifecycle.warranty:' || a.id || ':' || l.warranty_end || ':' || x.k,
                   'Warranty of ' || COALESCE(l.asset_tag || ' ', '') || COALESCE(a.hostname, a.serial_number, 'IT asset') ||
                       CASE WHEN x.k = 'end' THEN ' ended' ELSE ' ends on ' || to_char(l.warranty_end, 'YYYY-MM-DD') END,
                   concat_ws(E'\n', 'Provider: ' || l.warranty_provider, 'Type: ' || l.warranty_type, 'Assigned to: ' || l.assigned_to, 'Stage: ' || l.stage),
                   a.id, jsonb_build_object('warrantyEnd', l.warranty_end, 'tag', l.asset_tag), '/lifecycle/assets/' || a.id
              FROM zentriq.asset_lifecycle l JOIN zentriq.assets a ON a.id = l.asset_id
              CROSS JOIN LATERAL (VALUES ('30', (l.warranty_end - 30)::timestamptz), ('end', l.warranty_end::timestamptz)) AS x(k, at)
             WHERE l.warranty_end IS NOT NULL AND l.stage NOT IN ('retired','disposed') AND x.at > @from AND x.at <= @to
            """),
        new("lifecycle.contract", """
            SELECT 'lifecycle.contract', CASE WHEN x.k = 'end' THEN 'medium' ELSE 'low' END,
                   'lifecycle.contract:' || c.id || ':' || c.end_date || ':' || x.k,
                   initcap(c.kind) || ' contract "' || c.name || '"' || CASE WHEN x.k = 'end' THEN ' ended' ELSE ' ends on ' || to_char(c.end_date, 'YYYY-MM-DD') END,
                   concat_ws(E'\n', 'Vendor: ' || c.vendor, 'Reference: ' || c.reference,
                             (SELECT count(*) FROM zentriq.asset_contract_assets ca WHERE ca.contract_id = c.id) || ' asset(s) covered'),
                   NULL::uuid, jsonb_build_object('contractId', c.id, 'endDate', c.end_date), '/lifecycle?tab=contracts'
              FROM zentriq.asset_contracts c
              CROSS JOIN LATERAL (VALUES ('notice', (c.end_date - c.notify_days)::timestamptz), ('end', c.end_date::timestamptz)) AS x(k, at)
             WHERE c.end_date IS NOT NULL AND x.at > @from AND x.at <= @to AND (x.k = 'end' OR c.notify_days > 0)
            """),
        new("lifecycle.loan_overdue", """
            SELECT 'lifecycle.loan_overdue', 'low', 'lifecycle.loan:' || a.id || ':' || l.expected_return,
                   COALESCE(l.asset_tag || ' ', '') || COALESCE(a.hostname, a.serial_number, 'IT asset') || ' on loan to ' || l.assigned_to || ' was due back on ' || to_char(l.expected_return, 'YYYY-MM-DD'),
                   concat_ws(E'\n', 'E-mail: ' || l.assigned_email, 'Loaned since: ' || to_char(l.assigned_at, 'YYYY-MM-DD')),
                   a.id, jsonb_build_object('person', l.assigned_to, 'expectedReturn', l.expected_return), '/lifecycle/assets/' || a.id
              FROM zentriq.asset_lifecycle l JOIN zentriq.assets a ON a.id = l.asset_id
             WHERE l.status = 'loaned' AND (l.expected_return + 1)::timestamptz > @from AND (l.expected_return + 1)::timestamptz <= @to
            """),
        new("lifecycle.events", """
            SELECT CASE e.kind WHEN 'seen_again' THEN 'lifecycle.retired_seen' ELSE 'lifecycle.missing' END,
                   CASE e.kind WHEN 'seen_again' THEN 'high' ELSE 'medium' END,
                   'lifecycle.event:' || e.id,
                   CASE e.kind WHEN 'seen_again' THEN 'Retired IT asset seen again: ' ELSE 'IT asset not seen, marked missing: ' END ||
                       COALESCE(l.asset_tag || ' ', '') || COALESCE(a.hostname, a.serial_number, 'IT asset'),
                   concat_ws(E'\n', e.reason, 'Stage: ' || l.stage, 'Last custodian: ' || COALESCE(e.person, l.assigned_to)),
                   a.id, jsonb_build_object('eventId', e.id) || e.detail, '/lifecycle/assets/' || a.id
              FROM zentriq.asset_lifecycle_events e JOIN zentriq.assets a ON a.id = e.asset_id JOIN zentriq.asset_lifecycle l ON l.asset_id = a.id
             WHERE e.at > @from AND e.at <= @to AND (e.kind = 'seen_again' OR (e.kind = 'auto' AND e.to_stage = 'missing'))
            """),
        new("cloud.finding", """
            SELECT 'cloud.finding', f.severity, 'cloud.finding:' || f.id || ':' || to_char(f.first_seen_at, 'YYYYMMDDHH24MISS'),
                   f.title || ': ' || COALESCE(r.name, r.external_id),
                   concat_ws(E'\n', f.detail, 'Connection: ' || c.name, 'Type: ' || r.kind, 'Region / group: ' || concat_ws(' / ', r.region, r.resource_group)),
                   r.asset_id, jsonb_build_object('findingId', f.id, 'rule', f.rule, 'resourceId', r.id, 'provider', r.provider), '/cloud/resources/' || r.id
              FROM zentriq.cloud_findings f JOIN zentriq.cloud_resources r ON r.id = f.resource_id JOIN zentriq.cloud_accounts c ON c.id = r.account_id
             WHERE f.first_seen_at > @from AND f.first_seen_at <= @to AND f.resolved_at IS NULL AND NOT f.ignored AND f.severity <> 'info'
            """),
        new("cloud.sync_failed", """
            SELECT 'cloud.sync_failed', 'medium', 'cloud.sync_failed:' || c.id || ':' || to_char(c.last_error_at, 'YYYYMMDDHH24MISS'),
                   'Cloud connection failed: ' || c.name,
                   concat_ws(E'\n', c.last_error, 'Last successful sync: ' || COALESCE(to_char((SELECT max(r.last_seen_at) FROM zentriq.cloud_resources r WHERE r.account_id = c.id), 'YYYY-MM-DD HH24:MI'), 'never')),
                   NULL::uuid, jsonb_build_object('accountId', c.id, 'provider', c.provider), '/cloud?tab=accounts'
              FROM zentriq.cloud_accounts c
             WHERE c.last_error_at > @from AND c.last_error_at <= @to AND c.last_status = 'failed'
            """),
        new("job.failed", """
            SELECT 'job.failed', 'medium', 'job.failed:' || j.id,
                   'Background job failed: ' || j.job_type,
                   COALESCE(left(j.last_error, 1000), 'No error message.') || E'\nAttempts: ' || j.attempts,
                   NULL::uuid, jsonb_build_object('jobId', j.id, 'jobType', j.job_type), '/jobs'
              FROM zentriq.jobs j
             WHERE j.finished_at > @from AND j.finished_at <= @to AND j.status IN ('failed','dead') AND j.job_type NOT LIKE 'maintenance.%'
            """),
    ];

    /// <summary>Runs every detector over the time since it last ran. A detector's first run only sets its watermark.</summary>
    public async Task<int> DetectAsync(CancellationToken ct)
    {
        var hours = await OfflineHoursAsync(ct);
        var to = DateTimeOffset.UtcNow;
        var created = 0;
        await using var conn = await ds.OpenConnectionAsync(ct);
        foreach (var d in Detectors)
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            DateTimeOffset? from;
            await using (var w = new NpgsqlCommand("SELECT watermark FROM zentriq.int_detector_state WHERE detector = @d FOR UPDATE", conn, tx))
            {
                w.Parameters.AddWithValue("d", d.Name);
                var v = await w.ExecuteScalarAsync(ct);
                from = v is DateTime dt ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)) : v as DateTimeOffset?;
            }
            if (from is not null)
            {
                await using var ins = new NpgsqlCommand($"""
                    INSERT INTO zentriq.int_events (event_type, severity, dedupe_key, title, body, asset_id, data, link)
                    SELECT * FROM ({d.Sql}) x
                    ON CONFLICT (dedupe_key) DO NOTHING
                    """, conn, tx);
                ins.Parameters.AddWithValue("from", from.Value);
                ins.Parameters.AddWithValue("to", to);
                ins.Parameters.AddWithValue("hours", hours);
                created += await ins.ExecuteNonQueryAsync(ct);
            }
            await using (var up = new NpgsqlCommand("""
                INSERT INTO zentriq.int_detector_state (detector, watermark) VALUES (@d, @to)
                ON CONFLICT (detector) DO UPDATE SET watermark = EXCLUDED.watermark
                """, conn, tx))
            {
                up.Parameters.AddWithValue("d", d.Name);
                up.Parameters.AddWithValue("to", to);
                await up.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        return created;
    }

    public async Task<int> OfflineHoursAsync(CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT value::text FROM zentriq.system_settings WHERE key = 'integrations.offline_hours'");
        return int.TryParse(await cmd.ExecuteScalarAsync(ct) as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) && h is >= 1 and <= 2160 ? h : 24;
    }

    // ---- routing ----------------------------------------------------------------------------------------------

    private sealed record RuleRow(Guid Id, string EventType, string MinSeverity, RuleConditions Conditions, Guid[] Channels, int Throttle);

    /// <summary>Matches new events to enabled alert rules and queues one delivery per rule channel.</summary>
    public async Task<int> RouteAsync(CancellationToken ct)
    {
        var rules = new List<RuleRow>();
        await using (var cmd = ds.CreateCommand("SELECT id, event_type, min_severity, conditions::text, channel_ids, throttle_minutes FROM zentriq.int_rules WHERE enabled"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                rules.Add(new RuleRow(r.GetGuid(0), r.GetString(1), r.GetString(2), JsonSerializer.Deserialize<RuleConditions>(r.GetString(3), Json) ?? new(),
                    r.GetFieldValue<Guid[]>(4), r.GetInt32(5)));

        var queued = 0;
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var events = new List<(long Id, string Type, string Severity, Guid? AssetId, string? Category, string? Criticality)>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT e.id, e.event_type, e.severity, e.asset_id, a.category, a.criticality
              FROM zentriq.int_events e LEFT JOIN zentriq.assets a ON a.id = e.asset_id
             WHERE e.routed_at IS NULL ORDER BY e.id LIMIT 500 FOR UPDATE OF e SKIP LOCKED
            """, conn, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                events.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetGuid(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));

        foreach (var e in events)
        {
            var asset = e.AssetId is null ? null : new OutboundAsset { Id = e.AssetId.Value, Category = e.Category, Criticality = e.Criticality };
            foreach (var rule in rules.Where(x => x.EventType == e.Type && EventTypes.Rank(e.Severity) >= EventTypes.Rank(x.MinSeverity) && x.Conditions.Matches(asset)))
            {
                var suppressed = false;
                if (rule.Throttle > 0)
                {
                    // Throttle: at most one notification per rule (and device, for device events) in the period.
                    await using var t = new NpgsqlCommand("""
                        SELECT EXISTS (SELECT 1 FROM zentriq.int_deliveries d JOIN zentriq.int_events ev ON ev.id = d.event_id
                                        WHERE d.rule_id = @r AND d.status IN ('queued','sent') AND d.created_at > now() - make_interval(mins => @m)
                                          AND ev.asset_id IS NOT DISTINCT FROM @a)
                        """, conn, tx);
                    t.Parameters.AddWithValue("r", rule.Id);
                    t.Parameters.AddWithValue("m", rule.Throttle);
                    t.Parameters.Add(new NpgsqlParameter("a", NpgsqlDbType.Uuid) { Value = (object?)e.AssetId ?? DBNull.Value });
                    suppressed = (bool)(await t.ExecuteScalarAsync(ct))!;
                }
                await using var ins = new NpgsqlCommand("""
                    INSERT INTO zentriq.int_deliveries (event_id, rule_id, channel_id, status, error)
                    SELECT @e, @r, c.id, CASE WHEN @s THEN 'suppressed' ELSE 'queued' END, CASE WHEN @s THEN 'Throttled by the rule.' END
                      FROM zentriq.int_channels c WHERE c.id = ANY(@ch) AND c.enabled;
                    UPDATE zentriq.int_rules SET last_fired_at = now() WHERE id = @r AND NOT @s;
                    """, conn, tx);
                ins.Parameters.AddWithValue("e", e.Id);
                ins.Parameters.AddWithValue("r", rule.Id);
                ins.Parameters.AddWithValue("s", suppressed);
                ins.Parameters.AddWithValue("ch", rule.Channels);
                var n = await ins.ExecuteNonQueryAsync(ct);
                if (!suppressed) queued += Math.Max(0, n - 1);
            }
        }
        if (events.Count > 0)
        {
            await using var done = new NpgsqlCommand("UPDATE zentriq.int_events SET routed_at = now() WHERE id = ANY(@ids)", conn, tx);
            done.Parameters.AddWithValue("ids", events.Select(e => e.Id).ToArray());
            await done.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return queued;
    }

    // ---- delivery --------------------------------------------------------------------------------------------------

    public async Task<OutboundEvent?> LoadEventAsync(long id, string? consoleUrl, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            SELECT e.id, e.event_type, e.severity, e.title, e.body, e.occurred_at, e.link, e.data::text,
                   a.id, COALESCE(a.hostname, zentriq.asset_ipv4(a.id), host(a.primary_ip)), COALESCE(zentriq.asset_ipv4(a.id), host(a.primary_ip)), a.category, a.criticality, a.os_name
              FROM zentriq.int_events e LEFT JOIN zentriq.assets a ON a.id = e.asset_id WHERE e.id = @id
            """);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new OutboundEvent
        {
            Id = r.GetInt64(0), Type = r.GetString(1), Severity = r.GetString(2), Title = r.GetString(3), Text = r.GetString(4),
            OccurredAt = r.GetFieldValue<DateTimeOffset>(5),
            Link = r.IsDBNull(6) || consoleUrl is null ? null : consoleUrl + r.GetString(6),
            Data = JsonDocument.Parse(r.GetString(7)).RootElement.Clone(),
            Asset = r.IsDBNull(8) ? null : new OutboundAsset
            {
                Id = r.GetGuid(8), Hostname = r.IsDBNull(9) ? null : r.GetString(9), Ip = r.IsDBNull(10) ? null : r.GetString(10),
                Category = r.GetString(11), Criticality = r.GetString(12), Os = r.IsDBNull(13) ? null : r.GetString(13),
            },
        };
    }

    /// <summary>Sends due deliveries. Failures are retried after 1, 5, 15 and 60 minutes; then marked failed.</summary>
    public async Task<(int Sent, int Failed)> DeliverDueAsync(CancellationToken ct)
    {
        var due = new List<(long Id, long EventId, Guid ChannelId, int Attempts)>();
        await using (var cmd = ds.CreateCommand("""
            UPDATE zentriq.int_deliveries d SET next_attempt_at = now() + interval '5 minutes'
             WHERE d.id IN (SELECT id FROM zentriq.int_deliveries WHERE status = 'queued' AND next_attempt_at <= now() ORDER BY id LIMIT 50 FOR UPDATE SKIP LOCKED)
            RETURNING d.id, d.event_id, d.channel_id, d.attempts
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) due.Add((r.GetInt64(0), r.GetInt64(1), r.GetGuid(2), r.GetInt32(3)));
        if (due.Count == 0) return (0, 0);

        var consoleUrl = await ConsoleUrlAsync(ct);
        var channels = new Dictionary<Guid, Channel?>();
        int sent = 0, failed = 0;
        foreach (var d in due.OrderBy(x => x.Id))
        {
            if (!channels.TryGetValue(d.ChannelId, out var ch)) channels[d.ChannelId] = ch = await ChannelAsync(d.ChannelId, ct);
            var ev = await LoadEventAsync(d.EventId, consoleUrl, ct);
            SendResult res;
            if (ch is null || ev is null) res = new SendResult(false, null, "The channel or event no longer exists.", Permanent: true);
            else if (!ch.Enabled) res = new SendResult(false, null, "The channel is turned off.", Permanent: true);
            else res = await sender.SendAsync(ch, ev, ct);
            if (res.Ok) sent++; else failed++;
            await RecordAsync(d.Id, d.ChannelId, d.Attempts + 1, res, ev, ch?.Kind, ct);
        }
        return (sent, failed);
    }

    public static TimeSpan RetryDelay(int attempts) => attempts switch { 1 => TimeSpan.FromMinutes(1), 2 => TimeSpan.FromMinutes(5), 3 => TimeSpan.FromMinutes(15), _ => TimeSpan.FromMinutes(60) };

    private async Task RecordAsync(long deliveryId, Guid channelId, int attempts, SendResult res, OutboundEvent? ev, string? kind, CancellationToken ct)
    {
        var final = res.Ok || res.Permanent || attempts >= MaxAttempts;
        var status = res.Ok ? "sent" : final ? "failed" : "queued";
        var response = res.Ticket ?? (res.Response is { Length: > 200 } long_ ? long_[..200] : res.Response);
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand("""
            UPDATE zentriq.int_deliveries SET status = @s, attempts = @n, response = @resp, error = @err,
                   sent_at = CASE WHEN @s = 'sent' THEN now() END, next_attempt_at = now() + @delay
             WHERE id = @id;
            UPDATE zentriq.int_channels SET last_ok_at = CASE WHEN @ok THEN now() ELSE last_ok_at END,
                   last_error_at = CASE WHEN @ok THEN last_error_at ELSE now() END, last_error = CASE WHEN @ok THEN last_error ELSE @err END
             WHERE id = @ch;
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("s", status);
            cmd.Parameters.AddWithValue("n", attempts);
            cmd.Parameters.Add(new NpgsqlParameter("resp", NpgsqlDbType.Text) { Value = (object?)response ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("err", NpgsqlDbType.Text) { Value = (object?)res.Error ?? DBNull.Value });
            cmd.Parameters.AddWithValue("delay", RetryDelay(attempts));
            cmd.Parameters.AddWithValue("id", deliveryId);
            cmd.Parameters.AddWithValue("ok", res.Ok);
            cmd.Parameters.AddWithValue("ch", channelId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        // A ticket created for vulnerability findings is written back to the findings (unless they already have one).
        if (res.Ok && res.Ticket is not null && ev?.Data is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty("findingIds", out var ids) && ids.ValueKind == JsonValueKind.Array)
        {
            var findingIds = ids.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt64()).ToArray();
            await using var link = new NpgsqlCommand("""
                WITH up AS (UPDATE zentriq.vuln_findings SET ticket = @t WHERE id = ANY(@ids) AND (ticket IS NULL OR ticket = '') RETURNING id)
                INSERT INTO zentriq.vuln_finding_events (finding_id, user_id, kind, text)
                SELECT id, NULL, 'ticket', 'Ticket ' || @t || ' created in ' || @k || ' by an alert rule.' FROM up
                """, conn, tx);
            link.Parameters.AddWithValue("t", res.Ticket);
            link.Parameters.AddWithValue("ids", findingIds);
            link.Parameters.AddWithValue("k", kind == "jira" ? "Jira" : "ServiceNow");
            await link.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>Sends a test event to one channel now and records it in the delivery log.</summary>
    public async Task<SendResult> TestAsync(Channel ch, string userName, CancellationToken ct)
    {
        long eventId;
        await using (var cmd = ds.CreateCommand("""
            INSERT INTO zentriq.int_events (event_type, severity, dedupe_key, title, body, data, link, routed_at)
            VALUES ('test', 'info', 'test:' || gen_random_uuid(), 'Test message from Zentriq',
                    'This is a test of the "' || @n || '" channel, sent by ' || @u || '. If you can read it, the channel works.',
                    jsonb_build_object('test', true), '/integrations', now())
            RETURNING id
            """))
        {
            cmd.Parameters.AddWithValue("n", ch.Name);
            cmd.Parameters.AddWithValue("u", userName);
            eventId = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        long deliveryId;
        await using (var cmd = ds.CreateCommand("INSERT INTO zentriq.int_deliveries (event_id, channel_id, status, attempts) VALUES (@e, @c, 'queued', 0) RETURNING id"))
        {
            cmd.Parameters.AddWithValue("e", eventId);
            cmd.Parameters.AddWithValue("c", ch.Id);
            deliveryId = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        var ev = await LoadEventAsync(eventId, await ConsoleUrlAsync(ct), ct);
        var res = await sender.SendAsync(ch, ev!, ct);
        await RecordAsync(deliveryId, ch.Id, MaxAttempts, res, null, ch.Kind, ct);
        return res;
    }

    // ---- API tokens ---------------------------------------------------------------------------------------------------

    public const string TokenPrefix = "zq_";

    public static string NewToken() => TokenPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] HashToken(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
