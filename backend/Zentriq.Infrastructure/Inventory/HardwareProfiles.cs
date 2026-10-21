using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Inventory;

namespace Zentriq.Infrastructure.Inventory;

/// <summary>
/// Manufacturer, model and serial number entered by hand for devices that do not report them. A detected value always
/// wins; the manual value fills the gap. What was entered is also kept as a hardware profile keyed by the device's fixed
/// identities (SMBIOS UUID and network adapter MAC addresses), so it is applied again automatically when the agent or the
/// operating system is reinstalled, or the device is deleted and found again.
/// </summary>
public static class HardwareProfiles
{
    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "to be filled by o.e.m.", "to be filled by oem", "default string", "system serial number", "system manufacturer", "system product name",
        "system version", "not specified", "not applicable", "none", "n/a", "na", "oem", "o.e.m.", "chassis serial number", "base board serial number",
        "invalid", "unknown", "empty", "serial", "123456789", "0123456789", "xxxxxxxxxx", "type1productconfigid", "type1family", "sku",
    };
    private static readonly Regex SameChar = new(@"^(.)\1*$", RegexOptions.Compiled);

    /// <summary>The value, or null when it is empty or a firmware placeholder (same rules as SQL zentriq.hw_value).</summary>
    public static string? Clean(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var t = v.Trim();
        if (Placeholders.Contains(t) || SameChar.IsMatch(t)) return null;
        return t.Length > 200 ? t[..200] : t;
    }

    public sealed record Identity(string Kind, string Value);

    /// <summary>The fixed identities of a device as recorded from its inventories.</summary>
    public static async Task<List<Identity>> IdentitiesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid assetId, CancellationToken ct)
    {
        var list = new List<Identity>();
        await using var cmd = new NpgsqlCommand("SELECT kind, value FROM zentriq.asset_identifiers WHERE asset_id = @a AND kind IN ('smbios_uuid','mac') ORDER BY kind, value", conn, tx);
        cmd.Parameters.AddWithValue("a", assetId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new Identity(r.GetString(0), r.GetString(1)));
        return list;
    }

    /// <summary>
    /// Saves what was entered for a device (null clears a value) on the device and in its hardware profile, keyed by the
    /// device's fixed identities. Returns the number of identities the profile is linked to.
    /// </summary>
    public static async Task<int> SaveAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid assetId, string? manufacturer, string? model, string? serial, string? by, CancellationToken ct)
    {
        manufacturer = Trim(manufacturer); model = Trim(model); serial = Trim(serial);
        await using (var upd = new NpgsqlCommand("""
            UPDATE zentriq.assets SET manual_manufacturer = @mf, manual_model = @md, manual_serial = @sn, updated_at = now(), row_version = row_version + 1
             WHERE id = @a
            """, conn, tx))
        {
            upd.Parameters.AddWithValue("a", assetId);
            upd.Parameters.Add(Text("mf", manufacturer));
            upd.Parameters.Add(Text("md", model));
            upd.Parameters.Add(Text("sn", serial));
            await upd.ExecuteNonQueryAsync(ct);
        }
        var ids = await IdentitiesAsync(conn, tx, assetId, ct);
        var shared = new List<Identity>();
        foreach (var x in ids) if (x.Kind == "smbios_uuid" && !await UniqueUuidAsync(conn, tx, x.Value, ct)) shared.Add(x);
        ids.RemoveAll(shared.Contains);
        // One profile per device: reuse the profile any of its identities already points to.
        Guid? profile = null;
        foreach (var id in ids)
        {
            await using var find = new NpgsqlCommand("SELECT profile_id FROM zentriq.hardware_profile_identities WHERE kind = @k AND value = @v", conn, tx);
            find.Parameters.AddWithValue("k", id.Kind);
            find.Parameters.AddWithValue("v", id.Value);
            if (await find.ExecuteScalarAsync(ct) is Guid g) { profile = g; break; }
        }
        if (profile is null)
        {
            await using var byAsset = new NpgsqlCommand("SELECT id FROM zentriq.hardware_profiles WHERE asset_id = @a ORDER BY updated_at DESC LIMIT 1", conn, tx);
            byAsset.Parameters.AddWithValue("a", assetId);
            profile = await byAsset.ExecuteScalarAsync(ct) as Guid?;
        }
        if (manufacturer is null && model is null && serial is null)
        {
            if (profile is { } p)
            {
                await using var del = new NpgsqlCommand("DELETE FROM zentriq.hardware_profiles WHERE id = @p", conn, tx);
                del.Parameters.AddWithValue("p", p);
                await del.ExecuteNonQueryAsync(ct);
            }
            return 0;
        }
        profile ??= Guid.NewGuid();
        await using (var up = new NpgsqlCommand("""
            INSERT INTO zentriq.hardware_profiles (id, manufacturer, model, serial_number, asset_id, hostname, updated_by)
            VALUES (@p, @mf, @md, @sn, @a, (SELECT hostname FROM zentriq.assets WHERE id = @a), @by)
            ON CONFLICT (id) DO UPDATE SET manufacturer = EXCLUDED.manufacturer, model = EXCLUDED.model, serial_number = EXCLUDED.serial_number,
                asset_id = EXCLUDED.asset_id, hostname = EXCLUDED.hostname, updated_by = EXCLUDED.updated_by, updated_at = now()
            """, conn, tx))
        {
            up.Parameters.AddWithValue("p", profile.Value);
            up.Parameters.AddWithValue("a", assetId);
            up.Parameters.Add(Text("mf", manufacturer));
            up.Parameters.Add(Text("md", model));
            up.Parameters.Add(Text("sn", serial));
            up.Parameters.Add(Text("by", by));
            await up.ExecuteNonQueryAsync(ct);
        }
        foreach (var id in ids)
        {
            await using var link = new NpgsqlCommand("""
                INSERT INTO zentriq.hardware_profile_identities (profile_id, kind, value) VALUES (@p, @k, @v)
                ON CONFLICT (kind, value) DO UPDATE SET profile_id = EXCLUDED.profile_id
                """, conn, tx);
            link.Parameters.AddWithValue("p", profile.Value);
            link.Parameters.AddWithValue("k", id.Kind);
            link.Parameters.AddWithValue("v", id.Value);
            await link.ExecuteNonQueryAsync(ct);
        }
        return ids.Count;
    }

    /// <summary>
    /// After an inventory: a device without manual values whose identities match a saved profile gets the profile's
    /// values back. When the profile knows the SMBIOS UUID, the device must have the same one (a USB network adapter can
    /// move between machines); otherwise one matching MAC address is enough. Returns true when values were applied.
    /// </summary>
    public static async Task<bool> RestoreAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid assetId, InventoryDocument doc, CancellationToken ct)
    {
        await using (var has = new NpgsqlCommand("SELECT manual_manufacturer IS NOT NULL OR manual_model IS NOT NULL OR manual_serial IS NOT NULL FROM zentriq.assets WHERE id = @a", conn, tx))
        {
            has.Parameters.AddWithValue("a", assetId);
            if (await has.ExecuteScalarAsync(ct) is true) return false;
        }
        var uuid = doc.Device.SmbiosUuid;
        // A UUID that other devices also report (cloned VMs, boards with a placeholder UUID) does not identify this device.
        var trusted = uuid is not null && await UniqueUuidAsync(conn, tx, uuid, ct) ? uuid : null;
        var macs = doc.Network.Select(n => n.Mac).Where(m => m is not null).Select(m => m!).Distinct().ToArray();
        if (uuid is null && macs.Length == 0) return false;
        await using var find = new NpgsqlCommand("""
            SELECT p.id, p.manufacturer, p.model, p.serial_number
              FROM zentriq.hardware_profiles p
             WHERE EXISTS (SELECT 1 FROM zentriq.hardware_profile_identities i WHERE i.profile_id = p.id
                            AND ((i.kind = 'smbios_uuid' AND i.value = @t) OR (i.kind = 'mac' AND i.value = ANY(@m))))
               AND (@u::text IS NULL OR NOT EXISTS (SELECT 1 FROM zentriq.hardware_profile_identities i WHERE i.profile_id = p.id AND i.kind = 'smbios_uuid')
                    OR EXISTS (SELECT 1 FROM zentriq.hardware_profile_identities i WHERE i.profile_id = p.id AND i.kind = 'smbios_uuid' AND i.value = @u))
             ORDER BY p.updated_at DESC LIMIT 1
            """, conn, tx);
        find.Parameters.Add(Text("u", uuid));
        find.Parameters.Add(Text("t", trusted));
        find.Parameters.Add(new NpgsqlParameter("m", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = macs });
        Guid profile; string? mf, md, sn;
        await using (var r = await find.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return false;
            profile = r.GetGuid(0);
            mf = r.IsDBNull(1) ? null : r.GetString(1);
            md = r.IsDBNull(2) ? null : r.GetString(2);
            sn = r.IsDBNull(3) ? null : r.GetString(3);
        }
        await using (var upd = new NpgsqlCommand("UPDATE zentriq.assets SET manual_manufacturer = @mf, manual_model = @md, manual_serial = @sn WHERE id = @a", conn, tx))
        {
            upd.Parameters.AddWithValue("a", assetId);
            upd.Parameters.Add(Text("mf", mf));
            upd.Parameters.Add(Text("md", md));
            upd.Parameters.Add(Text("sn", sn));
            await upd.ExecuteNonQueryAsync(ct);
        }
        await using (var link = new NpgsqlCommand("UPDATE zentriq.hardware_profiles SET asset_id = @a WHERE id = @p", conn, tx))
        {
            link.Parameters.AddWithValue("a", assetId);
            link.Parameters.AddWithValue("p", profile);
            await link.ExecuteNonQueryAsync(ct);
        }
        return true;
    }

    /// <summary>True when no more than one device record reports this SMBIOS UUID and it is not a placeholder.</summary>
    private static async Task<bool> UniqueUuidAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string uuid, CancellationToken ct)
    {
        var hex = uuid.Replace("-", "");
        if (hex.Length == 0 || SameChar.IsMatch(hex) || hex.Equals("03000200040005000006000700080009", StringComparison.OrdinalIgnoreCase)) return false;
        await using var cmd = new NpgsqlCommand("""
            SELECT count(DISTINCT i.asset_id) FROM zentriq.asset_identifiers i JOIN zentriq.assets a ON a.id = i.asset_id AND a.status <> 'retired'
             WHERE i.kind = 'smbios_uuid' AND i.value = @u
            """, conn, tx);
        cmd.Parameters.AddWithValue("u", uuid);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) <= 1;
    }

    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length > 200 ? s.Trim()[..200] : s.Trim();
    private static NpgsqlParameter Text(string n, string? v) => new(n, NpgsqlDbType.Text) { Value = (object?)v ?? DBNull.Value };
}
