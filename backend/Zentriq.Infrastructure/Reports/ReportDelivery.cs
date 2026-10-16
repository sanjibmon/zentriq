using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Infrastructure.Mail;

namespace Zentriq.Infrastructure.Reports;

/// <summary>When a scheduled report runs. Times are the server's local time zone.</summary>
public sealed class ReportSchedule
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("frequency")] public string Frequency { get; set; } = "weekly";   // daily | weekly | monthly
    [JsonPropertyName("hour")] public int Hour { get; set; } = 7;
    [JsonPropertyName("weekday")] public int Weekday { get; set; } = 1;                  // 0 = Sunday … 6 = Saturday
    [JsonPropertyName("day")] public int Day { get; set; } = 1;                          // day of month 1–28

    public string? Validate()
    {
        if (Frequency is not ("daily" or "weekly" or "monthly")) return "Choose daily, weekly or monthly.";
        if (Hour is < 0 or > 23) return "Hour must be 0–23.";
        if (Weekday is < 0 or > 6) return "Choose a weekday.";
        if (Day is < 1 or > 28) return "Day of the month must be 1–28.";
        return null;
    }

    /// <summary>Next run strictly after <paramref name="after"/>.</summary>
    public DateTimeOffset NextRun(DateTimeOffset after, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(after, zone).DateTime;
        var candidate = new DateTime(local.Year, local.Month, local.Day, Hour, 0, 0, DateTimeKind.Unspecified);
        for (var i = 0; i < 400; i++)
        {
            var ok = Frequency switch
            {
                "daily" => true,
                "weekly" => (int)candidate.DayOfWeek == Weekday,
                _ => candidate.Day == Day,
            };
            if (ok && candidate > local) return new DateTimeOffset(candidate, zone.GetUtcOffset(candidate));
            candidate = candidate.AddDays(1);
        }
        return after.AddDays(1);
    }

    public string Describe()
    {
        var at = $"{Hour:00}:00";
        return Frequency switch
        {
            "daily" => $"Daily at {at}",
            "weekly" => $"Every {CultureInfo.InvariantCulture.DateTimeFormat.GetDayName((DayOfWeek)Weekday)} at {at}",
            _ => $"Monthly on day {Day} at {at}",
        };
    }
}

/// <summary>Runs a saved report, keeps the file in the run history and e-mails it to the recipients.</summary>
public sealed class ReportDelivery(NpgsqlDataSource ds, MailService mail)
{
    public const int MaxExportRows = 100_000;

    public sealed record Definition(Guid Id, string Name, string Dataset, ReportSpec Spec, string Format, string[] Recipients, string? Description, Guid? OwnerId = null);

    public async Task<Definition?> LoadAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT name, dataset, definition::text, format, recipients, description, owner_id FROM zentriq.report_definitions WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new Definition(id, r.GetString(0), r.GetString(1), ReportSpec.Parse(r.GetString(2)), r.GetString(3), r.GetFieldValue<string[]>(4),
            r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetGuid(6));
    }

    public static string ContentType(string format) => format switch
    {
        "csv" => "text/csv",
        "pdf" => "application/pdf",
        _ => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
    };

    public static (byte[] Content, string FileName, string ContentType) Render(string name, string format, ReportResult result, string? subtitle = null, string? chart = null)
    {
        var safe = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        if (safe.Length == 0) safe = "report";
        var stamp = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return format switch
        {
            "csv" => (ReportEngine.ToCsv(result), $"{safe}-{stamp}.csv", ContentType("csv")),
            "pdf" => (PdfWriter.Write(name, subtitle ?? $"{result.Rows.Count:N0} row(s) · generated {DateTime.Now:yyyy-MM-dd HH:mm}", result, chart), $"{safe}-{stamp}.pdf", ContentType("pdf")),
            _ => (XlsxWriter.Write(name, result), $"{safe}-{stamp}.xlsx", ContentType("xlsx")),
        };
    }

    /// <summary>Scheduled runs act for the report's owner: a data source with its own permission needs it (or administrator rights).</summary>
    public async Task<bool> OwnerMayUseAsync(Definition def, CancellationToken ct)
    {
        if (ReportCatalog.Find(def.Dataset) is not { Permission: { } perm } || def.OwnerId is null) return true;
        await using var cmd = ds.CreateCommand("""
            SELECT EXISTS (SELECT 1 FROM zentriq.user_roles ur JOIN zentriq.role_permissions rp ON rp.role_id = ur.role_id
                             JOIN zentriq.users u ON u.id = ur.user_id AND u.status = 'active'
                            WHERE ur.user_id = @u AND (rp.permission_code = @p OR rp.permission_code = 'settings.manage'))
            """);
        cmd.Parameters.AddWithValue("u", def.OwnerId.Value);
        cmd.Parameters.AddWithValue("p", perm);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    /// <summary>Runs the report and records a run; returns the run id. Throws only for unexpected failures.</summary>
    public async Task<(Guid RunId, int Rows, string Message)> DeliverAsync(Definition def, string trigger, Guid? requestedBy, CancellationToken ct)
    {
        var runId = Guid.NewGuid();
        await using (var ins = ds.CreateCommand("""
            INSERT INTO zentriq.report_runs (id, report_id, report_name, trigger, status, format, recipients, requested_by)
            VALUES (@id, @r, @n, @t, 'running', @f, @rc, @by)
            """))
        {
            ins.Parameters.AddWithValue("id", runId);
            ins.Parameters.AddWithValue("r", def.Id);
            ins.Parameters.AddWithValue("n", def.Name);
            ins.Parameters.AddWithValue("t", trigger);
            ins.Parameters.AddWithValue("f", def.Format);
            ins.Parameters.Add(new NpgsqlParameter("rc", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = def.Recipients });
            ins.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Uuid) { Value = (object?)requestedBy ?? DBNull.Value });
            await ins.ExecuteNonQueryAsync(ct);
        }
        try
        {
            if (!await OwnerMayUseAsync(def, ct))
                throw new ReportValidationException("The report's owner no longer has permission to use this data source; the report was not run.");
            var result = await ReportEngine.RunAsync(ds, def.Dataset, def.Spec, MaxExportRows, ct);
            var (content, fileName, contentType) = Render(def.Name, def.Format, result, def.Description is null ? null : $"{def.Description} · {result.Rows.Count:N0} row(s) · generated {DateTime.Now:yyyy-MM-dd HH:mm}", def.Spec.Chart);
            string message;
            var status = "succeeded";
            if (def.Recipients.Length == 0) message = $"{result.Rows.Count} row(s); no recipients, kept in the run history.";
            else
            {
                var smtp = await mail.LoadAsync(ct);
                if (!MailService.IsUsable(smtp))
                {
                    status = "failed";
                    message = "E-mail (SMTP) is not configured in System Settings › E-mail; the file is kept in the run history.";
                }
                else
                {
                    var failed = new List<string>();
                    var text = new StringBuilder()
                        .AppendLine($"Zentriq report: {def.Name}")
                        .AppendLine(def.Description ?? "")
                        .AppendLine()
                        .AppendLine($"{result.Rows.Count} row(s){(result.Truncated ? $" (first {result.Rows.Count} of {result.Total})" : "")}, generated {DateTime.Now:yyyy-MM-dd HH:mm}.")
                        .AppendLine($"The {(def.Format switch { "csv" => "CSV", "pdf" => "PDF", _ => "Excel" })} file is attached.")
                        .ToString();
                    var html = $"<p><b>Zentriq report: {WebUtility.HtmlEncode(def.Name)}</b></p>"
                               + (def.Description is null ? "" : $"<p>{WebUtility.HtmlEncode(def.Description)}</p>")
                               + $"<p>{result.Rows.Count} row(s), generated {DateTime.Now:yyyy-MM-dd HH:mm}. The file is attached.</p>"
                               + PreviewTable(result);
                    foreach (var to in def.Recipients)
                    {
                        try { await mail.SendAsync(smtp, to, $"Zentriq report: {def.Name}", text, html, [(fileName, contentType, content)], ct); }
                        catch (MailException ex) { failed.Add($"{to}: {ex.Message}"); }
                    }
                    if (failed.Count == def.Recipients.Length) status = "failed";
                    message = failed.Count == 0
                        ? $"{result.Rows.Count} row(s) sent to {def.Recipients.Length} recipient(s)."
                        : $"{result.Rows.Count} row(s); sending failed for {string.Join("; ", failed)}";
                }
            }
            await using var upd = ds.CreateCommand("""
                UPDATE zentriq.report_runs SET status = @s, row_count = @n, file_name = @fn, content = @c, error = @e, finished_at = now() WHERE id = @id;
                UPDATE zentriq.report_definitions SET last_run_at = now() WHERE id = @r;
                """);
            upd.Parameters.AddWithValue("id", runId);
            upd.Parameters.AddWithValue("r", def.Id);
            upd.Parameters.AddWithValue("s", status);
            upd.Parameters.AddWithValue("n", result.Rows.Count);
            upd.Parameters.AddWithValue("fn", fileName);
            upd.Parameters.AddWithValue("c", content);
            upd.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.Text) { Value = status == "failed" ? message : DBNull.Value });
            await upd.ExecuteNonQueryAsync(ct);
            return (runId, result.Rows.Count, message);
        }
        catch (ReportValidationException ex)
        {
            await FailAsync(runId, ex.Message, ct);
            return (runId, 0, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await FailAsync(runId, ex.Message, CancellationToken.None);
            throw;
        }
    }

    private async Task FailAsync(Guid runId, string error, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("UPDATE zentriq.report_runs SET status = 'failed', error = @e, finished_at = now() WHERE id = @id");
        cmd.Parameters.AddWithValue("id", runId);
        cmd.Parameters.AddWithValue("e", error.Length > 2000 ? error[..2000] : error);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string PreviewTable(ReportResult result)
    {
        var sb = new StringBuilder("<table style=\"border-collapse:collapse;font:12px Segoe UI,Arial\"><tr>");
        foreach (var c in result.Columns) sb.Append($"<th style=\"text-align:left;border-bottom:1px solid #ccc;padding:4px 8px\">{WebUtility.HtmlEncode(c.Label)}</th>");
        sb.Append("</tr>");
        foreach (var row in result.Rows.Take(20))
        {
            sb.Append("<tr>");
            for (var i = 0; i < row.Length; i++) sb.Append($"<td style=\"padding:3px 8px;border-bottom:1px solid #eee\">{WebUtility.HtmlEncode(ReportEngine.Text(row[i], result.Columns[i].Type))}</td>");
            sb.Append("</tr>");
        }
        sb.Append("</table>");
        if (result.Rows.Count > 20) sb.Append($"<p style=\"color:#666;font:12px Segoe UI,Arial\">First 20 of {result.Rows.Count} rows shown; all rows are in the attachment.</p>");
        return sb.ToString();
    }
}
