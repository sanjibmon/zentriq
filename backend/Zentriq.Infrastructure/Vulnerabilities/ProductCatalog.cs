using System.Text.RegularExpressions;

namespace Zentriq.Infrastructure.Vulnerabilities;

/// <summary>
/// Maps installed software titles to NVD CPE vendor:product names. Titles are normalised (version numbers,
/// architecture and language tags removed) so "Mozilla Firefox (x64 en-US)" and "Mozilla Firefox 133.0 (x64 de)"
/// share one mapping. Only well-known products are mapped automatically; everything else stays unmapped until an
/// administrator picks the product (with the NVD product search) — a wrong automatic guess would produce false findings.
/// </summary>
public static partial class ProductCatalog
{
    [GeneratedRegex(@"\((?:x64|x86|64-bit|32-bit|amd64|arm64|64 bit|32 bit)[^)]*\)|\((?:user|machine|system|per-user)\)|(?<![\w-])(?:x64|x86|amd64|arm64|64-bit|32-bit)(?![\w-])|(?<![\w-])[a-z]{2}-[a-z]{2}(?![\w-])|(?<![\w-])v?\d+(?:[.\-_]\d+)+[a-z]?(?![\w-])|(?<![\w-])v?\d+(?![\w-])|\(\s*\)|\s-\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>Normalised key for a title: lower-case, without versions, architecture, language and years.</summary>
    public static string Key(string name)
    {
        var s = name.ToLowerInvariant().Replace('®', ' ').Replace('™', ' ');
        for (var i = 0; i < 3; i++) s = Noise().Replace(s, " ");
        s = Spaces().Replace(s, " ").Trim(' ', '-', '(', ')', ',', '.');
        return s.Length == 0 ? name.Trim().ToLowerInvariant() : s;
    }

    private sealed record Known(Regex Pattern, string Vendor, string Product);

    // Ordered: the first match wins. Patterns match the normalised key. CPE names as used by NVD.
    private static readonly Known[] Builtin =
    [
        K(@"^google chrome$", "google", "chrome"),
        K(@"^mozilla firefox esr\b", "mozilla", "firefox_esr"),
        K(@"^mozilla firefox\b", "mozilla", "firefox"),
        K(@"^mozilla thunderbird\b", "mozilla", "thunderbird"),
        K(@"^microsoft edge$", "microsoft", "edge_chromium"),
        K(@"^7-zip\b", "7-zip", "7-zip"),
        K(@"^notepad\+\+", "notepad-plus-plus", "notepad++"),
        K(@"^vlc media player$", "videolan", "vlc_media_player"),
        K(@"^adobe acrobat reader\b", "adobe", "acrobat_reader_dc"),
        K(@"^adobe acrobat\b", "adobe", "acrobat_dc"),
        K(@"^wireshark\b", "wireshark", "wireshark"),
        K(@"^winrar\b", "rarlab", "winrar"),
        K(@"^putty\b", "putty", "putty"),
        K(@"^filezilla\b(?!.*server)", "filezilla-project", "filezilla_client"),
        K(@"^winscp\b", "winscp", "winscp"),
        K(@"^keepass\b", "keepass", "keepass"),
        K(@"^python\b(?!.*launcher)", "python", "python"),
        K(@"^node\.js$", "nodejs", "node.js"),
        K(@"^git$", "git-scm", "git"),
        K(@"^openssl\b", "openssl", "openssl"),
        K(@"^postgresql\b", "postgresql", "postgresql"),
        K(@"^microsoft visual studio code\b", "microsoft", "visual_studio_code"),
        K(@"^libreoffice\b", "libreoffice", "libreoffice"),
        K(@"^gimp\b", "gimp", "gimp"),
        K(@"^inkscape\b", "inkscape", "inkscape"),
        K(@"^vmware workstation\b", "vmware", "workstation"),
        K(@"^oracle vm virtualbox\b|^virtualbox\b", "oracle", "vm_virtualbox"),
        K(@"^docker desktop\b", "docker", "desktop"),
        K(@"^teamviewer\b", "teamviewer", "teamviewer"),
        K(@"^anydesk\b", "anydesk", "anydesk"),
        K(@"^foxit pdf reader\b|^foxit reader\b", "foxit", "pdf_reader"),
        K(@"^slack\b", "slack", "slack"),
        K(@"^greenshot\b", "greenshot", "greenshot"),
        K(@"^apache tomcat\b", "apache", "tomcat"),
        K(@"^citrix workspace\b", "citrix", "workspace"),
        K(@"^cisco webex meetings\b", "cisco", "webex_meetings"),
        K(@"^opera\b(?! gx)", "opera", "opera_browser"),
        K(@"^brave\b", "brave", "brave"),
        K(@"^microsoft teams\b", "microsoft", "teams"),
        K(@"^zoom\b", "zoom", "meetings"),
        K(@"^irfanview\b", "irfanview", "irfanview"),
        K(@"^paint\.net\b", "dotpdn", "paint.net"),
        K(@"^audacity\b", "audacityteam", "audacity"),
        K(@"^obs studio\b", "obsproject", "obs_studio"),
        K(@"^mremoteng\b", "mremoteng", "mremoteng"),
        K(@"^tortoisesvn\b", "tortoisesvn", "tortoisesvn"),
        K(@"^tortoisegit\b", "tortoisegit", "tortoisegit"),
        K(@"^github desktop\b", "github", "desktop"),
        K(@"^postman\b", "postman", "postman"),
    ];

    private static Known K(string pattern, string vendor, string product) => new(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), vendor, product);

    /// <summary>Built-in mapping for a normalised key, or null.</summary>
    public static (string Vendor, string Product)? Lookup(string key)
    {
        foreach (var k in Builtin) if (k.Pattern.IsMatch(key)) return (k.Vendor, k.Product);
        return null;
    }

    public static int BuiltinCount => Builtin.Length;
}
