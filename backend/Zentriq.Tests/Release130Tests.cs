using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Zentriq.Application.Licensing;
using Zentriq.Domain.Licensing;
using Zentriq.Infrastructure.Database;
using Zentriq.Infrastructure.Inventory;
using Zentriq.Infrastructure.Licensing;
using Zentriq.Infrastructure.Secrets;
using Zentriq.Inventory;

namespace Zentriq.Tests;

/// <summary>Build 130: product keys found on devices, licence type "retail".</summary>
public sealed class Release130Tests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    public Task InitializeAsync() => _db.InitializeAsync();
    public Task DisposeAsync() => _db.DisposeAsync();

    private sealed class Wmi(string? firmwareKey) : IWmiSource
    {
        public IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string ns, string wql) =>
            wql.Contains("SoftwareLicensingService") && firmwareKey is not null
                ? [new Dictionary<string, object?> { ["OA3xOriginalProductKey"] = firmwareKey }]
                : throw new InvalidOperationException("Invalid class");
    }

    private sealed class Reg(Dictionary<string, Dictionary<string, object>> keys, bool local) : IRegistrySource, IRegistryValueSource
    {
        public int Reads;
        public bool IsLocal => local;
        public IReadOnlyList<string> SubKeys(string path) =>
            keys.Keys.Where(k => k.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase)).Select(k => k[(path.Length + 1)..].Split('\\')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        public string? GetString(string path, string name) => keys.TryGetValue(path, out var v) && v.TryGetValue(name, out var o) ? o as string : null;
        public int? GetDword(string path, string name) => keys.TryGetValue(path, out var v) && v.TryGetValue(name, out var o) && o is int i ? i : null;
        public byte[]? GetBinary(string path, string name) => keys.TryGetValue(path, out var v) && v.TryGetValue(name, out var o) ? o as byte[] : null;
        public IReadOnlyList<string> ValueNames(string path) { Reads++; return keys.TryGetValue(path, out var v) ? v.Keys.ToList() : []; }
    }

    private static byte[] DigitalProductId(string key, int offset, int length)
    {
        var id = new byte[length];
        ProductKeys.Encode(key).CopyTo(id, offset);
        return id;
    }

    [Theory]
    [InlineData("VK7JG-NPHTM-C97JM-9MPGT-3V66T")]     // Windows 8 and later: an N in the key
    [InlineData("NKJFK-GPHP7-G8C3J-P6JXR-HQRJR")]     // the N first
    [InlineData("FJ82H-XT6CR-J8D7P-XQJJ2-GPDD4")]     // Windows 7 and Office 2010: no N
    public void Product_key_is_decoded_from_the_digital_product_id(string key)
    {
        Assert.Equal(key, ProductKeys.Decode(DigitalProductId(key, 52, 164), 52));
        Assert.Equal(key, ProductKeys.Decode(DigitalProductId(key, 808, 1272), 808));
    }

    [Fact]
    public void An_empty_digital_product_id_is_not_a_key()
    {
        Assert.Null(ProductKeys.Decode(new byte[164], 52));          // volume activation: zeros
        Assert.Null(ProductKeys.Decode(new byte[40], 52));           // too short
        Assert.Null(ProductKeys.Decode(null, 52));
        Assert.False(ProductKeys.IsMicrosoftKey("BBBBB-BBBBB-BBBBB-BBBBB-BBBBB"));
        Assert.False(ProductKeys.IsMicrosoftKey("VK7JG-NPHTM-C97JM-9MPGT"));
    }

    [Theory]
    [InlineData("RAR-7781-AB42-77A1C", true)]
    [InlineData("1234-5678-9012-3456", true)]
    [InlineData("ABCDEFGH", false)]                                        // no digit
    [InlineData("{8A69D345-D564-463C-AFF1-A69D9E530F96}", false)]          // a GUID
    [InlineData(@"C:\Program Files\App\license.key", false)]               // a path
    [InlineData("00000000", false)]                                        // a placeholder
    [InlineData("1.2.3", false)]
    public void Only_values_that_look_like_a_key_are_taken(string value, bool expected) => Assert.Equal(expected, ProductKeys.LooksLikeKey(value));

    private static Dictionary<string, Dictionary<string, object>> Registry() => new(StringComparer.OrdinalIgnoreCase)
    {
        [@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"] = new() { ["DigitalProductId"] = DigitalProductId("VK7JG-NPHTM-C97JM-9MPGT-3V66T", 52, 164) },
        [@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SoftwareProtectionPlatform"] = new() { ["BackupProductKeyDefault"] = "VK7JG-NPHTM-C97JM-9MPGT-3V66T" },
        [@"SOFTWARE\Microsoft\Office\14.0\Registration\{90140000-0011-0000-0000-0000000FF1CE}"] = new()
            { ["DigitalProductID"] = DigitalProductId("FJ82H-XT6CR-J8D7P-XQJJ2-GPDD4", 808, 1272), ["ProductName"] = "Microsoft Office Professional Plus 2010" },
        [@"SOFTWARE\Microsoft\Office\16.0\Registration\{90160000-0011-0000-0000-0000000FF1CE}"] = new() { ["ProductName"] = "Microsoft 365 Apps" },   // no key kept
        [@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WinRAR archiver"] = new()
            { ["DisplayName"] = "WinRAR 6.24 (64-bit)", ["Publisher"] = "win.rar GmbH", ["SerialNumber"] = "rar-7781-ab42-77a1c", ["InstallLocation"] = @"C:\Program Files\WinRAR" },
        [@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{8A69D345-D564-463C-AFF1-A69D9E530F96}"] = new()
            { ["DisplayName"] = "Some Tool", ["ProductKey"] = "{8A69D345-D564-463C-AFF1-A69D9E530F96}" },
        [@"SOFTWARE\Acme\Designer\9.0"] = new() { ["LicenseKey"] = "ACME-9000-XK21-55PQ" },
    };

    [Fact]
    public void Keys_of_windows_office_and_other_software_are_collected_once_each()
    {
        var keys = ProductKeys.Collect(new Wmi("NKJFK-GPHP7-G8C3J-P6JXR-HQRJR"), new Reg(Registry(), local: true), "Windows 11 Pro");
        Assert.Contains(keys, k => k is { Application: "firmware", Source: "firmware", Key: "NKJFK-GPHP7-G8C3J-P6JXR-HQRJR" });
        // The installed Windows key is found twice (backup value and digital product id) and listed once, marked as generic.
        var windows = Assert.Single(keys, k => k.Application == "windows");
        Assert.Equal(("Windows 11 Pro", "VK7JG-NPHTM-C97JM-9MPGT-3V66T"), (windows.Product, windows.Key));
        Assert.Contains("Generic key", windows.Note);
        var office = Assert.Single(keys, k => k.Application == "office");
        Assert.Equal(("Microsoft Office Professional Plus 2010", "FJ82H-XT6CR-J8D7P-XQJJ2-GPDD4"), (office.Product, office.Key));
        Assert.Contains(keys, k => k is { Product: "WinRAR 6.24 (64-bit)", Publisher: "win.rar GmbH", Key: "RAR-7781-AB42-77A1C", Application: "other" });
        Assert.Contains(keys, k => k is { Product: "Acme Designer 9.0", Publisher: "Acme", Key: "ACME-9000-XK21-55PQ" });
        Assert.DoesNotContain(keys, k => k.Product == "Some Tool");
        Assert.Equal(5, keys.Count);
    }

    [Fact]
    public void A_remote_collection_does_not_walk_the_whole_registry()
    {
        var reg = new Reg(Registry(), local: false);
        var keys = ProductKeys.Collect(new Wmi(null), reg, null);
        Assert.DoesNotContain(keys, k => k.Publisher == "Acme");
        Assert.Contains(keys, k => k.Product == "WinRAR 6.24 (64-bit)");
        Assert.Equal("Windows", Assert.Single(keys, k => k.Application == "windows").Product);
        Assert.True(reg.Reads < 10, $"{reg.Reads} keys read");
    }

    private static LicenseStatus Trial() => new(LicenseMode.Trial, "trial", null, 14, ["*"], new Dictionary<string, int> { ["devices"] = 30 }, null, null);

    private static InventoryDocument Doc(params ProductKeyItem[]? keys)
    {
        var d = new InventoryDocument { Source = "agent" };
        d.Device.Hostname = "fin-lt-014";
        d.Device.Category = "endpoint";
        d.Device.SmbiosUuid = "4c4c4544-0042-3510-8054-b7c04f4e3233";
        d.Os.Family = "windows";
        d.ProductKeys = keys?.ToList();
        return d;
    }

    private static ProductKeyItem Key(string product, string app, string key) => new() { Product = product, Application = app, Source = app == "firmware" ? "firmware" : "registry", Key = key };

    [DbFact]
    public async Task Product_keys_are_stored_encrypted_apart_from_the_inventory_document()
    {
        await new SqlMigrator(NullLogger<SqlMigrator>.Instance).MigrateAsync(_db.OwnerConnection, _db.App, "1.1.0-test");
        await using var conn = new NpgsqlConnection(_db.AppConnection);
        await conn.OpenAsync();
        var appKeys = new AppSecrets { CredentialKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
        var protector = new CredentialProtector(() => appKeys);
        var store = new InventoryStore(new LicensingService(null!, null!, null!, null!, null!), protector);
        const string winKey = "NKJFK-GPHP7-G8C3J-P6JXR-HQRJR", rarKey = "RAR-7781-AB42-77A1C";

        var first = await store.ApplyAsync(conn, Doc(Key("Windows 11 Pro", "windows", winKey), Key("WinRAR 6.24", "other", rarKey)), new MatchHints(AgentId: Guid.NewGuid()), Trial(), default);
        var asset = first.AssetId!.Value;

        async Task<List<(Guid Id, string Product, string Hint, string Protected, DateTimeOffset First)>> Rows()
        {
            var list = new List<(Guid, string, string, string, DateTimeOffset)>();
            await using var c = new NpgsqlCommand("SELECT id, product, key_hint, key_protected, first_seen_at FROM zentriq.asset_product_keys WHERE asset_id = @a ORDER BY application DESC", conn);
            c.Parameters.AddWithValue("a", asset);
            await using var r = await c.ExecuteReaderAsync();
            while (await r.ReadAsync()) list.Add((r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateTimeOffset>(4)));
            return list;
        }
        async Task<string> Scalar(string sql)
        {
            await using var c = new NpgsqlCommand(sql, conn);
            c.Parameters.AddWithValue("a", asset);
            return Convert.ToString(await c.ExecuteScalarAsync())!;
        }

        var rows = await Rows();
        Assert.Equal(["Windows 11 Pro", "WinRAR 6.24"], rows.Select(r => r.Product));
        Assert.Equal(["HQRJR", "77A1C"], rows.Select(r => r.Hint));
        Assert.All(rows, r => { Assert.DoesNotContain(winKey, r.Protected); Assert.DoesNotContain(rarKey, r.Protected); });
        Assert.Equal(winKey, protector.Unprotect(rows[0].Id, rows[0].Protected).Password);
        // A ciphertext moved to another row cannot be read.
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(rows[1].Id, rows[0].Protected));
        // Neither key is anywhere in the stored document.
        var doc = await Scalar("SELECT document::text FROM zentriq.asset_inventory_documents WHERE asset_id = @a");
        Assert.DoesNotContain(winKey, doc);
        Assert.DoesNotContain(rarKey, doc);

        // The same keys again: the rows stay (first seen kept). An inventory without the list (older agent) changes nothing.
        await store.ApplyAsync(conn, Doc(Key("Windows 11 Pro", "windows", winKey), Key("WinRAR 6.24", "other", rarKey)), new MatchHints(PreferredAssetId: asset), Trial(), default);
        await store.ApplyAsync(conn, Doc(null), new MatchHints(PreferredAssetId: asset), Trial(), default);
        Assert.Equal(rows.Select(r => (r.Id, r.First)), (await Rows()).Select(r => (r.Id, r.First)));

        // WinRAR removed, Windows key changed: one row, the new key.
        await store.ApplyAsync(conn, Doc(Key("Windows 11 Pro", "windows", "FJ82H-XT6CR-J8D7P-XQJJ2-GPDD4")), new MatchHints(PreferredAssetId: asset), Trial(), default);
        var after = Assert.Single(await Rows());
        Assert.Equal(("Windows 11 Pro", "GPDD4"), (after.Product, after.Hint));
        Assert.NotEqual(rows[0].Id, after.Id);

        // Switched off: nothing more is stored.
        await using (var off = new NpgsqlCommand("INSERT INTO zentriq.system_settings (key, value) VALUES ('licenses.settings', '{\"productKeys\": false}') ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value", conn)) await off.ExecuteNonQueryAsync();
        await store.ApplyAsync(conn, Doc(Key("Windows 11 Pro", "windows", winKey)), new MatchHints(PreferredAssetId: asset), Trial(), default);
        Assert.Equal("GPDD4", Assert.Single(await Rows()).Hint);
    }

    [DbFact]
    public async Task A_licence_can_be_of_type_retail()
    {
        await new SqlMigrator(NullLogger<SqlMigrator>.Instance).MigrateAsync(_db.OwnerConnection, _db.App, "1.1.0-test");
        await using var conn = new NpgsqlConnection(_db.AppConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            WITH p AS (INSERT INTO zentriq.sam_products (name) VALUES ('Corel Draw') RETURNING id)
            INSERT INTO zentriq.sam_licenses (product_id, name, license_type, quantity) SELECT id, 'Boxed copy', 'retail', 1 FROM p RETURNING license_type
            """, conn);
        Assert.Equal("retail", await cmd.ExecuteScalarAsync());
        await using var bad = new NpgsqlCommand("INSERT INTO zentriq.sam_licenses (product_id, name, license_type) SELECT id, 'x', 'boxed' FROM zentriq.sam_products LIMIT 1", conn);
        await Assert.ThrowsAsync<PostgresException>(() => bad.ExecuteNonQueryAsync());
    }
}
