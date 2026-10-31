using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Zentriq.Inventory;

/// <summary>
/// Linux details beyond the basics: distribution and edition (server / desktop), desktop environment, disks,
/// encryption, firewall, Secure Boot, TPM, security products, administrators and security settings. Same read-only
/// commands for the Linux agent (as root) and agentless SSH (as the given account; fields that need root stay null).
/// </summary>
public static partial class UnixCollector
{
    public static readonly IReadOnlyDictionary<string, string> LinuxExtraCommands = new Dictionary<string, string>
    {
        ["osroles"] = "systemctl get-default 2>/dev/null; echo '##'; readlink -f /etc/systemd/system/display-manager.service 2>/dev/null; echo '##'; " +
                      "ls -1 /usr/share/xsessions /usr/share/wayland-sessions 2>/dev/null; echo '##'; head -1 /etc/redhat-release 2>/dev/null; echo '##'; " +
                      "for s in $(loginctl list-sessions --no-legend 2>/dev/null | awk '{print $1}'); do loginctl show-session \"$s\" -p Name -p Type -p State -p Remote -p Class 2>/dev/null | tr '\\n' ' '; echo; done",
        ["disks"] = "lsblk -J -b -d -o NAME,MODEL,SERIAL,SIZE,ROTA,TRAN,TYPE 2>/dev/null",
        ["crypt"] = "findmnt -rn -o TARGET,SOURCE -t ext2,ext3,ext4,xfs,btrfs,f2fs,jfs,reiserfs,zfs,vfat 2>/dev/null | while read -r t s; do echo \"$t $(lsblk -n -s -o TYPE \"$s\" 2>/dev/null | tr '\\n' ',')\"; done",
        ["firewall"] = "echo \"uid=$(id -u)\"; if command -v ufw >/dev/null 2>&1; then echo \"ufw=$(ufw status 2>/dev/null | head -1)\"; fi; echo \"firewalld=$(systemctl is-active firewalld 2>/dev/null)\"; " +
                       "if command -v nft >/dev/null 2>&1; then echo \"nft=$(nft list ruleset 2>/dev/null | grep -c 'hook input')\"; fi; " +
                       "if command -v iptables >/dev/null 2>&1; then echo \"iptables=$(iptables -S INPUT 2>/dev/null | grep -c '^-A')\"; echo \"iptpolicy=$(iptables -S INPUT 2>/dev/null | grep '^-P' | head -1)\"; fi",
        ["secureboot"] = "if [ -d /sys/firmware/efi ]; then echo uefi; od -An -t u1 /sys/firmware/efi/efivars/SecureBoot-8be4df61-93ca-11d2-aa0d-00e098032b8c 2>/dev/null; else echo bios; fi",
        ["tpm"] = "if [ -e /sys/class/tpm/tpm0 ]; then cat /sys/class/tpm/tpm0/tpm_version_major 2>/dev/null || echo present; fi",
        ["admins"] = "getent group sudo wheel admin 2>/dev/null",
        ["ssh"] = "if [ -x /usr/sbin/sshd ] || command -v sshd >/dev/null 2>&1; then echo installed; echo \"active=$(systemctl is-active sshd ssh 2>/dev/null | grep -c '^active')\"; " +
                  "(/usr/sbin/sshd -T 2>/dev/null || sshd -T 2>/dev/null) | grep -Ei '^(permitrootlogin|passwordauthentication|port) ' | sed 's/^/eff:/'; " +
                  "grep -hEi '^[[:space:]]*(PermitRootLogin|PasswordAuthentication|Port)[[:space:]]' /etc/ssh/sshd_config.d/*.conf /etc/ssh/sshd_config 2>/dev/null | sed 's/^/cfg:/'; fi",
        ["mac"] = "command -v getenforce >/dev/null 2>&1 && echo \"selinux=$(getenforce 2>/dev/null)\"; [ -e /sys/module/apparmor/parameters/enabled ] && echo \"apparmor=$(cat /sys/module/apparmor/parameters/enabled 2>/dev/null)\"",
        ["autoupd"] = "apt-config dump 2>/dev/null | grep -E '^APT::Periodic::Unattended-Upgrade '; echo \"unattended=$(systemctl is-enabled unattended-upgrades 2>/dev/null)\"; " +
                      "echo \"dnfauto=$(systemctl is-enabled dnf-automatic.timer dnf-automatic-install.timer dnf5-automatic.timer 2>/dev/null | grep -c '^enabled')\"; " +
                      "echo \"yumcron=$(systemctl is-enabled yum-cron 2>/dev/null)\"; [ -f /etc/sysconfig/automatic_online_update ] && grep -E '^AOU_ENABLE_CRONJOB' /etc/sysconfig/automatic_online_update",
        ["accounts"] = "awk -F: '$3==0{print $1}' /etc/passwd 2>/dev/null | tr '\\n' ' '; echo; grep -E '^PASS_MAX_DAYS' /etc/login.defs 2>/dev/null",
        ["daemons"] = "echo \"auditd=$(systemctl is-active auditd 2>/dev/null)\"; echo \"ntp=$(timedatectl show -p NTPSynchronized --value 2>/dev/null)\"; " +
                      "if [ -f /var/run/reboot-required ]; then echo reboot=yes; elif command -v needs-restarting >/dev/null 2>&1; then needs-restarting -r >/dev/null 2>&1 && echo reboot=no || echo reboot=check; fi; " +
                      "for u in telnet.socket telnetd inetd xinetd; do systemctl is-active \"$u\" 2>/dev/null | grep -q '^active' && echo \"telnet=$u\"; done",
        ["updates"] = "if command -v apt-get >/dev/null 2>&1; then timeout 60 apt-get -s -o Debug::NoLocking=1 upgrade 2>/dev/null | grep '^Inst' | awk '{c++; if ($0 ~ /[Ss]ecurity/) s++} END {print \"apt \" c+0 \" \" s+0}'; " +
                      "elif command -v dnf >/dev/null 2>&1; then echo \"dnf $(timeout 60 dnf -q -C check-update 2>/dev/null | grep -cE '^[A-Za-z0-9_.+-]+\\.[a-z0-9_]+ ') $(timeout 60 dnf -q -C updateinfo list --security 2>/dev/null | grep -c .)\"; " +
                      "elif command -v yum >/dev/null 2>&1; then echo \"yum $(timeout 60 yum -q -C check-update 2>/dev/null | grep -cE '^[A-Za-z0-9_.+-]+\\.[a-z0-9_]+ ') -\"; " +
                      "elif command -v zypper >/dev/null 2>&1; then echo \"zypper $(timeout 60 zypper -q --no-refresh lu 2>/dev/null | grep -c '^v ') $(timeout 60 zypper -q --no-refresh lp --category security 2>/dev/null | grep -c '| needed')\"; fi",
    };

    /// <summary>Security products by systemd unit (Linux).</summary>
    private static readonly (string Unit, string Product)[] SecurityProducts =
    {
        ("clamav-daemon", "ClamAV"), ("clamd", "ClamAV"), ("clamd@scan", "ClamAV"), ("falcon-sensor", "CrowdStrike Falcon"), ("sentinelone", "SentinelOne"),
        ("mdatp", "Microsoft Defender for Endpoint"), ("sophos-spl", "Sophos Protection for Linux"), ("sav-protect", "Sophos Anti-Virus"), ("ds_agent", "Trend Micro Deep Security"),
        ("esets", "ESET Server Security"), ("eea", "ESET Endpoint Antivirus"), ("cbagentd", "VMware Carbon Black"), ("cbsensor", "VMware Carbon Black"),
        ("mfetpd", "Trellix Endpoint Security"), ("mfeespd", "Trellix Endpoint Security"), ("bdsec", "Bitdefender GravityZone"), ("kesl", "Kaspersky Endpoint Security"),
        ("cylancesvc", "Cylance"), ("elastic-agent", "Elastic Defend"), ("wazuh-agent", "Wazuh"), ("qualys-cloud-agent", "Qualys Cloud Agent"),
    };

    /// <summary>Adds the Linux details to a document made by <see cref="ParseLinux"/>.</summary>
    internal static void EnrichLinux(InventoryDocument doc, IReadOnlyDictionary<string, string> o, string? kernel)
    {
        doc.Os.Kernel = kernel;
        var osr = KeyValues(Get(o, "osrelease"), '=').ToDictionary(k => k.Key, k => Unquote(k.Value) ?? "", StringComparer.Ordinal);

        // Packages (names), package manager.
        var packages = new HashSet<string>(doc.Software.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        var sources = doc.Software.Select(s => s.Source).Where(s => s is not null).GroupBy(s => s).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        doc.Os.PackageManager = sources;

        // Roles: default target, display manager, sessions, redhat-release, sessions in use.
        var roles = Sections(Get(o, "osroles"), 5);
        var sessions = Lines(roles[2]).Select(l => l.Trim()).Where(l => l.EndsWith(".desktop", StringComparison.Ordinal))
            .Select(l => l.Substring(0, l.Length - 8)).Distinct().ToList();
        string? loggedOn = null; var graphical = false;
        foreach (var line in Lines(roles[4]))
        {
            var kv = Regex.Matches(line, @"(\w+)=(\S*)").Cast<Match>().GroupBy(m => m.Groups[1].Value).ToDictionary(g => g.Key, g => g.First().Groups[2].Value);
            if (!kv.TryGetValue("Class", out var cls) || cls != "user") continue;
            kv.TryGetValue("Type", out var type); kv.TryGetValue("State", out var state); kv.TryGetValue("Name", out var name);
            if (type is "x11" or "wayland" or "mir") graphical = true;
            if (loggedOn is null || (state == "active" && type is "x11" or "wayland")) loggedOn = name;
        }
        if (loggedOn is not null) doc.Users.LoggedOn = loggedOn;

        var id = LinuxOs.Identify(new LinuxOsFacts
        {
            OsRelease = osr,
            Packages = packages,
            DefaultTarget = Lines(roles[0]).FirstOrDefault(),
            DisplayManager = Lines(roles[1]).FirstOrDefault(),
            Sessions = sessions,
            RedhatRelease = Lines(roles[3]).FirstOrDefault(),
            Chassis = doc.Device.Chassis,
            GraphicalSession = graphical,
        });
        doc.Os.Distribution = id.Distribution;
        doc.Os.DistributionFamily = id.Family;
        doc.Os.Name = id.Name ?? doc.Os.Name;
        doc.Os.Version = id.Version ?? doc.Os.Version;
        doc.Os.DisplayVersion = id.Codename;
        doc.Os.EditionId = id.Edition;
        doc.Os.InstallationType = id.InstallationType;
        doc.Os.IsServer = id.IsServer;
        doc.Os.DesktopEnvironment = id.DesktopEnvironment;
        doc.Os.Lts = id.Lts;
        doc.Device.Category = id.IsServer ? "server" : "endpoint";

        // Disks.
        var disks = Get(o, "disks").Trim();
        if (disks.StartsWith("{", StringComparison.Ordinal))
            try
            {
                using var j = JsonDocument.Parse(disks);
                if (j.RootElement.TryGetProperty("blockdevices", out var devs) && devs.ValueKind == JsonValueKind.Array)
                    foreach (var d in devs.EnumerateArray())
                    {
                        var type = Str(d, "type");
                        var name = Str(d, "name") ?? "";
                        if (type != "disk" || name.StartsWith("loop", StringComparison.Ordinal) || name.StartsWith("zram", StringComparison.Ordinal) || name.StartsWith("ram", StringComparison.Ordinal)) continue;
                        var rota = d.TryGetProperty("rota", out var r) ? (r.ValueKind == JsonValueKind.True || (r.ValueKind == JsonValueKind.String && r.GetString() == "1") || (r.ValueKind == JsonValueKind.Number && r.GetInt32() == 1)) : (bool?)null;
                        var tran = Str(d, "tran");
                        doc.Hardware.Disks.Add(new Disk
                        {
                            Model = WindowsCollector.Clean(Str(d, "model")) ?? name,
                            Serial = WindowsCollector.Clean(Str(d, "serial")),
                            SizeBytes = Long(d, "size"),
                            MediaType = name.StartsWith("nvme", StringComparison.Ordinal) ? "SSD (NVMe)" : rota == true ? "HDD" : rota == false ? "SSD" : null,
                            Interface = tran?.ToUpperInvariant() ?? (name.StartsWith("vd", StringComparison.Ordinal) ? "VIRTIO" : null),
                        });
                    }
            }
            catch (JsonException) { }

        // Encryption: file systems on dm-crypt (LUKS): "mountpoint type,type,…" from the device up to the disk.
        var encrypted = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var line in Lines(Get(o, "crypt")))
        {
            var i = line.LastIndexOf(' ');
            if (i <= 0) continue;
            var chain = line.Substring(i + 1);
            if (chain.Length == 0) continue;
            encrypted[line.Substring(0, i).Replace("\\x20", " ")] = chain.Split(',').Contains("crypt");
        }
        if (encrypted.TryGetValue("/", out var rootEncrypted)) doc.Security.DiskEncryption = rootEncrypted;
        foreach (var v in doc.Hardware.Volumes)
            if (v.Name is not null && encrypted.TryGetValue(v.Name, out var e)) v.Encrypted = e;

        // Firewall: ufw, firewalld, nftables or iptables with input rules. Without root, "off" cannot be told from "not readable".
        var fw = KeyValues(Get(o, "firewall"), '=');
        string? firewall = null;
        if ((fw.TryGet("ufw") ?? "").IndexOf("Status: active", StringComparison.OrdinalIgnoreCase) >= 0) firewall = "ufw";
        else if (fw.TryGet("firewalld") == "active") firewall = "firewalld";
        else if (int.TryParse(fw.TryGet("nft"), out var nft) && nft > 0) firewall = "nftables";
        else if (int.TryParse(fw.TryGet("iptables"), out var ipt) && ipt > 0 || (fw.TryGet("iptpolicy") ?? "").Contains("DROP")) firewall = "iptables";
        if (firewall is not null) doc.Security.FirewallEnabled = true;
        else if (fw.TryGet("uid") == "0") doc.Security.FirewallEnabled = false;

        // Secure Boot and TPM.
        var sb = Lines(Get(o, "secureboot"));
        if (sb.FirstOrDefault() == "bios") doc.Security.SecureBoot = false;
        else if (sb.FirstOrDefault() == "uefi")
        {
            var bytes = sb.Skip(1).SelectMany(l => Regex.Split(l.Trim(), @"\s+")).Where(x => x.Length > 0).ToList();
            doc.Security.SecureBoot = bytes.Count >= 5 ? bytes.Last() == "1" : (bool?)null;
        }
        var tpm = Get(o, "tpm").Trim();
        if (tpm.Length > 0) { doc.Hardware.TpmPresent = true; doc.Hardware.TpmVersion = tpm == "2" ? "2.0" : tpm == "1" ? "1.2" : null; }
        else if (o.ContainsKey("tpm")) doc.Hardware.TpmPresent = false;

        // Security products (by service).
        foreach (var (unit, product) in SecurityProducts)
        {
            var svc = doc.Services.FirstOrDefault(s => string.Equals(s.Name, unit, StringComparison.OrdinalIgnoreCase));
            if (svc is null || doc.Security.Antivirus.Any(a => a.Name == product)) continue;
            doc.Security.Antivirus.Add(new AntivirusProduct { Name = product, Enabled = svc.State == "running" });
        }

        // Administrators: root and members of sudo / wheel / admin.
        var admins = new List<string>();
        foreach (var line in Lines(Get(o, "admins")))
        {
            var p = line.Split(':');
            if (p.Length >= 4) admins.AddRange(p[3].Split(',').Select(x => x.Trim()).Where(x => x.Length > 0));
        }
        if (o.ContainsKey("admins")) doc.Users.LocalAdmins = new[] { "root" }.Concat(admins).Distinct().ToList();

        // Security settings.
        var h = new LinuxHardeningInfo { Firewall = firewall };
        var ssh = Lines(Get(o, "ssh"));
        if (ssh.Contains("installed"))
        {
            h.SshServer = ssh.Any(l => l.StartsWith("active=", StringComparison.Ordinal) && l != "active=0");
            string? Val(string key)
            {
                var eff = ssh.FirstOrDefault(l => l.StartsWith("eff:" + key + " ", StringComparison.OrdinalIgnoreCase));
                if (eff is not null) return eff.Substring(eff.IndexOf(' ') + 1).Trim();
                var cfg = ssh.FirstOrDefault(l => Regex.IsMatch(l, @"^cfg:\s*" + key + @"\s", RegexOptions.IgnoreCase));   // first value wins, as in sshd
                return cfg is null ? null : Regex.Split(cfg.Substring(4).Trim(), @"\s+").ElementAtOrDefault(1);
            }
            var hasEffective = ssh.Any(l => l.StartsWith("eff:", StringComparison.Ordinal));
            h.SshRootLogin = (Val("permitrootlogin") ?? (hasEffective ? null : "prohibit-password"))?.ToLowerInvariant();
            if (h.SshRootLogin == "without-password") h.SshRootLogin = "prohibit-password";
            var pa = Val("passwordauthentication")?.ToLowerInvariant();
            h.SshPasswordAuth = pa is null ? true : pa == "yes";
            h.SshPort = int.TryParse(Val("port"), out var port) ? port : 22;
        }
        else if (o.ContainsKey("ssh")) h.SshServer = false;
        var mac = KeyValues(Get(o, "mac"), '=');
        h.Selinux = mac.TryGet("selinux")?.ToLowerInvariant();
        if (mac.TryGet("apparmor") is { } aa) h.AppArmor = aa.StartsWith("Y", StringComparison.OrdinalIgnoreCase);
        var au = Get(o, "autoupd");
        if (o.ContainsKey("autoupd"))
            h.AutoUpdates = Regex.IsMatch(au, @"Unattended-Upgrade ""?[1-9]") && !Regex.IsMatch(au, @"unattended=disabled")
                            || Regex.IsMatch(au, @"dnfauto=[1-9]") || Regex.IsMatch(au, @"yumcron=enabled") || Regex.IsMatch(au, @"AOU_ENABLE_CRONJOB=""?true", RegexOptions.IgnoreCase);
        var acc = Lines(Get(o, "accounts"));
        if (acc.Count > 0)
        {
            var uid0 = Regex.Split(acc[0].Trim(), @"\s+").Where(x => x.Length > 0 && x != "root").ToList();
            if (!acc[0].StartsWith("PASS_MAX_DAYS", StringComparison.Ordinal)) h.ExtraUid0 = uid0;
            var pm = acc.FirstOrDefault(l => l.StartsWith("PASS_MAX_DAYS", StringComparison.Ordinal));
            if (pm is not null && int.TryParse(Regex.Split(pm.Trim(), @"\s+").ElementAtOrDefault(1), out var days)) h.PassMaxDays = days;
        }
        var dm = KeyValues(Get(o, "daemons"), '=');
        if (dm.TryGet("auditd") is { } ad) h.Auditd = ad == "active";
        if (dm.TryGet("ntp") is { } ntp) h.TimeSync = ntp == "yes";
        if (dm.TryGet("reboot") is { } rb && rb != "check") h.RebootRequired = rb == "yes";
        if (o.ContainsKey("daemons")) h.TelnetServer = dm.ContainsKey("telnet") || packages.Contains("telnetd") || packages.Contains("telnet-server") || packages.Contains("inetutils-telnetd");
        var upd = Lines(Get(o, "updates")).FirstOrDefault();
        if (upd is not null)
        {
            var p = Regex.Split(upd.Trim(), @"\s+");
            if (p.Length >= 2 && int.TryParse(p[1], out var n)) h.PendingUpdates = n;
            if (p.Length >= 3 && int.TryParse(p[2], out var sec)) h.PendingSecurityUpdates = sec;
        }
        doc.Security.Linux = h;
    }

    /// <summary>Splits command output at '##' marker lines into <paramref name="count"/> parts.</summary>
    private static string[] Sections(string text, int count)
    {
        var parts = Regex.Split(text.Replace("\r", ""), @"^##$", RegexOptions.Multiline);
        var result = new string[count];
        for (var i = 0; i < count; i++) result[i] = i < parts.Length ? parts[i] : "";
        return result;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim().NullIfEmpty() : null;

    private static long? Long(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) return s;
        return null;
    }
}
