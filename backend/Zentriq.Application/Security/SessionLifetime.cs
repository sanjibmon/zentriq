using System.Globalization;

namespace Zentriq.Application.Security;

/// <summary>
/// Console sessions: activity keeps a session alive (30 minutes sliding), but it ends <see cref="Max"/> after signing in,
/// however active it is. The sign-in time is kept in the session cookie's properties, which sliding renewal preserves.
/// </summary>
public static class SessionLifetime
{
    /// <summary>Key of the sign-in time (Unix seconds) in the cookie's authentication properties.</summary>
    public const string Item = "zq.signin";

    public static readonly TimeSpan Max = TimeSpan.FromHours(12);

    public static string Format(DateTimeOffset at) => at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Whether a session must end: signed in more than <see cref="Max"/> ago, or a sign-in time that cannot be read or lies
    /// in the future. A session without one (signed in before build 133) is not expired; <see cref="Stamp"/> starts its clock.
    /// </summary>
    public static bool Expired(IDictionary<string, string?> items, DateTimeOffset now)
    {
        if (!items.TryGetValue(Item, out var v) || v is null) return false;
        if (!long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var t)) return true;
        DateTimeOffset signedIn;
        try { signedIn = DateTimeOffset.FromUnixTimeSeconds(t); }
        catch (ArgumentOutOfRangeException) { return true; }
        return now - signedIn > Max || signedIn > now.AddMinutes(5);
    }

    /// <summary>Records the sign-in time when the session has none; true when it was added (the cookie must be renewed).</summary>
    public static bool Stamp(IDictionary<string, string?> items, DateTimeOffset now)
    {
        if (items.ContainsKey(Item)) return false;
        items[Item] = Format(now);
        return true;
    }
}
