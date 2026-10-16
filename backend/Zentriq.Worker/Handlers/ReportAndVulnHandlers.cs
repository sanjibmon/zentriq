using System.Text.Json;
using Npgsql;
using Zentriq.Domain;
using Zentriq.Infrastructure.Reports;
using Zentriq.Infrastructure.Vulnerabilities;

namespace Zentriq.Worker.Handlers;

/// <summary>Runs a saved report (scheduled or "send now"), keeps the file in the run history and e-mails it.</summary>
public sealed class ReportDeliveryHandler(ReportDelivery delivery) : IJobHandler
{
    public string JobType => JobTypes.ReportDeliver;

    public async Task<object?> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var p = JsonDocument.Parse(context.Job.Payload).RootElement;
        var id = p.GetProperty("reportId").GetGuid();
        var trigger = p.TryGetProperty("trigger", out var t) && t.GetString() == "manual" ? "manual" : "schedule";
        Guid? by = p.TryGetProperty("requestedBy", out var b) && b.ValueKind == JsonValueKind.String && Guid.TryParse(b.GetString(), out var g) ? g : null;
        var def = await delivery.LoadAsync(id, ct) ?? throw new PermanentJobException("The report no longer exists.");
        await context.LogAsync($"Running '{def.Name}' ({def.Dataset}).", ct: ct);
        var (runId, rows, message) = await delivery.DeliverAsync(def, trigger, by, ct);
        await context.LogAsync(message, ct: ct);
        return new { runId, rows, message };
    }
}

/// <summary>Daily (or on demand) update of NVD, CISA KEV and EPSS data followed by re-evaluation of findings.</summary>
public sealed class VulnSyncHandler(VulnFeedService feeds) : IJobHandler
{
    public string JobType => JobTypes.VulnSync;

    public async Task<object?> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var s = await feeds.SyncAsync(m => context.LogAsync(m, ct: ct), (pct, text) => context.ProgressAsync(pct, text, ct), ct);
        return new
        {
            s.NewTitles, s.Products, s.Cves, s.WindowsCves, s.Kev, s.Epss,
            open = s.Evaluation?.Open, added = s.Evaluation?.New, fixedCount = s.Evaluation?.Fixed,
            problems = s.Problems.Take(20),
        };
    }
}

/// <summary>Re-evaluates installed software against the CVE data already in the database (no downloads).</summary>
public sealed class VulnEvaluateHandler(VulnFeedService feeds) : IJobHandler
{
    public string JobType => JobTypes.VulnEvaluate;

    public async Task<object?> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var e = await feeds.EvaluateAsync(ct);
        await context.LogAsync($"{e.Installs} installation(s) of {e.Products} product(s) and {e.OsAssessed} Windows device(s) checked ({e.OsNotAssessed} without a full build): {e.Open} open finding(s); {e.New} new, {e.Fixed} fixed, {e.Reopened} reopened.", ct: ct);
        return e;
    }
}

/// <summary>Imports an uploaded NVD / KEV / EPSS file (offline servers), then re-evaluates findings.</summary>
public sealed class VulnImportHandler(NpgsqlDataSource ds, VulnFeedService feeds) : IJobHandler
{
    public string JobType => JobTypes.VulnImport;

    public async Task<object?> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var id = JsonDocument.Parse(context.Job.Payload).RootElement.GetProperty("importId").GetGuid();
        string source, name;
        byte[] content;
        await using (var cmd = ds.CreateCommand("UPDATE zentriq.vuln_imports SET status = 'running' WHERE id = @id AND content IS NOT NULL RETURNING source, file_name, content"))
        {
            cmd.Parameters.AddWithValue("id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw new PermanentJobException("The uploaded file is no longer available.");
            source = r.GetString(0); name = r.GetString(1); content = r.GetFieldValue<byte[]>(2);
        }
        await context.LogAsync($"Importing {source.ToUpperInvariant()} file {name} ({content.Length / 1024} KB).", ct: ct);
        try
        {
            var (items, message) = await feeds.ImportAsync(source, content, ct);
            await Finish(id, "done", items, message, ct);
            await context.LogAsync(message, ct: ct);
            var e = await feeds.EvaluateAsync(ct);
            await context.LogAsync($"Evaluation: {e.Open} open finding(s); {e.New} new, {e.Fixed} fixed.", ct: ct);
            return new { items, message, open = e.Open, added = e.New };
        }
        catch (Exception ex) when (ex is FeedException or JsonException or InvalidDataException)
        {
            await Finish(id, "failed", null, ex.Message, ct);
            throw new PermanentJobException(ex.Message);
        }
    }

    private async Task Finish(Guid id, string status, int? items, string message, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("UPDATE zentriq.vuln_imports SET status = @s, items = @n, message = @m, content = NULL, finished_at = now() WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("s", status);
        cmd.Parameters.Add(new NpgsqlParameter("n", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)items ?? DBNull.Value });
        cmd.Parameters.AddWithValue("m", message);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
