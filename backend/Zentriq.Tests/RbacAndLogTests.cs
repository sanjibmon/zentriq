using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Zentriq.Domain;
using Zentriq.Infrastructure.Database;
using Zentriq.Infrastructure.Logs;
using Zentriq.Infrastructure.Reports;

namespace Zentriq.Tests;

public sealed class RbacAndLogTests
{
    [Fact]
    public void Report_sources_need_the_permission_of_their_area()
    {
        string P(string key) => ReportCatalog.RequiredPermission(ReportCatalog.Find(key)!);
        Assert.Equal(Permissions.NonItView, P("nonit_assets"));
        Assert.Equal(Permissions.AccessoriesView, P("accessory_stock"));
        Assert.Equal(Permissions.AccessoriesView, P("accessory_movements"));
        Assert.Equal(Permissions.LifecycleView, P("asset_lifecycle"));
        Assert.Equal(Permissions.CloudView, P("cloud_findings"));
        Assert.Equal(Permissions.ActivityView, P("activity"));
        Assert.Equal(Permissions.AssetsView, P("assets"));
        Assert.Equal(Permissions.AuditView, P("audit"));
        // Every source needs a permission that exists.
        Assert.All(ReportCatalog.Datasets, d => Assert.Contains(ReportCatalog.RequiredPermission(d), Permissions.All));
    }

    [Fact]
    public void Every_area_permission_is_known()
    {
        foreach (var p in Permissions.AssetViews.Concat(Permissions.AssetManages).Append(Permissions.DashboardView).Append(Permissions.ReportsView).Append(Permissions.ActivityView))
            Assert.Contains(p, Permissions.All);
        Assert.Equal(Permissions.All.Count, Permissions.All.Distinct().Count());
    }

    [Fact]
    public void Retention_cuts_off_whole_months()
    {
        var now = new DateTimeOffset(2026, 10, 25, 15, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2025, 10, 1, 0, 0, 0, TimeSpan.Zero), LogSettings.Cutoff(365, now, TimeZoneInfo.Utc));
        Assert.Equal(new DateTimeOffset(2024, 10, 1, 0, 0, 0, TimeSpan.Zero), LogSettings.Cutoff(730, now, TimeZoneInfo.Utc));
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), LogSettings.Cutoff(90, now, TimeZoneInfo.Utc));
        Assert.Null(LogSettings.Cutoff(0, now, TimeZoneInfo.Utc));
    }

    [Theory]
    [InlineData(3, "2026-07-01", "2026-10-01")]
    [InlineData(6, "2026-01-01", "2026-07-01")]
    [InlineData(12, "2025-01-01", "2026-01-01")]
    public void Scheduled_exports_cover_the_period_that_just_ended(int months, string from, string to)
    {
        var s = new LogExportSchedule { Enabled = true, EveryMonths = months };
        var (f, t) = s.LastPeriod(new DateTimeOffset(2026, 10, 25, 15, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc);
        Assert.Equal(DateTimeOffset.Parse(from + "T00:00:00Z"), f);
        Assert.Equal(DateTimeOffset.Parse(to + "T00:00:00Z"), t);
        // On the first day of a new period, the period before it is due.
        var (f2, _) = s.LastPeriod(DateTimeOffset.Parse(to + "T00:00:01Z"), TimeZoneInfo.Utc);
        Assert.Equal(f, f2);
    }

    [Fact]
    public void Log_settings_are_validated_and_default_to_one_year()
    {
        var d = LogSettings.Parse(null);
        Assert.Equal(365, d.AuditRetentionDays);
        Assert.Equal(365, d.ActivityRetentionDays);
        Assert.True(d.ArchiveBeforeDelete);
        Assert.Null(d.Validate());
        Assert.NotNull(new LogSettings { AuditRetentionDays = 30 }.Validate());
        Assert.NotNull(new LogSettings { Schedule = new() { EveryMonths = 4 } }.Validate());
        Assert.NotNull(new LogSettings { Schedule = new() { IncludeAudit = false, IncludeActivity = false } }.Validate());
        var round = LogSettings.Parse(new LogSettings { AuditRetentionDays = 2555, Schedule = new() { Enabled = true, EveryMonths = 6 } }.ToJson());
        Assert.Equal(2555, round.AuditRetentionDays);
        Assert.Equal(6, round.Schedule.EveryMonths);
        Assert.True(round.Schedule.Enabled);
    }

    [Fact]
    public void Csv_cells_are_quoted_and_formula_safe()
    {
        Assert.Equal("plain", LogArchiver.Cell("plain"));
        Assert.Equal("\"a,b\"", LogArchiver.Cell("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", LogArchiver.Cell("say \"hi\""));
        Assert.Equal("'=cmd|' /C calc'!A0", LogArchiver.Cell("=cmd|' /C calc'!A0"));
        Assert.Equal("'@SUM(A1)", LogArchiver.Cell("@SUM(A1)"));
        Assert.Equal("-5", LogArchiver.Cell("-5"));
        Assert.Equal("", LogArchiver.Cell(null));
        Assert.Equal("2026-01-02T03:04:05.000Z", LogArchiver.Cell(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)));
    }
}

public sealed class RbacAndLogDatabaseTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    public Task InitializeAsync() => _db.InitializeAsync();
    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<int> Migrate() => new SqlMigrator(NullLogger<SqlMigrator>.Instance).MigrateAsync(_db.OwnerConnection, _db.App, "1.0.0-test");

    private static async Task<List<string>> Strings(NpgsqlDataSource ds, string sql)
    {
        await using var cmd = ds.CreateCommand(sql);
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<string>();
        while (await r.ReadAsync()) list.Add(r.GetString(0));
        return list;
    }

    [DbFact]
    public async Task Built_in_roles_and_groups_give_only_their_areas()
    {
        await Migrate();
        await using var ds = NpgsqlDataSource.Create(_db.AppConnection);
        // The non-IT role opens the non-IT inventory, the dashboard and reports — nothing of the IT inventory.
        var nonit = await Strings(ds, "SELECT permission_code FROM zentriq.role_permissions WHERE role_id = 'nonit' ORDER BY 1");
        Assert.Equal(new[] { "dashboard.view", "nonit.manage", "nonit.view", "reports.design", "reports.export", "reports.view" }, nonit);
        var viewer = await Strings(ds, "SELECT permission_code FROM zentriq.role_permissions WHERE role_id = 'nonit_viewer' ORDER BY 1");
        Assert.DoesNotContain("nonit.manage", viewer);
        Assert.DoesNotContain("assets.view", viewer);
        // Existing roles keep what they could see (assets.view now brings the area permissions).
        var admin = await Strings(ds, "SELECT permission_code FROM zentriq.role_permissions WHERE role_id = 'admin'");
        Assert.All(Permissions.All, p => Assert.Contains(p, admin));
        foreach (var role in await Strings(ds, "SELECT DISTINCT role_id FROM zentriq.role_permissions WHERE permission_code = 'assets.view' AND role_id NOT IN ('security', 'it_assets')"))
        {
            var perms = await Strings(ds, $"SELECT permission_code FROM zentriq.role_permissions WHERE role_id = '{role}'");
            Assert.Contains("nonit.view", perms);
            Assert.Contains("dashboard.view", perms);
        }

        // A user in a group gets the group's roles through user_role_grants.
        await using (var c = ds.CreateCommand("""
            WITH u AS (INSERT INTO zentriq.users (email, display_name, password_hash) VALUES ('facilities@example.test', 'Fac', 'x') RETURNING id),
                 g AS (INSERT INTO zentriq.user_groups (name) VALUES ('Facilities team') RETURNING id),
                 m AS (INSERT INTO zentriq.user_group_members (group_id, user_id) SELECT g.id, u.id FROM g, u)
            INSERT INTO zentriq.user_group_roles (group_id, role_id) SELECT id, 'nonit' FROM g
            """))
            await c.ExecuteNonQueryAsync();
        var grants = await Strings(ds, "SELECT g.role_id FROM zentriq.user_role_grants g JOIN zentriq.users u ON u.id = g.user_id WHERE u.email = 'facilities@example.test'");
        Assert.Equal(new[] { "nonit" }, grants);
        // Group names are unique regardless of case.
        await using var dup = ds.CreateCommand("INSERT INTO zentriq.user_groups (name) VALUES ('FACILITIES TEAM ')");
        var ex = await Assert.ThrowsAsync<PostgresException>(() => dup.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
    }

    [DbFact]
    public async Task Only_the_owner_can_remove_old_audit_entries_and_never_the_last_90_days()
    {
        await Migrate();
        await using var app = NpgsqlDataSource.Create(_db.AppConnection);
        await using var owner = NpgsqlDataSource.Create(_db.OwnerConnection);
        await using (var c = app.CreateCommand("""
            INSERT INTO zentriq.audit_events (occurred_at, actor_name, action, outcome) VALUES
              (now() - interval '500 days', 't', 'old.one', 'Success'), (now() - interval '450 days', 't', 'old.two', 'Success'),
              (now() - interval '10 days', 't', 'recent', 'Success')
            """))
            await c.ExecuteNonQueryAsync();

        // The application role can neither delete nor call the purge function.
        await using (var del = app.CreateCommand("DELETE FROM zentriq.audit_events"))
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => del.ExecuteNonQueryAsync())).SqlState);
        await using (var bypass = app.CreateCommand("SET zentriq.audit_purge = 'on'; DELETE FROM zentriq.audit_events"))
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => bypass.ExecuteNonQueryAsync())).SqlState);
        await using (var fn = app.CreateCommand("SELECT zentriq.purge_audit_events(now() - interval '365 days')"))
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => fn.ExecuteScalarAsync())).SqlState);

        // The owner (maintenance task) cannot touch the last 90 days …
        await using (var recent = owner.CreateCommand("SELECT zentriq.purge_audit_events(now() - interval '30 days')"))
            await Assert.ThrowsAsync<PostgresException>(() => recent.ExecuteScalarAsync());
        // … and removes older entries, noting the removal (with the last removed hash) in the log.
        await using (var purge = owner.CreateCommand("SELECT zentriq.purge_audit_events(now() - interval '365 days')"))
            Assert.Equal(2L, await purge.ExecuteScalarAsync());
        var actions = await Strings(app, "SELECT action FROM zentriq.audit_events ORDER BY id");
        Assert.DoesNotContain("old.one", actions);
        Assert.Contains("recent", actions);
        Assert.Equal("audit.purged", actions[^1]);
        // Outside the function, deleting is still refused, even for the owner.
        await using (var direct = owner.CreateCommand("DELETE FROM zentriq.audit_events"))
            await Assert.ThrowsAsync<PostgresException>(() => direct.ExecuteNonQueryAsync());
    }

    [DbFact]
    public async Task Log_archives_contain_the_period_with_checksums()
    {
        await Migrate();
        await using var ds = NpgsqlDataSource.Create(_db.AppConnection);
        await using (var c = ds.CreateCommand("""
            INSERT INTO zentriq.audit_events (occurred_at, actor_name, action, target, outcome) VALUES
              ('2026-06-30 23:59:59+00', 'a', 'before', NULL, 'Success'),
              ('2026-07-01 00:00:00+00', 'a', 'in.one', '=HYPERLINK("x")', 'Success'),
              ('2026-09-30 23:59:59+00', 'a', 'in.two', 'two, with comma', 'Failure'),
              ('2026-10-01 00:00:00+00', 'a', 'after', NULL, 'Success')
            """))
            await c.ExecuteNonQueryAsync();
        var folder = Path.Combine(Path.GetTempPath(), "zq-logs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var r = await LogArchiver.WriteAsync(ds, folder, DateTimeOffset.Parse("2026-07-01T00:00:00Z"), DateTimeOffset.Parse("2026-10-01T00:00:00Z"), true, true, "TEST-SRV", default);
            Assert.Equal(2, r.AuditRows);
            Assert.Equal(0, r.ActivityRows);
            Assert.StartsWith("zentriq-logs-test-srv-", Path.GetFileName(r.File));
            Assert.Equal(64, r.Sha256.Length);
            using var zip = ZipFile.OpenRead(r.File);
            Assert.Equal(new[] { "activity.csv", "audit.csv", "manifest.json" }, zip.Entries.Select(e => e.FullName).OrderBy(x => x));
            string Read(string n) { using var s = new StreamReader(zip.GetEntry(n)!.Open()); return s.ReadToEnd(); }
            var audit = Read("audit.csv").TrimStart('﻿').Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(LogArchiver.AuditHeader, audit[0].TrimEnd('\r'));
            Assert.Equal(3, audit.Length);
            Assert.Contains("'=HYPERLINK", audit[1]);
            Assert.Contains("\"two, with comma\"", audit[2]);
            using var manifest = JsonDocument.Parse(Read("manifest.json"));
            Assert.Equal(2, manifest.RootElement.GetProperty("audit").GetProperty("rows").GetInt64());
            Assert.Equal(64, manifest.RootElement.GetProperty("audit").GetProperty("lastHash").GetString()!.Length);
            Assert.Empty(Directory.GetFiles(folder, "*.partial"));
            Assert.Empty(Directory.GetFiles(folder, "*.csv"));
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
    }
}
