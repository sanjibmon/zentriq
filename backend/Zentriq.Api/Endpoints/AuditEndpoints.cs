using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;
using Zentriq.Infrastructure.Data;

namespace Zentriq.Api.Endpoints;

public static class AuditEndpoints
{
    public static void MapAuditEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/audit", async (ZentriqDbContext db, string? q, string? action, string? outcome, DateTimeOffset? from, DateTimeOffset? to, int? page, int? pageSize, CancellationToken ct) =>
        {
            var (p, ps) = Paging.Normalize(page, pageSize);
            var query = db.AuditEvents.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(q)) { var t = q.Trim().ToLower(); query = query.Where(a => a.ActorName.ToLower().Contains(t) || (a.Target != null && a.Target.ToLower().Contains(t)) || a.Action.Contains(t)); }
            if (!string.IsNullOrEmpty(action)) query = query.Where(a => a.Action.StartsWith(action));
            if (!string.IsNullOrEmpty(outcome)) query = query.Where(a => a.Outcome == outcome);
            if (from is not null) query = query.Where(a => a.OccurredAt >= from.Value.ToUniversalTime());
            if (to is not null) query = query.Where(a => a.OccurredAt < to.Value.ToUniversalTime());
            var total = await query.CountAsync(ct);
            var rows = await query.OrderByDescending(a => a.Id).Skip((p - 1) * ps).Take(ps).ToListAsync(ct);
            return Results.Ok(new PagedResult<object>(rows.Select(a => (object)new
            {
                a.Id, a.OccurredAt, a.ActorName, a.SourceIp, a.Action, a.Target, a.Outcome, detail = JsonDocument.Parse(a.Detail).RootElement,
            }).ToList(), total, p, ps));
        }).WithTags("Audit").RequireAuthorization(Permissions.AuditView);
    }
}
