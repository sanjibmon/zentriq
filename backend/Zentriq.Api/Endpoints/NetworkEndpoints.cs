using Npgsql;
using NpgsqlTypes;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;
using Zentriq.Infrastructure.Inventory;

namespace Zentriq.Api.Endpoints;

/// <summary>
/// Network inventory: switches (managed and unmanaged), routers, firewalls, access points and controllers, with their
/// ports, what is connected to each port, LLDP/CDP neighbours (topology), VLANs, PoE, modules and firmware.
/// The queries return JSON built by PostgreSQL.
/// </summary>
public static class NetworkEndpoints
{
    private const string ManagementExpr = NetworkStore.ManagementExpr;

    private const string DeviceSelect = $"""
        SELECT a.id AS "id", a.hostname AS "hostname", host(a.primary_ip) AS "ip", COALESCE(a.device_type, 'network') AS "deviceType",
               a.manufacturer AS "manufacturer", a.model AS "model", a.serial_number AS "serialNumber", a.os_name AS "osName",
               d.document->'os'->>'version' AS "firmware", d.document->'device'->>'location' AS "location",
               (d.document->'os'->>'lastBoot')::timestamptz AS "lastBoot",
               {ManagementExpr} AS "management", nd.snmp_version AS "snmpVersion",
               COALESCE(nd.ports_total, (d.document->'ports'->>'physical')::int, 0) AS "portsTotal",
               COALESCE(nd.ports_up, (d.document->'ports'->>'up')::int, 0) AS "portsUp",
               COALESCE(nd.ports_admin_down, 0) AS "portsAdminDown",
               COALESCE(nd.vlans, 0) AS "vlans", COALESCE(nd.neighbors, 0) AS "neighbors", COALESCE(nd.mac_entries, 0) AS "macEntries",
               nd.ip_forwarding AS "routing", nd.routes AS "routes", nd.cpu_percent AS "cpuPercent",
               nd.poe_budget_w AS "poeBudgetWatts", nd.poe_used_w AS "poeUsedWatts", nd.stp_root AS "stpRoot", nd.stack_members AS "stackMembers",
               (SELECT count(*) FROM zentriq.network_interfaces i WHERE i.asset_id = a.id AND i.unmanaged_suspect)::int AS "unmanagedPorts",
               (SELECT count(*) FROM zentriq.network_interfaces i WHERE i.asset_id = a.id AND i.type = 'ethernet' AND i.oper = 'up' AND NOT i.uplink AND i.mac_count > 0)::int AS "edgePortsInUse",
               nd.collected_at AS "collectedAt", a.last_seen_at AS "lastSeenAt", dep.name AS "department"
          FROM zentriq.assets a
          LEFT JOIN zentriq.network_devices nd ON nd.asset_id = a.id
          LEFT JOIN zentriq.asset_inventory_documents d ON d.asset_id = a.id
          LEFT JOIN zentriq.departments dep ON dep.id = a.department_id
         WHERE a.status = 'active' AND (a.category = 'network_device' OR nd.asset_id IS NOT NULL)
        """;

    public static void MapNetworkEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/network").WithTags("Network inventory").RequireModule(Modules.Inventory);

        g.MapGet("/summary", async (NpgsqlDataSource ds, CancellationToken ct) => await JsonAsync(ds, $$"""
            WITH dev AS ({{DeviceSelect}}),
                 ports AS (SELECT i.* FROM zentriq.network_interfaces i JOIN zentriq.assets a ON a.id = i.asset_id AND a.status = 'active' WHERE i.type = 'ethernet')
            SELECT jsonb_build_object(
                'devices', (SELECT count(*) FROM dev),
                'byType', (SELECT COALESCE(jsonb_object_agg(t, n), '{}') FROM (SELECT "deviceType" t, count(*) n FROM dev GROUP BY 1) x),
                'byManagement', (SELECT COALESCE(jsonb_object_agg(t, n), '{}') FROM (SELECT management t, count(*) n FROM dev GROUP BY 1) x),
                'byManufacturer', (SELECT COALESCE(jsonb_agg(jsonb_build_object('name', t, 'count', n) ORDER BY n DESC), '[]') FROM (SELECT COALESCE(manufacturer, 'Unknown') t, count(*) n FROM dev GROUP BY 1 ORDER BY 2 DESC LIMIT 10) x),
                'switchesManaged', (SELECT count(*) FROM dev WHERE "deviceType" = 'switch' AND management = 'snmp'),
                'switchesWebOnly', (SELECT count(*) FROM dev WHERE "deviceType" = 'switch' AND management = 'web'),
                'switchesNoManagement', (SELECT count(*) FROM dev WHERE "deviceType" = 'switch' AND management = 'none'),
                'unmanagedSuspected', (SELECT count(*) FROM ports WHERE unmanaged_suspect),
                'portsTotal', (SELECT count(*) FROM ports),
                'portsUp', (SELECT count(*) FROM ports WHERE oper = 'up'),
                'portsAdminDown', (SELECT count(*) FROM ports WHERE admin = 'down'),
                'portsWithErrors', (SELECT count(*) FROM ports WHERE COALESCE(in_errors, 0) + COALESCE(out_errors, 0) > 0),
                'portsHalfDuplex', (SELECT count(*) FROM ports WHERE oper = 'up' AND duplex = 'half'),
                'portsPoe', (SELECT count(*) FROM ports WHERE poe),
                'poeBudgetWatts', (SELECT COALESCE(sum("poeBudgetWatts"), 0) FROM dev),
                'poeUsedWatts', (SELECT COALESCE(sum("poeUsedWatts"), 0) FROM dev),
                'vlans', (SELECT count(DISTINCT v.vlan_id) FROM zentriq.network_vlans v JOIN zentriq.assets a ON a.id = v.asset_id AND a.status = 'active'),
                'links', (SELECT count(*) FROM zentriq.network_neighbors nb JOIN zentriq.assets a ON a.id = nb.asset_id AND a.status = 'active'),
                'firmwareVersions', (SELECT count(DISTINCT (COALESCE(manufacturer, '') || '|' || COALESCE(model, '') || '|' || firmware)) FROM dev WHERE firmware IS NOT NULL),
                'staleDevices', (SELECT count(*) FROM dev WHERE "lastSeenAt" < now() - interval '7 days'),
                'highCpu', (SELECT count(*) FROM dev WHERE "cpuPercent" >= 80),
                'speeds', (SELECT COALESCE(jsonb_object_agg(s, n), '{}') FROM (SELECT CASE WHEN speed_mbps >= 10000 THEN '10G+' WHEN speed_mbps >= 1000 THEN '1G'
                              WHEN speed_mbps >= 100 THEN '100M' WHEN speed_mbps > 0 THEN '10M' ELSE 'unknown' END s, count(*) n FROM ports WHERE oper = 'up' GROUP BY 1) x)
            )::text
            """, null, ct)).RequireAuthorization(Permissions.AssetsView);

        g.MapGet("/devices", async (NpgsqlDataSource ds, string? type, string? management, string? q, CancellationToken ct) => await JsonAsync(ds, $"""
            SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."deviceType", lower(t.hostname)), '[]')::text FROM ({DeviceSelect}) t
             WHERE (@type::text IS NULL OR t."deviceType" = @type) AND (@mgmt::text IS NULL OR t.management = @mgmt)
               AND (@q::text IS NULL OR t.hostname ILIKE '%' || @q || '%' OR t.ip LIKE @q || '%' OR t.model ILIKE '%' || @q || '%' OR t.manufacturer ILIKE '%' || @q || '%')
            """, c => { c.Parameters.Add(Text("type", type)); c.Parameters.Add(Text("mgmt", management)); c.Parameters.Add(Text("q", Clean(q))); }, ct))
            .RequireAuthorization(Permissions.AssetsView);

        // Switch port map. Search finds the port a device is connected to by MAC address, IP address or name.
        g.MapGet("/ports", async (NpgsqlDataSource ds, Guid? assetId, string? q, string? filter, CancellationToken ct) => await JsonAsync(ds, """
            SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."deviceName", t."ifIndex"), '[]')::text FROM (
              SELECT i.asset_id AS "assetId", COALESCE(a.hostname, host(a.primary_ip)) AS "deviceName", i.if_index AS "ifIndex", i.name AS "name",
                     i.description AS "description", i.alias AS "alias", i.type AS "type", i.admin AS "admin", i.oper AS "oper", i.speed_mbps AS "speedMbps",
                     i.duplex AS "duplex", i.vlan AS "vlan", i.trunk AS "trunk", i.poe AS "poe", i.in_errors AS "inErrors", i.out_errors AS "outErrors",
                     i.mac AS "mac", i.ipv4 AS "ipv4", i.mac_count AS "macCount", i.connected AS "connected", i.neighbor AS "neighbor",
                     i.neighbor_asset_id AS "neighborAssetId", i.uplink AS "uplink", i.unmanaged_suspect AS "unmanagedSuspect",
                     CASE WHEN @q::text IS NULL OR i.uplink THEN NULL ELSE (
                        SELECT jsonb_agg(jsonb_build_object('mac', m.mac::text, 'ip', (SELECT host(r.ip) FROM zentriq.network_arp r WHERE r.mac = m.mac LIMIT 1), 'vlan', m.vlan))
                          FROM zentriq.network_mac_table m WHERE m.asset_id = i.asset_id AND m.if_index = i.if_index
                           AND (m.mac::text ILIKE '%' || @qmac || '%' OR EXISTS (SELECT 1 FROM zentriq.network_arp r WHERE r.mac = m.mac AND host(r.ip) = @q))) END AS "matches"
                FROM zentriq.network_interfaces i
                JOIN zentriq.assets a ON a.id = i.asset_id AND a.status = 'active'
               WHERE (@asset::uuid IS NULL OR i.asset_id = @asset)
                 AND (@asset::uuid IS NOT NULL OR i.type IN ('ethernet', 'lag', 'wireless'))
                 AND (@filter::text IS NULL
                      OR (@filter = 'up' AND i.oper = 'up') OR (@filter = 'down' AND i.oper <> 'up' AND i.admin = 'up')
                      OR (@filter = 'disabled' AND i.admin = 'down') OR (@filter = 'uplink' AND i.uplink)
                      OR (@filter = 'unmanaged' AND i.unmanaged_suspect) OR (@filter = 'poe' AND i.poe)
                      OR (@filter = 'errors' AND COALESCE(i.in_errors, 0) + COALESCE(i.out_errors, 0) > 0)
                      OR (@filter = 'halfduplex' AND i.oper = 'up' AND i.duplex = 'half')
                      OR (@filter = 'unused' AND i.type = 'ethernet' AND i.oper <> 'up' AND i.mac_count = 0))
                 AND (@q::text IS NULL
                      OR i.name ILIKE '%' || @q || '%' OR i.alias ILIKE '%' || @q || '%' OR a.hostname ILIKE '%' || @q || '%' OR i.neighbor ILIKE '%' || @q || '%'
                      OR i.connected::text ILIKE '%' || @q || '%'
                      OR (NOT i.uplink AND EXISTS (SELECT 1 FROM zentriq.network_mac_table m WHERE m.asset_id = i.asset_id AND m.if_index = i.if_index
                              AND (m.mac::text ILIKE '%' || @qmac || '%' OR EXISTS (SELECT 1 FROM zentriq.network_arp r WHERE r.mac = m.mac AND host(r.ip) = @q)))))
               LIMIT 3000) t
            """, c =>
            {
                var clean = Clean(q);
                c.Parameters.Add(new NpgsqlParameter("asset", NpgsqlDbType.Uuid) { Value = (object?)assetId ?? DBNull.Value });
                c.Parameters.Add(Text("q", clean));
                c.Parameters.Add(Text("qmac", MacQuery(clean)));
                c.Parameters.Add(Text("filter", filter));
            }, ct)).RequireAuthorization(Permissions.AssetsView);

        g.MapGet("/neighbors", async (NpgsqlDataSource ds, CancellationToken ct) => await JsonAsync(ds, """
            SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."deviceName", t."localPort"), '[]')::text FROM (
              SELECT nb.asset_id AS "assetId", COALESCE(a.hostname, host(a.primary_ip)) AS "deviceName", COALESCE(a.device_type, 'network') AS "deviceType", nb.protocol AS "protocol",
                     nb.local_port AS "localPort", nb.remote_name AS "remoteName", nb.remote_port AS "remotePort", nb.remote_platform AS "remotePlatform",
                     nb.remote_description AS "remoteDescription", host(nb.remote_address) AS "remoteAddress", nb.remote_chassis_id AS "remoteChassisId",
                     nb.capabilities AS "capabilities", nb.remote_asset_id AS "remoteAssetId", ra.hostname AS "remoteAssetName",
                     COALESCE(ra.device_type, ra.category) AS "remoteDeviceType", EXISTS (SELECT 1 FROM zentriq.network_devices x WHERE x.asset_id = nb.remote_asset_id) AS "remoteManaged"
                FROM zentriq.network_neighbors nb
                JOIN zentriq.assets a ON a.id = nb.asset_id AND a.status = 'active'
                LEFT JOIN zentriq.assets ra ON ra.id = nb.remote_asset_id
               LIMIT 5000) t
            """, null, ct)).RequireAuthorization(Permissions.AssetsView);

        g.MapGet("/vlans", async (NpgsqlDataSource ds, CancellationToken ct) => await JsonAsync(ds, """
            SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."id"), '[]')::text FROM (
              SELECT v.vlan_id AS "id", (array_agg(DISTINCT v.name) FILTER (WHERE v.name IS NOT NULL AND v.name <> ''))[1:3] AS "names",
                     count(DISTINCT v.asset_id)::int AS "devices",
                     (SELECT count(*) FROM zentriq.network_interfaces i JOIN zentriq.assets x ON x.id = i.asset_id AND x.status = 'active'
                       WHERE i.vlan = v.vlan_id AND i.type = 'ethernet' AND NOT COALESCE(i.trunk, false))::int AS "accessPorts",
                     (SELECT count(*) FROM zentriq.network_mac_table m JOIN zentriq.assets x ON x.id = m.asset_id AND x.status = 'active' WHERE m.vlan = v.vlan_id)::int AS "macs",
                     (array_agg(DISTINCT COALESCE(a.hostname, host(a.primary_ip))))[1:20] AS "deviceNames"
                FROM zentriq.network_vlans v JOIN zentriq.assets a ON a.id = v.asset_id AND a.status = 'active'
               GROUP BY v.vlan_id) t
            """, null, ct)).RequireAuthorization(Permissions.AssetsView);

        // Unmanaged switches: inferred from MAC address tables (several devices behind one access port, no LLDP/CDP neighbour),
        // plus discovered switches that cannot be managed over SNMP.
        g.MapGet("/unmanaged", async (NpgsqlDataSource ds, CancellationToken ct) => await JsonAsync(ds, $"""
            SELECT jsonb_build_object(
              'inferred', (SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."deviceName", t."ifIndex"), '[]') FROM (
                  SELECT i.asset_id AS "assetId", COALESCE(a.hostname, host(a.primary_ip)) AS "deviceName", i.if_index AS "ifIndex", i.name AS "port",
                         i.alias AS "alias", i.vlan AS "vlan", i.speed_mbps AS "speedMbps", i.mac_count AS "macCount", i.connected AS "connected",
                         (SELECT jsonb_agg(jsonb_build_object('mac', m.mac::text, 'ip', (SELECT host(r.ip) FROM zentriq.network_arp r WHERE r.mac = m.mac LIMIT 1)))
                            FROM (SELECT mac FROM zentriq.network_mac_table m WHERE m.asset_id = i.asset_id AND m.if_index = i.if_index ORDER BY mac LIMIT 50) m) AS "macs"
                    FROM zentriq.network_interfaces i JOIN zentriq.assets a ON a.id = i.asset_id AND a.status = 'active'
                   WHERE i.unmanaged_suspect LIMIT 2000) t),
              'notManageable', (SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY lower(t.hostname)), '[]') FROM ({DeviceSelect}) t
                   WHERE t."deviceType" IN ('switch', 'network') AND t.management <> 'snmp')
            )::text
            """, null, ct)).RequireAuthorization(Permissions.AssetsView);

        g.MapGet("/firmware", async (NpgsqlDataSource ds, CancellationToken ct) => await JsonAsync(ds, $"""
            SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t.manufacturer, t.model, t.firmware), '[]')::text FROM (
              SELECT COALESCE(manufacturer, 'Unknown') AS "manufacturer", COALESCE(model, '—') AS "model", COALESCE(firmware, 'Unknown') AS "firmware",
                     count(*)::int AS "count", (array_agg(hostname ORDER BY hostname))[1:20] AS "devices",
                     count(*) FILTER (WHERE management = 'snmp')::int AS "managed"
                FROM ({DeviceSelect}) d GROUP BY 1, 2, 3) t
            """, null, ct)).RequireAuthorization(Permissions.AssetsView);

        // Network details of one device: ports, neighbours, VLANs, modules — and, for any device, the switch port it is connected to.
        api.MapGet("/assets/{id:guid}/network", async (Guid id, NpgsqlDataSource ds, CancellationToken ct) => await JsonAsync(ds, $$"""
            SELECT jsonb_build_object(
              'device', (SELECT to_jsonb(t) FROM ({{DeviceSelect}}) t WHERE t.id = @id),
              'interfaces', (SELECT COALESCE(jsonb_agg(jsonb_build_object('ifIndex', i.if_index, 'name', i.name, 'description', i.description, 'alias', i.alias,
                    'type', i.type, 'mac', i.mac, 'admin', i.admin, 'oper', i.oper, 'speedMbps', i.speed_mbps, 'duplex', i.duplex, 'mtu', i.mtu, 'vlan', i.vlan,
                    'trunk', i.trunk, 'poe', i.poe, 'inErrors', i.in_errors, 'outErrors', i.out_errors, 'ipv4', i.ipv4, 'macCount', i.mac_count,
                    'connected', i.connected, 'neighbor', i.neighbor, 'neighborAssetId', i.neighbor_asset_id, 'uplink', i.uplink,
                    'unmanagedSuspect', i.unmanaged_suspect) ORDER BY i.if_index), '[]') FROM zentriq.network_interfaces i WHERE i.asset_id = @id),
              'neighbors', (SELECT COALESCE(jsonb_agg(jsonb_build_object('protocol', nb.protocol, 'localPort', nb.local_port, 'remoteName', nb.remote_name,
                    'remotePort', nb.remote_port, 'remotePlatform', nb.remote_platform, 'remoteAddress', host(nb.remote_address), 'capabilities', nb.capabilities,
                    'remoteAssetId', nb.remote_asset_id) ORDER BY nb.local_port), '[]') FROM zentriq.network_neighbors nb WHERE nb.asset_id = @id),
              'vlans', (SELECT COALESCE(jsonb_agg(jsonb_build_object('id', v.vlan_id, 'name', v.name) ORDER BY v.vlan_id), '[]') FROM zentriq.network_vlans v WHERE v.asset_id = @id),
              'modules', (SELECT COALESCE(jsonb_agg(jsonb_build_object('class', m.class, 'name', m.name, 'model', m.model, 'serial', m.serial, 'firmware', m.firmware) ORDER BY m.id), '[]')
                    FROM zentriq.network_modules m WHERE m.asset_id = @id),
              'connectedTo', (SELECT COALESCE(jsonb_agg(DISTINCT jsonb_build_object('assetId', i.asset_id, 'deviceName', COALESCE(sw.hostname, host(sw.primary_ip)),
                    'port', i.name, 'ifIndex', i.if_index, 'vlan', m.vlan, 'mac', m.mac::text, 'unmanagedSuspect', i.unmanaged_suspect)), '[]')
                    FROM zentriq.network_mac_table m
                    JOIN zentriq.network_interfaces i ON i.asset_id = m.asset_id AND i.if_index = m.if_index AND NOT i.uplink
                    JOIN zentriq.assets sw ON sw.id = m.asset_id AND sw.status = 'active'
                   WHERE m.asset_id <> @id AND m.mac IN (
                        SELECT ai.value::macaddr FROM zentriq.asset_identifiers ai WHERE ai.asset_id = @id AND ai.kind = 'mac' AND ai.value ~ '^([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$'
                        UNION SELECT mac_address FROM zentriq.assets WHERE id = @id AND mac_address IS NOT NULL))
            )::text
            """, c => c.Parameters.AddWithValue("id", id), ct)).RequireAuthorization(Permissions.AssetsView).RequireModule(Modules.Inventory).WithTags("Network inventory");
    }

    private static async Task<IResult> JsonAsync(NpgsqlDataSource ds, string sql, Action<NpgsqlCommand>? bind, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(sql);
        cmd.CommandTimeout = 60;
        bind?.Invoke(cmd);
        var json = await cmd.ExecuteScalarAsync(ct) as string;
        return Results.Text(json ?? "null", "application/json");
    }

    private static string? Clean(string? q) => string.IsNullOrWhiteSpace(q) ? null : q.Trim().Replace("%", "").Replace("_", "\\_");

    /// <summary>A MAC address typed in any notation (aabb.ccdd.eeff, AA-BB-CC-…) as the macaddr text form for a partial match.</summary>
    public static string? MacQuery(string? q)
    {
        if (q is null) return null;
        var hex = new string(q.Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
        if (hex.Length < 4 || hex.Length != q.Count(ch => ch is not (':' or '-' or '.' or ' '))) return "\u0001"; // not a MAC: match nothing
        return string.Join(":", Enumerable.Range(0, (hex.Length + 1) / 2).Select(i => hex.Substring(i * 2, Math.Min(2, hex.Length - i * 2))));
    }

    private static NpgsqlParameter Text(string name, string? value) => new(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };
}
