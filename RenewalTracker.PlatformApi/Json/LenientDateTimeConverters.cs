using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RenewalTracker.PlatformApi.Json;

/// <summary>
/// Root cause of the 400 you just hit on PUT /api/platform/sales/enquiries/11:
/// the request body carried "createdDate":"" - an empty string. System.Text.Json's
/// built-in DateTime converter throws on that (empty string is not a valid ISO
/// date, and it isn't the JSON null literal either). When a property converter
/// throws during deserialization, the WHOLE model binds to null, which is why
/// ASP.NET also reported "The dto field is required" alongside the
/// "$.createdDate" parse error - both came from the same single failure.
///
/// CreatedDate (and NextDate on FollowUpDto) are never read from the client on
/// write - PlatformSalesController sets/keeps them server-side - so the
/// correct fix is to make deserialization of DateTime/DateTime? tolerant of
/// "" and whitespace-only strings (treated as "not sent") wherever they show
/// up, instead of relying on every DTO/every future date field to be edited
/// by hand. Registered globally in Program.cs's AddJsonOptions, so this
/// applies to every controller, not just Sales.
/// </summary>
public sealed class LenientDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            if (string.IsNullOrWhiteSpace(s)) return default;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                return parsed;
            }
        }
        return reader.GetDateTime();
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

/// <summary>Nullable counterpart - see <see cref="LenientDateTimeConverter"/>.</summary>
public sealed class LenientNullableDateTimeConverter : JsonConverter<DateTime?>
{
    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                return parsed;
            }
        }
        return reader.GetDateTime();
    }

    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value.HasValue) writer.WriteStringValue(value.Value);
        else writer.WriteNullValue();
    }
}
