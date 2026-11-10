using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Zentriq.Application.Licensing;
using Zentriq.Domain.Licensing;
using Zentriq.Infrastructure.Database;
using Zentriq.Infrastructure.Experience;
using Zentriq.Infrastructure.Inventory;
using Zentriq.Infrastructure.Licenses;
using Zentriq.Infrastructure.Licensing;
using Zentriq.Inventory;

namespace Zentriq.Tests;

/// <summary>Build 120: software metering and Device Experience.</summary>
public sealed class Release120Tests
{
    private static RunningProgram P(string path, string user, int pid) => new(path, user, pid, pid * 10L);

    [Fact]
    public void Recorder_counts_minutes_per_program_and_user_and_launches_after_the_first_sample()
    {
        var file = Path.Combine(Path.GetTempPath(), "zq-exp-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var rec = new ExperienceRecorder(file, p => ("Microsoft Office", "Microsoft Corporation", "16.0"));
            var t = new DateTime(2026, 11, 10, 9, 0, 0);
            // Word open with two windows (two processes) when the service starts: one minute, not a launch.
            rec.Sample(t, [P(@"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE", @"CORP\ana", 100), P(@"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE", @"CORP\ana", 101)], true, 20, 50);
            // A minute later Excel starts: a launch.
            rec.Sample(t.AddMinutes(1), [P(@"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE", @"CORP\ana", 100), P(@"C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE", @"CORP\ana", 200)], true, 95, 92);
            // Word closed and opened again: a new process is a second launch.
            rec.Sample(t.AddMinutes(2), [P(@"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE", @"CORP\ana", 300)], true, 10, 40);

            var (usage, days, since) = rec.Report(t.AddMinutes(2));
            Assert.Equal("2026-11-10", since);
            var word = Assert.Single(usage.Items, i => i.Exe == "WINWORD.EXE");
            Assert.Equal(3, word.Minutes);
            Assert.Equal(1, word.Launches);
            Assert.Equal("Microsoft Office", word.Product);
            var excel = Assert.Single(usage.Items, i => i.Exe == "EXCEL.EXE");
            Assert.Equal((1, 1), (excel.Minutes, excel.Launches));
            var day = Assert.Single(days);
            Assert.Equal(3, day.Samples);
            Assert.Equal((1, 1), (day.CpuHigh, day.MemHigh));
            Assert.Equal(41.7, day.CpuAvg);

            // Kept on disk and read back by a new recorder (service restart).
            rec.Save(force: true);
            var again = new ExperienceRecorder(file, _ => (null, null, null));
            Assert.Equal(3, again.Report(t.AddMinutes(3)).Usage.Items.Single(i => i.Exe == "WINWORD.EXE").Minutes);

            // Metering turned off: the totals are removed, resource samples continue.
            again.ClearUsage();
            again.Sample(t.AddMinutes(4), [P(@"C:\Tools\app.exe", "u", 1)], false, 10, 10);
            Assert.Empty(again.Report(t.AddMinutes(4)).Usage.Items);
            Assert.Equal(4, again.Report(t.AddMinutes(4)).Days.Single().Samples);

            // Older than the report window: not sent again; older than 35 days: removed.
            var later = t.AddDays(40);
            again.Sample(later, [P(@"C:\Tools\app.exe", "u", 1)], true, 10, 10);
            Assert.Single(again.Report(later).Days);
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\notepad.exe", false)]
    [InlineData(@"C:\Program Files\Zentriq\Agent\ZentriqAgent.exe", false)]
    [InlineData(@"C:\Program Files\Adobe\Acrobat DC\Acrobat\Acrobat.exe", true)]
    [InlineData("/usr/bin/libreoffice", true)]
    [InlineData("/usr/bin/bash", false)]
    [InlineData("/usr/lib/systemd/systemd", false)]
    [InlineData("/opt/zentriq/zentriq-agent", false)]
    public void System_programs_are_not_metered(string path, bool metered) =>
        Assert.Equal(metered, ExperienceRecorder.Metered(path, @"C:\Windows", @"C:\Program Files\Zentriq\Agent\"));

    [Fact]
    public void Score_takes_points_for_each_problem()
    {
        var healthy = ExperienceStore.Score(new(20_000, 10_000, 0, 0, 0, 0, 0, 1, 2, 55, 95, 3));
        Assert.Equal((100, "good"), (healthy.Score, healthy.Rating));
        Assert.Empty(healthy.Issues);

        var bad = ExperienceStore.Score(new(95_000, 70_000, 3, 2, 1, 1, 0, 25, 4, 7, 55, 40));
        // boot 12 + logon 4 + crashes 12 + hangs 4 + blue screen 15 + shutdown 5 + cpu 12 + disk 8 + battery 8 + uptime 5 = 85
        Assert.Equal(15, bad.Score);
        Assert.Equal("poor", bad.Rating);
        Assert.Equal("bugcheck", bad.Issues[0].Kind);
        Assert.Contains(bad.Issues, i => i.Kind == "boot" && i.Text.Contains("1 min 35 s"));

        var capped = ExperienceStore.Score(new(null, null, 50, 0, 0, 0, 0, null, null, null, null, null));
        Assert.Equal((75, "fair"), (capped.Score, capped.Rating));
    }

    [Fact]
    public void Catalogue_products_name_the_programs_that_use_them()
    {
        var office = LicenseCatalog.Standard.Find("ms-365-apps")!;
        Assert.Contains("winword.exe", office.ExePatterns);
        Assert.All(LicenseCatalog.Standard.Products.SelectMany(p => p.ExePatterns), e => Assert.Equal(e.ToLowerInvariant(), e));
        Assert.True(LicenseCatalog.Standard.Products.Count(p => p.ExePatterns.Count > 0) >= 40);
    }
}

/// <summary>On PostgreSQL: metering and experience reports stored with the inventory, unused installations found.</summary>
public sealed class Release120DbTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    public Task InitializeAsync() => _db.InitializeAsync();
    public Task DisposeAsync() => _db.DisposeAsync();

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static LicenseStatus Trial() => new(LicenseMode.Trial, "trial", null, 14, ["*"], new Dictionary<string, int> { ["devices"] = 30 }, null, null);

    private static InventoryDocument Doc(string host, string software, DateTime today, int usedDaysAgo, string since)
    {
        var d = new InventoryDocument { Source = "agent" };
        d.Device.Hostname = host;
        d.Device.Category = "endpoint";
        d.Device.SmbiosUuid = Guid.NewGuid().ToString();
        d.Os.Family = "windows";
        d.Os.LastBoot = Now.AddDays(-2);
        d.Software.Add(new SoftwareItem { Name = software, Publisher = "Microsoft Corporation", Version = "16.0.1" });
        d.Usage = new UsageReport { Since = since };
        if (usedDaysAgo >= 0)
            d.Usage.Items.Add(new UsageItem { Day = ExperienceRecorder.Day(today.AddDays(-usedDaysAgo)), Path = @"C:\Program Files\Microsoft Office\root\Office16\VISIO.EXE", Exe = "VISIO.EXE", Product = "Microsoft Visio", User = @"CORP\" + host, Minutes = 42, Launches = 2 });
        d.Experience = new ExperienceReport
        {
            Since = since, SystemFreePercent = 6,
            Days = { new ExperienceDay { Day = ExperienceRecorder.Day(today), Samples = 100, CpuAvg = 30, CpuHigh = 30, MemAvg = 60, MemHigh = 0 } },
            Events =
            {
                new ExperienceEvent { Kind = "boot", At = Now.AddDays(-1), Ms = 75_000, Ms2 = 20_000 },
                new ExperienceEvent { Kind = "crash", At = Now.AddHours(-3), App = "VISIO.EXE", Version = "16.0", Detail = "Faulting module mso.dll" },
            },
        };
        return d;
    }

    [DbFact]
    public async Task Usage_and_experience_are_stored_and_unused_installations_found()
    {
        await new SqlMigrator(NullLogger<SqlMigrator>.Instance).MigrateAsync(_db.OwnerConnection, _db.App, "1.1.0-test");
        await using var conn = new NpgsqlConnection(_db.AppConnection);
        await conn.OpenAsync();
        var store = new InventoryStore(new LicensingService(null!, null!, null!, null!, null!));
        var today = DateTime.UtcNow.Date;
        var longAgo = ExperienceRecorder.Day(today.AddDays(-200));

        var used = await store.ApplyAsync(conn, Doc("pc-used", "Microsoft Visio Professional 2021", today, 3, longAgo), new MatchHints(AgentId: Guid.NewGuid()), Trial(), default);
        var idle = await store.ApplyAsync(conn, Doc("pc-idle", "Microsoft Visio Professional 2021", today, 120, longAgo), new MatchHints(AgentId: Guid.NewGuid()), Trial(), default);
        var fresh = await store.ApplyAsync(conn, Doc("pc-new", "Microsoft Visio Professional 2021", today, -1, ExperienceRecorder.Day(today.AddDays(-5))), new MatchHints(AgentId: Guid.NewGuid()), Trial(), default);
        // The same report sent twice changes nothing.
        await store.ApplyAsync(conn, Doc("pc-used", "Microsoft Visio Professional 2021", today, 3, longAgo), new MatchHints(PreferredAssetId: used.AssetId), Trial(), default);

        async Task<long> Count(string sql, Guid a)
        {
            await using var c = new NpgsqlCommand(sql, conn);
            c.Parameters.AddWithValue("a", a);
            return Convert.ToInt64(await c.ExecuteScalarAsync());
        }
        Assert.Equal(42, await Count("SELECT sum(minutes) FROM zentriq.software_usage WHERE asset_id = @a", used.AssetId!.Value));
        Assert.Equal(2, await Count("SELECT count(*) FROM zentriq.device_experience_events WHERE asset_id = @a", used.AssetId!.Value));
        // The report is not kept in the stored document.
        Assert.Equal(0, await Count("SELECT count(*) FROM zentriq.asset_inventory_documents WHERE asset_id = @a AND jsonb_typeof(document->'usage') = 'object'", used.AssetId!.Value));

        // Score: slow start (12), crash (4), processor 30 % high (12), disk 6 % free (8) = 64, fair.
        await using (var c = new NpgsqlCommand("SELECT score, rating, crashes, boot_ms FROM zentriq.device_experience WHERE asset_id = @a", conn))
        {
            c.Parameters.AddWithValue("a", used.AssetId!.Value);
            await using var r = await c.ExecuteReaderAsync();
            Assert.True(await r.ReadAsync());
            Assert.Equal((64, "fair", 1, 75_000L), (r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt64(3)));
        }

        // Visio from the catalogue: its program is visio.exe.
        Guid product;
        await using (var c = new NpgsqlCommand("""
            INSERT INTO zentriq.sam_products (name, name_patterns, exe_patterns) VALUES ('Microsoft Visio', ARRAY['Microsoft Visio*'], ARRAY['visio.exe']) RETURNING id
            """, conn))
            product = (Guid)(await c.ExecuteScalarAsync())!;

        var states = new Dictionary<Guid, string>();
        await using (var c = new NpgsqlCommand($$"""
            WITH p AS (SELECT * FROM zentriq.sam_products WHERE id = @id),
            {{LicenseCompliance.InstallsCte}},
            {{LicenseCompliance.UsageCte}}
            SELECT i.asset_id, {{LicenseCompliance.UsageState}}
              FROM inst i CROSS JOIN p LEFT JOIN used_by u ON u.product_id = p.id AND u.asset_id = i.asset_id
              LEFT JOIN zentriq.software_usage_devices m ON m.asset_id = i.asset_id
            """, conn))
        {
            c.Parameters.AddWithValue("id", product);
            c.Parameters.AddWithValue("days", 90);
            await using var r = await c.ExecuteReaderAsync();
            while (await r.ReadAsync()) states[r.GetGuid(0)] = r.GetString(1);
        }
        Assert.Equal("used", states[used.AssetId!.Value]);
        Assert.Equal("unused", states[idle.AssetId!.Value]);
        Assert.Equal("new", states[fresh.AssetId!.Value]);
    }
}
