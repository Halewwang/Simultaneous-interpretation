using System.Text.Json;
using EMKE.Realtime;

namespace EMKE.Contract.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.

[TestClass]
public sealed class SharedFixtureTests
{
    [TestMethod]
    [TestCategory("Inventory")]
    public void ContractManifestDeclaresCanonicalInventory()
    {
        string contractManifestPath = RepositoryPaths.FindContractManifest();

        using JsonDocument contractManifest = LoadJson(contractManifestPath);
        JsonElement root = contractManifest.RootElement;

        Assert.AreEqual(1, root.GetProperty("contractVersion").GetInt32());
        Assert.AreEqual(3, root.GetProperty("schemas").GetArrayLength());

        string fixtureManifestEntry = root.GetProperty("fixtureManifest").GetString()
            ?? throw new InvalidDataException("The fixture manifest entry must be a string.");
        string fixtureManifestPath = RepositoryPaths.ResolveFixtureManifest(
            contractManifestPath,
            fixtureManifestEntry);

        using JsonDocument fixtureManifest = LoadJson(fixtureManifestPath);
        Assert.AreEqual(1, fixtureManifest.RootElement.GetProperty("contractVersion").GetInt32());
        Assert.AreEqual(8, fixtureManifest.RootElement.GetProperty("fixtures").GetArrayLength());
    }

    [TestMethod]
    [TestCategory("Inventory")]
    public void SchemaInventoryContainsThreeUniqueParseableFiles()
    {
        string contractManifestPath = RepositoryPaths.FindContractManifest();
        using JsonDocument contractManifest = LoadJson(contractManifestPath);

        IReadOnlyList<string> schemaPaths = RepositoryPaths.ResolveSchemaFiles(
            contractManifestPath,
            contractManifest.RootElement.GetProperty("schemas"));

        Assert.HasCount(3, schemaPaths);
        ValidateSchemaIdentities(schemaPaths);
    }

    [TestMethod]
    [TestCategory("Inventory")]
    public void SchemaInventoryRejectsDuplicateSchemaIdentities()
    {
        string temporaryRoot = CreateTemporaryRoot();

        try
        {
            string schemaDirectory = Directory.CreateDirectory(
                Path.Combine(temporaryRoot, "Shared", "Contracts", "v1")).FullName;
            File.WriteAllText(
                Path.Combine(schemaDirectory, "first.schema.json"),
                """{"$id":"urn:emke:test:duplicate"}""");
            File.WriteAllText(
                Path.Combine(schemaDirectory, "second.schema.json"),
                """{"$id":"urn:emke:test:duplicate"}""");
            string contractManifestPath = WriteContractManifest(
                temporaryRoot,
                ["v1/first.schema.json", "v1/second.schema.json"]);
            using JsonDocument contractManifest = LoadJson(contractManifestPath);
            IReadOnlyList<string> schemaPaths = RepositoryPaths.ResolveSchemaFiles(
                contractManifestPath,
                contractManifest.RootElement.GetProperty("schemas"));

            Assert.ThrowsExactly<InvalidDataException>(
                () => ValidateSchemaIdentities(schemaPaths));
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Inventory")]
    public void FixtureInventoryContainsEightUniqueFilesWithCanonicalMetadata()
    {
        FixtureInventory inventory = LoadFixtureInventory();
        HashSet<string> fixtureIds = new(StringComparer.Ordinal);

        Assert.HasCount(8, inventory.Entries);
        Assert.HasCount(8, inventory.Paths);

        for (int index = 0; index < inventory.Paths.Count; index++)
        {
            string entry = inventory.Entries[index];
            using JsonDocument fixture = LoadJson(inventory.Paths[index]);
            JsonElement root = fixture.RootElement;

            Assert.AreEqual(JsonValueKind.Object, root.ValueKind);
            Assert.AreEqual(1, root.GetProperty("contractVersion").GetInt32());

            string fixtureId = root.GetProperty("fixtureId").GetString()
                ?? throw new InvalidDataException("Fixture fixtureId must be a string.");
            string category = root.GetProperty("category").GetString()
                ?? throw new InvalidDataException("Fixture category must be a string.");

            Assert.IsFalse(string.IsNullOrWhiteSpace(fixtureId));
            Assert.IsFalse(string.IsNullOrWhiteSpace(category));
            Assert.IsTrue(
                fixtureIds.Add(fixtureId),
                "Fixture fixtureId values must be unique.");
            Assert.AreEqual(ExpectedCategory(entry), category);
        }
    }

    [TestMethod]
    [TestCategory("Inventory")]
    public void RepositoryDiscoveryChecksCurrentDirectoryAndEightParentsOnly()
    {
        string temporaryRoot = CreateTemporaryRoot();

        try
        {
            string contractManifestPath = WriteContractManifest(
                temporaryRoot,
                ["v1/example.schema.json"]);
            string eightLevelsDeep = temporaryRoot;
            for (int level = 0; level < 8; level++)
            {
                eightLevelsDeep = Directory.CreateDirectory(
                    Path.Combine(eightLevelsDeep, $"level-{level}")).FullName;
            }

            Assert.AreEqual(
                contractManifestPath,
                RepositoryPaths.FindContractManifestFrom(eightLevelsDeep));

            string nineLevelsDeep = Directory.CreateDirectory(
                Path.Combine(eightLevelsDeep, "level-8")).FullName;
            FileNotFoundException exception = Assert.ThrowsExactly<FileNotFoundException>(
                () => RepositoryPaths.FindContractManifestFrom(nineLevelsDeep));

            Assert.AreEqual(
                "Unable to locate Shared/Contracts/contract-manifest.json within the current directory and eight parent levels.",
                exception.Message);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Inventory")]
    public void ManifestFileEntriesRejectUnsafeOrInvalidPaths()
    {
        string temporaryRoot = CreateTemporaryRoot();

        try
        {
            string contractsRoot = Path.Combine(temporaryRoot, "Shared", "Contracts");
            string validSchemaDirectory = Directory.CreateDirectory(
                Path.Combine(contractsRoot, "v1")).FullName;
            string validSchemaPath = Path.Combine(validSchemaDirectory, "example.schema.json");
            File.WriteAllText(validSchemaPath, "{}");
            string outsidePath = Path.Combine(temporaryRoot, "outside.schema.json");
            File.WriteAllText(outsidePath, "{}");
            Directory.CreateDirectory(Path.Combine(validSchemaDirectory, "directory.schema.json"));

            AssertInvalidSchemaEntries(temporaryRoot, outsidePath);
            AssertInvalidSchemaEntries(temporaryRoot, "../../outside.schema.json");
            AssertInvalidSchemaEntries(temporaryRoot, "v1/missing.schema.json");
            AssertInvalidSchemaEntries(
                temporaryRoot,
                "v1/example.schema.json",
                "v1/example.schema.json");
            AssertInvalidSchemaEntries(temporaryRoot, "v1/directory.schema.json");
            AssertInvalidSchemaEntries(temporaryRoot, "v1/../v1/example.schema.json");

            string contractManifestPath = WriteContractManifest(
                temporaryRoot,
                ["v1/example.schema.json"],
                "../../outside.schema.json");
            using JsonDocument contractManifest = LoadJson(contractManifestPath);
            string unsafeFixtureManifest =
                contractManifest.RootElement.GetProperty("fixtureManifest").GetString()
                ?? throw new InvalidDataException("The fixture manifest entry must be a string.");
            Assert.ThrowsExactly<InvalidDataException>(
                () => RepositoryPaths.ResolveFixtureManifest(
                    contractManifestPath,
                    unsafeFixtureManifest));
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("FixtureAdapter")]
    [TestCategory("Realtime")]
    public void RealtimeProtocolRegistryExactlyMatchesCanonicalSchema()
    {
        AssertFixtureCategoryIsReadable("realtime");

        using JsonDocument schema = LoadSchema("translation-events.schema.json");
        string[] schemaTypes = schema.RootElement
            .GetProperty("oneOf")
            .EnumerateArray()
            .Select(static branch => branch
                .GetProperty("properties")
                .GetProperty("type")
                .GetProperty("const")
                .GetString()
                ?? throw new InvalidDataException(
                    "Translation event type const values must be strings."))
            .ToArray();

        Assert.HasCount(
            schemaTypes.Length,
            schemaTypes.Distinct(StringComparer.Ordinal));
        CollectionAssert.AreEquivalent(
            schemaTypes,
            TranslationEventCodec.EventTypes.ToArray());
    }

    [TestMethod]
    [TestCategory("FixtureAdapter")]
    [TestCategory("Realtime")]
    public async Task RealtimeHandshakeFixtureUsesOwnedSessionAdapter()
    {
        using JsonDocument fixture = LoadFixture("Realtime/text-frame-handshake.json");
        await RealtimeFixtureAdapter.ValidateHandshakeAsync(fixture.RootElement)
            .ConfigureAwait(false);
    }

    [TestMethod]
    [TestCategory("FixtureAdapter")]
    [TestCategory("Routing")]
    public void RealtimeHandshakeChannelAndRouteExpectationsAwaitTask6Adapter()
    {
        using JsonDocument fixture = LoadFixture("Realtime/text-frame-handshake.json");
        Assert.IsTrue(fixture.RootElement.GetProperty("cases")
            .EnumerateArray()
            .All(static fixtureCase =>
                fixtureCase.GetProperty("expected")
                    .TryGetProperty("inboundRoute", out _)));
        Assert.Inconclusive(
            "Realtime channel and route projection belongs to the EMKE.Routing adapter (Runtime Task 6).");
    }

    [TestMethod]
    [TestCategory("FixtureAdapter")]
    [TestCategory("Realtime")]
    public async Task RealtimeCloseDeadlineFixtureUsesOwnedCoordinatorAdapter()
    {
        using JsonDocument fixture = LoadFixture("Realtime/close-deadline.json");
        await RealtimeFixtureAdapter.ValidateCloseDeadlineAsync(fixture.RootElement)
            .ConfigureAwait(false);
    }

    [TestMethod]
    [TestCategory("FixtureAdapter")]
    [TestCategory("Audio")]
    public async Task AudioPcmBatchingFixtureUsesOwnedBatcherAdapter()
    {
        using JsonDocument fixture = LoadFixture("Audio/pcm-batching.json");
        await RealtimeFixtureAdapter.ValidatePcmBatchingAsync(fixture.RootElement)
            .ConfigureAwait(false);
    }

    [TestMethod]
    [TestCategory("FixtureAdapter")]
    [TestCategory("Routing")]
    public void RoutingFixturesAwaitOwnedAdapter()
    {
        AssertFixtureCategoryIsReadable("routing");
        Assert.Inconclusive(
            "Awaiting EMKE.Routing fixture adapter validation (Runtime Task 6).");
    }

    [TestMethod]
    [TestCategory("FixtureAdapter")]
    [TestCategory("Audio")]
    public void AudioPcmConversionFixtureAwaitsOwnedNativeAdapter()
    {
        using JsonDocument fixture = LoadFixture("Audio/pcm-conversion.json");
        Assert.AreEqual("audio.pcm-conversion.v1", fixture.RootElement.GetProperty("fixtureId").GetString());
        Assert.Inconclusive(
            "Awaiting native-audio PCM conversion fixture adapter validation (Runtime Task 7).");
    }

    [TestMethod]
    [TestCategory("FixtureAdapter")]
    [TestCategory("Settings")]
    public void SettingsFixturesAwaitOwnedCompatibilityAdapter()
    {
        AssertFixtureCategoryIsReadable("settings");
        Assert.Inconclusive(
            "Awaiting Application/Platform settings migration and compatibility adapter validation.");
    }

    internal static FixtureInventory LoadFixtureInventory()
    {
        string contractManifestPath = RepositoryPaths.FindContractManifest();
        using JsonDocument contractManifest = LoadJson(contractManifestPath);
        string fixtureManifestEntry =
            contractManifest.RootElement.GetProperty("fixtureManifest").GetString()
            ?? throw new InvalidDataException("The fixture manifest entry must be a string.");
        string fixtureManifestPath = RepositoryPaths.ResolveFixtureManifest(
            contractManifestPath,
            fixtureManifestEntry);

        using JsonDocument fixtureManifest = LoadJson(fixtureManifestPath);
        string[] entries = fixtureManifest.RootElement.GetProperty("fixtures")
            .EnumerateArray()
            .Select(static entry => entry.GetString()
                ?? throw new InvalidDataException("Fixture entries must be strings."))
            .ToArray();
        IReadOnlyList<string> paths = RepositoryPaths.ResolveFixtureFiles(
            fixtureManifestPath,
            fixtureManifest.RootElement.GetProperty("fixtures"));

        return new FixtureInventory(entries, paths);
    }

    internal static JsonDocument LoadSchema(string fileName)
    {
        string contractManifestPath = RepositoryPaths.FindContractManifest();
        using JsonDocument contractManifest = LoadJson(contractManifestPath);
        IReadOnlyList<string> schemaPaths = RepositoryPaths.ResolveSchemaFiles(
            contractManifestPath,
            contractManifest.RootElement.GetProperty("schemas"));
        string schemaPath = schemaPaths.Single(
            path => string.Equals(Path.GetFileName(path), fileName, StringComparison.Ordinal));

        return LoadJson(schemaPath);
    }

    internal static JsonDocument LoadFixture(string suffix)
    {
        FixtureInventory inventory = LoadFixtureInventory();
        string normalizedSuffix = suffix.Replace('/', Path.DirectorySeparatorChar);
        string fixturePath = inventory.Paths.Single(
            path => path.EndsWith(normalizedSuffix, StringComparison.Ordinal));
        return LoadJson(fixturePath);
    }

    private static void AssertInvalidSchemaEntries(
        string temporaryRoot,
        params string[] entries)
    {
        string contractManifestPath = WriteContractManifest(temporaryRoot, entries);
        using JsonDocument contractManifest = LoadJson(contractManifestPath);

        Assert.ThrowsExactly<InvalidDataException>(
            () => RepositoryPaths.ResolveSchemaFiles(
                contractManifestPath,
                contractManifest.RootElement.GetProperty("schemas")));
    }

    private static void AssertFixtureCategoryIsReadable(string category)
    {
        FixtureInventory inventory = LoadFixtureInventory();
        List<JsonElement> fixtureRoots = [];

        for (int index = 0; index < inventory.Paths.Count; index++)
        {
            using JsonDocument fixture = LoadJson(inventory.Paths[index]);
            JsonElement root = fixture.RootElement;
            if (string.Equals(
                    root.GetProperty("category").GetString(),
                    category,
                    StringComparison.Ordinal))
            {
                fixtureRoots.Add(root.Clone());
            }
        }

        Assert.IsNotEmpty(fixtureRoots);
        Assert.IsTrue(
            fixtureRoots.All(static root => root.ValueKind == JsonValueKind.Object));
    }

    private static void ValidateSchemaIdentities(IReadOnlyList<string> schemaPaths)
    {
        HashSet<string> schemaIds = new(StringComparer.Ordinal);

        foreach (string schemaPath in schemaPaths)
        {
            using JsonDocument schema = LoadJson(schemaPath);
            JsonElement root = schema.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("$id", out JsonElement idElement)
                || idElement.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException(
                    "Shared schemas must declare a string $id.");
            }

            string schemaId = idElement.GetString()!;
            if (string.IsNullOrWhiteSpace(schemaId))
            {
                throw new InvalidDataException(
                    "Shared schema $id values must not be empty.");
            }

            if (!schemaIds.Add(schemaId))
            {
                throw new InvalidDataException(
                    "Shared schema $id values must be unique.");
            }
        }
    }

    private static string CreateTemporaryRoot()
    {
        return Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"emke-contract-{Guid.NewGuid():N}")).FullName;
    }

    private static string ExpectedCategory(string entry)
    {
        string directory = entry.Split('/', 2)[0];
        return directory switch
        {
            "Realtime" => "realtime",
            "Routing" => "routing",
            "Audio" => "audio",
            "Settings" => "settings",
            _ => throw new InvalidDataException("Fixture entries must use a canonical category directory."),
        };
    }

    private static JsonDocument LoadJson(string path)
    {
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static string WriteContractManifest(
        string temporaryRoot,
        IReadOnlyList<string> schemas,
        string fixtureManifest = "../TestVectors/fixture-manifest.json")
    {
        string contractsRoot = Directory.CreateDirectory(
            Path.Combine(temporaryRoot, "Shared", "Contracts")).FullName;
        string contractManifestPath = Path.Combine(contractsRoot, "contract-manifest.json");
        File.WriteAllText(
            contractManifestPath,
            JsonSerializer.Serialize(
                new
                {
                    contractVersion = 1,
                    schemas,
                    fixtureManifest,
                }));
        return contractManifestPath;
    }

    internal sealed record FixtureInventory(
        IReadOnlyList<string> Entries,
        IReadOnlyList<string> Paths);
}

#pragma warning restore CA1515
