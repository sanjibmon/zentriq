using System.Net;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Application.Licensing;
using Zentriq.Domain;
using Zentriq.Infrastructure.Licensing;
using Zentriq.Inventory;

namespace Zentriq.Infrastructure.Inventory;

public sealed record MatchHints(Guid? PreferredAssetId = null, IPAddress? Ip = null, Guid? AgentId = null);
public sealed record ApplyResult(Guid? AssetId, bool Created, string? Note);

/// <summary>
/// Stores an inventory document (from the agent or an agentless collector) on the matching asset, so a
/// device found by a network scan, Active Directory and an agent ends up as ONE asset and counts once
/// against the license. Matching order: preferred asset → agent id → SMBIOS UUID → serial number →
/// MAC address → FQDN/hostname → IP address. New assets are created only within the license capacity.
/// </summary>
public sealed class InventoryStore(LicensingService licensing)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ApplyResult> ApplyAsync(NpgsqlConnection conn, InventoryDocument doc, MatchHints hints, LicenseStatus license, CancellationToken ct)
    {
        if (hints.Ip is { IsIPv4MappedToIPv6: true } mapped) hints = hints with { Ip = mapped.MapToIPv4() };
        // Hypervisors emulate laptop/desktop chassis and even batteries; a virtual machine is recorded as such.
        if (doc.Hardware.IsVirtual == true) doc.Device.Chassis = "virtual";
        await using var tx = await conn.BeginTransactionAsync(ct);
        var assetId = await MatchAsync(conn, tx, doc, hints, ct);
        var created = false;
        if (assetId is null)
        {
            var category = NormalizeCategory(doc.Device.Category);
            try
            {
                await licensing.EnsureCapacityAsync(conn, tx, Workloads.FromAssetCategory(category), 1, license, ct);
            }
            catch (WorkloadLimitExceededException ex)
            {
                await tx.RollbackAsync(ct);
                return new(null, false, "Asset not added: " + ex.Message);
            }
            assetId = Guid.NewGuid();
            await using var ins = new NpgsqlCommand("""
                INSERT INTO zentriq.assets (id, hostname, category, source, inventory_status, first_seen_at, last_seen_at)
                VALUES (@id, @h, @c, @s, 'success', now(), now())
                """, conn, tx);
            ins.Parameters.AddWithValue("id", assetId.Value);
            ins.Parameters.Add(Text("h", doc.Device.Hostname));
            ins.Parameters.AddWithValue("c", category);
            ins.Parameters.AddWithValue("s", doc.Source == "agent" ? "agent" : "discovery");
            await ins.ExecuteNonQueryAsync(ct);
            created = true;
        }

        // The previous inventory, to record what changed (hardware replaced, software installed / removed / updated).
        var (prevDoc, prevSoftware) = created ? (null, null) : await PreviousAsync(conn, tx, assetId.Value, ct);

        await UpdateAssetAsync(conn, tx, assetId.Value, doc, hints, ct);
        await UpsertHardwareAsync(conn, tx, assetId.Value, doc, ct);
        await ReplaceSoftwareAsync(conn, tx, assetId.Value, doc, ct);
        await UpsertDocumentAsync(conn, tx, assetId.Value, doc, ct);
        await AddIdentifiersAsync(conn, tx, assetId.Value, doc, hints, ct);
        await RecordChangesAsync(conn, tx, assetId.Value, InventoryChanges.Compare(prevDoc, prevSoftware, doc), doc.Source, ct);
        await tx.CommitAsync(ct);
        return new(assetId, created, null);
    }

    private static async Task<(InventoryDocument? Doc, List<InstalledSoftware>? Software)> PreviousAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, CancellationToken ct)
    {
        InventoryDocument? doc = null;
        await using (var cmd = new NpgsqlCommand("SELECT document::text FROM zentriq.asset_inventory_documents WHERE asset_id = @id", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", id);
            if (await cmd.ExecuteScalarAsync(ct) is string json)
                try { doc = JsonSerializer.Deserialize<InventoryDocument>(json, Json); } catch (JsonException) { doc = null; }
        }
        if (doc is null) return (null, null);
        var sw = new List<InstalledSoftware>();
        await using (var cmd = new NpgsqlCommand("SELECT name, version, publisher FROM zentriq.software_inventory WHERE asset_id = @id", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) sw.Add(new InstalledSoftware(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
        }
        return (doc, sw);
    }

    private static async Task RecordChangesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, List<InventoryChange> changes, string source, CancellationToken ct)
    {
        if (changes.Count == 0) return;
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO zentriq.asset_changes (asset_id, kind, change, component, item, old_value, new_value, source)
            SELECT @id, k, c, comp, i, o, n, @s FROM unnest(@k, @c, @comp, @i, @o, @n) AS t(k, c, comp, i, o, n)
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("s", source);
        cmd.Parameters.Add(new NpgsqlParameter("k", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = changes.Select(c => c.Kind).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = changes.Select(c => c.Change).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("comp", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = changes.Select(c => c.Component).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("i", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = changes.Select(c => Trunc(c.Item, 400)!).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("o", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = changes.Select(c => Trunc(c.OldValue, 400)).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = changes.Select(c => Trunc(c.NewValue, 400)).ToArray() });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<Guid?> MatchAsync(NpgsqlConnection conn, NpgsqlTransaction tx, InventoryDocument doc, MatchHints hints, CancellationToken ct)
    {
        if (hints.PreferredAssetId is { } pref && await ActiveAsync(conn, tx, pref, ct)) return pref;

        var ids = new List<(string Kind, string Value)>();
        if (hints.AgentId is { } agent) ids.Add(("agent_id", agent.ToString("D")));
        if (doc.Device.SmbiosUuid is { } uuid) ids.Add(("smbios_uuid", uuid));
        if (doc.Device.SerialNumber is { } serial && serial.Length >= 4) ids.Add(("serial", Key(doc.Device.Manufacturer, serial)));
        foreach (var mac in doc.Network.Select(n => n.Mac).Where(m => m is not null).Distinct()) ids.Add(("mac", mac!));
        foreach (var (kind, value) in ids)
        {
            await using var cmd = new NpgsqlCommand("""
                SELECT a.id FROM zentriq.asset_identifiers i JOIN zentriq.assets a ON a.id = i.asset_id
                 WHERE i.kind = @k AND i.value = @v AND a.status = 'active' LIMIT 1 FOR UPDATE OF a
                """, conn, tx);
            cmd.Parameters.AddWithValue("k", kind);
            cmd.Parameters.AddWithValue("v", value);
            if (await cmd.ExecuteScalarAsync(ct) is Guid g) return g;
        }

        var name = doc.Device.Fqdn ?? doc.Device.Hostname;
        await using (var byName = new NpgsqlCommand("""
            SELECT id FROM zentriq.assets
             WHERE status = 'active' AND @n::text IS NOT NULL
               AND (lower(fqdn) = lower(@n) OR lower(hostname) = lower(split_part(@n, '.', 1)))
             ORDER BY (lower(fqdn) = lower(@n)) DESC NULLS LAST, last_seen_at DESC LIMIT 1 FOR UPDATE
            """, conn, tx))
        {
            byName.Parameters.Add(Text("n", name));
            if (name is not null && await byName.ExecuteScalarAsync(ct) is Guid g) return g;
        }

        if (hints.Ip is { } ip)
        {
            await using var byIp = new NpgsqlCommand("SELECT id FROM zentriq.assets WHERE status = 'active' AND primary_ip = @ip ORDER BY last_seen_at DESC LIMIT 1 FOR UPDATE", conn, tx);
            byIp.Parameters.Add(new NpgsqlParameter("ip", NpgsqlDbType.Inet) { Value = ip });
            if (await byIp.ExecuteScalarAsync(ct) is Guid g) return g;
        }
        return null;
    }

    private static async Task<bool> ActiveAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT 1 FROM zentriq.assets WHERE id = @id AND status = 'active' FOR UPDATE", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task UpdateAssetAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, InventoryDocument doc, MatchHints hints, CancellationToken ct)
    {
        var ip = hints.Ip ?? doc.Network.SelectMany(n => n.Ipv4).Select(s => IPAddress.TryParse(s, out var a) ? a : null).FirstOrDefault(a => a is not null && !IPAddress.IsLoopback(a));
        var mac = doc.Network.Select(n => n.Mac).FirstOrDefault(m => m is not null);
        // Category from authoritative inventory replaces the port-based guess, but never an administrator's manual choice.
        await using var cmd = new NpgsqlCommand("""
            UPDATE zentriq.assets SET
                hostname = COALESCE(@h, hostname), fqdn = COALESCE(@f, fqdn), primary_ip = COALESCE(@ip, primary_ip),
                mac_address = COALESCE(@mac::macaddr, mac_address), serial_number = COALESCE(@sn, serial_number),
                os_family = COALESCE(@of, os_family), os_name = COALESCE(@on, os_name),
                category = CASE WHEN source <> 'manual' AND @c::text IS NOT NULL THEN @c ELSE category END,
                inventory_status = 'success', inventory_source = @src, last_inventory_at = now(), last_seen_at = now(),
                updated_at = now(), row_version = row_version + 1
             WHERE id = @id
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.Add(Text("h", doc.Device.Hostname));
        cmd.Parameters.Add(Text("f", doc.Device.Fqdn));
        cmd.Parameters.Add(new NpgsqlParameter("ip", NpgsqlDbType.Inet) { Value = (object?)ip ?? DBNull.Value });
        cmd.Parameters.Add(Text("mac", mac));
        cmd.Parameters.Add(Text("sn", doc.Device.SerialNumber));
        cmd.Parameters.Add(Text("of", doc.Os.Family is "windows" or "linux" or "macos" or "network" or "other" ? doc.Os.Family : null));
        cmd.Parameters.Add(Text("on", Trunc(doc.Os.Name, 200)));
        cmd.Parameters.Add(Text("c", doc.Device.Category is null ? null : NormalizeCategory(doc.Device.Category)));
        cmd.Parameters.AddWithValue("src", doc.Source);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpsertHardwareAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, InventoryDocument doc, CancellationToken ct)
    {
        var h = doc.Hardware;
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO zentriq.hardware_inventory (asset_id, vendor, model, cpu, cpu_cores, cpu_threads, ram_gb, firmware, disks, serial_number, smbios_uuid, chassis, source, collected_at)
            VALUES (@id, @v, @m, @cpu, @cores, @thr, @ram, @fw, @disks, @sn, @uuid, @ch, @src, now())
            ON CONFLICT (asset_id) DO UPDATE SET vendor = EXCLUDED.vendor, model = EXCLUDED.model, cpu = EXCLUDED.cpu, cpu_cores = EXCLUDED.cpu_cores,
                cpu_threads = EXCLUDED.cpu_threads, ram_gb = EXCLUDED.ram_gb, firmware = EXCLUDED.firmware, disks = EXCLUDED.disks,
                serial_number = EXCLUDED.serial_number, smbios_uuid = EXCLUDED.smbios_uuid, chassis = EXCLUDED.chassis, source = EXCLUDED.source, collected_at = now()
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.Add(Text("v", Trunc(doc.Device.Manufacturer, 200)));
        cmd.Parameters.Add(Text("m", Trunc(doc.Device.Model, 200)));
        cmd.Parameters.Add(Text("cpu", Trunc(h.CpuName, 200)));
        cmd.Parameters.Add(new NpgsqlParameter("cores", NpgsqlDbType.Integer) { Value = (object?)h.CpuCores ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("thr", NpgsqlDbType.Integer) { Value = (object?)h.CpuThreads ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("ram", NpgsqlDbType.Numeric) { Value = h.MemoryBytes is { } b ? (object)Math.Round(b / 1073741824m, 2) : DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("fw", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(new { h.BiosVendor, h.BiosVersion, h.BiosDate, h.TpmPresent, h.TpmVersion, h.IsVirtual }, Json) });
        cmd.Parameters.Add(new NpgsqlParameter("disks", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(new { h.Disks, h.Volumes }, Json) });
        cmd.Parameters.Add(Text("sn", doc.Device.SerialNumber));
        cmd.Parameters.Add(Text("uuid", doc.Device.SmbiosUuid));
        cmd.Parameters.Add(Text("ch", doc.Device.Chassis));
        cmd.Parameters.AddWithValue("src", doc.Source);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ReplaceSoftwareAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, InventoryDocument doc, CancellationToken ct)
    {
        await using (var del = new NpgsqlCommand("DELETE FROM zentriq.software_inventory WHERE asset_id = @id", conn, tx))
        {
            del.Parameters.AddWithValue("id", id);
            await del.ExecuteNonQueryAsync(ct);
        }
        var items = doc.Software.Where(s => !string.IsNullOrWhiteSpace(s.Name)).Take(10_000).ToList();
        if (items.Count == 0) return;
        await using var ins = new NpgsqlCommand("""
            INSERT INTO zentriq.software_inventory (asset_id, name, publisher, version, installed_on, architecture, source, collected_at)
            SELECT @id, n, p, v, d, a, s, now() FROM unnest(@n, @p, @v, @d, @a, @s) AS t(n, p, v, d, a, s)
            """, conn, tx);
        ins.Parameters.AddWithValue("id", id);
        ins.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = items.Select(i => Trunc(i.Name, 300)!).ToArray() });
        ins.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = items.Select(i => Trunc(i.Publisher, 200)).ToArray() });
        ins.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = items.Select(i => Trunc(i.Version, 100)).ToArray() });
        ins.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Array | NpgsqlDbType.Date) { Value = items.Select(i => i.InstallDate is { } d ? DateOnly.FromDateTime(d.UtcDateTime) : (DateOnly?)null).ToArray() });
        ins.Parameters.Add(new NpgsqlParameter("a", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = items.Select(i => i.Architecture).ToArray() });
        ins.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = items.Select(i => i.Source ?? doc.Source).ToArray() });
        await ins.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpsertDocumentAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, InventoryDocument doc, CancellationToken ct)
    {
        // Software is stored in its own table; keep the document compact.
        var copy = JsonSerializer.SerializeToNode(doc, Json)!.AsObject();
        copy.Remove("software");
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO zentriq.asset_inventory_documents (asset_id, source, collected_at, document) VALUES (@id, @s, @t, @d)
            ON CONFLICT (asset_id) DO UPDATE SET source = EXCLUDED.source, collected_at = EXCLUDED.collected_at, document = EXCLUDED.document
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("s", doc.Source);
        cmd.Parameters.AddWithValue("t", doc.CollectedAt.ToUniversalTime());
        cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Jsonb) { Value = copy.ToJsonString() });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task AddIdentifiersAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, InventoryDocument doc, MatchHints hints, CancellationToken ct)
    {
        var ids = new List<(string, string)>();
        if (hints.AgentId is { } a) ids.Add(("agent_id", a.ToString("D")));
        if (doc.Device.SmbiosUuid is { } u) ids.Add(("smbios_uuid", u));
        if (doc.Device.SerialNumber is { } s && s.Length >= 4) ids.Add(("serial", Key(doc.Device.Manufacturer, s)));
        foreach (var m in doc.Network.Select(n => n.Mac).Where(m => m is not null).Distinct().Take(16)) ids.Add(("mac", m!));
        if (doc.Device.Fqdn is { } f) ids.Add(("hostname_fqdn", f.ToLowerInvariant()));
        foreach (var (kind, value) in ids)
        {
            await using var cmd = new NpgsqlCommand("INSERT INTO zentriq.asset_identifiers (asset_id, kind, value) VALUES (@a, @k, @v) ON CONFLICT (kind, value) DO NOTHING", conn, tx);
            cmd.Parameters.AddWithValue("a", id);
            cmd.Parameters.AddWithValue("k", kind);
            cmd.Parameters.AddWithValue("v", Trunc(value, 300)!);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>Serial numbers are only unique per manufacturer.</summary>
    private static string Key(string? manufacturer, string serial) => $"{(manufacturer ?? "").Trim().ToLowerInvariant()}|{serial.Trim().ToUpperInvariant()}";

    public static string NormalizeCategory(string? c) => c is "endpoint" or "server" or "network_device" or "printer" or "iot" or "virtual_host" or "other" ? c : "other";
    private static string? Trunc(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
    private static NpgsqlParameter Text(string name, string? value) => new(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };
}
