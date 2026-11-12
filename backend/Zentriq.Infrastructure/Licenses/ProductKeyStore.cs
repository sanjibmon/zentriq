using Npgsql;
using NpgsqlTypes;
using Zentriq.Infrastructure.Secrets;
using Zentriq.Inventory;

namespace Zentriq.Infrastructure.Licenses;

/// <summary>
/// Product keys found on a device with its inventory. Each key is stored encrypted with the installation credential
/// key (bound to its row), with only its last five characters readable; rows of keys no longer on the device are
/// removed. Nothing is stored while "Collect product keys" is switched off in the licence settings.
/// </summary>
public static class ProductKeyStore
{
    public static string Hint(string key) => key.Length <= 5 ? key : key[^5..];

    /// <summary>The key as shown before it is revealed: •••••-ABCDE.</summary>
    public static string Masked(string hint) => "•••••-" + hint;

    public static async Task<bool> EnabledAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT (value->>'productKeys')::boolean FROM zentriq.system_settings WHERE key = @k", conn, tx);
        cmd.Parameters.AddWithValue("k", LicenseSettings.Key);
        return await cmd.ExecuteScalarAsync(ct) is not false;
    }

    public static async Task RecordAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid assetId, IReadOnlyList<ProductKeyItem> keys, CredentialProtector protector, CancellationToken ct)
    {
        if (!await EnabledAsync(conn, tx, ct)) return;
        var found = keys.Where(k => !string.IsNullOrWhiteSpace(k.Key) && !string.IsNullOrWhiteSpace(k.Product) && k.Key.Length <= 100)
            .Take(ProductKeys.MaxKeys)
            .Select(k => (Item: k, Key: k.Key.Replace("\0", "").Trim(), Product: Cut(k.Product, 200)!, App: k.Application is "windows" or "office" or "firmware" ? k.Application : "other"))
            .ToList();

        var existing = new List<(Guid Id, string Product, string App, string? Key)>();
        await using (var cmd = new NpgsqlCommand("SELECT id, product, application, key_protected FROM zentriq.asset_product_keys WHERE asset_id = @a", conn, tx))
        {
            cmd.Parameters.AddWithValue("a", assetId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = r.GetGuid(0);
                string? key = null;
                try { key = protector.Unprotect(id, r.GetString(3)).Password; } catch (Exception) { /* unreadable: replaced */ }
                existing.Add((id, r.GetString(1), r.GetString(2), key));
            }
        }
        var keep = new List<Guid>();
        foreach (var f in found)
        {
            var match = existing.FirstOrDefault(e => e.Key == f.Key && e.App == f.App && string.Equals(e.Product, f.Product, StringComparison.OrdinalIgnoreCase) && !keep.Contains(e.Id));
            if (match.Id != Guid.Empty) { keep.Add(match.Id); continue; }
            var id = Guid.NewGuid();
            await using var ins = new NpgsqlCommand("""
                INSERT INTO zentriq.asset_product_keys (id, asset_id, product, publisher, application, source, key_hint, key_protected, note)
                VALUES (@id, @a, @p, @pub, @app, @src, @hint, @key, @note)
                """, conn, tx);
            ins.Parameters.AddWithValue("id", id);
            ins.Parameters.AddWithValue("a", assetId);
            ins.Parameters.AddWithValue("p", f.Product);
            ins.Parameters.Add(Text("pub", Cut(f.Item.Publisher, 200)));
            ins.Parameters.AddWithValue("app", f.App);
            ins.Parameters.AddWithValue("src", f.Item.Source is "firmware" or "windows" ? f.Item.Source : "registry");
            ins.Parameters.AddWithValue("hint", Hint(f.Key));
            ins.Parameters.AddWithValue("key", protector.Protect(id, new CredentialSecret { Password = f.Key }));
            ins.Parameters.Add(Text("note", Cut(f.Item.Note, 300)));
            await ins.ExecuteNonQueryAsync(ct);
        }
        await using var upd = new NpgsqlCommand("""
            WITH gone AS (DELETE FROM zentriq.asset_product_keys WHERE asset_id = @a AND id = ANY(@old) AND NOT (id = ANY(@keep)))
            UPDATE zentriq.asset_product_keys SET last_seen_at = now() WHERE id = ANY(@keep)
            """, conn, tx);
        upd.Parameters.AddWithValue("a", assetId);
        upd.Parameters.Add(new NpgsqlParameter("keep", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = keep.ToArray() });
        upd.Parameters.Add(new NpgsqlParameter("old", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = existing.Select(e => e.Id).ToArray() });
        await upd.ExecuteNonQueryAsync(ct);
    }

    private static string? Cut(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Replace("\0", "").Trim();
        return t.Length == 0 ? null : t.Length > max ? t[..max] : t;
    }

    private static NpgsqlParameter Text(string name, string? value) => new(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };
}
