using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace Zentriq.Agent;

/// <summary>
/// Windows Defender Application Control (WDAC) for device groups that turn it on (Software Deployment › Device groups):
/// a Zentriq base policy built from Windows' own DefaultWindows policy (Windows, Microsoft-signed and Store components)
/// plus the program folders only administrators can write to (Program Files, the Zentriq Agent). Programs run from
/// anywhere else — downloads, user profiles, temporary folders, USB drives — are reported (audit) or refused by Windows
/// itself (enforce), which also covers portable programs and per-user installers. Built with the ConfigCI PowerShell
/// module and activated without a restart where Windows supports it (CiTool, Windows 11 22H2 and later; the
/// CI update method otherwise). Off removes the policy again.
/// </summary>
internal static class Wdac
{
    public const string PolicyId = "{7A5C1E3B-2F4D-4B8E-9C6A-5D3E2F1A0B9C}";
    private const string StateKey = @"SOFTWARE\Zentriq\Agent\AppControl";

    /// <summary>off, audit, enforce, unsupported or error (what is in force on this device).</summary>
    public static string State => Read("WdacState") ?? "off";

    public static void Apply(string wanted, bool startup)
    {
        if (wanted is not ("audit" or "enforce")) wanted = "off";
        var applied = Read("WdacApplied") ?? "off";
        if (applied == wanted && !(startup && wanted != "off" && !File.Exists(ActivePolicyFile))) return;
        if (wanted == "off")
        {
            if (applied == "off") return;
            var (ok, output) = RunScript(RemoveScript());
            Write("WdacApplied", ok ? "off" : applied);
            Write("WdacState", ok ? "off" : "error");
            if (ok) Log.Info("WDAC: the Zentriq policy was removed" + (output.Contains("RESTART") ? " (Windows removes it completely at the next restart)." : "."));
            else Log.Warn("WDAC: could not remove the Zentriq policy: " + output.Trim());
            return;
        }
        var (done, text) = RunScript(ApplyScript(wanted));
        if (text.Contains("UNSUPPORTED"))
        {
            Write("WdacApplied", wanted);   // do not try again at every check-in
            Write("WdacState", "unsupported");
            Log.Warn("WDAC is not available on this edition of Windows (the ConfigCI module or the example policies are missing); application control continues without it.");
            return;
        }
        Write("WdacApplied", done ? wanted : applied);
        Write("WdacState", done ? wanted : "error");
        if (done) Log.Info($"WDAC: the Zentriq policy is active in {(wanted == "audit" ? "audit mode (Windows logs what it would block: Event Viewer › Applications and Services › Microsoft › Windows › CodeIntegrity)" : "enforced mode")}" +
                           (text.Contains("RESTART") ? "; Windows applies it completely at the next restart." : "."));
        else Log.Warn("WDAC: could not apply the policy: " + text.Trim());
    }

    private static string ActivePolicyFile => Path.Combine(Environment.SystemDirectory, "CodeIntegrity", "CiPolicies", "Active", PolicyId + ".cip");

    private static string ApplyScript(string mode)
    {
        var work = Path.Combine(AgentPaths.Root, "AppControl");
        return $@"
$ErrorActionPreference = 'Stop'
$id = '{PolicyId}'
$src = Join-Path $env:windir 'schemas\CodeIntegrity\ExamplePolicies\DefaultWindows_Audit.xml'
if (-not (Test-Path $src) -or -not (Get-Command New-CIPolicyRule -ErrorAction SilentlyContinue)) {{ 'UNSUPPORTED'; exit 3 }}
$dir = '{work.Replace("'", "''")}'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$base = Join-Path $dir 'wdac-base.xml'; $out = Join-Path $dir 'wdac-zentriq.xml'
Copy-Item $src $base -Force
$rules = @()
foreach ($p in @(""$env:ProgramFiles\*"", ""${{env:ProgramFiles(x86)}}\*"", '{TamperGuard.InstallFolder.Replace("'", "''")}\*')) {{ $rules += New-CIPolicyRule -FilePathRule $p }}
Merge-CIPolicy -PolicyPaths $base -OutputFilePath $out -Rules $rules | Out-Null
[xml]$x = Get-Content $out
if ($x.SiPolicy.PolicyID) {{ $x.SiPolicy.PolicyID = $id; $x.SiPolicy.BasePolicyID = $id }}
$x.Save($out)
Set-CIPolicyIdInfo -FilePath $out -PolicyName 'Zentriq Application Control' | Out-Null
Set-RuleOption -FilePath $out -Option 16
{(mode == "audit" ? "Set-RuleOption -FilePath $out -Option 3" : "Set-RuleOption -FilePath $out -Option 3 -Delete")}
$bin = Join-Path $env:windir ""System32\CodeIntegrity\CiPolicies\Active\$id.cip""
New-Item -ItemType Directory -Force -Path (Split-Path $bin) | Out-Null
ConvertFrom-CIPolicy -XmlFilePath $out -BinaryFilePath $bin | Out-Null
$citool = Join-Path $env:windir 'System32\CiTool.exe'
if (Test-Path $citool) {{ & $citool --update-policy $bin --json | Out-Null; 'OK' }}
else {{
  try {{ Invoke-CimMethod -Namespace root\Microsoft\Windows\CI -ClassName PS_UpdateAndCompareCIPolicy -MethodName Update -Arguments @{{ FilePath = $bin }} | Out-Null; 'OK' }}
  catch {{ 'OK RESTART' }}
}}
";
    }

    private static string RemoveScript() => $@"
$ErrorActionPreference = 'Stop'
$id = '{PolicyId}'
$bin = Join-Path $env:windir ""System32\CodeIntegrity\CiPolicies\Active\$id.cip""
$citool = Join-Path $env:windir 'System32\CiTool.exe'
if (Test-Path $citool) {{ & $citool --remove-policy $id --json | Out-Null }}
if (Test-Path $bin) {{ Remove-Item $bin -Force; 'OK RESTART' }} else {{ 'OK' }}
";

    private static (bool Ok, string Output) RunScript(string script)
    {
        var file = Path.Combine(AgentPaths.Root, "AppControl", "wdac.ps1");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, script, new UTF8Encoding(true));
            var ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            using var p = Process.Start(new ProcessStartInfo(ps, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{file}\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            })!;
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(180000)) { try { p.Kill(); } catch (Exception) { } return (false, "PowerShell did not finish within 3 minutes."); }
            var text = output.Result + error.Result;
            return (p.ExitCode == 0 && text.Contains("OK"), text);
        }
        catch (Exception ex) { return (false, ex.Message); }
        finally { try { File.Delete(file); } catch (Exception) { } }
    }

    private static string? Read(string name)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var k = hklm.OpenSubKey(StateKey, false);
            return k?.GetValue(name) as string;
        }
        catch (Exception) { return null; }
    }

    private static void Write(string name, string value)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var k = hklm.CreateSubKey(StateKey, true);
            k?.SetValue(name, value, RegistryValueKind.String);
        }
        catch (Exception ex) { Log.Warn("Could not save the WDAC state: " + ex.Message); }
    }
}
