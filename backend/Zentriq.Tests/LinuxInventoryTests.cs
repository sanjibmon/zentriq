using Zentriq.Inventory;

namespace Zentriq.Tests;

/// <summary>Linux distribution, edition and server / desktop identification, and the Linux inventory details.</summary>
public sealed class LinuxInventoryTests
{
    private static Dictionary<string, string> OsRelease(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('=', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim(), p => p[1].Trim().Trim('"'));

    private static LinuxIdentity Id(string osRelease, string[]? packages = null, string? target = "multi-user.target", string? dm = null,
        string[]? sessions = null, string? redhat = null, string chassis = "virtual", bool graphical = false) =>
        LinuxOs.Identify(new LinuxOsFacts
        {
            OsRelease = OsRelease(osRelease),
            Packages = new HashSet<string>(packages ?? [], StringComparer.OrdinalIgnoreCase),
            DefaultTarget = target,
            DisplayManager = dm,
            Sessions = sessions ?? [],
            RedhatRelease = redhat,
            Chassis = chassis,
            GraphicalSession = graphical,
        });

    private const string Ubuntu = "NAME=\"Ubuntu\"\nVERSION_ID=\"24.04\"\nVERSION=\"24.04.1 LTS (Noble Numbat)\"\nVERSION_CODENAME=noble\nID=ubuntu\nID_LIKE=debian\nPRETTY_NAME=\"Ubuntu 24.04.1 LTS\"";
    private const string Debian = "PRETTY_NAME=\"Debian GNU/Linux 12 (bookworm)\"\nNAME=\"Debian GNU/Linux\"\nVERSION_ID=\"12\"\nVERSION_CODENAME=bookworm\nID=debian";
    private const string Rhel9 = "NAME=\"Red Hat Enterprise Linux\"\nVERSION=\"9.4 (Plow)\"\nID=\"rhel\"\nID_LIKE=\"fedora\"\nVERSION_ID=\"9.4\"\nPRETTY_NAME=\"Red Hat Enterprise Linux 9.4 (Plow)\"";

    [Fact]
    public void Ubuntu_server_and_desktop()
    {
        var server = Id(Ubuntu, ["ubuntu-server", "openssh-server"]);
        Assert.Equal(("ubuntu", "debian", "Server", true, "Ubuntu Server"), (server.Distribution, server.Family, server.InstallationType, server.IsServer, server.Edition));
        Assert.True(server.Lts);
        Assert.Equal("noble", server.Codename);
        Assert.Null(server.DesktopEnvironment);

        var desktop = Id(Ubuntu, ["ubuntu-desktop-minimal", "gnome-shell"], "graphical.target", "/usr/lib/systemd/system/gdm3.service", ["ubuntu", "ubuntu-wayland"], chassis: "laptop");
        Assert.Equal(("Desktop", false, "Ubuntu Desktop", "GNOME"), (desktop.InstallationType, desktop.IsServer, desktop.Edition, desktop.DesktopEnvironment));

        // Ubuntu Server with a desktop added but not started: still a server.
        var headless = Id(Ubuntu, ["ubuntu-server", "xfce4-session"], "multi-user.target", null, ["xfce"]);
        Assert.Equal(("Server", true, "Xfce"), (headless.InstallationType, headless.IsServer, headless.DesktopEnvironment));
        // ...and started into it: server with GUI.
        var gui = Id(Ubuntu, ["ubuntu-server", "xfce4-session", "lightdm"], "graphical.target", "/lib/systemd/system/lightdm.service", ["xfce"]);
        Assert.Equal(("Server with GUI", true), (gui.InstallationType, gui.IsServer));
    }

    [Fact]
    public void Debian_headless_and_kde()
    {
        var d = Id(Debian, ["openssh-server"]);
        Assert.Equal(("debian", "Server", true, "Debian Server"), (d.Family, d.InstallationType, d.IsServer, d.Edition));
        var kde = Id(Debian, ["task-kde-desktop", "plasma-workspace", "sddm"], "graphical.target", "/lib/systemd/system/sddm.service", ["plasma", "plasmax11"], chassis: "desktop");
        Assert.Equal(("Desktop", "KDE Plasma", "Debian Desktop"), (kde.InstallationType, kde.DesktopEnvironment, kde.Edition));
    }

    [Fact]
    public void Rhel_minimal_server_with_gui_and_workstation()
    {
        var min = Id(Rhel9);
        Assert.Equal(("rhel", "rhel", "Server", "Red Hat Enterprise Linux Server"), (min.Distribution, min.Family, min.InstallationType, min.Edition));
        var gui = Id(Rhel9, ["gnome-shell", "gdm"], "graphical.target", "/usr/lib/systemd/system/gdm.service", ["gnome", "gnome-classic"], chassis: "server");
        Assert.Equal(("Server with GUI", true, "GNOME"), (gui.InstallationType, gui.IsServer, gui.DesktopEnvironment));
        var laptop = Id(Rhel9, ["gnome-shell", "gdm"], "graphical.target", "/usr/lib/systemd/system/gdm.service", ["gnome"], chassis: "laptop");
        Assert.Equal(("Desktop", false, "Red Hat Enterprise Linux Workstation"), (laptop.InstallationType, laptop.IsServer, laptop.Edition));
        var rhel7 = Id("NAME=\"Red Hat Enterprise Linux Workstation\"\nID=\"rhel\"\nVERSION_ID=\"7.9\"\nPRETTY_NAME=\"Red Hat Enterprise Linux Workstation 7.9 (Maipo)\"",
            redhat: "Red Hat Enterprise Linux Workstation release 7.9 (Maipo)");
        Assert.Equal(("Desktop", false), (rhel7.InstallationType, rhel7.IsServer));
        var rhel7s = Id("NAME=\"Red Hat Enterprise Linux Server\"\nID=\"rhel\"\nVERSION_ID=\"7.9\"", redhat: "Red Hat Enterprise Linux Server release 7.9 (Maipo)");
        Assert.Equal(("Server", true), (rhel7s.InstallationType, rhel7s.IsServer));
    }

    [Theory]
    [InlineData("NAME=\"Rocky Linux\"\nID=\"rocky\"\nID_LIKE=\"rhel centos fedora\"\nVERSION_ID=\"9.4\"", "rocky", "rhel", "Rocky Linux Server")]
    [InlineData("NAME=\"AlmaLinux\"\nID=\"almalinux\"\nID_LIKE=\"rhel centos fedora\"\nVERSION_ID=\"9.4\"", "almalinux", "rhel", "AlmaLinux Server")]
    [InlineData("NAME=\"Oracle Linux Server\"\nVERSION=\"9.4\"\nID=\"ol\"\nID_LIKE=\"fedora\"\nVARIANT=\"Server\"\nVARIANT_ID=\"server\"\nVERSION_ID=\"9.4\"", "ol", "rhel", "Oracle Linux Server")]
    [InlineData("NAME=\"Amazon Linux\"\nVERSION=\"2023\"\nID=\"amzn\"\nID_LIKE=\"fedora\"\nVERSION_ID=\"2023\"", "amzn", "rhel", "Amazon Linux Server")]
    [InlineData("NAME=\"CentOS Stream\"\nID=\"centos\"\nID_LIKE=\"rhel fedora\"\nVERSION_ID=\"9\"", "centos", "rhel", "CentOS Stream Server")]
    [InlineData("NAME=\"SLES\"\nVERSION=\"15-SP6\"\nVERSION_ID=\"15.6\"\nID=\"sles\"\nID_LIKE=\"suse\"", "sles", "suse", "SUSE Linux Enterprise Server")]
    [InlineData("NAME=\"openSUSE Leap\"\nVERSION=\"15.6\"\nID=\"opensuse-leap\"\nID_LIKE=\"suse opensuse\"\nVERSION_ID=\"15.6\"", "opensuse-leap", "suse", "openSUSE Leap Server")]
    [InlineData("NAME=\"Arch Linux\"\nID=arch\nBUILD_ID=rolling", "arch", "arch", "Arch Linux Server")]
    [InlineData("NAME=\"MyCorp Linux\"\nID=mycorp\nID_LIKE=\"rhel fedora\"\nVERSION_ID=\"9\"", "mycorp", "rhel", "MyCorp Linux Server")]
    public void Distributions_and_families(string osRelease, string id, string family, string edition)
    {
        var r = Id(osRelease);
        Assert.Equal((id, family, edition, true), (r.Distribution, r.Family, r.Edition, r.IsServer));
    }

    [Fact]
    public void Stated_editions_win()
    {
        var fw = Id("NAME=\"Fedora Linux\"\nVERSION=\"41 (Workstation Edition)\"\nID=fedora\nVERSION_ID=41\nVARIANT=\"Workstation Edition\"\nVARIANT_ID=workstation", chassis: "virtual");
        Assert.Equal(("fedora", "Desktop", false, "Fedora Workstation"), (fw.Family, fw.InstallationType, fw.IsServer, fw.Edition));
        var fs = Id("NAME=\"Fedora Linux\"\nID=fedora\nVERSION_ID=41\nVARIANT=\"Server Edition\"\nVARIANT_ID=server", chassis: "laptop");
        Assert.Equal(("Server", true, "Fedora Server"), (fs.InstallationType, fs.IsServer, fs.Edition));
        var sled = Id("NAME=\"SLED\"\nID=\"sled\"\nID_LIKE=\"suse\"\nVERSION_ID=\"15.6\"");
        Assert.Equal(("Desktop", false, "SUSE Linux Enterprise Desktop"), (sled.InstallationType, sled.IsServer, sled.Edition));
        var sles = Id("NAME=\"SLES\"\nID=\"sles\"\nVERSION_ID=\"15.6\"", ["gnome-shell", "gdm"], "graphical.target", "gdm.service", ["gnome"], chassis: "laptop");
        Assert.Equal(("Server with GUI", true), (sles.InstallationType, sles.IsServer));
        var mint = Id("NAME=\"Linux Mint\"\nVERSION=\"22 (Wilma)\"\nID=linuxmint\nID_LIKE=\"ubuntu debian\"\nVERSION_ID=\"22\"",
            ["mint-meta-core", "cinnamon"], "graphical.target", "/lib/systemd/system/lightdm.service", ["cinnamon", "cinnamon2d"], chassis: "laptop");
        Assert.Equal(("debian", "Desktop", "Linux Mint Desktop", "Cinnamon"), (mint.Family, mint.InstallationType, mint.Edition, mint.DesktopEnvironment));
    }

    [Fact]
    public void Linux_details_from_the_agent_commands()
    {
        var o = new Dictionary<string, string>
        {
            ["hostname"] = "ws01\n",
            ["osrelease"] = Ubuntu,
            ["dmi"] = "sys_vendor=LENOVO\nproduct_name=20XW\nproduct_serial=PF2ABCDE\nproduct_uuid=4C4C4544-0042-3510-8051-B3C04F4A3332\nchassis_type=10\nbios_vendor=LENOVO\nbios_version=N32ET86W\nbios_date=05/14/2024\n",
            ["df"] = "Filesystem Type 1024-blocks Used Available Capacity Mounted on\n/dev/mapper/vgubuntu-root ext4 500000000 100000000 400000000 20% /\n/dev/nvme0n1p1 vfat 524288 6000 518288 2% /boot/efi\n",
            ["packages"] = "dpkg\tubuntu-desktop-minimal\t1.539\tamd64\ndpkg\tgnome-shell\t46.0-0ubuntu6\tamd64\ndpkg\topenssh-server\t1:9.6p1\tamd64\n",
            ["services"] = "ssh.service loaded active running OpenBSD Secure Shell server\nmdatp.service loaded active running Microsoft Defender for Endpoint\n",
            ["virt"] = "none\n",
            ["osroles"] = "graphical.target\n##\n/usr/lib/systemd/system/gdm3.service\n##\nubuntu.desktop\nubuntu-xorg.desktop\n##\n##\nName=alice Type=wayland State=active Remote=no Class=user \nName=gdm Type=wayland State=online Remote=no Class=greeter \n",
            ["disks"] = "{\"blockdevices\": [{\"name\":\"nvme0n1\", \"model\":\"SAMSUNG MZVL2512\", \"serial\":\"S6XYZ\", \"size\":512110190592, \"rota\":false, \"tran\":\"nvme\", \"type\":\"disk\"},{\"name\":\"loop0\",\"model\":null,\"serial\":null,\"size\":1000,\"rota\":false,\"tran\":null,\"type\":\"loop\"}]}",
            ["crypt"] = "/ lvm,crypt,part,disk,\n/boot/efi part,disk,\n",
            ["firewall"] = "uid=0\nufw=Status: active\nfirewalld=inactive\n",
            ["secureboot"] = "uefi\n   6   0   0   0   1\n",
            ["tpm"] = "2\n",
            ["admins"] = "sudo:x:27:alice,bob\n",
            ["ssh"] = "installed\nactive=1\neff:permitrootlogin no\neff:passwordauthentication no\neff:port 2222\n",
            ["mac"] = "apparmor=Y\n",
            ["autoupd"] = "APT::Periodic::Unattended-Upgrade \"1\";\nunattended=enabled\ndnfauto=0\nyumcron=\n",
            ["accounts"] = "root toor\nPASS_MAX_DAYS\t99999\n",
            ["daemons"] = "auditd=inactive\nntp=yes\nreboot=yes\n",
            ["updates"] = "apt 12 3\n",
        };
        var doc = UnixCollector.ParseLinux(o, "Linux\nx86_64\n6.8.0-47-generic\n", "1.1.1");
        Assert.Equal(("ubuntu", "debian", "Desktop", false, "Ubuntu Desktop", "GNOME"),
            (doc.Os.Distribution, doc.Os.DistributionFamily, doc.Os.InstallationType, doc.Os.IsServer, doc.Os.EditionId, doc.Os.DesktopEnvironment));
        Assert.Equal(("6.8.0-47-generic", "dpkg", "noble"), (doc.Os.Kernel, doc.Os.PackageManager, doc.Os.DisplayVersion));
        Assert.Equal(("laptop", "endpoint"), (doc.Device.Chassis, doc.Device.Category));
        Assert.Equal("alice", doc.Users.LoggedOn);
        Assert.Equal(new[] { "root", "alice", "bob" }, doc.Users.LocalAdmins);
        var disk = Assert.Single(doc.Hardware.Disks);
        Assert.Equal(("SAMSUNG MZVL2512", "SSD (NVMe)", "NVME", 512110190592L), (disk.Model, disk.MediaType, disk.Interface, disk.SizeBytes!.Value));
        Assert.True(doc.Security.DiskEncryption);
        Assert.True(doc.Hardware.Volumes.Single(v => v.Name == "/").Encrypted);
        Assert.False(doc.Hardware.Volumes.Single(v => v.Name == "/boot/efi").Encrypted);
        Assert.True(doc.Security.FirewallEnabled);
        Assert.True(doc.Security.SecureBoot);
        Assert.Equal((true, "2.0"), (doc.Hardware.TpmPresent, doc.Hardware.TpmVersion));
        Assert.Equal("Microsoft Defender for Endpoint", Assert.Single(doc.Security.Antivirus).Name);
        var h = doc.Security.Linux!;
        Assert.Equal(("ufw", "no", false, 2222, true), (h.Firewall, h.SshRootLogin, h.SshPasswordAuth!.Value, h.SshPort!.Value, h.SshServer!.Value));
        Assert.Equal((true, true, 99999, false, true, true), (h.AppArmor!.Value, h.AutoUpdates!.Value, h.PassMaxDays!.Value, h.Auditd!.Value, h.TimeSync!.Value, h.RebootRequired!.Value));
        Assert.Equal(new[] { "toor" }, h.ExtraUid0);
        Assert.Equal((12, 3), (h.PendingUpdates!.Value, h.PendingSecurityUpdates!.Value));
        Assert.False(h.TelnetServer);
        Assert.Null(h.Selinux);
    }

    [Fact]
    public void Without_root_unknown_stays_unknown()
    {
        var o = new Dictionary<string, string>
        {
            ["osrelease"] = Rhel9,
            ["osroles"] = "multi-user.target\n##\n##\n##\n##\n",
            ["firewall"] = "uid=1000\nfirewalld=inactive\nnft=0\niptables=0\n",
            ["ssh"] = "installed\nactive=1\ncfg:PasswordAuthentication no\n",
            ["mac"] = "selinux=Enforcing\n",
        };
        var doc = UnixCollector.ParseLinux(o, "Linux\nx86_64\n5.14.0-427.el9.x86_64\n", "1.1.1");
        Assert.Equal(("Server", true, "server"), (doc.Os.InstallationType, doc.Os.IsServer, doc.Device.Category));
        Assert.Null(doc.Security.FirewallEnabled);
        Assert.Equal(("prohibit-password", false, "enforcing"), (doc.Security.Linux!.SshRootLogin, doc.Security.Linux.SshPasswordAuth!.Value, doc.Security.Linux.Selinux));
    }
}
