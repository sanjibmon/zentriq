using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Zentriq.Infrastructure.Database;
using Zentriq.Infrastructure.Reports;

namespace Zentriq.Tests;

public sealed class ReportPeriodTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 10, 30, 0, TimeSpan.Zero);   // a Friday

    [Theory]
    [InlineData("today", "2026-10-16", "2026-10-16")]
    [InlineData("yesterday", "2026-10-15", "2026-10-15")]
    [InlineData("last_7_days", "2026-10-10", "2026-10-16")]
    [InlineData("last_30_days", "2026-09-17", "2026-10-16")]
    [InlineData("this_week", "2026-10-12", "2026-10-18")]
    [InlineData("last_week", "2026-10-05", "2026-10-11")]
    [InlineData("this_month", "2026-10-01", "2026-10-31")]
    [InlineData("last_month", "2026-09-01", "2026-09-30")]
    [InlineData("this_quarter", "2026-10-01", "2026-12-31")]
    [InlineData("last_quarter", "2026-07-01", "2026-09-30")]
    [InlineData("this_year", "2026-01-01", "2026-12-31")]
    [InlineData("last_year", "2025-01-01", "2025-12-31")]
    public void Relative_ranges_resolve_to_calendar_days(string range, string first, string last)
    {
        var (f, l) = new ReportPeriod { Column = "x", Range = range }.Days(Now, TimeZoneInfo.Utc);
        Assert.Equal(DateOnly.Parse(first), f);
        Assert.Equal(DateOnly.Parse(last), l);
    }

    [Fact]
    public void Custom_dates_are_inclusive_and_use_the_time_zone()
    {
        var india = TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromHours(5.5), "IST", "IST");
        var p = new ReportPeriod { Column = "x", Range = "custom", From = "2026-09-01", To = "2026-09-30" };
        var (from, to) = p.Resolve(Now, india);
        Assert.Equal(new DateTimeOffset(2026, 8, 31, 18, 30, 0, TimeSpan.Zero), from);   // 1 Sep 00:00 IST
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 18, 30, 0, TimeSpan.Zero), to);     // 1 Oct 00:00 IST (exclusive)
        Assert.Equal("1 Sep 2026 – 30 Sep 2026", p.Describe(Now, india));
        Assert.Equal("from 1 Sep 2026", new ReportPeriod { Column = "x", From = "2026-09-01" }.Describe(Now, india));
        var (openFrom, openTo) = new ReportPeriod { Column = "x", To = "2026-09-30" }.Resolve(Now, TimeZoneInfo.Utc);
        Assert.Null(openFrom);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), openTo);
        // "Today" late in the evening in India is already the next day in UTC terms: the zone decides the day.
        var (todayFrom, _) = new ReportPeriod { Column = "x", Range = "today" }.Resolve(new DateTimeOffset(2026, 10, 16, 20, 0, 0, TimeSpan.Zero), india);
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 18, 30, 0, TimeSpan.Zero), todayFrom);
    }

    [Theory]
    [InlineData("hostname", "last_month", null, null, "not a date")]
    [InlineData("nope", "last_month", null, null, "Unknown date column")]
    [InlineData("first_seen", "fortnight", null, null, "Unknown time period")]
    [InlineData("first_seen", "custom", null, null, "From date, a To date or both")]
    [InlineData("first_seen", "custom", "01/09/2026", null, "yyyy-mm-dd")]
    [InlineData("first_seen", "custom", "2026-10-01", "2026-09-01", "after the To date")]
    public void Invalid_periods_are_refused(string column, string range, string? from, string? to, string message)
    {
        var ds = ReportCatalog.Find("assets")!;
        var spec = new ReportSpec { Columns = ["hostname"], Period = new ReportPeriod { Column = column, Range = range, From = from, To = to } };
        var ex = Assert.Throws<ReportValidationException>(() => ReportEngine.Validate(ds, spec));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void Period_becomes_parameters_never_sql_text()
    {
        var ds = ReportCatalog.Find("assets")!;
        var spec = new ReportSpec { Columns = ["hostname"], Period = new ReportPeriod { Column = "first_seen", Range = "custom", From = "2026-09-01", To = "2026-09-30" } };
        var (sql, count, ps, _) = ReportEngine.Build(ds, spec, 100, Now, TimeZoneInfo.Utc);
        Assert.Contains("t.\"first_seen\" >= @p0", sql);
        Assert.Contains("t.\"first_seen\" < @p1", sql);
        Assert.Contains("first_seen_at AS \"first_seen\"", sql);      // the period column is selected even when not shown
        Assert.DoesNotContain("2026", sql);
        Assert.Contains("@p1", count);
        Assert.Equal(2, ps.Count);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), ps[1].Value);
        Assert.Equal("first_seen", ReportEngine.DefaultPeriodColumn(ds));
        Assert.Equal("First seen: 1 Sep 2026 – 30 Sep 2026", ReportEngine.PeriodText("assets", spec, Now, TimeZoneInfo.Utc));
        Assert.Null(ReportEngine.PeriodText("assets", new ReportSpec { Columns = ["hostname"] }));
        // Saved reports keep the period.
        var round = ReportSpec.Parse(spec.ToJson());
        Assert.Equal("2026-09-30", round.Period!.To);
    }
}

public sealed class ReportPeriodDatabaseTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    public Task InitializeAsync() => _db.InitializeAsync();
    public Task DisposeAsync() => _db.DisposeAsync();

    [DbFact]
    public async Task Reports_only_include_records_in_the_period()
    {
        await new SqlMigrator(NullLogger<SqlMigrator>.Instance).MigrateAsync(_db.OwnerConnection, _db.App, "1.0.0-test");
        await using var ds = NpgsqlDataSource.Create(_db.AppConnection);
        await using (var c = ds.CreateCommand("""
            INSERT INTO zentriq.assets (hostname, category, first_seen_at) VALUES
              ('PER-AUG', 'endpoint', '2026-08-31 23:59:00+00'), ('PER-SEP1', 'endpoint', '2026-09-01 00:00:00+00'),
              ('PER-SEP2', 'endpoint', '2026-09-30 23:59:59+00'), ('PER-OCT', 'endpoint', '2026-10-01 00:00:00+00');
            """))
            await c.ExecuteNonQueryAsync();
        var now = new DateTimeOffset(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);
        async Task<string[]> Hosts(ReportPeriod? p)
        {
            var r = await ReportEngine.RunAsync(ds, "assets", new ReportSpec { Columns = ["hostname"], Filters = [new() { Column = "hostname", Op = "starts", Value = "PER-" }], Sort = [new() { Column = "hostname" }], Period = p }, 100, default, now, TimeZoneInfo.Utc);
            return r.Rows.Select(x => (string)x[0]!).ToArray();
        }
        Assert.Equal(new[] { "PER-AUG", "PER-OCT", "PER-SEP1", "PER-SEP2" }, await Hosts(null));
        Assert.Equal(new[] { "PER-SEP1", "PER-SEP2" }, await Hosts(new() { Column = "first_seen", Range = "last_month" }));
        Assert.Equal(new[] { "PER-SEP1", "PER-SEP2" }, await Hosts(new() { Column = "first_seen", Range = "custom", From = "2026-09-01", To = "2026-09-30" }));
        Assert.Equal(new[] { "PER-OCT", "PER-SEP2" }, await Hosts(new() { Column = "first_seen", Range = "custom", From = "2026-09-30" }));
        Assert.Equal(new[] { "PER-AUG" }, await Hosts(new() { Column = "first_seen", Range = "custom", To = "2026-08-31" }));
        // Summary reports honour the period too.
        var grouped = await ReportEngine.RunAsync(ds, "assets", new ReportSpec { Columns = [], GroupBy = ["category"], Filters = [new() { Column = "hostname", Op = "starts", Value = "PER-" }], Period = new() { Column = "first_seen", Range = "this_month" } }, 100, default, now, TimeZoneInfo.Utc);
        Assert.Equal(1L, Assert.Single(grouped.Rows)[1]);
    }
}
