using Npgsql;

namespace Zentriq.Infrastructure.Inventory;

/// <summary>
/// Finds devices recorded more than once (same host name, serial number or network adapter) and merges them into one
/// record. The newest information wins: the record kept carries the most recent address, name and last-seen time.
/// When an older record holds the device's collected inventory (agent or credentialed scan), that record is the one
/// kept, so hardware/software history is not lost. Merged records stay as "duplicate" (hidden, pointing to the kept one).
/// </summary>
public static class DuplicateAssets
{
    public sealed record Member(Guid Id, string? Hostname, string? Ip, string? Serial, string? Mac, string Source, string InventoryStatus,
        DateTimeOffset LastSeenAt, DateTimeOffset FirstSeenAt, string? DeviceType, bool HasAgent);

    public sealed record Group(Member Keep, IReadOnlyList<Member> Merge, IReadOnlyList<string> Reasons);

    private static readonly string[] GenericNames = ["localhost", "unknown", "android", "iphone", "ipad", "printer", "default", "none"];

    /// <summary>All groups of active assets that are the same device.</summary>
    public static async Task<List<Group>> FindAsync(NpgsqlConnection conn, CancellationToken ct, NpgsqlTransaction? tx = null)
    {
        var members = new Dictionary<Guid, Member>();
        var edges = new List<(Guid A, Guid B, string Reason)>();
        await using (var cmd = new NpgsqlCommand("""
            WITH a AS (
                SELECT x.id, x.hostname, host(x.primary_ip) AS ip, x.serial_number, x.mac_address, x.source, x.inventory_status, x.last_seen_at, x.first_seen_at, x.device_type,
                       NULLIF(lower(split_part(COALESCE(NULLIF(btrim(x.hostname), ''), x.fqdn), '.', 1)), '') AS h,
                       CASE WHEN length(btrim(x.serial_number)) >= 5
                             AND upper(btrim(x.serial_number)) NOT IN ('TO BE FILLED BY O.E.M.','DEFAULT STRING','SYSTEM SERIAL NUMBER','CHASSIS SERIAL NUMBER',
                                                                       'NOT SPECIFIED','0123456789','123456789','NONE','N/A','NOT AVAILABLE','UNKNOWN','INVALID')
                            THEN upper(btrim(x.serial_number)) END AS sn,
                       -- Globally unique adapters only: locally administered addresses (VPN, Npcap, randomised Wi-Fi) repeat across devices.
                       CASE WHEN x.mac_address IS NOT NULL AND x.mac_address <> '00:00:00:00:00:00'::macaddr
                             AND (('x' || substr(replace(x.mac_address::text, ':', ''), 1, 2))::bit(8)::int & 3) = 0
                            THEN x.mac_address END AS mac,
                       EXISTS (SELECT 1 FROM zentriq.agent_registrations r WHERE r.asset_id = x.id AND r.status = 'active') AS has_agent,
                       EXISTS (SELECT 1 FROM zentriq.agent_registrations r WHERE r.asset_id = x.id AND r.status = 'active'
                                  AND r.last_checkin_at > now() - interval '3 days') AS live_agent
                  FROM zentriq.assets x WHERE x.status = 'active')
            SELECT x.id, y.id,
                   CASE WHEN x.sn IS NOT NULL AND x.sn = y.sn THEN 'same serial number ' || x.sn
                        WHEN x.mac IS NOT NULL AND x.mac = y.mac THEN 'same network adapter ' || upper(x.mac::text)
                        ELSE 'same name ' || x.h END,
                   x.hostname, x.ip, x.serial_number, x.mac_address::text, x.source, x.inventory_status, x.last_seen_at, x.first_seen_at, x.device_type, x.has_agent,
                   y.hostname, y.ip, y.serial_number, y.mac_address::text, y.source, y.inventory_status, y.last_seen_at, y.first_seen_at, y.device_type, y.has_agent
              FROM a x JOIN a y ON x.id < y.id
               AND ((x.h IS NOT NULL AND x.h = y.h AND NOT (x.h = ANY(@generic)))
                    OR (x.sn IS NOT NULL AND x.sn = y.sn)
                    OR (x.mac IS NOT NULL AND x.mac = y.mac))
             -- Two different serial numbers, or two agents both checking in, are two devices that share a name.
             WHERE NOT (x.sn IS NOT NULL AND y.sn IS NOT NULL AND x.sn <> y.sn)
               AND NOT (x.live_agent AND y.live_agent)
             LIMIT 20000
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("generic", GenericNames);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                Member Read(int o, Guid id) => new(id, S(r, o), S(r, o + 1), S(r, o + 2), S(r, o + 3), r.GetString(o + 4), r.GetString(o + 5),
                    r.GetFieldValue<DateTimeOffset>(o + 6), r.GetFieldValue<DateTimeOffset>(o + 7), S(r, o + 8), r.GetBoolean(o + 9));
                var a = r.GetGuid(0);
                var b = r.GetGuid(1);
                members[a] = Read(3, a);
                members[b] = Read(13, b);
                edges.Add((a, b, r.GetString(2)));
            }
        }

        // Union-find: A~B and B~C are one device.
        var parent = members.Keys.ToDictionary(k => k, k => k);
        Guid Root(Guid g) { while (parent[g] != g) { parent[g] = parent[parent[g]]; g = parent[g]; } return g; }
        foreach (var (a, b, _) in edges) { var ra = Root(a); var rb = Root(b); if (ra != rb) parent[ra] = rb; }
        var groups = new List<Group>();
        foreach (var set in members.Values.GroupBy(m => Root(m.Id)))
        {
            var list = set.ToList();
            var keep = ChooseKeep(list);
            var ids = list.Select(m => m.Id).ToHashSet();
            var reasons = edges.Where(e => ids.Contains(e.A)).Select(e => e.Reason).Distinct().ToList();
            groups.Add(new Group(keep, list.Where(m => m.Id != keep.Id).OrderByDescending(m => m.LastSeenAt).ToList(), reasons));
        }
        return groups.OrderByDescending(g => g.Keep.LastSeenAt).ToList();
    }

    /// <summary>The record to keep: one with collected inventory if any (its history stays), then the most recently seen.</summary>
    public static Member ChooseKeep(IReadOnlyCollection<Member> list) =>
        list.OrderByDescending(m => m.InventoryStatus == "success")
            .ThenByDescending(m => m.HasAgent)
            .ThenByDescending(m => m.LastSeenAt)
            .ThenByDescending(m => m.FirstSeenAt)
            .First();

    /// <summary>
    /// Merges <paramref name="duplicates"/> into <paramref name="keep"/> in one transaction: the kept record takes the newest
    /// address and name, missing details (serial, adapter, department, owner, model) and the history (identifiers, agent,
    /// remote support link, actions, changes) of the others.
    /// </summary>
    public static async Task<int> MergeAsync(NpgsqlConnection conn, Guid keep, IReadOnlyCollection<Guid> duplicates, CancellationToken ct)
    {
        var dups = duplicates.Where(d => d != keep).Distinct().ToArray();
        if (dups.Length == 0) return 0;
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var check = new NpgsqlCommand("SELECT count(*) FROM zentriq.assets WHERE id = @k AND status = 'active'", conn, tx))
        {
            check.Parameters.AddWithValue("k", keep);
            if ((long)(await check.ExecuteScalarAsync(ct))! == 0) throw new InvalidOperationException("The device to keep is not active.");
        }
        await using var cmd = new NpgsqlCommand("""
            WITH d AS (SELECT * FROM zentriq.assets WHERE id = ANY(@d) AND status = 'active'),
                 newest AS (SELECT * FROM d ORDER BY last_seen_at DESC LIMIT 1)
            UPDATE zentriq.assets k SET
                primary_ip   = CASE WHEN n.last_seen_at > k.last_seen_at THEN COALESCE(n.primary_ip, k.primary_ip) ELSE COALESCE(k.primary_ip, n.primary_ip) END,
                hostname     = CASE WHEN n.last_seen_at > k.last_seen_at AND k.inventory_status <> 'success' THEN COALESCE(n.hostname, k.hostname) ELSE COALESCE(k.hostname, n.hostname) END,
                fqdn         = COALESCE(k.fqdn, n.fqdn),
                last_seen_at = GREATEST(k.last_seen_at, n.last_seen_at),
                first_seen_at = LEAST(k.first_seen_at, (SELECT min(first_seen_at) FROM d)),
                mac_address  = COALESCE(k.mac_address, (SELECT mac_address FROM d WHERE mac_address IS NOT NULL ORDER BY last_seen_at DESC LIMIT 1)),
                serial_number = COALESCE(k.serial_number, (SELECT serial_number FROM d WHERE serial_number IS NOT NULL ORDER BY last_seen_at DESC LIMIT 1)),
                department_id = COALESCE(k.department_id, (SELECT department_id FROM d WHERE department_id IS NOT NULL ORDER BY last_seen_at DESC LIMIT 1)),
                manual_manufacturer = COALESCE(k.manual_manufacturer, (SELECT manual_manufacturer FROM d WHERE manual_manufacturer IS NOT NULL ORDER BY last_seen_at DESC LIMIT 1)),
                manual_model  = COALESCE(k.manual_model, (SELECT manual_model FROM d WHERE manual_model IS NOT NULL ORDER BY last_seen_at DESC LIMIT 1)),
                manual_serial = COALESCE(k.manual_serial, (SELECT manual_serial FROM d WHERE manual_serial IS NOT NULL ORDER BY last_seen_at DESC LIMIT 1)),
                location_id   = COALESCE(k.location_id, (SELECT location_id FROM d WHERE location_id IS NOT NULL ORDER BY last_seen_at DESC LIMIT 1)),
                owner_name   = COALESCE(k.owner_name, (SELECT owner_name FROM d WHERE owner_name IS NOT NULL ORDER BY last_seen_at DESC LIMIT 1)),
                site_id      = COALESCE(k.site_id, (SELECT site_id FROM d WHERE site_id IS NOT NULL ORDER BY last_seen_at DESC LIMIT 1)),
                manufacturer = COALESCE(k.manufacturer, (SELECT manufacturer FROM d WHERE manufacturer IS NOT NULL ORDER BY last_seen_at DESC LIMIT 1)),
                model        = COALESCE(k.model, (SELECT model FROM d WHERE model IS NOT NULL ORDER BY last_seen_at DESC LIMIT 1)),
                device_type  = CASE WHEN NOT k.device_type_manual AND EXISTS (SELECT 1 FROM d WHERE device_type_manual)
                                    THEN (SELECT device_type FROM d WHERE device_type_manual ORDER BY last_seen_at DESC LIMIT 1)
                                    ELSE COALESCE(k.device_type, n.device_type) END,
                device_type_manual = k.device_type_manual OR EXISTS (SELECT 1 FROM d WHERE device_type_manual),
                updated_at = now(), row_version = k.row_version + 1
              FROM newest n
             WHERE k.id = @k;

            UPDATE zentriq.asset_identifiers SET asset_id = @k WHERE asset_id = ANY(@d);
            UPDATE zentriq.agent_registrations SET asset_id = @k WHERE asset_id = ANY(@d);
            UPDATE zentriq.remote_nodes SET asset_id = @k WHERE asset_id = ANY(@d);
            UPDATE zentriq.remote_sessions SET asset_id = @k WHERE asset_id = ANY(@d);
            UPDATE zentriq.asset_actions SET asset_id = @k WHERE asset_id = ANY(@d);
            UPDATE zentriq.asset_changes SET asset_id = @k WHERE asset_id = ANY(@d);
            -- Asset Lifecycle: the kept record takes the tag, custodian, purchase, warranty and contracts it does not have.
            WITH src AS (SELECT l.* FROM zentriq.asset_lifecycle l JOIN zentriq.assets a ON a.id = l.asset_id
                          WHERE l.asset_id = ANY(@d) ORDER BY (l.asset_tag IS NOT NULL) DESC, (l.purchase_cost IS NOT NULL) DESC, a.last_seen_at DESC LIMIT 1)
            UPDATE zentriq.asset_lifecycle k SET
                assigned_to = COALESCE(k.assigned_to, (SELECT assigned_to FROM src)), assigned_email = COALESCE(k.assigned_email, (SELECT assigned_email FROM src)),
                assigned_at = COALESCE(k.assigned_at, (SELECT assigned_at FROM src)),
                status = CASE WHEN k.stage = 'in_use' AND k.assigned_to IS NULL AND (SELECT assigned_to FROM src) IS NOT NULL THEN 'assigned' ELSE k.status END,
                purchase_date = COALESCE(k.purchase_date, (SELECT purchase_date FROM src)), purchase_cost = COALESCE(k.purchase_cost, (SELECT purchase_cost FROM src)),
                currency = COALESCE(k.currency, (SELECT currency FROM src)), vendor = COALESCE(k.vendor, (SELECT vendor FROM src)),
                invoice_number = COALESCE(k.invoice_number, (SELECT invoice_number FROM src)), po_number = COALESCE(k.po_number, (SELECT po_number FROM src)),
                warranty_provider = COALESCE(k.warranty_provider, (SELECT warranty_provider FROM src)), warranty_type = COALESCE(k.warranty_type, (SELECT warranty_type FROM src)),
                warranty_start = COALESCE(k.warranty_start, (SELECT warranty_start FROM src)), warranty_end = COALESCE(k.warranty_end, (SELECT warranty_end FROM src)),
                notes = COALESCE(k.notes, (SELECT notes FROM src))
             WHERE k.asset_id = @k AND EXISTS (SELECT 1 FROM src);
            INSERT INTO zentriq.asset_contract_assets (contract_id, asset_id) SELECT contract_id, @k FROM zentriq.asset_contract_assets WHERE asset_id = ANY(@d) ON CONFLICT DO NOTHING;
            UPDATE zentriq.asset_lifecycle_events SET asset_id = @k WHERE asset_id = ANY(@d);
            UPDATE zentriq.assets SET merged_into = @k WHERE merged_into = ANY(@d);
            UPDATE zentriq.assets SET status = 'duplicate', merged_into = @k, updated_at = now(), row_version = row_version + 1
             WHERE id = ANY(@d) AND status = 'active';
            """, conn, tx);
        cmd.Parameters.AddWithValue("k", keep);
        cmd.Parameters.AddWithValue("d", dups);
        await cmd.ExecuteNonQueryAsync(ct);
        // The asset tag moves to the kept record when it has none (two steps: tags are unique).
        await using (var tag = new NpgsqlCommand("""
            SELECT asset_tag FROM zentriq.asset_lifecycle WHERE asset_id = ANY(@d) AND asset_tag IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM zentriq.asset_lifecycle WHERE asset_id = @k AND asset_tag IS NOT NULL) LIMIT 1
            """, conn, tx))
        {
            tag.Parameters.AddWithValue("k", keep);
            tag.Parameters.AddWithValue("d", dups);
            if (await tag.ExecuteScalarAsync(ct) is string t)
            {
                await using var move = new NpgsqlCommand("""
                    UPDATE zentriq.asset_lifecycle SET asset_tag = NULL WHERE asset_id = ANY(@d) AND lower(asset_tag) = lower(@t);
                    UPDATE zentriq.asset_lifecycle SET asset_tag = @t WHERE asset_id = @k;
                    """, conn, tx);
                move.Parameters.AddWithValue("k", keep);
                move.Parameters.AddWithValue("d", dups);
                move.Parameters.AddWithValue("t", t);
                await move.ExecuteNonQueryAsync(ct);
            }
        }
        await tx.CommitAsync(ct);
        return dups.Length;
    }

    /// <summary>Permanently deletes assets (and records merged into them) with their inventory, findings and history.</summary>
    public static async Task<int> DeleteAsync(NpgsqlConnection conn, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return 0;
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            WITH RECURSIVE gone AS (
                SELECT id FROM zentriq.assets WHERE id = ANY(@ids)
                UNION SELECT a.id FROM zentriq.assets a JOIN gone g ON a.merged_into = g.id)
            DELETE FROM zentriq.assets WHERE id IN (SELECT id FROM gone)
            """, conn, tx);
        cmd.Parameters.AddWithValue("ids", ids.Distinct().ToArray());
        var n = await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return n;
    }

    private static string? S(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetValue(i).ToString();
}
