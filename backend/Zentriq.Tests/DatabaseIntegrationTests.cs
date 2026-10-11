using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Zentriq.Domain;
using Zentriq.Infrastructure.Database;
using Zentriq.Infrastructure.Jobs;

namespace Zentriq.Tests;

/// <summary>Skipped unless ZENTRIQ_TEST_PG points at a server where this role can create roles and databases.</summary>
public sealed class DbFactAttribute : FactAttribute
{
    public DbFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ZENTRIQ_TEST_PG")))
            Skip = "ZENTRIQ_TEST_PG is not set (database integration test).";
    }
}

/// <summary>Creates a throw-away database with owner/app roles exactly as zentriqctl does.</summary>
public sealed class TestDatabase : IAsyncLifetime
{
    private readonly string _admin = Environment.GetEnvironmentVariable("ZENTRIQ_TEST_PG") ?? "";
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..10];
    public string Db => $"zq_test_{_suffix}";
    public string Owner => $"zq_owner_{_suffix}";
    public string App => $"zq_app_{_suffix}";
    private readonly string _ownerPw = "Ow-" + Guid.NewGuid().ToString("N");
    private readonly string _appPw = "Ap-" + Guid.NewGuid().ToString("N");

    public string OwnerConnection => new NpgsqlConnectionStringBuilder(_admin) { Username = Owner, Password = _ownerPw, Database = Db, Pooling = false }.ConnectionString;
    public string AppConnection => new NpgsqlConnectionStringBuilder(_admin) { Username = App, Password = _appPw, Database = Db, Pooling = false }.ConnectionString;

    public async Task InitializeAsync()
    {
        if (_admin.Length == 0) return;
        await using (var c = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_admin) { Database = "postgres", Pooling = false }.ConnectionString))
        {
            await c.OpenAsync();
            await Exec(c, $"CREATE ROLE {Owner} LOGIN PASSWORD '{_ownerPw}' NOSUPERUSER NOCREATEDB NOCREATEROLE");
            await Exec(c, $"CREATE ROLE {App} LOGIN PASSWORD '{_appPw}' NOSUPERUSER NOCREATEDB NOCREATEROLE");
            await Exec(c, $"CREATE DATABASE {Db} OWNER {Owner} ENCODING 'UTF8' TEMPLATE template0");
        }
        await using (var c = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_admin) { Database = Db, Pooling = false }.ConnectionString))
        {
            await c.OpenAsync();
            await Exec(c, "CREATE EXTENSION IF NOT EXISTS pgcrypto WITH SCHEMA public");
            await Exec(c, "REVOKE CREATE ON SCHEMA public FROM PUBLIC");
            await Exec(c, $"REVOKE ALL ON DATABASE {Db} FROM PUBLIC");
            await Exec(c, $"GRANT CONNECT, TEMPORARY ON DATABASE {Db} TO {Owner}, {App}");
            await Exec(c, $"GRANT CREATE ON DATABASE {Db} TO {Owner}");
        }
    }

    public async Task DisposeAsync()
    {
        if (_admin.Length == 0) return;
        NpgsqlConnection.ClearAllPools();
        await using var c = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_admin) { Database = "postgres", Pooling = false }.ConnectionString);
        await c.OpenAsync();
        await Exec(c, $"DROP DATABASE IF EXISTS {Db} WITH (FORCE)");
        await Exec(c, $"DROP ROLE IF EXISTS {App}");
        await Exec(c, $"DROP ROLE IF EXISTS {Owner}");
    }

    public static async Task Exec(NpgsqlConnection c, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }
}

public sealed class DatabaseIntegrationTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    public Task InitializeAsync() => _db.InitializeAsync();
    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<int> Migrate() => new SqlMigrator(NullLogger<SqlMigrator>.Instance).MigrateAsync(_db.OwnerConnection, _db.App, "1.0.0-test");

    [DbFact]
    public async Task Migrations_apply_once_and_are_idempotent()
    {
        var first = await Migrate();
        Assert.Equal(SqlMigrator.LatestVersion, first);
        Assert.Equal(0, await Migrate());
        await using var c = new NpgsqlConnection(_db.AppConnection);
        await c.OpenAsync();
        var status = await new SqlMigrator(NullLogger<SqlMigrator>.Instance).GetStatusAsync(c);
        Assert.True(status.UpToDate);
    }

    [DbFact]
    public async Task Application_role_cannot_change_schema_or_trial_history()
    {
        await Migrate();
        await using var c = new NpgsqlConnection(_db.AppConnection);
        await c.OpenAsync();
        await Assert.ThrowsAsync<PostgresException>(() => TestDatabase.Exec(c, "CREATE TABLE zentriq.evil (id int)"));
        await Assert.ThrowsAsync<PostgresException>(() => TestDatabase.Exec(c, "DROP TABLE zentriq.schema_migrations"));
    }

    [DbFact]
    public async Task Jobs_are_leased_once_and_recovered_after_lease_expiry()
    {
        await Migrate();
        await using var ds = NpgsqlDataSource.Create(_db.AppConnection);
        var q = new JobQueue(ds);
        var id = await q.EnqueueAsync(new EnqueueRequest(JobTypes.SelfTest, new { }, "it:" + Guid.NewGuid(), Priority: 1, MaxAttempts: 3, TimeoutSeconds: 30));
        // Idempotency key: same key returns the same job.
        var leased = await q.LeaseNextAsync("w1", TimeSpan.FromMilliseconds(200), maintenance: false);
        Assert.Equal(id, leased!.Id);
        Assert.Null(await q.LeaseNextAsync("w2", TimeSpan.FromSeconds(30), maintenance: false));
        await Task.Delay(700);
        Assert.Equal(1, await q.RecoverExpiredLeasesAsync());
        var again = await q.LeaseNextAsync("w2", TimeSpan.FromSeconds(30), maintenance: false);
        Assert.Equal(id, again!.Id);
        await q.CompleteAsync(id, "w2", new { ok = true });
        // Maintenance jobs are never handed to the worker service.
        await q.EnqueueAsync(new EnqueueRequest(JobTypes.MaintenanceBackup, new { }, "it:" + Guid.NewGuid(), Priority: 1, MaxAttempts: 1, TimeoutSeconds: 30));
        Assert.Null(await q.LeaseNextAsync("w3", TimeSpan.FromSeconds(30), maintenance: false));
    }
}
