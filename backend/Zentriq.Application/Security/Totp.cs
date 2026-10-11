using System.Security.Cryptography;

namespace Zentriq.Application.Security;

/// <summary>RFC 6238 TOTP (SHA-1, 6 digits, 30 s) compatible with common authenticator apps.</summary>
public static class Totp
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    public static string ToBase32(byte[] data)
    {
        var sb = new System.Text.StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b; bits += 8;
            while (bits >= 5) { sb.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static string Code(byte[] secret, long timeStep)
    {
        Span<byte> counter = stackalloc byte[8];
        for (int i = 7; i >= 0; i--) { counter[i] = (byte)(timeStep & 0xFF); timeStep >>= 8; }
        var hash = HMACSHA1.HashData(secret, counter);
        int offset = hash[^1] & 0x0F;
        int binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    /// <summary>Accepts the current step and one step either side to tolerate small clock skew.</summary>
    public static bool Verify(byte[] secret, string? code, DateTimeOffset now)
    {
        if (code is null || code.Length != 6 || !code.All(char.IsDigit)) return false;
        var step = now.ToUnixTimeSeconds() / 30;
        for (long s = step - 1; s <= step + 1; s++)
            if (CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(Code(secret, s)), System.Text.Encoding.ASCII.GetBytes(code)))
                return true;
        return false;
    }

    public static string OtpAuthUri(string issuer, string account, byte[] secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}?secret={ToBase32(secret)}&issuer={Uri.EscapeDataString(issuer)}&digits=6&period=30";
}
