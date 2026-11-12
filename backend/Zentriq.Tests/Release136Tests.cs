using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Zentriq.Application.Security;
using Zentriq.Infrastructure.Data;
using Zentriq.Infrastructure.Database;

namespace Zentriq.Tests;

/// <summary>Build 136: user access rules, data scope (row-level security), directory groups mapped to Zentriq groups.</summary>
public sealed class Release136Tests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    public Task InitializeAsync() => _db.InitializeAsync();
    public Task DisposeAsync() => _db.DisposeAsync();

    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Fact]
    public void Sign_in_hours_including_a_range_past_midnight()
    {
        var office = new SigninHours([1, 2, 3, 4, 5], "08:00", "20:00");
        Assert.Null(office.Validate());
        Assert.True(office.Allows(new DateTime(2026, 11, 16, 9, 0, 0)));    // Monday 09:00
        Assert.False(office.Allows(new DateTime(2026, 11, 16, 20, 0, 0)));  // end is exclusive
        Assert.False(office.Allows(new DateTime(2026, 11, 15, 10, 0, 0)));  // Sunday
        var night = new SigninHours([5], "22:00", "06:00");                  // Friday night shift
        Assert.True(night.Allows(new DateTime(2026, 11, 20, 23, 0, 0)));    // Friday 23:00
        Assert.True(night.Allows(new DateTime(2026, 11, 21, 5, 59, 0)));    // Saturday 05:59 belongs to Friday
        Assert.False(night.Allows(new DateTime(2026, 11, 21, 23, 0, 0)));   // Saturday 23:00
        Assert.NotNull(new SigninHours([], "08:00", "20:00").Validate());
        Assert.NotNull(new SigninHours([1], "8am", "20:00").Validate());
        var back = SigninHours.Parse(office.ToJson())!;
        Assert.Equal(office.Days, back.Days);
        Assert.Equal((office.From, office.To), (back.From, back.To));
    }

    [Fact]
    public void Networks_expiry_and_hours_decide_access()
    {
        var now = new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);  // Monday 10:00 UTC
        Assert.Null(AccessRules.None.Denied(now, null, Utc));
        var rules = new AccessRules(now.AddDays(1), new SigninHours([1], "09:00", "17:00"), ["192.168.10.0/24", "10.1.2.3", "fd00::/8"]);
        Assert.Null(rules.Denied(now, IPAddress.Parse("192.168.10.77"), Utc));
        Assert.Null(rules.Denied(now, IPAddress.Parse("::ffff:10.1.2.3"), Utc));
        Assert.Null(rules.Denied(now, IPAddress.Parse("fd12::1"), Utc));
        Assert.Equal("network", rules.Denied(now, IPAddress.Parse("192.168.11.1"), Utc));
        Assert.Equal("outside-hours", rules.Denied(now.AddHours(8), IPAddress.Parse("10.1.2.3"), Utc));
        Assert.Equal("expired", rules.Denied(now.AddDays(2), IPAddress.Parse("10.1.2.3"), Utc));
        Assert.Null(AccessRules.ValidateNetworks([" 10.0.0.0/8 ", "", "2001:db8::/32"], out var clean));
        Assert.Equal(["10.0.0.0/8", "2001:db8::/32"], clean);
        Assert.NotNull(AccessRules.ValidateNetworks(["10.0.0.0/33"], out _));
        Assert.NotNull(AccessRules.ValidateNetworks(["office"], out _));
    }

    [Fact]
    public void Scoped_connection_string_carries_the_user_and_a_small_pool()
    {
        var id = Guid.NewGuid();
        var b = new NpgsqlConnectionStringBuilder(ScopedDataSources.ConnectionStringFor("Host=db;Username=zentriq_app;Password=x;Maximum Pool Size=100", id));
        Assert.Contains($"zentriq.scope_user={id:D}", b.Options);
        Assert.Equal(10, b.MaxPoolSize);
    }

    [Fact]
    public void Updates_are_sorted_into_operating_system_microsoft_software_and_third_party()
    {
        Assert.Equal("os", Zentriq.Infrastructure.Patches.PatchStore.KindOf("windows", "a1b2", "Windows 11"));
        Assert.Equal("os", Zentriq.Infrastructure.Patches.PatchStore.KindOf("windows", "a1b2", "Microsoft Defender Antivirus"));
        Assert.Equal("os", Zentriq.Infrastructure.Patches.PatchStore.KindOf("windows", "a1b2", ".NET Framework 4.8"));
        Assert.Equal("microsoft", Zentriq.Infrastructure.Patches.PatchStore.KindOf("windows", "a1b2", "Microsoft 365 Apps/Office 2019/Office LTSC"));
        Assert.Equal("microsoft", Zentriq.Infrastructure.Patches.PatchStore.KindOf("windows", "a1b2", "Microsoft SQL Server 2019"));
        Assert.Equal("microsoft", Zentriq.Infrastructure.Patches.PatchStore.KindOf("windows", "a1b2", "Microsoft Edge"));
        Assert.Equal("os", Zentriq.Infrastructure.Patches.PatchStore.KindOf("linux", "linux:openssl:3.0.2", "jammy-security"));
        Assert.Equal("thirdparty", Zentriq.Infrastructure.Patches.PatchStore.KindOf("windows", "winget:Google.Chrome:131.0.6778.86", "Google Chrome"));

        var u = Zentriq.Infrastructure.Patches.PatchStore.FromApp(new("Google.Chrome", "Google Chrome", "130.0.6723.117", "131.0.6778.86", "winget"));
        Assert.NotNull(u);
        Assert.Equal("winget:Google.Chrome:131.0.6778.86", Zentriq.Infrastructure.Patches.PatchStore.IdOf(u!, "windows"));
        Assert.Equal("application", u!.Classification);
        Assert.Contains("installed 130.0.6723.117", u.Title);
        Assert.Null(Zentriq.Infrastructure.Patches.PatchStore.FromApp(new("Bad Id With Spaces", "x", "1", "2", "winget")));
        Assert.Null(Zentriq.Infrastructure.Patches.PatchStore.FromApp(new("Vendor.App", "x", "1", "", "winget")));
        Assert.Equal("winget:Google.Chrome:131.0.6778.86", Zentriq.Infrastructure.Patches.PatchStore.NormId("winget:Google.Chrome:131.0.6778.86"));
    }

    [DbFact]
    public async Task A_scoped_user_sees_only_the_devices_of_their_scope()
    {
        await new SqlMigrator(NullLogger<SqlMigrator>.Instance).MigrateAsync(_db.OwnerConnection, _db.App, "1.1.0-test");
        Guid kolkata, mumbai, pcK, pcM, pcNone, tech, admin;
        await using (var conn = new NpgsqlConnection(_db.AppConnection))
        {
            await conn.OpenAsync();
            async Task<Guid> Id(string sql)
            {
                await using var c = new NpgsqlCommand(sql, conn);
                return (Guid)(await c.ExecuteScalarAsync())!;
            }
            kolkata = await Id("INSERT INTO zentriq.locations (name) VALUES ('Kolkata') RETURNING id");
            mumbai = await Id("INSERT INTO zentriq.locations (name) VALUES ('Mumbai') RETURNING id");
            pcK = await Id($"INSERT INTO zentriq.assets (hostname, category, source, location_id) VALUES ('PC-KOL-01', 'endpoint', 'manual', '{kolkata}') RETURNING id");
            pcM = await Id($"INSERT INTO zentriq.assets (hostname, category, source, location_id) VALUES ('PC-MUM-01', 'endpoint', 'manual', '{mumbai}') RETURNING id");
            pcNone = await Id("INSERT INTO zentriq.assets (hostname, category, source) VALUES ('PC-NOWHERE', 'endpoint', 'manual') RETURNING id");
            await using (var c = new NpgsqlCommand($"""
                INSERT INTO zentriq.roles (id, name, description, built_in, scope_location_ids) VALUES ('kolkata-helpdesk', 'Kolkata helpdesk', '', false, ARRAY['{kolkata}']::uuid[]);
                INSERT INTO zentriq.role_permissions (role_id, permission_code) VALUES ('kolkata-helpdesk', 'assets.view');
                INSERT INTO zentriq.asset_changes (asset_id, kind, change, component, item) VALUES ('{pcK}', 'hardware', 'added', 'Memory', 'DIMM 1'), ('{pcM}', 'hardware', 'added', 'Memory', 'DIMM 1');
                """, conn))
                await c.ExecuteNonQueryAsync();
            tech = await Id("INSERT INTO zentriq.users (email, display_name, password_hash) VALUES ('tech@example.test', 'Tech', 'x') RETURNING id");
            admin = await Id("INSERT INTO zentriq.users (email, display_name, password_hash) VALUES ('boss@example.test', 'Boss', 'x') RETURNING id");
            await using (var c = new NpgsqlCommand($"INSERT INTO zentriq.user_roles (user_id, role_id) VALUES ('{tech}', 'kolkata-helpdesk'), ('{admin}', 'admin')", conn))
                await c.ExecuteNonQueryAsync();

            async Task<bool> Scoped(Guid u)
            {
                await using var c = new NpgsqlCommand($"SELECT zentriq.user_is_scoped('{u}')", conn);
                return (bool)(await c.ExecuteScalarAsync())!;
            }
            Assert.True(await Scoped(tech));
            Assert.False(await Scoped(admin));

            // Without the scope setting (agents, worker, unrestricted users) everything is visible.
            await using var all = new NpgsqlCommand("SELECT count(*) FROM zentriq.assets", conn);
            Assert.Equal(3L, await all.ExecuteScalarAsync());
        }

        await using var scoped = new NpgsqlConnection(ScopedDataSources.ConnectionStringFor(_db.AppConnection, tech));
        await scoped.OpenAsync();
        async Task<List<string>> Names(string sql)
        {
            await using var c = new NpgsqlCommand(sql, scoped);
            var list = new List<string>();
            await using var r = await c.ExecuteReaderAsync();
            while (await r.ReadAsync()) list.Add(r.GetString(0));
            return list;
        }
        Assert.Equal(["PC-KOL-01"], await Names("SELECT hostname FROM zentriq.assets ORDER BY 1"));
        // Tables that belong to a device follow the device.
        Assert.Equal(["DIMM 1"], await Names("SELECT item FROM zentriq.asset_changes"));
        Assert.Equal([pcK.ToString()], await Names("SELECT asset_id::text FROM zentriq.asset_changes"));
        // Devices outside the scope cannot be changed or deleted…
        await using (var up = new NpgsqlCommand($"UPDATE zentriq.assets SET hostname = 'X' WHERE id IN ('{pcM}', '{pcNone}')", scoped))
            Assert.Equal(0, await up.ExecuteNonQueryAsync());
        // …nor moved out of the scope.
        await using (var move = new NpgsqlCommand($"UPDATE zentriq.assets SET location_id = '{mumbai}' WHERE id = '{pcK}'", scoped))
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => move.ExecuteNonQueryAsync());
            Assert.Equal("42501", ex.SqlState);
        }

        // A Zentriq group with its own scope replaces the role's scope for its members; a directory group can give it.
        await using (var conn = new NpgsqlConnection(_db.AppConnection))
        {
            await conn.OpenAsync();
            await using var c = new NpgsqlCommand($"""
                DELETE FROM zentriq.user_roles WHERE user_id = '{tech}';
                WITH g AS (INSERT INTO zentriq.user_groups (name, scope_location_ids) VALUES ('Mumbai desk', ARRAY['{mumbai}']::uuid[]) RETURNING id)
                , r AS (INSERT INTO zentriq.user_group_roles (group_id, role_id) SELECT id, 'kolkata-helpdesk' FROM g)
                INSERT INTO zentriq.user_directory_groups (user_id, group_id, provider) SELECT '{tech}', id, 'ldap' FROM g;
                """, conn);
            await c.ExecuteNonQueryAsync();
        }
        Assert.Equal(["PC-MUM-01"], await Names("SELECT hostname FROM zentriq.assets ORDER BY 1"));
    }
}
