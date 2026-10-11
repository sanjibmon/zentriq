using System.Text.RegularExpressions;

namespace Zentriq.Application.Security;

/// <summary>Removes secrets from text before it is written to logs or diagnostic bundles.</summary>
public static partial class Redactor
{
    [GeneratedRegex(@"(?i)\b(password|pwd|passwd|secret|token|superpassword|apikey|api_key|privatekey|private_key|pfxpassword)\b(\s*[=:]\s*|""\s*:\s*"")([^;,""\s}]+)")]
    private static partial Regex KeyValue();

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----")]
    private static partial Regex PemPrivateKey();

    [GeneratedRegex(@"SCRAM-SHA-256\$[^\s'""]+")]
    private static partial Regex ScramVerifier();

    public const string Mask = "***REDACTED***";

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        var r = KeyValue().Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + Mask);
        r = PemPrivateKey().Replace(r, "-----PRIVATE KEY " + Mask + "-----");
        r = ScramVerifier().Replace(r, "SCRAM-SHA-256$" + Mask);
        return r;
    }
}
