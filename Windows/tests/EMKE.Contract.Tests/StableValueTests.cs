using System.Text.Json;
using System.Text.Json.Serialization;
using EMKE.Core;

namespace EMKE.Contract.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.

[TestClass]
public sealed class StableValueTests
{
    [TestMethod]
    [TestCategory("Stable")]
    public void CoreStableConvertersMatchCompleteAppStateSchemaEnums()
    {
        using JsonDocument schema = SharedFixtureTests.LoadSchema(
            "app-state.schema.json");

        AssertConverterMatchesSchema(
            schema,
            "runtimeState",
            new RuntimeStateJsonConverter());
        AssertConverterMatchesSchema(
            schema,
            "channelState",
            new ChannelStateJsonConverter());
        AssertConverterMatchesSchema(
            schema,
            "inboundRoute",
            new InboundRouteJsonConverter());
        AssertConverterMatchesSchema(
            schema,
            "outboundRoute",
            new OutboundRouteJsonConverter());
        AssertConverterMatchesSchema(
            schema,
            "errorCategory",
            new ErrorCategoryJsonConverter());
        AssertConverterMatchesSchema(
            schema,
            "recoveryAction",
            new RecoveryActionJsonConverter());
    }

    [TestMethod]
    [TestCategory("Stable")]
    public void LanguageCodeConverterMatchesTargetLanguageSchemaEnum()
    {
        using JsonDocument schema = SharedFixtureTests.LoadSchema(
            "translation-events.schema.json");
        string[] schemaValues = schema.RootElement.GetProperty("oneOf")
            .EnumerateArray()
            .Where(static branch =>
                branch.GetProperty("properties").TryGetProperty(
                    "target_language",
                    out _))
            .SelectMany(static branch =>
                branch.GetProperty("properties")
                    .GetProperty("target_language")
                    .GetProperty("enum")
                    .EnumerateArray())
            .Select(static value => value.GetString()
                ?? throw new InvalidDataException(
                    "target_language enum values must be strings."))
            .ToArray();

        Assert.IsNotEmpty(schemaValues);
        Assert.HasCount(
            schemaValues.Length,
            schemaValues.Distinct(StringComparer.Ordinal));
        AssertConverterOutputs(schemaValues, new LanguageCodeJsonConverter());
    }

    [TestMethod]
    [TestCategory("Stable")]
    public void TranslationEventSchemaDeclaresUniqueClosedEventTypes()
    {
        using JsonDocument schema = SharedFixtureTests.LoadSchema(
            "translation-events.schema.json");
        JsonElement[] branches = schema.RootElement.GetProperty("oneOf")
            .EnumerateArray()
            .ToArray();
        HashSet<string> eventTypes = new(StringComparer.Ordinal);

        Assert.IsNotEmpty(branches);
        foreach (JsonElement branch in branches)
        {
            Assert.IsFalse(branch.GetProperty("additionalProperties").GetBoolean());
            string eventType = branch.GetProperty("properties")
                .GetProperty("type")
                .GetProperty("const")
                .GetString()
                ?? throw new InvalidDataException(
                    "Translation event type const values must be strings.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(eventType));
            Assert.IsTrue(
                eventTypes.Add(eventType),
                "Translation event type const values must be unique.");
        }

        Assert.HasCount(branches.Length, eventTypes);
    }

    private static void AssertConverterMatchesSchema<TEnum>(
        JsonDocument schema,
        string definitionName,
        JsonConverter<TEnum> converter)
        where TEnum : struct, Enum
    {
        string[] schemaValues = schema.RootElement
            .GetProperty("$defs")
            .GetProperty(definitionName)
            .GetProperty("enum")
            .EnumerateArray()
            .Select(static value => value.GetString()
                ?? throw new InvalidDataException(
                    "App state enum values must be strings."))
            .ToArray();

        Assert.IsNotEmpty(schemaValues);
        Assert.HasCount(
            schemaValues.Length,
            schemaValues.Distinct(StringComparer.Ordinal));
        AssertConverterOutputs(schemaValues, converter);
    }

    private static void AssertConverterOutputs<TEnum>(
        string[] schemaValues,
        JsonConverter<TEnum> converter)
        where TEnum : struct, Enum
    {
        JsonSerializerOptions options = new();
        options.Converters.Add(converter);
        string[] serializedValues = Enum.GetValues<TEnum>()
            .Select(value => JsonSerializer.Deserialize<string>(
                JsonSerializer.Serialize(value, options))!)
            .ToArray();

        Assert.HasCount(Enum.GetValues<TEnum>().Length, serializedValues);
        Assert.HasCount(
            serializedValues.Length,
            serializedValues.Distinct(StringComparer.Ordinal));
        CollectionAssert.AreEquivalent(schemaValues, serializedValues);
    }
}

#pragma warning restore CA1515
