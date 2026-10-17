using System.Diagnostics;
using Zentriq.Infrastructure.Configuration;

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
            if (SyncCertificate(paths, data)) logger.LogInformation("Remote support now uses the console's current HTTPS certificate.");
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
                            if (!SyncCertificate(paths, data)) continue;
                            logger.LogInformation("The console certificate changed; restarting the remote support server to use it.");
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
    /// Copies the console certificate written by the API (Remote\cert) into the engine's data folder when it changed.
    /// Only after the engine's first start (its own certificates exist; see zentriqctl), and it keeps the certificate
    /// (keepCerts) from then on. Returns true when the engine must restart.
    /// </summary>
    public static bool SyncCertificate(ZentriqPaths paths, string data)
    {
        var src = paths.RemoteCertificateDir;
        const string marker = "zentriq-web-cert.txt";
        if (!File.Exists(Path.Combine(src, marker)) || !File.Exists(Path.Combine(data, "root-cert-public.crt"))) return false;
        var want = File.ReadAllText(Path.Combine(src, marker)).Trim();
        var have = File.Exists(Path.Combine(data, marker)) ? File.ReadAllText(Path.Combine(data, marker)).Trim() : null;
        if (string.Equals(want, have, StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var f in new[] { Infrastructure.Remote.RemoteServerConfig.WebCertificateFile, Infrastructure.Remote.RemoteServerConfig.WebKeyFile })
            File.Copy(Path.Combine(src, f), Path.Combine(data, f), overwrite: true);
        var cfgPath = Path.Combine(data, "config.json");
        var cfg = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(cfgPath))!.AsObject();
        if (cfg["settings"] is System.Text.Json.Nodes.JsonObject settings && settings["keepCerts"]?.GetValue<bool>() != true)
        {
            settings["keepCerts"] = true;
            File.WriteAllText(cfgPath, cfg.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        File.WriteAllText(Path.Combine(data, marker), want); // last: a partial copy is retried
        return true;
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
