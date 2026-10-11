using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;
using Zentriq.Infrastructure.Data;
using Zentriq.Infrastructure.Jobs;

namespace Zentriq.Api.Endpoints;

public static class JobEndpoints
{
    public static void MapJobEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/jobs").WithTags("Jobs");

        g.MapGet("", async (ZentriqDbContext db, string? status, string? type, int? page, int? pageSize, CancellationToken ct) =>
        {
            var (p, ps) = Paging.Normalize(page, pageSize);
            var q = db.Jobs.AsNoTracking().AsQueryable();
            if (status == "active") q = q.Where(j => j.Status == "queued" || j.Status == "running");
            else if (!string.IsNullOrEmpty(status)) q = q.Where(j => j.Status == status);
            if (!string.IsNullOrEmpty(type)) q = q.Where(j => j.JobType == type);
            var total = await q.CountAsync(ct);
            var rows = await q.OrderByDescending(j => j.CreatedAt).Skip((p - 1) * ps).Take(ps)
                .Select(j => new { j.Id, j.JobType, j.Status, j.Attempts, j.MaxAttempts, j.ProgressPct, j.ProgressText, j.LastError, j.CreatedAt, j.StartedAt, j.FinishedAt, j.CancelRequested })
                .ToListAsync(ct);
            return Results.Ok(new PagedResult<object>(rows.Cast<object>().ToList(), total, p, ps));
        }).RequireAuthorization(Permissions.JobsView);

        g.MapGet("/{id:guid}", async (Guid id, ZentriqDbContext db, CancellationToken ct) =>
        {
            var j = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (j is null) return Results.NotFound();
            var events = await db.JobEvents.AsNoTracking().Where(e => e.JobId == id).OrderBy(e => e.Id).Take(1000)
                .Select(e => new { e.OccurredAt, e.Level, e.Message }).ToListAsync(ct);
            return Results.Ok(new
            {
                j.Id, j.JobType, j.Status, j.Attempts, j.MaxAttempts, j.ProgressPct, j.ProgressText, j.LastError, j.CreatedAt, j.StartedAt, j.FinishedAt,
                j.CancelRequested, result = j.Result is null ? (JsonElement?)null : JsonDocument.Parse(j.Result).RootElement, events,
            });
        }).RequireAuthorization(Permissions.JobsView);

        g.MapPost("/{id:guid}/cancel", async (Guid id, JobQueue jobs, RequestContext rc, CancellationToken ct) =>
        {
            var status = await jobs.RequestCancelAsync(id, ct);
            if (status is null) return Results.Problem(statusCode: 409, title: "The job is not running or queued.");
            await rc.AuditAsync("job.cancel", id.ToString(), detail: new { status });
            return Results.Ok(new { id, status });
        }).RequireAuthorization(Permissions.JobsView);
    }
}
