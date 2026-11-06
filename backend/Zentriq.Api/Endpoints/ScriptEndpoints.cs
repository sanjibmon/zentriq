using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;
using Zentriq.Infrastructure.Deployment;
using Zentriq.Infrastructure.Scripts;

namespace Zentriq.Api.Endpoints;

/// <summary>
/// Scripts &amp; Remediation: the script library, runs on devices with every device's output and exit code, and scheduled
/// runs and remediations (detection first, the fix only where the detection reports a problem).
/// </summary>
public static class ScriptEndpoints
{
    public sealed record RunBody(Guid ScriptId, Guid? DetectScriptId, string? Parameters, Guid[]? AssetIds, Guid[]? GroupIds, Guid[]? DepartmentIds, bool? All, string? Note);
    public sealed record ScheduleBody(string? Name, bool? Enabled, Guid? ScriptId, Guid? DetectScriptId, string? Parameters, Guid[]? AssetIds, Guid[]? GroupIds,
        Guid[]? DepartmentIds, bool? All, string? Frequency, int[]? Days, string? AtTime, DateTimeOffset? RunOnceAt);

    private static string? S(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static Guid? G(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetGuid(i);
    private static DateTimeOffset? T(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);
    private static int? I(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);

    public static void MapScriptEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/scripts").WithTags("Scripts & remediation").RequireModule(Modules.Patch).RequireAuthorization(Permissions.ScriptsView);

        g.MapGet("/summary", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await ScriptStore.EnsureBuiltInsAsync(ds, ct);
            await using var cmd = ds.CreateCommand($"""
                SELECT (SELECT count(*) FROM zentriq.scripts), (SELECT count(*) FROM zentriq.scripts WHERE NOT built_in),
                       (SELECT count(*) FROM zentriq.script_schedules WHERE enabled),
                       (SELECT count(*) FROM zentriq.script_jobs WHERE created_at > now() - interval '7 days'),
                       (SELECT count(*) FROM zentriq.asset_actions WHERE kind = 'script' AND status IN ('queued','sent','running')),
                       (SELECT count(*) FROM zentriq.asset_actions WHERE kind = 'script' AND status = 'succeeded' AND completed_at > now() - interval '7 days'),
                       (SELECT count(*) FROM zentriq.asset_actions WHERE kind = 'script' AND status IN ('failed','expired') AND completed_at > now() - interval '7 days'),
                       (SELECT count(*) FROM zentriq.asset_action_outputs o JOIN zentriq.asset_actions x ON x.id = o.action_id WHERE o.outcome = 'remediated' AND x.completed_at > now() - interval '7 days'),
                       (SELECT count(*) FROM zentriq.agent_registrations WHERE status = 'active' AND '{ScriptStore.Capability}' = ANY(capabilities)),
                       (SELECT count(*) FROM zentriq.agent_registrations WHERE status = 'active' AND NOT ('{ScriptStore.Capability}' = ANY(capabilities)))
                """);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            return Results.Ok(new
            {
                scripts = r.GetInt64(0), own = r.GetInt64(1), schedules = r.GetInt64(2), runs7d = r.GetInt64(3), running = r.GetInt64(4), succeeded7d = r.GetInt64(5),
                failed7d = r.GetInt64(6), remediated7d = r.GetInt64(7), agents = r.GetInt64(8), olderAgents = r.GetInt64(9), settings = await ScriptStore.SettingsAsync(ds, ct),
            });
        });

        // ---- library ------------------------------------------------------------------------------------------------
        g.MapGet("", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await ScriptStore.EnsureBuiltInsAsync(ds, ct);
            await using var cmd = ds.CreateCommand("""
                SELECT s.id, s.name, s.description, s.category, s.platform, s.language, s.content, s.sha256, s.parameters, s.timeout_seconds, s.built_in,
                       s.created_at, s.updated_at, u.email,
                       (SELECT max(j.created_at) FROM zentriq.script_jobs j WHERE j.script_id = s.id OR j.detect_script_id = s.id)
                  FROM zentriq.scripts s LEFT JOIN zentriq.users u ON u.id = s.updated_by
                 ORDER BY s.built_in, lower(s.category), lower(s.name)
                """);
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new
                {
                    id = r.GetGuid(0), name = r.GetString(1), description = S(r, 2), category = r.GetString(3), platform = r.GetString(4), language = r.GetString(5),
                    content = r.GetString(6), sha256 = r.GetString(7), parameters = S(r, 8), timeoutSeconds = r.GetInt32(9), builtIn = r.GetBoolean(10),
                    createdAt = T(r, 11), updatedAt = T(r, 12), updatedBy = S(r, 13), lastRunAt = T(r, 14),
                });
            return Results.Ok(list);
        });

        g.MapPost("", async (ScriptInput body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) => await SaveAsync(null, body, ds, rc, ct)).RequireAuthorization(Permissions.ScriptsManage);
        g.MapPut("/{id:guid}", async (Guid id, ScriptInput body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) => await SaveAsync(id, body, ds, rc, ct)).RequireAuthorization(Permissions.ScriptsManage);

        g.MapDelete("/{id:guid}", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("DELETE FROM zentriq.scripts WHERE id = @id AND NOT built_in RETURNING name");
            cmd.Parameters.AddWithValue("id", id);
            try
            {
                if (await cmd.ExecuteScalarAsync(ct) is not string name) return Results.Problem(statusCode: 404, title: "Script not found (ready-made scripts cannot be deleted).");
                await rc.AuditAsync("script.delete", name, ct: ct);
                return Results.NoContent();
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                return Results.Problem(statusCode: 409, title: "A schedule uses this script. Change or delete the schedule first.");
            }
        }).RequireAuthorization(Permissions.ScriptsManage);

        // ---- runs ---------------------------------------------------------------------------------------------------
        g.MapPost("/runs", async (RunBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            var targets = new TargetSpec(body.AssetIds, body.GroupIds, body.DepartmentIds, body.All);
            if (targets.IsEmpty) return Validation.Problem("assetIds", "Choose the devices (or groups, departments, all devices).");
            ScriptRunResult res;
            try { res = await ScriptStore.RunAsync(ds, new ScriptRunRequest(body.ScriptId, body.DetectScriptId, body.Parameters, targets, body.Note, null), rc.UserId, ct); }
            catch (KeyNotFoundException ex) { return Validation.Problem("scriptId", ex.Message); }
            catch (InvalidOperationException ex) { return Validation.Problem("scriptId", ex.Message); }
            AgentPresence.Instance.Nudge();
            var script = await ScriptStore.GetAsync(ds, body.ScriptId, ct);
            await rc.AuditAsync(body.DetectScriptId is null ? "script.run" : "script.remediate", script?.Name,
                detail: new { jobId = res.Id, res.Sent, res.Skipped, sha256 = script?.Sha256, body.DetectScriptId, parameters = body.Parameters }, ct: ct);
            return Results.Created($"/api/v1/scripts/runs/{res.Id}", new { id = res.Id, sent = res.Sent, skipped = res.Skipped });
        }).RequireAuthorization(Permissions.ScriptsRun);

        g.MapGet("/runs", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT j.id, j.created_at, j.name, j.note, j.parameters, j.script_id, j.detect_script_id, sch.name, u.email,
                       count(d.asset_id), count(*) FILTER (WHERE d.skipped IS NOT NULL),
                       count(*) FILTER (WHERE x.status IN ('queued','sent')), count(*) FILTER (WHERE x.status = 'running'),
                       count(*) FILTER (WHERE x.status = 'succeeded'), count(*) FILTER (WHERE x.status IN ('failed','expired')),
                       count(*) FILTER (WHERE o.outcome = 'compliant'), count(*) FILTER (WHERE o.outcome = 'remediated')
                  FROM zentriq.script_jobs j
                  LEFT JOIN zentriq.script_schedules sch ON sch.id = j.schedule_id
                  LEFT JOIN zentriq.users u ON u.id = j.created_by
                  LEFT JOIN zentriq.script_job_devices d ON d.job_id = j.id
                  LEFT JOIN zentriq.asset_actions x ON x.id = d.action_id
                  LEFT JOIN zentriq.asset_action_outputs o ON o.action_id = x.id
                 GROUP BY j.id, sch.name, u.email ORDER BY j.created_at DESC LIMIT 500
                """);
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new
                {
                    id = r.GetGuid(0), createdAt = r.GetFieldValue<DateTimeOffset>(1), name = r.GetString(2), note = S(r, 3), parameters = S(r, 4), scriptId = G(r, 5),
                    remediation = !r.IsDBNull(6), schedule = S(r, 7), createdBy = S(r, 8), devices = r.GetInt64(9), skipped = r.GetInt64(10), waiting = r.GetInt64(11),
                    running = r.GetInt64(12), succeeded = r.GetInt64(13), failed = r.GetInt64(14), compliant = r.GetInt64(15), remediated = r.GetInt64(16),
                });
            return Results.Ok(list);
        });

        g.MapGet("/runs/{id:guid}", async (Guid id, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT d.asset_id, COALESCE(a.hostname, host(a.primary_ip)), d.skipped, x.id, x.status, x.detail, x.requested_at, x.sent_at, x.completed_at,
                       o.exit_code, o.stdout, o.stderr, o.outcome, o.duration_ms
                  FROM zentriq.script_job_devices d JOIN zentriq.assets a ON a.id = d.asset_id
                  LEFT JOIN zentriq.asset_actions x ON x.id = d.action_id
                  LEFT JOIN zentriq.asset_action_outputs o ON o.action_id = x.id
                 WHERE d.job_id = @id ORDER BY lower(a.hostname)
                """);
            cmd.Parameters.AddWithValue("id", id);
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var skipped = S(r, 2);
                list.Add(new
                {
                    assetId = r.GetGuid(0), device = S(r, 1), status = skipped is not null ? "skipped" : S(r, 4) ?? "unknown", detail = skipped ?? S(r, 5), actionId = G(r, 3),
                    requestedAt = T(r, 6), sentAt = T(r, 7), completedAt = T(r, 8), exitCode = I(r, 9), stdout = S(r, 10), stderr = S(r, 11), outcome = S(r, 12), durationMs = I(r, 13),
                });
            }
            return Results.Ok(list);
        });

        g.MapPost("/runs/{id:guid}/cancel", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                UPDATE zentriq.asset_actions x SET status = 'expired', completed_at = now(), detail = 'Cancelled by ' || @u || ' before the device started it.'
                  FROM zentriq.script_job_devices d WHERE d.job_id = @id AND x.id = d.action_id AND x.status = 'queued'
                """);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("u", rc.UserName);
            var n = await cmd.ExecuteNonQueryAsync(ct);
            await rc.AuditAsync("script.run_cancel", id.ToString(), detail: new { cancelled = n }, ct: ct);
            return Results.Ok(new { cancelled = n });
        }).RequireAuthorization(Permissions.ScriptsRun);

        // ---- schedules ----------------------------------------------------------------------------------------------
        g.MapGet("/schedules", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT s.id, s.name, s.enabled, s.script_id, sc.name, s.detect_script_id, dc.name, s.parameters, s.targets::text, s.frequency, s.days, s.at_time,
                       s.run_once_at, s.next_run_at, s.last_run_at, sc.platform,
                       (SELECT j.id FROM zentriq.script_jobs j WHERE j.schedule_id = s.id ORDER BY j.created_at DESC LIMIT 1)
                  FROM zentriq.script_schedules s JOIN zentriq.scripts sc ON sc.id = s.script_id LEFT JOIN zentriq.scripts dc ON dc.id = s.detect_script_id
                 ORDER BY lower(s.name)
                """);
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new
                {
                    id = r.GetGuid(0), name = r.GetString(1), enabled = r.GetBoolean(2), scriptId = r.GetGuid(3), script = r.GetString(4), detectScriptId = G(r, 5), detectScript = S(r, 6),
                    parameters = S(r, 7), targets = JsonDocument.Parse(r.GetString(8)).RootElement, frequency = r.GetString(9), days = r.GetFieldValue<int[]>(10),
                    atTime = r.GetFieldValue<TimeOnly>(11).ToString("HH:mm"), runOnceAt = T(r, 12), nextRunAt = T(r, 13), lastRunAt = T(r, 14), platform = r.GetString(15), lastRunId = G(r, 16),
                });
            return Results.Ok(list);
        });

        g.MapPost("/schedules", (ScheduleBody b, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) => SaveScheduleAsync(null, b, ds, rc, ct)).RequireAuthorization(Permissions.ScriptsManage);
        g.MapPut("/schedules/{id:guid}", (Guid id, ScheduleBody b, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) => SaveScheduleAsync(id, b, ds, rc, ct)).RequireAuthorization(Permissions.ScriptsManage);

        g.MapDelete("/schedules/{id:guid}", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("DELETE FROM zentriq.script_schedules WHERE id = @id RETURNING name");
            cmd.Parameters.AddWithValue("id", id);
            if (await cmd.ExecuteScalarAsync(ct) is not string name) return Results.NotFound();
            await rc.AuditAsync("script.schedule_delete", name, ct: ct);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.ScriptsManage);

        g.MapPut("/settings", async (ScriptSettings body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (body.KeepOutputDays is < 7 or > 3650) return Validation.Problem("keepOutputDays", "Keep the output 7 to 3650 days.");
            await ScriptStore.SaveSettingsAsync(ds, body, ct);
            await rc.AuditAsync("script.settings", null, detail: new { body.Enabled, body.KeepOutputDays }, ct: ct);
            return Results.Ok(await ScriptStore.SettingsAsync(ds, ct));
        }).RequireAuthorization(Permissions.SettingsManage);
    }

    private static async Task<IResult> SaveAsync(Guid? id, ScriptInput body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct)
    {
        if (ScriptStore.Validate(body) is { } problem) return Validation.Problem(problem.Field, problem.Message);
        try
        {
            var saved = await ScriptStore.SaveAsync(ds, id, body, rc.UserId, ct);
            var s = await ScriptStore.GetAsync(ds, saved, ct);
            await rc.AuditAsync(id is null ? "script.create" : "script.update", s!.Name, detail: new { s.Platform, s.Language, s.Sha256 }, ct: ct);
            return Results.Ok(new { id = saved, sha256 = s.Sha256 });
        }
        catch (KeyNotFoundException ex) { return Results.Problem(statusCode: 404, title: ex.Message); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Validation.Problem("name", "A script with this name exists."); }
    }

    private static async Task<IResult> SaveScheduleAsync(Guid? id, ScheduleBody b, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct)
    {
        var name = b.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 120) return Validation.Problem("name", "Enter a name (at most 120 characters).");
        if (b.ScriptId is not { } scriptId || await ScriptStore.GetAsync(ds, scriptId, ct) is not { } script) return Validation.Problem("scriptId", "Choose the script.");
        if (b.DetectScriptId is { } did)
        {
            if (await ScriptStore.GetAsync(ds, did, ct) is not { } detect) return Validation.Problem("detectScriptId", "Choose the detection script.");
            if (detect.Platform != script.Platform) return Validation.Problem("detectScriptId", "The detection script and the fix must be for the same platform.");
            if (did == scriptId) return Validation.Problem("detectScriptId", "Choose a different script for the detection.");
        }
        if (b.Frequency is not ("once" or "hourly" or "daily" or "weekly")) return Validation.Problem("frequency", "Choose once, hourly, daily or weekly.");
        var days = (b.Days ?? []).Distinct().OrderBy(x => x).ToArray();
        if (b.Frequency == "weekly" && (days.Length == 0 || days.Any(d => d is < 0 or > 6))) return Validation.Problem("days", "Choose the days.");
        if (!TimeOnly.TryParse(b.AtTime ?? "02:00", System.Globalization.CultureInfo.InvariantCulture, out var at)) return Validation.Problem("atTime", "Enter the time (HH:mm).");
        if (b.Frequency == "once" && (b.RunOnceAt is null || b.RunOnceAt < DateTimeOffset.UtcNow)) return Validation.Problem("runOnceAt", "Choose a time in the future.");
        if (b.Parameters is { Length: > 1000 } || (b.Parameters is not null && b.Parameters.Contains('\n'))) return Validation.Problem("parameters", "Parameters must be on one line (at most 1000 characters).");
        var targets = new TargetSpec(b.AssetIds, b.GroupIds, b.DepartmentIds, b.All);
        if (targets.IsEmpty) return Validation.Problem("assetIds", "Choose the devices (or groups, departments, all devices).");
        var enabled = b.Enabled != false;
        var next = enabled ? ScriptStore.NextRun(b.Frequency, days, at, b.RunOnceAt?.LocalDateTime, DateTime.Now) : null;
        await using var cmd = ds.CreateCommand(id is null
            ? """
              INSERT INTO zentriq.script_schedules (name, enabled, script_id, detect_script_id, parameters, targets, frequency, days, at_time, run_once_at, next_run_at, created_by)
              VALUES (@n, @en, @s, @d, @pa, @t::jsonb, @f, @days, @at, @once, @next, @by) RETURNING id
              """
            : """
              UPDATE zentriq.script_schedules SET name = @n, enabled = @en, script_id = @s, detect_script_id = @d, parameters = @pa, targets = @t::jsonb, frequency = @f,
                     days = @days, at_time = @at, run_once_at = @once, next_run_at = @next, updated_at = now()
               WHERE id = @id RETURNING id
              """);
        if (id is not null) cmd.Parameters.AddWithValue("id", id.Value);
        cmd.Parameters.AddWithValue("n", name);
        cmd.Parameters.AddWithValue("en", enabled);
        cmd.Parameters.AddWithValue("s", scriptId);
        cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Uuid) { Value = (object?)b.DetectScriptId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("pa", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(b.Parameters) ? DBNull.Value : b.Parameters.Trim() });
        cmd.Parameters.AddWithValue("t", targets.ToJson());
        cmd.Parameters.AddWithValue("f", b.Frequency);
        cmd.Parameters.AddWithValue("days", b.Frequency == "weekly" ? days : new[] { 1 });
        cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.Time) { Value = at });
        cmd.Parameters.Add(new NpgsqlParameter("once", NpgsqlDbType.TimestampTz) { Value = b.Frequency == "once" ? b.RunOnceAt!.Value.ToUniversalTime() : DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("next", NpgsqlDbType.TimestampTz) { Value = next is { } n ? new DateTimeOffset(n).ToUniversalTime() : DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Uuid) { Value = (object?)rc.UserId ?? DBNull.Value });
        try
        {
            if (await cmd.ExecuteScalarAsync(ct) is not Guid saved) return Results.NotFound();
            await rc.AuditAsync(id is null ? "script.schedule_create" : "script.schedule_update", name,
                detail: new { script = script.Name, b.DetectScriptId, b.Frequency, days, atTime = b.AtTime, enabled }, ct: ct);
            return Results.Ok(new { id = saved, nextRunAt = next is { } nr ? new DateTimeOffset(nr) : (DateTimeOffset?)null });
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Validation.Problem("name", "A schedule with this name exists."); }
    }
}
