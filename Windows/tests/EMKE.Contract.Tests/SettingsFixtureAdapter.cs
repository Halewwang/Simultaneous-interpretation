using System.Text.Json;
using EMKE.Platform.Settings;

namespace EMKE.Contract.Tests;

internal static class SettingsFixtureAdapter
{
    private static readonly Version DefaultDriverVersion = new(0, 1, 0);

    public static void ValidateCompatibility(JsonElement fixture)
    {
        Assert.AreEqual(
            "settings.compatibility-gate.v1",
            fixture.GetProperty("fixtureId").GetString());

        foreach (JsonElement fixtureCase in fixture.GetProperty("cases").EnumerateArray())
        {
            string name = ReadRequiredString(fixtureCase, "name");
            JsonElement installedJson = fixtureCase.GetProperty("installed");
            Version recommendedVersion = fixtureCase.TryGetProperty(
                    "manifestOverride",
                    out JsonElement manifestOverride)
                ? Version.Parse(
                    ReadRequiredString(
                        manifestOverride,
                        "recommendedDriverVersion"))
                : DefaultDriverVersion;
            InstalledDriverSnapshot installed = new(
                installedJson.GetProperty("present").GetBoolean(),
                installedJson.GetProperty("signatureValid").GetBoolean(),
                installedJson.GetProperty("abi").GetInt32(),
                Version.Parse(ReadRequiredString(installedJson, "version")),
                installedJson.GetProperty("endpointCount").GetInt32());
            DriverCompatibilityRequirements requirements = new(
                RequiredAbi: 1,
                MinimumVersion: DefaultDriverVersion,
                RecommendedVersion: recommendedVersion,
                RequiredEndpointCount: 2);

            DriverCompatibilityDecision actual =
                WindowsDriverCompatibilityPolicy.Evaluate(installed, requirements);
            JsonElement expected = fixtureCase.GetProperty("expected");

            Assert.AreEqual(
                expected.GetProperty("allowed").GetBoolean(),
                actual.Allowed,
                name);
            Assert.AreEqual(
                ReadRequiredString(expected, "reason"),
                actual.Reason,
                name);
            Assert.AreEqual(
                expected.GetProperty("updateRecommended").GetBoolean(),
                actual.UpdateRecommended,
                name);
        }
    }

    public static void ValidateMigration(JsonElement fixture)
    {
        Assert.AreEqual(
            "settings.v1-migration.v1",
            fixture.GetProperty("fixtureId").GetString());

        foreach (JsonElement fixtureCase in fixture.GetProperty("cases").EnumerateArray())
        {
            string name = ReadRequiredString(fixtureCase, "name");
            JsonElement input = fixtureCase.GetProperty("input");
            string persistedJson = ReadRequiredString(input, "kind") switch
            {
                "object" => input.GetProperty("settings").GetRawText(),
                "raw" => ReadRequiredString(input, "raw"),
                string kind => throw new InvalidDataException(
                    $"Unsupported settings fixture input kind: {kind}"),
            };
            WindowsSettingsMigrationResult actual =
                WindowsSettingsMigrationPolicy.Migrate(persistedJson);
            JsonElement expected = fixtureCase.GetProperty("expected");

            Assert.AreEqual(
                ReadRequiredString(expected, "outcome"),
                actual.Outcome,
                name);
            Assert.AreEqual(
                expected.GetProperty("overwrite").GetBoolean(),
                actual.Overwrite,
                name);
            Assert.AreEqual(
                expected.GetProperty("quarantine").GetBoolean(),
                actual.Quarantine,
                name);
            AssertSettingsEqual(
                expected.GetProperty("resultSettings"),
                actual.Settings,
                name);
        }
    }

    private static void AssertSettingsEqual(
        JsonElement expected,
        WindowsSettingsDocument actual,
        string name)
    {
        Assert.AreEqual(
            expected.GetProperty("schemaVersion").GetInt32(),
            actual.SchemaVersion,
            name);
        Assert.AreEqual(ReadRequiredString(expected, "baseUrl"), actual.BaseUrl, name);
        Assert.AreEqual(ReadRequiredString(expected, "modelId"), actual.ModelId, name);
        Assert.AreEqual(
            ReadRequiredString(expected, "nativeLanguage"),
            actual.NativeLanguage,
            name);
        Assert.AreEqual(
            ReadRequiredString(expected, "meetingLanguage"),
            actual.MeetingLanguage,
            name);
        Assert.AreEqual(
            ReadRequiredString(expected, "interfaceLanguage"),
            actual.InterfaceLanguage,
            name);
        Assert.AreEqual(
            ReadOptionalString(expected, "inputEndpointId"),
            actual.InputEndpointId,
            name);
        Assert.AreEqual(
            ReadOptionalString(expected, "outputEndpointId"),
            actual.OutputEndpointId,
            name);
    }

    private static string ReadRequiredString(JsonElement element, string name)
    {
        return element.GetProperty(name).GetString()
            ?? throw new InvalidDataException($"Fixture field {name} must be a string.");
    }

    private static string? ReadOptionalString(JsonElement element, string name)
    {
        JsonElement value = element.GetProperty(name);
        return value.ValueKind == JsonValueKind.Null
            ? null
            : value.GetString()
                ?? throw new InvalidDataException(
                    $"Fixture field {name} must be null or a string.");
    }
}
