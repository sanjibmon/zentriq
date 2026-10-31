using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace Zentriq.Agent;

/// <summary>
/// The code dialog for a stop or restart of the protected agent service. The service (SYSTEM, session 0) cannot show
/// windows itself, so it starts "ZentriqAgent.exe --stop-prompt" on the desktop of every signed-in administrator — with
/// the administrator's elevated token, so the dialog may use the local control pipe — and the dialog hands the code to
/// the service, which checks it.
/// </summary>
internal static class StopPrompt
{
    private static readonly List<Process> Open = new List<Process>();

    // ---- service side ----------------------------------------------------------------------------------------

    /// <summary>Starts the dialog in the session of each signed-in administrator; returns how many were shown.</summary>
    public static int ShowToAdministrators()
    {
        CloseAll();
        var exe = Path.Combine(TamperGuard.InstallFolder, "ZentriqAgent.exe");
        var shown = 0;
        foreach (var session in ActiveSessions())
        {
            IntPtr user = IntPtr.Zero, admin = IntPtr.Zero, env = IntPtr.Zero;
            try
            {
                if (!WTSQueryUserToken(session, out user)) continue;
                admin = AdministratorToken(user);
                if (admin == IntPtr.Zero) continue;   // not an administrator: cannot stop the service anyway
                if (!CreateEnvironmentBlock(out env, admin, false)) env = IntPtr.Zero;
                var si = new StartupInfo { cb = Marshal.SizeOf(typeof(StartupInfo)), lpDesktop = @"winsta0\default" };
                var cmd = new StringBuilder($"\"{exe}\" --stop-prompt");
                if (!CreateProcessAsUser(admin, null, cmd, IntPtr.Zero, IntPtr.Zero, false, CreateNoWindow | CreateUnicodeEnvironment, env, TamperGuard.InstallFolder, ref si, out var pi))
                {
                    Log.Warn($"Could not show the code dialog in session {session}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
                    continue;
                }
                CloseHandle(pi.hThread);
                try { lock (Open) Open.Add(Process.GetProcessById(pi.dwProcessId)); } catch (ArgumentException) { }
                CloseHandle(pi.hProcess);
                shown++;
            }
            finally
            {
                if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
                if (admin != IntPtr.Zero && admin != user) CloseHandle(admin);
                if (user != IntPtr.Zero) CloseHandle(user);
            }
        }
        return shown;
    }

    public static int OpenDialogs()
    {
        lock (Open) return Open.Count(p => { try { return !p.HasExited; } catch (Exception) { return false; } });
    }

    public static void CloseAll()
    {
        lock (Open)
        {
            foreach (var p in Open) { try { if (!p.HasExited) p.Kill(); } catch (Exception) { } p.Dispose(); }
            Open.Clear();
        }
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
                if (info.State == WtsActive && info.SessionId != 0) result.Add(info.SessionId);
            }
        }
        finally { WTSFreeMemory(list); }
        return result;
    }

    /// <summary>The user's full administrator token (the elevated half under UAC), or zero when the user is not an administrator.</summary>
    private static IntPtr AdministratorToken(IntPtr user)
    {
        if (GetTokenInt(user, TokenElevationType, out var type, 4, out _) && type == ElevationLimited
            && GetTokenPtr(user, TokenLinkedToken, out var linked, IntPtr.Size, out _) && linked != IntPtr.Zero)
            return linked;   // SYSTEM (SeTcbPrivilege) receives a primary token
        using var id = new WindowsIdentity(user);
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator) ? user : IntPtr.Zero;
    }

    // ---- dialog side (ZentriqAgent.exe --stop-prompt) --------------------------------------------------------

    public static int RunDialog()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var form = new CodeForm();
        Application.Run(form);
        return 0;
    }

    private static (bool Ok, string Message, bool StopRequest) Send(string action, string? code, bool restart = false)
    {
        using var pipe = new NamedPipeClientStream(".", TamperGuard.PipeName, PipeDirection.InOut);
        pipe.Connect(5000);
        var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        writer.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object?> { ["action"] = action, ["code"] = code, ["restart"] = restart }));
        using var doc = JsonDocument.Parse(reader.ReadLine() ?? "{}");
        var r = doc.RootElement;
        var waiting = r.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Object && st.TryGetProperty("stopRequest", out var sr) && sr.ValueKind == JsonValueKind.True;
        return (r.TryGetProperty("ok", out var o) && o.GetBoolean(), r.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "", waiting);
    }

    private sealed class CodeForm : Form
    {
        private readonly TextBox _code = new TextBox();
        private readonly Label _message = new Label();
        private readonly Button _stop = new Button();
        private readonly Button _restart = new Button();
        private readonly Timer _watch = new Timer { Interval = 3000 };
        private bool _done;

        public CodeForm()
        {
            Text = "Zentriq Agent";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            ShowInTaskbar = true;
            Font = new Font("Segoe UI", 9.5f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(440, 250);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

            var title = new Label { Text = "A code is needed to stop or restart the Zentriq Agent", Font = new Font("Segoe UI", 11f, FontStyle.Bold), AutoSize = false, Location = new Point(20, 16), Size = new Size(400, 24) };
            var text = new Label
            {
                Text = "Tamper protection is on for this device, so the request was declined. Enter the one-hour code from the " +
                       "Zentriq console (Agents › the device's agent menu, or Agents › Uninstall code) to stop or restart the service.",
                AutoSize = false, Location = new Point(20, 46), Size = new Size(400, 56),
            };
            var label = new Label { Text = "Code (16 letters and digits, e.g. ABCD-EFGH-JKMN-PQRS):", AutoSize = true, Location = new Point(20, 108) };
            _code.Location = new Point(20, 130); _code.Size = new Size(400, 26); _code.CharacterCasing = CharacterCasing.Upper; _code.MaxLength = 24;
            _message.AutoSize = false; _message.Location = new Point(20, 162); _message.Size = new Size(400, 38); _message.ForeColor = Color.Firebrick;
            _restart.Text = "Restart service"; _restart.Size = new Size(124, 30); _restart.Location = new Point(20, 206);
            _stop.Text = "Stop service"; _stop.Size = new Size(110, 30); _stop.Location = new Point(206, 206);
            var cancel = new Button { Text = "Cancel", Size = new Size(96, 30), Location = new Point(324, 206), DialogResult = DialogResult.Cancel };
            _stop.Click += (_, _) => Submit(restart: false);
            _restart.Click += (_, _) => Submit(restart: true);
            cancel.Click += (_, _) => Close();
            AcceptButton = _stop; CancelButton = cancel;
            Controls.AddRange(new Control[] { title, text, label, _code, _message, _restart, _stop, cancel });

            // Closes by itself when the request ends elsewhere (another administrator's dialog, or the two minutes passed).
            _watch.Tick += (_, _) =>
            {
                try
                {
                    if (!Send("status", null).StopRequest) { _done = true; Close(); }
                }
                catch (Exception) { }
            };
            Shown += (_, _) => { _watch.Start(); Activate(); _code.Focus(); };
            FormClosed += (_, _) =>
            {
                _watch.Stop();
                if (!_done) { try { Send("cancel-stop", null); } catch (Exception) { } }
            };
        }

        private void Submit(bool restart)
        {
            _message.Text = "";
            _stop.Enabled = _restart.Enabled = false;
            try
            {
                var (ok, message, _) = Send("authorize-stop", _code.Text.Trim(), restart);
                if (ok) { _done = true; Close(); return; }
                _message.Text = message;
                _code.SelectAll(); _code.Focus();
            }
            catch (Exception ex) { _message.Text = "The Zentriq Agent service did not answer: " + ex.Message; }
            finally { _stop.Enabled = _restart.Enabled = true; }
        }
    }

    // ---- native ---------------------------------------------------------------------------------------------

    private const int WtsActive = 0;
    private const int TokenElevationType = 18, TokenLinkedToken = 19, ElevationLimited = 3;
    private const uint CreateNoWindow = 0x08000000, CreateUnicodeEnvironment = 0x00000400;

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo { public int SessionId; public IntPtr WinStationName; public int State; }

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
    private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);
    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(int sessionId, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "GetTokenInformation")]
    private static extern bool GetTokenInt(IntPtr token, int cls, out int value, int length, out int returned);
    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "GetTokenInformation")]
    private static extern bool GetTokenPtr(IntPtr token, int cls, out IntPtr value, int length, out int returned);
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
