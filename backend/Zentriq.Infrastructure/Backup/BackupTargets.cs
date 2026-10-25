using System.Text.Json.Serialization;
using Zentriq.Infrastructure.Secrets;

namespace Zentriq.Infrastructure.Backup;

/// <summary>Where backups are copied besides the server's own Backups folder.</summary>
public static class BackupKinds
{
    public const string Folder = "folder";
    public const string Smb = "smb";
    public const string Ftp = "ftp";
    public const string Sftp = "sftp";
    public const string Azure = "azure";
    public const string S3 = "s3";
    public const string Gcs = "gcs";
    public static readonly string[] All = [Folder, Smb, Ftp, Sftp, Azure, S3, Gcs];

    public static string Title(string kind) => kind switch
    {
        Folder => "Local or attached disk",
        Smb => "Network share (SMB)",
        Ftp => "FTP / FTPS server",
        Sftp => "SFTP server",
        Azure => "Azure Blob Storage",
        S3 => "Amazon S3 / S3-compatible",
        Gcs => "Google Cloud Storage",
        _ => kind,
    };
}

/// <summary>Non-secret settings of a destination (the meaning of each field depends on the kind).</summary>
public sealed class BackupTargetSettings
{
    /// <summary>Folder path, UNC path (\\server\share\folder), remote directory (FTP/SFTP) or object name prefix (cloud).</summary>
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("host")] public string? Host { get; set; }
    [JsonPropertyName("port")] public int? Port { get; set; }
    /// <summary>User name; Azure: storage account name; S3: access key ID.</summary>
    [JsonPropertyName("user")] public string? User { get; set; }
    /// <summary>S3 / GCS bucket or Azure container.</summary>
    [JsonPropertyName("bucket")] public string? Bucket { get; set; }
    [JsonPropertyName("region")] public string? Region { get; set; }
    /// <summary>S3-compatible service URL (Wasabi, MinIO, Backblaze B2 …) or Azure blob endpoint (sovereign clouds, private endpoints).</summary>
    [JsonPropertyName("endpoint")] public string? Endpoint { get; set; }
    /// <summary>FTP: "explicit" (FTPS, AUTH TLS) or "none".</summary>
    [JsonPropertyName("tls")] public string? Tls { get; set; }
    [JsonPropertyName("passive")] public bool Passive { get; set; } = true;
    /// <summary>SFTP: the server's SHA-256 host key fingerprint, pinned at the first connection.</summary>
    [JsonPropertyName("hostKey")] public string? HostKey { get; set; }
    /// <summary>Azure: "key" (storage account key) or "sas" (shared access signature).</summary>
    [JsonPropertyName("authMode")] public string? AuthMode { get; set; }
    /// <summary>S3-compatible: path-style URLs (https://endpoint/bucket/key).</summary>
    [JsonPropertyName("pathStyle")] public bool PathStyle { get; set; }
}

public sealed class BackupDestination
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = BackupKinds.Folder;
    public bool Enabled { get; set; } = true;
    /// <summary>Scheduled and manual backups kept at the destination (0 = keep all).</summary>
    public int Keep { get; set; } = 30;
    public BackupTargetSettings Settings { get; set; } = new();
    /// <summary>Password / secret key / account key or SAS (Password), private key or JSON key file (PrivateKey), key passphrase.</summary>
    public CredentialSecret Secret { get; set; } = new();
}

public sealed record RemoteBackup(string Name, long Size, DateTimeOffset? Modified);
public sealed record TargetTest(string Message, string? HostKey = null);

public sealed class BackupTargetException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>One destination's file operations. Uploads go to a temporary name first, so a half-copied file never looks like a backup.</summary>
public interface IBackupTarget : IDisposable
{
    string Where { get; }
    Task UploadAsync(string localFile, string name, CancellationToken ct);
    Task<List<RemoteBackup>> ListAsync(CancellationToken ct);
    Task DeleteAsync(string name, CancellationToken ct);
    Task DownloadAsync(string name, string localFile, CancellationToken ct);
    /// <summary>The SFTP host key seen at the last connection (to pin it).</summary>
    string? SeenHostKey => null;
}

public static class BackupTargets
{
    public static IBackupTarget Create(BackupDestination d, HttpClient? http = null) => d.Kind switch
    {
        BackupKinds.Folder => new FolderTarget(d.Settings.Path ?? ""),
        BackupKinds.Smb => new SmbTarget(d.Settings.Path ?? "", d.Settings.User, d.Secret.Password),
        BackupKinds.Ftp => new FtpTarget(d.Settings, d.Secret.Password),
        BackupKinds.Sftp => new SftpTarget(d.Settings, d.Secret),
        BackupKinds.Azure => new AzureBlobTarget(d.Settings, d.Secret.Password ?? "", http),
        BackupKinds.S3 => new S3Target(d.Settings, d.Secret.Password ?? "", http),
        BackupKinds.Gcs => new GcsTarget(d.Settings, d.Secret.PrivateKey ?? "", http),
        _ => throw new BackupTargetException($"Unknown destination type '{d.Kind}'."),
    };

    /// <summary>Null when the destination can be saved, else (field, message).</summary>
    public static (string Field, string Message)? Validate(BackupDestination d, bool hasStoredSecret)
    {
        var s = d.Settings;
        bool Has(string? v) => !string.IsNullOrWhiteSpace(v);
        if (!Has(d.Name) || d.Name.Length > 100) return ("name", "Enter a name (max 100 characters).");
        if (!BackupKinds.All.Contains(d.Kind)) return ("kind", "Choose a destination type.");
        if (d.Keep is < 0 or > BackupSettings.MaxKeep) return ("keep", $"Keep 0 (all) to {BackupSettings.MaxKeep} backups.");
        var secret = hasStoredSecret || Has(d.Secret.Password) || Has(d.Secret.PrivateKey);
        switch (d.Kind)
        {
            case BackupKinds.Folder:
                if (!Has(s.Path) || !System.IO.Path.IsPathFullyQualified(s.Path!)) return ("path", @"Enter a full folder path, for example D:\ZentriqBackups or E:\Backups\Zentriq.");
                break;
            case BackupKinds.Smb:
                if (!Has(s.Path) || !s.Path!.StartsWith(@"\\", StringComparison.Ordinal) || s.Path.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries).Length < 2)
                    return ("path", @"Enter the share as \\server\share or \\server\share\folder.");
                if (Has(s.User) && !secret) return ("password", "Enter the password of the account.");
                break;
            case BackupKinds.Ftp:
            case BackupKinds.Sftp:
                if (!Has(s.Host) || s.Host!.Contains('/') || s.Host.Contains(' ')) return ("host", "Enter the server's name or IP address (without ftp:// or a path).");
                if (s.Port is { } p && p is < 1 or > 65535) return ("port", "Port must be 1–65535.");
                if (!Has(s.User)) return ("user", "Enter the user name.");
                if (!secret) return ("password", d.Kind == BackupKinds.Sftp ? "Enter the password or paste a private key." : "Enter the password.");
                if (d.Kind == BackupKinds.Ftp && s.Tls is not (null or "explicit" or "none")) return ("tls", "Choose FTPS (TLS) or plain FTP.");
                break;
            case BackupKinds.Azure:
                if (!Has(s.User) || !System.Text.RegularExpressions.Regex.IsMatch(s.User!, "^[a-z0-9]{3,24}$")) return ("user", "Enter the storage account name (3–24 lower-case letters and digits).");
                if (!Has(s.Bucket) || !System.Text.RegularExpressions.Regex.IsMatch(s.Bucket!, "^[a-z0-9](?!.*--)[a-z0-9-]{1,61}[a-z0-9]$")) return ("bucket", "Enter the container name (3–63 lower-case letters, digits and hyphens).");
                if (s.AuthMode is not (null or "key" or "sas")) return ("authMode", "Choose account key or SAS token.");
                if (!secret) return ("password", s.AuthMode == "sas" ? "Paste the SAS token." : "Paste the storage account key.");
                if (Has(s.Endpoint) && !Uri.TryCreate(s.Endpoint, UriKind.Absolute, out _)) return ("endpoint", "Enter the endpoint as a URL, for example https://account.blob.core.usgovcloudapi.net.");
                break;
            case BackupKinds.S3:
                if (!Has(s.Bucket) || !System.Text.RegularExpressions.Regex.IsMatch(s.Bucket!, "^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$")) return ("bucket", "Enter the bucket name (3–63 lower-case letters, digits, dots and hyphens).");
                if (!Has(s.User)) return ("user", "Enter the access key ID.");
                if (!secret) return ("password", "Enter the secret access key.");
                if (!Has(s.Endpoint) && !Has(s.Region)) return ("region", "Enter the bucket's region, for example ap-south-1.");
                if (Has(s.Endpoint) && !Uri.TryCreate(s.Endpoint, UriKind.Absolute, out _)) return ("endpoint", "Enter the service URL, for example https://s3.eu-central-1.wasabisys.com.");
                break;
            case BackupKinds.Gcs:
                if (!Has(s.Bucket) || !System.Text.RegularExpressions.Regex.IsMatch(s.Bucket!, "^[a-z0-9][a-z0-9._-]{1,220}[a-z0-9]$")) return ("bucket", "Enter the bucket name.");
                if (Has(d.Secret.PrivateKey) && Zentriq.Infrastructure.Cloud.GcpCollector.ParseKey(d.Secret.PrivateKey) is null)
                    return ("privateKey", "Paste the service account's JSON key file (IAM & Admin › Service accounts › Keys › Add key › JSON).");
                if (!secret) return ("privateKey", "Paste the service account's JSON key file.");
                break;
        }
        if (Has(s.Path) && d.Kind is BackupKinds.Ftp or BackupKinds.Sftp or BackupKinds.Azure or BackupKinds.S3 or BackupKinds.Gcs && s.Path!.Contains(".."))
            return ("path", "The folder must not contain '..'.");
        return null;
    }

    /// <summary>Writes, lists and deletes a small test file; returns what was found.</summary>
    public static async Task<TargetTest> TestAsync(IBackupTarget target, CancellationToken ct)
    {
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"zentriq-test-{Guid.NewGuid():N}.tmp");
        var name = $"zentriq-write-test-{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(tmp, $"Zentriq backup destination test {DateTimeOffset.UtcNow:o}\r\n", ct);
        try
        {
            await target.UploadAsync(tmp, name, ct);
            await target.DeleteAsync(name, ct);
            var list = await target.ListAsync(ct);
            var size = list.Sum(f => f.Size);
            return new TargetTest($"Connected to {target.Where}: a test file was written and deleted. {list.Count} backup(s) there ({size / 1048576.0:0.#} MB).", target.SeenHostKey);
        }
        finally { try { File.Delete(tmp); } catch (IOException) { } }
    }

    internal static string Join(string? prefix, string name)
    {
        var p = (prefix ?? "").Replace('\\', '/').Trim('/');
        return p.Length == 0 ? name : $"{p}/{name}";
    }

    internal static bool IsBackupName(string name) => name.StartsWith("zentriq-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zbak", StringComparison.OrdinalIgnoreCase);

    /// <summary>Runs an operation up to three times when it fails for a passing reason (network, 5xx, throttling).</summary>
    public static async Task<T> RetryAsync<T>(Func<Task<T>> op, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return await op(); }
            catch (Exception ex) when (attempt < 3 && !ct.IsCancellationRequested && Transient(ex))
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt * 5), ct);
            }
        }
    }

    private static bool Transient(Exception ex) => ex is HttpRequestException or IOException or TimeoutException or TaskCanceledException
        || ex is BackupHttpException { Status: >= 500 or 408 or 429 };
}

/// <summary>An HTTP error from a cloud storage service.</summary>
public sealed class BackupHttpException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
