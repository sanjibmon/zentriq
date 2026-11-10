using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;
using Zentriq.Inventory;

namespace Zentriq.Agent;

/// <summary>
/// USB device control: USB storage disks are checked every few seconds. A disk that is not allowed is turned off
/// (blocked: the device is disabled) or made read-only (the disk is set read-only and re-mounted); allowed devices keep
/// full access. Files written, renamed and deleted on USB drives are recorded (name, size, user), never their content.
/// </summary>
internal static class UsbControl
{
    private sealed class Disk
    {
        public string InstanceId = "";
        public string? Name;
        public int Index;
        public string? VidPid, Serial;
        public List<string> Letters = new();
    }

    private static readonly object Gate = new object();
    private static readonly Dictionary<string, string> Applied = new(StringComparer.OrdinalIgnoreCase);   // instance id -> full | readonly | blocked
    private static readonly Dictionary<string, FileSystemWatcher> Watchers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTime> RecentWrites = new(StringComparer.OrdinalIgnoreCase);
    private static string Folder => Path.Combine(AgentPaths.Root, "SecurityPack");
    private static string StateFile => Path.Combine(Folder, "usb-state.txt");
    private static string _lastKey = "";

    /// <summary>Disk models treated as USB storage (for tests with virtual disks; HKLM\SOFTWARE\Zentriq\Agent\UsbTestModels).</summary>
    private static string[] TestModels()
    {
        try { using var k = Registry.LocalMachine.OpenSubKey(AgentState.KeyPath); return (k?.GetValue("UsbTestModels") as string ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToArray(); }
        catch (Exception) { return Array.Empty<string>(); }
    }

    private static void LoadState()
    {
        if (Applied.Count > 0 || !File.Exists(StateFile)) return;
        foreach (var line in File.ReadAllLines(StateFile))
        {
            var i = line.LastIndexOf('|');
            if (i > 0) Applied[line.Substring(0, i)] = line.Substring(i + 1);
        }
    }

    private static void SaveState()
    {
        try { Directory.CreateDirectory(Folder); File.WriteAllLines(StateFile, Applied.Where(kv => kv.Value != "full").Select(kv => kv.Key + "|" + kv.Value)); }
        catch (Exception) { }
    }

    private static List<Disk> Disks()
    {
        var test = TestModels();
        var list = new List<Disk>();
        using var s = new ManagementObjectSearcher("SELECT Index, Model, PNPDeviceID, InterfaceType, DeviceID FROM Win32_DiskDrive");
        foreach (ManagementObject o in s.Get())
        {
            using (o)
            {
                var model = o["Model"] as string ?? "";
                var usb = string.Equals(o["InterfaceType"] as string, "USB", StringComparison.OrdinalIgnoreCase) || test.Any(t => model.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!usb || o["PNPDeviceID"] is not string pnp) continue;
                var d = new Disk { InstanceId = pnp, Name = model, Index = o["Index"] is uint ix ? (int)ix : -1 };
                // USBSTOR\DISK&VEN_…\SERIAL&0 → the USB device USB\VID_xxxx&PID_yyyy\SERIAL
                var last = pnp.Substring(pnp.LastIndexOf('\\') + 1);
                var serial = Regex.Replace(last, "&\\d+$", "");
                if (serial.Length > 0 && serial.IndexOf('&') < 0)
                {
                    try
                    {
                        using var u = new ManagementObjectSearcher($"SELECT DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB\\\\VID_%\\\\{serial.Replace("'", "")}'");
                        foreach (ManagementObject p in u.Get()) using (p) { (d.VidPid, d.Serial) = SecurityPackRules.ParseUsbId(p["DeviceID"] as string); break; }
                    }
                    catch (ManagementException) { }
                    d.Serial ??= serial;
                }
                try
                {
                    using var parts = new ManagementObjectSearcher($"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='{((string)o["DeviceID"]).Replace("\\", "\\\\")}'}} WHERE AssocClass = Win32_DiskDriveToDiskPartition");
                    foreach (ManagementObject p in parts.Get())
                        using (p)
                        {
                            using var lds = new ManagementObjectSearcher($"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{p["DeviceID"]}'}} WHERE AssocClass = Win32_LogicalDiskToPartition");
                            foreach (ManagementObject l in lds.Get()) using (l) if (l["DeviceID"] is string letter) d.Letters.Add(letter);
                        }
                }
                catch (ManagementException) { }
                list.Add(d);
            }
        }
        return list;
    }

    /// <summary>
    /// One pass: new disks get their access, changed policies are applied, watchers follow the USB drives. Returns the
    /// devices seen (for the report).
    /// </summary>
    public static List<UsbDevice> Check(UsbPolicy policy, Action<UsbEvent> record, Action<string> notify)
    {
        lock (Gate)
        {
            LoadState();
            var devices = new List<UsbDevice>();
            List<Disk> disks;
            try { disks = Disks(); } catch (ManagementException) { return devices; }
            var key = string.Join(";", disks.Select(d => d.InstanceId + "=" + string.Join(",", d.Letters))) + "#" + policy.Mode + "#" + string.Join(",", policy.Allowed.Select(a => a.DeviceId + a.Access));
            var changed = key != _lastKey;
            _lastKey = key;
            foreach (var d in disks)
            {
                var want = policy.Mode == "off" ? "full" : SecurityPackRules.UsbAccess(policy, d.InstanceId, d.VidPid, d.Serial);
                Applied.TryGetValue(d.InstanceId, out var had);
                var isNew = had is null;
                if (want != (had ?? "full") || isNew && want != "full")
                {
                    var ok = SetAccess(d, want, had ?? "full", out var detail);
                    if (ok) Applied[d.InstanceId] = want;
                    record(new UsbEvent { At = DateTimeOffset.Now, Kind = ok ? (want == "blocked" ? "blocked" : want == "readonly" ? "readonly" : "allowed") : "failed", User = ConsoleUser(), DeviceId = d.VidPid is null ? d.InstanceId : d.VidPid + (d.Serial is null ? "" : "\\" + d.Serial), DeviceName = d.Name, Detail = detail });
                    if (ok && want != "full") notify(policy.Message ?? (want == "blocked" ? $"The USB drive {d.Name} is blocked by your organisation's policy." : $"The USB drive {d.Name} is read-only: files cannot be copied to it."));
                    SaveState();
                }
                else if (isNew)
                {
                    Applied[d.InstanceId] = "full";
                    if (policy.Mode != "off") record(new UsbEvent { At = DateTimeOffset.Now, Kind = "connected", User = ConsoleUser(), DeviceId = d.VidPid is null ? d.InstanceId : d.VidPid + (d.Serial is null ? "" : "\\" + d.Serial), DeviceName = d.Name });
                }
                devices.Add(new UsbDevice { DeviceId = d.VidPid is null ? d.InstanceId : d.VidPid + (d.Serial is null ? "" : "\\" + d.Serial), Name = d.Name, VidPid = d.VidPid, Serial = d.Serial, Access = Applied[d.InstanceId], SeenAt = DateTimeOffset.Now });
            }
            // Blocked devices are disabled and no longer listed as disks: allow them again when the policy allows them.
            foreach (var kv in Applied.Where(kv => kv.Value == "blocked").ToList())
            {
                if (disks.Any(d => string.Equals(d.InstanceId, kv.Key, StringComparison.OrdinalIgnoreCase))) continue;
                var (vp, serial) = (default(string), default(string));
                var want = policy.Mode == "off" ? "full" : SecurityPackRules.UsbAccess(policy, kv.Key, vp, serial);
                if (want == "blocked") continue;
                if (Pnp("/enable-device", kv.Key, out var detail)) { Applied.Remove(kv.Key); SaveState(); record(new UsbEvent { At = DateTimeOffset.Now, Kind = "allowed", DeviceId = kv.Key, Detail = "Enabled again: " + detail }); }
            }
            if (changed) Watch(policy.Mode is "audit" or "readonly" or "block" && policy.AuditFiles ? disks.Where(d => Applied.TryGetValue(d.InstanceId, out var a) && a == "full").ToList() : new List<Disk>(), record);
            return devices;
        }
    }

    private static bool SetAccess(Disk d, string want, string had, out string detail)
    {
        detail = "";
        if (want == "blocked") return Pnp("/disable-device", d.InstanceId, out detail);
        if (want == "readonly") return Diskpart(d.Index, true, out detail);
        if (had == "readonly") return Diskpart(d.Index, false, out detail);
        if (had == "blocked") return Pnp("/enable-device", d.InstanceId, out detail);
        return true;
    }

    private static bool Pnp(string verb, string instanceId, out string detail)
    {
        var r = Run(Path.Combine(Environment.SystemDirectory, "pnputil.exe"), $"{verb} \"{instanceId}\"", 60);
        detail = r.Output.Trim();
        return r.Code == 0;
    }

    private static bool Diskpart(int index, bool readOnly, out string detail)
    {
        if (index < 0) { detail = "Disk number unknown."; return false; }
        var script = Path.Combine(Folder, "usb-diskpart.txt");
        Directory.CreateDirectory(Folder);
        File.WriteAllText(script, $"select disk {index}\r\nattributes disk {(readOnly ? "set" : "clear")} readonly\r\noffline disk\r\nonline disk\r\n");
        var r = Run(Path.Combine(Environment.SystemDirectory, "diskpart.exe"), $"/s \"{script}\"", 120);
        detail = Regex.Replace(r.Output, @"\s+", " ").Trim();
        if (detail.Length > 400) detail = detail.Substring(detail.Length - 400);
        return r.Code == 0 && detail.IndexOf("successfully", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static (int Code, string Output) Run(string file, string args, int seconds)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(seconds * 1000)) { try { p.Kill(); } catch (Exception) { } return (-1, "timed out"); }
        return (p.ExitCode, output.Result + error.Result);
    }

    private static string? ConsoleUser()
    {
        var s = WTSGetActiveConsoleSessionId();
        return s == 0xFFFFFFFF ? null : InstallerWatch.SessionUser((int)s);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();

    /// <summary>Records files written, renamed and deleted on the USB drives with full access.</summary>
    private static void Watch(List<Disk> disks, Action<UsbEvent> record)
    {
        var wanted = disks.SelectMany(d => d.Letters.Select(l => (Letter: l, Disk: d))).ToList();
        foreach (var l in Watchers.Keys.ToList().Where(l => !wanted.Any(w => string.Equals(w.Letter, l, StringComparison.OrdinalIgnoreCase))))
        {
            Watchers[l].Dispose();
            Watchers.Remove(l);
        }
        foreach (var (letter, disk) in wanted)
        {
            if (Watchers.ContainsKey(letter)) continue;
            try
            {
                var w = new FileSystemWatcher(letter + "\\") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite, InternalBufferSize = 64 * 1024 };
                var deviceId = disk.VidPid is null ? disk.InstanceId : disk.VidPid + (disk.Serial is null ? "" : "\\" + disk.Serial);
                void Written(string path)
                {
                    lock (RecentWrites)
                    {
                        if (RecentWrites.TryGetValue(path, out var at) && DateTime.UtcNow - at < TimeSpan.FromSeconds(10)) return;
                        RecentWrites[path] = DateTime.UtcNow;
                        if (RecentWrites.Count > 2000) RecentWrites.Clear();
                    }
                    long? size = null;
                    try { var f = new FileInfo(path); if (f.Exists) size = f.Length; else return; } catch (Exception) { }
                    record(new UsbEvent { At = DateTimeOffset.Now, Kind = "file_written", User = ConsoleUser(), DeviceId = deviceId, DeviceName = disk.Name, Path = path, Size = size });
                }
                w.Created += (_, e) => Written(e.FullPath);
                w.Changed += (_, e) => Written(e.FullPath);
                w.Renamed += (_, e) => record(new UsbEvent { At = DateTimeOffset.Now, Kind = "file_renamed", User = ConsoleUser(), DeviceId = deviceId, DeviceName = disk.Name, Path = e.FullPath, Detail = "from " + e.OldName });
                w.Deleted += (_, e) => record(new UsbEvent { At = DateTimeOffset.Now, Kind = "file_deleted", User = ConsoleUser(), DeviceId = deviceId, DeviceName = disk.Name, Path = e.FullPath });
                w.EnableRaisingEvents = true;
                Watchers[letter] = w;
            }
            catch (Exception ex) { Log.Warn($"USB device control: cannot watch {letter}: {ex.Message}"); }
        }
    }
}
