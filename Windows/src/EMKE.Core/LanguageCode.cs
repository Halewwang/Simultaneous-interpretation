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

public sealed record LanguageProbabilities
{
    private const double SumTolerance = 1e-9;

    public LanguageProbabilities(double zh, double en, double de)
    {
        ValidateComponent(zh, nameof(zh));
        ValidateComponent(en, nameof(en));
        ValidateComponent(de, nameof(de));

        double sum = zh + en + de;
        if (Math.Abs(sum - 1) > SumTolerance)
        {
            throw new ArgumentOutOfRangeException(
                nameof(zh),
                sum,
                $"Language probabilities must sum to 1 within {SumTolerance}.");
        }

        Zh = zh;
        En = en;
        De = de;
    }

    public double Zh { get; }

    public double En { get; }

    public double De { get; }

    public double this[LanguageCode language]
    {
        get
        {
            DomainEnum.ThrowIfUndefined(language, nameof(language));
            return language switch
            {
                LanguageCode.Zh => Zh,
                LanguageCode.En => En,
                LanguageCode.De => De,
                _ => throw new ArgumentOutOfRangeException(nameof(language)),
            };
        }
    }

    private static void ValidateComponent(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value < 0 || value > 1)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Language probabilities must be finite values from 0 through 1.");
        }
    }
}

internal static class DomainEnum
{
    public static void ThrowIfUndefined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Undefined enum values are not allowed.");
        }
    }
}
