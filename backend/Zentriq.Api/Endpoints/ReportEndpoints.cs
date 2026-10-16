using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;
using Zentriq.Infrastructure.Jobs;
using Zentriq.Infrastructure.Reports;

namespace Zentriq.Api.Endpoints;

/// <summary>
/// Reports & Analytics: a catalogue of datasets and columns, ad-hoc runs, saved reports (own, shared, built-in
/// templates), exports to Excel/CSV, schedules that e-mail the file, and the run history.
/// </summary>
public static class ReportEndpoints
{
    public sealed record ReportBody(string Name, string? Description, string Dataset, ReportSpec Definition, bool Shared);
    public sealed record RunBody(string Dataset, ReportSpec Definition, int? Limit);
    public sealed record ExportBody(string Dataset, ReportSpec Definition, string? Format, string? Name);
    public sealed record ScheduleBody(ReportSchedule? Schedule, string[]? Recipients, string? Format);

    private static readonly System.Text.RegularExpressions.Regex Email = new(@"^[^@\s,;]+@[^@\s,;]+\.[^@\s,;]+$");

    public static void MapReportEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/reports").WithTags("Reports").RequireModule(Modules.Reports).RequireAuthorization(Permissions.AssetsView);

        g.MapGet("/datasets", (RequestContext rc) => Results.Ok(new
        {
            datasets = ReportCatalog.Datasets.Where(d => CanUse(d, rc)).Select(d => new
            {
                d.Key, d.Name, d.Description,
                columns = d.Columns.Select(c => new { c.Key, c.Label, type = c.Type.ToString().ToLowerInvariant(), group = c.Group }),
            }),
            aggregates = ReportEngine.AggregateFunctions,
            dateBuckets = ReportEngine.DateBuckets,
            charts = ReportEngine.Charts,
            formats = new[] { "xlsx", "csv", "pdf" },
        }));

        g.MapGet("", async (NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT r.id, r.name, r.description, r.dataset, r.shared, r.system, r.owner_id = @me, u.display_name, r.updated_at,
                       r.schedule::text, r.recipients, r.format, r.next_run_at, r.last_run_at,
                       (SELECT x.status FROM zentriq.report_runs x WHERE x.report_id = r.id ORDER BY x.started_at DESC LIMIT 1)
                  FROM zentriq.report_definitions r LEFT JOIN zentriq.users u ON u.id = r.owner_id
                 WHERE r.system OR r.shared OR r.owner_id = @me
                 ORDER BY r.system DESC, r.name
                """);
            cmd.Parameters.Add(new NpgsqlParameter("me", NpgsqlDbType.Uuid) { Value = (object?)rc.UserId ?? DBNull.Value });
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (ReportCatalog.Find(r.GetString(3)) is not { } dsx || !CanUse(dsx, rc)) continue;
                var schedule = r.IsDBNull(9) ? null : JsonSerializer.Deserialize<ReportSchedule>(r.GetString(9));
                list.Add(new
                {
                    id = r.GetGuid(0), name = r.GetString(1), description = r.IsDBNull(2) ? null : r.GetString(2), dataset = r.GetString(3),
                    shared = r.GetBoolean(4), system = r.GetBoolean(5), owned = !r.IsDBNull(6) && r.GetBoolean(6), owner = r.IsDBNull(7) ? null : r.GetString(7),
                    updatedAt = r.GetFieldValue<DateTimeOffset>(8), schedule, scheduleText = schedule is { Enabled: true } ? schedule.Describe() : null,
                    recipients = r.GetFieldValue<string[]>(10), format = r.GetString(11),
                    nextRunAt = r.IsDBNull(12) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(12),
                    lastRunAt = r.IsDBNull(13) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(13),
                    lastStatus = r.IsDBNull(14) ? null : r.GetString(14),
                });
            }
            return Results.Ok(list);
        });

        g.MapGet("/{id:guid}", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT name, description, dataset, definition::text, shared, system, owner_id = @me, schedule::text, recipients, format
                  FROM zentriq.report_definitions WHERE id = @id AND (system OR shared OR owner_id = @me)
                """);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.Add(new NpgsqlParameter("me", NpgsqlDbType.Uuid) { Value = (object?)rc.UserId ?? DBNull.Value });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Results.NotFound();
            if (Denied(r.GetString(2), rc) is { } denied) return denied;
            return Results.Ok(new
            {
                id, name = r.GetString(0), description = r.IsDBNull(1) ? null : r.GetString(1), dataset = r.GetString(2),
                definition = ReportSpec.Parse(r.GetString(3)), shared = r.GetBoolean(4), system = r.GetBoolean(5), owned = !r.IsDBNull(6) && r.GetBoolean(6),
                schedule = r.IsDBNull(7) ? null : JsonSerializer.Deserialize<ReportSchedule>(r.GetString(7)), recipients = r.GetFieldValue<string[]>(8), format = r.GetString(9),
            });
        });

        g.MapPost("/run", async (RunBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (Denied(body.Dataset, rc) is { } denied) return denied;
            try
            {
                var result = await ReportEngine.RunAsync(ds, body.Dataset, body.Definition, Math.Clamp(body.Limit ?? 500, 1, 5000), ct);
                return Results.Ok(new { columns = result.Columns, rows = result.Rows, total = result.Total, truncated = result.Truncated });
            }
            catch (ReportValidationException ex) { return Validation.Problem("definition", ex.Message); }
        });

        g.MapPost("/export", async (ExportBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (Denied(body.Dataset, rc) is { } denied) return denied;
            var format = body.Format is "csv" or "pdf" ? body.Format : "xlsx";
            var name = string.IsNullOrWhiteSpace(body.Name) ? ReportCatalog.Find(body.Dataset)?.Name ?? "Report" : body.Name.Trim();
            ReportResult result;
            try { result = await ReportEngine.RunAsync(ds, body.Dataset, body.Definition, format == "pdf" ? PdfWriter.MaxPdfRows : ReportDelivery.MaxExportRows, ct); }
            catch (ReportValidationException ex) { return Validation.Problem("definition", ex.Message); }
            var (content, fileName, contentType) = ReportDelivery.Render(name, format, result, chart: body.Definition.Chart);
            await rc.AuditAsync("report.export", name, detail: new { body.Dataset, format, rows = result.Rows.Count });
            return Results.File(content, contentType, fileName);
        }).RequireAuthorization(Permissions.ReportsExport);

        g.MapPost("", async (ReportBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (Check(body, rc) is { } err) return err;
            var id = Guid.NewGuid();
            await using var cmd = ds.CreateCommand("""
                INSERT INTO zentriq.report_definitions (id, name, description, dataset, definition, shared, owner_id, updated_by)
                VALUES (@id, @n, @d, @ds, @def::jsonb, @sh, @me, @me)
                """);
            cmd.Parameters.AddWithValue("id", id);
            Fill(cmd, body, rc);
            await cmd.ExecuteNonQueryAsync(ct);
            await rc.AuditAsync("report.create", body.Name.Trim(), detail: new { id, body.Dataset });
            return Results.Ok(new { id });
        }).RequireAuthorization(Permissions.ReportsDesign);

        g.MapPut("/{id:guid}", async (Guid id, ReportBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (Check(body, rc) is { } err) return err;
            if (await EditableAsync(ds, id, rc, ct) is { } denied) return denied;
            await using var cmd = ds.CreateCommand("""
                UPDATE zentriq.report_definitions SET name = @n, description = @d, dataset = @ds, definition = @def::jsonb, shared = @sh, updated_by = @me, updated_at = now()
                 WHERE id = @id
                """);
            cmd.Parameters.AddWithValue("id", id);
            Fill(cmd, body, rc);
            await cmd.ExecuteNonQueryAsync(ct);
            await rc.AuditAsync("report.update", body.Name.Trim(), detail: new { id });
            return Results.NoContent();
        }).RequireAuthorization(Permissions.ReportsDesign);

        g.MapDelete("/{id:guid}", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (await EditableAsync(ds, id, rc, ct) is { } denied) return denied;
            await using var cmd = ds.CreateCommand("DELETE FROM zentriq.report_definitions WHERE id = @id RETURNING name");
            cmd.Parameters.AddWithValue("id", id);
            var name = await cmd.ExecuteScalarAsync(ct) as string;
            await rc.AuditAsync("report.delete", name, detail: new { id });
            return Results.NoContent();
        }).RequireAuthorization(Permissions.ReportsDesign);

        g.MapPut("/{id:guid}/schedule", async (Guid id, ScheduleBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (await EditableAsync(ds, id, rc, ct, schedule: true) is { } denied) return denied;
            var recipients = (body.Recipients ?? []).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (recipients.Length > 50) return Validation.Problem("recipients", "Up to 50 recipients.");
            if (recipients.FirstOrDefault(x => !Email.IsMatch(x)) is { } bad) return Validation.Problem("recipients", $"'{bad}' is not an e-mail address.");
            if (body.Schedule is { } s && s.Validate() is { } msg) return Validation.Problem("schedule", msg);
            if (body.Schedule is { Enabled: true } && recipients.Length == 0) return Validation.Problem("recipients", "Add at least one recipient for a scheduled report.");
            var next = body.Schedule is { Enabled: true } sc ? sc.NextRun(DateTimeOffset.UtcNow) : (DateTimeOffset?)null;
            await using var cmd = ds.CreateCommand("""
                UPDATE zentriq.report_definitions SET schedule = @s::jsonb, recipients = @r, format = @f, next_run_at = @n, updated_by = @me, updated_at = now() WHERE id = @id
                """);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Text) { Value = body.Schedule is null ? DBNull.Value : JsonSerializer.Serialize(body.Schedule) });
            cmd.Parameters.Add(new NpgsqlParameter("r", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = recipients });
            cmd.Parameters.AddWithValue("f", body.Format is "csv" or "pdf" ? body.Format : "xlsx");
            cmd.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.TimestampTz) { Value = (object?)next ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("me", NpgsqlDbType.Uuid) { Value = (object?)rc.UserId ?? DBNull.Value });
            await cmd.ExecuteNonQueryAsync(ct);
            await rc.AuditAsync("report.schedule", id.ToString(), detail: new { body.Schedule, recipients = recipients.Length });
            return Results.Ok(new { nextRunAt = next, scheduleText = body.Schedule is { Enabled: true } x ? x.Describe() : null });
        }).RequireAuthorization(Permissions.ReportsDesign);

        g.MapPost("/{id:guid}/send", async (Guid id, NpgsqlDataSource ds, JobQueue jobs, RequestContext rc, CancellationToken ct) =>
        {
            await using (var cmd = ds.CreateCommand("SELECT dataset FROM zentriq.report_definitions WHERE id = @id AND (system OR shared OR owner_id = @me)"))
            {
                cmd.Parameters.AddWithValue("id", id);
                cmd.Parameters.Add(new NpgsqlParameter("me", NpgsqlDbType.Uuid) { Value = (object?)rc.UserId ?? DBNull.Value });
                if (await cmd.ExecuteScalarAsync(ct) is not string dataset) return Results.NotFound();
                if (Denied(dataset, rc) is { } denied) return denied;
            }
            var jobId = await jobs.EnqueueAsync(new EnqueueRequest(JobTypes.ReportDeliver, new { reportId = id, trigger = "manual", requestedBy = rc.UserId },
                MaxAttempts: 1, TimeoutSeconds: 1800, CreatedBy: rc.UserId), ct: ct);
            await rc.AuditAsync("report.send", id.ToString());
            return Results.Accepted(value: new { jobId, message = "The report is being generated and sent; see Run history." });
        }).RequireAuthorization(Permissions.ReportsExport);

        g.MapGet("/runs", async (Guid? reportId, int? page, int? pageSize, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            var (p, ps) = Paging.Normalize(page, pageSize);
            await using var cmd = ds.CreateCommand("""
                SELECT x.id, x.report_id, x.report_name, x.trigger, x.status, x.row_count, x.format, x.file_name, x.content IS NOT NULL, x.recipients, x.error,
                       u.display_name, x.started_at, x.finished_at, count(*) OVER ()
                  FROM zentriq.report_runs x LEFT JOIN zentriq.users u ON u.id = x.requested_by
                  LEFT JOIN zentriq.report_definitions d ON d.id = x.report_id
                 WHERE (@r::uuid IS NULL OR x.report_id = @r) AND (d.id IS NULL OR d.system OR d.shared OR d.owner_id = @me)
                 ORDER BY x.started_at DESC LIMIT @l OFFSET @o
                """);
            cmd.Parameters.Add(new NpgsqlParameter("r", NpgsqlDbType.Uuid) { Value = (object?)reportId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("me", NpgsqlDbType.Uuid) { Value = (object?)rc.UserId ?? DBNull.Value });
            cmd.Parameters.AddWithValue("l", ps);
            cmd.Parameters.AddWithValue("o", (p - 1) * ps);
            var items = new List<object>();
            long total = 0;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                total = r.GetInt64(14);
                items.Add(new
                {
                    id = r.GetGuid(0), reportId = r.IsDBNull(1) ? (Guid?)null : r.GetGuid(1), reportName = r.GetString(2), trigger = r.GetString(3), status = r.GetString(4),
                    rows = r.IsDBNull(5) ? (int?)null : r.GetInt32(5), format = r.IsDBNull(6) ? null : r.GetString(6), fileName = r.IsDBNull(7) ? null : r.GetString(7),
                    downloadable = r.GetBoolean(8), recipients = r.GetFieldValue<string[]>(9), error = r.IsDBNull(10) ? null : r.GetString(10),
                    requestedBy = r.IsDBNull(11) ? null : r.GetString(11), startedAt = r.GetFieldValue<DateTimeOffset>(12),
                    finishedAt = r.IsDBNull(13) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(13),
                });
            }
            return Results.Ok(new { items, total, page = p, pageSize = ps });
        });

        g.MapGet("/runs/{id:guid}/file", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT x.file_name, x.format, x.content, d.dataset FROM zentriq.report_runs x LEFT JOIN zentriq.report_definitions d ON d.id = x.report_id
                 WHERE x.id = @id AND x.content IS NOT NULL AND (d.id IS NULL OR d.system OR d.shared OR d.owner_id = @me)
                """);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.Add(new NpgsqlParameter("me", NpgsqlDbType.Uuid) { Value = (object?)rc.UserId ?? DBNull.Value });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Results.NotFound();
            if (!r.IsDBNull(3) && Denied(r.GetString(3), rc) is { } denied) return denied;
            await rc.AuditAsync("report.download", r.GetString(0));
            return Results.File(r.GetFieldValue<byte[]>(2), ReportDelivery.ContentType(r.GetString(1)), r.GetString(0));
        }).RequireAuthorization(Permissions.ReportsExport);
    }

    private static IResult? Check(ReportBody body, RequestContext rc)
    {
        if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 120) return Validation.Problem("name", "Enter a name (max 120 characters).");
        if (body.Description is { Length: > 500 }) return Validation.Problem("description", "Description: max 500 characters.");
        if (body.Shared && !rc.Has(Permissions.DashboardsShare) && !rc.Has(Permissions.SettingsManage)) return Validation.Problem("shared", "You do not have permission to share reports.");
        var ds = ReportCatalog.Find(body.Dataset);
        if (ds is null) return Validation.Problem("dataset", "Choose a dataset.");
        if (!CanUse(ds, rc)) return Results.Problem(statusCode: 403, title: $"You do not have permission to use the {ds.Name} data source.");
        try { ReportEngine.Validate(ds, body.Definition); }
        catch (ReportValidationException ex) { return Validation.Problem("definition", ex.Message); }
        return null;
    }

    /// <summary>Data sources with their own permission (vulnerabilities, audit trail) need it, or administrator rights.</summary>
    internal static bool CanUse(ReportDataset d, RequestContext rc) => d.Permission is null || rc.Has(d.Permission) || rc.Has(Permissions.SettingsManage);

    private static IResult? Denied(string dataset, RequestContext rc) =>
        ReportCatalog.Find(dataset) is { } d && !CanUse(d, rc) ? Results.Problem(statusCode: 403, title: $"You do not have permission to use the {d.Name} data source.") : null;

    private static void Fill(NpgsqlCommand cmd, ReportBody body, RequestContext rc)
    {
        cmd.Parameters.AddWithValue("n", body.Name.Trim());
        cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(body.Description) ? DBNull.Value : body.Description.Trim() });
        cmd.Parameters.AddWithValue("ds", body.Dataset);
        cmd.Parameters.AddWithValue("def", body.Definition.ToJson());
        cmd.Parameters.AddWithValue("sh", body.Shared);
        cmd.Parameters.Add(new NpgsqlParameter("me", NpgsqlDbType.Uuid) { Value = (object?)rc.UserId ?? DBNull.Value });
    }

    /// <summary>Built-in templates are read-only; others can be changed by their owner or an administrator.</summary>
    private static async Task<IResult?> EditableAsync(NpgsqlDataSource ds, Guid id, RequestContext rc, CancellationToken ct, bool schedule = false)
    {
        await using var cmd = ds.CreateCommand("SELECT system, owner_id FROM zentriq.report_definitions WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return Results.NotFound();
        var admin = rc.Has(Permissions.SettingsManage);
        if (r.GetBoolean(0))
            return schedule && admin ? null : Results.Problem(statusCode: 403, title: "Built-in reports are read-only. Use 'Save as' to make your own copy.");
        var owner = r.IsDBNull(1) ? (Guid?)null : r.GetGuid(1);
        if (owner != rc.UserId && !admin) return Results.Problem(statusCode: 403, title: "Only the owner or an administrator can change this report.");
        return null;
    }
}
