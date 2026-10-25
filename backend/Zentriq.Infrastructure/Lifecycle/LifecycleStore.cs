using Npgsql;
using NpgsqlTypes;

namespace Zentriq.Infrastructure.Lifecycle;

public sealed class LifecycleValidationException(string field, string message) : Exception(message)
{
    public string Field { get; } = field;
}

/// <summary>A lifecycle change requested by a person (or by Zentriq itself, actor "system").</summary>
public sealed record StageChange(
    string Stage, string? Status = null, string? Reason = null,
    string? AssignedTo = null, string? AssignedEmail = null, DateOnly? ExpectedReturn = null,
    Guid? DepartmentId = null, Guid? LocationId = null,
    DateOnly? DisposedOn = null, string? DisposalMethod = null, string? DisposalVendor = null, decimal? DisposalValue = null,
    string? SanitizationMethod = null, string? SanitizedBy = null, DateOnly? SanitizedOn = null, string? CertificateRef = null);

/// <summary>
/// IT asset lifecycle (ISO/IEC 19770-1, ITIL 4 IT asset management; stages as in ServiceNow HAM):
/// planned → on order → in stock → in use ⇄ in maintenance → retired → disposed, with "missing" for lost, stolen and
/// not-seen devices. Each stage has statuses. Only the transitions below are allowed; every change is recorded in
/// zentriq.asset_lifecycle_events. Retired and disposed devices are retired in the inventory (assets.status) and no
/// longer count for licensing; devices waiting on order or in stock do not count either.
/// </summary>
public static class LifecycleStore
{
    public static readonly IReadOnlyList<(string Stage, string Label, string[] Statuses)> Stages =
    [
        ("planned", "Planned", ["requested", "approved"]),
        ("on_order", "On order", ["ordered", "in_transit"]),
        ("in_stock", "In stock", ["new", "refurbished", "reserved", "awaiting_deployment"]),
        ("in_use", "In use", ["in_use", "assigned", "loaned"]),
        ("in_maintenance", "In maintenance", ["repair", "vendor_rma", "upgrade"]),
        ("missing", "Missing", ["not_seen", "lost", "stolen"]),
        ("retired", "Retired", ["end_of_life", "pending_disposal"]),
        ("disposed", "Disposed", ["recycled", "sold", "donated", "returned_to_lessor", "destroyed"]),
    ];

    public static readonly IReadOnlyDictionary<string, string> StatusLabels = new Dictionary<string, string>
    {
        ["requested"] = "Requested", ["approved"] = "Approved", ["ordered"] = "Ordered", ["in_transit"] = "In transit",
        ["new"] = "New", ["refurbished"] = "Refurbished", ["reserved"] = "Reserved", ["awaiting_deployment"] = "Awaiting deployment",
        ["in_use"] = "In use", ["assigned"] = "Assigned to a person", ["loaned"] = "On loan",
        ["repair"] = "Repair", ["vendor_rma"] = "Vendor RMA", ["upgrade"] = "Upgrade",
        ["not_seen"] = "Not seen (review)", ["lost"] = "Lost", ["stolen"] = "Stolen",
        ["end_of_life"] = "End of life", ["pending_disposal"] = "Pending disposal",
        ["recycled"] = "Recycled", ["sold"] = "Sold", ["donated"] = "Donated", ["returned_to_lessor"] = "Returned to lessor", ["destroyed"] = "Destroyed",
    };

    /// <summary>Allowed next stages.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Transitions = new Dictionary<string, string[]>
    {
        ["planned"] = ["on_order", "in_stock", "retired"],
        ["on_order"] = ["planned", "in_stock", "in_use"],
        ["in_stock"] = ["in_use", "in_maintenance", "missing", "retired"],
        ["in_use"] = ["in_stock", "in_maintenance", "missing", "retired"],
        ["in_maintenance"] = ["in_use", "in_stock", "retired"],
        ["missing"] = ["in_use", "in_stock", "retired", "disposed"],
        ["retired"] = ["disposed", "in_stock"],
        ["disposed"] = [],
    };

    public static readonly string[] Classes = ["laptop", "desktop", "mobile", "server", "virtual", "network", "printer", "other"];
    public static readonly string[] DisposalMethods = ["recycled", "sold", "donated", "returned_to_lessor", "destroyed"];
    public static readonly string[] SanitizationMethods = ["clear", "purge", "destroy", "not_required"];
    public static readonly string[] DepreciationMethods = ["straight_line", "declining_balance", "none"];

    /// <summary>SQL for the device class of asset alias "a" (needs the inventory document "d" and hardware "h" joined, or NULLs).</summary>
    public const string ClassSql = "zentriq.lifecycle_class(COALESCE(a.device_type, d.document #>> '{device,chassis}', h.chassis, a.category))";
    /// <summary>SQL joins giving "d" and "h" for <see cref="ClassSql"/>.</summary>
    public const string ClassJoins = "LEFT JOIN zentriq.asset_inventory_documents d ON d.asset_id = a.id LEFT JOIN zentriq.hardware_inventory h ON h.asset_id = a.id";

    public static string StageLabel(string stage) => Stages.FirstOrDefault(s => s.Stage == stage).Label ?? stage;
    public static string StatusLabel(string status) => StatusLabels.TryGetValue(status, out var l) ? l : status;
    public static bool IsStage(string? s) => s is not null && Stages.Any(x => x.Stage == s);
    public static string DefaultStatus(string stage, string? assignedTo) =>
        stage == "in_use" ? (string.IsNullOrWhiteSpace(assignedTo) ? "in_use" : "assigned") : Stages.First(s => s.Stage == stage).Statuses[0];

    /// <summary>
    /// Book value on a date, the same rule as SQL zentriq.asset_book_value: straight-line, or double-declining balance,
    /// never below the salvage value; "none" (or no useful life / start date) keeps the cost.
    /// </summary>
    public static decimal? BookValue(decimal? cost, decimal? salvage, string? method, int? lifeMonths, DateOnly? start, DateOnly asOf)
    {
        if (cost is not { } c) return null;
        if (start is not { } s || method == "none" || (lifeMonths ?? 0) <= 0) return c;
        var floor = Math.Min(salvage ?? 0, c);
        var days = (double)(asOf.DayNumber - s.DayNumber);
        double value;
        if (method == "declining_balance")
        {
            if (days / 30.4375 >= lifeMonths!.Value) value = (double)floor;
            else value = (double)c * Math.Pow(Math.Max(0, 1 - 2.0 / (lifeMonths.Value / 12.0)), Math.Max(0, days / 365.25));
        }
        else value = (double)c - (double)(c - floor) * Math.Min(1, Math.Max(0, days / 30.4375) / lifeMonths!.Value);
        return Math.Round((decimal)Math.Max((double)floor, value), 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>Year-end book values from the start date until fully depreciated (at most 15 rows).</summary>
    public static List<(int Year, decimal Opening, decimal Depreciation, decimal Closing)> Schedule(decimal cost, decimal? salvage, string method, int lifeMonths, DateOnly start)
    {
        var rows = new List<(int, decimal, decimal, decimal)>();
        if (method == "none" || lifeMonths <= 0) return rows;
        var opening = cost;
        for (var y = start.Year; y < start.Year + 16 && rows.Count < 15; y++)
        {
            var closing = BookValue(cost, salvage, method, lifeMonths, start, new DateOnly(y, 12, 31))!.Value;
            rows.Add((y, opening, opening - closing, closing));
            if (closing <= Math.Min(salvage ?? 0, cost)) break;
            opening = closing;
        }
        return rows;
    }

    private static NpgsqlParameter P(string n, object? v, NpgsqlDbType t) => new(n, t) { Value = v ?? DBNull.Value };
    private static string? Trim(string? s, int max = 200) => string.IsNullOrWhiteSpace(s) ? null : (s.Trim().Length > max ? s.Trim()[..max] : s.Trim());

    public sealed record Current(string Stage, string Status, string? AssignedTo, string AssetStatus, string? Name);

    public static async Task<Current?> LockAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid assetId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT l.stage, l.status, l.assigned_to, a.status, COALESCE(a.hostname, zentriq.asset_ipv4(a.id), host(a.primary_ip), a.serial_number)
              FROM zentriq.asset_lifecycle l JOIN zentriq.assets a ON a.id = l.asset_id
             WHERE l.asset_id = @a FOR UPDATE OF l, a
            """, conn, tx);
        cmd.Parameters.AddWithValue("a", assetId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4));
    }

    /// <summary>Validates and applies a stage change (or a status change within the stage). Returns the event id.</summary>
    public static async Task<long> ChangeAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid assetId, StageChange c, string actor, CancellationToken ct)
    {
        var cur = await LockAsync(conn, tx, assetId, ct) ?? throw new LifecycleValidationException("assetId", "IT asset not found.");
        if (!IsStage(c.Stage)) throw new LifecycleValidationException("stage", $"Unknown stage '{c.Stage}'.");
        if (cur.AssetStatus is "duplicate" or "excluded") throw new LifecycleValidationException("assetId", "This record was merged into another device or is excluded.");
        if (c.Stage != cur.Stage && !Transitions[cur.Stage].Contains(c.Stage))
            throw new LifecycleValidationException("stage", $"{StageLabel(cur.Stage)} cannot change to {StageLabel(c.Stage)}. Allowed: {string.Join(", ", Transitions[cur.Stage].Select(StageLabel))}{(Transitions[cur.Stage].Length == 0 ? "none" : "")}.");
        var person = Trim(c.AssignedTo) ?? (c.Stage == "in_use" ? cur.AssignedTo : null);
        var status = Trim(c.Status, 30) ?? DefaultStatus(c.Stage, person);
        if (!Stages.First(s => s.Stage == c.Stage).Statuses.Contains(status))
            throw new LifecycleValidationException("status", $"'{status}' is not a status of {StageLabel(c.Stage)}.");
        if (c.Stage == cur.Stage && status == cur.Status && c.Stage != "in_use")
            throw new LifecycleValidationException("stage", $"The asset is already {StageLabel(c.Stage)} · {StatusLabel(status)}.");
        if (status is "assigned" or "loaned" && person is null) throw new LifecycleValidationException("assignedTo", "Enter the person the asset is assigned to.");
        if (status == "loaned" && c.ExpectedReturn is null) throw new LifecycleValidationException("expectedReturn", "Enter the date the loan ends.");
        if (status == "in_use" && c.Stage == "in_use") person = null;
        var retiring = c.Stage is "retired" or "disposed";
        if (c.Stage == "retired" && string.IsNullOrWhiteSpace(c.Reason)) throw new LifecycleValidationException("reason", "Enter why the asset is retired.");
        if (c.Stage is "missing" && status is "lost" or "stolen" && string.IsNullOrWhiteSpace(c.Reason))
            throw new LifecycleValidationException("reason", "Describe what happened (where and when it was last seen, police / insurance reference).");
        if (c.Stage == "disposed")
        {
            if (c.DisposedOn is null) throw new LifecycleValidationException("disposedOn", "Enter the disposal date.");
            if (c.SanitizationMethod is null || !SanitizationMethods.Contains(c.SanitizationMethod))
                throw new LifecycleValidationException("sanitizationMethod", "Choose how the data was removed (NIST SP 800-88: clear, purge or destroy), or 'not required' for devices without storage.");
            if (c.SanitizationMethod != "not_required" && (Trim(c.SanitizedBy) is null || c.SanitizedOn is null))
                throw new LifecycleValidationException("sanitizedBy", "Enter who removed the data and when.");
        }

        await using (var upd = new NpgsqlCommand("""
            UPDATE zentriq.asset_lifecycle SET
                   stage = @stage, status = @status, stage_reason = @reason,
                   stage_changed_at = CASE WHEN stage = @stage THEN stage_changed_at ELSE now() END,
                   auto_deploy = CASE WHEN @stage IN ('planned','on_order','in_stock') THEN auto_deploy ELSE false END,
                   assigned_to = CASE WHEN @stage = 'in_use' THEN @person WHEN @stage IN ('in_maintenance','missing') THEN assigned_to ELSE NULL END,
                   assigned_email = CASE WHEN @stage = 'in_use' AND @person IS NOT NULL THEN COALESCE(@email, CASE WHEN lower(assigned_to) = lower(@person) THEN assigned_email END)
                                         WHEN @stage IN ('in_maintenance','missing') THEN assigned_email ELSE NULL END,
                   assigned_at = CASE WHEN @stage = 'in_use' AND @person IS NOT NULL THEN CASE WHEN lower(coalesce(assigned_to, '')) = lower(@person) THEN COALESCE(assigned_at, now()) ELSE now() END
                                      WHEN @stage IN ('in_maintenance','missing') THEN assigned_at ELSE NULL END,
                   expected_return = CASE WHEN @status = 'loaned' THEN @ret ELSE NULL END,
                   in_service_date = CASE WHEN @stage = 'in_use' THEN COALESCE(in_service_date, current_date) ELSE in_service_date END,
                   disposed_on = CASE WHEN @stage = 'disposed' THEN @dOn ELSE disposed_on END,
                   disposal_method = CASE WHEN @stage = 'disposed' THEN @status ELSE disposal_method END,
                   disposal_vendor = CASE WHEN @stage = 'disposed' THEN @dVendor ELSE disposal_vendor END,
                   disposal_value = CASE WHEN @stage = 'disposed' THEN @dValue ELSE disposal_value END,
                   sanitization_method = CASE WHEN @stage = 'disposed' THEN @san ELSE sanitization_method END,
                   sanitized_by = CASE WHEN @stage = 'disposed' THEN @sanBy ELSE sanitized_by END,
                   sanitized_on = CASE WHEN @stage = 'disposed' THEN @sanOn ELSE sanitized_on END,
                   certificate_ref = CASE WHEN @stage = 'disposed' THEN @cert ELSE certificate_ref END,
                   updated_at = now(), updated_by = @by, row_version = row_version + 1
             WHERE asset_id = @a
            """, conn, tx))
        {
            upd.Parameters.AddWithValue("a", assetId);
            upd.Parameters.AddWithValue("stage", c.Stage);
            upd.Parameters.AddWithValue("status", status);
            upd.Parameters.Add(P("reason", Trim(c.Reason, 1000), NpgsqlDbType.Text));
            upd.Parameters.Add(P("person", person, NpgsqlDbType.Text));
            upd.Parameters.Add(P("email", Trim(c.AssignedEmail, 254), NpgsqlDbType.Text));
            upd.Parameters.Add(P("ret", c.ExpectedReturn, NpgsqlDbType.Date));
            upd.Parameters.Add(P("dOn", c.DisposedOn, NpgsqlDbType.Date));
            upd.Parameters.Add(P("dVendor", Trim(c.DisposalVendor), NpgsqlDbType.Text));
            upd.Parameters.Add(P("dValue", c.DisposalValue, NpgsqlDbType.Numeric));
            upd.Parameters.Add(P("san", c.SanitizationMethod, NpgsqlDbType.Text));
            upd.Parameters.Add(P("sanBy", Trim(c.SanitizedBy), NpgsqlDbType.Text));
            upd.Parameters.Add(P("sanOn", c.SanitizedOn, NpgsqlDbType.Date));
            upd.Parameters.Add(P("cert", Trim(c.CertificateRef), NpgsqlDbType.Text));
            upd.Parameters.Add(P("by", actor, NpgsqlDbType.Text));
            await upd.ExecuteNonQueryAsync(ct);
        }

        // Department / location move with the assignment, like the inventory itself.
        if (c.DepartmentId is not null || c.LocationId is not null)
        {
            await using var org = new NpgsqlCommand("""
                UPDATE zentriq.assets SET department_id = COALESCE(@d, department_id), location_id = COALESCE(@l, location_id), updated_at = now() WHERE id = @a
                """, conn, tx);
            org.Parameters.AddWithValue("a", assetId);
            org.Parameters.Add(P("d", c.DepartmentId, NpgsqlDbType.Uuid));
            org.Parameters.Add(P("l", c.LocationId, NpgsqlDbType.Uuid));
            await org.ExecuteNonQueryAsync(ct);
        }
        // Retired and disposed devices leave the active inventory (and licensing); back in stock brings them back.
        if (retiring && cur.AssetStatus == "active")
            await Exec(conn, tx, "UPDATE zentriq.assets SET status = 'retired', retired_at = now(), updated_at = now(), row_version = row_version + 1 WHERE id = @a", assetId, ct);
        else if (!retiring && cur.AssetStatus == "retired")
            await Exec(conn, tx, "UPDATE zentriq.assets SET status = 'active', retired_at = NULL, updated_at = now(), row_version = row_version + 1 WHERE id = @a", assetId, ct);
        if (c.Stage == "in_use" && person is not null)
            await AddPersonAsync(conn, tx, person, Trim(c.AssignedEmail, 254), "assignment", actor, ct);
        if (c.Stage == "in_use" && person is not null)
            await Exec(conn, tx, $"UPDATE zentriq.assets SET owner_name = (SELECT assigned_to FROM zentriq.asset_lifecycle WHERE asset_id = @a), updated_at = now() WHERE id = @a", assetId, ct);

        var kind = c.Stage == "disposed" ? "disposed"
                 : c.Stage == "in_use" && person is not null && !string.Equals(person, cur.AssignedTo, StringComparison.OrdinalIgnoreCase) ? "assigned"
                 : cur.AssignedTo is not null && person is null && c.Stage is "in_stock" ? "returned"
                 : "stage";
        return await EventAsync(conn, tx, assetId, actor, kind, cur.Stage, c.Stage, cur.Status, status, person ?? cur.AssignedTo, c.Reason,
            new Dictionary<string, object?>
            {
                ["expectedReturn"] = c.ExpectedReturn?.ToString("yyyy-MM-dd"), ["disposalVendor"] = Trim(c.DisposalVendor), ["disposalValue"] = c.DisposalValue,
                ["sanitization"] = c.SanitizationMethod, ["sanitizedBy"] = Trim(c.SanitizedBy), ["certificate"] = Trim(c.CertificateRef),
            }.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value), ct);
    }

    private static async Task Exec(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, Guid id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("a", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task<long> EventAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid assetId, string actor, string kind,
        string? fromStage, string? toStage, string? fromStatus, string? toStatus, string? person, string? reason, object? detail, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO zentriq.asset_lifecycle_events (asset_id, actor, kind, from_stage, to_stage, from_status, to_status, person, reason, detail)
            VALUES (@a, @actor, @k, @fs, @ts, @fst, @tst, @p, @r, @d::jsonb) RETURNING id
            """, conn, tx);
        cmd.Parameters.AddWithValue("a", assetId);
        cmd.Parameters.AddWithValue("actor", actor);
        cmd.Parameters.AddWithValue("k", kind);
        cmd.Parameters.Add(P("fs", fromStage, NpgsqlDbType.Text));
        cmd.Parameters.Add(P("ts", toStage, NpgsqlDbType.Text));
        cmd.Parameters.Add(P("fst", fromStatus, NpgsqlDbType.Text));
        cmd.Parameters.Add(P("tst", toStatus, NpgsqlDbType.Text));
        cmd.Parameters.Add(P("p", person, NpgsqlDbType.Text));
        cmd.Parameters.Add(P("r", Trim(reason, 1000), NpgsqlDbType.Text));
        cmd.Parameters.AddWithValue("d", System.Text.Json.JsonSerializer.Serialize(detail ?? new { }));
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// Gives the asset the next tag in the organisation's format (setting lifecycle.tag_format, default PREFIX-000123 with the
    /// device class prefix) unless it has one. Returns the tag. Works in manual mode too (the "Give the next tag" action).
    /// </summary>
    public static async Task<string> EnsureTagAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid assetId, CancellationToken ct, TagSettings? settings = null)
    {
        await using (var has = new NpgsqlCommand("SELECT asset_tag FROM zentriq.asset_lifecycle WHERE asset_id = @a", conn, tx))
        {
            has.Parameters.AddWithValue("a", assetId);
            if (await has.ExecuteScalarAsync(ct) is string t) return t;
        }
        var ts = settings ?? await TagSettings.LoadAsync(conn, tx, ct);
        string prefix = "AST", loc = "";
        await using (var p = new NpgsqlCommand($"""
            SELECT COALESCE(lp.tag_prefix, 'AST'), lo.code, lo.name FROM zentriq.assets a {ClassJoins}
              LEFT JOIN zentriq.lifecycle_policies lp ON lp.class = {ClassSql}
              LEFT JOIN zentriq.locations lo ON lo.id = a.location_id
             WHERE a.id = @a
            """, conn, tx))
        {
            p.Parameters.AddWithValue("a", assetId);
            await using var r = await p.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                prefix = r.GetString(0);
                loc = TagFormat.LocationCode(r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
            }
        }
        var today = DateOnly.FromDateTime(DateTime.Today);
        var key = TagFormat.CounterKey(ts.Format, prefix, ts.Org, loc, today);
        for (var attempt = 0; attempt < 200; attempt++)
        {
            await using var next = new NpgsqlCommand("""
                INSERT INTO zentriq.lifecycle_tag_counters (prefix, next_value) VALUES (@p, @start + 1)
                ON CONFLICT (prefix) DO UPDATE SET next_value = lifecycle_tag_counters.next_value + 1
                RETURNING next_value - 1
                """, conn, tx);
            next.Parameters.AddWithValue("p", key);
            next.Parameters.AddWithValue("start", ts.Start);
            var n = (int)(await next.ExecuteScalarAsync(ct))!;
            var tag = TagFormat.Render(ts.Format, prefix, ts.Org, loc, today, n);
            await using var set = new NpgsqlCommand("""
                UPDATE zentriq.asset_lifecycle SET asset_tag = @t, updated_at = now()
                 WHERE asset_id = @a AND asset_tag IS NULL AND NOT EXISTS (SELECT 1 FROM zentriq.asset_lifecycle x WHERE lower(x.asset_tag) = lower(@t))
                """, conn, tx);
            set.Parameters.AddWithValue("a", assetId);
            set.Parameters.AddWithValue("t", tag);
            if (await set.ExecuteNonQueryAsync(ct) == 1) return tag;
        }
        throw new LifecycleValidationException("assetTag", "Could not find a free asset tag; check the tag format and prefixes.");
    }

    /// <summary>Sets a tag typed by a person (or taken from an upload). Checks the characters and that no other asset has it.</summary>
    public static async Task SetTagAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid assetId, string tag, CancellationToken ct)
    {
        tag = tag.Trim();
        if (!TagFormat.TagPattern.IsMatch(tag)) throw new LifecycleValidationException("assetTag", $"Asset tag \"{tag}\": use letters, digits and . _ / - (up to 40 characters).");
        await using var dup = new NpgsqlCommand("SELECT 1 FROM zentriq.asset_lifecycle WHERE lower(asset_tag) = lower(@t) AND asset_id <> @a", conn, tx);
        dup.Parameters.AddWithValue("t", tag);
        dup.Parameters.AddWithValue("a", assetId);
        if (await dup.ExecuteScalarAsync(ct) is not null) throw new LifecycleValidationException("assetTag", $"Asset tag {tag} is already used by another asset.");
        await using var set = new NpgsqlCommand("UPDATE zentriq.asset_lifecycle SET asset_tag = @t, updated_at = now() WHERE asset_id = @a", conn, tx);
        set.Parameters.AddWithValue("t", tag);
        set.Parameters.AddWithValue("a", assetId);
        await set.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Adds a person to the people directory unless someone with that name is there (assignments, uploads).</summary>
    public static async Task<bool> AddPersonAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string name, string? email, string source, string? actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO zentriq.lifecycle_people (name, email, source, created_by)
            SELECT @n, @e, @s, @by
             WHERE NOT EXISTS (SELECT 1 FROM zentriq.lifecycle_people p WHERE lower(btrim(p.name)) = lower(btrim(@n)))
               AND NOT (@e::text IS NOT NULL AND EXISTS (SELECT 1 FROM zentriq.lifecycle_people p WHERE lower(p.email) = lower(@e)))
            """, conn, tx);
        cmd.Parameters.AddWithValue("n", name.Trim());
        cmd.Parameters.Add(new NpgsqlParameter("e", NpgsqlTypes.NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(email) ? DBNull.Value : email.Trim() });
        cmd.Parameters.AddWithValue("s", source);
        cmd.Parameters.Add(new NpgsqlParameter("by", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)actor ?? DBNull.Value });
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>
    /// Devices in use that Zentriq has not seen (agent, scan) for the configured number of days become Missing · Not seen
    /// (for review). Only devices Zentriq has inventoried before are considered. Returns the number changed.
    /// </summary>
    public static async Task<int> MarkNotSeenAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        int days;
        await using (var s = new NpgsqlCommand("SELECT value::text FROM zentriq.system_settings WHERE key = 'lifecycle.not_seen_days'", conn))
            days = int.TryParse((await s.ExecuteScalarAsync(ct) as string)?.Trim('"'), out var d) ? d : 60;
        if (days <= 0) return 0;
        await using var cmd = new NpgsqlCommand("""
            WITH x AS (
                SELECT l.asset_id, l.status, l.assigned_to,
                       GREATEST(a.last_seen_at, (SELECT max(r.last_checkin_at) FROM zentriq.agent_registrations r WHERE r.asset_id = a.id AND r.status = 'active')) AS seen
                  FROM zentriq.asset_lifecycle l JOIN zentriq.assets a ON a.id = l.asset_id
                 WHERE l.stage = 'in_use' AND a.status = 'active' AND a.last_inventory_at IS NOT NULL
                   AND l.status <> 'loaned' FOR UPDATE OF l),
            stale AS (SELECT * FROM x WHERE seen < now() - make_interval(days => @d)),
            upd AS (
                UPDATE zentriq.asset_lifecycle l SET stage = 'missing', status = 'not_seen', stage_changed_at = now(),
                       stage_reason = 'Not seen for ' || @d || ' days', updated_at = now(), updated_by = 'system', row_version = row_version + 1
                  FROM stale WHERE l.asset_id = stale.asset_id RETURNING l.asset_id)
            INSERT INTO zentriq.asset_lifecycle_events (asset_id, actor, kind, from_stage, to_stage, from_status, to_status, person, reason, detail)
            SELECT s.asset_id, 'system', 'auto', 'in_use', 'missing', s.status, 'not_seen', s.assigned_to, 'Not seen for ' || @d || ' days',
                   jsonb_build_object('lastSeen', s.seen)
              FROM stale s JOIN upd u ON u.asset_id = s.asset_id
            """, conn);
        cmd.Parameters.AddWithValue("d", days);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// A new device record whose identifiers (SMBIOS UUID, serial, MAC) belong to a retired or disposed asset: the retired
    /// device is in use again. Recorded on the retired asset, which raises the lifecycle.retired_seen alert. Retired assets
    /// keep their identifiers, so inventory creates a new record rather than reviving them.
    /// </summary>
    public static async Task<Guid?> RecordRetiredSeenAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid newAssetId,
        IReadOnlyList<(string Kind, string Value)> ids, CancellationToken ct)
    {
        var use = ids.Where(i => i.Kind is "smbios_uuid" or "serial" or "mac").ToList();
        if (use.Count == 0) return null;
        await using var cmd = new NpgsqlCommand("""
            SELECT a.id, l.stage FROM zentriq.asset_identifiers o
              JOIN zentriq.assets a ON a.id = o.asset_id AND a.status = 'retired' AND a.id <> @n
              JOIN zentriq.asset_lifecycle l ON l.asset_id = a.id
              JOIN unnest(@k, @v) AS x(k, v) ON o.kind = x.k AND o.value = x.v
             ORDER BY (o.kind = 'smbios_uuid') DESC, a.retired_at DESC NULLS LAST LIMIT 1
            """, conn, tx);
        cmd.Parameters.AddWithValue("n", newAssetId);
        cmd.Parameters.Add(new NpgsqlParameter("k", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = use.Select(i => i.Kind).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = use.Select(i => i.Value).ToArray() });
        Guid old; string stage;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return null;
            old = r.GetGuid(0); stage = r.GetString(1);
        }
        await EventAsync(conn, tx, old, "system", "seen_again", stage, stage, null, null, null,
            $"This {StageLabel(stage).ToLowerInvariant()} device was seen on the network again", new { newAssetId }, ct);
        return old;
    }

    /// <summary>
    /// A stock or on-order item (added before Zentriq saw it) with this serial number: the device being inventoried is that
    /// item, so the inventory is recorded on it instead of a new record.
    /// </summary>
    public static async Task<Guid?> MatchStockAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string? serial, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(serial) || serial.Trim().Length < 4) return null;
        await using var cmd = new NpgsqlCommand("""
            SELECT a.id FROM zentriq.assets a JOIN zentriq.asset_lifecycle l ON l.asset_id = a.id
             WHERE a.status = 'active' AND l.auto_deploy AND l.stage IN ('planned','on_order','in_stock')
               AND lower(a.serial_number) = lower(@s)
             ORDER BY a.created_at LIMIT 1 FOR UPDATE OF a
            """, conn, tx);
        cmd.Parameters.AddWithValue("s", serial.Trim());
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }
}
