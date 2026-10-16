using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Zentriq.Infrastructure.Database;
using Zentriq.Infrastructure.Reports;
using Zentriq.Infrastructure.Vulnerabilities;

namespace Zentriq.Tests;

public sealed class MsrcTests
{
    /// <summary>A reduced Microsoft CVRF v3 document with the shapes the parser has to handle.</summary>
    public const string SampleCvrf = """
    {
      "DocumentTitle": { "Value": "October 2099 Security Updates" },
      "DocumentTracking": { "Identification": { "ID": { "Value": "2099-Oct" } }, "CurrentReleaseDate": "2099-10-14T07:00:00Z" },
      "ProductTree": {
        "Branch": [ { "Items": [ { "Items": [ { "ProductID": "11568", "Value": "Windows Server 2019" } ] } ] } ],
        "FullProductName": [
          { "ProductID": "11568", "Value": "Windows Server 2019" },
          { "ProductID": "11569", "Value": "Windows Server 2019 (Server Core installation)" },
          { "ProductID": "12243", "Value": "Windows 11 Version 23H2 for x64-based Systems" },
          { "ProductID": "11762", "Value": "Microsoft Office LTSC 2021 for 64-bit editions" },
          { "ProductID": "12085", "Value": "Windows Server 2022, 23H2 Edition (Server Core installation)" }
        ]
      },
      "Vulnerability": [
        {
          "Title": { "Value": "Windows Kernel Elevation of Privilege Vulnerability" },
          "Notes": [ { "Title": "Description", "Type": 2, "Value": "<p>An attacker who <b>successfully</b> exploited this&nbsp;vulnerability could gain SYSTEM privileges.</p>" },
                     { "Title": "FAQ", "Type": 4, "Value": "<p>Ignored.</p>" } ],
          "CVE": "CVE-2099-21001",
          "CWE": [ { "ID": "CWE-416", "Value": "Use After Free" } ],
          "Threats": [
            { "Type": 0, "Description": { "Value": "Elevation of Privilege" }, "ProductID": [ "11568" ] },
            { "Type": 3, "Description": { "Value": "Important" }, "ProductID": [ "11568" ] },
            { "Type": 3, "Description": { "Value": "Critical" }, "ProductID": [ "12243" ] },
            { "Type": 1, "Description": { "Value": "Publicly Disclosed:No;Exploited:Yes;Latest Software Release:Exploitation Detected" } }
          ],
          "CVSSScoreSets": [ { "BaseScore": 7.0, "TemporalScore": 6.5, "Vector": "CVSS:3.1/AV:L/AC:H/PR:L/UI:N/S:U/C:H/I:H/A:H/E:F/RL:O/RC:C", "ProductID": [ "11568" ] },
                             { "BaseScore": 7.8, "Vector": "CVSS:3.1/AV:L/AC:L/PR:L/UI:N/S:U/C:H/I:H/A:H", "ProductID": [ "12243" ] } ],
          "Remediations": [
            { "Description": { "Value": "5099001" }, "URL": "https://catalog.update.microsoft.com/v7/site/Search.aspx?q=KB5099001", "Supercedence": "5098001",
              "ProductID": [ "11568", "11569" ], "Type": 2, "SubType": "Security Update", "FixedBuild": "10.0.17763.9001", "RestartRequired": { "Value": "Yes" } },
            { "Description": { "Value": "5099002" }, "ProductID": [ "12243" ], "Type": 2, "SubType": "Security Update", "FixedBuild": "10.0.22631.9002" },
            { "Description": { "Value": "5099003" }, "ProductID": [ "12243" ], "Type": 2, "SubType": "Security Hotpatch Update", "FixedBuild": "10.0.22631.8000" },
            { "Description": { "Value": "Release Notes" }, "ProductID": [ "11568" ], "Type": 2, "SubType": "Security Update" },
            { "Description": { "Value": "Apply the workaround" }, "ProductID": [ "11568" ], "Type": 0 }
          ],
          "RevisionHistory": [ { "Number": "1.0", "Date": "2099-10-14T07:00:00", "Description": { "Value": "Information published." } } ]
        },
        {
          "Title": { "Value": "Microsoft Office Remote Code Execution Vulnerability" },
          "CVE": "CVE-2099-21002",
          "Threats": [ { "Type": 3, "Description": { "Value": "Critical" }, "ProductID": [ "11762" ] } ],
          "Remediations": [ { "Description": { "Value": "5099010" }, "ProductID": [ "11762" ], "Type": 2, "FixedBuild": "16.0.14332.20800" } ]
        },
        {
          "Title": { "Value": "Windows DNS Server Remote Code Execution Vulnerability" },
          "CVE": "CVE-2099-21003",
          "Threats": [ { "Type": "Severity", "Description": { "Value": "Critical" }, "ProductID": [ "11568" ] } ],
          "Remediations": [ { "Description": { "Value": "KB5099001" }, "ProductID": "11568", "Type": "Vendor Fix", "FixedBuild": "10.0.17763.9001" } ]
        }
      ]
    }
    """;

    [Theory]
    [InlineData("10.0.17763", true, "64-bit", "Server", "Windows Server 2019")]
    [InlineData("10.0.17763", true, "64-bit", "Server Core", "Windows Server 2019 (Server Core installation)")]
    [InlineData("10.0.20348", true, "64-bit", null, "Windows Server 2022")]
    [InlineData("10.0.14393", true, "64-bit", "Server", "Windows Server 2016")]
    [InlineData("10.0.26100", true, "64-bit", "Server", "Windows Server 2025")]
    [InlineData("10.0.25398", true, "64-bit", "Server Core", "Windows Server 2022, 23H2 Edition (Server Core installation)")]
    [InlineData("10.0.19045", false, "64-bit", "Client", "Windows 10 Version 22H2 for x64-based Systems")]
    [InlineData("10.0.19045", false, "32-bit", "Client", "Windows 10 Version 22H2 for 32-bit Systems")]
    [InlineData("10.0.22631", false, "64-bit", "Client", "Windows 11 Version 23H2 for x64-based Systems")]
    [InlineData("10.0.26100", false, "ARM 64-bit Processor", "Client", "Windows 11 Version 24H2 for ARM64-based Systems")]
    [InlineData("10.0.22000", false, "64-bit", "Client", "Windows 11 Version 21H2 for x64-based Systems")]
    [InlineData("6.3.9600", true, "64-bit", "Server", "Windows Server 2012 R2")]
    [InlineData("10.0.99999", false, "64-bit", "Client", null)]
    [InlineData("not a version", false, "64-bit", "Client", null)]
    public void Maps_devices_to_microsoft_products(string version, bool server, string arch, string? installType, string? expected) =>
        Assert.Equal(expected, Msrc.ProductFor(version, server, arch, installType));

    [Fact]
    public void Compares_builds_on_the_same_build_line_only()
    {
        Assert.Equal("10.0.17763.6893", Msrc.FullBuild("10.0.17763", 6893));
        Assert.Null(Msrc.FullBuild("10.0.17763", null));
        Assert.True(Msrc.IsVulnerable("10.0.17763.5000", "10.0.17763.9001"));
        Assert.False(Msrc.IsVulnerable("10.0.17763.9001", "10.0.17763.9001"));
        Assert.False(Msrc.IsVulnerable("10.0.17763.9500", "10.0.17763.9001"));
        Assert.False(Msrc.IsVulnerable("10.0.20348.100", "10.0.17763.9001"));     // other build line
        Assert.Equal("windows 11 version 21h2 for x64-based systems", Msrc.Key("Windows 11 version  21H2 for x64-based Systems"));
        Assert.Equal("2099-Oct", Msrc.MonthId(new DateTime(2099, 10, 1)));
    }

    [Fact]
    public void Parses_cvrf_windows_fixes()
    {
        using var json = JsonDocument.Parse(SampleCvrf);
        var doc = Msrc.ParseDocument(json.RootElement);
        Assert.Equal("2099-Oct", doc.Id);
        Assert.Equal(2, doc.Cves.Count);                                   // the Office CVE is not a Windows OS fix
        var k = doc.Cves.Single(c => c.Id == "CVE-2099-21001");
        Assert.Equal("Windows Kernel Elevation of Privilege Vulnerability", k.Title);
        Assert.Equal(7.8m, k.CvssScore);
        Assert.StartsWith("CVSS:3.1/AV:L/AC:L", k.CvssVector);
        Assert.Equal("Critical", k.MsSeverity);                            // highest across products
        Assert.True(k.Exploited);
        Assert.False(k.Disclosed);
        Assert.Equal("An attacker who successfully exploited this vulnerability could gain SYSTEM privileges.", k.Description);
        Assert.Equal(["CWE-416"], k.Cwes);
        Assert.Equal(new DateTimeOffset(2099, 10, 14, 7, 0, 0, TimeSpan.Zero), k.Published);
        // Two products from the first remediation + Windows 11 (the hotpatch, the build-less and the workaround entries are ignored).
        Assert.Equal(3, k.Fixes.Count);
        var s2019 = k.Fixes.Single(f => f.Product == "Windows Server 2019");
        Assert.Equal("10.0.17763.9001", s2019.FixedBuild);
        Assert.Equal("5099001", s2019.Kb);
        Assert.True(s2019.Restart);
        Assert.StartsWith("https://catalog.update.microsoft.com", s2019.KbUrl);
        Assert.Equal("10.0.22631.9002", k.Fixes.Single(f => f.Product.StartsWith("Windows 11")).FixedBuild);
        // Textual type codes, a single ProductID string and "KB" in the description are accepted too.
        var dns = doc.Cves.Single(c => c.Id == "CVE-2099-21003");
        Assert.Equal("5099001", Assert.Single(dns.Fixes).Kb);
        Assert.Equal("Critical", dns.MsSeverity);
    }

    [Fact]
    public void Parses_the_update_list_and_scores_microsoft_severity()
    {
        using var json = JsonDocument.Parse("""{ "value": [ { "ID": "2099-Oct", "CurrentReleaseDate": "2099-10-20T07:00:00Z" }, { "ID": "2099-Sep" } ] }""");
        var list = Msrc.ParseUpdates(json.RootElement);
        Assert.Equal(2, list.Count);
        Assert.Equal(new DateTimeOffset(2099, 10, 20, 7, 0, 0, TimeSpan.Zero), list[0].CurrentRelease);
        Assert.Null(list[1].CurrentRelease);
        Assert.Equal("high", VulnScoring.SeverityFor(null, "Important"));
        Assert.Equal("critical", VulnScoring.SeverityFor(9.1m, "Important"));
        Assert.Equal(100, VulnScoring.Risk(9.8m, 0.9m, true, 1.25m));
        Assert.Equal(62, VulnScoring.Risk(8.0m, 0m, false, 1.1m));       // 56 × 1.1
        Assert.Equal(1.25m, VulnScoring.CriticalityFactor("Critical"));
    }
}

public sealed class ReportAnalyticsTests
{
    private static readonly ReportDataset Hardware = ReportCatalog.Find("hardware")!;
    private static readonly ReportDataset Assets = ReportCatalog.Find("assets")!;

    [Fact]
    public void Summary_reports_calculate_per_group()
    {
        var spec = new ReportSpec
        {
            Columns = ["manufacturer"], GroupBy = ["manufacturer"],
            Aggregates = [new() { Fn = "avg", Column = "ram_gb" }, new() { Fn = "max", Column = "disk_gb" }, new() { Fn = "count_distinct", Column = "model" }],
            Sort = [new() { Column = "agg_avg_ram_gb", Dir = "desc" }],
        };
        var (sql, _, _, cols) = ReportEngine.Build(Hardware, spec, 100);
        Assert.Contains("round(avg((t.\"ram_gb\")::numeric), 2) AS \"agg_avg_ram_gb\"", sql);
        Assert.Contains("count(DISTINCT t.\"model\") AS \"agg_count_distinct_model\"", sql);
        Assert.Contains("ORDER BY \"agg_avg_ram_gb\" DESC", sql);
        Assert.Equal(["manufacturer", "count", "agg_avg_ram_gb", "agg_max_disk_gb", "agg_count_distinct_model"], cols.Select(c => c.Key));
        Assert.Equal("Average Memory (GB)", cols[2].Label);
    }

    [Fact]
    public void Date_columns_group_by_period()
    {
        var spec = new ReportSpec { Columns = ["first_seen"], GroupBy = ["first_seen"], DateBucket = "month", Sort = [new() { Column = "first_seen" }] };
        var (sql, _, _, cols) = ReportEngine.Build(Assets, spec, 100);
        Assert.Contains("to_char(date_trunc('month', t.\"first_seen\"), 'YYYY-MM') AS \"first_seen\"", sql);
        Assert.Contains("GROUP BY to_char(date_trunc('month', t.\"first_seen\"), 'YYYY-MM')", sql);
        Assert.Equal("text", cols[0].Type);
    }

    [Fact]
    public void Rejects_invalid_calculations()
    {
        Assert.Throws<ReportValidationException>(() => ReportEngine.Validate(Hardware, new ReportSpec { Columns = ["hostname"], Aggregates = [new() { Fn = "sum", Column = "ram_gb" }] }));
        Assert.Throws<ReportValidationException>(() => ReportEngine.Validate(Hardware, new ReportSpec { Columns = ["model"], GroupBy = ["model"], Aggregates = [new() { Fn = "sum", Column = "cpu" }] }));
        Assert.Throws<ReportValidationException>(() => ReportEngine.Validate(Hardware, new ReportSpec { Columns = ["model"], GroupBy = ["model"], Aggregates = [new() { Fn = "median", Column = "ram_gb" }] }));
        Assert.Throws<ReportValidationException>(() => ReportEngine.Validate(Hardware, new ReportSpec { Columns = ["model"], GroupBy = ["model"], DateBucket = "hour" }));
        Assert.Throws<ReportValidationException>(() => ReportEngine.Validate(Hardware, new ReportSpec { Columns = ["model"], GroupBy = ["model"], Sort = [new() { Column = "agg_sum_ram_gb" }] }));
    }

    [Fact]
    public void Restricted_datasets_declare_their_permission()
    {
        Assert.Equal("vuln.view", ReportCatalog.Find("vulnerabilities")!.Permission);
        Assert.Equal("audit.view", ReportCatalog.Find("audit")!.Permission);
        Assert.Null(ReportCatalog.Find("software_titles")!.Permission);
    }

    [Fact]
    public void Writes_a_valid_pdf()
    {
        var rows = Enumerable.Range(1, 120).Select(i => new object?[] { $"PC-{i:000} (Zürich) – very long host name that has to be cut to fit", (long)(i % 7), DateTimeOffset.Parse("2026-10-14T10:00:00Z") }).ToList();
        var result = new ReportResult([new("hostname", "Hostname", "text"), new("count", "Count", "number"), new("seen", "Last seen", "date")], rows, rows.Count, false);
        var pdf = PdfWriter.Write("Devices (test)", "120 row(s)", result, "bar");
        var text = Encoding.Latin1.GetString(pdf);
        Assert.StartsWith("%PDF-1.4", text);
        Assert.EndsWith("%%EOF\n", text);
        Assert.Contains("/BaseFont /Helvetica", text);
        Assert.Contains("Page 1 of", text);
        Assert.Contains("Devices \\(test\\)", text);                      // parentheses escaped
        Assert.Contains("Zürich", text);                             // Latin-1 kept
        // Every xref offset points at its object.
        var xref = text.LastIndexOf("\nxref\n", StringComparison.Ordinal) + 1;   // not the "xref" inside "startxref"
        var lines = text[xref..].Split('\n');
        var count = int.Parse(lines[1].Split(' ')[1]);
        for (var i = 1; i < count; i++)
        {
            var offset = int.Parse(lines[2 + i][..10]);
            Assert.StartsWith($"{i} 0 obj", text[offset..]);
        }
        var startxref = int.Parse(text[(text.LastIndexOf("startxref\n", StringComparison.Ordinal) + 10)..].Split('\n')[0]);
        Assert.Equal(xref, startxref);
        Assert.True(PdfWriter.Measure("WWW", 10, false) > PdfWriter.Measure("iii", 10, false));
    }
}

/// <summary>On PostgreSQL: Windows devices against Microsoft fixed builds, workflow-safe re-evaluation, new report datasets.</summary>
public sealed class WindowsVulnerabilityDatabaseTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    public Task InitializeAsync() => _db.InitializeAsync();
    public Task DisposeAsync() => _db.DisposeAsync();

    private static async Task SetBuild(NpgsqlDataSource ds, Guid asset, int? ubr)
    {
        var doc = JsonSerializer.Serialize(new { os = new { family = "windows", name = "Windows Server 2019 Standard", version = "10.0.17763", ubr, architecture = "64-bit", isServer = true, installationType = "Server" } });
        await using var c = ds.CreateCommand("""
            INSERT INTO zentriq.asset_inventory_documents (asset_id, source, collected_at, document) VALUES (@a, 'agent', now(), @d::jsonb)
            ON CONFLICT (asset_id) DO UPDATE SET document = EXCLUDED.document, collected_at = now()
            """);
        c.Parameters.AddWithValue("a", asset);
        c.Parameters.AddWithValue("d", doc);
        await c.ExecuteNonQueryAsync();
    }

    [DbFact]
    public async Task Windows_devices_are_matched_to_fixed_builds()
    {
        await new SqlMigrator(NullLogger<SqlMigrator>.Instance).MigrateAsync(_db.OwnerConnection, _db.App, "1.0.0-test");
        await using var ds = NpgsqlDataSource.Create(_db.AppConnection);
        var server = Guid.NewGuid();
        var unknown = Guid.NewGuid();
        await using (var c = ds.CreateCommand("""
            INSERT INTO zentriq.assets (id, hostname, category, os_family, os_name, inventory_status, criticality) VALUES
              (@a, 'SRV-2019', 'server', 'windows', 'Windows Server 2019 Standard', 'success', 'Critical'),
              (@b, 'SRV-OLD-AGENT', 'server', 'windows', 'Windows Server 2019 Standard', 'success', 'Medium');
            """))
        {
            c.Parameters.AddWithValue("a", server);
            c.Parameters.AddWithValue("b", unknown);
            await c.ExecuteNonQueryAsync();
        }
        await SetBuild(ds, server, 5000);
        await SetBuild(ds, unknown, null);          // agent without UBR: cannot be assessed

        var store = new VulnStore(ds);
        using (var json = JsonDocument.Parse(MsrcTests.SampleCvrf))
        {
            var (cves, rules) = await store.UpsertMsrcAsync(Msrc.ParseDocument(json.RootElement), default);
            Assert.Equal(2, cves);
            Assert.Equal(4, rules);
            // Loading the same month again replaces its rules rather than adding duplicates.
            (_, rules) = await store.UpsertMsrcAsync(Msrc.ParseDocument(json.RootElement), default);
            Assert.Equal(4, rules);
        }

        var first = await store.EvaluateAsync(default);
        Assert.Equal(1, first.OsAssessed);
        Assert.Equal(1, first.OsNotAssessed);
        Assert.Equal(2, first.New);                 // kernel + DNS on SRV-2019
        long kernelId;
        await using (var c = ds.CreateCommand("SELECT id, priority, risk_score, kb, fixed_version, installed_version, source FROM zentriq.vuln_findings WHERE cve_id = 'CVE-2099-21001'"))
        await using (var r = await c.ExecuteReaderAsync())
        {
            Assert.True(await r.ReadAsync());
            kernelId = r.GetInt64(0);
            Assert.Equal("P1", r.GetString(1));     // Microsoft: exploited
            Assert.Equal((int)Math.Min(100, Math.Round(VulnScoring.Risk(7.8m, null, true) * 1.25m)), r.GetInt32(2));   // critical server weighting
            Assert.Equal("5099001", r.GetString(3));
            Assert.Equal("10.0.17763.9001", r.GetString(4));
            Assert.Equal("10.0.17763.5000", r.GetString(5));
            Assert.Equal("os", r.GetString(6));
        }

        // Being worked on survives re-evaluation; the patch level then moves past the fix and both close as fixed.
        await using (var c = ds.CreateCommand("UPDATE zentriq.vuln_findings SET status = 'in_progress' WHERE id = @id"))
        {
            c.Parameters.AddWithValue("id", kernelId);
            await c.ExecuteNonQueryAsync();
        }
        var again = await store.EvaluateAsync(default);
        Assert.Equal(0, again.New);
        Assert.Equal(0, again.Fixed);
        await SetBuild(ds, unknown, null);
        await SetBuild(ds, server, 9100);
        var patched = await store.EvaluateAsync(default);
        Assert.Equal(2, patched.Fixed);
        Assert.Equal(0, patched.Open);
        await using (var c = ds.CreateCommand("SELECT count(*) FROM zentriq.vuln_finding_events WHERE finding_id = @id AND kind = 'system'"))
        {
            c.Parameters.AddWithValue("id", kernelId);
            Assert.Equal(1L, await c.ExecuteScalarAsync());
        }

        // Reports over the new columns and datasets run on the real schema.
        var aging = await ReportEngine.RunAsync(ds, "vulnerabilities", new ReportSpec
        {
            Columns = ["age_bucket"], GroupBy = ["age_bucket"],
            Aggregates = [new() { Fn = "count_distinct", Column = "hostname" }, new() { Fn = "avg", Column = "cvss" }, new() { Fn = "max", Column = "first_seen" }],
            Filters = [new() { Column = "source", Op = "eq", Value = "os" }],
        }, 100, default);
        var row = Assert.Single(aging.Rows);
        Assert.Equal("0–30 days", row[0]);
        Assert.Equal(2L, row[1]);
        var monthly = await ReportEngine.RunAsync(ds, "vulnerabilities", new ReportSpec { Columns = ["first_seen"], GroupBy = ["first_seen"], DateBucket = "month" }, 100, default);
        Assert.Matches(@"^\d{4}-\d{2}$", (string)Assert.Single(monthly.Rows)[0]!);
        foreach (var d in new[] { "software_titles", "discovery", "audit" })
        {
            var ds2 = ReportCatalog.Find(d)!;
            await ReportEngine.RunAsync(ds, d, new ReportSpec { Columns = ds2.Columns.Select(c => c.Key).ToList() }, 10, default);
        }
        var assets = await ReportEngine.RunAsync(ds, "assets", new ReportSpec { Columns = ["hostname", "os_build", "critical_vulns", "vuln_risk", "feature_update"], Sort = [new() { Column = "hostname" }] }, 10, default);
        Assert.Equal("10.0.17763.9100", assets.Rows[0][1]);
    }
}
