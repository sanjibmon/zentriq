using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Zentriq.Agent;

/// <summary>
/// Third-party application updates with winget (Windows Package Manager), run as SYSTEM from its installation folder:
/// <c>winget upgrade</c> lists installed applications with a newer version in the winget source, and
/// <c>winget upgrade --id … --exact --silent</c> installs one. Applications from other sources (Microsoft Store) are left
/// out: they cannot be updated as SYSTEM. Without winget (Windows Server 2019/2022, LTSC) the scan says so and reports
/// nothing, and the server keeps what it knew.
/// </summary>
internal static class WingetUpdates
{
    private static readonly TimeSpan ListTimeout = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan UpgradeTimeout = TimeSpan.FromMinutes(30);
    private const string Common = "--accept-source-agreements --disable-interactivity";

    internal sealed class App
    {
        public string Id = "";
        public string Name = "";
        public string Installed = "";
        public string Available = "";
        public string Source = "";
    }

    /// <summary>winget.exe of the newest App Installer (DesktopAppInstaller) package, or null.</summary>
    internal static string? Find()
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            if (!Directory.Exists(root)) return null;
            return Directory.GetDirectories(root, "Microsoft.DesktopAppInstaller_*__8wekyb3d8bbwe")
                .Select(d => new { Dir = d, Exe = Path.Combine(d, "winget.exe"), Version = VersionOf(Path.GetFileName(d)) })
                .Where(x => File.Exists(x.Exe))
                .OrderByDescending(x => x.Version)
                .Select(x => x.Exe)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static Version VersionOf(string folder)
    {
        var parts = folder.Split('_');
        return parts.Length > 1 && Version.TryParse(parts[1], out var v) ? v : new Version(0, 0);
    }

    /// <summary>Applications with an update in the winget source; null with a message when winget cannot answer.</summary>
    internal static (List<App>? Apps, string? Message) Scan(CancellationToken ct)
    {
        var exe = Find();
        if (exe is null) return (null, "winget (App Installer) is not installed on this device, so third-party application updates are not checked.");
        var dir = Path.GetDirectoryName(exe)!;
        // The source index is refreshed first (SYSTEM has its own copy); a failure here shows in the list below.
        ScriptRunner.Start(exe, "source update --disable-interactivity", dir, TimeSpan.FromMinutes(3), Encoding.UTF8, ct);
        var r = ScriptRunner.Start(exe, "upgrade " + Common, dir, ListTimeout, Encoding.UTF8, ct);
        if (r.TimedOut) return (null, "winget did not answer within 6 minutes.");
        var apps = Parse(r.Stdout);
        if (apps is null)
        {
            // No table: nothing to upgrade, or an error (winget prints the reason).
            var text = LastLine(r.Stdout + "\n" + r.Stderr);
            return r.ExitCode == 0 || unchecked((uint)r.ExitCode) == 0x8A150014 ? (new List<App>(), null) : (null, $"winget: {text} (exit code 0x{r.ExitCode:X8})");
        }
        Log.Info($"Patch management: winget lists {apps.Count} application update(s).");
        return (apps, null);
    }

    /// <summary>Installs the newer version of one application.</summary>
    internal static (bool Ok, string Message) Upgrade(string packageId, CancellationToken ct)
    {
        var exe = Find();
        if (exe is null) return (false, "winget is not installed on this device.");
        var r = ScriptRunner.Start(exe, $"upgrade --id \"{packageId}\" --exact --silent --accept-package-agreements {Common}",
            Path.GetDirectoryName(exe)!, UpgradeTimeout, Encoding.UTF8, ct);
        if (r.TimedOut) return (false, "winget did not finish within 30 minutes.");
        var code = unchecked((uint)r.ExitCode);
        return code switch
        {
            0 => (true, "Installed."),
            0x8A15002B => (true, "Not needed any more (already up to date)."),
            0x8A150014 => (false, "winget no longer finds this application on the device."),
            _ => (false, $"winget could not install it: {LastLine(r.Stdout + "\n" + r.Stderr)} (exit code 0x{code:X8})."),
        };
    }

    /// <summary>
    /// Reads the first table of <c>winget upgrade</c>: the header line before the line of dashes gives the column
    /// positions (Name, Id, Version, Available, Source — in that order whatever the display language). Null: no table.
    /// </summary>
    internal static List<App>? Parse(string output)
    {
        // Progress spinners use carriage returns: keep what follows the last one on each line.
        var lines = output.Replace("\r\n", "\n").Split('\n').Select(l => { var i = l.LastIndexOf('\r'); return i >= 0 ? l.Substring(i + 1) : l; }).ToList();
        var dash = lines.FindIndex(l => l.Trim().Length >= 10 && l.Trim().All(c => c == '-'));
        if (dash < 1) return null;
        var header = lines[dash - 1];
        var starts = new List<int>();
        for (var i = 0; i < header.Length; i++)
            if (!char.IsWhiteSpace(header[i]) && (i == 0 || char.IsWhiteSpace(header[i - 1]))) starts.Add(i);
        // Header words can contain a space in some languages ("Version disponible"): a column starts after 2+ spaces or at 0.
        starts = starts.Where(i => i == 0 || (i >= 2 && header[i - 1] == ' ' && header[i - 2] == ' ')).ToList();
        if (starts.Count < 4) return null;
        var list = new List<App>();
        for (var n = dash + 1; n < lines.Count; n++)
        {
            var line = lines[n];
            if (line.Trim().Length == 0) break;                         // end of the first table
            if (line.Length <= starts[3]) break;                         // "3 upgrades available." and similar
            string Col(int c) => c >= starts.Count || starts[c] >= line.Length ? "" : (c + 1 < starts.Count && starts[c + 1] <= line.Length ? line.Substring(starts[c], starts[c + 1] - starts[c]) : line.Substring(starts[c])).Trim();
            var app = new App { Name = Col(0), Id = Col(1), Installed = Col(2), Available = Col(3), Source = starts.Count > 4 ? Col(4) : "winget" };
            if (app.Id.Length == 0 || app.Id.EndsWith("…", StringComparison.Ordinal) || app.Id.Contains(' ')) continue;   // cut off by the width
            if (app.Available.Length == 0 || app.Available.Contains(' ')) continue;
            if (!string.Equals(app.Source, "winget", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(app);
            if (list.Count >= 1000) break;
        }
        return list;
    }

    internal static Dictionary<string, object?> ToReport(App a) => new()
    {
        ["id"] = a.Id, ["name"] = a.Name.TrimEnd('…'), ["installed"] = a.Installed, ["available"] = a.Available, ["source"] = a.Source,
    };

    private static string LastLine(string text)
    {
        var l = text.Replace("\r", "\n").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0 && x.Any(char.IsLetter)).LastOrDefault() ?? "no answer";
        return l.Length > 300 ? l.Substring(0, 300) : l;
    }
}
