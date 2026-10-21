using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;

namespace Zentriq.Api.Endpoints;

/// <summary>Collected inventory: per-asset detail and fleet-wide software / hardware views.</summary>
public static class InventoryEndpoints
{
    public static void MapInventoryEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/assets/{id:guid}/inventory", async (Guid id, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            object? document = null, hardware = null, agent = null;
            string? source = null;
            DateTimeOffset? collectedAt = null;
            await using (var cmd = new NpgsqlCommand("SELECT source, collected_at, document::text FROM zentriq.asset_inventory_documents WHERE asset_id = @id", conn))
            {
                cmd.Parameters.AddWithValue("id", id);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    source = r.GetString(0);
                    collectedAt = r.GetFieldValue<DateTimeOffset>(1);
                    document = JsonDocument.Parse(r.GetString(2)).RootElement;
                }
            }
            await using (var cmd = new NpgsqlCommand("""
                SELECT vendor, model, cpu, cpu_cores, cpu_threads, ram_gb, serial_number, smbios_uuid, chassis, firmware::text, disks::text, source, collected_at
                  FROM zentriq.hardware_inventory WHERE asset_id = @id
                """, conn))
            {
                cmd.Parameters.AddWithValue("id", id);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
                    hardware = new
                    {
                        vendor = S(0), model = S(1), cpu = S(2), cpuCores = r.IsDBNull(3) ? (int?)null : r.GetInt32(3), cpuThreads = r.IsDBNull(4) ? (int?)null : r.GetInt32(4),
                        ramGb = r.IsDBNull(5) ? (decimal?)null : r.GetDecimal(5), serialNumber = S(6), smbiosUuid = S(7), chassis = S(8),
                        firmware = JsonDocument.Parse(r.GetString(9)).RootElement, disks = JsonDocument.Parse(r.GetString(10)).RootElement,
                        source = S(11), collectedAt = r.GetFieldValue<DateTimeOffset>(12),
                    };
                }
            }
            var software = new List<object>();
            await using (var cmd = new NpgsqlCommand("SELECT name, publisher, version, installed_on, architecture, source FROM zentriq.software_inventory WHERE asset_id = @id ORDER BY lower(name), version", conn))
            {
                cmd.Parameters.AddWithValue("id", id);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    software.Add(new
                    {
                        name = r.GetString(0), publisher = r.IsDBNull(1) ? null : r.GetString(1), version = r.IsDBNull(2) ? null : r.GetString(2),
                        installedOn = r.IsDBNull(3) ? (DateOnly?)null : r.GetFieldValue<DateOnly>(3), architecture = r.IsDBNull(4) ? null : r.GetString(4), source = r.GetString(5),
                    });
            }
            await using (var cmd = new NpgsqlCommand("SELECT id, status, agent_version, last_checkin_at, last_inventory_at FROM zentriq.agent_registrations WHERE asset_id = @id ORDER BY enrolled_at DESC LIMIT 1", conn))
            {
                cmd.Parameters.AddWithValue("id", id);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                    agent = new
                    {
                        id = r.GetGuid(0), status = r.GetString(1), version = r.GetString(2),
                        lastCheckinAt = r.IsDBNull(3) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(3),
                        lastInventoryAt = r.IsDBNull(4) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(4),
                    };
            }
            var identifiers = new List<object>();
            await using (var cmd = new NpgsqlCommand("SELECT kind, value FROM zentriq.asset_identifiers WHERE asset_id = @id ORDER BY kind, value", conn))
            {
                cmd.Parameters.AddWithValue("id", id);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) identifiers.Add(new { kind = r.GetString(0), value = r.GetString(1) });
            }
            return Results.Ok(new { source, collectedAt, document, hardware, software, agent, identifiers });
        }).RequireAuthorization(Permissions.AssetsView).WithTags("Inventory");

        var g = api.MapGroup("/inventory").WithTags("Inventory");

        g.MapGet("/software", async (NpgsqlDataSource ds, string? q, string? department, string? location, string? os, int? page, int? pageSize, CancellationToken ct) =>
        {
            var (p, size) = Paging.Normalize(page, pageSize);
            await using var cmd = ds.CreateCommand("""
                WITH s AS (
                  SELECT s.name, s.publisher, s.version, s.asset_id
                    FROM zentriq.software_inventory s JOIN zentriq.assets a ON a.id = s.asset_id AND a.status = 'active'
                   WHERE (@q::text IS NULL OR s.name ILIKE @q OR s.publisher ILIKE @q)
                     AND (@dep::text IS NULL OR (@dep = 'none' AND a.department_id IS NULL) OR a.department_id::text = @dep)
                     AND (@loc::text IS NULL OR (@loc = 'none' AND a.location_id IS NULL) OR a.location_id::text = @loc)
                     AND (@os::text IS NULL OR a.os_family = @os)
                )
                SELECT name, max(publisher), count(DISTINCT asset_id) AS installs, count(DISTINCT version) AS versions,
                       (array_agg(DISTINCT version ORDER BY version DESC) FILTER (WHERE version IS NOT NULL))[1:5],
                       count(*) OVER () AS total
                  FROM s GROUP BY name ORDER BY installs DESC, lower(name)
                 LIMIT @l OFFSET @o
                """);
            cmd.Parameters.Add(new NpgsqlParameter("q", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(q) ? DBNull.Value : "%" + Escape(q.Trim()) + "%" });
            cmd.Parameters.Add(new NpgsqlParameter("dep", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(department) ? DBNull.Value : department.Trim() });
            cmd.Parameters.Add(new NpgsqlParameter("loc", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(location) ? DBNull.Value : location.Trim() });
            cmd.Parameters.Add(new NpgsqlParameter("os", NpgsqlDbType.Text) { Value = os is "windows" or "linux" or "macos" ? os : DBNull.Value });
            cmd.Parameters.AddWithValue("l", size);
            cmd.Parameters.AddWithValue("o", (p - 1) * size);
            var items = new List<object>();
            long total = 0;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                items.Add(new { name = r.GetString(0), publisher = r.IsDBNull(1) ? null : r.GetString(1), installs = r.GetInt64(2), versions = r.GetInt64(3), latestVersions = r.IsDBNull(4) ? Array.Empty<string>() : r.GetFieldValue<string[]>(4) });
                total = r.GetInt64(5);
            }
            return Results.Ok(new { items, total, page = p, pageSize = size });
        }).RequireAuthorization(Permissions.AssetsView);

        g.MapGet("/software/installs", async (NpgsqlDataSource ds, string name, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT a.id, a.hostname, host(a.primary_ip), a.os_name, s.version, s.installed_on, s.source,
                       (SELECT CASE WHEN 'software-uninstall' = ANY(r.capabilities) THEN 'ready' ELSE 'old' END FROM zentriq.agent_registrations r
                         WHERE r.asset_id = a.id AND r.status = 'active' ORDER BY r.last_checkin_at DESC NULLS LAST LIMIT 1),
                       (SELECT x.status || '|' || COALESCE(x.detail, '') FROM zentriq.asset_actions x
                         WHERE x.asset_id = a.id AND x.kind = 'uninstall' AND lower(x.target) = lower(s.name) ORDER BY x.requested_at DESC LIMIT 1),
                       (SELECT dp.name FROM zentriq.departments dp WHERE dp.id = a.department_id), (SELECT lo.name FROM zentriq.locations lo WHERE lo.id = a.location_id),
                       s.publisher
                  FROM zentriq.software_inventory s JOIN zentriq.assets a ON a.id = s.asset_id AND a.status = 'active'
                 WHERE s.name = @n ORDER BY lower(a.hostname) LIMIT 2000
                """);
            cmd.Parameters.AddWithValue("n", name);
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new
                {
                    assetId = r.GetGuid(0), hostname = r.IsDBNull(1) ? null : r.GetString(1), ip = r.IsDBNull(2) ? null : r.GetString(2), osName = r.IsDBNull(3) ? null : r.GetString(3),
                    version = r.IsDBNull(4) ? null : r.GetString(4), installedOn = r.IsDBNull(5) ? (DateOnly?)null : r.GetFieldValue<DateOnly>(5), source = r.GetString(6),
                    agent = r.IsDBNull(7) ? "none" : r.GetString(7),
                    lastUninstall = r.IsDBNull(8) ? null : r.GetString(8).Split('|', 2) is var u ? new { status = u[0], detail = u.Length > 1 ? u[1] : null } : null,
                    department = r.IsDBNull(9) ? null : r.GetString(9), location = r.IsDBNull(10) ? null : r.GetString(10), publisher = r.IsDBNull(11) ? null : r.GetString(11),
                });
            return Results.Ok(list);
        }).RequireAuthorization(Permissions.AssetsView);

        g.MapGet("/hardware", async (NpgsqlDataSource ds, string? q, string? department, string? location, string? type, string? vendor, string? os, int? page, int? pageSize, CancellationToken ct) =>
        {
            var (p, size) = Paging.Normalize(page, pageSize);
            await using var cmd = ds.CreateCommand("""
                SELECT a.id, a.hostname, a.category, a.os_name, hw.vendor, hw.model, hw.serial, h.cpu, h.cpu_cores, h.ram_gb, h.chassis,
                       (SELECT COALESCE(round(sum((d->>'sizeBytes')::numeric) / 1073741824, 1), 0)
                          FROM jsonb_array_elements(CASE WHEN jsonb_typeof(h.disks) = 'object' THEN COALESCE(h.disks->'disks', '[]'::jsonb) ELSE h.disks END) d
                         WHERE d ? 'sizeBytes' AND jsonb_typeof(d->'sizeBytes') = 'number') AS disk_gb,
                       h.source, h.collected_at, count(*) OVER () AS total,
                       a.department_id, dp.name, a.location_id, lo.name, host(a.primary_ip), hw.manual
                  FROM zentriq.hardware_inventory h JOIN zentriq.assets a ON a.id = h.asset_id AND a.status = 'active'
                  CROSS JOIN LATERAL (SELECT COALESCE(zentriq.hw_value(h.vendor), zentriq.hw_value(a.manufacturer)) AS dv, zentriq.hw_value(h.model) AS dm,
                                             COALESCE(zentriq.hw_value(h.serial_number), zentriq.hw_value(a.serial_number)) AS ds) det
                  CROSS JOIN LATERAL (SELECT COALESCE(det.dv, a.manual_manufacturer) AS vendor, COALESCE(det.dm, zentriq.hw_value(a.model), a.manual_model) AS model,
                                             COALESCE(det.ds, a.manual_serial) AS serial,
                                             array_remove(ARRAY[CASE WHEN det.dv IS NULL AND a.manual_manufacturer IS NOT NULL THEN 'manufacturer' END,
                                                                CASE WHEN det.dm IS NULL AND zentriq.hw_value(a.model) IS NULL AND a.manual_model IS NOT NULL THEN 'model' END,
                                                                CASE WHEN det.ds IS NULL AND a.manual_serial IS NOT NULL THEN 'serialNumber' END], NULL) AS manual) hw
                  LEFT JOIN zentriq.departments dp ON dp.id = a.department_id
                  LEFT JOIN zentriq.locations lo ON lo.id = a.location_id
                 WHERE (@q::text IS NULL OR a.hostname ILIKE @q OR hw.vendor ILIKE @q OR hw.model ILIKE @q OR hw.serial ILIKE @q OR h.cpu ILIKE @q)
                   AND (@dep::text IS NULL OR (@dep = 'none' AND a.department_id IS NULL) OR a.department_id::text = @dep)
                   AND (@loc::text IS NULL OR (@loc = 'none' AND a.location_id IS NULL) OR a.location_id::text = @loc)
                   AND (@type::text IS NULL OR lower(h.chassis) = lower(@type))
                   AND (@vendor::text IS NULL OR hw.vendor ILIKE @vendor)
                   AND (@os::text IS NULL OR a.os_name ILIKE @os)
                 ORDER BY lower(a.hostname) NULLS LAST
                 LIMIT @l OFFSET @o
                """);
            cmd.Parameters.Add(new NpgsqlParameter("q", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(q) ? DBNull.Value : "%" + Escape(q.Trim()) + "%" });
            cmd.Parameters.Add(new NpgsqlParameter("dep", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(department) ? DBNull.Value : department.Trim() });
            cmd.Parameters.Add(new NpgsqlParameter("loc", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(location) ? DBNull.Value : location.Trim() });
            cmd.Parameters.Add(new NpgsqlParameter("type", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(type) ? DBNull.Value : type.Trim() });
            cmd.Parameters.Add(new NpgsqlParameter("vendor", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(vendor) ? DBNull.Value : "%" + Escape(vendor.Trim()) + "%" });
            cmd.Parameters.Add(new NpgsqlParameter("os", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(os) ? DBNull.Value : "%" + Escape(os.Trim()) + "%" });
            cmd.Parameters.AddWithValue("l", size);
            cmd.Parameters.AddWithValue("o", (p - 1) * size);
            var items = new List<object>();
            long total = 0;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
                items.Add(new
                {
                    assetId = r.GetGuid(0), hostname = S(1), category = r.GetString(2), osName = S(3), vendor = S(4), model = S(5), serialNumber = S(6), cpu = S(7),
                    cpuCores = r.IsDBNull(8) ? (int?)null : r.GetInt32(8), ramGb = r.IsDBNull(9) ? (decimal?)null : r.GetDecimal(9), chassis = S(10),
                    diskGb = r.GetDecimal(11), source = S(12), collectedAt = r.GetFieldValue<DateTimeOffset>(13),
                    departmentId = r.IsDBNull(15) ? (Guid?)null : r.GetGuid(15), department = S(16),
                    locationId = r.IsDBNull(17) ? (Guid?)null : r.GetGuid(17), location = S(18), ip = S(19),
                    enteredManually = r.IsDBNull(20) ? Array.Empty<string>() : r.GetFieldValue<string[]>(20),
                });
                total = r.GetInt64(14);
            }
            return Results.Ok(new { items, total, page = p, pageSize = size });
        }).RequireAuthorization(Permissions.AssetsView);

        // Values for the Hardware Inventory filters.
        g.MapGet("/hardware/filters", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            async Task<List<string>> Distinct(string sql)
            {
                var list = new List<string>();
                await using var cmd = ds.CreateCommand(sql);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
                return list;
            }
            const string From = "FROM zentriq.hardware_inventory h JOIN zentriq.assets a ON a.id = h.asset_id AND a.status = 'active'";
            return Results.Ok(new
            {
                types = await Distinct($"SELECT DISTINCT lower(h.chassis) {From} WHERE h.chassis IS NOT NULL ORDER BY 1"),
                vendors = await Distinct($"SELECT v {From} CROSS JOIN LATERAL (SELECT COALESCE(zentriq.hw_value(h.vendor), zentriq.hw_value(a.manufacturer), a.manual_manufacturer) AS v) x WHERE v IS NOT NULL GROUP BY v ORDER BY count(*) DESC, 1 LIMIT 50"),
                os = await Distinct($"SELECT a.os_name {From} WHERE a.os_name IS NOT NULL GROUP BY a.os_name ORDER BY count(*) DESC, 1 LIMIT 50"),
            });
        }).RequireAuthorization(Permissions.AssetsView);

        // ---- change history (hardware replaced, software installed / uninstalled / updated) -----------------

        g.MapGet("/changes", async (NpgsqlDataSource ds, string? kind, string? change, Guid? assetId, int? days, string? q, Guid? departmentId, int? page, int? pageSize, CancellationToken ct) =>
        {
            var (p, size) = Paging.Normalize(page, pageSize);
            await using var cmd = ds.CreateCommand("""
                SELECT c.id, c.asset_id, COALESCE(a.hostname, host(a.primary_ip)), a.os_name, dp.name, c.detected_at, c.kind, c.change, c.component, c.item,
                       c.old_value, c.new_value, c.source, count(*) OVER ()
                  FROM zentriq.asset_changes c JOIN zentriq.assets a ON a.id = c.asset_id
                  LEFT JOIN zentriq.departments dp ON dp.id = a.department_id
                 WHERE (@k::text IS NULL OR c.kind = @k) AND (@ch::text IS NULL OR c.change = @ch) AND (@a::uuid IS NULL OR c.asset_id = @a)
                   AND (@dep::uuid IS NULL OR a.department_id = @dep)
                   AND c.detected_at > now() - make_interval(days => @days)
                   AND (@q::text IS NULL OR a.hostname ILIKE @q OR c.item ILIKE @q OR c.component ILIKE @q)
                 ORDER BY c.detected_at DESC, c.id DESC LIMIT @l OFFSET @o
                """);
            cmd.Parameters.Add(new NpgsqlParameter("k", NpgsqlDbType.Text) { Value = kind is "hardware" or "software" ? kind : DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("ch", NpgsqlDbType.Text) { Value = change is "added" or "removed" or "changed" or "installed" or "uninstalled" or "updated" ? change : DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("a", NpgsqlDbType.Uuid) { Value = (object?)assetId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("dep", NpgsqlDbType.Uuid) { Value = (object?)departmentId ?? DBNull.Value });
            cmd.Parameters.AddWithValue("days", Math.Clamp(days ?? 30, 1, 366));
            cmd.Parameters.Add(new NpgsqlParameter("q", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(q) ? DBNull.Value : "%" + Escape(q.Trim()) + "%" });
            cmd.Parameters.AddWithValue("l", size);
            cmd.Parameters.AddWithValue("o", (p - 1) * size);
            var items = new List<object>();
            long total = 0;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
                items.Add(new
                {
                    id = r.GetInt64(0), assetId = r.GetGuid(1), hostname = S(2), os = S(3), department = S(4), detectedAt = r.GetFieldValue<DateTimeOffset>(5),
                    kind = r.GetString(6), change = r.GetString(7), component = r.GetString(8), item = r.GetString(9), oldValue = S(10), newValue = S(11), source = S(12),
                });
                total = r.GetInt64(13);
            }
            return Results.Ok(new { items, total, page = p, pageSize = size });
        }).RequireAuthorization(Permissions.AssetsView);

        // Device-wise summary for the dashboard and the inventory pages.
        g.MapGet("/changes/summary", async (NpgsqlDataSource ds, int? days, CancellationToken ct) =>
        {
            var d = Math.Clamp(days ?? 30, 1, 366);
            await using var conn = await ds.OpenConnectionAsync(ct);
            async Task<List<T>> Rows<T>(string sql, Func<NpgsqlDataReader, T> map)
            {
                var list = new List<T>();
                await using var c = new NpgsqlCommand(sql, conn);
                c.Parameters.AddWithValue("days", d);
                await using var r = await c.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) list.Add(map(r));
                return list;
            }
            const string Window = "FROM zentriq.asset_changes c JOIN zentriq.assets a ON a.id = c.asset_id WHERE a.status = 'active' AND c.detected_at > now() - make_interval(days => @days)";
            var kpi = (await Rows($"""
                SELECT count(*) FILTER (WHERE c.kind = 'hardware'), count(DISTINCT c.asset_id) FILTER (WHERE c.kind = 'hardware'),
                       count(*) FILTER (WHERE c.kind = 'software'), count(DISTINCT c.asset_id) FILTER (WHERE c.kind = 'software'),
                       count(*) FILTER (WHERE c.change = 'installed' AND c.component = 'Application'), count(*) FILTER (WHERE c.change = 'uninstalled'),
                       count(*) FILTER (WHERE c.change = 'updated'), count(*) FILTER (WHERE c.component = 'Windows update')
                {Window}
                """, r => new
            {
                hardware = r.GetInt64(0), hardwareDevices = r.GetInt64(1), software = r.GetInt64(2), softwareDevices = r.GetInt64(3),
                installed = r.GetInt64(4), uninstalled = r.GetInt64(5), updated = r.GetInt64(6), windowsUpdates = r.GetInt64(7),
            }))[0];
            object Device(NpgsqlDataReader r) => new
            {
                assetId = r.GetGuid(0), hostname = r.IsDBNull(1) ? null : r.GetString(1), department = r.IsDBNull(2) ? null : r.GetString(2),
                changes = r.GetInt64(3), last = r.GetFieldValue<DateTimeOffset>(4), summary = r.IsDBNull(5) ? null : r.GetString(5),
            };
            string ByDevice(string kind) => $"""
                SELECT c.asset_id, COALESCE(a.hostname, host(a.primary_ip)), (SELECT dp.name FROM zentriq.departments dp WHERE dp.id = a.department_id),
                       count(*), max(c.detected_at),
                       string_agg(c.change || ' ' || c.component || ': ' || c.item || COALESCE(' (' || c.old_value || ' → ' || c.new_value || ')', ''), '; ' ORDER BY c.detected_at DESC, c.id DESC)
                {Window} AND c.kind = '{kind}' GROUP BY c.asset_id, a.hostname, a.primary_ip, a.department_id ORDER BY max(c.detected_at) DESC LIMIT 15
                """;
            var hardwareDevices = await Rows(ByDevice("hardware"), Device);
            var softwareDevices = await Rows(ByDevice("software"), Device);
            var byComponent = await Rows($"SELECT c.kind, c.component, c.change, count(*) {Window} GROUP BY 1, 2, 3 ORDER BY 4 DESC LIMIT 30",
                r => new { kind = r.GetString(0), component = r.GetString(1), change = r.GetString(2), count = r.GetInt64(3) });
            var trend = await Rows("""
                SELECT g::date, count(c.id) FILTER (WHERE c.kind = 'hardware'), count(c.id) FILTER (WHERE c.kind = 'software')
                  FROM generate_series(current_date - least(@days, 90) + 1, current_date, interval '1 day') g
                  LEFT JOIN zentriq.asset_changes c ON c.detected_at::date = g::date
                 GROUP BY 1 ORDER BY 1
                """, r => new { day = DateOnly.FromDateTime(r.GetDateTime(0)), hardware = r.GetInt64(1), software = r.GetInt64(2) });
            return Results.Ok(new { days = d, kpi, hardwareDevices, softwareDevices, byComponent, trend });
        }).RequireAuthorization(Permissions.AssetsView);
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
