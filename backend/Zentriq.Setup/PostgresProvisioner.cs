using System.Net.Sockets;
using Microsoft.Win32;
using Npgsql;
using Zentriq.Application.Security;
using Zentriq.Infrastructure.Secrets;

namespace Zentriq.Setup;

/// <summary>
/// Installs the bundled PostgreSQL (EDB Windows installer, documented unattended options only)
/// or validates an existing supported server, then creates the Zentriq database and its
/// least-privilege roles. Role passwords are sent as SCRAM verifiers, never as plaintext SQL.
/// </summary>
public sealed class PostgresProvisioner(Reporter log)
{
    public const int MinSupportedMajor = 14;
    public const int MaxSupportedMajor = 18;
    public const string OwnerRole = "zentriq_owner";
    public const string AppRole = "zentriq_app";

    public static string SuperConnection(PostgresInput pg, string password, string database = "postgres") => new NpgsqlConnectionStringBuilder
    {
        Host = pg.Host, Port = pg.Port, Username = pg.SuperUser, Password = password, Database = database,
        Timeout = 15, CommandTimeout = 120, ApplicationName = "zentriqctl", Pooling = false,
        SslMode = Enum.TryParse<SslMode>(pg.SslMode, true, out var m) ? m : SslMode.Prefer,
    }.ConnectionString;

    /// <summary>Returns the superuser password to use for the rest of provisioning.</summary>
    public async Task<string> EnsureServerAsync(PostgresInput pg, OwnerSecrets? existingOwner, CancellationToken ct)
    {
        if (pg.Mode == "existing")
        {
            if (string.IsNullOrEmpty(pg.SuperPassword))
                throw new SetupException("A PostgreSQL superuser password is required to prepare an existing server.",
                    "Enter the password of a PostgreSQL role that can create roles and databases.");
            await WaitForPortAsync(pg.Host, pg.Port, TimeSpan.FromSeconds(30), ct);
            await CheckVersionAsync(pg, pg.SuperPassword, ct);
            return pg.SuperPassword;
        }

        // Bundled server.
        if (WindowsHost.ServiceExists(pg.ServiceName))
        {
            log.Info($"PostgreSQL service {pg.ServiceName} already exists; reusing it.");
            WindowsHost.StartAndWait(pg.ServiceName, TimeSpan.FromMinutes(2));
            var pw = existingOwner?.PgSuperPassword ?? pg.SuperPassword
                     ?? throw new SetupException($"PostgreSQL service {pg.ServiceName} exists but its superuser password is not known to Zentriq.",
                         "Choose 'Use an existing PostgreSQL server' and enter the superuser password, or remove the existing service.");
            await WaitForPortAsync("localhost", pg.Port, TimeSpan.FromMinutes(2), ct);
            await CheckVersionAsync(pg, pw, ct);
            return pw;
        }

        if (string.IsNullOrEmpty(pg.InstallerPath) || !File.Exists(pg.InstallerPath))
            throw new SetupException($"The PostgreSQL installer was not found at '{pg.InstallerPath}'.",
                "Use Zentriq-Setup-x64.exe (which contains PostgreSQL), or place the PostgreSQL installer in the 'prereqs' folder next to Zentriq.msi.");

        var superPassword = SecretStore.NewPassword(24);
        var dataDir = pg.DataDir ?? Path.Combine(pg.InstallDir, "data");
        SetAsideIncompleteInstall(pg, dataDir);
        log.Info($"Installing PostgreSQL to {pg.InstallDir} (data: {dataDir}, port {pg.Port}, service {pg.ServiceName}).");
        if (IsPortInUse(pg.Port))
            throw new SetupException($"Port {pg.Port} is already in use, so PostgreSQL cannot be installed on it.",
                $"Stop the program using port {pg.Port} or choose another PostgreSQL port, then run setup again.");
        // Documented EDB installer options (installer --help). The initial superuser password is
        // rotated immediately afterwards, so its brief appearance on the command line is not reused.
        var args = new[]
        {
            "--mode", "unattended", "--unattendedmodeui", "none",
            "--prefix", pg.InstallDir, "--datadir", dataDir,
            "--serverport", pg.Port.ToString(), "--servicename", pg.ServiceName,
            "--superaccount", pg.SuperUser, "--superpassword", superPassword,
            "--enable-components", "server,commandlinetools",
            "--disable-components", "pgAdmin,stackbuilder",
            "--install_runtimes", "0", "--create_shortcuts", "0",
        };
        var started = DateTime.Now.AddMinutes(-1);
        var r = await WindowsHost.RunAsync(pg.InstallerPath, args, TimeSpan.FromMinutes(30), ct: ct);
        var installerLog = CollectInstallerLog(started, superPassword);
        if (r.ExitCode != 0 || !WindowsHost.ServiceExists(pg.ServiceName))
        {
            var cause = installerLog.Cause ?? Redactor.Redact(r.Output.Replace(superPassword, "***").Trim()).Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim();
            log.Error($"PostgreSQL installer exit code {r.ExitCode}. {(cause is null ? "" : "Installer reported: " + cause)}");
            throw new SetupException(
                $"PostgreSQL installation failed (exit code {r.ExitCode}){(cause is null ? "." : ": " + Shorten(cause, 220))}",
                installerLog.Path is null ? "See the Zentriq setup log." : $"PostgreSQL installer log: {installerLog.Path}");
        }
        WindowsHost.StartAndWait(pg.ServiceName, TimeSpan.FromMinutes(3));
        await WaitForPortAsync("localhost", pg.Port, TimeSpan.FromMinutes(2), ct);

        // Rotate the superuser password and restrict the listener to the local machine.
        var rotated = SecretStore.NewPassword(24);
        await using (var conn = new NpgsqlConnection(SuperConnection(pg, superPassword)))
        {
            await conn.OpenAsync(ct);
            await ExecAsync(conn, $"ALTER ROLE {Quote(pg.SuperUser)} PASSWORD '{ScramSha256.Verifier(rotated)}'", ct);
            await ExecAsync(conn, "ALTER SYSTEM SET listen_addresses = 'localhost'", ct);
            await ExecAsync(conn, "ALTER SYSTEM SET password_encryption = 'scram-sha-256'", ct);
        }
        log.Info("Restarting PostgreSQL to apply the local-only listener.");
        WindowsHost.Restart(pg.ServiceName, TimeSpan.FromMinutes(3));
        await WaitForPortAsync("localhost", pg.Port, TimeSpan.FromMinutes(2), ct);
        await CheckVersionAsync(pg, rotated, ct);
        return rotated;
    }

    /// <summary>
    /// A previous attempt that failed half-way can leave a PostgreSQL data folder (and EDB's install folder and
    /// registration) behind without a service. Zentriq never knew that cluster's password, so it cannot be used and
    /// makes the EDB installer fail ("data directory is not empty"). It is renamed — never deleted — so nothing is lost.
    /// </summary>
    private void SetAsideIncompleteInstall(PostgresInput pg, string dataDir)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        if (Directory.Exists(dataDir) && Directory.EnumerateFileSystemEntries(dataDir).Any())
        {
            var aside = dataDir.TrimEnd('\\') + ".incomplete-" + stamp;
            Directory.Move(dataDir, aside);
            log.Warn($"The PostgreSQL data folder {dataDir} was left by an earlier, incomplete installation (no {pg.ServiceName} service). Moved it to {aside}.");
        }
        // Stale EDB registration of an installation whose service does not exist.
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            foreach (var key in new[] { $@"SOFTWARE\PostgreSQL\Installations\{pg.ServiceName}", $@"SOFTWARE\PostgreSQL\Services\{pg.ServiceName}" })
                if (hklm.OpenSubKey(key) is { } k)
                {
                    k.Dispose();
                    hklm.DeleteSubKeyTree(key, false);
                    log.Warn($"Removed the stale PostgreSQL registration HKLM\\{key} (its service does not exist).");
                }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { log.Warn($"Could not clean the stale PostgreSQL registration: {ex.Message}"); }
    }

    private static bool IsPortInUse(int port) =>
        System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// The EDB installer writes install-postgresql.log (and bitrock_installer*.log) to the TEMP folder of the account
    /// running it (SYSTEM: C:\Windows\Temp). Copy the newest one into the Zentriq log folder and pull out its error.
    /// </summary>
    private (string? Path, string? Cause) CollectInstallerLog(DateTime since, string secret)
    {
        try
        {
            var temp = System.IO.Path.GetTempPath();
            var file = new[] { "install-postgresql.log", "bitrock_installer*.log" }
                .SelectMany(p => Directory.EnumerateFiles(temp, p))
                .Select(f => new FileInfo(f)).Where(f => f.LastWriteTime >= since)
                .OrderByDescending(f => f.Name.StartsWith("install-postgresql", StringComparison.OrdinalIgnoreCase)).ThenByDescending(f => f.LastWriteTime)
                .FirstOrDefault();
            if (file is null) return (null, null);
            var dest = file.FullName;
            if (log.LogPath is null) return (file.FullName, null); // never read an unredacted copy into messages
            if (log.LogPath is { } lp)
            {
                dest = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(lp)!, $"postgresql-install-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                // The log can contain the initial superuser password on the command line; redact while copying.
                File.WriteAllText(dest, Redactor.Redact(File.ReadAllText(file.FullName).Replace(secret, "***REDACTED***")));
            }
            var lines = File.ReadAllLines(dest);
            var cause = lines.Reverse()
                .FirstOrDefault(l => l.Contains("Error", StringComparison.OrdinalIgnoreCase) || l.Contains("failed", StringComparison.OrdinalIgnoreCase)
                                  || l.Contains("Problem running", StringComparison.OrdinalIgnoreCase) || l.Contains("FATAL", StringComparison.Ordinal))?.Trim();
            return (dest, cause);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, null); }
    }

    public async Task<int> CheckVersionAsync(PostgresInput pg, string superPassword, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(SuperConnection(pg, superPassword));
        try { await conn.OpenAsync(ct); }
        catch (PostgresException ex) when (ex.SqlState == "28P01")
        {
            throw new SetupException("PostgreSQL rejected the superuser credentials.", "Check the user name and password.");
        }
        catch (NpgsqlException ex)
        {
            throw new SetupException($"Cannot connect to PostgreSQL at {pg.Host}:{pg.Port}: {ex.Message}", "Check the host, port, firewall and pg_hba.conf.");
        }
        var major = conn.PostgreSqlVersion.Major;
        if (major is < MinSupportedMajor or > MaxSupportedMajor)
            throw new SetupException($"PostgreSQL {conn.PostgreSqlVersion} is not supported.", $"Use PostgreSQL {MinSupportedMajor} to {MaxSupportedMajor}.");
        await using var cmd = new NpgsqlCommand("SELECT rolsuper OR rolcreaterole FROM pg_roles WHERE rolname = current_user", conn);
        if (await cmd.ExecuteScalarAsync(ct) is not true)
            throw new SetupException($"The PostgreSQL role '{pg.SuperUser}' cannot create roles.", "Use a superuser or a role with CREATEROLE and CREATEDB.");
        log.Info($"Connected to PostgreSQL {conn.PostgreSqlVersion} at {pg.Host}:{pg.Port}.");
        return major;
    }

    /// <summary>Creates/updates roles and the database. Idempotent; safe on repair and upgrade.</summary>
    public async Task EnsureDatabaseAsync(PostgresInput pg, string superPassword, string ownerPassword, string appPassword, CancellationToken ct)
    {
        await using (var conn = new NpgsqlConnection(SuperConnection(pg, superPassword)))
        {
            await conn.OpenAsync(ct);
            foreach (var (role, pw) in new[] { (OwnerRole, ownerPassword), (AppRole, appPassword) })
            {
                var exists = await ScalarAsync(conn, "SELECT 1 FROM pg_roles WHERE rolname = @r", ("r", role), ct) is not null;
                var verb = exists ? "ALTER" : "CREATE";
                await ExecAsync(conn, $"{verb} ROLE {Quote(role)} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '{ScramSha256.Verifier(pw)}'", ct);
                log.Info($"{(exists ? "Updated" : "Created")} role {role}.");
            }
            await ExecAsync(conn, $"ALTER ROLE {Quote(AppRole)} SET search_path = zentriq, public", ct);
            await ExecAsync(conn, $"ALTER ROLE {Quote(OwnerRole)} SET search_path = zentriq, public", ct);

            if (await ScalarAsync(conn, "SELECT 1 FROM pg_database WHERE datname = @d", ("d", pg.Database), ct) is null)
            {
                await ExecAsync(conn, $"CREATE DATABASE {Quote(pg.Database)} OWNER {Quote(OwnerRole)} ENCODING 'UTF8' TEMPLATE template0", ct);
                log.Info($"Created database {pg.Database}.");
            }
            else log.Info($"Database {pg.Database} already exists; it will be preserved and migrated.");
        }

        await using (var db = new NpgsqlConnection(SuperConnection(pg, superPassword, pg.Database)))
        {
            await db.OpenAsync(ct);
            await ExecAsync(db, "CREATE EXTENSION IF NOT EXISTS pgcrypto WITH SCHEMA public", ct);
            await ExecAsync(db, "REVOKE CREATE ON SCHEMA public FROM PUBLIC", ct);
            await ExecAsync(db, $"REVOKE ALL ON DATABASE {Quote(pg.Database)} FROM PUBLIC", ct);
            await ExecAsync(db, $"GRANT CONNECT, TEMPORARY ON DATABASE {Quote(pg.Database)} TO {Quote(OwnerRole)}, {Quote(AppRole)}", ct);
            await ExecAsync(db, $"GRANT CREATE ON DATABASE {Quote(pg.Database)} TO {Quote(OwnerRole)}", ct);
        }
    }

    public static string? FindBinDirectory(PostgresInput pg)
    {
        if (!string.IsNullOrEmpty(pg.BinDir) && File.Exists(Path.Combine(pg.BinDir, "pg_dump.exe"))) return pg.BinDir;
        var bundled = Path.Combine(pg.InstallDir, "bin");
        if (File.Exists(Path.Combine(bundled, "pg_dump.exe"))) return bundled;
        // EDB installers register themselves here.
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\PostgreSQL\Installations");
        if (root is null) return null;
        foreach (var name in root.GetSubKeyNames().OrderDescending())
        {
            using var k = root.OpenSubKey(name);
            if (k?.GetValue("Base Directory") is string baseDir && File.Exists(Path.Combine(baseDir, "bin", "pg_dump.exe")))
                return Path.Combine(baseDir, "bin");
        }
        return null;
    }

    public static async Task WaitForPortAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var c = new TcpClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                await c.ConnectAsync(host, port, cts.Token);
                return;
            }
            catch (Exception) when (!ct.IsCancellationRequested) { await Task.Delay(1000, ct); }
        }
        throw new SetupException($"PostgreSQL did not accept connections on {host}:{port} within {timeout.TotalSeconds:N0} s.", "Check that the PostgreSQL service is running.");
    }

    public static string Quote(string identifier)
    {
        if (string.IsNullOrEmpty(identifier) || identifier.Length > 63 || identifier.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-')))
            throw new SetupException($"'{identifier}' is not an allowed PostgreSQL identifier.");
        return "\"" + identifier + "\"";
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection conn, string sql, (string, object) p, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(p.Item1, p.Item2);
        return await cmd.ExecuteScalarAsync(ct);
    }
}
