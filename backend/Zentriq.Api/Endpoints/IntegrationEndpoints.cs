using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;
using Zentriq.Infrastructure.Integrations;

namespace Zentriq.Api.Endpoints;

/// <summary>
/// Integrations & Automation: notification channels (signed webhooks, Slack, Teams, e-mail, syslog/CEF for SIEM,
/// ServiceNow incidents, Jira issues), alert rules over Zentriq events, the delivery log with retries, and read-only
/// API tokens for scripts, BI tools and SIEM pulls.
/// </summary>
public static partial class IntegrationEndpoints
{
    public sealed record ChannelBody(string Kind, string Name, bool? Enabled, ChannelSettings? Settings, ChannelSecrets? Secrets, string[]? ClearSecrets);
    public sealed record RuleBody(string Name, bool? Enabled, string EventType, string? MinSeverity, RuleConditions? Conditions, Guid[]? ChannelIds, int? ThrottleMinutes);
    public sealed record TokenBody(string Name, string[]? Permissions, int? ExpiresInDays);
    public sealed record SettingsBody(int? OfflineHours);

    /// <summary>Permissions an API token can carry (read-only data access).</summary>
    public static readonly string[] TokenPermissions = [Permissions.AssetsView, Permissions.VulnView, Permissions.SecurityView, Permissions.ReportsExport, Permissions.AuditView, Permissions.JobsView];

    [GeneratedRegex(@"^[^@\s,;]+@[^@\s,;]+\.[^@\s,;]+$")]
    private static partial Regex EmailRegex();

    public static void MapIntegrationEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/integrations").WithTags("Integrations").RequireModule(Modules.Integrations).RequireAuthorization(Permissions.IntegrationsManage);

        g.MapGet("/overview", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT (SELECT count(*) FROM zentriq.int_channels), (SELECT count(*) FROM zentriq.int_channels WHERE enabled),
                       (SELECT count(*) FROM zentriq.int_rules), (SELECT count(*) FROM zentriq.int_rules WHERE enabled),
                       (SELECT count(*) FROM zentriq.int_deliveries WHERE status = 'sent' AND created_at > now() - interval '24 hours'),
                       (SELECT count(*) FROM zentriq.int_deliveries WHERE status = 'failed' AND created_at > now() - interval '24 hours'),
                       (SELECT count(*) FROM zentriq.int_deliveries WHERE status = 'queued'),
                       (SELECT count(*) FROM zentriq.int_events WHERE occurred_at > now() - interval '24 hours' AND event_type <> 'test'),
                       (SELECT count(*) FROM zentriq.api_tokens WHERE revoked_at IS NULL AND (expires_at IS NULL OR expires_at > now()))
                """);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            return Results.Ok(new
            {
                channels = r.GetInt64(0), channelsEnabled = r.GetInt64(1), rules = r.GetInt64(2), rulesEnabled = r.GetInt64(3),
                sent24h = r.GetInt64(4), failed24h = r.GetInt64(5), queued = r.GetInt64(6), events24h = r.GetInt64(7), tokens = r.GetInt64(8),
            });
        });

        g.MapGet("/catalog", () => Results.Ok(new
        {
            eventTypes = EventTypes.All.Select(e => new { type = e.Type, name = e.Name, description = e.Description, perDevice = e.PerDevice }),
            severities = EventTypes.Severities,
            channelKinds = ChannelKinds.All,
            tokenPermissions = TokenPermissions,
        }));

        // ---- channels --------------------------------------------------------------------------------------

        g.MapGet("/channels", async (NpgsqlDataSource ds, IntegrationStore store, CancellationToken ct) =>
        {
            var list = new List<object>();
            await using var cmd = ds.CreateCommand("""
                SELECT c.id, c.kind, c.name, c.enabled, c.settings::text, c.secret_protected IS NOT NULL, c.last_ok_at, c.last_error_at, c.last_error, c.updated_at,
                       (SELECT count(*) FROM zentriq.int_rules r WHERE c.id = ANY(r.channel_ids)),
                       (SELECT count(*) FROM zentriq.int_deliveries d WHERE d.channel_id = c.id AND d.status = 'sent' AND d.created_at > now() - interval '7 days'),
                       (SELECT count(*) FROM zentriq.int_deliveries d WHERE d.channel_id = c.id AND d.status = 'failed' AND d.created_at > now() - interval '7 days'),
                       c.secret_protected
                  FROM zentriq.int_channels c ORDER BY c.name
                """);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = r.GetGuid(0);
                ChannelSecrets secrets;
                try { secrets = store.UnprotectSecrets(id, r.IsDBNull(13) ? null : r.GetString(13)); }
                catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or InvalidOperationException) { secrets = new(); }
                list.Add(new
                {
                    id, kind = r.GetString(1), name = r.GetString(2), enabled = r.GetBoolean(3),
                    settings = JsonSerializer.Deserialize<ChannelSettings>(r.GetString(4), IntegrationStore.Json),
                    secretsSet = SecretFlags(secrets),
                    lastOkAt = r.IsDBNull(6) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(6), lastErrorAt = r.IsDBNull(7) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(7),
                    lastError = r.IsDBNull(8) ? null : r.GetString(8), updatedAt = r.GetFieldValue<DateTimeOffset>(9),
                    rules = r.GetInt64(10), sent7d = r.GetInt64(11), failed7d = r.GetInt64(12),
                });
            }
            return Results.Ok(list);
        });

        g.MapPost("/channels", async (ChannelBody body, NpgsqlDataSource ds, IntegrationStore store, RequestContext rc, CancellationToken ct) =>
        {
            var secrets = Merge(new ChannelSecrets(), body.Secrets, body.ClearSecrets);
            var settings = body.Settings ?? new ChannelSettings();
            if (ValidateChannel(body.Kind, body.Name, settings, secrets) is { } problem) return problem;
            settings.SecretHint = Hint(body.Kind, secrets);
            var id = Guid.NewGuid();
            await using var cmd = ds.CreateCommand("""
                INSERT INTO zentriq.int_channels (id, kind, name, enabled, settings, secret_protected, created_by) VALUES (@id, @k, @n, @e, @s::jsonb, @p, @u)
                """);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("k", body.Kind);
            cmd.Parameters.AddWithValue("n", body.Name.Trim());
            cmd.Parameters.AddWithValue("e", body.Enabled ?? true);
            cmd.Parameters.AddWithValue("s", JsonSerializer.Serialize(settings, IntegrationStore.Json));
            cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Text) { Value = HasAny(secrets) ? store.ProtectSecrets(id, secrets) : DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("u", NpgsqlDbType.Uuid) { Value = (object?)rc.UserId ?? DBNull.Value });
            try { await cmd.ExecuteNonQueryAsync(ct); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Validation.Problem("name", "A channel with this name already exists."); }
            await rc.AuditAsync("integration.channel.create", body.Name.Trim(), detail: new { body.Kind });
            return Results.Created($"/api/integrations/channels/{id}", new { id });
        });

        g.MapPut("/channels/{id:guid}", async (Guid id, ChannelBody body, NpgsqlDataSource ds, IntegrationStore store, RequestContext rc, CancellationToken ct) =>
        {
            var current = await store.ChannelAsync(id, ct);
            if (current is null) return Results.NotFound();
            if (body.Kind != current.Kind) return Validation.Problem("kind", "The channel type cannot be changed; create a new channel instead.");
            var secrets = Merge(current.Secrets, body.Secrets, body.ClearSecrets);
            var settings = body.Settings ?? current.Settings;
            if (ValidateChannel(body.Kind, body.Name, settings, secrets) is { } problem) return problem;
            settings.SecretHint = Hint(body.Kind, secrets);
            await using var cmd = ds.CreateCommand("""
                UPDATE zentriq.int_channels SET name = @n, enabled = COALESCE(@e, enabled), settings = @s::jsonb, secret_protected = @p, updated_at = now() WHERE id = @id
                """);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("n", body.Name.Trim());
            cmd.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.Boolean) { Value = (object?)body.Enabled ?? DBNull.Value });
            cmd.Parameters.AddWithValue("s", JsonSerializer.Serialize(settings, IntegrationStore.Json));
            cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Text) { Value = HasAny(secrets) ? store.ProtectSecrets(id, secrets) : DBNull.Value });
            try { await cmd.ExecuteNonQueryAsync(ct); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Validation.Problem("name", "A channel with this name already exists."); }
            await rc.AuditAsync("integration.channel.update", body.Name.Trim(), detail: new { id, body.Enabled, secretsChanged = body.Secrets is not null || body.ClearSecrets is { Length: > 0 } });
            return Results.NoContent();
        });

        g.MapDelete("/channels/{id:guid}", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("DELETE FROM zentriq.int_channels WHERE id = @id RETURNING name");
            cmd.Parameters.AddWithValue("id", id);
            if (await cmd.ExecuteScalarAsync(ct) is not string name) return Results.NotFound();
            await using (var rules = ds.CreateCommand("UPDATE zentriq.int_rules SET channel_ids = array_remove(channel_ids, @id) WHERE @id = ANY(channel_ids)"))
            {
                rules.Parameters.AddWithValue("id", id);
                await rules.ExecuteNonQueryAsync(ct);
            }
            await rc.AuditAsync("integration.channel.delete", name);
            return Results.NoContent();
        });

        g.MapPost("/channels/{id:guid}/test", async (Guid id, IntegrationStore store, RequestContext rc, CancellationToken ct) =>
        {
            var ch = await store.ChannelAsync(id, ct);
            if (ch is null) return Results.NotFound();
            var res = await store.TestAsync(ch, rc.UserName, ct);
            await rc.AuditAsync("integration.channel.test", ch.Name, res.Ok ? "Success" : "Failure", new { res.Response, res.Error });
            return Results.Ok(new { ok = res.Ok, response = res.Ticket ?? (res.Response is { Length: > 200 } x ? x[..200] : res.Response), error = res.Error });
        });

        // ---- alert rules -------------------------------------------------------------------------------------

        g.MapGet("/rules", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var list = new List<object>();
            await using var cmd = ds.CreateCommand("""
                SELECT r.id, r.name, r.enabled, r.event_type, r.min_severity, r.conditions::text, r.channel_ids, r.throttle_minutes, r.last_fired_at, r.updated_at,
                       (SELECT count(*) FROM zentriq.int_deliveries d WHERE d.rule_id = r.id AND d.status = 'sent' AND d.created_at > now() - interval '7 days')
                  FROM zentriq.int_rules r ORDER BY r.name
                """);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new
                {
                    id = r.GetGuid(0), name = r.GetString(1), enabled = r.GetBoolean(2), eventType = r.GetString(3), minSeverity = r.GetString(4),
                    conditions = JsonSerializer.Deserialize<RuleConditions>(r.GetString(5), IntegrationStore.Json), channelIds = r.GetFieldValue<Guid[]>(6),
                    throttleMinutes = r.GetInt32(7), lastFiredAt = r.IsDBNull(8) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(8),
                    updatedAt = r.GetFieldValue<DateTimeOffset>(9), sent7d = r.GetInt64(10),
                });
            return Results.Ok(list);
        });

        g.MapPost("/rules", async (RuleBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (await ValidateRuleAsync(body, ds, ct) is { } problem) return problem;
            var id = Guid.NewGuid();
            await using var cmd = ds.CreateCommand("""
                INSERT INTO zentriq.int_rules (id, name, enabled, event_type, min_severity, conditions, channel_ids, throttle_minutes, created_by)
                VALUES (@id, @n, @e, @t, @s, @c::jsonb, @ch, @th, @u)
                """);
            FillRule(cmd, id, body);
            cmd.Parameters.Add(new NpgsqlParameter("u", NpgsqlDbType.Uuid) { Value = (object?)rc.UserId ?? DBNull.Value });
            await cmd.ExecuteNonQueryAsync(ct);
            await rc.AuditAsync("integration.rule.create", body.Name.Trim(), detail: new { body.EventType, body.MinSeverity, body.ChannelIds });
            return Results.Created($"/api/integrations/rules/{id}", new { id });
        });

        g.MapPut("/rules/{id:guid}", async (Guid id, RuleBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (await ValidateRuleAsync(body, ds, ct) is { } problem) return problem;
            await using var cmd = ds.CreateCommand("""
                UPDATE zentriq.int_rules SET name = @n, enabled = @e, event_type = @t, min_severity = @s, conditions = @c::jsonb, channel_ids = @ch,
                       throttle_minutes = @th, updated_at = now() WHERE id = @id
                """);
            FillRule(cmd, id, body);
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            await rc.AuditAsync("integration.rule.update", body.Name.Trim(), detail: new { id, body.Enabled, body.EventType, body.MinSeverity, body.ChannelIds });
            return Results.NoContent();
        });

        g.MapDelete("/rules/{id:guid}", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("DELETE FROM zentriq.int_rules WHERE id = @id RETURNING name");
            cmd.Parameters.AddWithValue("id", id);
            if (await cmd.ExecuteScalarAsync(ct) is not string name) return Results.NotFound();
            await rc.AuditAsync("integration.rule.delete", name);
            return Results.NoContent();
        });

        // ---- delivery log & events ---------------------------------------------------------------------------

        g.MapGet("/deliveries", async (string? status, Guid? channelId, int? page, int? pageSize, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var (p, ps) = Paging.Normalize(page, pageSize);
            await using var cmd = ds.CreateCommand("""
                SELECT d.id, d.status, d.attempts, d.response, d.error, d.created_at, d.sent_at, d.next_attempt_at,
                       e.id, e.event_type, e.severity, e.title, c.id, c.name, c.kind, r.name, COALESCE(a.hostname, zentriq.asset_ipv4(a.id), host(a.primary_ip)), count(*) OVER ()
                  FROM zentriq.int_deliveries d
                  JOIN zentriq.int_events e ON e.id = d.event_id
                  LEFT JOIN zentriq.int_channels c ON c.id = d.channel_id
                  LEFT JOIN zentriq.int_rules r ON r.id = d.rule_id
                  LEFT JOIN zentriq.assets a ON a.id = e.asset_id
                 WHERE (@s::text IS NULL OR d.status = @s) AND (@c::uuid IS NULL OR d.channel_id = @c)
                 ORDER BY d.id DESC LIMIT @l OFFSET @o
                """);
            cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Text) { Value = status is "queued" or "sent" or "failed" or "suppressed" ? status : DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Uuid) { Value = (object?)channelId ?? DBNull.Value });
            cmd.Parameters.AddWithValue("l", ps);
            cmd.Parameters.AddWithValue("o", (p - 1) * ps);
            var items = new List<object>();
            long total = 0;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                total = r.GetInt64(17);
                items.Add(new
                {
                    id = r.GetInt64(0), status = r.GetString(1), attempts = r.GetInt32(2), response = r.IsDBNull(3) ? null : r.GetString(3), error = r.IsDBNull(4) ? null : r.GetString(4),
                    createdAt = r.GetFieldValue<DateTimeOffset>(5), sentAt = r.IsDBNull(6) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(6),
                    nextAttemptAt = r.GetFieldValue<DateTimeOffset>(7), eventId = r.GetInt64(8), eventType = r.GetString(9), severity = r.GetString(10), title = r.GetString(11),
                    channelId = r.IsDBNull(12) ? (Guid?)null : r.GetGuid(12), channel = r.IsDBNull(13) ? null : r.GetString(13), kind = r.IsDBNull(14) ? null : r.GetString(14),
                    rule = r.IsDBNull(15) ? null : r.GetString(15), hostname = r.IsDBNull(16) ? null : r.GetString(16),
                });
            }
            return Results.Ok(new { items, total, page = p, pageSize = ps });
        });

        g.MapPost("/deliveries/{id:long}/retry", async (long id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                UPDATE zentriq.int_deliveries SET status = 'queued', attempts = 0, next_attempt_at = now(), error = NULL
                 WHERE id = @id AND status IN ('failed','suppressed') AND channel_id IS NOT NULL
                """);
            cmd.Parameters.AddWithValue("id", id);
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) return Results.Problem(statusCode: 409, title: "Only failed or throttled deliveries can be sent again.");
            await rc.AuditAsync("integration.delivery.retry", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return Results.Accepted(value: new { message = "Queued; it is sent within a minute." });
        });

        g.MapGet("/events", async (string? type, int? page, int? pageSize, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var (p, ps) = Paging.Normalize(page, pageSize);
            await using var cmd = ds.CreateCommand("""
                SELECT e.id, e.event_type, e.severity, e.title, left(e.body, 2000), e.occurred_at, e.link, e.asset_id, COALESCE(a.hostname, zentriq.asset_ipv4(a.id), host(a.primary_ip)),
                       (SELECT count(*) FROM zentriq.int_deliveries d WHERE d.event_id = e.id AND d.status = 'sent'),
                       (SELECT count(*) FROM zentriq.int_deliveries d WHERE d.event_id = e.id), count(*) OVER ()
                  FROM zentriq.int_events e LEFT JOIN zentriq.assets a ON a.id = e.asset_id
                 WHERE (@t::text IS NULL OR e.event_type = @t) ORDER BY e.id DESC LIMIT @l OFFSET @o
                """);
            cmd.Parameters.Add(new NpgsqlParameter("t", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(type) ? DBNull.Value : type });
            cmd.Parameters.AddWithValue("l", ps);
            cmd.Parameters.AddWithValue("o", (p - 1) * ps);
            var items = new List<object>();
            long total = 0;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                total = r.GetInt64(11);
                items.Add(new
                {
                    id = r.GetInt64(0), type = r.GetString(1), severity = r.GetString(2), title = r.GetString(3), body = r.GetString(4), occurredAt = r.GetFieldValue<DateTimeOffset>(5),
                    link = r.IsDBNull(6) ? null : r.GetString(6), assetId = r.IsDBNull(7) ? (Guid?)null : r.GetGuid(7), hostname = r.IsDBNull(8) ? null : r.GetString(8),
                    sent = r.GetInt64(9), deliveries = r.GetInt64(10),
                });
            }
            return Results.Ok(new { items, total, page = p, pageSize = ps });
        });

        g.MapGet("/settings", async (IntegrationStore store, CancellationToken ct) =>
            Results.Ok(new { offlineHours = await store.OfflineHoursAsync(ct), consoleUrl = await store.ConsoleUrlAsync(ct) }));

        g.MapPut("/settings", async (SettingsBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (body.OfflineHours is { } h)
            {
                if (h is < 1 or > 2160) return Validation.Problem("offlineHours", "Use 1–2160 hours.");
                await using var cmd = ds.CreateCommand("""
                    INSERT INTO zentriq.system_settings (key, value) VALUES ('integrations.offline_hours', @v::jsonb)
                    ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now()
                    """);
                cmd.Parameters.AddWithValue("v", h.ToString(System.Globalization.CultureInfo.InvariantCulture));
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await rc.AuditAsync("integration.settings", null, detail: body);
            return Results.NoContent();
        });

        // ---- API tokens --------------------------------------------------------------------------------------------

        g.MapGet("/tokens", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var list = new List<object>();
            await using var cmd = ds.CreateCommand("""
                SELECT t.id, t.name, t.prefix, t.permissions, u.display_name, t.created_at, t.expires_at, t.last_used_at, t.last_used_ip, t.revoked_at
                  FROM zentriq.api_tokens t JOIN zentriq.users u ON u.id = t.created_by ORDER BY t.revoked_at IS NOT NULL, t.created_at DESC
                """);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new
                {
                    id = r.GetGuid(0), name = r.GetString(1), prefix = r.GetString(2), permissions = r.GetFieldValue<string[]>(3), createdBy = r.GetString(4),
                    createdAt = r.GetFieldValue<DateTimeOffset>(5), expiresAt = r.IsDBNull(6) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(6),
                    lastUsedAt = r.IsDBNull(7) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(7), lastUsedIp = r.IsDBNull(8) ? null : r.GetString(8),
                    revokedAt = r.IsDBNull(9) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(9),
                });
            return Results.Ok(list);
        });

        g.MapPost("/tokens", async (TokenBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Trim().Length > 100) return Validation.Problem("name", "Give the token a name (up to 100 characters).");
            var perms = (body.Permissions ?? []).Distinct().ToArray();
            if (perms.Length == 0) return Validation.Problem("permissions", "Choose at least one permission.");
            if (perms.FirstOrDefault(p => !TokenPermissions.Contains(p)) is { } bad) return Validation.Problem("permissions", $"'{bad}' cannot be given to an API token (tokens are read-only).");
            if (perms.FirstOrDefault(p => !rc.Has(p)) is { } notMine) return Validation.Problem("permissions", $"You do not have the '{notMine}' permission yourself.");
            if (body.ExpiresInDays is { } d && d is < 0 or > 730) return Validation.Problem("expiresInDays", "Tokens can last 1–730 days (0 = no expiry).");
            if (rc.UserId is not { } uid) return Results.Unauthorized();
            var token = IntegrationStore.NewToken();
            await using var cmd = ds.CreateCommand("""
                INSERT INTO zentriq.api_tokens (name, prefix, token_hash, permissions, created_by, expires_at)
                VALUES (@n, @p, @h, @perm, @u, CASE WHEN @d::int IS NULL THEN NULL ELSE now() + make_interval(days => @d::int) END) RETURNING id, expires_at
                """);
            cmd.Parameters.AddWithValue("n", body.Name.Trim());
            cmd.Parameters.AddWithValue("p", token[..10]);
            cmd.Parameters.AddWithValue("h", IntegrationStore.HashToken(token));
            cmd.Parameters.AddWithValue("perm", perms);
            cmd.Parameters.AddWithValue("u", uid);
            cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Integer) { Value = (body.ExpiresInDays ?? 365) is var days && days > 0 ? (object)days : DBNull.Value });
            Guid id; DateTimeOffset? expires;
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                await r.ReadAsync(ct);
                id = r.GetGuid(0);
                expires = r.IsDBNull(1) ? null : r.GetFieldValue<DateTimeOffset>(1);
            }
            await rc.AuditAsync("integration.token.create", body.Name.Trim(), detail: new { id, permissions = perms, expires });
            return Results.Ok(new { id, token, expiresAt = expires, message = "Copy the token now — it is not shown again. Use it as: Authorization: Bearer <token>." });
        });

        g.MapDelete("/tokens/{id:guid}", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("UPDATE zentriq.api_tokens SET revoked_at = now() WHERE id = @id AND revoked_at IS NULL RETURNING name");
            cmd.Parameters.AddWithValue("id", id);
            if (await cmd.ExecuteScalarAsync(ct) is not string name) return Results.NotFound();
            await rc.AuditAsync("integration.token.revoke", name);
            return Results.NoContent();
        });
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private static ChannelSecrets Merge(ChannelSecrets current, ChannelSecrets? update, string[]? clear)
    {
        var s = new ChannelSecrets { Url = current.Url, SigningSecret = current.SigningSecret, HeaderValue = current.HeaderValue, Password = current.Password };
        if (!string.IsNullOrEmpty(update?.Url)) s.Url = update.Url.Trim();
        if (!string.IsNullOrEmpty(update?.SigningSecret)) s.SigningSecret = update.SigningSecret;
        if (!string.IsNullOrEmpty(update?.HeaderValue)) s.HeaderValue = update.HeaderValue;
        if (!string.IsNullOrEmpty(update?.Password)) s.Password = update.Password;
        foreach (var c in clear ?? [])
            switch (c)
            {
                case "url": s.Url = null; break;
                case "signingSecret": s.SigningSecret = null; break;
                case "headerValue": s.HeaderValue = null; break;
                case "password": s.Password = null; break;
            }
        return s;
    }

    private static bool HasAny(ChannelSecrets s) => s.Url is not null || s.SigningSecret is not null || s.HeaderValue is not null || s.Password is not null;

    private static object SecretFlags(ChannelSecrets s) => new { url = s.Url is not null, signingSecret = s.SigningSecret is not null, headerValue = s.HeaderValue is not null, password = s.Password is not null };

    /// <summary>A non-secret hint of a stored webhook URL (scheme and host only).</summary>
    private static string? Hint(string kind, ChannelSecrets s) =>
        kind is "slack" or "teams" && Uri.TryCreate(s.Url, UriKind.Absolute, out var u) ? $"{u.Host}/…" : null;

    private static IResult? ValidateChannel(string kind, string name, ChannelSettings s, ChannelSecrets secrets)
    {
        if (!ChannelKinds.All.Contains(kind)) return Validation.Problem("kind", "Unknown channel type.");
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100) return Validation.Problem("name", "Give the channel a name (up to 100 characters).");
        switch (kind)
        {
            case "webhook":
                if (!ChannelSender.ValidUrl(s.Url, allowHttp: true)) return Validation.Problem("settings.url", "Enter the full URL (https:// recommended).");
                if (s.HeaderName is { Length: > 0 } hn && (hn.Length > 100 || !hn.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
                    return Validation.Problem("settings.headerName", "Use a header name such as Authorization or X-Api-Key.");
                break;
            case "slack":
                if (!ChannelSender.ValidUrl(secrets.Url, allowHttp: false)) return Validation.Problem("secrets.url", "Paste the Slack incoming webhook URL (https://hooks.slack.com/services/…).");
                break;
            case "teams":
                if (!ChannelSender.ValidUrl(secrets.Url, allowHttp: false)) return Validation.Problem("secrets.url", "Paste the Teams Workflows (or incoming webhook) URL.");
                break;
            case "email":
                s.Recipients = s.Recipients.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (s.Recipients.Count is 0 or > 50 || s.Recipients.Any(x => !EmailRegex().IsMatch(x))) return Validation.Problem("settings.recipients", "Enter 1–50 valid e-mail addresses.");
                break;
            case "syslog":
                if (string.IsNullOrWhiteSpace(s.Host) || s.Host.Trim().Length > 253 || s.Host.Any(char.IsWhiteSpace)) return Validation.Problem("settings.host", "Enter the syslog / SIEM server name or address.");
                if (s.Port is not null and (< 1 or > 65535)) return Validation.Problem("settings.port", "Port must be 1–65535.");
                if (s.Protocol is not (null or "udp" or "tcp")) return Validation.Problem("settings.protocol", "Protocol must be UDP or TCP.");
                if (s.Format is not (null or "cef" or "json")) return Validation.Problem("settings.format", "Format must be CEF or JSON.");
                break;
            case "servicenow":
                if (!ChannelSender.ValidUrl(s.InstanceUrl, allowHttp: false)) return Validation.Problem("settings.instanceUrl", "Enter the instance URL, e.g. https://example.service-now.com.");
                if (string.IsNullOrWhiteSpace(s.Username) || secrets.Password is null) return Validation.Problem("settings.username", "Enter the integration user name and password.");
                if (s.Table is { Length: > 0 } t && !t.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) return Validation.Problem("settings.table", "Enter a table name such as incident.");
                break;
            case "jira":
                if (!ChannelSender.ValidUrl(s.InstanceUrl, allowHttp: false)) return Validation.Problem("settings.instanceUrl", "Enter the Jira URL, e.g. https://example.atlassian.net.");
                if (string.IsNullOrWhiteSpace(s.ProjectKey)) return Validation.Problem("settings.projectKey", "Enter the project key.");
                if (secrets.Password is null || (s.Auth != "bearer" && string.IsNullOrWhiteSpace(s.Username))) return Validation.Problem("settings.username", "Enter the account e-mail and API token (or a personal access token).");
                break;
        }
        return null;
    }

    private static async Task<IResult?> ValidateRuleAsync(RuleBody body, NpgsqlDataSource ds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Trim().Length > 120) return Validation.Problem("name", "Give the rule a name (up to 120 characters).");
        if (!EventTypes.Exists(body.EventType)) return Validation.Problem("eventType", "Choose an event.");
        if (body.MinSeverity is not null && !EventTypes.Severities.Contains(body.MinSeverity)) return Validation.Problem("minSeverity", "Unknown severity.");
        if (body.ThrottleMinutes is < 0 or > 10080) return Validation.Problem("throttleMinutes", "Throttle must be 0–10080 minutes (a week).");
        if (body.ChannelIds is not { Length: > 0 }) return Validation.Problem("channelIds", "Choose at least one channel.");
        await using var cmd = ds.CreateCommand("SELECT count(*) FROM zentriq.int_channels WHERE id = ANY(@ids)");
        cmd.Parameters.AddWithValue("ids", body.ChannelIds.Distinct().ToArray());
        if ((long)(await cmd.ExecuteScalarAsync(ct))! != body.ChannelIds.Distinct().Count()) return Validation.Problem("channelIds", "A chosen channel no longer exists.");
        if (body.Conditions is { } c && (c.Categories.Any(x => !AssetCategories.All.Contains(x)) || c.Criticality.Any(x => x is not ("Critical" or "High" or "Medium" or "Low"))))
            return Validation.Problem("conditions", "Unknown device category or criticality.");
        return null;
    }

    private static void FillRule(NpgsqlCommand cmd, Guid id, RuleBody body)
    {
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("n", body.Name.Trim());
        cmd.Parameters.AddWithValue("e", body.Enabled ?? true);
        cmd.Parameters.AddWithValue("t", body.EventType);
        cmd.Parameters.AddWithValue("s", body.MinSeverity ?? "info");
        cmd.Parameters.AddWithValue("c", JsonSerializer.Serialize(body.Conditions ?? new RuleConditions(), IntegrationStore.Json));
        cmd.Parameters.AddWithValue("ch", body.ChannelIds!.Distinct().ToArray());
        cmd.Parameters.AddWithValue("th", body.ThrottleMinutes ?? 0);
    }
}
