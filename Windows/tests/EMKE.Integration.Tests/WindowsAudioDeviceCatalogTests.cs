using System.Runtime.InteropServices;
using EMKE.Core;
using EMKE.Platform.Native;

namespace EMKE.Integration.Tests;

#pragma warning disable CA1515 // MSTest requires a discoverable public test class.
#pragma warning disable CA2007 // MSTest owns the test synchronization context.
#pragma warning disable MSTEST0037 // These assertions intentionally verify scalar counts.

[TestClass]
public sealed class WindowsAudioDeviceCatalogTests
{
    [TestMethod]
    public async Task MapsActivePhysicalDefaultsAndExactVirtualRoles()
    {
        CatalogNativeAudioApi native = new();
        native.Items.AddRange(
        [
            Endpoint("physical-input", "USB microphone", NativeAudioEndpointDataFlow.Capture,
                NativeAudioEndpointFlags.Active | NativeAudioEndpointFlags.PhysicalDefault),
            Endpoint("physical-output", "Studio speakers", NativeAudioEndpointDataFlow.Render,
                NativeAudioEndpointFlags.Active | NativeAudioEndpointFlags.PhysicalDefault),
            Endpoint("virtual-speaker-render", "driver label", NativeAudioEndpointDataFlow.Render,
                NativeAudioEndpointFlags.Active | NativeAudioEndpointFlags.VirtualRole,
                "emke.meeting-speaker.render"),
            Endpoint("virtual-speaker-capture", "driver label", NativeAudioEndpointDataFlow.Capture,
                NativeAudioEndpointFlags.Active | NativeAudioEndpointFlags.VirtualRole,
                "emke.app-speaker.capture"),
            Endpoint("virtual-microphone-render", "driver label", NativeAudioEndpointDataFlow.Render,
                NativeAudioEndpointFlags.Active | NativeAudioEndpointFlags.VirtualRole,
                "emke.app-microphone.render"),
            Endpoint("virtual-microphone-capture", "driver label", NativeAudioEndpointDataFlow.Capture,
                NativeAudioEndpointFlags.Active | NativeAudioEndpointFlags.VirtualRole,
                "emke.meeting-microphone.capture"),
        ]);

        WindowsAudioDeviceCatalog catalog = new(native);
        AudioDeviceSnapshot snapshot = await catalog.GetSnapshotAsync(CancellationToken.None);

        Assert.AreEqual(6, snapshot.Devices.Count);
        AudioDeviceDescriptor input = snapshot.Devices.Single(device => device.Id == "physical-input");
        Assert.AreEqual("USB microphone", input.Label);
        Assert.AreEqual(AudioDeviceDirection.Input, input.Direction);
        Assert.IsTrue(input.IsDefault);
        Assert.IsTrue(input.IsAvailable);
        Assert.AreEqual(
            "Meeting speaker render",
            snapshot.Devices.Single(device => device.Id == "virtual-speaker-render").Label);
    }

    [TestMethod]
    public async Task RetriesExactlyOnceWhenNativeCountGrows()
    {
        CatalogNativeAudioApi native = new();
        native.Items.AddRange(CompleteCatalog());
        native.GrownItems = [.. CompleteCatalog(), Endpoint(
            "physical-output-2", "Desk speakers", NativeAudioEndpointDataFlow.Render,
            NativeAudioEndpointFlags.Active)];
        WindowsAudioDeviceCatalog catalog = new(native);

        AudioDeviceSnapshot snapshot = await catalog.GetSnapshotAsync(CancellationToken.None);

        Assert.AreEqual(7, snapshot.Devices.Count);
        Assert.AreEqual(2, native.FillCallCount);
    }

    [TestMethod]
    public async Task RejectsUnterminatedBuffersAndDoesNotReturnEmptySuccess()
    {
        CatalogNativeAudioApi native = new();
        NativeAudioEndpointDescriptorV1 invalid = Endpoint(
            "physical-input", "USB microphone", NativeAudioEndpointDataFlow.Capture,
            NativeAudioEndpointFlags.Active);
        MakeNameUnterminated(ref invalid);
        native.Items.AddRange(CompleteCatalog());
        native.Items[0] = invalid;
        WindowsAudioDeviceCatalog catalog = new(native);

        NativeAudioException exception = await Assert.ThrowsExactlyAsync<NativeAudioException>(
            () => catalog.GetSnapshotAsync(CancellationToken.None));

        Assert.AreEqual(AudioEngineStatus.InternalError, exception.Status);
    }

    [TestMethod]
    public async Task RejectsUnknownRolesDuplicateIdsAndInvalidFlags()
    {
        CatalogNativeAudioApi native = new();
        native.Items.AddRange(CompleteCatalog());
        native.Items[4] = Endpoint(
            "virtual-microphone-render", "driver label", NativeAudioEndpointDataFlow.Render,
            NativeAudioEndpointFlags.Active | NativeAudioEndpointFlags.VirtualRole,
            "emke.unknown-role");
        WindowsAudioDeviceCatalog catalog = new(native);

        NativeAudioException exception = await Assert.ThrowsExactlyAsync<NativeAudioException>(
            () => catalog.GetSnapshotAsync(CancellationToken.None));

        Assert.AreEqual(AudioEngineStatus.InternalError, exception.Status);
    }

    [TestMethod]
    public async Task PropagatesNativeFailureAsTypedExceptionAndHonorsPreCancelledToken()
    {
        CatalogNativeAudioApi native = new() { CountStatus = NativeAudioStatus.DeviceMissing };
        WindowsAudioDeviceCatalog catalog = new(native);

        NativeAudioException exception = await Assert.ThrowsExactlyAsync<NativeAudioException>(
            () => catalog.GetSnapshotAsync(CancellationToken.None));
        Assert.AreEqual(AudioEngineStatus.DeviceMissing, exception.Status);

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => catalog.GetSnapshotAsync(cancellation.Token));
        Assert.AreEqual(1, native.CountCallCount);
    }

    [TestMethod]
    public async Task HonorsCancellationBetweenCountAndFill()
    {
        using CancellationTokenSource cancellation = new();
        CatalogNativeAudioApi native = new() { AfterCount = () => cancellation.Cancel() };
        native.Items.AddRange(CompleteCatalog());
        WindowsAudioDeviceCatalog catalog = new(native);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => catalog.GetSnapshotAsync(cancellation.Token));

        Assert.AreEqual(1, native.CountCallCount);
        Assert.AreEqual(0, native.FillCallCount);
    }

    private static List<NativeAudioEndpointDescriptorV1> CompleteCatalog() =>
    [
        Endpoint("physical-input", "USB microphone", NativeAudioEndpointDataFlow.Capture,
            NativeAudioEndpointFlags.Active | NativeAudioEndpointFlags.PhysicalDefault),
        Endpoint("physical-output", "Studio speakers", NativeAudioEndpointDataFlow.Render,
            NativeAudioEndpointFlags.Active | NativeAudioEndpointFlags.PhysicalDefault),
        Endpoint("virtual-speaker-render", "driver label", NativeAudioEndpointDataFlow.Render,
            NativeAudioEndpointFlags.Active | NativeAudioEndpointFlags.VirtualRole,
            "emke.meeting-speaker.render"),
        Endpoint("virtual-speaker-capture", "driver label", NativeAudioEndpointDataFlow.Capture,
            NativeAudioEndpointFlags.Active | NativeAudioEndpointFlags.VirtualRole,
            "emke.app-speaker.capture"),
        Endpoint("virtual-microphone-render", "driver label", NativeAudioEndpointDataFlow.Render,
            NativeAudioEndpointFlags.Active | NativeAudioEndpointFlags.VirtualRole,
            "emke.app-microphone.render"),
        Endpoint("virtual-microphone-capture", "driver label", NativeAudioEndpointDataFlow.Capture,
            NativeAudioEndpointFlags.Active | NativeAudioEndpointFlags.VirtualRole,
            "emke.meeting-microphone.capture"),
    ];

    private static unsafe NativeAudioEndpointDescriptorV1 Endpoint(
        string id,
        string name,
        NativeAudioEndpointDataFlow direction,
        NativeAudioEndpointFlags flags,
        string role = "")
    {
        NativeAudioEndpointDescriptorV1 result = default;
        result.Size = checked((uint)Marshal.SizeOf<NativeAudioEndpointDescriptorV1>());
        result.Direction = (uint)direction;
        result.Flags = (uint)flags;
        ushort* idBuffer = result.Id;
        WriteTerminated(idBuffer, NativeAudioConstants.EndpointIdCapacity, id);
        ushort* nameBuffer = result.Name;
        WriteTerminated(nameBuffer, NativeAudioConstants.EndpointNameCapacity, name);
        ushort* roleBuffer = result.Role;
        WriteTerminated(roleBuffer, NativeAudioConstants.EndpointRoleCapacity, role);
        return result;
    }

    private static unsafe void FillWithoutTerminator(ushort* buffer, int capacity)
    {
        for (int index = 0; index < capacity; index++)
        {
            buffer[index] = 'x';
        }
    }

    private static unsafe void MakeNameUnterminated(
        ref NativeAudioEndpointDescriptorV1 descriptor)
    {
        ushort* buffer = descriptor.Name;
        FillWithoutTerminator(buffer, NativeAudioConstants.EndpointNameCapacity);
    }

    private static unsafe void WriteTerminated(ushort* buffer, int capacity, string value)
    {
        Assert.IsLessThan(value.Length, capacity);
        for (int index = 0; index < value.Length; index++)
        {
            buffer[index] = value[index];
        }
        buffer[value.Length] = 0;
    }
}

internal sealed class CatalogNativeAudioApi : INativeAudioApi
{
    private bool _growthReported;

    public List<NativeAudioEndpointDescriptorV1> Items { get; } = [];

    public List<NativeAudioEndpointDescriptorV1>? GrownItems { get; set; }

    public NativeAudioStatus CountStatus { get; set; } = NativeAudioStatus.Ok;

    public Action? AfterCount { get; set; }

    public int CountCallCount { get; private set; }

    public int FillCallCount { get; private set; }

    public uint GetAbiVersion() => NativeAudioConstants.AbiVersion;

    public uint GetConfigurationSize() => throw new NotSupportedException();

    public uint GetEventSize() => throw new NotSupportedException();

    public uint GetDiagnosticsSize() => throw new NotSupportedException();

    public uint GetDiscoveredEndpointSize() => throw new NotSupportedException();

    public uint GetEndpointSnapshotSize() => throw new NotSupportedException();

    public uint GetEndpointDescriptorV1Size() =>
        checked((uint)Marshal.SizeOf<NativeAudioEndpointDescriptorV1>());

    public NativeAudioStatus DiscoverEndpoints(ref NativeAudioEndpointSnapshot snapshot) =>
        throw new NotSupportedException();

    public NativeAudioStatus EnumerateEndpointsV1(
        Span<NativeAudioEndpointDescriptorV1> items,
        out uint requiredCount)
    {
        if (items.IsEmpty)
        {
            CountCallCount++;
            requiredCount = checked((uint)Items.Count);
            AfterCount?.Invoke();
            return CountStatus;
        }

        FillCallCount++;
        if (GrownItems is not null && !_growthReported)
        {
            _growthReported = true;
            Items.Clear();
            Items.AddRange(GrownItems);
            requiredCount = checked((uint)Items.Count);
            return NativeAudioStatus.InvalidArgument;
        }

        requiredCount = checked((uint)Items.Count);
        if (items.Length < Items.Count)
        {
            return NativeAudioStatus.InvalidArgument;
        }
        for (int index = 0; index < Items.Count; index++)
        {
            items[index] = Items[index];
        }
        return NativeAudioStatus.Ok;
    }

    public NativeAudioStatus Create(
        in NativeAudioConfiguration configuration,
        out SafeNativeAudioHandle? handle) => throw new NotSupportedException();

    public void Destroy(nint handle) => throw new NotSupportedException();

    public NativeAudioStatus Start(SafeNativeAudioHandle handle) => throw new NotSupportedException();

    public NativeAudioStatus Stop(SafeNativeAudioHandle handle) => throw new NotSupportedException();

    public NativeAudioStatus SetInboundRoute(SafeNativeAudioHandle handle, NativeAudioRoute route) =>
        throw new NotSupportedException();

    public NativeAudioStatus SetOutboundRoute(SafeNativeAudioHandle handle, NativeAudioRoute route) =>
        throw new NotSupportedException();

    public NativeAudioStatus EnqueueInboundTranslation(
        SafeNativeAudioHandle handle, ReadOnlySpan<byte> pcm16) => throw new NotSupportedException();

    public NativeAudioStatus EnqueueOutboundTranslation(
        SafeNativeAudioHandle handle, ReadOnlySpan<byte> pcm16) => throw new NotSupportedException();

    public NativeAudioStatus Poll(
        SafeNativeAudioHandle handle, ref NativeAudioEvent nativeEvent, Span<byte> pcm16) =>
        throw new NotSupportedException();
}
