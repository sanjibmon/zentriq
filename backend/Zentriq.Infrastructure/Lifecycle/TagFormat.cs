using System.Globalization;
using System.Text.RegularExpressions;
using Npgsql;

namespace Zentriq.Infrastructure.Lifecycle;

/// <summary>The organisation's asset tag scheme (settings lifecycle.tag_*).</summary>
public sealed record TagSettings(string Mode, string Format, string Org, int Start, bool TagDiscovered)
{
    public static readonly TagSettings Default = new("auto", "{PREFIX}-{SEQ:6}", "", 1, false);
    public bool Auto => Mode == "auto";

    public static async Task<TagSettings> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT key, value #>> '{}' FROM zentriq.system_settings WHERE key LIKE 'lifecycle.tag_%'", conn, tx);
        var d = new Dictionary<string, string?>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) d[r.GetString(0)] = r.IsDBNull(1) ? null : r.GetString(1);
        return new TagSettings(
            d.GetValueOrDefault("lifecycle.tag_mode") is "manual" ? "manual" : "auto",
            d.GetValueOrDefault("lifecycle.tag_format") is { Length: > 0 } f ? f : Default.Format,
            d.GetValueOrDefault("lifecycle.tag_org") ?? "",
            int.TryParse(d.GetValueOrDefault("lifecycle.tag_start"), out var s) && s > 0 ? s : 1,
            d.GetValueOrDefault("lifecycle.tag_discovered") == "true");
    }
}

/// <summary>
/// Renders asset tags from a format such as <c>{PREFIX}-{SEQ:6}</c> or <c>{ORG}/{YYYY}/{LOC}/{SEQ:5}</c>.
/// Tokens: {PREFIX} the device class prefix (LAP, DSK …), {ORG} the organisation code, {LOC} the location code (or the
/// first letters of its name), {YYYY} {YY} {MM} the date, {SEQ} or {SEQ:n} the running number (n digits, 1–10).
/// Numbers run separately for every rendered text around the number, so a new year or location starts again at the start
/// number. Anything else in the format is kept as typed (letters, digits and . _ / - only).
/// </summary>
public static class TagFormat
{
    private static readonly Regex Token = new(@"\{(PREFIX|ORG|LOC|YYYY|YY|MM|SEQ(?::(\d{1,2}))?)\}", RegexOptions.CultureInvariant);
    public static readonly Regex TagPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._/\-]{0,39}$", RegexOptions.CultureInvariant);

    /// <summary>Null when the format is usable, else why not.</summary>
    public static string? Validate(string? format)
    {
        if (string.IsNullOrWhiteSpace(format)) return "Enter a format, for example {PREFIX}-{SEQ:6}.";
        var seq = Token.Matches(format).Count(m => m.Groups[1].Value.StartsWith("SEQ", StringComparison.Ordinal));
        if (seq != 1) return "The format needs exactly one running number: {SEQ} or {SEQ:n} (n = digits, for example {SEQ:5}).";
        if (Token.Matches(format).Any(m => m.Groups[2].Success && int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) is < 1 or > 10))
            return "{SEQ:n}: n is 1 to 10 digits.";
        var literal = Token.Replace(format, "");
        if (Regex.IsMatch(literal, @"[{}]")) return "Unknown token. Use {PREFIX}, {ORG}, {LOC}, {YYYY}, {YY}, {MM} and {SEQ:n}.";
        if (!Regex.IsMatch(literal, @"^[A-Za-z0-9._/\-]*$")) return "Besides the tokens, use only letters, digits and . _ / -";
        var sample = Render(format, "LAPTOP12", "ORGCODE123", "LOCATION", new DateOnly(2026, 12, 31), 1234567890);
        if (sample.Length > 40) return "Tags made with this format can be longer than 40 characters.";
        return null;
    }

    /// <summary>The tag for a number.</summary>
    public static string Render(string format, string prefix, string org, string loc, DateOnly date, long seq) =>
        Token.Replace(format, m => m.Groups[1].Value switch
        {
            "PREFIX" => Clean(prefix),
            "ORG" => Clean(org),
            "LOC" => Clean(loc),
            "YYYY" => date.Year.ToString("D4", CultureInfo.InvariantCulture),
            "YY" => (date.Year % 100).ToString("D2", CultureInfo.InvariantCulture),
            "MM" => date.Month.ToString("D2", CultureInfo.InvariantCulture),
            _ => seq.ToString("D" + (m.Groups[2].Success ? m.Groups[2].Value : "6"), CultureInfo.InvariantCulture),
        }).Replace("--", "-").Replace("//", "/").Trim('-', '/', '.', '_');

    /// <summary>The counter key: the rendered tag with "#" for the number.</summary>
    public static string CounterKey(string format, string prefix, string org, string loc, DateOnly date) =>
        Token.Replace(format, m => m.Groups[1].Value.StartsWith("SEQ", StringComparison.Ordinal) ? "#" : Render(m.Value, prefix, org, loc, date, 0));

    /// <summary>A location's code, or the first letters of its name ("Pune office" → "PUNE").</summary>
    public static string LocationCode(string? code, string? name)
    {
        if (!string.IsNullOrWhiteSpace(code)) return Clean(code);
        if (string.IsNullOrWhiteSpace(name)) return "";
        var c = Clean(name.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]).ToUpperInvariant();
        return c.Length > 4 ? c[..4] : c;
    }

    private static string Clean(string s) => Regex.Replace(s.Trim(), @"[^A-Za-z0-9._\-]", "");
}
