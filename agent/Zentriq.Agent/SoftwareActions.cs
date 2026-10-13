using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Zentriq.Agent;

/// <summary>
/// Silent uninstall of one application, requested by an administrator in the console. The command comes from this
/// device's own uninstall registration (never from the server): Windows Installer products are removed with
/// msiexec /x {ProductCode} /qn; other installers only when they register a QuietUninstallString.
/// </summary>
internal static class SoftwareActions
{
    private static readonly string[] Roots =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    };

    public static (bool Ok, string? State, string Message) Uninstall(string displayName, string? version, TimeSpan timeout)
    {
        if (displayName.IndexOf("Zentriq", StringComparison.OrdinalIgnoreCase) >= 0)
            return (false, null, "The Zentriq Agent cannot be uninstalled from the console.");

        string? command = null, args = null, found = null;
        using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            foreach (var root in Roots)
            {
                using var key = hklm.OpenSubKey(root);
                if (key is null) continue;
                foreach (var sub in key.GetSubKeyNames())
                {
                    using var k = key.OpenSubKey(sub);
                    if (k is null) continue;
                    var name = (k.GetValue("DisplayName") as string)?.Trim();
                    if (name is null || !name.Equals(displayName, StringComparison.OrdinalIgnoreCase)) continue;
                    var ver = k.GetValue("DisplayVersion") as string;
                    if (!string.IsNullOrEmpty(version) && !string.Equals(ver, version, StringComparison.OrdinalIgnoreCase)) continue;
                    found = name + (ver is null ? "" : " " + ver);
                    var isMsi = Convert.ToInt32(k.GetValue("WindowsInstaller") ?? 0) == 1 && Regex.IsMatch(sub, "^\\{[0-9A-Fa-f-]{36}\\}$");
                    var uninstall = k.GetValue("UninstallString") as string ?? "";
                    var msiCode = Regex.Match(uninstall, "\\{[0-9A-Fa-f-]{36}\\}");
                    if (isMsi || (uninstall.IndexOf("msiexec", StringComparison.OrdinalIgnoreCase) >= 0 && msiCode.Success))
                    {
                        command = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\msiexec.exe");
                        args = "/x " + (isMsi ? sub : msiCode.Value) + " /qn /norestart";
                    }
                    else if (k.GetValue("QuietUninstallString") is string quiet && quiet.Trim().Length > 0)
                    {
                        (command, args) = Split(quiet.Trim());
                    }
                    if (command is not null) break;
                }
                if (command is not null) break;
            }

        if (found is null) return (true, "uninstalled", $"'{displayName}' is not installed on this device (already removed).");
        if (command is null)
            return (false, null, $"'{found}' has no silent uninstaller (it is not a Windows Installer package and registers no QuietUninstallString). Remove it on the device or with your software distribution tool.");

        using var p = Process.Start(new ProcessStartInfo(command, args) { UseShellExecute = false, CreateNoWindow = true })!;
        if (!p.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { p.Kill(); } catch { }
            return (false, null, $"The uninstaller for '{found}' did not finish within {timeout.TotalMinutes:0} minutes and was stopped.");
        }
        return p.ExitCode switch
        {
            0 => (true, "uninstalled", $"'{found}' was uninstalled."),
            3010 or 1641 => (true, "uninstalled", $"'{found}' was uninstalled; Windows needs a restart to finish."),
            1605 => (true, "uninstalled", $"'{found}' is no longer installed."),
            1618 => (false, null, "Another installation is in progress on the device; try again later."),
            _ => (false, null, $"The uninstaller for '{found}' failed (exit code {p.ExitCode})."),
        };
    }

    private static (string, string) Split(string cmd)
    {
        if (cmd.StartsWith("\""))
        {
            var end = cmd.IndexOf('"', 1);
            if (end > 0) return (cmd.Substring(1, end - 1), cmd.Substring(end + 1).Trim());
        }
        var i = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return i > 0 ? (cmd.Substring(0, i + 4), cmd.Substring(i + 4).Trim()) : (cmd, "");
    }
}
