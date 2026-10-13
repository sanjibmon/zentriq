using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Zentriq.Infrastructure.Agents;
using Zentriq.Infrastructure.Secrets;
using Zentriq.Inventory;

namespace Zentriq.Tests;

public sealed class InventoryParserTests
{
    private sealed class FakeWmi(Dictionary<string, List<Dictionary<string, object?>>> byClass) : IWmiSource
    {
        public IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string ns, string wql)
        {
            var cls = System.Text.RegularExpressions.Regex.Match(wql, @"FROM\s+(\w+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups[1].Value;
            if (byClass.TryGetValue(cls, out var rows)) return rows;
            throw new InvalidOperationException("Invalid namespace"); // like WMI when a provider is missing
        }
    }

    private sealed class FakeRegistry(Dictionary<string, Dictionary<string, object>> keys) : IRegistrySource
    {
        public IReadOnlyList<string> SubKeys(string path) =>
            keys.Keys.Where(k => k.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase) && !k[(path.Length + 1)..].Contains('\\')).Select(k => k[(path.Length + 1)..]).ToList();
        public string? GetString(string path, string name) => keys.TryGetValue(path, out var v) && v.TryGetValue(name, out var o) ? o as string : null;
        public int? GetDword(string path, string name) => keys.TryGetValue(path, out var v) && v.TryGetValue(name, out var o) && o is int i ? i : null;
    }

    private static Dictionary<string, object?> Row(params (string K, object? V)[] kv) => kv.ToDictionary(x => x.K, x => x.V, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void VirtualBox_guest_with_emulated_battery_is_a_virtual_machine_not_a_laptop()
    {
        var wmi = new FakeWmi(new()
        {
            ["Win32_ComputerSystem"] = [Row(("Name", "ZENTRIQ"), ("DNSHostName", "ZENTRIQ"), ("PartOfDomain", false), ("Manufacturer", "innotek GmbH"), ("Model", "VirtualBox"), ("PCSystemType", (ushort)2))],
            ["Win32_SystemEnclosure"] = [Row(("ChassisTypes", new ushort[] { 1 }))],
            ["Win32_OperatingSystem"] = [Row(("Caption", "Microsoft Windows Server 2019 Standard Evaluation"), ("Version", "10.0.17763"), ("BuildNumber", "17763"), ("ProductType", 3u))],
        });
        var doc = WindowsCollector.Collect(wmi, null, "agent", "1.1.1");
        Assert.True(doc.Hardware.IsVirtual);
        Assert.Equal("virtual", doc.Device.Chassis);
        Assert.Equal("server", doc.Device.Category);
    }

    [Fact]
    public void Windows_collector_maps_wmi_and_registry_and_records_missing_sections()
    {
        const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        var wmi = new FakeWmi(new()
        {
            ["Win32_ComputerSystem"] = [Row(("Name", "LT-01"), ("DNSHostName", "lt-01"), ("Domain", "corp.example.com"), ("PartOfDomain", true), ("Manufacturer", "Dell Inc."), ("Model", "Latitude 7440"), ("TotalPhysicalMemory", 34359738368UL), ("PCSystemType", (ushort)2), ("UserName", @"CORP\alice"))],
            ["Win32_ComputerSystemProduct"] = [Row(("UUID", "4C4C4544-0042-3510-8048-B7C04F4B3233"), ("IdentifyingNumber", "7HX2LQ3"))],
            ["Win32_BIOS"] = [Row(("SerialNumber", "7HX2LQ3"), ("Manufacturer", "Dell Inc."), ("SMBIOSBIOSVersion", "1.14.1"), ("ReleaseDate", "20250312000000.000000+000"))],
            ["Win32_SystemEnclosure"] = [Row(("ChassisTypes", new ushort[] { 10 }))],
            ["Win32_OperatingSystem"] = [Row(("Caption", "Microsoft Windows 11 Pro"), ("Version", "10.0.26100"), ("BuildNumber", "26100"), ("OSArchitecture", "64-bit"), ("ProductType", 1u))],
            ["Win32_Processor"] = [Row(("Name", "13th Gen Intel(R) Core(TM) i7-1365U"), ("NumberOfCores", 10u), ("NumberOfLogicalProcessors", 12u))],
            ["Win32_DiskDrive"] = [Row(("Model", "NVMe SSD"), ("Size", 512110190592UL))],
            ["Win32_LogicalDisk"] = [Row(("DeviceID", "C:"), ("FileSystem", "NTFS"), ("Size", 510000000000UL), ("FreeSpace", 200000000000UL))],
            ["Win32_NetworkAdapterConfiguration"] = [Row(("Description", "Wi-Fi"), ("MACAddress", "AA-BB-CC-00-11-22"), ("IPAddress", new[] { "10.1.2.3", "fe80::1" }), ("DHCPEnabled", true))],
            ["Win32_QuickFixEngineering"] = [Row(("HotFixID", "KB5066782"), ("Description", "Security Update"), ("InstalledOn", "10/1/2026"))],
            ["Win32_Service"] = [Row(("Name", "Spooler"), ("DisplayName", "Print Spooler"), ("State", "Running"), ("StartMode", "Auto"))],
        });
        var reg = new FakeRegistry(new()
        {
            [uninstall + @"\{A}"] = new() { ["DisplayName"] = "7-Zip 24.08 (x64)", ["DisplayVersion"] = "24.08", ["Publisher"] = "Igor Pavlov", ["InstallDate"] = "20261001" },
            [uninstall + @"\KB123"] = new() { ["DisplayName"] = "Security Update for X", ["SystemComponent"] = 1 },
        });

        var doc = WindowsCollector.Collect(wmi, reg, "agent", "1.1.0");

        Assert.Equal("lt-01", doc.Device.Hostname);
        Assert.Equal("lt-01.corp.example.com", doc.Device.Fqdn);
        Assert.Equal("7HX2LQ3", doc.Device.SerialNumber);
        Assert.Equal("4C4C4544-0042-3510-8048-B7C04F4B3233", doc.Device.SmbiosUuid);
        Assert.Equal("laptop", doc.Device.Chassis);
        Assert.Equal("endpoint", doc.Device.Category);
        Assert.Equal("Windows 11 Pro", doc.Os.Name);
        Assert.False(doc.Os.IsServer);
        Assert.Equal(10, doc.Hardware.CpuCores);
        Assert.Equal(12, doc.Hardware.CpuThreads);
        Assert.Equal("AA:BB:CC:00:11:22", doc.Network.Single().Mac);
        Assert.Equal(new[] { "10.1.2.3" }, doc.Network.Single().Ipv4);
        var sw = Assert.Single(doc.Software);
        Assert.Equal("7-Zip 24.08 (x64)", sw.Name);
        Assert.Equal("24.08", sw.Version);
        Assert.Equal("KB5066782", Assert.Single(doc.Patches).Id);
        Assert.Equal("Spooler", Assert.Single(doc.Services).Name);
        // Optional providers (BitLocker/TPM/Security Center) missing are not errors; required sections did not fail.
        Assert.DoesNotContain(doc.Errors, e => e.StartsWith("computer system", StringComparison.Ordinal));
    }

    [Fact]
    public void Linux_parser_reads_os_hardware_network_and_packages()
    {
        var o = new Dictionary<string, string>
        {
            ["hostname"] = "web01.corp.example.com\n",
            ["osrelease"] = "NAME=\"Ubuntu\"\nVERSION_ID=\"24.04\"\nPRETTY_NAME=\"Ubuntu 24.04.1 LTS\"\n",
            ["cpuinfo"] = "processor\t: 0\nmodel name\t: AMD EPYC 7763\nphysical id\t: 0\ncore id\t\t: 0\n\nprocessor\t: 1\nmodel name\t: AMD EPYC 7763\nphysical id\t: 0\ncore id\t\t: 1\n",
            ["meminfo"] = "MemTotal:        8131072 kB\n",
            ["dmi"] = "sys_vendor=QEMU\nproduct_name=Standard PC (Q35 + ICH9, 2009)\nproduct_serial=\nproduct_uuid=\nchassis_type=1\nbios_vendor=SeaBIOS\nbios_version=1.16\nbios_date=04/01/2014\n",
            ["df"] = "Filesystem Type 1024-blocks Used Available Capacity Mounted on\n/dev/vda1 ext4 50000000 20000000 30000000 40% /\n",
            ["iplink"] = "2: eth0: <BROADCAST,MULTICAST,UP,LOWER_UP> mtu 1500 qdisc fq_codel state UP mode DEFAULT group default qlen 1000\\    link/ether 52:54:00:12:34:56 brd ff:ff:ff:ff:ff:ff\n",
            ["ipaddr"] = "1: lo    inet 127.0.0.1/8 scope host lo\n2: eth0    inet 10.0.0.5/24 brd 10.0.0.255 scope global eth0\n",
            ["route"] = "default via 10.0.0.1 dev eth0 proto static\n",
            ["packages"] = "dpkg\topenssh-server\t1:9.6p1-3ubuntu13\tamd64\ndpkg\tnginx\t1.24.0-2ubuntu7\tamd64\n",
            ["virt"] = "kvm\n",
            ["services"] = "nginx.service loaded active running A high performance web server\n",
        };
        var doc = UnixCollector.ParseLinux(o, "Linux\nx86_64\n6.8.0-45-generic\n", "1.1.0");
        Assert.Equal("web01", doc.Device.Hostname);
        Assert.Equal("corp.example.com", doc.Device.Domain);
        Assert.Equal("Ubuntu 24.04.1 LTS", doc.Os.Name);
        Assert.Equal("AMD EPYC 7763", doc.Hardware.CpuName);
        Assert.Equal(2, doc.Hardware.CpuThreads);
        Assert.Equal(8131072L * 1024, doc.Hardware.MemoryBytes);
        Assert.True(doc.Hardware.IsVirtual);
        var nic = Assert.Single(doc.Network);
        Assert.Equal("52:54:00:12:34:56", nic.Mac);
        Assert.Equal(new[] { "10.0.0.5" }, nic.Ipv4);
        Assert.Equal("10.0.0.1", nic.Gateway);
        Assert.Equal(2, doc.Software.Count);
        Assert.Equal("nginx", Assert.Single(doc.Services).Name);
        Assert.Contains(doc.Errors, e => e.Contains("root", StringComparison.Ordinal)); // serial/uuid need root
    }

    [Fact]
    public void Snmp_mapper_identifies_vendor_category_and_chassis_serial()
    {
        var v = new Dictionary<string, string>
        {
            [SnmpMapper.SysDescr] = "Cisco IOS Software, C2960X Software (C2960X-UNIVERSALK9-M), Version 15.2(7)E8",
            [SnmpMapper.SysObjectId] = "1.3.6.1.4.1.9.1.1208",
            [SnmpMapper.SysName] = "sw-core-01.corp.example.com",
            [SnmpMapper.SysUpTime] = "123456",
            [SnmpMapper.EntPhysicalClass + ".1001"] = "3",
            [SnmpMapper.EntPhysicalSerialNum + ".1001"] = "FOC1234X0AB",
            [SnmpMapper.EntPhysicalModelName + ".1001"] = "WS-C2960X-48FPD-L",
            [SnmpMapper.IfDescr + ".1"] = "GigabitEthernet1/0/1",
            [SnmpMapper.IfPhysAddress + ".1"] = "00:1a:2b:3c:4d:5e",
        };
        var doc = SnmpMapper.Map(v, "10.0.0.2", "1.1.0");
        Assert.Equal("Cisco", doc.Device.Manufacturer);
        Assert.Equal("network_device", doc.Device.Category);
        Assert.Equal("FOC1234X0AB", doc.Device.SerialNumber);
        Assert.Equal("WS-C2960X-48FPD-L", doc.Device.Model);
        Assert.Equal("sw-core-01", doc.Device.Hostname);
        Assert.Equal("00:1A:2B:3C:4D:5E", doc.Network[0].Mac);
    }

    [Fact]
    public void Snmp_mapper_detects_printers() =>
        Assert.Equal("printer", SnmpMapper.Map(new Dictionary<string, string>
        {
            [SnmpMapper.SysDescr] = "HP LaserJet MFP M428fdw",
            [SnmpMapper.SysObjectId] = "1.3.6.1.4.1.11.2.3.9.1",
            [SnmpMapper.PrtGeneralSerial + ".1"] = "PHB8K01234",
        }, "10.0.0.50", "1").Device.Category);
}

public sealed class AgentSecurityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("zq-agent-ca-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void Enrollment_code_round_trips_and_rejects_garbage()
    {
        var code = AgentProtocol.FormatCode(new AgentProtocol.EnrollmentCode { Url = "https://zq.example.com:8444", Token = "secret", Pins = ["pin1"] });
        Assert.StartsWith("ZQ1.", code);
        Assert.True(AgentProtocol.TryParseCode(code, out var c, out _));
        Assert.Equal("https://zq.example.com:8444", c!.Url);
        Assert.Equal(new[] { "pin1" }, c.Pins);
        Assert.False(AgentProtocol.TryParseCode("ZQ1.not-base64!!", out _, out var err));
        Assert.NotNull(err);
        Assert.False(AgentProtocol.TryParseCode(AgentProtocol.FormatCode(new AgentProtocol.EnrollmentCode { Url = "http://plain", Token = "x" }), out _, out _));
    }

    [Theory]
    [InlineData("rsa")]
    [InlineData("ec")]
    public void Agent_spki_pin_matches_dotnet(string alg)
    {
        using AsymmetricAlgorithm key = alg == "rsa" ? RSA.Create(2048) : ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = key is RSA rsa ? new CertificateRequest("CN=srv", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1) : new CertificateRequest("CN=srv", (ECDsa)key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        Assert.Equal(AgentCertificateAuthority.SpkiSha256(cert), AgentProtocol.SpkiSha256(cert.RawData));
    }

    [Fact]
    public void Agent_ca_issues_and_validates_client_certificates()
    {
        var path = Path.Combine(_dir, "agent-ca.pfx");
        Assert.True(AgentCertificateAuthority.Ensure(path, "pw", "TEST"));
        Assert.False(AgentCertificateAuthority.Ensure(path, "pw", "TEST")); // kept
        using var ca = AgentCertificateAuthority.Load(path, "pw");

        using var agentKey = RSA.Create(2048);
        var csr = new CertificateRequest("CN=whatever-the-agent-claims", agentKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequest();
        var id = Guid.NewGuid();
        using var issued = AgentCertificateAuthority.Issue(ca, csr, id);
        Assert.Equal(id.ToString("D"), issued.GetNameInfo(X509NameType.SimpleName, false)); // server decides the subject
        Assert.Equal(id, AgentCertificateAuthority.Validate(ca, issued, out var err));
        Assert.Null(err);

        // A certificate from another CA is refused.
        Assert.True(AgentCertificateAuthority.Ensure(Path.Combine(_dir, "other.pfx"), "pw", "OTHER"));
        using var other = AgentCertificateAuthority.Load(Path.Combine(_dir, "other.pfx"), "pw");
        using var foreign = AgentCertificateAuthority.Issue(other, csr, id);
        Assert.Null(AgentCertificateAuthority.Validate(ca, foreign, out _));
    }

    [Fact]
    public void Agent_ca_refuses_weak_keys()
    {
        var path = Path.Combine(_dir, "agent-ca.pfx");
        AgentCertificateAuthority.Ensure(path, "pw", "TEST");
        using var ca = AgentCertificateAuthority.Load(path, "pw");
        using var weak = RSA.Create(1024);
        var csr = new CertificateRequest("CN=x", weak, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequest();
        Assert.Throws<ArgumentException>(() => AgentCertificateAuthority.Issue(ca, csr, Guid.NewGuid()));
    }

    [Fact]
    public void Credential_secrets_are_bound_to_their_record()
    {
        var secrets = new AppSecrets { CredentialKey = SecretStore.NewKey() };
        var protector = new CredentialProtector(() => secrets);
        var id = Guid.NewGuid();
        var blob = protector.Protect(id, new CredentialSecret { Password = "P@ssw0rd!" });
        Assert.DoesNotContain("P@ssw0rd", blob);
        Assert.Equal("P@ssw0rd!", protector.Unprotect(id, blob).Password);
        // Copying the ciphertext onto another credential row does not decrypt (record id is authenticated data).
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(Guid.NewGuid(), blob));
    }
}
