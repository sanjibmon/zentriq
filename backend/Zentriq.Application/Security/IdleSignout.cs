using System.Globalization;

namespace Zentriq.Application.Security;

/// <summary>
/// Sign-out after inactivity (Settings › Security). Background refreshes of console pages are not activity: the console
/// sends the seconds since the user last typed, clicked or scrolled (in any of its tabs) with every request in
/// <see cref="Header"/>, and the session remembers the time of the last real activity in its cookie. A request without the
/// header (a file download, a script with the session cookie) counts as activity.
/// </summary>
public static class IdleSignout
{
    /// <summary>Setting key: minutes without activity before a console session ends; 0 = never (the 12-hour limit remains).</summary>
    public const string Setting = "security.idle_signout_minutes";
    public const int Default = 30;
    public const int MaxMinutes = 720;

    /// <summary>Request header: whole seconds since the user's last activity in the console.</summary>
    public const string Header = "X-Zentriq-Idle";

    /// <summary>Key of the last activity (Unix seconds) in the cookie's authentication properties.</summary>
    public const string Item = "zq.active";

    /// <summary>Allowance for the request that answers the console's warning arriving just at the limit.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(20);

    /// <summary>The cookie is rewritten at most this often when the last activity moves on.</summary>
    public static readonly TimeSpan RenewEvery = TimeSpan.FromSeconds(60);

    public static string? Validate(int minutes) =>
        minutes is >= 0 and <= MaxMinutes ? null : $"Must be between 0 (never) and {MaxMinutes} minutes.";

    /// <summary>Seconds since the last activity from the request header; null when absent or unreadable (counts as activity).</summary>
    public static int? ParseHeader(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? Math.Min(s, 7 * 24 * 3600) : null;

    public enum Outcome { Active, Renew, Expired }

    /// <summary>
    /// Checks a session at a request and updates its last activity. Expired: no activity for longer than the setting
    /// (plus <see cref="Grace"/>). Renew: the last activity moved on far enough that the cookie should be rewritten.
    /// </summary>
    public static Outcome Check(IDictionary<string, string?> items, DateTimeOffset now, int? idleSeconds, int minutes)
    {
        var activity = now - TimeSpan.FromSeconds(Math.Max(0, idleSeconds ?? 0));
        DateTimeOffset? last = null;
        if (items.TryGetValue(Item, out var v) && long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var t))
        {
            try { last = DateTimeOffset.FromUnixTimeSeconds(t); } catch (ArgumentOutOfRangeException) { last = null; }
        }
        // A session without one (signed in before build 138, or an unreadable value) starts its clock now.
        if (last is null || last > now.AddMinutes(5))
        {
            items[Item] = SessionLifetime.Format(now);
            return Outcome.Renew;
        }
        var newest = activity > last.Value ? activity : last.Value;
        if (minutes > 0 && now - newest > TimeSpan.FromMinutes(minutes) + Grace) return Outcome.Expired;
        if (newest - last.Value >= RenewEvery)
        {
            items[Item] = SessionLifetime.Format(newest);
            return Outcome.Renew;
        }
        return Outcome.Active;
    }
}
