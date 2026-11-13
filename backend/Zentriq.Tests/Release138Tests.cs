using Zentriq.Application.Security;

namespace Zentriq.Tests;

/// <summary>Build 138: sign-out after inactivity.</summary>
public sealed class Release138Tests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 8, 0, 0, TimeSpan.Zero);

    private static Dictionary<string, string?> SignedInAt(DateTimeOffset at) => new() { [IdleSignout.Item] = SessionLifetime.Format(at) };

    [Fact]
    public void Background_refreshes_do_not_keep_a_session_alive()
    {
        var items = SignedInAt(T0);
        // Pages refresh every minute; the user has not touched the console since signing in.
        for (var m = 1; m <= 10; m++)
            Assert.NotEqual(IdleSignout.Outcome.Expired, IdleSignout.Check(items, T0.AddMinutes(m), m * 60, 10));
        Assert.Equal(IdleSignout.Outcome.Expired, IdleSignout.Check(items, T0.AddMinutes(11), 11 * 60, 10));
    }

    [Fact]
    public void Activity_moves_the_clock_and_renews_the_cookie_at_most_once_a_minute()
    {
        var items = SignedInAt(T0);
        Assert.Equal(IdleSignout.Outcome.Active, IdleSignout.Check(items, T0.AddSeconds(30), 0, 5));
        Assert.Equal(IdleSignout.Outcome.Renew, IdleSignout.Check(items, T0.AddMinutes(4), 0, 5));
        Assert.Equal(SessionLifetime.Format(T0.AddMinutes(4)), items[IdleSignout.Item]);
        // 5 minutes after the last activity (plus the grace) the session ends, not 5 minutes after sign-in.
        Assert.NotEqual(IdleSignout.Outcome.Expired, IdleSignout.Check(items, T0.AddMinutes(9), 300, 5));
        Assert.Equal(IdleSignout.Outcome.Expired, IdleSignout.Check(items, T0.AddMinutes(9) + IdleSignout.Grace + TimeSpan.FromSeconds(1), 400, 5));
    }

    [Fact]
    public void A_request_without_the_header_counts_as_activity()
    {
        var items = SignedInAt(T0);
        Assert.Equal(IdleSignout.Outcome.Renew, IdleSignout.Check(items, T0.AddMinutes(4), null, 5));
        Assert.NotEqual(IdleSignout.Outcome.Expired, IdleSignout.Check(items, T0.AddMinutes(8), null, 5));
    }

    [Fact]
    public void Zero_means_never_and_old_sessions_start_their_clock()
    {
        var items = SignedInAt(T0);
        Assert.NotEqual(IdleSignout.Outcome.Expired, IdleSignout.Check(items, T0.AddHours(11), 11 * 3600, 0));
        var old = new Dictionary<string, string?>();
        Assert.Equal(IdleSignout.Outcome.Renew, IdleSignout.Check(old, T0, 9999, 5));
        Assert.Equal(SessionLifetime.Format(T0), old[IdleSignout.Item]);
        var bogus = new Dictionary<string, string?> { [IdleSignout.Item] = "x" };
        Assert.Equal(IdleSignout.Outcome.Renew, IdleSignout.Check(bogus, T0, 0, 5));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(720, true)]
    [InlineData(-1, false)]
    [InlineData(721, false)]
    public void Setting_range(int minutes, bool ok) => Assert.Equal(ok, IdleSignout.Validate(minutes) is null);

    [Theory]
    [InlineData("42", 42)]
    [InlineData("0", 0)]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("-5", null)]
    [InlineData("abc", null)]
    public void Header_parsing(string? value, int? expected) => Assert.Equal(expected, IdleSignout.ParseHeader(value));
}
