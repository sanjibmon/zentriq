using System;
using System.Net;
using System.Threading;

namespace Zentriq.Agent;

/// <summary>
/// The agent's live connection to the server: one presence request held open by the server and renewed as soon as it
/// returns. The server shows the device online from the moment the connection is made and offline the moment it closes
/// (or, when it dies silently, within a minute). When an administrator queues work (collect now, an action, an update) the
/// server answers early and the agent checks in at once. Runs beside the regular check-in / inventory schedule.
/// </summary>
internal static class Presence
{
    /// <summary>Set when the network changes: retry a lost connection now.</summary>
    internal static readonly AutoResetEvent Retry = new AutoResetEvent(false);

    public static bool Connected { get; private set; }

    public static void Loop(CancellationToken ct)
    {
        ServerClient? client = null;
        string? clientKey = null;
        var failures = 0;
        DateTime? lostAt = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var state = AgentState.Load();
                var cert = state.Enrolled && !string.IsNullOrEmpty(state.ServerUrl) ? CertificateStore.Find(state.CertThumbprint) : null;
                if (cert is null)
                {
                    Connected = false;
                    if (WaitAny(ct, TimeSpan.FromSeconds(30))) return;
                    continue;
                }
                var key = state.ServerUrl + "|" + state.CertThumbprint + "|" + string.Join(",", state.ServerPins) + "|" + state.ProxyUrl;
                if (client is null || key != clientKey)
                {
                    client?.Dispose();
                    // The server holds the request for about 25 seconds; give up on a dead connection after 60.
                    client = new ServerClient(state.ServerUrl!, state.ServerPins, cert, state.ProxyUrl, timeout: TimeSpan.FromSeconds(60));
                    clientKey = key;
                }
                try
                {
                    var r = client.PresenceAsync(ct).GetAwaiter().GetResult();
                    if (!Connected)
                    {
                        Log.Info(lostAt is { } since
                            ? $"Live connection to the server restored at {state.ServerUrl} (lost for {(int)(DateTime.UtcNow - since).TotalSeconds} s)."
                            : $"Live connection to the server at {state.ServerUrl}.");
                        // After a gap, check in now (reports the outage, picks up settings and queued work).
                        if (lostAt is not null) AgentService.RequestServerWake();
                    }
                    Connected = true;
                    failures = 0;
                    lostAt = null;
                    if (r.Wake)
                    {
                        AgentService.RequestServerWake();
                        // The work stays pending until the check-in has taken it: give it time instead of asking again at once
                        // (still well inside the online grace period).
                        if (WaitAny(ct, TimeSpan.FromSeconds(15))) return;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (ServerException ex) when (ex.Status == HttpStatusCode.NotFound)
                {
                    // A server older than this agent: no live connection, the check-in schedule still applies.
                    Connected = false;
                    if (failures++ == 0) Log.Info("The server does not support live connections yet (older version); using the check-in schedule only.");
                    if (WaitAny(ct, TimeSpan.FromMinutes(30))) return;
                }
                catch (Exception ex)
                {
                    if (Connected || failures == 0)
                    {
                        lostAt = DateTime.UtcNow;
                        Log.Warn("Live connection to the server lost: " + AgentWorker.Describe(ex));
                    }
                    Connected = false;
                    failures++;
                    client?.Dispose();
                    client = null;
                    // Server refused (revoked, certificate): the check-in cycle deals with that; retry slowly.
                    var wait = ex is ServerException se && (se.Status == HttpStatusCode.Forbidden || se.Status == HttpStatusCode.Unauthorized)
                        ? TimeSpan.FromMinutes(10)
                        : TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(2, Math.Min(failures - 1, 4))));   // 5, 10, 20, 40, 60 s
                    if (WaitAny(ct, wait)) return;
                }
            }
        }
        finally
        {
            Connected = false;
            client?.Dispose();   // closes the connection: the server shows the device offline at once
        }
    }

    /// <summary>Waits; returns true when the agent is stopping. The network changing ends the wait early.</summary>
    private static bool WaitAny(CancellationToken ct, TimeSpan timeout) =>
        WaitHandle.WaitAny(new[] { ct.WaitHandle, Retry }, timeout) == 0;
}
