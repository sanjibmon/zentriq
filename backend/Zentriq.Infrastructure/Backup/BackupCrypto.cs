using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Zentriq.Infrastructure.Backup;

/// <summary>
/// Unencrypted first line of a backup. Format 1: the chunks are encrypted with the installation backup key itself.
/// Format 2: they are encrypted with a random data key, which is stored wrapped (AES-256-GCM) once per way to open the
/// backup — the installation backup key (recovery key) and, optionally, a key derived from the backup password.
/// </summary>
public sealed record BackupHeader(int Format, string ProductVersion, DateTimeOffset CreatedAt, string Label, string KeyFingerprint, string NonceSeed, string Machine,
    IReadOnlyList<BackupKeyWrap>? Keys = null, string? InstallationId = null, string? Contents = null)
{
    public bool HasPassword => Keys?.Any(k => k.Type == BackupKeyWrap.PasswordType) == true;
}

/// <summary>The data key wrapped for one way of opening a format-2 backup ("install" key or "password").</summary>
public sealed record BackupKeyWrap(string Type, string? Fingerprint, string? Salt, int? Iterations, string Nonce, string Wrapped)
{
    public const string InstallType = "install";
    public const string PasswordType = "password";
}

/// <summary>A key derived from the backup password (PBKDF2-SHA256); the password itself is never stored.</summary>
public sealed record BackupPasswordKey(string Salt, int Iterations, byte[] Key);

/// <summary>What can open a backup: installation backup keys (base64), a password and/or an already unwrapped data key.</summary>
public sealed record BackupUnlock(IReadOnlyList<string>? InstallKeys = null, string? Password = null, byte[]? DataKey = null);

public sealed class BackupKeyException(string message) : Exception(message);

/// <summary>
/// Streaming AES-256-GCM container for backups (format documented in docs/backup-restore.md):
/// "ZBAK1\n" + JSON header line + chunks of [u32 length|last-flag][16-byte tag][ciphertext].
/// Nonce = 4-byte random prefix || 8-byte big-endian chunk counter. The header bytes are the
/// associated data of every chunk, so header tampering, chunk reordering and truncation are detected.
/// </summary>
public static class BackupCrypto
{
    private static readonly byte[] Magic = "ZBAK1\n"u8.ToArray();
    public const int ChunkSize = 1 << 20;
    private const uint LastFlag = 0x8000_0000;
    public const int PasswordIterations = 600_000;
    public const int MinPasswordLength = 10;

    public static string Fingerprint(string base64Key) =>
        Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(base64Key)))[..16];

    /// <summary>Null when the password is acceptable, else why not.</summary>
    public static string? CheckPassword(string? password) =>
        string.IsNullOrEmpty(password) || password.Length < MinPasswordLength ? $"Use at least {MinPasswordLength} characters." :
        password.Length > 256 ? "Use at most 256 characters." : null;

    public static BackupPasswordKey DerivePasswordKey(string password, byte[]? salt = null, int iterations = PasswordIterations)
    {
        salt ??= RandomNumberGenerator.GetBytes(16);
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
        return new BackupPasswordKey(Convert.ToBase64String(salt), iterations, key);
    }

    private static byte[] WrapAad(string type) => Encoding.UTF8.GetBytes("zentriq-backup-key-v2|" + type);

    private static BackupKeyWrap Wrap(string type, byte[] kek, byte[] dataKey, string? fingerprint, string? salt, int? iterations)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ct = new byte[dataKey.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(kek, 16);
        aes.Encrypt(nonce, dataKey, ct, tag, WrapAad(type));
        return new BackupKeyWrap(type, fingerprint, salt, iterations, Convert.ToBase64String(nonce), Convert.ToBase64String([.. tag, .. ct]));
    }

    private static byte[]? Unwrap(BackupKeyWrap w, byte[] kek)
    {
        var nonce = Convert.FromBase64String(w.Nonce);
        var data = Convert.FromBase64String(w.Wrapped);
        if (nonce.Length != 12 || data.Length != 48) return null;
        var key = new byte[32];
        try
        {
            using var aes = new AesGcm(kek, 16);
            aes.Decrypt(nonce, data.AsSpan(16), data.AsSpan(0, 16), key, WrapAad(w.Type));
            return key;
        }
        catch (AuthenticationTagMismatchException) { return null; }
    }

    /// <summary>A format-2 header: a fresh data key wrapped with the installation key and, when given, the password key.</summary>
    public static (BackupHeader Header, byte[] DataKey) NewHeader(string productVersion, string label, string installKeyBase64, BackupPasswordKey? password,
        string machine, string? installationId = null, string? contents = null, DateTimeOffset? createdAt = null)
    {
        var dataKey = RandomNumberGenerator.GetBytes(32);
        var fp = Fingerprint(installKeyBase64);
        var keys = new List<BackupKeyWrap> { Wrap(BackupKeyWrap.InstallType, Convert.FromBase64String(installKeyBase64), dataKey, fp, null, null) };
        if (password is not null) keys.Add(Wrap(BackupKeyWrap.PasswordType, password.Key, dataKey, null, password.Salt, password.Iterations));
        var header = new BackupHeader(2, productVersion, createdAt ?? DateTimeOffset.UtcNow, label, fp, Convert.ToBase64String(RandomNumberGenerator.GetBytes(4)), machine,
            keys, installationId, contents);
        return (header, dataKey);
    }

    /// <summary>
    /// The key that decrypts the backup's chunks, from any of the given ways to open it. Throws <see cref="BackupKeyException"/>
    /// with a readable message when none fits.
    /// </summary>
    public static byte[] Unlock(BackupHeader h, BackupUnlock u)
    {
        if (u.DataKey is { Length: 32 } dk) return dk;
        var installKeys = u.InstallKeys ?? [];
        if (h.Format < 2)
        {
            var k = installKeys.FirstOrDefault(x => Fingerprint(x) == h.KeyFingerprint);
            if (k is not null) return Convert.FromBase64String(k);
            throw new BackupKeyException($"This backup can only be opened with the recovery key of the installation that made it (key {h.KeyFingerprint}).");
        }
        foreach (var w in h.Keys ?? [])
        {
            if (w.Type == BackupKeyWrap.InstallType)
            {
                var k = installKeys.FirstOrDefault(x => Fingerprint(x) == w.Fingerprint);
                if (k is not null && Unwrap(w, Convert.FromBase64String(k)) is { } key) return key;
            }
        }
        var pw = h.Keys?.FirstOrDefault(w => w.Type == BackupKeyWrap.PasswordType);
        if (!string.IsNullOrEmpty(u.Password))
        {
            if (pw is null) throw new BackupKeyException($"This backup has no password. Open it with the recovery key of the installation that made it (key {h.KeyFingerprint}).");
            if (pw.Iterations is not ({ } it and >= 10_000 and <= 10_000_000) || pw.Salt is null) throw new BackupKeyException("The backup's password data is damaged.");
            var kek = DerivePasswordKey(u.Password, Convert.FromBase64String(pw.Salt), it).Key;
            return Unwrap(pw, kek) ?? throw new BackupKeyException("The backup password is wrong.");
        }
        throw new BackupKeyException(pw is not null
            ? $"Enter the backup password, or use the recovery key of the installation that made the backup (key {h.KeyFingerprint})."
            : $"This backup can only be opened with the recovery key of the installation that made it (key {h.KeyFingerprint}).");
    }

    /// <summary>A recovery key from pasted text: the "Key:" line of backup-recovery-key.txt, or the base64 key itself.</summary>
    public static string? ParseRecoveryKey(string text)
    {
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("Key:", StringComparison.Ordinal));
        var key = (line is null ? text : line["Key:".Length..]).Trim();
        try { return Convert.FromBase64String(key).Length == 32 ? key : null; }
        catch (FormatException) { return null; }
    }

    /// <summary>Reads only the unencrypted header line (no key needed).</summary>
    public static BackupHeader ReadHeader(Stream input)
    {
        var magic = new byte[Magic.Length];
        if (input.Read(magic, 0, magic.Length) != Magic.Length || !magic.SequenceEqual(Magic)) throw new InvalidDataException("Not a Zentriq backup file (.zbak).");
        var headerBytes = new List<byte>();
        int b;
        while ((b = input.ReadByte()) != -1) { headerBytes.Add((byte)b); if (b == '\n') break; if (headerBytes.Count > 16384) throw new InvalidDataException("Backup header is corrupt."); }
        try { return JsonSerializer.Deserialize<BackupHeader>(headerBytes.ToArray()) ?? throw new InvalidDataException("Backup header is corrupt."); }
        catch (JsonException) { throw new InvalidDataException("Backup header is corrupt."); }
    }


    public static async Task EncryptAsync(Stream input, Stream output, byte[] key, BackupHeader header, CancellationToken ct)
    {
        var headerBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(header) + "\n");
        await output.WriteAsync(Magic, ct);
        await output.WriteAsync(headerBytes, ct);
        var prefix = Convert.FromBase64String(header.NonceSeed);
        using var aes = new AesGcm(key, 16);
        var buffer = new byte[ChunkSize];
        var next = new byte[ChunkSize];
        int read = await ReadFullAsync(input, buffer, ct);
        ulong counter = 0;
        while (true)
        {
            int nextRead = await ReadFullAsync(input, next, ct);
            var last = nextRead == 0;
            var nonce = new byte[12];
            prefix.CopyTo(nonce, 0);
            BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), counter++);
            var ct2 = new byte[read];
            var tag = new byte[16];
            aes.Encrypt(nonce, buffer.AsSpan(0, read), ct2, tag, headerBytes);
            var len = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)read | (last ? LastFlag : 0));
            await output.WriteAsync(len, ct);
            await output.WriteAsync(tag, ct);
            await output.WriteAsync(ct2, ct);
            if (last) break;
            (buffer, next) = (next, buffer);
            read = nextRead;
        }
    }

    public static async Task<BackupHeader> DecryptAsync(Stream input, Stream output, Func<BackupHeader, byte[]> keyFor, CancellationToken ct)
    {
        var magic = new byte[Magic.Length];
        if (await ReadFullAsync(input, magic, ct) != Magic.Length || !magic.SequenceEqual(Magic)) throw new InvalidDataException("Not a Zentriq backup file.");
        var headerBytes = new List<byte>();
        int b;
        while ((b = input.ReadByte()) != -1) { headerBytes.Add((byte)b); if (b == '\n') break; if (headerBytes.Count > 16384) throw new InvalidDataException("Backup header is corrupt."); }
        var hb = headerBytes.ToArray();
        var header = JsonSerializer.Deserialize<BackupHeader>(hb) ?? throw new InvalidDataException("Backup header is corrupt.");
        using var aes = new AesGcm(keyFor(header), 16);
        var prefix = Convert.FromBase64String(header.NonceSeed);
        ulong counter = 0;
        var lenBuf = new byte[4]; var tag = new byte[16];
        while (true)
        {
            if (await ReadFullAsync(input, lenBuf, ct) != 4) throw new InvalidDataException("Backup file is truncated.");
            var raw = BinaryPrimitives.ReadUInt32BigEndian(lenBuf);
            var last = (raw & LastFlag) != 0;
            var len = (int)(raw & ~LastFlag);
            if (len > ChunkSize) throw new InvalidDataException("Backup file is corrupt.");
            if (await ReadFullAsync(input, tag, ct) != 16) throw new InvalidDataException("Backup file is truncated.");
            var cipher = new byte[len];
            if (await ReadFullAsync(input, cipher, ct) != len) throw new InvalidDataException("Backup file is truncated.");
            var nonce = new byte[12];
            prefix.CopyTo(nonce, 0);
            BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), counter++);
            var plain = new byte[len];
            try { aes.Decrypt(nonce, cipher, tag, plain, hb); }
            catch (AuthenticationTagMismatchException) { throw new InvalidDataException("Backup decryption failed: wrong key or password, or the file was modified."); }
            await output.WriteAsync(plain, ct);
            if (last) return header;
        }
    }

    private static async Task<int> ReadFullAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            var n = await s.ReadAsync(buffer.AsMemory(total), ct);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
