using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Infrastructure.Secrets;

namespace Zentriq.Infrastructure.Backup;

public sealed record DestinationStatus(string? Status, string? Message, DateTimeOffset? At);
public sealed record DestinationRow(BackupDestination Destination, bool HasSecret, DestinationStatus Status, DateTimeOffset CreatedAt);

/// <summary>Result of copying one backup to one destination (kept with the run).</summary>
public sealed record DestinationResult(Guid Id, string Name, string Kind, string Status, string Message, int Deleted = 0);

/// <summary>Backup settings, destinations (secrets encrypted with the installation credential key) and the run history.</summary>
public sealed class BackupStore(NpgsqlDataSource ds, CredentialProtector protector)
{
    /// <summary>Associated data for the encrypted password key (so it cannot be swapped with another secret).</summary>
    public static readonly Guid PasswordPurpose = Guid.Parse("6b1f3c1e-5d2a-4c1e-9b7a-2f0d8a7b6c51");
    /// <summary>Associated data for a data key handed from the console to the maintenance task for a restore.</summary>
    public static readonly Guid RestorePurpose = Guid.Parse("0e4d7c55-91a3-4f6b-8d2e-7a5c3b1f9e20");

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<BackupSettings> LoadSettingsAsync(CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT value::text FROM zentriq.system_settings WHERE key = @k");
        cmd.Parameters.AddWithValue("k", BackupSettings.Key);
        return BackupSettings.Parse(await cmd.ExecuteScalarAsync(ct) as string);
    }

    public async Task SaveSettingsAsync(BackupSettings s, Guid? by, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            INSERT INTO zentriq.system_settings (key, value, updated_at, updated_by) VALUES (@k, @v::jsonb, now(), @by)
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now(), updated_by = EXCLUDED.updated_by
            """);
        cmd.Parameters.AddWithValue("k", BackupSettings.Key);
        cmd.Parameters.AddWithValue("v", s.ToJson());
        cmd.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Uuid) { Value = (object?)by ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public string ProtectPasswordKey(byte[] key) => protector.Protect(PasswordPurpose, new CredentialSecret { Password = Convert.ToBase64String(key) });

    /// <summary>The key derived from the backup password (null when no password is set).</summary>
    public BackupPasswordKey? PasswordKey(BackupSettings s)
    {
        if (!s.HasPassword || s.PasswordSalt is null || s.PasswordIterations is not { } it) return null;
        var k = protector.Unprotect(PasswordPurpose, s.PasswordKey!).Password;
        return k is null ? null : new BackupPasswordKey(s.PasswordSalt, it, Convert.FromBase64String(k));
    }

    /// <summary>
    /// What the console hands the maintenance task to open a backup for a restore, encrypted with the credential key:
    /// the backup password, a recovery key and/or the data key the console already unwrapped.
    /// </summary>
    public string ProtectUnlock(string? password, string? recoveryKey, byte[]? dataKey) =>
        protector.Protect(RestorePurpose, new CredentialSecret { Password = password, PrivateKey = recoveryKey, Passphrase = dataKey is null ? null : Convert.ToBase64String(dataKey) });

    public BackupUnlock UnprotectUnlock(string value)
    {
        var s = protector.Unprotect(RestorePurpose, value);
        return new BackupUnlock(s.PrivateKey is { Length: > 0 } k ? [k] : null, s.Password, s.Passphrase is { Length: > 0 } d ? Convert.FromBase64String(d) : null);
    }

    /// <summary>Fingerprint of this installation's backup key (written by the maintenance task, which can read the key).</summary>
    public async Task SetKeyFingerprintAsync(string fingerprint, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            INSERT INTO zentriq.system_settings (key, value, updated_at) VALUES ('backup.key_fingerprint', to_jsonb(@v::text), now())
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now()
            """);
        cmd.Parameters.AddWithValue("v", fingerprint);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> KeyFingerprintAsync(CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT value #>> '{}' FROM zentriq.system_settings WHERE key = 'backup.key_fingerprint'");
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    // ---- destinations ----------------------------------------------------------------------------

    private const string DestinationColumns = "id, name, kind, enabled, keep, settings::text, secret, last_status, last_message, last_at, created_at";

    private DestinationRow Read(NpgsqlDataReader r, bool withSecret)
    {
        var id = r.GetGuid(0);
        var secret = r.IsDBNull(6) ? null : r.GetString(6);
        var d = new BackupDestination
        {
            Id = id, Name = r.GetString(1), Kind = r.GetString(2), Enabled = r.GetBoolean(3), Keep = r.GetInt32(4),
            Settings = JsonSerializer.Deserialize<BackupTargetSettings>(r.GetString(5), Json) ?? new(),
            Secret = withSecret && secret is not null ? protector.Unprotect(id, secret) : new CredentialSecret(),
        };
        return new DestinationRow(d, secret is not null,
            new DestinationStatus(r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9)),
            r.GetFieldValue<DateTimeOffset>(10));
    }

    public async Task<List<DestinationRow>> DestinationsAsync(bool withSecrets, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand($"SELECT {DestinationColumns} FROM zentriq.backup_destinations ORDER BY lower(name)");
        var list = new List<DestinationRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Read(r, withSecrets));
        return list;
    }

    public async Task<DestinationRow?> DestinationAsync(Guid id, bool withSecret, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand($"SELECT {DestinationColumns} FROM zentriq.backup_destinations WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r, withSecret) : null;
    }

    /// <summary>Inserts or updates; the secret is replaced only when <paramref name="replaceSecret"/> (blank fields keep the stored one).</summary>
    public async Task SaveDestinationAsync(BackupDestination d, bool insert, bool replaceSecret, Guid? by, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(insert
            ? """
              INSERT INTO zentriq.backup_destinations (id, name, kind, enabled, keep, settings, secret, created_by)
              VALUES (@id, @n, @k, @e, @keep, @s::jsonb, @sec, @by)
              """
            : """
              UPDATE zentriq.backup_destinations SET name = @n, enabled = @e, keep = @keep, settings = @s::jsonb,
                     secret = CASE WHEN @replace THEN @sec ELSE secret END, updated_at = now()
               WHERE id = @id
              """);
        cmd.Parameters.AddWithValue("id", d.Id);
        cmd.Parameters.AddWithValue("n", d.Name.Trim());
        cmd.Parameters.AddWithValue("k", d.Kind);
        cmd.Parameters.AddWithValue("e", d.Enabled);
        cmd.Parameters.AddWithValue("keep", d.Keep);
        cmd.Parameters.AddWithValue("s", JsonSerializer.Serialize(d.Settings, Json));
        var hasSecret = !string.IsNullOrEmpty(d.Secret.Password) || !string.IsNullOrEmpty(d.Secret.PrivateKey);
        cmd.Parameters.Add(new NpgsqlParameter("sec", NpgsqlDbType.Text) { Value = hasSecret ? protector.Protect(d.Id, d.Secret) : DBNull.Value });
        cmd.Parameters.AddWithValue("replace", replaceSecret);
        cmd.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Uuid) { Value = (object?)by ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> DeleteDestinationAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("DELETE FROM zentriq.backup_destinations WHERE id = @id RETURNING name");
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task SetStatusAsync(Guid id, bool ok, string message, string? pinHostKey, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            UPDATE zentriq.backup_destinations SET last_status = @s, last_message = @m, last_at = now(),
                   settings = CASE WHEN @hk::text IS NOT NULL AND COALESCE(settings ->> 'hostKey', '') = '' THEN settings || jsonb_build_object('hostKey', @hk::text) ELSE settings END
             WHERE id = @id
            """);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("s", ok ? "ok" : "failed");
        cmd.Parameters.AddWithValue("m", message.Length > 1000 ? message[..1000] : message);
        cmd.Parameters.Add(new NpgsqlParameter("hk", NpgsqlDbType.Text) { Value = (object?)pinHostKey ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---- runs -------------------------------------------------------------------------------------

    public async Task<Guid> StartRunAsync(string trigger, string label, string fileName, string? requestedBy, bool passwordProtected, string version, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await using var cmd = ds.CreateCommand("""
            INSERT INTO zentriq.backup_runs (id, trigger, label, file_name, status, password_protected, requested_by, product_version)
            VALUES (@id, @t, @l, @f, 'running', @pw, @by, @v)
            """);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("t", trigger);
        cmd.Parameters.AddWithValue("l", label);
        cmd.Parameters.AddWithValue("f", fileName);
        cmd.Parameters.AddWithValue("pw", passwordProtected);
        cmd.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Text) { Value = (object?)requestedBy ?? DBNull.Value });
        cmd.Parameters.AddWithValue("v", version);
        await cmd.ExecuteNonQueryAsync(ct);
        return id;
    }

    public async Task FinishRunAsync(Guid id, string status, long? size, string? sha256, IEnumerable<DestinationResult> destinations, object? contents, string? error, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            UPDATE zentriq.backup_runs SET status = @s, size_bytes = @size, sha256 = @sha, destinations = @d::jsonb, contents = @c::jsonb,
                   error = @e, finished_at = now()
             WHERE id = @id
            """);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("s", status);
        cmd.Parameters.Add(new NpgsqlParameter("size", NpgsqlDbType.Bigint) { Value = (object?)size ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("sha", NpgsqlDbType.Text) { Value = (object?)sha256 ?? DBNull.Value });
        cmd.Parameters.AddWithValue("d", JsonSerializer.Serialize(destinations, Json));
        cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Text) { Value = contents is null ? DBNull.Value : JsonSerializer.Serialize(contents, Json) });
        cmd.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.Text) { Value = error is null ? DBNull.Value : error.Length > 4000 ? error[..4000] : error });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<DateTimeOffset?> LastScheduledStartAsync(CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT max(started_at) FROM zentriq.backup_runs WHERE trigger = 'schedule'");
        // timestamptz comes back as a UTC DateTime.
        return await cmd.ExecuteScalarAsync(ct) switch
        {
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            DateTimeOffset o => o,
            _ => null,
        };
    }

    /// <summary>Marks local files that retention removed.</summary>
    public async Task MarkLocalDeletedAsync(IEnumerable<string> files, CancellationToken ct)
    {
        var names = files.ToArray();
        if (names.Length == 0) return;
        await using var cmd = ds.CreateCommand("UPDATE zentriq.backup_runs SET local_deleted_at = now() WHERE file_name = ANY(@f) AND local_deleted_at IS NULL");
        cmd.Parameters.Add(new NpgsqlParameter("f", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = names });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
