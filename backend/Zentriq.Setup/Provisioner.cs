using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Zentriq.Domain;
using Zentriq.Infrastructure.Configuration;
using Zentriq.Infrastructure.Data;
using Zentriq.Infrastructure.Database;
using Zentriq.Infrastructure.Jobs;
using Zentriq.Infrastructure.Secrets;
using Zentriq.Infrastructure.Setup;

namespace Zentriq.Setup;

/// <summary>
/// The installer's provisioning engine (install, repair, upgrade). Success is reported only after
/// end-to-end verification: authenticated PostgreSQL connection, migrations, API readiness over
/// HTTPS, a worker self-test job, first-administrator creation and a committed trial.
/// </summary>
public sealed class Provisioner(ProvisionInput input, ZentriqPaths paths, Reporter log)
{
    private readonly SecretStore _store = new(SecretProtectorFactory.Create());
    private readonly ProvisionResult _result = new();
    private string _thumbprint = "";

    private string BaseUrl => $"https://localhost:{input.Https.Port}";
    private string PublicUrl => $"https://{CertificateManager.SubjectNames(input.Https.HostName)[0]}{(input.Https.Port == 443 ? "" : ":" + input.Https.Port)}/";

    public async Task<ProvisionResult> RunAsync(CancellationToken ct)
    {
        _result.Mode = input.Mode;
        _result.ProductVersion = input.ProductVersion;
        _result.LogPath = log.LogPath;
        var steps = new (string Name, Func<CancellationToken, Task> Run)[]
        {
            ("System compatibility and prerequisite checks", PreflightAsync),
            ("Prepare data directories and permissions", DirectoriesAsync),
            ("Install or connect to PostgreSQL", PostgresAsync),
            ("Create database, roles and protected credentials", DatabaseAsync),
            ("Pre-upgrade backup", PreUpgradeBackupAsync),
            ("Apply database migrations", MigrateAsync),
            ("Configure HTTPS certificate", HttpsAsync),
            ("Write configuration", ConfigAsync),
            ("Configure and start Zentriq services", ServicesAsync),
            ("Configure remote support", RemoteAsync),
            ("Verify API readiness over HTTPS", ApiReadyAsync),
            ("Verify the worker processes a test job", WorkerSelfTestAsync),
            ("Create the first administrator and start the trial", InitializeAsync),
            ("Verify the web console and trial state", VerifyAsync),
            ("Register maintenance and backup tasks", TasksAsync),
        };

        for (int i = 0; i < steps.Length; i++)
        {
            var (name, run) = steps[i];
            log.Step(i + 1, steps.Length, name);
            log.Progress((int)(i * 100.0 / steps.Length), name);
            var sw = Stopwatch.StartNew();
            try
            {
                await run(ct);
                _result.Checks[name] = "PASS";
                log.Info($"OK  {name} ({sw.ElapsedMilliseconds} ms)");
            }
            catch (Exception ex)
            {
                _result.Checks[name] = "FAIL";
                _result.Succeeded = false;
                _result.FailedStep = name;
                _result.Error = ex is SetupException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
                _result.Remedy = (ex as SetupException)?.Remedy ?? "Review the log, correct the problem and run the installer again (Repair), or run 'zentriqctl diagnose'.";
                log.Error($"FAILED {name}: {ex}");
                await RecordHistoryAsync("failed", new { step = name, error = _result.Error });
                WriteResult();
                return _result;
            }
        }

        log.Progress(100, "Zentriq is ready");
        _result.Succeeded = true;
        _result.Url = PublicUrl;
        await RecordHistoryAsync("succeeded", new { url = _result.Url });
        WriteResult();
        log.Info($"Zentriq {input.ProductVersion} is ready at {_result.Url}");
        return _result;
    }

    // ---- state carried between steps ------------------------------------------------------------
    private string _superPassword = "";
    private AppSecrets _app = new();
    private OwnerSecrets _owner = new();
    private ZentriqOptions _options = new();
    private string OwnerConnection => ConnectionStrings.Build(_options.Database, _options.Database.OwnerUser, _owner.DbOwnerPassword, applicationName: "zentriqctl");

    private Task PreflightAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new SetupException("zentriqctl provisioning runs on Windows only.");
        if (!WindowsHost.IsAdministrator()) throw new SetupException("Administrator rights are required.", "Run the installer or zentriqctl from an elevated session.");
        if (Environment.OSVersion.Version < new Version(10, 0, 14393))
            throw new SetupException($"Windows {Environment.OSVersion.Version} is not supported.", "The minimum operating system requirement is Windows Server 2016 (any edition).");
        if (!Environment.Is64BitOperatingSystem) throw new SetupException("A 64-bit operating system is required.");
        var drive = new DriveInfo(Path.GetPathRoot(paths.Root)!);
        if (drive.AvailableFreeSpace < 2L * 1024 * 1024 * 1024)
            throw new SetupException($"Less than 2 GB free on {drive.Name}.", "Free disk space or choose another data location.");
        if (input.Mode == "install" && input.Admin is null && !File.Exists(paths.AppSecretsFile))
            log.Warn("No administrator details supplied; initialization will be skipped if the installation is not yet initialized.");
        foreach (var svc in new[] { input.ApiService, input.WorkerService })
            if (!WindowsHost.ServiceExists(svc)) throw new SetupException($"Service {svc} is not registered.", "Run the Zentriq MSI (it registers the services) rather than zentriqctl alone.");
        log.Info($"Windows {Environment.OSVersion.Version}, {Environment.ProcessorCount} CPUs, {drive.AvailableFreeSpace / (1024 * 1024 * 1024)} GB free on {drive.Name}");
        return Task.CompletedTask;
    }

    private Task DirectoriesAsync(CancellationToken ct)
    {
        var api = WindowsHost.ServiceSid(input.ApiService);
        var worker = WindowsHost.ServiceSid(input.WorkerService);
        const FileSystemRights read = FileSystemRights.ReadAndExecute;
        const FileSystemRights modify = FileSystemRights.Modify;
        WindowsHost.SecureDirectory(paths.Root, (api, read), (worker, read));
        WindowsHost.SecureDirectory(paths.Config, (api, read), (worker, read));
        WindowsHost.SecureDirectory(paths.Secrets, (api, read), (worker, read));
        WindowsHost.SecureDirectory(paths.DataProtectionKeys, (api, modify));
        if (WindowsHost.ServiceExists(RemoteService))
            WindowsHost.SecureDirectory(paths.Logs, (api, modify), (worker, modify), (WindowsHost.ServiceSid(RemoteService), modify));
        else
            WindowsHost.SecureDirectory(paths.Logs, (api, modify), (worker, modify));
        WindowsHost.SecureDirectory(paths.Packages, (worker, read));
        WindowsHost.SecureDirectory(paths.Exports, (api, modify), (worker, modify));
        WindowsHost.SecureDirectory(paths.Backups); // SYSTEM and Administrators only
        log.Info($"Data root {paths.Root} secured (services: {input.ApiService}, {input.WorkerService}).");
        return Task.CompletedTask;
    }

    private async Task PostgresAsync(CancellationToken ct)
    {
        _owner = _store.TryRead<OwnerSecrets>(paths.OwnerSecretsFile) ?? new OwnerSecrets();
        _app = _store.TryRead<AppSecrets>(paths.AppSecretsFile) ?? new AppSecrets();
        var pg = new PostgresProvisioner(log);
        if (input.Postgres.Mode == "existing" && string.IsNullOrEmpty(input.Postgres.SuperPassword)
            && !string.IsNullOrEmpty(_owner.DbOwnerPassword) && !string.IsNullOrEmpty(_app.DbAppPassword))
        {
            // Repair/upgrade against a customer-managed server: Zentriq's own roles already exist and
            // no superuser password is kept, so role management is skipped.
            await PostgresProvisioner.WaitForPortAsync(input.Postgres.Host, input.Postgres.Port, TimeSpan.FromMinutes(1), ct);
            log.Info($"Using existing Zentriq roles on {input.Postgres.Host}:{input.Postgres.Port} (no superuser password supplied).");
            _superPassword = "";
            return;
        }
        _superPassword = await pg.EnsureServerAsync(input.Postgres, _owner, ct);
        if (input.Postgres.Mode == "bundled") { _owner.PgSuperPassword = _superPassword; _owner.PgSuperUser = input.Postgres.SuperUser; }
    }

    private async Task DatabaseAsync(CancellationToken ct)
    {
        // Keep existing secrets on repair/upgrade: the install secret protects the trial record and
        // the backup key decrypts existing backups. Role passwords are (re)applied to the server.
        if (string.IsNullOrEmpty(_owner.DbOwnerPassword)) _owner.DbOwnerPassword = SecretStore.NewPassword();
        if (string.IsNullOrEmpty(_owner.BackupKey)) _owner.BackupKey = SecretStore.NewKey();
        if (string.IsNullOrEmpty(_app.DbAppPassword)) _app.DbAppPassword = SecretStore.NewPassword();
        if (string.IsNullOrEmpty(_app.InstallSecret)) _app.InstallSecret = SecretStore.NewKey();
        if (string.IsNullOrEmpty(_app.HttpsPfxPassword)) _app.HttpsPfxPassword = SecretStore.NewPassword();
        if (string.IsNullOrEmpty(_app.CredentialKey)) _app.CredentialKey = SecretStore.NewKey();
        if (string.IsNullOrEmpty(_app.AgentCaPfxPassword)) _app.AgentCaPfxPassword = SecretStore.NewPassword();
        if (_app.RemoteLoginKey is not { Length: 160 }) _app.RemoteLoginKey = Zentriq.Infrastructure.Remote.MeshCookie.NewKeyHex();

        if (_superPassword.Length > 0)
            await new PostgresProvisioner(log).EnsureDatabaseAsync(input.Postgres, _superPassword, _owner.DbOwnerPassword, _app.DbAppPassword, ct);

        _store.Write(paths.OwnerSecretsFile, _owner);
        WindowsHost.SecureFile(paths.OwnerSecretsFile); // SYSTEM + Administrators only
        _store.Write(paths.AppSecretsFile, _app);
        WindowsHost.SecureFile(paths.AppSecretsFile, (WindowsHost.ServiceSid(input.ApiService), FileSystemRights.Read), (WindowsHost.ServiceSid(input.WorkerService), FileSystemRights.Read));

        var recovery = Path.Combine(paths.Secrets, "backup-recovery-key.txt");
        if (!File.Exists(recovery))
        {
            File.WriteAllText(recovery,
                "Zentriq backup recovery key\r\n" +
                "Store this file OFFLINE (password manager or safe) and delete it from this server.\r\n" +
                "It is required to restore Zentriq backups on another server.\r\n\r\n" +
                $"Installation: {Environment.MachineName}\r\nCreated: {DateTimeOffset.UtcNow:O}\r\n" +
                $"Key fingerprint: {BackupService.Fingerprint(_owner.BackupKey)}\r\nKey: {_owner.BackupKey}\r\n");
            WindowsHost.SecureFile(recovery);
            log.Warn($"Backup recovery key written to {recovery}. Move it to offline storage.");
        }

        _options = new ZentriqOptions
        {
            Database = new DatabaseOptions
            {
                Host = input.Postgres.Host, Port = input.Postgres.Port, Database = input.Postgres.Database,
                AppUser = PostgresProvisioner.AppRole, OwnerUser = PostgresProvisioner.OwnerRole, SslMode = input.Postgres.SslMode,
            },
            Https = new HttpsOptions { Port = input.Https.Port, HostName = CertificateManager.SubjectNames(input.Https.HostName)[0], CertificateThumbprint = input.Https.CertificateThumbprint },
            PostgresBinDirectory = PostgresProvisioner.FindBinDirectory(input.Postgres),
            PostgresServiceName = input.Postgres.Mode == "bundled" ? input.Postgres.ServiceName : null,
            ProductVersion = input.ProductVersion,
        };

        // Prove the least-privilege role can authenticate.
        await using var conn = new NpgsqlConnection(ConnectionStrings.Build(_options.Database, _options.Database.AppUser, _app.DbAppPassword, applicationName: "zentriqctl"));
        await conn.OpenAsync(ct);
        log.Info("PostgreSQL accepts authenticated connections for the application role.");
    }

    private async Task PreUpgradeBackupAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(OwnerConnection);
        await conn.OpenAsync(ct);
        var status = await new SqlMigrator(NullLogger<SqlMigrator>.Instance).GetStatusAsync(conn, ct);
        if (status.CurrentVersion == 0 || status.UpToDate)
        {
            log.Info(status.CurrentVersion == 0 ? "New database; no backup needed." : "Schema is current; no pre-upgrade backup needed.");
            return;
        }
        log.Info($"Schema {status.CurrentVersion} -> {status.LatestVersion}: taking a pre-upgrade backup.");
        var file = await new BackupService(paths, _options, log).CreateAsync("pre-upgrade", ct);
        log.Info($"Pre-upgrade backup: {file}");
    }

    private async Task MigrateAsync(CancellationToken ct)
    {
        using var lf = LoggerFactory.Create(b => b.AddProvider(new ReporterLoggerProvider(log)));
        var applied = await new SqlMigrator(lf.CreateLogger<SqlMigrator>()).MigrateAsync(OwnerConnection, PostgresProvisioner.AppRole, input.ProductVersion, ct);
        log.Info(applied == 0 ? "Database schema already current." : $"Applied {applied} migration(s).");
    }

    private Task HttpsAsync(CancellationToken ct)
    {
        var cm = new CertificateManager(log);
        var info = string.IsNullOrWhiteSpace(input.Https.CertificateThumbprint)
            ? cm.EnsureSelfSigned(paths, _options.Https.HostName, _app.HttpsPfxPassword!, forceNew: false)
            : cm.UseExisting(input.Https.CertificateThumbprint!, input.ApiService);
        _thumbprint = info.Thumbprint;
        // Agent CA (issues agent client certificates for mutual TLS); readable only by the API service.
        if (Zentriq.Infrastructure.Agents.AgentCertificateAuthority.Ensure(paths.AgentCaFile, _app.AgentCaPfxPassword!, Environment.MachineName))
            log.Info("Created the agent certificate authority (Secrets\\agent-ca.pfx).");
        WindowsHost.SecureFile(paths.AgentCaFile, (WindowsHost.ServiceSid(input.ApiService), FileSystemRights.Read));
        if (string.IsNullOrWhiteSpace(input.Https.CertificateThumbprint))
        {
            _options.Https.CertificatePath = paths.HttpsCertificateFile;
            WindowsHost.SecureFile(paths.HttpsCertificateFile, (WindowsHost.ServiceSid(input.ApiService), FileSystemRights.Read));
        }
        return Task.CompletedTask;
    }

    private const string RemoteService = "ZentriqRemote";
    private string RemoteDir => Path.GetFullPath(Path.Combine(input.InstallDir, "..", "remote"));
    private bool RemoteInstalled => File.Exists(Path.Combine(RemoteDir, "node", "node.exe")) && WindowsHost.ServiceExists(RemoteService);

    private Task ConfigAsync(CancellationToken ct)
    {
        // Keep remote support settings an administrator changed in zentriq.json (port, public host name).
        try
        {
            if (File.Exists(paths.ConfigFile) && JsonDocument.Parse(File.ReadAllText(paths.ConfigFile)).RootElement.TryGetProperty("Zentriq", out var z)
                && z.TryGetProperty("Remote", out var rem))
            {
                var old = rem.Deserialize<RemoteOptions>();
                if (old is not null) { _options.Remote.Port = old.Port is > 0 and < 65536 ? old.Port : 8446; _options.Remote.PublicHost = old.PublicHost; }
            }
        }
        catch (JsonException) { }
        _options.Remote.Enabled = RemoteInstalled;
        var json = JsonSerializer.Serialize(new { Zentriq = _options }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(paths.ConfigFile, json);
        log.Info($"Configuration written to {paths.ConfigFile} (no secrets).");
        return Task.CompletedTask;
    }

    private async Task ServicesAsync(CancellationToken ct)
    {
        var dep = input.Postgres.Mode == "bundled" ? input.Postgres.ServiceName : null;
        await WindowsHost.ConfigureRecoveryAsync(input.WorkerService, dep, log);
        await WindowsHost.ConfigureRecoveryAsync(input.ApiService, dep, log);
        if (input.SkipServiceStart) { log.Warn("Service start skipped by request."); return; }
        // Restart so new configuration and secrets are loaded (repair/upgrade).
        WindowsHost.StopAndWait(input.ApiService, TimeSpan.FromMinutes(1));
        WindowsHost.StopAndWait(input.WorkerService, TimeSpan.FromMinutes(1));
        try
        {
            WindowsHost.StartAndWait(input.WorkerService, TimeSpan.FromMinutes(2));
            WindowsHost.StartAndWait(input.ApiService, TimeSpan.FromMinutes(2));
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            throw new SetupException("A Zentriq service did not start in time.", $"Check {paths.Logs}\\api-*.log, worker-*.log and the Windows Application event log.");
        }
        catch (InvalidOperationException ex)
        {
            throw new SetupException($"A Zentriq service failed to start: {ex.Message}", $"Check {paths.Logs} and the Windows Application event log.");
        }
        log.Info($"{input.WorkerService}: {WindowsHost.ServiceStatus(input.WorkerService)}, {input.ApiService}: {WindowsHost.ServiceStatus(input.ApiService)}");
    }

    /// <summary>
    /// Configures the bundled remote support server (MeshCentral) and starts the ZentriqRemote service. Never fails
    /// the installation: when something goes wrong it is reported and Zentriq continues without remote support.
    /// </summary>
    private async Task RemoteAsync(CancellationToken ct)
    {
        if (!_options.Remote.Enabled) { log.Info("Remote support is not part of this installation."); return; }
        try
        {
            var sid = WindowsHost.ServiceSid(RemoteService);
            var root = Path.Combine(paths.Root, "Remote");
            var data = Path.Combine(root, "data");
            WindowsHost.SecureDirectory(root, (sid, FileSystemRights.Modify)); // SYSTEM, Administrators, ZentriqRemote only
            foreach (var d in new[] { data, Path.Combine(root, "files"), Path.Combine(root, "backups") }) Directory.CreateDirectory(d);

            var host = string.IsNullOrWhiteSpace(_options.Remote.PublicHost) ? _options.Https.HostName : _options.Remote.PublicHost!;
            // The engine presents the console's certificate, so a browser that trusts the console trusts remote sessions too.
            bool ownCert;
            string? certReason;
            using (var consoleCert = ConsoleCertificate())
                ownCert = Zentriq.Infrastructure.Remote.RemoteServerConfig.WriteWebCertificate(consoleCert, data, out certReason);
            if (ownCert) log.Info("Remote support uses the console's HTTPS certificate.");
            else log.Warn($"Remote support uses its own certificate ({certReason}); browsers will ask to trust it once.");
            var config = Zentriq.Infrastructure.Remote.RemoteServerConfig.Build(new(host, _options.Remote.Port, _app.RemoteLoginKey!,
                Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), data, Path.Combine(root, "files"), Path.Combine(root, "backups"), ownCert));
            File.WriteAllText(Path.Combine(data, "config.json"), config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            foreach (var f in new[] { Zentriq.Infrastructure.Remote.RemoteServerConfig.TitlePicture, Zentriq.Infrastructure.Remote.RemoteServerConfig.AgentImage, Zentriq.Infrastructure.Remote.RemoteServerConfig.AgentIcon })
            {
                var src = Path.Combine(RemoteDir, "branding", f);
                if (File.Exists(src)) File.Copy(src, Path.Combine(data, f), overwrite: true);
            }

            WindowsHost.StopAndWait(RemoteService, TimeSpan.FromMinutes(1));
            var node = Path.Combine(RemoteDir, "node", "node.exe");
            var script = Path.Combine(RemoteDir, "app", "node_modules", "meshcentral", "meshcentral.js");
            // The account Zentriq manages remote support with. It signs in by key; its random password is never used or stored.
            string[] createArgs = [script, "--datapath", data, "--createaccount", _options.Remote.ServiceUser, "--" + "pass", SecretStore.NewPassword(), "--name", "Zentriq"];
            var create = await WindowsHost.RunAsync(node, createArgs, TimeSpan.FromMinutes(3), ct: ct);
            log.Info("Remote support service account: " + LastLine(create.Output));
            var admin = await WindowsHost.RunAsync(node, [script, "--datapath", data, "--adminaccount", _options.Remote.ServiceUser], TimeSpan.FromMinutes(3), ct: ct);
            log.Info("Remote support administrator rights: " + LastLine(admin.Output));

            await WindowsHost.ConfigureRecoveryAsync(RemoteService, null, log);
            if (input.SkipServiceStart) { log.Warn("Remote support service start skipped by request."); return; }
            WindowsHost.StartAndWait(RemoteService, TimeSpan.FromMinutes(2));
            using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true }) { Timeout = TimeSpan.FromSeconds(10) };
            var deadline = DateTime.UtcNow.AddMinutes(4); // the first start creates the server's certificates
            string last = "no response";
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var r = await http.GetAsync($"https://127.0.0.1:{_options.Remote.Port}/health.ashx", ct);
                    if (r.IsSuccessStatusCode) { log.Info($"Remote support is running on port {_options.Remote.Port}."); _result.Checks["remote support"] = "PASS"; return; }
                    last = $"HTTP {(int)r.StatusCode}";
                }
                catch (HttpRequestException ex) { last = ex.Message; }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested) { last = "timeout"; }
                await Task.Delay(3000, ct);
            }
            _result.Checks["remote support"] = "WARN";
            log.Warn($"Remote support did not answer on port {_options.Remote.Port} yet ({last}). See {paths.Logs}\remote-*.log. Zentriq works without it.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _result.Checks["remote support"] = "WARN";
            log.Warn($"Remote support could not be configured: {ex.Message}. Zentriq works without it; run Repair to try again.");
        }
    }

    /// <summary>The console's HTTPS certificate with an exportable private key, or null.</summary>
    private X509Certificate2? ConsoleCertificate()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_options.Https.CertificateThumbprint))
            {
                using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadOnly);
                return store.Certificates.Find(X509FindType.FindByThumbprint, _options.Https.CertificateThumbprint!, validOnly: false).OfType<X509Certificate2>().FirstOrDefault();
            }
            if (File.Exists(paths.HttpsCertificateFile))
                return X509CertificateLoader.LoadPkcs12FromFile(paths.HttpsCertificateFile, _app.HttpsPfxPassword, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (CryptographicException ex) { log.Warn("Could not open the console certificate for remote support: " + ex.Message); }
        return null;
    }

    private static string LastLine(string s) => s.Trim().Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "(no output)";

    private HttpClient Http()
    {
        var handler = new HttpClientHandler
        {
            // Pin the exact certificate we configured instead of trusting any certificate.
            ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None ||
                (cert is not null && string.Equals(cert.GetCertHashString(), _thumbprint, StringComparison.OrdinalIgnoreCase)),
        };
        return new HttpClient(handler) { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(20) };
    }

    private async Task ApiReadyAsync(CancellationToken ct)
    {
        using var http = Http();
        var deadline = DateTime.UtcNow.AddMinutes(3);
        string last = "no response";
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var r = await http.GetAsync("/health/ready", ct);
                last = $"{(int)r.StatusCode} {await r.Content.ReadAsStringAsync(ct)}";
                if (r.IsSuccessStatusCode) { log.Info($"API ready: {last}"); _result.Checks["https"] = "PASS"; return; }
            }
            catch (HttpRequestException ex) { last = ex.Message; }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { last = "timeout"; }
            await Task.Delay(3000, ct);
        }
        throw new SetupException($"The API did not become ready at {BaseUrl}/health/ready ({last}).", $"Check {paths.Logs}\\api-*.log and that port {input.Https.Port} is not used by another program.");
    }

    private async Task WorkerSelfTestAsync(CancellationToken ct)
    {
        await using var ds = NpgsqlDataSource.Create(OwnerConnection);
        var jobs = new JobQueue(ds);
        var id = await jobs.EnqueueAsync(new EnqueueRequest(JobTypes.SelfTest, new { source = "installer" }, $"selftest:{Guid.NewGuid():N}", Priority: 1, MaxAttempts: 1, TimeoutSeconds: 60), ct: ct);
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            await using var cmd = ds.CreateCommand("SELECT status, last_error FROM zentriq.jobs WHERE id = @id");
            cmd.Parameters.AddWithValue("id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            var status = r.GetString(0);
            if (status == "succeeded") { log.Info($"Worker processed self-test job {id}."); return; }
            if (status is "failed" or "dead" or "cancelled")
                throw new SetupException($"The worker self-test job {status}: {(r.IsDBNull(1) ? "" : r.GetString(1))}", $"Check {paths.Logs}\\worker-*.log.");
            await Task.Delay(2000, ct);
        }
        throw new SetupException("The worker did not process the self-test job within 2 minutes.", $"Check that the {input.WorkerService} service is running and {paths.Logs}\\worker-*.log.");
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(OwnerConnection);
        await conn.OpenAsync(ct);
        await using (var check = new NpgsqlCommand("SELECT initialized_at IS NOT NULL FROM zentriq.install_state WHERE id = 1", conn))
            if (await check.ExecuteScalarAsync(ct) is true) { log.Info("Installation already initialized; the trial is unchanged."); return; }

        if (input.Admin is null || string.IsNullOrWhiteSpace(input.Admin.Email))
            throw new SetupException("This Zentriq installation has no administrator yet (an earlier setup did not finish).", "Run setup again and fill in the Administrator page; for silent installs add the admin section to the answer file.");

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await using (var set = new NpgsqlCommand("UPDATE zentriq.install_state SET setup_token_hash = @h, setup_token_expires_at = now() + interval '30 minutes' WHERE id = 1", conn))
        {
            set.Parameters.AddWithValue("h", InitializationService.HashToken(token));
            await set.ExecuteNonQueryAsync(ct);
        }

        using var http = Http();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/setup/initialize")
        {
            Content = JsonContent.Create(new
            {
                setupToken = token, organizationName = string.IsNullOrWhiteSpace(input.Admin.Organization) ? "My organization" : input.Admin.Organization,
                adminEmail = input.Admin.Email, adminDisplayName = string.IsNullOrWhiteSpace(input.Admin.DisplayName) ? "Administrator" : input.Admin.DisplayName,
                adminPassword = input.Admin.Password,
            }),
        };
        req.Headers.Add("X-Zentriq-Request", "1");
        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            var title = TryTitle(body) ?? body;
            throw new SetupException($"Initialization was rejected: {title}", "Correct the administrator details (password: 12+ characters, three character classes) and run Repair.");
        }
        log.Info($"Initialization: {body}");
    }

    private async Task VerifyAsync(CancellationToken ct)
    {
        using var http = Http();
        var status = await http.GetFromJsonAsync<JsonElement>("/api/v1/setup/status", ct);
        if (!status.GetProperty("initialized").GetBoolean()) throw new SetupException("Setup status reports the installation is not initialized.");
        var mode = status.GetProperty("licenseMode").GetString();
        if (mode is not ("Trial" or "Licensed" or "LicensedGrace" or "TrialExpired" or "LicenseExpired"))
            throw new SetupException($"Unexpected license state after setup: {mode}.");
        if (status.TryGetProperty("trialExpiresAt", out var te) && te.ValueKind == JsonValueKind.String) _result.TrialExpiresAt = te.GetDateTimeOffset();

        await using var conn = new NpgsqlConnection(OwnerConnection);
        await conn.OpenAsync(ct);
        await using (var cmd = new NpgsqlCommand("SELECT count(*) FROM zentriq.trial", conn))
            if ((long)(await cmd.ExecuteScalarAsync(ct))! != 1) throw new SetupException("The trial record was not committed.");
        await using (var cmd = new NpgsqlCommand("SELECT count(*) FROM zentriq.user_roles WHERE role_id = 'admin'", conn))
            if ((long)(await cmd.ExecuteScalarAsync(ct))! < 1) throw new SetupException("No administrator exists.");

        using var ui = await http.GetAsync("/", ct);
        var html = await ui.Content.ReadAsStringAsync(ct);
        if (!ui.IsSuccessStatusCode || !html.Contains("Zentriq", StringComparison.OrdinalIgnoreCase))
            throw new SetupException($"The web console did not load from {BaseUrl}/ ({(int)ui.StatusCode}).");
        _result.Checks["trial"] = mode!;
        log.Info($"Web console loads; license mode {mode}; trial committed.");
    }

    private async Task TasksAsync(CancellationToken ct)
    {
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "zentriqctl.exe");
        await WindowsHost.RegisterTaskAsync(@"\Zentriq\Maintenance", exe, "maintenance run-pending", "MINUTE", "5", log);
        await WindowsHost.RegisterTaskAsync(@"\Zentriq\Nightly backup", exe, "backup --label nightly --keep 14", "DAILY", "01:30", log);
        log.Info("Registered scheduled tasks \\Zentriq\\Maintenance (every 5 min) and \\Zentriq\\Nightly backup (01:30).");
    }

    // ---- helpers -------------------------------------------------------------------------------

    private void WriteResult()
    {
        try
        {
            Directory.CreateDirectory(paths.Logs);
            File.WriteAllText(paths.InstallResultFile, JsonSerializer.Serialize(_result, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { log.Warn($"Could not write {paths.InstallResultFile}: {ex.Message}"); }
    }

    private async Task RecordHistoryAsync(string outcome, object detail)
    {
        if (string.IsNullOrEmpty(_owner.DbOwnerPassword) || string.IsNullOrEmpty(_options.Database.Host)) return;
        try
        {
            await using var conn = new NpgsqlConnection(OwnerConnection);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("INSERT INTO zentriq.installation_history (action, to_version, outcome, detail) VALUES (@a, @v, @o, @d::jsonb)", conn);
            cmd.Parameters.AddWithValue("a", input.Mode is "repair" or "upgrade" ? input.Mode : "install");
            cmd.Parameters.AddWithValue("v", input.ProductVersion);
            cmd.Parameters.AddWithValue("o", outcome);
            cmd.Parameters.AddWithValue("d", JsonSerializer.Serialize(detail));
            await cmd.ExecuteNonQueryAsync();
        }
        catch { /* the database may not exist yet */ }
    }

    private static string? TryTitle(string body)
    {
        try { return JsonDocument.Parse(body).RootElement.GetProperty("title").GetString(); } catch { return null; }
    }
}

/// <summary>Bridges ILogger (used by the migrator) to the Reporter.</summary>
public sealed class ReporterLoggerProvider(Reporter reporter) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new L(reporter);
    public void Dispose() { }

    private sealed class L(Reporter r) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var m = formatter(state, exception);
            if (logLevel >= LogLevel.Error) r.Error(m); else if (logLevel == LogLevel.Warning) r.Warn(m); else r.Info(m);
        }
    }
}
