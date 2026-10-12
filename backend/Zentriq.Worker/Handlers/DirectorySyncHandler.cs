using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Domain;
using Zentriq.Infrastructure.ActiveDirectory;

namespace Zentriq.Worker.Handlers;

/// <summary>
/// Reads organizational units, users, groups and computers from a saved Active Directory connection into
/// directory_objects (read-only LDAP; nothing is written to AD). Objects no longer present are removed.
/// </summary>
public sealed class DirectorySyncHandler(NpgsqlDataSource ds, DirectoryStore store, ILogger<DirectorySyncHandler> logger) : IJobHandler
{
    public string JobType => JobTypes.DirectorySync;

    private static readonly (string Label, string Filter, string Class)[] Kinds =
    [
        ("organizational units", DirectoryClient.OrganizationalUnits, "organizationalUnit"),
        ("computers", DirectoryClient.Computers, "computer"),
        ("users", DirectoryClient.Users, "user"),
        ("groups", DirectoryClient.Groups, "group"),
    ];

    public async Task<object?> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var id = JsonDocument.Parse(context.Job.Payload).RootElement.GetProperty("connectionId").GetGuid();
        DirectoryConnectionInfo info;
        try { info = await store.LoadAsync(id, ct) ?? throw new PermanentJobException("The Active Directory connection no longer exists."); }
        catch (Exception ex) when (ex is not (OperationCanceledException or PermanentJobException))
        {
            throw new PermanentJobException("The directory password could not be decrypted: " + ex.Message);
        }
        await SetStatusAsync(id, "running", null, null, ct);
        await using var limitCmd = ds.CreateCommand("SELECT value::text FROM zentriq.system_settings WHERE key = 'discovery.ad_page_limit'");
        var limit = int.TryParse(((await limitCmd.ExecuteScalarAsync(ct)) as string)?.Trim('"'), out var l) ? l : 20000;

        var counts = new Dictionary<string, int>();
        var started = DateTimeOffset.UtcNow;
        try
        {
            using var ldap = await Task.Run(() => DirectoryClient.Connect(info.Settings, TimeSpan.FromSeconds(60)), ct);
            await context.LogAsync($"Connected to {info.Settings.ServerHost}:{info.Settings.Port} ({(info.Settings.Ldaps ? "LDAPS" : "LDAP signed/sealed")}) as {info.Settings.Username}; reading {info.Settings.BaseDn}.", ct: ct);
            var step = 0;
            foreach (var (label, filter, cls) in Kinds)
            {
                ct.ThrowIfCancellationRequested();
                await context.ProgressAsync(step++ * 100 / Kinds.Length, $"Reading {label}", ct);
                var entries = await Task.Run(() => DirectoryClient.Search(ldap, info.Settings.BaseDn, filter, cls, limit).ToList(), ct);
                await using var conn = await ds.OpenConnectionAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                foreach (var e in entries) await store.UpsertAsync(conn, id, e, ct);
                await tx.CommitAsync(ct);
                counts[cls] = entries.Count;
                await context.LogAsync($"{entries.Count} {label}{(entries.Count >= limit ? $" (limit {limit} reached)" : "")}.", ct: ct);
            }
        }
        catch (DirectoryException ex)
        {
            await SetStatusAsync(id, "failed", ex.Message, null, ct);
            throw new PermanentJobException(ex.Message);
        }
        catch (System.DirectoryServices.Protocols.LdapException ex)
        {
            var msg = DirectoryClient.Explain(ex, info.Settings);
            await SetStatusAsync(id, "failed", msg, null, ct);
            throw new PermanentJobException(msg);
        }
        catch (System.DirectoryServices.Protocols.DirectoryOperationException ex)
        {
            var msg = ex.Response?.ResultCode == System.DirectoryServices.Protocols.ResultCode.NoSuchObject
                ? $"'{info.Settings.BaseDn}' does not exist in this domain. Check the domain / OU distinguished name."
                : $"Directory query failed: {ex.Message}";
            await SetStatusAsync(id, "failed", msg, null, ct);
            throw new PermanentJobException(msg);
        }

        // Remove objects that were not seen in this sync (deleted or moved out of scope).
        await using (var del = ds.CreateCommand("DELETE FROM zentriq.directory_objects WHERE connection_id = @id AND synced_at < @t"))
        {
            del.Parameters.AddWithValue("id", id);
            del.Parameters.AddWithValue("t", started.AddSeconds(-1));
            var removed = await del.ExecuteNonQueryAsync(ct);
            if (removed > 0) await context.LogAsync($"{removed} object(s) no longer in the directory were removed.", ct: ct);
        }
        await SetStatusAsync(id, "succeeded", null, counts, ct);
        logger.LogInformation("Directory sync {Name}: {Counts}", info.Name, string.Join(", ", counts.Select(c => $"{c.Key}={c.Value}")));
        return new { counts };
    }

    private async Task SetStatusAsync(Guid id, string status, string? error, Dictionary<string, int>? counts, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand($"""
            UPDATE zentriq.directory_connections SET last_sync_status = @s, last_error = @e
                   {(status is "succeeded" or "failed" ? ", last_sync_at = now()" : "")}
                   {(counts is not null ? ", counts = @c, last_validated_at = now()" : "")}
             WHERE id = @id
            """);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("s", status);
        cmd.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.Text) { Value = (object?)error ?? DBNull.Value });
        if (counts is not null) cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(counts) });
        await cmd.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
