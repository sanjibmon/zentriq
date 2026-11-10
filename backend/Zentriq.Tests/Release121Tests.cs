using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Zentriq.Application.Licensing;
using Zentriq.Domain;
using Zentriq.Domain.Licensing;
using Zentriq.Infrastructure.Database;
using Zentriq.Infrastructure.Secrets;
using Zentriq.Infrastructure.SecurityPack;
using Zentriq.Inventory;

namespace Zentriq.Tests;

/// <summary>Build 121: the Security Pack (separately licensed add-on), its rules and what the server keeps.</summary>
public sealed class Release121Tests
{
    private static LicenseStatus Licensed(string? edition, params string[] modules) =>
        new(LicenseMode.Licensed, "", null, null, Editions.Expand(edition, modules), new Dictionary<string, int>(), "L1", "C", edition);

    [Fact]
    public void Security_pack_is_an_add_on_not_part_of_an_edition()
    {
        Assert.False(Licensed(Editions.Advanced).ModuleEnabled(Modules.SecurityPack));
        Assert.False(Licensed(Editions.Standard).ModuleEnabled(Modules.SecurityPack));
        Assert.True(Licensed(Editions.Advanced, Modules.SecurityPack).ModuleEnabled(Modules.SecurityPack));
        Assert.Contains(Modules.SecurityPack, Modules.All);
        // The trial has it where the Secure section is installed.
        var trial = new LicenseStatus(LicenseMode.Trial, "", null, 14, ["*"], new Dictionary<string, int>(), null, null, "trial");
        Assert.True(trial.RestrictTo(ProductSections.All).ModuleEnabled(Modules.SecurityPack));
        Assert.True(trial.RestrictTo([.. ProductSections.Standard, ProductSections.Secure]).ModuleEnabled(Modules.SecurityPack));
        Assert.False(trial.RestrictTo(ProductSections.Standard).ModuleEnabled(Modules.SecurityPack));
        // An Advanced license with the add-on on a Standard installation: not installed, not usable.
        Assert.False(Licensed(Editions.Advanced, Modules.SecurityPack).RestrictTo(ProductSections.Standard).ModuleEnabled(Modules.SecurityPack));
        foreach (var p in new[] { Permissions.PrivilegeView, Permissions.PrivilegeManage, Permissions.BitLockerView, Permissions.BitLockerManage, Permissions.BitLockerRecover, Permissions.DeviceControlView, Permissions.DeviceControlManage })
            Assert.Contains(p, Permissions.All);
    }

    [Fact]
    public void Elevation_rules_match_hash_publisher_and_path()
    {
        var rules = new List<ElevationRule>
        {
            new() { Id = "1", Name = "Wireshark", Kind = "publisher", Value = "Wireshark Foundation", FileName = "Wireshark*.exe" },
            new() { Id = "2", Name = "Tool", Kind = "hash", Value = new string('a', 64) },
            new() { Id = "3", Name = "Vendor", Kind = "path", Value = @"C:\Program Files\Vendor\*" },
            new() { Id = "4", Name = "Used up", Kind = "hash", Value = new string('b', 64), UsesLeft = 0 },
        };
        Assert.Equal("1", SecurityPackRules.Match(rules, @"C:\Users\ana\Downloads\Wireshark-4.4.2-x64.exe", "x", "Wireshark Foundation")?.Id);
        Assert.Null(SecurityPackRules.Match(rules, @"C:\Users\ana\Downloads\other.exe", "x", "Wireshark Foundation"));
        Assert.Equal("2", SecurityPackRules.Match(rules, @"D:\x.exe", new string('A', 64), null)?.Id);
        Assert.Equal("3", SecurityPackRules.Match(rules, @"C:\Program Files\Vendor\bin\tool.exe", null, null)?.Id);
        Assert.Null(SecurityPackRules.Match(rules, @"D:\y.exe", new string('b', 64), null));
    }

    [Fact]
    public void Administrators_that_stay_and_go()
    {
        var p = new PrivilegePolicy { Mode = "enforce", AllowedAdmins = { @"CORP\Workstation Admins", "it-*", "S-1-5-21-1-2-3-1500" } };
        p.Grants.Add(new AdminGrant { User = @"CORP\ana", Until = DateTimeOffset.UtcNow.AddHours(1) });
        p.Grants.Add(new AdminGrant { User = @"CORP\old", Until = DateTimeOffset.UtcNow.AddHours(-1) });
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("kept", SecurityPackRules.AdminStatus(@"PC1\Administrator", "S-1-5-21-11-22-33-500", p, now));
        Assert.Equal("kept", SecurityPackRules.AdminStatus(@"CORP\Domain Admins", "S-1-5-21-44-55-66-512", p, now));
        Assert.Equal("allowed", SecurityPackRules.AdminStatus(@"CORP\Workstation Admins", "S-1-5-21-44-55-66-3001", p, now));
        Assert.Equal("allowed", SecurityPackRules.AdminStatus(@"CORP\it-raj", "S-1-5-21-44-55-66-3002", p, now));
        Assert.Equal("allowed", SecurityPackRules.AdminStatus(@"PC1\helper", "S-1-5-21-1-2-3-1500", p, now));
        Assert.Equal("granted", SecurityPackRules.AdminStatus(@"CORP\ana", "S-1-5-21-44-55-66-4000", p, now));
        Assert.Equal("removed", SecurityPackRules.AdminStatus(@"CORP\old", "S-1-5-21-44-55-66-4001", p, now));
        Assert.Equal("removed", SecurityPackRules.AdminStatus(@"PC1\localuser", "S-1-5-21-11-22-33-1001", p, now));
        p.Mode = "audit";
        Assert.Equal("to_remove", SecurityPackRules.AdminStatus(@"PC1\localuser", "S-1-5-21-11-22-33-1001", p, now));
        Assert.True(SecurityPackRules.SameAccount("ana", @"CORP\ana"));
        Assert.False(SecurityPackRules.SameAccount(@"PC1\ana", @"CORP\ana"));
    }

    [Fact]
    public void Usb_access_from_mode_and_allowed_devices()
    {
        var (vp, serial) = SecurityPackRules.ParseUsbId(@"USB\VID_0781&PID_5581\4C530001231122108455");
        Assert.Equal(("VID_0781&PID_5581", "4C530001231122108455"), (vp, serial));
        Assert.Null(SecurityPackRules.ParseUsbId(@"USB\VID_0781&PID_5581\6&2B0A2C4F&0&2").Serial);   // no serial: Windows made one up
        var p = new UsbPolicy { Mode = "block", Allowed = { new UsbAllow { DeviceId = @"VID_0781&PID_5581\4C530001231122108455" }, new UsbAllow { DeviceId = "VID_0951&PID_*", Access = "readonly" } } };
        Assert.Equal("full", SecurityPackRules.UsbAccess(p, @"USBSTOR\DISK&VEN_SANDISK\4C530001231122108455&0", vp, serial));
        Assert.Equal("blocked", SecurityPackRules.UsbAccess(p, @"USBSTOR\DISK&VEN_SANDISK\OTHER&0", vp, "OTHER"));
        Assert.Equal("readonly", SecurityPackRules.UsbAccess(p, @"USBSTOR\DISK&VEN_KINGSTON\X&0", "VID_0951&PID_1666", "X"));
        p.Mode = "readonly";
        Assert.Equal("readonly", SecurityPackRules.UsbAccess(p, @"USBSTOR\DISK&VEN_X\Y&0", null, null));
        p.Mode = "audit";
        Assert.Equal("full", SecurityPackRules.UsbAccess(p, @"USBSTOR\DISK&VEN_X\Y&0", null, null));
        Assert.Equal("block", SecurityPackRules.StrictestUsb(["audit", "block", "readonly"]));
        Assert.Equal("enforce", SecurityPackRules.StrictestPrivilege(["audit", null, "enforce"]));
        Assert.Equal("off", SecurityPackRules.StrictestBitLocker([]));
    }
}

/// <summary>On PostgreSQL: the policy a device gets, recovery passwords kept encrypted, retired and revealed.</summary>
public sealed class Release121DbTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    public Task InitializeAsync() => _db.InitializeAsync();
    public Task DisposeAsync() => _db.DisposeAsync();

    private static async Task<object?> Scalar(NpgsqlConnection c, string sql, params (string, object)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        return await cmd.ExecuteScalarAsync();
    }

    [DbFact]
    public async Task Device_policy_and_recovery_keys()
    {
        await new SqlMigrator(NullLogger<SqlMigrator>.Instance).MigrateAsync(_db.OwnerConnection, _db.App, "1.1.0-test");
        await using var c = new NpgsqlConnection(_db.AppConnection);
        await c.OpenAsync();
        var keys = new AppSecrets { CredentialKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
        var protector = new CredentialProtector(() => keys);
        var asset = (Guid)(await Scalar(c, "INSERT INTO zentriq.assets (hostname, category, status, source, os_family) VALUES ('dev-7', 'endpoint', 'active', 'agent', 'windows') RETURNING id"))!;
        var other = (Guid)(await Scalar(c, "INSERT INTO zentriq.assets (hostname, category, status, source, os_family) VALUES ('fin-2', 'endpoint', 'active', 'agent', 'windows') RETURNING id"))!;
        var group = (Guid)(await Scalar(c, "INSERT INTO zentriq.device_groups (name, asset_ids) VALUES ('Developers', ARRAY[@a]::uuid[]) RETURNING id", ("a", asset)))!;

        var r0 = await SecurityPackStore.RevisionAsync(c, default);
        await SecurityPackStore.SavePolicyAsync(c, "privilege", null, new { mode = "audit", allowedAdmins = new[] { @"CORP\IT" }, elevation = true, requests = true, maxAdminMinutes = 240 }, "t", default);
        await SecurityPackStore.SavePolicyAsync(c, "privilege", group, new { mode = "enforce", allowedAdmins = Array.Empty<string>(), elevation = true, requests = false, maxAdminMinutes = 60 }, "t", default);
        await SecurityPackStore.SavePolicyAsync(c, "usb", null, new { mode = "readonly", auditFiles = true }, "t", default);
        Assert.True(await SecurityPackStore.RevisionAsync(c, default) > r0);
        await Scalar(c, "INSERT INTO zentriq.epm_rules (name, kind, value, asset_id, uses_left) VALUES ('Once', 'hash', @h, @a, 1)", ("h", new string('c', 64)), ("a", asset));
        await Scalar(c, "INSERT INTO zentriq.epm_rules (name, kind, value) VALUES ('Everyone', 'publisher', 'Wireshark Foundation')");
        await Scalar(c, "INSERT INTO zentriq.epm_grants (asset_id, user_name, ends_at) VALUES (@a, 'CORP\\ana', now() + interval '1 hour')", ("a", asset));
        await Scalar(c, "INSERT INTO zentriq.usb_allowed (device_id, access, group_id) VALUES ('VID_0781&PID_5581', 'full', @g)", ("g", group));

        var dev = await SecurityPackStore.PolicyForAsync(c, asset, licensed: true, default);
        Assert.Equal("enforce", dev.Privilege.Mode);          // the device group's policy is stricter
        Assert.False(dev.Privilege.Requests);
        Assert.Equal(2, dev.Privilege.Rules.Count);
        Assert.Single(dev.Privilege.Grants);
        Assert.Equal("readonly", dev.Usb.Mode);
        Assert.Single(dev.Usb.Allowed);
        var fin = await SecurityPackStore.PolicyForAsync(c, other, licensed: true, default);
        Assert.Equal("audit", fin.Privilege.Mode);
        Assert.Single(fin.Privilege.Rules);
        Assert.Empty(fin.Usb.Allowed);
        var unlicensed = await SecurityPackStore.PolicyForAsync(c, asset, licensed: false, default);
        Assert.Equal(("off", "off", "off"), (unlicensed.Privilege.Mode, unlicensed.BitLocker.Mode, unlicensed.Usb.Mode));

        // The report: administrators, a recovery password (kept encrypted), events, a rule used once.
        const string pw = "111111-222222-333333-444444-555555-666666-777777-888888";
        var report = new SecurityPackReport
        {
            Admins = [new AdminMember { Member = @"DEV-7\Administrator", Sid = "S-1-5-21-1-2-3-500", Status = "kept" }, new AdminMember { Member = @"DEV-7\helper", Sid = "S-1-5-21-1-2-3-1001", Status = "removed" }],
            Volumes = [new BitLockerVolume { Volume = "C:", Kind = "os", Protection = "on", Status = "encrypted", Protectors = ["TPM", "Recovery password"], RecoveryIds = ["AAAA1111-2222-3333-4444-555566667777"] }],
            Tpm = "ready",
            Keys = [new RecoveryKey { Volume = "C:", ProtectorId = "{aaaa1111-2222-3333-4444-555566667777}", Password = pw }],
            PrivilegeEvents = [new PrivilegeEvent { At = DateTimeOffset.UtcNow, Kind = "elevated", User = @"CORP\ana", Path = @"C:\t.exe", RuleId = dev.Privilege.Rules.First(r => r.UsesLeft == 1).Id }],
            UsbEvents = [new UsbEvent { At = DateTimeOffset.UtcNow, Kind = "file_written", User = @"CORP\ana", Path = @"E:\a.docx", Size = 1234, DeviceId = @"VID_0781&PID_5581\1" }],
            UsedRules = [dev.Privilege.Rules.First(r => r.UsesLeft == 1).Id],
        };
        await SecurityPackStore.SaveReportAsync(c, asset, report, protector, default);
        Assert.Equal(2L, await Scalar(c, "SELECT count(*) FROM zentriq.device_local_admins WHERE asset_id = @a", ("a", asset)));
        var stored = (string)(await Scalar(c, "SELECT key_protected FROM zentriq.bitlocker_keys WHERE asset_id = @a", ("a", asset)))!;
        Assert.DoesNotContain("111111", stored);
        var keyId = (Guid)(await Scalar(c, "SELECT id FROM zentriq.bitlocker_keys WHERE upper(protector_id) LIKE 'AAAA1111%'"))!;
        var shown = await SecurityPackStore.RevealAsync(c, keyId, protector, default);
        Assert.Equal(pw, shown!.Value.Password);
        Assert.Equal(0, await Scalar(c, "SELECT uses_left FROM zentriq.epm_rules WHERE name = 'Once'"));
        Assert.Single((await SecurityPackStore.PolicyForAsync(c, asset, true, default)).Privilege.Rules);   // used up: no longer sent
        Assert.Equal(1L, await Scalar(c, "SELECT count(*) FROM zentriq.usb_events WHERE asset_id = @a AND size = 1234", ("a", asset)));

        // Replaced on the device: the old password is retired, the new one kept.
        await Scalar(c, "UPDATE zentriq.bitlocker_keys SET rotate_requested_at = now() WHERE id = @id", ("id", keyId));
        Assert.Contains("AAAA1111-2222-3333-4444-555566667777", (await SecurityPackStore.PolicyForAsync(c, asset, true, default)).BitLocker.Rotate);
        await SecurityPackStore.SaveReportAsync(c, asset, new SecurityPackReport
        {
            Volumes = [new BitLockerVolume { Volume = "C:", Kind = "os", Protection = "on", Status = "encrypted", RecoveryIds = ["BBBB1111-2222-3333-4444-555566667777"] }],
            Keys = [new RecoveryKey { Volume = "C:", ProtectorId = "BBBB1111-2222-3333-4444-555566667777", Password = "999999-222222-333333-444444-555555-666666-777777-888888" }],
        }, protector, default);
        Assert.NotNull(await Scalar(c, "SELECT retired_at FROM zentriq.bitlocker_keys WHERE id = @id", ("id", keyId)));
        Assert.Equal(1L, await Scalar(c, "SELECT count(*) FROM zentriq.bitlocker_keys WHERE asset_id = @a AND retired_at IS NULL", ("a", asset)));
        Assert.Empty((await SecurityPackStore.PolicyForAsync(c, asset, true, default)).BitLocker.Rotate);
        // Not a recovery password: refused.
        await SecurityPackStore.SaveReportAsync(c, asset, new SecurityPackReport { Keys = [new RecoveryKey { Volume = "D:", ProtectorId = "X", Password = "not-a-key" }] }, protector, default);
        Assert.Equal(2L, await Scalar(c, "SELECT count(*) FROM zentriq.bitlocker_keys WHERE asset_id = @a", ("a", asset)));
    }
}
