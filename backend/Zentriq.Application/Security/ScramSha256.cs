using System.Security.Cryptography;
using System.Text;

namespace Zentriq.Application.Security;

/// <summary>
/// Builds a PostgreSQL SCRAM-SHA-256 password verifier on the client, so CREATE/ALTER ROLE
/// statements never carry a plaintext password (which could otherwise appear in server logs
/// when log_statement = 'ddl').
/// </summary>
public static class ScramSha256
{
    public static string Verifier(string password, int iterations = 4096, byte[]? salt = null)
    {
        salt ??= RandomNumberGenerator.GetBytes(16);
        var salted = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
        var clientKey = HMACSHA256.HashData(salted, Encoding.UTF8.GetBytes("Client Key"));
        var storedKey = SHA256.HashData(clientKey);
        var serverKey = HMACSHA256.HashData(salted, Encoding.UTF8.GetBytes("Server Key"));
        return $"SCRAM-SHA-256${iterations}:{Convert.ToBase64String(salt)}${Convert.ToBase64String(storedKey)}:{Convert.ToBase64String(serverKey)}";
    }
}
