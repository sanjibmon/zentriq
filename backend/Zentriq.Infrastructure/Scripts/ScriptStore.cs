using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Infrastructure.Deployment;

namespace Zentriq.Infrastructure.Scripts;

public sealed class ScriptSettings
{
    /// <summary>Scripts may be run on devices (a switch for the whole organisation).</summary>
    public bool Enabled { get; set; } = true;
    public int KeepOutputDays { get; set; } = 90;
}

public sealed record ScriptInput(string? Name, string? Description, string? Category, string? Platform, string? Language, string? Content, string? Parameters, int? TimeoutSeconds);

public sealed record Script(Guid Id, string Name, string? Description, string Category, string Platform, string Language, string Content, string Sha256, string? Parameters, int TimeoutSeconds, bool BuiltIn);

public sealed record ScriptRunRequest(Guid ScriptId, Guid? DetectScriptId, string? Parameters, TargetSpec Targets, string? Note, Guid? ScheduleId, string? Name = null);

public sealed record ScriptRunResult(Guid Id, int Sent, int Skipped);

/// <summary>
/// Scripts &amp; Remediation: a library of scripts, runs on devices with the Zentriq Agent (device actions
/// <c>script/run</c>; the agent checks the script's SHA-256, runs it as SYSTEM / root with a time limit and returns the
/// output and exit code), and schedules — a remediation runs a detection script first and the fix only where the
/// detection exits with a code other than 0.
/// </summary>
public static class ScriptStore
{
    public const string SettingsKey = "scripts.settings";
    public const string Capability = "scripts";
    public const int MaxContent = 128 * 1024;
    public const int MaxOutput = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static readonly string[] Languages = ["powershell", "cmd", "bash", "sh", "python"];

    public static string? PlatformOf(string language) => language switch
    {
        "powershell" or "cmd" => "windows",
        "bash" or "sh" => "linux",
        _ => null, // python: either
    };

    public static string Sha256(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    /// <summary>Line endings as the device expects them (CRLF for Windows scripts, LF for Linux).</summary>
    public static string Normalize(string content, string platform)
    {
        var lf = content.Replace("\r\n", "\n").Replace('\r', '\n');
        return platform == "windows" ? lf.Replace("\n", "\r\n") : lf;
    }

    /// <summary>Checks a script; returns (field, message) for the first problem.</summary>
    public static (string Field, string Message)? Validate(ScriptInput s)
    {
        if (string.IsNullOrWhiteSpace(s.Name) || s.Name.Trim().Length > 120) return ("name", "Enter a name (at most 120 characters).");
        if (s.Platform is not ("windows" or "linux")) return ("platform", "Choose Windows or Linux.");
        if (s.Language is null || !Languages.Contains(s.Language)) return ("language", "Choose PowerShell, Command Prompt, Bash, sh or Python.");
        if (PlatformOf(s.Language) is { } p && p != s.Platform)
            return ("language", s.Platform == "windows" ? "Windows scripts are PowerShell, Command Prompt or Python." : "Linux scripts are Bash, sh or Python.");
        if (string.IsNullOrWhiteSpace(s.Content)) return ("content", "The script is empty.");
        if (Encoding.UTF8.GetByteCount(s.Content) > MaxContent) return ("content", $"Scripts can be at most {MaxContent / 1024} KB.");
        if (s.Parameters is { Length: > 1000 }) return ("parameters", "Parameters can be at most 1000 characters.");
        if (s.Parameters is not null && (s.Parameters.Contains('\n') || s.Parameters.Contains('\r'))) return ("parameters", "Parameters must be on one line.");
        if (s.TimeoutSeconds is < 10 or > 14400) return ("timeoutSeconds", "The time limit is 10 seconds to 4 hours.");
        return null;
    }

    // ---- settings ---------------------------------------------------------------------------------------------------

    public static async Task<ScriptSettings> SettingsAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand($"SELECT value::text FROM zentriq.system_settings WHERE key = '{SettingsKey}'");
        return await cmd.ExecuteScalarAsync(ct) is string s ? JsonSerializer.Deserialize<ScriptSettings>(s, Json) ?? new() : new();
    }

    public static async Task SaveSettingsAsync(NpgsqlDataSource ds, ScriptSettings s, CancellationToken ct)
    {
        s.KeepOutputDays = Math.Clamp(s.KeepOutputDays, 7, 3650);
        await using var cmd = ds.CreateCommand("""
            INSERT INTO zentriq.system_settings (key, value, updated_at) VALUES (@k, @v::jsonb, now())
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now()
            """);
        cmd.Parameters.AddWithValue("k", SettingsKey);
        cmd.Parameters.AddWithValue("v", JsonSerializer.Serialize(s, Json));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---- library ----------------------------------------------------------------------------------------------------

    /// <summary>Ready-made scripts added to every library (they can be duplicated and changed, not edited).</summary>
    public static readonly ScriptInput[] BuiltIns =
    [
        new("Clear Temporary Files", "Deletes files older than 7 days from the Windows and user temporary folders.", "Maintenance", "windows", "powershell", """
            $limit = (Get-Date).AddDays(-7)
            $folders = @("$env:WINDIR\Temp") + (Get-ChildItem 'C:\Users' -Directory -ErrorAction SilentlyContinue | ForEach-Object { Join-Path $_.FullName 'AppData\Local\Temp' })
            $freed = 0
            foreach ($f in $folders) {
              if (-not (Test-Path $f)) { continue }
              Get-ChildItem $f -Recurse -Force -File -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -lt $limit } | ForEach-Object {
                try { $len = $_.Length; Remove-Item $_.FullName -Force -ErrorAction Stop; $freed += $len } catch { }
              }
            }
            "Freed {0:N1} MB" -f ($freed / 1MB)
            """, null, 900),
        new("Flush DNS Cache", "Clears the DNS resolver cache.", "Network", "windows", "cmd", "@echo off\r\nipconfig /flushdns", null, 60),
        new("Refresh Group Policy", "Applies computer Group Policy now (gpupdate /target:computer /force).", "Maintenance", "windows", "cmd", "@echo off\r\ngpupdate /target:computer /force", null, 300),
        new("Restart Print Spooler", "Restarts the Print Spooler service (stuck print queues).", "Services", "windows", "powershell", """
            Restart-Service -Name Spooler -Force -ErrorAction Stop
            Get-Service Spooler | Select-Object Name, Status | Format-List | Out-String
            """, null, 120),
        new("Disk Space Report", "Free space of every fixed drive.", "Information", "windows", "powershell", """
            Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | ForEach-Object {
              '{0} {1:N1} GB free of {2:N1} GB ({3:N0}%)' -f $_.DeviceID, ($_.FreeSpace / 1GB), ($_.Size / 1GB), (100.0 * $_.FreeSpace / [math]::Max(1.0, [double]$_.Size))
            }
            """, null, 60),
        new("Local Administrators", "Lists the members of the local Administrators group.", "Information", "windows", "powershell", """
            Get-LocalGroupMember -SID 'S-1-5-32-544' | Select-Object Name, ObjectClass, PrincipalSource | Format-Table -AutoSize | Out-String -Width 200
            """, null, 60),
        new("Detect: Print Spooler Not Running", "Remediation detection: exits with 1 when the Print Spooler service is not running.", "Remediation", "windows", "powershell", """
            $s = Get-Service -Name Spooler -ErrorAction SilentlyContinue
            if ($null -eq $s) { 'Print Spooler is not installed.'; exit 0 }
            if ($s.Status -ne 'Running') { "Print Spooler is $($s.Status)."; exit 1 }
            'Print Spooler is running.'; exit 0
            """, null, 60),
        new("Fix: Start Print Spooler", "Remediation fix: sets the Print Spooler to start automatically and starts it.", "Remediation", "windows", "powershell", """
            Set-Service -Name Spooler -StartupType Automatic
            Start-Service -Name Spooler -ErrorAction Stop
            "Print Spooler is $((Get-Service Spooler).Status)."
            """, null, 120),
        new("Disk Usage Report", "Disk usage of every mounted file system.", "Information", "linux", "sh", "df -hT -x tmpfs -x devtmpfs -x squashfs -x overlay", null, 60),
        new("Clean Package Caches", "Removes downloaded package files (apt, dnf, yum, zypper) and journal entries older than 14 days.", "Maintenance", "linux", "sh", """
            if command -v apt-get >/dev/null 2>&1; then apt-get clean; fi
            if command -v dnf >/dev/null 2>&1; then dnf -q clean packages; elif command -v yum >/dev/null 2>&1; then yum -q clean packages; fi
            if command -v zypper >/dev/null 2>&1; then zypper --non-interactive clean; fi
            if command -v journalctl >/dev/null 2>&1; then journalctl --vacuum-time=14d; fi
            df -h /
            """, null, 600),
        new("Restart a Service", "Restarts the systemd service named in the parameters (for example: cron).", "Services", "linux", "bash", """
            set -e
            if [ -z "$1" ]; then echo "Give the service name as the parameter." >&2; exit 2; fi
            systemctl restart -- "$1"
            systemctl --no-pager status -- "$1" | head -n 5
            """, "cron", 120),
        new("Detect: Root File System Over 90%", "Remediation detection: exits with 1 when / is more than 90% full.", "Remediation", "linux", "sh", """
            used=$(df -P / | awk 'NR==2 {gsub("%","",$5); print $5}')
            echo "/ is ${used}% full."
            [ "$used" -le 90 ]
            """, null, 60),
    ];

    /// <summary>Adds the ready-made scripts that are not in the library yet (by name).</summary>
    public static async Task EnsureBuiltInsAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        foreach (var b in BuiltIns)
        {
            var content = Normalize(b.Content!, b.Platform!);
            await using var cmd = ds.CreateCommand("""
                INSERT INTO zentriq.scripts (name, description, category, platform, language, content, sha256, parameters, timeout_seconds, built_in)
                VALUES (@n, @d, @c, @p, @l, @x, @h, @pa, @t, true)
                ON CONFLICT (lower(name)) DO UPDATE SET description = EXCLUDED.description, content = EXCLUDED.content, sha256 = EXCLUDED.sha256,
                    parameters = EXCLUDED.parameters, timeout_seconds = EXCLUDED.timeout_seconds, updated_at = now()
                 WHERE scripts.built_in AND scripts.sha256 <> EXCLUDED.sha256
                """);
            cmd.Parameters.AddWithValue("n", b.Name!);
            cmd.Parameters.AddWithValue("d", b.Description!);
            cmd.Parameters.AddWithValue("c", b.Category!);
            cmd.Parameters.AddWithValue("p", b.Platform!);
            cmd.Parameters.AddWithValue("l", b.Language!);
            cmd.Parameters.AddWithValue("x", content);
            cmd.Parameters.AddWithValue("h", Sha256(content));
            cmd.Parameters.Add(new NpgsqlParameter("pa", NpgsqlDbType.Text) { Value = (object?)b.Parameters ?? DBNull.Value });
            cmd.Parameters.AddWithValue("t", b.TimeoutSeconds ?? 600);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public static async Task<Script?> GetAsync(NpgsqlDataSource ds, Guid id, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT id, name, description, category, platform, language, content, sha256, parameters, timeout_seconds, built_in FROM zentriq.scripts WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public static Script Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
        r.GetString(6), r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), r.GetInt32(9), r.GetBoolean(10));

    public static async Task<Guid> SaveAsync(NpgsqlDataSource ds, Guid? id, ScriptInput s, Guid? userId, CancellationToken ct)
    {
        var content = Normalize(s.Content!, s.Platform!);
        await using var cmd = ds.CreateCommand(id is null
            ? """
              INSERT INTO zentriq.scripts (name, description, category, platform, language, content, sha256, parameters, timeout_seconds, created_by, updated_by)
              VALUES (@n, @d, @c, @p, @l, @x, @h, @pa, @t, @by, @by) RETURNING id
              """
            : """
              UPDATE zentriq.scripts SET name = @n, description = @d, category = @c, platform = @p, language = @l, content = @x, sha256 = @h, parameters = @pa,
                     timeout_seconds = @t, updated_by = @by, updated_at = now()
               WHERE id = @id AND NOT built_in RETURNING id
              """);
        if (id is not null) cmd.Parameters.AddWithValue("id", id.Value);
        cmd.Parameters.AddWithValue("n", s.Name!.Trim());
        cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(s.Description) ? DBNull.Value : s.Description.Trim()[..Math.Min(1000, s.Description.Trim().Length)] });
        cmd.Parameters.AddWithValue("c", string.IsNullOrWhiteSpace(s.Category) ? "General" : s.Category.Trim()[..Math.Min(60, s.Category.Trim().Length)]);
        cmd.Parameters.AddWithValue("p", s.Platform!);
        cmd.Parameters.AddWithValue("l", s.Language!);
        cmd.Parameters.AddWithValue("x", content);
        cmd.Parameters.AddWithValue("h", Sha256(content));
        cmd.Parameters.Add(new NpgsqlParameter("pa", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(s.Parameters) ? DBNull.Value : s.Parameters.Trim() });
        cmd.Parameters.AddWithValue("t", s.TimeoutSeconds ?? 600);
        cmd.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Uuid) { Value = (object?)userId ?? DBNull.Value });
        return await cmd.ExecuteScalarAsync(ct) is Guid g ? g : throw new KeyNotFoundException("Script not found (ready-made scripts cannot be changed; duplicate them).");
    }

    // ---- runs -------------------------------------------------------------------------------------------------------

    private static object Payload(Script s, int? timeout = null) => new { name = s.Name, language = s.Language, content = s.Content, sha256 = s.Sha256, timeout = timeout ?? s.TimeoutSeconds };

    /// <summary>Starts a script (or a remediation: detection, then the fix where needed) on the chosen devices.</summary>
    public static async Task<ScriptRunResult> RunAsync(NpgsqlDataSource ds, ScriptRunRequest req, Guid? userId, CancellationToken ct)
    {
        if (!(await SettingsAsync(ds, ct)).Enabled) throw new InvalidOperationException("Running scripts is turned off (Scripts & Remediation › Settings).");
        var script = await GetAsync(ds, req.ScriptId, ct) ?? throw new KeyNotFoundException("Choose a script.");
        Script? detect = null;
        if (req.DetectScriptId is { } did)
        {
            detect = await GetAsync(ds, did, ct) ?? throw new KeyNotFoundException("Choose the detection script.");
            if (detect.Platform != script.Platform) throw new InvalidOperationException("The detection script and the fix must be for the same platform.");
        }
        var parameters = string.IsNullOrWhiteSpace(req.Parameters) ? script.Parameters : req.Parameters.Trim();
        if (parameters is { Length: > 1000 } || (parameters is not null && (parameters.Contains('\n') || parameters.Contains('\r'))))
            throw new InvalidOperationException("Parameters must be on one line (at most 1000 characters).");
        var targets = await DeviceTargets.ResolveAsync(ds, req.Targets, Capability, ct);
        if (targets.Count > 10000) throw new InvalidOperationException("Run on at most 10 000 devices at a time.");
        var name = req.Name ?? (detect is null ? script.Name : $"{detect.Name} → {script.Name}");
        var argument = JsonSerializer.Serialize(new
        {
            name = script.Name, language = script.Language, content = script.Content, sha256 = script.Sha256, timeout = script.TimeoutSeconds, parameters,
            detect = detect is null ? null : Payload(detect),
        }, Json);
        var jobId = Guid.NewGuid();
        int sent = 0, skipped = 0;
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var j = new NpgsqlCommand("""
            INSERT INTO zentriq.script_jobs (id, script_id, detect_script_id, schedule_id, name, sha256, parameters, targets, note, created_by)
            VALUES (@id, @s, @d, @sch, @n, @h, @pa, @t::jsonb, @note, @by)
            """, conn, tx))
        {
            j.Parameters.AddWithValue("id", jobId);
            j.Parameters.AddWithValue("s", script.Id);
            j.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Uuid) { Value = (object?)detect?.Id ?? DBNull.Value });
            j.Parameters.Add(new NpgsqlParameter("sch", NpgsqlDbType.Uuid) { Value = (object?)req.ScheduleId ?? DBNull.Value });
            j.Parameters.AddWithValue("n", name.Length > 200 ? name[..200] : name);
            j.Parameters.AddWithValue("h", script.Sha256);
            j.Parameters.Add(new NpgsqlParameter("pa", NpgsqlDbType.Text) { Value = (object?)parameters ?? DBNull.Value });
            j.Parameters.AddWithValue("t", req.Targets.ToJson());
            j.Parameters.Add(new NpgsqlParameter("note", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(req.Note) ? DBNull.Value : req.Note.Trim()[..Math.Min(500, req.Note.Trim().Length)] });
            j.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Uuid) { Value = (object?)userId ?? DBNull.Value });
            await j.ExecuteNonQueryAsync(ct);
        }
        foreach (var t in targets)
        {
            var why = DeviceTargets.WhyNot(t, script.Platform, "scripts");
            Guid? actionId = null;
            if (why is null)
            {
                await using var a = new NpgsqlCommand("""
                    INSERT INTO zentriq.asset_actions (asset_id, agent_id, channel, kind, target, operation, argument, requested_by, detail, automatic)
                    VALUES (@as, @ag, 'agent', 'script', @t, 'run', @arg, @by, @d, @auto) RETURNING id
                    """, conn, tx);
                a.Parameters.AddWithValue("as", t.AssetId);
                a.Parameters.AddWithValue("ag", t.AgentId!.Value);
                a.Parameters.AddWithValue("t", jobId.ToString());
                a.Parameters.AddWithValue("arg", argument);
                a.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Uuid) { Value = (object?)userId ?? DBNull.Value });
                a.Parameters.AddWithValue("d", (detect is null ? "Run " : "Remediate: ") + name);
                a.Parameters.AddWithValue("auto", req.ScheduleId is not null);
                actionId = (Guid)(await a.ExecuteScalarAsync(ct))!;
                sent++;
            }
            else skipped++;
            await using var d = new NpgsqlCommand("INSERT INTO zentriq.script_job_devices (job_id, asset_id, action_id, skipped) VALUES (@j, @a, @x, @s)", conn, tx);
            d.Parameters.AddWithValue("j", jobId);
            d.Parameters.AddWithValue("a", t.AssetId);
            d.Parameters.Add(new NpgsqlParameter("x", NpgsqlDbType.Uuid) { Value = (object?)actionId ?? DBNull.Value });
            d.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Text) { Value = (object?)why ?? DBNull.Value });
            await d.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return new ScriptRunResult(jobId, sent, skipped);
    }

    public static string? Clip(string? s, int max = MaxOutput) => s is null ? null : s.Length > max ? s[..max] + "\n… (output shortened)" : s;

    /// <summary>Stores the output the agent reports for one of its script actions.</summary>
    public static async Task<bool> SaveOutputAsync(NpgsqlDataSource ds, Guid actionId, Guid agentId, int? exitCode, string? stdout, string? stderr, string? outcome, int? durationMs, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            INSERT INTO zentriq.asset_action_outputs (action_id, exit_code, stdout, stderr, outcome, duration_ms)
            SELECT id, @x, @o, @e, @oc, @ms FROM zentriq.asset_actions WHERE id = @id AND agent_id = @ag AND kind = 'script'
            ON CONFLICT (action_id) DO UPDATE SET exit_code = EXCLUDED.exit_code, stdout = EXCLUDED.stdout, stderr = EXCLUDED.stderr,
                outcome = EXCLUDED.outcome, duration_ms = EXCLUDED.duration_ms, received_at = now()
            """);
        cmd.Parameters.AddWithValue("id", actionId);
        cmd.Parameters.AddWithValue("ag", agentId);
        cmd.Parameters.Add(new NpgsqlParameter("x", NpgsqlDbType.Integer) { Value = (object?)exitCode ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("o", NpgsqlDbType.Text) { Value = (object?)Clip(Strip(stdout)) ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.Text) { Value = (object?)Clip(Strip(stderr)) ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("oc", NpgsqlDbType.Text) { Value = outcome is "ok" or "failed" or "compliant" or "remediated" or "timeout" ? outcome : DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("ms", NpgsqlDbType.Integer) { Value = durationMs is >= 0 ? durationMs : DBNull.Value });
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>PostgreSQL text cannot hold NUL characters.</summary>
    private static string? Strip(string? s) => s?.Replace("\0", "");

    // ---- schedules --------------------------------------------------------------------------------------------------

    /// <summary>
    /// The next run (server local time) after <paramref name="after"/>: hourly at the minute of <paramref name="at"/>,
    /// daily at <paramref name="at"/>, weekly on <paramref name="days"/> (0 = Sunday) at <paramref name="at"/>, once at
    /// <paramref name="once"/> (null when that has passed).
    /// </summary>
    public static DateTime? NextRun(string frequency, IReadOnlyCollection<int> days, TimeOnly at, DateTime? once, DateTime after)
    {
        switch (frequency)
        {
            case "once":
                return once is { } o && o > after ? o : null;
            case "hourly":
            {
                var t = new DateTime(after.Year, after.Month, after.Day, after.Hour, at.Minute, 0, after.Kind);
                return t > after ? t : t.AddHours(1);
            }
            case "daily":
            {
                var t = after.Date + at.ToTimeSpan();
                return t > after ? t : t.AddDays(1);
            }
            case "weekly":
            {
                if (days.Count == 0) return null;
                for (var i = 0; i <= 7; i++)
                {
                    var t = after.Date.AddDays(i) + at.ToTimeSpan();
                    if (t > after && days.Contains((int)t.DayOfWeek)) return t;
                }
                return null;
            }
            default:
                return null;
        }
    }

    public static async Task<int> RunDueSchedulesAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        var due = new List<(Guid Id, string Name, Guid Script, Guid? Detect, string? Parameters, string Targets, string Frequency, int[] Days, TimeOnly At, DateTime? Once)>();
        await using (var cmd = ds.CreateCommand("""
            SELECT id, name, script_id, detect_script_id, parameters, targets::text, frequency, days, at_time, run_once_at
              FROM zentriq.script_schedules WHERE enabled AND next_run_at IS NOT NULL AND next_run_at <= now()
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                due.Add((r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.IsDBNull(3) ? null : r.GetGuid(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.GetString(6),
                    r.GetFieldValue<int[]>(7), r.GetFieldValue<TimeOnly>(8), r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9).LocalDateTime));
        var ran = 0;
        foreach (var s in due)
        {
            var next = NextRun(s.Frequency, s.Days, s.At, s.Once, DateTime.Now);
            await using (var upd = ds.CreateCommand("UPDATE zentriq.script_schedules SET last_run_at = now(), next_run_at = @n, enabled = CASE WHEN @n IS NULL THEN false ELSE enabled END WHERE id = @id"))
            {
                upd.Parameters.AddWithValue("id", s.Id);
                upd.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.TimestampTz) { Value = next is { } n ? new DateTimeOffset(n) : DBNull.Value });
                await upd.ExecuteNonQueryAsync(ct);
            }
            try
            {
                await RunAsync(ds, new ScriptRunRequest(s.Script, s.Detect, s.Parameters, TargetSpec.FromJson(s.Targets), null, s.Id, $"Schedule: {s.Name}"), null, ct);
                ran++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException) { /* turned off or the script was removed: the schedule waits for its next run */ }
        }
        return ran;
    }

    /// <summary>Deletes script output older than the retention period.</summary>
    public static async Task<int> PurgeOutputAsync(NpgsqlDataSource ds, int days, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("DELETE FROM zentriq.asset_action_outputs WHERE received_at < now() - make_interval(days => @d)");
        cmd.Parameters.AddWithValue("d", Math.Max(7, days));
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
