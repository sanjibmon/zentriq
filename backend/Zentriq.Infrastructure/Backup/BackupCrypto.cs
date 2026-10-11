using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Zentriq.Infrastructure.Backup;

public sealed record BackupHeader(int Format, string ProductVersion, DateTimeOffset CreatedAt, string Label, string KeyFingerprint, string NonceSeed, string Machine);

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

    public static string Fingerprint(string base64Key) =>
        Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(base64Key)))[..16];


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
        while ((b = input.ReadByte()) != -1) { headerBytes.Add((byte)b); if (b == '\n') break; if (headerBytes.Count > 8192) throw new InvalidDataException("Backup header is corrupt."); }
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
            catch (AuthenticationTagMismatchException) { throw new InvalidDataException("Backup decryption failed: wrong recovery key or the file was modified."); }
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
