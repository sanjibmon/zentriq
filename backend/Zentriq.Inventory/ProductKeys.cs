using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Zentriq.Inventory;

/// <summary>Registry reads needed for product keys: binary values and the value names of a key.</summary>
public interface IRegistryValueSource
{
    byte[]? GetBinary(string path, string name);
    IReadOnlyList<string> ValueNames(string path);
    /// <summary>True when the registry is read on the device itself (a wide search is cheap).</summary>
    bool IsLocal { get; }
}

/// <summary>
/// A product key found on the device. Sent with the inventory; the server stores it encrypted in its own table and
/// never in the inventory document.
/// </summary>
public sealed class ProductKeyItem
{
    [JsonPropertyName("product")] public string Product { get; set; } = "";
    [JsonPropertyName("publisher")] public string? Publisher { get; set; }
    /// <summary>windows | office | firmware | other</summary>
    [JsonPropertyName("application")] public string Application { get; set; } = "other";
    /// <summary>firmware | windows | registry</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "registry";
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("note")] public string? Note { get; set; }
}

/// <summary>
/// Product keys of Windows, Office and other software, where the device keeps them: the firmware (OEM), the Windows
/// registration, Office and SQL Server installed from an installer package, and programs that store a key or serial
/// number under their own registry key. Office 2013 and later (Click-to-Run, Microsoft 365) and volume-activated
/// Windows keep only the last five characters; there is no full key to read for those.
/// </summary>
public static class ProductKeys
{
    public const int MaxKeys = 200;
    private const string Chars = "BCDFGHJKMPQRTVWXY2346789";
    private const string WindowsNt = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    /// <summary>Keys that Microsoft publishes and installs itself (digital licence, KMS client); they license nothing on their own.</summary>
    private static readonly HashSet<string> Generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "YTMG3-N6DKC-DKB77-7M9GH-8HVX7", "VK7JG-NPHTM-C97JM-9MPGT-3V66T", "YNMGQ-8RYV3-4PGQ3-C8XTP-7CFBY", "XGVPP-NMH47-7TTHJ-W3FW7-8HV2C",
        "W269N-WFGWX-YVC9B-4J6C9-T83GX", "NPPR9-FWDCX-D2C8J-H872K-2YT43", "TX9XD-98N7V-6WMQ6-BX7FG-H8Q99", "NW6C2-QMPVW-D7KKK-3GKT6-VCFB2",
    };

    private static readonly HashSet<string> KeyValueNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ProductKey", "Product Key", "Product_Key", "SerialNumber", "Serial Number", "Serial", "SerialNo", "SerialKey", "Serial Key",
        "LicenseKey", "License Key", "LicenceKey", "LicenseCode", "RegKey", "RegCode", "RegistrationKey", "RegistrationCode",
        "CDKey", "CD Key", "CD-Key", "ActivationKey", "ActivationCode", "PIDKEY",
    };

    private static readonly HashSet<string> SkippedVendors = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Classes", "Policies", "WOW6432Node", "Clients", "RegisteredApplications", "ODBC", "OpenSSH", "Windows", "DefaultUserEnvironment",
        "Khronos", "Partner", "OEM", "Setup", "Zentriq",
    };

    private static readonly Regex GuidShape = new Regex(@"^\{?[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}?$", RegexOptions.Compiled);
    private static readonly Regex KeyShape = new Regex(@"^[A-Za-z0-9][A-Za-z0-9 \-]{6,78}[A-Za-z0-9]$", RegexOptions.Compiled);

    public static List<ProductKeyItem> Collect(IWmiSource wmi, IRegistrySource registry, string? osName, IEnumerable<SoftwareItem>? software = null)
    {
        var list = new List<ProductKeyItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string product, string? publisher, string application, string source, string? key)
        {
            key = key?.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(key) || list.Count >= MaxKeys) return;
            if (!seen.Add(application + "|" + key)) return;
            list.Add(new ProductKeyItem
            {
                Product = product.Length > 200 ? product.Substring(0, 200) : product, Publisher = publisher, Application = application, Source = source, Key = key!,
                Note = Generic.Contains(key!) ? "Generic key installed by Windows (digital licence or KMS activation); it is not a purchased key." : null,
            });
        }
        var values = registry as IRegistryValueSource;
        var windows = string.IsNullOrWhiteSpace(osName) ? "Windows" : osName!.Trim();

        // The key the manufacturer put in the firmware (OEM activation 3.0).
        Try(() =>
        {
            var svc = wmi.Query(@"root\cimv2", "SELECT OA3xOriginalProductKey FROM SoftwareLicensingService").FirstOrDefault();
            if (svc != null && svc.TryGetValue("OA3xOriginalProductKey", out var v) && IsMicrosoftKey(v as string))
                Add(windows + " (firmware / OEM key)", "Microsoft Corporation", "firmware", "firmware", v as string);
        });
        // The key Windows was installed or activated with.
        Try(() =>
        {
            var backup = registry.GetString(WindowsNt + @"\SoftwareProtectionPlatform", "BackupProductKeyDefault");
            if (IsMicrosoftKey(backup)) Add(windows, "Microsoft Corporation", "windows", "windows", backup);
        });
        if (values != null)
        {
            Try(() => { if (Decode(values.GetBinary(WindowsNt, "DigitalProductId"), 52) is { } k) Add(windows, "Microsoft Corporation", "windows", "windows", k); });

            // Office installed from an installer package (2010 and earlier keep the whole key; later versions do not).
            foreach (var root in new[] { @"SOFTWARE\Microsoft\Office", @"SOFTWARE\WOW6432Node\Microsoft\Office" })
                foreach (var ver in new[] { "11.0", "12.0", "14.0", "15.0", "16.0" })
                    Try(() =>
                    {
                        var reg = root + "\\" + ver + @"\Registration";
                        foreach (var sub in registry.SubKeys(reg))
                        {
                            var bin = values.GetBinary(reg + "\\" + sub, "DigitalProductID");
                            var key = bin != null && bin.Length >= 823 ? Decode(bin, 808) ?? Decode(bin, 52) : Decode(bin, 52);
                            if (key is null) continue;
                            var name = registry.GetString(reg + "\\" + sub, "ProductName") ?? registry.GetString(reg + "\\" + sub, "ConvertToEdition") ?? "Microsoft Office " + ver;
                            Add(name.Trim(), "Microsoft Corporation", "office", "registry", key);
                        }
                    });

            // SQL Server (not the free editions, which have no key).
            Try(() =>
            {
                const string sql = @"SOFTWARE\Microsoft\Microsoft SQL Server";
                foreach (var inst in values.ValueNames(sql + @"\Instance Names\SQL"))
                {
                    var id = registry.GetString(sql + @"\Instance Names\SQL", inst);
                    if (string.IsNullOrEmpty(id)) continue;
                    var setup = sql + "\\" + id + @"\Setup";
                    var edition = registry.GetString(setup, "Edition") ?? "";
                    if (edition.IndexOf("Express", StringComparison.OrdinalIgnoreCase) >= 0 || edition.IndexOf("Developer", StringComparison.OrdinalIgnoreCase) >= 0
                        || edition.IndexOf("Evaluation", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    var major = 0;
                    var version = registry.GetString(setup, "Version") ?? "";
                    if (version.IndexOf('.') > 0) int.TryParse(version.Substring(0, version.IndexOf('.')), out major);
                    var key = Decode(values.GetBinary(setup, "DigitalProductID"), major >= 11 ? 0 : 52);
                    if (key != null) Add(("Microsoft SQL Server " + edition).Trim() + " (" + inst + ")", "Microsoft Corporation", "other", "registry", key);
                }
            });

            // Programs that keep a key or serial number with their uninstall entry or under SOFTWARE\<publisher>\<product>.
            Try(() =>
            {
                foreach (var root in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
                    foreach (var k in SafeSubKeys(registry, root))
                    {
                        var path = root + "\\" + k;
                        var names = SafeValueNames(values, path).Where(KeyValueNames.Contains).ToList();
                        if (names.Count == 0) continue;
                        var display = registry.GetString(path, "DisplayName");
                        if (string.IsNullOrWhiteSpace(display)) continue;
                        foreach (var n in names)
                            if (LooksLikeKey(registry.GetString(path, n))) Add(display!.Trim(), registry.GetString(path, "Publisher")?.Trim(), "other", "registry", registry.GetString(path, n));
                    }
            });
            if (values.IsLocal)
                Try(() =>
                {
                    var budget = 4000;
                    foreach (var root in new[] { "SOFTWARE", @"SOFTWARE\WOW6432Node" })
                        foreach (var vendor in SafeSubKeys(registry, root))
                        {
                            if (SkippedVendors.Contains(vendor)) continue;
                            Walk(registry, values, root + "\\" + vendor, vendor, vendor, 0, ref budget, Add);
                            if (budget <= 0) return;
                        }
                });
        }
        return list;
    }

    private static void Walk(IRegistrySource registry, IRegistryValueSource values, string path, string vendor, string label, int depth, ref int budget,
        Action<string, string?, string, string, string?> add)
    {
        if (--budget <= 0) return;
        foreach (var n in SafeValueNames(values, path))
        {
            if (!KeyValueNames.Contains(n)) continue;
            var v = registry.GetString(path, n);
            if (LooksLikeKey(v)) add(label, vendor, "other", "registry", v);
        }
        if (depth >= 2) return;
        foreach (var sub in SafeSubKeys(registry, path))
        {
            Walk(registry, values, path + "\\" + sub, vendor, depth == 0 ? vendor + " " + sub : label + " " + sub, depth + 1, ref budget, add);
            if (budget <= 0) return;
        }
    }

    private static IReadOnlyList<string> SafeSubKeys(IRegistrySource r, string path)
    {
        try { return r.SubKeys(path); } catch (Exception) { return new string[0]; }
    }

    private static IReadOnlyList<string> SafeValueNames(IRegistryValueSource r, string path)
    {
        try { return r.ValueNames(path); } catch (Exception) { return new string[0]; }
    }

    private static void Try(Action a)
    {
        try { a(); } catch (Exception) { /* one source failing never stops the others */ }
    }

    /// <summary>XXXXX-XXXXX-XXXXX-XXXXX-XXXXX in the alphabet of Microsoft product keys.</summary>
    public static bool IsMicrosoftKey(string? s)
    {
        if (s is null) return false;
        s = s.Trim().ToUpperInvariant();
        if (s.Length != 29) return false;
        for (var i = 0; i < 29; i++)
        {
            if (i % 6 == 5) { if (s[i] != '-') return false; }
            else if (Chars.IndexOf(s[i]) < 0 && s[i] != 'N') return false;
        }
        return s.Replace("-", "").Distinct().Count() > 3;
    }

    /// <summary>A value that can be a licence key or serial number: letters, digits and dashes, with a digit, not a GUID, a path or a word.</summary>
    public static bool LooksLikeKey(string? s)
    {
        if (s is null) return false;
        s = s.Trim();
        if (!KeyShape.IsMatch(s) || GuidShape.IsMatch(s)) return false;
        if (!s.Any(char.IsDigit)) return false;
        var compact = s.Replace("-", "").Replace(" ", "");
        if (compact.Length < 8 || compact.Distinct().Count() < 4) return false;
        return s.Count(c => c == ' ') <= 5;
    }

    /// <summary>
    /// Decodes the product key in a DigitalProductId value (15 bytes at <paramref name="offset"/>, base 24; keys of
    /// Windows 8 and later carry an N whose position is coded separately). Null when there is no key (volume activation).
    /// </summary>
    public static string? Decode(byte[]? id, int offset)
    {
        if (id is null || id.Length < offset + 15) return null;
        var key = new byte[15];
        Array.Copy(id, offset, key, 0, 15);
        if (key.All(b => b == 0)) return null;
        var withN = (key[14] & 0x08) != 0;
        key[14] = (byte)(key[14] & 0xF7);
        var chars = new char[25];
        var last = 0;
        for (var i = 24; i >= 0; i--)
        {
            var cur = 0;
            for (var x = 14; x >= 0; x--)
            {
                cur = cur * 256 + key[x];
                key[x] = (byte)(cur / 24);
                cur %= 24;
            }
            chars[i] = Chars[cur];
            last = cur;
        }
        var s = new string(chars);
        if (withN) s = s.Substring(1, last) + "N" + s.Substring(last + 1);
        var result = string.Join("-", Enumerable.Range(0, 5).Select(i => s.Substring(i * 5, 5)));
        return IsMicrosoftKey(result) ? result : null;
    }

    /// <summary>The reverse of <see cref="Decode"/> (tests).</summary>
    public static byte[] Encode(string productKey)
    {
        var s = productKey.Replace("-", "").ToUpperInvariant();
        var n = s.IndexOf('N');
        if (n >= 0) s = Chars[n].ToString() + s.Remove(n, 1);
        var key = new byte[15];
        foreach (var c in s)
        {
            var carry = Chars.IndexOf(c);
            for (var x = 0; x < 15; x++)
            {
                var v = key[x] * 24 + carry;
                key[x] = (byte)(v & 0xFF);
                carry = v >> 8;
            }
        }
        if (n >= 0) key[14] |= 0x08;
        return key;
    }
}
