using System.Net;
using System.Text.Json;
using Npgsql;
using Zentriq.Domain;
using Zentriq.Infrastructure.Secrets;
using Zentriq.Worker.Agentless;

namespace Zentriq.Worker.Handlers;

/// <summary>
/// Runs an agentless device action (service start/stop/restart) over WinRM, falling back to DCOM/WMI, with the
/// stored credential or the one typed for this action. A typed password is deleted when the action finishes.
/// </summary>
public sealed class AssetActionHandler(NpgsqlDataSource ds, CredentialProtector protector, ILogger<AssetActionHandler> logger) : IJobHandler
{
    public string JobType => JobTypes.AssetAction;

    public async Task<object?> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var actionId = JsonDocument.Parse(context.Job.Payload).RootElement.GetProperty("actionId").GetGuid();
        string target, operation; Guid? credId; string? username, secret; string? fqdn, hostname, ip;
        await using (var cmd = ds.CreateCommand("""
            UPDATE zentriq.asset_actions x SET status = 'running', sent_at = now()
              FROM zentriq.assets a WHERE x.id = @id AND a.id = x.asset_id AND x.status = 'queued'
            RETURNING x.target, x.operation, x.credential_id, x.username, x.secret_protected, a.fqdn, a.hostname, host(a.primary_ip)
            """))
        {
            cmd.Parameters.AddWithValue("id", actionId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return new { skipped = true };
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            target = r.GetString(0); operation = r.GetString(1); credId = r.IsDBNull(2) ? null : r.GetGuid(2);
            username = S(3); secret = S(4); fqdn = S(5); hostname = S(6); ip = S(7);
        }

        async Task<object?> Finish(bool ok, string? state, string message)
        {
            await Endpoints.CompleteAsync(ds, actionId, ok, state, message, ct);
            await context.LogAsync(message, ok ? "info" : "error", ct);
            return new { ok, state, message };
        }

        ScanCredential cred;
        try
        {
            if (credId is { } cid)
            {
                var list = await ScanCredential.LoadAsync(ds, protector, [cid], ct);
                if (list.Count == 0) return await Finish(false, null, "The selected credential no longer exists.");
                cred = list[0];
            }
            else if (username is not null && secret is not null)
                cred = new ScanCredential(actionId, "typed for this action", username.Contains('\\') && !username.StartsWith(".\\") ? "windows_domain" : "windows_local",
                    username, protector.Unprotect(actionId, secret), default, []);
            else return await Finish(false, null, "No credential was given for this device.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return await Finish(false, null, "The credential could not be decrypted: " + ex.Message); }

        if (ip is not null && IPAddress.TryParse(ip, out var addr) && !cred.AllowedFor(addr))
            return await Finish(false, null, $"Credential '{cred.Name}' is not allowed for {ip} (credential scope).");

        var hosts = new[] { fqdn, hostname, ip }.Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var errors = new List<string>();
        foreach (var useDcom in new[] { false, true })
            foreach (var host in hosts)
            {
                try
                {
                    var result = await Task.Run(() =>
                    {
                        using var session = WinRmCollector.CreateSession(host!, cred, useDcom, false, 5985, TimeSpan.FromSeconds(30));
                        return WinRmCollector.ControlService(session, target, operation, TimeSpan.FromSeconds(60));
                    }, ct);
                    return await Finish(result.Ok, result.State, $"{result.Message} ({host} via {(useDcom ? "DCOM/WMI" : "WinRM")}, account {cred.Username})");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    var msg = Zentriq.Application.Security.Redactor.Redact(ex.Message);
                    errors.Add($"{host} via {(useDcom ? "DCOM" : "WinRM")}: {msg}");
                    logger.LogDebug(ex, "Service action {Action} on {Host} failed", actionId, host);
                    if (WinRmCollector.IsAccessDenied(ex))
                        return await Finish(false, null, $"Access denied for {cred.Username} on {host}. Use an account that is an administrator on the device.");
                }
            }
        return await Finish(false, null, "Could not reach the device over WinRM (5985) or DCOM (135): " + string.Join(" | ", errors.Take(4)));
    }

    /// <summary>Same completion logic as the API (kept here to avoid a dependency on the API project).</summary>
    private static class Endpoints
    {
        public static async Task CompleteAsync(NpgsqlDataSource ds, Guid actionId, bool ok, string? state, string message, CancellationToken ct)
        {
            await using var cmd = ds.CreateCommand("""
                WITH done AS (
                    UPDATE zentriq.asset_actions SET status = @st, result_state = @rs, detail = @d, completed_at = now(), secret_protected = NULL
                     WHERE id = @id RETURNING asset_id, target)
                UPDATE zentriq.asset_inventory_documents doc
                   SET document = jsonb_set(doc.document, '{services}', (
                        SELECT COALESCE(jsonb_agg(CASE WHEN lower(s ->> 'name') = lower(done.target) THEN s || jsonb_build_object('state', @rs::text) ELSE s END ORDER BY o), '[]')
                          FROM jsonb_array_elements(doc.document -> 'services') WITH ORDINALITY AS e(s, o)))
                  FROM done
                 WHERE doc.asset_id = done.asset_id AND @rs::text IS NOT NULL AND jsonb_typeof(doc.document -> 'services') = 'array'
                """);
            cmd.Parameters.AddWithValue("id", actionId);
            cmd.Parameters.AddWithValue("st", ok ? "succeeded" : "failed");
            cmd.Parameters.Add(new NpgsqlParameter("rs", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)state ?? DBNull.Value });
            cmd.Parameters.AddWithValue("d", Zentriq.Application.Security.Redactor.Redact(message));
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
