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
    public void NextAtomicallyAppliesNewFieldsAndIncrementsVersion()
    {
        AppSnapshot source = CreateSnapshot(version: 7);
        RuntimeError error = new(
            ErrorCategory.Network,
            "network.unavailable",
            new Dictionary<string, string> { ["retry"] = "available" },
            RecoveryAction.Retry);

        AppSnapshot next = source.Next(
            RuntimeState.Degraded,
            ChannelState.Reconnecting,
            ChannelState.Bypassed,
            InboundRoute.OriginalFailOpen,
            OutboundRoute.OriginalBypass,
            -1,
            2,
            "new source",
            "new translation",
            new AudioSelection("New input", "New output"),
            new DriverCompatibility(false, "driver unavailable"),
            new TranslationCompatibilityReport(false, ["model mismatch"]),
            new AudioDiagnostics(false, 12),
            new UpdateAvailability(true, "2.0"),
            error);

        Assert.AreEqual<ulong>(7, source.Version);
        Assert.AreEqual(RuntimeState.Running, source.RuntimeState);
        Assert.AreEqual("hello", source.SourceCaption);
        Assert.AreEqual<ulong>(8, next.Version);
        Assert.AreEqual(RuntimeState.Degraded, next.RuntimeState);
        Assert.AreEqual(ChannelState.Reconnecting, next.InboundChannelState);
        Assert.AreEqual(ChannelState.Bypassed, next.OutboundChannelState);
        Assert.AreEqual(InboundRoute.OriginalFailOpen, next.InboundRoute);
        Assert.AreEqual(OutboundRoute.OriginalBypass, next.OutboundRoute);
        Assert.AreEqual(0, next.InboundLevel);
        Assert.AreEqual(1, next.OutboundLevel);
        Assert.AreEqual("new source", next.SourceCaption);
        Assert.AreEqual("new translation", next.TranslatedCaption);
        Assert.AreSame(error, next.Error);
        Assert.ThrowsExactly<OverflowException>(
            () => CreateSnapshot(version: ulong.MaxValue).Next(
                RuntimeState.Stopped,
                ChannelState.Inactive,
                ChannelState.Inactive,
                InboundRoute.Stopped,
                OutboundRoute.Stopped,
                0,
                0,
                string.Empty,
                string.Empty,
                new AudioSelection(string.Empty, string.Empty),
                new DriverCompatibility(false, string.Empty),
                null,
                new AudioDiagnostics(false, 0),
                new UpdateAvailability(false, string.Empty),
                null));
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
    public void CollectionBackedValuesUseContentEquality()
    {
        TranslationCompatibilityReport left = new(false, ["first", "second"]);
        TranslationCompatibilityReport same = new(false, ["first", "second"]);
        TranslationCompatibilityReport different = new(false, ["second", "first"]);

        Assert.AreEqual(left, same);
        Assert.AreEqual(left.GetHashCode(), same.GetHashCode());
        Assert.AreNotEqual(left, different);
        Assert.AreEqual(CreateSnapshot(), CreateSnapshot());
        Assert.AreEqual(CreateSnapshot().GetHashCode(), CreateSnapshot().GetHashCode());
    }

    [TestMethod]
    public void PublicDomainConstructorsRejectUndefinedEnums()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => CreateSnapshot(runtimeState: (RuntimeState)int.MaxValue));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => CreateSnapshot(inboundChannelState: (ChannelState)int.MaxValue));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => CreateSnapshot(outboundChannelState: (ChannelState)int.MaxValue));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => CreateSnapshot(inboundRoute: (InboundRoute)int.MaxValue));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => CreateSnapshot(outboundRoute: (OutboundRoute)int.MaxValue));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new TranslationSessionConfiguration((LanguageCode)int.MaxValue, LanguageCode.En, "model"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new RuntimeSettings(LanguageCode.Zh, (LanguageCode)int.MaxValue, "model", false, false));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new AudioDeviceDescriptor("id", "label", (AudioDeviceDirection)int.MaxValue, false, true));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new TranslationSessionEvent.SourceCaption("text", (LanguageCode)int.MaxValue, false));
    }

    [TestMethod]
    public void TranslationEventsFormAClosedHierarchyAndAudioDeltaOwnsItsLease()
    {
        TranslationSessionEvent[] events =
        [
            new TranslationSessionEvent.SourceCaption("hello", LanguageCode.En, true),
            new TranslationSessionEvent.TranslatedCaption("你好", LanguageCode.Zh, true),
            new TranslationSessionEvent.Completed(),
        ];
        using FakePcmBufferLease lease = new([0x01, 0x00, 0x02, 0x00]);
        TranslationSessionEvent.AudioDelta audioDelta = new(lease);

        Assert.IsTrue(typeof(TranslationSessionEvent).IsAbstract);
        Assert.IsTrue(events.All(static item => item.GetType().IsSealed));
        Assert.AreEqual(TranslationSessionEventKind.SourceCaption, events[0].Kind);
        Assert.AreEqual("hello", ((TranslationSessionEvent.SourceCaption)events[0]).Text);
        Assert.AreEqual(LanguageCode.En, ((TranslationSessionEvent.SourceCaption)events[0]).DetectedLanguage);
        Assert.IsTrue(((TranslationSessionEvent.SourceCaption)events[0]).IsFinal);
        Assert.AreEqual(TranslationSessionEventKind.TranslatedCaption, events[1].Kind);
        Assert.AreEqual(TranslationSessionEventKind.Completed, events[2].Kind);
        CollectionAssert.AreEqual(new byte[] { 0x01, 0x00, 0x02, 0x00 }, audioDelta.Pcm16.ToArray());
        Assert.AreEqual(TranslationSessionEventKind.AudioDelta, audioDelta.Kind);
        Assert.IsFalse(audioDelta.ToString()!.Contains("01000200", StringComparison.Ordinal));

        audioDelta.Dispose();
        audioDelta.Dispose();

        Assert.AreEqual(1, lease.DisposeCount);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = audioDelta.Pcm16);
    }

    [TestMethod]
    public void TranslationAudioDeltaRejectsNullEmptyAndOddPcmLeases()
    {
        using FakePcmBufferLease empty = new([]);
        using FakePcmBufferLease odd = new([0x01]);

        Assert.ThrowsExactly<ArgumentNullException>(
            () => new TranslationSessionEvent.AudioDelta(null!));
        Assert.ThrowsExactly<ArgumentException>(
            () => new TranslationSessionEvent.AudioDelta(empty));
        Assert.ThrowsExactly<ArgumentException>(
            () => new TranslationSessionEvent.AudioDelta(odd));
    }

    [TestMethod]
    public void PcmBufferLeaseOnlyExposesBorrowedMemoryAndDispose()
    {
        Type lease = typeof(IPcmBufferLease);
        PropertyInfo[] properties = lease.GetProperties();

        Assert.IsTrue(typeof(IDisposable).IsAssignableFrom(lease));
        Assert.HasCount(1, properties);
        Assert.AreEqual("Memory", properties[0].Name);
        Assert.AreEqual(typeof(ReadOnlyMemory<byte>), properties[0].PropertyType);
    }

    [TestMethod]
    public void AudioEnginePcmEventsValidateDirectionRouteFrameCountAndLeaseLifetime()
    {
        using FakePcmBufferLease inboundLease = new([0x01, 0x00, 0x02, 0x00]);
        AudioEngineEvent inbound = AudioEngineEvent.CreatePcm(
            inboundLease,
            AudioDirection.Inbound,
            AudioEngineRoute.OriginalFailOpen,
            AudioEngineStatus.Ok,
            2,
            41);
        using FakePcmBufferLease outboundLease = new([0x03, 0x00]);
        AudioEngineEvent outbound = AudioEngineEvent.CreatePcm(
            outboundLease,
            AudioDirection.Outbound,
            AudioEngineRoute.MutedFailClosed,
            AudioEngineStatus.Ok,
            1,
            42);

        Assert.AreEqual(AudioEngineEventKind.InboundPcm16, inbound.Kind);
        Assert.AreEqual(AudioDirection.Inbound, inbound.Direction);
        Assert.AreEqual(AudioEngineRoute.OriginalFailOpen, inbound.Route);
        Assert.AreEqual<uint>(2, inbound.FrameCount);
        Assert.AreEqual<ulong>(41, inbound.Sequence);
        CollectionAssert.AreEqual(new byte[] { 0x01, 0x00, 0x02, 0x00 }, inbound.Pcm16.ToArray());
        Assert.AreEqual(AudioEngineEventKind.OutboundPcm16, outbound.Kind);

        inbound.Dispose();
        inbound.Dispose();
        outbound.Dispose();

        Assert.AreEqual(1, inboundLease.DisposeCount);
        Assert.AreEqual(1, outboundLease.DisposeCount);
    }

    [TestMethod]
    public void AudioEngineControlEventsCarryMetadataWithoutPcm()
    {
        using AudioEngineEvent control = AudioEngineEvent.CreateControl(
            AudioEngineEventKind.DeviceChanged,
            AudioEngineStatus.DeviceMissing,
            AudioEngineRoute.Stopped,
            93);

        Assert.AreEqual(AudioEngineEventKind.DeviceChanged, control.Kind);
        Assert.AreEqual(AudioEngineStatus.DeviceMissing, control.Status);
        Assert.AreEqual(AudioEngineRoute.Stopped, control.Route);
        Assert.AreEqual<ulong>(93, control.Sequence);
        Assert.IsNull(control.Direction);
        Assert.AreEqual<uint>(0, control.FrameCount);
        Assert.IsTrue(control.Pcm16.IsEmpty);
    }

    [TestMethod]
    public void AudioEngineEventsRejectImpossibleStates()
    {
        using FakePcmBufferLease empty = new([]);
        using FakePcmBufferLease odd = new([0x01]);
        using FakePcmBufferLease oneFrame = new([0x01, 0x00]);

        Assert.ThrowsExactly<ArgumentNullException>(
            () => AudioEngineEvent.CreatePcm(
                null!,
                AudioDirection.Inbound,
                AudioEngineRoute.Translated,
                AudioEngineStatus.Ok,
                1,
                1));
        Assert.ThrowsExactly<ArgumentException>(
            () => AudioEngineEvent.CreatePcm(
                empty,
                AudioDirection.Inbound,
                AudioEngineRoute.Translated,
                AudioEngineStatus.Ok,
                0,
                1));
        Assert.ThrowsExactly<ArgumentException>(
            () => AudioEngineEvent.CreatePcm(
                odd,
                AudioDirection.Inbound,
                AudioEngineRoute.Translated,
                AudioEngineStatus.Ok,
                1,
                1));
        Assert.ThrowsExactly<ArgumentException>(
            () => AudioEngineEvent.CreatePcm(
                oneFrame,
                AudioDirection.Inbound,
                AudioEngineRoute.Translated,
                AudioEngineStatus.Ok,
                2,
                1));
        Assert.ThrowsExactly<ArgumentException>(
            () => AudioEngineEvent.CreatePcm(
                oneFrame,
                AudioDirection.Inbound,
                AudioEngineRoute.MutedFailClosed,
                AudioEngineStatus.Ok,
                1,
                1));
        Assert.ThrowsExactly<ArgumentException>(
            () => AudioEngineEvent.CreatePcm(
                oneFrame,
                AudioDirection.Outbound,
                AudioEngineRoute.OriginalFailOpen,
                AudioEngineStatus.Ok,
                1,
                1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => AudioEngineEvent.CreatePcm(
                oneFrame,
                (AudioDirection)int.MaxValue,
                AudioEngineRoute.Translated,
                AudioEngineStatus.Ok,
                1,
                1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => AudioEngineEvent.CreatePcm(
                oneFrame,
                AudioDirection.Inbound,
                (AudioEngineRoute)int.MaxValue,
                AudioEngineStatus.Ok,
                1,
                1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => AudioEngineEvent.CreatePcm(
                oneFrame,
                AudioDirection.Inbound,
                AudioEngineRoute.Translated,
                (AudioEngineStatus)int.MaxValue,
                1,
                1));
        Assert.ThrowsExactly<ArgumentException>(
            () => AudioEngineEvent.CreateControl(
                AudioEngineEventKind.InboundPcm16,
                AudioEngineStatus.Ok,
                AudioEngineRoute.Translated,
                1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => AudioEngineEvent.CreateControl(
                (AudioEngineEventKind)int.MaxValue,
                AudioEngineStatus.Ok,
                AudioEngineRoute.Stopped,
                1));
    }

    [TestMethod]
    public void AudioEnginePortExposesNativeEventSemanticsWithoutAbiLayouts()
    {
        MethodInfo[] methods = typeof(ITranslationAudioEngine).GetMethods();
        string[] methodNames = methods
            .Select(static method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] expectedNames =
        [
            "EnqueueInboundTranslationAsync",
            "EnqueueOutboundTranslationAsync",
            "PollEventAsync",
            "SetInboundRouteAsync",
            "SetOutboundRouteAsync",
            "StartAsync",
            "StopAsync",
        ];

        CollectionAssert.AreEqual(expectedNames, methodNames);
        MethodInfo poll = methods.Single(static method => method.Name == "PollEventAsync");
        Assert.AreEqual(typeof(ValueTask<AudioEngineEvent>), poll.ReturnType);
        Assert.IsTrue(Enum.GetNames<AudioEngineEventKind>().Contains("None", StringComparer.Ordinal));
        Assert.IsFalse(methodNames.Contains("PollInboundPcmAsync", StringComparer.Ordinal));
        Assert.IsFalse(methodNames.Contains("WriteOutboundPcmAsync", StringComparer.Ordinal));
    }

    [TestMethod]
    public void LanguageProbabilitiesValidateNormalizeAndSupportTypedLookup()
    {
        LanguageProbabilities probabilities = new(0.6, 0.3, 0.1);
        LanguageProbabilities withinTolerance = new(0.2, 0.3, 0.5000000005);

        Assert.AreEqual(0.6, probabilities.Zh);
        Assert.AreEqual(0.3, probabilities.En);
        Assert.AreEqual(0.1, probabilities.De);
        Assert.AreEqual(0.6, probabilities[LanguageCode.Zh]);
        Assert.AreEqual(0.3, probabilities[LanguageCode.En]);
        Assert.AreEqual(0.1, probabilities[LanguageCode.De]);
        Assert.AreEqual(0.5000000005, withinTolerance.De);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => _ = probabilities[(LanguageCode)int.MaxValue]);
    }

    [TestMethod]
    [DataRow(double.NaN, 0.5, 0.5)]
    [DataRow(double.PositiveInfinity, 0, 0)]
    [DataRow(-0.1, 0.5, 0.6)]
    [DataRow(1.1, 0, 0)]
    [DataRow(0.2, 0.2, 0.2)]
    [DataRow(0.2, 0.3, 0.500000002)]
    public void LanguageProbabilitiesRejectInvalidComponentsOrTotals(double zh, double en, double de)
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new LanguageProbabilities(zh, en, de));
    }

    [TestMethod]
    public void LanguageClassifierReturnsProbabilities()
    {
        MethodInfo classify = typeof(ILanguageClassifier).GetMethod("ClassifyAsync")!;

        Assert.AreEqual(typeof(ValueTask<LanguageProbabilities>), classify.ReturnType);
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

    private sealed class FakePcmBufferLease : IPcmBufferLease
    {
        private readonly byte[] _bytes;

        public FakePcmBufferLease(byte[] bytes)
        {
            _bytes = bytes;
        }

        public int DisposeCount { get; private set; }

        public ReadOnlyMemory<byte> Memory => _bytes;

        public void Dispose()
        {
            DisposeCount++;
        }
    }
}

#pragma warning restore CA1515
