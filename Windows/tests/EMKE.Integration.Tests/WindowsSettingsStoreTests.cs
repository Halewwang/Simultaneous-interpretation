using System.Text.Json;
using EMKE.Core;
using EMKE.Platform.Settings;

namespace EMKE.Integration.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.
#pragma warning disable CA2007 // MSTest provides no UI synchronization context.

[TestClass]
public sealed class WindowsSettingsStoreTests
{
    private static readonly string[] PrivacyPreferenceIdentifiers =
        ["privacy-v2"];

    private string? _temporaryDirectory;

    [TestCleanup]
    public void Cleanup()
    {
        if (_temporaryDirectory is not null
            && Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task SaveWritesAndFlushesSameDirectoryTemporaryFileBeforeAtomicReplace()
    {
        string settingsPath = CreateSettingsPath();
        RecordingAtomicSettingsFileSystem fileSystem = new();
        FileSystemWindowsSettingsPersistence persistence = new(
            settingsPath,
            fileSystem,
            new FixedTimeProvider(
                new DateTimeOffset(
                    2026,
                    7,
                    28,
                    9,
                    8,
                    7,
                    654,
                    TimeSpan.Zero)));

        await persistence.OverwriteAsync(
            """{"schemaVersion":1}""",
            CancellationToken.None);

        Assert.HasCount(2, fileSystem.Operations);
        AtomicFileOperation write = fileSystem.Operations[0];
        AtomicFileOperation move = fileSystem.Operations[1];
        Assert.AreEqual(AtomicFileOperationKind.WriteAndFlush, write.Kind);
        Assert.AreEqual(AtomicFileOperationKind.MoveReplace, move.Kind);
        Assert.AreEqual(
            Path.GetDirectoryName(settingsPath),
            Path.GetDirectoryName(write.SourcePath));
        Assert.AreNotEqual(settingsPath, write.SourcePath);
        Assert.AreEqual(write.SourcePath, move.SourcePath);
        Assert.AreEqual(settingsPath, move.DestinationPath);
    }

    [TestMethod]
    public async Task MalformedSettingsAreRenamedWithoutOverwritingTheSourceBytes()
    {
        const string malformed = """{"schemaVersion":""";
        string settingsPath = CreateSettingsPath();
        await File.WriteAllTextAsync(settingsPath, malformed);
        FileSystemWindowsSettingsPersistence persistence = new(
            settingsPath,
            new FixedTimeProvider(
                new DateTimeOffset(
                    2026,
                    7,
                    28,
                    9,
                    8,
                    7,
                    654,
                    TimeSpan.Zero)));
        WindowsSettingsStore store = new(persistence);

        RuntimeSettings? loaded =
            await store.LoadAsync(CancellationToken.None);

        Assert.IsNotNull(loaded);
        Assert.IsFalse(File.Exists(settingsPath));
        string corruptPath = Path.Combine(
            Path.GetDirectoryName(settingsPath)!,
            "settings.corrupt.20260728T090807654Z.json");
        Assert.IsTrue(File.Exists(corruptPath));
        Assert.AreEqual(malformed, await File.ReadAllTextAsync(corruptPath));
    }

    [TestMethod]
    public async Task FutureSchemaIsRejectedWithoutChangingItsFile()
    {
        const string future = """{"schemaVersion":99,"future":"keep-me"}""";
        string settingsPath = CreateSettingsPath();
        await File.WriteAllTextAsync(settingsPath, future);
        WindowsSettingsStore store = new(
            new FileSystemWindowsSettingsPersistence(settingsPath));

        RuntimeSettings? loaded =
            await store.LoadAsync(CancellationToken.None);

        Assert.IsNotNull(loaded);
        Assert.AreEqual(future, await File.ReadAllTextAsync(settingsPath));
        Assert.IsEmpty(
            Directory.GetFiles(
                Path.GetDirectoryName(settingsPath)!,
                "settings.corrupt.*.json"));
    }

    [TestMethod]
    public async Task MigrationIsIdempotentOnTheRealFileSystem()
    {
        string settingsPath = CreateSettingsPath();
        await File.WriteAllTextAsync(settingsPath, "{}");
        WindowsSettingsStore first = new(
            new FileSystemWindowsSettingsPersistence(settingsPath));

        _ = await first.LoadAsync(CancellationToken.None);
        string migrated = await File.ReadAllTextAsync(settingsPath);
        File.SetLastWriteTimeUtc(
            settingsPath,
            new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        WindowsSettingsStore second = new(
            new FileSystemWindowsSettingsPersistence(settingsPath));

        _ = await second.LoadAsync(CancellationToken.None);

        Assert.AreEqual(migrated, await File.ReadAllTextAsync(settingsPath));
        Assert.AreEqual(
            new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            File.GetLastWriteTimeUtc(settingsPath));
    }

    [TestMethod]
    public async Task ProductSettingsPersistOnlyTheApprovedWhitelist()
    {
        string settingsPath = CreateSettingsPath();
        WindowsSettingsStore store = new(
            new FileSystemWindowsSettingsPersistence(settingsPath));
        WindowsProductSettings settings = new(
            new Uri("https://example.test/v1"),
            "gpt-realtime-translate",
            LanguageCode.En,
            LanguageCode.De,
            "input-device",
            "output-device",
            followDefaultInput: false,
            followDefaultOutput: true,
            "zhHans",
            ["privacy-v2", "audio-v1"]);

        await store.SaveProductSettingsAsync(
            settings,
            CancellationToken.None);

        using JsonDocument document = JsonDocument.Parse(
            await File.ReadAllTextAsync(settingsPath));
        string[] actualNames = document.RootElement
            .EnumerateObject()
            .Select(static property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] expectedNames =
        [
            "baseUrl",
            "followDefaultInput",
            "followDefaultOutput",
            "inputEndpointId",
            "interfaceLanguage",
            "meetingLanguage",
            "modelId",
            "nativeLanguage",
            "onboardingPreferenceIdentifiers",
            "outputEndpointId",
            "schemaVersion",
        ];
        Array.Sort(expectedNames, StringComparer.Ordinal);
        CollectionAssert.AreEqual(expectedNames, actualNames);
    }

    [TestMethod]
    public async Task RuntimeSavePreservesProductOnlyFieldsAndNeverPersistsBypass()
    {
        string settingsPath = CreateSettingsPath();
        WindowsSettingsStore store = new(
            new FileSystemWindowsSettingsPersistence(settingsPath));
        await store.SaveProductSettingsAsync(
            new WindowsProductSettings(
                new Uri("https://example.test/realtime"),
                "old-model",
                LanguageCode.Zh,
                LanguageCode.En,
                "input",
                "output",
                followDefaultInput: false,
                followDefaultOutput: false,
                "english",
                ["privacy-v2"]),
            CancellationToken.None);

        await store.SaveAsync(
            new RuntimeSettings(
                LanguageCode.De,
                LanguageCode.Zh,
                "new-model",
                inboundBypass: true,
                outboundBypass: true),
            CancellationToken.None);

        WindowsProductSettings reloaded =
            await new WindowsSettingsStore(
                    new FileSystemWindowsSettingsPersistence(settingsPath))
                .LoadProductSettingsAsync(CancellationToken.None);
        Assert.AreEqual(
            "https://example.test/realtime",
            reloaded.BaseUri.AbsoluteUri);
        Assert.AreEqual("new-model", reloaded.ModelId);
        Assert.AreEqual(LanguageCode.De, reloaded.NativeLanguage);
        Assert.AreEqual(LanguageCode.Zh, reloaded.MeetingLanguage);
        Assert.AreEqual("input", reloaded.InputEndpointId);
        Assert.AreEqual("output", reloaded.OutputEndpointId);
        Assert.IsFalse(reloaded.FollowDefaultInput);
        Assert.IsFalse(reloaded.FollowDefaultOutput);
        Assert.AreEqual("english", reloaded.InterfaceLanguage);
        CollectionAssert.AreEqual(
            PrivacyPreferenceIdentifiers,
            reloaded.OnboardingPreferenceIdentifiers.ToArray());
        Assert.IsFalse(
            (await File.ReadAllTextAsync(settingsPath))
                .Contains("bypass", StringComparison.OrdinalIgnoreCase));
    }

    private string CreateSettingsPath()
    {
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"emke-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
        return Path.Combine(_temporaryDirectory, "settings.json");
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow)
        : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }
    }

    private sealed class RecordingAtomicSettingsFileSystem
        : IAtomicSettingsFileSystem
    {
        public List<AtomicFileOperation> Operations { get; } = [];

        public ValueTask<string?> ReadAllTextAsync(
            string path,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<string?>(null);
        }

        public ValueTask WriteAndFlushAsync(
            string path,
            string contents,
            CancellationToken cancellationToken)
        {
            Operations.Add(
                new AtomicFileOperation(
                    AtomicFileOperationKind.WriteAndFlush,
                    path,
                    null));
            return ValueTask.CompletedTask;
        }

        public void Move(
            string sourcePath,
            string destinationPath,
            bool overwrite)
        {
            Operations.Add(
                new AtomicFileOperation(
                    overwrite
                        ? AtomicFileOperationKind.MoveReplace
                        : AtomicFileOperationKind.MoveNoReplace,
                    sourcePath,
                    destinationPath));
        }

        public bool FileExists(string path)
        {
            return false;
        }
    }
}

#pragma warning restore CA1515
#pragma warning restore CA2007
