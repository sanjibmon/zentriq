using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zentriq.Application;

/// <summary>
/// Reads every <see cref="DateTimeOffset"/> in JSON as UTC: agents and browsers send local times with their offset
/// ("+05:30"), and PostgreSQL's <c>timestamp with time zone</c> accepts only offset 0 through Npgsql. A time without an
/// offset is taken as UTC (as on the build servers, which run in UTC), never as the server's local time. Written as UTC.
/// </summary>
public sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("A date and time must be a string.");
        var s = reader.GetString()!;
        if (!DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var v))
            throw new JsonException($"'{s}' is not a date and time.");
        return v.ToUniversalTime();
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime());

    /// <summary>Adds the converter (for <see cref="DateTimeOffset"/> and, through it, nullable ones) unless present.</summary>
    public static JsonSerializerOptions AddTo(JsonSerializerOptions options)
    {
        if (!options.Converters.Any(c => c is UtcDateTimeOffsetConverter)) options.Converters.Add(new UtcDateTimeOffsetConverter());
        return options;
    }
}
