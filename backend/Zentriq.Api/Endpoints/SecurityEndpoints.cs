using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;
using Zentriq.Infrastructure.Jobs;
using Zentriq.Infrastructure.Security;

namespace Zentriq.Api.Endpoints;

/// <summary>
/// Security Configuration Assessment: read-only checks of Windows settings (encryption, firewall, accounts, credential
/// protection, protocols, patch currency…) against baselines, with a score per device, documented exceptions, a
/// daily trend and remediation guidance. Zentriq does not change device settings.
/// </summary>
public static class SecurityEndpoints
{
    public sealed record BaselineBody(string Name, string? Description, bool? Enabled, List<string>? Categories, List<BaselineRule>? Rules, Guid? CopyFrom);
    public sealed record ExceptionBody(string RuleId, Guid[]? AssetIds, Guid? BaselineId, string Reason, DateTimeOffset? ExpiresAt);
    public sealed record SettingsBody(bool? AutoEvaluate);

    public static void MapSecurityEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/security").WithTags("Security configuration").RequireModule(Modules.Configuration).RequireAuthorization(Permissions.SecurityView);

        g.MapGet("/overview", async (Guid? baselineId, NpgsqlDataSource ds, SecurityStore store, CancellationToken ct) =>
        {
            var baselines = await store.BaselinesAsync(enabledOnly: false, ct);
            var b = await ResolveBaselineAsync(ds, baselineId, ct);
            await using var conn = await ds.OpenConnectionAsync(ct);
            async Task<List<T>> Rows<T>(string sql, Func<NpgsqlDataReader, T> map)
            {
                var list = new List<T>();
                await using var c = new NpgsqlCommand(sql, conn);
                c.Parameters.Add(new NpgsqlParameter("b", NpgsqlDbType.Uuid) { Value = (object?)b ?? DBNull.Value });
                await using var r = await c.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) list.Add(map(r));
                return list;
            }
            const string Scores = "FROM zentriq.sec_scores s JOIN zentriq.assets a ON a.id = s.asset_id WHERE a.status = 'active' AND s.baseline_id = @b";
            const string Results_ = "FROM zentriq.sec_results x JOIN zentriq.assets a ON a.id = x.asset_id WHERE a.status = 'active' AND x.baseline_id = @b";
            var kpi = (await Rows($"""
                SELECT count(*), round(avg(s.score)), count(*) FILTER (WHERE s.failed_high = 0 AND s.score IS NOT NULL), COALESCE(sum(s.failed), 0),
                       COALESCE(sum(s.failed_high), 0), count(*) FILTER (WHERE s.score IS NULL), COALESCE(sum(s.exempt), 0), COALESCE(sum(s.unknown), 0),
                       count(*) FILTER (WHERE s.score >= 90), count(*) FILTER (WHERE s.score < 70)
                {Scores}
                """, r => new
            {
                devices = r.GetInt64(0), avgScore = r.IsDBNull(1) ? (decimal?)null : r.GetDecimal(1), compliant = r.GetInt64(2), failedChecks = r.GetInt64(3),
                failedHigh = r.GetInt64(4), notAssessed = r.GetInt64(5), exempt = r.GetInt64(6), unknown = r.GetInt64(7), good = r.GetInt64(8), poor = r.GetInt64(9),
            }))[0];
            var coverage = (await Rows("""
                SELECT count(*), count(*) FILTER (WHERE d.document #> '{security,hardening}' IS NULL OR jsonb_typeof(d.document #> '{security,hardening}') = 'null')
                  FROM zentriq.assets a JOIN zentriq.asset_inventory_documents d ON d.asset_id = a.id
                 WHERE a.status = 'active' AND d.document #>> '{os,family}' = 'windows'
                """, r => new { windowsDevices = r.GetInt64(0), noSettingsData = r.GetInt64(1) }))[0];
            var byCategory = await Rows($"""
                SELECT COALESCE(r.category, 'Other'), count(*) FILTER (WHERE x.status = 'pass'), count(*) FILTER (WHERE x.status = 'fail'),
                       count(*) FILTER (WHERE x.status = 'unknown'), count(*) FILTER (WHERE x.status = 'exempt')
                  FROM zentriq.sec_results x JOIN zentriq.assets a ON a.id = x.asset_id LEFT JOIN zentriq.sec_rules r ON r.id = x.rule_id
                 WHERE a.status = 'active' AND x.baseline_id = @b GROUP BY 1 ORDER BY 3 DESC, 1
                """, r => new { category = r.GetString(0), pass = r.GetInt64(1), fail = r.GetInt64(2), unknown = r.GetInt64(3), exempt = r.GetInt64(4) });
            var topFailing = await Rows($"""
                SELECT x.rule_id, r.title, min(x.severity), count(*) FILTER (WHERE x.status = 'fail'), count(*) FILTER (WHERE x.status IN ('pass','fail'))
                {Results_.Replace("FROM zentriq.sec_results x", "FROM zentriq.sec_results x LEFT JOIN zentriq.sec_rules r ON r.id = x.rule_id")}
                 GROUP BY x.rule_id, r.title HAVING count(*) FILTER (WHERE x.status = 'fail') > 0
                 ORDER BY array_position(ARRAY['high','medium','low'], min(x.severity)), 4 DESC LIMIT 10
                """, r => new { rule = r.GetString(0), title = r.IsDBNull(1) ? r.GetString(0) : r.GetString(1), severity = r.GetString(2), failing = r.GetInt64(3), assessed = r.GetInt64(4) });
            var worst = await Rows($"""
                SELECT a.id, COALESCE(a.hostname, zentriq.asset_ipv4(a.id), host(a.primary_ip)), a.os_name, s.score, s.failed, s.failed_high, a.criticality
                {Scores} AND s.score IS NOT NULL ORDER BY s.score, s.failed_high DESC LIMIT 10
                """, r => new { assetId = r.GetGuid(0), hostname = r.IsDBNull(1) ? null : r.GetString(1), os = r.IsDBNull(2) ? null : r.GetString(2), score = r.GetInt32(3), failed = r.GetInt32(4), failedHigh = r.GetInt32(5), criticality = r.GetString(6) });
            var distribution = await Rows($"""
                SELECT bucket, count(*) FROM (SELECT CASE WHEN s.score >= 90 THEN '90–100' WHEN s.score >= 70 THEN '70–89' WHEN s.score >= 50 THEN '50–69' ELSE '0–49' END AS bucket {Scores} AND s.score IS NOT NULL) q
                 GROUP BY bucket ORDER BY array_position(ARRAY['0–49','50–69','70–89','90–100'], bucket)
                """, r => new { bucket = r.GetString(0), count = r.GetInt64(1) });
            var trend = await Rows("""
                SELECT d::date, h.avg_score, h.devices, h.compliant, h.failed
                  FROM generate_series(current_date - 29, current_date, interval '1 day') d
                  LEFT JOIN zentriq.sec_history h ON h.day = d::date AND h.baseline_id = @b ORDER BY 1
                """, r => new { day = DateOnly.FromDateTime(r.GetDateTime(0)), avgScore = r.IsDBNull(1) ? (decimal?)null : r.GetDecimal(1), devices = r.IsDBNull(2) ? (int?)null : r.GetInt32(2), compliant = r.IsDBNull(3) ? (int?)null : r.GetInt32(3), failed = r.IsDBNull(4) ? (int?)null : r.GetInt32(4) });
            DateTimeOffset? last = null;
            await using (var lc = new NpgsqlCommand("SELECT value #>> '{}' FROM zentriq.system_settings WHERE key = 'security.last_evaluation'", conn))
                if (await lc.ExecuteScalarAsync(ct) is string s && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var lv)) last = lv;
            var exceptions = (await Rows("SELECT count(*) FROM zentriq.sec_exceptions WHERE expires_at IS NULL OR expires_at > now()", r => r.GetInt64(0)))[0];
            return Results.Ok(new
            {
                baselineId = b, baselines = baselines.Select(x => new { x.Id, x.Name }),
                kpi, coverage, byCategory, topFailing, worst, distribution, trend, exceptions, lastEvaluation = last, generatedAt = DateTimeOffset.UtcNow,
            });
        });

        g.MapGet("/rules", async (Guid? baselineId, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var b = await ResolveBaselineAsync(ds, baselineId, ct);
            var def = b is null ? BaselineDefinition.Default() : await DefinitionAsync(ds, b.Value, ct) ?? new BaselineDefinition();
            var counts = new Dictionary<string, (long Pass, long Fail, long Unknown, long Exempt)>();
            await using (var cmd = ds.CreateCommand("""
                SELECT x.rule_id, count(*) FILTER (WHERE x.status = 'pass'), count(*) FILTER (WHERE x.status = 'fail'), count(*) FILTER (WHERE x.status = 'unknown'), count(*) FILTER (WHERE x.status = 'exempt')
                  FROM zentriq.sec_results x JOIN zentriq.assets a ON a.id = x.asset_id WHERE a.status = 'active' AND x.baseline_id = @b GROUP BY 1
                """))
            {
                cmd.Parameters.Add(new NpgsqlParameter("b", NpgsqlDbType.Uuid) { Value = (object?)b ?? DBNull.Value });
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) counts[r.GetString(0)] = (r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4));
            }
            var effective = def.Effective().ToDictionary(e => e.Rule.Id);
            return Results.Ok(new
            {
                baselineId = b,
                rules = SecurityRules.All.Select(rule =>
                {
                    var on = effective.TryGetValue(rule.Id, out var e);
                    counts.TryGetValue(rule.Id, out var c);
                    return new
                    {
                        id = rule.Id, title = rule.Title, category = rule.Category, defaultSeverity = rule.Severity, severity = on ? e.Severity : rule.Severity,
                        applies = rule.Applies, reference = rule.Reference, rationale = rule.Rationale, remediation = rule.Remediation, enabled = on,
                        @params = (rule.Params ?? []).Select(p => new { p.Key, p.Label, p.Default, p.Min, p.Max, value = on ? e.Params[p.Key] : p.Default }),
                        pass = c.Pass, fail = c.Fail, unknown = c.Unknown, exempt = c.Exempt,
                    };
                }),
            });
        });

        g.MapGet("/rules/{id}", async (string id, Guid? baselineId, string? status, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            if (SecurityRules.Find(id) is not { } rule) return Results.NotFound();
            var b = await ResolveBaselineAsync(ds, baselineId, ct);
            var devices = new List<object>();
            await using (var cmd = ds.CreateCommand("""
                SELECT a.id, COALESCE(a.hostname, zentriq.asset_ipv4(a.id), host(a.primary_ip)), a.os_name, a.category, x.status, x.actual, x.first_failed_at, x.evaluated_at, x.severity
                  FROM zentriq.sec_results x JOIN zentriq.assets a ON a.id = x.asset_id
                 WHERE a.status = 'active' AND x.baseline_id = @b AND x.rule_id = @r AND (@s::text IS NULL OR x.status = @s)
                 ORDER BY array_position(ARRAY['fail','unknown','exempt','pass'], x.status), x.first_failed_at NULLS LAST, 2 LIMIT 2000
                """))
            {
                cmd.Parameters.Add(new NpgsqlParameter("b", NpgsqlDbType.Uuid) { Value = (object?)b ?? DBNull.Value });
                cmd.Parameters.AddWithValue("r", rule.Id);
                cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Text) { Value = status is "pass" or "fail" or "unknown" or "exempt" ? status : DBNull.Value });
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    devices.Add(new
                    {
                        assetId = r.GetGuid(0), hostname = r.IsDBNull(1) ? null : r.GetString(1), os = r.IsDBNull(2) ? null : r.GetString(2), category = r.GetString(3),
                        status = r.GetString(4), actual = r.IsDBNull(5) ? null : r.GetString(5),
                        failingSince = r.IsDBNull(6) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(6), evaluatedAt = r.GetFieldValue<DateTimeOffset>(7), severity = r.GetString(8),
                    });
            }
            var exceptions = await ExceptionsAsync(ds, rule.Id, ct);
            return Results.Ok(new
            {
                rule = new { id = rule.Id, title = rule.Title, category = rule.Category, severity = rule.Severity, applies = rule.Applies, reference = rule.Reference, rationale = rule.Rationale, remediation = rule.Remediation },
                devices, exceptions,
            });
        });

        g.MapGet("/devices", async (Guid? baselineId, string? q, string? sort, int? page, int? pageSize, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var b = await ResolveBaselineAsync(ds, baselineId, ct);
            var (p, ps) = Paging.Normalize(page, pageSize);
            var order = sort switch { "host" => "2", "failed" => "s.failed DESC, s.score", "best" => "s.score DESC NULLS LAST, 2", _ => "s.score NULLS FIRST, s.failed_high DESC, 2" };
            await using var cmd = ds.CreateCommand($"""
                SELECT a.id, COALESCE(a.hostname, zentriq.asset_ipv4(a.id), host(a.primary_ip)), a.os_name, a.category, a.criticality, s.score, s.passed, s.failed, s.failed_high, s.unknown, s.exempt, s.evaluated_at, count(*) OVER ()
                  FROM zentriq.sec_scores s JOIN zentriq.assets a ON a.id = s.asset_id
                 WHERE a.status = 'active' AND s.baseline_id = @b AND (@q::text IS NULL OR a.hostname ILIKE '%' || @q || '%' OR host(a.primary_ip) LIKE @q || '%')
                 ORDER BY {order} LIMIT @l OFFSET @o
                """);
            cmd.Parameters.Add(new NpgsqlParameter("b", NpgsqlDbType.Uuid) { Value = (object?)b ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("q", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(q) ? DBNull.Value : q.Trim() });
            cmd.Parameters.AddWithValue("l", ps);
            cmd.Parameters.AddWithValue("o", (p - 1) * ps);
            var items = new List<object>();
            long total = 0;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                total = r.GetInt64(12);
                items.Add(new
                {
                    assetId = r.GetGuid(0), hostname = r.IsDBNull(1) ? null : r.GetString(1), os = r.IsDBNull(2) ? null : r.GetString(2), category = r.GetString(3), criticality = r.GetString(4),
                    score = r.IsDBNull(5) ? (int?)null : r.GetInt32(5), passed = r.GetInt32(6), failed = r.GetInt32(7), failedHigh = r.GetInt32(8), unknown = r.GetInt32(9), exempt = r.GetInt32(10),
                    evaluatedAt = r.GetFieldValue<DateTimeOffset>(11),
                });
            }
            return Results.Ok(new { items, total, page = p, pageSize = ps, baselineId = b });
        });

        // A device's results in every baseline (Security tab of the device page).
        g.MapGet("/devices/{assetId:guid}", async (Guid assetId, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var baselines = new List<object>();
            await using (var cmd = ds.CreateCommand("""
                SELECT b.id, b.name, s.score, s.passed, s.failed, s.failed_high, s.unknown, s.exempt, s.evaluated_at
                  FROM zentriq.sec_scores s JOIN zentriq.sec_baselines b ON b.id = s.baseline_id WHERE s.asset_id = @a ORDER BY b.built_in DESC, b.name
                """))
            {
                cmd.Parameters.AddWithValue("a", assetId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    baselines.Add(new
                    {
                        id = r.GetGuid(0), name = r.GetString(1), score = r.IsDBNull(2) ? (int?)null : r.GetInt32(2), passed = r.GetInt32(3), failed = r.GetInt32(4),
                        failedHigh = r.GetInt32(5), unknown = r.GetInt32(6), exempt = r.GetInt32(7), evaluatedAt = r.GetFieldValue<DateTimeOffset>(8),
                    });
            }
            var results = new List<object>();
            await using (var cmd = ds.CreateCommand("""
                SELECT x.baseline_id, x.rule_id, x.status, x.severity, x.actual, x.first_failed_at
                  FROM zentriq.sec_results x WHERE x.asset_id = @a
                 ORDER BY array_position(ARRAY['fail','unknown','exempt','pass'], x.status), array_position(ARRAY['high','medium','low'], x.severity), x.rule_id
                """))
            {
                cmd.Parameters.AddWithValue("a", assetId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var rule = SecurityRules.Find(r.GetString(1));
                    results.Add(new
                    {
                        baselineId = r.GetGuid(0), rule = r.GetString(1), title = rule?.Title ?? r.GetString(1), category = rule?.Category, status = r.GetString(2), severity = r.GetString(3),
                        actual = r.IsDBNull(4) ? null : r.GetString(4), failingSince = r.IsDBNull(5) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(5),
                        remediation = rule?.Remediation, reference = rule?.Reference,
                    });
                }
            }
            bool? hasData = null;
            await using (var cmd = ds.CreateCommand("SELECT document #> '{security,hardening}' IS NOT NULL AND jsonb_typeof(document #> '{security,hardening}') <> 'null', document #>> '{os,family}' FROM zentriq.asset_inventory_documents WHERE asset_id = @a"))
            {
                cmd.Parameters.AddWithValue("a", assetId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct) && !r.IsDBNull(1) && r.GetString(1) == "windows") hasData = !r.IsDBNull(0) && r.GetBoolean(0);
            }
            return Results.Ok(new { baselines, results, settingsReported = hasData });
        });

        // ---- baselines -----------------------------------------------------------------------------------------

        g.MapGet("/baselines", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var list = new List<object>();
            await using var cmd = ds.CreateCommand("""
                SELECT b.id, b.name, b.description, b.built_in, b.enabled, b.definition::text, b.updated_at, u.display_name,
                       (SELECT count(*) FROM zentriq.sec_scores s WHERE s.baseline_id = b.id), (SELECT round(avg(s.score)) FROM zentriq.sec_scores s WHERE s.baseline_id = b.id)
                  FROM zentriq.sec_baselines b LEFT JOIN zentriq.users u ON u.id = b.updated_by ORDER BY b.built_in DESC, b.name
                """);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var def = SecurityStore.Parse(r.GetString(5));
                list.Add(new
                {
                    id = r.GetGuid(0), name = r.GetString(1), description = r.IsDBNull(2) ? null : r.GetString(2), builtIn = r.GetBoolean(3), enabled = r.GetBoolean(4),
                    categories = def.Categories, rules = def.Effective().Count(), updatedAt = r.GetFieldValue<DateTimeOffset>(6), updatedBy = r.IsDBNull(7) ? null : r.GetString(7),
                    devices = r.GetInt64(8), avgScore = r.IsDBNull(9) ? (decimal?)null : r.GetDecimal(9),
                });
            }
            return Results.Ok(list);
        });

        g.MapGet("/baselines/{id:guid}", async (Guid id, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("SELECT name, description, built_in, enabled, definition::text FROM zentriq.sec_baselines WHERE id = @id");
            cmd.Parameters.AddWithValue("id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Results.NotFound();
            var def = SecurityStore.Parse(r.GetString(4));
            var eff = def.Effective().ToDictionary(e => e.Rule.Id);
            return Results.Ok(new
            {
                id, name = r.GetString(0), description = r.IsDBNull(1) ? null : r.GetString(1), builtIn = r.GetBoolean(2), enabled = r.GetBoolean(3), categories = def.Categories,
                rules = SecurityRules.All.Select(rule => new
                {
                    id = rule.Id, title = rule.Title, category = rule.Category, defaultSeverity = rule.Severity, applies = rule.Applies,
                    enabled = eff.ContainsKey(rule.Id), severity = eff.TryGetValue(rule.Id, out var e) ? e.Severity : rule.Severity,
                    @params = (rule.Params ?? []).Select(p => new { p.Key, p.Label, p.Default, p.Min, p.Max, value = eff.TryGetValue(rule.Id, out var e2) ? e2.Params[p.Key] : p.Default }),
                }),
            });
        });

        g.MapPost("/baselines", async (BaselineBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            BaselineDefinition def;
            if (body.CopyFrom is { } src && body.Rules is null)
                def = await DefinitionAsync(ds, src, ct) ?? BaselineDefinition.Default();
            else def = new BaselineDefinition { Rules = body.Rules ?? BaselineDefinition.Default().Rules };
            if (Check(body, def) is { } problem) return problem;
            def.Categories = Categories(body.Categories) ?? def.Categories;
            var id = Guid.NewGuid();
            await using var cmd = ds.CreateCommand("""
                INSERT INTO zentriq.sec_baselines (id, name, description, enabled, definition, created_by, updated_by)
                VALUES (@id, @n, @d, @e, @def::jsonb, @u, @u)
                """);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("n", body.Name.Trim());
            cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Text) { Value = (object?)body.Description?.Trim() ?? DBNull.Value });
            cmd.Parameters.AddWithValue("e", body.Enabled ?? true);
            cmd.Parameters.AddWithValue("def", JsonSerializer.Serialize(Normalize(def), SecurityStore.Json));
            cmd.Parameters.Add(new NpgsqlParameter("u", NpgsqlDbType.Uuid) { Value = (object?)rc.UserId ?? DBNull.Value });
            try { await cmd.ExecuteNonQueryAsync(ct); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Validation.Problem("name", "A baseline with this name already exists."); }
            await rc.AuditAsync("security.baseline.create", body.Name.Trim());
            return Results.Created($"/api/security/baselines/{id}", new { id });
        }).RequireAuthorization(Permissions.SecurityManage);

        g.MapPut("/baselines/{id:guid}", async (Guid id, BaselineBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            var current = await DefinitionAsync(ds, id, ct);
            if (current is null) return Results.NotFound();
            var def = new BaselineDefinition { Rules = body.Rules ?? current.Rules, Categories = Categories(body.Categories) ?? current.Categories };
            if (Check(body, def) is { } problem) return problem;
            await using var cmd = ds.CreateCommand("""
                UPDATE zentriq.sec_baselines SET name = @n, description = @d, enabled = COALESCE(@e, enabled), definition = @def::jsonb, updated_by = @u, updated_at = now()
                 WHERE id = @id
                """);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("n", body.Name.Trim());
            cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Text) { Value = (object?)body.Description?.Trim() ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.Boolean) { Value = (object?)body.Enabled ?? DBNull.Value });
            cmd.Parameters.AddWithValue("def", JsonSerializer.Serialize(Normalize(def), SecurityStore.Json));
            cmd.Parameters.Add(new NpgsqlParameter("u", NpgsqlDbType.Uuid) { Value = (object?)rc.UserId ?? DBNull.Value });
            try { await cmd.ExecuteNonQueryAsync(ct); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Validation.Problem("name", "A baseline with this name already exists."); }
            await rc.AuditAsync("security.baseline.update", body.Name.Trim(), detail: new { id, body.Enabled, rules = def.Effective().Count(), def.Categories });
            return Results.NoContent();
        }).RequireAuthorization(Permissions.SecurityManage);

        g.MapDelete("/baselines/{id:guid}", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("DELETE FROM zentriq.sec_baselines WHERE id = @id AND NOT built_in RETURNING name");
            cmd.Parameters.AddWithValue("id", id);
            if (await cmd.ExecuteScalarAsync(ct) is not string name) return Results.Problem(statusCode: 409, title: "The built-in baseline cannot be deleted (turn it off instead).");
            await rc.AuditAsync("security.baseline.delete", name);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.SecurityManage);

        // ---- exceptions -------------------------------------------------------------------------------------------

        g.MapGet("/exceptions", async (NpgsqlDataSource ds, CancellationToken ct) => Results.Ok(await ExceptionsAsync(ds, null, ct)));

        g.MapPost("/exceptions", async (ExceptionBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (SecurityRules.Find(body.RuleId) is null) return Validation.Problem("ruleId", "Unknown check.");
            if (string.IsNullOrWhiteSpace(body.Reason) || body.Reason.Trim().Length < 5) return Validation.Problem("reason", "Give a reason (at least 5 characters) — it is kept for audit.");
            if (body.ExpiresAt is { } x && (x <= DateTimeOffset.UtcNow || x > DateTimeOffset.UtcNow.AddDays(731))) return Validation.Problem("expiresAt", "The expiry date must be in the future and within two years.");
            if (body.AssetIds is { Length: > 1000 }) return Validation.Problem("assetIds", "At most 1000 devices at a time.");
            var targets = body.AssetIds is { Length: > 0 } ids ? ids.Distinct().Select(i => (Guid?)i).ToArray() : [null];
            foreach (var a in targets)
            {
                await using var cmd = ds.CreateCommand("""
                    INSERT INTO zentriq.sec_exceptions (rule_id, asset_id, baseline_id, reason, expires_at, created_by)
                    SELECT @r, @a, @b, @reason, @x, @u WHERE @a::uuid IS NULL OR EXISTS (SELECT 1 FROM zentriq.assets WHERE id = @a)
                    """);
                cmd.Parameters.AddWithValue("r", body.RuleId);
                cmd.Parameters.Add(new NpgsqlParameter("a", NpgsqlDbType.Uuid) { Value = (object?)a ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("b", NpgsqlDbType.Uuid) { Value = (object?)body.BaselineId ?? DBNull.Value });
                cmd.Parameters.AddWithValue("reason", body.Reason.Trim());
                cmd.Parameters.Add(new NpgsqlParameter("x", NpgsqlDbType.TimestampTz) { Value = (object?)body.ExpiresAt?.ToUniversalTime() ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("u", NpgsqlDbType.Uuid) { Value = (object?)rc.UserId ?? DBNull.Value });
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await rc.AuditAsync("security.exception.create", body.RuleId, detail: new { body.AssetIds, body.BaselineId, body.Reason, body.ExpiresAt });
            return Results.Ok(new { created = targets.Length, message = "Exception recorded. Results update at the next evaluation." });
        }).RequireAuthorization(Permissions.SecurityManage);

        g.MapDelete("/exceptions/{id:guid}", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("DELETE FROM zentriq.sec_exceptions WHERE id = @id RETURNING rule_id");
            cmd.Parameters.AddWithValue("id", id);
            if (await cmd.ExecuteScalarAsync(ct) is not string rule) return Results.NotFound();
            await rc.AuditAsync("security.exception.delete", rule, detail: new { id });
            return Results.NoContent();
        }).RequireAuthorization(Permissions.SecurityManage);

        // ---- evaluation & settings ---------------------------------------------------------------------------------

        g.MapPost("/evaluate", async (JobQueue jobs, RequestContext rc, CancellationToken ct) =>
        {
            var jobId = await jobs.EnqueueAsync(new EnqueueRequest(JobTypes.SecurityEvaluate, new { source = "manual" }, MaxAttempts: 1, TimeoutSeconds: 1800, CreatedBy: rc.UserId), ct: ct);
            await rc.AuditAsync("security.evaluate", null);
            return Results.Accepted(value: new { jobId, message = "Checking device settings against the baselines." });
        }).RequireAuthorization(Permissions.SecurityManage);

        g.MapGet("/settings", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("SELECT value::text FROM zentriq.system_settings WHERE key = 'security.auto_evaluate'");
            return Results.Ok(new { autoEvaluate = (await cmd.ExecuteScalarAsync(ct) as string)?.Trim() != "false" });
        });

        g.MapPut("/settings", async (SettingsBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (body.AutoEvaluate is { } a)
            {
                await using var cmd = ds.CreateCommand("""
                    INSERT INTO zentriq.system_settings (key, value) VALUES ('security.auto_evaluate', @v::jsonb)
                    ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now()
                    """);
                cmd.Parameters.AddWithValue("v", a ? "true" : "false");
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await rc.AuditAsync("security.settings", null, detail: body);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.SecurityManage);
    }

    /// <summary>The requested baseline, else the built-in one, else the first enabled one.</summary>
    private static async Task<Guid?> ResolveBaselineAsync(NpgsqlDataSource ds, Guid? requested, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            SELECT id FROM zentriq.sec_baselines ORDER BY (id = @r) DESC, built_in DESC, enabled DESC, name LIMIT 1
            """);
        cmd.Parameters.Add(new NpgsqlParameter("r", NpgsqlDbType.Uuid) { Value = (object?)requested ?? Guid.Empty });
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    private static async Task<BaselineDefinition?> DefinitionAsync(NpgsqlDataSource ds, Guid id, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT definition::text FROM zentriq.sec_baselines WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteScalarAsync(ct) is string s ? SecurityStore.Parse(s) : null;
    }

    private static async Task<List<object>> ExceptionsAsync(NpgsqlDataSource ds, string? rule, CancellationToken ct)
    {
        var list = new List<object>();
        await using var cmd = ds.CreateCommand("""
            SELECT e.id, e.rule_id, e.asset_id, COALESCE(a.hostname, zentriq.asset_ipv4(a.id), host(a.primary_ip)), e.baseline_id, b.name, e.reason, e.expires_at, u.display_name, e.created_at,
                   e.expires_at IS NOT NULL AND e.expires_at <= now()
              FROM zentriq.sec_exceptions e
              LEFT JOIN zentriq.assets a ON a.id = e.asset_id LEFT JOIN zentriq.sec_baselines b ON b.id = e.baseline_id LEFT JOIN zentriq.users u ON u.id = e.created_by
             WHERE (@r::text IS NULL OR e.rule_id = @r) ORDER BY e.created_at DESC LIMIT 1000
            """);
        cmd.Parameters.Add(new NpgsqlParameter("r", NpgsqlDbType.Text) { Value = (object?)rule ?? DBNull.Value });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new
            {
                id = r.GetGuid(0), rule = r.GetString(1), title = SecurityRules.Find(r.GetString(1))?.Title, assetId = r.IsDBNull(2) ? (Guid?)null : r.GetGuid(2),
                hostname = r.IsDBNull(3) ? null : r.GetString(3), baselineId = r.IsDBNull(4) ? (Guid?)null : r.GetGuid(4), baseline = r.IsDBNull(5) ? null : r.GetString(5),
                reason = r.GetString(6), expiresAt = r.IsDBNull(7) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(7), createdBy = r.IsDBNull(8) ? null : r.GetString(8),
                createdAt = r.GetFieldValue<DateTimeOffset>(9), expired = r.GetBoolean(10),
            });
        return list;
    }

    private static List<string>? Categories(List<string>? c) => c?.Where(x => x is "server" or "endpoint").Distinct().ToList();

    private static IResult? Check(BaselineBody body, BaselineDefinition def)
    {
        if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Trim().Length > 120) return Validation.Problem("name", "Give the baseline a name (up to 120 characters).");
        foreach (var r in def.Rules)
        {
            if (SecurityRules.Find(r.Id) is not { } rule) return Validation.Problem("rules", $"Unknown check '{r.Id}'.");
            if (r.Severity is not (null or "high" or "medium" or "low")) return Validation.Problem("rules", $"{r.Id}: severity must be high, medium or low.");
            foreach (var (k, v) in r.Params ?? [])
            {
                var p = rule.Params?.FirstOrDefault(x => x.Key == k);
                if (p is null) return Validation.Problem("rules", $"{r.Id}: unknown setting '{k}'.");
                if (v < p.Min || v > p.Max) return Validation.Problem("rules", $"{r.Id}: {p.Label} must be {p.Min}–{p.Max}.");
            }
        }
        if (!def.Effective().Any()) return Validation.Problem("rules", "Turn on at least one check.");
        return null;
    }

    /// <summary>Stores one entry per catalogue rule, so later catalogue additions keep their own default on/off.</summary>
    private static BaselineDefinition Normalize(BaselineDefinition def) => new()
    {
        Categories = def.Categories,
        Rules = def.Rules.GroupBy(r => r.Id).Select(g => g.Last())
            .Select(r => new BaselineRule { Id = r.Id, Enabled = r.Enabled, Severity = r.Severity, Params = r.Params is { Count: > 0 } ? r.Params : null }).ToList(),
    };
}
