using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Zentriq.Infrastructure.Database;
using Zentriq.Infrastructure.Deployment;

namespace Zentriq.Tests;

/// <summary>The tray icon: Software Center (self-service) and help requests.</summary>
public sealed class TrayDbTests : IAsyncLifetime
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

    private static async Task<Guid> Package(NpgsqlDataSource ds, string name, bool offered, bool approval) =>
        (Guid)(await Scalar(ds, """
            INSERT INTO zentriq.deployment_packages (name, version, platform, format, file_name, size, sha256, self_service, self_service_approval, self_service_category)
            VALUES (@n, '1.0', 'windows', 'exe', 'setup.exe', 1000, repeat('a', 64), @o, @ap, 'Tools') RETURNING id
            """, ("n", name), ("o", offered), ("ap", approval)))!;

    [DbFact]
    public async Task Software_center_installs_at_once_or_after_approval()
    {
        await using var ds = NpgsqlDataSource.Create(_db.AppConnection);
        var asset = (Guid)(await Scalar(ds, "INSERT INTO zentriq.assets (hostname, category, status, os_family) VALUES ('PC-41', 'endpoint', 'active', 'windows') RETURNING id"))!;
        var agent = (Guid)(await Scalar(ds, "INSERT INTO zentriq.agent_registrations (asset_id, agent_version, status, capabilities, platform, last_checkin_at) VALUES (@a, '1.1.1.118', 'active', '{software-deploy}', 'windows-x64', now()) RETURNING id", ("a", asset)))!;
        var free = await Package(ds, "7-Zip", true, false);
        var gated = await Package(ds, "Visio Viewer", true, true);
        await Package(ds, "Not Offered", false, false);
        var groupOnly = await Package(ds, "Finance Tool", true, false);
        var group = (Guid)(await Scalar(ds, "INSERT INTO zentriq.device_groups (name) VALUES ('Finance') RETURNING id"))!;
        await Scalar(ds, "UPDATE zentriq.deployment_packages SET self_service_group_ids = ARRAY[@g]::uuid[] WHERE id = @p", ("g", group), ("p", groupOnly));

        var cat = await SelfService.CatalogAsync(ds, asset, "windows", default);
        Assert.Equal(["7-Zip", "Visio Viewer"], cat.Select(c => c.Name).OrderBy(n => n));
        Assert.All(cat, c => Assert.Equal("available", c.State));
        Assert.Empty(await SelfService.CatalogAsync(ds, asset, "linux", default));

        // At once: a deployment of this one device; the agent installs it as SYSTEM.
        var r = await SelfService.RequestAsync(ds, free, asset, agent, "windows", @"CONTOSO\jane", null, _ => true, default);
        Assert.Equal("installing", r.State);
        Assert.Equal("installing", (await SelfService.CatalogAsync(ds, asset, "windows", default)).Single(c => c.Id == free).State);
        Assert.Equal("installing", (await SelfService.RequestAsync(ds, free, asset, agent, "windows", @"CONTOSO\jane", null, _ => true, default)).State);   // no second job
        Assert.Equal(1L, await Scalar(ds, "SELECT count(*) FROM zentriq.asset_actions WHERE kind = 'deploy' AND target = @p", ("p", free.ToString())));
        await Scalar(ds, "UPDATE zentriq.asset_actions SET status = 'succeeded', completed_at = now() WHERE target = @p", ("p", free.ToString()));
        Assert.Equal("installed", (await SelfService.CatalogAsync(ds, asset, "windows", default)).Single(c => c.Id == free).State);

        // Needs approval: a request; approved, it installs.
        var q = await SelfService.RequestAsync(ds, gated, asset, agent, "windows", @"CONTOSO\jane", "for the network drawing", _ => true, default);
        Assert.Equal("pending", q.State);
        Assert.Equal(0L, await Scalar(ds, "SELECT count(*) FROM zentriq.asset_actions WHERE target = @p", ("p", gated.ToString())));
        var request = (Guid)(await Scalar(ds, "SELECT id FROM zentriq.self_service_requests WHERE package_id = @p", ("p", gated)))!;
        Assert.Equal("Visio Viewer", await SelfService.DecideAsync(ds, request, true, null, null, _ => true, default));
        Assert.Null(await SelfService.DecideAsync(ds, request, true, null, null, _ => true, default));   // already decided
        Assert.Equal("installing", (await SelfService.CatalogAsync(ds, asset, "windows", default)).Single(c => c.Id == gated).State);

        // Not offered to this device, or the file is missing on the server.
        Assert.Equal("unavailable", (await SelfService.RequestAsync(ds, groupOnly, asset, agent, "windows", null, null, _ => true, default)).State);
        await Scalar(ds, "UPDATE zentriq.asset_actions SET status = 'failed', completed_at = now(), detail = 'exit code 7' WHERE target = @p", ("p", free.ToString()));
        Assert.Equal("failed", (await SelfService.RequestAsync(ds, free, asset, agent, "windows", null, null, _ => false, default)).State);
    }

    [DbFact]
    public async Task Help_requests_are_numbered_and_tray_settings_kept()
    {
        await using var ds = NpgsqlDataSource.Create(_db.AppConnection);
        var asset = (Guid)(await Scalar(ds, "INSERT INTO zentriq.assets (hostname, category, status) VALUES ('PC-42', 'endpoint', 'active') RETURNING id"))!;
        var a = await SelfService.CreateHelpAsync(ds, asset, @"CONTOSO\jane", "Outlook does not start", "ext. 4711", default);
        var b = await SelfService.CreateHelpAsync(ds, asset, @"CONTOSO\jane", "Printer offline", null, default);
        Assert.True(b > a);
        Assert.Equal("open", await Scalar(ds, "SELECT status FROM zentriq.help_requests WHERE number = @n", ("n", a)));

        Assert.True((await SelfService.SettingsAsync(ds, default)).Enabled);
        await SelfService.SaveSettingsAsync(ds, new TraySettings { Enabled = true, SoftwareCenter = false, RemoteHelp = true, CompanyName = "  Contoso IT  ", SupportText = "" }, default);
        var s = await SelfService.SettingsAsync(ds, default);
        Assert.False(s.SoftwareCenter);
        Assert.Equal("Contoso IT", s.CompanyName);
        Assert.Null(s.SupportText);
    }
}
