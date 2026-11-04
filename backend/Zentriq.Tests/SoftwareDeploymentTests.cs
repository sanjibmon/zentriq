using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Zentriq.Infrastructure.Database;
using Zentriq.Infrastructure.Deployment;
using Zentriq.Inventory;

namespace Zentriq.Tests;

/// <summary>Software Deployment: the approved-list decision (shared by the server and the agents) and the standard catalogue.</summary>
public sealed class AppControlTests
{
    private static AppControlPolicy Policy(params AppControlRule[] rules) => new() { Mode = "block", Rules = [.. rules] };
    private static SoftwareCandidate Win(string name, string? publisher = null, string? version = null, string? signer = null, string? sha = null) =>
        new() { Name = name, Publisher = publisher, Version = version, Signer = signer, Sha256 = sha, Platform = "windows" };

    [Fact]
    public void Nothing_is_approved_without_a_rule_except_built_in_components()
    {
        var p = Policy();
        Assert.False(AppControl.Evaluate(p, Win("Some Game", "Games Inc")).Approved);
        Assert.Equal("not-approved", AppControl.Evaluate(p, Win("Some Game")).Outcome);
        // Windows components, updates, drivers, Microsoft runtimes and the Zentriq agents are always allowed.
        Assert.Equal("built-in", AppControl.Evaluate(p, Win("Feature pack", signer: "Microsoft Windows")).Outcome);
        Assert.True(AppControl.Evaluate(p, Win("Microsoft Visual C++ 2015-2022 Redistributable (x64)", "Microsoft Corporation")).Approved);
        Assert.True(AppControl.Evaluate(p, Win("Zentriq Agent", "Zentriq")).Approved);
        Assert.True(AppControl.Evaluate(p, Win("Security Update for Microsoft Office", "Microsoft Corporation")).Approved);
        // ... but not something that only calls itself a runtime.
        Assert.False(AppControl.Evaluate(p, Win("Microsoft Visual C++ 2015 Redistributable", "Totally Not Microsoft")).Approved);
    }

    [Fact]
    public void Rules_match_name_publisher_signer_version_range_and_hash()
    {
        var chrome = new AppControlRule { Id = "1", Name = "Google Chrome*;Chrome*", Publisher = "Google LLC;Google Inc*", Platform = "windows" };
        var p = Policy(chrome);
        Assert.True(AppControl.Evaluate(p, Win("Google Chrome", "Google LLC", "120.0")).Approved);
        Assert.True(AppControl.Evaluate(p, Win("Chrome Installer", signer: "Google LLC")).Approved);              // signer instead of publisher
        Assert.False(AppControl.Evaluate(p, Win("Google Chrome", "Evil Corp")).Approved);                          // wrong publisher
        Assert.False(AppControl.Evaluate(p, new SoftwareCandidate { Name = "Google Chrome", Publisher = "Google LLC", Platform = "linux" }).Approved);
        // Other names the installer goes by.
        var c = Win("ChromeSetup", "Google LLC");
        c.OtherNames.Add("Google Chrome");
        Assert.True(AppControl.Evaluate(Policy(new AppControlRule { Id = "x", Name = "Google Chrome", Publisher = "Google LLC" }), c).Approved);

        var ranged = Policy(new AppControlRule { Id = "2", Name = "7-Zip*", VersionMin = "23.0", VersionMax = "24.99" });
        Assert.True(AppControl.Evaluate(ranged, Win("7-Zip 24.08 (x64)", version: "24.08")).Approved);
        Assert.False(AppControl.Evaluate(ranged, Win("7-Zip 19.00", version: "19.00")).Approved);
        Assert.False(AppControl.Evaluate(ranged, Win("7-Zip", version: null)).Approved);

        var sha = new string('a', 64);
        var byHash = Policy(new AppControlRule { Id = "3", Sha256 = sha });
        Assert.True(AppControl.Evaluate(byHash, Win("anything.exe", sha: sha)).Approved);
        Assert.False(AppControl.Evaluate(byHash, Win("anything.exe", sha: new string('b', 64))).Approved);

        // An empty rule matches nothing.
        Assert.False(AppControl.Matches(new AppControlRule(), Win("x")));
    }

    [Fact]
    public void Block_rules_win_over_allow_rules()
    {
        var p = Policy(new AppControlRule { Id = "a", Publisher = "Mozilla*" }, new AppControlRule { Id = "b", Action = "block", Name = "Firefox Nightly*" });
        Assert.True(AppControl.Evaluate(p, Win("Mozilla Firefox", "Mozilla")).Approved);
        var d = AppControl.Evaluate(p, Win("Firefox Nightly", "Mozilla"));
        Assert.False(d.Approved);
        Assert.Equal("blocked", d.Outcome);
        Assert.Equal("b", d.RuleId);
    }

    [Fact]
    public void Linux_updates_kernels_and_libraries_are_built_in_but_not_new_programs()
    {
        var p = Policy();
        SoftwareCandidate L(string n, string? installed = null, string? section = null) => new() { Name = n, InstalledVersion = installed, Section = section, Platform = "linux" };
        Assert.True(AppControl.Evaluate(p, L("nginx", installed: "1.24.0-2")).Approved);   // update of an installed package
        Assert.True(AppControl.Evaluate(p, L("linux-image-6.8.0-45-generic")).Approved);
        Assert.True(AppControl.Evaluate(p, L("libssl3")).Approved);
        Assert.True(AppControl.Evaluate(p, L("openssl-libs")).Approved);
        Assert.True(AppControl.Evaluate(p, L("zentriq-agent")).Approved);
        Assert.True(AppControl.Evaluate(p, L("libfoo", section: "libs")).Approved);
        Assert.False(AppControl.Evaluate(p, L("libreoffice-core")).Approved);
        Assert.False(AppControl.Evaluate(p, L("nmap")).Approved);
        Assert.True(AppControl.Evaluate(Policy(new AppControlRule { Id = "n", Name = "nmap", Platform = "linux" }), L("nmap")).Approved);
    }

    [Theory]
    [InlineData("1.10.2", "1.9", 1)]
    [InlineData("1.2", "1.2.0", 0)]
    [InlineData("1:2.3-4", "2.3-4", 0)]
    [InlineData("120.0.6099.71", "120.0.6099.109", -1)]
    [InlineData("2.0b1", "2.0", -1)]
    public void Versions_compare_part_by_part(string a, string b, int expected) => Assert.Equal(expected, AppControl.CompareVersions(a, b));

    [Theory]
    [InlineData("Google*", "Google Chrome", true)]
    [InlineData("google chrome", "Google Chrome", true)]
    [InlineData("Chrome", "Google Chrome", false)]
    [InlineData("7-Zip ??.*", "7-Zip 24.08", true)]
    [InlineData("a;b;Firefox*", "Firefox Setup", true)]
    [InlineData("a.b", "aXb", false)]
    public void Wildcards(string pattern, string value, bool expected) => Assert.Equal(expected, AppControl.Glob(pattern, value));

    [Theory]
    [InlineData("Firefox Setup 128.0.2.exe", "Firefox")]
    [InlineData("npp.8.6.Installer.x64.exe", "npp")]
    [InlineData("ChromeSetup.exe", "Chrome")]
    public void Installer_file_names(string file, string expected) => Assert.Equal(expected, AppControl.InstallerBaseName(file));

    [Fact]
    public void Stricter_modes()
    {
        Assert.Equal("block", AppControl.Stricter("audit", "block"));
        Assert.Equal("audit", AppControl.Stricter("off", "audit"));
        Assert.Equal("enforce", AppControl.StricterWdac("audit", "enforce"));
        Assert.Equal("CN Name", AppControl.SubjectName("CN=CN Name, O=Org, C=US"));
        Assert.Equal("Org Only", AppControl.SubjectName("O=Org Only, C=US"));
        Assert.Equal(new string('a', 64), AppControl.NormalizeSha256(" " + new string('A', 32) + ":" + new string('a', 32)));
        Assert.Null(AppControl.NormalizeSha256("xyz"));
    }

    [Fact]
    public void Standard_catalogue_loads_with_unique_keys_and_rules_for_each_platform()
    {
        var c = SoftwareCatalog.Standard;
        Assert.True(c.Entries.Count >= 150, $"{c.Entries.Count} entries");
        Assert.Equal(c.Entries.Count, c.Entries.Select(e => e.Key).Distinct().Count());
        Assert.All(c.Entries, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Name));
            Assert.NotEmpty(e.Platforms);
            Assert.All(e.Platforms, p => Assert.Contains(p, new[] { "windows", "linux" }));
            Assert.Equal(e.Platforms.Count, SoftwareCatalog.RulesFor(e).Count());
        });
        var chrome = c.Find("google-chrome")!;
        var rules = SoftwareCatalog.RulesFor(chrome).ToList();
        var win = rules.Single(r => r.Platform == "windows");
        var lin = rules.Single(r => r.Platform == "linux");
        var winRule = new AppControlRule { Name = win.Name, Publisher = win.Publisher, Platform = "windows" };
        Assert.True(AppControl.Matches(winRule, Win("Google Chrome", "Google LLC")));
        Assert.True(AppControl.Matches(winRule, Win("Chrome", signer: "Google LLC")));   // the installer's own name
        Assert.True(AppControl.Matches(new AppControlRule { Name = lin.Name, Platform = "linux" }, new SoftwareCandidate { Name = "google-chrome-stable", Platform = "linux" }));
        var firefox = SoftwareCatalog.RulesFor(c.Find("mozilla-firefox")!).Single(r => r.Platform == "windows");
        Assert.True(AppControl.Matches(new AppControlRule { Name = firefox.Name, Publisher = firefox.Publisher }, Win("Firefox", signer: "Mozilla Corporation")));
        Assert.Contains("Remote access", SoftwareCatalog.Categories(c.Entries));
    }
}

/// <summary>Software Deployment in PostgreSQL: groups, the policy each device receives, the inventory safety net.</summary>
public sealed class SoftwareDeploymentDbTests : IAsyncLifetime
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

    [DbFact]
    public async Task Policy_follows_groups_device_rules_and_the_safety_net_reports_and_removes()
    {
        await using var ds = NpgsqlDataSource.Create(_db.AppConnection);
        var dept = (Guid)(await Scalar(ds, "INSERT INTO zentriq.departments (name) VALUES ('Engineering') RETURNING id"))!;
        var asset = (Guid)(await Scalar(ds, "INSERT INTO zentriq.assets (hostname, category, status, os_family, department_id) VALUES ('dev-01', 'endpoint', 'active', 'windows', @d) RETURNING id", ("d", dept)))!;
        var other = (Guid)(await Scalar(ds, "INSERT INTO zentriq.assets (hostname, category, status, os_family) VALUES ('fin-01', 'endpoint', 'active', 'windows') RETURNING id"))!;
        var agent = (Guid)(await Scalar(ds, "INSERT INTO zentriq.agent_registrations (asset_id, agent_version, status, capabilities) VALUES (@a, '1.1.2', 'active', ARRAY['app-control','software-uninstall']) RETURNING id", ("a", asset)))!;

        // Off by default; audit for all devices, block for the Engineering group with WDAC in audit.
        var s = await AppControlStore.LoadSettingsAsync(ds, default);
        Assert.Equal("off", s.Mode);
        Assert.Equal(AppControl.DefaultMessage, s.Message);
        s.Mode = "audit"; s.AutoRemove = true; s.EnabledAt = DateTimeOffset.UtcNow.AddDays(-1);
        await AppControlStore.SaveSettingsAsync(ds, s, default);
        var group = (Guid)(await Scalar(ds, "INSERT INTO zentriq.device_groups (name, department_ids, app_mode, wdac_mode) VALUES ('Engineering', ARRAY[@d]::uuid[], 'block', 'audit') RETURNING id", ("d", dept)))!;
        await Scalar(ds, "INSERT INTO zentriq.app_control_rules (label, name_pattern, platform) VALUES ('7-Zip', '7-Zip*', 'windows')");
        await Scalar(ds, "INSERT INTO zentriq.app_control_rules (label, name_pattern, group_id) VALUES ('Visual Studio Code', 'Microsoft Visual Studio Code*', @g)", ("g", group));
        await Scalar(ds, "INSERT INTO zentriq.app_control_rules (label, name_pattern, asset_id) VALUES ('Wireshark on dev-01', 'Wireshark*', @a)", ("a", asset));
        await Scalar(ds, "INSERT INTO zentriq.app_control_rules (label, name_pattern, expires_at) VALUES ('Expired', 'Old*', now() - interval '1 day')");
        await Scalar(ds, "INSERT INTO zentriq.app_control_rules (label, name_pattern, platform) VALUES ('Linux only', 'nmap', 'linux')");
        var r1 = await AppControlStore.BumpRevisionAsync(ds, default);
        Assert.True(r1 > s.Revision);

        var mine = await AppControlStore.PolicyForAsync(ds, asset, "windows-x64", default);
        Assert.Equal("block", mine.Mode);           // the group's mode
        Assert.Equal("audit", mine.Wdac);
        Assert.True(mine.AutoRemove);
        Assert.Equal(r1, mine.Revision);
        Assert.Equal(new[] { "7-Zip", "Visual Studio Code", "Wireshark on dev-01" }, mine.Rules.Select(r => r.Label).Order().ToArray());
        var theirs = await AppControlStore.PolicyForAsync(ds, other, "windows-x64", default);
        Assert.Equal("audit", theirs.Mode);
        Assert.Equal("off", theirs.Wdac);
        Assert.False(theirs.AutoRemove);            // removal only in block mode
        Assert.Equal(new[] { "7-Zip" }, theirs.Rules.Select(r => r.Label).ToArray());
        var linux = await AppControlStore.PolicyForAsync(ds, null, "linux-x64", default);
        Assert.Equal(new[] { "Linux only" }, linux.Rules.Select(r => r.Label).ToArray());
        Assert.Equal("off", linux.Wdac);

        // Safety net: software installed after application control was turned on and not approved.
        var software = new List<SoftwareItem>
        {
            new() { Name = "7-Zip 24.08 (x64)", Version = "24.08", InstallDate = DateTimeOffset.UtcNow },
            new() { Name = "Some Game", Publisher = "Games Inc", Version = "1.0", InstallDate = DateTimeOffset.UtcNow },
            new() { Name = "Legacy Tool", Version = "2.0", InstallDate = DateTimeOffset.UtcNow.AddYears(-2) },   // before: not touched
            new() { Name = "Microsoft Visual C++ 2015-2022 Redistributable (x64)", Publisher = "Microsoft Corporation", InstallDate = DateTimeOffset.UtcNow },
        };
        Assert.Equal(1, await AppControlStore.CheckInventoryAsync(ds, asset, agent, "windows-x64", software, ["app-control", "software-uninstall"], default));
        Assert.Equal(0, await AppControlStore.CheckInventoryAsync(ds, asset, agent, "windows-x64", software, ["app-control", "software-uninstall"], default));   // once
        Assert.Equal("Some Game", await Scalar(ds, "SELECT name FROM zentriq.app_control_events WHERE asset_id = @a AND kind = 'detected'", ("a", asset)));
        var action = (Guid)(await Scalar(ds, "SELECT id FROM zentriq.asset_actions WHERE asset_id = @a AND kind = 'uninstall' AND target = 'Some Game'", ("a", asset)))!;
        await AppControlStore.RecordRemovalAsync(ds, action, true, "'Some Game' was uninstalled.", default);
        Assert.Equal(1L, await Scalar(ds, "SELECT count(*) FROM zentriq.app_control_events WHERE asset_id = @a AND kind = 'removed'", ("a", asset)));

        // Group membership by department, location and device; and only for its platform.
        var groups = await AppControlStore.GroupsOfAssetAsync(ds, asset, default);
        Assert.Equal("Engineering", Assert.Single(groups).Name);
        await Scalar(ds, "UPDATE zentriq.device_groups SET platform = 'linux' WHERE id = @g", ("g", group));
        Assert.Empty(await AppControlStore.GroupsOfAssetAsync(ds, asset, default));

        // A device's agent is woken while it has an older policy revision (it then fetches the new one).
        Assert.Equal(1L, await Scalar(ds, "SELECT count(*) FROM zentriq.agent_registrations WHERE id = @id AND COALESCE(app_policy_revision, -1) <> (SELECT (value ->> 'revision')::bigint FROM zentriq.system_settings WHERE key = 'appcontrol.settings')", ("id", agent)));

        // Permissions: deploy.view / deploy.manage exist and the administrator role has them.
        Assert.Equal(2L, await Scalar(ds, "SELECT count(*) FROM zentriq.role_permissions WHERE role_id = 'admin' AND permission_code IN ('deploy.view','deploy.manage')"));
    }
}
