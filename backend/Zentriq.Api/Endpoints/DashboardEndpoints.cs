using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;
using Zentriq.Infrastructure.Data;

namespace Zentriq.Api.Endpoints;

/// <summary>Dashboard definitions, widget layout and filters persisted per user / shared.</summary>
public static class DashboardEndpoints
{
    public sealed record WidgetBody(string Type, string? Title, short Width, short Height, JsonElement? Settings);
    public sealed record DashboardBody(string Name, bool Shared, JsonElement? Filters, int RefreshSeconds, WidgetBody[] Widgets);

    public static readonly string[] WidgetTypes =
        ["kpi.assets", "assets.byCategory", "assets.byOs", "assets.byInventory", "assets.byCriticality", "scans.recent", "jobs.active", "license.status", "license.usage"];

    public static void MapDashboardEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/dashboards").WithTags("Dashboards").RequireModule(Modules.Dashboards).RequireAuthorization(Permissions.AssetsView);

        g.MapGet("", async (ZentriqDbContext db, RequestContext rc, CancellationToken ct) =>
        {
            var uid = rc.UserId;
            if (!await db.Dashboards.AnyAsync(d => d.OwnerId == uid || d.Shared, ct))
                await CreateDefaultAsync(db, uid, ct);
            var list = await db.Dashboards.AsNoTracking().Include(d => d.Widgets).Where(d => d.OwnerId == uid || d.Shared).OrderBy(d => d.CreatedAt).ToListAsync(ct);
            return Results.Ok(list.Select(d => Dto(d, uid)));
        });

        g.MapPost("", async (DashboardBody body, ZentriqDbContext db, RequestContext rc, CancellationToken ct) =>
        {
            var err = Validate(body, rc);
            if (err is not null) return err;
            var d = new Dashboard { Id = Guid.NewGuid(), OwnerId = rc.UserId, CreatedAt = DateTimeOffset.UtcNow };
            Apply(d, body);
            db.Dashboards.Add(d);
            await db.SaveChangesAsync(ct);
            await rc.AuditAsync("dashboard.create", d.Name, detail: new { d.Id });
            return Results.Created($"/api/v1/dashboards/{d.Id}", Dto(d, rc.UserId));
        });

        g.MapPut("/{id:guid}", async (Guid id, DashboardBody body, ZentriqDbContext db, RequestContext rc, CancellationToken ct) =>
        {
            var d = await db.Dashboards.Include(x => x.Widgets).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (d is null) return Results.NotFound();
            if (d.OwnerId != rc.UserId && !rc.Has(Permissions.SettingsManage)) return Results.Problem(statusCode: 403, title: "Only the owner or an administrator can change this dashboard.");
            var err = Validate(body, rc);
            if (err is not null) return err;
            db.DashboardWidgets.RemoveRange(d.Widgets);
            d.Widgets = [];
            Apply(d, body);
            await db.SaveChangesAsync(ct);
            await rc.AuditAsync("dashboard.update", d.Name, detail: new { d.Id });
            return Results.Ok(Dto(d, rc.UserId));
        });

        g.MapDelete("/{id:guid}", async (Guid id, ZentriqDbContext db, RequestContext rc, CancellationToken ct) =>
        {
            var d = await db.Dashboards.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (d is null) return Results.NotFound();
            if (d.OwnerId != rc.UserId && !rc.Has(Permissions.SettingsManage)) return Results.Problem(statusCode: 403, title: "Only the owner or an administrator can delete this dashboard.");
            db.Dashboards.Remove(d);
            await db.SaveChangesAsync(ct);
            await rc.AuditAsync("dashboard.delete", d.Name, detail: new { d.Id });
            return Results.NoContent();
        });
    }

    private static IResult? Validate(DashboardBody b, RequestContext rc)
    {
        if (string.IsNullOrWhiteSpace(b.Name) || b.Name.Length > 120) return Validation.Problem("name", "Name is required (max 120 characters).");
        if (b.Shared && !rc.Has(Permissions.DashboardsShare)) return Validation.Problem("shared", "You do not have permission to share dashboards.");
        if (b.Widgets is null || b.Widgets.Length > 40) return Validation.Problem("widgets", "A dashboard can have up to 40 widgets.");
        foreach (var w in b.Widgets)
        {
            if (!WidgetTypes.Contains(w.Type)) return Validation.Problem("widgets", $"Unknown widget type '{w.Type}'.");
            if (w.Width is < 1 or > 12 || w.Height is < 1 or > 6) return Validation.Problem("widgets", "Widget size is out of range.");
        }
        if (b.RefreshSeconds is not (0 or 60 or 300 or 900)) return Validation.Problem("refreshSeconds", "Unsupported refresh interval.");
        return null;
    }

    private static void Apply(Dashboard d, DashboardBody b)
    {
        d.Name = b.Name.Trim(); d.Shared = b.Shared; d.RefreshSeconds = b.RefreshSeconds; d.UpdatedAt = DateTimeOffset.UtcNow;
        d.Filters = b.Filters is { } f ? f.GetRawText() : "{}";
        d.Widgets = b.Widgets.Select((w, i) => new DashboardWidget
        {
            Id = Guid.NewGuid(), DashboardId = d.Id, WidgetType = w.Type, Title = w.Title, Position = i, Width = w.Width, Height = w.Height,
            Settings = w.Settings is { } s ? s.GetRawText() : "{}",
        }).ToList();
    }

    private static object Dto(Dashboard d, Guid? uid) => new
    {
        d.Id, d.Name, d.Shared, d.RefreshSeconds, owned = d.OwnerId == uid, filters = JsonDocument.Parse(d.Filters).RootElement, d.UpdatedAt,
        widgets = d.Widgets.OrderBy(w => w.Position).Select(w => new { w.Id, type = w.WidgetType, w.Title, width = w.Width, height = w.Height, settings = JsonDocument.Parse(w.Settings).RootElement }),
    };

    private static async Task CreateDefaultAsync(ZentriqDbContext db, Guid? owner, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var d = new Dashboard { Id = Guid.NewGuid(), Name = "Operations overview", OwnerId = owner, Shared = false, CreatedAt = now, UpdatedAt = now };
        (string type, short w, short h)[] widgets =
        [
            ("kpi.assets", 12, 1), ("assets.byCategory", 4, 2), ("assets.byOs", 4, 2), ("license.usage", 4, 2),
            ("scans.recent", 8, 2), ("jobs.active", 4, 2), ("assets.byInventory", 6, 2), ("assets.byCriticality", 6, 2),
        ];
        d.Widgets = widgets.Select((w, i) => new DashboardWidget { Id = Guid.NewGuid(), DashboardId = d.Id, WidgetType = w.type, Position = i, Width = w.w, Height = w.h }).ToList();
        db.Dashboards.Add(d);
        await db.SaveChangesAsync(ct);
    }
}
