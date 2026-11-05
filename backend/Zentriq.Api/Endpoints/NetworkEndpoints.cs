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
        SELECT a.id AS "id", a.hostname AS "hostname", COALESCE(zentriq.asset_ipv4(a.id), host(a.primary_ip)) AS "ip", COALESCE(a.device_type, 'network') AS "deviceType",
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
               nd.memory_percent AS "memoryPercent", nd.memory_total_mb AS "memoryTotalMb", nd.temperature_c AS "temperatureC",
               COALESCE(nd.components_bad, 0) AS "componentsBad", COALESCE(nd.peers_down, 0) AS "peersDown", COALESCE(nd.busy_ports, 0) AS "busyPorts",
               jsonb_array_length(COALESCE(nd.routing_peers, '[]')) AS "routingPeers",
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
                'highMemory', (SELECT count(*) FROM dev WHERE "memoryPercent" >= 85),
                'hotDevices', (SELECT count(*) FROM dev WHERE "temperatureC" >= 60),
                'componentsBad', (SELECT COALESCE(sum("componentsBad"), 0) FROM dev),
                'peersDown', (SELECT COALESCE(sum("peersDown"), 0) FROM dev),
                'routingPeers', (SELECT COALESCE(sum("routingPeers"), 0) FROM dev),
                'busyPorts', (SELECT count(*) FROM ports WHERE util_percent >= 70),
                'blockingPorts', (SELECT count(*) FROM ports WHERE stp_state = 'blocking'),
                'portsWithDiscards', (SELECT count(*) FROM ports WHERE COALESCE(in_discards, 0) + COALESCE(out_discards, 0) > 0),
                'transceivers', (SELECT count(*) FROM zentriq.network_modules m JOIN zentriq.assets a ON a.id = m.asset_id AND a.status = 'active' WHERE m.class = 'transceiver'),
                'endpoints', (SELECT count(*) FROM zentriq.network_endpoints WHERE last_seen > now() - interval '30 days'),
                'unknownDevices', (SELECT count(*) FROM zentriq.network_endpoints WHERE asset_id IS NULL AND NOT acknowledged AND last_seen > now() - interval '30 days'),
                'newDevices7d', (SELECT count(*) FROM zentriq.network_endpoints WHERE NOT baseline AND first_seen > now() - interval '7 days'),
                'movedDevices7d', (SELECT count(*) FROM zentriq.network_endpoints WHERE moved_at > now() - interval '7 days'),
                'ipConflicts', (SELECT count(*) FROM ({{ConflictsSql}}) c),
                'subnets', (SELECT count(DISTINCT n) FROM zentriq.network_interfaces i JOIN zentriq.assets a ON a.id = i.asset_id AND a.status = 'active', unnest(i.subnets) n),
                'macVendors', (SELECT count(*) FROM zentriq.mac_vendors),
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
              SELECT i.asset_id AS "assetId", COALESCE(a.hostname, zentriq.asset_ipv4(a.id), host(a.primary_ip)) AS "deviceName", i.if_index AS "ifIndex", i.name AS "name",
                     i.description AS "description", i.alias AS "alias", i.type AS "type", i.admin AS "admin", i.oper AS "oper", i.speed_mbps AS "speedMbps",
                     i.duplex AS "duplex", i.vlan AS "vlan", i.trunk AS "trunk", i.poe AS "poe", i.in_errors AS "inErrors", i.out_errors AS "outErrors",
                     i.mac AS "mac", i.ipv4 AS "ipv4", i.mac_count AS "macCount", i.connected AS "connected", i.neighbor AS "neighbor",
                     i.neighbor_asset_id AS "neighborAssetId", i.uplink AS "uplink", i.unmanaged_suspect AS "unmanagedSuspect",
                     i.in_bps AS "inBps", i.out_bps AS "outBps", i.util_percent AS "utilPercent", i.in_discards AS "inDiscards", i.out_discards AS "outDiscards",
                     i.stp_state AS "stpState", i.lag_if_index AS "lagIfIndex",
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
                      OR (@filter = 'busy' AND i.util_percent >= 70)
                      OR (@filter = 'blocking' AND i.stp_state = 'blocking')
                      OR (@filter = 'discards' AND COALESCE(i.in_discards, 0) + COALESCE(i.out_discards, 0) > 0)
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
              SELECT nb.asset_id AS "assetId", COALESCE(a.hostname, zentriq.asset_ipv4(a.id), host(a.primary_ip)) AS "deviceName", COALESCE(a.device_type, 'network') AS "deviceType", nb.protocol AS "protocol",
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
                     (array_agg(DISTINCT COALESCE(a.hostname, zentriq.asset_ipv4(a.id), host(a.primary_ip))))[1:20] AS "deviceNames"
                FROM zentriq.network_vlans v JOIN zentriq.assets a ON a.id = v.asset_id AND a.status = 'active'
               GROUP BY v.vlan_id) t
            """, null, ct)).RequireAuthorization(Permissions.AssetsView);

        // Unmanaged switches: inferred from MAC address tables (several devices behind one access port, no LLDP/CDP neighbour),
        // plus discovered switches that cannot be managed over SNMP.
        g.MapGet("/unmanaged", async (NpgsqlDataSource ds, CancellationToken ct) => await JsonAsync(ds, $"""
            SELECT jsonb_build_object(
              'inferred', (SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."deviceName", t."ifIndex"), '[]') FROM (
                  SELECT i.asset_id AS "assetId", COALESCE(a.hostname, zentriq.asset_ipv4(a.id), host(a.primary_ip)) AS "deviceName", i.if_index AS "ifIndex", i.name AS "port",
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

        // ---- connected devices (every MAC address on an access port) ---------------------------------------------
        g.MapGet("/endpoints", async (NpgsqlDataSource ds, string? filter, string? q, string? kind, Guid? switchId, CancellationToken ct) => await JsonAsync(ds, """
            SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."unknown" DESC, t."lastSeen" DESC), '[]')::text FROM (
              SELECT e.mac::text AS "mac", host(e.ip) AS "ip", e.vendor AS "vendor", e.kind AS "kind", e.asset_id AS "assetId",
                     COALESCE(a.hostname, host(a.primary_ip)) AS "assetName", COALESCE(a.device_type, a.category) AS "deviceType",
                     e.switch_id AS "switchId", COALESCE(sw.hostname, host(sw.primary_ip)) AS "switchName", e.port AS "port", e.if_index AS "ifIndex", e.vlan AS "vlan",
                     e.behind_unmanaged AS "behindUnmanaged", e.first_seen AS "firstSeen", e.last_seen AS "lastSeen", e.baseline AS "baseline",
                     e.moved_at AS "movedAt", COALESCE(ps.hostname, host(ps.primary_ip)) AS "prevSwitchName", e.prev_port AS "prevPort",
                     host(e.prev_ip) AS "prevIp", e.ip_changed_at AS "ipChangedAt", e.acknowledged AS "acknowledged", e.note AS "note",
                     e.ack_by AS "ackBy", (e.asset_id IS NULL AND NOT e.acknowledged) AS "unknown"
                FROM zentriq.network_endpoints e
                LEFT JOIN zentriq.assets a ON a.id = e.asset_id
                LEFT JOIN zentriq.assets sw ON sw.id = e.switch_id
                LEFT JOIN zentriq.assets ps ON ps.id = e.prev_switch_id
               WHERE (@sw::uuid IS NULL OR e.switch_id = @sw)
                 AND (@kind::text IS NULL OR e.kind = @kind)
                 AND (@filter::text IS NULL
                      OR (@filter = 'unknown' AND e.asset_id IS NULL AND NOT e.acknowledged)
                      OR (@filter = 'known' AND (e.asset_id IS NOT NULL OR e.acknowledged))
                      OR (@filter = 'new' AND NOT e.baseline AND e.first_seen > now() - interval '7 days')
                      OR (@filter = 'moved' AND e.moved_at > now() - interval '7 days')
                      OR (@filter = 'ipchanged' AND e.ip_changed_at > now() - interval '7 days')
                      OR (@filter = 'unmanaged' AND e.behind_unmanaged)
                      OR (@filter = 'private' AND e.kind = 'private')
                      OR (@filter = 'stale' AND e.last_seen < now() - interval '7 days'))
                 AND (@q::text IS NULL OR e.mac::text ILIKE '%' || @qmac || '%' OR host(e.ip) LIKE @q || '%' OR e.vendor ILIKE '%' || @q || '%'
                      OR a.hostname ILIKE '%' || @q || '%' OR sw.hostname ILIKE '%' || @q || '%' OR e.port ILIKE '%' || @q || '%' OR e.note ILIKE '%' || @q || '%')
               LIMIT 5000) t
            """, c =>
            {
                var clean = Clean(q);
                c.Parameters.Add(Text("filter", filter)); c.Parameters.Add(Text("kind", kind)); c.Parameters.Add(Text("q", clean)); c.Parameters.Add(Text("qmac", MacQuery(clean)));
                c.Parameters.Add(new NpgsqlParameter("sw", NpgsqlDbType.Uuid) { Value = (object?)switchId ?? DBNull.Value });
            }, ct)).RequireAuthorization(Permissions.AssetsView);

        g.MapGet("/endpoints/vendors", async (NpgsqlDataSource ds, CancellationToken ct) => await JsonAsync(ds, """
            SELECT COALESCE(jsonb_agg(jsonb_build_object('vendor', v, 'count', n, 'unknown', u) ORDER BY n DESC), '[]')::text FROM (
              SELECT COALESCE(vendor, 'Not registered') v, count(*) n, count(*) FILTER (WHERE asset_id IS NULL AND NOT acknowledged) u
                FROM zentriq.network_endpoints WHERE last_seen > now() - interval '30 days' GROUP BY 1 ORDER BY 2 DESC LIMIT 25) x
            """, null, ct)).RequireAuthorization(Permissions.AssetsView);

        g.MapPost("/endpoints/acknowledge", async (AckBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            var macs = (body.Macs ?? []).Select(NetworkStore.Mac).Where(m => m is not null).Distinct().Take(1000).ToArray();
            if (macs.Length == 0) return Validation.Problem("macs", "Choose at least one device.");
            var note = string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim();
            if (note is { Length: > 500 }) return Validation.Problem("note", "Keep the note under 500 characters.");
            await using var cmd = ds.CreateCommand("""
                UPDATE zentriq.network_endpoints SET acknowledged = @ack, note = COALESCE(@note, note),
                       ack_by = CASE WHEN @ack THEN @by END, ack_at = CASE WHEN @ack THEN now() END
                 WHERE mac = ANY (@m::macaddr[])
                """);
            cmd.Parameters.AddWithValue("ack", body.Acknowledged ?? true);
            cmd.Parameters.Add(Text("note", note));
            cmd.Parameters.Add(Text("by", rc.UserName));
            cmd.Parameters.Add(new NpgsqlParameter("m", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = macs });
            var n = await cmd.ExecuteNonQueryAsync(ct);
            await rc.AuditAsync(body.Acknowledged ?? true ? "network.endpoint_ack" : "network.endpoint_unack", $"{n} device(s)", detail: new { macs, note });
            return Results.Ok(new { updated = n });
        }).RequireAuthorization(Permissions.AssetsManage);

        // ---- IP address management --------------------------------------------------------------------------------
        g.MapGet("/subnets", async (NpgsqlDataSource ds, CancellationToken ct) => await JsonAsync(ds, $$"""
            WITH nets AS (
                SELECT n, (array_agg(DISTINCT via) FILTER (WHERE via IS NOT NULL))[1:4] AS via, (array_agg(DISTINCT vlan) FILTER (WHERE vlan IS NOT NULL))[1:4] AS vlans,
                       (array_agg(DISTINCT gw) FILTER (WHERE gw IS NOT NULL))[1:4] AS gateways, bool_or(src = 'interface') AS from_devices
                  FROM (
                    SELECT sn AS n, 'interface' AS src, COALESCE(a.hostname, host(a.primary_ip)) || COALESCE(' ' || i.name, '') AS via, i.vlan,
                           (SELECT ip FROM unnest(i.ipv4) ip WHERE ip::inet << sn LIMIT 1) AS gw
                      FROM zentriq.network_interfaces i JOIN zentriq.assets a ON a.id = i.asset_id AND a.status = 'active', unnest(i.subnets) sn
                    UNION ALL
                    SELECT x::cidr, 'scope', NULL, NULL, NULL
                      FROM jsonb_array_elements_text(COALESCE((SELECT CASE WHEN jsonb_typeof(value) = 'array' THEN value END FROM zentriq.system_settings WHERE key = 'discovery.approved_scopes'), '[]')) x
                     WHERE x ~ '^[0-9]{1,3}(\.[0-9]{1,3}){3}/[0-9]{1,2}$' AND split_part(x, '/', 2)::int BETWEEN 16 AND 30
                  ) s GROUP BY n),
            {{IpsCte}}
            SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."cidr"), '[]')::text FROM (
              SELECT n.n::text AS "cidr", masklen(n.n) AS "prefix", (2 ^ (32 - masklen(n.n)))::bigint - 2 AS "size", n.via AS "via", n.vlans AS "vlans",
                     n.gateways AS "gateways", n.from_devices AS "fromDevices",
                     (SELECT count(DISTINCT ip) FROM ips WHERE ips.ip << n.n) AS "used",
                     (SELECT count(DISTINCT ip) FROM ips WHERE ips.ip << n.n AND ips.asset_id IS NOT NULL) AS "known",
                     (SELECT count(DISTINCT ip) FROM ips WHERE ips.ip << n.n AND ips.asset_id IS NULL) AS "unknown",
                     (SELECT count(*) FROM ({{ConflictsSql}}) c WHERE c.ip::inet << n.n) AS "conflicts",
                     (SELECT max(seen) FROM ips WHERE ips.ip << n.n) AS "lastSeen"
                FROM nets n WHERE family(n.n) = 4 AND masklen(n.n) BETWEEN 16 AND 30) t
            """, null, ct)).RequireAuthorization(Permissions.AssetsView);

        g.MapGet("/subnets/ips", async (NpgsqlDataSource ds, string cidr, bool? all, CancellationToken ct) =>
        {
            if (!System.Net.IPNetwork.TryParse(cidr ?? "", out var net) || net.BaseAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || net.PrefixLength is < 16 or > 30)
                return Validation.Problem("cidr", "Enter an IPv4 network between /16 and /30, for example 10.1.20.0/24.");
            return await JsonAsync(ds, $$"""
                WITH n AS (SELECT @cidr::cidr AS n),
                {{IpsCte}},
                inside AS (SELECT * FROM ips, n WHERE ips.ip << n.n),
                conflicts AS ({{ConflictsSql}}),
                hosts AS (
                    SELECT DISTINCT ip FROM inside
                    UNION
                    SELECT (host(network(n.n))::inet + g) FROM n, generate_series(1, CASE WHEN @all AND masklen(n.n) >= 22 THEN (2 ^ (32 - masklen(n.n)))::int - 2 ELSE 0 END) g)
                SELECT jsonb_build_object('cidr', (SELECT n::text FROM n), 'rows', COALESCE((SELECT jsonb_agg(to_jsonb(t) ORDER BY t."sort") FROM (
                  SELECT h.ip AS "sort", host(h.ip) AS "ip", EXISTS (SELECT 1 FROM inside i WHERE i.ip = h.ip) AS "used",
                         (SELECT array_agg(DISTINCT i.mac::text) FROM inside i WHERE i.ip = h.ip AND i.mac IS NOT NULL) AS "macs",
                         (SELECT array_agg(DISTINCT i.src) FROM inside i WHERE i.ip = h.ip) AS "sources",
                         x.asset_id AS "assetId", COALESCE(a.hostname, host(a.primary_ip)) AS "assetName", COALESCE(a.device_type, a.category) AS "deviceType",
                         ep.vendor AS "vendor", ep.kind AS "kind", COALESCE(sw.hostname, host(sw.primary_ip)) AS "switchName", ep.switch_id AS "switchId", ep.port AS "port", ep.vlan AS "vlan",
                         (SELECT max(i.seen) FROM inside i WHERE i.ip = h.ip) AS "lastSeen",
                         EXISTS (SELECT 1 FROM conflicts c WHERE c.ip = host(h.ip)) AS "conflict"
                    FROM hosts h
                    LEFT JOIN LATERAL (SELECT i.asset_id FROM inside i WHERE i.ip = h.ip AND i.asset_id IS NOT NULL LIMIT 1) x ON true
                    LEFT JOIN zentriq.assets a ON a.id = x.asset_id
                    LEFT JOIN LATERAL (SELECT e.* FROM zentriq.network_endpoints e WHERE host(e.ip) = host(h.ip) ORDER BY e.last_seen DESC LIMIT 1) ep ON true
                    LEFT JOIN zentriq.assets sw ON sw.id = ep.switch_id
                   LIMIT 4096) t), '[]'))::text
                """, c => { c.Parameters.AddWithValue("cidr", net.ToString()); c.Parameters.AddWithValue("all", all ?? true); }, ct);
        }).RequireAuthorization(Permissions.AssetsView);

        g.MapGet("/conflicts", async (NpgsqlDataSource ds, CancellationToken ct) => await JsonAsync(ds, $$"""
            SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t.ip), '[]')::text FROM (
              SELECT c.ip AS "ip", c.macs AS "macs",
                     (SELECT jsonb_agg(jsonb_build_object('mac', m, 'vendor', zentriq.mac_vendor(m::macaddr),
                             'assetId', (SELECT ai.asset_id FROM zentriq.asset_identifiers ai WHERE ai.kind = 'mac' AND lower(ai.value) = m LIMIT 1),
                             'assetName', (SELECT a.hostname FROM zentriq.asset_identifiers ai JOIN zentriq.assets a ON a.id = ai.asset_id WHERE ai.kind = 'mac' AND lower(ai.value) = m LIMIT 1),
                             'port', (SELECT COALESCE(sw.hostname, '') || ' ' || COALESCE(e.port, '') FROM zentriq.network_endpoints e LEFT JOIN zentriq.assets sw ON sw.id = e.switch_id WHERE e.mac = m::macaddr)))
                        FROM unnest(c.macs) m) AS "devices",
                     c.seen_by AS "seenBy"
                FROM ({{ConflictsSql}}) c) t
            """, null, ct)).RequireAuthorization(Permissions.AssetsView);

        // ---- MAC address vendors (IEEE registry) ----------------------------------------------------------------------
        g.MapGet("/vendors/status", async (NpgsqlDataSource ds, CancellationToken ct) => await JsonAsync(ds, $"""
            SELECT jsonb_build_object('prefixes', (SELECT count(*) FROM zentriq.mac_vendors),
                   'status', (SELECT value FROM zentriq.system_settings WHERE key = '{MacVendors.StatusKey}'))::text
            """, null, ct)).RequireAuthorization(Permissions.AssetsView);

        g.MapPost("/vendors/update", async (NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            try
            {
                var n = await MacVendors.DownloadAsync(ds, ct);
                await rc.AuditAsync("network.vendors_update", $"{n} prefixes");
                return Results.Ok(new { prefixes = n });
            }
            catch (InvalidOperationException ex)
            {
                await MacVendors.RecordErrorAsync(ds, ex.Message, ct);
                return Results.Problem(statusCode: 502, title: ex.Message);
            }
        }).RequireAuthorization(Permissions.AssetsManage);

        g.MapPost("/vendors/import", async (VendorImportBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            var rows = MacVendors.Parse(body.Csv ?? "");
            if (rows.Count == 0) return Validation.Problem("csv", "No MAC address prefixes found. Use oui.csv, mam.csv or oui36.csv from standards-oui.ieee.org.");
            var n = await MacVendors.StoreAsync(ds, rows, "import", ct);
            await rc.AuditAsync("network.vendors_import", $"{n} prefixes");
            return Results.Ok(new { prefixes = n });
        }).RequireAuthorization(Permissions.AssetsManage);

        // Network details of one device: ports, neighbours, VLANs, modules — and, for any device, the switch port it is connected to.
        api.MapGet("/assets/{id:guid}/network", async (Guid id, NpgsqlDataSource ds, CancellationToken ct) => await JsonAsync(ds, $$"""
            SELECT jsonb_build_object(
              'device', (SELECT to_jsonb(t) FROM ({{DeviceSelect}}) t WHERE t.id = @id),
              'interfaces', (SELECT COALESCE(jsonb_agg(jsonb_build_object('ifIndex', i.if_index, 'name', i.name, 'description', i.description, 'alias', i.alias,
                    'type', i.type, 'mac', i.mac, 'admin', i.admin, 'oper', i.oper, 'speedMbps', i.speed_mbps, 'duplex', i.duplex, 'mtu', i.mtu, 'vlan', i.vlan,
                    'trunk', i.trunk, 'poe', i.poe, 'inErrors', i.in_errors, 'outErrors', i.out_errors, 'ipv4', i.ipv4, 'macCount', i.mac_count,
                    'connected', i.connected, 'neighbor', i.neighbor, 'neighborAssetId', i.neighbor_asset_id, 'uplink', i.uplink,
                    'unmanagedSuspect', i.unmanaged_suspect, 'inBps', i.in_bps, 'outBps', i.out_bps, 'utilPercent', i.util_percent,
                    'inDiscards', i.in_discards, 'outDiscards', i.out_discards, 'stpState', i.stp_state, 'lagIfIndex', i.lag_if_index) ORDER BY i.if_index), '[]') FROM zentriq.network_interfaces i WHERE i.asset_id = @id),
              'neighbors', (SELECT COALESCE(jsonb_agg(jsonb_build_object('protocol', nb.protocol, 'localPort', nb.local_port, 'remoteName', nb.remote_name,
                    'remotePort', nb.remote_port, 'remotePlatform', nb.remote_platform, 'remoteAddress', host(nb.remote_address), 'capabilities', nb.capabilities,
                    'remoteAssetId', nb.remote_asset_id) ORDER BY nb.local_port), '[]') FROM zentriq.network_neighbors nb WHERE nb.asset_id = @id),
              'sensors', (SELECT COALESCE(nd.sensors, '[]') FROM zentriq.network_devices nd WHERE nd.asset_id = @id),
              'components', (SELECT COALESCE(nd.components, '[]') FROM zentriq.network_devices nd WHERE nd.asset_id = @id),
              'routingPeers', (SELECT COALESCE(nd.routing_peers, '[]') FROM zentriq.network_devices nd WHERE nd.asset_id = @id),
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

    public sealed record AckBody(string[]? Macs, string? Note, bool? Acknowledged);
    public sealed record VendorImportBody(string? Csv);

    /// <summary>Every IPv4 address in use: newest ARP entry per address (routers, layer-3 switches, firewalls), inventoried devices, connected devices.</summary>
    private const string IpsCte = """
        ips AS (
            SELECT host(r.ip)::inet AS ip, r.mac, 'arp' AS src, (SELECT ai.asset_id FROM zentriq.asset_identifiers ai WHERE ai.kind = 'mac' AND lower(ai.value) = r.mac::text LIMIT 1) AS asset_id, d.collected_at AS seen
              FROM zentriq.network_arp r JOIN zentriq.network_devices d ON d.asset_id = r.asset_id JOIN zentriq.assets x ON x.id = r.asset_id AND x.status = 'active'
             WHERE family(r.ip) = 4
            UNION ALL
            SELECT host(a.primary_ip)::inet, a.mac_address, 'inventory', a.id, a.last_seen_at FROM zentriq.assets a
             WHERE a.status = 'active' AND a.primary_ip IS NOT NULL AND family(a.primary_ip) = 4
            UNION ALL
            SELECT host(e.ip)::inet, e.mac, 'switch', e.asset_id, e.last_seen FROM zentriq.network_endpoints e
             WHERE e.ip IS NOT NULL AND family(e.ip) = 4 AND e.last_seen > now() - interval '30 days')
        """;

    /// <summary>
    /// IP addresses that ARP tables give to more than one device. Virtual router MAC addresses (VRRP, HSRP, GLBP) and
    /// several MAC addresses of the same inventoried device (teaming, docking stations) are not conflicts.
    /// </summary>
    private const string ConflictsSql = """
        SELECT host(r.ip) AS ip, array_agg(DISTINCT r.mac::text) AS macs,
               array_agg(DISTINCT COALESCE(x.hostname, host(x.primary_ip))) AS seen_by
          FROM zentriq.network_arp r JOIN zentriq.assets x ON x.id = r.asset_id AND x.status = 'active'
         WHERE family(r.ip) = 4
           AND left(r.mac::text, 14) NOT IN ('00:00:5e:00:01', '00:00:5e:00:02', '00:00:0c:07:ac') AND left(r.mac::text, 13) <> '00:00:0c:9f:f'
           AND left(r.mac::text, 8) <> '00:07:b4'
         GROUP BY host(r.ip)
        HAVING count(DISTINCT COALESCE((SELECT ai.asset_id::text FROM zentriq.asset_identifiers ai WHERE ai.kind = 'mac' AND lower(ai.value) = r.mac::text LIMIT 1), r.mac::text)) > 1
        """;

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
