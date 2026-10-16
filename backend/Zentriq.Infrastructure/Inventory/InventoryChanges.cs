using System.Globalization;
using Zentriq.Inventory;

namespace Zentriq.Infrastructure.Inventory;

/// <summary>One difference between two inventories of the same device.</summary>
public sealed record InventoryChange(string Kind, string Change, string Component, string Item, string? OldValue = null, string? NewValue = null);

/// <summary>An installed application as stored in software_inventory.</summary>
public sealed record InstalledSoftware(string Name, string? Version, string? Publisher);

/// <summary>
/// Compares the previous inventory of a device with the new one and lists hardware and software changes. Values that
/// one of the two inventories did not report are never treated as a change (so a failed collection section, an older
/// agent or a different collection method does not produce false "removed" entries).
/// </summary>
public static class InventoryChanges
{
    public const int MaxPerInventory = 500;

    private static readonly string[] VirtualAdapters = ["virtual", "vpn", "hyper-v", "tap-", "tap ", "loopback", "bluetooth", "wan miniport", "teredo", "isatap", "vmware", "virtualbox", "docker", "wsl", "npcap", "fortinet", "cisco anyconnect", "globalprotect", "wireguard", "zerotier", "tailscale"];

    public static List<InventoryChange> Compare(InventoryDocument? before, IReadOnlyList<InstalledSoftware>? beforeSoftware, InventoryDocument after)
    {
        var list = new List<InventoryChange>();
        // No earlier inventory (first collection) or a different collection method: nothing to compare.
        if (before is null || !string.Equals(before.Source, after.Source, StringComparison.OrdinalIgnoreCase)) return list;

        Hardware(before, after, list);
        Software(beforeSoftware, after, list);
        Os(before, after, list);
        Patches(before, after, list);
        return list.Count > MaxPerInventory ? list.Take(MaxPerInventory).ToList() : list;
    }

    // ---- hardware ---------------------------------------------------------------------------------------

    private static void Hardware(InventoryDocument b, InventoryDocument a, List<InventoryChange> list)
    {
        void Changed(string component, string item, string? oldV, string? newV)
        {
            if (string.IsNullOrWhiteSpace(oldV) || string.IsNullOrWhiteSpace(newV)) return;
            if (string.Equals(Norm(oldV), Norm(newV), StringComparison.OrdinalIgnoreCase)) return;
            list.Add(new InventoryChange("hardware", "changed", component, item, oldV.Trim(), newV.Trim()));
        }

        var model = (string? m, string? v) => string.Join(" ", new[] { v, m }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()));
        if (!string.IsNullOrWhiteSpace(b.Device.Model) && !string.IsNullOrWhiteSpace(a.Device.Model))
            Changed("System", "Manufacturer / model", model(b.Device.Model, b.Device.Manufacturer), model(a.Device.Model, a.Device.Manufacturer));
        Changed("System", "Serial number", b.Device.SerialNumber, a.Device.SerialNumber);
        Changed("Processor", "Processor", b.Hardware.CpuName, a.Hardware.CpuName);
        if (b.Hardware.CpuCores is { } oc && a.Hardware.CpuCores is { } nc && oc != nc)
            list.Add(new InventoryChange("hardware", "changed", "Processor", "Cores", Num(oc), Num(nc)));
        if (b.Hardware.MemoryBytes is { } om && a.Hardware.MemoryBytes is { } nm && Gb(om) != Gb(nm))
            list.Add(new InventoryChange("hardware", Gb(nm) > Gb(om) ? "added" : "removed", "Memory", "Installed memory", $"{Gb(om)} GB", $"{Gb(nm)} GB"));
        Changed("BIOS / firmware", "BIOS version", b.Hardware.BiosVersion, a.Hardware.BiosVersion);
        if (b.Hardware.TpmPresent is { } ot && a.Hardware.TpmPresent is { } nt && ot != nt)
            list.Add(new InventoryChange("hardware", nt ? "added" : "removed", "TPM", "Trusted Platform Module", ot ? "present" : "none", nt ? "present" : "none"));

        // Disks: identified by serial number, else model + size.
        if (b.Hardware.Disks.Count > 0 && a.Hardware.Disks.Count > 0)
        {
            static string Key(Disk d) => !string.IsNullOrWhiteSpace(d.Serial) ? "sn:" + d.Serial!.Trim().ToUpperInvariant() : $"m:{Norm(d.Model)}|{d.SizeBytes}";
            static string Describe(Disk d) => $"{d.Model?.Trim() ?? "Disk"}{(d.SizeBytes is { } s ? $" ({Size(s)})" : "")}{(string.IsNullOrWhiteSpace(d.Serial) ? "" : $", S/N {d.Serial!.Trim()}")}";
            var old = b.Hardware.Disks.GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
            var cur = a.Hardware.Disks.GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
            foreach (var (k, d) in cur) if (!old.ContainsKey(k)) list.Add(new InventoryChange("hardware", "added", "Disk", Describe(d), null, d.Interface ?? d.MediaType));
            foreach (var (k, d) in old) if (!cur.ContainsKey(k)) list.Add(new InventoryChange("hardware", "removed", "Disk", Describe(d), d.Interface ?? d.MediaType, null));
        }

        // Physical network adapters, by MAC address.
        var oldNics = PhysicalNics(b);
        var newNics = PhysicalNics(a);
        if (oldNics.Count > 0 && newNics.Count > 0)
        {
            foreach (var (mac, n) in newNics) if (!oldNics.ContainsKey(mac)) list.Add(new InventoryChange("hardware", "added", "Network adapter", $"{n.Name ?? "Adapter"} ({mac})"));
            foreach (var (mac, n) in oldNics) if (!newNics.ContainsKey(mac)) list.Add(new InventoryChange("hardware", "removed", "Network adapter", $"{n.Name ?? "Adapter"} ({mac})"));
        }
    }

    private static Dictionary<string, NetworkAdapter> PhysicalNics(InventoryDocument d) =>
        d.Network.Where(n => !string.IsNullOrWhiteSpace(n.Mac) && !VirtualAdapters.Any(v => (n.Name ?? "").Contains(v, StringComparison.OrdinalIgnoreCase)))
                 .GroupBy(n => n.Mac!.Trim().ToUpperInvariant()).ToDictionary(g => g.Key, g => g.First());

    // ---- software ---------------------------------------------------------------------------------------

    private static void Software(IReadOnlyList<InstalledSoftware>? before, InventoryDocument a, List<InventoryChange> list)
    {
        var after = a.Software.Where(s => !string.IsNullOrWhiteSpace(s.Name)).Select(s => new InstalledSoftware(s.Name.Trim(), s.Version?.Trim(), s.Publisher?.Trim())).ToList();
        if (before is null || before.Count == 0 || after.Count == 0) return;
        var old = Group(before);
        var cur = Group(after);
        // A collection that suddenly lost most applications is treated as incomplete, not as mass uninstallation.
        var lost = old.Keys.Count(k => !cur.ContainsKey(k));
        if (old.Count >= 20 && lost > old.Count / 2) return;

        foreach (var (k, n) in cur)
        {
            if (!old.TryGetValue(k, out var o))
            {
                list.Add(new InventoryChange("software", "installed", "Application", n.Name, null, Versions(n.Versions)));
                continue;
            }
            if (!o.Versions.SetEquals(n.Versions))
                list.Add(new InventoryChange("software", "updated", "Application", n.Name, Versions(o.Versions), Versions(n.Versions)));
        }
        foreach (var (k, o) in old)
            if (!cur.ContainsKey(k)) list.Add(new InventoryChange("software", "uninstalled", "Application", o.Name, Versions(o.Versions), null));
    }

    private sealed record Title(string Name, HashSet<string> Versions);

    private static Dictionary<string, Title> Group(IEnumerable<InstalledSoftware> items)
    {
        var d = new Dictionary<string, Title>(StringComparer.Ordinal);
        foreach (var s in items)
        {
            var key = Norm(s.Name);
            if (!d.TryGetValue(key, out var t)) d[key] = t = new Title(s.Name, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(s.Version)) t.Versions.Add(s.Version!);
        }
        return d;
    }

    private static string? Versions(HashSet<string> v) => v.Count == 0 ? null : string.Join(", ", v.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

    private static void Os(InventoryDocument b, InventoryDocument a, List<InventoryChange> list)
    {
        static string? Describe(OsInfo o) => string.IsNullOrWhiteSpace(o.Name) ? null
            : $"{o.Name!.Trim()}{(o.DisplayVersion is { Length: > 0 } dv ? $" {dv}" : "")}{(o.Version is { Length: > 0 } v ? $" ({v}{(o.Ubr is { } u ? $".{u}" : "")})" : "")}";
        var o = Describe(b.Os);
        var n = Describe(a.Os);
        if (o is null || n is null) return;
        // Monthly cumulative updates only change the UBR; those are reported as Windows updates, not as an OS change.
        var sameRelease = string.Equals(b.Os.Name?.Trim(), a.Os.Name?.Trim(), StringComparison.OrdinalIgnoreCase) && b.Os.Version == a.Os.Version;
        if (sameRelease && (b.Os.DisplayVersion ?? "") == (a.Os.DisplayVersion ?? "")) return;
        list.Add(new InventoryChange("software", "updated", "Operating system", a.Os.Name!.Trim(), o, n));
    }

    private static void Patches(InventoryDocument b, InventoryDocument a, List<InventoryChange> list)
    {
        if (b.Patches.Count == 0 || a.Patches.Count == 0) return;
        var old = b.Patches.Select(p => p.Id.Trim().ToUpperInvariant()).Where(x => x.Length > 0).ToHashSet();
        foreach (var p in a.Patches.Where(p => !string.IsNullOrWhiteSpace(p.Id)).GroupBy(p => p.Id.Trim().ToUpperInvariant()).Select(g => g.First()))
            if (!old.Contains(p.Id.Trim().ToUpperInvariant()))
                list.Add(new InventoryChange("software", "installed", "Windows update", p.Id.Trim(), null, p.Description));
    }

    private static string Norm(string? s) => string.Join(' ', (s ?? "").Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static int Gb(long bytes) => (int)Math.Round(bytes / 1073741824.0);
    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);
    private static string Size(long bytes) => bytes >= 1_000_000_000_000 ? $"{bytes / 1e12:0.#} TB" : $"{Math.Round(bytes / 1e9):0} GB";
}
