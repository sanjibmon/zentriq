using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Zentriq.Infrastructure.Backup;
using Zentriq.Infrastructure.Configuration;
using Zentriq.Infrastructure.Data;
using Zentriq.Infrastructure.Secrets;

namespace Zentriq.Setup;

/// <summary>
/// Encrypted backups (.zbak) of the whole installation. Contents (inside the encryption): a zip with
///   db.dump                         pg_dump custom-format dump of the zentriq schema (all data)
///   config/zentriq.json             installation settings
///   secrets/app.json, owner.json    installation secrets (plaintext JSON, protected by the backup encryption)
///   secrets/https.pfx, https-ca.pfx console certificate and the local CA that issued it (agents pin the key)
///   secrets/agent-ca.pfx            CA of the agents' client certificates
///   dpkeys/*.xml                    console data protection keys (MFA secrets), without the machine-bound DPAPI layer
///   remote/data/…                   remote support engine data (devices, its agent identity)
///   files/exports/…, files/logs/…   report files (and logs when chosen)
///   manifest.json
/// File format: "ZBAK1\n" + one JSON header line (unencrypted metadata) + AES-256-GCM chunks (see BackupCrypto). Format 2:
/// a random data key encrypts the chunks; it is stored wrapped with the installation backup key (recovery key) and, when a
/// backup password is set, with a key derived from the password, so a rebuilt server needs only the file and the password.
/// </summary>
public sealed class BackupService(ZentriqPaths paths, ZentriqOptions options, Reporter log)
{
    public const string ApiService = "ZentriqApi";
    public const string WorkerService = "ZentriqWorker";
    public const string RemoteService = "ZentriqRemote";

    public static string Fingerprint(string base64Key) => BackupCrypto.Fingerprint(base64Key);

    public sealed record Outcome(string File, long Size, string Status, IReadOnlyList<DestinationResult> Destinations);

    private (OwnerSecrets Owner, AppSecrets App) Secrets()
    {
        var store = new SecretStore(SecretProtectorFactory.Create());
        return (store.Read<OwnerSecrets>(paths.OwnerSecretsFile), store.Read<AppSecrets>(paths.AppSecretsFile));
    }

    private string PgBin(string exe)
    {
        var dir = options.PostgresBinDirectory;
        var p = dir is null ? null : Path.Combine(dir, exe);
        if (p is null || !File.Exists(p))
            throw new SetupException($"{exe} was not found ({dir ?? "PostgresBinDirectory not configured"}).",
                "Install the PostgreSQL command-line tools on this server and set PostgresBinDirectory in Config\\zentriq.json.");
        return p;
    }

    private NpgsqlDataSource OwnerDataSource(OwnerSecrets owner) =>
        NpgsqlDataSource.Create(ConnectionStrings.Build(options.Database, options.Database.OwnerUser, owner.DbOwnerPassword, applicationName: "zentriqctl-backup"));

    private static string TriggerOf(string label) => label switch
    {
        "scheduled" => "schedule",
        "manual" => "manual",
        "pre-upgrade" => "pre-upgrade",
        "pre-restore" => "pre-restore",
        _ => "command",
    };

    /// <summary>Creates a backup in the Backups folder; returns its path.</summary>
    public async Task<string> CreateAsync(string label, CancellationToken ct, int keep = 0, string? requestedBy = null) =>
        (await CreateWithCopiesAsync(label, ct, keep, requestedBy)).File;

    /// <summary>
    /// Creates a backup, copies it to the enabled destinations (not for pre-upgrade / pre-restore safety backups) and applies
    /// retention. The run is recorded in the backup history when the database already has it.
    /// </summary>
    public async Task<Outcome> CreateWithCopiesAsync(string label, CancellationToken ct, int keep = 0, string? requestedBy = null)
    {
        var (owner, app) = Secrets();
        var trigger = TriggerOf(label);
        Directory.CreateDirectory(paths.Backups);
        await using var ds = OwnerDataSource(owner);
        var store = new BackupStore(ds, new CredentialProtector(() => app));

        // Settings and the history table exist from schema 27 on; a pre-upgrade backup may run on an older schema.
        BackupSettings settings = new();
        BackupPasswordKey? password = null;
        var hasHistory = true;
        try
        {
            settings = await store.LoadSettingsAsync(ct);
            password = store.PasswordKey(settings);
        }
        catch (Exception ex) when (ex is PostgresException or CryptographicException or FormatException)
        {
            log.Warn($"Backup settings could not be read ({ex.Message}); using the defaults.");
        }

        try { await store.SetKeyFingerprintAsync(Fingerprint(owner.BackupKey), ct); } catch (PostgresException) { }
        var name = BackupNames.Make(Environment.MachineName, DateTime.UtcNow, label);
        Guid? runId = null;
        try { runId = await store.StartRunAsync(trigger, label, name, requestedBy, password is not null, options.ProductVersion, ct); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable) { hasHistory = false; }

        var work = Path.Combine(paths.Backups, ".work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var outPath = Path.Combine(paths.Backups, name);
        try
        {
            var dump = Path.Combine(work, "db.dump");
            var r = await WindowsHost.RunAsync(PgBin("pg_dump.exe"),
                ["--format=custom", "--schema=zentriq", "--no-owner", "--no-privileges", "--file", dump,
                 "--host", options.Database.Host, "--port", options.Database.Port.ToString(), "--username", options.Database.OwnerUser, options.Database.Database],
                TimeSpan.FromHours(4), env: new Dictionary<string, string> { ["PGPASSWORD"] = owner.DbOwnerPassword }, ct: ct);
            if (r.ExitCode != 0) throw new SetupException($"pg_dump failed ({r.ExitCode}): {r.Output.Trim()}");

            string? installationId = null;
            var schema = 0;
            await using (var c = ds.CreateCommand("SELECT installation_id::text, (SELECT max(version) FROM zentriq.schema_migrations) FROM zentriq.install_state"))
            await using (var rd = await c.ExecuteReaderAsync(ct))
                if (await rd.ReadAsync(ct)) { installationId = rd.IsDBNull(0) ? null : rd.GetString(0); schema = rd.IsDBNull(1) ? 0 : Convert.ToInt32(rd.GetValue(1)); }

            var contents = new BackupContents { Schema = schema };
            var zipPath = Path.Combine(work, "payload.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(dump, "db.dump", CompressionLevel.Fastest);
                contents.DatabaseBytes = new FileInfo(dump).Length;
                if (File.Exists(paths.ConfigFile)) zip.CreateEntryFromFile(paths.ConfigFile, "config/zentriq.json");
                foreach (var (file, entry) in new[] { (paths.AgentCaFile, "secrets/agent-ca.pfx"), (paths.HttpsCertificateFile, "secrets/https.pfx"), (paths.HttpsCaFile, "secrets/https-ca.pfx") })
                    if (File.Exists(file)) { zip.CreateEntryFromFile(file, entry); contents.Certificates.Add(Path.GetFileName(file)); }
                WriteEntry(zip, "secrets/app.json", JsonSerializer.Serialize(app));
                WriteEntry(zip, "secrets/owner.json", JsonSerializer.Serialize(owner));
                try
                {
                    var (keys, portable, kept) = DataProtectionKeyBackup.Export(paths.DataProtectionKeys);
                    foreach (var k in keys) WriteEntry(zip, "dpkeys/" + k.Name, k.Xml);
                    contents.DataProtectionKeys = keys.Count;
                    if (kept > 0) log.Warn($"{kept} data protection key secret(s) could not be made portable; MFA may need to be set up again after a restore on another server.");
                    if (portable > 0) log.Info($"Data protection keys: {keys.Count} file(s), {portable} secret(s) made portable.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
                {
                    log.Warn("Data protection keys were not included: " + ex.Message);
                }
                contents.RemoteSupportFiles = AddFolder(zip, paths.RemoteData, "remote/data/", f => !IsRemoteLocalFile(f));
                if (settings.IncludeExports) contents.ReportFiles = AddFolder(zip, paths.Exports, "files/exports/", _ => true);
                if (settings.IncludeLogs)
                {
                    var since = DateTime.UtcNow.AddDays(-30);
                    contents.LogFiles = AddFolder(zip, paths.Logs, "files/logs/", f => File.GetLastWriteTimeUtc(f) >= since);
                }
                WriteEntry(zip, "manifest.json", JsonSerializer.Serialize(new
                {
                    options.ProductVersion, createdAt = DateTimeOffset.UtcNow, label, schema = "zentriq", schemaVersion = schema,
                    installationId, machine = Environment.MachineName, contents,
                }));
            }

            var (header, dataKey) = BackupCrypto.NewHeader(options.ProductVersion, label, owner.BackupKey, password, Environment.MachineName, installationId, contents.Describe());
            await using (var input = File.OpenRead(zipPath))
            await using (var output = File.Create(outPath + ".partial"))
                await BackupCrypto.EncryptAsync(input, output, dataKey, header, ct);
            CryptographicOperations.ZeroMemory(dataKey);
            File.Move(outPath + ".partial", outPath, overwrite: true);
            var size = new FileInfo(outPath).Length;
            string sha;
            await using (var f = File.OpenRead(outPath)) sha = Convert.ToHexString(await SHA256.HashDataAsync(f, ct)).ToLowerInvariant();
            log.Info($"Backup written: {outPath} ({size / 1024} KiB, {contents.Describe()}; key {header.KeyFingerprint}{(password is null ? "" : ", password protected")}).");
            try { Directory.Delete(work, true); } catch (IOException) { }

            // Copies to the destinations, then retention there and here.
            var results = new List<DestinationResult>();
            if (hasHistory && trigger is "schedule" or "manual" or "command")
            {
                List<DestinationRow> destinations = [];
                try { destinations = await store.DestinationsAsync(true, ct); }
                catch (Exception ex) when (ex is PostgresException or CryptographicException) { log.Warn("Backup destinations could not be read: " + ex.Message); }
                foreach (var row in destinations.Where(d => d.Destination.Enabled))
                    results.Add(await CopyAsync(store, row.Destination, outPath, name, ct));
            }
            var localKeep = trigger is "schedule" or "manual" ? settings.KeepLocal : keep;
            var removed = PruneLocal(localKeep);
            if (hasHistory) { try { await store.MarkLocalDeletedAsync(removed, ct); } catch (PostgresException) { } }

            var status = results.Any(x => x.Status == "failed") ? "partial" : "succeeded";
            if (status == "partial")
                await NotifyAsync(ds, "warn", "Backup copy failed", $"{name} was created on the server, but copying it failed: " +
                    string.Join("; ", results.Where(x => x.Status == "failed").Select(x => $"{x.Name}: {x.Message}")));
            if (runId is { } id) await store.FinishRunAsync(id, status, size, sha, results, contents, status == "partial" ? string.Join("; ", results.Where(x => x.Status == "failed").Select(x => $"{x.Name}: {x.Message}")) : null, ct);
            await RecordAsync(owner, "backup", "succeeded", new { file = name, label, bytes = size, copies = results.Count(x => x.Status == "ok"), failedCopies = results.Count(x => x.Status == "failed") });
            return new Outcome(outPath, size, status, results);
        }
        catch (Exception ex)
        {
            try { File.Delete(outPath + ".partial"); } catch (IOException) { }
            if (runId is { } id)
            {
                try { await store.FinishRunAsync(id, "failed", null, null, [], null, ex is SetupException se ? se.Message : ex.Message, CancellationToken.None); }
                catch (Exception inner) when (inner is NpgsqlException or PostgresException) { log.Warn("Could not record the failed backup: " + inner.Message); }
            }
            await RecordAsync(owner, "backup", "failed", new { file = name, label, error = ex.Message });
            if (hasHistory) await NotifyAsync(ds, "crit", "Backup failed", $"The {label} backup failed: {(ex is SetupException se2 ? se2.Message : ex.Message)}");
            throw;
        }
        finally { try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (IOException) { } }
    }

    private async Task<DestinationResult> CopyAsync(BackupStore store, BackupDestination d, string file, string name, CancellationToken ct)
    {
        try
        {
            using var target = BackupTargets.Create(d);
            await BackupTargets.RetryAsync(async () => { await target.UploadAsync(file, name, ct); return true; }, ct);
            if (d.Kind == BackupKinds.Folder && d.Settings.Path is { } folder)
            {
                // The console lists this folder's backups (restore); it runs as its own service account.
                try { WindowsHost.GrantRead(folder, WindowsHost.ServiceSid(ApiService)); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or System.Security.Principal.IdentityNotMappedException) { log.Warn($"{d.Name}: could not let the console read {folder}: {ex.Message}"); }
            }
            var deleted = 0;
            if (d.Keep > 0)
            {
                try
                {
                    var existing = await target.ListAsync(ct);
                    foreach (var old in BackupNames.ToDelete(existing.Select(x => x.Name), Environment.MachineName, d.Keep))
                    {
                        try { await target.DeleteAsync(old, ct); deleted++; }
                        catch (Exception ex) when (ex is not OperationCanceledException) { log.Warn($"{d.Name}: could not delete old backup {old}: {ex.Message}"); }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.Warn($"{d.Name}: retention skipped: {ex.Message}"); }
            }
            var msg = $"Copied to {target.Where}" + (deleted > 0 ? $"; {deleted} older backup(s) removed" : "");
            log.Info($"{d.Name}: {msg}.");
            await store.SetStatusAsync(d.Id, true, msg, target.SeenHostKey, ct);
            return new DestinationResult(d.Id, d.Name, d.Kind, "ok", msg, deleted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var msg = ex is BackupTargetException or BackupHttpException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            log.Error($"{d.Name}: copy failed: {msg}");
            try { await store.SetStatusAsync(d.Id, false, msg, null, ct); } catch (Exception inner) when (inner is NpgsqlException or PostgresException) { }
            return new DestinationResult(d.Id, d.Name, d.Kind, "failed", msg);
        }
    }

    private List<string> PruneLocal(int keep)
    {
        var names = new DirectoryInfo(paths.Backups).GetFiles("zentriq-*.zbak").Select(f => f.Name).ToList();
        var delete = BackupNames.ToDelete(names, Environment.MachineName, keep);
        foreach (var n in delete)
        {
            try { File.Delete(Path.Combine(paths.Backups, n)); log.Info($"Removed old backup {n}."); }
            catch (IOException ex) { log.Warn($"Could not remove old backup {n}: {ex.Message}"); }
        }
        return delete;
    }

    /// <summary>The engine's own settings file and host name stay those of this server.</summary>
    private static bool IsRemoteLocalFile(string path)
    {
        var n = Path.GetFileName(path);
        return n.Equals("config.json", StringComparison.OrdinalIgnoreCase) || n.Equals(Zentriq.Infrastructure.Remote.RemoteServerConfig.LanHostFile, StringComparison.OrdinalIgnoreCase);
    }

    private int AddFolder(ZipArchive zip, string dir, string prefix, Func<string, bool> include)
    {
        if (!Directory.Exists(dir)) return 0;
        var n = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            if (!include(file)) continue;
            var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
            try
            {
                using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var dst = zip.CreateEntry(prefix + rel, CompressionLevel.Fastest).Open();
                src.CopyTo(dst);
                n++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.Warn($"Not included (in use or not readable): {file}: {ex.Message}"); }
        }
        return n;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Verify and restore

    /// <summary>The installation keys this server can try: its own backup key and an optional recovery key file.</summary>
    private BackupUnlock WithOwnKey(BackupUnlock unlock, string? recoveryKeyFile)
    {
        var keys = new List<string>(unlock.InstallKeys ?? []);
        if (recoveryKeyFile is not null) keys.Add(ReadRecoveryKey(recoveryKeyFile));
        try { keys.Add(Secrets().Owner.BackupKey); } catch (Exception ex) when (ex is IOException or InvalidDataException or CryptographicException or UnauthorizedAccessException) { }
        return unlock with { InstallKeys = keys };
    }

    /// <summary>
    /// Restore validation workflow: decrypt, check the archive, list the dump with pg_restore and,
    /// when a superuser is available, restore into a temporary database and count key tables.
    /// </summary>
    public async Task<bool> VerifyAsync(string file, string? recoveryKeyFile, bool deep, CancellationToken ct, string? password = null)
    {
        var work = Path.Combine(paths.Backups, ".verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var (zipPath, header) = await DecryptToFileAsync(file, WithOwnKey(new BackupUnlock(Password: password), recoveryKeyFile), work, ct);
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var required in new[] { "db.dump", "secrets/app.json", "secrets/owner.json", "manifest.json" })
                if (zip.GetEntry(required) is null) throw new SetupException($"Backup is missing {required}.");
            var dump = Path.Combine(work, "db.dump");
            zip.GetEntry("db.dump")!.ExtractToFile(dump);
            var list = await WindowsHost.RunAsync(PgBin("pg_restore.exe"), ["--list", dump], TimeSpan.FromMinutes(10), ct: ct);
            if (list.ExitCode != 0) throw new SetupException("pg_restore could not read the dump: " + list.Output.Trim());
            log.Info($"Backup {Path.GetFileName(file)} decrypted and readable: version {header.ProductVersion}, created {header.CreatedAt:u}{(header.HasPassword ? ", password protected" : "")}, {zip.Entries.Count} item(s) ({header.Contents ?? "database"}), {list.Output.Split('\n').Length} TOC lines.");
            if (!deep) return true;

            var owner = Secrets().Owner;
            if (string.IsNullOrEmpty(owner.PgSuperPassword))
            {
                log.Warn("Deep validation needs the bundled PostgreSQL superuser; skipped for an external server.");
                return true;
            }
            var pg = new PostgresInput { Host = options.Database.Host, Port = options.Database.Port, SuperUser = owner.PgSuperUser ?? "postgres" };
            var tempDb = "zentriq_restore_check";
            await using (var su = new NpgsqlConnection(PostgresProvisioner.SuperConnection(pg, owner.PgSuperPassword)))
            {
                await su.OpenAsync(ct);
                await using (var d = new NpgsqlCommand($"DROP DATABASE IF EXISTS {tempDb} WITH (FORCE)", su)) await d.ExecuteNonQueryAsync(ct);
                await using (var c = new NpgsqlCommand($"CREATE DATABASE {tempDb} TEMPLATE template0", su)) await c.ExecuteNonQueryAsync(ct);
            }
            try
            {
                await using (var ext = new NpgsqlConnection(PostgresProvisioner.SuperConnection(pg, owner.PgSuperPassword, tempDb)))
                {
                    await ext.OpenAsync(ct);
                    await using var e = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS pgcrypto WITH SCHEMA public", ext);
                    await e.ExecuteNonQueryAsync(ct);
                }
                var rr = await WindowsHost.RunAsync(PgBin("pg_restore.exe"),
                    ["--no-owner", "--no-privileges", "--exit-on-error", "--host", pg.Host, "--port", pg.Port.ToString(), "--username", pg.SuperUser, "--dbname", tempDb, dump],
                    TimeSpan.FromHours(2), env: new Dictionary<string, string> { ["PGPASSWORD"] = owner.PgSuperPassword }, ct: ct);
                if (rr.ExitCode != 0) throw new SetupException("Test restore failed: " + rr.Output.Trim());
                await using var chk = new NpgsqlConnection(PostgresProvisioner.SuperConnection(pg, owner.PgSuperPassword, tempDb));
                await chk.OpenAsync(ct);
                foreach (var t in new[] { "schema_migrations", "install_state", "users", "assets", "audit_events", "trial" })
                {
                    await using var q = new NpgsqlCommand($"SELECT count(*) FROM zentriq.{t}", chk);
                    log.Info($"  restored zentriq.{t}: {await q.ExecuteScalarAsync(ct)} row(s)");
                }
                log.Info("Deep restore validation PASSED.");
                return true;
            }
            finally
            {
                await using var su = new NpgsqlConnection(PostgresProvisioner.SuperConnection(pg, owner.PgSuperPassword));
                await su.OpenAsync(CancellationToken.None);
                await using var d = new NpgsqlCommand($"DROP DATABASE IF EXISTS {tempDb} WITH (FORCE)", su);
                await d.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    /// <summary>
    /// Restores a backup onto this installation (the same server, or a rebuilt one after a fresh installation). Services are
    /// stopped, a safety backup of the current data is taken, the database is replaced in one transaction, and the
    /// installation's identity is brought back: install secret (trial), credential key (stored credentials), backup key,
    /// agent CA, console certificate and its local CA (agents pin the key), data protection keys (MFA), remote support
    /// engine data and report files. This server's own database passwords, ports and installation choices are kept.
    /// Migrations run for backups of older versions; backups of newer versions are refused.
    /// </summary>
    public async Task RestoreAsync(string file, string? recoveryKeyFile, string apiService, string workerService, CancellationToken ct,
        string? password = null, byte[]? dataKey = null, string? requestedBy = null, IReadOnlyList<string>? recoveryKeys = null)
    {
        var store = new SecretStore(SecretProtectorFactory.Create());
        var currentOwner = store.Read<OwnerSecrets>(paths.OwnerSecretsFile);
        var currentApp = store.Read<AppSecrets>(paths.AppSecretsFile);
        var work = Path.Combine(paths.Backups, ".restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var stopped = new List<string>();
        try
        {
            var (zipPath, header) = await DecryptToFileAsync(file, WithOwnKey(new BackupUnlock(recoveryKeys, password, dataKey), recoveryKeyFile), work, ct);
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var required in new[] { "db.dump", "secrets/app.json", "secrets/owner.json" })
                if (zip.GetEntry(required) is null) throw new SetupException($"The backup is incomplete: {required} is missing.");
            var manifest = zip.GetEntry("manifest.json") is not null ? JsonDocument.Parse(ReadEntry(zip, "manifest.json")).RootElement.Clone() : default;
            var backupSchema = manifest.ValueKind == JsonValueKind.Object && manifest.TryGetProperty("schemaVersion", out var sv) && sv.TryGetInt32(out var svi) ? svi : 0;
            var supported = Zentriq.Infrastructure.Database.SqlMigrator.LatestVersion;
            if (backupSchema > supported || (backupSchema == 0 && Version.TryParse(header.ProductVersion, out var bv) && Version.TryParse(options.ProductVersion, out var cv) && bv > cv))
                throw new SetupException($"This backup was made with a newer Zentriq ({header.ProductVersion}, schema {backupSchema}) than this server ({options.ProductVersion}, schema {supported}).",
                    "Install the same or a newer Zentriq version on this server first, then restore.");

            var dump = Path.Combine(work, "db.dump");
            zip.GetEntry("db.dump")!.ExtractToFile(dump);
            var restoredApp = JsonSerializer.Deserialize<AppSecrets>(ReadEntry(zip, "secrets/app.json"))!;
            var restoredOwner = JsonSerializer.Deserialize<OwnerSecrets>(ReadEntry(zip, "secrets/owner.json"))!;

            log.Info("Taking a safety backup of the current data before restoring.");
            try { await CreateAsync("pre-restore", ct); } catch (Exception ex) when (ex is not OperationCanceledException) { log.Warn($"Safety backup failed: {ex.Message}"); }

            log.Info("Stopping Zentriq services.");
            foreach (var svc in new[] { apiService, workerService, RemoteService })
            {
                if (!WindowsHost.ServiceExists(svc)) continue;
                // Remote support is started again only when it was running (it is disabled on Standard installations).
                if (svc == RemoteService && WindowsHost.ServiceStatus(svc) != "Running") continue;
                WindowsHost.StopAndWait(svc, TimeSpan.FromMinutes(2));
                stopped.Add(svc);
            }

            var rr = await WindowsHost.RunAsync(PgBin("pg_restore.exe"),
                ["--clean", "--if-exists", "--no-owner", "--no-privileges", "--exit-on-error", "--single-transaction",
                 "--host", options.Database.Host, "--port", options.Database.Port.ToString(), "--username", options.Database.OwnerUser,
                 "--dbname", options.Database.Database, dump],
                TimeSpan.FromHours(4), env: new Dictionary<string, string> { ["PGPASSWORD"] = currentOwner.DbOwnerPassword }, ct: ct);
            if (rr.ExitCode != 0) throw new SetupException("pg_restore failed; the database was left unchanged (single transaction): " + rr.Output.Trim());
            log.Info($"Database restored from the backup of {header.CreatedAt:u} ({header.Machine}, version {header.ProductVersion}).");

            // Keep this server's database passwords; restore the secrets the data depends on.
            var api = WindowsHost.ServiceSid(apiService);
            currentApp.InstallSecret = restoredApp.InstallSecret;
            if (!string.IsNullOrEmpty(restoredApp.CredentialKey)) currentApp.CredentialKey = restoredApp.CredentialKey;
            if (!string.IsNullOrEmpty(restoredApp.AgentCaPfxPassword) && zip.GetEntry("secrets/agent-ca.pfx") is { } caEntry)
            {
                caEntry.ExtractToFile(paths.AgentCaFile, overwrite: true);
                currentApp.AgentCaPfxPassword = restoredApp.AgentCaPfxPassword;
                WindowsHost.SecureFile(paths.AgentCaFile, (api, FileSystemRights.Read));
            }
            var certificates = false;
            if (!string.IsNullOrEmpty(restoredApp.HttpsPfxPassword) && !string.IsNullOrEmpty(restoredApp.HttpsCaPfxPassword)
                && zip.GetEntry("secrets/https.pfx") is { } httpsEntry && zip.GetEntry("secrets/https-ca.pfx") is { } httpsCaEntry
                && string.IsNullOrWhiteSpace(options.Https.CertificateThumbprint))
            {
                httpsEntry.ExtractToFile(paths.HttpsCertificateFile, overwrite: true);
                httpsCaEntry.ExtractToFile(paths.HttpsCaFile, overwrite: true);
                WindowsHost.SecureFile(paths.HttpsCertificateFile, (api, FileSystemRights.Read));
                WindowsHost.SecureFile(paths.HttpsCaFile, (api, FileSystemRights.Read));
                currentApp.HttpsPfxPassword = restoredApp.HttpsPfxPassword;
                currentApp.HttpsCaPfxPassword = restoredApp.HttpsCaPfxPassword;
                certificates = true;
            }
            currentOwner.BackupKey = restoredOwner.BackupKey;
            store.Write(paths.AppSecretsFile, currentApp);
            store.Write(paths.OwnerSecretsFile, currentOwner);
            var recovery = Path.Combine(paths.Secrets, "backup-recovery-key.txt");
            if (File.Exists(recovery))
                File.WriteAllText(recovery,
                    "Zentriq backup recovery key\r\n" +
                    "Store this file OFFLINE (password manager or safe) and delete it from this server.\r\n" +
                    "It is required to restore Zentriq backups on another server.\r\n\r\n" +
                    $"Installation: restored from {header.Machine}\r\nRestored: {DateTimeOffset.UtcNow:O}\r\n" +
                    $"Key fingerprint: {Fingerprint(restoredOwner.BackupKey)}\r\nKey: {restoredOwner.BackupKey}\r\n");

            if (certificates)
            {
                // Same key and CA as before (agents pin the key); re-issued for this server's names when they differ.
                try
                {
                    var info = new CertificateManager(log).EnsureSelfSigned(paths, options.Https.HostName, currentApp.HttpsPfxPassword!, currentApp.HttpsCaPfxPassword!, forceNew: false);
                    log.Info($"Console certificate restored ({info.Thumbprint}, same key as the backed-up server).");
                }
                catch (Exception ex) when (ex is CryptographicException or IOException or SetupException) { log.Warn("The restored console certificate could not be checked: " + ex.Message); }
            }

            // Data protection keys (MFA secrets).
            var keyFiles = zip.Entries.Where(e => e.FullName.StartsWith("dpkeys/", StringComparison.Ordinal) && e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .Select(e => new DataProtectionKeyBackup.KeyFile(e.Name, ReadEntry(zip, e.FullName))).ToList();
            if (keyFiles.Count > 0)
            {
                try { log.Info($"Data protection keys: {DataProtectionKeyBackup.Import(paths.DataProtectionKeys, keyFiles)} added (MFA keeps working)."); }
                catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException or System.Xml.XmlException or FormatException)
                {
                    log.Warn("Data protection keys could not be restored; users with MFA may need to set it up again: " + ex.Message);
                }
            }

            // Remote support engine data.
            var remoteEntries = zip.Entries.Where(e => e.FullName.StartsWith("remote/data/", StringComparison.Ordinal) && e.Name.Length > 0).ToList();
            if (remoteEntries.Count > 0)
            {
                if (!options.Remote.Enabled) log.Warn("The backup has remote support data, but remote support is not installed on this server; it was not restored. Install remote support (Setup › Modify) and restore again to bring it back.");
                else
                {
                    var n = Extract(remoteEntries, "remote/data/", paths.RemoteData, IsRemoteLocalFile);
                    log.Info($"Remote support data restored ({n} file(s)).");
                }
            }
            var exports = zip.Entries.Where(e => e.FullName.StartsWith("files/exports/", StringComparison.Ordinal) && e.Name.Length > 0).ToList();
            if (exports.Count > 0) log.Info($"Report files restored ({Extract(exports, "files/exports/", paths.Exports, _ => false)}).");

            var cs = ConnectionStrings.Build(options.Database, options.Database.OwnerUser, currentOwner.DbOwnerPassword, applicationName: "zentriqctl");
            var migrator = new Zentriq.Infrastructure.Database.SqlMigrator(Microsoft.Extensions.Logging.Abstractions.NullLogger<Zentriq.Infrastructure.Database.SqlMigrator>.Instance);
            var applied = await migrator.MigrateAsync(cs, PostgresProvisioner.AppRole, options.ProductVersion, ct);
            log.Info($"Restored backup from {header.CreatedAt:u} (version {header.ProductVersion}); applied {applied} migration(s).");
            await AfterRestoreAsync(currentOwner, Path.GetFileName(file), header, requestedBy, ct);
            await RecordAsync(currentOwner, "restore", "succeeded", new { file = Path.GetFileName(file), from = header.ProductVersion, machine = header.Machine, created = header.CreatedAt });
        }
        finally
        {
            foreach (var svc in new[] { workerService, apiService, RemoteService }.Where(stopped.Contains))
            {
                try { WindowsHost.StartAndWait(svc, TimeSpan.FromMinutes(3)); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException) { log.Warn($"Could not start {svc}: {ex.Message}"); }
            }
            if (stopped.Count > 0) log.Info("Services restarted. Run 'zentriqctl verify' to confirm health.");
            try { Directory.Delete(work, true); } catch { }
        }
    }

    /// <summary>Tidies the restored database: work that was in progress when the backup was taken is closed, and the restore is noted.</summary>
    private async Task AfterRestoreAsync(OwnerSecrets owner, string file, Zentriq.Infrastructure.Backup.BackupHeader header, string? requestedBy, CancellationToken ct)
    {
        try
        {
            await using var ds = OwnerDataSource(owner);
            await using var cmd = ds.CreateCommand("""
                -- The run that made this backup was still "running" when the database was dumped.
                UPDATE zentriq.backup_runs SET status = CASE WHEN file_name = @f OR started_at <= @created THEN 'succeeded' ELSE 'interrupted' END,
                       finished_at = COALESCE(finished_at, @created)
                 WHERE status = 'running';
                UPDATE zentriq.jobs SET status = 'cancelled', finished_at = now(), lease_owner = NULL, lease_expires_at = NULL, last_error = 'Cancelled by a restore from backup.'
                 WHERE job_type LIKE 'maintenance.%' AND status IN ('queued', 'running');
                INSERT INTO zentriq.system_settings (key, value, updated_at) VALUES ('backup.last_restore', @v::jsonb, now())
                ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now();
                INSERT INTO zentriq.system_settings (key, value, updated_at) VALUES ('backup.key_fingerprint', to_jsonb(@fp::text), now())
                ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now();
                INSERT INTO zentriq.notifications (user_id, kind, title, body, link)
                VALUES (NULL, 'ok', 'Restored from backup', @note, '/backup');
                """);
            cmd.Parameters.AddWithValue("note", $"Zentriq was restored from the backup of {header.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} ({header.Machine}, version {header.ProductVersion}).");
            cmd.Parameters.AddWithValue("fp", Fingerprint(owner.BackupKey));
            cmd.Parameters.AddWithValue("f", file);
            cmd.Parameters.AddWithValue("created", header.CreatedAt);
            cmd.Parameters.AddWithValue("v", JsonSerializer.Serialize(new
            {
                at = DateTimeOffset.UtcNow, file, backupCreatedAt = header.CreatedAt, fromVersion = header.ProductVersion, fromMachine = header.Machine,
                toMachine = Environment.MachineName, by = requestedBy,
            }));
            await cmd.ExecuteNonQueryAsync(ct);
            await new Zentriq.Infrastructure.Audit.AuditWriter(ds).WriteAsync(new Zentriq.Infrastructure.Audit.AuditActor(null, requestedBy ?? $"zentriqctl ({Environment.UserName})", null),
                "backup.restore", file, "Success", new { backupCreatedAt = header.CreatedAt, fromVersion = header.ProductVersion, fromMachine = header.Machine }, ct: ct);
        }
        catch (Exception ex) when (ex is NpgsqlException or PostgresException) { log.Warn("Could not note the restore in the database: " + ex.Message); }
    }

    /// <summary>A console notification for administrators (bell menu), linking to Backup &amp; Restore.</summary>
    private async Task NotifyAsync(NpgsqlDataSource ds, string kind, string title, string body)
    {
        try
        {
            await using var cmd = ds.CreateCommand("INSERT INTO zentriq.notifications (user_id, kind, title, body, link) VALUES (NULL, @k, @t, @b, '/backup')");
            cmd.Parameters.AddWithValue("k", kind);
            cmd.Parameters.AddWithValue("t", title);
            cmd.Parameters.AddWithValue("b", body.Length > 1000 ? body[..1000] : body);
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is NpgsqlException or PostgresException) { log.Warn("Could not add a console notification: " + ex.Message); }
    }

    private int Extract(IEnumerable<ZipArchiveEntry> entries, string prefix, string dir, Func<string, bool> skip)
    {
        var root = Path.GetFullPath(dir);
        var n = 0;
        foreach (var e in entries)
        {
            var rel = e.FullName[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
            var dest = Path.GetFullPath(Path.Combine(root, rel));
            if (!dest.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || skip(dest)) continue;   // no paths outside the folder
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                e.ExtractToFile(dest, overwrite: true);
                n++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.Warn($"Could not restore {dest}: {ex.Message}"); }
        }
        return n;
    }

    private async Task<(string ZipPath, Zentriq.Infrastructure.Backup.BackupHeader Header)> DecryptToFileAsync(string file, BackupUnlock unlock, string work, CancellationToken ct)
    {
        var zipPath = Path.Combine(work, "payload.zip");
        await using var input = File.OpenRead(file);
        await using var output = File.Create(zipPath);
        try
        {
            var header = await BackupCrypto.DecryptAsync(input, output, h => BackupCrypto.Unlock(h, unlock), ct);
            return (zipPath, header);
        }
        catch (BackupKeyException ex) { throw new SetupException(ex.Message, "Use --password (the backup password) or --recovery-key <file> with the recovery key of the installation that made the backup."); }
        catch (InvalidDataException ex) { throw new SetupException(ex.Message, "Check that the file is a complete Zentriq backup and that the right password or recovery key is used."); }
    }

    public static string ReadRecoveryKey(string file)
    {
        var line = File.ReadAllLines(file).FirstOrDefault(l => l.StartsWith("Key:", StringComparison.Ordinal))
                   ?? throw new SetupException("The recovery key file has no 'Key:' line.");
        return line["Key:".Length..].Trim();
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Fastest).Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    private static string ReadEntry(ZipArchive zip, string name)
    {
        using var r = new StreamReader(zip.GetEntry(name)?.Open() ?? throw new SetupException($"Backup is missing {name}."));
        return r.ReadToEnd();
    }

    private async Task RecordAsync(OwnerSecrets owner, string action, string outcome, object detail)
    {
        try
        {
            await using var conn = new NpgsqlConnection(ConnectionStrings.Build(options.Database, options.Database.OwnerUser, owner.DbOwnerPassword, applicationName: "zentriqctl"));
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("INSERT INTO zentriq.installation_history (action, to_version, outcome, detail) VALUES (@a, @v, @o, @d::jsonb)", conn);
            cmd.Parameters.AddWithValue("a", action);
            cmd.Parameters.AddWithValue("v", options.ProductVersion);
            cmd.Parameters.AddWithValue("o", outcome);
            cmd.Parameters.AddWithValue("d", JsonSerializer.Serialize(detail));
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex) { log.Warn($"Could not record {action} in installation history: {ex.Message}"); }
    }
}
