using System.Text.Json;
using System.Text.Json.Serialization;

namespace Backend.Models;

/// <summary>
/// Explicit yyyy-MM-dd converter for <see cref="DateOnly"/>, used on every
/// JsonSerializerOptions instance in this app (API responses, the WebSocket
/// broadcast, and Kafka tick parsing) so the date format is guaranteed
/// regardless of which .NET version's built-in DateOnly support is present.
/// </summary>
public sealed class DateOnlyJsonConverter : JsonConverter<DateOnly>
{
    private const string Format = "yyyy-MM-dd";

    public override DateOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => DateOnly.ParseExact(reader.GetString()!, Format);

    public override void Write(Utf8JsonWriter writer, DateOnly value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString(Format));
}
