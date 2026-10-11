using System.Security.Cryptography;
using System.Text;

namespace Zentriq.Application.Licensing;

/// <summary>HMAC over the immutable trial fields, keyed with the per-installation secret.</summary>
public static class TrialIntegrity
{
    public static string Compute(byte[] installSecret, Guid installationId, DateTimeOffset startedAt, DateTimeOffset expiresAt)
    {
        var msg = $"{installationId:D}|{startedAt.ToUniversalTime().ToUnixTimeMilliseconds()}|{expiresAt.ToUniversalTime().ToUnixTimeMilliseconds()}";
        return Convert.ToHexString(HMACSHA256.HashData(installSecret, Encoding.UTF8.GetBytes(msg)));
    }

    public static bool Verify(byte[] installSecret, Guid installationId, DateTimeOffset startedAt, DateTimeOffset expiresAt, string mac) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Compute(installSecret, installationId, startedAt, expiresAt)),
            Encoding.ASCII.GetBytes(mac ?? string.Empty));
}
