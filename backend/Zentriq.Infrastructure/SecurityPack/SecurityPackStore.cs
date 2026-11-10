using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Infrastructure.Deployment;
using Zentriq.Infrastructure.Secrets;
using Zentriq.Inventory;

namespace Zentriq.Infrastructure.SecurityPack;

/// <summary>
/// Security Pack policies (privilege, BitLocker, USB) for all devices and per device group, the policy each device gets,
/// and what devices report: local administrators, BitLocker status and recovery passwords (encrypted with the
/// installation's credential key), USB devices and events, elevations.
/// </summary>
public static class SecurityPackStore
{
    public static readonly string[] Areas = ["privilege", "bitlocker", "usb"];
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const string RevisionKey = "securitypack.revision";

    public sealed record AreaPolicy(Guid? GroupId, string? GroupName, JsonElement Settings, string? UpdatedBy, DateTimeOffset UpdatedAt);

    public static async Task<long> RevisionAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT COALESCE((value ->> 'revision')::bigint, 1) FROM zentriq.system_settings WHERE key = @k", conn);
        cmd.Parameters.AddWithValue("k", RevisionKey);
        return await cmd.ExecuteScalarAsync(ct) is long r ? r : 1;
    }

    /// <summary>A change that devices must pick up: they are woken through their live connection.</summary>
    public static async Task<long> BumpAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO zentriq.system_settings (key, value, updated_at) VALUES (@k, '{"revision": 2}', now())
            ON CONFLICT (key) DO UPDATE SET value = jsonb_build_object('revision', COALESCE((zentriq.system_settings.value ->> 'revision')::bigint, 1) + 1), updated_at = now()
            RETURNING (value ->> 'revision')::bigint
            """, conn);
        cmd.Parameters.AddWithValue("k", RevisionKey);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public static async Task<List<AreaPolicy>> PoliciesAsync(NpgsqlConnection conn, string area, CancellationToken ct)
    {
        var list = new List<AreaPolicy>();
        await using var cmd = new NpgsqlCommand("""
            SELECT p.group_id, g.name, p.settings::text, p.updated_by, p.updated_at FROM zentriq.secpack_policies p
              LEFT JOIN zentriq.device_groups g ON g.id = p.group_id WHERE p.area = @a ORDER BY p.group_id NULLS FIRST, lower(g.name)
            """, conn);
        cmd.Parameters.AddWithValue("a", area);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new(r.IsDBNull(0) ? null : r.GetGuid(0), r.IsDBNull(1) ? null : r.GetString(1), JsonDocument.Parse(r.GetString(2)).RootElement.Clone(),
                r.IsDBNull(3) ? null : r.GetString(3), r.GetFieldValue<DateTimeOffset>(4)));
        return list;
    }

    public static async Task SavePolicyAsync(NpgsqlConnection conn, string area, Guid? groupId, object settings, string? by, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO zentriq.secpack_policies (area, group_id, settings, updated_by, updated_at) VALUES (@a, @g, @s, @b, now())
            ON CONFLICT (area, COALESCE(group_id, '00000000-0000-0000-0000-000000000000'::uuid)) DO UPDATE SET settings = EXCLUDED.settings, updated_by = EXCLUDED.updated_by, updated_at = now()
            """, conn);
        cmd.Parameters.AddWithValue("a", area);
        cmd.Parameters.Add(new NpgsqlParameter("g", NpgsqlDbType.Uuid) { Value = (object?)groupId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(settings, Json) });
        cmd.Parameters.Add(new NpgsqlParameter("b", NpgsqlDbType.Text) { Value = (object?)by ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
        await BumpAsync(conn, ct);
    }

    public static async Task<bool> DeletePolicyAsync(NpgsqlConnection conn, string area, Guid groupId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("DELETE FROM zentriq.secpack_policies WHERE area = @a AND group_id = @g", conn);
        cmd.Parameters.AddWithValue("a", area);
        cmd.Parameters.AddWithValue("g", groupId);
        var n = await cmd.ExecuteNonQueryAsync(ct);
        if (n > 0) await BumpAsync(conn, ct);
        return n > 0;
    }

    private static T Read<T>(JsonElement e) where T : new() => e.Deserialize<T>(Json) ?? new T();

    /// <summary>
    /// The policy of one device: for each area the device group policy with the strictest mode, otherwise the policy for
    /// all devices; the elevation rules, time-limited administrator rights, allowed USB devices and recovery passwords to
    /// replace that concern this device. Without the Security Pack licence everything is off.
    /// </summary>
    public static async Task<SecurityPackPolicy> PolicyForAsync(NpgsqlConnection conn, Guid? assetId, bool licensed, CancellationToken ct)
    {
        var policy = new SecurityPackPolicy { Revision = await RevisionAsync(conn, ct), Licensed = licensed, GeneratedAt = DateTimeOffset.UtcNow };
        if (!licensed || assetId is null) return policy;
        var groups = new List<Guid>();
        await using (var cmd = new NpgsqlCommand($"SELECT g.id FROM zentriq.device_groups g, zentriq.assets a WHERE a.id = @a AND {AppControlStore.MemberSql}", conn))
        {
            cmd.Parameters.AddWithValue("a", assetId.Value);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) groups.Add(r.GetGuid(0));
        }

        AreaPolicy? Pick(List<AreaPolicy> all, Func<JsonElement, string?> mode, Func<IEnumerable<string?>, string> strictest)
        {
            var mine = all.Where(p => p.GroupId is { } g && groups.Contains(g)).ToList();
            if (mine.Count == 0) return all.FirstOrDefault(p => p.GroupId is null);
            var top = strictest(mine.Select(p => mode(p.Settings)));
            return mine.First(p => mode(p.Settings) == top);
        }
        static string? ModeOf(JsonElement e) => e.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;

        if (Pick(await PoliciesAsync(conn, "privilege", ct), ModeOf, SecurityPackRules.StrictestPrivilege) is { } pp)
        {
            var p = Read<PrivilegePolicy>(pp.Settings);
            policy.Privilege = new PrivilegePolicy
            {
                Mode = SecurityPackRules.IsMode("privilege", p.Mode) ? p.Mode : "off", AllowedAdmins = p.AllowedAdmins, Elevation = p.Elevation,
                Requests = p.Requests, MaxAdminMinutes = Math.Clamp(p.MaxAdminMinutes, 15, 1440),
            };
        }
        if (Pick(await PoliciesAsync(conn, "bitlocker", ct), ModeOf, SecurityPackRules.StrictestBitLocker) is { } bp)
        {
            var b = Read<BitLockerPolicy>(bp.Settings);
            b.Rotate = [];
            if (!SecurityPackRules.IsMode("bitlocker", b.Mode)) b.Mode = "off";
            policy.BitLocker = b;
        }
        if (Pick(await PoliciesAsync(conn, "usb", ct), ModeOf, SecurityPackRules.StrictestUsb) is { } up)
        {
            var u = Read<UsbPolicy>(up.Settings);
            u.Allowed = [];
            if (!SecurityPackRules.IsMode("usb", u.Mode)) u.Mode = "off";
            policy.Usb = u;
        }

        // Elevation rules, administrator rights for a while, allowed USB devices, recovery passwords to replace.
        await using (var cmd = new NpgsqlCommand("""
            SELECT id, name, kind, value, file_name, uses_left FROM zentriq.epm_rules
             WHERE enabled AND (expires_at IS NULL OR expires_at > now()) AND (uses_left IS NULL OR uses_left > 0)
               AND ((group_id IS NULL AND asset_id IS NULL) OR group_id = ANY(@g) OR asset_id = @a)
             ORDER BY created_at
            """, conn))
        {
            cmd.Parameters.AddWithValue("a", assetId.Value);
            cmd.Parameters.AddWithValue("g", groups.ToArray());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                policy.Privilege.Rules.Add(new ElevationRule { Id = r.GetGuid(0).ToString(), Name = r.GetString(1), Kind = r.GetString(2), Value = r.GetString(3), FileName = r.IsDBNull(4) ? null : r.GetString(4), UsesLeft = r.IsDBNull(5) ? null : r.GetInt32(5) });
        }
        await using (var cmd = new NpgsqlCommand("SELECT id, user_name, ends_at FROM zentriq.epm_grants WHERE asset_id = @a AND revoked_at IS NULL AND ends_at > now() AND starts_at <= now()", conn))
        {
            cmd.Parameters.AddWithValue("a", assetId.Value);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) policy.Privilege.Grants.Add(new AdminGrant { Id = r.GetGuid(0).ToString(), User = r.GetString(1), Until = r.GetFieldValue<DateTimeOffset>(2) });
        }
        await using (var cmd = new NpgsqlCommand("SELECT device_id, access, name FROM zentriq.usb_allowed WHERE group_id IS NULL OR group_id = ANY(@g) ORDER BY created_at", conn))
        {
            cmd.Parameters.AddWithValue("g", groups.ToArray());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) policy.Usb.Allowed.Add(new UsbAllow { DeviceId = r.GetString(0), Access = r.GetString(1), Name = r.IsDBNull(2) ? null : r.GetString(2) });
        }
        await using (var cmd = new NpgsqlCommand("SELECT protector_id FROM zentriq.bitlocker_keys WHERE asset_id = @a AND retired_at IS NULL AND rotate_requested_at IS NOT NULL", conn))
        {
            cmd.Parameters.AddWithValue("a", assetId.Value);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) policy.BitLocker.Rotate.Add(r.GetString(0));
        }
        return policy;
    }

    private static string? T(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Replace("\0", "").Trim();
        return t.Length > max ? t[..max] : t;
    }

    private static NpgsqlParameter P(string name, object? value, NpgsqlDbType type) => new(name, type) { Value = value ?? DBNull.Value };

    /// <summary>Stores a device's report. Recovery passwords are encrypted; ones no longer on a reported volume are retired.</summary>
    public static async Task SaveReportAsync(NpgsqlConnection conn, Guid assetId, SecurityPackReport report, CredentialProtector protector, CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (report.Admins is { } admins)
        {
            await using (var del = new NpgsqlCommand("DELETE FROM zentriq.device_local_admins WHERE asset_id = @a", conn, tx))
            {
                del.Parameters.AddWithValue("a", assetId);
                await del.ExecuteNonQueryAsync(ct);
            }
            foreach (var m in admins.Where(m => !string.IsNullOrWhiteSpace(m.Member)).GroupBy(m => m.Member.ToLowerInvariant()).Select(g => g.First()).Take(200))
            {
                await using var ins = new NpgsqlCommand("INSERT INTO zentriq.device_local_admins (asset_id, member, sid, kind, status) VALUES (@a, @m, @s, @k, @st)", conn, tx);
                ins.Parameters.AddWithValue("a", assetId);
                ins.Parameters.AddWithValue("m", T(m.Member, 300)!);
                ins.Parameters.Add(P("s", T(m.Sid, 200), NpgsqlDbType.Text));
                ins.Parameters.Add(P("k", T(m.Kind, 30), NpgsqlDbType.Text));
                ins.Parameters.AddWithValue("st", T(m.Status, 20) ?? "kept");
                await ins.ExecuteNonQueryAsync(ct);
            }
        }
        foreach (var v in (report.Volumes ?? []).Where(v => !string.IsNullOrWhiteSpace(v.Volume)).Take(30))
        {
            await using var up = new NpgsqlCommand("""
                INSERT INTO zentriq.bitlocker_volumes (asset_id, volume, kind, protection, status, percent, method, protectors, tpm, error, reported_at)
                VALUES (@a, @v, @k, @p, @s, @pc, @m, @pr, @t, @e, now())
                ON CONFLICT (asset_id, volume) DO UPDATE SET kind = EXCLUDED.kind, protection = EXCLUDED.protection, status = EXCLUDED.status, percent = EXCLUDED.percent,
                    method = EXCLUDED.method, protectors = EXCLUDED.protectors, tpm = EXCLUDED.tpm, error = EXCLUDED.error, reported_at = now()
                """, conn, tx);
            up.Parameters.AddWithValue("a", assetId);
            up.Parameters.AddWithValue("v", T(v.Volume, 60)!);
            up.Parameters.Add(P("k", T(v.Kind, 20), NpgsqlDbType.Text));
            up.Parameters.Add(P("p", T(v.Protection, 20), NpgsqlDbType.Text));
            up.Parameters.Add(P("s", T(v.Status, 20), NpgsqlDbType.Text));
            up.Parameters.Add(P("pc", v.Percent is { } pc ? (float)Math.Clamp(pc, 0, 100) : null, NpgsqlDbType.Real));
            up.Parameters.Add(P("m", T(v.Method, 40), NpgsqlDbType.Text));
            up.Parameters.AddWithValue("pr", v.Protectors.Select(x => T(x, 60) ?? "").Where(x => x.Length > 0).Take(20).ToArray());
            up.Parameters.Add(P("t", T(report.Tpm, 20), NpgsqlDbType.Text));
            up.Parameters.Add(P("e", T(v.Error, 500), NpgsqlDbType.Text));
            await up.ExecuteNonQueryAsync(ct);
            // Recovery passwords escrowed earlier that are no longer on the volume.
            await using var retire = new NpgsqlCommand("""
                UPDATE zentriq.bitlocker_keys SET retired_at = now() WHERE asset_id = @a AND upper(volume) = upper(@v) AND retired_at IS NULL AND NOT (upper(protector_id) = ANY(@ids))
                """, conn, tx);
            retire.Parameters.AddWithValue("a", assetId);
            retire.Parameters.AddWithValue("v", T(v.Volume, 60)!);
            retire.Parameters.AddWithValue("ids", v.RecoveryIds.Select(x => x.Trim().ToUpperInvariant()).ToArray());
            await retire.ExecuteNonQueryAsync(ct);
        }
        foreach (var k in report.Keys.Where(k => System.Text.RegularExpressions.Regex.IsMatch(k.Password ?? "", @"^\d{6}(-\d{6}){7}$") && !string.IsNullOrWhiteSpace(k.ProtectorId)).Take(30))
        {
            var id = Guid.NewGuid();
            await using var ins = new NpgsqlCommand("""
                INSERT INTO zentriq.bitlocker_keys (id, asset_id, volume, protector_id, key_protected) VALUES (@id, @a, @v, @p, @k)
                ON CONFLICT (asset_id, upper(protector_id)) DO UPDATE SET retired_at = NULL, volume = EXCLUDED.volume
                """, conn, tx);
            ins.Parameters.AddWithValue("id", id);
            ins.Parameters.AddWithValue("a", assetId);
            ins.Parameters.AddWithValue("v", T(k.Volume, 60) ?? "?");
            ins.Parameters.AddWithValue("p", k.ProtectorId.Trim().Trim('{', '}').ToUpperInvariant());
            ins.Parameters.AddWithValue("k", protector.Protect(id, new CredentialSecret { Password = k.Password }));
            await ins.ExecuteNonQueryAsync(ct);
        }
        foreach (var d in report.UsbDevices.Where(d => !string.IsNullOrWhiteSpace(d.DeviceId)).Take(100))
        {
            await using var up = new NpgsqlCommand("""
                INSERT INTO zentriq.usb_devices (asset_id, device_id, name, vid_pid, serial, first_seen, last_seen, last_access) VALUES (@a, @d, @n, @vp, @s, @t, @t, @x)
                ON CONFLICT (asset_id, device_id) DO UPDATE SET name = COALESCE(EXCLUDED.name, zentriq.usb_devices.name), vid_pid = COALESCE(EXCLUDED.vid_pid, zentriq.usb_devices.vid_pid),
                    serial = COALESCE(EXCLUDED.serial, zentriq.usb_devices.serial), last_seen = GREATEST(zentriq.usb_devices.last_seen, EXCLUDED.last_seen), last_access = EXCLUDED.last_access
                """, conn, tx);
            up.Parameters.AddWithValue("a", assetId);
            up.Parameters.AddWithValue("d", T(d.DeviceId, 400)!);
            up.Parameters.Add(P("n", T(d.Name, 200), NpgsqlDbType.Text));
            up.Parameters.Add(P("vp", T(d.VidPid, 40), NpgsqlDbType.Text));
            up.Parameters.Add(P("s", T(d.Serial, 120), NpgsqlDbType.Text));
            up.Parameters.AddWithValue("t", Clamp(d.SeenAt));
            up.Parameters.Add(P("x", T(d.Access, 20), NpgsqlDbType.Text));
            await up.ExecuteNonQueryAsync(ct);
        }
        foreach (var e in report.PrivilegeEvents.Where(e => e.Kind is "elevated" or "refused" or "failed" or "admin_added" or "admin_removed" or "member_removed" or "member_kept").Take(500))
        {
            await using var ins = new NpgsqlCommand("""
                INSERT INTO zentriq.epm_events (asset_id, at, user_name, kind, file_path, sha256, publisher, rule_id, detail) VALUES (@a, @t, @u, @k, @f, @h, @p, @r, @d)
                """, conn, tx);
            ins.Parameters.AddWithValue("a", assetId);
            ins.Parameters.AddWithValue("t", Clamp(e.At));
            ins.Parameters.Add(P("u", T(e.User, 300), NpgsqlDbType.Text));
            ins.Parameters.AddWithValue("k", e.Kind);
            ins.Parameters.Add(P("f", T(e.Path, 1000), NpgsqlDbType.Text));
            ins.Parameters.Add(P("h", T(e.Sha256, 64), NpgsqlDbType.Text));
            ins.Parameters.Add(P("p", T(e.Publisher, 300), NpgsqlDbType.Text));
            ins.Parameters.Add(P("r", Guid.TryParse(e.RuleId, out var rid) ? rid : null, NpgsqlDbType.Uuid));
            ins.Parameters.Add(P("d", T(e.Detail, 1000), NpgsqlDbType.Text));
            await ins.ExecuteNonQueryAsync(ct);
        }
        foreach (var e in report.UsbEvents.Where(e => e.Kind is "connected" or "blocked" or "readonly" or "allowed" or "file_written" or "file_renamed" or "file_deleted" or "failed").Take(2000))
        {
            await using var ins = new NpgsqlCommand("""
                INSERT INTO zentriq.usb_events (asset_id, at, user_name, kind, device_id, device_name, file_path, size, detail) VALUES (@a, @t, @u, @k, @d, @n, @f, @s, @x)
                """, conn, tx);
            ins.Parameters.AddWithValue("a", assetId);
            ins.Parameters.AddWithValue("t", Clamp(e.At));
            ins.Parameters.Add(P("u", T(e.User, 300), NpgsqlDbType.Text));
            ins.Parameters.AddWithValue("k", e.Kind);
            ins.Parameters.Add(P("d", T(e.DeviceId, 400), NpgsqlDbType.Text));
            ins.Parameters.Add(P("n", T(e.DeviceName, 200), NpgsqlDbType.Text));
            ins.Parameters.Add(P("f", T(e.Path, 1000), NpgsqlDbType.Text));
            ins.Parameters.Add(P("s", e.Size is >= 0 ? e.Size : null, NpgsqlDbType.Bigint));
            ins.Parameters.Add(P("x", T(e.Detail, 1000), NpgsqlDbType.Text));
            await ins.ExecuteNonQueryAsync(ct);
        }
        var used = report.UsedRules.Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();
        foreach (var g in used.Distinct())
        {
            await using var dec = new NpgsqlCommand("UPDATE zentriq.epm_rules SET uses_left = GREATEST(0, uses_left - @n) WHERE id = @id AND uses_left IS NOT NULL", conn, tx);
            dec.Parameters.AddWithValue("id", g);
            dec.Parameters.AddWithValue("n", used.Count(x => x == g));
            await dec.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    private static DateTimeOffset Clamp(DateTimeOffset t)
    {
        var now = DateTimeOffset.UtcNow;
        return (t > now.AddMinutes(10) || t < now.AddDays(-60) ? now : t).ToUniversalTime();
    }

    /// <summary>A recovery password, decrypted (callers audit every use).</summary>
    public static async Task<(string Volume, string ProtectorId, string Password, Guid AssetId)?> RevealAsync(NpgsqlConnection conn, Guid keyId, CredentialProtector protector, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT volume, protector_id, key_protected, asset_id FROM zentriq.bitlocker_keys WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", keyId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return (r.GetString(0), r.GetString(1), protector.Unprotect(keyId, r.GetString(2)).Password ?? "", r.GetGuid(3));
    }
}
