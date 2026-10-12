using System.Buffers.Binary;
using System.IO.Compression;
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
/// Encrypted backups (.zbak). Contents (inside the encryption): a zip with
///   db.dump        pg_dump custom-format dump of the zentriq schema
///   config/zentriq.json
///   secrets/app.json, secrets/owner.json   (plaintext JSON, protected by the backup encryption)
///   manifest.json
/// File format: "ZBAK1\n" + one JSON header line (unencrypted metadata) + AES-256-GCM chunks
/// (1 MiB plaintext each: [u32 length][16-byte tag][ciphertext]; nonce = 4-byte random prefix
/// || 8-byte chunk counter; the header line is bound as associated data of every chunk; the last
/// chunk has the high bit of the length set). The key is the installation backup key; its
/// fingerprint is in the header. A copy of the key (the recovery key) is kept offline by the customer.
/// </summary>
public sealed class BackupService(ZentriqPaths paths, ZentriqOptions options, Reporter log)
{
    public static string Fingerprint(string base64Key) => BackupCrypto.Fingerprint(base64Key);

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

    public async Task<string> CreateAsync(string label, CancellationToken ct, int keep = 0)
    {
        var (owner, app) = Secrets();
        Directory.CreateDirectory(paths.Backups);
        var work = Path.Combine(paths.Backups, ".work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var dump = Path.Combine(work, "db.dump");
            var r = await WindowsHost.RunAsync(PgBin("pg_dump.exe"),
                ["--format=custom", "--schema=zentriq", "--no-owner", "--no-privileges", "--file", dump,
                 "--host", options.Database.Host, "--port", options.Database.Port.ToString(), "--username", options.Database.OwnerUser, options.Database.Database],
                TimeSpan.FromHours(4), env: new Dictionary<string, string> { ["PGPASSWORD"] = owner.DbOwnerPassword }, ct: ct);
            if (r.ExitCode != 0) throw new SetupException($"pg_dump failed ({r.ExitCode}): {r.Output.Trim()}");

            var zipPath = Path.Combine(work, "payload.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(dump, "db.dump", CompressionLevel.Fastest);
                if (File.Exists(paths.ConfigFile)) zip.CreateEntryFromFile(paths.ConfigFile, "config/zentriq.json");
                if (File.Exists(paths.AgentCaFile)) zip.CreateEntryFromFile(paths.AgentCaFile, "secrets/agent-ca.pfx");
                WriteEntry(zip, "secrets/app.json", JsonSerializer.Serialize(app));
                WriteEntry(zip, "secrets/owner.json", JsonSerializer.Serialize(owner));
                WriteEntry(zip, "manifest.json", JsonSerializer.Serialize(new { options.ProductVersion, createdAt = DateTimeOffset.UtcNow, label, schema = "zentriq" }));
            }

            var name = $"zentriq-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Sanitize(label)}.zbak";
            var outPath = Path.Combine(paths.Backups, name);
            var header = new BackupHeader(1, options.ProductVersion, DateTimeOffset.UtcNow, label, Fingerprint(owner.BackupKey),
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(4)), Environment.MachineName);
            await using (var input = File.OpenRead(zipPath))
            await using (var output = File.Create(outPath))
                await BackupCrypto.EncryptAsync(input, output, Convert.FromBase64String(owner.BackupKey), header, ct);
            log.Info($"Backup written: {outPath} ({new FileInfo(outPath).Length / 1024} KiB, key {header.KeyFingerprint}).");
            await RecordAsync(owner, "backup", "succeeded", new { file = name, label });
            if (keep > 0) Prune(keep);
            return outPath;
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    private void Prune(int keep)
    {
        foreach (var f in new DirectoryInfo(paths.Backups).GetFiles("zentriq-*-nightly.zbak").OrderByDescending(f => f.CreationTimeUtc).Skip(keep))
        {
            log.Info($"Removing old nightly backup {f.Name}");
            f.Delete();
        }
    }

    /// <summary>
    /// Restore validation workflow: decrypt, check the archive, list the dump with pg_restore and,
    /// when a superuser is available, restore into a temporary database and count key tables.
    /// </summary>
    public async Task<bool> VerifyAsync(string file, string? recoveryKeyFile, bool deep, CancellationToken ct)
    {
        var work = Path.Combine(paths.Backups, ".verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var (zipPath, header) = await DecryptToFileAsync(file, recoveryKeyFile, work, ct);
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var required in new[] { "db.dump", "secrets/app.json", "secrets/owner.json", "manifest.json" })
                if (zip.GetEntry(required) is null) throw new SetupException($"Backup is missing {required}.");
            var dump = Path.Combine(work, "db.dump");
            zip.GetEntry("db.dump")!.ExtractToFile(dump);
            var list = await WindowsHost.RunAsync(PgBin("pg_restore.exe"), ["--list", dump], TimeSpan.FromMinutes(10), ct: ct);
            if (list.ExitCode != 0) throw new SetupException("pg_restore could not read the dump: " + list.Output.Trim());
            log.Info($"Backup {Path.GetFileName(file)} decrypted and readable: version {header.ProductVersion}, created {header.CreatedAt:u}, {list.Output.Split('\n').Length} TOC lines.");
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
    /// Restores a backup onto this installation (same or replacement server). Services are stopped,
    /// a safety backup of the current database is taken, the schema is replaced, the installation
    /// secrets that the data depends on (install secret, backup key) are restored, migrations and
    /// grants are re-applied and services are restarted.
    /// </summary>
    public async Task RestoreAsync(string file, string? recoveryKeyFile, string apiService, string workerService, CancellationToken ct)
    {
        var store = new SecretStore(SecretProtectorFactory.Create());
        var currentOwner = store.Read<OwnerSecrets>(paths.OwnerSecretsFile);
        var currentApp = store.Read<AppSecrets>(paths.AppSecretsFile);
        var work = Path.Combine(paths.Backups, ".restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var (zipPath, header) = await DecryptToFileAsync(file, recoveryKeyFile, work, ct);
            using var zip = ZipFile.OpenRead(zipPath);
            var dump = Path.Combine(work, "db.dump");
            zip.GetEntry("db.dump")!.ExtractToFile(dump);
            var restoredApp = JsonSerializer.Deserialize<AppSecrets>(ReadEntry(zip, "secrets/app.json"))!;
            var restoredOwner = JsonSerializer.Deserialize<OwnerSecrets>(ReadEntry(zip, "secrets/owner.json"))!;

            log.Info("Taking a safety backup of the current database before restoring.");
            try { await CreateAsync("pre-restore", ct); } catch (Exception ex) { log.Warn($"Safety backup failed: {ex.Message}"); }

            log.Info("Stopping Zentriq services.");
            WindowsHost.StopAndWait(apiService, TimeSpan.FromMinutes(1));
            WindowsHost.StopAndWait(workerService, TimeSpan.FromMinutes(1));

            var rr = await WindowsHost.RunAsync(PgBin("pg_restore.exe"),
                ["--clean", "--if-exists", "--no-owner", "--no-privileges", "--exit-on-error", "--single-transaction",
                 "--host", options.Database.Host, "--port", options.Database.Port.ToString(), "--username", options.Database.OwnerUser,
                 "--dbname", options.Database.Database, dump],
                TimeSpan.FromHours(4), env: new Dictionary<string, string> { ["PGPASSWORD"] = currentOwner.DbOwnerPassword }, ct: ct);
            if (rr.ExitCode != 0) throw new SetupException("pg_restore failed; the database was left unchanged (single transaction): " + rr.Output.Trim());

            // Keep this server's database passwords; restore the secrets the data depends on.
            currentApp.InstallSecret = restoredApp.InstallSecret;
            // Scan credentials are encrypted with the credential key; enrolled agents chain to the agent CA.
            if (!string.IsNullOrEmpty(restoredApp.CredentialKey)) currentApp.CredentialKey = restoredApp.CredentialKey;
            if (!string.IsNullOrEmpty(restoredApp.AgentCaPfxPassword) && zip.GetEntry("secrets/agent-ca.pfx") is { } caEntry)
            {
                caEntry.ExtractToFile(paths.AgentCaFile, overwrite: true);
                currentApp.AgentCaPfxPassword = restoredApp.AgentCaPfxPassword;
                WindowsHost.SecureFile(paths.AgentCaFile, (WindowsHost.ServiceSid(apiService), System.Security.AccessControl.FileSystemRights.Read));
            }
            currentOwner.BackupKey = restoredOwner.BackupKey;
            store.Write(paths.AppSecretsFile, currentApp);
            store.Write(paths.OwnerSecretsFile, currentOwner);

            var cs = ConnectionStrings.Build(options.Database, options.Database.OwnerUser, currentOwner.DbOwnerPassword, applicationName: "zentriqctl");
            var migrator = new Zentriq.Infrastructure.Database.SqlMigrator(Microsoft.Extensions.Logging.Abstractions.NullLogger<Zentriq.Infrastructure.Database.SqlMigrator>.Instance);
            var applied = await migrator.MigrateAsync(cs, PostgresProvisioner.AppRole, options.ProductVersion, ct);
            log.Info($"Restored backup from {header.CreatedAt:u} (version {header.ProductVersion}); applied {applied} migration(s).");
            await RecordAsync(currentOwner, "restore", "succeeded", new { file = Path.GetFileName(file), from = header.ProductVersion });

            WindowsHost.StartAndWait(workerService, TimeSpan.FromMinutes(2));
            WindowsHost.StartAndWait(apiService, TimeSpan.FromMinutes(2));
            log.Info("Services restarted. Run 'zentriqctl verify' to confirm health.");
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    private async Task<(string ZipPath, BackupHeader Header)> DecryptToFileAsync(string file, string? recoveryKeyFile, string work, CancellationToken ct)
    {
        var zipPath = Path.Combine(work, "payload.zip");
        await using var input = File.OpenRead(file);
        await using var output = File.Create(zipPath);
        BackupHeader header;
        try
        {
        header = await BackupCrypto.DecryptAsync(input, output, h =>
        {
            var key = recoveryKeyFile is not null ? ReadRecoveryKey(recoveryKeyFile) : Secrets().Owner.BackupKey;
            if (Fingerprint(key) != h.KeyFingerprint)
                throw new SetupException($"This backup was encrypted with key {h.KeyFingerprint}; the key supplied is {Fingerprint(key)}.",
                    "Use --recovery-key <file> with the recovery key of the installation that created the backup.");
            return Convert.FromBase64String(key);
        }, ct);
        }
        catch (InvalidDataException ex) { throw new SetupException(ex.Message, "Check that the file is a complete Zentriq backup and that the right recovery key is used."); }
        return (zipPath, header);
    }

    public static string ReadRecoveryKey(string file)
    {
        var line = File.ReadAllLines(file).FirstOrDefault(l => l.StartsWith("Key:", StringComparison.Ordinal))
                   ?? throw new SetupException("The recovery key file has no 'Key:' line.");
        return line["Key:".Length..].Trim();
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Fastest).Open(), Encoding.UTF8);
        w.Write(content);
    }

    private static string ReadEntry(ZipArchive zip, string name)
    {
        using var r = new StreamReader(zip.GetEntry(name)?.Open() ?? throw new SetupException($"Backup is missing {name}."));
        return r.ReadToEnd();
    }

    private static string Sanitize(string s) => new(s.Where(c => char.IsLetterOrDigit(c) || c == '-').Take(32).ToArray());

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
