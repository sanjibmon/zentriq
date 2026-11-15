using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Npgsql;
using Zentriq.Api.Infrastructure;
using Zentriq.Application.Licensing;
using Zentriq.Domain;
using Zentriq.Infrastructure.Assistant;
using Zentriq.Infrastructure.Configuration;

namespace Zentriq.Api.Endpoints;

/// <summary>
/// Zentriq Assistant (build 145): the console's chat help. Answers questions about errors, configuration and how-to from
/// the documentation and, when allowed, from this installation's live state (read-only, limited to what the user may see).
/// </summary>
public static partial class AssistantEndpoints
{
    public sealed record AskBody(string? Question, List<TurnBody>? History);
    public sealed record TurnBody(string? Role, string? Text);
    public sealed record RatingBody(int Rating);
    public sealed record SettingsBody(bool? Enabled, string? Provider, string? AnthropicModel, string? LocalUrl, string? LocalModel,
        bool? LiveData, string? Audience, int? KeepDays, string? ApiKey, bool? ClearKey);

    private static readonly string[] Suggestions =
    [
        "Why is a device not showing up after I installed the agent?",
        "How do I connect Active Directory and sign in with domain accounts?",
        "A scan says 'Credentials rejected'. What should I check?",
        "How do I activate a license on a server without internet?",
        "How do I set up automatic patching with a maintenance window?",
    ];

    public static void MapAssistantEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/assistant").WithTags("Assistant").RequireAuthorization();

        g.MapGet("/status", async (AssistantStore store, RequestContext rc, CancellationToken ct) =>
        {
            var s = await store.GetAsync(ct);
            return Results.Ok(new
            {
                enabled = s.Enabled,
                available = Allowed(s, rc),
                provider = s.Provider,
                providerLabel = ProviderLabel(s),
                liveData = s.LiveData,
                audience = s.Audience,
                suggestions = Suggestions,
            });
        });

        g.MapPost("/ask", async (AskBody body, AssistantService assistant, AssistantStore store, RequestContext rc, NpgsqlDataSource ds,
            IServiceProvider sp, ZentriqPaths paths, ZentriqOptions options, HttpContext http, CancellationToken ct) =>
        {
            var s = await store.GetAsync(ct);
            if (!s.Enabled) return Results.Problem(statusCode: 403, title: "The Zentriq Assistant is turned off (System Settings › Assistant).");
            if (!Allowed(s, rc)) return Results.Problem(statusCode: 403, title: "The Zentriq Assistant is available to administrators only on this server.");
            var q = body.Question?.Trim() ?? "";
            if (q.Length == 0) return Validation.Problem("question", "Ask a question.");
            if (q.Length > 4000) return Validation.Problem("question", "Keep the question under 4,000 characters (paste only the relevant part of an error).");
            var history = (body.History ?? []).Where(t => !string.IsNullOrWhiteSpace(t.Text)).TakeLast(12)
                .Select(t => new ChatTurn(t.Role == "assistant" ? "assistant" : "user", t.Text!.Length > 6000 ? t.Text[..6000] : t.Text)).ToList();
            IAssistantLiveData? live = null;
            if (s.LiveData)
            {
                // ds is this user's data source (the data scope filter switches it for limited users); system facts use the full one.
                var system = sp.GetRequiredService<NpgsqlDataSource>();
                live = new AssistantLiveData(rc, ds, system, paths, options, await rc.LicenseAsync(ct));
            }
            var roles = string.Join(", ", rc.Principal.FindAll(System.Security.Claims.ClaimTypes.Role).Select(c => c.Value).DefaultIfEmpty("user"));
            var answer = await assistant.AskAsync(q, history, live, s, rc.UserName, rc.UserId, roles, ct);
            await rc.AuditAsync("assistant.ask", null, "Success", new { provider = answer.Provider, tools = answer.ToolsUsed.Distinct(), messageId = answer.MessageId, length = q.Length }, ct);
            return Results.Ok(new
            {
                id = answer.MessageId,
                text = answer.Text,
                sources = answer.Sources.Select(x => new { title = x.Title, heading = x.Heading, anchor = x.Anchor }),
                provider = answer.Provider,
                tools = answer.ToolsUsed.Distinct(),
                notice = answer.Notice,
            });
        }).RequireRateLimiting("assistant");

        g.MapPost("/messages/{id:long}/rating", async (long id, RatingBody body, AssistantStore store, RequestContext rc, CancellationToken ct) =>
            await store.RateAsync(id, rc.UserId, body.Rating, ct) ? Results.NoContent() : Results.NotFound());

        g.MapGet("/settings", async (AssistantStore store, CancellationToken ct) =>
        {
            var s = await store.GetAsync(ct);
            var key = await store.GetKeyAsync(ct);
            return Results.Ok(new
            {
                s.Enabled, s.Provider, s.AnthropicModel, s.LocalUrl, s.LocalModel, s.LiveData, s.Audience, s.KeepDays,
                hasKey = key is not null,
                keyHint = key is { Length: > 6 } ? "…" + key[^4..] : null,
            });
        }).RequireAuthorization(Permissions.SettingsManage);

        g.MapPut("/settings", async (SettingsBody b, AssistantStore store, RequestContext rc, CancellationToken ct) =>
        {
            var cur = await store.GetAsync(ct);
            var provider = b.Provider?.Trim().ToLowerInvariant() ?? cur.Provider;
            if (!AssistantSettings.Providers.Contains(provider)) return Validation.Problem("provider", "Choose offline, anthropic or local.");
            var localUrl = b.LocalUrl is null ? cur.LocalUrl : string.IsNullOrWhiteSpace(b.LocalUrl) ? null : b.LocalUrl.Trim();
            if (localUrl is not null && (!Uri.TryCreate(localUrl, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")))
                return Validation.Problem("localUrl", "Enter the model server's address, for example http://10.0.0.20:11434 (Ollama) or http://localhost:1234 (LM Studio).");
            var next = cur with
            {
                Enabled = b.Enabled ?? cur.Enabled,
                Provider = provider,
                AnthropicModel = string.IsNullOrWhiteSpace(b.AnthropicModel) ? cur.AnthropicModel : b.AnthropicModel.Trim(),
                LocalUrl = localUrl,
                LocalModel = b.LocalModel is null ? cur.LocalModel : string.IsNullOrWhiteSpace(b.LocalModel) ? null : b.LocalModel.Trim(),
                LiveData = b.LiveData ?? cur.LiveData,
                Audience = b.Audience is "admins" or "everyone" ? b.Audience : cur.Audience,
                KeepDays = Math.Clamp(b.KeepDays ?? cur.KeepDays, 7, 3650),
            };
            if (provider == "local" && (next.LocalUrl is null || next.LocalModel is null))
                return Validation.Problem("localModel", "Enter the local model server's address and the model name (for example llama3.1:8b or qwen2.5:14b).");
            await store.SaveAsync(next, ct);
            if (b.ClearKey == true) await store.SetKeyAsync(null, ct);
            else if (!string.IsNullOrWhiteSpace(b.ApiKey)) await store.SetKeyAsync(b.ApiKey, ct);
            if (provider == "anthropic" && await store.GetKeyAsync(ct) is null)
                return Validation.Problem("apiKey", "Enter the Anthropic API key (console.anthropic.com › API keys) to use Claude.");
            await rc.AuditAsync("assistant.settings", null, "Success", new { next.Enabled, next.Provider, next.AnthropicModel, next.LocalUrl, next.LocalModel, next.LiveData, next.Audience, next.KeepDays, keyChanged = b.ClearKey == true || !string.IsNullOrWhiteSpace(b.ApiKey) }, ct);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.SettingsManage);

        g.MapPost("/settings/test", async (AssistantService assistant, AssistantStore store, RequestContext rc, CancellationToken ct) =>
        {
            var s = await store.GetAsync(ct);
            if (s.Provider == "offline") return Results.Ok(new { ok = true, message = "Documentation search needs no connection: it works on this server." });
            var model = assistant.ModelFactory(s, await store.GetKeyAsync(ct));
            if (model is null) return Results.Ok(new { ok = false, message = "The model is not configured completely: " + (s.Provider == "anthropic" ? "enter the API key." : "enter the address and the model name.") });
            try
            {
                var started = DateTimeOffset.UtcNow;
                var r = await model.CompleteAsync("Reply with the single word OK.", [new ChatTurn("user", "Connection test from Zentriq.")], [], (_, _, _) => Task.FromResult(""), ct);
                var ms = (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds;
                await rc.AuditAsync("assistant.test", null, "Success", new { provider = model.Name }, ct);
                return Results.Ok(new { ok = true, message = $"{model.Name} answered in {ms / 1000.0:0.0} s." });
            }
            catch (AssistantModelException ex)
            {
                await rc.AuditAsync("assistant.test", null, "Failure", new { provider = model.Name, error = ex.Message }, ct);
                return Results.Ok(new { ok = false, message = ex.Message });
            }
        }).RequireAuthorization(Permissions.SettingsManage);

        g.MapGet("/history", async (NpgsqlDataSource ds, IServiceProvider sp, int? limit, CancellationToken ct) =>
        {
            await using var cmd = sp.GetRequiredService<NpgsqlDataSource>().CreateCommand("""
                SELECT id, user_name, asked_at, question, answer, provider, tools, sources, duration_ms, ok, error, rating
                FROM zentriq.assistant_messages ORDER BY asked_at DESC LIMIT @n
                """);
            cmd.Parameters.AddWithValue("n", Math.Clamp(limit ?? 100, 1, 500));
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new
                {
                    id = r.GetInt64(0), user = r.GetString(1), askedAt = r.GetFieldValue<DateTimeOffset>(2), question = r.GetString(3),
                    answer = r.IsDBNull(4) ? null : r.GetString(4), provider = r.GetString(5), tools = r.GetFieldValue<string[]>(6),
                    sources = r.GetFieldValue<string[]>(7), durationMs = r.IsDBNull(8) ? (int?)null : r.GetInt32(8), ok = r.GetBoolean(9),
                    error = r.IsDBNull(10) ? null : r.GetString(10), rating = r.IsDBNull(11) ? (short?)null : r.GetInt16(11),
                });
            return Results.Ok(list);
        }).RequireAuthorization(Permissions.AuditView);
    }

    private static bool Allowed(AssistantSettings s, RequestContext rc) => s.Enabled && (s.Audience != "admins" || rc.Has(Permissions.SettingsManage));

    private static string ProviderLabel(AssistantSettings s) => s.Provider switch
    {
        "anthropic" => "Claude",
        "local" => "Local model" + (string.IsNullOrWhiteSpace(s.LocalModel) ? "" : $" ({s.LocalModel})"),
        _ => "Documentation search",
    };
}

/// <summary>
/// Read-only facts about this installation for the Assistant, limited to what the asking user may see: devices and
/// agents through the user's (data-scoped) connection, server health only for administrators with access to all devices.
/// </summary>
public sealed partial class AssistantLiveData(RequestContext rc, NpgsqlDataSource devices, NpgsqlDataSource system, ZentriqPaths paths, ZentriqOptions options, LicenseStatus license)
    : IAssistantLiveData
{
    private bool CanSeeDevices => rc.Has(Permissions.AssetsView);
    private bool CanSeeHealth => rc.Has(Permissions.SettingsManage) && !rc.Scoped;

    private static JsonObject Schema(params (string Name, string Type, string Description)[] props)
    {
        var p = new JsonObject();
        var required = new JsonArray();
        foreach (var (n, t, d) in props) { p[n] = new JsonObject { ["type"] = t, ["description"] = d }; required.Add(n); }
        return new JsonObject { ["type"] = "object", ["properties"] = p, ["required"] = required };
    }

    public IReadOnlyList<AssistantTool> Tools
    {
        get
        {
            var list = new List<AssistantTool>
            {
                new("license_status", "This installation's license: mode (trial, licensed, free), edition, sections, days left, limits and warnings.", Schema()),
            };
            if (CanSeeDevices)
            {
                list.Add(new("find_devices", "Find devices by host name, IP address or serial number (part of a name works). Returns status, last contact, inventory state, the agent (version, platform, last check-in) and, for a single match, its latest scan result with the reason inventory failed.",
                    Schema(("query", "string", "Host name, IP address or serial number, or part of one"))));
                list.Add(new("agent_problems", "Agents that have not checked in for a day or more, and agent counts and versions.", Schema()));
                list.Add(new("recent_scans", "The latest discovery scans with their results per status, and the reasons for failures in the newest scan.", Schema()));
            }
            if (CanSeeHealth)
                list.Add(new("server_health", "Zentriq server health: version, worker heartbeat, background jobs that failed in the last 24 hours, and the latest errors in the server logs.", Schema()));
            return list;
        }
    }

    public async Task<string> RunAsync(string tool, JsonElement args, CancellationToken ct)
    {
        try
        {
            return tool switch
            {
                "license_status" => License(),
                "find_devices" when CanSeeDevices => await FindDevicesAsync(args.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "", ct),
                "agent_problems" when CanSeeDevices => await AgentProblemsAsync(ct),
                "recent_scans" when CanSeeDevices => await RecentScansAsync(ct),
                "server_health" when CanSeeHealth => await HealthAsync(ct),
                _ => $"The tool {tool} is not available to this user.",
            };
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or IOException)
        {
            return $"{tool} could not read this information: {ex.Message}";
        }
    }

    private string License()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Mode: {license.Mode}; edition: {license.Edition ?? "-"}; type: {license.LicenseType ?? "-"}; activation: {license.Activation ?? "-"}");
        sb.AppendLine($"Summary: {license.Summary}");
        if (license.DaysRemaining is { } d) sb.AppendLine($"Days remaining: {d} (until {license.ExpiresAt:yyyy-MM-dd})");
        if (license.Warning is { } w) sb.AppendLine($"Warning: {w}");
        sb.AppendLine("Installed sections: " + string.Join(", ", license.InstalledSections));
        sb.AppendLine("Limits: " + string.Join(", ", license.Limits.Select(kv => $"{kv.Key} {kv.Value}")));
        return sb.ToString();
    }

    private static string Ago(DateTimeOffset? t) => t is null ? "never" : (DateTimeOffset.UtcNow - t.Value) switch
    {
        var s when s.TotalMinutes < 2 => "just now",
        var s when s.TotalHours < 1 => $"{(int)s.TotalMinutes} min ago",
        var s when s.TotalDays < 1 => $"{(int)s.TotalHours} h ago",
        var s => $"{(int)s.TotalDays} days ago",
    };

    private async Task<string> FindDevicesAsync(string query, CancellationToken ct)
    {
        query = query.Trim();
        if (query.Length < 2) return "Give at least two characters of a host name, IP address or serial number.";
        await using var cmd = devices.CreateCommand("""
            SELECT a.id, a.hostname, host(a.primary_ip), a.os_name, a.status, a.inventory_status, a.last_seen_at, a.last_inventory_at,
                   r.agent_version, r.platform, r.status, r.last_checkin_at, a.category
            FROM zentriq.assets a
            LEFT JOIN LATERAL (SELECT agent_version, platform, status, last_checkin_at FROM zentriq.agent_registrations
                               WHERE asset_id = a.id ORDER BY (status = 'active') DESC, last_checkin_at DESC NULLS LAST LIMIT 1) r ON true
            WHERE a.merged_into IS NULL
              AND (a.hostname ILIKE @p OR a.fqdn ILIKE @p OR host(a.primary_ip) = @q OR a.serial_number ILIKE @p)
            ORDER BY (lower(a.hostname) = lower(@q)) DESC, a.last_seen_at DESC
            LIMIT 10
            """);
        cmd.Parameters.AddWithValue("p", "%" + query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
        cmd.Parameters.AddWithValue("q", query);
        var rows = new List<(Guid Id, string Line)>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var agent = r.IsDBNull(8) ? "no Zentriq Agent"
                    : $"agent {r.GetString(8)} ({r.GetString(9)}, {r.GetString(10)}), last check-in {Ago(r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11))}";
                rows.Add((r.GetGuid(0), $"{(r.IsDBNull(1) ? "(no name)" : r.GetString(1))} · {(r.IsDBNull(2) ? "-" : r.GetString(2))} · {r.GetString(12)} · {(r.IsDBNull(3) ? "OS unknown" : r.GetString(3))} · status {r.GetString(4)} · inventory {r.GetString(5)} (last {Ago(r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7))}) · last seen {Ago(r.GetFieldValue<DateTimeOffset>(6))} · {agent}"));
            }
        if (rows.Count == 0) return $"No device matches '{query}' (among the devices this user may see).";
        var sb = new StringBuilder($"{rows.Count} device(s) match '{query}':\n");
        foreach (var x in rows) sb.Append("- ").AppendLine(x.Line);
        if (rows.Count == 1)
        {
            await using var s = devices.CreateCommand("""
                SELECT d.name, r.status, r.detail, r.protocol, r.recorded_at, r.open_ports
                FROM zentriq.scan_results r JOIN zentriq.discovery_scans d ON d.id = r.scan_id
                WHERE r.asset_id = @id ORDER BY r.recorded_at DESC LIMIT 1
                """);
            s.Parameters.AddWithValue("id", rows[0].Id);
            await using var sr = await s.ExecuteReaderAsync(ct);
            if (await sr.ReadAsync(ct))
                sb.AppendLine($"Latest scan '{sr.GetString(0)}' ({Ago(sr.GetFieldValue<DateTimeOffset>(4))}): {sr.GetString(1)}"
                    + (sr.IsDBNull(3) ? "" : $" via {sr.GetString(3)}") + (sr.IsDBNull(2) ? "" : $" — {sr.GetString(2)}")
                    + $"; open ports: {string.Join(", ", sr.GetFieldValue<int[]>(5))}");
            else sb.AppendLine("No discovery scan has reached this device.");
        }
        return sb.ToString();
    }

    private async Task<string> AgentProblemsAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        await using (var c = devices.CreateCommand("""
            SELECT count(*), count(*) FILTER (WHERE last_checkin_at > now() - interval '1 hour'),
                   count(*) FILTER (WHERE last_checkin_at > now() - interval '1 day'),
                   string_agg(DISTINCT agent_version, ', ')
            FROM zentriq.agent_registrations WHERE status = 'active'
            """))
        await using (var r = await c.ExecuteReaderAsync(ct))
            if (await r.ReadAsync(ct))
                sb.AppendLine($"Active agents: {r.GetInt64(0)}; checked in within 1 hour: {r.GetInt64(1)}; within 1 day: {r.GetInt64(2)}; versions: {(r.IsDBNull(3) ? "-" : r.GetString(3))}");
        await using var cmd = devices.CreateCommand("""
            SELECT a.hostname, host(a.primary_ip), r.agent_version, r.platform, r.last_checkin_at
            FROM zentriq.agent_registrations r JOIN zentriq.assets a ON a.id = r.asset_id
            WHERE r.status = 'active' AND (r.last_checkin_at IS NULL OR r.last_checkin_at < now() - interval '1 day')
            ORDER BY r.last_checkin_at NULLS FIRST LIMIT 20
            """);
        await using var rr = await cmd.ExecuteReaderAsync(ct);
        var n = 0;
        while (await rr.ReadAsync(ct))
        {
            if (n++ == 0) sb.AppendLine("Not checked in for a day or more:");
            sb.AppendLine($"- {(rr.IsDBNull(0) ? "(no name)" : rr.GetString(0))} ({(rr.IsDBNull(1) ? "-" : rr.GetString(1))}) agent {rr.GetString(2)} {rr.GetString(3)}, last check-in {Ago(rr.IsDBNull(4) ? null : rr.GetFieldValue<DateTimeOffset>(4))}");
        }
        if (n == 0) sb.AppendLine("Every active agent checked in within the last day.");
        return sb.ToString();
    }

    private async Task<string> RecentScansAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        Guid? newest = null;
        await using (var cmd = devices.CreateCommand("""
            SELECT s.id, s.number, s.name, s.status, s.created_at, s.total_targets, s.assets_created, s.assets_updated, s.error,
                   (SELECT string_agg(status || ' ' || n, ', ') FROM (SELECT status, count(*) n FROM zentriq.scan_results WHERE scan_id = s.id GROUP BY status) x)
            FROM zentriq.discovery_scans s ORDER BY s.created_at DESC LIMIT 5
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                newest ??= r.GetGuid(0);
                sb.AppendLine($"- Scan #{r.GetInt64(1)} '{r.GetString(2)}' {r.GetString(3)} ({Ago(r.GetFieldValue<DateTimeOffset>(4))}): {r.GetInt32(5)} targets, {r.GetInt32(6)} new, {r.GetInt32(7)} updated; results: {(r.IsDBNull(9) ? "none" : r.GetString(9))}{(r.IsDBNull(8) ? "" : "; error: " + r.GetString(8))}");
            }
        if (newest is null) return "No discovery scan has been run yet.";
        await using var f = devices.CreateCommand("""
            SELECT target, status, detail FROM zentriq.scan_results
            WHERE scan_id = @id AND status IN ('auth_failed', 'inventory_unavailable', 'inventory_failed', 'reachable_unauthenticated')
            ORDER BY recorded_at LIMIT 8
            """);
        f.Parameters.AddWithValue("id", newest.Value);
        await using var fr = await f.ExecuteReaderAsync(ct);
        var first = true;
        while (await fr.ReadAsync(ct))
        {
            if (first) { sb.AppendLine("Failures in the newest scan:"); first = false; }
            sb.AppendLine($"- {fr.GetString(0)}: {fr.GetString(1)}{(fr.IsDBNull(2) ? "" : " — " + fr.GetString(2))}");
        }
        return sb.ToString();
    }

    private async Task<string> HealthAsync(CancellationToken ct)
    {
        var sb = new StringBuilder($"Zentriq {options.ProductVersion}\n");
        await using (var w = system.CreateCommand("SELECT host_name, version, last_beat_at FROM zentriq.worker_heartbeats ORDER BY last_beat_at DESC LIMIT 3"))
        await using (var r = await w.ExecuteReaderAsync(ct))
        {
            var any = false;
            while (await r.ReadAsync(ct))
            {
                any = true;
                var beat = r.GetFieldValue<DateTimeOffset>(2);
                sb.AppendLine($"Worker on {r.GetString(0)} ({r.GetString(1)}): last heartbeat {Ago(beat)} — {(beat > DateTimeOffset.UtcNow.AddMinutes(-2) ? "healthy" : "NOT RUNNING (check the ZentriqWorker service)")}");
            }
            if (!any) sb.AppendLine("No worker heartbeat recorded: the ZentriqWorker service may never have started.");
        }
        await using (var j = system.CreateCommand("""
            SELECT job_type, count(*), max(finished_at), (array_agg(last_error ORDER BY finished_at DESC))[1]
            FROM zentriq.jobs WHERE status IN ('failed', 'dead') AND finished_at > now() - interval '1 day'
            GROUP BY job_type ORDER BY count(*) DESC LIMIT 8
            """))
        await using (var r = await j.ExecuteReaderAsync(ct))
        {
            var any = false;
            while (await r.ReadAsync(ct))
            {
                if (!any) { sb.AppendLine("Background jobs that failed in the last 24 hours:"); any = true; }
                sb.AppendLine($"- {r.GetString(0)}: {r.GetInt64(1)} time(s), last {Ago(r.IsDBNull(2) ? null : r.GetFieldValue<DateTimeOffset>(2))}: {(r.IsDBNull(3) ? "-" : Cut(r.GetString(3), 300))}");
            }
            if (!any) sb.AppendLine("No background job failed in the last 24 hours.");
        }
        var errors = RecentLogErrors(10);
        if (errors.Count > 0) { sb.AppendLine("Latest errors in the server logs:"); foreach (var e in errors) sb.Append("- ").AppendLine(e); }
        else sb.AppendLine("No errors in today's server logs.");
        sb.AppendLine("License: " + license.Summary);
        return sb.ToString();
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max] + " …";

    /// <summary>The newest Error/Critical lines of the latest API and Worker log files (already redacted when written).</summary>
    private List<string> RecentLogErrors(int take)
    {
        var found = new List<(DateTimeOffset At, string Line)>();
        if (!Directory.Exists(paths.Logs)) return [];
        foreach (var prefix in new[] { "api-", "worker-" })
        {
            var file = new DirectoryInfo(paths.Logs).GetFiles(prefix + "*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (file is null) continue;
            foreach (var line in Tail(file.FullName, 2000))
            {
                if (!line.Contains("\"level\":\"Error\"", StringComparison.Ordinal) && !line.Contains("\"level\":\"Critical\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var d = JsonDocument.Parse(line);
                    var ts = DateTimeOffset.TryParse(d.RootElement.GetProperty("ts").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : DateTimeOffset.MinValue;
                    var msg = d.RootElement.TryGetProperty("msg", out var m) ? m.GetString() : null;
                    var cat = d.RootElement.TryGetProperty("category", out var c) ? c.GetString() : null;
                    var ex = d.RootElement.TryGetProperty("exception", out var e) ? e.GetString()?.Split('\n')[0] : null;
                    found.Add((ts, $"{ts:yyyy-MM-dd HH:mm} UTC {prefix.TrimEnd('-')} {cat}: {Cut(msg ?? "", 300)}{(ex is null ? "" : " | " + Cut(ex, 200))}"));
                }
                catch (JsonException) { }
            }
        }
        return found.OrderByDescending(x => x.At).Take(take).Select(x => x.Line).ToList();
    }

    private static IEnumerable<string> Tail(string path, int lines)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, fs.Length - 1_500_000);
        fs.Seek(start, SeekOrigin.Begin);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        var all = sr.ReadToEnd().Split('\n');
        return all.Skip(Math.Max(0, all.Length - lines)).ToList();
    }

    // ---- facts for the answer without an AI model ----------------------------------------------------------------

    public async Task<IReadOnlyList<string>> OfflineFactsAsync(string question, CancellationToken ct)
    {
        var facts = new List<string>();
        var lower = question.ToLowerInvariant();
        if (CanSeeDevices)
        {
            foreach (var name in DeviceNames(question).Take(2))
            {
                var r = await FindDevicesAsync(name, ct);
                if (!r.StartsWith("No device", StringComparison.Ordinal)) facts.AddRange(r.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(4).Select(l => l.TrimStart('-', ' ')));
            }
            if (lower.Contains("agent") && (lower.Contains("offline") || lower.Contains("not ") || lower.Contains("check in") || lower.Contains("check-in") || lower.Contains("contact")))
                facts.AddRange((await AgentProblemsAsync(ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(6).Select(l => l.TrimStart('-', ' ')));
            if (lower.Contains("scan"))
                facts.AddRange((await RecentScansAsync(ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(3).Select(l => l.TrimStart('-', ' ')));
        }
        if (lower.Contains("licen")) facts.Add("License: " + license.Summary);
        if (CanSeeHealth && HealthWords().IsMatch(lower))
            facts.AddRange((await HealthAsync(ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(6).Select(l => l.TrimStart('-', ' ')));
        return facts.Distinct().Take(14).ToList();
    }

    /// <summary>Words in a question that look like a device: a name with digits or dashes (LAPTOP-104, srv01) or an IPv4 address.</summary>
    public static IEnumerable<string> DeviceNames(string question) =>
        DeviceRx().Matches(question).Select(m => m.Value.Trim('.', ',', '?', '!', '\'', '"')).Where(w => w.Length >= 3).Distinct(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b|\b(?=[A-Za-z0-9-]*\d)(?=[A-Za-z0-9-]*[A-Za-z])[A-Za-z0-9][A-Za-z0-9-]{2,}\b")]
    private static partial Regex DeviceRx();
    [GeneratedRegex(@"error|fail|not working|down|slow|service|worker|stuck|crash|exception|cannot|can't|unable")]
    private static partial Regex HealthWords();
}
