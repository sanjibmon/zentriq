#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Zentriq.Inventory
{
    /// <summary>
    /// Application control policy for one device, as the server compiles it and the agent caches it (Windows and Linux).
    /// </summary>
    public sealed class AppControlPolicy
    {
        /// <summary>Changes whenever an administrator changes the approved list, a group or the settings.</summary>
        [JsonPropertyName("revision")] public long Revision { get; set; }
        /// <summary>off: nothing is checked · audit: installations of software that is not approved are reported · block: they are stopped with the message.</summary>
        [JsonPropertyName("mode")] public string Mode { get; set; } = "off";
        [JsonPropertyName("message")] public string Message { get; set; } = AppControl.DefaultMessage;
        /// <summary>The message offers "Request approval".</summary>
        [JsonPropertyName("allowRequests")] public bool AllowRequests { get; set; } = true;
        /// <summary>Software that is not approved and is found installed anyway is removed (block mode only).</summary>
        [JsonPropertyName("autoRemove")] public bool AutoRemove { get; set; }
        /// <summary>Windows Defender Application Control on this device: off, audit or enforce (Windows only).</summary>
        [JsonPropertyName("wdac")] public string Wdac { get; set; } = "off";
        /// <summary>The remote support agent is protected like the Zentriq Agent (uninstall, stop and disable need a code).</summary>
        [JsonPropertyName("protectRemoteAgent")] public bool ProtectRemoteAgent { get; set; } = true;
        [JsonPropertyName("rules")] public List<AppControlRule> Rules { get; set; } = new List<AppControlRule>();
        [JsonPropertyName("generatedAt")] public DateTimeOffset GeneratedAt { get; set; }
    }

    public sealed class AppControlRule
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        /// <summary>allow | block</summary>
        [JsonPropertyName("action")] public string Action { get; set; } = "allow";
        [JsonPropertyName("label")] public string? Label { get; set; }
        /// <summary>Software name; * and ? wildcards, not case-sensitive. Empty: any name.</summary>
        [JsonPropertyName("name")] public string? Name { get; set; }
        /// <summary>Publisher or code-signing certificate subject; wildcards. Empty: any publisher.</summary>
        [JsonPropertyName("publisher")] public string? Publisher { get; set; }
        [JsonPropertyName("versionMin")] public string? VersionMin { get; set; }
        [JsonPropertyName("versionMax")] public string? VersionMax { get; set; }
        /// <summary>SHA-256 of the installer file (lower-case hex).</summary>
        [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
        /// <summary>windows | linux | empty (both).</summary>
        [JsonPropertyName("platform")] public string? Platform { get; set; }
    }

    /// <summary>Software about to be installed (or found installed).</summary>
    public sealed class SoftwareCandidate
    {
        public string Name { get; set; } = "";
        /// <summary>Other names the installer goes by (product name, file description, file name without "setup" and version).</summary>
        public List<string> OtherNames { get; set; } = new List<string>();
        public string? Publisher { get; set; }
        /// <summary>Subject (CN or O) of the installer's code-signing certificate (Windows), when signed and valid.</summary>
        public string? Signer { get; set; }
        public string? Version { get; set; }
        public string? Sha256 { get; set; }
        public string Platform { get; set; } = "windows";
        /// <summary>Linux: the version installed now (the installation is an update of an installed package).</summary>
        public string? InstalledVersion { get; set; }
        /// <summary>Linux: the package's section (libs, kernel, …) when known.</summary>
        public string? Section { get; set; }
        public string? Path { get; set; }
        /// <summary>Everything the device knows about the installer (file, version resource, signature, MSI codes,
        /// where it was downloaded from, what started it), shown with events and approval requests.</summary>
        public Dictionary<string, string> Details { get; set; } = new Dictionary<string, string>();

        public override string ToString() =>
            Name + (string.IsNullOrEmpty(Version) ? "" : " " + Version) + (string.IsNullOrEmpty(Publisher ?? Signer) ? "" : " (" + (Publisher ?? Signer) + ")");
    }

    public sealed class AppDecision
    {
        public bool Approved { get; set; }
        /// <summary>approved | built-in | blocked | not-approved</summary>
        public string Outcome { get; set; } = "not-approved";
        public string? RuleId { get; set; }
        public string Reason { get; set; } = "";
    }

    /// <summary>
    /// The decision whether software may be installed, the same on the server (inventory, reports) and in the agents:
    /// <list type="number">
    /// <item>Always allowed (built in): Windows components and updates, drivers, Microsoft runtimes, the Zentriq Agent and the
    /// remote support agent; on Linux also updates of installed packages, kernels, firmware and libraries.</item>
    /// <item>A block rule that matches: blocked (even when an allow rule matches too).</item>
    /// <item>An allow rule that matches: approved.</item>
    /// <item>Otherwise: not approved.</item>
    /// </list>
    /// A rule matches when every part it sets matches: name, publisher (or signer), version range, installer SHA-256, platform.
    /// </summary>
    public static class AppControl
    {
        public const string DefaultMessage = "This is unauthorized software. Please contact your administrator for further installation.";
        public static readonly string[] Modes = { "off", "audit", "block" };
        public static readonly string[] WdacModes = { "off", "audit", "enforce" };

        /// <summary>The stricter of two modes (block &gt; audit &gt; off).</summary>
        public static string Stricter(string? a, string? b) => Rank(a, Modes) >= Rank(b, Modes) ? (a ?? "off") : (b ?? "off");
        public static string StricterWdac(string? a, string? b) => Rank(a, WdacModes) >= Rank(b, WdacModes) ? (a ?? "off") : (b ?? "off");
        private static int Rank(string? v, string[] order) { var i = Array.IndexOf(order, v ?? "off"); return i < 0 ? 0 : i; }

        public static AppDecision Evaluate(AppControlPolicy policy, SoftwareCandidate c)
        {
            if (BuiltIn(c) is { } why) return new AppDecision { Approved = true, Outcome = "built-in", Reason = why };
            foreach (var r in policy.Rules.Where(r => r.Action == "block"))
                if (Matches(r, c)) return new AppDecision { Approved = false, Outcome = "blocked", RuleId = r.Id, Reason = "Blocked by rule '" + (r.Label ?? r.Name ?? r.Publisher ?? r.Sha256) + "'." };
            foreach (var r in policy.Rules.Where(r => r.Action != "block"))
                if (Matches(r, c)) return new AppDecision { Approved = true, Outcome = "approved", RuleId = r.Id, Reason = "Approved by '" + (r.Label ?? r.Name ?? r.Publisher ?? r.Sha256) + "'." };
            return new AppDecision { Approved = false, Outcome = "not-approved", Reason = "Not on the approved software list." };
        }

        public static bool Matches(AppControlRule r, SoftwareCandidate c)
        {
            if (string.IsNullOrWhiteSpace(r.Name) && string.IsNullOrWhiteSpace(r.Publisher) && string.IsNullOrWhiteSpace(r.Sha256)) return false;
            if (!string.IsNullOrEmpty(r.Platform) && !string.Equals(r.Platform, c.Platform, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrWhiteSpace(r.Sha256) && !string.Equals(r.Sha256!.Trim(), c.Sha256 ?? "", StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrWhiteSpace(r.Name) && !Glob(r.Name!, c.Name) && !c.OtherNames.Any(n => Glob(r.Name!, n))) return false;
            if (!string.IsNullOrWhiteSpace(r.Publisher) && !(Glob(r.Publisher!, c.Publisher) || Glob(r.Publisher!, c.Signer))) return false;
            if (!string.IsNullOrWhiteSpace(r.VersionMin) && (string.IsNullOrEmpty(c.Version) || CompareVersions(c.Version, r.VersionMin) < 0)) return false;
            if (!string.IsNullOrWhiteSpace(r.VersionMax) && (string.IsNullOrEmpty(c.Version) || CompareVersions(c.Version, r.VersionMax) > 0)) return false;
            return true;
        }

        private static readonly string[] WindowsBuiltInPublishers = { "Microsoft Windows", "Microsoft Windows Publisher", "Microsoft Windows Hardware Compatibility Publisher", "Zentriq*" };
        private static readonly string[] WindowsBuiltInNames =
        {
            "Security Update for *", "Update for *", "Hotfix for *", "KB*", "Windows Driver Package*", "Microsoft Visual C++ * Redistributable*",
            "Microsoft .NET*Runtime*", "Microsoft Windows Desktop Runtime*", "Microsoft ASP.NET Core*Shared Framework*", "Microsoft Edge Update*",
            "Microsoft Edge WebView2 Runtime*", "Microsoft Update Health Tools", "Windows Malicious Software Removal Tool*", "Zentriq*",
            "Mesh Agent*", "MeshCentral*", "Zentriq Remote Support*",
        };
        private static readonly string[] LinuxBuiltInNames =
        {
            "zentriq-agent", "meshagent", "linux-image*", "linux-headers*", "linux-modules*", "linux-firmware*", "*-firmware", "firmware-*",
            "kernel", "kernel-*", "*-libs", "tzdata", "ca-certificates*", "base-files", "*-keyring", "gpg-pubkey",
        };

        /// <summary>Shared libraries by Debian / RPM naming (libssl3, libc6, libglib2.0-0, libstdc++6), not programs such as libreoffice.</summary>
        private static readonly Regex LinuxLibrary = new Regex("^lib[a-z0-9+_.-]*[0-9][a-z0-9+_.-]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Why the software is always allowed, or null.</summary>
        public static string? BuiltIn(SoftwareCandidate c)
        {
            if (c.Platform == "linux")
            {
                if (!string.IsNullOrEmpty(c.InstalledVersion)) return "Update of an installed package.";
                if (LinuxBuiltInNames.Any(p => Glob(p, c.Name)) || LinuxLibrary.IsMatch(c.Name)) return "System package (kernel, firmware, library or the Zentriq agents).";
                if (c.Section is { } s && (s == "libs" || s == "oldlibs" || s == "kernel" || s.EndsWith("/libs", StringComparison.Ordinal) || s.EndsWith("/kernel", StringComparison.Ordinal))) return "System package (" + s + ").";
                return null;
            }
            if (WindowsBuiltInPublishers.Any(p => Glob(p, c.Signer) || Glob(p, c.Publisher))) return "Windows component, driver or Zentriq.";
            if (WindowsBuiltInNames.Any(p => Glob(p, c.Name)) && (IsMicrosoftOrZentriq(c.Publisher) || IsMicrosoftOrZentriq(c.Signer) || c.Name.StartsWith("KB", StringComparison.OrdinalIgnoreCase)))
                return "Windows update, Microsoft runtime or Zentriq component.";
            return null;
        }

        private static bool IsMicrosoftOrZentriq(string? p) =>
            !string.IsNullOrEmpty(p) && (p!.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) || p.StartsWith("Zentriq", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Wildcard match (* any characters, ? one), not case-sensitive, whole value. Several patterns can be given separated
        /// by ";" (any of them).
        /// </summary>
        public static bool Glob(string pattern, string? value)
        {
            if (value is null) return false;
            if (pattern.IndexOf(';') >= 0) return pattern.Split(';').Any(p => p.Trim().Length > 0 && Glob(p, value));
            var p = pattern.Trim();
            if (p.Length == 0) return false;
            if (p.IndexOf('*') < 0 && p.IndexOf('?') < 0) return string.Equals(p, value.Trim(), StringComparison.OrdinalIgnoreCase);
            var rx = "^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(value.Trim(), rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromMilliseconds(100));
        }

        /// <summary>Compares versions part by part ("1.10.2" &gt; "1.9"; Debian epochs "1:2.3-4" and suffixes are handled loosely).</summary>
        public static int CompareVersions(string? a, string? b)
        {
            var pa = Parts(a); var pb = Parts(b);
            for (var i = 0; i < Math.Max(pa.Count, pb.Count); i++)
            {
                var x = i < pa.Count ? pa[i] : "0"; var y = i < pb.Count ? pb[i] : "0";
                var xn = long.TryParse(x, out var xi); var yn = long.TryParse(y, out var yi);
                int c = xn && yn ? xi.CompareTo(yi) : xn ? 1 : yn ? -1 : string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c < 0 ? -1 : 1;
            }
            return 0;
        }

        private static List<string> Parts(string? v)
        {
            var s = (v ?? "").Trim();
            var colon = s.IndexOf(':');
            if (colon > 0 && colon < 5 && s.Substring(0, colon).All(char.IsDigit)) s = s.Substring(colon + 1);   // Debian epoch
            return Regex.Split(s, "[^0-9A-Za-z]+").Where(x => x.Length > 0)
                .SelectMany(x => Regex.Matches(x, "[0-9]+|[A-Za-z]+").Cast<Match>().Select(m => m.Value)).ToList();
        }

        /// <summary>The first CN= (or O=) of a certificate subject: "CN=Google LLC, O=Google LLC, …" → "Google LLC".</summary>
        public static string? SubjectName(string? subject)
        {
            if (string.IsNullOrWhiteSpace(subject)) return null;
            foreach (var key in new[] { "CN=", "O=" })
            {
                var m = Regex.Match(subject!, "(?:^|,\\s*)" + key + "(\"[^\"]*\"|[^,]*)");
                if (m.Success) return m.Groups[1].Value.Trim().Trim('"');
            }
            return subject!.Trim();
        }

        private static readonly Regex InstallerWords = new Regex(@"(?i)[\s._-]*(setup|installer|install|x64|x86|amd64|arm64|win64|win32|64-?bit|32-?bit|full|offline|online|stub|web|en-us|v?\d+([._]\d+)*)\b", RegexOptions.CultureInvariant);

        /// <summary>"Firefox Setup 128.0.exe" → "Firefox"; "npp.8.6.Installer.x64.exe" → "npp".</summary>
        public static string? InstallerBaseName(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            var n = System.IO.Path.GetFileNameWithoutExtension(fileName!.Trim());
            var cleaned = InstallerWords.Replace(n, " ");
            cleaned = Regex.Replace(cleaned, "[\\s._-]+", " ").Trim();
            return cleaned.Length >= 2 ? cleaned : null;
        }

        /// <summary>A short text for logs and the console: "Google Chrome 120.0 (Google LLC)".</summary>
        public static string Describe(SoftwareCandidate c) => c.ToString();

        /// <summary>Normalises a SHA-256 given by a user (spaces, upper case): lower-case hex or null.</summary>
        public static string? NormalizeSha256(string? v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            var sb = new StringBuilder();
            foreach (var ch in v!.Trim()) { if (ch == ' ' || ch == ':' || ch == '-') continue; sb.Append(char.ToLowerInvariant(ch)); }
            var s = sb.ToString();
            return s.Length == 64 && s.All(ch => (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f')) ? s : null;
        }
    }
}
