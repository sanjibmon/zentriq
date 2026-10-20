using System.Diagnostics;
using Zentriq.Infrastructure.Configuration;
using Zentriq.Infrastructure.Remote;

namespace Zentriq.Worker.Remote;

/// <summary>
/// The ZentriqRemote service (Zentriq.Worker.exe --remote-host): runs the bundled MeshCentral server (Apache-2.0)
/// on the bundled Node.js, restarts it if it stops, and writes its console output to the Zentriq log. Configuration
/// (data\config.json) is written by zentriqctl; this service only starts and supervises the server.
/// </summary>
public sealed class RemoteHost(ZentriqPaths paths, ILogger<RemoteHost> logger) : BackgroundService
{
    public static string InstallRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
    public static string NodeExe => Path.Combine(InstallRoot, "remote", "node", "node.exe");
    public static string Script => Path.Combine(InstallRoot, "remote", "app", "node_modules", "meshcentral", "meshcentral.js");
    public static string DataPath(ZentriqPaths p) => Path.Combine(p.Root, "Remote", "data");

    private Process? _process;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(5);
        while (!stoppingToken.IsCancellationRequested)
        {
            var data = DataPath(paths);
            if (!File.Exists(NodeExe) || !File.Exists(Script))
            {
                logger.LogError("Remote support files are missing ({Node}, {Script}). Repair the Zentriq installation.", NodeExe, Script);
                await Wait(TimeSpan.FromMinutes(5), stoppingToken); continue;
            }
            if (!File.Exists(Path.Combine(data, "config.json")))
            {
                logger.LogWarning("Remote support is not configured yet ({Data}\\config.json missing); run Zentriq Setup (Repair).", data);
                await Wait(TimeSpan.FromMinutes(1), stoppingToken); continue;
            }
            var started = DateTime.UtcNow;
            try { if (Refresh(paths, data, out var why)) logger.LogInformation("Remote support configuration updated: {Why}.", why); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
            {
                logger.LogWarning("Could not update the remote support configuration: {Message}", ex.Message);
            }
            using var running = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            try
            {
                var psi = new ProcessStartInfo(NodeExe)
                {
                    WorkingDirectory = Path.GetDirectoryName(Script)!,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add(Script);
                psi.ArgumentList.Add("--datapath");
                psi.ArgumentList.Add(data);
                psi.Environment["NODE_ENV"] = "production";
                _process = Process.Start(psi) ?? throw new InvalidOperationException("node.exe did not start.");
                _process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) logger.LogInformation("[remote] {Line}", e.Data); };
                _process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) logger.LogWarning("[remote] {Line}", e.Data); };
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
                logger.LogInformation("Remote support server started (pid {Pid}).", _process.Id);
                var proc = _process;
                _ = ReportOriginsAsync(data, running.Token);
                // The API re-issues the console certificate when the server's names or addresses change; restart the
                // engine with the new certificate.
                _ = Task.Run(async () =>
                {
                    while (!running.IsCancellationRequested)
                    {
                        await Wait(TimeSpan.FromMinutes(1), running.Token);
                        if (running.IsCancellationRequested) return;
                        try
                        {
                            if (!Refresh(paths, data, out var what)) continue;
                            logger.LogInformation("Restarting the remote support server: {What}.", what);
                            delay = TimeSpan.FromSeconds(1);
                            proc.Kill(entireProcessTree: true);
                            return;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
                        {
                            logger.LogWarning("Could not update the remote support certificate: {Message}", ex.Message);
                        }
                    }
                });
                await _process.WaitForExitAsync(stoppingToken);
                logger.LogWarning("Remote support server exited with code {Code}.", _process.ExitCode);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "Remote support server could not be started"); }
            finally { running.Cancel(); _process?.Dispose(); _process = null; }
            // Back off when it keeps failing quickly; reset after a run of more than 5 minutes.
            delay = delay == TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(5)
                : DateTime.UtcNow - started > TimeSpan.FromMinutes(5) ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(Math.Min(120, delay.TotalSeconds * 2));
            await Wait(delay, stoppingToken);
        }
    }

    /// <summary>
    /// Brings the engine's configuration up to date: the console certificate written by the API (Remote\cert), and the
    /// browser origins it accepts (every name and address of this server plus host names technicians used). The
    /// certificate is only taken after the engine's first start (its own certificates exist; see zentriqctl).
    /// Returns true when something changed and the engine must restart.
    /// </summary>
    public static bool Refresh(ZentriqPaths paths, string data, out string what)
    {
        var changes = new List<string>();
        var src = paths.RemoteCertificateDir;
        const string marker = "zentriq-web-cert.txt";
        var cfgPath = Path.Combine(data, "config.json");
        var cfg = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(cfgPath))!.AsObject();
        var settings = cfg["settings"] as System.Text.Json.Nodes.JsonObject;
        var domain = cfg["domains"]?[""] as System.Text.Json.Nodes.JsonObject;
        var cfgChanged = false;

        if (File.Exists(Path.Combine(src, marker)) && File.Exists(Path.Combine(data, "root-cert-public.crt")))
        {
            var want = File.ReadAllText(Path.Combine(src, marker)).Trim();
            var have = File.Exists(Path.Combine(data, marker)) ? File.ReadAllText(Path.Combine(data, marker)).Trim() : null;
            if (!string.Equals(want, have, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var f in new[] { RemoteServerConfig.WebCertificateFile, RemoteServerConfig.WebKeyFile })
                    File.Copy(Path.Combine(src, f), Path.Combine(data, f), overwrite: true);
                if (settings is not null && settings["keepCerts"]?.GetValue<bool>() != true) { settings["keepCerts"] = true; cfgChanged = true; }
                File.WriteAllText(Path.Combine(data, marker), want); // after the files: a partial copy is retried
                changes.Add("new console certificate");
            }
        }

        // Internet access: devices and invitation links use the public address when one is set (Remote Support ›
        // Settings), otherwise the LAN host name Setup configured (remembered in zentriq-host.txt).
        var lanFile = Path.Combine(data, RemoteServerConfig.LanHostFile);
        if (!File.Exists(lanFile)) File.WriteAllText(lanFile, settings?["agentAliasDNS"]?.GetValue<string>() ?? "localhost");
        var lanHost = File.ReadAllText(lanFile).Trim();
        var internet = RemoteServerConfig.ReadInternet(src);
        if (settings is not null && RemoteServerConfig.ApplyInternet(settings, lanHost, internet))
        {
            cfgChanged = true;
            changes.Add(internet is null ? "internet address removed" : $"internet address {internet}");
        }

        if (domain is not null)
        {
            var extra = RemoteServerConfig.ReadLines(Path.Combine(src, RemoteServerConfig.OriginsFile)).ToList();
            if (internet is not null) extra.Add(internet.Host);
            var origins = RemoteServerConfig.Origins(lanHost, extra);
            var current = (domain["allowedOrigin"] as System.Text.Json.Nodes.JsonArray)?.Select(n => n?.GetValue<string>() ?? "").ToArray() ?? [];
            if (!current.SequenceEqual(origins))
            {
                domain["allowedOrigin"] = new System.Text.Json.Nodes.JsonArray(origins.Select(o => (System.Text.Json.Nodes.JsonNode)System.Text.Json.Nodes.JsonValue.Create(o)!).ToArray());
                cfgChanged = true;
                changes.Add("accepted host names " + string.Join(", ", origins));
            }
        }

        if (cfgChanged) File.WriteAllText(cfgPath, cfg.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        what = string.Join("; ", changes);
        return changes.Count > 0;
    }

    /// <summary>Once the engine answers, tells the API which host names it accepts (Remote\cert\origins-applied.txt).</summary>
    private async Task ReportOriginsAsync(string data, CancellationToken ct)
    {
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true }) { Timeout = TimeSpan.FromSeconds(5) };
        int port;
        string[] origins;
        try
        {
            var cfg = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(data, "config.json")))!;
            port = cfg["settings"]?["port"]?.GetValue<int>() ?? 8446;
            origins = (cfg["domains"]?[""]?["allowedOrigin"] as System.Text.Json.Nodes.JsonArray)?.Select(n => n?.GetValue<string>() ?? "").ToArray() ?? [];
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException) { return; }
        for (var i = 0; i < 120 && !ct.IsCancellationRequested; i++)
        {
            try
            {
                using var r = await http.GetAsync($"https://127.0.0.1:{port}/health.ashx", ct);
                if (r.IsSuccessStatusCode)
                {
                    if (Directory.Exists(paths.RemoteCertificateDir))
                        File.WriteAllLines(Path.Combine(paths.RemoteCertificateDir, RemoteServerConfig.AppliedOriginsFile), origins);
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException) { }
            await Wait(TimeSpan.FromSeconds(3), ct);
        }
    }

    private static async Task Wait(TimeSpan t, CancellationToken ct)
    {
        try { await Task.Delay(t, ct); } catch (OperationCanceledException) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try { if (_process is { HasExited: false } p) p.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        await base.StopAsync(cancellationToken);
    }
}
