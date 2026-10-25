using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zentriq.Infrastructure.Cloud;

namespace Zentriq.Tests;

/// <summary>Google Cloud, Nutanix, Proxmox VE, XCP-ng / Citrix Hypervisor and oVirt / RHV: answers mapped to resources.</summary>
public sealed class CloudPlatformTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string KeyFile(RSA rsa, string project = "contoso-prod") => JsonSerializer.Serialize(new Dictionary<string, string>
    {
        ["type"] = "service_account", ["project_id"] = project, ["client_email"] = "zentriq@contoso-prod.iam.gserviceaccount.com",
        ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(), ["token_uri"] = "https://oauth2.googleapis.com/token",
    });

    [Fact]
    public void Gcp_key_file_is_read_and_the_jwt_is_signed_with_it()
    {
        using var rsa = RSA.Create(2048);
        var key = GcpCollector.ParseKey(KeyFile(rsa))!;
        Assert.Equal(("zentriq@contoso-prod.iam.gserviceaccount.com", "contoso-prod"), (key.ClientEmail, key.ProjectId));
        Assert.Null(GcpCollector.ParseKey("""{"type":"authorized_user"}"""));
        Assert.Null(GcpCollector.ParseKey("not json"));

        var jwt = GcpCollector.Assertion(key, "https://oauth2.googleapis.com/token", DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        var parts = jwt.Split('.');
        static byte[] B64(string s) => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
        var claims = JsonDocument.Parse(B64(parts[1])).RootElement;
        Assert.Equal("zentriq@contoso-prod.iam.gserviceaccount.com", claims.GetProperty("iss").GetString());
        Assert.Contains("cloud-platform.read-only", claims.GetProperty("scope").GetString());
        Assert.Equal(1_800_000_000 + 3300, claims.GetProperty("exp").GetInt64());
        Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), B64(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void Gcp_compute_storage_sql_and_firewall_answers_are_mapped()
    {
        var snap = new CloudSnapshot();
        GcpCollector.MapInstances([J("""
            {"items":{"zones/asia-south1-a":{"instances":[{"id":"1234567890","name":"web-1","status":"TERMINATED","lastStopTimestamp":"2026-05-01T10:00:00.000-07:00",
              "machineType":"https://www.googleapis.com/compute/v1/projects/p/zones/asia-south1-a/machineTypes/e2-medium","zone":".../zones/asia-south1-a",
              "networkInterfaces":[{"networkIP":"10.160.0.2","accessConfigs":[{"natIP":"34.93.1.2"}]}],"labels":{"env":"prod"},
              "disks":[{"boot":true,"source":".../disks/web-1","licenses":[".../licenses/windows-server-2022-dc"]}]}]},
              "zones/us-east1-b":{"warning":{"code":"NO_RESULTS_ON_PAGE"}}}}
            """)], "contoso-prod", snap);
        var vm = Assert.Single(snap.Items);
        Assert.Equal(("1234567890", "terminated", "e2-medium", "asia-south1-a"), (vm.ExternalId, vm.State, vm.Size, vm.Region));
        Assert.Equal(("10.160.0.2", "34.93.1.2", "Windows server 2022 dc"), (vm.PrivateIp, vm.PublicIp, vm.Os));
        Assert.Equal(new DateTimeOffset(2026, 5, 1, 17, 0, 0, TimeSpan.Zero), vm.StateSince);

        GcpCollector.MapDisks([J("""{"items":{"zones/z":{"disks":[{"id":"9","name":"old-data","sizeGb":"200","zone":".../zones/asia-south1-a","type":".../diskTypes/pd-ssd"}]}}}""")], "p", snap);
        GcpCollector.MapAddresses([J("""{"items":{"regions/r":{"addresses":[{"id":"7","name":"ip-1","address":"35.1.1.1","status":"RESERVED","region":".../regions/asia-south1"},{"id":"8","name":"int","addressType":"INTERNAL"}]}}}""")], "p", snap);
        GcpCollector.MapFirewalls([J("""
            {"items":[{"id":"f1","name":"allow-rdp","direction":"INGRESS","sourceRanges":["0.0.0.0/0"],"allowed":[{"IPProtocol":"tcp","ports":["3389"]}]},
                      {"id":"f2","name":"allow-all","direction":"INGRESS","sourceRanges":["0.0.0.0/0"],"allowed":[{"IPProtocol":"all"}]},
                      {"id":"f3","name":"internal","direction":"INGRESS","sourceRanges":["10.0.0.0/8"],"allowed":[{"IPProtocol":"tcp","ports":["22"]}]},
                      {"id":"f4","name":"off","direction":"INGRESS","disabled":true,"sourceRanges":["0.0.0.0/0"],"allowed":[{"IPProtocol":"tcp","ports":["22"]}]}]}
            """)], "p", snap);
        GcpCollector.MapSql([J("""{"items":[{"name":"orders","databaseVersion":"POSTGRES_16","state":"RUNNABLE","region":"asia-south1", "settings":{"tier":"db-custom-2-7680","dataDiskSizeGb":"100","ipConfiguration":{"ipv4Enabled":true,"authorizedNetworks":[{"value":"0.0.0.0/0"}]}}, "ipAddresses":[{"type":"PRIMARY","ipAddress":"34.1.1.9"}]}]}""")], "p", snap);
        GcpCollector.MapFunctions([J("""{"functions":[{"name":"projects/p/locations/asia-south1/functions/resize","state":"ACTIVE","buildConfig":{"runtime":"python312"},"serviceConfig":{"availableMemory":"512Mi"}}]}""")], "p", snap);

        var disk = snap.Items.Single(i => i.Kind == "disk");
        Assert.Equal((false, 200m, "pd-ssd"), (disk.Bool("attached"), disk.DiskGb, disk.Size));
        var ip = Assert.Single(snap.Items, i => i.Kind == "public_ip");
        Assert.False(ip.Bool("associated"));
        Assert.Equal(new[] { 3389 }, snap.Items.Single(i => i.Name == "allow-rdp").Ports("openPorts").ToArray());
        Assert.Equal(new[] { 0 }, snap.Items.Single(i => i.Name == "allow-all").Ports("openPorts").ToArray());
        Assert.Empty(snap.Items.Single(i => i.Name == "internal").Ports("openPorts"));
        Assert.Empty(snap.Items.Single(i => i.Name == "off").Ports("openPorts"));
        var db = snap.Items.Single(i => i.Kind == "database");
        Assert.Equal(("POSTGRES 16", true, 100m), (db.Os, db.Bool("publiclyAccessible"), db.DiskGb));
        Assert.Equal((512L, "resize", "asia-south1"), (snap.Items.Single(i => i.Kind == "function").MemoryMb, snap.Items.Single(i => i.Kind == "function").Name, snap.Items.Single(i => i.Kind == "function").Region));

        var rules = CloudRules.Evaluate(CloudProviders.Gcp, snap.Items, _ => DateTimeOffset.UtcNow.AddDays(-60), DateTimeOffset.UtcNow).Select(f => f.Rule).ToHashSet();
        Assert.Superset(new HashSet<string> { "vm_off", "disk_unattached", "ip_idle", "exposed_admin", "exposed_all", "db_public", "untagged" }, rules);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            answer(request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
    }
    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Gcp_disabled_api_is_a_warning_and_the_keys_project_is_used()
    {
        using var rsa = RSA.Create(2048);
        var h = new FakeHandler((req, body) => req.RequestUri!.Host switch
        {
            "oauth2.test" => body.Contains("jwt-bearer") ? Json("""{"access_token":"ya29.x"}""") : Json("{}", HttpStatusCode.BadRequest),
            "cloudresourcemanager.test" => Json("""{"error":{"code":403,"message":"Cloud Resource Manager API has not been used in project 1 before or it is disabled.","status":"PERMISSION_DENIED"}}""", HttpStatusCode.Forbidden),
            "sqladmin.test" => Json("""{"error":{"code":403,"message":"Cloud SQL Admin API has not been used in project 1 before or it is disabled.","details":[{"reason":"SERVICE_DISABLED"}]}}""", HttpStatusCode.Forbidden),
            "compute.test" when req.RequestUri.AbsolutePath.EndsWith("/aggregated/instances") =>
                Assert.IsType<string>(req.Headers.Authorization?.Parameter) == "ya29.x" ? Json("""{"items":{"zones/a":{"instances":[{"id":"1","name":"vm1","status":"RUNNING"}]}}}""") : Json("{}", HttpStatusCode.Unauthorized),
            _ => Json("{}"),
        });
        var c = new GcpCollector(new HttpClient(h), "https://oauth2.test/token", api => $"https://{api}.test");
        var snap = await c.CollectAsync(new CloudSettings(), KeyFile(rsa), default);
        Assert.Contains("contoso-prod", snap.Identity);
        Assert.Single(snap.Items, i => i.Kind == "vm");
        Assert.Contains("database", snap.PartialKinds);
        Assert.Contains(snap.Warnings, w => w.Contains("Cloud SQL") && w.Contains("not enabled"));
        await Assert.ThrowsAsync<CloudException>(() => c.CollectAsync(new CloudSettings(), """{"type":"service_account"}""", default));
    }

    [Fact]
    public void Nutanix_v3_lists_are_mapped()
    {
        var snap = new CloudSnapshot();
        NutanixCollector.Map(
            [J("""{"metadata":{"uuid":"c1"},"status":{"name":"NTNX-DC1","state":"COMPLETE","resources":{"nodes":{"hypervisor_server_list":[{},{},{}]},"config":{"service_list":["AOS"],"build":{"version":"6.5.5"}}}}}"""),
             J("""{"metadata":{"uuid":"pc"},"status":{"name":"PC","resources":{"config":{"service_list":["PRISM_CENTRAL"]}}}}""")],
            [J("""{"metadata":{"uuid":"h1"},"status":{"name":"NTNX-A","cluster_reference":{"uuid":"c1"},"resources":{"hypervisor":{"hypervisor_full_name":"AHV 20230302","ip":"10.0.0.11"}, "num_cpu_sockets":2,"num_cpu_cores":32,"memory_capacity_mib":524288,"serial_number":"21SM1A","block":{"block_model":"NX-3170-G8"}}}}""")],
            [J("""{"metadata":{"uuid":"7a1b2c3d-1111-2222-3333-444455556666","categories":{"Department":"Finance"},"creation_time":"2025-01-02T03:04:05Z"}, "status":{"name":"FIN-APP01","cluster_reference":{"uuid":"c1"},"resources":{"power_state":"ON","num_sockets":2,"num_vcpus_per_socket":2,"memory_size_mib":16384, "host_reference":{"uuid":"h1"},"nic_list":[{"mac_address":"50:6b:8d:aa:bb:cc","ip_endpoint_list":[{"ip":"10.0.1.20"}]}], "disk_list":[{"disk_size_bytes":107374182400,"device_properties":{"device_type":"DISK"}},{"disk_size_bytes":5000000,"device_properties":{"device_type":"CDROM"}}]}}}""")],
            snap);
        Assert.Single(snap.Items, i => i.Kind == "cluster");                       // Prism Central itself is not a cluster
        var host = snap.Items.Single(i => i.Kind == "host");
        Assert.Equal(("AHV 20230302", 32, 524288L, "NTNX-DC1"), (host.Os, host.Cpu, host.MemoryMb, host.Group));
        var vm = snap.Items.Single(i => i.Kind == "vm");
        Assert.Equal(("running", 4, 16384L, 100m, "10.0.1.20"), (vm.State, vm.Cpu, vm.MemoryMb, vm.DiskGb, vm.PrivateIp));
        Assert.Equal(("7A1B2C3D-1111-2222-3333-444455556666", "h1", "Finance"), (vm.BiosUuid, vm.ParentId, vm.Tags["department"]));
        Assert.Equal(new[] { "50:6B:8D:AA:BB:CC" }, vm.Macs.ToArray());
    }

    [Fact]
    public void Proxmox_cluster_resources_config_snapshots_and_agent_are_mapped()
    {
        var now = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var rows = new List<JsonElement>
        {
            J("""{"type":"node","node":"pve1","status":"online","maxcpu":32,"maxmem":137438953472,"uptime":7200}"""),
            J("""{"type":"storage","id":"storage/pve1/local-lvm","storage":"local-lvm","node":"pve1","plugintype":"lvmthin","disk":966367641600,"maxdisk":1073741824000,"status":"available","shared":0}"""),
            J("""{"type":"qemu","vmid":101,"name":"dc01","node":"pve1","status":"running","maxcpu":4,"maxmem":8589934592,"maxdisk":68719476736,"tags":"prod;windows","template":0}"""),
            J("""{"type":"lxc","vmid":200,"name":"web","node":"pve1","status":"stopped","maxcpu":2,"maxmem":2147483648,"maxdisk":8589934592}"""),
            J("""{"type":"qemu","vmid":9000,"name":"tmpl","node":"pve1","status":"stopped","template":1}"""),
        };
        var details = new Dictionary<string, (JsonElement, JsonElement?, JsonElement?, JsonElement?)>
        {
            ["qemu/101"] = (J("""{"data":{"ostype":"win11","net0":"virtio=BC:24:11:AA:BB:CC,bridge=vmbr0,firewall=1","smbios1":"uuid=0e1d2c3b-aaaa-bbbb-cccc-ddddeeeeffff","agent":"1"}}"""),
                J("""{"data":[{"name":"before-update","snaptime":1788220800},{"name":"current"}]}"""),
                J("""{"data":{"result":[{"name":"Loopback Pseudo-Interface 1","ip-addresses":[{"ip-address":"127.0.0.1","ip-address-type":"ipv4"}]},{"name":"Ethernet","ip-addresses":[{"ip-address":"fe80::1","ip-address-type":"ipv6"},{"ip-address":"10.0.0.10","ip-address-type":"ipv4"}]}]}}"""),
                J("""{"data":{"result":{"pretty-name":"Windows Server 2022 Standard"}}}""")),
            ["lxc/200"] = (J("""{"data":{"hostname":"web01","ostype":"debian","net0":"name=eth0,bridge=vmbr0,hwaddr=BC:24:11:00:11:22,ip=10.0.0.20/24"}}"""), null, null, null),
        };
        var snap = new CloudSnapshot();
        ProxmoxCollector.Map(rows, details, snap, now);
        Assert.DoesNotContain(snap.Items, i => i.Name == "tmpl");
        var host = snap.Items.Single(i => i.Kind == "host");
        Assert.Equal(("node/pve1", "running", 131072L, 6L), (host.ExternalId, host.State, host.MemoryMb, host.Long("memoryCommitPercent")));
        var ds = snap.Items.Single(i => i.Kind == "datastore");
        Assert.Equal((1000m, 10L), (ds.DiskGb, ds.Long("freePercent")));
        var vm = snap.Items.Single(i => i.ExternalId == "qemu/101");
        Assert.Equal(("0E1D2C3B-AAAA-BBBB-CCCC-DDDDEEEEFFFF", "10.0.0.10", "Windows Server 2022 Standard", 8192L), (vm.BiosUuid, vm.PrivateIp, vm.Os, vm.MemoryMb));
        Assert.Equal(new[] { "BC:24:11:AA:BB:CC" }, vm.Macs.ToArray());
        Assert.Equal((1L, 30L), (vm.Long("snapshotCount"), vm.Long("snapshotOldestDays")));
        Assert.True(vm.Tags.ContainsKey("prod"));
        var ct = snap.Items.Single(i => i.ExternalId == "lxc/200");
        Assert.Equal(("stopped", "web01", "10.0.0.20", "Linux container (debian)"), (ct.State, ct.Hostname, ct.PrivateIp, ct.Os));
    }

    [Fact]
    public void XenServer_records_are_mapped()
    {
        var now = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var d = new Dictionary<string, JsonElement>
        {
            ["pool"] = J("""{"OpaqueRef:p":{"uuid":"pool-1","name_label":"DC2 pool","ha_enabled":false}}"""),
            ["host"] = J("""{"OpaqueRef:h1":{"uuid":"host-1","name_label":"xcp01","hostname":"xcp01","address":"10.0.0.31","enabled":true,"metrics":"OpaqueRef:m1", "software_version":{"product_brand":"XCP-ng","product_version":"8.3.0"},"cpu_info":{"cpu_count":"24"},"bios_strings":{"system-manufacturer":"Dell Inc.","system-product-name":"PowerEdge R650","system-serial-number":"7XQ1"}}}"""),
            ["host_metrics"] = J("""{"OpaqueRef:m1":{"memory_total":"68719476736"}}"""),
            ["VM"] = J("""{"OpaqueRef:v1":{"uuid":"a1b2c3d4-0000-1111-2222-333344445555","name_label":"SQL02","power_state":"Running","is_a_template":false,"is_a_snapshot":false,"is_control_domain":false, "VCPUs_max":"4","memory_static_max":"17179869184","resident_on":"OpaqueRef:h1","guest_metrics":"OpaqueRef:g1","snapshots":["OpaqueRef:s1"],"tags":["prod"]}, "OpaqueRef:s1":{"uuid":"snap","name_label":"before","is_a_snapshot":true,"is_a_template":false,"is_control_domain":false,"snapshot_time":"20260901T00:00:00Z"}, "OpaqueRef:dom0":{"uuid":"dom0","name_label":"Control domain","is_control_domain":true,"is_a_template":false,"is_a_snapshot":false}, "OpaqueRef:t":{"uuid":"tmpl","name_label":"Debian 12","is_a_template":true,"is_a_snapshot":false,"is_control_domain":false}}"""),
            ["VIF"] = J("""{"OpaqueRef:i1":{"VM":"OpaqueRef:v1","MAC":"da:4e:6b:00:11:22"}}"""),
            ["VM_guest_metrics"] = J("""{"OpaqueRef:g1":{"os_version":{"name":"Microsoft Windows Server 2022 Standard|C:\\Windows|\\Device\\Harddisk0\\Partition2"},"networks":{"0/ip":"10.0.2.15","0/ipv6/0":"fe80::1"}}}"""),
            ["SR"] = J("""{"OpaqueRef:r1":{"uuid":"sr-1","name_label":"Local storage","type":"lvm","content_type":"user","physical_size":"1000000000000","physical_utilisation":"950000000000","shared":false}, "OpaqueRef:r2":{"uuid":"sr-iso","name_label":"ISO","type":"iso","content_type":"iso"}}"""),
        };
        var snap = new CloudSnapshot();
        XenServerCollector.Map(d, snap, now);
        Assert.Single(snap.Items, i => i.Kind == "vm");                            // no dom0, template or snapshot
        var vm = snap.Items.Single(i => i.Kind == "vm");
        Assert.Equal(("running", 4, 16384L, "10.0.2.15", "Microsoft Windows Server 2022 Standard"), (vm.State, vm.Cpu, vm.MemoryMb, vm.PrivateIp, vm.Os));
        Assert.Equal(("A1B2C3D4-0000-1111-2222-333344445555", "host-1", 30L), (vm.BiosUuid, vm.ParentId, vm.Long("snapshotOldestDays")));
        var host = snap.Items.Single(i => i.Kind == "host");
        Assert.Equal(("XCP-ng 8.3.0", 24, 65536L, 25L), (host.Os, host.Cpu, host.MemoryMb, host.Long("memoryCommitPercent")));
        var sr = Assert.Single(snap.Items, i => i.Kind == "datastore");
        Assert.Equal(5L, sr.Long("freePercent"));
        Assert.Equal("DC2 pool", snap.Items.Single(i => i.Kind == "cluster").Name);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), XenServerCollector.XapiDate("20260901T00:00:00Z"));
    }

    [Fact]
    public void Ovirt_rest_answers_are_mapped()
    {
        var now = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var snap = new CloudSnapshot();
        OvirtCollector.Map(
            J("""{"cluster":[{"id":"cl1","name":"Default","cpu":{"type":"Intel Icelake Server Family"},"version":{"major":"4","minor":"7"}}]}"""),
            J("""{"host":[{"id":"h1","name":"rhvh01","address":"rhvh01.corp.local","status":"up","cluster":{"id":"cl1"},"memory":"274877906944", "cpu":{"topology":{"sockets":"2","cores":"16","threads":"2"}},"os":{"type":"RHEL","version":{"full_version":"8.10"}}, "hardware_information":{"uuid":"4c4c4544-0042-3510-8051-b4c04f4d3232","serial_number":"ABC123","manufacturer":"Dell Inc.","product_name":"PowerEdge R750"}}]}"""),
            J("""{"storage_domain":[{"id":"sd1","name":"data1","type":"data","available":"107374182400","used":"966367641600","committed":"2147483648000","storage":{"type":"nfs"}}, {"id":"iso","name":"iso","type":"iso"}]}"""),
            J("""{"vm":[{"id":"aa11bb22-cc33-dd44-ee55-ff6677889900","name":"app01","status":"down","stop_time":1754006400000,"creation_time":1700000000000, "memory":"8589934592","cpu":{"topology":{"sockets":"1","cores":"4","threads":"1"}},"host":{"id":"h1"},"cluster":{"id":"cl1"},"fqdn":"app01.corp.local", "guest_operating_system":{"distribution":"Red Hat Enterprise Linux","version":{"full_version":"9.4"}}, "nics":{"nic":[{"mac":{"address":"56:6f:1a:2b:3c:4d"}}]}, "reported_devices":{"reported_device":[{"ips":{"ip":[{"address":"fe80::1","version":"v6"},{"address":"10.0.3.5","version":"v4"}]}}]}, "snapshots":{"snapshot":[{"snapshot_type":"active","date":1759000000000},{"snapshot_type":"regular","date":1788220800000}]}}]}"""),
            snap, now);
        var vm = snap.Items.Single(i => i.Kind == "vm");
        Assert.Equal(("down", 4, 8192L, "app01", "10.0.3.5", "Red Hat Enterprise Linux 9.4"), (vm.State, vm.Cpu, vm.MemoryMb, vm.Hostname, vm.PrivateIp, vm.Os));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1754006400000), vm.StateSince);
        Assert.Equal(("AA11BB22-CC33-DD44-EE55-FF6677889900", "Default", 1L, 30L), (vm.BiosUuid, vm.Group, vm.Long("snapshotCount"), vm.Long("snapshotOldestDays")));
        var host = snap.Items.Single(i => i.Kind == "host");
        Assert.Equal(("RHEL 8.10", 64, 262144L, "4C4C4544-0042-3510-8051-B4C04F4D3232"), (host.Os, host.Cpu, host.MemoryMb, host.BiosUuid));
        var sd = Assert.Single(snap.Items, i => i.Kind == "datastore");
        Assert.Equal((1000m, 10L), (sd.DiskGb, sd.Long("freePercent")));
        var f = CloudRules.Evaluate(CloudProviders.Ovirt, snap.Items, i => i.StateSince, now);
        Assert.Contains(f, x => x.Rule == "vm_off");                               // down since August
        Assert.Contains(f, x => x.Rule == "snapshot_old");
        Assert.Contains(f, x => x.Rule == "datastore_full" && x.Severity == "medium");
        Assert.DoesNotContain(f, x => x.Rule == "untagged");
    }

    [Fact]
    public void Every_platform_has_a_title_and_cloud_or_virtualization_class()
    {
        Assert.Equal(9, CloudProviders.All.Length);
        Assert.All(CloudProviders.All, p => Assert.NotEqual(p, CloudProviders.Title(p)));
        Assert.Equal(new[] { "azure", "aws", "gcp" }, CloudProviders.All.Where(CloudProviders.IsCloud).ToArray());
    }
}
