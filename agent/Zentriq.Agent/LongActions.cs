using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Zentriq.Agent;

/// <summary>
/// Patch scans and installations and scripts can take minutes or hours, so they do not run inside the check-in: the
/// action is reported as started, runs on a background thread (one queue per kind: one patch operation and one script
/// at a time), and its result is reported when it ends, over a connection of its own. Check-ins, service actions and
/// remote support go on meanwhile.
/// </summary>
internal static class LongActions
{
    private sealed class Queue
    {
        public readonly BlockingCollection<(AgentAction Action, Func<ServerClient, AgentAction, (bool Ok, string? State, string Message)> Work)> Items = new();
        public readonly HashSet<string> Ids = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly Dictionary<string, Queue> Queues = new(StringComparer.Ordinal);

    /// <summary>Queues the action; returns the result to report now ("started", or already queued).</summary>
    public static (bool Ok, string? State, string Message) Start(AgentAction a, Func<ServerClient, AgentAction, (bool Ok, string? State, string Message)> work)
    {
        Queue q;
        lock (Queues)
        {
            if (Queues.TryGetValue(a.Kind, out var existing)) q = existing;
            else
            {
                q = new Queue();
                Queues[a.Kind] = q;
                var t = new Thread(() => Loop(q)) { IsBackground = true, Name = "Zentriq " + a.Kind };
                t.Start();
            }
            if (!q.Ids.Add(a.Id)) return (true, "started", "Already running on the device.");
        }
        q.Items.Add((a, work));
        return (true, "started", q.Items.Count > 1 ? "Queued on the device (another one is running)." : "Started on the device.");
    }

    /// <summary>The long actions queued or running on this device now (sent with every check-in, so the server can tell
    /// which ones a restart of the agent interrupted).</summary>
    public static string[] Running()
    {
        lock (Queues)
        {
            var list = new List<string>();
            foreach (var q in Queues.Values) list.AddRange(q.Ids);
            return list.ToArray();
        }
    }

    private static void Loop(Queue q)
    {
        foreach (var (a, work) in q.Items.GetConsumingEnumerable())
        {
            (bool Ok, string? State, string Message) result;
            ServerClient? client = null;
            try
            {
                client = Connect();
                result = work(client, a);
            }
            catch (Exception ex) { result = (false, null, ex.Message); }
            Log.Info($"Action {a.Kind} {a.Operation}: {(result.Ok ? "OK" : "FAILED")} - {result.Message}");
            for (var i = 0; i < 5; i++)
            {
                try
                {
                    client ??= Connect();
                    client.ReportActionAsync(a.Id, result.Ok, result.State, result.Message, CancellationToken.None).GetAwaiter().GetResult();
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warn("Could not report the result of " + a.Kind + " " + a.Operation + ": " + ex.Message);
                    client?.Dispose(); client = null;
                    Thread.Sleep(TimeSpan.FromSeconds(30 * (i + 1)));
                }
            }
            client?.Dispose();
            lock (Queues) q.Ids.Remove(a.Id);
        }
    }

    private static ServerClient Connect()
    {
        var state = AgentState.Load();
        var cert = CertificateStore.Find(state.CertThumbprint) ?? throw new InvalidOperationException("The agent certificate was not found.");
        return new ServerClient(state.ServerUrl!, state.ServerPins, cert, state.ProxyUrl, timeout: TimeSpan.FromMinutes(5));
    }
}
