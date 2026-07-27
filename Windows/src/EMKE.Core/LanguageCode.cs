using System.Text.Json;
using System.Text.Json.Serialization;

namespace EMKE.Core;

[JsonConverter(typeof(LanguageCodeJsonConverter))]
public enum LanguageCode
{
    Zh,
    En,
    De,
}

public sealed class LanguageCodeJsonConverter : JsonConverter<LanguageCode>
{
    public override LanguageCode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("LanguageCode must be a string.");
        }

        return reader.GetString() switch
        {
            "zh" => LanguageCode.Zh,
            "en" => LanguageCode.En,
            "de" => LanguageCode.De,
            _ => throw new JsonException("Unknown LanguageCode value."),
        };
    }

    public override void Write(Utf8JsonWriter writer, LanguageCode value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        string stableValue = value switch
        {
            LanguageCode.Zh => "zh",
            LanguageCode.En => "en",
            LanguageCode.De => "de",
            _ => throw new JsonException("Undefined LanguageCode value."),
        };

        writer.WriteStringValue(stableValue);
    }
}
