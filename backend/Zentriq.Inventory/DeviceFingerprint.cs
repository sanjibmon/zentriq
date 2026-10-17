using System;
using System.Collections.Generic;
using System.Linq;

namespace Zentriq.Inventory;

/// <summary>What a device is, recognised from what it exposes without credentials.</summary>
public sealed class Fingerprint
{
    public string? DeviceType { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    /// <summary>Why: "web page title 'HP LaserJet M507'", "port 9100 (printing)".</summary>
    public List<string> Evidence { get; set; } = new();
    public bool IsEmpty => DeviceType is null && Manufacturer is null;
}

/// <summary>
/// Recognises printers, scanners, switches, routers, firewalls, access points, cameras, phones, UPS and storage devices
/// from their open ports, the title / server header / certificate of their web interface and their SNMP description.
/// Stronger evidence wins: SNMP over web interface over ports.
/// </summary>
public static class DeviceFingerprint
{
    private sealed class Rule
    {
        public Rule(string type, string? maker, params string[] words) { Type = type; Maker = maker; Words = words; }
        public string Type { get; }
        public string? Maker { get; }
        public string[] Words { get; }
    }

    // Order matters: more specific first (a multifunction printer before a printer, a firewall before a router).
    private static readonly Rule[] Rules =
    {
        // Scanners and multifunction printers
        new Rule("scanner", "Fujitsu / Ricoh", "scansnap", "fi-7", "fi-8", "scan station", "network scanner"),
        new Rule("mfp", "Canon", "imagerunner", "imageclass mf", "i-sensys mf"),
        new Rule("mfp", "Xerox", "workcentre", "altalink", "versalink c4", "versalink b4"),
        new Rule("mfp", "Konica Minolta", "bizhub"),
        new Rule("mfp", "Kyocera", "taskalfa", "ecosys m"),
        new Rule("mfp", "Ricoh", "ricoh mp", "ricoh im"),
        new Rule("mfp", "HP", "laserjet mfp", "officejet pro", "pagewide mfp", "color laserjet flow", "laserjet pro mfp"),
        new Rule("mfp", null, " mfp", "multifunction", "multi-function"),
        // Printers
        new Rule("printer", "HP", "laserjet", "officejet", "deskjet", "pagewide", "designjet", "hp color", "jetdirect"),
        new Rule("printer", "Epson", "epson"),
        new Rule("printer", "Brother", "brother"),
        new Rule("printer", "Canon", "canon"),
        new Rule("printer", "Kyocera", "kyocera", "ecosys"),
        new Rule("printer", "Xerox", "xerox", "phaser"),
        new Rule("printer", "Lexmark", "lexmark"),
        new Rule("printer", "Ricoh", "ricoh"),
        new Rule("printer", "Sharp", "sharp mx"),
        new Rule("printer", "OKI", "oki data", "okidata"),
        new Rule("printer", "Zebra", "zebra", "zt410", "zt230"),
        new Rule("printer", null, "printer", "print server", "airprint"),
        // Firewalls (before routers and switches)
        new Rule("firewall", "Fortinet", "fortigate", "fortios", "fortinet"),
        new Rule("firewall", "Palo Alto Networks", "palo alto", "pan-os", "globalprotect"),
        new Rule("firewall", "SonicWall", "sonicwall", "sonicos"),
        new Rule("firewall", "Sophos", "sophos", "xg firewall", "utm 9"),
        new Rule("firewall", "Netgate", "pfsense"),
        new Rule("firewall", "Deciso", "opnsense"),
        new Rule("firewall", "WatchGuard", "watchguard", "fireware"),
        new Rule("firewall", "Check Point", "check point", "gaia portal"),
        new Rule("firewall", "Cisco", "adaptive security appliance", "cisco asa", "firepower", "meraki mx"),
        new Rule("firewall", "Barracuda", "barracuda"),
        new Rule("firewall", "Zyxel", "zywall", "usg flex"),
        new Rule("firewall", null, "firewall"),
        // Load balancers
        new Rule("load_balancer", "F5", "big-ip", "f5 networks"),
        new Rule("load_balancer", "Citrix", "netscaler", "citrix adc"),
        new Rule("load_balancer", "Kemp", "kemp", "loadmaster"),
        // Wireless
        new Rule("wireless_controller", null, "wireless lan controller", "wireless controller", "virtual controller", "aireos"),
        new Rule("access_point", "Ubiquiti", "unifi ap", "uap-", "u6-", "unifi access point"),
        new Rule("access_point", "TP-Link", "omada eap", "eap2", "eap6"),
        new Rule("access_point", "Aruba", "aruba instant", "aruba ap", "instant on"),
        new Rule("access_point", "Ruckus", "ruckus", "unleashed"),
        new Rule("access_point", "Cisco Meraki", "meraki mr"),
        new Rule("access_point", "Cambium", "cambium", "cnpilot"),
        new Rule("access_point", null, "access point", "wireless ap"),
        // Routers
        new Rule("router", "MikroTik", "routeros", "mikrotik"),
        new Rule("router", "Ubiquiti", "edgerouter", "edgeos", "unifi security gateway", "unifi dream machine"),
        new Rule("router", "DrayTek", "draytek", "vigor"),
        new Rule("router", null, "openwrt", "luci", "dd-wrt", "router"),
        // Switches
        new Rule("switch", "HPE Aruba", "procurve", "aruba os-cx", "arubaos-switch", "aruba 2930", "aruba 2540"),
        new Rule("switch", "H3C / HPE", "comware"),
        new Rule("switch", "Cisco", "catalyst", "nexus", "cisco sg", "cbs350", "sg350", "sg300"),
        new Rule("switch", "Netgear", "prosafe", "netgear gs", "netgear m4"),
        new Rule("switch", "D-Link", "dgs-", "des-"),
        new Rule("switch", "Ubiquiti", "unifi switch", "edgeswitch", "usw-"),
        new Rule("switch", "Dell", "powerconnect", "dell emc networking"),
        new Rule("switch", "Juniper Networks", "junos ex", "ex2300", "ex3400", "ex4300"),
        new Rule("switch", null, "switch"),
        // Power, storage, cameras, phones, hypervisors
        new Rule("ups", "APC (Schneider Electric)", "network management card", "powerchute", "apc web", "smart-ups"),
        new Rule("ups", "Eaton", "eaton", "network-m2", "gigabit network card"),
        new Rule("ups", "CyberPower", "cyberpower", "rmcard"),
        new Rule("ups", null, " ups", "uninterruptible"),
        new Rule("storage", "Synology", "synology", "diskstation", "rackstation"),
        new Rule("storage", "QNAP", "qnap", "qts"),
        new Rule("storage", "iXsystems", "truenas", "freenas"),
        new Rule("storage", "NetApp", "netapp", "ontap"),
        new Rule("storage", "Netgear", "readynas"),
        new Rule("storage", null, "asustor", "terramaster", "network attached storage", " nas"),
        new Rule("ip_camera", "Hikvision", "hikvision"),
        new Rule("ip_camera", "Dahua", "dahua"),
        new Rule("ip_camera", "Axis", "axis communications", "axis network camera"),
        new Rule("ip_camera", null, "network camera", "ip camera", "nvr", "dvr", "reolink", "amcrest", "vivotek", "hanwha", "uniview", "camera"),
        new Rule("ip_phone", "Polycom", "polycom", "poly vvx"),
        new Rule("ip_phone", "Yealink", "yealink"),
        new Rule("ip_phone", "Grandstream", "grandstream"),
        new Rule("ip_phone", "Cisco", "cisco ip phone"),
        new Rule("ip_phone", null, "snom", "fanvil", "avaya", "mitel", "ip phone"),
        new Rule("hypervisor", "VMware", "vmware esxi", "esxi", "vsphere"),
        new Rule("hypervisor", "Proxmox", "proxmox"),
        new Rule("hypervisor", null, "xenserver", "xcp-ng", "nutanix", "hyper-v server"),
    };

    /// <summary>Recognises a device from text it shows (web page title, server header, certificate, SNMP description).</summary>
    public static Fingerprint? FromText(string? text, string source)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = " " + text!.ToLowerInvariant() + " ";
        foreach (var r in Rules)
            foreach (var w in r.Words)
                if (t.Contains(w))
                    return new Fingerprint { DeviceType = r.Type, Manufacturer = r.Maker, Evidence = { $"{source} mentions \"{w.Trim()}\"" } };
        return null;
    }

    /// <summary>Weak evidence from open TCP ports (used only when nothing better is known).</summary>
    public static Fingerprint? FromPorts(IReadOnlyCollection<int> open)
    {
        bool Has(int p) => open.Contains(p);
        Fingerprint F(string type, string why, string? maker = null) => new() { DeviceType = type, Manufacturer = maker, Evidence = { why } };
        if (Has(9100) || Has(515) || Has(631)) return F("printer", "printing port open (" + string.Join(", ", new[] { 9100, 515, 631 }.Where(Has)) + ")");
        if (Has(8291)) return F("router", "MikroTik Winbox port 8291 open", "MikroTik");
        if (Has(554) && !Has(445)) return F("ip_camera", "video streaming port 554 (RTSP) open");
        if ((Has(5060) || Has(5061)) && !Has(445)) return F("ip_phone", "SIP port open");
        if (Has(902) && Has(443) && !Has(445)) return F("hypervisor", "VMware host port 902 open", "VMware");
        if (Has(3260) && !Has(445)) return F("storage", "iSCSI port 3260 open");
        if (Has(23) && !Has(445) && !Has(22) && !Has(135)) return F("network", "only Telnet / web management ports open");
        return null;
    }

    /// <summary>
    /// SNMP evidence: Printer-MIB present, sysServices layers, IP forwarding, a bridge (switching) table, the vendor.
    /// </summary>
    public static Fingerprint FromSnmp(string? sysDescr, string? vendor, int? sysServices, bool? ipForwarding, bool hasBridge, bool hasPrinterMib, bool isHostOs)
    {
        var text = FromText(sysDescr, "SNMP description");
        var fp = new Fingerprint { Manufacturer = vendor };
        if (hasPrinterMib)
        {
            fp.DeviceType = text?.DeviceType is "mfp" or "scanner" ? text.DeviceType : "printer";
            fp.Evidence.Add("Printer-MIB answered");
        }
        else if (text?.DeviceType is { } t && t != "switch" && t != "router")
        {
            fp.DeviceType = t;
            fp.Evidence.AddRange(text.Evidence);
        }
        else if (isHostOs) { fp.DeviceType = null; }
        else if (vendor is not null && FirewallVendors.Contains(vendor))
        {
            fp.DeviceType = "firewall";
            fp.Evidence.Add($"firewall vendor ({vendor})");
        }
        else
        {
            var layer2 = sysServices is { } s2 && (s2 & 2) != 0;
            var layer3 = sysServices is { } s3 && (s3 & 4) != 0;
            if (ipForwarding == true && (layer3 || !hasBridge)) { fp.DeviceType = layer2 && hasBridge ? "switch" : "router"; fp.Evidence.Add(layer2 && hasBridge ? "switching table and IP routing (layer 3 switch)" : "routes IP (ipForwarding)"); }
            else if (hasBridge || layer2) { fp.DeviceType = "switch"; fp.Evidence.Add("switching (bridge) table"); }
            else if (text?.DeviceType is { } tt) { fp.DeviceType = tt; fp.Evidence.AddRange(text.Evidence); }
            else { fp.DeviceType = "network"; fp.Evidence.Add("answers SNMP as a network device"); }
        }
        if (fp.Manufacturer is null || fp.Manufacturer.StartsWith("Enterprise ", StringComparison.Ordinal)) fp.Manufacturer = text?.Manufacturer ?? fp.Manufacturer;
        return fp;
    }

    private static readonly HashSet<string> FirewallVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        "Fortinet", "Palo Alto Networks", "Check Point", "SonicWall", "Sophos", "pfSense/FreeBSD", "WatchGuard", "Barracuda",
    };

    /// <summary>Combines evidence: the first non-empty type in order of strength; manufacturer and model filled from any.</summary>
    public static Fingerprint Combine(params Fingerprint?[] byStrength)
    {
        var r = new Fingerprint();
        foreach (var f in byStrength)
        {
            if (f is null) continue;
            if (r.DeviceType is null && f.DeviceType is not null) { r.DeviceType = f.DeviceType; r.Evidence.AddRange(f.Evidence); }
            r.Manufacturer ??= f.Manufacturer;
            r.Model ??= f.Model;
        }
        return r;
    }

    /// <summary>A model name from a web page title such as "HP LaserJet Pro M404dn" or "FortiGate-60F".</summary>
    public static string? ModelFromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var t = title!.Trim();
        foreach (var sep in new[] { " - ", " | ", " :: " }) { var i = t.IndexOf(sep, StringComparison.Ordinal); if (i > 0) t = t.Substring(0, i); }
        var lower = t.ToLowerInvariant();
        if (lower is "login" or "home" or "index" or "web interface" or "untitled" or "welcome" || t.Length < 4 || t.Length > 80) return null;
        if (!t.Any(char.IsDigit)) return null; // "Sign in" or a brand alone is not a model
        return t;
    }
}
