using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Zentriq.Agent;

/// <summary>
/// Patch management on Windows with the Windows Update Agent (the device's own update source: Windows Update /
/// Microsoft Update, or WSUS when a policy sets one). Scan: the updates that are not installed and not hidden. Install:
/// downloads and installs the chosen updates silently as SYSTEM, then scans again and reports both; restarts the device
/// with a notice when the updates need it (unless the deployment says not to). The Windows Update Agent is driven by a
/// short PowerShell script (PowerShell 5.1 is part of every supported Windows).
/// </summary>
internal static class PatchAgent
{
    private static string Root => Path.Combine(AgentPaths.Root, "Patches");
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromHours(3);

    // Classification by category ID (names are translated on non-English Windows).
    private const string Common = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        $classes = @{
          'e6cf1350-c01b-414d-a61f-263d14d133b4' = 'critical'; '0fa1201d-4330-4fa8-8ae9-b877473b6441' = 'security'; 'e0789628-ce08-4437-be74-2495b842f43b' = 'definition'
          'cd5ffd1e-e932-4e3a-bf74-18bf0b1bbd83' = 'updates'; '28bc880e-0592-4cbf-8f95-c79b17911d5f' = 'update-rollup'; 'b54e7d24-7add-428f-8b75-90a396fa584f' = 'feature-pack'
          '68c5b0a3-d1a6-4553-ae49-01d3a7827828' = 'service-pack'; 'ebfc1fc5-71a4-4f7b-9aca-3b9a503104a0' = 'driver'; 'b4832bd8-e735-4761-8daf-37f882276dab' = 'tools'
          '3689bdc8-b205-4af4-8d4a-a63924c5e9d5' = 'upgrade'
        }
        function Write-Result($o) { [Console]::Out.Write(($o | ConvertTo-Json -Depth 6 -Compress)) }
        function Get-Session { $s = New-Object -ComObject Microsoft.Update.Session; $s.ClientApplicationID = 'Zentriq Agent'; $s }
        function Get-Source {
          $wsus = $false; $server = $null
          try { $wsus = (Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU' -ErrorAction Stop).UseWUServer -eq 1 } catch { }
          if ($wsus) { try { $server = (Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate' -ErrorAction Stop).WUServer } catch { } }
          if ($wsus) { if ($server) { "WSUS ($server)" } else { 'WSUS' } } else { 'Windows Update' }
        }
        function Get-Missing($session) {
          $result = $session.CreateUpdateSearcher().Search("IsInstalled=0 and IsHidden=0 and Type='Software'")
          $list = @()
          foreach ($u in $result.Updates) {
            $cls = $null; $clsName = $null; $prod = $null
            foreach ($c in $u.Categories) {
              if ($c.Type -eq 'UpdateClassification' -and -not $clsName) { $clsName = $c.Name; $cls = $classes[$c.CategoryID.ToLower()] }
              elseif ($c.Type -eq 'Product' -and -not $prod) { $prod = $c.Name }
            }
            if (-not $cls) { $cls = $clsName }
            $kb = $null; foreach ($k in $u.KBArticleIDs) { $kb = [string]$k; break }
            $cves = @(); foreach ($c in $u.CveIDs) { $cves += [string]$c }
            $released = $null; try { $released = $u.LastDeploymentChangeTime.ToString('yyyy-MM-dd') } catch { }
            $list += [pscustomobject]@{ id = [string]$u.Identity.UpdateID; kb = $kb; title = [string]$u.Title; classification = $cls; severity = [string]$u.MsrcSeverity
              product = $prod; released = $released; size = [int64]$u.MaxDownloadSize; reboot = ($u.InstallationBehavior.RebootBehavior -ne 0); cves = $cves; url = [string]$u.SupportUrl }
          }
          ,$list
        }
        function Test-Reboot { try { (New-Object -ComObject Microsoft.Update.SystemInfo).RebootRequired } catch { $false } }
        function Write-Failure($e) { Write-Result ([pscustomobject]@{ ok = $false; hresult = ('0x{0:X8}' -f $e.Exception.HResult); message = $e.Exception.Message }) }
        """;

    private const string ScanScript = Common + """

        try {
          $session = Get-Session
          $list = Get-Missing $session
          Write-Result ([pscustomobject]@{ ok = $true; source = (Get-Source); rebootPending = (Test-Reboot); updates = @($list) })
        } catch { Write-Failure $_ }
        """;

    private const string InstallScript = "param([string]$IdsFile)\r\n" + Common + """

        try {
          $wanted = @{}
          foreach ($i in (Get-Content -LiteralPath $IdsFile -Raw | ConvertFrom-Json)) { $wanted[([string]$i).ToLower()] = $true }
          $session = Get-Session
          $found = $session.CreateUpdateSearcher().Search("IsInstalled=0 and Type='Software'")
          $coll = New-Object -ComObject Microsoft.Update.UpdateColl
          $results = @()
          $seen = @{}
          foreach ($u in $found.Updates) {
            $id = ([string]$u.Identity.UpdateID).ToLower()
            if (-not $wanted.ContainsKey($id) -or $seen.ContainsKey($id)) { continue }
            $seen[$id] = $true
            if (-not $u.EulaAccepted) { $u.AcceptEula() }
            [void]$coll.Add($u)
          }
          foreach ($id in $wanted.Keys) { if (-not $seen.ContainsKey($id)) { $results += [pscustomobject]@{ id = $id; ok = $true; reboot = $false; message = 'Not needed any more (installed or replaced by a newer update).' } } }
          $rebootRequired = $false
          if ($coll.Count -gt 0) {
            $dl = $session.CreateUpdateDownloader(); $dl.Updates = $coll; [void]$dl.Download()
            $ready = New-Object -ComObject Microsoft.Update.UpdateColl
            for ($i = 0; $i -lt $coll.Count; $i++) {
              $u = $coll.Item($i)
              if ($u.IsDownloaded) { [void]$ready.Add($u) }
              else { $results += [pscustomobject]@{ id = ([string]$u.Identity.UpdateID).ToLower(); ok = $false; reboot = $false; message = 'The download failed.' } }
            }
            if ($ready.Count -gt 0) {
              $inst = $session.CreateUpdateInstaller()
              try { $inst.ForceQuiet = $true } catch { }
              $inst.Updates = $ready
              $ir = $inst.Install()
              $rebootRequired = [bool]$ir.RebootRequired
              for ($i = 0; $i -lt $ready.Count; $i++) {
                $r = $ir.GetUpdateResult($i)
                $u = $ready.Item($i)
                $ok = ($r.ResultCode -eq 2 -or $r.ResultCode -eq 3)
                $msg = switch ($r.ResultCode) { 2 { 'Installed.' } 3 { 'Installed with errors.' } 4 { 'Failed (0x{0:X8}).' -f $r.HResult } 5 { 'Aborted.' } default { 'Not installed (result {0}).' -f $r.ResultCode } }
                $results += [pscustomobject]@{ id = ([string]$u.Identity.UpdateID).ToLower(); ok = $ok; reboot = [bool]$r.RebootRequired; message = $msg }
              }
            }
          }
          $list = Get-Missing $session
          Write-Result ([pscustomobject]@{ ok = $true; source = (Get-Source); rebootPending = ($rebootRequired -or (Test-Reboot)); updates = @($list); results = @($results); installRebootRequired = $rebootRequired })
        } catch { Write-Failure $_ }
        """;

    public static (bool Ok, string? State, string Message) Run(ServerClient client, AgentAction a, CancellationToken ct)
    {
        if (a.Operation == "scan")
        {
            var (report, error) = Execute(ScanScript, null, ScanTimeout, ct);
            if (report is null) { Post(client, Failure(error!), ct); return (false, null, error!); }
            Post(client, report, ct);
            if (report.TryGetValue("ok", out var okv) && okv is JsonElement { ValueKind: JsonValueKind.False })
                return (false, null, report["message"] as string ?? "The scan failed.");
            var n = report.TryGetValue("updates", out var u) && u is JsonElement { ValueKind: JsonValueKind.Array } arr ? arr.GetArrayLength() : 0;
            return (true, "scanned", n == 0 ? "No missing updates." : $"{n} missing update(s) found.");
        }
        if (a.Operation != "install") return (false, null, "Unknown patch operation " + a.Operation + ".");

        Dictionary<string, JsonElement> p;
        try { p = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(a.Argument ?? "{}") ?? new Dictionary<string, JsonElement>(); }
        catch (JsonException) { return (false, null, "The patch request is not valid."); }
        var ids = p.TryGetValue("ids", out var idv) && idv.ValueKind == JsonValueKind.Array
            ? idv.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim().ToLowerInvariant())
                .Where(x => x.Length is > 0 and <= 80 && x.All(c => char.IsLetterOrDigit(c) || c == '-')).Distinct().ToList()
            : new List<string>();
        if (ids.Count == 0) return (false, null, "No updates were chosen.");
        var reboot = p.TryGetValue("reboot", out var rb) && rb.ValueKind == JsonValueKind.String ? rb.GetString() : "when-needed";
        var delay = p.TryGetValue("rebootDelayMinutes", out var dv) && dv.ValueKind == JsonValueKind.Number ? Math.Max(1, Math.Min(240, dv.GetInt32())) : 10;
        var message = p.TryGetValue("rebootMessage", out var mv) && mv.ValueKind == JsonValueKind.String ? mv.GetString() : null;

        AgentUpdater.PrepareFolder(Root);
        var idsFile = Path.Combine(Root, "ids-" + a.Id + ".json");
        File.WriteAllText(idsFile, JsonSerializer.Serialize(ids), new UTF8Encoding(false));
        Dictionary<string, object?>? rep; string? err;
        try { (rep, err) = Execute(InstallScript, idsFile, InstallTimeout, ct); }
        finally { try { File.Delete(idsFile); } catch (IOException) { } }
        if (rep is null) { Post(client, Failure(err!), ct); return (false, null, err!); }
        Post(client, rep, ct);
        if (rep.TryGetValue("ok", out var ok2) && ok2 is JsonElement { ValueKind: JsonValueKind.False })
            return (false, null, rep["message"] as string ?? "The installation failed.");

        int installed = 0, failed = 0;
        string? firstError = null;
        if (rep.TryGetValue("results", out var rs) && rs is JsonElement { ValueKind: JsonValueKind.Array } results)
            foreach (var r in results.EnumerateArray())
            {
                if (r.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True) installed++;
                else { failed++; firstError ??= r.TryGetProperty("message", out var m) ? m.GetString() : null; }
            }
        var needsRestart = rep.TryGetValue("installRebootRequired", out var irr) && irr is JsonElement { ValueKind: JsonValueKind.True };
        var text = $"{installed} of {installed + failed} update(s) installed" + (failed > 0 ? $"; {failed} failed ({firstError})" : "") + ".";
        if (needsRestart)
        {
            if (reboot == "never") text += " The device needs a restart to finish (not restarted: the deployment says not to).";
            else
            {
                Restart(delay, message);
                text += $" The device restarts in {delay} minute(s) to finish.";
            }
        }
        AgentService.RequestInventory();
        return (failed == 0, failed == 0 ? "installed" : null, text);
    }

    private static Dictionary<string, object?> Failure(string message) => new() { ["ok"] = false, ["message"] = message };

    private static void Post(ServerClient client, Dictionary<string, object?> report, CancellationToken ct)
    {
        try { client.PostPatchesAsync(report, ct).GetAwaiter().GetResult(); }
        catch (Exception ex) { Log.Warn("Could not send the patch scan: " + ex.Message); }
    }

    /// <summary>Runs a Windows Update Agent script and returns its JSON answer (or a message why there is none).</summary>
    private static (Dictionary<string, object?>? Report, string? Error) Execute(string script, string? idsFile, TimeSpan timeout, CancellationToken ct)
    {
        AgentUpdater.PrepareFolder(Root);
        var file = Path.Combine(Root, "wua-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(file, script, new UTF8Encoding(true));
        try
        {
            var ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            var args = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{file}\"" + (idsFile is null ? "" : $" -IdsFile \"{idsFile}\"");
            Log.Info(idsFile is null ? "Patch management: scanning with the Windows Update Agent." : "Patch management: installing updates with the Windows Update Agent.");
            // A Windows Update Agent call can hang after the service is stopped: end it when the service is disabled.
            using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var unavailable = false;
            using var watch = new Timer(_ => { if (WindowsUpdateDisabled()) { unavailable = true; try { run.Cancel(); } catch (ObjectDisposedException) { } } },
                null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
            var r = ScriptRunner.Start(ps, args, Root, timeout, Encoding.UTF8, run.Token);
            watch.Dispose();
            if (unavailable && !ct.IsCancellationRequested)
            {
                Log.Info("Patch management: the Windows Update service was disabled during the run; the Windows Update Agent was ended.");
                return (null, Explain("0x80070422", null));
            }
            Log.Info($"Patch management: the Windows Update Agent {(r.TimedOut ? "did not answer" : "answered")} after {r.Milliseconds / 1000} s (exit code {r.ExitCode}).");
            if (r.TimedOut) return (null, $"Windows Update did not answer within {timeout.TotalMinutes:0} minutes.");
            var json = r.Stdout.Trim();
            var start = json.IndexOf('{');
            if (start < 0) return (null, "Windows Update Agent: " + Last(r.Stderr.Length > 0 ? r.Stderr : json, "no answer"));
            Dictionary<string, JsonElement>? doc;
            try { doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json.Substring(start)); }
            catch (JsonException) { return (null, "The Windows Update Agent answer could not be read."); }
            if (doc is null) return (null, "No answer from the Windows Update Agent.");
            var report = doc.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
            if (doc.TryGetValue("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            {
                var hr = doc.TryGetValue("hresult", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
                var msg = doc.TryGetValue("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                report["message"] = Explain(hr, msg);
            }
            return (report, null);
        }
        finally { try { File.Delete(file); } catch (IOException) { } }
    }

    private static bool WindowsUpdateDisabled()
    {
        try
        {
            using var sc = new System.ServiceProcess.ServiceController("wuauserv");
            // Disabled is enough: a scan cannot finish then, and a stop of the service often stays pending while the Windows
            // Update Agent waits (the scan would hang until the time limit).
            return sc.StartType == System.ServiceProcess.ServiceStartMode.Disabled;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    /// <summary>Common Windows Update Agent errors in words.</summary>
    internal static string Explain(string? hresult, string? message)
    {
        var hr = (hresult ?? "").ToUpperInvariant();
        var text = hr switch
        {
            "0X80070422" => "The Windows Update service is disabled on this device; Zentriq cannot scan for updates until it is set to Manual or Automatic.",
            "0X8024402C" or "0X80072EE7" or "0X80072EFD" or "0X80072EE2" or "0X8024401C" or "0X80244022" or "0X8024402F" =>
                "The device cannot reach its update server (Windows Update or WSUS): check the network, the proxy and the WSUS address.",
            "0X80240438" or "0X8024500C" => "Access to Windows Update is turned off by a policy on this device (for example \"Remove access to use all Windows Update features\").",
            "0X8024000B" => "The scan was cancelled (Windows Update was busy or the device is shutting down).",
            "0X80070005" => "Access denied by the Windows Update Agent.",
            _ => null,
        };
        return text is null ? $"Windows Update Agent error {hresult}: {Last(message ?? "", "unknown error")}" : $"{text} ({hresult})";
    }

    private static string Last(string text, string fallback)
    {
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var s = lines.Count == 0 ? fallback : lines[0];
        return s.Length > 400 ? s.Substring(0, 400) : s;
    }

    /// <summary>Restarts the device after a notice to signed-in users (shutdown.exe; planned restart for a hotfix).</summary>
    private static void Restart(int minutes, string? message)
    {
        var text = (string.IsNullOrWhiteSpace(message) ? "Updates were installed on this computer. Save your work: it restarts in a few minutes." : message!).Replace("\"", "'");
        if (text.Length > 500) text = text.Substring(0, 500);
        try
        {
            var exe = Path.Combine(Environment.SystemDirectory, "shutdown.exe");
            using var proc = Process.Start(new ProcessStartInfo(exe, $"/r /t {minutes * 60} /d p:2:17 /c \"{text}\"") { UseShellExecute = false, CreateNoWindow = true });
            proc?.WaitForExit(30000);
            Log.Info($"Patch management: the device restarts in {minutes} minute(s) to finish the updates.");
        }
        catch (Exception ex) { Log.Warn("Patch management: the restart could not be scheduled: " + ex.Message); }
    }
}
