using System.Net;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using Zentriq.Infrastructure.Secrets;

namespace Zentriq.Infrastructure.Backup;

/// <summary>
/// FTP server, plain or with explicit TLS (FTPS, AUTH TLS; the server's certificate must be trusted by Windows). The folder is
/// relative to the account's home directory and is created when missing.
/// </summary>
#pragma warning disable SYSLIB0014 // FtpWebRequest is the only FTP client in .NET; it is supported and sufficient here.
public sealed class FtpTarget(BackupTargetSettings s, string? password) : IBackupTarget
{
    private readonly string _host = s.Host ?? "";
    private readonly int _port = s.Port ?? 21;
    private readonly bool _tls = s.Tls != "none";
    private readonly string[] _dir = (s.Path ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    public string Where => $"{(_tls ? "ftps" : "ftp")}://{_host}{(_port == 21 ? "" : $":{_port}")}/{string.Join('/', _dir)}";

    private Uri UriOf(string? name, bool directory = false)
    {
        var parts = _dir.Select(Uri.EscapeDataString).ToList();
        if (name is not null) parts.Add(Uri.EscapeDataString(name));
        return new Uri($"ftp://{_host}:{_port}/{string.Join('/', parts)}{(directory ? "/" : "")}");
    }

    private FtpWebRequest Request(Uri uri, string method)
    {
        var r = (FtpWebRequest)WebRequest.Create(uri);
        r.Method = method;
        r.Credentials = new NetworkCredential(s.User, password);
        r.EnableSsl = _tls;
        r.UsePassive = s.Passive;
        r.UseBinary = true;
        r.KeepAlive = false;
        r.Timeout = 60_000;
        r.ReadWriteTimeout = 600_000;
        return r;
    }

    private async Task<T> Run<T>(string what, Func<Task<T>> op)
    {
        try { return await op(); }
        catch (WebException ex)
        {
            var status = (ex.Response as FtpWebResponse)?.StatusDescription?.Trim();
            var msg = (ex.Response as FtpWebResponse)?.StatusCode switch
            {
                FtpStatusCode.NotLoggedIn => "the user name or password was rejected",
                FtpStatusCode.ActionNotTakenFileUnavailable => $"not allowed or not found ({status})",
                _ when ex.Status == WebExceptionStatus.NameResolutionFailure => $"the server name {_host} was not found",
                _ when ex.Status == WebExceptionStatus.ConnectFailure => $"could not connect to {_host}:{_port}",
                _ when ex.Status == WebExceptionStatus.TrustFailure || ex.Status == WebExceptionStatus.SecureChannelFailure => "the server's TLS certificate is not trusted (or the server does not support FTPS)",
                _ => status ?? ex.Message,
            };
            throw new BackupTargetException($"FTP {what} failed: {msg}", ex);
        }
    }

    private async Task Simple(Uri uri, string method, Action<FtpWebRequest>? setup = null)
    {
        var r = Request(uri, method);
        setup?.Invoke(r);
        using var resp = (FtpWebResponse)await r.GetResponseAsync();
    }

    private async Task EnsureDirectoryAsync()
    {
        for (var i = 1; i <= _dir.Length; i++)
        {
            var uri = new Uri($"ftp://{_host}:{_port}/{string.Join('/', _dir.Take(i).Select(Uri.EscapeDataString))}/");
            try { await Simple(uri, WebRequestMethods.Ftp.MakeDirectory); }
            catch (WebException) { /* exists (or not allowed: the upload reports it) */ }
        }
    }

    public Task UploadAsync(string localFile, string name, CancellationToken ct) => Run("upload", async () =>
    {
        await EnsureDirectoryAsync();
        var tmp = name + ".partial";
        var r = Request(UriOf(tmp), WebRequestMethods.Ftp.UploadFile);
        await using (var src = File.OpenRead(localFile))
        {
            r.ContentLength = src.Length;
            await using (var dst = await r.GetRequestStreamAsync())
                await src.CopyToAsync(dst, 1 << 20, ct);
        }
        using (await r.GetResponseAsync()) { }
        try { await Simple(UriOf(name), WebRequestMethods.Ftp.DeleteFile); } catch (WebException) { }
        await Simple(UriOf(tmp), WebRequestMethods.Ftp.Rename, x => x.RenameTo = name);
        return true;
    });

    public Task<List<RemoteBackup>> ListAsync(CancellationToken ct) => Run("list", async () =>
    {
        var names = new List<string>();
        try
        {
            var r = Request(UriOf(null, directory: true), WebRequestMethods.Ftp.ListDirectory);
            using var resp = (FtpWebResponse)await r.GetResponseAsync();
            using var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8);
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                var n = line.Trim().Replace('\\', '/');
                n = n[(n.LastIndexOf('/') + 1)..];
                if (BackupTargets.IsBackupName(n)) names.Add(n);
            }
        }
        catch (WebException ex) when ((ex.Response as FtpWebResponse)?.StatusCode == FtpStatusCode.ActionNotTakenFileUnavailable) { return new List<RemoteBackup>(); }
        var list = new List<RemoteBackup>();
        foreach (var n in names)
        {
            long size = 0;
            try
            {
                var r = Request(UriOf(n), WebRequestMethods.Ftp.GetFileSize);
                using var resp = (FtpWebResponse)await r.GetResponseAsync();
                size = resp.ContentLength;
            }
            catch (WebException) { }
            list.Add(new RemoteBackup(n, size, BackupNames.Parse(n) is { } p ? new DateTimeOffset(p.Utc, TimeSpan.Zero) : null));
        }
        return list;
    });

    public Task DeleteAsync(string name, CancellationToken ct) => Run("delete", async () => { await Simple(UriOf(name), WebRequestMethods.Ftp.DeleteFile); return true; });

    public Task DownloadAsync(string name, string localFile, CancellationToken ct) => Run("download", async () =>
    {
        var r = Request(UriOf(name), WebRequestMethods.Ftp.DownloadFile);
        using var resp = (FtpWebResponse)await r.GetResponseAsync();
        await using var src = resp.GetResponseStream();
        await using var dst = File.Create(localFile);
        await src.CopyToAsync(dst, 1 << 20, ct);
        return true;
    });

    public void Dispose() { }
}
#pragma warning restore SYSLIB0014

/// <summary>
/// SFTP (SSH file transfer) with a password or a private key. The server's host key is pinned at the first connection;
/// a different key later stops the transfer before the credentials are sent.
/// </summary>
public sealed class SftpTarget(BackupTargetSettings s, CredentialSecret secret) : IBackupTarget
{
    private readonly string _dir = "/" + string.Join('/', (s.Path ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));
    private readonly bool _absolute = (s.Path ?? "").Replace('\\', '/').StartsWith('/');
    private SftpClient? _client;

    public string Where => $"sftp://{s.Host}{(s.Port is null or 22 ? "" : $":{s.Port}")}{(_absolute ? _dir : _dir == "/" ? "" : "/~" + _dir)}";
    public string? SeenHostKey { get; private set; }

    private string Dir(SftpClient c) => _absolute ? _dir : (_dir == "/" ? c.WorkingDirectory : c.WorkingDirectory.TrimEnd('/') + _dir);
    private string PathOf(SftpClient c, string name) => Dir(c).TrimEnd('/') + "/" + name;

    private SftpClient Client()
    {
        if (_client is { IsConnected: true }) return _client;
        var user = s.User ?? "";
        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrWhiteSpace(secret.PrivateKey))
        {
            try
            {
                using var ms = new MemoryStream(Encoding.UTF8.GetBytes(secret.PrivateKey));
                var key = string.IsNullOrEmpty(secret.Passphrase) ? new PrivateKeyFile(ms) : new PrivateKeyFile(ms, secret.Passphrase);
                methods.Add(new PrivateKeyAuthenticationMethod(user, key));
            }
            catch (Exception ex) when (ex is SshException or ArgumentException or InvalidOperationException)
            {
                throw new BackupTargetException("The private key could not be read (OpenSSH or PEM format; check the passphrase).", ex);
            }
        }
        if (!string.IsNullOrEmpty(secret.Password)) methods.Add(new PasswordAuthenticationMethod(user, secret.Password));
        var info = new ConnectionInfo(s.Host, s.Port ?? 22, user, methods.ToArray()) { Timeout = TimeSpan.FromSeconds(30) };
        var c = new SftpClient(info) { OperationTimeout = TimeSpan.FromMinutes(10) };
        c.HostKeyReceived += (_, e) =>
        {
            SeenHostKey = e.FingerPrintSHA256;
            e.CanTrust = string.IsNullOrEmpty(s.HostKey) || string.Equals(s.HostKey, e.FingerPrintSHA256, StringComparison.Ordinal);
        };
        try { c.Connect(); }
        catch (Exception ex) when (ex is SshException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            c.Dispose();
            if (!string.IsNullOrEmpty(s.HostKey) && SeenHostKey is not null && SeenHostKey != s.HostKey)
                throw new BackupTargetException($"The SFTP server's host key changed (expected {s.HostKey}, got {SeenHostKey}). Nothing was sent. If the change is expected, clear the pinned key on the destination and test again.", ex);
            throw new BackupTargetException(ex is SshAuthenticationException ? "SFTP sign-in failed: the user name, password or key was rejected." : $"Could not connect to {s.Host}:{s.Port ?? 22}: {ex.Message}", ex);
        }
        return _client = c;
    }

    private static Task<T> Op<T>(string what, Func<T> f) => Task.Run(() =>
    {
        try { return f(); }
        catch (Exception ex) when (ex is SshException or IOException)
        {
            throw new BackupTargetException($"SFTP {what} failed: {ex.Message}", ex);
        }
    });

    public Task UploadAsync(string localFile, string name, CancellationToken ct) => Op("upload", () =>
    {
        var c = Client();
        var dir = Dir(c);
        var built = "";
        foreach (var part in dir.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            built += "/" + part;
            if (!c.Exists(built)) c.CreateDirectory(built);
        }
        var tmp = PathOf(c, name + ".partial");
        using (var src = File.OpenRead(localFile)) c.UploadFile(src, tmp, true);
        var dest = PathOf(c, name);
        if (c.Exists(dest)) c.DeleteFile(dest);
        c.RenameFile(tmp, dest);
        return true;
    });

    public Task<List<RemoteBackup>> ListAsync(CancellationToken ct) => Op("list", () =>
    {
        var c = Client();
        var dir = Dir(c);
        if (!c.Exists(dir)) return new List<RemoteBackup>();
        return c.ListDirectory(dir).Where(f => f.IsRegularFile && BackupTargets.IsBackupName(f.Name))
            .Select(f => new RemoteBackup(f.Name, f.Length, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero))).ToList();
    });

    public Task DeleteAsync(string name, CancellationToken ct) => Op("delete", () => { var c = Client(); c.DeleteFile(PathOf(c, name)); return true; });

    public Task DownloadAsync(string name, string localFile, CancellationToken ct) => Op("download", () =>
    {
        var c = Client();
        using var dst = File.Create(localFile);
        c.DownloadFile(PathOf(c, name), dst);
        return true;
    });

    public void Dispose()
    {
        try { _client?.Disconnect(); } catch (Exception) { }
        _client?.Dispose();
    }
}
