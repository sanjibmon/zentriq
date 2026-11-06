using System;
using System.Collections.Generic;
using System.Linq;

namespace Zentriq.Inventory;

/// <summary>A package upgrade available on a Linux device.</summary>
public sealed class LinuxUpdate
{
    public string Package { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Arch { get; set; }
    public string? Repository { get; set; }
    public bool Security { get; set; }
    public string? Severity { get; set; }
    public string? Advisory { get; set; }
}

/// <summary>Reads what the package managers print about available upgrades (apt, dnf / yum, zypper; C locale).</summary>
public static class LinuxPatches
{
    private static readonly string[] Arches = { "x86_64", "aarch64", "noarch", "i686", "i386", "armv7hl", "ppc64le", "s390x", "amd64", "arm64", "all" };

    private static string[] Tokens(string line) => line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

    /// <summary><c>apt list --upgradable</c>: <c>openssl/jammy-updates,jammy-security 3.0.2-0ubuntu1.12 amd64 [upgradable from: …]</c>.</summary>
    public static List<LinuxUpdate> ParseApt(string output)
    {
        var list = new List<LinuxUpdate>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var slash = line.IndexOf('/');
            if (slash <= 0 || line.StartsWith("Listing", StringComparison.Ordinal) || line.StartsWith("WARNING", StringComparison.Ordinal)) continue;
            var t = Tokens(line.Substring(slash + 1));
            if (t.Length < 3) continue;
            var suites = t[0];
            list.Add(new LinuxUpdate
            {
                Package = line.Substring(0, slash), Version = t[1], Arch = t[2], Repository = suites,
                Security = suites.Split(',').Any(s => s.EndsWith("-security", StringComparison.Ordinal)),
            });
        }
        return list;
    }

    /// <summary><c>dnf check-update</c> / <c>yum check-update</c>: <c>kernel.x86_64  5.14.0-362.8.1.el9_3  baseos</c> (until "Obsoleting Packages").</summary>
    public static List<LinuxUpdate> ParseCheckUpdate(string output)
    {
        var list = new List<LinuxUpdate>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("Obsoleting", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Security:", StringComparison.OrdinalIgnoreCase)) break;
            if (line.Length == 0 || char.IsWhiteSpace(line[0])) continue;
            var t = Tokens(line);
            if (t.Length < 3) continue;
            var dot = t[0].LastIndexOf('.');
            if (dot <= 0 || !Arches.Contains(t[0].Substring(dot + 1))) continue;
            if (!(char.IsDigit(t[1][0]) || t[1].IndexOf(':') > 0)) continue;
            list.Add(new LinuxUpdate { Package = t[0].Substring(0, dot), Arch = t[0].Substring(dot + 1), Version = t[1], Repository = t[2] });
        }
        return list;
    }

    /// <summary>The package name of a NEVRA (<c>kernel-core-5.14.0-362.el9.x86_64</c> → <c>kernel-core</c>).</summary>
    public static string? NameOfNevra(string nevra)
    {
        var s = nevra;
        var dot = s.LastIndexOf('.');
        if (dot > 0 && Arches.Contains(s.Substring(dot + 1))) s = s.Substring(0, dot);
        // name-version-release: the last two dash-separated parts are version and release.
        var r = s.LastIndexOf('-');
        if (r <= 0) return null;
        var v = s.LastIndexOf('-', r - 1);
        if (v <= 0) return null;
        var name = s.Substring(0, v);
        var colon = name.IndexOf(':');
        return colon >= 0 ? null : name;
    }

    /// <summary>
    /// <c>dnf updateinfo list --updates</c> / <c>yum updateinfo list</c> (dnf 4: <c>RHSA-2023:7549 Important/Sec. kernel-5.14…x86_64</c>;
    /// dnf 5: <c>FEDORA-2024-1 security moderate openssl-3.2.1-2.fc40.x86_64 2024-…</c>): package name → advisory.
    /// </summary>
    public static Dictionary<string, (bool Security, string? Severity, string Advisory)> ParseUpdateInfo(string output)
    {
        var map = new Dictionary<string, (bool, string?, string)>(StringComparer.Ordinal);
        foreach (var raw in output.Split('\n'))
        {
            var t = Tokens(raw.Trim());
            if (t.Length < 3 || t[0].Equals("Name", StringComparison.OrdinalIgnoreCase)) continue;
            string? nevra = null; var security = false; string? severity = null;
            foreach (var tok in t.Skip(1))
            {
                var low = tok.ToLowerInvariant();
                var dot = tok.LastIndexOf('.');
                if (nevra is null && tok.IndexOf('-') > 0 && dot > 0 && Arches.Contains(tok.Substring(dot + 1))) { nevra = tok; continue; }
                foreach (var part in low.Split('/'))
                {
                    var p = part.TrimEnd('.');
                    if (p is "sec" or "security") security = true;
                    else if (p is "critical" or "important" or "moderate" or "low") severity = p;
                }
            }
            if (nevra is null || NameOfNevra(nevra) is not { } name) continue;
            if (map.TryGetValue(name, out var old))
                map[name] = (old.Item1 || security, Worse(old.Item2, severity), old.Item3);
            else map[name] = (security, severity, t[0]);
        }
        return map;
    }

    private static string? Worse(string? a, string? b)
    {
        static int Rank(string? s) => s switch { "critical" => 4, "important" => 3, "moderate" => 2, "low" => 1, _ => 0 };
        return Rank(b) > Rank(a) ? b : a;
    }

    /// <summary><c>zypper list-updates</c> table: <c>v | repo | name | current | available | arch</c>.</summary>
    public static List<LinuxUpdate> ParseZypper(string output)
    {
        var list = new List<LinuxUpdate>();
        foreach (var raw in output.Split('\n'))
        {
            var cols = raw.Split('|').Select(c => c.Trim()).ToArray();
            if (cols.Length < 6 || cols[0] == "S" || cols[2] == "Name" || cols[2].Length == 0 || cols[2].StartsWith("-", StringComparison.Ordinal)) continue;
            list.Add(new LinuxUpdate { Package = cols[2], Repository = cols[1], Version = cols[4], Arch = cols[5] });
        }
        return list;
    }

    /// <summary>Applies the advisories (security, severity) to the upgrades.</summary>
    public static void Apply(List<LinuxUpdate> updates, Dictionary<string, (bool Security, string? Severity, string Advisory)> advisories)
    {
        foreach (var u in updates)
            if (advisories.TryGetValue(u.Package, out var a))
            {
                u.Security |= a.Security;
                u.Severity = a.Severity ?? u.Severity;
                u.Advisory = a.Advisory;
            }
    }
}
