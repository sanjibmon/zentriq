using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;

namespace Zentriq.Agent;

/// <summary>
/// The Zentriq tray icon. The agent service starts <c>ZentriqAgent.exe --tray</c> on the desktop of every signed-in
/// user (when the tray is turned on in the console) and answers it over a local pipe; the tray never talks to the
/// server itself. The service asks the server on behalf of the session's user, so the tray needs no rights:
/// Software Center installs run as SYSTEM through the agent.
/// </summary>
internal static class TrayHost
{
    public const string PipeName = "ZentriqAgentTray";
    private static readonly Dictionary<int, IntPtr> Started = new Dictionary<int, IntPtr>();
    private static JsonElement? _config;
    private static DateTime _configAt = DateTime.MinValue;

    /// <summary>Keeps one tray per signed-in user (every 20 seconds); ends them when the tray is turned off.</summary>
    public static void Loop(CancellationToken ct)
    {
        while (!ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(20)))
        {
            try
            {
                var state = AgentState.Load();
                if (!state.Enrolled) continue;
                var enabled = Enabled();
                lock (Started)
                {
                    foreach (var s in Started.Keys.ToList())
                    {
                        var h = Started[s];
                        var running = WaitForSingleObject(h, 0) == 0x102;
                        if (running && enabled) continue;
                        if (running) TerminateProcess(h, 0);
                        CloseHandle(h);
                        Started.Remove(s);
                    }
                    if (!enabled) continue;
                    foreach (var session in ActiveSessions())
                    {
                        if (Started.ContainsKey(session)) continue;
                        var p = Notice.StartInSession(session, "--tray");
                        if (p != IntPtr.Zero) { Started[session] = p; Log.Info($"Tray icon started for session {session}."); }
                    }
                }
            }
            catch (Exception ex) { Log.Warn("Tray icon: " + ex.Message); }
        }
        StopAll();
    }

    /// <summary>Ends the trays (the service stops: an update or an uninstall must be able to replace the program).</summary>
    public static void StopAll()
    {
        lock (Started)
        {
            foreach (var h in Started.Values) { TerminateProcess(h, 0); CloseHandle(h); }
            Started.Clear();
        }
        try
        {
            foreach (var p in Process.GetProcessesByName("ZentriqAgent"))
                using (p)
                    if (p.Id != Process.GetCurrentProcess().Id && CommandLineOf(p.Id)?.IndexOf("--tray", StringComparison.OrdinalIgnoreCase) >= 0) p.Kill();
        }
        catch (Exception) { /* best effort */ }
    }

    private static string? CommandLineOf(int pid)
    {
        try
        {
            using var s = new System.Management.ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (System.Management.ManagementObject o in s.Get()) using (o) return o["CommandLine"] as string;
        }
        catch (Exception) { }
        return null;
    }

    private static bool Enabled()
    {
        var c = Config();
        return c is null || !(c.Value.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.False);
    }

    /// <summary>The tray settings from the server (cached for 10 minutes).</summary>
    private static JsonElement? Config()
    {
        if (DateTime.UtcNow - _configAt < TimeSpan.FromMinutes(10)) return _config;
        try
        {
            using var client = Connect();
            var text = client.SendJsonAsync(HttpMethod.Get, "tray/config", null, CancellationToken.None).GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(text);
            _config = doc.RootElement.Clone();
        }
        catch (Exception ex) { Log.Warn("Tray icon: the settings could not be read: " + ex.Message); }
        _configAt = DateTime.UtcNow;
        return _config;
    }

    private static ServerClient Connect()
    {
        var state = AgentState.Load();
        var cert = CertificateStore.Find(state.CertThumbprint) ?? throw new InvalidOperationException("The agent certificate was not found.");
        return new ServerClient(state.ServerUrl!, state.ServerPins, cert, state.ProxyUrl, timeout: TimeSpan.FromSeconds(60));
    }

    // ---- the pipe the trays talk to ---------------------------------------------------------------------------------

    public static void PipeLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                var security = new PipeSecurity();
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
                pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 16, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536, security);
                var wait = pipe.BeginWaitForConnection(null, null);
                while (!wait.AsyncWaitHandle.WaitOne(1000)) if (ct.IsCancellationRequested) return;
                pipe.EndWaitForConnection(wait);
                var connected = pipe;
                pipe = null;
                new Thread(() => Serve(connected)) { IsBackground = true, Name = "tray request" }.Start();
            }
            catch (Exception ex) { Log.Warn("Tray pipe: " + ex.Message); ct.WaitHandle.WaitOne(2000); }
            finally { pipe?.Dispose(); }
        }
    }

    private static void Serve(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 65536, leaveOpen: true);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), 65536, leaveOpen: true) { AutoFlush = true };
            string reply;
            try
            {
                var line = reader.ReadLine();
                if (line is null) return;
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                string? S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var user = GetNamedPipeClientSessionId(pipe.SafePipeHandle.DangerousGetHandle(), out var session) ? InstallerWatch.SessionUser((int)session) : null;
                object? data = S("op") switch
                {
                    "config" => Server(HttpMethod.Get, "tray/config", null),
                    "catalog" => Server(HttpMethod.Get, "tray/catalog", null),
                    "install" => Server(HttpMethod.Post, "tray/install", new Dictionary<string, object?> { ["packageId"] = S("packageId"), ["user"] = user, ["reason"] = S("reason") }),
                    "help" => Server(HttpMethod.Post, "tray/help", new Dictionary<string, object?> { ["user"] = user, ["message"] = S("message"), ["contact"] = S("contact") }),
                    "helpList" => Server(HttpMethod.Get, "tray/help?user=" + Uri.EscapeDataString(user ?? ""), null),
                    "info" => Info(user),
                    "checkin" => CheckIn(),
                    _ => throw new InvalidOperationException("Unknown request."),
                };
                reply = JsonSerializer.Serialize(new Dictionary<string, object?> { ["ok"] = true, ["data"] = data });
            }
            catch (Exception ex)
            {
                var msg = ex is ServerException ? Clean(ex.Message) : ex.Message;
                reply = JsonSerializer.Serialize(new Dictionary<string, object?> { ["ok"] = false, ["error"] = msg });
            }
            try { writer.WriteLine(reply); } catch (IOException) { }
        }
    }

    private static string Clean(string message)
    {
        var i = message.IndexOf(": ", StringComparison.Ordinal);
        return message.StartsWith("Server returned", StringComparison.Ordinal) && i > 0 ? message.Substring(i + 2) : message;
    }

    private static JsonElement Server(HttpMethod method, string path, object? body)
    {
        using var client = Connect();
        var text = client.SendJsonAsync(method, path, body, CancellationToken.None).GetAwaiter().GetResult();
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "null" : text);
        return doc.RootElement.Clone();
    }

    private static object CheckIn()
    {
        var r = AgentService.RunNow(false, TimeSpan.FromSeconds(60));
        return new Dictionary<string, object?> { ["ok"] = r?.Ok ?? false, ["message"] = r is null ? "No answer within a minute; the agent keeps trying." : r.Ok ? "Checked in with the server." : r.Message };
    }

    private static object Info(string? user)
    {
        var state = AgentState.Load();
        var ips = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork) ips.Add(a.Address.ToString());
        }
        catch (NetworkInformationException) { }
        return new Dictionary<string, object?>
        {
            ["computer"] = Environment.MachineName, ["user"] = user, ["ip"] = string.Join(", ", ips.Distinct()), ["os"] = OsName(),
            ["agentVersion"] = Program.Version, ["server"] = state.ServerUrl, ["status"] = state.Status,
            ["lastCheckin"] = state.LastCheckinUtc?.ToLocalTime().ToString("g"), ["lastInventory"] = state.LastInventoryUtc?.ToLocalTime().ToString("g"),
        };
    }

    private static string OsName()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var name = k?.GetValue("ProductName") as string ?? "Windows";
            var build = k?.GetValue("CurrentBuild") as string;
            if (build is not null && int.TryParse(build, out var b) && b >= 22000) name = name.Replace("Windows 10", "Windows 11");
            return build is null ? name : $"{name} (build {build})";
        }
        catch (Exception) { return "Windows"; }
    }

    private static IEnumerable<int> ActiveSessions()
    {
        var result = new List<int>();
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var list, out var count)) return result;
        try
        {
            var size = Marshal.SizeOf(typeof(WtsSessionInfo));
            for (var i = 0; i < count; i++)
            {
                var info = (WtsSessionInfo)Marshal.PtrToStructure(new IntPtr(list.ToInt64() + i * size), typeof(WtsSessionInfo))!;
                if (info.State == 0 /* WTSActive */ && info.SessionId != 0 && InstallerWatch.SessionUser(info.SessionId) is not null) result.Add(info.SessionId);
            }
        }
        finally { WTSFreeMemory(list); }
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WtsSessionInfo { public int SessionId; public string? WinStationName; public int State; }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);
    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientSessionId(IntPtr pipe, out uint sessionId);
    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(IntPtr handle, uint exitCode);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>The tray side (<c>ZentriqAgent.exe --tray</c>, on the user's desktop, with the user's rights).</summary>
internal static class TrayApp
{
    internal static readonly Color Ink = Color.FromArgb(14, 42, 54);
    internal static readonly Color Accent = Color.FromArgb(18, 140, 158);
    internal static readonly Color Soft = Color.FromArgb(244, 247, 249);
    internal static readonly Color Line = Color.FromArgb(220, 228, 233);
    internal static readonly Color Muted = Color.FromArgb(90, 106, 118);

    public static int Run()
    {
        using var single = new Mutex(true, @"Local\ZentriqAgentTray", out var first);
        if (!first) return 0;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var ctx = new TrayContext();
        Application.Run(ctx);
        return 0;
    }

    /// <summary>One tray window on its own (<c>--tray-window store|help|info</c>): for support and for the acceptance test.</summary>
    public static int Window(string? which)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            var c = Call(new Dictionary<string, object?> { ["op"] = "config" }, 30000);
            TrayContext.Company = Str(c, "companyName") ?? "";
            TrayContext.Support = Str(c, "supportText") ?? "";
        }
        catch (Exception) { }
        using Form form = which switch { "help" => new HelpForm(), "info" => new InfoForm(), _ => new SoftwareCenterForm(null) };
        Application.Run(form);
        return 0;
    }

    /// <summary>One request to the agent service.</summary>
    internal static JsonElement Call(Dictionary<string, object?> request, int timeoutMs = 90000)
    {
        using var pipe = new NamedPipeClientStream(".", TrayHost.PipeName, PipeDirection.InOut);
        pipe.Connect(5000);
        pipe.ReadMode = PipeTransmissionMode.Byte;
        var writer = new StreamWriter(pipe, new UTF8Encoding(false), 65536, leaveOpen: true) { AutoFlush = true };
        var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 65536, leaveOpen: true);
        writer.WriteLine(JsonSerializer.Serialize(request));
        var task = reader.ReadLineAsync();
        if (!task.Wait(timeoutMs)) throw new TimeoutException("The Zentriq Agent did not answer.");
        using var doc = JsonDocument.Parse(task.Result ?? "{}");
        var r = doc.RootElement;
        if (!(r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True))
            throw new InvalidOperationException(r.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : "The request failed.");
        return r.TryGetProperty("data", out var d) ? d.Clone() : default;
    }

    internal static string? Str(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    internal static bool Bool(JsonElement e, string k, bool fallback) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) ? v.ValueKind == JsonValueKind.True || (v.ValueKind != JsonValueKind.False && fallback) : fallback;

    internal static Icon AppIcon()
    {
        try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application; } catch (Exception) { return SystemIcons.Application; }
    }

    /// <summary>A window with the Zentriq header bar.</summary>
    internal class TrayForm : Form
    {
        protected readonly Panel Body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(22, 16, 22, 16), BackColor = Color.White };

        protected TrayForm(string title, string subtitle, Size size)
        {
            Text = title;
            Icon = AppIcon();
            Font = new Font("Segoe UI", 9.75f);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = size;
            MinimumSize = new Size(Math.Min(size.Width, 520), Math.Min(size.Height, 420));
            BackColor = Color.White;
            var head = new Panel { Dock = DockStyle.Top, Height = 72, BackColor = Ink, Padding = new Padding(22, 12, 22, 10) };
            var t = new Label { Text = title, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 15f), AutoSize = true, Location = new Point(22, 10), BackColor = Color.Transparent };
            var s = new Label { Text = subtitle, ForeColor = Color.FromArgb(150, 214, 224), Font = new Font("Segoe UI", 9.5f), AutoSize = true, Location = new Point(24, 44), BackColor = Color.Transparent };
            head.Controls.AddRange(new Control[] { t, s });
            Controls.Add(Body);
            Controls.Add(head);
        }

        internal static Button Primary(string text, int width = 120) => new Button
        {
            Text = text, Width = width, Height = 34, FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 9.75f),
            FlatAppearance = { BorderSize = 0 }, Cursor = Cursors.Hand,
        };

        internal static Button Secondary(string text, int width = 110) => new Button
        {
            Text = text, Width = width, Height = 34, FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Ink, Font = new Font("Segoe UI", 9.75f),
            FlatAppearance = { BorderColor = Line, BorderSize = 1 }, Cursor = Cursors.Hand,
        };
    }

    private sealed class TrayContext : ApplicationContext
    {
        private readonly NotifyIcon _icon;
        private readonly ToolStripMenuItem _store, _help;
        private readonly System.Windows.Forms.Timer _refresh = new System.Windows.Forms.Timer { Interval = 10 * 60 * 1000 };
        private SoftwareCenterForm? _storeForm;
        private HelpForm? _helpForm;
        private InfoForm? _infoForm;
        internal static string Company = "";
        internal static string Support = "";

        public TrayContext()
        {
            _store = new ToolStripMenuItem("Software Center", null, (_, _) => ShowStore()) { Font = new Font("Segoe UI", 9.75f, FontStyle.Bold) };
            _help = new ToolStripMenuItem("Get Remote Help", null, (_, _) => ShowHelp());
            var menu = new ContextMenuStrip { Font = new Font("Segoe UI", 9.75f), ShowImageMargin = false };
            menu.Items.Add(new ToolStripLabel("Zentriq — IT Support") { ForeColor = Muted, Font = new Font("Segoe UI", 8.75f) });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.AddRange(new ToolStripItem[]
            {
                _store, _help, new ToolStripSeparator(),
                new ToolStripMenuItem("Device Information", null, (_, _) => ShowInfo()),
                new ToolStripMenuItem("Check In Now", null, (_, _) => CheckIn()),
            });
            _icon = new NotifyIcon { Icon = AppIcon(), Text = "Zentriq — IT Support", ContextMenuStrip = menu, Visible = true };
            _icon.DoubleClick += (_, _) => { if (_store.Visible) ShowStore(); else ShowInfo(); };
            _refresh.Tick += (_, _) => LoadConfig();
            _refresh.Start();
            LoadConfig();
        }

        private void LoadConfig()
        {
            try
            {
                var c = Call(new Dictionary<string, object?> { ["op"] = "config" }, 30000);
                if (!Bool(c, "enabled", true)) { ExitThread(); return; }
                _store.Visible = Bool(c, "softwareCenter", true);
                _help.Visible = Bool(c, "remoteHelp", true);
                Company = Str(c, "companyName") ?? "";
                Support = Str(c, "supportText") ?? "";
            }
            catch (Exception) { /* the agent is restarting or offline: keep the menu as it is */ }
        }

        private static T Open<T>(ref T? form, Func<T> create) where T : Form
        {
            if (form is null || form.IsDisposed) form = create();
            form.Show();
            if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
            form.Activate();
            return form;
        }

        private void ShowStore() => Open(ref _storeForm, () => new SoftwareCenterForm(this));
        private void ShowHelp() => Open(ref _helpForm, () => new HelpForm());
        private void ShowInfo() => Open(ref _infoForm, () => new InfoForm());

        internal void Balloon(string title, string text, ToolTipIcon icon = ToolTipIcon.Info) => _icon.ShowBalloonTip(5000, title, text, icon);

        private void CheckIn()
        {
            new Thread(() =>
            {
                string text;
                try { var r = Call(new Dictionary<string, object?> { ["op"] = "checkin" }); text = Str(r, "message") ?? "Done."; }
                catch (Exception ex) { text = ex.Message; }
                Balloon("Zentriq", text);
            }) { IsBackground = true }.Start();
        }

        protected override void ExitThreadCore()
        {
            _icon.Visible = false;
            _icon.Dispose();
            _refresh.Dispose();
            base.ExitThreadCore();
        }
    }

    // ---- Software Center ------------------------------------------------------------------------------------------

    private sealed class SoftwareCenterForm : TrayForm
    {
        private readonly TrayContext? _ctx;
        private readonly TextBox _search = new TextBox { Font = new Font("Segoe UI", 10.5f) };
        private readonly ComboBox _category = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Font = new Font("Segoe UI", 10f) };
        private readonly FlowLayoutPanel _list = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Soft, Padding = new Padding(10) };
        private readonly Label _status = new Label { Dock = DockStyle.Bottom, Height = 26, ForeColor = Muted, TextAlign = ContentAlignment.MiddleLeft };
        private readonly System.Windows.Forms.Timer _poll = new System.Windows.Forms.Timer { Interval = 8000 };
        private List<JsonElement> _items = new List<JsonElement>();
        private readonly Dictionary<string, string> _known = new Dictionary<string, string>();

        public SoftwareCenterForm(TrayContext? ctx) : base("Software Center", "Software approved by " + (TrayContext.Company.Length > 0 ? TrayContext.Company : "your IT team") + " — installs without administrator rights", new Size(780, 600))
        {
            _ctx = ctx;
            var bar = new Panel { Dock = DockStyle.Top, Height = 46 };
            var searchLabel = new Label { Text = "Search", AutoSize = true, Location = new Point(0, 12), ForeColor = Muted };
            _search.Location = new Point(56, 8); _search.Width = 330;
            _category.Location = new Point(400, 7); _category.Width = 200;
            var refresh = Secondary("Refresh", 80); refresh.Location = new Point(_category.Right + 10, 5); refresh.Height = 30;
            refresh.Click += (_, _) => Load();
            bar.Controls.AddRange(new Control[] { searchLabel, _search, _category, refresh });
            _search.TextChanged += (_, _) => Render();
            _category.SelectedIndexChanged += (_, _) => Render();
            _list.SizeChanged += (_, _) => { foreach (Control c in _list.Controls) c.Width = _list.ClientSize.Width - 24; };
            Body.Controls.Add(_list);
            Body.Controls.Add(_status);
            Body.Controls.Add(bar);
            _poll.Tick += (_, _) => Load(quiet: true);
            Shown += (_, _) => Load();
            FormClosed += (_, _) => _poll.Dispose();
        }

        private void Load(bool quiet = false)
        {
            if (!quiet) _status.Text = "Loading…";
            new Thread(() =>
            {
                try
                {
                    var data = Call(new Dictionary<string, object?> { ["op"] = "catalog" });
                    var items = data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().Select(x => x.Clone()).ToList() : new List<JsonElement>();
                    BeginInvoke(new Action(() => { _items = items; Notify(); FillCategories(); Render(); _status.Text = items.Count == 0 ? "No software is offered to this computer yet." : $"{items.Count} application(s) offered."; }));
                }
                catch (Exception ex) { BeginInvoke(new Action(() => _status.Text = "Could not load the list: " + ex.Message)); }
            }) { IsBackground = true }.Start();
        }

        /// <summary>A balloon when something the user asked for finished.</summary>
        private void Notify()
        {
            foreach (var i in _items)
            {
                var id = Str(i, "id") ?? ""; var state = Str(i, "state") ?? "";
                if (_known.TryGetValue(id, out var before) && before != state)
                {
                    if (state == "installed") _ctx?.Balloon("Installed", $"{Str(i, "name")} is ready to use.");
                    else if (state == "failed") _ctx?.Balloon("Not installed", $"{Str(i, "name")} could not be installed. {Str(i, "message")}", ToolTipIcon.Warning);
                    else if (state == "denied") _ctx?.Balloon("Not approved", $"{Str(i, "name")}: {Str(i, "message")}", ToolTipIcon.Warning);
                }
                _known[id] = state;
            }
            _poll.Enabled = _items.Any(i => Str(i, "state") is "installing" or "pending" or "removing");
        }

        private void FillCategories()
        {
            var current = _category.SelectedItem as string;
            var cats = _items.Select(i => Str(i, "category")).Where(c => !string.IsNullOrEmpty(c)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c).ToList();
            _category.BeginUpdate();
            _category.Items.Clear();
            _category.Items.Add("All categories");
            foreach (var c in cats) _category.Items.Add(c!);
            _category.SelectedItem = current is not null && _category.Items.Contains(current) ? current : "All categories";
            _category.EndUpdate();
        }

        private void Render()
        {
            var q = _search.Text.Trim();
            var cat = _category.SelectedItem as string;
            _list.SuspendLayout();
            foreach (Control c in _list.Controls) c.Dispose();
            _list.Controls.Clear();
            foreach (var i in _items)
            {
                if (q.Length > 0 && $"{Str(i, "name")} {Str(i, "publisher")} {Str(i, "description")}".IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (cat is not null && cat != "All categories" && !string.Equals(Str(i, "category"), cat, StringComparison.OrdinalIgnoreCase)) continue;
                _list.Controls.Add(Card(i));
            }
            if (_list.Controls.Count == 0)
                _list.Controls.Add(new Label { Text = _items.Count == 0 ? "Nothing here yet. Ask IT to offer software in the Software Center." : "No application matches the search.", AutoSize = true, ForeColor = Muted, Padding = new Padding(8) });
            _list.ResumeLayout();
        }

        private Control Card(JsonElement i)
        {
            var w = _list.ClientSize.Width - 24;
            var card = new Panel { Width = w, Height = 88, BackColor = Color.White, Margin = new Padding(4, 4, 4, 6), Padding = new Padding(14, 10, 14, 10) };
            card.Paint += (_, e) => { using var pen = new Pen(Line); e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1); };
            var badge = new Label
            {
                Text = (Str(i, "name") ?? "?").Substring(0, 1).ToUpperInvariant(), Size = new Size(44, 44), Location = new Point(14, 22), TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.FromArgb(225, 242, 245), ForeColor = Accent, Font = new Font("Segoe UI Semibold", 15f),
            };
            var name = new Label { Text = $"{Str(i, "name")}{(Str(i, "version") is { } v ? "  " + v : "")}", Font = new Font("Segoe UI Semibold", 11f), AutoSize = false, Location = new Point(72, 10), Size = new Size(w - 260, 24), ForeColor = Ink };
            var size = i.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : 0;
            var meta = string.Join("  ·  ", new[] { Str(i, "publisher"), Str(i, "category"), size > 0 ? $"{size / 1048576.0:0.#} MB" : null, Bool(i, "approval", false) ? "Needs approval" : null }.Where(x => !string.IsNullOrEmpty(x)));
            var sub = new Label { Text = meta, ForeColor = Muted, AutoSize = false, Location = new Point(73, 34), Size = new Size(w - 260, 18), Font = new Font("Segoe UI", 9f) };
            var state = Str(i, "state") ?? "available";
            var desc = new Label { Text = Str(i, "message") is { } m && state != "available" && state != "installed" ? m : Str(i, "description") ?? "", ForeColor = state is "failed" or "denied" ? Color.Firebrick : Color.FromArgb(55, 65, 72), AutoSize = false, Location = new Point(73, 54), Size = new Size(w - 260, 30), Font = new Font("Segoe UI", 9f), AutoEllipsis = true };
            var button = state switch
            {
                "installed" => Secondary("Installed  ✓", 150),
                "installing" => Secondary("Installing…", 150),
                "removing" => Secondary("Removing…", 150),
                "pending" => Secondary("Awaiting Approval", 150),
                "failed" => Primary("Try Again", 150),
                _ => Primary(Bool(i, "approval", false) ? "Request" : "Install", 150),
            };
            button.Location = new Point(w - 172, 27);
            button.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            button.Enabled = state is "available" or "failed" or "denied";
            if (state == "installed") button.ForeColor = Color.SeaGreen;
            var id = Str(i, "id");
            button.Click += (_, _) => Install(id!, Str(i, "name") ?? "", Bool(i, "approval", false), button);
            card.Controls.AddRange(new Control[] { badge, name, sub, desc, button });
            return card;
        }

        private void Install(string id, string name, bool approval, Button button)
        {
            string? reason = null;
            if (approval)
            {
                reason = ReasonDialog.Ask(this, name);
                if (reason is null) return;
            }
            button.Enabled = false;
            button.Text = approval ? "Sending…" : "Starting…";
            new Thread(() =>
            {
                try
                {
                    var r = Call(new Dictionary<string, object?> { ["op"] = "install", ["packageId"] = id, ["reason"] = reason });
                    BeginInvoke(new Action(() => { _status.Text = Str(r, "message") ?? ""; Load(quiet: true); }));
                }
                catch (Exception ex) { BeginInvoke(new Action(() => { _status.Text = ex.Message; button.Enabled = true; button.Text = approval ? "Request" : "Install"; })); }
            }) { IsBackground = true }.Start();
        }
    }

    private sealed class ReasonDialog : Form
    {
        private readonly TextBox _text = new TextBox { Multiline = true, Font = new Font("Segoe UI", 10f), ScrollBars = ScrollBars.Vertical, MaxLength = 1000 };

        private ReasonDialog(string name)
        {
            Text = "Request " + name;
            Icon = AppIcon();
            Font = new Font("Segoe UI", 9.75f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(440, 230);
            BackColor = Color.White;
            var label = new Label { Text = $"{name} needs approval by IT. Why do you need it?", AutoSize = false, Location = new Point(20, 18), Size = new Size(400, 40) };
            _text.Location = new Point(20, 60); _text.Size = new Size(400, 100);
            var send = TrayForm.Primary("Send Request", 130); send.Location = new Point(290, 176); send.DialogResult = DialogResult.OK;
            var cancel = TrayForm.Secondary("Cancel", 100); cancel.Location = new Point(180, 176); cancel.DialogResult = DialogResult.Cancel;
            Controls.AddRange(new Control[] { label, _text, send, cancel });
            AcceptButton = send; CancelButton = cancel;
        }

        public static string? Ask(IWin32Window owner, string name)
        {
            using var d = new ReasonDialog(name);
            return d.ShowDialog(owner) == DialogResult.OK ? d._text.Text.Trim() : null;
        }
    }

    // ---- Get Remote Help --------------------------------------------------------------------------------------------

    private sealed class HelpForm : TrayForm
    {
        private readonly TextBox _message = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Segoe UI", 10.5f), MaxLength = 2000 };
        private readonly TextBox _contact = new TextBox { Font = new Font("Segoe UI", 10.5f), MaxLength = 200 };
        private readonly Label _result = new Label { AutoSize = false, ForeColor = Color.SeaGreen, Font = new Font("Segoe UI Semibold", 9.75f) };
        private readonly ListView _history = new ListView { View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, BorderStyle = BorderStyle.FixedSingle };
        private readonly Button _send = Primary("Send Request", 140);

        public HelpForm() : base("Get Remote Help", "Tell IT what is wrong — a technician contacts you and, with your consent, connects to this computer", new Size(620, 600))
        {
            var y = 4;
            var content = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            Label L(string text, int top) => new Label { Text = text, AutoSize = true, Location = new Point(0, top), Font = new Font("Segoe UI Semibold", 9.75f), ForeColor = Ink };
            content.Controls.Add(L("What is the problem?", y));
            _message.Location = new Point(0, y + 24); _message.Size = new Size(560, 120);
            content.Controls.Add(_message);
            y += 158;
            content.Controls.Add(L("How can we reach you? (phone or extension, optional)", y));
            _contact.Location = new Point(0, y + 24); _contact.Width = 300;
            content.Controls.Add(_contact);
            y += 66;
            _send.Location = new Point(0, y); _send.Click += (_, _) => Send();
            _result.Location = new Point(156, y + 8); _result.Size = new Size(400, 22);
            content.Controls.AddRange(new Control[] { _send, _result });
            y += 50;
            if (TrayContext.Support.Length > 0)
            {
                content.Controls.Add(new Label { Text = TrayContext.Support, AutoSize = false, Location = new Point(0, y), Size = new Size(560, 38), ForeColor = Muted });
                y += 42;
            }
            content.Controls.Add(L("Your requests", y));
            _history.Location = new Point(0, y + 24); _history.Size = new Size(560, 120);
            _history.Columns.Add("Number", 80); _history.Columns.Add("Problem", 280); _history.Columns.Add("Status", 90); _history.Columns.Add("Sent", 100);
            content.Controls.Add(_history);
            // Sized from the panel itself: anchors are measured against the panel's size before docking and overflowed.
            content.Resize += (_, _) =>
            {
                var w = Math.Max(200, content.ClientSize.Width - 2);
                _message.Width = w;
                _history.Size = new Size(w, Math.Max(80, content.ClientSize.Height - _history.Top - 2));
                _history.Columns[1].Width = Math.Max(120, w - 80 - 90 - 110 - 4);
            };
            Body.Controls.Add(content);
            AcceptButton = null;
            Shown += (_, _) => { _message.Focus(); LoadHistory(); };
        }

        private void Send()
        {
            var text = _message.Text.Trim();
            if (text.Length < 3) { _result.ForeColor = Color.Firebrick; _result.Text = "Describe the problem first."; return; }
            _send.Enabled = false; _result.ForeColor = Muted; _result.Text = "Sending…";
            var contact = _contact.Text.Trim();
            new Thread(() =>
            {
                try
                {
                    var r = Call(new Dictionary<string, object?> { ["op"] = "help", ["message"] = text, ["contact"] = contact.Length > 0 ? contact : null });
                    BeginInvoke(new Action(() => { _result.ForeColor = Color.SeaGreen; _result.Text = Str(r, "message") ?? "Sent."; _message.Clear(); _send.Enabled = true; LoadHistory(); }));
                }
                catch (Exception ex) { BeginInvoke(new Action(() => { _result.ForeColor = Color.Firebrick; _result.Text = ex.Message; _send.Enabled = true; })); }
            }) { IsBackground = true }.Start();
        }

        private void LoadHistory()
        {
            new Thread(() =>
            {
                try
                {
                    var data = Call(new Dictionary<string, object?> { ["op"] = "helpList" });
                    BeginInvoke(new Action(() =>
                    {
                        _history.Items.Clear();
                        if (data.ValueKind != JsonValueKind.Array) return;
                        foreach (var h in data.EnumerateArray())
                        {
                            var status = Str(h, "status") switch { "open" => "Open", "in_progress" => "In progress", "closed" => "Closed", var s => s ?? "" };
                            var at = h.TryGetProperty("createdAt", out var c) && c.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(c.GetString(), out var d) ? d.LocalDateTime.ToString("g") : "";
                            _history.Items.Add(new ListViewItem(new[] { "#" + (h.TryGetProperty("number", out var n) ? n.ToString() : ""), Str(h, "message") ?? "", status, at }));
                        }
                    }));
                }
                catch (Exception) { /* the list is a convenience */ }
            }) { IsBackground = true }.Start();
        }
    }

    // ---- Device Information ---------------------------------------------------------------------------------------

    private sealed class InfoForm : TrayForm
    {
        private readonly TableLayoutPanel _table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };

        public InfoForm() : base("Device Information", "Give these details to IT when you call for help", new Size(560, 440))
        {
            _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var copy = Secondary("Copy", 90);
            copy.Dock = DockStyle.Bottom;
            copy.Click += (_, _) =>
            {
                var sb = new StringBuilder();
                for (var r = 0; r < _table.RowCount; r++) sb.Append(_table.GetControlFromPosition(0, r)?.Text).Append(": ").AppendLine(_table.GetControlFromPosition(1, r)?.Text);
                try { Clipboard.SetText(sb.ToString()); } catch (ExternalException) { }
            };
            Body.Controls.Add(_table);
            Body.Controls.Add(copy);
            Shown += (_, _) => Fill();
        }

        private void Fill()
        {
            try
            {
                var i = Call(new Dictionary<string, object?> { ["op"] = "info" }, 15000);
                var rows = new[]
                {
                    ("Computer name", Str(i, "computer")), ("Signed-in user", Str(i, "user")), ("IP address", Str(i, "ip")), ("Operating system", Str(i, "os")),
                    ("Zentriq Agent", Str(i, "agentVersion")), ("Server", Str(i, "server")), ("Last check-in", Str(i, "lastCheckin")), ("Last inventory", Str(i, "lastInventory")),
                };
                _table.RowCount = rows.Length;
                for (var r = 0; r < rows.Length; r++)
                {
                    _table.Controls.Add(new Label { Text = rows[r].Item1, AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 7, 8, 7) }, 0, r);
                    _table.Controls.Add(new Label { Text = rows[r].Item2 ?? "—", AutoSize = true, ForeColor = Ink, Font = new Font("Segoe UI Semibold", 9.75f), Margin = new Padding(0, 7, 0, 7) }, 1, r);
                }
            }
            catch (Exception ex) { _table.Controls.Add(new Label { Text = "The Zentriq Agent did not answer: " + ex.Message, AutoSize = true, ForeColor = Color.Firebrick }); }
        }
    }
}
