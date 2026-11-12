using System.Collections.Concurrent;
using Npgsql;

namespace Zentriq.Infrastructure.Data;

/// <summary>
/// Connections for console users limited by a data scope: each such user gets a small pool whose connections carry
/// <c>zentriq.scope_user</c> and switch to the <c>&lt;app role&gt;_scoped</c> role, which PostgreSQL row-level security uses to show only that user's devices
/// (migration V0043). Every other connection (agents, the worker, unrestricted users) uses the normal data source.
/// </summary>
public sealed class ScopedDataSources(Func<string> connectionString) : IDisposable
{
    private readonly ConcurrentDictionary<Guid, Lazy<NpgsqlDataSource>> _sources = new();

    public NpgsqlDataSource For(Guid userId) =>
        _sources.GetOrAdd(userId, id => new Lazy<NpgsqlDataSource>(() => Build(connectionString(), id))).Value;

    /// <summary>The connection string for a scoped user: the scope setting, a small pool of its own.</summary>
    public static string ConnectionStringFor(string baseConnectionString, Guid userId)
    {
        var b = new NpgsqlConnectionStringBuilder(baseConnectionString);
        // The application role's "_scoped" role (created by Setup) is the one row-level security filters.
        var option = $"-c role={b.Username}_scoped -c zentriq.scope_user={userId:D}";
        b.Options = string.IsNullOrWhiteSpace(b.Options) ? option : b.Options + " " + option;
        b.MaxPoolSize = Math.Min(b.MaxPoolSize, 10);
        b.ConnectionIdleLifetime = 120;
        return b.ConnectionString;
    }

    private static NpgsqlDataSource Build(string cs, Guid userId) => new NpgsqlDataSourceBuilder(ConnectionStringFor(cs, userId)).Build();

    public void Dispose()
    {
        foreach (var s in _sources.Values)
            if (s.IsValueCreated) s.Value.Dispose();
    }
}
