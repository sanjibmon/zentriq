using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Zentriq.Infrastructure.Backup;

/// <summary>A folder on this server: another disk, a USB / removable drive, or a mapped path.</summary>
public class FolderTarget(string folder) : IBackupTarget
{
    protected string Folder { get; } = folder;
    public virtual string Where => Folder;

    protected virtual void Connect() { }

    public Task UploadAsync(string localFile, string name, CancellationToken ct) => Task.Run(() =>
    {
        Connect();
        try
        {
            Directory.CreateDirectory(Folder);
            var dest = Path.Combine(Folder, name);
            var tmp = dest + ".partial";
            using (var src = File.OpenRead(localFile))
            using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                src.CopyTo(dst, 1 << 20);
            ct.ThrowIfCancellationRequested();
            File.Move(tmp, dest, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BackupTargetException($"Could not write to {Where}: {ex.Message}", ex);
        }
    }, ct);

    public Task<List<RemoteBackup>> ListAsync(CancellationToken ct) => Task.Run(() =>
    {
        Connect();
        try
        {
            if (!Directory.Exists(Folder)) return new List<RemoteBackup>();
            return new DirectoryInfo(Folder).EnumerateFiles("zentriq-*.zbak")
                .Select(f => new RemoteBackup(f.Name, f.Length, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero))).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BackupTargetException($"Could not read {Where}: {ex.Message}", ex);
        }
    }, ct);

    public Task DeleteAsync(string name, CancellationToken ct) => Task.Run(() =>
    {
        Connect();
        try { File.Delete(Path.Combine(Folder, name)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new BackupTargetException($"Could not delete {name} in {Where}: {ex.Message}", ex); }
    }, ct);

    public Task DownloadAsync(string name, string localFile, CancellationToken ct) => Task.Run(() =>
    {
        Connect();
        try
        {
            using var src = File.OpenRead(Path.Combine(Folder, name));
            using var dst = File.Create(localFile);
            src.CopyTo(dst, 1 << 20);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new BackupTargetException($"Could not read {name} in {Where}: {ex.Message}", ex); }
    }, ct);

    public virtual void Dispose() { GC.SuppressFinalize(this); }
}

/// <summary>
/// A Windows file share. With a user name, the share is connected with that account for the copy (WNetAddConnection2);
/// without one, the server's own computer account is used (grant DOMAIN\SERVER$ write access to the share).
/// </summary>
public sealed class SmbTarget(string unc, string? user, string? password) : FolderTarget(unc)
{
    private bool _connected;
    private readonly string _share = ShareOf(unc);

    public override string Where => Folder;

    public static string ShareOf(string unc)
    {
        var parts = unc.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}" : unc;
    }

    protected override void Connect()
    {
        if (_connected || string.IsNullOrWhiteSpace(user)) return;
        if (!OperatingSystem.IsWindows()) throw new BackupTargetException("Network shares (SMB) need a Windows server.");
        var r = Native.Add(_share, user!, password ?? "");
        if (r == Native.SessionCredentialConflict)
        {
            // Already connected with other credentials (for example an earlier run): reconnect with these.
            Native.Cancel(_share);
            r = Native.Add(_share, user!, password ?? "");
        }
        if (r != 0) throw new BackupTargetException($"Could not connect to {_share}: {Describe(r)}");
        _connected = true;
    }

    private static string Describe(int code) => code switch
    {
        5 => "access denied — give the account change (write) permission on the share and its folder",
        53 or 67 => "the server or share was not found — check the name and that the server can be reached on port 445",
        86 or 1326 => "the user name or password was rejected",
        1219 => "this server is already connected to the share with another account",
        1311 or 1355 => "no domain controller could check the account — use a local account of the file server (SERVER\\user)",
        1909 => "the account is locked out",
        _ => new Win32Exception(code).Message,
    };

    public override void Dispose()
    {
        if (_connected) { try { Native.Cancel(_share); } catch (Exception) { } _connected = false; }
        base.Dispose();
    }

    private static class Native
    {
        public const int SessionCredentialConflict = 1219;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NetResource
        {
            public int Scope, Type, DisplayType, Usage;
            public string? LocalName, RemoteName, Comment, Provider;
        }

        [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetAddConnection2W")]
        private static extern int WNetAddConnection2(ref NetResource resource, string? password, string? user, int flags);

        [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetCancelConnection2W")]
        private static extern int WNetCancelConnection2(string name, int flags, bool force);

        public static int Add(string share, string user, string password)
        {
            var r = new NetResource { Type = 1 /* RESOURCETYPE_DISK */, RemoteName = share };
            return WNetAddConnection2(ref r, password, user, 4 /* CONNECT_TEMPORARY */);
        }

        public static void Cancel(string share) => WNetCancelConnection2(share, 0, true);
    }
}
