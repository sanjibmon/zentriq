using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Zentriq.Infrastructure.Database;
using Zentriq.Infrastructure.Integrations;
using Zentriq.Infrastructure.Mail;
using Zentriq.Infrastructure.Reports;
using Zentriq.Infrastructure.Secrets;
using Zentriq.Infrastructure.Security;
using Zentriq.Inventory;

namespace Zentriq.Tests;

public sealed class SecurityRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    internal static InventoryDocument Hardened(bool server = false) => new()
    {
        Os = new OsInfo { Family = "windows", Name = server ? "Windows Server 2022 Standard" : "Windows 11 Enterprise", Version = server ? "10.0.20348" : "10.0.26100", Build = server ? "20348" : "26100", IsServer = server, EditionId = server ? "ServerStandard" : "Enterprise" },
        Hardware = new HardwareInfo { TpmPresent = true, TpmVersion = "2.0", IsVirtual = false },
        Security = new SecurityInfo
        {
            DiskEncryption = true, FirewallEnabled = true, SecureBoot = true,
            Antivirus = [new AntivirusProduct { Name = "Microsoft Defender Antivirus", Enabled = true, UpToDate = true }],
            Hardening = new HardeningInfo
            {
                FirewallDomain = true, FirewallPrivate = true, FirewallPublic = true, UacEnabled = true, UacAdminPrompt = 5, Smb1Enabled = false, SmbSigningRequired = true,
                RdpEnabled = false, LlmnrDisabled = true, AutorunDisabled = true, WDigestCleartext = false, LsaProtection = true, LmCompatibilityLevel = 5, NoLmHash = true,
                Tls10Enabled = false, Tls11Enabled = false, Ssl3Enabled = false, AutoUpdateDisabled = false, InactivityTimeoutSecs = 600, ScriptBlockLogging = true,
                GuestEnabled = false, BuiltinAdminEnabled = false, DefenderRealtime = true, DefenderSignatureAgeDays = 0, TamperProtection = true, CredentialGuard = true,
            },
        },
        Users = new UserInfo { LocalAdmins = ["ACME\\Domain Admins", "Administrator"] },
        Patches = [new PatchItem { Id = "KB5066793", InstalledOn = Now.AddDays(-10) }],
        Services = [new ServiceItem { Name = "Spooler", State = "Stopped", StartMode = "Disabled" }, new ServiceItem { Name = "RemoteRegistry", State = "Stopped", StartMode = "Disabled" }],
    };

    private static CheckResult Run(string id, InventoryDocument doc, string category = "endpoint", Dictionary<string, int>? p = null) =>
        SecurityRules.Find(id)!.Evaluate(new DeviceFacts(doc, category, Now), p);

    [Fact]
    public void Catalogue_has_unique_ids_guidance_and_references()
    {
        Assert.Equal(30, SecurityRules.All.Count);
        Assert.Equal(SecurityRules.All.Count, SecurityRules.All.Select(r => r.Id).Distinct().Count());
        Assert.All(SecurityRules.All, r =>
        {
            Assert.Contains(r.Severity, new[] { "high", "medium", "low" });
            Assert.Contains(r.Applies, new[] { "all", "server", "endpoint" });
            Assert.StartsWith("CIS Controls v8", r.Reference);
            Assert.True(r.Remediation.Length > 20 && r.Rationale.Length > 10);
        });
    }

    [Fact]
    public void A_hardened_workstation_passes_every_applicable_check()
    {
        var doc = Hardened();
        foreach (var rule in SecurityRules.All)
        {
            var r = rule.Evaluate(new DeviceFacts(doc, "endpoint", Now));
            Assert.True(r.Status is CheckStatus.Pass or CheckStatus.NotApplicable, $"{rule.Id} {rule.Title}: {r.Status} ({r.Actual})");
        }
        var server = Hardened(server: true);
        foreach (var rule in SecurityRules.All)
        {
            var r = rule.Evaluate(new DeviceFacts(server, "server", Now));
            Assert.True(r.Status is CheckStatus.Pass or CheckStatus.NotApplicable, $"server {rule.Id}: {r.Status} ({r.Actual})");
        }
    }

    [Fact]
    public void Weak_settings_fail_with_the_value_found()
    {
        var doc = Hardened();
        doc.Security.DiskEncryption = false;
        doc.Security.Hardening!.FirewallPublic = false;
        doc.Security.Hardening.RdpEnabled = true;
        doc.Security.Hardening.RdpNla = false;
        doc.Security.Hardening.UacAdminPrompt = 0;
        doc.Security.Hardening.WDigestCleartext = true;
        doc.Security.Hardening.DefenderSignatureAgeDays = 9;
        doc.Users.LocalAdmins = ["A", "B", "C", "D"];
        doc.Patches = [new PatchItem { Id = "KB1", InstalledOn = Now.AddDays(-70) }];

        Assert.Equal(CheckStatus.Fail, Run("SC-01", doc).Status);
        Assert.Equal("off for public", Run("SC-02", doc).Actual);
        Assert.Equal(CheckStatus.Fail, Run("SC-17", doc).Status);
        Assert.Contains("never prompted", Run("SC-11", doc).Actual);
        Assert.Equal(CheckStatus.Fail, Run("SC-20", doc).Status);
        Assert.Equal("9 day(s) old", Run("SC-04", doc).Actual);
        Assert.StartsWith("4 members", Run("SC-14", doc).Actual);
        Assert.Contains("70 day(s) ago", Run("SC-07", doc).Actual);
        // Parameters change the threshold (and are clamped to the allowed range).
        Assert.Equal(CheckStatus.Pass, Run("SC-14", doc, p: new() { ["max"] = 5 }).Status);
        Assert.Equal(CheckStatus.Pass, Run("SC-04", doc, p: new() { ["maxDays"] = 10 }).Status);
        Assert.Equal(30, SecurityRules.Find("SC-04")!.Resolve(new Dictionary<string, int> { ["maxDays"] = 999 })["maxDays"]);
    }

    [Fact]
    public void Missing_data_is_unknown_and_scope_is_respected()
    {
        var old = Hardened();
        old.Security.Hardening = null;     // agent older than this release
        Assert.Equal(CheckStatus.Unknown, Run("SC-20", old).Status);
        Assert.Equal(CheckStatus.Pass, Run("SC-02", old).Status);   // falls back to the overall firewall flag
        Assert.Equal(CheckStatus.NotApplicable, Run("SC-24", Hardened()).Status);              // servers only
        Assert.Equal(CheckStatus.NotApplicable, Run("SC-13", Hardened(true), "server").Status); // workstations only
        var linux = Hardened();
        linux.Os.Family = "linux";
        Assert.Equal(CheckStatus.NotApplicable, Run("SC-01", linux).Status);
        var vm = Hardened();
        vm.Hardware.IsVirtual = true;
        vm.Hardware.TpmPresent = false;
        Assert.Equal(CheckStatus.NotApplicable, Run("SC-10", vm).Status);
        var noServices = Hardened(true);
        noServices.Services = [];
        Assert.Equal(CheckStatus.Unknown, Run("SC-28", noServices, "server").Status);
        var spooler = Hardened(true);
        spooler.Services = [new ServiceItem { Name = "Spooler", State = "Running", StartMode = "Auto" }];
        Assert.Equal("Running, Auto", Run("SC-28", spooler, "server").Actual);
    }

    [Theory]
    [InlineData("10.0.19045", "Professional", false, false)]   // Windows 10 22H2: ended 2025-10-14
    [InlineData("10.0.22631", "Professional", false, false)]   // Windows 11 23H2 Pro: ended 2025-11-11
    [InlineData("10.0.22631", "Enterprise", false, true)]      // 23H2 Enterprise: until 2026-11-10
    [InlineData("10.0.19044", "EnterpriseS", false, true)]     // LTSC 2021
    [InlineData("10.0.17763", "ServerStandard", true, true)]   // Server 2019
    [InlineData("10.0.26100", "ServerDatacenter", true, true)] // Server 2025
    [InlineData("6.3.9600", "ServerStandard", true, false)]    // Server 2012 R2
    public void Windows_lifecycle_decides_SC06(string version, string edition, bool server, bool supported)
    {
        var doc = Hardened(server);
        doc.Os.Version = version;
        doc.Os.EditionId = edition;
        doc.Os.Name = server ? "Windows Server" : "Windows";
        Assert.Equal(supported ? CheckStatus.Pass : CheckStatus.Fail, Run("SC-06", doc, server ? "server" : "endpoint").Status);
    }

    [Fact]
    public void Score_weights_severity_and_ignores_unknown_and_exempt()
    {
        Assert.Equal(40, SecurityScoring.Score([("high", CheckStatus.Fail), ("medium", CheckStatus.Pass), ("low", CheckStatus.Unknown)]));
        Assert.Equal(100, SecurityScoring.Score([("low", CheckStatus.Pass)]));
        Assert.Null(SecurityScoring.Score([("high", CheckStatus.Unknown)]));
    }

    [Fact]
    public void Baselines_apply_defaults_overrides_scope_and_exceptions()
    {
        var def = new BaselineDefinition { Rules = [new BaselineRule { Id = "SC-18", Enabled = true }, new BaselineRule { Id = "SC-01", Enabled = false }, new BaselineRule { Id = "SC-02", Severity = "low" }] };
        var eff = def.Effective().ToDictionary(x => x.Rule.Id);
        Assert.True(eff.ContainsKey("SC-18"));       // default off, turned on here
        Assert.False(eff.ContainsKey("SC-30"));      // default off
        Assert.False(eff.ContainsKey("SC-01"));
        Assert.Equal("low", eff["SC-02"].Severity);
        Assert.Equal(28, BaselineDefinition.Default().Effective().Count());
        Assert.True(new BaselineDefinition { Categories = ["server"] }.InScope("server"));
        Assert.False(new BaselineDefinition { Categories = ["server"] }.InScope("endpoint"));

        var doc = Hardened();
        doc.Security.DiskEncryption = false;
        var baseline = new SecurityStore.Baseline(Guid.NewGuid(), "Test", BaselineDefinition.Default());
        var asset = Guid.NewGuid();
        var rows = SecurityStore.EvaluateDevice(baseline, asset, new DeviceFacts(doc, "endpoint", Now),
            [new SecurityException(Guid.NewGuid(), "SC-01", asset, null, "Kiosk without data, approved by CISO", null)]).ToList();
        var sc01 = rows.Single(r => r.RuleId == "SC-01");
        Assert.Equal("exempt", sc01.Status);
        Assert.Contains("Kiosk", sc01.Actual);
        Assert.DoesNotContain(rows, r => r.RuleId == "SC-24");   // not applicable rows are not stored
    }

    private sealed class Reg(Dictionary<string, Dictionary<string, int>> values) : IRegistrySource
    {
        public IReadOnlyList<string> SubKeys(string path) => [];
        public string? GetString(string path, string name) => null;
        public int? GetDword(string path, string name) => values.TryGetValue(path, out var v) && v.TryGetValue(name, out var i) ? i : null;
    }

    [Fact]
    public void Registry_settings_are_read_with_policy_first_and_windows_defaults()
    {
        var doc = new InventoryDocument { Os = new OsInfo { Family = "windows", Build = "17763" } };
        WindowsCollector.ReadHardening(doc, new Reg(new()
        {
            [@"SOFTWARE\Policies\Microsoft\WindowsFirewall\PublicProfile"] = new() { ["EnableFirewall"] = 0 },
            [@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\PublicProfile"] = new() { ["EnableFirewall"] = 1 },
            [@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\DomainProfile"] = new() { ["EnableFirewall"] = 1 },
            [@"SYSTEM\CurrentControlSet\Control\Terminal Server"] = new() { ["fDenyTSConnections"] = 0 },
            [@"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp"] = new() { ["UserAuthentication"] = 1, ["PortNumber"] = 3390 },
            [@"SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\TLS 1.0\Server"] = new() { ["Enabled"] = 0 },
            [@"SYSTEM\CurrentControlSet\Control\Lsa"] = new() { ["RunAsPPL"] = 2 },
        }));
        var h = doc.Security.Hardening!;
        Assert.False(h.FirewallPublic);        // policy wins over the local setting
        Assert.True(h.FirewallDomain);
        Assert.Null(h.FirewallPrivate);
        Assert.True(h.RdpEnabled);
        Assert.True(h.RdpNla);
        Assert.Equal(3390, h.RdpPort);
        Assert.False(h.Tls10Enabled);
        Assert.True(h.Tls11Enabled);           // not configured on 1809 = enabled by default
        Assert.False(h.Ssl3Enabled);
        Assert.True(h.LsaProtection);
        Assert.Equal(3, h.LmCompatibilityLevel);
        Assert.True(h.UacEnabled);
        Assert.False(h.Smb1Enabled);
        Assert.False(h.WDigestCleartext);
    }
}

public sealed class ChannelFormatTests
{
    private static OutboundEvent Event() => new()
    {
        Id = 42, Type = "vulnerability.new", Severity = "critical", Title = "3 new vulnerabilities on SRV|01 (1 known exploited)",
        Text = "CVE-2099-1 [critical] key=value\nline two", OccurredAt = new DateTimeOffset(2026, 10, 16, 9, 30, 0, TimeSpan.Zero),
        Link = "https://zentriq.example/assets/1", Asset = new OutboundAsset { Id = Guid.Empty, Hostname = "SRV01", Ip = "10.0.0.5", Category = "server", Criticality = "High" },
    };

    [Fact]
    public void Webhook_signature_is_hmac_sha256_of_timestamp_and_body()
    {
        var body = ChannelSender.WebhookBody(Event());
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes("s3cret"));
        var expected = "sha256=" + Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes("1760000000." + body))).ToLowerInvariant();
        Assert.Equal(expected, ChannelSender.Sign("s3cret", 1760000000, body));
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal("vulnerability.new", json.GetProperty("type").GetString());
        Assert.Equal("SRV01", json.GetProperty("asset").GetProperty("hostname").GetString());
    }

    [Fact]
    public void Chat_messages_carry_title_severity_and_link()
    {
        var slack = JsonDocument.Parse(ChannelSender.SlackBody(Event()).ToJsonString()).RootElement;
        Assert.StartsWith("[CRITICAL]", slack.GetProperty("text").GetString());
        Assert.Contains("|Open in Zentriq>", slack.GetProperty("blocks")[1].GetProperty("elements")[1].GetProperty("text").GetString());
        var teams = JsonDocument.Parse(ChannelSender.TeamsBody(Event()).ToJsonString()).RootElement;
        var card = teams.GetProperty("attachments")[0];
        Assert.Equal("application/vnd.microsoft.card.adaptive", card.GetProperty("contentType").GetString());
        Assert.Equal("AdaptiveCard", card.GetProperty("content").GetProperty("type").GetString());
        Assert.Equal("Action.OpenUrl", card.GetProperty("content").GetProperty("actions")[0].GetProperty("type").GetString());
    }

    [Fact]
    public void Syslog_cef_line_is_escaped()
    {
        var line = ChannelSender.SyslogLine(Event(), "cef", "1.1.2", "ZENTRIQ");
        Assert.StartsWith("<130>1 2026-10-16T09:30:00.000Z ZENTRIQ Zentriq - vulnerability.new - CEF:0|Zentriq|Zentriq|1.1.2|vulnerability.new|", line);
        Assert.Contains(@"SRV\|01", line);                 // header pipe escaped
        Assert.Contains(@"key\=value\nline two", line);    // extension = and newline escaped
        Assert.Contains("|10|", line);                     // CEF severity for critical
        Assert.Contains("dhost=SRV01 dst=10.0.0.5", line);
        var json = ChannelSender.SyslogLine(Event(), "json", "1.1.2", "ZENTRIQ");
        Assert.EndsWith("}", json);
    }

    [Fact]
    public void Tickets_map_severity_and_fields()
    {
        var ch = new Channel(Guid.NewGuid(), "servicenow", "SN", true, new ChannelSettings { AssignmentGroup = "Desktop Support", Labels = ["sec", "bad label"], ProjectKey = "OPS" }, new ChannelSecrets());
        var sn = ChannelSender.ServiceNowBody(ch, Event(), "zentriq-42");
        Assert.Equal("1", sn["urgency"]!.GetValue<string>());
        Assert.Equal("Desktop Support", sn["assignment_group"]!.GetValue<string>());
        Assert.StartsWith("[Zentriq] 3 new", sn["short_description"]!.GetValue<string>());
        var jira = ChannelSender.JiraBody(ch, Event())["fields"]!;
        Assert.Equal("OPS", jira["project"]!["key"]!.GetValue<string>());
        Assert.Equal("Task", jira["issuetype"]!["name"]!.GetValue<string>());
        Assert.Equal(new[] { "zentriq", "sec" }, jira["labels"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        var (subject, body) = ChannelSender.EmailContent(Event());
        Assert.StartsWith("[Zentriq][CRITICAL]", subject);
        Assert.Contains("https://zentriq.example/assets/1", body);
    }

    [Fact]
    public void Rules_match_on_severity_and_device()
    {
        Assert.True(EventTypes.Rank("critical") > EventTypes.Rank("high"));
        Assert.Equal(0, EventTypes.Rank(null));
        var c = new RuleConditions { Categories = ["server"], Criticality = ["High", "Critical"] };
        Assert.True(c.Matches(new OutboundAsset { Category = "server", Criticality = "High" }));
        Assert.False(c.Matches(new OutboundAsset { Category = "endpoint", Criticality = "High" }));
        Assert.False(c.Matches(null));
        Assert.True(new RuleConditions().Matches(null));
        Assert.Equal(TimeSpan.FromMinutes(1), IntegrationStore.RetryDelay(1));
        Assert.Equal(TimeSpan.FromMinutes(60), IntegrationStore.RetryDelay(4));
    }

    [Fact]
    public void Api_tokens_are_random_and_hashed()
    {
        var a = IntegrationStore.NewToken();
        var b = IntegrationStore.NewToken();
        Assert.StartsWith("zq_", a);
        Assert.NotEqual(a, b);
        Assert.True(a.Length >= 40);
        Assert.Equal(IntegrationStore.HashToken(a), IntegrationStore.HashToken(a));
        Assert.Equal(32, IntegrationStore.HashToken(a).Length);
        Assert.False(ChannelSender.ValidUrl("http://hooks.slack.com/x", allowHttp: false));
        Assert.True(ChannelSender.ValidUrl("http://siem.local:8080/in", allowHttp: true));
    }

    [Fact]
    public void Security_dataset_needs_its_permission()
    {
        var d = ReportCatalog.Find("security")!;
        Assert.Equal("security.view", d.Permission);
        Assert.NotNull(d.Column("severity_rank"));
        Assert.NotNull(ReportCatalog.Find("assets")!.Column("security_score"));
    }
}

/// <summary>On PostgreSQL: security evaluation end to end, and an alert rule delivering a signed webhook.</summary>
public sealed class SecurityAndIntegrationDatabaseTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    public Task InitializeAsync() => _db.InitializeAsync();
    public Task DisposeAsync() => _db.DisposeAsync();

    private static async Task SetDoc(NpgsqlDataSource ds, Guid asset, InventoryDocument doc)
    {
        await using var c = ds.CreateCommand("""
            INSERT INTO zentriq.asset_inventory_documents (asset_id, source, collected_at, document) VALUES (@a, 'agent', now(), @d::jsonb)
            ON CONFLICT (asset_id) DO UPDATE SET document = EXCLUDED.document, collected_at = now()
            """);
        c.Parameters.AddWithValue("a", asset);
        c.Parameters.AddWithValue("d", JsonSerializer.Serialize(doc));
        await c.ExecuteNonQueryAsync();
    }

    [DbFact]
    public async Task Security_evaluation_scores_devices_and_tracks_failures()
    {
        await new SqlMigrator(NullLogger<SqlMigrator>.Instance).MigrateAsync(_db.OwnerConnection, _db.App, "1.0.0-test");
        await using var ds = NpgsqlDataSource.Create(_db.AppConnection);
        var good = Guid.NewGuid();
        var weak = Guid.NewGuid();
        await using (var c = ds.CreateCommand("""
            INSERT INTO zentriq.assets (id, hostname, category, os_family, os_name, inventory_status) VALUES
              (@a, 'PC-GOOD', 'endpoint', 'windows', 'Windows 11 Enterprise', 'success'),
              (@b, 'PC-WEAK', 'endpoint', 'windows', 'Windows 11 Enterprise', 'success')
            """))
        {
            c.Parameters.AddWithValue("a", good);
            c.Parameters.AddWithValue("b", weak);
            await c.ExecuteNonQueryAsync();
        }
        var doc = SecurityRuleTests.Hardened();
        doc.Patches = [new PatchItem { Id = "KB1", InstalledOn = DateTimeOffset.UtcNow.AddDays(-3) }];
        await SetDoc(ds, good, doc);
        var weakDoc = SecurityRuleTests.Hardened();
        weakDoc.Patches = doc.Patches;
        weakDoc.Security.DiskEncryption = false;
        weakDoc.Security.Hardening!.WDigestCleartext = true;
        await SetDoc(ds, weak, weakDoc);

        var store = new SecurityStore(ds);
        var e = await store.EvaluateAsync(default);
        Assert.Equal(1, e.Baselines);
        Assert.Equal(2, e.Devices);
        Assert.Equal(2, e.Failed);
        Assert.Equal(2, e.NewFailures);
        async Task<int?> Score(Guid a)
        {
            await using var c = ds.CreateCommand("SELECT score FROM zentriq.sec_scores WHERE asset_id = @a");
            c.Parameters.AddWithValue("a", a);
            return await c.ExecuteScalarAsync() is int i ? i : null;
        }
        Assert.Equal(100, await Score(good));
        Assert.True(await Score(weak) < 100);
        await using (var c = ds.CreateCommand("SELECT count(*) FROM zentriq.sec_rules"))
            Assert.Equal(30L, await c.ExecuteScalarAsync());

        // An exception exempts the check; fixing the other setting resolves it; failing_since is kept while failing.
        await using (var c = ds.CreateCommand("INSERT INTO zentriq.sec_exceptions (rule_id, asset_id, reason) VALUES ('SC-01', @a, 'Lab device without data')"))
        {
            c.Parameters.AddWithValue("a", weak);
            await c.ExecuteNonQueryAsync();
        }
        weakDoc.Security.Hardening.WDigestCleartext = false;
        await SetDoc(ds, weak, weakDoc);
        var e2 = await store.EvaluateAsync(default);
        Assert.Equal(0, e2.Failed);
        Assert.Equal(2, e2.Resolved);
        Assert.Equal(100, await Score(weak));
        await using (var c = ds.CreateCommand("SELECT status FROM zentriq.sec_results WHERE asset_id = @a AND rule_id = 'SC-01'"))
        {
            c.Parameters.AddWithValue("a", weak);
            Assert.Equal("exempt", await c.ExecuteScalarAsync());
        }
        await using (var c = ds.CreateCommand("SELECT devices, compliant FROM zentriq.sec_history WHERE day = current_date"))
        await using (var r = await c.ExecuteReaderAsync())
        {
            Assert.True(await r.ReadAsync());
            Assert.Equal(2, r.GetInt32(0));
            Assert.Equal(2, r.GetInt32(1));
        }

        // The report dataset and asset columns run on the real schema.
        var ds2 = ReportCatalog.Find("security")!;
        var all = await ReportEngine.RunAsync(ds, "security", new ReportSpec { Columns = ds2.Columns.Select(c => c.Key).ToList() }, 100, default);
        Assert.True(all.Total >= 50);
        var assets = await ReportEngine.RunAsync(ds, "assets", new ReportSpec { Columns = ["hostname", "security_score", "security_failed"], Sort = [new() { Column = "hostname" }] }, 10, default);
        Assert.Equal(100m, Convert.ToDecimal(assets.Rows[0][1]));

        // Every built-in report (including the new security ones) is valid and runs.
        var builtIn = new List<(string Name, string Dataset, string Def)>();
        await using (var c = ds.CreateCommand("SELECT name, dataset, definition::text FROM zentriq.report_definitions WHERE system"))
        await using (var r = await c.ExecuteReaderAsync())
            while (await r.ReadAsync()) builtIn.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
        Assert.Contains(builtIn, b => b.Name == "Failed security checks");
        foreach (var (name, dataset, def) in builtIn)
        {
            var ex = await Record.ExceptionAsync(() => ReportEngine.RunAsync(ds, dataset, ReportSpec.Parse(def), 10, default));
            Assert.True(ex is null, $"{name}: {ex?.Message}");
        }
    }

    [DbFact]
    public async Task Alert_rule_sends_a_signed_webhook_for_a_new_device()
    {
        await new SqlMigrator(NullLogger<SqlMigrator>.Instance).MigrateAsync(_db.OwnerConnection, _db.App, "1.0.0-test");
        await using var ds = NpgsqlDataSource.Create(_db.AppConnection);
        var secrets = new AppSecrets { CredentialKey = SecretStore.NewKey() };
        var protector = new CredentialProtector(() => secrets);
        var mail = new MailService(ds, protector);
        var store = new IntegrationStore(ds, protector, new ChannelSender(mail), mail);

        // Local webhook receiver.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/hook/");
        listener.Start();
        var received = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var result = (body, sig: ctx.Request.Headers["X-Zentriq-Signature"], ts: ctx.Request.Headers["X-Zentriq-Timestamp"], ev: ctx.Request.Headers["X-Zentriq-Event"], auth: ctx.Request.Headers["X-Api-Key"]);
            ctx.Response.StatusCode = 204;
            ctx.Response.Close();
            return result;
        });

        var channelId = Guid.NewGuid();
        await using (var c = ds.CreateCommand("""
            INSERT INTO zentriq.int_channels (id, kind, name, settings, secret_protected) VALUES (@id, 'webhook', 'Test hook', @s::jsonb, @p);
            INSERT INTO zentriq.int_rules (name, event_type, min_severity, channel_ids) VALUES ('New devices', 'asset.discovered', 'info', ARRAY[@id]);
            """))
        {
            c.Parameters.AddWithValue("id", channelId);
            c.Parameters.AddWithValue("s", JsonSerializer.Serialize(new ChannelSettings { Url = $"http://127.0.0.1:{port}/hook/", HeaderName = "X-Api-Key" }, IntegrationStore.Json));
            c.Parameters.AddWithValue("p", store.ProtectSecrets(channelId, new ChannelSecrets { SigningSecret = "whsec-test", HeaderValue = "k-123" }));
            await c.ExecuteNonQueryAsync();
        }

        Assert.Equal(0, await store.DetectAsync(default));   // first run only sets the watermarks
        await Task.Delay(50);
        await using (var c = ds.CreateCommand("INSERT INTO zentriq.assets (hostname, category, os_family, source) VALUES ('NEW-LAPTOP', 'endpoint', 'windows', 'discovery')"))
            await c.ExecuteNonQueryAsync();
        await Task.Delay(50);
        Assert.Equal(1, await store.DetectAsync(default));
        Assert.Equal(0, await store.DetectAsync(default));   // deduplicated
        Assert.Equal(1, await store.RouteAsync(default));
        var (sent, failed) = await store.DeliverDueAsync(default);
        Assert.Equal((1, 0), (sent, failed));

        var got = await received.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("asset.discovered", got.ev);
        Assert.Equal("k-123", got.auth);
        Assert.Equal(ChannelSender.Sign("whsec-test", long.Parse(got.ts!), got.body), got.sig);
        Assert.Equal("NEW-LAPTOP", JsonDocument.Parse(got.body).RootElement.GetProperty("asset").GetProperty("hostname").GetString());
        await using (var c = ds.CreateCommand("SELECT status, response FROM zentriq.int_deliveries"))
        await using (var r = await c.ExecuteReaderAsync())
        {
            Assert.True(await r.ReadAsync());
            Assert.Equal("sent", r.GetString(0));
            Assert.Equal("HTTP 204", r.GetString(1));
        }

        // A failing channel is retried later, then marked failed; the channel records the error.
        await using (var c = ds.CreateCommand("UPDATE zentriq.int_channels SET settings = jsonb_set(settings, '{url}', to_jsonb('http://127.0.0.1:1/none'::text))"))
            await c.ExecuteNonQueryAsync();
        await using (var c = ds.CreateCommand("INSERT INTO zentriq.assets (hostname, category, os_family, source) VALUES ('SECOND', 'endpoint', 'windows', 'discovery')"))
            await c.ExecuteNonQueryAsync();
        await Task.Delay(50);
        await store.DetectAsync(default);
        await store.RouteAsync(default);
        (sent, failed) = await store.DeliverDueAsync(default);
        Assert.Equal((0, 1), (sent, failed));
        await using (var c = ds.CreateCommand("SELECT d.status, d.attempts, c.last_error IS NOT NULL FROM zentriq.int_deliveries d JOIN zentriq.int_channels c ON c.id = d.channel_id ORDER BY d.id DESC LIMIT 1"))
        await using (var r = await c.ExecuteReaderAsync())
        {
            Assert.True(await r.ReadAsync());
            Assert.Equal("queued", r.GetString(0));
            Assert.Equal(1, r.GetInt32(1));
            Assert.True(r.GetBoolean(2));
        }
    }
}
