// Experiment: how the Service Control Manager treats a service that declines SERVICE_CONTROL_STOP.
// Mode (argument in the service's ImagePath): the handler's answer to the first two stop requests.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

static class TestSvc
{
    [StructLayout(LayoutKind.Sequential)] struct Status { public int Type, State, Accepted, Exit, Specific, Check, Wait; }
    delegate void MainProc(int argc, IntPtr argv);
    delegate int HandlerProc(int control, int type, IntPtr data, IntPtr ctx);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct Entry { public string Name; public MainProc Proc; }
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool StartServiceCtrlDispatcher(Entry[] t);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr RegisterServiceCtrlHandlerEx(string n, HandlerProc h, IntPtr c);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetServiceStatus(IntPtr h, ref Status s);

    static MainProc _main; static HandlerProc _handler; static IntPtr _h; static Status _s;
    static readonly ManualResetEvent Done = new ManualResetEvent(false);
    static int _stops; static int _mode; static string _log = @"C:\scm-exp.log";

    static void Log(string m) { File.AppendAllText(_log, DateTime.Now.ToString("HH:mm:ss.fff") + " " + m + Environment.NewLine); }

    static int Main(string[] args)
    {
        _mode = args.Length > 0 ? int.Parse(args[0]) : 5;
        _main = ServiceMain; _handler = Handler;
        StartServiceCtrlDispatcher(new[] { new Entry { Name = "ZqScmExp", Proc = _main }, new Entry() });
        return 0;
    }

    static void Report(int state, int accepted)
    {
        _s.Type = 0x10; _s.State = state; _s.Accepted = accepted; _s.Check = 0; _s.Wait = 0;
        var ok = SetServiceStatus(_h, ref _s);
        Log("SetServiceStatus state=" + state + " ok=" + ok + (ok ? "" : " err=" + Marshal.GetLastWin32Error()));
    }

    static void ServiceMain(int argc, IntPtr argv)
    {
        _h = RegisterServiceCtrlHandlerEx("ZqScmExp", _handler, IntPtr.Zero);
        Report(4, 0x1 | 0x4);
        Log("running, mode " + _mode);
        Done.WaitOne();
        Report(1, 0);
    }

    static int Handler(int control, int type, IntPtr data, IntPtr ctx)
    {
        if (control == 4) { SetServiceStatus(_h, ref _s); return 0; }
        if (control != 1) return 120;
        var n = Interlocked.Increment(ref _stops);
        if (n <= 2)
        {
            Log("stop #" + n + " declined with " + _mode);
            if (_mode == 0) { Report(4, 0x1 | 0x4); return 0; }   // NO_ERROR, re-report running
            return _mode;
        }
        Log("stop #" + n + " accepted");
        Report(3, 0);
        new Thread(() => { Thread.Sleep(500); Done.Set(); }).Start();
        return 0;
    }
}
