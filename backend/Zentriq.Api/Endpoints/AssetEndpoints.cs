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
        string InventoryStatus, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt, DateTimeOffset? LastInventoryAt, int RowVersion,
        IReadOnlyList<string>? Ipv4 = null, IReadOnlyList<string>? Ipv6 = null, string? LoggedOnUser = null, string? Chassis = null,
        bool? IsVirtual = null, Guid? AgentId = null, string? AgentHealth = null, string? InventorySource = null,
        DateTimeOffset? AgentLastCheckinAt = null, DateTimeOffset? LastContactAt = null, int? OfflineDays = null, bool? Online = null,
        Guid? DepartmentId = null, string? Department = null, string? DeviceType = null, bool DeviceTypeManual = false,
        string? Manufacturer = null, string? Model = null, IReadOnlyList<string>? IdentifiedBy = null);

    public sealed record AssetBody(string? Hostname, string? Ip, string? SerialNumber, string? Category, string? OsFamily,
        string? OsName, string? Criticality, string? OwnerName, int? RowVersion, string? DeviceType = null, string? MacAddress = null,
        string? Manufacturer = null, string? Model = null, Guid? DepartmentId = null);
    public sealed record DeviceTypeBody(string? DeviceType);
    public sealed record IdsBody(Guid[]? Ids);
    public sealed record MergeDuplicatesBody(Guid[]? KeepIds);
    public sealed record DuplicateSettingsBody(bool Auto);

    private static readonly string[] Criticalities = ["Critical", "High", "Medium", "Low"];
    private static readonly string[] OsFamilies = ["windows", "linux", "macos", "network", "other"];

    public static AssetDto ToDto(Asset a) => new(a.Id, a.Hostname, a.Fqdn, Display(a.PrimaryIp), a.SerialNumber, a.Category,
        Workloads.FromAssetCategory(a.Category), a.OsFamily, a.OsName, a.Status, a.Criticality, a.OwnerName, a.Source,
        a.InventoryStatus, a.FirstSeenAt, a.LastSeenAt, a.LastInventoryAt, a.RowVersion, DepartmentId: a.DepartmentId,
        DeviceType: a.DeviceType, DeviceTypeManual: a.DeviceTypeManual, Manufacturer: a.Manufacturer, Model: a.Model,
        IdentifiedBy: a.IdentifiedBy is { } ib ? System.Text.Json.JsonSerializer.Deserialize<List<string>>(ib) : null);

    public static void MapAssetEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/assets").WithTags("Assets").RequireModule(Modules.Inventory);

        g.MapGet("", async (ZentriqDbContext db, NpgsqlDataSource ds, string? q, string? category, string? deviceType, string? department, string? status, string? agent, string? inventory, string? facet, string? value, int? offlineDays, string? sort, string? dir, int? page, int? pageSize, CancellationToken ct) =>
        {
            var (p, ps) = Paging.Normalize(page, pageSize);
            var query = db.Assets.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                // IP addresses and logged-on users live outside the EF model.
                var like = "%" + term + "%";
                var extraIds = await db.Database.SqlQuery<Guid>($$"""
                    SELECT a.id AS "Value" FROM zentriq.assets a LEFT JOIN zentriq.asset_inventory_documents d ON d.asset_id = a.id
                     WHERE host(a.primary_ip) LIKE {{like}} OR lower(d.document #>> '{users,loggedOn}') LIKE {{like}} OR lower((d.document -> 'network')::text) LIKE {{like}}
                     LIMIT 5000
                    """).ToListAsync(ct);
                query = query.Where(a => (a.Hostname != null && a.Hostname.ToLower().Contains(term)) ||
                                         (a.SerialNumber != null && a.SerialNumber.ToLower().Contains(term)) ||
                                         (a.Manufacturer != null && a.Manufacturer.ToLower().Contains(term)) ||
                                         (a.Model != null && a.Model.ToLower().Contains(term)) ||
                                         (a.OsName != null && a.OsName.ToLower().Contains(term)) ||
                                         (a.OwnerName != null && a.OwnerName.ToLower().Contains(term)) ||
                                         extraIds.Contains(a.Id));
            }
            if (agent is "with" or "without")
            {
                var withAgent = await db.Database.SqlQuery<Guid>($"SELECT DISTINCT asset_id AS \"Value\" FROM zentriq.agent_registrations WHERE status = 'active' AND asset_id IS NOT NULL").ToListAsync(ct);
                query = agent == "with" ? query.Where(a => withAgent.Contains(a.Id)) : query.Where(a => !withAgent.Contains(a.Id));
            }
            if (!string.IsNullOrEmpty(facet) && AssetFacets.Expr.TryGetValue(facet, out var fexpr) && value is not null)
            {
                // Same expression as the dashboard chart that was clicked; "Other" is the remainder of a top-N chart.
                var sql = value == "Other" && facet is "os" or "vendor" or "cpu" or "department"
                    ? $"SELECT a.id AS \"Value\" {AssetFacets.From} WHERE {fexpr} IS NOT NULL AND {fexpr} NOT IN (SELECT k FROM (SELECT {fexpr} AS k, count(*) AS n {AssetFacets.From} WHERE a.status = 'active' AND {fexpr} IS NOT NULL GROUP BY 1 ORDER BY 2 DESC, 1 LIMIT 7) t)"
                    : $"SELECT a.id AS \"Value\" {AssetFacets.From} WHERE {fexpr} = @v";
                var facetIds = await IdsAsync(ds, sql, ct, new NpgsqlParameter("v", value));
                query = query.Where(a => facetIds.Contains(a.Id));
            }
            if (offlineDays is > 0)
            {
                var offIds = await IdsAsync(ds, $"SELECT a.id {AssetFacets.From} WHERE {AssetFacets.OfflineDays} >= @d", ct, new NpgsqlParameter("d", offlineDays.Value));
                query = query.Where(a => offIds.Contains(a.Id));
            }
            if (inventory is "success" or "not_collected" or "failed" or "unavailable") query = query.Where(a => a.InventoryStatus == inventory);
            if (inventory == "missing") query = query.Where(a => a.InventoryStatus != "success");
            if (!string.IsNullOrEmpty(category)) query = query.Where(a => a.Category == category);
            if (!string.IsNullOrEmpty(deviceType)) query = deviceType == "unknown" ? query.Where(a => a.DeviceType == null) : query.Where(a => a.DeviceType == deviceType);
            if (department == "none") query = query.Where(a => a.DepartmentId == null);
            else if (Guid.TryParse(department, out var depId)) query = query.Where(a => a.DepartmentId == depId);
            query = string.IsNullOrEmpty(status) ? query.Where(a => a.Status == "active") : status == "all" ? query : query.Where(a => a.Status == status);
            var desc = dir == "desc";
            query = sort switch
            {
                "lastSeenAt" => desc ? query.OrderByDescending(a => a.LastSeenAt) : query.OrderBy(a => a.LastSeenAt),
                "category" => desc ? query.OrderByDescending(a => a.Category) : query.OrderBy(a => a.Category),
                "offline" => desc ? query.OrderBy(a => a.LastSeenAt) : query.OrderByDescending(a => a.LastSeenAt),
                "criticality" => desc ? query.OrderByDescending(a => a.Criticality) : query.OrderBy(a => a.Criticality),
                "department" => desc ? query.OrderByDescending(a => a.DepartmentId) : query.OrderBy(a => a.DepartmentId),
                _ => desc ? query.OrderByDescending(a => a.Hostname) : query.OrderBy(a => a.Hostname),
            };
            var total = await query.CountAsync(ct);
            var items = await query.Skip((p - 1) * ps).Take(ps).ToListAsync(ct);
            return Results.Ok(new PagedResult<AssetDto>(await EnrichAsync(ds, items.Select(ToDto).ToList(), ct), total, p, ps));
        }).RequireAuthorization(Permissions.AssetsView);

        g.MapGet("/{id:guid}", async (Guid id, ZentriqDbContext db, NpgsqlDataSource ds, CancellationToken ct) =>
            await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct) is { } a ? Results.Ok((await EnrichAsync(ds, [ToDto(a)], ct))[0]) : Results.NotFound())
            .RequireAuthorization(Permissions.AssetsView);

        g.MapPost("", async (AssetBody body, ZentriqDbContext db, LicensingService licensing, RequestContext rc, CancellationToken ct) =>
        {
            var err = Validate(body, out var ip);
            if (err is not null) return err;
            var now = DateTimeOffset.UtcNow;
            var asset = new Asset
            {
                Id = Guid.NewGuid(), Hostname = body.Hostname?.Trim(), PrimaryIp = ip, SerialNumber = body.SerialNumber?.Trim(),
                Category = CategoryOf(body), OsFamily = body.OsFamily, OsName = body.OsName?.Trim(), Criticality = body.Criticality ?? "Medium",
                OwnerName = body.OwnerName?.Trim(), Source = "manual", FirstSeenAt = now, LastSeenAt = now, CreatedAt = now, UpdatedAt = now,
                DeviceType = body.DeviceType, DeviceTypeManual = body.DeviceType is not null, Manufacturer = Clean(body.Manufacturer), Model = Clean(body.Model),
                DepartmentId = body.DepartmentId,
            };

            // Licensing: count + insert under one transaction and advisory lock (concurrent enrolment safe).
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var status = await rc.LicenseAsync(ct);
            await licensing.EnsureCapacityAsync((NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction)tx.GetDbTransaction(),
                Workloads.FromAssetCategory(asset.Category), 1, status, ct);
            db.Assets.Add(asset);
            await db.SaveChangesAsync(ct);
            if (Mac(body.MacAddress) is { } mac)
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE zentriq.assets SET mac_address = {mac}::macaddr WHERE id = {asset.Id}", ct);
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
            var newCategory = CategoryOf(body, a.Category);
            var newWorkload = Workloads.FromAssetCategory(newCategory);
            if (a.Status == "active" && oldWorkload != newWorkload)
                await licensing.EnsureCapacityAsync((NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction)tx.GetDbTransaction(), newWorkload, 1, await rc.LicenseAsync(ct), ct);
            a.Hostname = body.Hostname?.Trim(); a.PrimaryIp = ip; a.SerialNumber = body.SerialNumber?.Trim(); a.Category = newCategory;
            if (body.DeviceType is not null) { a.DeviceType = body.DeviceType; a.DeviceTypeManual = true; }
            if (body.Manufacturer is not null) a.Manufacturer = Clean(body.Manufacturer);
            if (body.Model is not null) a.Model = Clean(body.Model);
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

        g.MapPost("/{id:guid}/merge-into/{survivorId:guid}", async (Guid id, Guid survivorId, ZentriqDbContext db, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (id == survivorId) return Results.Problem(statusCode: 400, title: "An asset cannot be merged into itself.");
            var dup = await db.Assets.FirstOrDefaultAsync(x => x.Id == id, ct);
            var survivor = await db.Assets.FirstOrDefaultAsync(x => x.Id == survivorId, ct);
            if (dup is null || survivor is null) return Results.NotFound();
            if (survivor.Status != "active") return Results.Problem(statusCode: 400, title: "The surviving asset must be active.");
            await using (var conn = await ds.OpenConnectionAsync(ct))
                await Zentriq.Infrastructure.Inventory.DuplicateAssets.MergeAsync(conn, survivor.Id, [dup.Id], ct);
            await db.Entry(survivor).ReloadAsync(ct);
            await rc.AuditAsync("asset.merge", dup.Hostname, detail: new { duplicate = dup.Id, survivor = survivor.Id });
            return Results.Ok(ToDto(survivor));
        }).RequireAuthorization(Permissions.AssetsManage);

        // Device type chosen by an administrator (never overwritten by discovery), or null to go back to automatic detection.
        g.MapPut("/{id:guid}/device-type", async (Guid id, DeviceTypeBody body, ZentriqDbContext db, NpgsqlDataSource ds, LicensingService licensing, RequestContext rc, CancellationToken ct) =>
        {
            if (body.DeviceType is not null && !Zentriq.Inventory.DeviceTypes.IsKnown(body.DeviceType)) return Validation.Problem("deviceType", "Unknown device type.");
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var a = await db.Assets.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (a is null) return Results.NotFound();
            if (body.DeviceType is null)
            {
                a.DeviceTypeManual = false;
                // Back to automatic: the type from the latest inventory, if any (the next scan refines it otherwise).
                await using var cmd = ds.CreateCommand("SELECT document::text FROM zentriq.asset_inventory_documents WHERE asset_id = @id");
                cmd.Parameters.AddWithValue("id", id);
                if (await cmd.ExecuteScalarAsync(ct) is string json && System.Text.Json.JsonSerializer.Deserialize<Zentriq.Inventory.InventoryDocument>(json, Zentriq.Infrastructure.Inventory.InventoryStore.Json) is { } doc)
                    a.DeviceType = Zentriq.Infrastructure.Inventory.InventoryStore.DeviceTypeOf(doc) ?? a.DeviceType;
            }
            else
            {
                var category = Zentriq.Inventory.DeviceTypes.CategoryFor(body.DeviceType) ?? a.Category;
                if (a.Status == "active" && Workloads.FromAssetCategory(category) != Workloads.FromAssetCategory(a.Category))
                    await licensing.EnsureCapacityAsync((NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction)tx.GetDbTransaction(), Workloads.FromAssetCategory(category), 1, await rc.LicenseAsync(ct), ct);
                a.DeviceType = body.DeviceType; a.DeviceTypeManual = true; a.Category = category;
                if (category == "network_device") a.OsFamily ??= "network";
            }
            a.UpdatedAt = DateTimeOffset.UtcNow; a.RowVersion++;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            await rc.AuditAsync("asset.device_type", a.Hostname ?? Display(a.PrimaryIp), detail: new { a.Id, body.DeviceType });
            return Results.Ok((await EnrichAsync(ds, [ToDto(a)], ct))[0]);
        }).RequireAuthorization(Permissions.AssetsManage);

        g.MapGet("/device-types", () => Results.Ok(Zentriq.Inventory.DeviceTypes.All.Select(t => new { key = t.Key, label = t.Label, category = t.Category, group = t.Group })))
            .RequireAuthorization(Permissions.AssetsView);

        // Permanent deletion (the device is added again if a scan or an agent reports it later).
        g.MapDelete("/{id:guid}", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
            await DeleteAsync([id], ds, rc, ct)).RequireAuthorization(Permissions.AssetsManage);
        g.MapPost("/delete", async (IdsBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
            body.Ids is not { Length: > 0 } ids ? Validation.Problem("ids", "Select devices.") : await DeleteAsync(ids.Take(5000).ToArray(), ds, rc, ct))
            .RequireAuthorization(Permissions.AssetsManage);

        // Duplicate devices: groups, merge (newest information kept), automatic clean-up setting.
        g.MapGet("/duplicates", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var groups = await Zentriq.Infrastructure.Inventory.DuplicateAssets.FindAsync(conn, ct);
            return Results.Ok(new { auto = await DuplicatesAutoAsync(ds, ct), devices = groups.Sum(x => x.Merge.Count), groups });
        }).RequireAuthorization(Permissions.AssetsView);

        g.MapPost("/duplicates/merge", async (MergeDuplicatesBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var groups = await Zentriq.Infrastructure.Inventory.DuplicateAssets.FindAsync(conn, ct);
            if (body.KeepIds is { Length: > 0 } keep) groups = groups.Where(x => keep.Contains(x.Keep.Id)).ToList();
            var merged = 0;
            foreach (var grp in groups)
            {
                merged += await Zentriq.Infrastructure.Inventory.DuplicateAssets.MergeAsync(conn, grp.Keep.Id, grp.Merge.Select(m => m.Id).ToList(), ct);
                await rc.AuditAsync("asset.merge", grp.Keep.Hostname ?? grp.Keep.Ip, detail: new { kept = grp.Keep.Id, merged = grp.Merge.Select(m => m.Id), reasons = grp.Reasons });
            }
            return Results.Ok(new { groups = groups.Count, merged, message = merged == 0 ? "No duplicates to merge." : $"Merged {merged} duplicate record(s) into {groups.Count} device(s)." });
        }).RequireAuthorization(Permissions.AssetsManage);

        g.MapPut("/duplicates/settings", async (DuplicateSettingsBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                INSERT INTO zentriq.system_settings (key, value) VALUES ('inventory.duplicates', @v::jsonb)
                ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value
                """);
            cmd.Parameters.AddWithValue("v", System.Text.Json.JsonSerializer.Serialize(new { auto = body.Auto }));
            await cmd.ExecuteNonQueryAsync(ct);
            await rc.AuditAsync("settings.duplicates", body.Auto ? "automatic" : "manual");
            return Results.Ok(new { auto = body.Auto });
        }).RequireAuthorization(Permissions.SettingsManage);

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

    private static async Task<List<Guid>> IdsAsync(NpgsqlDataSource ds, string sql, CancellationToken ct, params NpgsqlParameter[] ps)
    {
        await using var cmd = ds.CreateCommand(sql);
        cmd.Parameters.AddRange(ps);
        var ids = new List<Guid>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) ids.Add(r.GetGuid(0));
        return ids;
    }

    private static DateTimeOffset Max(DateTimeOffset? a, DateTimeOffset b) => a is { } x && x > b ? x : b;

    private static string? Display(IPAddress? ip) => ip is null ? null : ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString() : ip.ToString();

    /// <summary>Addresses, logged-on user, device type and agent state from the latest inventory, for a page of assets.</summary>
    public static async Task<List<AssetDto>> EnrichAsync(NpgsqlDataSource ds, List<AssetDto> items, CancellationToken ct)
    {
        if (items.Count == 0) return items;
        await using var setting = ds.CreateCommand("SELECT value::text FROM zentriq.system_settings WHERE key = 'display.show_ipv6'");
        var showV6 = ((await setting.ExecuteScalarAsync(ct)) as string)?.Trim() == "true";
        await using var cmd = ds.CreateCommand("""
            SELECT x.id, d.document #>> '{users,loggedOn}', d.document -> 'network', COALESCE(d.document #>> '{device,chassis}', h.chassis),
                   (d.document #>> '{hardware,isVirtual}')::boolean, d.source, ag.id, ag.last_checkin_at,
                   (SELECT dp.name FROM zentriq.assets aa JOIN zentriq.departments dp ON dp.id = aa.department_id WHERE aa.id = x.id)
              FROM unnest(@ids) AS x(id)
              LEFT JOIN zentriq.asset_inventory_documents d ON d.asset_id = x.id
              LEFT JOIN zentriq.hardware_inventory h ON h.asset_id = x.id
              LEFT JOIN LATERAL (SELECT r.id, r.last_checkin_at FROM zentriq.agent_registrations r
                                  WHERE r.asset_id = x.id AND r.status = 'active' ORDER BY r.last_checkin_at DESC NULLS LAST LIMIT 1) ag ON true
            """);
        cmd.Parameters.AddWithValue("ids", items.Select(i => i.Id).ToArray());
        var extra = new Dictionary<Guid, AssetDto>();
        var byId = items.ToDictionary(i => i.Id);
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var a = byId[r.GetGuid(0)];
                var v4 = new List<string>(); var v6 = new List<string>();
                if (!r.IsDBNull(2))
                    using (var net = System.Text.Json.JsonDocument.Parse(r.GetString(2)))
                        if (net.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                            foreach (var n in net.RootElement.EnumerateArray())
                            {
                                if (n.TryGetProperty("ipv4", out var a4) && a4.ValueKind == System.Text.Json.JsonValueKind.Array)
                                    foreach (var x in a4.EnumerateArray()) if (x.GetString() is { } s4 && !s4.StartsWith("169.254.") && !s4.StartsWith("127.")) v4.Add(s4);
                                if (n.TryGetProperty("ipv6", out var a6) && a6.ValueKind == System.Text.Json.JsonValueKind.Array)
                                    foreach (var x in a6.EnumerateArray()) if (x.GetString() is { } s6) v6.Add(s6);
                            }
                if (a.Ip is { } primary && primary.Contains('.') && !v4.Contains(primary)) v4.Insert(0, primary);
                else if (a.Ip is { } p4 && v4.Remove(p4)) v4.Insert(0, p4);
                DateTimeOffset? lastCheckin = r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7);
                var health = r.IsDBNull(6) ? null : lastCheckin is null ? "never" : lastCheckin > DateTimeOffset.UtcNow.AddMinutes(-45) ? "online" : lastCheckin > DateTimeOffset.UtcNow.AddDays(-7) ? "stale" : "offline";
                extra[a.Id] = a with
                {
                    Ip = a.Ip is { } ip && ip.Contains(':') && v4.Count > 0 ? v4[0] : a.Ip,
                    Ipv4 = v4.Distinct().ToList(), Ipv6 = showV6 ? v6.Distinct().ToList() : null,
                    LoggedOnUser = r.IsDBNull(1) ? null : r.GetString(1), Chassis = a.DeviceType ?? (r.IsDBNull(3) ? null : r.GetString(3)),
                    IsVirtual = r.IsDBNull(4) ? null : r.GetBoolean(4), InventorySource = r.IsDBNull(5) ? null : r.GetString(5),
                    AgentId = r.IsDBNull(6) ? null : r.GetGuid(6), AgentHealth = health, AgentLastCheckinAt = lastCheckin,
                    LastContactAt = Max(lastCheckin, a.LastSeenAt),
                    OfflineDays = (int)Math.Floor((DateTimeOffset.UtcNow - Max(lastCheckin, a.LastSeenAt)).TotalDays),
                    Online = lastCheckin is { } lc && lc > DateTimeOffset.UtcNow.AddMinutes(-30) ? true : r.IsDBNull(6) ? null : false,
                    Department = r.IsDBNull(8) ? null : r.GetString(8),
                };
            }
        return items.Select(i => extra.TryGetValue(i.Id, out var e) ? e : i).ToList();
    }

    private static string CategoryOf(AssetBody b, string? current = null) =>
        Zentriq.Inventory.DeviceTypes.CategoryFor(b.DeviceType) ?? b.Category ?? current ?? "other";

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(200, s.Trim().Length)];

    private static string? Mac(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var hex = new string(s.Where(Uri.IsHexDigit).ToArray());
        return hex.Length == 12 ? string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2))).ToLowerInvariant() : null;
    }

    private static async Task<bool> DuplicatesAutoAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT value::text FROM zentriq.system_settings WHERE key = 'inventory.duplicates'");
        return await cmd.ExecuteScalarAsync(ct) is not string json
            || !System.Text.Json.JsonDocument.Parse(json).RootElement.TryGetProperty("auto", out var a) || a.ValueKind != System.Text.Json.JsonValueKind.False;
    }

    private static async Task<IResult> DeleteAsync(Guid[] ids, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct)
    {
        var names = new List<string>();
        await using (var q = ds.CreateCommand("SELECT COALESCE(hostname, host(primary_ip), id::text) FROM zentriq.assets WHERE id = ANY(@ids)"))
        {
            q.Parameters.AddWithValue("ids", ids);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) names.Add(r.GetString(0));
        }
        if (names.Count == 0) return Results.NotFound();
        await using var conn = await ds.OpenConnectionAsync(ct);
        var n = await Zentriq.Infrastructure.Inventory.DuplicateAssets.DeleteAsync(conn, ids, ct);
        await rc.AuditAsync("asset.delete", names.Count == 1 ? names[0] : $"{names.Count} devices", detail: new { ids, names = names.Take(50) });
        return Results.Ok(new { deleted = n, message = $"Deleted {names.Count} device(s)." });
    }

    private static IResult? Validate(AssetBody b, out IPAddress? ip)
    {
        ip = null;
        if (string.IsNullOrWhiteSpace(b.Hostname) && string.IsNullOrWhiteSpace(b.Ip))
            return Validation.Problem("hostname", "Enter a hostname or an IP address.");
        if (b.Hostname is { Length: > 255 }) return Validation.Problem("hostname", "Hostname is too long.");
        if (!string.IsNullOrWhiteSpace(b.Ip) && !IPAddress.TryParse(b.Ip.Trim(), out ip)) return Validation.Problem("ip", "Enter a valid IP address.");
        if (b.DeviceType is not null && !Zentriq.Inventory.DeviceTypes.IsKnown(b.DeviceType)) return Validation.Problem("deviceType", "Unknown device type.");
        if (b.DeviceType is null && (b.Category is null || !AssetCategories.All.Contains(b.Category))) return Validation.Problem("category", "Choose a device type.");
        if (b.Category is not null && !AssetCategories.All.Contains(b.Category)) return Validation.Problem("category", "Unknown category.");
        if (!string.IsNullOrWhiteSpace(b.MacAddress) && Mac(b.MacAddress) is null) return Validation.Problem("macAddress", "Enter a MAC address like 00:1A:2B:3C:4D:5E.");
        if (b.Criticality is not null && !Criticalities.Contains(b.Criticality)) return Validation.Problem("criticality", "Unknown criticality.");
        if (b.OsFamily is not null && !OsFamilies.Contains(b.OsFamily)) return Validation.Problem("osFamily", "Unknown OS family.");
        return null;
    }
}
