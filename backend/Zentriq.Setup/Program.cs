using System.IO.Compression;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Zentriq.Domain;
using Zentriq.Infrastructure.Configuration;
using Zentriq.Infrastructure.Data;
using Zentriq.Infrastructure.Database;
using Zentriq.Infrastructure.Jobs;
using Zentriq.Infrastructure.Secrets;
using Zentriq.Setup;

// zentriqctl — Zentriq installation, repair, verification and maintenance tool.
// Runs elevated (installer custom action as LocalSystem, scheduled tasks as SYSTEM, or an administrator).
return await Cli.RunAsync(args);

internal static class Cli
{
    private const string Usage = """
        zentriqctl <command> [options]

          provision --input <file|->     Install, repair or upgrade using a JSON answer file ('-' = stdin).
                                         Used by the Zentriq installer; can also be run manually.
          repair                         Re-run provisioning with the existing configuration (no data loss).
          verify                         End-to-end health check (services, PostgreSQL, API over HTTPS, trial).
          status                         Short status summary.
          diagnose                       Collect a redacted diagnostics bundle in the Logs folder.
          backup [--label L] [--keep N]  Create an encrypted backup in the Backups folder and copy it to the
                                         destinations set in the console (Backup & Restore).
          backup-verify <file> [--recovery-key F | --password | --password-file F] [--deep]
                                         Check a backup can be decrypted (--deep: restore into a scratch database).
          restore <file> [--recovery-key F | --password | --password-file F] --yes
                                         Replace the current data with a backup (a safety backup is taken first).
                                         On a rebuilt server: install Zentriq, then restore with the backup
                                         password (or the old server's recovery key file).
          reset-password --email <addr> [--disable-mfa]
                                         Set a new password for a console user (prompted, not echoed),
                                         unlock the account and optionally turn off its MFA.
          maintenance run-pending        Run queued privileged jobs (used by the scheduled task).
          uninstall-cleanup              Remove scheduled tasks (used by the uninstaller). Never deletes data.
          version                        Print the version.

        Global options: --data-dir <dir>  (default: registry DataDirectory, then %ProgramData%\Zentriq)
        """;

    public static async Task<int> RunAsync(string[] argv)
    {
        var args = new List<string>(argv);
        var dataDir = TakeOption(args, "--data-dir");
        if (args.Count == 0 || args[0] is "-h" or "--help" or "help") { Console.WriteLine(Usage); return args.Count == 0 ? 2 : 0; }
        var command = args[0].ToLowerInvariant();
        args.RemoveAt(0);

        var paths = new ZentriqPaths(dataDir);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var ct = cts.Token;

        try
        {
            switch (command)
            {
                case "version":
                    Console.WriteLine(Version);
                    return 0;
                case "provision":
                {
                    var src = TakeOption(args, "--input") ?? throw new SetupException("provision requires --input <file|->.");
                    var mode = TakeOption(args, "--mode");
                    var pgInstaller = TakeOption(args, "--pg-installer");
                    var sections = TakeOption(args, "--sections");
                    var json = src == "-" ? await Console.In.ReadToEndAsync(ct) : await File.ReadAllTextAsync(src, ct);
                    var input = ProvisionInput.Parse(json);
                    json = ""; // do not keep the answer file (it may contain passwords) around longer than needed
                    if (mode is "install" or "repair" or "upgrade") input.Mode = mode;
                    else if (mode is not null) throw new SetupException($"Unknown mode '{mode}'.");
                    if (string.IsNullOrEmpty(input.Postgres.InstallerPath)) input.Postgres.InstallerPath = pgInstaller;
                    if (!string.IsNullOrWhiteSpace(sections)) input.Sections = sections;
                    return await ProvisionAsync(input, paths, ct);
                }
                case "repair":
                    return await ProvisionAsync(new ProvisionInput { Mode = "repair" }, paths, ct);
                case "verify":
                    return await VerifyAsync(paths, ct);
                case "status":
                    return await StatusAsync(paths, ct);
                case "diagnose":
                    return await DiagnoseAsync(paths, ct);
                case "backup":
                {
                    using var log = new Reporter(paths.Logs, "backup");
                    var label = TakeOption(args, "--label") ?? "manual";
                    var keep = int.TryParse(TakeOption(args, "--keep"), out var k) ? k : 0;
                    var file = await new BackupService(paths, LoadOptions(paths), log).CreateAsync(label, ct, keep);
                    log.Info($"Backup created: {file}");
                    return 0;
                }
                case "backup-verify":
                {
                    using var log = new Reporter(paths.Logs, "backup-verify");
                    var key = TakeOption(args, "--recovery-key");
                    var secret = BackupSecret(args);
                    var deep = TakeFlag(args, "--deep");
                    var file = args.FirstOrDefault() ?? throw new SetupException("backup-verify requires a backup file.");
                    var ok = await new BackupService(paths, LoadOptions(paths), log).VerifyAsync(file, key, deep, ct, secret);
                    log.Info(ok ? "Backup verification PASSED." : "Backup verification FAILED.");
                    return ok ? 0 : 1;
                }
                case "restore":
                {
                    using var log = new Reporter(paths.Logs, "restore");
                    var key = TakeOption(args, "--recovery-key");
                    var secret = BackupSecret(args);
                    var yes = TakeFlag(args, "--yes");
                    var file = args.FirstOrDefault() ?? throw new SetupException("restore requires a backup file.");
                    if (!yes) throw new SetupException("Restore replaces all current Zentriq data.", "Re-run with --yes to confirm.");
                    await new BackupService(paths, LoadOptions(paths), log).RestoreAsync(file, key, BackupService.ApiService, BackupService.WorkerService, ct, secret,
                        requestedBy: $"zentriqctl ({Environment.UserName})");
                    return 0;
                }
                case "reset-password":
                {
                    var email = TakeOption(args, "--email") ?? throw new SetupException("reset-password requires --email <address>.");
                    return await ResetPasswordAsync(paths, email, TakeFlag(args, "--disable-mfa"), ct);
                }
                case "maintenance":
                    if (args.FirstOrDefault() != "run-pending") throw new SetupException("Usage: zentriqctl maintenance run-pending");
                    return await MaintenanceAsync(paths, ct);
                case "uninstall-cleanup":
                    return await UninstallCleanupAsync(paths);
                default:
                    Console.Error.WriteLine($"Unknown command '{command}'.\n");
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }
        catch (SetupException ex)
        {
            Console.Error.WriteLine($"ERROR {ex.Message}");
            if (ex.Remedy is not null) Console.Error.WriteLine($"REMEDY {ex.Remedy}");
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("ERROR Cancelled.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR {ex.GetType().Name}: {Zentriq.Application.Security.Redactor.Redact(ex.Message)}");
            return 1;
        }
    }

    public static string Version => typeof(Cli).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    // ---- provision ------------------------------------------------------------------------------

    private static async Task<int> ProvisionAsync(ProvisionInput input, ZentriqPaths paths, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.Logs);
        using var log = new Reporter(paths.Logs, "provision");
        input.ProductVersion = Version;
        MergeExistingConfiguration(input, paths, log);
        log.Info($"zentriqctl {Version} provisioning (mode {input.Mode}), data directory {paths.Root}");
        var result = await new Provisioner(input, paths, log).RunAsync(ct);
        if (result.Succeeded)
        {
            // Marks this data directory as provisioned. Not owned by the MSI, so it survives uninstall
            // and lets a later install find and reuse the existing data instead of starting over.
            using (var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(ZentriqPaths.RegistryKey))
                key.SetValue("ProvisionedDataDirectory", paths.Root);
            Console.WriteLine($"RESULT OK {result.Url}");
            return 0;
        }
        Console.WriteLine($"RESULT FAILED {result.FailedStep}");
        Console.Error.WriteLine($"ERROR {result.Error}");
        if (result.Remedy is not null) Console.Error.WriteLine($"REMEDY {result.Remedy}");
        Console.Error.WriteLine($"LOG {result.LogPath}");
        return 1;
    }

    /// <summary>
    /// Repair and upgrade must keep the existing PostgreSQL connection, HTTPS settings and service
    /// names; values in the existing configuration file win over defaults in the answer file.
    /// A fresh install over existing data (e.g. reinstall after uninstall) is treated the same way.
    /// </summary>
    internal static void MergeExistingConfiguration(ProvisionInput input, ZentriqPaths paths, Reporter log)
    {
        if (!File.Exists(paths.ConfigFile)) return;
        ZentriqOptions existing;
        try { existing = LoadOptions(paths); }
        catch (Exception ex) { log.Warn($"Existing configuration could not be read ({ex.Message}); using installer values."); return; }

        var keep = input.Mode is "repair" or "upgrade" || File.Exists(paths.OwnerSecretsFile);
        if (!keep) return;
        if (input.Mode == "install") { input.Mode = "repair"; log.Info("Existing Zentriq data found; running as repair (data is preserved)."); }

        input.Postgres.Host = existing.Database.Host;
        input.Postgres.Port = existing.Database.Port;
        input.Postgres.Database = existing.Database.Database;
        input.Postgres.SslMode = existing.Database.SslMode;
        if (!string.IsNullOrEmpty(existing.PostgresServiceName))
        {
            input.Postgres.Mode = "bundled";
            input.Postgres.ServiceName = existing.PostgresServiceName;
        }
        else if (input.Postgres.Mode == "bundled")
        {
            input.Postgres.Mode = "existing";
        }
        if (!string.IsNullOrEmpty(existing.PostgresBinDirectory)) input.Postgres.BinDir = existing.PostgresBinDirectory;
        input.Https.Port = existing.Https.Port;
        input.Https.HostName ??= existing.Https.HostName;
        if (string.IsNullOrWhiteSpace(input.Https.CertificateThumbprint)) input.Https.CertificateThumbprint = existing.Https.CertificateThumbprint;
        log.Info($"Reusing configuration: PostgreSQL {input.Postgres.Mode} at {input.Postgres.Host}:{input.Postgres.Port}, HTTPS port {input.Https.Port}.");
    }

    internal static ZentriqOptions LoadOptions(ZentriqPaths paths)
    {
        if (!File.Exists(paths.ConfigFile))
            throw new SetupException($"Configuration not found at {paths.ConfigFile}.", "Run the Zentriq installer (or 'zentriqctl provision').");
        using var doc = JsonDocument.Parse(File.ReadAllText(paths.ConfigFile));
        return doc.RootElement.GetProperty(ZentriqOptions.Section).Deserialize<ZentriqOptions>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new SetupException("Configuration file is empty.");
    }

    // ---- verify / status ------------------------------------------------------------------------

    private static async Task<int> VerifyAsync(ZentriqPaths paths, CancellationToken ct)
    {
        using var log = new Reporter(paths.Logs, "verify");
        var checks = await HealthProbe.RunAsync(paths, log, ct);
        foreach (var (name, (ok, detail)) in checks) log.Info($"{(ok ? "PASS" : "FAIL")} {name}: {detail}");
        var allOk = checks.Values.All(c => c.Ok);
        log.Info(allOk ? "All checks passed." : "One or more checks failed. Run 'zentriqctl diagnose' and see docs/troubleshooting.md.");
        return allOk ? 0 : 1;
    }

    private static async Task<int> StatusAsync(ZentriqPaths paths, CancellationToken ct)
    {
        using var log = new Reporter(null, "status");
        var checks = await HealthProbe.RunAsync(paths, log, ct);
        foreach (var (name, (ok, detail)) in checks) Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {name,-28} {detail}");
        return checks.Values.All(c => c.Ok) ? 0 : 1;
    }

    // ---- diagnose -------------------------------------------------------------------------------

    private static async Task<int> DiagnoseAsync(ZentriqPaths paths, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.Logs);
        using var log = new Reporter(paths.Logs, "diagnose");
        var sb = new StringBuilder();
        sb.AppendLine($"Zentriq diagnostics {DateTimeOffset.UtcNow:O}");
        sb.AppendLine($"zentriqctl {Version}; Windows {Environment.OSVersion.Version}; machine {Environment.MachineName}; data {paths.Root}");
        foreach (var svc in new[] { "ZentriqApi", "ZentriqWorker" })
            sb.AppendLine($"service {svc}: {WindowsHost.ServiceStatus(svc)}");
        try
        {
            var o = LoadOptions(paths);
            if (!string.IsNullOrEmpty(o.PostgresServiceName)) sb.AppendLine($"service {o.PostgresServiceName}: {WindowsHost.ServiceStatus(o.PostgresServiceName)}");
        }
        catch (Exception ex) { sb.AppendLine($"config: {ex.Message}"); }

        try
        {
            var checks = await HealthProbe.RunAsync(paths, log, ct);
            foreach (var (name, (ok, detail)) in checks) sb.AppendLine($"{(ok ? "PASS" : "FAIL")} {name}: {detail}");
        }
        catch (Exception ex) { sb.AppendLine($"health probe error: {ex.Message}"); }

        var zipPath = Path.Combine(paths.Logs, $"diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            void AddText(string name, string text)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
                w.Write(Zentriq.Application.Security.Redactor.Redact(text));
            }
            AddText("summary.txt", sb.ToString());
            if (File.Exists(paths.ConfigFile)) AddText("config/zentriq.json", File.ReadAllText(paths.ConfigFile));
            if (File.Exists(paths.InstallResultFile)) AddText("install-result.json", File.ReadAllText(paths.InstallResultFile));
            // Most recent logs only; secrets files and keys are never included.
            foreach (var f in new DirectoryInfo(paths.Logs).EnumerateFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).Take(15))
            {
                try
                {
                    using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (fs.Length > 8 * 1024 * 1024) fs.Seek(-8 * 1024 * 1024, SeekOrigin.End);
                    using var r = new StreamReader(fs);
                    AddText("logs/" + f.Name, r.ReadToEnd());
                }
                catch (IOException) { }
            }
        }
        Console.WriteLine(sb.ToString());
        log.Info($"Diagnostics bundle written to {zipPath} (redacted; contains no secrets files).");
        return 0;
    }

    // ---- reset-password ---------------------------------------------------------------------------

    /// <summary>Break-glass password reset for an administrator who is locked out (run elevated on the server).</summary>
    private static async Task<int> ResetPasswordAsync(ZentriqPaths paths, string email, bool disableMfa, CancellationToken ct)
    {
        if (!File.Exists(paths.ConfigFile)) throw new SetupException("Zentriq is not provisioned on this server.");
        var options = LoadOptions(paths);
        var app = new SecretStore(SecretProtectorFactory.Create()).Read<AppSecrets>(paths.AppSecretsFile);
        await using var ds = NpgsqlDataSource.Create(ConnectionStrings.Build(options.Database, options.Database.AppUser, app.DbAppPassword, applicationName: "zentriqctl"));
        Guid id; string displayName;
        await using (var find = ds.CreateCommand("SELECT id, display_name FROM zentriq.users WHERE lower(email) = lower(@e)"))
        {
            find.Parameters.AddWithValue("e", email.Trim());
            await using var r = await find.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw new SetupException($"No user with the e-mail address '{email}'.");
            id = r.GetGuid(0); displayName = r.GetString(1);
        }
        string password;
        if (Console.IsInputRedirected) password = (await Console.In.ReadLineAsync(ct) ?? "").TrimEnd('\r', '\n');
        else
        {
            password = ReadHidden($"New password for {email}: ");
            if (ReadHidden("Repeat the new password: ") != password) throw new SetupException("The passwords do not match.");
        }
        var err = Zentriq.Application.Security.PasswordPolicy.Validate(password, email);
        if (err is not null) throw new SetupException(err);
        var user = new User { Id = id, Email = email };
        var hash = new Microsoft.AspNetCore.Identity.PasswordHasher<User>().HashPassword(user, password);
        await using (var upd = ds.CreateCommand($"""
            UPDATE zentriq.users SET password_hash = @h, password_changed_at = now(), security_stamp = gen_random_uuid(), failed_attempts = 0,
                   locked_until = NULL, status = 'active', updated_at = now(){(disableMfa ? ", mfa_enabled = false, mfa_secret_protected = NULL" : "")}
             WHERE id = @id
            """))
        {
            upd.Parameters.AddWithValue("h", hash);
            upd.Parameters.AddWithValue("id", id);
            await upd.ExecuteNonQueryAsync(ct);
        }
        await new Zentriq.Infrastructure.Audit.AuditWriter(ds).WriteAsync(new Zentriq.Infrastructure.Audit.AuditActor(null, $"zentriqctl ({Environment.UserName})", null),
            "user.password_reset_local", email, "Success", new { mfaDisabled = disableMfa }, ct: ct);
        Console.WriteLine($"Password reset for {displayName} <{email}>. The account is unlocked{(disableMfa ? " and MFA is turned off (set it up again after signing in)" : "")}. Existing sessions are signed out.");
        return 0;
    }

    /// <summary>The backup password from --password-file (first line) or --password (prompted, not echoed); null when neither is given.</summary>
    private static string? BackupSecret(List<string> args)
    {
        var file = TakeOption(args, "--password-file");
        if (file is not null) return File.ReadLines(file).FirstOrDefault()?.TrimEnd('\r', '\n') ?? throw new SetupException("The password file is empty.");
        return TakeFlag(args, "--password") ? ReadHidden("Backup password: ") : null;
    }

    private static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }

    // ---- maintenance ----------------------------------------------------------------------------

    private static async Task<int> MaintenanceAsync(ZentriqPaths paths, CancellationToken ct)
    {
        if (!File.Exists(paths.ConfigFile)) return 0; // not provisioned yet
        var options = LoadOptions(paths);
        var owner = new SecretStore(SecretProtectorFactory.Create()).Read<OwnerSecrets>(paths.OwnerSecretsFile);
        await using var ds = NpgsqlDataSource.Create(ConnectionStrings.Build(options.Database, options.Database.OwnerUser, owner.DbOwnerPassword, applicationName: "zentriqctl-maintenance"));
        var queue = new JobQueue(ds);
        var workerId = $"zentriqctl:{Environment.MachineName}";
        Reporter? log = null;
        try
        {
            // Scheduled backups (Backup & Restore › Schedule).
            try
            {
                var store = new Zentriq.Infrastructure.Backup.BackupStore(ds, new CredentialProtector(() => new SecretStore(SecretProtectorFactory.Create()).Read<AppSecrets>(paths.AppSecretsFile)));
                var settings = await store.LoadSettingsAsync(ct);
                if (settings.Schedule.IsDue(DateTimeOffset.UtcNow, await store.LastScheduledStartAsync(ct), settings.ScheduleChangedAt))
                {
                    log ??= new Reporter(paths.Logs, "maintenance");
                    log.Info($"Scheduled backup ({settings.Schedule.Describe()}).");
                    try
                    {
                        var r = await new BackupService(paths, options, log).CreateWithCopiesAsync("scheduled", ct);
                        log.Info($"Scheduled backup {Path.GetFileName(r.File)}: {r.Status}.");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { log.Error("Scheduled backup failed: " + (ex is SetupException se ? se.Message : ex.Message)); }
                }
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable) { /* schema older than 27: upgrade in progress */ }

            for (var i = 0; i < 10; i++)
            {
                var job = await queue.LeaseNextAsync(workerId, TimeSpan.FromHours(2), maintenance: true, ct);
                if (job is null) break;
                log ??= new Reporter(paths.Logs, "maintenance");
                log.Info($"Running {job.JobType} {job.Id} (attempt {job.Attempts}/{job.MaxAttempts}).");
                try
                {
                    switch (job.JobType)
                    {
                        case JobTypes.MaintenanceBackup:
                        {
                            await queue.ReportProgressAsync(job.Id, 10, "Creating encrypted backup", ct);
                            var by = Payload(job, "requestedBy");
                            var r = await new BackupService(paths, options, log).CreateWithCopiesAsync("manual", ct, requestedBy: by);
                            await queue.CompleteAsync(job.Id, workerId, new { file = Path.GetFileName(r.File), bytes = r.Size, status = r.Status, copies = r.Destinations }, ct);
                            log.Info($"Backup {r.File} completed ({r.Status}).");
                            break;
                        }
                        case JobTypes.MaintenanceRestore:
                            await queue.ReportProgressAsync(job.Id, 5, "Restoring from backup", ct);
                            // The job row is replaced with the database it is in; the result is noted in the restored database.
                            await RestoreJobAsync(paths, options, ds, log, job, ct);
                            break;
                        default:
                            await queue.FailAsync(job.Id, workerId, $"Unknown maintenance job type {job.JobType}.", retryable: false, ct);
                            break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var msg = ex is SetupException se ? se.Message : $"{ex.GetType().Name}: {ex.Message}";
                    log.Error($"{job.JobType} {job.Id} failed: {msg}");
                    await queue.FailAsync(job.Id, workerId, msg, retryable: job.Attempts < job.MaxAttempts, ct);
                    if (job.JobType == JobTypes.MaintenanceRestore)
                    {
                        try
                        {
                            await using var n = ds.CreateCommand("INSERT INTO zentriq.notifications (user_id, kind, title, body, link) VALUES (NULL, 'crit', 'Restore failed', @b, '/backup')");
                            n.Parameters.AddWithValue("b", $"The restore did not complete: {msg}");
                            await n.ExecuteNonQueryAsync(ct);
                        }
                        catch (Exception nex) when (nex is NpgsqlException or PostgresException) { log.Warn("Could not add a console notification: " + nex.Message); }
                    }
                }
            }
            return 0;
        }
        finally { log?.Dispose(); }
    }

    private static string? Payload(LeasedJob job, string name)
    {
        using var d = JsonDocument.Parse(job.Payload);
        return d.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    /// <summary>
    /// A restore requested in the console: the backup is an uploaded file (Restore folder), a backup on this server, or one
    /// at a destination (downloaded first). The console checked the password or recovery key and handed over the data key,
    /// encrypted with the installation credential key.
    /// </summary>
    private static async Task RestoreJobAsync(ZentriqPaths paths, ZentriqOptions options, NpgsqlDataSource ds, Reporter log, LeasedJob job, CancellationToken ct)
    {
        var source = Payload(job, "source");
        var name = Path.GetFileName(Payload(job, "file") ?? throw new SetupException("The restore request names no backup file."));
        if (!name.EndsWith(".zbak", StringComparison.OrdinalIgnoreCase)) throw new SetupException($"'{name}' is not a backup file name.");
        var app = new SecretStore(SecretProtectorFactory.Create()).Read<AppSecrets>(paths.AppSecretsFile);
        var store = new Zentriq.Infrastructure.Backup.BackupStore(ds, new CredentialProtector(() => app));
        var unlock = Payload(job, "unlock") is { } u ? store.UnprotectUnlock(u) : new Zentriq.Infrastructure.Backup.BackupUnlock();
        var by = Payload(job, "requestedBy");
        string file;
        string? downloaded = null;
        switch (source)
        {
            case "upload":
                file = Path.Combine(paths.RestoreUploads, name);
                break;
            case "local":
                file = Path.Combine(paths.Backups, name);
                break;
            case "destination":
            {
                var id = Guid.Parse(Payload(job, "destinationId") ?? throw new SetupException("The restore request names no destination."));
                var row = await store.DestinationAsync(id, true, ct) ?? throw new SetupException("The backup destination no longer exists.");
                downloaded = file = Path.Combine(paths.Backups, ".download-" + name);
                using var target = Zentriq.Infrastructure.Backup.BackupTargets.Create(row.Destination);
                log.Info($"Downloading {name} from {target.Where}.");
                await target.DownloadAsync(name, file, ct);
                break;
            }
            default:
                throw new SetupException($"Unknown restore source '{source}'.");
        }
        if (!File.Exists(file)) throw new SetupException($"The backup file {name} was not found.");
        try
        {
            await new BackupService(paths, options, log).RestoreAsync(file, null, BackupService.ApiService, BackupService.WorkerService, ct,
                unlock.Password, unlock.DataKey, by, unlock.InstallKeys);
        }
        finally
        {
            if (downloaded is not null) { try { File.Delete(downloaded); } catch (IOException) { } }
            if (source == "upload") { try { File.Delete(file); } catch (IOException) { } }
        }
    }

    // ---- uninstall ------------------------------------------------------------------------------

    private static async Task<int> UninstallCleanupAsync(ZentriqPaths paths)
    {
        Reporter log;
        try { log = new Reporter(paths.Logs, "uninstall"); } catch { log = new Reporter(null, "uninstall"); }
        using (log)
        {
            await WindowsHost.RemoveTaskAsync(@"\Zentriq\Maintenance");
            await WindowsHost.RemoveTaskAsync(@"\Zentriq\Nightly backup");
            log.Info("Removed Zentriq scheduled tasks.");
            log.Info($"Customer data was NOT removed: {paths.Root} and the PostgreSQL database are preserved. See docs/administrator-guide.md for complete removal.");
        }
        return 0;
    }

    // ---- argument helpers -----------------------------------------------------------------------

    private static string? TakeOption(List<string> args, string name)
    {
        var i = args.FindIndex(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return null;
        if (i + 1 >= args.Count) throw new SetupException($"{name} requires a value.");
        var v = args[i + 1];
        args.RemoveRange(i, 2);
        return v;
    }

    private static bool TakeFlag(List<string> args, string name)
    {
        var i = args.FindIndex(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return false;
        args.RemoveAt(i);
        return true;
    }
}

/// <summary>Checks shared by verify, status and diagnose.</summary>
internal static class HealthProbe
{
    public static async Task<Dictionary<string, (bool Ok, string Detail)>> RunAsync(ZentriqPaths paths, Reporter log, CancellationToken ct)
    {
        var r = new Dictionary<string, (bool, string)>();
        ZentriqOptions options;
        try { options = Cli.LoadOptions(paths); r["configuration"] = (true, paths.ConfigFile); }
        catch (Exception ex) { r["configuration"] = (false, ex.Message); return r; }

        if (!string.IsNullOrEmpty(options.PostgresServiceName))
            r["postgresql service"] = Svc(options.PostgresServiceName);
        r["ZentriqWorker service"] = Svc("ZentriqWorker");
        r["ZentriqApi service"] = Svc("ZentriqApi");

        var store = new SecretStore(SecretProtectorFactory.Create());
        try
        {
            var app = store.Read<AppSecrets>(paths.AppSecretsFile);
            await using var conn = new NpgsqlConnection(ConnectionStrings.Build(options.Database, options.Database.AppUser, app.DbAppPassword, applicationName: "zentriqctl"));
            await conn.OpenAsync(ct);
            var status = await new SqlMigrator(NullLogger<SqlMigrator>.Instance).GetStatusAsync(conn, ct);
            r["postgresql connection"] = (true, $"PostgreSQL {conn.PostgreSqlVersion} as {options.Database.AppUser}");
            r["database schema"] = (status.UpToDate, $"version {status.CurrentVersion} of {status.LatestVersion}");
            await using var cmd = new NpgsqlCommand("SELECT initialized_at FROM zentriq.install_state WHERE id = 1", conn);
            var init = await cmd.ExecuteScalarAsync(ct);
            r["initialization"] = (init is DateTime or DateTimeOffset, init is null or DBNull ? "not initialized" : $"initialized {init:u}");
            await using var hb = new NpgsqlCommand("SELECT max(last_beat_at) FROM zentriq.worker_heartbeats", conn);
            var beat = await hb.ExecuteScalarAsync(ct);
            var recent = beat is DateTime dt && dt.ToUniversalTime() > DateTime.UtcNow.AddMinutes(-2);
            r["worker heartbeat"] = (recent, beat is DateTime d2 ? $"last {d2.ToUniversalTime():u}" : "none");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            r["postgresql connection"] = (false, Zentriq.Application.Security.Redactor.Redact(ex.Message));
        }

        try
        {
            using var http = Https(paths, options);
            using var ready = await http.GetAsync("/health/ready", ct);
            r["api readiness (https)"] = (ready.IsSuccessStatusCode, $"{(int)ready.StatusCode} {http.BaseAddress}health/ready");
            var setup = await http.GetFromJsonAsync<JsonElement>("/api/v1/setup/status", ct);
            var mode = setup.TryGetProperty("licenseMode", out var m) ? m.GetString() : null;
            var exp = setup.TryGetProperty("trialExpiresAt", out var te) && te.ValueKind == JsonValueKind.String ? te.GetString() : null;
            r["license state"] = (mode is "Trial" or "Licensed" or "LicensedGrace", $"{mode}{(exp is null ? "" : $" (trial ends {exp})")}");
            using var ui = await http.GetAsync("/", ct);
            r["web console"] = (ui.IsSuccessStatusCode, $"{(int)ui.StatusCode} {http.BaseAddress}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            r["api readiness (https)"] = (false, ex.Message);
        }
        return r;
    }

    private static (bool, string) Svc(string name)
    {
        var s = WindowsHost.ServiceStatus(name);
        return (s == "Running", s);
    }

    private static HttpClient Https(ZentriqPaths paths, ZentriqOptions options)
    {
        string? pinned = options.Https.CertificateThumbprint;
        if (string.IsNullOrWhiteSpace(pinned) && File.Exists(options.Https.CertificatePath ?? paths.HttpsCertificateFile))
        {
            var app = new SecretStore(SecretProtectorFactory.Create()).Read<AppSecrets>(paths.AppSecretsFile);
            using var c = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(
                options.Https.CertificatePath ?? paths.HttpsCertificateFile, app.HttpsPfxPassword,
                System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.EphemeralKeySet);
            pinned = c.Thumbprint;
        }
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None ||
                (cert is not null && pinned is not null && string.Equals(cert.GetCertHashString(), pinned.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)),
        };
        return new HttpClient(handler) { BaseAddress = new Uri($"https://localhost:{options.Https.Port}/"), Timeout = TimeSpan.FromSeconds(20) };
    }
}
