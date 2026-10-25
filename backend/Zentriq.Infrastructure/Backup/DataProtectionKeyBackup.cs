using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Zentriq.Infrastructure.Backup;

/// <summary>
/// The console's data protection keys (Secrets\dpkeys: they protect MFA secrets and sign-in cookies) are encrypted with
/// Windows DPAPI for this machine, so on a rebuilt server they could not be read and every MFA user would be locked out.
/// The backup holds them in a portable form instead — the DPAPI layer removed, protected by the backup encryption — and the
/// restore protects them with DPAPI again for the new machine. The key ring format itself is unchanged.
/// </summary>
public static class DataProtectionKeyBackup
{
    // ASP.NET Core's DPAPI XML encryptor protects the key element with this entropy (DpapiSecretSerializerHelper).
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DPAPI-Protected Secret");
    private const string PortableMarker = "zentriqPortable";

    public sealed record KeyFile(string Name, string Xml);

    /// <summary>The key files with their DPAPI-protected parts decrypted; Count = how many secrets were made portable.</summary>
    public static (List<KeyFile> Files, int Portable, int Kept) Export(string dir)
    {
        var files = new List<KeyFile>();
        int portable = 0, kept = 0;
        if (!Directory.Exists(dir)) return (files, 0, 0);
        foreach (var path in Directory.EnumerateFiles(dir, "*.xml"))
        {
            var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
            foreach (var enc in doc.Descendants().Where(e => e.Name.LocalName == "encryptedSecret" &&
                                                             (e.Attribute("decryptorType")?.Value ?? "").Contains("DpapiXmlDecryptor", StringComparison.Ordinal)).ToList())
            {
                var key = enc.Elements().FirstOrDefault(e => e.Name.LocalName == "encryptedKey");
                var value = key?.Elements().FirstOrDefault(e => e.Name.LocalName == "value");
                if (key is null || value is null) { kept++; continue; }
                try
                {
                    var plain = OperatingSystem.IsWindows()
                        ? ProtectedData.Unprotect(Convert.FromBase64String(value.Value.Trim()), Entropy, DataProtectionScope.LocalMachine)
                        : throw new PlatformNotSupportedException();
                    value.Value = Convert.ToBase64String(plain);
                    key.SetAttributeValue(PortableMarker, "1");
                    CryptographicOperations.ZeroMemory(plain);
                    portable++;
                }
                catch (Exception ex) when (ex is CryptographicException or FormatException or PlatformNotSupportedException) { kept++; }
            }
            files.Add(new KeyFile(Path.GetFileName(path), doc.ToString(SaveOptions.DisableFormatting)));
        }
        return (files, portable, kept);
    }

    /// <summary>
    /// Adds the backup's keys to <paramref name="dir"/>, protecting their secrets with DPAPI for this machine. Keys that are
    /// already present (same file name, i.e. same key id) are left alone. Returns the number of key files added.
    /// </summary>
    public static int Import(string dir, IEnumerable<KeyFile> files)
    {
        Directory.CreateDirectory(dir);
        var added = 0;
        foreach (var f in files)
        {
            var name = Path.GetFileName(f.Name);
            if (!name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
            var path = Path.Combine(dir, name);
            if (File.Exists(path)) continue;
            var doc = XDocument.Parse(f.Xml, LoadOptions.PreserveWhitespace);
            foreach (var key in doc.Descendants().Where(e => e.Name.LocalName == "encryptedKey" && e.Attribute(PortableMarker) is not null).ToList())
            {
                var value = key.Elements().First(e => e.Name.LocalName == "value");
                var plain = Convert.FromBase64String(value.Value.Trim());
                value.Value = OperatingSystem.IsWindows()
                    ? Convert.ToBase64String(ProtectedData.Protect(plain, Entropy, DataProtectionScope.LocalMachine))
                    : throw new PlatformNotSupportedException("Data protection keys can only be restored on Windows.");
                CryptographicOperations.ZeroMemory(plain);
                key.Attribute(PortableMarker)!.Remove();
            }
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, doc.ToString(SaveOptions.DisableFormatting), new UTF8Encoding(false));
            File.Move(tmp, path);
            added++;
        }
        return added;
    }
}
