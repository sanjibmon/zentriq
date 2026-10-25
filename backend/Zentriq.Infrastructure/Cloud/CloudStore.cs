using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Infrastructure.Secrets;

namespace Zentriq.Infrastructure.Cloud;

public sealed record CloudAccountInfo(Guid Id, string Name, string Provider, CloudSettings Settings, string? Secret, Guid? CredentialId, bool Enabled);

public sealed record CloudSaveResult(int Resources, int Created, int Changed, int Deleted, int Restored, int Linked, int Findings, int NewFindings, Dictionary<string, int> ByKind);

/// <summary>
/// Cloud and virtualization connections and what their syncs found: resources (created / changed / deleted, with a change
/// history), the inventoried device each machine is (BIOS UUID, MAC, instance id, host name and IP), department and
/// location from tags, and findings (opened, kept, resolved).
/// </summary>
public sealed class CloudStore(NpgsqlDataSource ds, CredentialProtector protector)
{
    public static readonly string[] DepartmentTagKeys = ["department", "dept", "costcenter", "cost-center", "cost_center", "costcentre", "cost-centre"];
    public static readonly string[] LocationTagKeys = ["location", "site", "office"];

    public async Task<CloudAccountInfo?> LoadAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT name, provider, settings::text, secret_protected, credential_id, enabled FROM zentriq.cloud_accounts WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var secret = r.IsDBNull(3) ? null : protector.Unprotect(id, r.GetString(3)).Password;
        return new CloudAccountInfo(id, r.GetString(0), r.GetString(1), CloudSettings.Parse(r.GetString(2)), secret, r.IsDBNull(4) ? null : r.GetGuid(4), r.GetBoolean(5));
    }

    public string ProtectSecret(Guid id, string secret) => protector.Protect(id, new CredentialSecret { Password = secret });

    public async Task SetStatusAsync(Guid id, string status, string? error, object? counts, IReadOnlyList<string>? warnings, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand($"""
            UPDATE zentriq.cloud_accounts SET last_status = @s
                   {(status is "succeeded" or "partial" or "failed" ? ", last_sync_at = now()" : "")}
                   {(status == "failed" ? ", last_error = @e, last_error_at = now()" : status is "succeeded" or "partial" ? ", last_error = NULL" : "")}
                   {(counts is not null ? ", last_counts = @c" : "")}
                   {(warnings is not null ? ", last_warnings = @w" : "")}
             WHERE id = @id
            """);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("s", status);
        cmd.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.Text) { Value = (object?)error ?? DBNull.Value });
        if (counts is not null) cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(counts) });
        if (warnings is not null) cmd.Parameters.Add(new NpgsqlParameter("w", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = warnings.Take(50).ToArray() });
        await cmd.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private sealed record Existing(Guid Id, string Kind, string? State, DateTimeOffset? StateSince, DateTimeOffset? DeletedAt, Dictionary<string, string?> Fields);

    private static Dictionary<string, string?> Fields(string? name, string? state, string? size, string? os, string? privateIp, string? publicIp, int? cpu, long? mem, decimal? disk, string? parent) => new()
    {
        ["name"] = name, ["state"] = state, ["size"] = size, ["os"] = os, ["privateIp"] = privateIp, ["publicIp"] = publicIp,
        ["cpu"] = cpu?.ToString(), ["memoryMb"] = mem?.ToString(), ["diskGb"] = disk?.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture), ["parent"] = parent,
    };

    public async Task<CloudSaveResult> SaveSnapshotAsync(Guid accountId, string provider, CloudSnapshot snap, DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var existing = new Dictionary<string, Existing>();
        await using (var q = new NpgsqlCommand("""
            SELECT id, external_id, kind, state, state_since, deleted_at, name, size, os, private_ip, public_ip, cpu, memory_mb, disk_gb, parent_id
              FROM zentriq.cloud_resources WHERE account_id = @a
            """, conn, tx))
        {
            q.Parameters.AddWithValue("a", accountId);
            await using var r = await q.ExecuteReaderAsync(ct);
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            while (await r.ReadAsync(ct))
                existing[r.GetString(1)] = new Existing(r.GetGuid(0), r.GetString(2), S(3), r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4),
                    r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5),
                    Fields(S(6), S(3), S(7), S(8), S(9), S(10), r.IsDBNull(11) ? null : r.GetInt32(11), r.IsDBNull(12) ? null : r.GetInt64(12), r.IsDBNull(13) ? null : r.GetDecimal(13), S(14)));
        }

        // One entry per external id (a VM reported by two cluster nodes during a live migration counts once).
        var items = snap.Items.Where(i => !string.IsNullOrEmpty(i.ExternalId)).GroupBy(i => i.ExternalId).Select(g => g.Last()).ToList();
        int created = 0, changed = 0, deleted = 0, restored = 0;
        var since = new Dictionary<string, DateTimeOffset?>();
        var ids = new Dictionary<string, Guid>();
        foreach (var i in items)
        {
            existing.TryGetValue(i.ExternalId, out var old);
            var stateSince = i.StateSince ?? (old is not null && old.DeletedAt is null && string.Equals(old.State, i.State, StringComparison.OrdinalIgnoreCase) ? old.StateSince ?? now : now);
            since[i.ExternalId] = stateSince;
            var id = old?.Id ?? Guid.NewGuid();
            ids[i.ExternalId] = id;
            await using var up = new NpgsqlCommand("""
                INSERT INTO zentriq.cloud_resources (id, account_id, provider, kind, external_id, name, region, resource_group, parent_id, state, state_since,
                    size, os, hostname, private_ip, public_ip, cpu, memory_mb, disk_gb, bios_uuid, macs, tags, properties, created_at_provider,
                    first_seen_at, last_seen_at, deleted_at)
                VALUES (@id, @a, @p, @k, @x, @n, @reg, @grp, @par, @st, @since, @size, @os, @host, @pip, @pub, @cpu, @mem, @disk, @uuid, @macs, @tags, @props, @cat,
                    @now, @now, NULL)
                ON CONFLICT (account_id, external_id) DO UPDATE SET
                    provider = EXCLUDED.provider, kind = EXCLUDED.kind, name = EXCLUDED.name, region = EXCLUDED.region, resource_group = EXCLUDED.resource_group,
                    parent_id = EXCLUDED.parent_id, state = EXCLUDED.state, state_since = EXCLUDED.state_since, size = EXCLUDED.size, os = EXCLUDED.os,
                    hostname = EXCLUDED.hostname, private_ip = EXCLUDED.private_ip, public_ip = EXCLUDED.public_ip, cpu = EXCLUDED.cpu, memory_mb = EXCLUDED.memory_mb,
                    disk_gb = EXCLUDED.disk_gb, bios_uuid = EXCLUDED.bios_uuid, macs = EXCLUDED.macs, tags = EXCLUDED.tags, properties = EXCLUDED.properties,
                    created_at_provider = COALESCE(EXCLUDED.created_at_provider, cloud_resources.created_at_provider), last_seen_at = @now, deleted_at = NULL
                """, conn, tx);
            NpgsqlParameter T(string n, string? v) => new(n, NpgsqlDbType.Text) { Value = string.IsNullOrEmpty(v) ? DBNull.Value : v.Length > 2000 ? v[..2000] : v };
            up.Parameters.AddWithValue("id", id);
            up.Parameters.AddWithValue("a", accountId);
            up.Parameters.AddWithValue("p", provider);
            up.Parameters.AddWithValue("k", i.Kind);
            up.Parameters.AddWithValue("x", i.ExternalId);
            up.Parameters.Add(T("n", i.Name));
            up.Parameters.Add(T("reg", i.Region));
            up.Parameters.Add(T("grp", i.Group));
            up.Parameters.Add(T("par", i.ParentId));
            up.Parameters.Add(T("st", i.State));
            up.Parameters.Add(new NpgsqlParameter("since", NpgsqlDbType.TimestampTz) { Value = (object?)stateSince ?? DBNull.Value });
            up.Parameters.Add(T("size", i.Size));
            up.Parameters.Add(T("os", i.Os));
            up.Parameters.Add(T("host", i.Hostname));
            up.Parameters.Add(T("pip", i.PrivateIp));
            up.Parameters.Add(T("pub", i.PublicIp));
            up.Parameters.Add(new NpgsqlParameter("cpu", NpgsqlDbType.Integer) { Value = (object?)i.Cpu ?? DBNull.Value });
            up.Parameters.Add(new NpgsqlParameter("mem", NpgsqlDbType.Bigint) { Value = (object?)i.MemoryMb ?? DBNull.Value });
            up.Parameters.Add(new NpgsqlParameter("disk", NpgsqlDbType.Numeric) { Value = (object?)i.DiskGb ?? DBNull.Value });
            up.Parameters.Add(T("uuid", i.BiosUuid));
            up.Parameters.Add(new NpgsqlParameter("macs", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = i.Macs.Distinct().ToArray() });
            up.Parameters.Add(new NpgsqlParameter("tags", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(i.Tags) });
            up.Parameters.Add(new NpgsqlParameter("props", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(i.Properties) });
            up.Parameters.Add(new NpgsqlParameter("cat", NpgsqlDbType.TimestampTz) { Value = (object?)i.CreatedAt ?? DBNull.Value });
            up.Parameters.AddWithValue("now", now);
            await up.ExecuteNonQueryAsync(ct);

            string? change = null;
            object detail = new { };
            if (old is null) { change = "created"; created++; detail = new { i.Kind, i.Name, i.State }; }
            else if (old.DeletedAt is not null) { change = "restored"; restored++; detail = new { i.Name, i.State }; }
            else
            {
                var now2 = Fields(i.Name, i.State, i.Size, i.Os, i.PrivateIp, i.PublicIp, i.Cpu, i.MemoryMb, i.DiskGb, i.ParentId);
                var diff = now2.Where(f => !string.Equals(f.Value ?? "", old.Fields.GetValueOrDefault(f.Key) ?? "", StringComparison.Ordinal)
                                           // a VM's guest details disappear while it is off; don't record that as a change
                                           && !(f.Value is null && f.Key is "os" or "privateIp" or "publicIp"))
                    .ToDictionary(f => f.Key, f => new[] { old.Fields.GetValueOrDefault(f.Key), f.Value });
                if (diff.Count > 0) { change = "changed"; changed++; detail = diff; }
            }
            if (change is not null) await AddChangeAsync(conn, tx, id, change, detail, now, ct);
        }

        // Deleted at the provider: only when everything of that kind could be read.
        if (snap.Complete)
        {
            var seen = items.Select(i => i.ExternalId).ToHashSet();
            foreach (var (ext, old) in existing)
            {
                if (seen.Contains(ext) || old.DeletedAt is not null || snap.PartialKinds.Contains(old.Kind)) continue;
                await using var del = new NpgsqlCommand("UPDATE zentriq.cloud_resources SET deleted_at = @now WHERE id = @id", conn, tx);
                del.Parameters.AddWithValue("id", old.Id);
                del.Parameters.AddWithValue("now", now);
                await del.ExecuteNonQueryAsync(ct);
                await AddChangeAsync(conn, tx, old.Id, "deleted", new { name = old.Fields["name"], kind = old.Kind }, now, ct);
                deleted++;
            }
        }

        var linked = await LinkAsync(conn, tx, accountId, ct);
        await MapTagsAsync(conn, tx, accountId, ct);

        // Findings: open (or keep) what the rules find now; resolve the rest for the resources seen in this sync.
        var findings = CloudRules.Evaluate(provider, items, i => since.GetValueOrDefault(i.ExternalId), now);
        var newFindings = 0;
        foreach (var f in findings)
        {
            if (!ids.TryGetValue(f.ExternalId, out var rid)) continue;
            await using var fc = new NpgsqlCommand("""
                INSERT INTO zentriq.cloud_findings (resource_id, rule, severity, title, detail, first_seen_at, last_seen_at)
                VALUES (@r, @rule, @sev, @t, @d, @now, @now)
                ON CONFLICT (resource_id, rule) DO UPDATE SET severity = EXCLUDED.severity, title = EXCLUDED.title, detail = EXCLUDED.detail,
                    last_seen_at = @now, first_seen_at = CASE WHEN cloud_findings.resolved_at IS NOT NULL THEN @now ELSE cloud_findings.first_seen_at END,
                    resolved_at = NULL
                RETURNING (xmax::text = '0') OR first_seen_at = @now
                """, conn, tx);
            fc.Parameters.AddWithValue("r", rid);
            fc.Parameters.AddWithValue("rule", f.Rule);
            fc.Parameters.AddWithValue("sev", f.Severity);
            fc.Parameters.AddWithValue("t", f.Title);
            fc.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Text) { Value = (object?)f.Detail ?? DBNull.Value });
            fc.Parameters.AddWithValue("now", now);
            if (await fc.ExecuteScalarAsync(ct) is true) newFindings++;
        }
        await using (var res = new NpgsqlCommand("""
            UPDATE zentriq.cloud_findings f SET resolved_at = @now
              FROM zentriq.cloud_resources r
             WHERE f.resource_id = r.id AND r.account_id = @a AND f.resolved_at IS NULL AND f.last_seen_at < @now
               AND (r.last_seen_at >= @now OR r.deleted_at IS NOT NULL)
            """, conn, tx))
        {
            res.Parameters.AddWithValue("a", accountId);
            res.Parameters.AddWithValue("now", now);
            await res.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return new CloudSaveResult(items.Count, created, changed, deleted, restored, linked, findings.Count, newFindings,
            items.GroupBy(i => i.Kind).ToDictionary(g => g.Key, g => g.Count()));
    }

    private static async Task AddChangeAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, string change, object detail, DateTimeOffset now, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("INSERT INTO zentriq.cloud_changes (resource_id, at, change, detail) VALUES (@r, @at, @c, @d)", conn, tx);
        c.Parameters.AddWithValue("r", id);
        c.Parameters.AddWithValue("at", now);
        c.Parameters.AddWithValue("c", change);
        c.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(detail) });
        await c.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Links virtual machines and hosts to inventoried devices: BIOS UUID (as reported, or with the first three groups
    /// byte-swapped as Windows shows Azure and VMware machines), cloud instance id, MAC address, then host name with a
    /// matching IP address. Links set by hand are kept.
    /// </summary>
    public const string LinkSql = """
        WITH m AS (
            SELECT r.id,
                   COALESCE(
                     (SELECT i.asset_id FROM zentriq.asset_identifiers i JOIN zentriq.assets a ON a.id = i.asset_id AND a.status = 'active'
                       WHERE (i.kind = 'smbios_uuid' AND r.bios_uuid IS NOT NULL AND length(r.bios_uuid) = 36
                              AND upper(i.value) IN (upper(r.bios_uuid),
                                  upper(substr(r.bios_uuid,7,2)||substr(r.bios_uuid,5,2)||substr(r.bios_uuid,3,2)||substr(r.bios_uuid,1,2)||'-'||
                                        substr(r.bios_uuid,12,2)||substr(r.bios_uuid,10,2)||'-'||substr(r.bios_uuid,17,2)||substr(r.bios_uuid,15,2)||substr(r.bios_uuid,19))))
                          OR (i.kind = 'cloud_instance_id' AND lower(i.value) = lower(r.external_id))
                       ORDER BY a.last_seen_at DESC NULLS LAST LIMIT 1),
                     (SELECT i.asset_id FROM zentriq.asset_identifiers i JOIN zentriq.assets a ON a.id = i.asset_id AND a.status = 'active'
                       WHERE i.kind = 'mac' AND i.value = ANY (r.macs)
                       ORDER BY a.last_seen_at DESC NULLS LAST LIMIT 1),
                     (SELECT a.id FROM zentriq.assets a
                       WHERE a.status = 'active' AND r.hostname IS NOT NULL AND lower(a.hostname) = lower(r.hostname)
                         AND (r.private_ip IS NULL OR a.primary_ip IS NULL OR host(a.primary_ip) = r.private_ip
                              OR EXISTS (SELECT 1 FROM zentriq.asset_identifiers i WHERE i.asset_id = a.id AND i.kind = 'mac' AND i.value = ANY (r.macs)))
                       ORDER BY a.last_seen_at DESC NULLS LAST LIMIT 1)) AS asset_id
              FROM zentriq.cloud_resources r
             WHERE r.account_id = @a AND r.kind IN ('vm','host') AND r.deleted_at IS NULL AND NOT r.asset_link_manual)
        UPDATE zentriq.cloud_resources r SET asset_id = m.asset_id
          FROM m WHERE r.id = m.id AND r.asset_id IS DISTINCT FROM m.asset_id
        """;

    private static async Task<int> LinkAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid accountId, CancellationToken ct)
    {
        await using (var cmd = new NpgsqlCommand(LinkSql, conn, tx))
        {
            cmd.Parameters.AddWithValue("a", accountId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using var count = new NpgsqlCommand("SELECT count(*) FROM zentriq.cloud_resources WHERE account_id = @a AND asset_id IS NOT NULL AND deleted_at IS NULL", conn, tx);
        count.Parameters.AddWithValue("a", accountId);
        return (int)(long)(await count.ExecuteScalarAsync(ct))!;
    }

    private static async Task MapTagsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid accountId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            UPDATE zentriq.cloud_resources r SET
                department_id = (SELECT d.id FROM zentriq.departments d, jsonb_each_text(r.tags) t
                                  WHERE lower(t.key) = ANY (@dk) AND (lower(t.value) = lower(d.name) OR lower(t.value) = lower(d.code)) LIMIT 1),
                location_id = (SELECT l.id FROM zentriq.locations l, jsonb_each_text(r.tags) t
                                WHERE lower(t.key) = ANY (@lk) AND (lower(t.value) = lower(l.name) OR lower(t.value) = lower(l.code)) LIMIT 1)
             WHERE r.account_id = @a AND r.deleted_at IS NULL AND r.tags <> '{}'::jsonb
            """, conn, tx);
        cmd.Parameters.AddWithValue("a", accountId);
        cmd.Parameters.Add(new NpgsqlParameter("dk", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = DepartmentTagKeys });
        cmd.Parameters.Add(new NpgsqlParameter("lk", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = LocationTagKeys });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
