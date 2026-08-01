using System.Text.Json;
using EMKE.Core;
using EMKE.Platform.Driver;
using EMKE.Platform.Settings;

namespace EMKE.Contract.Tests;

#pragma warning disable CA1859 // Contract fixtures intentionally invoke Core ports.

internal static class SettingsFixtureAdapter
{
    private static readonly Version DefaultDriverVersion = new(0, 1, 0);

    public static async Task ValidateCompatibilityAsync(JsonElement fixture)
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
            RecordingDriverSnapshotSource source = new(
                new WindowsInstalledDriverSnapshot(
                    installedJson.GetProperty("present").GetBoolean(),
                    installedJson.GetProperty("present").GetBoolean()
                        ? @"ROOT\EMKEVIRTUALAUDIO"
                        : null,
                    Version.Parse(ReadRequiredString(installedJson, "version")),
                    installedJson.GetProperty("abi").GetInt32(),
                    installedJson.GetProperty("signatureValid").GetBoolean()
                        ? "fixture signer"
                        : null,
                    installedJson.GetProperty("signatureValid").GetBoolean(),
                    CreateEndpointStates(
                        installedJson.GetProperty("endpointCount").GetInt32())));
            RecordingDriverCompatibilityDiagnostics diagnostics = new();
            IDriverManager manager = new WindowsDriverManager(
                source,
                new CompatibilityManifest(
                    appVersion: DefaultDriverVersion,
                    contractVersion: 1,
                    settingsSchemaVersion: 1,
                    driverAbiVersion: 1,
                    minimumDriverVersion: DefaultDriverVersion,
                    recommendedDriverVersion: recommendedVersion,
                    driverPackageAvailable: true,
                    channel: "contract",
                    minimumWindowsBuild: 19045,
                    requiredEndpointRoleCount: 2),
                ContractWindowsHostCompatibilitySource.Instance,
                diagnostics);

            DriverCompatibility actual =
                await manager.CheckCompatibilityAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            JsonElement expected = fixtureCase.GetProperty("expected");

            Assert.AreEqual(1, source.ReadCount, name);
            Assert.AreEqual(
                expected.GetProperty("allowed").GetBoolean(),
                actual.IsCompatible,
                name);
            Assert.AreEqual(
                ReadRequiredString(expected, "reason"),
                actual.StatusLabel,
                name);
            Assert.IsNotNull(diagnostics.LastObservation, name);
            Assert.AreEqual(
                expected.GetProperty("updateRecommended").GetBoolean(),
                diagnostics.LastObservation.UpdateRecommended,
                name);
        }
    }

    public static async Task ValidateMigrationAsync(JsonElement fixture)
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
            RecordingSettingsPersistence persistence = new(persistedJson);
            RecordingSettingsMigrationDiagnostics diagnostics = new();
            ISettingsStore store = new WindowsSettingsStore(
                persistence,
                diagnostics);

            RuntimeSettings? actual =
                await store.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            JsonElement expected = fixtureCase.GetProperty("expected");
            JsonElement expectedSettings = expected.GetProperty("resultSettings");

            Assert.IsNotNull(actual, name);
            Assert.AreEqual(1, persistence.ReadCount, name);
            Assert.AreEqual(
                expected.GetProperty("overwrite").GetBoolean() ? 1 : 0,
                persistence.OverwriteCount,
                name);
            Assert.AreEqual(
                expected.GetProperty("quarantine").GetBoolean() ? 1 : 0,
                persistence.QuarantineCount,
                name);
            Assert.AreEqual(
                expected.GetProperty("quarantine").GetBoolean()
                    ? persistedJson
                    : null,
                persistence.QuarantinedJson,
                name);
            Assert.AreEqual(
                ReadRequiredString(expectedSettings, "nativeLanguage"),
                SerializeLanguage(actual.SourceLanguage),
                name);
            Assert.AreEqual(
                ReadRequiredString(expectedSettings, "meetingLanguage"),
                SerializeLanguage(actual.TargetLanguage),
                name);
            Assert.AreEqual(
                ReadRequiredString(expectedSettings, "modelId"),
                actual.Model,
                name);
            Assert.IsFalse(actual.InboundBypass, name);
            Assert.IsFalse(actual.OutboundBypass, name);
            Assert.IsNotNull(diagnostics.LastObservation, name);
            Assert.AreEqual(
                ReadRequiredString(expected, "outcome"),
                diagnostics.LastObservation.Outcome,
                name);
            AssertSettingsEqual(
                expectedSettings,
                diagnostics.LastObservation.Settings,
                name);
            if (persistence.OverwriteCount == 1)
            {
                using JsonDocument overwritten =
                    JsonDocument.Parse(persistence.OverwrittenJson!);
                AssertPersistedSettingsEqual(
                    expectedSettings,
                    overwritten.RootElement,
                    name);
            }
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

    private static void AssertPersistedSettingsEqual(
        JsonElement expected,
        JsonElement actual,
        string name)
    {
        Assert.AreEqual(
            expected.GetProperty("schemaVersion").GetInt32(),
            actual.GetProperty("schemaVersion").GetInt32(),
            name);
        foreach (string propertyName in new[]
        {
            "baseUrl",
            "modelId",
            "nativeLanguage",
            "meetingLanguage",
            "interfaceLanguage",
        })
        {
            Assert.AreEqual(
                ReadRequiredString(expected, propertyName),
                ReadRequiredString(actual, propertyName),
                name);
        }

        Assert.AreEqual(
            ReadOptionalString(expected, "inputEndpointId"),
            ReadOptionalString(actual, "inputEndpointId"),
            name);
        Assert.AreEqual(
            ReadOptionalString(expected, "outputEndpointId"),
            ReadOptionalString(actual, "outputEndpointId"),
            name);
    }

    private static string SerializeLanguage(LanguageCode language)
    {
        return JsonSerializer.Deserialize<string>(
            JsonSerializer.Serialize(language))!;
    }

    private static IReadOnlyList<WindowsInstalledDriverEndpointState>
        CreateEndpointStates(int count)
    {
        string[] roles =
        [
            "meetingSpeakerRender",
            "appSpeakerCapture",
            "appMicrophoneRender",
            "meetingMicrophoneCapture",
        ];
        return roles
            .Take(Math.Min(count, roles.Length))
            .Select(static role =>
                new WindowsInstalledDriverEndpointState(role, "active"))
            .ToArray();
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

    private sealed class RecordingSettingsPersistence(string persistedJson)
        : IWindowsSettingsPersistence
    {
        public int ReadCount { get; private set; }

        public int OverwriteCount { get; private set; }

        public int QuarantineCount { get; private set; }

        public string? OverwrittenJson { get; private set; }

        public string? QuarantinedJson { get; private set; }

        public ValueTask<string?> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return ValueTask.FromResult<string?>(persistedJson);
        }

        public ValueTask OverwriteAsync(
            string canonicalJson,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OverwriteCount++;
            OverwrittenJson = canonicalJson;
            return ValueTask.CompletedTask;
        }

        public ValueTask QuarantineAsync(
            string invalidJson,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QuarantineCount++;
            QuarantinedJson = invalidJson;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingSettingsMigrationDiagnostics
        : IWindowsSettingsMigrationDiagnostics
    {
        public WindowsSettingsMigrationObservation? LastObservation { get; private set; }

        public void Record(WindowsSettingsMigrationObservation observation)
        {
            LastObservation = observation;
        }
    }

    private sealed class RecordingDriverSnapshotSource(
        WindowsInstalledDriverSnapshot snapshot)
        : IWindowsDriverSnapshotSource
    {
        public int ReadCount { get; private set; }

        public ValueTask<WindowsInstalledDriverSnapshot> ReadAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return ValueTask.FromResult(snapshot);
        }
    }

    private sealed class RecordingDriverCompatibilityDiagnostics
        : IWindowsDriverCompatibilityDiagnostics
    {
        public WindowsDriverCompatibilityObservation? LastObservation { get; private set; }

        public void Record(WindowsDriverCompatibilityObservation observation)
        {
            LastObservation = observation;
        }
    }

    private sealed class ContractWindowsHostCompatibilitySource
        : IWindowsHostCompatibilitySource
    {
        public static ContractWindowsHostCompatibilitySource Instance { get; } =
            new();

        private ContractWindowsHostCompatibilitySource()
        {
        }

        public int GetCurrentWindowsBuild()
        {
            return 19045;
        }
    }
}

#pragma warning restore CA1859
