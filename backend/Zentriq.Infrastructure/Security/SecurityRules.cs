using System.Globalization;
using System.Text.Json.Serialization;
using Zentriq.Inventory;

namespace Zentriq.Infrastructure.Security;

public enum CheckStatus { Pass, Fail, Unknown, NotApplicable }

/// <summary>The device being assessed: its inventory and a few facts from the asset record.</summary>
public sealed record DeviceFacts(InventoryDocument Doc, string Category, DateTimeOffset Now)
{
    public bool IsServer => Category == "server" || Doc.Os.IsServer == true;
    public bool IsEndpoint => !IsServer && Category is "endpoint" or "";
    public bool IsVirtual => Doc.Hardware.IsVirtual == true;
    public HardeningInfo? H => Doc.Security.Hardening;
    public LinuxHardeningInfo? L => Doc.Security.Linux;
}

public sealed record CheckResult(CheckStatus Status, string? Actual)
{
    public static CheckResult Pass(string? actual = null) => new(CheckStatus.Pass, actual);
    public static CheckResult Fail(string? actual) => new(CheckStatus.Fail, actual);
    public static CheckResult Unknown(string why = "not reported by this device's inventory") => new(CheckStatus.Unknown, why);
    public static readonly CheckResult NotApplicable = new(CheckStatus.NotApplicable, null);
    public static CheckResult Bool(bool? ok, string onPass, string onFail) => ok is null ? Unknown() : ok.Value ? Pass(onPass) : Fail(onFail);
}

/// <summary>A rule parameter an administrator can change in a baseline (e.g. maximum days).</summary>
public sealed record RuleParam(string Key, string Label, int Default, int Min, int Max);

/// <summary>
/// One security check. <see cref="Applies"/>: all, server or endpoint. Checks only read the inventory; remediation is
/// guidance for the administrator (Group Policy / Intune), Zentriq does not change device settings.
/// </summary>
public sealed record SecurityRule(
    string Id, string Title, string Category, string Severity, string Applies, string Reference, string Rationale, string Remediation,
    Func<DeviceFacts, IReadOnlyDictionary<string, int>, CheckResult> Check, RuleParam[]? Params = null, bool DefaultOn = true, string Platform = "windows")
{
    public IReadOnlyDictionary<string, int> Resolve(IReadOnlyDictionary<string, int>? overrides)
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in Params ?? []) d[p.Key] = overrides is not null && overrides.TryGetValue(p.Key, out var v) ? Math.Clamp(v, p.Min, p.Max) : p.Default;
        return d;
    }

    public CheckResult Evaluate(DeviceFacts f, IReadOnlyDictionary<string, int>? overrides = null)
    {
        if (f.Doc.Os.Family != Platform) return CheckResult.NotApplicable;
        if (Applies == "server" && !f.IsServer) return CheckResult.NotApplicable;
        if (Applies == "endpoint" && f.IsServer) return CheckResult.NotApplicable;
        try { return Check(f, Resolve(overrides)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return CheckResult.Unknown($"could not be evaluated: {ex.Message}"); }
    }
}

/// <summary>Weights for the compliance score: a failed high-severity check costs three times a low one.</summary>
public static class SecurityScoring
{
    public static int Weight(string severity) => severity switch { "high" => 3, "medium" => 2, _ => 1 };

    /// <summary>0–100: weighted share of passed checks among passed and failed ones (unknown and exempt are left out).</summary>
    public static int? Score(IEnumerable<(string Severity, CheckStatus Status)> results)
    {
        int pass = 0, total = 0;
        foreach (var (sev, st) in results)
        {
            if (st is not (CheckStatus.Pass or CheckStatus.Fail)) continue;
            var w = Weight(sev);
            total += w;
            if (st == CheckStatus.Pass) pass += w;
        }
        return total == 0 ? null : (int)Math.Round(100.0 * pass / total);
    }
}

/// <summary>
/// The built-in Windows checks, aligned with CIS Controls v8 safeguards and common CIS Benchmark / Microsoft security
/// baseline settings. References name the CIS Controls safeguard.
/// </summary>
public static class SecurityRules
{
    private static string YesNo(bool v) => v ? "yes" : "no";
    private static string Days(double d) => d < 1 ? "today" : $"{Math.Floor(d):0} day(s) ago";

    public static readonly IReadOnlyList<SecurityRule> All =
    [
        // ---- Encryption ----------------------------------------------------------------------------
        new("SC-01", "System drive encrypted (BitLocker)", "Encryption", "high", "endpoint", "CIS Controls v8 3.6",
            "A lost or stolen laptop must not expose its data.",
            "Turn on BitLocker for the operating system drive (Intune › Endpoint security › Disk encryption, or Group Policy › Windows Components › BitLocker Drive Encryption) and escrow the recovery key in AD/Entra ID.",
            (f, _) => CheckResult.Bool(f.Doc.Security.DiskEncryption, "encrypted", "not encrypted")),

        new("SC-24", "Old TLS / SSL versions disabled for incoming connections", "Encryption", "medium", "server", "CIS Controls v8 3.10",
            "TLS 1.0, TLS 1.1 and SSL 3.0 have known weaknesses; services should accept TLS 1.2 or later only.",
            @"Set HKLM\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\<TLS 1.0|TLS 1.1|SSL 3.0>\Server Enabled=0 (via Group Policy Preferences or IIS Crypto), test applications, then restart.",
            (f, _) => f.H is null || f.H.Tls10Enabled is null ? CheckResult.Unknown()
                : f.H.Tls10Enabled == true || f.H.Tls11Enabled == true || f.H.Ssl3Enabled == true
                    ? CheckResult.Fail(string.Join(", ", new[] { f.H.Ssl3Enabled == true ? "SSL 3.0" : null, f.H.Tls10Enabled == true ? "TLS 1.0" : null, f.H.Tls11Enabled == true ? "TLS 1.1" : null }.Where(x => x is not null)) + " enabled")
                    : CheckResult.Pass("TLS 1.2+ only")),

        // ---- Network -------------------------------------------------------------------------------
        new("SC-02", "Firewall on for every network profile", "Network", "high", "all", "CIS Controls v8 4.4 / 4.5",
            "A host firewall limits what an attacker can reach from the network, including inside the company.",
            "Group Policy › Windows Defender Firewall with Advanced Security: set Firewall state to On for the Domain, Private and Public profiles.",
            (f, _) =>
            {
                var h = f.H;
                if (h?.FirewallDomain is null && h?.FirewallPrivate is null && h?.FirewallPublic is null)
                    return CheckResult.Bool(f.Doc.Security.FirewallEnabled, "on", "off for at least one profile");
                var off = new[] { h!.FirewallDomain == false ? "domain" : null, h.FirewallPrivate == false ? "private" : null, h.FirewallPublic == false ? "public" : null }.Where(x => x is not null).ToList();
                return off.Count == 0 ? CheckResult.Pass("on (domain, private, public)") : CheckResult.Fail($"off for {string.Join(", ", off)}");
            }),

        new("SC-15", "SMB version 1 disabled", "Network", "high", "all", "CIS Controls v8 4.8",
            "SMBv1 is obsolete and was used by WannaCry and NotPetya.",
            "Remove the feature: Disable-WindowsOptionalFeature -Online -FeatureName SMB1Protocol (or Group Policy: MS Security Guide › Configure SMB v1 server/client driver = Disabled).",
            (f, _) => CheckResult.Bool(f.H?.Smb1Enabled is { } on ? !on : null, "disabled", "enabled")),

        new("SC-16", "SMB signing required (server)", "Network", "medium", "server", "CIS Controls v8 4.1",
            "Required SMB signing prevents relay and tampering of file-share traffic.",
            "Group Policy › Security Options › Microsoft network server: Digitally sign communications (always) = Enabled.",
            (f, _) => CheckResult.Bool(f.H?.SmbSigningRequired, "required", "not required")),

        new("SC-19", "LLMNR disabled", "Network", "medium", "all", "CIS Controls v8 4.8",
            "Link-local name resolution lets an attacker on the network capture password hashes (Responder).",
            "Group Policy › Network › DNS Client › Turn off multicast name resolution = Enabled.",
            (f, _) => CheckResult.Bool(f.H?.LlmnrDisabled, "disabled", "enabled")),

        // ---- Remote access -------------------------------------------------------------------------
        new("SC-17", "Remote Desktop requires Network Level Authentication", "Remote access", "high", "all", "CIS Controls v8 4.1",
            "Without NLA the logon screen is exposed before authentication (BlueKeep-style attacks, brute force).",
            "Group Policy › Remote Desktop Session Host › Security › Require user authentication for remote connections by using NLA = Enabled.",
            (f, _) => f.H?.RdpEnabled is null ? CheckResult.Unknown() : f.H.RdpEnabled == false ? CheckResult.Pass("Remote Desktop off")
                : CheckResult.Bool(f.H.RdpNla, "NLA required" + (f.H.RdpPort is { } p and not 3389 ? $" (port {p})" : ""), "NLA not required")),

        new("SC-18", "Remote Desktop disabled on workstations", "Remote access", "low", "endpoint", "CIS Controls v8 4.8",
            "Workstations rarely need inbound Remote Desktop; every open service is attack surface.",
            "Group Policy › Remote Desktop Session Host › Connections › Allow users to connect remotely = Disabled (or limit it with the firewall).",
            (f, _) => CheckResult.Bool(f.H?.RdpEnabled is { } on ? !on : null, "off", "on"), DefaultOn: false),

        // ---- Accounts ------------------------------------------------------------------------------
        new("SC-11", "User Account Control on", "Accounts", "high", "all", "CIS Controls v8 5.4",
            "With UAC off every administrator process runs with full rights.",
            "Group Policy › Security Options › User Account Control: Run all administrators in Admin Approval Mode = Enabled; prompt for consent on the secure desktop.",
            (f, _) => f.H?.UacEnabled is null ? CheckResult.Unknown() : f.H.UacEnabled == false ? CheckResult.Fail("off")
                : f.H.UacAdminPrompt == 0 ? CheckResult.Fail("on, but administrators are never prompted") : CheckResult.Pass("on")),

        new("SC-12", "Guest account disabled", "Accounts", "high", "all", "CIS Controls v8 4.7",
            "The Guest account allows access without a password.",
            "Group Policy › Security Options › Accounts: Guest account status = Disabled.",
            (f, _) => CheckResult.Bool(f.H?.GuestEnabled is { } on ? !on : null, "disabled", "enabled")),

        new("SC-13", "Built-in Administrator account disabled", "Accounts", "medium", "endpoint", "CIS Controls v8 4.7",
            "The built-in Administrator has a well-known SID and is a common brute-force and pass-the-hash target; use named accounts and LAPS.",
            "Group Policy › Security Options › Accounts: Administrator account status = Disabled; manage local admin passwords with Windows LAPS.",
            (f, _) => CheckResult.Bool(f.H?.BuiltinAdminEnabled is { } on ? !on : null, "disabled", "enabled")),

        new("SC-14", "Few local administrators", "Accounts", "medium", "all", "CIS Controls v8 5.4",
            "Every local administrator account is a way to take over the device; keep the group small and managed.",
            "Remove personal accounts from the local Administrators group (Group Policy › Restricted Groups, or Intune › Account protection › Local user group membership).",
            (f, p) => f.Doc.Users.LocalAdmins.Count == 0 ? CheckResult.Unknown("local Administrators group not reported (domain controllers have none)")
                : f.Doc.Users.LocalAdmins.Count <= p["max"] ? CheckResult.Pass($"{f.Doc.Users.LocalAdmins.Count} member(s)")
                : CheckResult.Fail($"{f.Doc.Users.LocalAdmins.Count} members: {string.Join(", ", f.Doc.Users.LocalAdmins.Take(6))}{(f.Doc.Users.LocalAdmins.Count > 6 ? ", …" : "")}"),
            [new RuleParam("max", "Maximum members of the local Administrators group", 3, 1, 50)]),

        new("SC-26", "Screen locks after inactivity", "Accounts", "medium", "endpoint", "CIS Controls v8 4.3",
            "An unattended, unlocked workstation can be used by anyone passing by.",
            "Group Policy › Security Options › Interactive logon: Machine inactivity limit = 900 seconds or less (or Intune device restrictions › Password › Maximum minutes of inactivity).",
            (f, p) => f.H is null ? CheckResult.Unknown()
                : f.H.InactivityTimeoutSecs is { } s and > 0 ? (s <= p["maxSeconds"] ? CheckResult.Pass($"{s} s") : CheckResult.Fail($"{s} s"))
                : CheckResult.Fail("not configured"),
            [new RuleParam("maxSeconds", "Maximum seconds of inactivity", 900, 60, 3600)]),

        // ---- Credential protection -----------------------------------------------------------------
        new("SC-20", "WDigest does not keep passwords in memory", "Credential protection", "high", "all", "CIS Controls v8 4.1",
            "With WDigest UseLogonCredential = 1, clear-text passwords can be read from memory (Mimikatz).",
            @"Set HKLM\SYSTEM\CurrentControlSet\Control\SecurityProviders\WDigest UseLogonCredential = 0 (MS Security Guide › WDigest Authentication = Disabled).",
            (f, _) => CheckResult.Bool(f.H?.WDigestCleartext is { } on ? !on : null, "off", "clear-text credentials kept")),

        new("SC-21", "LSA protection on", "Credential protection", "medium", "all", "CIS Controls v8 4.1",
            "Running LSASS as a protected process blocks most credential-dumping tools.",
            @"Group Policy › System › Local Security Authority › Configure LSASS to run as a protected process = Enabled with UEFI lock (or RunAsPPL = 1).",
            (f, _) => CheckResult.Bool(f.H?.LsaProtection, "on", "off")),

        new("SC-22", "Only NTLMv2 accepted (no LM / NTLMv1)", "Credential protection", "medium", "all", "CIS Controls v8 4.1",
            "LM and NTLMv1 responses can be cracked quickly.",
            "Group Policy › Security Options › Network security: LAN Manager authentication level = Send NTLMv2 response only. Refuse LM & NTLM (level 5).",
            (f, p) => f.H?.LmCompatibilityLevel is { } l ? (l >= p["minLevel"] ? CheckResult.Pass($"level {l}") : CheckResult.Fail($"level {l}")) : CheckResult.Unknown(),
            [new RuleParam("minLevel", "Minimum LAN Manager authentication level", 5, 3, 5)]),

        new("SC-23", "LM password hashes not stored", "Credential protection", "medium", "all", "CIS Controls v8 4.1",
            "LM hashes are trivial to crack.",
            "Group Policy › Security Options › Network security: Do not store LAN Manager hash value on next password change = Enabled.",
            (f, _) => CheckResult.Bool(f.H?.NoLmHash, "not stored", "stored")),

        new("SC-30", "Credential Guard running", "Credential protection", "low", "endpoint", "CIS Controls v8 4.1",
            "Credential Guard isolates domain credentials in virtualisation-based security.",
            "Group Policy › System › Device Guard › Turn On Virtualization Based Security › Credential Guard Configuration = Enabled with UEFI lock (Enterprise/Education, hardware support needed).",
            (f, _) => CheckResult.Bool(f.H?.CredentialGuard, "running", "not running"), DefaultOn: false),

        // ---- Malware protection --------------------------------------------------------------------
        new("SC-03", "Antivirus active", "Malware protection", "high", "all", "CIS Controls v8 10.1",
            "Every Windows device needs active anti-malware protection.",
            "Turn on Microsoft Defender Antivirus or the company's endpoint protection; check that it is not disabled by policy.",
            (f, _) =>
            {
                var av = f.Doc.Security.Antivirus;
                if (av.Count == 0) return f.H?.DefenderRealtime is null ? CheckResult.Unknown("no antivirus reported") : CheckResult.Bool(f.H.DefenderRealtime, "Microsoft Defender", "Microsoft Defender off");
                var on = av.FirstOrDefault(a => a.Enabled == true);
                return on is not null ? CheckResult.Pass(on.Name) : av.All(a => a.Enabled is null) ? CheckResult.Unknown() : CheckResult.Fail($"{string.Join(", ", av.Select(a => a.Name))} not active");
            }),

        new("SC-04", "Antivirus signatures up to date", "Malware protection", "medium", "all", "CIS Controls v8 10.2",
            "Out-of-date signatures miss current malware.",
            "Allow the antivirus to update automatically (Windows Update / WSUS / Intune) and check that the device reaches the update source.",
            (f, p) => f.H?.DefenderSignatureAgeDays is { } age
                ? (age <= p["maxDays"] ? CheckResult.Pass(age == 0 ? "today" : $"{age} day(s) old") : CheckResult.Fail($"{age} day(s) old"))
                : f.Doc.Security.Antivirus.Count > 0 && f.Doc.Security.Antivirus.All(a => a.UpToDate is not null)
                    ? CheckResult.Bool(f.Doc.Security.Antivirus.Any(a => a.Enabled == true && a.UpToDate == true), "up to date", "out of date")
                    : CheckResult.Unknown(),
            [new RuleParam("maxDays", "Maximum signature age (days)", 3, 1, 30)]),

        new("SC-05", "Defender tamper protection on", "Malware protection", "medium", "all", "CIS Controls v8 10.1",
            "Tamper protection stops malware and users from turning Defender off.",
            "Microsoft Defender portal or Intune › Endpoint security › Antivirus › Tamper protection = On.",
            (f, _) => f.H?.DefenderRealtime is null ? CheckResult.NotApplicable : CheckResult.Bool(f.H.TamperProtection, "on", "off")),

        // ---- Patching ------------------------------------------------------------------------------
        new("SC-06", "Operating system still receives security updates", "Patching", "high", "all", "CIS Controls v8 2.2",
            "Windows versions past their end of servicing get no more security fixes.",
            "Upgrade to a supported feature update or Windows version (or enrol in Extended Security Updates and record an exception).",
            (f, _) => WindowsLifecycle.EndOfServicing(f.Doc.Os) is { } end
                ? (end > f.Now ? CheckResult.Pass($"supported until {end:yyyy-MM-dd}") : CheckResult.Fail($"end of servicing {end:yyyy-MM-dd}"))
                : CheckResult.Unknown("Windows version not in Zentriq's lifecycle table")),

        new("SC-07", "Security updates installed recently", "Patching", "high", "all", "CIS Controls v8 7.3",
            "Monthly cumulative updates fix actively exploited vulnerabilities; a device that has not installed one for weeks is exposed.",
            "Check Windows Update / WSUS / Intune update rings for this device; install the latest cumulative update and restart.",
            (f, p) =>
            {
                var last = f.Doc.Patches.Where(x => x.InstalledOn is not null).Select(x => x.InstalledOn!.Value).DefaultIfEmpty().Max();
                if (last == default) return CheckResult.Unknown("no update history reported");
                var age = (f.Now - last).TotalDays;
                return age <= p["maxDays"] ? CheckResult.Pass($"last update {Days(age)}") : CheckResult.Fail($"last update {Days(age)} ({last:yyyy-MM-dd})");
            },
            [new RuleParam("maxDays", "Maximum days since the last installed update", 35, 7, 180)]),

        new("SC-08", "Automatic updates not turned off by policy", "Patching", "medium", "all", "CIS Controls v8 7.3",
            "NoAutoUpdate = 1 stops Windows Update from installing fixes unless someone does it by hand.",
            "Group Policy › Windows Update › Configure Automatic Updates = Enabled (or manage updates with WSUS / Intune update rings).",
            (f, _) => CheckResult.Bool(f.H?.AutoUpdateDisabled is { } off ? !off : null, "allowed", "turned off by policy")),

        // ---- Hardening -----------------------------------------------------------------------------
        new("SC-09", "Secure Boot on", "Hardening", "medium", "all", "CIS Controls v8 4.1",
            "Secure Boot blocks boot-level malware (bootkits).",
            "Enable UEFI Secure Boot in the firmware (Generation 2 VMs: enable it in the VM settings).",
            (f, _) => CheckResult.Bool(f.Doc.Security.SecureBoot, "on", "off")),

        new("SC-10", "TPM present", "Hardening", "low", "endpoint", "CIS Controls v8 4.1",
            "A TPM protects BitLocker keys, Windows Hello and credentials.",
            "Enable the TPM in the firmware, or replace hardware without one at the next refresh.",
            (f, _) => f.IsVirtual ? CheckResult.NotApplicable : CheckResult.Bool(f.Doc.Hardware.TpmPresent, $"present{(f.Doc.Hardware.TpmVersion is { } v ? $" ({v})" : "")}", "none")),

        new("SC-25", "AutoRun disabled", "Hardening", "medium", "all", "CIS Controls v8 10.3",
            "AutoRun starts programs from removable media without the user asking.",
            "Group Policy › Windows Components › AutoPlay Policies › Turn off AutoPlay = Enabled (All drives).",
            (f, _) => CheckResult.Bool(f.H?.AutorunDisabled, "disabled", "enabled")),

        new("SC-28", "Print Spooler stopped on servers", "Hardening", "medium", "server", "CIS Controls v8 4.8",
            "The Print Spooler has had critical remote-code-execution flaws (PrintNightmare); servers that are not print servers do not need it.",
            "Stop and disable the Print Spooler service on servers that do not share printers (especially domain controllers); record an exception for print servers.",
            (f, _) => Service(f, "Spooler") is { } s ? (IsOff(s) ? CheckResult.Pass($"{s.State}, {s.StartMode}") : CheckResult.Fail($"{s.State}, {s.StartMode}")) : CheckResult.Unknown("service list not reported")),

        new("SC-29", "Remote Registry service disabled", "Hardening", "low", "all", "CIS Controls v8 4.8",
            "Remote Registry lets anyone with credentials read and change the registry over the network.",
            "Set the Remote Registry service to Disabled (Group Policy › System Services). Note: agentless inventory over WinRM does not need it.",
            (f, _) => Service(f, "RemoteRegistry") is { } s ? (IsOff(s) ? CheckResult.Pass($"{s.State}, {s.StartMode}") : CheckResult.Fail($"{s.State}, {s.StartMode}")) : CheckResult.Unknown("service list not reported")),

        // ---- Logging -------------------------------------------------------------------------------
        new("SC-27", "PowerShell script block logging on", "Logging", "low", "all", "CIS Controls v8 8.8",
            "Script block logging records the PowerShell code that runs, which is essential for investigating attacks.",
            "Group Policy › Windows Components › Windows PowerShell › Turn on PowerShell Script Block Logging = Enabled.",
            (f, _) => CheckResult.Bool(f.H?.ScriptBlockLogging, "on", "off")),

        // ---- Linux ---------------------------------------------------------------------------------
        new("SL-01", "SSH: root cannot sign in", "Linux: remote access", "high", "all", "CIS Controls v8 5.4",
            "Signing in as root over SSH skips individual accountability and is the first target of password guessing.",
            "In /etc/ssh/sshd_config (or a file in sshd_config.d) set PermitRootLogin no, then reload sshd; administrators sign in with their own account and use sudo.",
            (f, _) => f.L?.SshServer == false ? CheckResult.Pass("no SSH server")
                : f.L?.SshRootLogin is { } r ? (r == "no" ? CheckResult.Pass("PermitRootLogin no") : CheckResult.Fail($"PermitRootLogin {r}")) : CheckResult.Unknown(),
            Platform: "linux"),

        new("SL-02", "SSH: password sign-in turned off", "Linux: remote access", "medium", "all", "CIS Controls v8 6.3",
            "SSH keys (or certificates) cannot be guessed; passwords exposed to the network can.",
            "Set PasswordAuthentication no (and KbdInteractiveAuthentication no) in sshd_config after the users have SSH keys, then reload sshd.",
            (f, _) => f.L?.SshServer == false ? CheckResult.Pass("no SSH server") : CheckResult.Bool(f.L?.SshPasswordAuth is { } pa ? !pa : null, "keys only", "passwords allowed"),
            Platform: "linux"),

        new("SL-03", "Host firewall active", "Linux: hardening", "high", "all", "CIS Controls v8 4.4",
            "A host firewall limits what an attacker on the network can reach, even when a service is exposed by mistake.",
            "Turn on ufw (Ubuntu / Debian: ufw allow OpenSSH; ufw enable) or firewalld (RHEL family / SUSE: systemctl enable --now firewalld) and allow only the services the server provides.",
            (f, _) => f.Doc.Security.FirewallEnabled is { } on ? (on ? CheckResult.Pass(f.L?.Firewall ?? "on") : CheckResult.Fail("no active firewall")) : CheckResult.Unknown("needs the agent or an SSH account with sudo"),
            Platform: "linux"),

        new("SL-04", "SELinux or AppArmor enforcing", "Linux: hardening", "medium", "all", "CIS Controls v8 4.1",
            "Mandatory access control confines services, so a compromised one cannot read or change the rest of the system.",
            "RHEL family: SELINUX=enforcing in /etc/selinux/config (relabel and reboot). Ubuntu / Debian / SUSE: keep AppArmor enabled (aa-status) with the profiles in enforce mode.",
            (f, _) => f.L is null ? CheckResult.Unknown()
                : f.L.Selinux == "enforcing" ? CheckResult.Pass("SELinux enforcing")
                : f.L.AppArmor == true ? CheckResult.Pass("AppArmor")
                : f.L.Selinux is null && f.L.AppArmor is null ? CheckResult.Unknown()
                : CheckResult.Fail(f.L.Selinux is { } se ? $"SELinux {se}" : "AppArmor off"),
            Platform: "linux"),

        new("SL-05", "Automatic security updates", "Linux: patching", "medium", "all", "CIS Controls v8 7.3",
            "Security fixes that install themselves close vulnerabilities without waiting for someone to log in.",
            "Ubuntu / Debian: install unattended-upgrades (dpkg-reconfigure -plow unattended-upgrades). RHEL family: dnf install dnf-automatic; systemctl enable --now dnf-automatic-install.timer. SUSE: enable automatic online update (yast2 online_update_configuration).",
            (f, _) => CheckResult.Bool(f.L?.AutoUpdates, "on", "off"),
            Platform: "linux"),

        new("SL-06", "No pending security updates", "Linux: patching", "high", "all", "CIS Controls v8 7.3",
            "Published security updates that are not installed leave known vulnerabilities open.",
            "Install the updates: apt-get update && apt-get upgrade (Ubuntu / Debian), dnf upgrade --security (RHEL family), zypper patch --category security (SUSE); reboot if a new kernel was installed.",
            (f, p) => f.L?.PendingSecurityUpdates is { } n
                ? (n <= p["maxPending"] ? CheckResult.Pass(n == 0 ? "none" : $"{n} pending") : CheckResult.Fail($"{n} security update(s) pending{(f.L.PendingUpdates is { } all ? $" ({all} updates in total)" : "")}"))
                : CheckResult.Unknown("the package manager did not tell which updates are security updates"),
            [new RuleParam("maxPending", "Pending security updates allowed", 0, 0, 50)], Platform: "linux"),

        new("SL-07", "No reboot pending after updates", "Linux: patching", "low", "all", "CIS Controls v8 7.3",
            "A new kernel or library is used only after a restart; until then the old, vulnerable one keeps running.",
            "Restart the device in its next maintenance window (or use live patching where the distribution offers it).",
            (f, _) => CheckResult.Bool(f.L?.RebootRequired is { } rb ? !rb : null, "not needed", "reboot required"),
            Platform: "linux"),

        new("SL-08", "Only root has UID 0", "Linux: accounts", "high", "all", "CIS Controls v8 5.4",
            "Another account with UID 0 is a second, often hidden, root account.",
            "Remove the extra account, or give it its own UID (usermod -u); review how it was created.",
            (f, _) => f.L?.ExtraUid0 is { } x ? (x.Count == 0 ? CheckResult.Pass("root only") : CheckResult.Fail(string.Join(", ", x))) : CheckResult.Unknown(),
            Platform: "linux"),

        new("SL-09", "Password maximum age set", "Linux: accounts", "low", "all", "CIS Controls v8 5.2",
            "Without a maximum age a leaked local password stays valid indefinitely.",
            "Set PASS_MAX_DAYS in /etc/login.defs (and chage -M for existing users), or manage local accounts centrally.",
            (f, p) => f.L?.PassMaxDays is { } d ? (d > 0 && d <= p["maxDays"] ? CheckResult.Pass($"{d} days") : CheckResult.Fail(d >= 99999 ? "never expires" : $"{d} days")) : CheckResult.Unknown(),
            [new RuleParam("maxDays", "Maximum password age (days)", 365, 30, 3650)], Platform: "linux", DefaultOn: false),

        new("SL-10", "No Telnet server", "Linux: remote access", "high", "all", "CIS Controls v8 4.8",
            "Telnet sends passwords and sessions in clear text.",
            "Remove the telnet server package (telnetd / telnet-server / inetutils-telnetd); use SSH.",
            (f, _) => CheckResult.Bool(f.L?.TelnetServer is { } t ? !t : null, "not installed", "installed"),
            Platform: "linux"),

        new("SL-11", "Time synchronised", "Linux: logging", "low", "all", "CIS Controls v8 8.4",
            "Logs from different systems can only be correlated when their clocks agree; Kerberos and TLS need correct time too.",
            "Enable chrony or systemd-timesyncd (timedatectl set-ntp true) with the company's time servers.",
            (f, _) => CheckResult.Bool(f.L?.TimeSync, "synchronised", "not synchronised"),
            Platform: "linux"),

        new("SL-12", "Audit daemon running on servers", "Linux: logging", "low", "server", "CIS Controls v8 8.2",
            "auditd records security-relevant events (logins, privilege use, changes to key files) for investigations.",
            "Install and enable auditd (apt install auditd / dnf install audit; systemctl enable --now auditd) with the distribution's or CIS audit rules.",
            (f, _) => CheckResult.Bool(f.L?.Auditd, "running", "not running"),
            Platform: "linux"),

        new("SL-13", "Disk encrypted (laptops and desktops)", "Linux: hardening", "medium", "endpoint", "CIS Controls v8 3.6",
            "A lost or stolen laptop with an unencrypted disk gives away all its data.",
            "Install with LUKS full-disk encryption (re-installation is usually needed for the system disk); encrypt data disks with cryptsetup.",
            (f, _) => CheckResult.Bool(f.Doc.Security.DiskEncryption, "encrypted", "not encrypted"),
            Platform: "linux"),
    ];

    public static SecurityRule? Find(string id) => All.FirstOrDefault(r => r.Id == id);

    private static ServiceItem? Service(DeviceFacts f, string name) =>
        f.Doc.Services.Count == 0 ? null : f.Doc.Services.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) ?? new ServiceItem { Name = name, State = "Not installed", StartMode = "-" };

    private static bool IsOff(ServiceItem s) => !string.Equals(s.State, "Running", StringComparison.OrdinalIgnoreCase) && !string.Equals(s.StartMode, "Auto", StringComparison.OrdinalIgnoreCase);
}

/// <summary>End of security servicing for Windows versions (Microsoft lifecycle). Enterprise/Education get longer support for client feature updates.</summary>
public static class WindowsLifecycle
{
    public static DateTimeOffset? EndOfServicing(OsInfo os)
    {
        if (!Version.TryParse(os.Version, out var v)) return null;
        var edition = os.EditionId ?? "";
        var ltsc = edition.Contains("EnterpriseS", StringComparison.OrdinalIgnoreCase) || edition.Contains("IoTEnterpriseS", StringComparison.OrdinalIgnoreCase)
                   || (os.Name ?? "").Contains("LTSC", StringComparison.OrdinalIgnoreCase) || (os.Name ?? "").Contains("LTSB", StringComparison.OrdinalIgnoreCase);
        var enterprise = edition.StartsWith("Enterprise", StringComparison.OrdinalIgnoreCase) || edition.StartsWith("Education", StringComparison.OrdinalIgnoreCase)
                         || (os.Name ?? "").Contains("Enterprise", StringComparison.OrdinalIgnoreCase) || (os.Name ?? "").Contains("Education", StringComparison.OrdinalIgnoreCase);
        var server = os.IsServer == true || (os.Name ?? "").Contains("Server", StringComparison.OrdinalIgnoreCase);
        static DateTimeOffset D(int y, int m, int d) => new(y, m, d, 0, 0, 0, TimeSpan.Zero);
        if (v.Major == 6)
            return (v.Minor, v.Build) switch
            {
                (1, 7601) => D(2020, 1, 14),
                (2, 9200) => D(2023, 10, 10),
                (3, 9600) => server ? D(2023, 10, 10) : D(2023, 1, 10),
                _ => null,
            };
        if (v.Major != 10) return null;
        if (server)
            return v.Build switch
            {
                14393 => D(2027, 1, 12),
                17763 => D(2029, 1, 9),
                20348 => D(2031, 10, 14),
                25398 => D(2025, 10, 24),
                26100 => D(2034, 11, 4),
                _ => null,
            };
        return v.Build switch
        {
            14393 => ltsc ? D(2026, 10, 13) : D(2018, 4, 10),
            17763 => ltsc ? D(2029, 1, 9) : D(2021, 5, 11),
            19044 => ltsc ? D(2027, 1, 12) : enterprise ? D(2024, 6, 11) : D(2023, 6, 13),
            19045 => D(2025, 10, 14),
            22000 => enterprise ? D(2024, 10, 8) : D(2023, 10, 10),
            22621 => enterprise ? D(2025, 10, 14) : D(2024, 10, 8),
            22631 => enterprise ? D(2026, 11, 10) : D(2025, 11, 11),
            26100 => ltsc ? D(2034, 10, 10) : enterprise ? D(2027, 10, 12) : D(2026, 10, 13),
            26200 => enterprise ? D(2028, 10, 10) : D(2027, 10, 12),
            _ => v.Build > 26200 ? D(2099, 1, 1) : null,
        };
    }
}

/// <summary>Per-rule settings in a baseline: on/off, severity override and parameter values.</summary>
public sealed class BaselineRule
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("severity")] public string? Severity { get; set; }
    [JsonPropertyName("params")] public Dictionary<string, int>? Params { get; set; }
}

public sealed class BaselineDefinition
{
    [JsonPropertyName("rules")] public List<BaselineRule> Rules { get; set; } = [];
    /// <summary>Device categories in scope (server, endpoint); empty = all Windows devices.</summary>
    [JsonPropertyName("categories")] public List<string> Categories { get; set; } = [];

    /// <summary>The rules in effect: every catalogue rule, with this baseline's settings applied (new rules use their default).</summary>
    public IEnumerable<(SecurityRule Rule, string Severity, IReadOnlyDictionary<string, int> Params)> Effective()
    {
        foreach (var rule in SecurityRules.All)
        {
            var b = Rules.FirstOrDefault(x => x.Id == rule.Id);
            if (!(b?.Enabled ?? rule.DefaultOn)) continue;
            var sev = b?.Severity is "high" or "medium" or "low" ? b.Severity : rule.Severity;
            yield return (rule, sev, rule.Resolve(b?.Params));
        }
    }

    public bool InScope(string category) => Categories.Count == 0 || Categories.Contains(category == "server" ? "server" : "endpoint");

    public static BaselineDefinition Default() => new() { Rules = SecurityRules.All.Select(r => new BaselineRule { Id = r.Id, Enabled = r.DefaultOn }).ToList() };
}

internal static class SecurityText
{
    public static string Status(CheckStatus s) => s switch { CheckStatus.Pass => "pass", CheckStatus.Fail => "fail", CheckStatus.Unknown => "unknown", _ => "n/a" };
    public static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);
}
