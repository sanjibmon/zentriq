using System.Text.RegularExpressions;

namespace Zentriq.Infrastructure.Assistant;

/// <summary>
/// Removes secrets from text before it is sent to an AI model or stored: passwords, keys, tokens, private keys,
/// connection-string passwords, enrollment and license codes. Applied to the question, the live facts and log lines.
/// </summary>
public static partial class AssistantRedactor
{
    public const string Mask = "[removed]";

    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var s = PrivateKeyRx().Replace(text, "-----PRIVATE KEY " + Mask + "-----");
        s = KeyValueRx().Replace(s, m => m.Groups[1].Value + m.Groups[2].Value + Mask);
        s = JsonSecretRx().Replace(s, m => m.Groups[1].Value + Mask + "\"");
        s = BearerRx().Replace(s, "$1 " + Mask);
        s = AwsKeyRx().Replace(s, Mask);
        s = AnthropicKeyRx().Replace(s, Mask);
        s = LicenseKeyRx().Replace(s, "ZQL-" + Mask);
        s = EnrollmentRx().Replace(s, Mask);
        s = LongSecretRx().Replace(s, Mask);
        return s;
    }

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----")]
    private static partial Regex PrivateKeyRx();
    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|secret|client[_ -]?secret|api[_ -]?key|access[_ -]?key|token|passphrase|community)(\s*[:=]\s*)(""[^""]*""|'[^']*'|[^\s;,""']+)")]
    private static partial Regex KeyValueRx();
    [GeneratedRegex(@"(?i)(""(?:password|secret|clientSecret|apiKey|token|passphrase|privateKey|accessKey|secretKey)""\s*:\s*"")[^""]*""")]
    private static partial Regex JsonSecretRx();
    [GeneratedRegex(@"(?i)\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=-]{8,}")]
    private static partial Regex BearerRx();
    [GeneratedRegex(@"\b(AKIA|ASIA)[A-Z0-9]{16}\b")]
    private static partial Regex AwsKeyRx();
    [GeneratedRegex(@"\bsk-ant-[A-Za-z0-9_-]{10,}")]
    private static partial Regex AnthropicKeyRx();
    [GeneratedRegex(@"\bZQL-[A-Z0-9]{5}(-[A-Z0-9]{5}){3}\b")]
    private static partial Regex LicenseKeyRx();
    [GeneratedRegex(@"\b[A-Z0-9]{5}-[A-Z0-9]{5}-[A-Z0-9]{5}-[A-Z0-9]{5}\b")]
    private static partial Regex EnrollmentRx();
    [GeneratedRegex(@"(?<![A-Za-z0-9+/])[A-Za-z0-9+/_-]{48,}={0,2}(?![A-Za-z0-9+/])")]
    private static partial Regex LongSecretRx();
}
