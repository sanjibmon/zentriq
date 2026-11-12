using Zentriq.Application.Security;

namespace Zentriq.Tests;

/// <summary>Build 133 (go-live hardening): console sessions end 12 hours after signing in.</summary>
public sealed class Release133Tests
{
    private static Dictionary<string, string?> SignedIn(DateTimeOffset at) => new() { [SessionLifetime.Item] = SessionLifetime.Format(at) };

    [Fact]
    public void Session_ends_twelve_hours_after_sign_in_however_active()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.False(SessionLifetime.Expired(SignedIn(now.AddMinutes(-5)), now));
        Assert.False(SessionLifetime.Expired(SignedIn(now.AddHours(-11.9)), now));
        Assert.True(SessionLifetime.Expired(SignedIn(now.AddHours(-12.1)), now));
        Assert.True(SessionLifetime.Expired(SignedIn(now.AddDays(-3)), now));
    }

    [Fact]
    public void Unreadable_or_future_sign_in_time_ends_the_session()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(SessionLifetime.Expired(new Dictionary<string, string?> { [SessionLifetime.Item] = "not-a-time" }, now));
        Assert.True(SessionLifetime.Expired(new Dictionary<string, string?> { [SessionLifetime.Item] = "-5" }, now));
        Assert.True(SessionLifetime.Expired(new Dictionary<string, string?> { [SessionLifetime.Item] = "99999999999999999" }, now));
        Assert.True(SessionLifetime.Expired(SignedIn(now.AddHours(2)), now));
    }

    [Fact]
    public void Session_from_before_the_upgrade_gets_a_sign_in_time_once()
    {
        var now = DateTimeOffset.UtcNow;
        var old = new Dictionary<string, string?>();
        Assert.False(SessionLifetime.Expired(old, now));
        Assert.True(SessionLifetime.Stamp(old, now));
        Assert.False(SessionLifetime.Stamp(old, now.AddMinutes(1)));
        Assert.False(SessionLifetime.Expired(old, now.AddHours(11)));
        Assert.True(SessionLifetime.Expired(old, now.AddHours(13)));
    }
}
