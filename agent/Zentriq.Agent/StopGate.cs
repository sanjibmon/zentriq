using System;
using System.Threading;

namespace Zentriq.Agent;

/// <summary>
/// Decides whether a stop request may go through, and carries the code check between the code dialog (through the
/// local control pipe) and the service waiting for it.
/// </summary>
internal static class StopGate
{
    public enum Outcome { Pending, Authorized, Cancelled }

    private static readonly object Gate = new object();
    private static DateTime _authorizedUntil;
    private static string _authorizedFor = "";
    private static bool _pending;
    private static Outcome _outcome = Outcome.Pending;

    /// <summary>The agent itself is about to stop or restart its service (console control, --restart, update).</summary>
    public static void AuthorizeInternal(string why, TimeSpan? window = null)
    {
        lock (Gate) { _authorizedUntil = DateTime.UtcNow + (window ?? TimeSpan.FromMinutes(2)); _authorizedFor = why; }
    }

    /// <summary>Whether a stop request may go through without a code; <paramref name="why"/> says why.</summary>
    public static bool Allowed(out string why)
    {
        lock (Gate)
        {
            if (_authorizedUntil > DateTime.UtcNow) { why = _authorizedFor; return true; }
        }
        if (!UninstallProtection.Enabled) { why = "tamper protection is off"; return true; }
        if (!TamperGuard.Wanted) { why = "tamper protection is paused, or the agent was stopped or disabled with a code"; return true; }
        if (WindowsInstallerRunning()) { why = "Windows Installer is updating or removing the agent"; return true; }
        why = "";
        return false;
    }

    /// <summary>Windows Installer holds this mutex while it installs, updates or removes a product (as SYSTEM).</summary>
    private static bool WindowsInstallerRunning()
    {
        try
        {
            if (Mutex.TryOpenExisting(@"Global\_MSIExecute", out var m)) { m.Dispose(); return true; }
        }
        catch (UnauthorizedAccessException) { return true; }   // exists, but not ours to open
        return false;
    }

    public static bool Pending { get { lock (Gate) return _pending; } }

    public static void Begin()
    {
        lock (Gate) { _pending = true; _outcome = Outcome.Pending; }
    }

    public static void End()
    {
        lock (Gate) { _pending = false; Monitor.PulseAll(Gate); }
    }

    /// <summary>The code dialog: a valid code was entered.</summary>
    public static bool Authorize()
    {
        lock (Gate)
        {
            if (!_pending) return false;
            _outcome = Outcome.Authorized; _pending = false;
            Monitor.PulseAll(Gate);
            return true;
        }
    }

    /// <summary>The code dialog was cancelled.</summary>
    public static void Cancel()
    {
        lock (Gate)
        {
            if (!_pending) return;
            _outcome = Outcome.Cancelled; _pending = false;
            Monitor.PulseAll(Gate);
        }
    }

    public static Outcome Wait(TimeSpan timeout)
    {
        lock (Gate)
        {
            if (_outcome == Outcome.Pending && _pending) Monitor.Wait(Gate, timeout);
            return _outcome;
        }
    }
}
