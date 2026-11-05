using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Zentriq.Infrastructure.Database;
using Zentriq.Infrastructure.Inventory;
using Zentriq.Infrastructure.Licenses;
using Zentriq.Infrastructure.Secrets;
using Zentriq.Infrastructure.WebApps;
using Zentriq.Inventory;

namespace Zentriq.Tests;

/// <summary>Software License Management, Web Apps &amp; Accounts and the network additions of build 111 (no database).</summary>
public sealed class LicensesAndWebAppsTests
{
    private static ComplianceRow Row(string metric = "device", long devices = 0, long users = 0, long entitled = 0, long licenses = 1, bool unlimited = false,
        bool needs = true, string kind = "installed", long? cloudConsumed = null, long? cloudUnits = null, decimal? unitCost = null) =>
        new() { Name = "X", Metric = metric, Devices = devices, Users = users, Entitled = entitled, Licenses = licenses, ValidLicenses = licenses, Unlimited = unlimited, NeedsLicense = needs,
                Kind = kind, CloudConsumed = cloudConsumed, CloudUnits = cloudUnits, UnitCost = unitCost };

    [Fact]
    public void Licence_position_follows_the_metric_and_the_licences()
    {
        var short1 = LicenseCompliance.Evaluate(Row(devices: 12, entitled: 10, unitCost: 100m), 25);
        Assert.Equal("shortfall", short1.Status);
        Assert.Equal(2, short1.Shortfall);
        Assert.Equal(200m, short1.ShortfallCost);

        Assert.Equal("compliant", LicenseCompliance.Evaluate(Row(devices: 9, entitled: 10), 25).Status);
        var over = LicenseCompliance.Evaluate(Row(devices: 4, entitled: 10, unitCost: 50m), 25);
        Assert.Equal("over_licensed", over.Status);
        Assert.Equal(6, over.Unused);
        Assert.Equal(300m, over.UnusedValue);

        // Per user: users, not devices.
        Assert.Equal("compliant", LicenseCompliance.Evaluate(Row("user", devices: 30, users: 10, entitled: 10), 25).Status);
        Assert.Equal("unlicensed", LicenseCompliance.Evaluate(Row(devices: 3, licenses: 0), 25).Status);
        Assert.Equal("unused", LicenseCompliance.Evaluate(Row(devices: 0, licenses: 0), 25).Status);
        Assert.Equal("not_required", LicenseCompliance.Evaluate(Row(devices: 50, licenses: 0, needs: false), 25).Status);
        Assert.Equal("compliant", LicenseCompliance.Evaluate(Row(devices: 5000, entitled: 0, unlimited: true), 25).Status);
        Assert.Equal("not_measured", LicenseCompliance.Evaluate(Row("concurrent", devices: 80, entitled: 5), 25).Status);

        // Cloud: what Microsoft 365 says was bought counts when no licence is recorded here.
        var cloud = LicenseCompliance.Evaluate(Row(kind: "cloud", licenses: 0, cloudConsumed: 120, cloudUnits: 100), 25);
        Assert.Equal("shortfall", cloud.Status);
        Assert.Equal(20, cloud.Shortfall);
    }

    [Fact]
    public void Wildcards_and_the_licence_catalogue_match_installed_names()
    {
        Assert.True(LicenseCompliance.Like("Microsoft Visio Professional 2021", "Microsoft Visio*"));
        Assert.True(LicenseCompliance.Like("microsoft visio", "Microsoft Visio*"));
        Assert.False(LicenseCompliance.Like("Microsoft Visio Viewer", "Microsoft Project*"));
        Assert.True(LicenseCompliance.Like("AutoCAD 2025", "AutoCAD 20??"));

        var cat = LicenseCatalog.Standard;
        Assert.True(cat.Products.Count >= 40);
        Assert.Equal(cat.Products.Count, cat.Products.Select(p => p.Key).Distinct().Count());
        var visio = cat.Find("ms-visio")!;
        Assert.True(LicenseCatalog.Matches(visio, "Microsoft Visio Professional 2021", "Microsoft Corporation"));
        Assert.False(LicenseCatalog.Matches(visio, "Microsoft Visio Viewer 2016", "Microsoft Corporation"));
        Assert.False(LicenseCatalog.Matches(visio, "Microsoft Visio Professional 2021", "Someone Else"));
        var idea = cat.Find("intellij-ultimate")!;
        Assert.True(LicenseCatalog.Matches(idea, "IntelliJ IDEA 2024.2.1", "JetBrains s.r.o."));
        Assert.False(LicenseCatalog.Matches(idea, "IntelliJ IDEA Community Edition 2024.2.1", "JetBrains s.r.o."));
        Assert.All(cat.Products, p => Assert.NotEmpty(p.NamePatterns));
    }

    [Fact]
    public void Web_app_catalogue_and_host_matching()
    {
        var cat = WebAppCatalog.Standard;
        Assert.True(cat.Apps.Count >= 80);
        Assert.Equal(cat.Apps.Count, cat.Apps.Select(a => a.Key).Distinct().Count());
        Assert.All(cat.Apps.SelectMany(a => a.Domains), d => Assert.Equal(d, WebAppCatalog.CleanDomain(d)));
        Assert.True(WebAppCatalog.HostMatches("acme.slack.com", "slack.com"));
        Assert.True(WebAppCatalog.HostMatches("slack.com", "slack.com"));
        Assert.False(WebAppCatalog.HostMatches("notslack.com", "slack.com"));
        // The most specific domain wins: Teams, not Microsoft 365.
        var all = cat.Apps.SelectMany(a => a.Domains);
        Assert.Equal("teams.microsoft.com", WebAppCatalog.BestDomain("teams.microsoft.com", all));
        Assert.Equal("app.example.com", WebAppCatalog.CleanDomain("https://App.Example.com:8443/login?x=1"));
        Assert.Null(WebAppCatalog.CleanDomain("not a domain"));

        Assert.Equal("teams.microsoft.com", WindowsCollector.WebHost("Teams.Microsoft.com."));
        Assert.Null(WindowsCollector.WebHost("10.1.2.3"));
        Assert.Null(WindowsCollector.WebHost("1.2.168.192.in-addr.arpa"));
        Assert.Null(WindowsCollector.WebHost("printer.local"));
        Assert.Null(WindowsCollector.WebHost("wpad"));
    }

    [Fact]
    public void Windows_activation_channels_and_status_names()
    {
        Assert.Equal("Volume:GVLK", WindowsCollector.ChannelFromDescription("Windows(R) Operating System, VOLUME_KMSCLIENT channel"));
        Assert.Equal("Retail", WindowsCollector.ChannelFromDescription("Windows(R) Operating System, RETAIL channel"));
        Assert.Equal("OEM", WindowsCollector.ChannelFromDescription("Windows(R) Operating System, OEM_DM channel"));
        Assert.Equal("Volume:MAK", WindowsCollector.ChannelFromDescription("Office 21, VOLUME_MAK channel"));
        Assert.Equal("licensed", WindowsCollector.LicenseStatusName(1));
        Assert.Equal("notification", WindowsCollector.LicenseStatusName(5));
        Assert.Equal("unknown", WindowsCollector.LicenseStatusName(null));
    }

    [Fact]
    public void Account_csv_exports_are_read_with_their_own_column_names()
    {
        var (rows, err) = AccountCsv.Parse("Email Address,Full Name,Role,Status,Last Sign In,License\nanna@example.com,Anna Rao,Super Admin,Active,2026-10-02,Business\n" +
                                                         "\"bob@example.com\",\"Bob, Jr.\",Member,Suspended,,\nanna@example.com,Anna R,Admin,active,2026-10-03,Business\n");
        Assert.Null(err);
        Assert.Equal(2, rows.Count);
        var anna = rows.Single(r => r.UserName == "anna@example.com");
        Assert.Equal("admin", anna.Role);
        Assert.Equal("active", anna.Status);
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero), anna.LastLogin);
        var bob = rows.Single(r => r.UserName == "bob@example.com");
        Assert.Equal("Bob, Jr.", bob.DisplayName);
        Assert.Equal("disabled", bob.Status);
        Assert.NotNull(AccountCsv.Parse("name,department\nAnna,IT\n").Error);
    }

    [Fact]
    public void Subnets_vendors_and_sku_names()
    {
        Assert.Equal("10.1.20.0/24", NetworkSnmp.Subnet("10.1.20.5", "255.255.255.0"));
        Assert.Equal("172.16.0.0/22", NetworkSnmp.Subnet("172.16.3.1", "255.255.252.0"));
        Assert.Null(NetworkSnmp.Subnet("10.0.0.1", "255.255.255.255"));   // host route
        Assert.Null(NetworkSnmp.Subnet("10.0.0.1", "255.0.255.0"));       // not contiguous
        var v = MacVendors.Parse("Registry,Assignment,Organization Name,Organization Address\nMA-L,00500C,\"e-Tek Labs, Inc.\",\"Street\n2nd line\"\nMA-M,8C1F64A,Example Ltd,Somewhere\n");
        Assert.Equal(2, v.Count);
        Assert.Equal(("00500c", "e-Tek Labs, Inc."), v[0]);
        Assert.Equal("8c1f64a", v[1].Prefix);
        Assert.Equal("Microsoft 365 E3", SkuNames.Name("SPE_E3"));
        Assert.Equal("CONTOSO CUSTOM SKU", SkuNames.Name("CONTOSO_CUSTOM_SKU"));
    }
}

/// <summary>Licence position, alerts, web app usage and accounts, the connected devices register and Microsoft 365 against PostgreSQL.</summary>
public sealed class LicensesAndWebAppsDbTests : IAsyncLifetime
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

    private static async Task<Guid> Device(NpgsqlDataSource ds, string name, string user, params (string Name, string Publisher, string Version)[] software)
    {
        var id = (Guid)(await Scalar(ds, "INSERT INTO zentriq.assets (hostname, category, status, os_family) VALUES (@h, 'endpoint', 'active', 'windows') RETURNING id", ("h", name)))!;
        foreach (var s in software)
            await Scalar(ds, "INSERT INTO zentriq.software_inventory (asset_id, name, publisher, version) VALUES (@a, @n, @p, @v)", ("a", id), ("n", s.Name), ("p", s.Publisher), ("v", s.Version));
        await Scalar(ds, "INSERT INTO zentriq.asset_inventory_documents (asset_id, source, collected_at, document) VALUES (@a, 'agent', now(), jsonb_build_object('users', jsonb_build_object('loggedOn', @u::text)))",
            ("a", id), ("u", user));
        return id;
    }

    [DbFact]
    public async Task Licence_position_alerts_and_their_resolution()
    {
        await using var ds = NpgsqlDataSource.Create(_db.AppConnection);
        await Device(ds, "pc-01", @"CORP\anna", ("Microsoft Visio Professional 2021", "Microsoft Corporation", "16.0.1"));
        await Device(ds, "pc-02", @"CORP\anna", ("Microsoft Visio Standard 2019", "Microsoft Corporation", "16.0.2"));
        var pc3 = await Device(ds, "pc-03", @"CORP\bob", ("Microsoft Visio Professional 2021", "Microsoft Corporation", "16.0.1"), ("Microsoft Visio Viewer 2016", "Microsoft Corporation", "16"));
        await Device(ds, "pc-04", @"CORP\carl", ("Microsoft Visio Viewer 2016", "Microsoft Corporation", "16"), ("Adobe Photoshop 2025", "Adobe Inc.", "26.0"));

        var visio = (Guid)(await Scalar(ds, """
            INSERT INTO zentriq.sam_products (name, publisher, name_patterns, exclude_patterns, publisher_patterns, metric)
            VALUES ('Microsoft Visio', 'Microsoft', ARRAY['Microsoft Visio*'], ARRAY['*Viewer*'], ARRAY['Microsoft*'], 'device') RETURNING id
            """))!;
        var photoshop = (Guid)(await Scalar(ds, "INSERT INTO zentriq.sam_products (name, name_patterns, metric) VALUES ('Adobe Photoshop', ARRAY['Adobe Photoshop*'], 'user') RETURNING id"))!;
        var lic = (Guid)(await Scalar(ds, """
            INSERT INTO zentriq.sam_licenses (product_id, name, license_type, quantity, unit_cost, end_date, notify_days)
            VALUES (@p, 'Visio 2 seats', 'subscription', 2, 150, current_date + 10, 30) RETURNING id
            """, ("p", visio)))!;

        await using (var conn = await ds.OpenConnectionAsync())
        {
            var rows = await LicenseCompliance.LoadAsync(conn, null, 25, default);
            var v = rows.Single(r => r.Id == visio);
            Assert.Equal(3, v.Devices);           // the viewer is excluded
            Assert.Equal(2, v.Users);
            Assert.Equal("shortfall", v.Status);
            Assert.Equal(1, v.Shortfall);
            Assert.Equal(150m, v.ShortfallCost);
            Assert.Equal("unlicensed", rows.Single(r => r.Id == photoshop).Status);
        }

        var r1 = await LicenseEvaluator.RunAsync(ds, default);
        Assert.True(r1.Opened >= 3);
        Assert.Equal(1L, await Scalar(ds, "SELECT count(*) FROM zentriq.sam_alerts WHERE key = @k AND resolved_at IS NULL", ("k", $"shortfall:{visio}")));
        Assert.Equal(1L, await Scalar(ds, "SELECT count(*) FROM zentriq.sam_alerts WHERE key = @k AND resolved_at IS NULL", ("k", $"unlicensed:{photoshop}")));
        Assert.Equal("medium", await Scalar(ds, "SELECT severity FROM zentriq.sam_alerts WHERE kind = 'expiring' AND license_id = @l", ("l", lic)));
        // Evaluating again opens nothing new.
        Assert.Equal(0, (await LicenseEvaluator.RunAsync(ds, default)).Opened);

        // One more seat and a renewal: the shortfall and the expiry alerts are resolved.
        await Scalar(ds, "UPDATE zentriq.sam_licenses SET quantity = 3, end_date = current_date + 200 WHERE id = @l", ("l", lic));
        var r2 = await LicenseEvaluator.RunAsync(ds, default);
        Assert.True(r2.Resolved >= 2);
        Assert.Equal(0L, await Scalar(ds, "SELECT count(*) FROM zentriq.sam_alerts WHERE (key = @k OR kind = 'expiring') AND resolved_at IS NULL", ("k", $"shortfall:{visio}")));

        // An expired licence no longer counts.
        await Scalar(ds, "UPDATE zentriq.sam_licenses SET end_date = current_date - 1 WHERE id = @l", ("l", lic));
        await LicenseEvaluator.RunAsync(ds, default);
        Assert.Equal("high", await Scalar(ds, "SELECT severity FROM zentriq.sam_alerts WHERE kind = 'expired' AND license_id = @l AND resolved_at IS NULL", ("l", lic)));
        Assert.Equal(1L, await Scalar(ds, "SELECT count(*) FROM zentriq.sam_alerts WHERE key = @k AND resolved_at IS NULL", ("k", $"unlicensed:{visio}")));

        // Windows not activated.
        await Scalar(ds, """UPDATE zentriq.asset_inventory_documents SET document = document || '{"licenses":[{"product":"Windows(R), Professional edition","application":"windows","status":"notification","channel":"Retail"}]}'::jsonb WHERE asset_id = @a""", ("a", pc3));
        await LicenseEvaluator.RunAsync(ds, default);
        Assert.Equal(1L, await Scalar(ds, "SELECT count(*) FROM zentriq.sam_alerts WHERE kind = 'not_genuine' AND asset_id = @a AND resolved_at IS NULL", ("a", pc3)));

        // In-app notification once, for users who can see licences.
        var notifier = new LicenseNotifier(ds, new Zentriq.Infrastructure.Mail.MailService(ds, new CredentialProtector(() => new AppSecrets { CredentialKey = SecretStore.NewKey() })));
        var sent = await notifier.RunAsync(default);
        Assert.True(sent.InApp >= 1);
        Assert.Equal(0, (await notifier.RunAsync(default)).Alerts);
        Assert.True((long)(await Scalar(ds, "SELECT count(*) FROM zentriq.notifications WHERE permission = 'licenses.view'"))! >= 1);

        // Permissions exist and the administrator role has them.
        Assert.Equal(4L, await Scalar(ds, "SELECT count(*) FROM zentriq.role_permissions WHERE role_id = 'admin' AND permission_code IN ('licenses.view','licenses.manage','webapps.view','webapps.manage')"));
    }

    [DbFact]
    public async Task Web_app_usage_accounts_and_orphans()
    {
        await using var ds = NpgsqlDataSource.Create(_db.AppConnection);
        var pc = await Device(ds, "pc-10", @"CORP\anna");
        await using (var conn = await ds.OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            var n = await WebAppUsage.RecordAsync(conn, tx, pc, ["acme.slack.com", "teams.microsoft.com", "outlook.office365.com", "intranet.corp.example", "www.google.com"], @"CORP\anna", default);
            Assert.Equal(3, n);   // Slack, Teams, Microsoft 365; the rest is discarded
            await tx.CommitAsync();
        }
        Assert.Equal("review", await Scalar(ds, "SELECT status FROM zentriq.webapps WHERE catalog_key = 'slack'"));
        Assert.Equal(0L, await Scalar(ds, "SELECT count(*) FROM zentriq.webapps WHERE name ILIKE '%google%'"));
        var slack = (Guid)(await Scalar(ds, "SELECT id FROM zentriq.webapps WHERE catalog_key = 'slack'"))!;

        // Unsanctioned use raises an alert; an active account of someone who left is orphaned.
        await Scalar(ds, "UPDATE zentriq.webapps SET status = 'unsanctioned' WHERE id = @a", ("a", slack));
        await Scalar(ds, "INSERT INTO zentriq.lifecycle_people (name, email, status) VALUES ('Bob Left', 'bob@example.com', 'left')");
        await Scalar(ds, "INSERT INTO zentriq.webapp_accounts (app_id, user_name, role, created_at) VALUES (@a, 'bob@example.com', 'admin', now() - interval '200 days')", ("a", slack));
        await Scalar(ds, "INSERT INTO zentriq.webapp_accounts (app_id, user_name, created_at, last_login) VALUES (@a, 'old@example.com', now() - interval '200 days', now() - interval '150 days')", ("a", slack));
        await Scalar(ds, "INSERT INTO zentriq.webapp_accounts (app_id, user_name, created_at, last_login) VALUES (@a, 'anna@example.com', now() - interval '200 days', now() - interval '2 days')", ("a", slack));
        await LicenseEvaluator.RunAsync(ds, default);
        Assert.Equal("medium", await Scalar(ds, "SELECT severity FROM zentriq.sam_alerts WHERE key = @k AND resolved_at IS NULL", ("k", $"webapp_unsanctioned:{slack}")));
        Assert.Equal("high", await Scalar(ds, "SELECT severity FROM zentriq.sam_alerts WHERE kind = 'webapp_orphaned' AND app_id = @a AND resolved_at IS NULL", ("a", slack)));
        Assert.Equal(1L, await Scalar(ds, "SELECT count(*) FROM zentriq.sam_alerts WHERE key = @k AND resolved_at IS NULL", ("k", $"webapp_inactive:{slack}")));
        Assert.Contains("2 account(s)", (string)(await Scalar(ds, "SELECT title FROM zentriq.sam_alerts WHERE key = @k", ("k", $"webapp_inactive:{slack}")))!);   // bob and old

        // Discovery can be turned off.
        var s = await LicenseSettings.LoadAsync(ds, default);
        s.WebDiscovery = false;
        await s.SaveAsync(ds, null, default);
        await using (var conn = await ds.OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
            Assert.Equal(0, await WebAppUsage.RecordAsync(conn, tx, pc, ["zoom.us"], null, default));
    }

    [DbFact]
    public async Task Connected_devices_register_tracks_ports_vendors_and_moves()
    {
        await using var ds = NpgsqlDataSource.Create(_db.AppConnection);
        var sw = (Guid)(await Scalar(ds, "INSERT INTO zentriq.assets (hostname, category, status, device_type) VALUES ('sw-01', 'network_device', 'active', 'switch') RETURNING id"))!;
        var pc = (Guid)(await Scalar(ds, "INSERT INTO zentriq.assets (hostname, category, status, mac_address) VALUES ('pc-20', 'endpoint', 'active', 'aa:bb:cc:00:00:01') RETURNING id"))!;
        await Scalar(ds, "INSERT INTO zentriq.network_devices (asset_id) VALUES (@s)", ("s", sw));
        await Scalar(ds, "INSERT INTO zentriq.network_interfaces (asset_id, if_index, name, type, oper) VALUES (@s, 1, 'Gi1/0/1', 'ethernet', 'up'), (@s, 2, 'Gi1/0/2', 'ethernet', 'up')", ("s", sw));
        await Scalar(ds, "INSERT INTO zentriq.network_mac_table (asset_id, mac, vlan, if_index) VALUES (@s, 'aa:bb:cc:00:00:01', 20, 1), (@s, '00:50:0c:12:34:56', 20, 2)", ("s", sw));
        await Scalar(ds, "INSERT INTO zentriq.network_arp (asset_id, ip, mac) VALUES (@s, '10.1.20.15', '00:50:0c:12:34:56')", ("s", sw));
        await MacVendors.StoreAsync(ds, [("00500c", "e-Tek Labs, Inc.")], "test", default);

        await using (var conn = await ds.OpenConnectionAsync()) await NetworkStore.RefreshAsync(conn, null, default);
        Assert.Equal(pc, await Scalar(ds, "SELECT asset_id FROM zentriq.network_endpoints WHERE mac = 'aa:bb:cc:00:00:01'"));
        Assert.Equal("10.1.20.15", await Scalar(ds, "SELECT host(ip) FROM zentriq.network_endpoints WHERE mac = '00:50:0c:12:34:56'"));
        Assert.Equal("e-Tek Labs, Inc.", await Scalar(ds, "SELECT vendor FROM zentriq.network_endpoints WHERE mac = '00:50:0c:12:34:56'"));
        Assert.Equal(true, await Scalar(ds, "SELECT baseline FROM zentriq.network_endpoints WHERE mac = '00:50:0c:12:34:56'"));   // first build: not alerted

        // The unknown device moves to port 1 (and a new device appears): recorded as a move; the new one is not baseline.
        await Scalar(ds, "UPDATE zentriq.network_mac_table SET if_index = 1 WHERE mac = '00:50:0c:12:34:56'");
        await Scalar(ds, "INSERT INTO zentriq.network_mac_table (asset_id, mac, vlan, if_index) VALUES (@s, '00:50:0c:99:99:99', 20, 2)", ("s", sw));
        await Scalar(ds, "UPDATE zentriq.network_devices SET collected_at = now() + interval '1 minute' WHERE asset_id = @s", ("s", sw));
        await using (var conn = await ds.OpenConnectionAsync()) await NetworkStore.RefreshAsync(conn, null, default);
        Assert.Equal("Gi1/0/2", await Scalar(ds, "SELECT prev_port FROM zentriq.network_endpoints WHERE mac = '00:50:0c:12:34:56' AND moved_at IS NOT NULL"));
        Assert.Equal(false, await Scalar(ds, "SELECT baseline FROM zentriq.network_endpoints WHERE mac = '00:50:0c:99:99:99'"));
        Assert.Equal("Private (randomised address)", await Scalar(ds, "SELECT zentriq.mac_vendor('02:11:22:33:44:55')"));
    }

    [DbFact]
    public async Task Microsoft_365_licences_users_and_products()
    {
        await using var ds = NpgsqlDataSource.Create(_db.AppConnection);
        var keys = new AppSecrets { CredentialKey = SecretStore.NewKey() };
        var protector = new CredentialProtector(() => keys);
        var conn0 = Guid.NewGuid();
        await Scalar(ds, "INSERT INTO zentriq.sam_cloud_connections (id, name, tenant_id, client_id, client_protected) VALUES (@id, 'Contoso', 'contoso.onmicrosoft.com', @c, @s)",
            ("id", conn0), ("c", Guid.NewGuid().ToString()), ("s", M365Sync.Protect(protector, conn0, "value")));
        const string e3 = "05e9a617-0261-4cee-bb44-138d3ef5d965";
        var next = DateTimeOffset.UtcNow.AddDays(12).ToString("O");
        var skus = "{\"value\":[{\"skuId\":\"" + e3 + "\",\"skuPartNumber\":\"SPE_E3\",\"capabilityStatus\":\"Enabled\",\"consumedUnits\":3,\"prepaidUnits\":{\"enabled\":2,\"suspended\":0,\"warning\":0}}," +
                   "{\"skuId\":\"x\",\"skuPartNumber\":\"FLOW_FREE\",\"capabilityStatus\":\"Enabled\",\"consumedUnits\":5,\"prepaidUnits\":{\"enabled\":10000,\"suspended\":0,\"warning\":0}}]}";
        var subs = "{\"value\":[{\"skuId\":\"" + e3 + "\",\"status\":\"Enabled\",\"nextLifecycleDateTime\":\"" + next + "\",\"isTrial\":false}]}";
        var users = "{\"value\":[" +
            "{\"id\":\"1\",\"userPrincipalName\":\"anna@contoso.com\",\"displayName\":\"Anna\",\"accountEnabled\":true,\"assignedLicenses\":[{\"skuId\":\"" + e3 + "\"}],\"signInActivity\":{\"lastSignInDateTime\":\"2026-10-01T10:00:00Z\"}}," +
            "{\"id\":\"2\",\"userPrincipalName\":\"bob@contoso.com\",\"displayName\":\"Bob\",\"accountEnabled\":false,\"assignedLicenses\":[{\"skuId\":\"" + e3 + "\"}]}," +
            "{\"id\":\"3\",\"userPrincipalName\":\"carl@contoso.com\",\"displayName\":\"Carl\",\"accountEnabled\":true,\"assignedLicenses\":[{\"skuId\":\"" + e3 + "\"}]}]}";
        var handler = new Graph(req =>
        {
            var u = req.RequestUri!.AbsoluteUri;
            if (u.Contains("/oauth2/v2.0/token")) return "{\"access_token\":\"t\"}";
            if (u.Contains("/subscribedSkus")) return skus;
            if (u.Contains("/directory/subscriptions")) return subs;
            if (u.Contains("/users")) return users;
            if (u.Contains("/servicePrincipals")) return "{\"value\":[]}";
            return null;
        });
        var outcome = await new M365Sync(ds, protector, handler).RunAsync(conn0, default);
        Assert.Equal(3, outcome.Users);
        Assert.Equal("succeeded", await Scalar(ds, "SELECT last_status FROM zentriq.sam_cloud_connections WHERE id = @id", ("id", conn0)));
        // A product and a synced subscription for the paid licence (not for the free one), ending at the renewal date.
        var product = (Guid)(await Scalar(ds, "SELECT id FROM zentriq.sam_products WHERE 'SPE_E3' = ANY (cloud_skus)"))!;
        Assert.Equal(0L, await Scalar(ds, "SELECT count(*) FROM zentriq.sam_products WHERE 'FLOW_FREE' = ANY (cloud_skus)"));
        Assert.Equal(1L, await Scalar(ds, "SELECT count(*) FROM zentriq.sam_licenses WHERE product_id = @p AND synced AND end_date = (current_date + 12)", ("p", product)));
        // Microsoft 365 web app with an account per licensed user; Bob is disabled but still has a licence.
        Assert.Equal(3L, await Scalar(ds, "SELECT count(*) FROM zentriq.webapp_accounts a JOIN zentriq.webapps w ON w.id = a.app_id WHERE w.catalog_key = 'microsoft-365' AND a.source = 'm365'"));

        await LicenseEvaluator.RunAsync(ds, default);
        Assert.Equal(1L, await Scalar(ds, "SELECT count(*) FROM zentriq.sam_alerts WHERE kind = 'cloud_overuse' AND resolved_at IS NULL"));
        Assert.Equal(1L, await Scalar(ds, "SELECT count(*) FROM zentriq.sam_alerts WHERE kind = 'shortfall' AND product_id = @p AND resolved_at IS NULL", ("p", product)));
        Assert.Equal(1L, await Scalar(ds, "SELECT count(*) FROM zentriq.sam_alerts WHERE kind = 'expiring' AND product_id = @p AND resolved_at IS NULL", ("p", product)));
        Assert.True((long)(await Scalar(ds, "SELECT count(*) FROM zentriq.sam_alerts WHERE kind = 'webapp_orphaned' AND resolved_at IS NULL"))! >= 1);
    }

    private sealed class Graph(Func<HttpRequestMessage, string?> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = answer(request);
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":{\"message\":\"not found\"}}") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
