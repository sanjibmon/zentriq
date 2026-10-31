using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace Zentriq.Agent;

/// <summary>
/// The agent's own Windows service host (instead of System.ServiceProcess.ServiceBase), so a stop request can be held.
/// Windows does not let a service refuse a stop: once a stop control was delivered, the service control manager accepts
/// no other control until the service has stopped (error 1061), whatever the service answers. So while tamper
/// protection is on, a stop request — Stop or Restart in the Services console, net stop, sc stop — is held: the service
/// shows "Stopping" and a code dialog opens on the desktop of the administrators who are signed in
/// (<see cref="StopPrompt"/>). With a valid code from the Zentriq console it stops (or restarts). Without one
/// (cancelled, wrong, nobody to enter it, or after two minutes) it completes the stop and starts again at once, and
/// the attempt is reported.
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
    private const int NoError = 0, ErrorCallNotImplemented = 120, ErrorServiceSpecific = 1066;

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
            // While waiting for a code only shutdown is accepted (a second Stop would change nothing).
            _status.Accepted = state == StateRunning ? AcceptStop | AcceptShutdown | AcceptPowerEvent
                : state == StateStopPending && Volatile.Read(ref _waitingForCode) == 1 && Volatile.Read(ref _stopping) == 0 ? AcceptShutdown : 0;
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

    /// <summary>A stop request from outside the agent: through at once when authorized, otherwise held for the code.</summary>
    private int OnStopRequested()
    {
        if (Volatile.Read(ref _stopping) == 1) return NoError;
        if (StopGate.Allowed(out var why)) { BeginStop(why); return NoError; }
        if (Interlocked.Exchange(ref _waitingForCode, 1) == 0)
        {
            Report(StateStopPending, waitHint: 15000);
            new Thread(WaitForCode) { IsBackground = true, Name = "stop-code" }.Start();
        }
        return NoError;
    }

    /// <summary>A separate process (SYSTEM) that starts the service again once this process has stopped.</summary>
    private static void StartAgainAfterStop(string why)
    {
        Log.Info("The agent service starts again after this stop: " + why + ".");
        var cmd = System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(cmd,
            $"/c for /l %i in (1,1,30) do (ping -n 3 127.0.0.1 >nul & sc query {Program.ServiceName} | find \"STOPPED\" >nul && (net start {Program.ServiceName} & exit /b 0))")
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = System.IO.Path.GetTempPath() });
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
            Report(StateStopPending, waitHint: 15000);   // still working on it (keeps the Services console waiting)
        }
        StopPrompt.CloseAll();
        Volatile.Write(ref _waitingForCode, 0);
        if (Volatile.Read(ref _stopping) == 1) { StopGate.End(); return; }   // Windows is shutting down meanwhile
        if (outcome == StopGate.Outcome.Authorized)
        {
            if (StopGate.RestartRequested)
            {
                StartAgainAfterStop("restart with a code entered on the device");
                BeginStop("restart with a code entered on the device");
            }
            else BeginStop("stopped with a code entered on the device");
            return;
        }
        StopGate.End();
        // The stop cannot be taken back (Windows accepts no other request until the service has stopped): stop and
        // start again at once.
        var reason = outcome == StopGate.Outcome.Cancelled ? "the code dialog was cancelled"
            : shown == 0 ? "no administrator was signed in to enter the code" : "no valid code was entered within two minutes";
        TamperGuard.Record("tamper", $"Someone tried to stop the agent service (Services console, net stop or sc stop) without a code: {reason}. It was started again at once.");
        StartAgainAfterStop("stop without a code");
        BeginStop("stop without a code (starting again)");
    }
}
