using System.Net;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Inventory;

namespace Zentriq.Infrastructure.Inventory;

/// <summary>
/// Stores the network inventory of a switch, router, firewall or access point (ports, neighbours, VLANs, MAC address and
/// ARP tables, modules) in relational tables, and derives what is connected to each port:
/// <list type="bullet">
/// <item>the known device on an access port (by MAC address, with its IP address from any ARP table);</item>
/// <item>uplinks — ports with an LLDP/CDP neighbour that is network equipment, a trunk, or the MAC address of other network equipment;</item>
/// <item>suspected unmanaged switches or hubs — an access port that is not an uplink but on which two or more devices
/// (real, globally administered, non-virtual MAC addresses) were learned: there is a switch without management behind it.</item>
/// </list>
/// </summary>
public static class NetworkStore
{
    /// <summary>OUIs of virtual NICs (VMware, Hyper-V, KVM/QEMU, VirtualBox, Xen): several on a port means a hypervisor, not a switch.</summary>
    public static readonly string[] VirtualOuis = ["00:50:56", "00:0c:29", "00:05:69", "00:1c:14", "00:15:5d", "52:54:00", "08:00:27", "00:16:3e"];

    /// <summary>
    /// How a network device is managed: "snmp" — full inventory collected over SNMP; "web" — answers on a web interface
    /// but not over SNMP (managed / smart switch without SNMP access, or SNMP disabled); "none" — no management interface found.
    /// </summary>
    public const string ManagementExpr = """
        CASE WHEN nd.asset_id IS NOT NULL THEN 'snmp'
             WHEN EXISTS (SELECT 1 FROM zentriq.scan_results sr WHERE sr.asset_id = a.id AND sr.open_ports && ARRAY[80,443,8080,8443]) THEN 'web'
             ELSE 'none' END
        """;

    /// <summary>A port above this share of its speed (average between two collections) counts as busy.</summary>
    public const double BusyPercent = 70;

    private sealed record Rate(long? InBps, long? OutBps, double? Util);

    /// <summary>Average traffic since the previous collection, from the 64-bit octet counters (a counter reset gives no rate).</summary>
    public static (long? InBps, long? OutBps, double? Util) Traffic(decimal? prevIn, decimal? prevOut, DateTime? prevAt, long? inOctets, long? outOctets, DateTime now, long? speedMbps)
    {
        if (prevAt is null) return (null, null, null);
        var secs = (now - prevAt.Value).TotalSeconds;
        if (secs < 30 || secs > 7 * 86400) return (null, null, null);
        long? Bps(decimal? before, long? after) => before is { } b && after is { } a && a >= b ? (long)((a - b) * 8 / (decimal)secs) : null;
        var i = Bps(prevIn, inOctets);
        var o = Bps(prevOut, outOctets);
        double? util = speedMbps is > 0 && (i is not null || o is not null)
            ? Math.Min(100, Math.Round(Math.Max(i ?? 0, o ?? 0) * 100.0 / (speedMbps.Value * 1_000_000.0), 1)) : null;
        return (i, o, util);
    }

    public static async Task SaveAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid assetId, NetworkDeviceInfo n, CancellationToken ct)
    {
        // Counters of the previous collection, for traffic rates.
        var previous = new Dictionary<int, (decimal? In, decimal? Out, DateTime? At)>();
        await using (var prev = new NpgsqlCommand("SELECT if_index, in_octets, out_octets, counters_at FROM zentriq.network_interfaces WHERE asset_id = @a", conn, tx))
        {
            prev.Parameters.AddWithValue("a", assetId);
            await using var r = await prev.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                previous[r.GetInt32(0)] = (r.IsDBNull(1) ? null : r.GetDecimal(1), r.IsDBNull(2) ? null : r.GetDecimal(2), r.IsDBNull(3) ? null : r.GetFieldValue<DateTime>(3));
        }
        var now = DateTime.UtcNow;
        var rates = n.Interfaces.GroupBy(i => i.Index).ToDictionary(g => g.Key, g =>
        {
            var i = g.First();
            var p = previous.TryGetValue(i.Index, out var x) ? x : default;
            var t = Traffic(p.In, p.Out, p.At, i.InOctets, i.OutOctets, now, i.SpeedMbps);
            return new Rate(t.InBps, t.OutBps, t.Util);
        });

        foreach (var t in new[] { "network_interfaces", "network_neighbors", "network_vlans", "network_mac_table", "network_arp", "network_modules", "network_devices" })
        {
            await using var del = new NpgsqlCommand($"DELETE FROM zentriq.{t} WHERE asset_id = @a", conn, tx);
            del.Parameters.AddWithValue("a", assetId);
            await del.ExecuteNonQueryAsync(ct);
        }

        var physical = n.Interfaces.Where(i => i.Type == "ethernet").ToList();
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO zentriq.network_devices (asset_id, snmp_version, ports_total, ports_up, ports_admin_down, interfaces, vlans, neighbors,
                mac_entries, arp_entries, ip_forwarding, routes, cpu_percent, poe_budget_w, poe_used_w, stp_root, stack_members, bridge_address, collected_at,
                memory_percent, memory_total_mb, temperature_c, sensors, components, routing_peers, components_bad, peers_down, busy_ports)
            VALUES (@a, @v, @pt, @pu, @pd, @i, @vl, @nb, @me, @ae, @fw, @r, @cpu, @pb, @pw, @stp, @sm, @ba, now(),
                @mem, @memt, @temp, @sens, @comp, @peers, @cbad, @pdown, @busy)
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("a", assetId);
            cmd.Parameters.Add(Text("v", n.SnmpVersion));
            cmd.Parameters.AddWithValue("pt", physical.Count);
            cmd.Parameters.AddWithValue("pu", physical.Count(i => i.Oper == "up"));
            cmd.Parameters.AddWithValue("pd", physical.Count(i => i.Admin == "down"));
            cmd.Parameters.AddWithValue("i", n.Interfaces.Count);
            cmd.Parameters.AddWithValue("vl", n.Vlans.Count);
            cmd.Parameters.AddWithValue("nb", n.Neighbors.Count);
            cmd.Parameters.AddWithValue("me", n.MacTable.Count);
            cmd.Parameters.AddWithValue("ae", n.Arp.Count);
            cmd.Parameters.Add(Nullable("fw", NpgsqlDbType.Boolean, n.IpForwarding));
            cmd.Parameters.Add(Nullable("r", NpgsqlDbType.Bigint, n.Routes));
            cmd.Parameters.Add(Nullable("cpu", NpgsqlDbType.Integer, n.CpuPercent));
            cmd.Parameters.Add(Nullable("pb", NpgsqlDbType.Integer, n.PoeBudgetWatts));
            cmd.Parameters.Add(Nullable("pw", NpgsqlDbType.Integer, n.PoeUsedWatts));
            cmd.Parameters.Add(Nullable("stp", NpgsqlDbType.Boolean, n.StpRoot));
            cmd.Parameters.Add(Nullable("sm", NpgsqlDbType.Integer, n.StackMembers));
            cmd.Parameters.Add(Text("ba", Mac(n.BridgeAddress)));
            cmd.Parameters.Add(Nullable("mem", NpgsqlDbType.Integer, n.MemoryPercent));
            cmd.Parameters.Add(Nullable("memt", NpgsqlDbType.Bigint, n.MemoryTotalMb));
            cmd.Parameters.Add(Nullable("temp", NpgsqlDbType.Numeric, n.TemperatureC is { } tc ? (decimal?)Math.Round((decimal)tc, 1) : null));
            cmd.Parameters.Add(Json("sens", n.Sensors.Take(256)));
            cmd.Parameters.Add(Json("comp", n.Components.Take(128)));
            cmd.Parameters.Add(Json("peers", n.RoutingPeers.Take(512)));
            cmd.Parameters.AddWithValue("cbad", n.Components.Count(c => c.Status is "warning" or "critical" or "shutdown" or "notFunctioning")
                                               + n.Sensors.Count(x => x.Status == "nonoperational"));
            cmd.Parameters.AddWithValue("pdown", n.RoutingPeers.Count(x => !x.Up));
            cmd.Parameters.AddWithValue("busy", rates.Values.Count(x => x.Util >= BusyPercent));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var ifs = n.Interfaces.GroupBy(i => i.Index).Select(g => g.First()).Take(4096).ToList();
        if (ifs.Count > 0)
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO zentriq.network_interfaces (asset_id, if_index, name, description, alias, type, mac, admin, oper, speed_mbps, duplex, mtu,
                    vlan, trunk, poe, in_errors, out_errors, ipv4, last_change_sec,
                    in_octets, out_octets, in_discards, out_discards, counters_at, in_bps, out_bps, util_percent, stp_state, lag_if_index)
                SELECT @a, x.ix, x.nm, x.ds, x.al, x.ty, x.mc, x.ad, x.op, x.sp, x.dp, x.mt, x.vl, x.tr, x.pe, x.ie, x.oe,
                       CASE WHEN x.ip IS NULL OR x.ip = '' THEN '{}'::text[] ELSE string_to_array(x.ip, ',') END, x.lc,
                       x.io, x.oo, x.idc, x.odc, CASE WHEN x.io IS NULL AND x.oo IS NULL THEN NULL ELSE @now END, x.ib, x.ob, x.ut, x.st, x.lg
                FROM unnest(@ix, @nm, @ds, @al, @ty, @mc, @ad, @op, @sp, @dp, @mt, @vl, @tr, @pe, @ie, @oe, @ip, @lc,
                            @io, @oo, @idc, @odc, @ib, @ob, @ut, @st, @lg)
                     AS x(ix, nm, ds, al, ty, mc, ad, op, sp, dp, mt, vl, tr, pe, ie, oe, ip, lc, io, oo, idc, odc, ib, ob, ut, st, lg)
                """, conn, tx);
            cmd.Parameters.AddWithValue("a", assetId);
            cmd.Parameters.AddWithValue("ix", ifs.Select(i => i.Index).ToArray());
            cmd.Parameters.Add(TextArray("nm", ifs.Select(i => Trunc(i.Name, 200))));
            cmd.Parameters.Add(TextArray("ds", ifs.Select(i => Trunc(i.Description, 300))));
            cmd.Parameters.Add(TextArray("al", ifs.Select(i => Trunc(i.Alias, 300))));
            cmd.Parameters.Add(TextArray("ty", ifs.Select(i => i.Type)));
            cmd.Parameters.Add(TextArray("mc", ifs.Select(i => Mac(i.Mac))));
            cmd.Parameters.Add(TextArray("ad", ifs.Select(i => i.Admin)));
            cmd.Parameters.Add(TextArray("op", ifs.Select(i => i.Oper)));
            cmd.Parameters.Add(Array("sp", NpgsqlDbType.Bigint, ifs.Select(i => i.SpeedMbps)));
            cmd.Parameters.Add(TextArray("dp", ifs.Select(i => i.Duplex)));
            cmd.Parameters.Add(Array("mt", NpgsqlDbType.Integer, ifs.Select(i => i.Mtu)));
            cmd.Parameters.Add(Array("vl", NpgsqlDbType.Integer, ifs.Select(i => i.Vlan)));
            cmd.Parameters.Add(Array("tr", NpgsqlDbType.Boolean, ifs.Select(i => i.Trunk)));
            cmd.Parameters.Add(Array("pe", NpgsqlDbType.Boolean, ifs.Select(i => i.PoeDelivering)));
            cmd.Parameters.Add(Array("ie", NpgsqlDbType.Bigint, ifs.Select(i => i.InErrors)));
            cmd.Parameters.Add(Array("oe", NpgsqlDbType.Bigint, ifs.Select(i => i.OutErrors)));
            cmd.Parameters.Add(TextArray("ip", ifs.Select(i => i.Ipv4.Count == 0 ? null : string.Join(",", i.Ipv4.Where(Ip).Take(16)))));
            cmd.Parameters.Add(Array("lc", NpgsqlDbType.Bigint, ifs.Select(i => i.LastChangeSeconds)));
            cmd.Parameters.Add(Array("io", NpgsqlDbType.Numeric, ifs.Select(i => i.InOctets is { } x ? (decimal?)x : null)));
            cmd.Parameters.Add(Array("oo", NpgsqlDbType.Numeric, ifs.Select(i => i.OutOctets is { } x ? (decimal?)x : null)));
            cmd.Parameters.Add(Array("idc", NpgsqlDbType.Bigint, ifs.Select(i => i.InDiscards)));
            cmd.Parameters.Add(Array("odc", NpgsqlDbType.Bigint, ifs.Select(i => i.OutDiscards)));
            cmd.Parameters.Add(Array("ib", NpgsqlDbType.Bigint, ifs.Select(i => rates[i.Index].InBps)));
            cmd.Parameters.Add(Array("ob", NpgsqlDbType.Bigint, ifs.Select(i => rates[i.Index].OutBps)));
            cmd.Parameters.Add(Array("ut", NpgsqlDbType.Numeric, ifs.Select(i => rates[i.Index].Util is { } u ? (decimal?)u : null)));
            cmd.Parameters.Add(TextArray("st", ifs.Select(i => i.StpState)));
            cmd.Parameters.Add(Array("lg", NpgsqlDbType.Integer, ifs.Select(i => i.LagIfIndex)));
            cmd.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var nbs = n.Neighbors.Take(1024).ToList();
        if (nbs.Count > 0)
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO zentriq.network_neighbors (asset_id, protocol, local_port, local_if_index, remote_name, remote_port, remote_description,
                    remote_platform, remote_chassis_id, remote_address, capabilities)
                SELECT @a, x.pr, x.lp, x.li, x.rn, x.rp, x.rd, x.pl, x.rc, x.ra::inet,
                       CASE WHEN x.cp IS NULL OR x.cp = '' THEN '{}'::text[] ELSE string_to_array(x.cp, ',') END
                FROM unnest(@pr, @lp, @li, @rn, @rp, @rd, @pl, @rc, @ra, @cp) AS x(pr, lp, li, rn, rp, rd, pl, rc, ra, cp)
                """, conn, tx);
            cmd.Parameters.AddWithValue("a", assetId);
            cmd.Parameters.Add(TextArray("pr", nbs.Select(x => x.Protocol == "cdp" ? "cdp" : "lldp")));
            cmd.Parameters.Add(TextArray("lp", nbs.Select(x => Trunc(x.LocalPort, 200))));
            cmd.Parameters.Add(Array("li", NpgsqlDbType.Integer, nbs.Select(x => x.LocalIfIndex)));
            cmd.Parameters.Add(TextArray("rn", nbs.Select(x => Trunc(x.RemoteName, 255))));
            cmd.Parameters.Add(TextArray("rp", nbs.Select(x => Trunc(x.RemotePort, 200))));
            cmd.Parameters.Add(TextArray("rd", nbs.Select(x => Trunc(x.RemoteDescription, 500))));
            cmd.Parameters.Add(TextArray("pl", nbs.Select(x => Trunc(x.RemotePlatform, 200))));
            cmd.Parameters.Add(TextArray("rc", nbs.Select(x => Trunc(x.RemoteChassisId, 100))));
            cmd.Parameters.Add(TextArray("ra", nbs.Select(x => Ip(x.RemoteAddress) ? x.RemoteAddress : null)));
            cmd.Parameters.Add(TextArray("cp", nbs.Select(x => x.Capabilities.Count == 0 ? null : string.Join(",", x.Capabilities))));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var vlans = n.Vlans.Where(v => v.Id is > 0 and < 4096).GroupBy(v => v.Id).Select(g => g.First()).ToList();
        if (vlans.Count > 0)
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO zentriq.network_vlans (asset_id, vlan_id, name) SELECT @a, x.i, x.n FROM unnest(@i, @n) AS x(i, n)
                """, conn, tx);
            cmd.Parameters.AddWithValue("a", assetId);
            cmd.Parameters.AddWithValue("i", vlans.Select(v => v.Id).ToArray());
            cmd.Parameters.Add(TextArray("n", vlans.Select(v => Trunc(v.Name, 100))));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var macs = n.MacTable.Select(m => (Mac: Mac(m.Mac), Vlan: m.Vlan ?? 0, m.IfIndex)).Where(m => m.Mac is not null)
            .GroupBy(m => (m.Mac, m.Vlan)).Select(g => g.First()).Take(16384).ToList();
        if (macs.Count > 0)
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO zentriq.network_mac_table (asset_id, mac, vlan, if_index) SELECT @a, x.m::macaddr, x.v, x.i FROM unnest(@m, @v, @i) AS x(m, v, i)
                """, conn, tx);
            cmd.Parameters.AddWithValue("a", assetId);
            cmd.Parameters.Add(TextArray("m", macs.Select(m => m.Mac)));
            cmd.Parameters.AddWithValue("v", macs.Select(m => m.Vlan).ToArray());
            cmd.Parameters.Add(Array("i", NpgsqlDbType.Integer, macs.Select(m => m.IfIndex)));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var arp = n.Arp.Select(a => (a.Ip, Mac: Mac(a.Mac), a.IfIndex)).Where(a => a.Mac is not null && Ip(a.Ip))
            .GroupBy(a => a.Ip).Select(g => g.First()).Take(8192).ToList();
        if (arp.Count > 0)
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO zentriq.network_arp (asset_id, ip, mac, if_index) SELECT @a, x.p::inet, x.m::macaddr, x.i FROM unnest(@p, @m, @i) AS x(p, m, i)
                """, conn, tx);
            cmd.Parameters.AddWithValue("a", assetId);
            cmd.Parameters.Add(TextArray("p", arp.Select(a => a.Ip)));
            cmd.Parameters.Add(TextArray("m", arp.Select(a => a.Mac)));
            cmd.Parameters.Add(Array("i", NpgsqlDbType.Integer, arp.Select(a => a.IfIndex)));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var mods = n.Modules.Take(512).ToList();
        if (mods.Count > 0)
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO zentriq.network_modules (asset_id, class, name, model, serial, firmware)
                SELECT @a, x.c, x.n, x.m, x.s, x.f FROM unnest(@c, @n, @m, @s, @f) AS x(c, n, m, s, f)
                """, conn, tx);
            cmd.Parameters.AddWithValue("a", assetId);
            cmd.Parameters.Add(TextArray("c", mods.Select(m => m.Class)));
            cmd.Parameters.Add(TextArray("n", mods.Select(m => Trunc(m.Name, 200))));
            cmd.Parameters.Add(TextArray("m", mods.Select(m => Trunc(m.Model, 200))));
            cmd.Parameters.Add(TextArray("s", mods.Select(m => Trunc(m.Serial, 100))));
            cmd.Parameters.Add(TextArray("f", mods.Select(m => Trunc(m.Firmware, 200))));
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Re-derives neighbour devices, uplinks, connected devices and suspected unmanaged switches for every network device.
    /// Runs after each collection and periodically (devices found later by agents or scans are linked to their port).
    /// </summary>
    /// <param name="assetId">Only the ports and neighbours of this device (after its collection, inside its transaction); null = all, serialised.</param>
    public static async Task RefreshAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct, Guid? assetId = null)
    {
        // The working table lives until the end of the transaction.
        var own = tx is null ? await conn.BeginTransactionAsync(ct) : null;
        await using var cmd = new NpgsqlCommand(RefreshSql, conn, tx ?? own) { CommandTimeout = 120 };
        cmd.Parameters.Add(new NpgsqlParameter("virt", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = VirtualOuis });
        cmd.Parameters.Add(new NpgsqlParameter("asset", NpgsqlDbType.Uuid) { Value = (object?)assetId ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
        if (own is not null) { await own.CommitAsync(ct); await own.DisposeAsync(); }
    }

    public const string RefreshSql = """
        -- A full refresh runs one at a time.
        SELECT CASE WHEN @asset::uuid IS NULL THEN pg_advisory_xact_lock(hashtext('zentriq.network.refresh')) END;
        -- Every MAC address known for an active device.
        CREATE TEMP TABLE IF NOT EXISTS zq_mac_owner (mac macaddr, asset_id uuid, infra boolean) ON COMMIT DROP;
        TRUNCATE zq_mac_owner;
        INSERT INTO zq_mac_owner
        SELECT DISTINCT ON (m.mac) m.mac, m.asset_id, m.infra FROM (
            SELECT ai.value::macaddr AS mac, a.id AS asset_id, a.category IN ('network_device','virtual_host') AS infra, 1 AS prio
              FROM zentriq.asset_identifiers ai JOIN zentriq.assets a ON a.id = ai.asset_id AND a.status = 'active'
             WHERE ai.kind = 'mac' AND ai.value ~ '^([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$'
            UNION ALL
            SELECT a.mac_address, a.id, a.category IN ('network_device','virtual_host'), 2 FROM zentriq.assets a WHERE a.status = 'active' AND a.mac_address IS NOT NULL
            UNION ALL
            SELECT nd.bridge_address::macaddr, nd.asset_id, true, 3 FROM zentriq.network_devices nd
             WHERE nd.bridge_address ~ '^([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$'
            UNION ALL
            SELECT ni.mac::macaddr, ni.asset_id, true, 4 FROM zentriq.network_interfaces ni
             WHERE ni.mac ~ '^([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$' AND ni.mac <> '00:00:00:00:00:00'
        ) m ORDER BY m.mac, m.prio;
        CREATE INDEX IF NOT EXISTS zq_mac_owner_mac ON zq_mac_owner (mac);

        -- Neighbour → known device: by management address, chassis MAC, or name.
        UPDATE zentriq.network_neighbors nb SET remote_asset_id = COALESCE(
            (SELECT a.id FROM zentriq.assets a WHERE a.status = 'active' AND nb.remote_address IS NOT NULL AND a.primary_ip = nb.remote_address LIMIT 1),
            (SELECT o.asset_id FROM zq_mac_owner o WHERE o.mac = CASE WHEN nb.remote_chassis_id ~* '^[0-9a-f]{12}$|^([0-9a-f]{2}[:-]){5}[0-9a-f]{2}$'
                 THEN (regexp_replace(nb.remote_chassis_id, '[^0-9A-Fa-f]', '', 'g'))::macaddr END LIMIT 1),
            (SELECT a.id FROM zentriq.assets a WHERE a.status = 'active' AND nb.remote_name IS NOT NULL AND nb.remote_name <> ''
                 AND (lower(a.hostname) = lower(split_part(nb.remote_name, '.', 1)) OR lower(a.fqdn) = lower(nb.remote_name)) LIMIT 1))
         WHERE @asset::uuid IS NULL OR nb.asset_id = @asset;

        -- Port derivations.
        UPDATE zentriq.network_interfaces ni SET
            mac_count = COALESCE(s.macs, 0),
            neighbor = nbr.label,
            neighbor_asset_id = nbr.remote_asset_id,
            uplink = COALESCE(nbr.infra, false) OR COALESCE(ni.trunk, false) OR COALESCE(s.infra, false),
            unmanaged_suspect = ni.type = 'ethernet' AND nbr.label IS NULL AND NOT COALESCE(ni.trunk, false)
                AND NOT COALESCE(s.infra, false) AND COALESCE(s.real_macs, 0) >= 2,
            connected = COALESCE(s.connected, '[]'::jsonb)
        FROM zentriq.network_interfaces base
        LEFT JOIN LATERAL (
            SELECT count(DISTINCT m.mac)::int AS macs,
                   bool_or(o.infra AND o.asset_id <> base.asset_id) AS infra,
                   count(DISTINCT m.mac) FILTER (WHERE (get_byte(decode(left(replace(m.mac::text, ':', ''), 2), 'hex'), 0) & 2) = 0
                        AND left(m.mac::text, 8) <> ALL (@virt))::int AS real_macs,
                   (SELECT jsonb_agg(c) FROM (
                        SELECT DISTINCT ON (a.id) jsonb_build_object('assetId', a.id, 'name', COALESCE(a.hostname, zentriq.asset_ipv4(a.id), host(a.primary_ip)),
                               'ip', COALESCE(zentriq.asset_ipv4(a.id), host(a.primary_ip), (SELECT host(r.ip) FROM zentriq.network_arp r WHERE r.mac = m2.mac LIMIT 1)),
                               'mac', m2.mac::text, 'type', COALESCE(a.device_type, a.category)) AS c
                          FROM zentriq.network_mac_table m2
                          JOIN zq_mac_owner o2 ON o2.mac = m2.mac AND o2.asset_id <> base.asset_id
                          JOIN zentriq.assets a ON a.id = o2.asset_id
                         WHERE m2.asset_id = base.asset_id AND m2.if_index = base.if_index
                         ORDER BY a.id LIMIT 10) q) AS connected
              FROM zentriq.network_mac_table m
              LEFT JOIN zq_mac_owner o ON o.mac = m.mac
             WHERE m.asset_id = base.asset_id AND m.if_index = base.if_index
        ) s ON true
        LEFT JOIN LATERAL (
            -- A neighbour that is network equipment (or does not say) makes the port an uplink; an IP phone does not.
            SELECT trim(COALESCE(nb.remote_name, nb.remote_chassis_id, '') || ' ' || COALESCE(nb.remote_port, '')) AS label, nb.remote_asset_id,
                   (nb.capabilities && ARRAY['bridge','router','wlan-ap','repeater']::text[] OR cardinality(nb.capabilities) = 0) AS infra
              FROM zentriq.network_neighbors nb
             WHERE nb.asset_id = base.asset_id AND nb.local_if_index = base.if_index
             ORDER BY 3 DESC, nb.remote_asset_id IS NULL, nb.protocol LIMIT 1
        ) nbr ON true
        WHERE ni.asset_id = base.asset_id AND ni.if_index = base.if_index AND (@asset::uuid IS NULL OR base.asset_id = @asset);
        """;

    /// <summary>Normalises a MAC address to aa:bb:cc:dd:ee:ff, or null.</summary>
    public static string? Mac(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var hex = new string(s.Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
        if (hex.Length != 12 || hex == "000000000000" || hex == "ffffffffffff") return null;
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }

    private static bool Ip(string? s) => s is not null && IPAddress.TryParse(s, out _);
    private static string? Trunc(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
    private static NpgsqlParameter Text(string name, string? value) => new(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };
    private static NpgsqlParameter Nullable<T>(string name, NpgsqlDbType type, T? value) where T : struct => new(name, type) { Value = value.HasValue ? value.Value : DBNull.Value };
    private static NpgsqlParameter Json<T>(string name, IEnumerable<T> value) =>
        new(name, NpgsqlDbType.Jsonb) { Value = System.Text.Json.JsonSerializer.Serialize(value.ToList()) };
    private static NpgsqlParameter TextArray(string name, IEnumerable<string?> values) =>
        new(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = values.ToArray() };
    private static NpgsqlParameter Array<T>(string name, NpgsqlDbType type, IEnumerable<T?> values) where T : struct =>
        new(name, NpgsqlDbType.Array | type) { Value = values.ToArray() };
}
