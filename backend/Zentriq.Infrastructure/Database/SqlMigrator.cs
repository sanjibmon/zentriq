using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Zentriq.Infrastructure.Database;

public sealed record Migration(int Version, string Name, string Sql, string Checksum);

public sealed record MigrationStatus(int CurrentVersion, int LatestVersion, IReadOnlyList<Migration> Pending)
{
    public bool UpToDate => Pending.Count == 0;
}

/// <summary>
/// Applies the versioned SQL files embedded from /database/migrations. Each file runs in its own
/// transaction under a session advisory lock (so concurrent installers or services cannot race).
/// Checksums of applied files are verified so an edited, already-applied migration is detected.
/// </summary>
public sealed partial class SqlMigrator(ILogger<SqlMigrator> logger)
{
    private const long LockKey = 0x5A454E5452495121; // "ZENTRIQ!"

    [GeneratedRegex(@"^Zentriq\.Database\.Migrations\.V(\d{4})__([A-Za-z0-9_]+)\.sql$")]
    private static partial Regex ResourceName();

    public static IReadOnlyList<Migration> LoadEmbedded()
    {
        var asm = typeof(SqlMigrator).Assembly;
        var list = new List<Migration>();
        foreach (var name in asm.GetManifestResourceNames())
        {
            var m = ResourceName().Match(name);
            if (!m.Success) continue;
            var sql = ReadResource(asm, name);
            list.Add(new Migration(int.Parse(m.Groups[1].Value), $"V{m.Groups[1].Value}__{m.Groups[2].Value}.sql", sql, Checksum(sql)));
        }
        list.Sort((a, b) => a.Version.CompareTo(b.Version));
        for (int i = 0; i < list.Count; i++)
            if (list[i].Version != i + 1) throw new InvalidOperationException($"Migration versions are not contiguous at {list[i].Name}.");
        return list;
    }

    public static string Bootstrap => ReadResource(typeof(SqlMigrator).Assembly, "Zentriq.Database.bootstrap.sql");

    public static int LatestVersion => LoadEmbedded().Count;

    private static string ReadResource(Assembly asm, string name)
    {
        using var s = asm.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Missing embedded resource {name}.");
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    /// <summary>Checksum over normalised line endings so git autocrlf cannot change it.</summary>
    public static string Checksum(string sql) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql.Replace("\r\n", "\n")))).ToLowerInvariant();

    public async Task<MigrationStatus> GetStatusAsync(NpgsqlConnection conn, CancellationToken ct = default)
    {
        var all = LoadEmbedded();
        var applied = await ReadAppliedAsync(conn, ct);
        foreach (var (version, checksum) in applied)
        {
            var m = all.FirstOrDefault(x => x.Version == version);
            if (m is null)
                throw new InvalidOperationException($"Database is at schema version {version}, newer than this Zentriq build supports ({all.Count}). Install the matching or newer Zentriq release.");
            if (!string.Equals(m.Checksum, checksum, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Applied migration {m.Name} does not match this build (checksum mismatch).");
        }
        var current = applied.Count == 0 ? 0 : applied.Keys.Max();
        return new MigrationStatus(current, all.Count, all.Where(x => !applied.ContainsKey(x.Version)).ToList());
    }

    /// <summary>Runs as the schema owner role. Returns the number of migrations applied.</summary>
    public async Task<int> MigrateAsync(string ownerConnectionString, string appRole, string productVersion, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(ownerConnectionString);
        await conn.OpenAsync(ct);
        await using (var l = new NpgsqlCommand("SELECT pg_advisory_lock(@k)", conn)) { l.Parameters.AddWithValue("k", LockKey); await l.ExecuteNonQueryAsync(ct); }
        try
        {
            await using (var boot = new NpgsqlCommand(Bootstrap, conn)) await boot.ExecuteNonQueryAsync(ct);

            var status = await GetStatusAsync(conn, ct);
            foreach (var m in status.Pending)
            {
                logger.LogInformation("Applying migration {Name}", m.Name);
                var sw = Stopwatch.StartNew();
                await using var tx = await conn.BeginTransactionAsync(ct);
                await using (var cmd = new NpgsqlCommand(m.Sql, conn, tx) { CommandTimeout = 0 })
                    await cmd.ExecuteNonQueryAsync(ct);
                await using (var rec = new NpgsqlCommand(
                    "INSERT INTO zentriq.schema_migrations (version, name, checksum, duration_ms, product_version) VALUES (@v, @n, @c, @d, @p)", conn, tx))
                {
                    rec.Parameters.AddWithValue("v", m.Version);
                    rec.Parameters.AddWithValue("n", m.Name);
                    rec.Parameters.AddWithValue("c", m.Checksum);
                    rec.Parameters.AddWithValue("d", (int)sw.ElapsedMilliseconds);
                    rec.Parameters.AddWithValue("p", productVersion);
                    await rec.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
                logger.LogInformation("Applied migration {Name} in {Ms} ms", m.Name, sw.ElapsedMilliseconds);
            }

            await using (var ins = new NpgsqlCommand(
                "INSERT INTO zentriq.install_state (id, product_version) VALUES (1, @p) ON CONFLICT (id) DO UPDATE SET product_version = EXCLUDED.product_version, upgraded_at = CASE WHEN zentriq.install_state.product_version <> EXCLUDED.product_version THEN now() ELSE zentriq.install_state.upgraded_at END", conn))
            {
                ins.Parameters.AddWithValue("p", productVersion);
                await ins.ExecuteNonQueryAsync(ct);
            }
            await using (var g = new NpgsqlCommand("SELECT zentriq.apply_app_grants(@r)", conn))
            {
                g.Parameters.AddWithValue("r", appRole);
                await g.ExecuteNonQueryAsync(ct);
            }
            await using (var h = new NpgsqlCommand(
                "INSERT INTO zentriq.installation_history (action, to_version, outcome, detail) VALUES ('migrate', @p, 'succeeded', jsonb_build_object('applied', @n::int, 'schemaVersion', @s::int))", conn))
            {
                h.Parameters.AddWithValue("p", productVersion);
                h.Parameters.AddWithValue("n", status.Pending.Count);
                h.Parameters.AddWithValue("s", status.LatestVersion);
                await h.ExecuteNonQueryAsync(ct);
            }
            return status.Pending.Count;
        }
        finally
        {
            await using var u = new NpgsqlCommand("SELECT pg_advisory_unlock(@k)", conn);
            u.Parameters.AddWithValue("k", LockKey);
            await u.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private static async Task<Dictionary<int, string>> ReadAppliedAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var result = new Dictionary<int, string>();
        await using (var exists = new NpgsqlCommand("SELECT to_regclass('zentriq.schema_migrations') IS NOT NULL", conn))
            if (!(bool)(await exists.ExecuteScalarAsync(ct))!) return result;
        await using var cmd = new NpgsqlCommand("SELECT version, checksum FROM zentriq.schema_migrations", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result[r.GetInt32(0)] = r.GetString(1);
        return result;
    }
}
