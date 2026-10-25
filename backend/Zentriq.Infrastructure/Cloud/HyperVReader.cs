using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Zentriq.Infrastructure.Cloud;

/// <summary>
/// Microsoft Hyper-V, read-only, from one host's WMI (root\virtualization\v2 and root\cimv2): the host itself, and each
/// virtual machine with its state and since when, vCPU, startup memory, network adapters (MAC), virtual disks,
/// checkpoints, BIOS GUID, and — with the integration services running — guest host name, IP addresses and operating
/// system. The WMI queries are passed in, so the mapping is tested without a Hyper-V host.
/// </summary>
public static class HyperVReader
{
    public const string Ns = @"root\virtualization\v2";
    public delegate IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string wmiNamespace, string wql);

    public static void Read(string host, Query query, CloudSnapshot snap, DateTimeOffset now)
    {
        var cs = query(@"root\cimv2", "SELECT Name, Domain, Manufacturer, Model, TotalPhysicalMemory, NumberOfLogicalProcessors FROM Win32_ComputerSystem").FirstOrDefault();
        var os = query(@"root\cimv2", "SELECT Caption, Version, LastBootUpTime FROM Win32_OperatingSystem").FirstOrDefault();
        var product = query(@"root\cimv2", "SELECT UUID, IdentifyingNumber FROM Win32_ComputerSystemProduct").FirstOrDefault();
        var hostName = S(cs, "Name") ?? host;
        var domain = S(cs, "Domain");
        var hostItem = new CloudItem
        {
            Kind = "host", ExternalId = "host:" + hostName.ToLowerInvariant(), Name = hostName, Hostname = hostName,
            Os = S(os, "Caption")?.Replace("Microsoft ", "").Trim(), State = "running",
            Cpu = I(cs, "NumberOfLogicalProcessors"), MemoryMb = L(cs, "TotalPhysicalMemory") is { } tm ? tm / 1048576 : null,
            BiosUuid = S(product, "UUID")?.ToUpperInvariant(), Group = domain,
        };
        hostItem.Properties["osVersion"] = S(os, "Version");
        hostItem.Properties["model"] = $"{S(cs, "Manufacturer")} {S(cs, "Model")}".Trim();
        hostItem.Properties["serial"] = S(product, "IdentifyingNumber");
        hostItem.Properties["osEndOfSupport"] = CloudRules.OsOutOfSupport(hostItem.Os);
        hostItem.Properties["address"] = host;

        var vms = query(Ns, "SELECT Name, ElementName, EnabledState, TimeOfLastStateChange, OnTimeInMilliseconds, InstallDate FROM Msvm_ComputerSystem WHERE Caption = 'Virtual Machine'");
        var settings = query(Ns, "SELECT VirtualSystemIdentifier, VirtualSystemType, BIOSGUID, CreationTime, Version, ElementName, Notes, VirtualSystemSubType FROM Msvm_VirtualSystemSettingData");
        var memory = query(Ns, "SELECT InstanceID, VirtualQuantity, DynamicMemoryEnabled, Limit FROM Msvm_MemorySettingData");
        var cpu = query(Ns, "SELECT InstanceID, VirtualQuantity FROM Msvm_ProcessorSettingData");
        var nics = query(Ns, "SELECT InstanceID, Address, StaticMacAddress FROM Msvm_SyntheticEthernetPortSettingData")
            .Concat(Safe(() => query(Ns, "SELECT InstanceID, Address FROM Msvm_EmulatedEthernetPortSettingData"))).ToList();
        var disks = query(Ns, "SELECT InstanceID, HostResource FROM Msvm_StorageAllocationSettingData WHERE ResourceType = 31");
        var kvp = Safe(() => query(Ns, "SELECT SystemName, GuestIntrinsicExchangeItems FROM Msvm_KvpExchangeComponent"));

        static string? Vm(IReadOnlyDictionary<string, object?> row)
        {
            var m = Regex.Match(S(row, "InstanceID") ?? "", @"^Microsoft:([0-9A-Fa-f\-]{36})\\", RegexOptions.CultureInvariant);
            return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
        }
        ILookup<string?, IReadOnlyDictionary<string, object?>> By(IEnumerable<IReadOnlyDictionary<string, object?>> rows) => rows.ToLookup(Vm);
        var mem = By(memory);
        var cpus = By(cpu);
        var adapters = By(nics);
        var vhds = By(disks);

        long running = 0;
        foreach (var v in vms)
        {
            var id = (S(v, "Name") ?? "").ToUpperInvariant();
            if (id.Length == 0) continue;
            var realized = settings.FirstOrDefault(x => string.Equals(S(x, "VirtualSystemIdentifier"), id, StringComparison.OrdinalIgnoreCase)
                                                        && (S(x, "VirtualSystemType") ?? "").EndsWith(":Realized", StringComparison.Ordinal) && !(S(x, "VirtualSystemType") ?? "").Contains("Snapshot"));
            var snaps = settings.Where(x => string.Equals(S(x, "VirtualSystemIdentifier"), id, StringComparison.OrdinalIgnoreCase)
                                            && (S(x, "VirtualSystemType") ?? "").Contains("Snapshot")).ToList();
            var state = I(v, "EnabledState") switch { 2 => "running", 3 => "off", 6 or 32769 => "saved", 9 or 32768 => "paused", 10 => "starting", 4 => "stopping", var x => x?.ToString() };
            var item = new CloudItem
            {
                Kind = "vm", ExternalId = id.ToLowerInvariant(), Name = S(v, "ElementName"), State = state, ParentId = hostItem.ExternalId, Group = hostName,
                StateSince = D(v, "TimeOfLastStateChange"), CreatedAt = D(realized, "CreationTime") ?? D(v, "InstallDate"),
                BiosUuid = S(realized, "BIOSGUID")?.Trim('{', '}').ToUpperInvariant(),
                Cpu = cpus[id].Select(r => I(r, "VirtualQuantity")).FirstOrDefault(x => x is not null),
                MemoryMb = mem[id].Select(r => L(r, "VirtualQuantity")).FirstOrDefault(x => x is not null),
            };
            item.Properties["generation"] = (S(realized, "VirtualSystemSubType") ?? "").EndsWith(":2", StringComparison.Ordinal) ? 2 : 1;
            item.Properties["configurationVersion"] = S(realized, "Version");
            item.Properties["dynamicMemory"] = mem[id].Select(r => r.TryGetValue("DynamicMemoryEnabled", out var d) ? d as bool? : null).FirstOrDefault();
            if (L(v, "OnTimeInMilliseconds") is { } on && on > 0) item.Properties["uptimeHours"] = on / 3_600_000;
            foreach (var n in adapters[id])
                if (S(n, "Address") is { Length: 12 } mac && mac != "000000000000") item.Macs.Add(AzureCollector.NormalizeMac(mac));
            var paths = vhds[id].SelectMany(d => d.TryGetValue("HostResource", out var hr) && hr is string[] a ? a : []).ToList();
            item.Properties["disks"] = paths;
            if (snaps.Count > 0)
            {
                item.Properties["snapshotCount"] = snaps.Count;
                if (snaps.Select(x => D(x, "CreationTime")).Where(x => x is not null).Min() is { } oldest) item.Properties["snapshotOldestDays"] = (long)(now - oldest).TotalDays;
            }
            var guest = Kvp(kvp.FirstOrDefault(k => string.Equals(S(k, "SystemName"), id, StringComparison.OrdinalIgnoreCase)));
            if (guest.TryGetValue("FullyQualifiedDomainName", out var fqdn) && fqdn.Length > 0) { item.Hostname = fqdn.Split('.')[0]; item.Properties["fqdn"] = fqdn; }
            if (guest.TryGetValue("OSName", out var osName) && osName.Length > 0) item.Os = osName.Replace("Microsoft ", "").Trim();
            if (guest.TryGetValue("NetworkAddressIPv4", out var ips) && ips.Length > 0) item.PrivateIp = ips.Split(';', StringSplitOptions.RemoveEmptyEntries)[0];
            if (state == "running" && item.MemoryMb is { } mb) running += mb;
            snap.Items.Add(item);
        }
        hostItem.Properties["vms"] = vms.Count;
        hostItem.Properties["runningMemoryMb"] = running;
        if (hostItem.MemoryMb is > 0) hostItem.Properties["memoryCommitPercent"] = running * 100 / hostItem.MemoryMb.Value;
        snap.Items.Add(hostItem);
    }

    /// <summary>Name → value from Msvm_KvpExchangeComponent.GuestIntrinsicExchangeItems (each item an embedded-instance XML).</summary>
    internal static Dictionary<string, string> Kvp(IReadOnlyDictionary<string, object?>? row)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (row is null || !row.TryGetValue("GuestIntrinsicExchangeItems", out var v) || v is not string[] items) return d;
        foreach (var xml in items)
        {
            try
            {
                var x = XElement.Parse(xml);
                string? P(string name) => x.Elements("PROPERTY").FirstOrDefault(p => (string?)p.Attribute("NAME") == name)?.Element("VALUE")?.Value;
                if (P("Name") is { } n) d[n] = P("Data") ?? "";
            }
            catch (System.Xml.XmlException) { /* skip a malformed item */ }
        }
        return d;
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Safe(Func<IReadOnlyList<IReadOnlyDictionary<string, object?>>> f)
    {
        try { return f(); }
        catch (Exception) { return []; }
    }

    private static string? S(IReadOnlyDictionary<string, object?>? r, string k) => r is not null && r.TryGetValue(k, out var v) && v is not null ? v.ToString() : null;
    private static int? I(IReadOnlyDictionary<string, object?>? r, string k) => r is not null && r.TryGetValue(k, out var v) && v is not null && int.TryParse(v.ToString(), out var i) ? i : null;
    private static long? L(IReadOnlyDictionary<string, object?>? r, string k) => r is not null && r.TryGetValue(k, out var v) && v is not null && long.TryParse(v.ToString(), out var i) ? i : null;
    private static DateTimeOffset? D(IReadOnlyDictionary<string, object?>? r, string k)
    {
        if (r is null || !r.TryGetValue(k, out var v) || v is null) return null;
        DateTimeOffset? d = v switch
        {
            DateTimeOffset o => o,
            DateTime t => new DateTimeOffset(t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Local) : t),
            string s when DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var p) => p,
            _ => null,
        };
        // WMI reports "never" as 1601 or 0001 dates.
        return d is { Year: > 1970 } ? d : null;
    }
}
