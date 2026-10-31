using System.Security.Cryptography;

namespace Zentriq.Api.Endpoints;

/// <summary>
/// The agent packages this server serves (web root, downloads/): the Windows MSI and the Linux packages
/// (downloads/linux: .deb, .rpm and .tar.gz for x64 and ARM64, the version, and install.sh, the one-line installer).
/// </summary>
public static class AgentPackages
{
    public const string Windows = "windows-x64", LinuxX64 = "linux-x64", LinuxArm64 = "linux-arm64";
    public static readonly string[] Platforms = [Windows, LinuxX64, LinuxArm64];
    public static readonly string[] LinuxPlatforms = [LinuxX64, LinuxArm64];
    public static readonly string[] LinuxFormats = ["deb", "rpm", "tar"];
    public const string LinuxDir = "downloads/linux";
    public const string LinuxInstallScript = LinuxDir + "/install.sh";

    /// <summary>Agents that do not report a platform are Windows agents (earlier builds).</summary>
    public static string Normalize(string? platform) => platform?.Trim().ToLowerInvariant() is LinuxX64 or LinuxArm64 ? platform.Trim().ToLowerInvariant() : Windows;
    public static bool IsLinux(string? platform) => platform is LinuxX64 or LinuxArm64;

    public static string LinuxFile(string platform, string format) =>
        $"{LinuxDir}/zentriq-agent-{platform}.{(format == "tar" ? "tar.gz" : format)}";

    public static string ContentType(string format) => format switch
    {
        "deb" => "application/vnd.debian.binary-package",
        "rpm" => "application/x-rpm",
        _ => "application/gzip",
    };

    public static string? Physical(IWebHostEnvironment env, string relative)
    {
        var f = env.WebRootFileProvider.GetFileInfo(relative);
        return f.Exists && f.PhysicalPath is not null ? f.PhysicalPath : null;
    }

    public static string? LinuxVersion(IWebHostEnvironment env) =>
        Physical(env, LinuxDir + "/version") is { } p ? File.ReadAllText(p).Trim() is { Length: > 0 } v ? v : null : null;

    /// <summary>The version an agent on <paramref name="platform"/> can update to from this server, or null.</summary>
    public static string? Version(IWebHostEnvironment env, string platform) =>
        IsLinux(platform) ? (LinuxFormats.Any(f => Physical(env, LinuxFile(platform, f)) is not null) ? LinuxVersion(env) : null) : AgentEndpoints.PackageVersion(env);

    /// <summary>
    /// What an update action carries for the agent to check the download: the MSI's SHA-256 for Windows;
    /// "deb=&lt;sha&gt;;rpm=&lt;sha&gt;;tar=&lt;sha&gt;" for Linux (the agent installs the format it was installed with).
    /// </summary>
    public static string? UpdateArgument(IWebHostEnvironment env, string platform)
    {
        if (!IsLinux(platform)) return Physical(env, AgentEndpoints.PackagePath) is { } msi ? Sha256(msi) : null;
        var parts = LinuxFormats.Select(f => Physical(env, LinuxFile(platform, f)) is { } p ? $"{f}={Sha256(p)}" : null).Where(x => x is not null).ToList();
        return parts.Count > 0 ? string.Join(';', parts) : null;
    }

    private static readonly object HashLock = new();
    private static readonly Dictionary<string, (DateTime Written, long Length, string Sha)> Hashes = new(StringComparer.Ordinal);

    /// <summary>SHA-256 (lower-case hex) of a package file, cached until the file changes.</summary>
    public static string Sha256(string path)
    {
        var info = new FileInfo(path);
        lock (HashLock)
        {
            if (Hashes.TryGetValue(path, out var h) && h.Written == info.LastWriteTimeUtc && h.Length == info.Length) return h.Sha;
            using var s = File.OpenRead(path);
            var sha = Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
            Hashes[path] = (info.LastWriteTimeUtc, info.Length, sha);
            return sha;
        }
    }

    /// <summary>The Linux packages on this server, for the console (Agents › Install on Linux).</summary>
    public static object? LinuxInfo(IWebHostEnvironment env)
    {
        var files = (from p in LinuxPlatforms
                     from f in LinuxFormats
                     let path = Physical(env, LinuxFile(p, f))
                     where path is not null
                     select new { platform = p, format = f, url = "/" + LinuxFile(p, f), size = new FileInfo(path).Length, sha256 = Sha256(path) }).ToList();
        if (files.Count == 0) return null;
        return new { version = LinuxVersion(env), files, installScript = Physical(env, LinuxInstallScript) is not null };
    }

    /// <summary>
    /// One line that installs and enrolls the Linux agent: it fetches install.sh from the agent port, trusting only the
    /// server's pinned key (like the agent does), which then downloads the right package for the device (deb, rpm or
    /// tar.gz; x64 or ARM64) the same way, installs it and enrolls with the code.
    /// </summary>
    public static string LinuxInstallCommand(string serverUrl, string? pin, string code, int checkin, int inventory)
    {
        var url = serverUrl.TrimEnd('/');
        var curl = pin is null ? "curl -fsSL" : $"curl -fsSLk --pinnedpubkey 'sha256//{pin}'";
        var pinArg = pin is null ? "" : $" --pin '{pin}'";
        return $"{curl} {url}/api/agent/v1/linux/install.sh | sudo sh -s -- --server {url} --code {code} --checkin {checkin} --inventory {inventory}{pinArg}";
    }
}
