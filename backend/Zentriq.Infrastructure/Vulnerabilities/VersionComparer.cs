using System.Globalization;
using System.Text.RegularExpressions;

namespace Zentriq.Infrastructure.Vulnerabilities;

/// <summary>
/// Compares software version strings the way NVD version ranges are meant: segment by segment, numbers numerically,
/// text case-insensitively, with pre-release tags (alpha, beta, rc, preview) sorting before the release.
/// "1.2" = "1.2.0", "10.0.19045" &gt; "10.0.9200", "2.0rc1" &lt; "2.0", "24.08" &gt; "24.07".
/// </summary>
public static partial class VersionComparer
{
    [GeneratedRegex(@"\d+|[a-zA-Z]+")]
    private static partial Regex Tokens();

    private static readonly string[] PreRelease = ["dev", "alpha", "a", "beta", "b", "pre", "preview", "rc", "cr"];

    public static int Compare(string? x, string? y)
    {
        var a = Tokenize(x);
        var b = Tokenize(y);
        var n = Math.Max(a.Count, b.Count);
        for (var i = 0; i < n; i++)
        {
            var ta = i < a.Count ? a[i] : null;
            var tb = i < b.Count ? b[i] : null;
            var c = CompareToken(ta, tb);
            if (c != 0) return c;
        }
        return 0;
    }

    private static int CompareToken(string? a, string? b)
    {
        if (a == b) return 0;
        // A missing segment equals 0 against a number, and is newer than a pre-release tag ("2.0" > "2.0rc1").
        if (a is null) return b is not null && IsNumber(b) ? (IsZero(b) ? 0 : -1) : (b is not null && IsPre(b) ? 1 : -1);
        if (b is null) return IsNumber(a) ? (IsZero(a) ? 0 : 1) : (IsPre(a) ? -1 : 1);
        var na = IsNumber(a);
        var nb = IsNumber(b);
        if (na && nb)
        {
            var ta = a.TrimStart('0');
            var tb = b.TrimStart('0');
            if (ta.Length != tb.Length) return ta.Length.CompareTo(tb.Length);
            return string.CompareOrdinal(ta, tb);
        }
        if (na) return IsPre(b) ? 1 : 1;      // 1.0.1 > 1.0.beta
        if (nb) return IsPre(a) ? -1 : -1;
        var pa = Array.IndexOf(PreRelease, a.ToLowerInvariant());
        var pb = Array.IndexOf(PreRelease, b.ToLowerInvariant());
        if (pa >= 0 && pb >= 0) return pa.CompareTo(pb);
        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNumber(string s) => s.Length > 0 && char.IsAsciiDigit(s[0]);
    private static bool IsZero(string s) => s.All(c => c == '0');
    private static bool IsPre(string s) => Array.IndexOf(PreRelease, s.ToLowerInvariant()) >= 0;

    private static List<string> Tokenize(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return [];
        return Tokens().Matches(v.Trim()).Select(m => m.Value).ToList();
    }

    /// <summary>True when the version contains at least one digit (comparable against NVD ranges).</summary>
    public static bool IsComparable(string? v) => !string.IsNullOrWhiteSpace(v) && v.Any(char.IsAsciiDigit);

    /// <summary>Does <paramref name="installed"/> fall within a CPE match rule?</summary>
    public static bool Matches(string installed, string? exact, string? startIncl, string? startExcl, string? endIncl, string? endExcl)
    {
        if (!IsComparable(installed)) return false;
        var ranged = startIncl is not null || startExcl is not null || endIncl is not null || endExcl is not null;
        if (!ranged)
        {
            // "*" = every version (NVD uses it when all versions are affected); "-" = not applicable.
            if (exact is null or "*") return true;
            if (exact == "-") return false;
            return Compare(installed, exact) == 0 || SamePrefix(installed, exact);
        }
        if (startIncl is not null && Compare(installed, startIncl) < 0) return false;
        if (startExcl is not null && Compare(installed, startExcl) <= 0) return false;
        if (endIncl is not null && Compare(installed, endIncl) > 0) return false;
        if (endExcl is not null && Compare(installed, endExcl) >= 0) return false;
        return true;
    }

    /// <summary>"3.0.21.0" installed vs an exact "3.0.21" rule (Windows often appends ".0").</summary>
    private static bool SamePrefix(string installed, string exact)
    {
        var a = Tokenize(installed);
        var b = Tokenize(exact);
        if (b.Count == 0 || a.Count < b.Count) return false;
        for (var i = 0; i < b.Count; i++) if (CompareToken(a[i], b[i]) != 0) return false;
        return a.Skip(b.Count).All(t => IsNumber(t) && IsZero(t));
    }

    internal static string Invariant(decimal d) => d.ToString(CultureInfo.InvariantCulture);
}
