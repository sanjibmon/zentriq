using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Zentriq.Inventory;

namespace Zentriq.Agent;

/// <summary>
/// Watches for installers started by signed-in users (Windows Installer packages, setup programs and per-user
/// installers) while application control is in audit or block mode. A new installer is suspended at once, identified
/// (product name, publisher, code-signing certificate, version, SHA-256) and checked against the approved list:
/// approved software continues; otherwise, in audit mode it continues and is reported, in block mode it is stopped,
/// reported, and the user sees the organisation's message with "Request approval".
/// <para>Not checked: installations started by services (session 0: Windows Update, Configuration Manager, Intune,
/// Zentriq deployments), programs that installers start themselves once allowed, uninstallers, and Windows
/// components. Software installed in another way is caught by the inventory check on the server.</para>
/// </summary>
internal static class InstallerWatch
{
    private static ManagementEventWatcher? _watcher;
    private static readonly object Gate = new object();
    /// <summary>Processes allowed recently (and their children): an installer's own helper processes are not checked again.</summary>
    private static readonly Dictionary<int, DateTime> Allowed = new Dictionary<int, DateTime>();
    private static readonly int Self = Process.GetCurrentProcess().Id;
    /// <summary>
    /// Processes of a blocked installation: the installer, what it started, and the program that launched it when that
    /// is not the user's shell (a downloader or bootstrapper that tries again). Installers they start are stopped
    /// without a new message: the user sees one message per attempt.
    /// </summary>
    private static readonly Dictionary<int, DateTime> Blocked = new Dictionary<int, DateTime>();
    /// <summary>Programs through which a user starts an installer: a start from these is a new attempt.</summary>
    private static readonly HashSet<string> UserShells = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "WindowsTerminal.exe", "OpenConsole.exe", "msedge.exe", "chrome.exe", "firefox.exe",
        "brave.exe", "opera.exe", "vivaldi.exe", "iexplore.exe", "outlook.exe", "7zFM.exe", "WinRAR.exe", "TOTALCMD64.EXE", "TOTALCMD.EXE", "Everything.exe",
    };
    /// <summary>Programs that start installers on their own (scheduled tasks, run-once entries, services).</summary>
    private static readonly HashSet<string> AutoStarters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "svchost.exe", "taskhostw.exe", "taskeng.exe", "services.exe", "WmiPrvSE.exe", "runonce.exe", "rundll32.exe", "userinit.exe", "sihost.exe",
    };

    public static bool Running { get { lock (Gate) return _watcher is not null; } }

    public static void Start()
    {
        lock (Gate)
        {
            if (_watcher is not null) return;
            try
            {
                var w = new ManagementEventWatcher(new ManagementScope(@"\\.\root\cimv2"), new EventQuery("SELECT ProcessID, ParentProcessID, ProcessName, SessionID FROM Win32_ProcessStartTrace"));
                w.EventArrived += (_, e) =>
                {
                    try
                    {
                        var pid = Convert.ToInt32(e.NewEvent["ProcessID"]);
                        var ppid = Convert.ToInt32(e.NewEvent["ParentProcessID"]);
                        var name = e.NewEvent["ProcessName"] as string ?? "";
                        var session = Convert.ToInt32(e.NewEvent["SessionID"]);
                        if (!Interesting(name, session, ppid, pid)) return;
                        ThreadPool.QueueUserWorkItem(_ => Inspect(pid, ppid, name, session));
                    }
                    catch (Exception ex) { Log.Warn("Installer watch: " + ex.Message); }
                };
                w.Start();
                _watcher = w;
                Log.Info("Application control: watching for installations (" + AppControlAgent.Policy.Mode + " mode).");
            }
            catch (Exception ex) { Log.Error("Application control: the installer watch could not start", ex); }
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            if (_watcher is null) return;
            try { _watcher.Stop(); _watcher.Dispose(); } catch (Exception) { }
            _watcher = null;
            Log.Info("Application control is off: installations are not checked.");
        }
    }

    private static readonly HashSet<string> SystemNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "svchost.exe", "conhost.exe", "dllhost.exe", "backgroundTaskHost.exe", "RuntimeBroker.exe", "SearchProtocolHost.exe", "SearchFilterHost.exe",
        "WmiPrvSE.exe", "taskhostw.exe", "sihost.exe", "ctfmon.exe", "explorer.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "WerFault.exe",
        "smartscreen.exe", "consent.exe", "audiodg.exe", "fontdrvhost.exe", "ZentriqAgent.exe", "TiWorker.exe", "TrustedInstaller.exe", "MoUsoCoreWorker.exe",
        "wuauclt.exe", "UsoClient.exe", "msedge.exe", "chrome.exe", "firefox.exe", "Teams.exe", "ms-teams.exe", "OneDrive.exe", "SecurityHealthSystray.exe",
    };

    /// <summary>A quick first filter on the start event (most processes are not installers).</summary>
    private static bool Interesting(string name, int session, int ppid, int pid)
    {
        if (session == 0) return false;   // services: Windows Update, management tools, Zentriq deployments
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || SystemNames.Contains(name)) return false;
        lock (Gate)
        {
            foreach (var old in Allowed.Where(kv => DateTime.UtcNow - kv.Value > TimeSpan.FromMinutes(30)).Select(kv => kv.Key).ToList()) Allowed.Remove(old);
            if (ppid == Self || Allowed.ContainsKey(ppid)) { Allowed[pid] = DateTime.UtcNow; return false; }
        }
        return true;
    }

    private static readonly Regex InstallerName = new Regex(@"(?i)(setup|install|_inst\b|instal+er|\binst\b)", RegexOptions.CultureInvariant);
    private static readonly Regex UninstallerName = new Regex(@"(?i)(unins|uninst|uninstall|remove|deinstall)", RegexOptions.CultureInvariant);
    private static readonly string[] Markers = { "Nullsoft.NSIS", "NullsoftInst", "Inno Setup", ".wixburn", "InstallShield", "Squirrel", "Advanced Installer", "Setup Factory", "WiseMain", "7zSfx", "InstallAware" };

    private static void Inspect(int pid, int ppid, string name, int session)
    {
        IntPtr h = IntPtr.Zero;
        var suspended = false;
        try
        {
            h = OpenProcess(PROCESS_SUSPEND_RESUME | PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return;   // already ended
            var image = ImagePath(h);
            if (image is null) return;
            var isMsiexec = name.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase);
            var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!isMsiexec && image.StartsWith(windir + "\\", StringComparison.OrdinalIgnoreCase)) return;
            if (image.StartsWith(TamperGuard.InstallFolder + "\\", StringComparison.OrdinalIgnoreCase)) return;

            var candidate = isMsiexec ? FromMsiexec(pid, image) : FromExecutable(image);
            if (candidate is null) return;   // not an installer (or an uninstaller)
            // An installer: hold it while it is checked.
            suspended = NtSuspendProcess(h) == 0;
            if (candidate.Path is { } file && Path.IsPathRooted(file) && File.Exists(file)) Sign(candidate, file);
            var policy = AppControlAgent.Policy;
            if (policy.Mode == "off") return;
            var d = AppControlAgent.Evaluate(candidate);
            var user = SessionUser(session);
            if (d.Approved)
            {
                lock (Gate) Allowed[pid] = DateTime.UtcNow;
                Log.Info($"Application control: {candidate} allowed ({d.Reason}).");
                return;
            }
            if (policy.Mode == "audit")
            {
                lock (Gate) Allowed[pid] = DateTime.UtcNow;
                Describe(candidate, isMsiexec, pid, ParentImage(ppid));
                Log.Warn($"Application control (audit): {user ?? "a user"} installs {candidate}, which is not approved ({d.Reason}).");
                AppControlAgent.AddEvent("audited", candidate, user, d.Reason + " Audit mode: the installation was allowed.");
                return;
            }
            // Block: stop the installer (and what it already started), report it and tell the user - once per attempt.
            var parent = ParentImage(ppid);
            Describe(candidate, isMsiexec, pid, parent);
            bool again;
            lock (Gate)
            {
                foreach (var old in Blocked.Where(kv => DateTime.UtcNow - kv.Value > TimeSpan.FromMinutes(30)).Select(kv => kv.Key).ToList()) Blocked.Remove(old);
                again = Blocked.ContainsKey(ppid);
                Blocked[pid] = DateTime.UtcNow;
                var parentName = parent is null ? null : Path.GetFileName(parent);
                // A launcher that is not the user's shell (a downloader, a bootstrapper, an updater) belongs to this attempt.
                if (!again && ppid > 4 && (parentName is null || !UserShells.Contains(parentName)) && !AutoStarters.Contains(parentName ?? "")) Blocked[ppid] = DateTime.UtcNow;
                // The message comes back only when a person starts the software again (from Explorer, a command prompt, a
                // browser, …). Retries by the software itself, an updater, a scheduled task or a service do not show it again
                // for 4 hours; a double start by the person (within 5 seconds) shows it once.
                var byPerson = parentName is not null && UserShells.Contains(parentName);
                if (!again && Notice.ShownRecently(session, candidate, byPerson ? TimeSpan.FromSeconds(5) : TimeSpan.FromHours(4))) again = true;
                if (!again && Notice.IsOpen(session, candidate)) again = true;   // the message for this software is still on the screen
            }
            KillTree(pid, h, Blocked);
            suspended = false;
            Log.Warn($"Application control: blocked {candidate} started by {user ?? "a user"} ({d.Reason})" + (again ? "; the same attempt, no new message." : "."));
            AppControlAgent.AddEvent("blocked", candidate, user, d.Reason);
            if (!again) Notice.Show(session, candidate, policy, user);
        }
        catch (Exception ex) { Log.Warn($"Installer watch ({name}, {pid}): {ex.Message}"); }
        finally
        {
            if (suspended && h != IntPtr.Zero) NtResumeProcess(h);
            if (h != IntPtr.Zero) CloseHandle(h);
        }
    }

    // ---- identifying the installer ---------------------------------------------------------------------------

    /// <summary>msiexec /i or /package &lt;file.msi&gt; (not /x uninstall, not the Windows Installer service).</summary>
    private static SoftwareCandidate? FromMsiexec(int pid, string image)
    {
        var cmd = CommandLine(pid) ?? "";
        if (Regex.IsMatch(cmd, @"(?i)(^|\s)[/-](x|uninstall)\b") || Regex.IsMatch(cmd, @"(?i)(^|\s)[/-](v|embedding|unregister|regserver)\b")) return null;
        var m = Regex.Match(cmd, "(?i)(?:^|\\s)[/-](?:i|package|a|j[mu]?)\\s*(\"[^\"]+\"|\\S+)");
        if (!m.Success) return null;
        var path = m.Groups[1].Value.Trim('"');
        if (!Path.IsPathRooted(path))
        {
            var rel = new SoftwareCandidate { Name = Path.GetFileNameWithoutExtension(path), Path = path, Platform = "windows" };
            if (AppControl.InstallerBaseName(path) is { } rb) rel.OtherNames.Add(rb);
            return rel;
        }
        if (!File.Exists(path)) return null;
        var c = new SoftwareCandidate
        {
            Name = MsiProperty(path, "ProductName") ?? Path.GetFileNameWithoutExtension(path),
            Publisher = MsiProperty(path, "Manufacturer"),
            Version = MsiProperty(path, "ProductVersion"),
            Path = path, Platform = "windows",
        };
        if (AppControl.InstallerBaseName(path) is { } b) c.OtherNames.Add(b);
        return c;
    }

    private static SoftwareCandidate? FromExecutable(string image)
    {
        var fileName = Path.GetFileName(image);
        if (UninstallerName.IsMatch(fileName)) return null;
        FileVersionInfo? vi = null;
        try { vi = FileVersionInfo.GetVersionInfo(image); } catch (Exception) { }
        var described = string.Join(" ", new[] { vi?.FileDescription, vi?.ProductName, vi?.OriginalFilename, vi?.InternalName }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (UninstallerName.IsMatch(vi?.FileDescription ?? "") || UninstallerName.IsMatch(vi?.OriginalFilename ?? "")) return null;
        // Installed programs (Program Files) are only installers by name; elsewhere (downloads, temporary folders, user
        // profiles, shares) also by the installer framework found in the file.
        var installed = image.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + "\\", StringComparison.OrdinalIgnoreCase)
                        || image.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) + "\\", StringComparison.OrdinalIgnoreCase);
        var isInstaller = InstallerName.IsMatch(fileName) || InstallerName.IsMatch(described) || (!installed && HasInstallerMarker(image))
            || (fileName.Equals("Update.exe", StringComparison.OrdinalIgnoreCase) && image.IndexOf("\\AppData\\", StringComparison.OrdinalIgnoreCase) >= 0);
        if (!isInstaller) return null;
        var product = Clean(vi?.ProductName);
        var description = Clean(vi?.FileDescription);
        var c = new SoftwareCandidate
        {
            Name = product ?? description ?? AppControl.InstallerBaseName(fileName) ?? Path.GetFileNameWithoutExtension(fileName),
            Publisher = Clean(vi?.CompanyName),
            Version = Clean(vi?.ProductVersion) ?? Clean(vi?.FileVersion),
            Path = image, Platform = "windows",
        };
        foreach (var n in new[] { description, StripInstaller(product), StripInstaller(description), AppControl.InstallerBaseName(fileName), AppControl.InstallerBaseName(vi?.OriginalFilename) })
            if (!string.IsNullOrWhiteSpace(n) && !c.OtherNames.Contains(n!) && n != c.Name) c.OtherNames.Add(n!);
        return c;
    }

    /// <summary>"Google Chrome Installer" → "Google Chrome"; "Notepad++ Setup" → "Notepad++".</summary>
    private static string? StripInstaller(string? s) =>
        s is null ? null : Regex.Replace(s, @"(?i)\s*(web\s+)?(setup|installer|install|installation)(\s+program)?\s*$", "").Trim() is { Length: > 1 } t && t != s ? t : null;

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s!.Trim().Trim('\0').Trim();

    private static bool HasInstallerMarker(string path) => MarkerOf(path) is not null;

    private static readonly Dictionary<string, string> MarkerNames = new Dictionary<string, string>
    {
        ["Nullsoft.NSIS"] = "NSIS", ["NullsoftInst"] = "NSIS", ["Inno Setup"] = "Inno Setup", [".wixburn"] = "WiX bundle", ["InstallShield"] = "InstallShield",
        ["Squirrel"] = "Squirrel", ["Advanced Installer"] = "Advanced Installer", ["Setup Factory"] = "Setup Factory", ["WiseMain"] = "Wise", ["7zSfx"] = "7-Zip self-extracting",
        ["InstallAware"] = "InstallAware",
    };

    private static string? MarkerOf(string path)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var len = (int)Math.Min(f.Length, 4 * 1024 * 1024);
            var buf = new byte[len];
            var read = 0;
            while (read < len) { var n = f.Read(buf, read, len - read); if (n <= 0) break; read += n; }
            var text = Encoding.ASCII.GetString(buf, 0, read);
            var m = Markers.FirstOrDefault(x => text.IndexOf(x, StringComparison.Ordinal) >= 0);
            return m is null ? null : MarkerNames[m];
        }
        catch (Exception) { return null; }
    }

    private static string? ParentImage(int ppid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, ppid);
        if (h == IntPtr.Zero) return null;
        try { return ImagePath(h); } finally { CloseHandle(h); }
    }

    /// <summary>Collects everything about the installer for the console (events and approval requests).</summary>
    private static void Describe(SoftwareCandidate c, bool isMsi, int pid, string? parent)
    {
        var d = c.Details;
        void Add(string key, string? value) { if (!string.IsNullOrWhiteSpace(value) && !d.ContainsKey(key)) d[key] = value!.Trim().Trim('\0'); }
        try
        {
            var file = c.Path;
            var exists = file is not null && Path.IsPathRooted(file) && File.Exists(file);
            Add("Product", c.Name);
            Add("Version", c.Version);
            Add("Publisher", c.Publisher);
            if (exists)
            {
                var fi = new FileInfo(file!);
                Add("File name", fi.Name);
                Add("Folder", fi.DirectoryName);
                Add("File size", fi.Length >= 1048576 ? $"{fi.Length / 1048576.0:0.0} MB ({fi.Length:N0} bytes)" : $"{fi.Length / 1024.0:0.0} KB ({fi.Length:N0} bytes)");
                Add("File date", fi.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm") + " UTC");
            }
            Add("SHA-256", c.Sha256);
            if (isMsi && exists)
            {
                Add("Installer type", "Windows Installer package (.msi)");
                Add("Product code", MsiProperty(file!, "ProductCode"));
                Add("Upgrade code", MsiProperty(file!, "UpgradeCode"));
                Add("Manufacturer", MsiProperty(file!, "Manufacturer"));
            }
            else if (exists)
            {
                Add("Installer type", MarkerOf(file!) is { } m ? m + " setup program" : "Setup program (.exe)");
                try
                {
                    var vi = FileVersionInfo.GetVersionInfo(file!);
                    Add("Product name", vi.ProductName);
                    Add("File description", vi.FileDescription);
                    Add("Company", vi.CompanyName);
                    Add("Product version", vi.ProductVersion);
                    Add("File version", vi.FileVersion);
                    Add("Original file name", vi.OriginalFilename);
                    Add("Copyright", vi.LegalCopyright);
                }
                catch (Exception) { }
            }
            if (exists)
            {
                var valid = false;
                try { valid = Authenticode.Valid(file!); } catch (Exception) { }
                try
                {
                    using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(file!));
                    Add("Signature", valid ? "Valid" : "Present but not valid");
                    Add("Signed by", AppControl.SubjectName(cert.Subject));
                    Add("Certificate issuer", AppControl.SubjectName(cert.Issuer));
                    Add("Certificate valid until", cert.NotAfter.ToUniversalTime().ToString("yyyy-MM-dd"));
                    Add("Certificate thumbprint", cert.Thumbprint);
                }
                catch (Exception) { Add("Signature", "Not signed"); }
                foreach (var kv in ZoneIdentifier(file!)) Add(kv.Key, kv.Value);
            }
            Add("Started by", parent);
            var cmd = CommandLine(pid);
            if (cmd is not null) Add("Command line", cmd.Length > 600 ? cmd.Substring(0, 600) + "…" : cmd);
            Add("Detected at", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC");
        }
        catch (Exception ex) { Log.Warn("Application control: details: " + ex.Message); }
    }

    /// <summary>Where a downloaded file came from (the Mark of the Web: Zone.Identifier stream).</summary>
    private static Dictionary<string, string> ZoneIdentifier(string file)
    {
        var r = new Dictionary<string, string>();
        var h = CreateFile(file + ":Zone.Identifier", 0x80000000 /* GENERIC_READ */, 7, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
        if (h.IsInvalid) { h.Dispose(); return r; }
        try
        {
            using var fs = new FileStream(h, FileAccess.Read);
            using var reader = new StreamReader(fs);
            var text = reader.ReadToEnd();
            foreach (var line in text.Split('\n'))
            {
                var i = line.IndexOf('=');
                if (i <= 0) continue;
                var k = line.Substring(0, i).Trim(); var v = line.Substring(i + 1).Trim();
                if (k == "HostUrl") r["Downloaded from"] = v;
                else if (k == "ReferrerUrl") r["Download page"] = v;
                else if (k == "ZoneId") r["Zone"] = v switch { "3" => "Internet", "2" => "Trusted sites", "1" => "Local intranet", "4" => "Restricted sites", _ => v };
            }
        }
        catch (Exception) { }
        return r;
    }

    /// <summary>Adds the code-signing certificate (when the signature is valid) and the SHA-256 of the file.</summary>
    private static void Sign(SoftwareCandidate c, string path)
    {
        try
        {
            if (Authenticode.Valid(path))
                using (var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)))
                    c.Signer = AppControl.SubjectName(cert.Subject);
        }
        catch (Exception) { }
        try
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (s.Length <= 4L * 1024 * 1024 * 1024)
                using (var sha = SHA256.Create()) c.Sha256 = BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        }
        catch (Exception) { }
        c.Publisher ??= c.Signer;
    }

    private static string? MsiProperty(string path, string property)
    {
        IntPtr db = IntPtr.Zero, view = IntPtr.Zero, rec = IntPtr.Zero;
        try
        {
            if (MsiOpenDatabase(path, IntPtr.Zero, out db) != 0) return null;
            if (MsiDatabaseOpenView(db, "SELECT `Value` FROM `Property` WHERE `Property`='" + property + "'", out view) != 0) return null;
            if (MsiViewExecute(view, IntPtr.Zero) != 0 || MsiViewFetch(view, out rec) != 0) return null;
            var size = 1024;
            var sb = new StringBuilder(size);
            return MsiRecordGetString(rec, 1, sb, ref size) == 0 ? sb.ToString() : null;
        }
        catch (Exception) { return null; }
        finally
        {
            if (rec != IntPtr.Zero) MsiCloseHandle(rec);
            if (view != IntPtr.Zero) MsiCloseHandle(view);
            if (db != IntPtr.Zero) MsiCloseHandle(db);
        }
    }

    private static string? CommandLine(int pid)
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\cimv2", "SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + pid);
            foreach (ManagementObject o in s.Get()) using (o) return o["CommandLine"] as string;
        }
        catch (Exception) { }
        return null;
    }

    private static string? ImagePath(IntPtr h)
    {
        var size = 1024;
        var sb = new StringBuilder(size);
        return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
    }

    /// <summary>DOMAIN\user signed in to the session.</summary>
    internal static string? SessionUser(int session)
    {
        try
        {
            string? user = null, domain = null;
            if (WTSQuerySessionInformation(IntPtr.Zero, session, 5 /* WTSUserName */, out var buf, out _)) { user = Marshal.PtrToStringUni(buf); WTSFreeMemory(buf); }
            if (WTSQuerySessionInformation(IntPtr.Zero, session, 7 /* WTSDomainName */, out var d, out _)) { domain = Marshal.PtrToStringUni(d); WTSFreeMemory(d); }
            return string.IsNullOrEmpty(user) ? null : string.IsNullOrEmpty(domain) ? user : domain + "\\" + user;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Ends the installer and every process it started.</summary>
    private static void KillTree(int pid, IntPtr handle, Dictionary<int, DateTime>? mark = null)
    {
        if (mark is not null) lock (Gate) mark[pid] = DateTime.UtcNow;
        try
        {
            using var s = new ManagementObjectSearcher(@"root\cimv2", "SELECT ProcessId FROM Win32_Process WHERE ParentProcessId = " + pid);
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    var child = Convert.ToInt32(o["ProcessId"]);
                    var ch = OpenProcess(PROCESS_TERMINATE | PROCESS_SUSPEND_RESUME, false, child);
                    if (ch != IntPtr.Zero) { KillTree(child, ch, mark); CloseHandle(ch); }
                }
        }
        catch (Exception) { }
        if (!TerminateProcess(handle, 1)) Log.Warn($"Application control: could not stop process {pid}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
    }

    // ---- native --------------------------------------------------------------------------------------------

    private const uint PROCESS_TERMINATE = 0x0001, PROCESS_SUSPEND_RESUME = 0x0800, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr h, uint code);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr h);
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool WTSQuerySessionInformation(IntPtr server, int session, int infoClass, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr p);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiOpenDatabase(string path, IntPtr persist, out IntPtr handle);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiDatabaseOpenView(IntPtr db, string query, out IntPtr view);
    [DllImport("msi.dll")] private static extern uint MsiViewExecute(IntPtr view, IntPtr record);
    [DllImport("msi.dll")] private static extern uint MsiViewFetch(IntPtr view, out IntPtr record);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiRecordGetString(IntPtr record, int field, StringBuilder value, ref int size);
    [DllImport("msi.dll")] private static extern uint MsiCloseHandle(IntPtr h);
}

/// <summary>Whether a file's Authenticode signature is valid (WinVerifyTrust, no network retrieval).</summary>
internal static class Authenticode
{
    public static bool Valid(string path)
    {
        var file = new WintrustFileInfo { cbStruct = (uint)Marshal.SizeOf(typeof(WintrustFileInfo)), pcwszFilePath = path };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WintrustFileInfo)));
        try
        {
            Marshal.StructureToPtr(file, filePtr, false);
            var data = new WintrustData
            {
                cbStruct = (uint)Marshal.SizeOf(typeof(WintrustData)), dwUIChoice = 2 /* none */, fdwRevocationChecks = 0, dwUnionChoice = 1 /* file */,
                pFile = filePtr, dwStateAction = 0, dwProvFlags = 0x1000 /* cache only URL retrieval */,
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");   // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrust(new IntPtr(-1), ref action, ref data) == 0;
        }
        finally { Marshal.FreeHGlobal(filePtr); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WintrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WintrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WintrustData data);
}
