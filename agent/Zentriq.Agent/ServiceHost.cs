using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace Zentriq.Agent;

/// <summary>
/// The agent's own Windows service host (instead of System.ServiceProcess.ServiceBase, which cannot decline a stop).
/// While tamper protection is on, a stop request — Stop or Restart in the Services console, net stop, sc stop — is
/// declined at once ("Access is denied"; the service never enters "Stopping", which Windows could not take back), and a
/// code dialog opens on the desktop of the administrators who are signed in (<see cref="StopPrompt"/>). With a valid
/// code from the Zentriq console the agent then stops — or restarts — itself. Without a code (cancelled, wrong, nobody
/// to enter it, or after two minutes) nothing happens: it keeps running and the attempt is reported.
/// Stops that are already authorized go straight through: protection off or paused, a stop or disable with a code,
/// console control, the agent's own restart and update, and Windows Installer (updates, uninstall with a code).
/// </summary>
internal sealed partial class AgentService
{
    private const int ServiceWin32OwnProcess = 0x10;
    private const int StateStopped = 1, StateStartPending = 2, StateStopPending = 3, StateRunning = 4;
    private const int AcceptStop = 0x1, AcceptShutdown = 0x4, AcceptPowerEvent = 0x40;
    private const int ControlStop = 1, ControlInterrogate = 4, ControlShutdown = 5, ControlPowerEvent = 13;
    private const int PbtResumeSuspend = 7, PbtResumeAutomatic = 18;
    private const int NoError = 0, ErrorAccessDenied = 5, ErrorCallNotImplemented = 120, ErrorServiceSpecific = 1066;

    /// <summary>How long a stop request waits for the code before the service carries on.</summary>
    public static readonly TimeSpan CodeWait = TimeSpan.FromMinutes(2);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public int Type, State, Accepted, Win32ExitCode, SpecificExitCode, CheckPoint, WaitHint;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ServiceMainProc(int argc, IntPtr argv);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int HandlerProc(int control, int eventType, IntPtr eventData, IntPtr context);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceTableEntry
    {
        public string? Name;
        public ServiceMainProc? Proc;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool StartServiceCtrlDispatcher([In] ServiceTableEntry[] table);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr RegisterServiceCtrlHandlerEx(string name, HandlerProc handler, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus status);

    // The delegates are kept alive for the life of the process (native code holds pointers to them).
    private static ServiceMainProc? _mainProc;
    private static HandlerProc? _handlerProc;
    private static AgentService? _service;

    private readonly object _statusGate = new object();
    private readonly ManualResetEvent _stopped = new ManualResetEvent(false);
    private IntPtr _handle;
    private ServiceStatus _status;
    private int _stopping;      // 1 once the stop has begun
    private int _waitingForCode;

    private AgentService() { }

    /// <summary>Entry point when started by the service control manager.</summary>
    public static int RunAsService()
    {
        _service = new AgentService();
        _mainProc = _service.ServiceMain;
        _handlerProc = _service.Handler;
        var table = new[] { new ServiceTableEntry { Name = Program.ServiceName, Proc = _mainProc }, new ServiceTableEntry() };
        if (!StartServiceCtrlDispatcher(table))
        {
            var error = Marshal.GetLastWin32Error();
            Log.Error("The agent could not connect to the service control manager", new Win32Exception(error));
            return 1;
        }
        return 0;
    }

    private void ServiceMain(int argc, IntPtr argv)
    {
        _handle = RegisterServiceCtrlHandlerEx(Program.ServiceName, _handlerProc!, IntPtr.Zero);
        if (_handle == IntPtr.Zero) { Log.Error("RegisterServiceCtrlHandlerEx failed", new Win32Exception(Marshal.GetLastWin32Error())); return; }
        Report(StateStartPending, waitHint: 30000);
        try { OnStart(); }
        catch (Exception ex)
        {
            Log.Error("The agent service could not start", ex);
            Report(StateStopped, exitCode: ErrorServiceSpecific, specific: 1);
            return;
        }
        Report(StateRunning);
        _stopped.WaitOne();
        Report(StateStopped);
    }

    private int Handler(int control, int eventType, IntPtr eventData, IntPtr context)
    {
        switch (control)
        {
            case ControlInterrogate:
                lock (_statusGate) SetServiceStatus(_handle, ref _status);
                return NoError;
            case ControlShutdown:
                BeginStop("Windows is shutting down");
                return NoError;
            case ControlPowerEvent:
                if (eventType == PbtResumeSuspend || eventType == PbtResumeAutomatic) RequestWake("Resumed from sleep");
                return NoError;
            case ControlStop:
                return OnStopRequested();
            default:
                return ErrorCallNotImplemented;
        }
    }

    private void Report(int state, int waitHint = 0, int exitCode = 0, int specific = 0)
    {
        lock (_statusGate)
        {
            var sameState = _status.State == state;
            _status.Type = ServiceWin32OwnProcess;
            _status.State = state;
            _status.Accepted = state == StateRunning ? AcceptStop | AcceptShutdown | AcceptPowerEvent : 0;
            _status.Win32ExitCode = exitCode;
            _status.SpecificExitCode = specific;
            _status.CheckPoint = state == StateStartPending || state == StateStopPending ? (sameState ? _status.CheckPoint + 1 : 1) : 0;
            _status.WaitHint = waitHint;
            if (!SetServiceStatus(_handle, ref _status)) Log.Warn("SetServiceStatus: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }
    }

    /// <summary>Stops without asking (authorized stops and Windows shutdown).</summary>
    private void BeginStop(string why)
    {
        if (Interlocked.Exchange(ref _stopping, 1) == 1) return;
        Log.Info("Stopping: " + why + ".");
        Report(StateStopPending, waitHint: 30000);
        new Thread(() =>
        {
            try { OnStop(); }
            catch (Exception ex) { Log.Error("Error while stopping", ex); }
            _stopped.Set();
        }) { IsBackground = true, Name = "stop" }.Start();
    }

    /// <summary>
    /// A stop request from outside the agent: through at once when authorized; otherwise declined, and the code dialog
    /// opens (the service then stops or restarts itself when the code is entered).
    /// </summary>
    private int OnStopRequested()
    {
        if (Volatile.Read(ref _stopping) == 1) return NoError;
        if (StopGate.Allowed(out var why)) { BeginStop(why); return NoError; }
        if (Interlocked.Exchange(ref _waitingForCode, 1) == 0)
            new Thread(WaitForCode) { IsBackground = true, Name = "stop-code" }.Start();
        return ErrorAccessDenied;
    }

    private void WaitForCode()
    {
        StopGate.Begin();
        var shown = 0;
        try { shown = StopPrompt.ShowToAdministrators(); }
        catch (Exception ex) { Log.Warn("Could not show the code dialog: " + ex.Message); }
        Log.Info($"Stop requested while tamper protection is on: waiting up to {CodeWait.TotalMinutes:0} minutes for a code ({shown} dialog(s) shown).");
        var deadline = DateTime.UtcNow + (shown == 0 ? TimeSpan.FromSeconds(20) : CodeWait);
        var outcome = StopGate.Outcome.Pending;
        while (DateTime.UtcNow < deadline && Volatile.Read(ref _stopping) == 0)
        {
            outcome = StopGate.Wait(TimeSpan.FromSeconds(3));
            if (outcome != StopGate.Outcome.Pending) break;
            if (shown > 0 && StopPrompt.OpenDialogs() == 0 && DateTime.UtcNow > deadline - CodeWait + TimeSpan.FromSeconds(5)) { outcome = StopGate.Outcome.Cancelled; break; }
        }
        StopPrompt.CloseAll();
        if (outcome == StopGate.Outcome.Authorized)
        {
            Volatile.Write(ref _waitingForCode, 0);
            if (StopGate.RestartRequested)
            {
                // A separate process stops and starts the service again (this process ends with the stop).
                StopGate.AuthorizeInternal("restarted with a code entered on the device");
                var cmd = System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(cmd, $"/c ping -n 2 127.0.0.1 >nul & net stop {Program.ServiceName} & net start {Program.ServiceName}")
                { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = System.IO.Path.GetTempPath() });
            }
            else BeginStop("stopped with a code entered on the device");
            return;
        }
        StopGate.End();
        Volatile.Write(ref _waitingForCode, 0);
        if (Volatile.Read(ref _stopping) == 1) return;   // Windows is shutting down meanwhile
        var reason = outcome == StopGate.Outcome.Cancelled ? "the code dialog was cancelled"
            : shown == 0 ? "no administrator was signed in to enter the code" : "no valid code was entered within two minutes";
        TamperGuard.Record("tamper", $"Someone tried to stop the agent service (Services console, net stop or sc stop) without a code: {reason}. It keeps running.");
    }
}
