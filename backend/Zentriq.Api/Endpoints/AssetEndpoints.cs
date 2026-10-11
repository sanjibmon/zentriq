using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;
using Zentriq.Infrastructure.Data;
using Zentriq.Infrastructure.Licensing;

namespace Zentriq.Api.Endpoints;

public static class AssetEndpoints
{
    public sealed record AssetDto(Guid Id, string? Hostname, string? Fqdn, string? Ip, string? SerialNumber, string Category,
        string Workload, string? OsFamily, string? OsName, string Status, string Criticality, string? OwnerName, string Source,
        string InventoryStatus, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt, DateTimeOffset? LastInventoryAt, int RowVersion);

    public sealed record AssetBody(string? Hostname, string? Ip, string? SerialNumber, string Category, string? OsFamily,
        string? OsName, string? Criticality, string? OwnerName, int? RowVersion);

    private static readonly string[] Criticalities = ["Critical", "High", "Medium", "Low"];
    private static readonly string[] OsFamilies = ["windows", "linux", "macos", "network", "other"];

    public static AssetDto ToDto(Asset a) => new(a.Id, a.Hostname, a.Fqdn, a.PrimaryIp?.ToString(), a.SerialNumber, a.Category,
        Workloads.FromAssetCategory(a.Category), a.OsFamily, a.OsName, a.Status, a.Criticality, a.OwnerName, a.Source,
        a.InventoryStatus, a.FirstSeenAt, a.LastSeenAt, a.LastInventoryAt, a.RowVersion);

    public static void MapAssetEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/assets").WithTags("Assets").RequireModule(Modules.Inventory);

        g.MapGet("", async (ZentriqDbContext db, string? q, string? category, string? status, string? sort, string? dir, int? page, int? pageSize, CancellationToken ct) =>
        {
            var (p, ps) = Paging.Normalize(page, pageSize);
            var query = db.Assets.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                query = query.Where(a => (a.Hostname != null && a.Hostname.ToLower().Contains(term)) ||
                                         (a.SerialNumber != null && a.SerialNumber.ToLower().Contains(term)) ||
                                         (a.OsName != null && a.OsName.ToLower().Contains(term)) ||
                                         (a.OwnerName != null && a.OwnerName.ToLower().Contains(term)));
            }
            if (!string.IsNullOrEmpty(category)) query = query.Where(a => a.Category == category);
            query = string.IsNullOrEmpty(status) ? query.Where(a => a.Status == "active") : status == "all" ? query : query.Where(a => a.Status == status);
            var desc = dir == "desc";
            query = sort switch
            {
                "lastSeenAt" => desc ? query.OrderByDescending(a => a.LastSeenAt) : query.OrderBy(a => a.LastSeenAt),
                "category" => desc ? query.OrderByDescending(a => a.Category) : query.OrderBy(a => a.Category),
                "criticality" => desc ? query.OrderByDescending(a => a.Criticality) : query.OrderBy(a => a.Criticality),
                _ => desc ? query.OrderByDescending(a => a.Hostname) : query.OrderBy(a => a.Hostname),
            };
            var total = await query.CountAsync(ct);
            var items = await query.Skip((p - 1) * ps).Take(ps).ToListAsync(ct);
            return Results.Ok(new PagedResult<AssetDto>(items.Select(ToDto).ToList(), total, p, ps));
        }).RequireAuthorization(Permissions.AssetsView);

        g.MapGet("/{id:guid}", async (Guid id, ZentriqDbContext db, CancellationToken ct) =>
            await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct) is { } a ? Results.Ok(ToDto(a)) : Results.NotFound())
            .RequireAuthorization(Permissions.AssetsView);

        g.MapPost("", async (AssetBody body, ZentriqDbContext db, LicensingService licensing, RequestContext rc, CancellationToken ct) =>
        {
            var err = Validate(body, out var ip);
            if (err is not null) return err;
            var now = DateTimeOffset.UtcNow;
            var asset = new Asset
            {
                Id = Guid.NewGuid(), Hostname = body.Hostname?.Trim(), PrimaryIp = ip, SerialNumber = body.SerialNumber?.Trim(),
                Category = body.Category, OsFamily = body.OsFamily, OsName = body.OsName?.Trim(), Criticality = body.Criticality ?? "Medium",
                OwnerName = body.OwnerName?.Trim(), Source = "manual", FirstSeenAt = now, LastSeenAt = now, CreatedAt = now, UpdatedAt = now,
            };

            // Licensing: count + insert under one transaction and advisory lock (concurrent enrolment safe).
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var status = await rc.LicenseAsync(ct);
            await licensing.EnsureCapacityAsync((NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction)tx.GetDbTransaction(),
                Workloads.FromAssetCategory(asset.Category), 1, status, ct);
            db.Assets.Add(asset);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            await rc.AuditAsync("asset.create", asset.Hostname ?? asset.PrimaryIp?.ToString(), detail: new { asset.Id, asset.Category });
            return Results.Created($"/api/v1/assets/{asset.Id}", ToDto(asset));
        }).RequireAuthorization(Permissions.AssetsManage);

        g.MapPut("/{id:guid}", async (Guid id, AssetBody body, ZentriqDbContext db, LicensingService licensing, RequestContext rc, CancellationToken ct) =>
        {
            var err = Validate(body, out var ip);
            if (err is not null) return err;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var a = await db.Assets.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (a is null) return Results.NotFound();
            if (body.RowVersion is { } rv && rv != a.RowVersion) return Results.Problem(statusCode: 409, title: "The asset was changed by someone else. Reload and try again.");
            var oldWorkload = Workloads.FromAssetCategory(a.Category);
            var newWorkload = Workloads.FromAssetCategory(body.Category);
            if (a.Status == "active" && oldWorkload != newWorkload)
                await licensing.EnsureCapacityAsync((NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction)tx.GetDbTransaction(), newWorkload, 1, await rc.LicenseAsync(ct), ct);
            a.Hostname = body.Hostname?.Trim(); a.PrimaryIp = ip; a.SerialNumber = body.SerialNumber?.Trim(); a.Category = body.Category;
            a.OsFamily = body.OsFamily; a.OsName = body.OsName?.Trim(); a.Criticality = body.Criticality ?? a.Criticality; a.OwnerName = body.OwnerName?.Trim();
            a.UpdatedAt = DateTimeOffset.UtcNow; a.RowVersion++;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            await rc.AuditAsync("asset.update", a.Hostname, detail: new { a.Id });
            return Results.Ok(ToDto(a));
        }).RequireAuthorization(Permissions.AssetsManage);

        g.MapPost("/{id:guid}/retire", async (Guid id, ZentriqDbContext db, RequestContext rc, CancellationToken ct) =>
        {
            var a = await db.Assets.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (a is null) return Results.NotFound();
            if (a.Status == "retired") return Results.Ok(ToDto(a));
            a.Status = "retired"; a.RetiredAt = DateTimeOffset.UtcNow; a.UpdatedAt = DateTimeOffset.UtcNow; a.RowVersion++;
            await db.SaveChangesAsync(ct);
            await rc.AuditAsync("asset.retire", a.Hostname, detail: new { a.Id });
            return Results.Ok(ToDto(a));
        }).RequireAuthorization(Permissions.AssetsManage);

        g.MapPost("/{id:guid}/merge-into/{survivorId:guid}", async (Guid id, Guid survivorId, ZentriqDbContext db, RequestContext rc, CancellationToken ct) =>
        {
            if (id == survivorId) return Results.Problem(statusCode: 400, title: "An asset cannot be merged into itself.");
            var dup = await db.Assets.FirstOrDefaultAsync(x => x.Id == id, ct);
            var survivor = await db.Assets.FirstOrDefaultAsync(x => x.Id == survivorId, ct);
            if (dup is null || survivor is null) return Results.NotFound();
            if (survivor.Status != "active") return Results.Problem(statusCode: 400, title: "The surviving asset must be active.");
            dup.Status = "duplicate"; dup.MergedInto = survivor.Id; dup.UpdatedAt = DateTimeOffset.UtcNow; dup.RowVersion++;
            if (dup.LastSeenAt > survivor.LastSeenAt) survivor.LastSeenAt = dup.LastSeenAt;
            await db.SaveChangesAsync(ct);
            await rc.AuditAsync("asset.merge", dup.Hostname, detail: new { duplicate = dup.Id, survivor = survivor.Id });
            return Results.Ok(ToDto(survivor));
        }).RequireAuthorization(Permissions.AssetsManage);

        // Inventory summary used by dashboards.
        g.MapGet("/summary", async (ZentriqDbContext db, CancellationToken ct) =>
        {
            var active = db.Assets.AsNoTracking().Where(a => a.Status == "active");
            var byCategory = await active.GroupBy(a => a.Category).Select(x => new { key = x.Key, count = x.Count() }).ToListAsync(ct);
            var byOs = await active.GroupBy(a => a.OsFamily ?? "unknown").Select(x => new { key = x.Key, count = x.Count() }).ToListAsync(ct);
            var byInventory = await active.GroupBy(a => a.InventoryStatus).Select(x => new { key = x.Key, count = x.Count() }).ToListAsync(ct);
            var byCriticality = await active.GroupBy(a => a.Criticality).Select(x => new { key = x.Key, count = x.Count() }).ToListAsync(ct);
            var stale = await active.CountAsync(a => a.LastSeenAt < DateTimeOffset.UtcNow.AddDays(-30), ct);
            var total = await active.CountAsync(ct);
            var retired = await db.Assets.CountAsync(a => a.Status == "retired", ct);
            return Results.Ok(new { total, retired, stale, byCategory, byOs, byInventory, byCriticality });
        }).RequireAuthorization(Permissions.AssetsView);
    }

    private static IResult? Validate(AssetBody b, out IPAddress? ip)
    {
        ip = null;
        if (string.IsNullOrWhiteSpace(b.Hostname) && string.IsNullOrWhiteSpace(b.Ip))
            return Validation.Problem("hostname", "Enter a hostname or an IP address.");
        if (b.Hostname is { Length: > 255 }) return Validation.Problem("hostname", "Hostname is too long.");
        if (!string.IsNullOrWhiteSpace(b.Ip) && !IPAddress.TryParse(b.Ip.Trim(), out ip)) return Validation.Problem("ip", "Enter a valid IP address.");
        if (!AssetCategories.All.Contains(b.Category)) return Validation.Problem("category", "Unknown category.");
        if (b.Criticality is not null && !Criticalities.Contains(b.Criticality)) return Validation.Problem("criticality", "Unknown criticality.");
        if (b.OsFamily is not null && !OsFamilies.Contains(b.OsFamily)) return Validation.Problem("osFamily", "Unknown OS family.");
        return null;
    }
}
