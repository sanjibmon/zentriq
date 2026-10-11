using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Zentriq.Application.Discovery;

/// <summary>An inclusive IPv4 range, or a single hostname to be resolved at scan time.</summary>
public sealed record TargetItem(string Source, uint Start, uint End, string? Hostname)
{
    public bool IsHost => Hostname is not null;
    public long Count => IsHost ? 1 : (long)End - Start + 1;
}

public sealed record ScanSafeguards(
    IReadOnlyList<string> ApprovedScopes,
    int MaxTargets,
    int MinPrefix);

public sealed record ParsedTargets(
    IReadOnlyList<TargetItem> Items,
    IReadOnlyList<TargetItem> Exclusions,
    IReadOnlyList<string> Errors,
    long TotalAddresses)
{
    public bool IsValid => Errors.Count == 0 && Items.Count > 0;
}

/// <summary>
/// Parses scan targets: single IPv4 addresses, CIDR networks (a.b.c.d/nn), ranges
/// (a.b.c.d-e.f.g.h or a.b.c.d-N) and hostnames. Enforces approved scopes, the broadest
/// accepted prefix and the per-scan target limit. Hostnames are checked against scope after
/// resolution by the worker.
/// </summary>
public static partial class TargetParser
{
    [GeneratedRegex(@"^(?=.{1,253}$)(?!-)[A-Za-z0-9-]{1,63}(?<!-)(\.(?!-)[A-Za-z0-9-]{1,63}(?<!-))*$")]
    private static partial Regex HostnameRegex();

    public static IEnumerable<string> SplitEntries(string? text) =>
        (text ?? string.Empty)
            .Split(['\n', '\r', ',', ';', '\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(e => !e.StartsWith('#'));

    public static ParsedTargets Parse(string? targetsText, string? excludeText, ScanSafeguards safeguards)
    {
        var errors = new List<string>();
        var items = new List<TargetItem>();
        var excl = new List<TargetItem>();

        foreach (var entry in SplitEntries(targetsText))
        {
            if (TryParseEntry(entry, safeguards.MinPrefix, out var item, out var err)) items.Add(item!);
            else errors.Add(err!);
        }
        foreach (var entry in SplitEntries(excludeText))
        {
            if (TryParseEntry(entry, 0, out var item, out var err))
            {
                if (item!.IsHost) errors.Add($"Exclusion '{entry}': only IP addresses, ranges and CIDR networks can be excluded.");
                else excl.Add(item);
            }
            else errors.Add("Exclusion " + err);
        }

        if (items.Count == 0 && errors.Count == 0) errors.Add("Enter at least one target.");

        var scopes = safeguards.ApprovedScopes
            .Select(s => TryParseCidr(s, 0, out var r, out _) ? r : null)
            .Where(r => r is not null).Cast<TargetItem>().ToList();
        foreach (var it in items.Where(i => !i.IsHost))
        {
            if (!InScope(it, scopes))
                errors.Add($"'{it.Source}' is outside the approved network scopes. An administrator can change scopes in System Settings.");
        }

        long total = items.Sum(i => i.Count) - CountExcluded(items, excl);
        if (total > safeguards.MaxTargets)
            errors.Add($"This scan covers {total:N0} addresses, above the limit of {safeguards.MaxTargets:N0} per scan. Split it into smaller scans.");

        return new ParsedTargets(items, excl, errors, Math.Max(0, total));
    }

    public static bool InScope(TargetItem item, IReadOnlyList<TargetItem> scopes) =>
        scopes.Any(s => item.Start >= s.Start && item.End <= s.End);

    public static bool AddressInScope(IPAddress ip, IReadOnlyList<string> approvedScopes)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var n = ToUInt(ip);
        return approvedScopes.Any(s => TryParseCidr(s, 0, out var r, out _) && n >= r!.Start && n <= r.End);
    }

    /// <summary>Enumerates individual targets (IPs as strings, hostnames as-is) minus exclusions, capped.</summary>
    public static IEnumerable<string> Expand(ParsedTargets parsed, int cap)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var produced = 0;
        foreach (var it in parsed.Items)
        {
            if (it.IsHost)
            {
                if (seen.Add(it.Hostname!)) { yield return it.Hostname!; if (++produced >= cap) yield break; }
                continue;
            }
            for (ulong n = it.Start; n <= it.End; n++)
            {
                var u = (uint)n;
                if (parsed.Exclusions.Any(e => u >= e.Start && u <= e.End)) continue;
                var s = FromUInt(u);
                if (seen.Add(s)) { yield return s; if (++produced >= cap) yield break; }
            }
        }
    }

    public static bool TryParseEntry(string entry, int minPrefix, out TargetItem? item, out string? error)
    {
        item = null; error = null;
        if (entry.Contains('/'))
            return TryParseCidr(entry, minPrefix, out item, out error);
        if (entry.Contains('-') && TryParseIp(entry.Split('-')[0], out _))
            return TryParseRange(entry, out item, out error);
        if (TryParseIp(entry, out var ip))
        {
            item = new TargetItem(entry, ip, ip, null);
            return true;
        }
        if (HostnameRegex().IsMatch(entry) && !entry.All(c => char.IsDigit(c) || c == '.'))
        {
            item = new TargetItem(entry, 0, 0, entry.ToLowerInvariant());
            return true;
        }
        error = $"'{entry}' is not a valid IP address, CIDR network, range or hostname.";
        return false;
    }

    public static bool TryParseCidr(string entry, int minPrefix, out TargetItem? item, out string? error)
    {
        item = null; error = null;
        var parts = entry.Split('/');
        if (parts.Length != 2 || !TryParseIp(parts[0], out var addr) || !int.TryParse(parts[1], out var bits) || bits is < 0 or > 32)
        {
            error = $"'{entry}' is not a valid CIDR network (example: 10.10.10.0/24).";
            return false;
        }
        if (bits < minPrefix)
        {
            error = $"'{entry}' is broader than /{minPrefix}, the broadest network accepted in one scan.";
            return false;
        }
        var mask = bits == 0 ? 0u : uint.MaxValue << (32 - bits);
        var net = addr & mask;
        var bc = net | ~mask;
        if (net != addr)
        {
            error = $"'{entry}' is not aligned to its prefix; did you mean {FromUInt(net)}/{bits}?";
            return false;
        }
        // Skip network and broadcast addresses for prefixes that have them.
        var start = bits < 31 ? net + 1 : net;
        var end = bits < 31 ? bc - 1 : bc;
        item = new TargetItem(entry, start, end, null);
        return true;
    }

    private static bool TryParseRange(string entry, out TargetItem? item, out string? error)
    {
        item = null; error = null;
        var parts = entry.Split('-', 2);
        if (!TryParseIp(parts[0], out var a))
        {
            error = $"'{entry}' is not a valid range.";
            return false;
        }
        uint b;
        if (TryParseIp(parts[1], out var full)) b = full;
        else if (byte.TryParse(parts[1], out var lastOctet)) b = (a & 0xFFFFFF00u) | lastOctet;
        else
        {
            error = $"'{entry}' is not a valid range (examples: 10.0.0.1-10.0.0.50 or 10.0.0.1-50).";
            return false;
        }
        if (b < a)
        {
            error = $"'{entry}': the range end is before its start.";
            return false;
        }
        item = new TargetItem(entry, a, b, null);
        return true;
    }

    private static bool TryParseIp(string s, out uint value)
    {
        value = 0;
        var parts = s.Split('.');
        if (parts.Length != 4) return false;
        foreach (var p in parts)
        {
            if (p.Length is 0 or > 3 || !p.All(char.IsDigit) || !byte.TryParse(p, out var b)) return false;
            if (p.Length > 1 && p[0] == '0') return false; // reject octal-looking octets
            value = (value << 8) | b;
        }
        return true;
    }

    private static long CountExcluded(IReadOnlyList<TargetItem> items, IReadOnlyList<TargetItem> excl)
    {
        long n = 0;
        foreach (var it in items.Where(i => !i.IsHost))
            foreach (var e in excl)
            {
                var s = Math.Max(it.Start, e.Start);
                var en = Math.Min(it.End, e.End);
                if (en >= s) n += (long)en - s + 1;
            }
        return n;
    }

    public static uint ToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    public static string FromUInt(uint n) => $"{n >> 24}.{(n >> 16) & 255}.{(n >> 8) & 255}.{n & 255}";
}
