using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Zentriq.Infrastructure.Deployment;

/// <summary>What the Zentriq tray icon offers on Windows devices.</summary>
public sealed class TraySettings
{
    public bool Enabled { get; set; } = true;
    public bool SoftwareCenter { get; set; } = true;
    public bool RemoteHelp { get; set; } = true;
    /// <summary>Shown in the tray windows ("Software Center — Contoso IT").</summary>
    public string? CompanyName { get; set; }
    /// <summary>How to reach IT otherwise (phone, e-mail, portal), shown in Get Remote Help.</summary>
    public string? SupportText { get; set; }
}

public sealed record CatalogItem(Guid Id, string Name, string? Version, string? Publisher, string? Description, string? Category, long Size, bool Approval,
    string State, string? Message, DateTimeOffset? Since);

public sealed record SelfServiceResult(string State, string Message);

/// <summary>
/// The Software Center of the tray icon: packages IT offers for self-service, their state on the device (available,
/// waiting for approval, installing, installed, failed), installation through the agent as SYSTEM (or a request when IT
/// wants to approve first); and help requests sent from the tray.
/// </summary>
public static class SelfService
{
    public const string SettingsKey = "tray.settings";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<TraySettings> SettingsAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand($"SELECT value::text FROM zentriq.system_settings WHERE key = '{SettingsKey}'");
        return await cmd.ExecuteScalarAsync(ct) is string s ? JsonSerializer.Deserialize<TraySettings>(s, Json) ?? new() : new();
    }

    public static async Task SaveSettingsAsync(NpgsqlDataSource ds, TraySettings s, CancellationToken ct)
    {
        s.CompanyName = Clip(s.CompanyName, 80);
        s.SupportText = Clip(s.SupportText, 400);
        await using var cmd = ds.CreateCommand("""
            INSERT INTO zentriq.system_settings (key, value, updated_at) VALUES (@k, @v::jsonb, now())
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now()
            """);
        cmd.Parameters.AddWithValue("k", SettingsKey);
        cmd.Parameters.AddWithValue("v", JsonSerializer.Serialize(s, Json));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length > max ? s.Trim()[..max] : s.Trim();

    private const string OfferedSql = $"""
        p.self_service AND p.active AND p.platform = @pf
        AND (cardinality(p.self_service_group_ids) = 0 OR EXISTS (
             SELECT 1 FROM zentriq.device_groups g JOIN zentriq.assets a ON a.id = @a WHERE g.id = ANY(p.self_service_group_ids) AND {AppControlStore.MemberSql}))
        """;

    /// <summary>The packages offered to this device, with their state on it.</summary>
    public static async Task<List<CatalogItem>> CatalogAsync(NpgsqlDataSource ds, Guid assetId, string platform, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand($"""
            SELECT p.id, p.name, p.version, p.publisher, p.self_service_description, p.self_service_category, p.size, p.self_service_approval,
                   r.status, r.decision_note, r.created_at, x.status, x.operation, x.detail, COALESCE(x.completed_at, x.requested_at)
              FROM zentriq.deployment_packages p
              LEFT JOIN LATERAL (SELECT status, decision_note, created_at FROM zentriq.self_service_requests q
                                  WHERE q.package_id = p.id AND q.asset_id = @a ORDER BY q.created_at DESC LIMIT 1) r ON true
              LEFT JOIN LATERAL (SELECT status, operation, detail, completed_at, requested_at FROM zentriq.asset_actions y
                                  WHERE y.kind = 'deploy' AND y.asset_id = @a AND y.target = p.id::text ORDER BY y.requested_at DESC LIMIT 1) x ON true
             WHERE {OfferedSql}
             ORDER BY lower(COALESCE(p.self_service_category, 'zz')), lower(p.name)
            """);
        cmd.Parameters.AddWithValue("a", assetId);
        cmd.Parameters.AddWithValue("pf", platform);
        var list = new List<CatalogItem>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            DateTimeOffset? T(int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);
            var request = S(8); var requestAt = T(10);
            var action = S(11); var op = S(12); var actionAt = T(14);
            string state; string? message = null; DateTimeOffset? since = null;
            // The newest of the request and the installation decides.
            if (request == "pending" && (actionAt is null || requestAt > actionAt)) { state = "pending"; message = "Waiting for approval by IT."; since = requestAt; }
            else if (request == "denied" && (actionAt is null || requestAt > actionAt) && requestAt > DateTimeOffset.UtcNow.AddDays(-7))
            { state = "denied"; message = "Not approved" + (S(9) is { } note ? ": " + note : "."); since = requestAt; }
            else if (action is "queued" or "sent" or "running") { state = op == "uninstall" ? "removing" : "installing"; message = op == "uninstall" ? "Being removed…" : "Installing…"; since = actionAt; }
            else if (action == "succeeded" && op == "install") { state = "installed"; since = actionAt; }
            else if (action is "failed" or "expired" && op == "install") { state = "failed"; message = S(13); since = actionAt; }
            else state = "available";
            list.Add(new CatalogItem(r.GetGuid(0), r.GetString(1), S(2), S(3), S(4), S(5), r.GetInt64(6), r.GetBoolean(7), state, message, since));
        }
        return list;
    }

    /// <summary>
    /// The user asked for a package: installed at once through the agent, or a request for IT when the package needs
    /// approval. <paramref name="fileExists"/> says whether the package file is on the server.
    /// </summary>
    public static async Task<SelfServiceResult> RequestAsync(NpgsqlDataSource ds, Guid packageId, Guid assetId, Guid agentId, string platform, string? user, string? reason,
        Func<Guid, bool> fileExists, CancellationToken ct)
    {
        var item = (await CatalogAsync(ds, assetId, platform, ct)).FirstOrDefault(i => i.Id == packageId);
        if (item is null) return new("unavailable", "This software is not offered to this device.");
        if (item.State is "pending" or "installing" or "removing") return new(item.State, item.Message ?? "Already in progress.");
        if (item.Approval)
        {
            await using var cmd = ds.CreateCommand("INSERT INTO zentriq.self_service_requests (package_id, asset_id, user_name, reason) VALUES (@p, @a, @u, @r)");
            cmd.Parameters.AddWithValue("p", packageId);
            cmd.Parameters.AddWithValue("a", assetId);
            cmd.Parameters.Add(new NpgsqlParameter("u", NpgsqlDbType.Text) { Value = (object?)Clip(user, 200) ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("r", NpgsqlDbType.Text) { Value = (object?)Clip(reason, 1000) ?? DBNull.Value });
            await cmd.ExecuteNonQueryAsync(ct);
            return new("pending", "Your request was sent to IT. The installation starts by itself once it is approved.");
        }
        if (!fileExists(packageId)) return new("failed", "The package is not available on the server. Contact IT.");
        await DeployAsync(ds, packageId, assetId, agentId, null, $"Software Center: requested by {Clip(user, 200) ?? "the user"}", ct);
        return new("installing", "Installing… You can keep working; you are told when it is done.");
    }

    /// <summary>One device installation of a package (a deployment job with one device), as from the console.</summary>
    public static async Task<Guid> DeployAsync(NpgsqlDataSource ds, Guid packageId, Guid assetId, Guid agentId, Guid? userId, string note, CancellationToken ct)
    {
        Dictionary<string, object?> argument; string name; string? version;
        await using (var pc = ds.CreateCommand("SELECT name, version, publisher, format, file_name, size, sha256, install_args, uninstall_args, detect_name FROM zentriq.deployment_packages WHERE id = @id"))
        {
            pc.Parameters.AddWithValue("id", packageId);
            await using var r = await pc.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw new KeyNotFoundException("The package was not found.");
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            name = r.GetString(0); version = S(1);
            argument = new()
            {
                ["name"] = name, ["version"] = version, ["publisher"] = S(2), ["format"] = r.GetString(3), ["fileName"] = r.GetString(4), ["size"] = r.GetInt64(5),
                ["sha256"] = r.GetString(6), ["installArgs"] = S(7), ["uninstallArgs"] = S(8), ["detectName"] = S(9),
            };
        }
        var jobId = Guid.NewGuid();
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var j = new NpgsqlCommand("INSERT INTO zentriq.deployment_jobs (id, package_id, operation, targets, note, created_by) VALUES (@id, @p, 'install', @t::jsonb, @n, @by)", conn, tx))
        {
            j.Parameters.AddWithValue("id", jobId);
            j.Parameters.AddWithValue("p", packageId);
            j.Parameters.AddWithValue("t", JsonSerializer.Serialize(new { assetIds = new[] { assetId }, selfService = true }, Json));
            j.Parameters.AddWithValue("n", note.Length > 500 ? note[..500] : note);
            j.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Uuid) { Value = (object?)userId ?? DBNull.Value });
            await j.ExecuteNonQueryAsync(ct);
        }
        Guid actionId;
        await using (var a = new NpgsqlCommand("""
            INSERT INTO zentriq.asset_actions (asset_id, agent_id, channel, kind, target, operation, argument, requested_by, detail)
            VALUES (@as, @ag, 'agent', 'deploy', @t, 'install', @arg, @by, @d) RETURNING id
            """, conn, tx))
        {
            a.Parameters.AddWithValue("as", assetId);
            a.Parameters.AddWithValue("ag", agentId);
            a.Parameters.AddWithValue("t", packageId.ToString());
            a.Parameters.AddWithValue("arg", JsonSerializer.Serialize(argument, Json));
            a.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Uuid) { Value = (object?)userId ?? DBNull.Value });
            a.Parameters.AddWithValue("d", $"Install {name}{(version is null ? "" : " " + version)} (Software Center)");
            actionId = (Guid)(await a.ExecuteScalarAsync(ct))!;
        }
        await using (var d = new NpgsqlCommand("INSERT INTO zentriq.deployment_job_devices (job_id, asset_id, action_id) VALUES (@j, @a, @x)", conn, tx))
        {
            d.Parameters.AddWithValue("j", jobId);
            d.Parameters.AddWithValue("a", assetId);
            d.Parameters.AddWithValue("x", actionId);
            await d.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return actionId;
    }

    /// <summary>IT approves a request: the installation starts on the device. Returns the package name (null: not pending).</summary>
    public static async Task<string?> DecideAsync(NpgsqlDataSource ds, Guid requestId, bool approve, string? note, Guid? userId, Func<Guid, bool> fileExists, CancellationToken ct)
    {
        Guid packageId, assetId; string name;
        await using (var q = ds.CreateCommand("""
            SELECT r.package_id, r.asset_id, p.name FROM zentriq.self_service_requests r JOIN zentriq.deployment_packages p ON p.id = r.package_id
             WHERE r.id = @id AND r.status = 'pending'
            """))
        {
            q.Parameters.AddWithValue("id", requestId);
            await using var r = await q.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            packageId = r.GetGuid(0); assetId = r.GetGuid(1); name = r.GetString(2);
        }
        Guid? actionId = null;
        if (approve)
        {
            if (!fileExists(packageId)) throw new InvalidOperationException("The package file is missing on the server. Upload the package again.");
            await using var ag = ds.CreateCommand("SELECT id FROM zentriq.agent_registrations WHERE asset_id = @a AND status = 'active' ORDER BY last_checkin_at DESC NULLS LAST LIMIT 1");
            ag.Parameters.AddWithValue("a", assetId);
            if (await ag.ExecuteScalarAsync(ct) is not Guid agentId) throw new InvalidOperationException("The device has no active Zentriq Agent any more.");
            actionId = await DeployAsync(ds, packageId, assetId, agentId, userId, "Software Center: request approved", ct);
        }
        await using var u = ds.CreateCommand("""
            UPDATE zentriq.self_service_requests SET status = @s, decided_by = @by, decided_at = now(), decision_note = @n, action_id = @x WHERE id = @id
            """);
        u.Parameters.AddWithValue("id", requestId);
        u.Parameters.AddWithValue("s", approve ? "approved" : "denied");
        u.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Uuid) { Value = (object?)userId ?? DBNull.Value });
        u.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Text) { Value = (object?)Clip(note, 500) ?? DBNull.Value });
        u.Parameters.Add(new NpgsqlParameter("x", NpgsqlDbType.Uuid) { Value = (object?)actionId ?? DBNull.Value });
        await u.ExecuteNonQueryAsync(ct);
        return name;
    }

    // ---- help requests ----------------------------------------------------------------------------------------------

    public static async Task<long> CreateHelpAsync(NpgsqlDataSource ds, Guid assetId, string? user, string message, string? contact, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("INSERT INTO zentriq.help_requests (asset_id, user_name, message, contact) VALUES (@a, @u, @m, @c) RETURNING number");
        cmd.Parameters.AddWithValue("a", assetId);
        cmd.Parameters.Add(new NpgsqlParameter("u", NpgsqlDbType.Text) { Value = (object?)Clip(user, 200) ?? DBNull.Value });
        cmd.Parameters.AddWithValue("m", Clip(message, 2000)!);
        cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Text) { Value = (object?)Clip(contact, 200) ?? DBNull.Value });
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
