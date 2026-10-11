using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Application.Security;

namespace Zentriq.Infrastructure.Audit;

public sealed record AuditActor(Guid? Id, string Name, string? SourceIp)
{
    public static AuditActor System(string component) => new(null, $"system:{component}", null);
}

/// <summary>
/// Writes append-only audit events. The hash chain is computed by a database trigger; the
/// application role has INSERT and SELECT only. Detail values are redacted before storage.
/// </summary>
public sealed class AuditWriter(NpgsqlDataSource dataSource)
{
    public async Task WriteAsync(AuditActor actor, string action, string? target, string outcome = "Success",
        object? detail = null, NpgsqlConnection? connection = null, NpgsqlTransaction? transaction = null, CancellationToken ct = default)
    {
        var json = detail is null ? "{}" : Redactor.Redact(JsonSerializer.Serialize(detail));
        const string sql = "INSERT INTO zentriq.audit_events (actor_id, actor_name, source_ip, action, target, outcome, detail) VALUES (@a, @n, @ip, @act, @t, @o, @d)";
        if (connection is not null)
        {
            await using var cmd = new NpgsqlCommand(sql, connection, transaction);
            Bind(cmd);
            await cmd.ExecuteNonQueryAsync(ct);
            return;
        }
        await using var c = dataSource.CreateCommand(sql);
        Bind(c);
        await c.ExecuteNonQueryAsync(ct);

        void Bind(NpgsqlCommand cmd)
        {
            cmd.Parameters.Add(new NpgsqlParameter("a", NpgsqlDbType.Uuid) { Value = (object?)actor.Id ?? DBNull.Value });
            cmd.Parameters.AddWithValue("n", actor.Name);
            cmd.Parameters.Add(new NpgsqlParameter("ip", NpgsqlDbType.Text) { Value = (object?)actor.SourceIp ?? DBNull.Value });
            cmd.Parameters.AddWithValue("act", action);
            cmd.Parameters.Add(new NpgsqlParameter("t", NpgsqlDbType.Text) { Value = (object?)target ?? DBNull.Value });
            cmd.Parameters.AddWithValue("o", outcome);
            cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Jsonb) { Value = json });
        }
    }
}
