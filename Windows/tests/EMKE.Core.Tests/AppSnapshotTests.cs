using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EMKE.Core.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.

[TestClass]
public sealed class AppSnapshotTests
{
    [TestMethod]
    public void StableEnumsMatchSharedSchemaAndRoundTripExactly()
    {
        using JsonDocument schema = LoadAppStateSchema();

        AssertEnumMatchesSchema<RuntimeState>(schema, "runtimeState");
        AssertEnumMatchesSchema<ChannelState>(schema, "channelState");
        AssertEnumMatchesSchema<InboundRoute>(schema, "inboundRoute");
        AssertEnumMatchesSchema<OutboundRoute>(schema, "outboundRoute");

        AssertEnumRoundTrips(
            new Dictionary<LanguageCode, string>
            {
                [LanguageCode.Zh] = "zh",
                [LanguageCode.En] = "en",
                [LanguageCode.De] = "de",
            });
    }

    [TestMethod]
    public void StableEnumsRejectUnknownStringsAndUndefinedValues()
    {
        AssertEnumRejectsUnknownValues<RuntimeState>();
        AssertEnumRejectsUnknownValues<ChannelState>();
        AssertEnumRejectsUnknownValues<InboundRoute>();
        AssertEnumRejectsUnknownValues<OutboundRoute>();
        AssertEnumRejectsUnknownValues<LanguageCode>();
    }

    [TestMethod]
    public void StoppedSnapshotProjectsOnlyTheSharedSchemaFields()
    {
        AppSnapshot snapshot = CreateSnapshot(
            contractVersion: 1,
            version: 0,
            runtimeState: RuntimeState.Stopped,
            inboundChannelState: ChannelState.Inactive,
            outboundChannelState: ChannelState.Inactive,
            inboundRoute: InboundRoute.Stopped,
            outboundRoute: OutboundRoute.Stopped,
            inboundLevel: 0,
            outboundLevel: 0,
            sourceCaption: string.Empty,
            translatedCaption: string.Empty);

        JsonNode? actual = JsonNode.Parse(JsonSerializer.Serialize(snapshot));
        JsonNode? expected = JsonNode.Parse(
            """
            {
              "contractVersion": 1,
              "version": 0,
              "runtimeState": "stopped",
              "inboundChannelState": "inactive",
              "outboundChannelState": "inactive",
              "inboundRoute": "stopped",
              "outboundRoute": "stopped",
              "inboundLevel": 0,
              "outboundLevel": 0,
              "sourceCaption": "",
              "translatedCaption": ""
            }
            """);

        Assert.IsTrue(JsonNode.DeepEquals(expected, actual));

        using JsonDocument schema = LoadAppStateSchema();
        JsonElement root = schema.RootElement;
        string[] required = root.GetProperty("required")
            .EnumerateArray()
            .Select(static item => item.GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] actualKeys = actual!.AsObject()
            .Select(static property => property.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(required, actualKeys);
        Assert.IsFalse(root.GetProperty("additionalProperties").GetBoolean());
        Assert.AreEqual(1, root.GetProperty("properties").GetProperty("contractVersion").GetProperty("const").GetInt32());
    }

    [TestMethod]
    public void ConstructorClampsFiniteLevelsAtTheBoundary()
    {
        AppSnapshot snapshot = CreateSnapshot(inboundLevel: -0.25, outboundLevel: 1.25);

        Assert.AreEqual(0, snapshot.InboundLevel);
        Assert.AreEqual(1, snapshot.OutboundLevel);
    }

    [TestMethod]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    public void ConstructorRejectsNonFiniteLevels(double invalidLevel)
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => CreateSnapshot(inboundLevel: invalidLevel));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => CreateSnapshot(outboundLevel: invalidLevel));
    }

    [TestMethod]
    public void ConstructorRejectsUnsupportedContractVersionsAndNullCaptions()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreateSnapshot(contractVersion: 2));
        Assert.ThrowsExactly<ArgumentNullException>(() => CreateSnapshot(sourceCaption: null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => CreateSnapshot(translatedCaption: null!));
    }

    [TestMethod]
    public void WithNextVersionIncrementsInCheckedContextWithoutMutatingTheSource()
    {
        AppSnapshot source = CreateSnapshot(version: 41);

        AppSnapshot next = source.WithNextVersion();

        Assert.AreEqual<ulong>(41, source.Version);
        Assert.AreEqual<ulong>(42, next.Version);
        Assert.AreEqual(source.SourceCaption, next.SourceCaption);
        Assert.AreNotSame(source, next);
        Assert.ThrowsExactly<OverflowException>(
            () => CreateSnapshot(version: ulong.MaxValue).WithNextVersion());
    }

    [TestMethod]
    public void VersionRetainsTheFullUnsignedRangeInJson()
    {
        using JsonDocument json = JsonDocument.Parse(
            JsonSerializer.Serialize(CreateSnapshot(version: ulong.MaxValue)));

        Assert.AreEqual(ulong.MaxValue, json.RootElement.GetProperty("version").GetUInt64());
    }

    [TestMethod]
    public void SnapshotAndOperationalValuesAreImmutableAndDefensivelyCopyCollections()
    {
        Assert.IsTrue(
            typeof(AppSnapshot).GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .All(static property => property.SetMethod is null));

        List<string> findings = ["microphone unavailable"];
        TranslationCompatibilityReport report = new(false, findings);
        findings[0] = "mutated";
        Assert.AreEqual("microphone unavailable", report.Findings[0]);
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<string>)report.Findings)[0] = "mutated again");

        List<AudioDeviceDescriptor> devices =
        [
            new("endpoint-1", "System microphone", AudioDeviceDirection.Input, true, true),
        ];
        AudioDeviceSnapshot deviceSnapshot = new(devices);
        devices.Clear();
        Assert.HasCount(1, deviceSnapshot.Devices);
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<AudioDeviceDescriptor>)deviceSnapshot.Devices).Clear());
    }

    [TestMethod]
    public void RuntimeCommandsFormAClosedImmutableHierarchy()
    {
        RuntimeCommand[] commands =
        [
            new RuntimeCommand.Start(),
            new RuntimeCommand.Stop(),
            new RuntimeCommand.Exit(),
            new RuntimeCommand.SetInboundBypass(true),
            new RuntimeCommand.SetOutboundBypass(false),
            new RuntimeCommand.RefreshDevices(),
            new RuntimeCommand.CheckForUpdates(),
        ];

        Assert.IsTrue(typeof(RuntimeCommand).IsAbstract);
        Assert.IsTrue(commands.All(static command => command.GetType().IsSealed));
        Assert.IsTrue(((RuntimeCommand.SetInboundBypass)commands[3]).Enabled);
        Assert.IsFalse(((RuntimeCommand.SetOutboundBypass)commands[4]).Enabled);
    }

    [TestMethod]
    public void EveryAsynchronousPortMethodAcceptsCancellationToken()
    {
        Type[] ports =
        [
            typeof(ITranslationSession),
            typeof(ITranslationSessionFactory),
            typeof(ITranslationAudioEngine),
            typeof(IAudioDeviceCatalog),
            typeof(IAudioDiagnostics),
            typeof(ILanguageClassifier),
            typeof(ISecretStore),
            typeof(ISettingsStore),
            typeof(IOnboardingProgressStore),
            typeof(IDriverManager),
            typeof(IUpdateService),
            typeof(IClock),
            typeof(IRuntimeLog),
        ];

        MethodInfo[] asynchronousMethods = ports
            .SelectMany(static port => port.GetMethods())
            .Where(static method => IsAsynchronous(method.ReturnType))
            .ToArray();

        Assert.IsNotEmpty(asynchronousMethods);
        foreach (MethodInfo method in asynchronousMethods)
        {
            Assert.IsTrue(
                method.GetParameters().Any(static parameter => parameter.ParameterType == typeof(CancellationToken)),
                $"{method.DeclaringType!.Name}.{method.Name} must accept a CancellationToken.");
        }
    }

    [TestMethod]
    public void SecretBufferIsDisposableAndExposesOnlyReadOnlyMemory()
    {
        Type secretBuffer = typeof(ISecretBuffer);

        Assert.IsTrue(typeof(IDisposable).IsAssignableFrom(secretBuffer));
        PropertyInfo[] properties = secretBuffer.GetProperties();
        Assert.HasCount(1, properties);
        PropertyInfo property = properties[0];
        Assert.AreEqual(typeof(ReadOnlyMemory<char>), property.PropertyType);
        Assert.AreEqual("Memory", property.Name);
        Assert.IsNull(secretBuffer.GetMethod(nameof(ToString), BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
    }

    private static AppSnapshot CreateSnapshot(
        int contractVersion = 1,
        ulong version = 1,
        RuntimeState runtimeState = RuntimeState.Running,
        ChannelState inboundChannelState = ChannelState.Connected,
        ChannelState outboundChannelState = ChannelState.Connected,
        InboundRoute inboundRoute = InboundRoute.Translated,
        OutboundRoute outboundRoute = OutboundRoute.Translated,
        double inboundLevel = 0.25,
        double outboundLevel = 0.5,
        string sourceCaption = "hello",
        string translatedCaption = "你好")
    {
        return new AppSnapshot(
            contractVersion,
            version,
            runtimeState,
            inboundChannelState,
            outboundChannelState,
            inboundRoute,
            outboundRoute,
            inboundLevel,
            outboundLevel,
            sourceCaption,
            translatedCaption,
            new AudioSelection("System microphone", "EMKE virtual speaker"),
            new DriverCompatibility(true, "compatible"),
            new TranslationCompatibilityReport(true, Array.Empty<string>()),
            new AudioDiagnostics(true, 0),
            new UpdateAvailability(false, string.Empty),
            null);
    }

    private static bool IsAsynchronous(Type returnType)
    {
        if (returnType == typeof(Task) || returnType == typeof(ValueTask))
        {
            return true;
        }

        if (!returnType.IsGenericType)
        {
            return false;
        }

        Type definition = returnType.GetGenericTypeDefinition();
        return definition == typeof(Task<>)
            || definition == typeof(ValueTask<>)
            || definition == typeof(IAsyncEnumerable<>);
    }

    private static void AssertEnumMatchesSchema<TEnum>(JsonDocument schema, string definitionName)
        where TEnum : struct, Enum
    {
        string[] schemaValues = schema.RootElement
            .GetProperty("$defs")
            .GetProperty(definitionName)
            .GetProperty("enum")
            .EnumerateArray()
            .Select(static value => value.GetString()!)
            .ToArray();
        TEnum[] enumValues = Enum.GetValues<TEnum>();

        Assert.HasCount(schemaValues.Length, enumValues);
        foreach (string stableValue in schemaValues)
        {
            TEnum parsed = JsonSerializer.Deserialize<TEnum>($"\"{stableValue}\"");
            Assert.AreEqual(stableValue, JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(parsed)));
        }

        string[] serializedValues = enumValues
            .Select(static value => JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(value))!)
            .ToArray();
        CollectionAssert.AreEquivalent(schemaValues, serializedValues);
    }

    private static void AssertEnumRoundTrips<TEnum>(IReadOnlyDictionary<TEnum, string> expected)
        where TEnum : struct, Enum
    {
        Assert.HasCount(Enum.GetValues<TEnum>().Length, expected);
        foreach ((TEnum value, string stableValue) in expected)
        {
            Assert.AreEqual($"\"{stableValue}\"", JsonSerializer.Serialize(value));
            Assert.AreEqual(value, JsonSerializer.Deserialize<TEnum>($"\"{stableValue}\""));
        }
    }

    private static void AssertEnumRejectsUnknownValues<TEnum>()
        where TEnum : struct, Enum
    {
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<TEnum>("\"unknown\""));
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<TEnum>(int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Serialize((TEnum)Enum.ToObject(typeof(TEnum), int.MaxValue)));
    }

    private static JsonDocument LoadAppStateSchema()
    {
        string? directory = Directory.GetCurrentDirectory();
        while (directory is not null)
        {
            string candidate = Path.Combine(directory, "Shared", "Contracts", "v1", "app-state.schema.json");
            if (File.Exists(candidate))
            {
                return JsonDocument.Parse(File.ReadAllText(candidate));
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        Assert.Fail("Could not locate Shared/Contracts/v1/app-state.schema.json from the test working directory.");
        throw new InvalidOperationException("Assert.Fail should have thrown.");
    }
}

#pragma warning restore CA1515
