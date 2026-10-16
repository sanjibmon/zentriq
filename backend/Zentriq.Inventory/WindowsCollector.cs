using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Zentriq.Inventory;

/// <summary>Runs WQL queries (local System.Management in the agent, a remote CIM session over WinRM in the server).</summary>
public interface IWmiSource
{
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string wmiNamespace, string wql);
}

/// <summary>Reads HKEY_LOCAL_MACHINE (64-bit view; 32-bit data via the WOW6432Node path).</summary>
public interface IRegistrySource
{
    IReadOnlyList<string> SubKeys(string path);
    string? GetString(string path, string name);
    int? GetDword(string path, string name);
}

/// <summary>
/// Windows inventory from WMI/CIM and the registry. Read-only: never calls Win32_Product (which can
/// trigger MSI self-repair) and never changes anything on the device. Each section is independent; a
/// failing section is recorded in <see cref="InventoryDocument.Errors"/> and the rest still succeeds.
/// </summary>
public static class WindowsCollector
{
    private const string Cimv2 = @"root\cimv2";
    private static readonly string[] UninstallRoots =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    };

    public static InventoryDocument Collect(IWmiSource wmi, IRegistrySource? registry, string source, string collectorVersion, bool includeServices = true)
    {
        var doc = new InventoryDocument { Source = source, CollectorVersion = collectorVersion, CollectedAt = DateTimeOffset.UtcNow };
        int? productType = null;

        Section(doc, "computer system", () =>
        {
            var cs = First(wmi, Cimv2, "SELECT Name, DNSHostName, Domain, PartOfDomain, Manufacturer, Model, TotalPhysicalMemory, NumberOfLogicalProcessors, UserName, PCSystemType, HypervisorPresent FROM Win32_ComputerSystem");
            if (cs is null) return;
            doc.Device.Hostname = Str(cs, "DNSHostName") ?? Str(cs, "Name");
            var partOfDomain = Bool(cs, "PartOfDomain") == true;
            doc.Device.Domain = partOfDomain ? Str(cs, "Domain") : null;
            doc.Device.Fqdn = doc.Device.Domain is null ? doc.Device.Hostname : $"{doc.Device.Hostname}.{doc.Device.Domain}".ToLowerInvariant();
            doc.Device.Manufacturer = Clean(Str(cs, "Manufacturer"));
            doc.Device.Model = Clean(Str(cs, "Model"));
            doc.Hardware.MemoryBytes = Long(cs, "TotalPhysicalMemory");
            doc.Users.LoggedOn = Str(cs, "UserName");
            if (Int(cs, "PCSystemType") == 2) doc.Device.Chassis = "laptop";
        });

        // Win32_ComputerSystem.UserName is only the console session. Servers are used over Remote Desktop, so when it
        // is empty take the most recent interactive / Remote Desktop logon session instead.
        if (doc.Users.LoggedOn is null)
            Section(doc, "logon sessions", () => doc.Users.LoggedOn = InteractiveUser(wmi), optional: true);

        Section(doc, "system product", () =>
        {
            var p = First(wmi, Cimv2, "SELECT UUID, IdentifyingNumber FROM Win32_ComputerSystemProduct");
            if (p is null) return;
            doc.Device.SmbiosUuid = NormalizeUuid(Str(p, "UUID"));
            doc.Device.SerialNumber = Clean(Str(p, "IdentifyingNumber"));
        });

        Section(doc, "bios", () =>
        {
            var b = First(wmi, Cimv2, "SELECT SerialNumber, Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");
            if (b is null) return;
            doc.Device.SerialNumber ??= Clean(Str(b, "SerialNumber"));
            doc.Hardware.BiosVendor = Clean(Str(b, "Manufacturer"));
            doc.Hardware.BiosVersion = Str(b, "SMBIOSBIOSVersion");
            doc.Hardware.BiosDate = Date(b, "ReleaseDate");
        });

        Section(doc, "enclosure", () =>
        {
            var e = First(wmi, Cimv2, "SELECT ChassisTypes FROM Win32_SystemEnclosure");
            var types = e is null ? Array.Empty<long>() : Numbers(e, "ChassisTypes");
            doc.Device.Chassis ??= ChassisName(types);
        });

        Section(doc, "operating system", () =>
        {
            var os = First(wmi, Cimv2, "SELECT Caption, Version, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime, ProductType FROM Win32_OperatingSystem");
            if (os is null) return;
            doc.Os.Family = "windows";
            doc.Os.Name = Str(os, "Caption")?.Replace("Microsoft ", "").Trim();
            doc.Os.Version = Str(os, "Version");
            doc.Os.Build = Str(os, "BuildNumber");
            doc.Os.Architecture = Str(os, "OSArchitecture");
            doc.Os.InstallDate = Date(os, "InstallDate");
            doc.Os.LastBoot = Date(os, "LastBootUpTime");
            productType = Int(os, "ProductType");
            doc.Os.IsServer = productType is 2 or 3;
        });

        Section(doc, "processor", () =>
        {
            var cpus = wmi.Query(Cimv2, "SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
            if (cpus.Count == 0) return;
            doc.Hardware.CpuName = Clean(Str(cpus[0], "Name"));
            doc.Hardware.CpuCores = cpus.Sum(c => Int(c, "NumberOfCores") ?? 0);
            doc.Hardware.CpuThreads = cpus.Sum(c => Int(c, "NumberOfLogicalProcessors") ?? 0);
        });

        Section(doc, "disks", () =>
        {
            foreach (var d in wmi.Query(Cimv2, "SELECT Model, SerialNumber, Size, InterfaceType, MediaType FROM Win32_DiskDrive"))
                doc.Hardware.Disks.Add(new Disk { Model = Clean(Str(d, "Model")), Serial = Clean(Str(d, "SerialNumber")), SizeBytes = Long(d, "Size"), Interface = Str(d, "InterfaceType"), MediaType = Str(d, "MediaType") });
            foreach (var v in wmi.Query(Cimv2, "SELECT DeviceID, FileSystem, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType = 3"))
                doc.Hardware.Volumes.Add(new Volume { Name = Str(v, "DeviceID"), FileSystem = Str(v, "FileSystem"), SizeBytes = Long(v, "Size"), FreeBytes = Long(v, "FreeSpace") });
        });

        Section(doc, "bitlocker", () =>
        {
            var vols = wmi.Query(@"root\cimv2\Security\MicrosoftVolumeEncryption", "SELECT DriveLetter, ProtectionStatus FROM Win32_EncryptableVolume");
            foreach (var v in vols)
            {
                var letter = Str(v, "DriveLetter");
                var vol = doc.Hardware.Volumes.FirstOrDefault(x => string.Equals(x.Name, letter, StringComparison.OrdinalIgnoreCase));
                if (vol is not null) vol.Encrypted = Int(v, "ProtectionStatus") == 1;
            }
            var system = doc.Hardware.Volumes.FirstOrDefault(v => string.Equals(v.Name, "C:", StringComparison.OrdinalIgnoreCase));
            doc.Security.DiskEncryption = system?.Encrypted;
        }, optional: true);

        Section(doc, "tpm", () =>
        {
            var t = First(wmi, @"root\cimv2\Security\MicrosoftTpm", "SELECT IsEnabled_InitialValue, SpecVersion FROM Win32_Tpm");
            doc.Hardware.TpmPresent = t is not null;
            if (t is not null) doc.Hardware.TpmVersion = Str(t, "SpecVersion")?.Split(',')[0].Trim();
        }, optional: true);

        Section(doc, "network", () =>
        {
            foreach (var n in wmi.Query(Cimv2, "SELECT Description, MACAddress, IPAddress, DefaultIPGateway, DHCPEnabled FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE"))
            {
                var ips = Strings(n, "IPAddress");
                doc.Network.Add(new NetworkAdapter
                {
                    Name = Str(n, "Description"),
                    Mac = NormalizeMac(Str(n, "MACAddress")),
                    Ipv4 = ips.Where(i => i.Contains(".")).ToList(),
                    Ipv6 = ips.Where(i => i.Contains(":")).ToList(),
                    Gateway = Strings(n, "DefaultIPGateway").FirstOrDefault(),
                    Dhcp = Bool(n, "DHCPEnabled"),
                });
            }
        });

        if (registry is not null)
        {
            Section(doc, "software", () => doc.Software.AddRange(ReadUninstallKeys(registry)));
            Section(doc, "firewall", () =>
            {
                var domain = registry.GetDword(@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\DomainProfile", "EnableFirewall");
                var std = registry.GetDword(@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\StandardProfile", "EnableFirewall");
                var pub = registry.GetDword(@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\PublicProfile", "EnableFirewall");
                if (domain is not null || std is not null || pub is not null) doc.Security.FirewallEnabled = (domain ?? 1) == 1 && (std ?? 1) == 1 && (pub ?? 1) == 1;
                var sb = registry.GetDword(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");
                if (sb is not null) doc.Security.SecureBoot = sb == 1;
            }, optional: true);
        }

        // Full OS build (10.0.17763 + UBR) and feature update from the registry: the build alone does not say which
        // monthly cumulative update is installed, and that is what Windows vulnerability matching compares.
        if (registry is not null && doc.Os.Family == "windows")
            Section(doc, "os build", () =>
            {
                const string cv = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
                doc.Os.Ubr = registry.GetDword(cv, "UBR");
                doc.Os.DisplayVersion = registry.GetString(cv, "DisplayVersion") ?? registry.GetString(cv, "ReleaseId");
                doc.Os.InstallationType = registry.GetString(cv, "InstallationType");
            }, optional: true);

        Section(doc, "patches", () =>
        {
            foreach (var q in wmi.Query(Cimv2, "SELECT HotFixID, Description, InstalledOn FROM Win32_QuickFixEngineering"))
            {
                var id = Str(q, "HotFixID");
                if (string.IsNullOrEmpty(id) || id == "File 1") continue;
                doc.Patches.Add(new PatchItem { Id = id!, Description = Str(q, "Description"), InstalledOn = ParseLooseDate(Str(q, "InstalledOn")) });
            }
        });

        if (includeServices)
            Section(doc, "services", () =>
            {
                foreach (var s in wmi.Query(Cimv2, "SELECT Name, DisplayName, State, StartMode FROM Win32_Service"))
                    doc.Services.Add(new ServiceItem { Name = Str(s, "Name") ?? "", DisplayName = Str(s, "DisplayName"), State = Str(s, "State"), StartMode = Str(s, "StartMode") });
            });

        Section(doc, "antivirus", () =>
        {
            if (productType == 1)
            {
                foreach (var av in wmi.Query(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct"))
                {
                    var state = Long(av, "productState") ?? 0;
                    doc.Security.Antivirus.Add(new AntivirusProduct { Name = Str(av, "displayName") ?? "Antivirus", Enabled = ((state >> 12) & 0xF) == 1, UpToDate = ((state >> 4) & 0xF) == 0 });
                }
            }
            if (doc.Security.Antivirus.Count == 0)
            {
                var d = First(wmi, @"root\Microsoft\Windows\Defender", "SELECT AntivirusEnabled, AntivirusSignatureAge FROM MSFT_MpComputerStatus");
                if (d is not null)
                    doc.Security.Antivirus.Add(new AntivirusProduct { Name = "Microsoft Defender Antivirus", Enabled = Bool(d, "AntivirusEnabled"), UpToDate = (Long(d, "AntivirusSignatureAge") ?? 99) <= 7 });
            }
        }, optional: true);

        Section(doc, "local administrators", () =>
        {
            if (productType == 2) return; // domain controllers have no local groups
            var group = First(wmi, Cimv2, "SELECT Name, Domain FROM Win32_Group WHERE LocalAccount = TRUE AND SID = 'S-1-5-32-544'");
            if (group is null) return;
            var gName = Str(group, "Name")!.Replace("'", "''");
            var gDomain = Str(group, "Domain")!.Replace("'", "''");
            foreach (var m in wmi.Query(Cimv2, $"SELECT PartComponent FROM Win32_GroupUser WHERE GroupComponent = \"Win32_Group.Domain='{gDomain}',Name='{gName}'\""))
            {
                var part = Str(m, "PartComponent") ?? "";
                var dom = Between(part, "Domain=\"", "\"");
                var name = Between(part, "Name=\"", "\"");
                if (name is not null) doc.Users.LocalAdmins.Add(dom is null ? name : $"{dom}\\{name}");
            }
        }, optional: true);

        Classify(doc);
        return doc;
    }

    public static IEnumerable<SoftwareItem> ReadUninstallKeys(IRegistrySource registry)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in UninstallRoots)
        {
            var arch = root.Contains("WOW6432Node") ? "x86" : "x64";
            IReadOnlyList<string> keys;
            try { keys = registry.SubKeys(root); } catch (Exception) { continue; }
            foreach (var k in keys)
            {
                var path = root + "\\" + k;
                var name = registry.GetString(path, "DisplayName");
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (registry.GetDword(path, "SystemComponent") == 1) continue;              // hidden components
                if (!string.IsNullOrEmpty(registry.GetString(path, "ParentKeyName"))) continue; // updates of another product
                var version = registry.GetString(path, "DisplayVersion");
                if (!seen.Add(name!.Trim() + "|" + version + "|" + arch)) continue;
                yield return new SoftwareItem
                {
                    Name = name.Trim(),
                    Version = version?.Trim(),
                    Publisher = registry.GetString(path, "Publisher")?.Trim(),
                    InstallDate = ParseYyyyMmDd(registry.GetString(path, "InstallDate")),
                    Architecture = arch,
                    Source = "registry",
                };
            }
        }
    }

    /// <summary>Asset category and virtualization from the collected facts.</summary>
    public static void Classify(InventoryDocument doc)
    {
        var model = (doc.Device.Model ?? "") + " " + (doc.Device.Manufacturer ?? "");
        doc.Hardware.IsVirtual ??= IsVirtualModel(model);
        // VirtualBox, Hyper-V and others report a laptop chassis (PCSystemType 2) or pass the host battery through; a VM is a VM.
        if (doc.Hardware.IsVirtual == true) doc.Device.Chassis = "virtual";
        doc.Device.Category = doc.Os.IsServer == true ? "server" : doc.Os.Family is "windows" or "macos" ? "endpoint" : doc.Device.Category;
    }

    public static bool IsVirtualModel(string s)
    {
        s = s.ToLowerInvariant();
        return s.Contains("virtual machine") || s.Contains("vmware") || s.Contains("virtualbox") || s.Contains("kvm") || s.Contains("qemu")
               || s.Contains("hvm domu") || s.Contains("xen") || s.Contains("bochs") || s.Contains("parallels") || s.Contains("google compute engine")
               || s.Contains("amazon ec2") || s.Contains("openstack");
    }

    public static string? ChassisName(long[] types)
    {
        foreach (var t in types)
        {
            if (t is 8 or 9 or 10 or 11 or 12 or 14 or 18 or 21 or 30 or 31 or 32) return "laptop";
            if (t is 17 or 22 or 23 or 25 or 28 or 29) return "server";
            if (t is 3 or 4 or 5 or 6 or 7 or 13 or 15 or 16 or 24 or 33 or 34 or 35 or 36) return "desktop";
        }
        return types.Length == 0 ? null : "other";
    }

    // ---- value helpers (System.Management and MMI return slightly different CLR types) --------

    private static void Section(InventoryDocument doc, string name, Action body, bool optional = false)
    {
        try { body(); }
        catch (Exception ex) { if (!optional) doc.Errors.Add($"{name}: {ex.GetType().Name}: {Short(ex.Message)}"); }
    }

    private static string Short(string s) => s.Length > 200 ? s.Substring(0, 200) : s;

    private static IReadOnlyDictionary<string, object?>? First(IWmiSource wmi, string ns, string wql) => wmi.Query(ns, wql).FirstOrDefault();

    private static readonly System.Text.RegularExpressions.Regex RefDomain = new("Domain\\s*=\\s*\"([^\"]*)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex RefName = new("(?<![A-Za-z])Name\\s*=\\s*\"([^\"]*)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex RefLogonId = new("LogonId\\s*=\\s*\"?(\\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// User of the newest interactive (2), Remote Desktop (10) or cached interactive (11) logon session, as DOMAIN\user.
    /// References are strings locally ("Win32_Account.Domain=\"X\",Name=\"y\"") and CIM instances over WinRM, whose text
    /// form has the same key = "value" pairs. Windows' own sessions (DWM-*, UMFD-*) are ignored.
    /// </summary>
    internal static string? InteractiveUser(IWmiSource wmi)
    {
        var sessions = new Dictionary<string, DateTimeOffset>();
        foreach (var r in wmi.Query(Cimv2, "SELECT LogonId, LogonType, StartTime FROM Win32_LogonSession WHERE LogonType = 2 OR LogonType = 10 OR LogonType = 11"))
        {
            var id = Convert.ToString(r.TryGetValue("LogonId", out var v) ? v : null, CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(id)) sessions[id!] = Date(r, "StartTime") ?? DateTimeOffset.MinValue;
        }
        if (sessions.Count == 0) return null;
        string? best = null;
        var bestStart = DateTimeOffset.MinValue;
        foreach (var r in wmi.Query(Cimv2, "SELECT Antecedent, Dependent FROM Win32_LoggedOnUser"))
        {
            var dep = Convert.ToString(r.TryGetValue("Dependent", out var d) ? d : null, CultureInfo.InvariantCulture) ?? "";
            var ant = Convert.ToString(r.TryGetValue("Antecedent", out var a) ? a : null, CultureInfo.InvariantCulture) ?? "";
            var lid = RefLogonId.Match(dep);
            if (!lid.Success || !sessions.TryGetValue(lid.Groups[1].Value, out var start)) continue;
            var name = RefName.Match(ant);
            if (!name.Success) continue;
            var user = name.Groups[1].Value;
            var domain = RefDomain.Match(ant) is { Success: true } dm ? dm.Groups[1].Value : null;
            if (user.Length == 0 || user.StartsWith("DWM-", StringComparison.OrdinalIgnoreCase) || user.StartsWith("UMFD-", StringComparison.OrdinalIgnoreCase)
                || string.Equals(domain, "Window Manager", StringComparison.OrdinalIgnoreCase) || string.Equals(domain, "Font Driver Host", StringComparison.OrdinalIgnoreCase)) continue;
            if (best is null || start > bestStart) { best = string.IsNullOrEmpty(domain) ? user : domain + "\\" + user; bestStart = start; }
        }
        return best;
    }

    internal static string? Str(IReadOnlyDictionary<string, object?> r, string k) =>
        r.TryGetValue(k, out var v) && v is not null ? (v is string s ? (s.Length == 0 ? null : s) : Convert.ToString(v, CultureInfo.InvariantCulture)) : null;

    internal static long? Long(IReadOnlyDictionary<string, object?> r, string k)
    {
        if (!r.TryGetValue(k, out var v) || v is null) return null;
        try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch (Exception) { return null; }
    }

    internal static int? Int(IReadOnlyDictionary<string, object?> r, string k) => Long(r, k) is { } l ? (int)Math.Min(int.MaxValue, l) : null;

    internal static bool? Bool(IReadOnlyDictionary<string, object?> r, string k)
    {
        if (!r.TryGetValue(k, out var v) || v is null) return null;
        if (v is bool b) return b;
        return bool.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out var p) ? p : null;
    }

    internal static DateTimeOffset? Date(IReadOnlyDictionary<string, object?> r, string k)
    {
        if (!r.TryGetValue(k, out var v) || v is null) return null;
        if (v is DateTime dt) return new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime());
        if (v is DateTimeOffset dto) return dto.ToUniversalTime();
        return ParseCimDate(Convert.ToString(v, CultureInfo.InvariantCulture));
    }

    /// <summary>CIM datetime "yyyyMMddHHmmss.ffffff+UUU" (UUU = UTC offset in minutes).</summary>
    public static DateTimeOffset? ParseCimDate(string? s)
    {
        if (string.IsNullOrEmpty(s) || s!.Length < 14) return null;
        if (!DateTime.TryParseExact(s.Substring(0, 14), "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) return null;
        var offset = TimeSpan.Zero;
        if (s.Length >= 25 && int.TryParse(s.Substring(21, 4), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var mins)) offset = TimeSpan.FromMinutes(mins);
        try { return new DateTimeOffset(local, offset).ToUniversalTime(); } catch (ArgumentException) { return null; }
    }

    public static DateTimeOffset? ParseYyyyMmDd(string? s) =>
        s is { Length: 8 } && DateTime.TryParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? new DateTimeOffset(d.ToUniversalTime()) : null;

    public static DateTimeOffset? ParseLooseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        string[] formats = { "M/d/yyyy", "MM/dd/yyyy", "yyyyMMdd", "yyyy-MM-dd", "d/M/yyyy" };
        if (DateTime.TryParseExact(s!.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)) return new DateTimeOffset(d.ToUniversalTime());
        // Some systems store a hex FILETIME.
        if (long.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var ft) && ft > 0)
            try { return DateTimeOffset.FromFileTime(ft).ToUniversalTime(); } catch (ArgumentOutOfRangeException) { }
        return null;
    }

    internal static string[] Strings(IReadOnlyDictionary<string, object?> r, string k)
    {
        if (!r.TryGetValue(k, out var v) || v is null) return Array.Empty<string>();
        if (v is string s) return new[] { s };
        if (v is System.Collections.IEnumerable e) return e.Cast<object?>().Where(x => x is not null).Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)!).ToArray();
        return new[] { Convert.ToString(v, CultureInfo.InvariantCulture)! };
    }

    internal static long[] Numbers(IReadOnlyDictionary<string, object?> r, string k)
    {
        if (!r.TryGetValue(k, out var v) || v is null) return Array.Empty<long>();
        if (v is System.Collections.IEnumerable e && v is not string)
            return e.Cast<object?>().Where(x => x is not null).Select(x => Convert.ToInt64(x, CultureInfo.InvariantCulture)).ToArray();
        return new[] { Convert.ToInt64(v, CultureInfo.InvariantCulture) };
    }

    private static string? Between(string s, string start, string end)
    {
        var i = s.IndexOf(start, StringComparison.Ordinal);
        if (i < 0) return null;
        i += start.Length;
        var j = s.IndexOf(end, i, StringComparison.Ordinal);
        return j < 0 ? null : s.Substring(i, j - i);
    }

    /// <summary>Drops placeholder values OEMs leave in SMBIOS ("To be filled by O.E.M.", "Default string", "0", ...).</summary>
    public static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s!.Trim();
        var l = t.ToLowerInvariant();
        if (l is "to be filled by o.e.m." or "default string" or "system serial number" or "not specified" or "none" or "0" or "n/a" or "oem" or "system product name" or "system manufacturer" or "chassis serial number" or "123456789")
            return null;
        return t;
    }

    public static string? NormalizeUuid(string? s)
    {
        var c = Clean(s);
        if (c is null) return null;
        if (!Guid.TryParse(c, out var g) || g == Guid.Empty) return null;
        var t = g.ToString("D").ToUpperInvariant();
        return t is "FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF" or "03000200-0400-0500-0006-000700080009" ? null : t;
    }

    public static string? NormalizeMac(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var hex = new string(s!.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (hex.Length != 12 || hex == "000000000000") return null;
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }
}
