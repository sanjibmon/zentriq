using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Zentriq.Infrastructure.Cloud;
using Zentriq.Infrastructure.Database;
using Zentriq.Infrastructure.Secrets;

namespace Zentriq.Tests;

/// <summary>Cloud &amp; Infrastructure: request signing, mapping of each platform's answers, findings, storing and linking.</summary>
public sealed class CloudTests
{
    [Fact]
    public void SigV4_matches_the_AWS_test_vector()
    {
        // AWS Signature Version 4 test suite, "get-vanilla".
        var headers = new Dictionary<string, string> { ["host"] = "example.amazonaws.com", ["x-amz-date"] = "20150830T123600Z" };
        var auth = AwsSigV4.Authorization("GET", "/", "", headers, AwsSigV4.Sha256Hex(""), "AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY",
            "us-east-1", "service", new DateTime(2015, 8, 30, 12, 36, 0, DateTimeKind.Utc));
        Assert.Equal("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/service/aws4_request, SignedHeaders=host;x-amz-date, Signature=5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31", auth);
        Assert.Equal("Action=DescribeRegions&Version=2016-11-15", AwsSigV4.CanonicalQuery([new("Version", "2016-11-15"), new("Action", "DescribeRegions")]));
        Assert.Equal("a%20b%2Fc~", AwsSigV4.Encode("a b/c~"));
    }

    [Theory]
    [InlineData("*", new[] { 0 })]
    [InlineData("3389", new[] { 3389 })]
    [InlineData("20-3400", new[] { 22, 3389 })]
    [InlineData("443", new int[0])]
    [InlineData("0-65535", new[] { 0 })]
    public void Open_admin_ports_are_found_in_port_ranges(string range, int[] expected) =>
        Assert.Equal(expected, AzureCollector.OpenAdminPorts(range).ToArray());

    private const string AzureRows = """
        [
          {"id":"/subscriptions/s1/resourceGroups/RG1/providers/Microsoft.Compute/virtualMachines/vm-app01","name":"vm-app01","type":"microsoft.compute/virtualmachines",
           "location":"centralindia","resourceGroup":"rg1","subscriptionId":"s1","tags":{"department":"Finance"},
           "properties":{"vmId":"aabbccdd-eeff-0011-2233-445566778899","hardwareProfile":{"vmSize":"Standard_B2s"},
             "storageProfile":{"imageReference":{"offer":"WindowsServer","sku":"2022-datacenter"},"osDisk":{"osType":"Windows"}},
             "osProfile":{"computerName":"APP01"},"extended":{"instanceView":{"powerState":{"code":"PowerState/deallocated"}}},"timeCreated":"2024-02-01T10:00:00Z"}},
          {"id":"/subscriptions/s1/resourceGroups/RG1/providers/Microsoft.Network/networkInterfaces/nic1","name":"nic1","type":"microsoft.network/networkinterfaces",
           "properties":{"macAddress":"00-0D-3A-12-34-56","virtualMachine":{"id":"/subscriptions/s1/resourceGroups/RG1/providers/Microsoft.Compute/virtualMachines/vm-app01"},
             "ipConfigurations":[{"properties":{"privateIPAddress":"10.1.0.4","publicIPAddress":{"id":"/subscriptions/s1/resourceGroups/rg1/providers/Microsoft.Network/publicIPAddresses/pip1"}}}]}},
          {"id":"/subscriptions/s1/resourceGroups/rg1/providers/Microsoft.Network/publicIPAddresses/pip1","name":"pip1","type":"microsoft.network/publicipaddresses",
           "properties":{"ipAddress":"20.1.2.3","ipConfiguration":{"id":"/x/ipconfig1"}}},
          {"id":"/subscriptions/s1/resourceGroups/rg1/providers/Microsoft.Network/publicIPAddresses/pip2","name":"pip2","type":"microsoft.network/publicipaddresses",
           "properties":{"ipAddress":"20.1.2.4"}},
          {"id":"/subscriptions/s1/resourceGroups/rg1/providers/Microsoft.Compute/disks/old","name":"old","type":"microsoft.compute/disks","sku":{"name":"Premium_LRS"},
           "properties":{"diskSizeGB":128,"diskState":"Unattached"}},
          {"id":"/subscriptions/s1/resourceGroups/rg1/providers/Microsoft.Network/networkSecurityGroups/nsg1","name":"nsg1","type":"microsoft.network/networksecuritygroups",
           "properties":{"securityRules":[
             {"name":"rdp","properties":{"access":"Allow","direction":"Inbound","sourceAddressPrefix":"*","destinationPortRange":"3389"}},
             {"name":"web","properties":{"access":"Allow","direction":"Inbound","sourceAddressPrefix":"Internet","destinationPortRange":"443"}},
             {"name":"ssh-office","properties":{"access":"Allow","direction":"Inbound","sourceAddressPrefix":"203.0.113.0/24","destinationPortRange":"22"}}]}},
          {"id":"/subscriptions/s1/resourceGroups/rg1/providers/Microsoft.Storage/storageAccounts/st1","name":"st1","type":"microsoft.storage/storageaccounts",
           "properties":{"allowBlobPublicAccess":true,"supportsHttpsTrafficOnly":false}},
          {"id":"/subscriptions/s1/resourceGroups/rg1/providers/Microsoft.Sql/servers/sql1/databases/master","name":"master","type":"microsoft.sql/servers/databases","properties":{}},
          {"id":"/subscriptions/s1/resourceGroups/rg1/providers/Microsoft.Web/sites/web1","name":"web1","type":"microsoft.web/sites","kind":"app","properties":{}}
        ]
        """;

    [Fact]
    public void Azure_resource_graph_rows_become_resources()
    {
        var snap = new CloudSnapshot();
        AzureCollector.Map(JsonDocument.Parse(AzureRows).RootElement.EnumerateArray().ToList(), snap);
        var vm = Assert.Single(snap.Items, i => i.Kind == "vm");
        Assert.Equal("deallocated", vm.State);
        Assert.Equal("Standard_B2s", vm.Size);
        Assert.Equal("WindowsServer 2022-datacenter", vm.Os);
        Assert.Equal("APP01", vm.Hostname);
        Assert.Equal("AABBCCDD-EEFF-0011-2233-445566778899", vm.BiosUuid);
        Assert.Equal(new[] { "00:0D:3A:12:34:56" }, vm.Macs.ToArray());
        Assert.Equal(("10.1.0.4", "20.1.2.3"), (vm.PrivateIp, vm.PublicIp));
        Assert.Equal(vm.ExternalId, vm.ExternalId.ToLowerInvariant());
        Assert.Equal("Finance", vm.Tags["DEPARTMENT"]);
        Assert.Equal(2, snap.Items.Count(i => i.Kind == "public_ip"));
        Assert.False(snap.Items.Single(i => i.Name == "pip2").Bool("associated"));
        Assert.DoesNotContain(snap.Items, i => i.Name is "master" or "web1" or "nic1");   // master DB, plain web app, NIC
        Assert.Equal(new[] { 3389 }, snap.Items.Single(i => i.Kind == "security_group").Ports("openPorts").ToArray());

        var findings = CloudRules.Evaluate(CloudProviders.Azure, snap.Items, _ => DateTimeOffset.UtcNow.AddDays(-40), DateTimeOffset.UtcNow);
        var rules = findings.Select(f => f.Rule).ToHashSet();
        Assert.Contains("vm_off", rules);                       // deallocated for 40 days
        Assert.DoesNotContain("vm_stopped_billed", rules);      // deallocated is not billed
        Assert.Contains("disk_unattached", rules);
        Assert.Contains("ip_idle", rules);
        Assert.Contains("exposed_admin", rules);
        Assert.Contains("storage_public", rules);
        Assert.Contains("storage_http", rules);
        Assert.Equal("high", findings.Single(f => f.Rule == "exposed_admin").Severity);
        Assert.Contains(findings, f => f.Rule == "untagged" && f.ExternalId.EndsWith("/disks/old"));
        Assert.DoesNotContain(findings, f => f.Rule == "untagged" && f.ExternalId == vm.ExternalId);
    }

    [Fact]
    public void Rules_cover_billing_capacity_and_exposure()
    {
        var now = DateTimeOffset.UtcNow;
        CloudItem I(string kind, string id, params (string K, object V)[] props)
        {
            var i = new CloudItem { Kind = kind, ExternalId = id, Name = id, State = "running" };
            foreach (var (k, v) in props) i.Properties[k] = v;
            return i;
        }
        var stopped = I("vm", "stopped");
        stopped.State = "stopped";
        var items = new List<CloudItem>
        {
            stopped, I("security_group", "all", ("openPorts", new List<int> { 0, 22 })), I("datastore", "ds1", ("freePercent", 8L)), I("datastore", "ds2", ("freePercent", 15L)),
            I("datastore", "ds3", ("freePercent", 50L)), I("host", "h1", ("memoryCommitPercent", 180L), ("osEndOfSupport", true)), I("vm", "cp", ("snapshotOldestDays", 30L), ("snapshotCount", 2L)),
            I("database", "db", ("publiclyAccessible", true), ("encrypted", false)),
        };
        var f = CloudRules.Evaluate(CloudProviders.Azure, items, i => i.ExternalId == "stopped" ? now.AddDays(-2) : null, now);
        Assert.Contains(f, x => x.ExternalId == "stopped" && x.Rule == "vm_stopped_billed");
        Assert.DoesNotContain(f, x => x.ExternalId == "stopped" && x.Rule == "vm_off");     // only 2 days
        Assert.Equal("critical", f.Single(x => x.ExternalId == "all").Severity);
        Assert.Single(f, x => x.ExternalId == "all");                                          // all ports wins over SSH
        Assert.Equal("high", f.Single(x => x.ExternalId == "ds1").Severity);
        Assert.Equal("medium", f.Single(x => x.ExternalId == "ds2").Severity);
        Assert.DoesNotContain(f, x => x.ExternalId == "ds3");
        Assert.Contains(f, x => x.ExternalId == "h1" && x.Rule == "host_overcommit");
        Assert.Contains(f, x => x.ExternalId == "h1" && x.Rule == "host_eos");
        Assert.Contains(f, x => x.ExternalId == "cp" && x.Rule == "snapshot_old");
        Assert.Contains(f, x => x.ExternalId == "db" && x.Rule == "db_public");
        Assert.Contains(f, x => x.ExternalId == "db" && x.Rule == "db_unencrypted");
        // Virtualization platforms are not asked for tags.
        Assert.DoesNotContain(CloudRules.Evaluate(CloudProviders.VMware, items, _ => null, now), x => x.Rule == "untagged");
        Assert.True(CloudRules.OsOutOfSupport("Windows Server 2012 R2 Datacenter"));
        Assert.False(CloudRules.OsOutOfSupport("Windows Server 2022 Datacenter"));
    }

    [Fact]
    public void Aws_query_api_answers_are_parsed()
    {
        const string ns = "http://ec2.amazonaws.com/doc/2016-11-15/";
        var instances = XDocument.Parse($"""
            <DescribeInstancesResponse xmlns="{ns}"><reservationSet><item><instancesSet><item>
              <instanceId>i-0abc</instanceId><instanceType>t3.medium</instanceType><instanceState><code>80</code><name>stopped</name></instanceState>
              <reason>User initiated (2026-01-10 08:30:00 GMT)</reason><privateDnsName>ip-10-0-0-5.ec2.internal</privateDnsName><privateIpAddress>10.0.0.5</privateIpAddress>
              <vpcId>vpc-1</vpcId><platformDetails>Windows</platformDetails><placement><availabilityZone>ap-south-1a</availabilityZone></placement>
              <networkInterfaceSet><item><macAddress>0a:1b:2c:3d:4e:5f</macAddress><privateIpAddress>10.0.0.5</privateIpAddress></item></networkInterfaceSet>
              <tagSet><item><key>Name</key><value>web-01</value></item><item><key>dept</key><value>IT</value></item></tagSet>
            </item></instancesSet></item></reservationSet></DescribeInstancesResponse>
            """);
        var snap = new CloudSnapshot();
        AwsCollector.ParseInstances([instances], "ap-south-1", snap);
        var vm = Assert.Single(snap.Items);
        Assert.Equal(("i-0abc", "web-01", "stopped", "t3.medium"), (vm.ExternalId, vm.Name, vm.State, vm.Size));
        Assert.Equal(("ip-10-0-0-5", "10.0.0.5", "vpc-1"), (vm.Hostname, vm.PrivateIp, vm.Group));
        Assert.Equal(new[] { "0A:1B:2C:3D:4E:5F" }, vm.Macs.ToArray());
        Assert.Equal(new DateTimeOffset(2026, 1, 10, 8, 30, 0, TimeSpan.Zero), vm.StateSince);
        Assert.Equal("IT", vm.Tags["dept"]);

        var groups = XDocument.Parse($"""
            <DescribeSecurityGroupsResponse xmlns="{ns}"><securityGroupInfo><item><groupId>sg-1</groupId><groupName>open</groupName>
              <ipPermissions><item><ipProtocol>tcp</ipProtocol><fromPort>22</fromPort><toPort>22</toPort><ipRanges><item><cidrIp>0.0.0.0/0</cidrIp></item></ipRanges></item>
              <item><ipProtocol>tcp</ipProtocol><fromPort>3389</fromPort><toPort>3389</toPort><ipRanges><item><cidrIp>10.0.0.0/8</cidrIp></item></ipRanges></item></ipPermissions>
            </item></securityGroupInfo></DescribeSecurityGroupsResponse>
            """);
        var volumes = XDocument.Parse($"""
            <DescribeVolumesResponse xmlns="{ns}"><volumeSet><item><volumeId>vol-1</volumeId><size>100</size><status>available</status><encrypted>false</encrypted><volumeType>gp3</volumeType></item></volumeSet></DescribeVolumesResponse>
            """);
        var s2 = new CloudSnapshot();
        AwsCollector.ParseSecurityGroups([groups], "ap-south-1", s2);
        AwsCollector.ParseVolumes([volumes], "ap-south-1", s2);
        Assert.Equal(new[] { 22 }, s2.Items.Single(i => i.Kind == "security_group").Ports("openPorts").ToArray());
        var vol = s2.Items.Single(i => i.Kind == "disk");
        Assert.Equal((false, false, 100m), (vol.Bool("attached"), vol.Bool("encrypted"), vol.DiskGb));

        var rds = XDocument.Parse("""
            <DescribeDBInstancesResponse xmlns="http://rds.amazonaws.com/doc/2014-10-31/"><DescribeDBInstancesResult><DBInstances><DBInstance>
              <DBInstanceIdentifier>orders</DBInstanceIdentifier><DBInstanceArn>arn:aws:rds:ap-south-1:1:db:orders</DBInstanceArn><Engine>postgres</Engine><EngineVersion>16.3</EngineVersion>
              <DBInstanceClass>db.t3.micro</DBInstanceClass><DBInstanceStatus>available</DBInstanceStatus><PubliclyAccessible>true</PubliclyAccessible><StorageEncrypted>true</StorageEncrypted>
              <AllocatedStorage>20</AllocatedStorage><Endpoint><Address>orders.x.rds.amazonaws.com</Address></Endpoint>
              <TagList><Tag><Key>owner</Key><Value>ops</Value></Tag></TagList>
            </DBInstance></DBInstances><Marker>next</Marker></DescribeDBInstancesResult></DescribeDBInstancesResponse>
            """);
        var s3 = new CloudSnapshot();
        AwsCollector.ParseDatabases([rds], "ap-south-1", s3);
        var db = Assert.Single(s3.Items);
        Assert.Equal(("orders", "postgres 16.3", true, true, "ops"), (db.Name, db.Os, db.Bool("publiclyAccessible"), db.Bool("encrypted"), db.Tags["owner"]));

        Assert.Equal(("UnauthorizedOperation", "You are not authorized"), AwsCollector.ParseError(
            "<Response><Errors><Error><Code>UnauthorizedOperation</Code><Message>You are not authorized</Message></Error></Errors></Response>"));
        Assert.Equal(("AccessDeniedException", "no"), AwsCollector.ParseError("""{"__type":"AccessDeniedException","message":"no"}"""));
    }

    [Fact]
    public void Vmware_answers_from_api_and_legacy_rest_are_mapped()
    {
        var summary = JsonDocument.Parse("""{"vm":"vm-42","name":"SQL01","power_state":"POWERED_ON","cpu_count":4,"memory_size_MiB":16384}""").RootElement;
        var api = JsonDocument.Parse("""
            {"guest_OS":"WINDOWS_SERVER_2021","identity":{"bios_uuid":"4213a1b2-c3d4-e5f6-0718-293a4b5c6d7e"},
             "nics":{"4000":{"mac_address":"00:50:56:aa:bb:cc"}},"disks":{"2000":{"capacity":107374182400},"2001":{"capacity":53687091200}},"hardware":{"version":"VMX_19"}}
            """).RootElement;
        var guest = JsonDocument.Parse("""{"host_name":"sql01.corp.example.com","ip_address":"10.2.0.10","full_name":{"default_message":"Microsoft Windows Server 2022 (64-bit)"}}""").RootElement;
        var vm = VmwareCollector.MapVm(summary, api, guest);
        Assert.Equal(("vm-42", "SQL01", "running", 4, 16384L), (vm.ExternalId, vm.Name, vm.State, vm.Cpu, vm.MemoryMb));
        Assert.Equal("4213A1B2-C3D4-E5F6-0718-293A4B5C6D7E", vm.BiosUuid);
        Assert.Equal(new[] { "00:50:56:AA:BB:CC" }, vm.Macs.ToArray());
        Assert.Equal(150m, vm.DiskGb);
        Assert.Equal(("sql01", "10.2.0.10", "Microsoft Windows Server 2022 (64-bit)"), (vm.Hostname, vm.PrivateIp, vm.Os));

        var legacy = JsonDocument.Parse("""{"guest_OS":"RHEL_8_64","nics":[{"key":"4000","value":{"mac_address":"00:50:56:01:02:03"}}],"disks":[{"key":"2000","value":{"capacity":21474836480}}]}""").RootElement;
        var off = VmwareCollector.MapVm(JsonDocument.Parse("""{"vm":"vm-7","name":"old","power_state":"POWERED_OFF"}""").RootElement, legacy, null);
        Assert.Equal(("off", "RHEL 8 64", 20m), (off.State, off.Os, off.DiskGb));
        Assert.Equal(new[] { "00:50:56:01:02:03" }, off.Macs.ToArray());

        var ds = VmwareCollector.MapDatastore(JsonDocument.Parse("""{"datastore":"datastore-1","name":"SAN01","type":"VMFS","free_space":107374182400,"capacity":1099511627776}""").RootElement);
        Assert.Equal((1024m, 10L), (ds.DiskGb, ds.Long("freePercent")));
    }

    [Fact]
    public void HyperV_wmi_rows_become_host_and_virtual_machines()
    {
        const string id = "9F3C1A22-4B5D-4E6F-8A9B-0C1D2E3F4A5B";
        var now = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows(params Dictionary<string, object?>[] r) => r;
        IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string ns, string wql)
        {
            if (wql.Contains("Win32_ComputerSystemProduct")) return Rows(new() { ["UUID"] = "4c4c4544-0042-3510-8051-b4c04f4d3232", ["IdentifyingNumber"] = "ABC123" });
            if (wql.Contains("Win32_ComputerSystem")) return Rows(new() { ["Name"] = "HV01", ["Domain"] = "corp.example.com", ["TotalPhysicalMemory"] = 17179869184L, ["NumberOfLogicalProcessors"] = 16 });
            if (wql.Contains("Win32_OperatingSystem")) return Rows(new() { ["Caption"] = "Microsoft Windows Server 2012 R2 Datacenter", ["Version"] = "6.3.9600" });
            if (wql.Contains("Msvm_ComputerSystem")) return Rows(new() { ["Name"] = id, ["ElementName"] = "DC02", ["EnabledState"] = (ushort)2, ["TimeOfLastStateChange"] = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc) });
            if (wql.Contains("Msvm_VirtualSystemSettingData")) return Rows(
                new() { ["VirtualSystemIdentifier"] = id, ["VirtualSystemType"] = "Microsoft:Hyper-V:System:Realized", ["BIOSGUID"] = "{11112222-3333-4444-5555-666677778888}", ["VirtualSystemSubType"] = "Microsoft:Hyper-V:SubType:2" },
                new() { ["VirtualSystemIdentifier"] = id, ["VirtualSystemType"] = "Microsoft:Hyper-V:Snapshot:Realized", ["CreationTime"] = new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc) });
            if (wql.Contains("Msvm_MemorySettingData")) return Rows(new() { ["InstanceID"] = $"Microsoft:{id}\\4764334d-e001-4176-82ee-5594ec9b530e", ["VirtualQuantity"] = 24576UL });
            if (wql.Contains("Msvm_ProcessorSettingData")) return Rows(new() { ["InstanceID"] = $"Microsoft:{id}\\b637f346-6a0e-4dec-af52-bd70cb80a21d\\0", ["VirtualQuantity"] = 4UL });
            if (wql.Contains("Msvm_SyntheticEthernetPortSettingData")) return Rows(new() { ["InstanceID"] = $"Microsoft:{id}\\C3F1", ["Address"] = "00155D010203" });
            if (wql.Contains("Msvm_KvpExchangeComponent")) return Rows(new() { ["SystemName"] = id, ["GuestIntrinsicExchangeItems"] = new[]
            {
                "<INSTANCE CLASSNAME=\"Msvm_KvpExchangeDataItem\"><PROPERTY NAME=\"Data\" TYPE=\"string\"><VALUE>dc02.corp.example.com</VALUE></PROPERTY><PROPERTY NAME=\"Name\" TYPE=\"string\"><VALUE>FullyQualifiedDomainName</VALUE></PROPERTY></INSTANCE>",
                "<INSTANCE CLASSNAME=\"Msvm_KvpExchangeDataItem\"><PROPERTY NAME=\"Data\" TYPE=\"string\"><VALUE>10.0.0.2;fe80::1</VALUE></PROPERTY><PROPERTY NAME=\"Name\" TYPE=\"string\"><VALUE>NetworkAddressIPv4</VALUE></PROPERTY></INSTANCE>",
            } });
            return [];
        }
        var snap = new CloudSnapshot();
        HyperVReader.Read("hv01", Query, snap, now);
        var vm = snap.Items.Single(i => i.Kind == "vm");
        Assert.Equal((id.ToLowerInvariant(), "DC02", "running", 4, 24576L), (vm.ExternalId, vm.Name, vm.State, vm.Cpu, vm.MemoryMb));
        Assert.Equal("11112222-3333-4444-5555-666677778888", vm.BiosUuid);
        Assert.Equal(new[] { "00:15:5D:01:02:03" }, vm.Macs.ToArray());
        Assert.Equal(("dc02", "10.0.0.2"), (vm.Hostname, vm.PrivateIp));
        Assert.Equal((1L, 20L), (vm.Long("snapshotCount"), vm.Long("snapshotOldestDays")));
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), vm.StateSince);
        var host = snap.Items.Single(i => i.Kind == "host");
        Assert.Equal("host:hv01", host.ExternalId);
        Assert.Equal(host.ExternalId, vm.ParentId);
        Assert.Equal(16384L, host.MemoryMb);
        Assert.Equal(150L, host.Long("memoryCommitPercent"));
        Assert.True(host.Bool("osEndOfSupport"));
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return answer(request, body);
        }
    }
    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Azure_signs_in_lists_subscriptions_and_pages_resource_graph()
    {
        var page = 0;
        var h = new FakeHandler((req, body) => req.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/oauth2/v2.0/token") => body.Contains("client_secret=s3cret") ? Json("""{"access_token":"tok"}""")
                : Json("""{"error":"invalid_client","error_description":"AADSTS7000215: Invalid client secret provided.\r\nTrace ID: x"}""", HttpStatusCode.Unauthorized),
            "/subscriptions" => req.Headers.Authorization?.Parameter == "tok" ? Json("""{"value":[{"subscriptionId":"s1","displayName":"Prod"}]}""") : Json("{}", HttpStatusCode.Unauthorized),
            _ => ++page == 1
                ? Json("""{"data":[{"id":"/subscriptions/s1/rg/x/disks/d1","name":"d1","type":"microsoft.compute/disks","properties":{"diskState":"Attached","diskSizeGB":32}}],"$skipToken":"p2"}""")
                : body.Contains("p2") ? Json("""{"data":[{"id":"/subscriptions/s1/rg/x/disks/d2","name":"d2","type":"microsoft.compute/disks","properties":{"diskState":"Unattached"}}]}""") : Json("{}", HttpStatusCode.BadRequest),
        });
        var c = new AzureCollector(new HttpClient(h), "https://login.test", "https://arm.test");
        var s = new CloudSettings { TenantId = "t1", ClientId = "c1" };
        var snap = await c.CollectAsync(s, "s3cret", default);
        Assert.Equal(new[] { "d1", "d2" }, snap.Items.Select(i => i.Name).ToArray());
        Assert.Contains("Prod", snap.Identity);
        var ex = await Assert.ThrowsAsync<CloudException>(() => new AzureCollector(new HttpClient(h), "https://login.test", "https://arm.test").CollectAsync(s, "wrong", default));
        Assert.Contains("client secret is wrong", ex.Message);
        await Assert.ThrowsAsync<CloudException>(() => c.CollectAsync(new CloudSettings { TenantId = "t1" }, "x", default));
    }

    [Fact]
    public async Task Aws_missing_permission_for_one_service_is_a_warning_not_a_failure()
    {
        var h = new FakeHandler((req, body) =>
        {
            Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIAEXAMPLE0000000/", req.Headers.GetValues("Authorization").Single());
            var host = req.RequestUri!.Host;
            if (host.StartsWith("sts")) return Json("<GetCallerIdentityResponse><GetCallerIdentityResult><Arn>arn:aws:iam::1:user/zq</Arn><Account>1</Account></GetCallerIdentityResult></GetCallerIdentityResponse>");
            if (host.StartsWith("rds")) return Json("<ErrorResponse><Error><Code>AccessDenied</Code><Message>no</Message></Error></ErrorResponse>", HttpStatusCode.Forbidden);
            if (host.StartsWith("ec2") && body.Contains("DescribeVolumes")) return Json("<DescribeVolumesResponse><volumeSet><item><volumeId>vol-1</volumeId><status>in-use</status><size>8</size></item></volumeSet></DescribeVolumesResponse>");
            if (host.StartsWith("lambda")) return Json("""{"Functions":[{"FunctionName":"f1","FunctionArn":"arn:f1","Runtime":"python3.12","MemorySize":128}]}""");
            if (host.StartsWith("eks")) return Json("""{"clusters":[]}""");
            if (host.StartsWith("s3")) return Json("<ListAllMyBucketsResult><Buckets><Bucket><Name>logs</Name></Bucket></Buckets></ListAllMyBucketsResult>");
            return Json("<Response/>");
        });
        var snap = await new AwsCollector(new HttpClient(h), (svc, region) => $"https://{svc}.{region}.test").CollectAsync(
            new CloudSettings { AccessKeyId = "AKIAEXAMPLE0000000", Regions = ["ap-south-1"] }, "secret", default);
        Assert.Contains("arn:aws:iam::1:user/zq", snap.Identity);
        Assert.Contains(snap.Items, i => i.Kind == "disk" && i.ExternalId == "vol-1");
        Assert.Contains(snap.Items, i => i.Kind == "function" && i.MemoryMb == 128);
        Assert.Contains(snap.Items, i => i.Kind == "storage" && i.Name == "logs");
        Assert.Contains("database", snap.PartialKinds);
        Assert.True(snap.Complete);
        Assert.Contains(snap.Warnings, w => w.Contains("RDS") && w.Contains("ReadOnlyAccess"));
    }
}

public sealed class CloudDatabaseTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        if (Environment.GetEnvironmentVariable("ZENTRIQ_TEST_PG") is { Length: > 0 })
            await new SqlMigrator(NullLogger<SqlMigrator>.Instance).MigrateAsync(_db.OwnerConnection, _db.App, "1.1.0-test");
    }
    public Task DisposeAsync() => _db.DisposeAsync();

    [DbFact]
    public async Task Sync_stores_changes_links_devices_and_tracks_findings()
    {
        await using var ds = NpgsqlDataSource.Create(_db.AppConnection);
        var secrets = new AppSecrets { CredentialKey = SecretStore.NewKey() };
        var store = new CloudStore(ds, new CredentialProtector(() => secrets));
        var account = Guid.NewGuid();
        var asset = Guid.NewGuid();
        await using (var c = await ds.OpenConnectionAsync())
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO zentriq.cloud_accounts (id, name, provider, secret_protected) VALUES (@a, 'Azure test', 'azure', NULL);
                INSERT INTO zentriq.assets (id, hostname, category, status, last_seen_at) VALUES (@x, 'app01', 'endpoint', 'active', now());
                INSERT INTO zentriq.asset_identifiers (asset_id, kind, value) VALUES (@x, 'smbios_uuid', 'DDCCBBAA-FFEE-1100-2233-445566778899');
                INSERT INTO zentriq.departments (code, name) VALUES ('FIN', 'Finance');
                """, c);
            cmd.Parameters.AddWithValue("a", account);
            cmd.Parameters.AddWithValue("x", asset);
            await cmd.ExecuteNonQueryAsync();
        }
        var t0 = DateTimeOffset.UtcNow.AddDays(-45);
        CloudSnapshot Snap(string vmState, bool withDisk, bool complete = true)
        {
            var s = new CloudSnapshot { Complete = complete };
            s.Items.Add(new CloudItem { Kind = "vm", ExternalId = "/s/vm1", Name = "vm-app01", State = vmState, BiosUuid = "AABBCCDD-EEFF-0011-2233-445566778899", Tags = { ["department"] = "finance" } });
            if (withDisk) s.Items.Add(new CloudItem { Kind = "disk", ExternalId = "/s/disk1", Name = "disk1", DiskGb = 64, Properties = { ["attached"] = false } });
            return s;
        }

        var r1 = await store.SaveSnapshotAsync(account, CloudProviders.Azure, Snap("deallocated", true), t0, default);
        Assert.Equal((2, 2, 0, 1), (r1.Resources, r1.Created, r1.Deleted, r1.Linked));
        var r2 = await store.SaveSnapshotAsync(account, CloudProviders.Azure, Snap("deallocated", true), DateTimeOffset.UtcNow, default);
        Assert.Equal(0, r2.Changed);
        Assert.Equal(1, r2.NewFindings);                         // findings kept; the VM is now off for 45 days (vm_off)

        await using var c2 = await ds.OpenConnectionAsync();
        async Task<T> Scalar<T>(string sql)
        {
            await using var q = new NpgsqlCommand(sql, c2);
            q.Parameters.AddWithValue("a", account);
            return (T)(await q.ExecuteScalarAsync())!;
        }
        Assert.Equal(asset, await Scalar<Guid>("SELECT asset_id FROM zentriq.cloud_resources WHERE account_id = @a AND kind = 'vm'"));
        Assert.Equal("Finance", await Scalar<string>("SELECT d.name FROM zentriq.cloud_resources r JOIN zentriq.departments d ON d.id = r.department_id WHERE r.account_id = @a AND r.kind = 'vm'"));
        Assert.True(await Scalar<bool>("SELECT EXISTS (SELECT 1 FROM zentriq.cloud_findings f JOIN zentriq.cloud_resources r ON r.id = f.resource_id WHERE r.account_id = @a AND f.rule = 'vm_off' AND f.resolved_at IS NULL)"));

        // A partial read does not delete; a complete one does, and resolves its findings. The VM starts: changed, vm_off resolved.
        var r3 = await store.SaveSnapshotAsync(account, CloudProviders.Azure, Snap("running", false, complete: false), DateTimeOffset.UtcNow, default);
        Assert.Equal((1, 0), (r3.Changed, r3.Deleted));
        var r4 = await store.SaveSnapshotAsync(account, CloudProviders.Azure, Snap("running", false), DateTimeOffset.UtcNow, default);
        Assert.Equal(1, r4.Deleted);
        Assert.Equal(0L, await Scalar<long>("SELECT count(*) FROM zentriq.cloud_findings f JOIN zentriq.cloud_resources r ON r.id = f.resource_id WHERE r.account_id = @a AND f.resolved_at IS NULL AND f.rule IN ('vm_off','disk_unattached')"));
        Assert.Equal("changed,created,created,deleted", await Scalar<string>("SELECT string_agg(h.change, ',' ORDER BY h.change) FROM zentriq.cloud_changes h JOIN zentriq.cloud_resources r ON r.id = h.resource_id WHERE r.account_id = @a"));

        // The disk comes back.
        var r5 = await store.SaveSnapshotAsync(account, CloudProviders.Azure, Snap("running", true), DateTimeOffset.UtcNow, default);
        Assert.Equal(1, r5.Restored);
    }
}
