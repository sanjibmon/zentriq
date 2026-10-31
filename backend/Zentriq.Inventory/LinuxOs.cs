using System;
using System.Collections.Generic;
using System.Linq;

namespace Zentriq.Inventory;

/// <summary>What is known about a Linux system for telling distribution, edition and server / desktop apart.</summary>
public sealed class LinuxOsFacts
{
    /// <summary>/etc/os-release as key → unquoted value.</summary>
    public IReadOnlyDictionary<string, string> OsRelease { get; set; } = new Dictionary<string, string>();
    /// <summary>Installed package names (any package manager), lower case.</summary>
    public ISet<string> Packages { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>systemctl get-default (graphical.target / multi-user.target), or null.</summary>
    public string? DefaultTarget { get; set; }
    /// <summary>Unit the display-manager.service alias points to (gdm, sddm, lightdm, …), or null.</summary>
    public string? DisplayManager { get; set; }
    /// <summary>Session files in /usr/share/xsessions and /usr/share/wayland-sessions (without .desktop).</summary>
    public IReadOnlyList<string> Sessions { get; set; } = Array.Empty<string>();
    /// <summary>First line of /etc/redhat-release (RHEL 7 names its variant there).</summary>
    public string? RedhatRelease { get; set; }
    /// <summary>desktop | laptop | server | virtual | …</summary>
    public string? Chassis { get; set; }
    /// <summary>A graphical (x11 / wayland) session is open now.</summary>
    public bool GraphicalSession { get; set; }
}

/// <summary>The result: distribution, its family, the edition and whether this is a server or a desktop installation.</summary>
public sealed class LinuxIdentity
{
    public string? Distribution { get; set; }
    public string Family { get; set; } = "other";
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? Codename { get; set; }
    /// <summary>Server | Server with GUI | Desktop</summary>
    public string InstallationType { get; set; } = "Server";
    public bool IsServer { get; set; } = true;
    /// <summary>"Ubuntu Server", "Ubuntu Desktop", "Fedora Workstation", "SUSE Linux Enterprise Server", …</summary>
    public string? Edition { get; set; }
    public string? DesktopEnvironment { get; set; }
    public bool? Lts { get; set; }
    /// <summary>How the role was decided (shown in diagnostics).</summary>
    public string Reason { get; set; } = "";
}

/// <summary>
/// Tells Linux distributions, editions and server from desktop installations apart. Decided in this order:
/// the edition the distribution states (SLES / SLED, Fedora and RHEL 7 variants, Ubuntu Core), server meta-packages
/// (ubuntu-server, fedora-release-server), then whether a desktop is installed and in use (display manager, graphical
/// boot target, desktop sessions, desktop packages) together with the chassis. A system without a desktop is a server.
/// </summary>
public static class LinuxOs
{
    private static readonly Dictionary<string, string> Families = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["debian"] = "debian", ["ubuntu"] = "debian", ["linuxmint"] = "debian", ["pop"] = "debian", ["elementary"] = "debian", ["kali"] = "debian",
        ["raspbian"] = "debian", ["zorin"] = "debian", ["neon"] = "debian", ["devuan"] = "debian", ["mx"] = "debian", ["parrot"] = "debian", ["deepin"] = "debian",
        ["rhel"] = "rhel", ["centos"] = "rhel", ["rocky"] = "rhel", ["almalinux"] = "rhel", ["ol"] = "rhel", ["amzn"] = "rhel", ["scientific"] = "rhel",
        ["cloudlinux"] = "rhel", ["eurolinux"] = "rhel", ["virtuozzo"] = "rhel", ["circle"] = "rhel", ["navy"] = "rhel", ["miraclelinux"] = "rhel",
        ["fedora"] = "fedora", ["nobara"] = "fedora", ["ultramarine"] = "fedora",
        ["sles"] = "suse", ["sled"] = "suse", ["sles_sap"] = "suse", ["opensuse"] = "suse", ["opensuse-leap"] = "suse", ["opensuse-tumbleweed"] = "suse",
        ["opensuse-microos"] = "suse", ["sl-micro"] = "suse",
        ["arch"] = "arch", ["manjaro"] = "arch", ["endeavouros"] = "arch", ["garuda"] = "arch", ["artix"] = "arch", ["cachyos"] = "arch",
        ["alpine"] = "alpine", ["gentoo"] = "gentoo", ["nixos"] = "nixos", ["photon"] = "photon", ["mariner"] = "rhel", ["azurelinux"] = "rhel",
    };

    /// <summary>Distributions made for servers: a desktop on them is "Server with GUI" unless the device is a laptop / desktop PC.</summary>
    private static readonly HashSet<string> ServerDistributions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "rhel", "centos", "rocky", "almalinux", "ol", "amzn", "sles", "sles_sap", "cloudlinux", "scientific", "eurolinux", "virtuozzo", "circle", "navy", "miraclelinux", "mariner", "azurelinux", "photon", "sl-micro" };

    private static readonly string[] ServerMetaPackages = { "ubuntu-server", "ubuntu-server-minimal", "fedora-release-server", "redhat-release-server", "sles-release", "sles_sap-release" };
    private static readonly string[] DesktopMetaPackages =
    {
        "ubuntu-desktop", "ubuntu-desktop-minimal", "kubuntu-desktop", "xubuntu-desktop", "lubuntu-desktop", "ubuntu-mate-desktop", "ubuntucinnamon-desktop",
        "ubuntu-budgie-desktop", "task-desktop", "task-gnome-desktop", "task-kde-desktop", "task-xfce-desktop", "task-cinnamon-desktop", "task-mate-desktop",
        "task-lxqt-desktop", "fedora-release-workstation", "fedora-release-kde", "redhat-release-workstation", "sled-release", "patterns-gnome-gnome",
        "patterns-kde-kde_plasma", "patterns-xfce-xfce", "mint-meta-core", "pop-desktop", "elementary-desktop", "zorin-os-desktop",
    };

    /// <summary>Desktop environments by session file or package.</summary>
    private static readonly (string Key, string Name)[] Desktops =
    {
        ("gnome", "GNOME"), ("ubuntu", "GNOME"), ("pop", "GNOME"), ("plasma", "KDE Plasma"), ("kde", "KDE Plasma"), ("xfce", "Xfce"), ("cinnamon", "Cinnamon"),
        ("mate", "MATE"), ("lxqt", "LXQt"), ("lxde", "LXDE"), ("budgie", "Budgie"), ("pantheon", "Pantheon"), ("deepin", "Deepin"), ("cosmic", "COSMIC"),
        ("enlightenment", "Enlightenment"), ("i3", "i3"), ("sway", "Sway"), ("hyprland", "Hyprland"), ("openbox", "Openbox"), ("icewm", "IceWM"),
    };

    private static readonly (string Package, string Name)[] DesktopPackages =
    {
        ("gnome-shell", "GNOME"), ("plasma-workspace", "KDE Plasma"), ("plasma-desktop", "KDE Plasma"), ("xfce4-session", "Xfce"), ("cinnamon", "Cinnamon"),
        ("cinnamon-session", "Cinnamon"), ("mate-session-manager", "MATE"), ("lxqt-session", "LXQt"), ("lxsession", "LXDE"), ("budgie-desktop", "Budgie"),
        ("gala", "Pantheon"), ("dde-session", "Deepin"), ("cosmic-session", "COSMIC"),
    };

    private static readonly string[] DisplayManagers = { "gdm", "gdm3", "sddm", "lightdm", "lxdm", "xdm", "slim", "kdm", "mdm", "greetd", "ly", "cosmic-greeter" };

    public static string FamilyOf(string? id, string? idLike)
    {
        if (id is not null && Families.TryGetValue(id, out var f)) return f;
        foreach (var like in (idLike ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(like, "fedora", StringComparison.OrdinalIgnoreCase) && (idLike ?? "").IndexOf("rhel", StringComparison.OrdinalIgnoreCase) >= 0) return "rhel";
            if (Families.TryGetValue(like, out var lf)) return lf;
            if (like.StartsWith("suse", StringComparison.OrdinalIgnoreCase)) return "suse";
        }
        return "other";
    }

    public static LinuxIdentity Identify(LinuxOsFacts f)
    {
        string? Os(string k) => f.OsRelease.TryGetValue(k, out var v) && v.Trim().Length > 0 ? v.Trim() : null;
        var id = Os("ID")?.ToLowerInvariant();
        var result = new LinuxIdentity
        {
            Distribution = id,
            Family = FamilyOf(id, Os("ID_LIKE")),
            Name = Os("PRETTY_NAME") ?? Os("NAME") ?? f.RedhatRelease,
            Version = Os("VERSION_ID"),
            Codename = Os("VERSION_CODENAME") ?? Os("UBUNTU_CODENAME"),
        };
        var version = Os("VERSION") ?? "";
        if (version.IndexOf("LTS", StringComparison.OrdinalIgnoreCase) >= 0 || (Os("PRETTY_NAME") ?? "").IndexOf(" LTS", StringComparison.OrdinalIgnoreCase) >= 0) result.Lts = true;

        // Desktop environment(s).
        var des = new List<string>();
        foreach (var s in f.Sessions)
        {
            var low = s.ToLowerInvariant();
            foreach (var (key, name) in Desktops)
                if (low == key || low.StartsWith(key + "-", StringComparison.Ordinal) || low.StartsWith(key + "_", StringComparison.Ordinal) || low.StartsWith(key + "wayland", StringComparison.Ordinal)
                    || (key == "plasma" && low.StartsWith("plasma", StringComparison.Ordinal)) || (key == "xfce" && low.StartsWith("xfce", StringComparison.Ordinal)))
                { if (!des.Contains(name)) des.Add(name); break; }
        }
        foreach (var (pkg, name) in DesktopPackages)
            if (f.Packages.Contains(pkg) && !des.Contains(name)) des.Add(name);
        result.DesktopEnvironment = des.Count > 0 ? string.Join(", ", des) : null;

        var dm = Unit(f.DisplayManager);
        var hasDisplayManager = dm is not null && DisplayManagers.Any(d => string.Equals(d, dm, StringComparison.OrdinalIgnoreCase));
        var graphicalBoot = string.Equals(f.DefaultTarget?.Trim(), "graphical.target", StringComparison.OrdinalIgnoreCase);
        var desktopMeta = DesktopMetaPackages.FirstOrDefault(p => f.Packages.Contains(p));
        var desktopInstalled = des.Count > 0 || desktopMeta is not null || hasDisplayManager;
        // A desktop is in use when the system starts into it (display manager / graphical target) or someone uses it now.
        var desktopActive = desktopInstalled && (hasDisplayManager || graphicalBoot || f.GraphicalSession);
        var portable = f.Chassis is "laptop" or "desktop";

        // 1. What the distribution states.
        string? stated = null; string? statedName = null;
        var variant = (Os("VARIANT_ID") ?? "").ToLowerInvariant();
        var variantName = Os("VARIANT");
        switch (id)
        {
            case "sles": case "sles_sap": stated = "server"; statedName = id == "sles_sap" ? "SUSE Linux Enterprise Server for SAP Applications" : "SUSE Linux Enterprise Server"; break;
            case "sled": stated = "desktop"; statedName = "SUSE Linux Enterprise Desktop"; break;
            case "ubuntu-core": stated = "server"; statedName = "Ubuntu Core"; break;
        }
        if (stated is null && variant.Length > 0)
        {
            if (variant is "server" or "coreos" or "iot" or "cloud" or "container" or "core") stated = "server";
            else if (variant is "workstation" or "kde" or "xfce" or "cinnamon" or "mate" or "lxqt" or "lxde" or "budgie" or "silverblue" or "kinoite" or "sway" or "sericea" or "onyx" or "i3" or "cosmic" or "desktop")
                stated = "desktop";
        }
        var rr = f.RedhatRelease ?? "";
        if (stated is null && rr.Length > 0)
        {
            if (rr.IndexOf(" Server", StringComparison.OrdinalIgnoreCase) >= 0 && rr.IndexOf("Oracle", StringComparison.OrdinalIgnoreCase) < 0) stated = "server";
            else if (rr.IndexOf(" Workstation", StringComparison.OrdinalIgnoreCase) >= 0 || rr.IndexOf(" Client", StringComparison.OrdinalIgnoreCase) >= 0 || rr.IndexOf(" Desktop", StringComparison.OrdinalIgnoreCase) >= 0) stated = "desktop";
        }
        var serverMeta = ServerMetaPackages.FirstOrDefault(p => f.Packages.Contains(p));
        if (stated is null && serverMeta is not null) stated = "server";
        if (stated is null && desktopMeta is not null && (desktopMeta.Contains("workstation") || desktopMeta == "sled-release")) stated = "desktop";

        // 2. Decide.
        if (stated == "server")
        {
            result.IsServer = true;
            result.InstallationType = desktopActive ? "Server with GUI" : "Server";
            result.Reason = variant.Length > 0 ? $"VARIANT_ID={variant}" : serverMeta is not null ? $"package {serverMeta}" : id is not null && statedName is not null ? $"distribution {id}" : $"/etc/redhat-release: {rr}";
        }
        else if (stated == "desktop")
        {
            result.IsServer = false;
            result.InstallationType = "Desktop";
            result.Reason = variant.Length > 0 ? $"VARIANT_ID={variant}" : desktopMeta is not null ? $"package {desktopMeta}" : id == "sled" ? "distribution sled" : $"/etc/redhat-release: {rr}";
        }
        else if (desktopActive || (desktopInstalled && portable))
        {
            var serverDistro = id is not null && ServerDistributions.Contains(id);
            if (serverDistro && !portable)
            {
                result.IsServer = true;
                result.InstallationType = "Server with GUI";
                result.Reason = $"{id} with a desktop ({result.DesktopEnvironment ?? dm ?? "graphical"}) on a {f.Chassis ?? "non-portable"} machine";
            }
            else
            {
                result.IsServer = false;
                result.InstallationType = "Desktop";
                result.Reason = hasDisplayManager ? $"display manager {dm}" : graphicalBoot ? "starts into graphical.target" : f.GraphicalSession ? "graphical session open" : $"desktop installed on a {f.Chassis}";
            }
        }
        else
        {
            result.IsServer = true;
            result.InstallationType = "Server";
            result.Reason = desktopInstalled ? "desktop installed but not started (multi-user.target, no display manager)" : "no desktop environment";
        }

        // 3. Edition name.
        result.Edition = statedName ?? EditionName(id, Os("NAME"), variant, variantName, result);
        return result;
    }

    private static string? Unit(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var name = path!.Trim().Replace('\\', '/');
        name = name.Substring(name.LastIndexOf('/') + 1);
        if (name.EndsWith(".service", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 8);
        return name.Length == 0 || name == "display-manager" ? null : name;
    }

    private static string? EditionName(string? id, string? name, string variant, string? variantName, LinuxIdentity r)
    {
        var product = id switch
        {
            "ubuntu" => "Ubuntu",
            "debian" => "Debian",
            "rhel" => "Red Hat Enterprise Linux",
            "centos" => "CentOS" + ((name ?? "").IndexOf("Stream", StringComparison.OrdinalIgnoreCase) >= 0 ? " Stream" : ""),
            "rocky" => "Rocky Linux",
            "almalinux" => "AlmaLinux",
            "ol" => "Oracle Linux",
            "amzn" => "Amazon Linux",
            "fedora" => "Fedora",
            "opensuse-leap" => "openSUSE Leap",
            "opensuse-tumbleweed" => "openSUSE Tumbleweed",
            "linuxmint" => "Linux Mint",
            "arch" => "Arch Linux",
            _ => Clean(name),
        };
        if (product is null) return null;
        if (id == "fedora" && variantName is not null && variant.Length > 0)
        {
            var v = variantName.Replace(" Edition", "").Trim();
            return $"Fedora {v}";
        }
        if (id == "rhel" && r.InstallationType == "Desktop") return "Red Hat Enterprise Linux Workstation";
        var role = r.InstallationType == "Desktop" ? "Desktop" : "Server";
        return $"{product} {role}";
    }

    private static string? Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name!.Trim();
        foreach (var tail in new[] { " GNU/Linux", " Server", " Linux Server", " Workstation" })
            if (n.EndsWith(tail, StringComparison.OrdinalIgnoreCase) && n.Length > tail.Length) n = n.Substring(0, n.Length - tail.Length);
        return n;
    }
}
