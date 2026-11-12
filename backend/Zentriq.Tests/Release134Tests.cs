using System.Text.Json;
using Zentriq.Application;

namespace Zentriq.Tests;

/// <summary>Build 134: times with a local offset (India +05:30) reach PostgreSQL as UTC.</summary>
public sealed class Release134Tests
{
    private sealed record Body(DateTimeOffset At, DateTimeOffset? Maybe);

    private static readonly JsonSerializerOptions Options = UtcDateTimeOffsetConverter.AddTo(new(JsonSerializerDefaults.Web));

    [Fact]
    public void Time_with_india_offset_is_read_as_the_same_instant_in_utc()
    {
        var b = JsonSerializer.Deserialize<Body>("""{"at":"2026-11-12T17:18:00+05:30","maybe":"2026-11-12T17:18:00.123+05:30"}""", Options)!;
        Assert.Equal(TimeSpan.Zero, b.At.Offset);
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 11, 48, 0, TimeSpan.Zero), b.At);
        Assert.Equal(TimeSpan.Zero, b.Maybe!.Value.Offset);
    }

    [Fact]
    public void Time_without_offset_is_taken_as_utc_not_the_server_local_time()
    {
        var b = JsonSerializer.Deserialize<Body>("""{"at":"2026-12-31","maybe":null}""", Options)!;
        Assert.Equal(new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero), b.At);
        Assert.Null(b.Maybe);
        var c = JsonSerializer.Deserialize<Body>("""{"at":"2026-12-31T10:00:00"}""", Options)!;
        Assert.Equal(new DateTimeOffset(2026, 12, 31, 10, 0, 0, TimeSpan.Zero), c.At);
    }

    [Fact]
    public void Written_as_utc_and_bad_values_are_refused()
    {
        var json = JsonSerializer.Serialize(new Body(new DateTimeOffset(2026, 11, 12, 17, 18, 0, TimeSpan.FromMinutes(330)), null), Options);
        Assert.Contains("2026-11-12T11:48:00+00:00", json);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Body>("""{"at":"not a date"}""", Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Body>("""{"at":12345}""", Options));
    }

    [Fact]
    public void Active_directory_file_time_is_utc()
    {
        // lastLogonTimestamp / pwdLastSet: FromFileTime gives the server's local offset; ToUniversalTime keeps the instant.
        var ft = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero).ToFileTime();
        var v = DateTimeOffset.FromFileTime(ft).ToUniversalTime();
        Assert.Equal(TimeSpan.Zero, v.Offset);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero), v);
    }
}
