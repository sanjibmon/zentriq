using System.Reflection;
using System.Text.RegularExpressions;
using Npgsql;

namespace Zentriq.LicenseServer.Services;

/// <summary>Applies the embedded SQL migrations (Migrations/V####__name.sql) in order, once each, under an advisory lock.</summary>
public static partial class MigrationRunner
{
    [GeneratedRegex(@"\.V(\d{4})__([a-z0-9_]+)\.sql$")]
    private static partial Regex Name();

    public static IReadOnlyList<(int Version, string Name, string Sql)> Scripts()
    {
        var asm = typeof(MigrationRunner).Assembly;
        return asm.GetManifestResourceNames()
            .Select(n => (n, m: Name().Match(n)))
            .Where(x => x.m.Success)
            .Select(x =>
            {
                using var s = asm.GetManifestResourceStream(x.n)!;
                using var r = new StreamReader(s);
                return (int.Parse(x.m.Groups[1].Value), x.m.Groups[2].Value, r.ReadToEnd());
            })
            .OrderBy(x => x.Item1).ToList();
    }

    public static async Task<int> MigrateAsync(NpgsqlDataSource ds, CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using (var lk = new NpgsqlCommand("SELECT pg_advisory_lock(727274001)", conn)) await lk.ExecuteNonQueryAsync(ct);
        try
        {
            await using (var c = new NpgsqlCommand("""
                CREATE SCHEMA IF NOT EXISTS zls;
                CREATE TABLE IF NOT EXISTS zls.schema_migrations (version int PRIMARY KEY, name text NOT NULL, applied_at timestamptz NOT NULL DEFAULT now());
                """, conn)) await c.ExecuteNonQueryAsync(ct);
            var applied = new HashSet<int>();
            await using (var c = new NpgsqlCommand("SELECT version FROM zls.schema_migrations", conn))
            await using (var r = await c.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) applied.Add(r.GetInt32(0));
            var count = 0;
            foreach (var (version, name, sql) in Scripts().Where(s => !applied.Contains(s.Version)))
            {
                await using var tx = await conn.BeginTransactionAsync(ct);
                await using (var c = new NpgsqlCommand(sql, conn, tx)) await c.ExecuteNonQueryAsync(ct);
                await using (var c = new NpgsqlCommand("INSERT INTO zls.schema_migrations (version, name) VALUES (@v, @n)", conn, tx))
                {
                    c.Parameters.AddWithValue("v", version); c.Parameters.AddWithValue("n", name);
                    await c.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
                count++;
            }
            return count;
        }
        finally
        {
            await using var ul = new NpgsqlCommand("SELECT pg_advisory_unlock(727274001)", conn);
            await ul.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}

/// <summary>Small helpers over Npgsql (the license server has a handful of tables; plain SQL keeps it obvious).</summary>
public static class Sql
{
    public static NpgsqlCommand Cmd(this NpgsqlConnection conn, string sql, object? args = null, NpgsqlTransaction? tx = null)
    {
        var cmd = new NpgsqlCommand(sql, conn, tx);
        if (args is not null)
            foreach (var p in args.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                cmd.Parameters.AddWithValue(p.Name, p.GetValue(args) ?? DBNull.Value);
        return cmd;
    }

    public static async Task<List<T>> ListAsync<T>(this NpgsqlConnection conn, string sql, object? args, Func<NpgsqlDataReader, T> map, CancellationToken ct, NpgsqlTransaction? tx = null)
    {
        await using var cmd = conn.Cmd(sql, args, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<T>();
        while (await r.ReadAsync(ct)) list.Add(map(r));
        return list;
    }

    public static async Task<T?> FirstAsync<T>(this NpgsqlConnection conn, string sql, object? args, Func<NpgsqlDataReader, T> map, CancellationToken ct, NpgsqlTransaction? tx = null) where T : class =>
        (await conn.ListAsync(sql, args, map, ct, tx)).FirstOrDefault();

    public static async Task<int> ExecAsync(this NpgsqlConnection conn, string sql, object? args, CancellationToken ct, NpgsqlTransaction? tx = null)
    {
        await using var cmd = conn.Cmd(sql, args, tx);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task<object?> ScalarAsync(this NpgsqlConnection conn, string sql, object? args, CancellationToken ct, NpgsqlTransaction? tx = null)
    {
        await using var cmd = conn.Cmd(sql, args, tx);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is DBNull ? null : v;
    }

    public static string? Str(this NpgsqlDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? null : r.GetString(i); }
    public static int? Int(this NpgsqlDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? null : r.GetInt32(i); }
    public static DateTimeOffset? Time(this NpgsqlDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i); }
    public static Guid? Uuid(this NpgsqlDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? null : r.GetGuid(i); }
}
