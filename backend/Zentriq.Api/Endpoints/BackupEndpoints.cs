using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;
using Zentriq.Infrastructure.Backup;
using Zentriq.Infrastructure.Configuration;
using Zentriq.Infrastructure.Jobs;
using Zentriq.Infrastructure.Secrets;

namespace Zentriq.Api.Endpoints;

/// <summary>
/// Backup &amp; Restore: schedule and retention, backup password, destinations (another disk, network share, FTP/FTPS, SFTP,
/// Azure Blob, S3 / S3-compatible, Google Cloud Storage), back up now, history, download, and restore from an uploaded
/// file, a backup on this server or one at a destination. Backups and restores run in the privileged maintenance task
/// (zentriqctl as SYSTEM); the web process never reads the installation's backup key.
/// </summary>
public static class BackupEndpoints
{
    public sealed record SettingsBody(BackupSchedule? Schedule, int? KeepLocal, bool? IncludeExports, bool? IncludeLogs);
    public sealed record PasswordBody(string? Password, string? Confirm);
    public sealed record DestinationBody(string? Name, string? Kind, bool? Enabled, int? Keep, BackupTargetSettings? Settings,
        string? Password, string? PrivateKey, string? Passphrase, Guid? Id);
    public sealed record RestoreBody(string? Source, string? File, Guid? DestinationId, string? Password, string? RecoveryKey, string? Confirm);

    private static readonly string[] Sources = ["upload", "local", "destination"];

    public static void MapBackupEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/backup").WithTags("Backup & Restore").RequireAuthorization(Permissions.BackupRun);

        g.MapGet("", async (BackupStore store, NpgsqlDataSource ds, JobQueue jobs, CancellationToken ct) =>
        {
            var s = await store.LoadSettingsAsync(ct);
            var destinations = await store.DestinationsAsync(false, ct);
            var now = DateTimeOffset.UtcNow;
            object? lastRestore = null, running = null;
            await using (var cmd = ds.CreateCommand("SELECT value::text FROM zentriq.system_settings WHERE key = 'backup.last_restore'"))
                if (await cmd.ExecuteScalarAsync(ct) is string lr) lastRestore = JsonSerializer.Deserialize<JsonElement>(lr);
            await using (var cmd = ds.CreateCommand("""
                SELECT id, job_type, status, created_at, progress_text FROM zentriq.jobs
                 WHERE job_type IN ('maintenance.backup', 'maintenance.restore') AND status IN ('queued', 'running')
                 ORDER BY created_at DESC LIMIT 1
                """))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                if (await r.ReadAsync(ct))
                    running = new { jobId = r.GetGuid(0), kind = r.GetString(1) == "maintenance.restore" ? "restore" : "backup", status = r.GetString(2), requestedAt = r.GetFieldValue<DateTimeOffset>(3), progress = r.IsDBNull(4) ? null : r.GetString(4) };
            return Results.Ok(new
            {
                schedule = s.Schedule, scheduleText = s.Schedule.Describe(), nextRun = s.Schedule.NextRun(now),
                s.KeepLocal, s.IncludeExports, s.IncludeLogs,
                password = new { set = s.HasPassword, setAt = s.PasswordSetAt, setBy = s.PasswordSetBy },
                keyFingerprint = await store.KeyFingerprintAsync(ct),
                destinations = destinations.Select(DestinationJson),
                kinds = BackupKinds.All.Select(k => new { kind = k, title = BackupKinds.Title(k) }),
                lastRestore, running,
            });
        });

        g.MapPut("/settings", async (SettingsBody body, BackupStore store, RequestContext rc, CancellationToken ct) =>
        {
            var s = await store.LoadSettingsAsync(ct);
            if (body.Schedule is { } sch)
            {
                if (sch.Validate() is { } err) return Validation.Problem("schedule", err);
                sch.Weekdays = sch.Weekdays.Distinct().Order().ToArray();
                if (JsonSerializer.Serialize(sch) != JsonSerializer.Serialize(s.Schedule)) s.ScheduleChangedAt = DateTimeOffset.UtcNow;
                s.Schedule = sch;
            }
            if (body.KeepLocal is { } k)
            {
                if (k is < 0 or > BackupSettings.MaxKeep) return Validation.Problem("keepLocal", $"Keep 0 (all) to {BackupSettings.MaxKeep} backups on this server.");
                s.KeepLocal = k;
            }
            if (body.IncludeExports is { } ie) s.IncludeExports = ie;
            if (body.IncludeLogs is { } il) s.IncludeLogs = il;
            await store.SaveSettingsAsync(s, rc.UserId, ct);
            await rc.AuditAsync("backup.settings", "schedule", detail: new { schedule = s.Schedule.Describe(), s.KeepLocal, s.IncludeExports, s.IncludeLogs });
            return Results.Ok(new { scheduleText = s.Schedule.Describe(), nextRun = s.Schedule.NextRun(DateTimeOffset.UtcNow) });
        }).RequireAuthorization(Permissions.SettingsManage);

        // The password is turned into a key (PBKDF2-SHA256, 600,000 rounds); only that key is kept, encrypted.
        g.MapPut("/password", async (PasswordBody body, BackupStore store, RequestContext rc, CancellationToken ct) =>
        {
            if (BackupCrypto.CheckPassword(body.Password) is { } err) return Validation.Problem("password", err);
            if (body.Confirm is not null && body.Confirm != body.Password) return Validation.Problem("confirm", "The passwords do not match.");
            var s = await store.LoadSettingsAsync(ct);
            var key = await Task.Run(() => BackupCrypto.DerivePasswordKey(body.Password!), ct);
            s.PasswordSalt = key.Salt;
            s.PasswordIterations = key.Iterations;
            s.PasswordKey = store.ProtectPasswordKey(key.Key);
            s.PasswordSetAt = DateTimeOffset.UtcNow;
            s.PasswordSetBy = rc.UserName;
            await store.SaveSettingsAsync(s, rc.UserId, ct);
            await rc.AuditAsync("backup.password_set", "backup password");
            return Results.NoContent();
        }).RequireAuthorization(Permissions.SettingsManage);

        g.MapDelete("/password", async (BackupStore store, RequestContext rc, CancellationToken ct) =>
        {
            var s = await store.LoadSettingsAsync(ct);
            (s.PasswordSalt, s.PasswordIterations, s.PasswordKey, s.PasswordSetAt, s.PasswordSetBy) = (null, null, null, null, null);
            await store.SaveSettingsAsync(s, rc.UserId, ct);
            await rc.AuditAsync("backup.password_removed", "backup password");
            return Results.NoContent();
        }).RequireAuthorization(Permissions.SettingsManage);

        // ---- destinations ------------------------------------------------------------------------------------

        g.MapPost("/destinations", async (DestinationBody body, BackupStore store, RequestContext rc, CancellationToken ct) =>
        {
            var d = FromBody(body, Guid.NewGuid(), null);
            if (BackupTargets.Validate(d, false) is { } v) return Validation.Problem(v.Field, v.Message);
            try { await store.SaveDestinationAsync(d, insert: true, replaceSecret: true, rc.UserId, ct); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Validation.Problem("name", "A destination with this name exists."); }
            await rc.AuditAsync("backup.destination_create", d.Name, detail: new { d.Kind, where = Where(d) });
            return Results.Ok(new { id = d.Id });
        }).RequireAuthorization(Permissions.SettingsManage);

        g.MapPut("/destinations/{id:guid}", async (Guid id, DestinationBody body, BackupStore store, RequestContext rc, CancellationToken ct) =>
        {
            var existing = await store.DestinationAsync(id, true, ct);
            if (existing is null) return Results.NotFound();
            var d = FromBody(body, id, existing);
            if (d.Kind != existing.Destination.Kind) return Validation.Problem("kind", "The type of a destination cannot change; add a new destination instead.");
            var newSecret = !string.IsNullOrEmpty(body.Password) || !string.IsNullOrEmpty(body.PrivateKey);
            if (!newSecret) d.Secret = existing.Destination.Secret;
            // A changed server or account invalidates the pinned SFTP host key.
            if (d.Kind == BackupKinds.Sftp && (d.Settings.Host != existing.Destination.Settings.Host || d.Settings.Port != existing.Destination.Settings.Port)) d.Settings.HostKey = null;
            if (BackupTargets.Validate(d, existing.HasSecret) is { } v) return Validation.Problem(v.Field, v.Message);
            try { await store.SaveDestinationAsync(d, insert: false, replaceSecret: newSecret, rc.UserId, ct); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Validation.Problem("name", "A destination with this name exists."); }
            await rc.AuditAsync("backup.destination_update", d.Name, detail: new { d.Kind, where = Where(d), secretChanged = newSecret });
            return Results.NoContent();
        }).RequireAuthorization(Permissions.SettingsManage);

        g.MapDelete("/destinations/{id:guid}", async (Guid id, BackupStore store, RequestContext rc, CancellationToken ct) =>
        {
            var name = await store.DeleteDestinationAsync(id, ct);
            if (name is null) return Results.NotFound();
            await rc.AuditAsync("backup.destination_delete", name, detail: new { note = "Backups already copied there are not deleted." });
            return Results.NoContent();
        }).RequireAuthorization(Permissions.SettingsManage);

        // Test a destination before saving (body) or a saved one; the stored secret is used when no new one is given.
        g.MapPost("/destinations/test", async (DestinationBody body, BackupStore store, RequestContext rc, CancellationToken ct) =>
        {
            var existing = body.Id is { } eid ? await store.DestinationAsync(eid, true, ct) : null;
            var d = FromBody(body, body.Id ?? Guid.NewGuid(), existing);
            if (existing is not null && string.IsNullOrEmpty(body.Password) && string.IsNullOrEmpty(body.PrivateKey)) d.Secret = existing.Destination.Secret;
            if (existing is not null && d.Kind == BackupKinds.Sftp && d.Settings.Host == existing.Destination.Settings.Host) d.Settings.HostKey ??= existing.Destination.Settings.HostKey;
            if (BackupTargets.Validate(d, existing?.HasSecret == true) is { } v) return Validation.Problem(v.Field, v.Message);
            return await TestAsync(d, existing is null ? null : store, rc, ct);
        }).RequireAuthorization(Permissions.SettingsManage);

        g.MapPost("/destinations/{id:guid}/test", async (Guid id, BackupStore store, RequestContext rc, CancellationToken ct) =>
        {
            var row = await store.DestinationAsync(id, true, ct);
            return row is null ? Results.NotFound() : await TestAsync(row.Destination, store, rc, ct);
        }).RequireAuthorization(Permissions.SettingsManage);

        // Backups at a destination (to restore one; also after a rebuild, once the destination is added again).
        g.MapGet("/destinations/{id:guid}/files", async (Guid id, BackupStore store, CancellationToken ct) =>
        {
            var row = await store.DestinationAsync(id, true, ct);
            if (row is null) return Results.NotFound();
            try
            {
                using var t = BackupTargets.Create(row.Destination);
                var files = await t.ListAsync(ct);
                return Results.Ok(files.OrderByDescending(f => BackupNames.Parse(f.Name)?.Utc ?? f.Modified?.UtcDateTime ?? DateTime.MinValue).Select(f => FileJson(f.Name, f.Size, f.Modified)));
            }
            catch (Exception ex) when (ex is BackupTargetException or BackupHttpException or HttpRequestException or IOException or UnauthorizedAccessException)
            {
                var msg = ex is UnauthorizedAccessException ? $"The console service cannot read {row.Destination.Settings.Path}; it gets read access after the next backup copied there, or copy the file and use Upload." : ex.Message;
                return Results.Problem(statusCode: 502, title: msg);
            }
        }).RequireAuthorization(Permissions.SettingsManage);

        // ---- backups ----------------------------------------------------------------------------------------

        g.MapPost("/run", async (JobQueue jobs, RequestContext rc, CancellationToken ct) =>
        {
            var id = await jobs.EnqueueAsync(new EnqueueRequest(JobTypes.MaintenanceBackup, new { requestedBy = rc.UserName }, $"backup:{DateTimeOffset.UtcNow:yyyyMMddHHmm}",
                Priority: 10, MaxAttempts: 1, TimeoutSeconds: 4 * 3600, CreatedBy: rc.UserId), ct: ct);
            await rc.AuditAsync("backup.request", id.ToString());
            return Results.Accepted($"/api/v1/jobs/{id}", new { jobId = id, note = "The backup starts within 5 minutes (Zentriq maintenance task) and is then copied to the destinations." });
        });

        g.MapGet("/runs", async (int? page, int? pageSize, NpgsqlDataSource ds, ZentriqPaths paths, CancellationToken ct) =>
        {
            var (p, ps) = Paging.Normalize(page, pageSize);
            await using var cmd = ds.CreateCommand("""
                SELECT id, trigger, label, file_name, status, size_bytes, password_protected, contents::text, destinations::text, error, requested_by,
                       product_version, started_at, finished_at, local_deleted_at, count(*) OVER ()
                  FROM zentriq.backup_runs ORDER BY started_at DESC LIMIT @l OFFSET @o
                """);
            cmd.Parameters.AddWithValue("l", ps);
            cmd.Parameters.AddWithValue("o", (p - 1) * ps);
            var items = new List<object>();
            long total = 0;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                total = r.GetInt64(15);
                var file = r.GetString(3);
                bool? local = null;
                try { local = r.IsDBNull(14) && File.Exists(Path.Combine(paths.Backups, file)); } catch (UnauthorizedAccessException) { }
                items.Add(new
                {
                    id = r.GetGuid(0), trigger = r.GetString(1), label = r.GetString(2), file, status = r.GetString(4),
                    sizeBytes = r.IsDBNull(5) ? (long?)null : r.GetInt64(5), passwordProtected = r.GetBoolean(6),
                    contents = r.IsDBNull(7) ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(r.GetString(7)),
                    destinations = JsonSerializer.Deserialize<JsonElement>(r.GetString(8)),
                    error = r.IsDBNull(9) ? null : r.GetString(9), requestedBy = r.IsDBNull(10) ? null : r.GetString(10),
                    version = r.IsDBNull(11) ? null : r.GetString(11), startedAt = r.GetFieldValue<DateTimeOffset>(12),
                    finishedAt = r.IsDBNull(13) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(13), onServer = local,
                });
            }
            return Results.Ok(new { items, total, page = p, pageSize = ps });
        });

        // Backups in this server's Backups folder (including ones made on the command line or before this build).
        g.MapGet("/files", (ZentriqPaths paths) =>
        {
            try
            {
                if (!Directory.Exists(paths.Backups)) return Results.Ok(Array.Empty<object>());
                var files = new DirectoryInfo(paths.Backups).EnumerateFiles("zentriq-*.zbak").OrderByDescending(f => f.LastWriteTimeUtc).Take(200)
                    .Select(f => FileJson(f.Name, f.Length, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero))).ToList();
                return Results.Ok(files);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Problem(statusCode: 403, title: "The console cannot read the Backups folder yet. Run Zentriq Setup (Repair) once, or use zentriqctl on the server.");
            }
        });

        g.MapGet("/files/{name}", (string name, ZentriqPaths paths, RequestContext rc) =>
        {
            if (!IsFileName(name)) return Results.NotFound();
            var path = Path.Combine(paths.Backups, name);
            if (!File.Exists(path)) return Results.NotFound();
            _ = rc.AuditAsync("backup.download", name);
            return Results.File(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true), "application/octet-stream", name, enableRangeProcessing: true);
        }).RequireAuthorization(Permissions.SettingsManage);

        // ---- restore -----------------------------------------------------------------------------------------

        // Upload a backup file for a restore (body = the .zbak file). Returns what the backup is and how it can be opened.
        g.MapPost("/restore/upload", async (HttpContext http, ZentriqPaths paths, BackupStore store, CancellationToken ct) =>
        {
            if (http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size) size.MaxRequestBodySize = null;
            Directory.CreateDirectory(paths.RestoreUploads);
            foreach (var old in new DirectoryInfo(paths.RestoreUploads).EnumerateFiles().Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)))
                try { old.Delete(); } catch (IOException) { }
            var name = $"zentriq-upload-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.zbak";
            var path = Path.Combine(paths.RestoreUploads, name);
            await using (var f = File.Create(path)) await http.Request.Body.CopyToAsync(f, 1 << 20, ct);
            try
            {
                var header = ReadHeader(path);
                return Results.Ok(new { file = name, sizeBytes = new FileInfo(path).Length, backup = HeaderJson(header, await store.KeyFingerprintAsync(ct)) });
            }
            catch (InvalidDataException ex)
            {
                File.Delete(path);
                return Validation.Problem("file", ex.Message);
            }
        }).RequireAuthorization(Permissions.SettingsManage).DisableAntiforgery();

        g.MapGet("/restore/inspect", async (string file, ZentriqPaths paths, BackupStore store, CancellationToken ct) =>
        {
            if (!IsFileName(file)) return Results.NotFound();
            var path = Path.Combine(paths.Backups, file);
            if (!File.Exists(path)) return Results.NotFound();
            try { return Results.Ok(new { file, sizeBytes = new FileInfo(path).Length, backup = HeaderJson(ReadHeader(path), await store.KeyFingerprintAsync(ct)) }); }
            catch (InvalidDataException ex) { return Validation.Problem("file", ex.Message); }
        }).RequireAuthorization(Permissions.SettingsManage);

        // Start a restore. The password / recovery key is checked here when the file can be read; the maintenance task
        // runs the restore (stops the services, replaces the data, starts them again).
        g.MapPost("/restore", async (RestoreBody body, ZentriqPaths paths, BackupStore store, JobQueue jobs, RequestContext rc, CancellationToken ct) =>
        {
            if (body.Confirm != "RESTORE") return Validation.Problem("confirm", "Type RESTORE to confirm: all current data is replaced by the backup.");
            if (body.Source is null || !Sources.Contains(body.Source)) return Validation.Problem("source", "Choose the backup to restore.");
            if (body.File is null || !IsFileName(body.File)) return Validation.Problem("file", "Choose a backup file.");
            string? recovery = null;
            if (!string.IsNullOrWhiteSpace(body.RecoveryKey))
            {
                recovery = BackupCrypto.ParseRecoveryKey(body.RecoveryKey);
                if (recovery is null) return Validation.Problem("recoveryKey", "Paste the 'Key:' line (or the whole text) of the backup-recovery-key.txt file.");
            }
            byte[]? dataKey = null;
            if (body.Source is "upload" or "local")
            {
                var path = Path.Combine(body.Source == "upload" ? paths.RestoreUploads : paths.Backups, body.File);
                if (!File.Exists(path)) return Validation.Problem("file", "The backup file was not found; upload it again.");
                BackupHeader header;
                try { header = ReadHeader(path); }
                catch (InvalidDataException ex) { return Validation.Problem("file", ex.Message); }
                var serverKey = await store.KeyFingerprintAsync(ct);
                var ownKeyOpens = serverKey is not null && OpenedBy(header, serverKey);
                if (!string.IsNullOrEmpty(body.Password) || recovery is not null)
                {
                    try { dataKey = BackupCrypto.Unlock(header, new BackupUnlock(recovery is null ? null : [recovery], body.Password)); }
                    catch (BackupKeyException ex) when (!ownKeyOpens) { return Validation.Problem(string.IsNullOrEmpty(body.Password) ? "recoveryKey" : "password", ex.Message); }
                    catch (BackupKeyException) { /* this server's own key opens it */ }
                }
                else if (!ownKeyOpens)
                    return Validation.Problem("password", header.HasPassword
                        ? "This backup was made on another installation: enter its backup password (or paste its recovery key)."
                        : "This backup was made on another installation without a backup password: paste that installation's recovery key.");
            }
            else
            {
                if (body.DestinationId is null || await store.DestinationAsync(body.DestinationId.Value, false, ct) is null) return Validation.Problem("destinationId", "Choose the destination.");
            }
            var unlock = store.ProtectUnlock(string.IsNullOrEmpty(body.Password) ? null : body.Password, recovery, dataKey);
            var id = await jobs.EnqueueAsync(new EnqueueRequest(JobTypes.MaintenanceRestore,
                new { source = body.Source, file = body.File, destinationId = body.DestinationId?.ToString(), unlock, requestedBy = rc.UserName },
                $"restore:{DateTimeOffset.UtcNow:yyyyMMddHHmm}", Priority: 1, MaxAttempts: 1, TimeoutSeconds: 6 * 3600, CreatedBy: rc.UserId), ct: ct);
            await rc.AuditAsync("backup.restore_request", body.File, detail: new { body.Source, body.DestinationId, jobId = id });
            return Results.Accepted($"/api/v1/jobs/{id}", new
            {
                jobId = id,
                note = "The restore starts within 5 minutes. The console stops while the data is replaced and comes back by itself; then sign in with an account from the backup.",
            });
        }).RequireAuthorization(Permissions.SettingsManage);
    }

    private static async Task<IResult> TestAsync(BackupDestination d, BackupStore? store, RequestContext rc, CancellationToken ct)
    {
        // A local folder is written by the maintenance task (SYSTEM); the console only checks it can be reached.
        if (d.Kind == BackupKinds.Folder)
        {
            var path = d.Settings.Path!;
            var root = Path.GetPathRoot(path);
            string msg;
            if (root is null || !Directory.Exists(root)) msg = $"The drive {root} does not exist on the server.";
            else
            {
                string? free = null;
                try { var di = new DriveInfo(root); free = $"{di.AvailableFreeSpace / 1073741824.0:0.#} GB free on {root}"; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
                msg = (Directory.Exists(path) ? $"Folder {path} exists" : $"Folder {path} will be created") + (free is null ? "" : $"; {free}") +
                      ". Backups are written there by the Zentriq maintenance task (SYSTEM).";
                if (store is not null) await store.SetStatusAsync(d.Id, true, msg, null, ct);
                return Results.Ok(new { ok = true, message = msg });
            }
            if (store is not null) await store.SetStatusAsync(d.Id, false, msg, null, ct);
            return Results.Ok(new { ok = false, message = msg });
        }
        try
        {
            using var t = BackupTargets.Create(d);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            var r = await BackupTargets.TestAsync(t, timeout.Token);
            if (store is not null) await store.SetStatusAsync(d.Id, true, r.Message, r.HostKey, ct);
            await rc.AuditAsync("backup.destination_test", d.Name, detail: new { ok = true });
            return Results.Ok(new { ok = true, message = r.Message, hostKey = r.HostKey });
        }
        catch (Exception ex) when (ex is BackupTargetException or BackupHttpException or HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException)
        {
            var msg = ex is OperationCanceledException ? "No answer within 2 minutes." : ex.Message;
            if (store is not null) await store.SetStatusAsync(d.Id, false, msg, null, ct);
            return Results.Ok(new { ok = false, message = msg });
        }
    }

    private static BackupDestination FromBody(DestinationBody b, Guid id, DestinationRow? existing) => new()
    {
        Id = id,
        Name = (b.Name ?? existing?.Destination.Name ?? "").Trim(),
        Kind = b.Kind ?? existing?.Destination.Kind ?? "",
        Enabled = b.Enabled ?? existing?.Destination.Enabled ?? true,
        Keep = b.Keep ?? existing?.Destination.Keep ?? 30,
        Settings = Clean(b.Settings ?? existing?.Destination.Settings ?? new BackupTargetSettings(), existing?.Destination.Settings),
        Secret = new CredentialSecret
        {
            Password = string.IsNullOrEmpty(b.Password) ? null : b.Password,
            PrivateKey = string.IsNullOrWhiteSpace(b.PrivateKey) ? null : b.PrivateKey.Trim(),
            Passphrase = string.IsNullOrEmpty(b.Passphrase) ? null : b.Passphrase,
        },
    };

    private static BackupTargetSettings Clean(BackupTargetSettings s, BackupTargetSettings? before)
    {
        static string? T(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        return new BackupTargetSettings
        {
            Path = T(s.Path), Host = T(s.Host), Port = s.Port, User = T(s.User), Bucket = T(s.Bucket), Region = T(s.Region), Endpoint = T(s.Endpoint),
            Tls = T(s.Tls), Passive = s.Passive, AuthMode = T(s.AuthMode), PathStyle = s.PathStyle,
            // The pinned host key is kept unless the console clears it on purpose (empty string).
            HostKey = s.HostKey is null ? before?.HostKey : T(s.HostKey),
        };
    }

    private static string Where(BackupDestination d) => d.Kind switch
    {
        BackupKinds.Folder or BackupKinds.Smb => d.Settings.Path ?? "",
        BackupKinds.Ftp or BackupKinds.Sftp => $"{d.Kind}://{d.Settings.Host}/{d.Settings.Path}",
        BackupKinds.Azure => $"{d.Settings.User}/{d.Settings.Bucket}/{d.Settings.Path}",
        _ => $"{d.Settings.Bucket}/{d.Settings.Path}",
    };

    private static object DestinationJson(DestinationRow r)
    {
        var d = r.Destination;
        return new
        {
            d.Id, d.Name, d.Kind, kindTitle = BackupKinds.Title(d.Kind), d.Enabled, d.Keep, settings = d.Settings, hasSecret = r.HasSecret,
            where = Where(d), status = r.Status.Status, message = r.Status.Message, at = r.Status.At,
        };
    }

    private static object FileJson(string name, long size, DateTimeOffset? modified)
    {
        var p = BackupNames.Parse(name);
        return new { name, sizeBytes = size, createdAt = p is { } x ? new DateTimeOffset(x.Utc, TimeSpan.Zero) : modified, server = p?.Server, label = p?.Label };
    }

    private static object HeaderJson(BackupHeader h, string? serverKey) => new
    {
        h.CreatedAt, version = h.ProductVersion, machine = h.Machine, h.Label, format = h.Format, passwordProtected = h.HasPassword,
        keyFingerprint = h.KeyFingerprint, installationId = h.InstallationId, contents = h.Contents,
        sameInstallation = serverKey is not null && OpenedBy(h, serverKey),
    };

    private static bool OpenedBy(BackupHeader h, string fingerprint) =>
        h.KeyFingerprint == fingerprint || h.Keys?.Any(k => k.Type == BackupKeyWrap.InstallType && k.Fingerprint == fingerprint) == true;

    private static BackupHeader ReadHeader(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return BackupCrypto.ReadHeader(f);
    }

    private static bool IsFileName(string name) =>
        name.Length is > 6 and < 200 && name.EndsWith(".zbak", StringComparison.OrdinalIgnoreCase) && name == Path.GetFileName(name)
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
