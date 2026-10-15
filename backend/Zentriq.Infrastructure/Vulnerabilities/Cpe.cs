using System.Text;

namespace Zentriq.Infrastructure.Vulnerabilities;

/// <summary>CPE 2.3 formatted strings: cpe:2.3:part:vendor:product:version:update:edition:language:sw_edition:target_sw:target_hw:other.</summary>
public sealed record Cpe(string Part, string Vendor, string Product, string Version, string Update, string TargetSoftware)
{
    /// <summary>Parses a formatted string; components are unescaped ("notepad\+\+" → "notepad++").</summary>
    public static Cpe? Parse(string? s)
    {
        if (string.IsNullOrEmpty(s) || !s.StartsWith("cpe:2.3:", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = new List<string>();
        var sb = new StringBuilder();
        for (var i = 8; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < s.Length) { sb.Append(s[++i]); continue; }
            if (c == ':') { parts.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(c);
        }
        parts.Add(sb.ToString());
        if (parts.Count < 4) return null;
        string P(int i) => i < parts.Count ? parts[i] : "*";
        return new Cpe(P(0), P(1).ToLowerInvariant(), P(2).ToLowerInvariant(), P(3), P(4), P(8));
    }

    /// <summary>Escapes a component for a formatted string (letters, digits, '_', '-' and '.' stay as they are).</summary>
    public static string Escape(string component)
    {
        var sb = new StringBuilder();
        foreach (var c in component)
        {
            if (char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-' || c == '.') sb.Append(c);
            else sb.Append('\\').Append(c);
        }
        return sb.ToString();
    }

    public static string MatchString(string vendor, string product) => $"cpe:2.3:a:{Escape(vendor)}:{Escape(product)}";

    /// <summary>A vendor or product as entered by an administrator: lower-case, spaces as underscores.</summary>
    public static string Normalize(string s) => s.Trim().ToLowerInvariant().Replace(' ', '_');
}
