using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Zentriq.Infrastructure.Database;
using Zentriq.Infrastructure.Deployment;
using Zentriq.Infrastructure.Patches;
using Zentriq.Infrastructure.Scripts;
using Zentriq.Inventory;

namespace Zentriq.Tests;

/// <summary>Release 2: Patch Management and Scripts &amp; Remediation.</summary>
public sealed class Release2Tests
{
    // ---- patch management ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Security Updates", "security")]
    [InlineData("Critical Updates", "critical")]
    [InlineData("Definition Updates", "definition")]
    [InlineData("Update Rollups", "update-rollup")]
    [InlineData("Drivers", "driver")]
    [InlineData("security", "security")]
    [InlineData(null, "updates")]
    [InlineData("Sicherheitsupdates", "other")]
    public void Windows_update_classifications_are_normalised(string? name, string expected) => Assert.Equal(expected, PatchStore.Classification(name));

    [Theory]
    [InlineData("Critical", "critical")]
    [InlineData("Important", "important")]
    [InlineData("Moderate", "moderate")]
    [InlineData("Low", "low")]
    [InlineData("High", "important")]
    [InlineData("", "unspecified")]
    [InlineData(null, "unspecified")]
    public void Severities_are_normalised(string? name, string expected) => Assert.Equal(expected, PatchStore.Severity(name));

    [Fact]
    public void Update_ids_are_checked()
    {
        Assert.Equal("1a2b3c4d-0000-1111-2222-333344445555", PatchStore.IdOf(new ScanUpdate("1A2B3C4D-0000-1111-2222-333344445555", "5034441", "t", null, null, null, null, null, null, null, null, null, null), "windows"));
        Assert.Null(PatchStore.IdOf(new ScanUpdate("bad id; rm -rf", null, "t", null, null, null, null, null, null, null, null, null, null), "windows"));
        Assert.Equal("linux:openssl:3.0.2-0ubuntu1.12", PatchStore.IdOf(new ScanUpdate(null, null, null, null, null, null, "openssl", "3.0.2-0ubuntu1.12", null, null, null, null, null), "linux"));
        // A package name the package manager could take as an option is refused.
        Assert.Null(PatchStore.IdOf(new ScanUpdate(null, null, null, null, null, null, "--allow-downgrades", "1", null, null, null, null, null), "linux"));
        Assert.Null(PatchStore.IdOf(new ScanUpdate(null, null, null, null, null, null, "a b", "1", null, null, null, null, null), "linux"));
        Assert.Equal("5034441", PatchStore.CleanKb("KB5034441"));
        Assert.Null(PatchStore.CleanKb("KB50x"));
    }

    [Fact]
    public void Maintenance_windows_open_on_their_days_and_may_run_past_midnight()
    {
        int[] wednesday = [3];
        var start = new TimeOnly(22, 0);
        // Wednesday 6 November 2024, 23:30: in the window that started at 22:00.
        Assert.Equal(new DateTime(2024, 11, 6, 22, 0, 0), PatchStore.WindowStart(wednesday, start, 4, new DateTime(2024, 11, 6, 23, 30, 0)));
        // Thursday 01:00: still Wednesday's window (22:00 + 4 h).
        Assert.Equal(new DateTime(2024, 11, 6, 22, 0, 0), PatchStore.WindowStart(wednesday, start, 4, new DateTime(2024, 11, 7, 1, 0, 0)));
        // Thursday 02:00: closed.
        Assert.Null(PatchStore.WindowStart(wednesday, start, 4, new DateTime(2024, 11, 7, 2, 0, 0)));
        // Wednesday 21:59: not yet.
        Assert.Null(PatchStore.WindowStart(wednesday, start, 4, new DateTime(2024, 11, 6, 21, 59, 0)));
        // Every day, 03:00 for 2 hours.
        Assert.Equal(new DateTime(2024, 11, 9, 3, 0, 0), PatchStore.WindowStart([0, 1, 2, 3, 4, 5, 6], new TimeOnly(3, 0), 2, new DateTime(2024, 11, 9, 4, 59, 0)));
    }

    // ---- Linux package managers ------------------------------------------------------------------------------------------

    [Fact]
    public void Apt_upgrades_and_security_suites_are_read()
    {
        const string output = """
            Listing...
            openssl/jammy-updates,jammy-security 3.0.2-0ubuntu1.12 amd64 [upgradable from: 3.0.2-0ubuntu1.10]
            tzdata/jammy-updates 2024a-0ubuntu0.22.04 all [upgradable from: 2023c-0ubuntu0.22.04.2]
            curl/stable-security 7.88.1-10+deb12u5 amd64 [upgradable from: 7.88.1-10+deb12u4]
            """;
        var list = LinuxPatches.ParseApt(output);
        Assert.Equal(3, list.Count);
        Assert.Equal(("openssl", "3.0.2-0ubuntu1.12", true), (list[0].Package, list[0].Version, list[0].Security));
        Assert.False(list[1].Security);
        Assert.True(list[2].Security);
    }

    [Fact]
    public void Dnf_upgrades_and_advisories_are_read()
    {
        const string check = """

            kernel.x86_64                     5.14.0-362.13.1.el9_3        baseos
            openssl-libs.x86_64               1:3.0.7-25.el9_3             baseos
            tzdata.noarch                     2024a-1.el9                  appstream
            Obsoleting Packages
            grub2-tools.x86_64                1:2.06-70.el9_3.1            baseos
            """;
        var list = LinuxPatches.ParseCheckUpdate(check);
        Assert.Equal(["kernel", "openssl-libs", "tzdata"], list.Select(u => u.Package));
        Assert.Equal("1:3.0.7-25.el9_3", list[1].Version);

        const string info = """
            RHSA-2024:0310 Important/Sec. openssl-libs-1:3.0.7-25.el9_3.x86_64
            RHSA-2024:0461 Important/Sec. kernel-5.14.0-362.13.1.el9_3.x86_64
            RHBA-2024:0100 bugfix         tzdata-2024a-1.el9.noarch
            """;
        var adv = LinuxPatches.ParseUpdateInfo(info);
        Assert.True(adv["kernel"].Security);
        Assert.Equal("important", adv["kernel"].Severity);
        Assert.False(adv["tzdata"].Security);
        LinuxPatches.Apply(list, adv);
        Assert.True(list.Single(u => u.Package == "kernel").Security);
        // The epoch is part of the version, not of the name.
        Assert.False(adv.ContainsKey("openssl-libs-1"));

        // dnf 5 (Fedora 41+)
        var dnf5 = LinuxPatches.ParseUpdateInfo("Name             Type     Severity Package                           Issued\nFEDORA-2024-1a2b security Moderate openssl-3.2.1-2.fc40.x86_64       2024-02-01 00:00:00");
        Assert.True(dnf5["openssl"].Security);
        Assert.Equal("moderate", dnf5["openssl"].Severity);

        Assert.Equal("kernel-core", LinuxPatches.NameOfNevra("kernel-core-5.14.0-362.el9.x86_64"));
    }

    [Fact]
    public void Zypper_updates_are_read()
    {
        const string output = """
            S | Repository              | Name | Current Version | Available Version | Arch
            --+-------------------------+------+-----------------+-------------------+-------
            v | SLE-Module-Basesystem   | curl | 8.0.1-150400.5.1 | 8.0.1-150400.5.41.1 | x86_64
            """;
        var list = LinuxPatches.ParseZypper(output);
        Assert.Single(list);
        Assert.Equal(("curl", "8.0.1-150400.5.41.1"), (list[0].Package, list[0].Version));
    }

    // ---- scripts -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Scripts_are_validated()
    {
        Assert.Null(ScriptStore.Validate(new ScriptInput("Disk", null, null, "windows", "powershell", "Get-PSDrive", null, 60)));
        Assert.Equal("language", ScriptStore.Validate(new ScriptInput("X", null, null, "windows", "bash", "ls", null, 60))!.Value.Field);
        Assert.Equal("language", ScriptStore.Validate(new ScriptInput("X", null, null, "linux", "powershell", "ls", null, 60))!.Value.Field);
        Assert.Null(ScriptStore.Validate(new ScriptInput("Py", null, null, "linux", "python", "print(1)", null, 60)));
        Assert.Equal("content", ScriptStore.Validate(new ScriptInput("X", null, null, "linux", "sh", "  ", null, 60))!.Value.Field);
        Assert.Equal("content", ScriptStore.Validate(new ScriptInput("X", null, null, "linux", "sh", new string('a', ScriptStore.MaxContent + 1), null, 60))!.Value.Field);
        Assert.Equal("parameters", ScriptStore.Validate(new ScriptInput("X", null, null, "linux", "sh", "ls", "a\nb", 60))!.Value.Field);
        Assert.Equal("timeoutSeconds", ScriptStore.Validate(new ScriptInput("X", null, null, "linux", "sh", "ls", null, 5))!.Value.Field);
        // Every ready-made script is valid and for the platform of its language.
        Assert.All(ScriptStore.BuiltIns, b => Assert.Null(ScriptStore.Validate(b)));
    }

    [Fact]
    public void Line_endings_follow_the_platform_and_the_checksum_is_of_what_is_sent()
    {
        Assert.Equal("a\r\nb\r\n", ScriptStore.Normalize("a\nb\r\n", "windows"));
        Assert.Equal("a\nb\n", ScriptStore.Normalize("a\r\nb\r", "linux"));
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", ScriptStore.Sha256("abc"));
    }

    [Fact]
    public void Schedules_compute_their_next_run()
    {
        var now = new DateTime(2024, 11, 6, 10, 30, 0);   // Wednesday
        Assert.Equal(new DateTime(2024, 11, 6, 11, 15, 0), ScriptStore.NextRun("hourly", [], new TimeOnly(0, 15), null, now));
        Assert.Equal(new DateTime(2024, 11, 7, 2, 0, 0), ScriptStore.NextRun("daily", [], new TimeOnly(2, 0), null, now));
        Assert.Equal(new DateTime(2024, 11, 6, 18, 0, 0), ScriptStore.NextRun("daily", [], new TimeOnly(18, 0), null, now));
        Assert.Equal(new DateTime(2024, 11, 11, 2, 0, 0), ScriptStore.NextRun("weekly", [1], new TimeOnly(2, 0), null, now));   // next Monday
        Assert.Equal(new DateTime(2024, 11, 13, 9, 0, 0), ScriptStore.NextRun("weekly", [3], new TimeOnly(9, 0), null, now));   // today 09:00 has passed
        Assert.Equal(new DateTime(2024, 11, 8, 8, 0, 0), ScriptStore.NextRun("once", [], default, new DateTime(2024, 11, 8, 8, 0, 0), now));
        Assert.Null(ScriptStore.NextRun("once", [], default, new DateTime(2024, 11, 1), now));
        Assert.Null(ScriptStore.NextRun("weekly", [], new TimeOnly(2, 0), null, now));
    }

    [Fact]
    public void Targets_round_trip()
    {
        var t = new TargetSpec([Guid.Parse("11111111-1111-1111-1111-111111111111")], null, null, false);
        var back = TargetSpec.FromJson(t.ToJson());
        Assert.Equal(t.AssetIds, back.AssetIds);
        Assert.False(back.IsEmpty);
        Assert.True(new TargetSpec(null, [], null, false).IsEmpty);
        Assert.False(new TargetSpec(null, null, null, true).IsEmpty);
        Assert.True(TargetSpec.FromJson("not json").IsEmpty);
    }
}

public sealed class Release2DbTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        if (Environment.GetEnvironmentVariable("ZENTRIQ_TEST_PG") is { Length: > 0 })
            await new SqlMigrator(NullLogger<SqlMigrator>.Instance).MigrateAsync(_db.OwnerConnection, _db.App, "1.1.0-test");
    }
    public Task DisposeAsync() => _db.DisposeAsync();

    private static async Task<object?> Scalar(NpgsqlDataSource ds, string sql, params (string, object)[] ps)
    {
        await using var cmd = ds.CreateCommand(sql);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        return await cmd.ExecuteScalarAsync();
    }

    private static async Task<(Guid Asset, Guid Agent)> Device(NpgsqlDataSource ds, string name, string family, params string[] caps)
    {
        var asset = (Guid)(await Scalar(ds, "INSERT INTO zentriq.assets (hostname, category, status, os_family) VALUES (@h, 'endpoint', 'active', @f) RETURNING id", ("h", name), ("f", family)))!;
        await using var cmd = ds.CreateCommand("INSERT INTO zentriq.agent_registrations (asset_id, agent_version, status, capabilities, platform, last_checkin_at) VALUES (@a, '1.1.1.114', 'active', @c, @p, now()) RETURNING id");
        cmd.Parameters.AddWithValue("a", asset);
        cmd.Parameters.AddWithValue("c", caps);
        cmd.Parameters.AddWithValue("p", family == "linux" ? "linux-x64" : "windows-x64");
        return (asset, (Guid)(await cmd.ExecuteScalarAsync())!);
    }

    private static ScanUpdate Win(string id, string kb, string cls, string? sev = null, string? released = "2024-10-08") =>
        new(id, kb, $"Update for Windows (KB{kb})", cls, sev, "Windows Server 2022", null, null, released, 1000, true, sev is null ? null : ["CVE-2024-38063"], "https://support.microsoft.com/kb/" + kb);

    [DbFact]
    public async Task Scans_record_missing_updates_and_installations_and_deployments_send_only_what_is_missing()
    {
        await using var ds = NpgsqlDataSource.Create(_db.AppConnection);
        var (pc1, _) = await Device(ds, "PC-01", "windows", PatchStore.Capability);
        var (pc2, _) = await Device(ds, "PC-02", "windows", PatchStore.Capability);
        var (old, _) = await Device(ds, "OLD-01", "windows");                    // an agent before build 114
        const string sec = "aaaaaaaa-0000-0000-0000-000000000001", def = "aaaaaaaa-0000-0000-0000-000000000002", opt = "aaaaaaaa-0000-0000-0000-000000000003";

        var r1 = await PatchStore.IngestAsync(ds, pc1, "windows", new ScanReport(true, null, "Windows Update", false, [Win(sec, "5044281", "security", "Critical"), Win(def, "2267602", "definition"), Win(opt, "5044000", "updates", null, null)]), default);
        Assert.Equal(3, r1.Missing);
        await PatchStore.IngestAsync(ds, pc2, "windows", new ScanReport(true, null, "WSUS (http://wsus:8530)", false, [Win(sec, "5044281", "security", "Critical")]), default);
        Assert.Equal("critical", await Scalar(ds, "SELECT severity FROM zentriq.patches WHERE id = @id", ("id", sec)));
        Assert.Equal(new[] { "CVE-2024-38063" }, (string[])(await Scalar(ds, "SELECT cves FROM zentriq.patches WHERE id = @id", ("id", sec)))!);

        // A deployment of the security update reaches both devices; the older agent is skipped.
        var job = await PatchStore.CreateJobAsync(ds, new PatchJobRequest("Install", [sec], false, null, "when-needed", new TargetSpec([pc1, pc2, old], null, null, false), "windows", null, null), null, default);
        Assert.Equal((2, 1), (job.Sent, job.Skipped));
        var arg = (string)(await Scalar(ds, "SELECT argument FROM zentriq.asset_actions WHERE asset_id = @a AND kind = 'patch' AND operation = 'install'", ("a", pc1)))!;
        Assert.Contains(sec, arg);
        Assert.DoesNotContain(def, arg);
        Assert.Contains("\"reboot\":\"when-needed\"", arg);
        Assert.Contains("older", (string)(await Scalar(ds, "SELECT skipped FROM zentriq.patch_job_devices WHERE job_id = @j AND asset_id = @a", ("j", job.Id), ("a", old)))!);

        // "All missing" with a classification filter; declined updates are left out.
        await PatchStore.SetApprovalAsync(ds, [opt], "declined", null, default);
        var all = await PatchStore.CreateJobAsync(ds, new PatchJobRequest("All", null, true, new PatchFilter(["security", "definition", "updates"], null, false, 0), "never",
            new TargetSpec([pc1], null, null, false), "windows", null, null), null, default);
        Assert.Equal(1, all.Sent);
        var arg2 = (string)(await Scalar(ds, "SELECT argument FROM zentriq.asset_actions WHERE target = @j", ("j", all.Id.ToString())))!;
        Assert.Contains(sec, arg2);
        Assert.Contains(def, arg2);
        Assert.DoesNotContain(opt, arg2);
        // Waiting 30 days after the release: an update without a release date counts from when it was first seen (today).
        await PatchStore.SetApprovalAsync(ds, [opt], "none", null, default);
        var delayed = await PatchStore.CreateJobAsync(ds, new PatchJobRequest("Delayed", null, true, new PatchFilter(["updates"], null, false, 30), "never",
            new TargetSpec([pc1], null, null, false), "windows", null, null), null, default);
        Assert.Equal((0, 1, 1), (delayed.Sent, delayed.Skipped, delayed.Nothing));

        // The install: the security update succeeded and waits for the restart, the definition update failed.
        await PatchStore.IngestAsync(ds, pc1, "windows", new ScanReport(true, null, "Windows Update", true, [Win(def, "2267602", "definition"), Win(opt, "5044000", "updates", null, null)],
            [new InstallResult(sec, true, true, "Installed."), new InstallResult(def, false, false, "Failed (0x80240022).")]), default);
        Assert.Equal("pending_reboot", await Scalar(ds, "SELECT state FROM zentriq.asset_patches WHERE asset_id = @a AND patch_id = @p", ("a", pc1), ("p", sec)));
        Assert.Equal("failed", await Scalar(ds, "SELECT state FROM zentriq.asset_patches WHERE asset_id = @a AND patch_id = @p", ("a", pc1), ("p", def)));
        Assert.Equal(true, await Scalar(ds, "SELECT reboot_pending FROM zentriq.asset_patch_scans WHERE asset_id = @a", ("a", pc1)));
        // After the restart: installed; the failed one stays failed while it is still missing.
        await PatchStore.IngestAsync(ds, pc1, "windows", new ScanReport(true, null, "Windows Update", false, [Win(def, "2267602", "definition"), Win(opt, "5044000", "updates", null, null)]), default);
        Assert.Equal("installed", await Scalar(ds, "SELECT state FROM zentriq.asset_patches WHERE asset_id = @a AND patch_id = @p", ("a", pc1), ("p", sec)));
        Assert.Equal("failed", await Scalar(ds, "SELECT state FROM zentriq.asset_patches WHERE asset_id = @a AND patch_id = @p", ("a", pc1), ("p", def)));

        // A failed scan keeps what is known and says why.
        await PatchStore.IngestAsync(ds, pc2, "windows", new ScanReport(false, "The Windows Update service is disabled on this device.", null, false, null), default);
        Assert.Equal(false, await Scalar(ds, "SELECT ok FROM zentriq.asset_patch_scans WHERE asset_id = @a", ("a", pc2)));
        Assert.Equal("missing", await Scalar(ds, "SELECT state FROM zentriq.asset_patches WHERE asset_id = @a AND patch_id = @p", ("a", pc2), ("p", sec)));

        // Automatic scans: only devices with a capable agent whose scan is due (and no scan already waiting).
        await Scalar(ds, "UPDATE zentriq.asset_patch_scans SET scanned_at = now() - interval '2 days'");
        await Scalar(ds, "UPDATE zentriq.asset_actions SET status = 'succeeded', completed_at = now()");   // a waiting installation also holds the scan back
        Assert.Equal(2, await PatchStore.QueueDueScansAsync(ds, 24, 100, default));
        Assert.Equal(0, await PatchStore.QueueDueScansAsync(ds, 24, 100, default));
    }

    [DbFact]
    public async Task Linux_package_upgrades_replace_older_versions()
    {
        await using var ds = NpgsqlDataSource.Create(_db.AppConnection);
        var (srv, _) = await Device(ds, "WEB-01", "linux", PatchStore.Capability);
        await PatchStore.IngestAsync(ds, srv, "linux", new ScanReport(true, null, "apt", false, [
            new ScanUpdate(null, null, "openssl 3.0.2-0ubuntu1.12", "security", null, "jammy-security", "openssl", "3.0.2-0ubuntu1.12", null, null, false, null, null),
            new ScanUpdate(null, null, "tzdata 2024a", "updates", null, "jammy-updates", "tzdata", "2024a-0ubuntu0.22.04", null, null, false, null, null)]), default);
        // A newer openssl appears: the older version's row goes, it is not counted as installed.
        await PatchStore.IngestAsync(ds, srv, "linux", new ScanReport(true, null, "apt", false, [
            new ScanUpdate(null, null, "openssl 3.0.2-0ubuntu1.13", "security", null, "jammy-security", "openssl", "3.0.2-0ubuntu1.13", null, null, false, null, null)]), default);
        Assert.Null(await Scalar(ds, "SELECT state FROM zentriq.asset_patches WHERE asset_id = @a AND patch_id = 'linux:openssl:3.0.2-0ubuntu1.12'", ("a", srv)));
        Assert.Equal("missing", await Scalar(ds, "SELECT state FROM zentriq.asset_patches WHERE asset_id = @a AND patch_id = 'linux:openssl:3.0.2-0ubuntu1.13'", ("a", srv)));
        Assert.Equal("installed", await Scalar(ds, "SELECT state FROM zentriq.asset_patches WHERE asset_id = @a AND patch_id = 'linux:tzdata:2024a-0ubuntu0.22.04'", ("a", srv)));

        var job = await PatchStore.CreateJobAsync(ds, new PatchJobRequest("All", null, true, new PatchFilter(["security"], null, false, 0), "never", new TargetSpec([srv], null, null, false), "linux", null, null), null, default);
        Assert.Equal(1, job.Sent);
        Assert.Contains("\"packages\":[\"openssl\"]", (string)(await Scalar(ds, "SELECT argument FROM zentriq.asset_actions WHERE target = @j", ("j", job.Id.ToString())))!);
    }

    [DbFact]
    public async Task Policies_deploy_once_per_window()
    {
        await using var ds = NpgsqlDataSource.Create(_db.AppConnection);
        var (pc, _) = await Device(ds, "PC-11", "windows", PatchStore.Capability);
        await PatchStore.IngestAsync(ds, pc, "windows", new ScanReport(true, null, "Windows Update", false, [Win("bbbbbbbb-0000-0000-0000-000000000001", "5044281", "security", "Important")]), default);
        await Scalar(ds, """
            INSERT INTO zentriq.patch_policies (name, platform, classifications, targets, days, start_time, window_hours)
            VALUES ('Nightly', 'windows', '{security}', '{"all":true}'::jsonb, '{0,1,2,3,4,5,6}', '00:00', 24)
            """);
        var first = await PatchStore.RunDuePoliciesAsync(ds, DateTime.Now, default);
        Assert.Single(first);
        Assert.Equal(1, first[0].Result.Sent);
        Assert.Empty(await PatchStore.RunDuePoliciesAsync(ds, DateTime.Now, default));   // same window: not again
        Assert.Equal(true, await Scalar(ds, "SELECT automatic FROM zentriq.asset_actions WHERE asset_id = @a AND operation = 'install'", ("a", pc)));
    }

    [DbFact]
    public async Task Scripts_run_on_capable_devices_and_their_output_is_kept()
    {
        await using var ds = NpgsqlDataSource.Create(_db.AppConnection);
        await ScriptStore.EnsureBuiltInsAsync(ds, default);
        await ScriptStore.EnsureBuiltInsAsync(ds, default);   // twice: no duplicates
        Assert.Equal((long)ScriptStore.BuiltIns.Length, await Scalar(ds, "SELECT count(*) FROM zentriq.scripts WHERE built_in"));
        var (win, winAgent) = await Device(ds, "PC-21", "windows", ScriptStore.Capability);
        var (lin, _) = await Device(ds, "SRV-21", "linux", ScriptStore.Capability);
        var (old, _) = await Device(ds, "PC-22", "windows");

        var id = await ScriptStore.SaveAsync(ds, null, new ScriptInput("Hello", null, "Test", "windows", "powershell", "Write-Output 'hello'\nexit 3", "-Name x", 60), null, default);
        var script = (await ScriptStore.GetAsync(ds, id, default))!;
        Assert.Equal("Write-Output 'hello'\r\nexit 3", script.Content);
        Assert.Equal(ScriptStore.Sha256(script.Content), script.Sha256);

        var run = await ScriptStore.RunAsync(ds, new ScriptRunRequest(id, null, null, new TargetSpec([win, lin, old], null, null, false), "test", null), null, default);
        Assert.Equal((1, 2), (run.Sent, run.Skipped));
        var (actionId, argument) = await First(ds, run.Id);
        Assert.Contains(script.Sha256, argument);
        Assert.Contains("-Name x", argument);

        // Only the agent the action was sent to can store its output.
        Assert.False(await ScriptStore.SaveOutputAsync(ds, actionId, Guid.NewGuid(), 0, "x", null, "ok", 1, default));
        Assert.True(await ScriptStore.SaveOutputAsync(ds, actionId, winAgent, 3, "hello\0", "", "failed", 120, default));
        Assert.Equal("hello", await Scalar(ds, "SELECT stdout FROM zentriq.asset_action_outputs WHERE action_id = @a", ("a", actionId)));

        // A remediation carries the detection script; both must be for the same platform.
        var detect = (Guid)(await Scalar(ds, "SELECT id FROM zentriq.scripts WHERE name = 'Detect: Print Spooler Not Running'"))!;
        var fix = (Guid)(await Scalar(ds, "SELECT id FROM zentriq.scripts WHERE name = 'Fix: Start Print Spooler'"))!;
        var rem = await ScriptStore.RunAsync(ds, new ScriptRunRequest(fix, detect, null, new TargetSpec([win], null, null, false), null, null), null, default);
        Assert.Contains("\"detect\":{", (await First(ds, rem.Id)).Argument);
        var linuxScript = (Guid)(await Scalar(ds, "SELECT id FROM zentriq.scripts WHERE name = 'Disk Usage Report'"))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ScriptStore.RunAsync(ds, new ScriptRunRequest(fix, linuxScript, null, new TargetSpec([win], null, null, false), null, null), null, default));

        // Ready-made scripts cannot be changed.
        await Assert.ThrowsAsync<KeyNotFoundException>(() => ScriptStore.SaveAsync(ds, fix, new ScriptInput("Fix: Start Print Spooler", null, null, "windows", "powershell", "x", null, 60), null, default));

        // Turned off: nothing runs.
        await ScriptStore.SaveSettingsAsync(ds, new ScriptSettings { Enabled = false, KeepOutputDays = 30 }, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ScriptStore.RunAsync(ds, new ScriptRunRequest(id, null, null, new TargetSpec([win], null, null, false), null, null), null, default));
    }

    [DbFact]
    public async Task Due_schedules_start_a_run_and_move_to_the_next_time()
    {
        await using var ds = NpgsqlDataSource.Create(_db.AppConnection);
        await ScriptStore.EnsureBuiltInsAsync(ds, default);
        var (win, _) = await Device(ds, "PC-31", "windows", ScriptStore.Capability);
        var script = (Guid)(await Scalar(ds, "SELECT id FROM zentriq.scripts WHERE name = 'Flush DNS Cache'"))!;
        await using (var cmd = ds.CreateCommand("""
            INSERT INTO zentriq.script_schedules (name, script_id, targets, frequency, at_time, next_run_at)
            VALUES ('Nightly DNS', @s, @t::jsonb, 'daily', '02:00', now() - interval '1 minute')
            """))
        {
            cmd.Parameters.AddWithValue("s", script);
            cmd.Parameters.AddWithValue("t", new TargetSpec([win], null, null, false).ToJson());
            await cmd.ExecuteNonQueryAsync();
        }
        Assert.Equal(1, await ScriptStore.RunDueSchedulesAsync(ds, default));
        Assert.Equal(0, await ScriptStore.RunDueSchedulesAsync(ds, default));
        Assert.Equal(true, await Scalar(ds, "SELECT next_run_at > now() FROM zentriq.script_schedules WHERE name = 'Nightly DNS'"));
        Assert.Equal(1L, await Scalar(ds, "SELECT count(*) FROM zentriq.script_jobs j JOIN zentriq.script_schedules s ON s.id = j.schedule_id WHERE s.name = 'Nightly DNS'"));
    }

    private static async Task<(Guid ActionId, string Argument)> First(NpgsqlDataSource ds, Guid jobId)
    {
        await using var cmd = ds.CreateCommand("SELECT x.id, x.argument FROM zentriq.script_job_devices d JOIN zentriq.asset_actions x ON x.id = d.action_id WHERE d.job_id = @j LIMIT 1");
        cmd.Parameters.AddWithValue("j", jobId);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        return (r.GetGuid(0), r.GetString(1));
    }
}
