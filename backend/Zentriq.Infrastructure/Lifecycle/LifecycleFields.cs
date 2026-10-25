using System.Globalization;
using System.Text.Json.Nodes;
using Npgsql;
using Zentriq.Infrastructure.NonIt;

namespace Zentriq.Infrastructure.Lifecycle;

/// <summary>An organisation-defined field on IT assets (Asset Lifecycle › Policies › Custom fields).</summary>
public sealed record LcField(Guid Id, string Key, string Label, string Type, string[] Options, bool Required, int Sort);

public static class LifecycleFields
{
    public static readonly string[] Types = ["text", "number", "date", "list", "yesno"];

    public static async Task<List<LcField>> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        var list = new List<LcField>();
        await using var cmd = new NpgsqlCommand("SELECT id, key, label, type, options, required, sort FROM zentriq.lifecycle_fields ORDER BY sort, lower(label)", conn, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new LcField(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetFieldValue<string[]>(4), r.GetBoolean(5), r.GetInt32(6)));
        return list;
    }

    /// <summary>"Cost centre" → "cost_centre".</summary>
    public static string KeyOf(string label)
    {
        var k = new string(label.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        while (k.Contains("__", StringComparison.Ordinal)) k = k.Replace("__", "_", StringComparison.Ordinal);
        if (k.Length == 0 || !char.IsLetter(k[0])) k = "f_" + k;
        return k.Length > 40 ? k[..40] : k;
    }

    /// <summary>
    /// The stored value for a field from text typed or uploaded: null when empty; numbers as numbers, dates as YYYY-MM-DD,
    /// yes/no as booleans, list values spelled as defined. Throws <see cref="LifecycleValidationException"/> when invalid.
    /// </summary>
    public static JsonNode? Parse(LcField f, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            if (f.Required) throw new LifecycleValidationException("custom." + f.Key, $"{f.Label} is required.");
            return null;
        }
        var t = text.Trim();
        switch (f.Type)
        {
            case "number":
                return SheetReader.ParseNumber(t) is { } n ? JsonValue.Create(n) : throw new LifecycleValidationException("custom." + f.Key, $"{f.Label}: \"{t}\" is not a number.");
            case "date":
                return SheetReader.ParseDate(t) is { } d ? JsonValue.Create(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) : throw new LifecycleValidationException("custom." + f.Key, $"{f.Label}: \"{t}\" is not a date (YYYY-MM-DD).");
            case "yesno":
                return t.ToLowerInvariant() switch
                {
                    "yes" or "y" or "true" or "1" => JsonValue.Create(true),
                    "no" or "n" or "false" or "0" => JsonValue.Create(false),
                    _ => throw new LifecycleValidationException("custom." + f.Key, $"{f.Label}: enter Yes or No."),
                };
            case "list":
                var match = f.Options.FirstOrDefault(o => string.Equals(o, t, StringComparison.OrdinalIgnoreCase));
                return match is not null ? JsonValue.Create(match) : throw new LifecycleValidationException("custom." + f.Key, $"{f.Label}: \"{t}\" is not one of {string.Join(", ", f.Options)}.");
            default:
                return JsonValue.Create(t.Length > 500 ? t[..500] : t);
        }
    }

    /// <summary>Text for the template, export and forms.</summary>
    public static string Show(JsonNode? v) => v switch
    {
        null => "",
        JsonValue b when b.TryGetValue<bool>(out var x) => x ? "Yes" : "No",
        _ => v.ToString(),
    };
}
