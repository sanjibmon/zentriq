using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Zentriq.Application.Security;

/// <summary>Allowed sign-in hours in the server's time zone. <see cref="Days"/>: 0 = Sunday … 6 = Saturday.</summary>
public sealed record SigninHours(int[] Days, string From, string To)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static SigninHours? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<SigninHours>(json, Json); }
        catch (JsonException) { return null; }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public string? Validate()
    {
        if (Days is null || Days.Length == 0) return "Choose at least one day.";
        if (Days.Any(d => d is < 0 or > 6)) return "Days are 0 (Sunday) to 6 (Saturday).";
        if (!TryTime(From, out var f) || !TryTime(To, out var t)) return "Enter times as HH:mm, for example 08:00.";
        if (f == t) return "The start and end time must differ.";
        return null;
    }

    /// <summary>Whether <paramref name="local"/> (server time) is inside the hours. A range past midnight (22:00–06:00) belongs to the day it starts.</summary>
    public bool Allows(DateTime local)
    {
        if (!TryTime(From, out var f) || !TryTime(To, out var t)) return true;
        var now = TimeOnly.FromDateTime(local);
        var day = (int)local.DayOfWeek;
        if (f < t) return Days.Contains(day) && now >= f && now < t;
        // Overnight: from the start time on a listed day until the end time the next morning.
        if (now >= f) return Days.Contains(day);
        var yesterday = (day + 6) % 7;
        return now < t && Days.Contains(yesterday);
    }

    public string Describe()
    {
        var names = CultureInfo.InvariantCulture.DateTimeFormat;
        var days = Days.Length == 7 ? "every day" : string.Join(", ", Days.Order().Select(d => names.GetAbbreviatedDayName((DayOfWeek)d)));
        return $"{days}, {From}–{To}";
    }

    private static bool TryTime(string? s, out TimeOnly t) => TimeOnly.TryParseExact(s ?? "", "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out t);
}

/// <summary>
/// Per-user access rules checked at sign-in and on every request: account expiry, allowed sign-in hours and the
/// networks the user may sign in from.
/// </summary>
public sealed record AccessRules(DateTimeOffset? ExpiresAt, SigninHours? Hours, IReadOnlyList<string> Networks)
{
    public static readonly AccessRules None = new(null, null, []);

    /// <summary>Why access is refused now, or null when it is allowed.</summary>
    public string? Denied(DateTimeOffset now, IPAddress? ip, TimeZoneInfo zone)
    {
        if (ExpiresAt is { } e && now >= e) return "expired";
        if (Hours is { } h && !h.Allows(TimeZoneInfo.ConvertTime(now, zone).DateTime)) return "outside-hours";
        if (Networks.Count > 0 && (ip is null || !Networks.Any(n => Contains(n, ip)))) return "network";
        return null;
    }

    public static string Message(string reason) => reason switch
    {
        "expired" => "Your Zentriq access has expired. Ask a Zentriq administrator to extend it.",
        "outside-hours" => "You cannot sign in to Zentriq at this time (outside your allowed sign-in hours).",
        "network" => "You cannot sign in to Zentriq from this network.",
        _ => "You cannot sign in to Zentriq now.",
    };

    /// <summary>Normalises and checks a list of addresses or CIDR ranges; returns an error message or null.</summary>
    public static string? ValidateNetworks(IEnumerable<string> networks, out List<string> clean)
    {
        clean = [];
        foreach (var raw in networks)
        {
            var n = (raw ?? "").Trim();
            if (n.Length == 0) continue;
            if (!TryParse(n, out _, out _)) return $"'{n}' is not an IP address or a range such as 192.168.10.0/24.";
            if (!clean.Contains(n, StringComparer.OrdinalIgnoreCase)) clean.Add(n);
        }
        if (clean.Count > 50) return "At most 50 addresses or ranges.";
        return null;
    }

    public static bool Contains(string network, IPAddress ip)
    {
        if (!TryParse(network, out var net, out var bits)) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (net.IsIPv4MappedToIPv6) net = net.MapToIPv4();
        if (ip.AddressFamily != net.AddressFamily) return false;
        var a = ip.GetAddressBytes();
        var b = net.GetAddressBytes();
        for (var i = 0; i < a.Length && bits > 0; i++, bits -= 8)
        {
            var mask = bits >= 8 ? 0xFF : (byte)(0xFF << (8 - bits));
            if ((a[i] & mask) != (b[i] & mask)) return false;
        }
        return true;
    }

    private static bool TryParse(string s, out IPAddress net, out int bits)
    {
        net = IPAddress.None; bits = 0;
        var slash = s.IndexOf('/');
        var addr = slash < 0 ? s : s[..slash];
        if (!IPAddress.TryParse(addr, out var parsed)) return false;
        net = parsed;
        var max = parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        if (slash < 0) { bits = max; return true; }
        return int.TryParse(s[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out bits) && bits >= 0 && bits <= max;
    }
}
