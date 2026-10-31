using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;
using Npgsql;

namespace Zentriq.LicenseServer.Services;

/// <summary>
/// What the Windows installer runs (as SYSTEM): <c>install --data &lt;folder&gt;</c> prepares the data folder, the
/// bundled PostgreSQL (its own cluster on 127.0.0.1:5439, a random password kept with DPAPI) and the two Windows
/// services, and starts them; <c>uninstall</c> stops and removes the services. The data folder (database, signing
/// key, backups) is kept, so a later installation continues where this one ended.
/// Both are safe to run again (repair, upgrade).
/// </summary>
[SupportedOSPlatform("windows")]
public static class Installation
{
    public const string ServerService = "ZentriqLicenseServer";
    public const string DatabaseService = "ZentriqLicenseDb";
    public const string ServiceAccount = @"NT SERVICE\" + ServerService;
    public const string FirewallRule = "Zentriq License Server (HTTP/HTTPS)";
    public static string DefaultDataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Zentriq", "LicenseServer");

    private static string? _log;

    private static void Say(string text)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {text}";
        Console.WriteLine(line);
        if (_log is not null) try { File.AppendAllText(_log, line + Environment.NewLine); } catch (IOException) { }
    }

    public static string ServerExe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Zentriq.LicenseServer.exe");
    public static string PgBin => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "pgsql", "bin"));

    public static async Task<int> InstallAsync(string dataRoot, string? version)
    {
        var data = new ServerData(dataRoot);
        Directory.CreateDirectory(data.Logs);
        _log = Path.Combine(data.Logs, "install.log");
        try
        {
            Say($"Installing the Zentriq License Server {version} (data folder {data.Root}).");
            foreach (var d in new[] { data.Keys, data.Protected, data.Certs, data.Backups }) Directory.CreateDirectory(d);
            var state = data.Update(s => s with { InstalledVersion = version ?? s.InstalledVersion });

            // 1. Data folder: administrators and SYSTEM (the service is added once it exists).
            Run("icacls.exe", $"\"{data.Root}\" /inheritance:r /grant:r *S-1-5-32-544:(OI)(CI)F *S-1-5-18:(OI)(CI)F /C /Q");

            // 2. Database: own cluster, loopback only, run by NETWORK SERVICE.
            StopService(ServerService, TimeSpan.FromSeconds(60));
            await EnsureDatabaseAsync(data, state);

            // 3. The license server service, under its own virtual account.
            var serverBin = $"\"{ServerExe}\" serve --data \"{data.Root}\"".Replace("\"", "\\\"");
            Run("sc.exe", ServiceExists(ServerService)
                ? $"config {ServerService} binPath= \"{serverBin}\" start= delayed-auto obj= \"{ServiceAccount}\" depend= {DatabaseService}"
                : $"create {ServerService} binPath= \"{serverBin}\" start= delayed-auto obj= \"{ServiceAccount}\" depend= {DatabaseService} DisplayName= \"Zentriq License Server\"");
            Run("sc.exe", $"description {ServerService} \"License generation, activation and the license portal (Zentriq). Data: {data.Root}\"");
            // Restart after a failure, and when the setup wizard restarts it to apply new address settings.
            Run("sc.exe", $"failure {ServerService} reset= 86400 actions= restart/5000/restart/5000/restart/30000");
            Run("sc.exe", $"failureflag {ServerService} 1");
            Run("icacls.exe", $"\"{data.Root}\" /grant \"{ServiceAccount}:(OI)(CI)M\" /C /Q");

            // Firewall: HTTP/HTTPS for the public mode (the server listens on them only when the setup chose it).
            Run("netsh.exe", $"advfirewall firewall delete rule name=\"{FirewallRule}\"", allowFail: true);
            Run("netsh.exe", $"advfirewall firewall add rule name=\"{FirewallRule}\" dir=in action=allow protocol=TCP localport=80,443 program=\"{ServerExe}\" enable=yes");

            // 4. Start.
            StartService(ServerService, TimeSpan.FromSeconds(90));
            Say($"Done. Finish the setup at http://localhost:{state.HttpPort}/setup on this computer.");
            return 0;
        }
        catch (Exception ex)
        {
            Say("Installation failed: " + ex.Message);
            return 1;
        }
    }

    private static async Task EnsureDatabaseAsync(ServerData data, SetupState state)
    {
        var initdb = Path.Combine(PgBin, "initdb.exe");
        var pgCtl = Path.Combine(PgBin, "pg_ctl.exe");
        if (!File.Exists(initdb)) throw new InvalidOperationException($"PostgreSQL is missing ({PgBin}); repair the installation.");
        if (!File.Exists(Path.Combine(data.Pg, "PG_VERSION")))
        {
            Say("Creating the database cluster.");
            var password = ServerData.NewSecret();
            var pwFile = Path.Combine(data.Protected, "initdb.tmp");
            File.WriteAllText(pwFile, password);
            try
            {
                Run(initdb, $"-D \"{data.Pg}\" -U {ServerData.DatabaseUser} -A scram-sha-256 --pwfile=\"{pwFile}\" -E UTF8 --no-locale");
            }
            finally { File.Delete(pwFile); }
            data.SetSecret("database", password);
        }
        // NETWORK SERVICE runs PostgreSQL; it needs the cluster folder (only that).
        Run("icacls.exe", $"\"{data.Pg}\" /grant *S-1-5-20:(OI)(CI)F /C /Q");
        if (!ServiceExists(DatabaseService))
            Run(pgCtl, $"register -N {DatabaseService} -U \"NT AUTHORITY\\NetworkService\" -D \"{data.Pg}\" -S auto -w -o \"-p {state.PgPort} -c listen_addresses=127.0.0.1 -c logging_collector=on -c log_directory=log -c log_rotation_age=7d -c log_truncate_on_rotation=on\"");
        Run("sc.exe", $"description {DatabaseService} \"Database of the Zentriq License Server (127.0.0.1:{state.PgPort} only).\"");
        Run("sc.exe", $"failure {DatabaseService} reset= 86400 actions= restart/5000/restart/5000/restart/30000");
        StartService(DatabaseService, TimeSpan.FromSeconds(120));

        var admin = new NpgsqlConnectionStringBuilder(data.ConnectionString(state)) { Database = "postgres", Timeout = 15 }.ConnectionString;
        await using (var conn = new NpgsqlConnection(admin))
        {
            await OpenWithRetryAsync(conn);
            await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @n", conn);
            exists.Parameters.AddWithValue("n", ServerData.DatabaseName);
            if (await exists.ExecuteScalarAsync() is null)
            {
                Say("Creating the database.");
                await using var create = new NpgsqlCommand($"CREATE DATABASE {ServerData.DatabaseName} ENCODING 'UTF8'", conn);
                await create.ExecuteNonQueryAsync();
            }
        }
        await using var ds = NpgsqlDataSource.Create(data.ConnectionString(state));
        var applied = await MigrationRunner.MigrateAsync(ds);
        Say($"Database ready ({applied} migration(s) applied).");
    }

    private static async Task OpenWithRetryAsync(NpgsqlConnection conn)
    {
        for (var i = 0; ; i++)
        {
            try { await conn.OpenAsync(); return; }
            catch (NpgsqlException) when (i < 20) { await Task.Delay(1500); }
        }
    }

    public static int Uninstall()
    {
        _log = Path.Combine(DefaultDataRoot, "logs", "install.log");
        foreach (var name in new[] { ServerService, DatabaseService })
        {
            try
            {
                StopService(name, TimeSpan.FromSeconds(90));
                if (ServiceExists(name)) Run("sc.exe", $"delete {name}", allowFail: true);
            }
            catch (Exception ex) { Say($"Could not remove {name}: {ex.Message}"); }
        }
        Run("netsh.exe", $"advfirewall firewall delete rule name=\"{FirewallRule}\"", allowFail: true);
        Say($"Services removed. The data folder {DefaultDataRoot} (database, signing key, backups) is kept.");
        return 0;
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    public static bool ServiceExists(string name) => ServiceController.GetServices().Any(s => s.ServiceName.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static void StartService(string name, TimeSpan timeout)
    {
        using var sc = new ServiceController(name);
        sc.Refresh();
        if (sc.Status == ServiceControllerStatus.Running) return;
        if (sc.Status != ServiceControllerStatus.StartPending) sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
        Say($"{name} is running.");
    }

    private static void StopService(string name, TimeSpan timeout)
    {
        if (!ServiceExists(name)) return;
        using var sc = new ServiceController(name);
        if (sc.Status is ServiceControllerStatus.Stopped) return;
        if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
        Say($"{name} stopped.");
    }

    private static void Run(string file, string args, bool allowFail = false)
    {
        var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {file}.");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(180_000)) { try { p.Kill(true); } catch (InvalidOperationException) { } throw new InvalidOperationException($"{Path.GetFileName(file)} did not finish in 3 minutes."); }
        var output = (stdout.Result + stderr.Result).Trim();
        Say($"{Path.GetFileName(file)} {Redact(args)} -> {p.ExitCode}{(output.Length > 0 ? ": " + output.Replace(Environment.NewLine, " ") : "")}");
        if (p.ExitCode != 0 && !allowFail) throw new InvalidOperationException($"{Path.GetFileName(file)} failed ({p.ExitCode}): {output}");
    }

    private static string Redact(string args) => System.Text.RegularExpressions.Regex.Replace(args, "--pwfile=\"[^\"]*\"", "--pwfile=…");
}
