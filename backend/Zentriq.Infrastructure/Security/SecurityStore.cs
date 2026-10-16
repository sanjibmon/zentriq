using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Infrastructure.Inventory;
using Zentriq.Inventory;

namespace Zentriq.Infrastructure.Security;

public sealed record SecurityEvaluation(int Baselines, int Devices, int Checks, int Failed, int NewFailures, int Resolved, int NoSettingsData);

public sealed record SecurityException(Guid Id, string RuleId, Guid? AssetId, Guid? BaselineId, string Reason, DateTimeOffset? ExpiresAt);

/// <summary>
/// Security Configuration Assessment: evaluates every enabled baseline against the latest inventory of each Windows
/// device in its scope, keeps per-check results (with the start of the current failing period), device scores and a
/// daily trend. Read-only towards devices: nothing is changed on them.
/// </summary>
public sealed class SecurityStore(NpgsqlDataSource ds)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record Baseline(Guid Id, string Name, BaselineDefinition Definition);

    public async Task<List<Baseline>> BaselinesAsync(bool enabledOnly, CancellationToken ct)
    {
        var list = new List<Baseline>();
        await using var cmd = ds.CreateCommand($"SELECT id, name, definition::text FROM zentriq.sec_baselines {(enabledOnly ? "WHERE enabled" : "")} ORDER BY built_in DESC, name");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new Baseline(r.GetGuid(0), r.GetString(1), Parse(r.GetString(2))));
        return list;
    }

    public static BaselineDefinition Parse(string json)
    {
        try { return JsonSerializer.Deserialize<BaselineDefinition>(json, Json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    /// <summary>Writes the rule catalogue to sec_rules so reports and SQL views can show titles and categories.</summary>
    public async Task SyncCatalogAsync(CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var rule in SecurityRules.All)
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO zentriq.sec_rules (id, title, category, severity, applies, reference, remediation, updated_at)
                VALUES (@id, @t, @c, @s, @a, @ref, @rem, now())
                ON CONFLICT (id) DO UPDATE SET title = EXCLUDED.title, category = EXCLUDED.category, severity = EXCLUDED.severity, applies = EXCLUDED.applies,
                    reference = EXCLUDED.reference, remediation = EXCLUDED.remediation, updated_at = now()
                 WHERE (sec_rules.title, sec_rules.category, sec_rules.severity, sec_rules.applies, sec_rules.reference, sec_rules.remediation)
                       IS DISTINCT FROM (EXCLUDED.title, EXCLUDED.category, EXCLUDED.severity, EXCLUDED.applies, EXCLUDED.reference, EXCLUDED.remediation)
                """, conn, tx);
            cmd.Parameters.AddWithValue("id", rule.Id);
            cmd.Parameters.AddWithValue("t", rule.Title);
            cmd.Parameters.AddWithValue("c", rule.Category);
            cmd.Parameters.AddWithValue("s", rule.Severity);
            cmd.Parameters.AddWithValue("a", rule.Applies);
            cmd.Parameters.AddWithValue("ref", rule.Reference);
            cmd.Parameters.AddWithValue("rem", rule.Remediation);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var del = new NpgsqlCommand("DELETE FROM zentriq.sec_rules WHERE NOT (id = ANY(@ids))", conn, tx))
        {
            del.Parameters.AddWithValue("ids", SecurityRules.All.Select(r => r.Id).ToArray());
            await del.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<List<SecurityException>> ActiveExceptionsAsync(CancellationToken ct)
    {
        var list = new List<SecurityException>();
        await using var cmd = ds.CreateCommand("SELECT id, rule_id, asset_id, baseline_id, reason, expires_at FROM zentriq.sec_exceptions WHERE expires_at IS NULL OR expires_at > now()");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new SecurityException(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.IsDBNull(3) ? null : r.GetGuid(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5)));
        return list;
    }

    /// <summary>One check result for a device (pure; used by the evaluator and the tests).</summary>
    public sealed record Row(Guid BaselineId, Guid AssetId, string RuleId, string Status, string Severity, string? Actual);

    /// <summary>Evaluates one device against one baseline. Not-applicable checks produce no row.</summary>
    public static IEnumerable<Row> EvaluateDevice(Baseline baseline, Guid assetId, DeviceFacts facts, IReadOnlyList<SecurityException> exceptions)
    {
        foreach (var (rule, severity, prms) in baseline.Definition.Effective())
        {
            var result = rule.Evaluate(facts, prms);
            if (result.Status == CheckStatus.NotApplicable) continue;
            var exempt = exceptions.FirstOrDefault(e => e.RuleId == rule.Id && (e.AssetId is null || e.AssetId == assetId) && (e.BaselineId is null || e.BaselineId == baseline.Id));
            var status = exempt is not null ? "exempt" : SecurityText.Status(result.Status);
            var actual = exempt is not null ? $"exception: {exempt.Reason}" + (result.Actual is null ? "" : $" (found: {result.Actual})") : result.Actual;
            yield return new Row(baseline.Id, assetId, rule.Id, status, severity, actual is { Length: > 500 } ? actual[..500] : actual);
        }
    }

    public async Task<SecurityEvaluation> EvaluateAsync(CancellationToken ct)
    {
        await SyncCatalogAsync(ct);
        var baselines = await BaselinesAsync(enabledOnly: true, ct);
        var exceptions = await ActiveExceptionsAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var rows = new List<Row>();
        int devices = 0, noData = 0;

        await using (var cmd = ds.CreateCommand("""
            SELECT a.id, a.category, d.document::text
              FROM zentriq.assets a JOIN zentriq.asset_inventory_documents d ON d.asset_id = a.id
             WHERE a.status = 'active' AND d.document #>> '{os,family}' = 'windows'
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                InventoryDocument? doc;
                try { doc = JsonSerializer.Deserialize<InventoryDocument>(r.GetString(2), InventoryStore.Json); }
                catch (JsonException) { continue; }
                if (doc is null) continue;
                var id = r.GetGuid(0);
                var facts = new DeviceFacts(doc, r.GetString(1), now);
                devices++;
                if (doc.Security.Hardening is null) noData++;
                foreach (var b in baselines.Where(b => b.Definition.InScope(facts.IsServer ? "server" : "endpoint")))
                    rows.AddRange(EvaluateDevice(b, id, facts, exceptions));
            }
        }

        int newFailures, resolved;
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var create = new NpgsqlCommand("""
            CREATE TEMP TABLE sr_new (baseline_id uuid, asset_id uuid, rule_id text, status text, severity text, actual text) ON COMMIT DROP
            """, conn, tx))
            await create.ExecuteNonQueryAsync(ct);
        await using (var copy = await conn.BeginBinaryImportAsync("COPY sr_new (baseline_id, asset_id, rule_id, status, severity, actual) FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var x in rows)
            {
                await copy.StartRowAsync(ct);
                await copy.WriteAsync(x.BaselineId, NpgsqlDbType.Uuid, ct);
                await copy.WriteAsync(x.AssetId, NpgsqlDbType.Uuid, ct);
                await copy.WriteAsync(x.RuleId, NpgsqlDbType.Text, ct);
                await copy.WriteAsync(x.Status, NpgsqlDbType.Text, ct);
                await copy.WriteAsync(x.Severity, NpgsqlDbType.Text, ct);
                if (x.Actual is null) await copy.WriteNullAsync(ct); else await copy.WriteAsync(x.Actual, NpgsqlDbType.Text, ct);
            }
            await copy.CompleteAsync(ct);
        }

        await using (var count = new NpgsqlCommand("""
            SELECT count(*) FILTER (WHERE n.status = 'fail' AND (o.status IS NULL OR o.status <> 'fail')),
                   (SELECT count(*) FROM zentriq.sec_results o2 WHERE o2.status = 'fail'
                       AND NOT EXISTS (SELECT 1 FROM sr_new n2 WHERE n2.baseline_id = o2.baseline_id AND n2.asset_id = o2.asset_id AND n2.rule_id = o2.rule_id AND n2.status = 'fail'))
              FROM sr_new n LEFT JOIN zentriq.sec_results o ON o.baseline_id = n.baseline_id AND o.asset_id = n.asset_id AND o.rule_id = n.rule_id
            """, conn, tx))
        await using (var r = await count.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            newFailures = (int)r.GetInt64(0);
            resolved = (int)r.GetInt64(1);
        }

        await using (var merge = new NpgsqlCommand("""
            INSERT INTO zentriq.sec_results AS s (baseline_id, asset_id, rule_id, status, severity, actual, first_failed_at, changed_at, evaluated_at)
            SELECT baseline_id, asset_id, rule_id, status, severity, actual, CASE WHEN status = 'fail' THEN @now END, @now, @now FROM sr_new
            ON CONFLICT (baseline_id, asset_id, rule_id) DO UPDATE SET
                status = EXCLUDED.status, severity = EXCLUDED.severity, actual = EXCLUDED.actual, evaluated_at = @now,
                first_failed_at = CASE WHEN EXCLUDED.status <> 'fail' THEN NULL WHEN s.status = 'fail' THEN s.first_failed_at ELSE @now END,
                changed_at = CASE WHEN s.status <> EXCLUDED.status THEN @now ELSE s.changed_at END;
            DELETE FROM zentriq.sec_results WHERE evaluated_at < @now;

            INSERT INTO zentriq.sec_scores AS sc (baseline_id, asset_id, score, passed, failed, failed_high, unknown, exempt, evaluated_at)
            SELECT baseline_id, asset_id,
                   CASE WHEN sum(w) FILTER (WHERE status IN ('pass','fail')) > 0
                        THEN round(100.0 * COALESCE(sum(w) FILTER (WHERE status = 'pass'), 0) / sum(w) FILTER (WHERE status IN ('pass','fail')))::int END,
                   count(*) FILTER (WHERE status = 'pass'), count(*) FILTER (WHERE status = 'fail'), count(*) FILTER (WHERE status = 'fail' AND severity = 'high'),
                   count(*) FILTER (WHERE status = 'unknown'), count(*) FILTER (WHERE status = 'exempt'), @now
              FROM (SELECT *, CASE severity WHEN 'high' THEN 3 WHEN 'medium' THEN 2 ELSE 1 END AS w FROM sr_new) x
             GROUP BY baseline_id, asset_id
            ON CONFLICT (baseline_id, asset_id) DO UPDATE SET score = EXCLUDED.score, passed = EXCLUDED.passed, failed = EXCLUDED.failed,
                failed_high = EXCLUDED.failed_high, unknown = EXCLUDED.unknown, exempt = EXCLUDED.exempt, evaluated_at = @now;
            DELETE FROM zentriq.sec_scores WHERE evaluated_at < @now;

            INSERT INTO zentriq.sec_history (day, baseline_id, avg_score, devices, compliant, failed)
            SELECT current_date, b.id, round(avg(s.score), 1), count(s.asset_id), count(s.asset_id) FILTER (WHERE s.failed_high = 0 AND s.score IS NOT NULL), COALESCE(sum(s.failed), 0)
              FROM zentriq.sec_baselines b LEFT JOIN zentriq.sec_scores s ON s.baseline_id = b.id
             WHERE b.enabled GROUP BY b.id
            ON CONFLICT (day, baseline_id) DO UPDATE SET avg_score = EXCLUDED.avg_score, devices = EXCLUDED.devices, compliant = EXCLUDED.compliant, failed = EXCLUDED.failed;
            DELETE FROM zentriq.sec_history WHERE day < current_date - 730;

            INSERT INTO zentriq.system_settings (key, value) VALUES ('security.last_evaluation', to_jsonb(@now))
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now();
            """, conn, tx))
        {
            merge.Parameters.AddWithValue("now", now);
            await merge.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);

        return new SecurityEvaluation(baselines.Count, devices, rows.Count, rows.Count(x => x.Status == "fail"), newFailures, resolved, noData);
    }
}
