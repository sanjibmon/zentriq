using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Zentriq.Infrastructure.Cloud;

/// <summary>AWS Signature Version 4 (request signing for the AWS APIs), without the AWS SDK.</summary>
public static class AwsSigV4
{
    public static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
    public static string Sha256Hex(string s) => Hex(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    /// <summary>RFC 3986 encoding as AWS wants it (unreserved characters kept; '/' encoded unless <paramref name="path"/>).</summary>
    public static string Encode(string s, bool path = false)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            var c = (char)b;
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' or '~' || (path && c == '/')) sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    public static string CanonicalQuery(IEnumerable<KeyValuePair<string, string>> query) =>
        string.Join("&", query.Select(kv => (K: Encode(kv.Key), V: Encode(kv.Value))).OrderBy(x => x.K, StringComparer.Ordinal).ThenBy(x => x.V, StringComparer.Ordinal).Select(x => $"{x.K}={x.V}"));

    /// <summary>
    /// The Authorization header value. <paramref name="headers"/> are the headers to sign (lower-case names), and must
    /// include host and x-amz-date.
    /// </summary>
    public static string Authorization(string method, string path, string canonicalQuery, IReadOnlyDictionary<string, string> headers,
        string payloadHash, string accessKey, string secretKey, string region, string service, DateTime utc)
    {
        var amzDate = utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var day = amzDate[..8];
        var sorted = headers.Select(h => (K: h.Key.ToLowerInvariant().Trim(), V: string.Join(" ", h.Value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))))
            .OrderBy(h => h.K, StringComparer.Ordinal).ToList();
        var signedHeaders = string.Join(";", sorted.Select(h => h.K));
        var canonical = $"{method}\n{(path.Length == 0 ? "/" : path)}\n{canonicalQuery}\n{string.Concat(sorted.Select(h => $"{h.K}:{h.V}\n"))}\n{signedHeaders}\n{payloadHash}";
        var scope = $"{day}/{region}/{service}/aws4_request";
        var toSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Sha256Hex(canonical)}";
        var key = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), day), region), service), "aws4_request");
        var signature = Hex(Hmac(key, toSign));
        return $"AWS4-HMAC-SHA256 Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";
    }
}
