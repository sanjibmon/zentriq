using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zentriq.Inventory;

/// <summary>Shared by the server and the Zentriq Agent: enrollment-code format and certificate pinning.</summary>
public static class AgentProtocol
{
    /// <summary>True when agent <paramref name="version"/> is older than package <paramref name="latest"/> (1.1.1 = 1.1.1.0).</summary>
    public static bool IsOlderVersion(string? version, string? latest)
    {
        if (!System.Version.TryParse(version, out var v) || !System.Version.TryParse(latest, out var l)) return false;
        return Normalize(v) < Normalize(l);
    }

    private static System.Version Normalize(System.Version v) =>
        new System.Version(v.Major, v.Minor, System.Math.Max(v.Build, 0), System.Math.Max(v.Revision, 0));

    public const string CodePrefix = "ZQ1.";

    public sealed class EnrollmentCode
    {
        [JsonPropertyName("v")] public int Version { get; set; } = 1;
        /// <summary>Agent endpoint, e.g. https://zentriq.example.com:8444</summary>
        [JsonPropertyName("u")] public string Url { get; set; } = "";
        /// <summary>Enrollment secret (server stores only its SHA-256).</summary>
        [JsonPropertyName("t")] public string Token { get; set; } = "";
        /// <summary>SHA-256 of the server certificate's SubjectPublicKeyInfo (base64).</summary>
        [JsonPropertyName("p")] public List<string> Pins { get; set; } = new();
    }

    public static bool TryParseCode(string? code, out EnrollmentCode? result, out string? error)
    {
        result = null;
        error = null;
        var s = (code ?? "").Trim().Trim('"');
        if (!s.StartsWith(CodePrefix, StringComparison.Ordinal)) { error = "The enrollment code must start with " + CodePrefix; return false; }
        try
        {
            var b64 = s.Substring(CodePrefix.Length).Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            result = JsonSerializer.Deserialize<EnrollmentCode>(Convert.FromBase64String(b64));
        }
        catch (Exception ex) when (ex is FormatException || ex is JsonException) { error = "The enrollment code is damaged (copy it again from the console)."; return false; }
        if (result is null || result.Version != 1) { error = "Unsupported enrollment code version."; return false; }
        if (!Uri.TryCreate(result.Url, UriKind.Absolute, out var u) || u.Scheme != "https") { error = "The enrollment code has no valid https:// server address."; return false; }
        if (string.IsNullOrEmpty(result.Token)) { error = "The enrollment code has no token."; return false; }
        return true;
    }

    public static string FormatCode(EnrollmentCode code) =>
        CodePrefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(code)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>SHA-256 (base64) of the SubjectPublicKeyInfo of a DER-encoded X.509 certificate.</summary>
    public static string SpkiSha256(byte[] certificateDer)
    {
        using var sha = SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(ExtractSpki(certificateDer)));
    }

    /// <summary>Returns the SubjectPublicKeyInfo TLV from a DER certificate (minimal DER walk; no external ASN.1 library).</summary>
    public static byte[] ExtractSpki(byte[] der)
    {
        var pos = 0;
        ReadHeader(der, ref pos, 0x30, out _);                // Certificate
        ReadHeader(der, ref pos, 0x30, out _);                // TBSCertificate
        if (der[pos] == 0xA0) Skip(der, ref pos);             // [0] version
        for (var i = 0; i < 5; i++) Skip(der, ref pos);       // serial, signature, issuer, validity, subject
        var start = pos;
        Skip(der, ref pos);                                   // subjectPublicKeyInfo
        if (der[start] != 0x30) throw new FormatException("Unexpected certificate structure.");
        var spki = new byte[pos - start];
        Buffer.BlockCopy(der, start, spki, 0, spki.Length);
        return spki;
    }

    private static void ReadHeader(byte[] d, ref int pos, byte expectedTag, out int length)
    {
        if (pos >= d.Length || d[pos] != expectedTag) throw new FormatException("Unexpected certificate structure.");
        pos++;
        length = ReadLength(d, ref pos);
    }

    private static void Skip(byte[] d, ref int pos)
    {
        if (pos >= d.Length) throw new FormatException("Truncated certificate.");
        pos++;
        var len = ReadLength(d, ref pos);
        pos += len;
        if (pos > d.Length) throw new FormatException("Truncated certificate.");
    }

    private static int ReadLength(byte[] d, ref int pos)
    {
        int b = d[pos++];
        if (b < 0x80) return b;
        var n = b & 0x7F;
        if (n == 0 || n > 4) throw new FormatException("Unsupported DER length.");
        var len = 0;
        for (var i = 0; i < n; i++) len = (len << 8) | d[pos++];
        if (len < 0) throw new FormatException("Invalid DER length.");
        return len;
    }

    public static string Sha256Base64(string s)
    {
        using var sha = SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(s)));
    }
}
