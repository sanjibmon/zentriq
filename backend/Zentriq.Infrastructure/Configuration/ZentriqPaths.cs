namespace Zentriq.Infrastructure.Configuration;

/// <summary>
/// Mutable data lives under ProgramData (never in the binaries directory). Resolution order:
///   1. explicit root (zentriqctl --data-dir, tests)
///   2. ZENTRIQ_DATA_DIR environment variable
///   3. HKLM\SOFTWARE\Zentriq\Console : DataDirectory   (written by the MSI when a non-default folder is chosen)
///   4. %ProgramData%\Zentriq
/// </summary>
public sealed class ZentriqPaths
{
    public const string RegistryKey = @"SOFTWARE\Zentriq\Console";
    public const string RegistryValue = "DataDirectory";

    public ZentriqPaths(string? root = null)
    {
        Root = root
               ?? NonEmpty(Environment.GetEnvironmentVariable("ZENTRIQ_DATA_DIR"))
               ?? FromRegistry()
               ?? (OperatingSystem.IsWindows()
                   ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Zentriq")
                   : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zentriq"));
        Root = Path.TrimEndingDirectorySeparator(Root);
    }

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? FromRegistry()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64)
                .OpenSubKey(RegistryKey);
            return NonEmpty(key?.GetValue(RegistryValue) as string);
        }
        catch (Exception) { return null; }
    }

    public string Root { get; }
    public string Config => Path.Combine(Root, "Config");
    public string Secrets => Path.Combine(Root, "Secrets");
    public string Logs => Path.Combine(Root, "Logs");
    public string Packages => Path.Combine(Root, "Packages");
    public string Backups => Path.Combine(Root, "Backups");
    public string Exports => Path.Combine(Root, "Exports");
    /// <summary>Exported audit and activity logs (Audit Logs › Retention &amp; export).</summary>
    public string LogArchives => Path.Combine(Exports, "LogArchives");
    /// <summary>Backup files uploaded in the console for a restore (the API writes them, the maintenance task restores them).</summary>
    public string RestoreUploads => Path.Combine(Root, "Restore");
    /// <summary>Data folder of the remote support engine (devices, its own certificates and settings).</summary>
    public string RemoteData => Path.Combine(Root, "Remote", "data");
    public string DataProtectionKeys => Path.Combine(Secrets, "dpkeys");
    public string ConfigFile => Path.Combine(Config, "zentriq.json");
    public string AppSecretsFile => Path.Combine(Secrets, "app.secrets");
    public string OwnerSecretsFile => Path.Combine(Secrets, "owner.secrets");
    public string HttpsCertificateFile => Path.Combine(Secrets, "https.pfx");
    public string AgentCaFile => Path.Combine(Secrets, "agent-ca.pfx");
    /// <summary>Local CA that issues the console certificate (when no customer certificate is configured).</summary>
    public string HttpsCaFile => Path.Combine(Secrets, "https-ca.pfx");
    /// <summary>The console certificate in PEM for the remote support engine (written by the API, read by ZentriqRemote).</summary>
    public string RemoteCertificateDir => Path.Combine(Root, "Remote", "cert");
    public string InstallResultFile => Path.Combine(Logs, "install-result.json");
}
