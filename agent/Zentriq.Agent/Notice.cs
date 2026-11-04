using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using Zentriq.Inventory;

namespace Zentriq.Agent;

/// <summary>
/// The message a user sees when an installation was blocked ("This is unauthorized software. Please contact your
/// administrator…", set in the console), with "Request approval". The service (session 0) starts
/// "ZentriqAgent.exe --app-notice &lt;details&gt;" on the user's desktop with the user's own token; a request comes back
/// over a one-time pipe that only this notice knows, and the service sends it to the server.
/// </summary>
internal static class Notice
{
    // ---- service side ------------------------------------------------------------------------------------------

    public static void Show(int session, SoftwareCandidate c, AppControlPolicy policy, string? user)
    {
        var pipeName = "ZentriqAgentNotice-" + Guid.NewGuid().ToString("N");
        var details = new Dictionary<string, object?>
        {
            ["message"] = policy.Message, ["name"] = c.Name, ["publisher"] = c.Publisher ?? c.Signer, ["version"] = c.Version, ["file"] = c.Path is null ? null : Path.GetFileName(c.Path),
            ["requests"] = policy.AllowRequests, ["pipe"] = policy.AllowRequests ? pipeName : null,
        };
        if (policy.AllowRequests) new Thread(() => Listen(pipeName, c, user)) { IsBackground = true, Name = "notice" }.Start();
        var arg = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(details)));
        if (!StartInSession(session, "--app-notice " + arg)) Log.Warn($"Application control: could not show the message in session {session}.");
    }

    /// <summary>Waits (up to 15 minutes) for the notice to send a request.</summary>
    private static void Listen(string pipeName, SoftwareCandidate c, string? user)
    {
        try
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
            using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
            var wait = pipe.BeginWaitForConnection(null, null);
            if (!wait.AsyncWaitHandle.WaitOne(TimeSpan.FromMinutes(15))) return;
            pipe.EndWaitForConnection(wait);
            var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            var line = reader.ReadLine();
            if (line is null || line.Length > 4000) return;
            using var doc = JsonDocument.Parse(line);
            var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null;
            AppControlAgent.AddRequest(c, user, string.IsNullOrWhiteSpace(reason) ? null : reason!.Trim());
            writer.WriteLine("{\"ok\":true}");
        }
        catch (Exception ex) { Log.Warn("Application control: the approval request could not be received: " + ex.Message); }
    }

    private static bool StartInSession(int session, string arguments)
    {
        IntPtr token = IntPtr.Zero, env = IntPtr.Zero;
        try
        {
            if (!WTSQueryUserToken(session, out token)) return false;
            if (!CreateEnvironmentBlock(out env, token, false)) env = IntPtr.Zero;
            var exe = Path.Combine(TamperGuard.InstallFolder, "ZentriqAgent.exe");
            var si = new StartupInfo { cb = Marshal.SizeOf(typeof(StartupInfo)), lpDesktop = @"winsta0\default" };
            if (!CreateProcessAsUser(token, null, new StringBuilder($"\"{exe}\" {arguments}"), IntPtr.Zero, IntPtr.Zero, false, CreateNoWindow | CreateUnicodeEnvironment, env,
                    TamperGuard.InstallFolder, ref si, out var pi))
            {
                Log.Warn("Application control: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
                return false;
            }
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return true;
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    // ---- dialog side (ZentriqAgent.exe --app-notice) -----------------------------------------------------------

    public static int RunDialog(string? arg)
    {
        Dictionary<string, JsonElement> d;
        try { d = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Encoding.UTF8.GetString(Convert.FromBase64String(arg ?? ""))) ?? new Dictionary<string, JsonElement>(); }
        catch (Exception) { return 2; }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var form = new NoticeForm(d);
        Application.Run(form);
        return 0;
    }

    private static string? Get(Dictionary<string, JsonElement> d, string key) =>
        d.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private sealed class NoticeForm : Form
    {
        private readonly TextBox _reason = new TextBox();
        private readonly Label _status = new Label();
        private readonly Button _request = new Button();

        public NoticeForm(Dictionary<string, JsonElement> d)
        {
            Text = "Zentriq";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            Font = new Font("Segoe UI", 9.5f);
            AutoScaleMode = AutoScaleMode.Dpi;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
            var requests = d.TryGetValue("requests", out var rq) && rq.ValueKind == JsonValueKind.True && Get(d, "pipe") is not null;
            ClientSize = new Size(460, requests ? 330 : 220);

            var title = new Label { Text = "Installation blocked", Font = new Font("Segoe UI", 12f, FontStyle.Bold), AutoSize = false, Location = new Point(20, 16), Size = new Size(420, 26), ForeColor = Color.Firebrick };
            var message = new Label { Text = Get(d, "message") ?? AppControl.DefaultMessage, AutoSize = false, Location = new Point(20, 48), Size = new Size(420, 56) };
            var what = new StringBuilder();
            what.Append(Get(d, "name"));
            if (Get(d, "version") is { } v) what.Append(' ').Append(v);
            if (Get(d, "publisher") is { } p) what.Append("\r\nPublisher: ").Append(p);
            if (Get(d, "file") is { } f) what.Append("\r\nFile: ").Append(f);
            var details = new Label { Text = what.ToString(), AutoSize = false, Location = new Point(20, 106), Size = new Size(420, 56), ForeColor = Color.DimGray };
            var close = new Button { Text = "Close", Size = new Size(96, 30), DialogResult = DialogResult.Cancel };
            Controls.AddRange(new Control[] { title, message, details });
            if (requests)
            {
                var label = new Label { Text = "Why do you need it? (optional)", AutoSize = true, Location = new Point(20, 170) };
                _reason.Location = new Point(20, 192); _reason.Size = new Size(420, 50); _reason.Multiline = true; _reason.MaxLength = 1000;
                _status.AutoSize = false; _status.Location = new Point(20, 248); _status.Size = new Size(420, 36);
                _request.Text = "Request approval"; _request.Size = new Size(150, 30); _request.Location = new Point(188, 290);
                close.Location = new Point(344, 290);
                _request.Click += (_, _) => SendRequest(Get(d, "pipe")!);
                Controls.AddRange(new Control[] { label, _reason, _status, _request });
                AcceptButton = _request;
            }
            else close.Location = new Point(344, 178);
            close.Click += (_, _) => Close();
            CancelButton = close;
            Controls.Add(close);
            Shown += (_, _) => Activate();
        }

        private void SendRequest(string pipeName)
        {
            _request.Enabled = false;
            try
            {
                using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
                pipe.Connect(5000);
                var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                writer.WriteLine(JsonSerializer.Serialize(new Dictionary<string, string?> { ["reason"] = _reason.Text.Trim() }));
                reader.ReadLine();
                _status.ForeColor = Color.DarkGreen;
                _status.Text = "Your request was sent to your administrator. You can install the software once it is approved.";
                _reason.Enabled = false;
            }
            catch (Exception ex)
            {
                _status.ForeColor = Color.Firebrick;
                _status.Text = "The request could not be sent: " + ex.Message;
                _request.Enabled = true;
            }
        }
    }

    // ---- native ---------------------------------------------------------------------------------------------

    private const uint CreateNoWindow = 0x08000000, CreateUnicodeEnvironment = 0x00000400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(int sessionId, out IntPtr token);
    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr env);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string? application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
