using System.Collections.Generic;
using System.Linq;

namespace Zentriq.Inventory;

/// <summary>
/// Device types Zentriq knows (detected automatically, or chosen by an administrator). The type is what people see
/// ("Firewall", "Multifunction printer"); the category groups types for licensing and dashboards.
/// </summary>
public static class DeviceTypes
{
    public sealed class Info
    {
        public Info(string key, string label, string? category, string group) { Key = key; Label = label; Category = category; Group = group; }
        public string Key { get; }
        public string Label { get; }
        /// <summary>Asset category; null = keep the category from the operating system (a virtual machine can be a server or a desktop).</summary>
        public string? Category { get; }
        public string Group { get; }
    }

    public static readonly IReadOnlyList<Info> All = new[]
    {
        new Info("desktop", "Desktop", "endpoint", "Computers"),
        new Info("laptop", "Laptop", "endpoint", "Computers"),
        new Info("tablet", "Tablet", "endpoint", "Computers"),
        new Info("mobile", "Mobile device", "endpoint", "Computers"),
        new Info("server", "Physical server", "server", "Computers"),
        new Info("virtual", "Virtual machine", null, "Computers"),
        new Info("hypervisor", "Hypervisor host", "virtual_host", "Computers"),
        new Info("storage", "Storage / NAS", "server", "Computers"),
        new Info("switch", "Network switch", "network_device", "Network"),
        new Info("router", "Router", "network_device", "Network"),
        new Info("firewall", "Firewall", "network_device", "Network"),
        new Info("access_point", "Wireless access point", "network_device", "Network"),
        new Info("wireless_controller", "Wireless controller", "network_device", "Network"),
        new Info("load_balancer", "Load balancer", "network_device", "Network"),
        new Info("network", "Network device", "network_device", "Network"),
        new Info("printer", "Printer", "printer", "Printing"),
        new Info("mfp", "Multifunction printer", "printer", "Printing"),
        new Info("scanner", "Scanner", "printer", "Printing"),
        new Info("ups", "UPS / power", "iot", "Other devices"),
        new Info("ip_camera", "IP camera / NVR", "iot", "Other devices"),
        new Info("ip_phone", "IP phone", "iot", "Other devices"),
        new Info("iot", "IoT device", "iot", "Other devices"),
        new Info("other", "Other", "other", "Other devices"),
    };

    private static readonly Dictionary<string, Info> ByKey = All.ToDictionary(i => i.Key);

    public static bool IsKnown(string? key) => key is not null && ByKey.ContainsKey(key);
    public static Info? Get(string? key) => key is not null && ByKey.TryGetValue(key, out var i) ? i : null;

    /// <summary>Category for a device type, or null when the type does not decide it.</summary>
    public static string? CategoryFor(string? key) => Get(key)?.Category;
}
