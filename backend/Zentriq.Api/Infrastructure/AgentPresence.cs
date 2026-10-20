using System.Collections.Concurrent;

namespace Zentriq.Api.Infrastructure;

/// <summary>
/// Live connections of Zentriq Agents (POST /api/agent/v1/presence, a long poll over the agent's mTLS channel).
/// An agent holds one request open at a time and renews it as soon as it returns, so the server knows at once when an
/// agent connects and — when the connection closes — when it goes away. A connection that dies silently (cable pulled,
/// Wi-Fi gone) is noticed when the agent fails to renew it within <see cref="OnlineGrace"/>.
/// </summary>
public sealed class AgentPresence
{
    /// <summary>How long the server holds a presence request before answering (the agent renews it at once).</summary>
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(25);
    /// <summary>An agent stays online for this long after its latest presence request started.</summary>
    public static readonly TimeSpan OnlineGrace = TimeSpan.FromSeconds(45);

    /// <summary>The process-wide instance (also registered for dependency injection).</summary>
    public static AgentPresence Instance { get; } = new();

    private readonly ConcurrentDictionary<Guid, int> _connections = new();
    private readonly object _gate = new();
    private TaskCompletionSource _nudge = New();

    private static TaskCompletionSource New() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when work was queued for agents (held requests then check whether it is theirs).</summary>
    public Task Nudged { get { lock (_gate) return _nudge.Task; } }

    /// <summary>Work was queued for one or more agents (an action, "collect now", an update): wake the held requests.</summary>
    public void Nudge()
    {
        TaskCompletionSource old;
        lock (_gate) { old = _nudge; _nudge = New(); }
        old.TrySetResult();
    }

    public void Enter(Guid agentId) => _connections.AddOrUpdate(agentId, 1, (_, n) => n + 1);

    /// <summary>Ends one connection; true when it was the agent's last one.</summary>
    public bool Leave(Guid agentId)
    {
        var n = _connections.AddOrUpdate(agentId, 0, (_, v) => v - 1);
        if (n > 0) return false;
        _connections.TryRemove(new KeyValuePair<Guid, int>(agentId, n));
        return true;
    }

    public bool IsConnected(Guid agentId) => _connections.TryGetValue(agentId, out var n) && n > 0;
    public int Count => _connections.Count;
}
