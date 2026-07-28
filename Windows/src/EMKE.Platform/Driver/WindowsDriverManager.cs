using EMKE.Application;
using EMKE.Core;

namespace EMKE.Platform.Driver;

public sealed record WindowsInstalledDriverEndpointState
{
    public WindowsInstalledDriverEndpointState(string role, string state)
    {
        Role = string.IsNullOrWhiteSpace(role)
            ? throw new ArgumentException(
                "The endpoint role is required.",
                nameof(role))
            : role;
        State = string.IsNullOrWhiteSpace(state)
            ? throw new ArgumentException(
                "The endpoint state is required.",
                nameof(state))
            : state;
    }

    public string Role { get; }

    public string State { get; }
}

public sealed record WindowsInstalledDriverSnapshot
{
    public WindowsInstalledDriverSnapshot(
        bool present,
        string? rootDevnodeHardwareId,
        Version driverFileVersion,
        int driverAbiProperty,
        string? catalogSigner,
        bool catalogChainValid,
        IEnumerable<WindowsInstalledDriverEndpointState> endpointStates)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(driverAbiProperty);
        ArgumentNullException.ThrowIfNull(endpointStates);

        WindowsInstalledDriverEndpointState[] endpoints = endpointStates.ToArray();
        if (endpoints.Any(static endpoint => endpoint is null))
        {
            throw new ArgumentException(
                "Endpoint states cannot contain null values.",
                nameof(endpointStates));
        }

        Present = present;
        RootDevnodeHardwareId = rootDevnodeHardwareId;
        DriverFileVersion = driverFileVersion
            ?? throw new ArgumentNullException(nameof(driverFileVersion));
        DriverAbiProperty = driverAbiProperty;
        CatalogSigner = catalogSigner;
        CatalogChainValid = catalogChainValid;
        EndpointStates = Array.AsReadOnly(endpoints);
    }

    public bool Present { get; }

    public string? RootDevnodeHardwareId { get; }

    public Version DriverFileVersion { get; }

    public int DriverAbiProperty { get; }

    public string? CatalogSigner { get; }

    public bool CatalogChainValid { get; }

    public IReadOnlyList<WindowsInstalledDriverEndpointState> EndpointStates { get; }
}

public interface IWindowsDriverSnapshotSource
{
    ValueTask<WindowsInstalledDriverSnapshot> ReadAsync(
        CancellationToken cancellationToken);
}

public interface IWindowsHostCompatibilitySource
{
    int GetCurrentWindowsBuild();
}

internal sealed class SystemWindowsHostCompatibilitySource
    : IWindowsHostCompatibilitySource
{
    public static SystemWindowsHostCompatibilitySource Instance { get; } = new();

    private SystemWindowsHostCompatibilitySource()
    {
    }

    public int GetCurrentWindowsBuild()
    {
        return OperatingSystem.IsWindows()
            ? Environment.OSVersion.Version.Build
            : 0;
    }
}

internal sealed record WindowsDriverCompatibilityObservation(
    bool Allowed,
    string Reason,
    bool UpdateRecommended,
    bool RepairAvailable);

internal interface IWindowsDriverCompatibilityDiagnostics
{
    void Record(WindowsDriverCompatibilityObservation observation);
}

internal sealed class NullWindowsDriverCompatibilityDiagnostics
    : IWindowsDriverCompatibilityDiagnostics
{
    public static NullWindowsDriverCompatibilityDiagnostics Instance { get; } = new();

    private NullWindowsDriverCompatibilityDiagnostics()
    {
    }

    public void Record(WindowsDriverCompatibilityObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
    }
}

public sealed class WindowsDriverManager : IDriverManager
{
    private readonly IWindowsDriverSnapshotSource _snapshotSource;
    private readonly CompatibilityManifest _manifest;
    private readonly IWindowsHostCompatibilitySource _hostSource;
    private readonly IWindowsDriverCompatibilityDiagnostics _diagnostics;

    public WindowsDriverManager(
        IWindowsDriverSnapshotSource snapshotSource,
        CompatibilityManifest manifest)
        : this(
            snapshotSource,
            manifest,
            SystemWindowsHostCompatibilitySource.Instance,
            NullWindowsDriverCompatibilityDiagnostics.Instance)
    {
    }

    public WindowsDriverManager(
        IWindowsDriverSnapshotSource snapshotSource,
        CompatibilityManifest manifest,
        IWindowsHostCompatibilitySource hostSource)
        : this(
            snapshotSource,
            manifest,
            hostSource,
            NullWindowsDriverCompatibilityDiagnostics.Instance)
    {
    }

    internal WindowsDriverManager(
        IWindowsDriverSnapshotSource snapshotSource,
        CompatibilityManifest manifest,
        IWindowsHostCompatibilitySource hostSource,
        IWindowsDriverCompatibilityDiagnostics diagnostics)
    {
        _snapshotSource =
            snapshotSource ?? throw new ArgumentNullException(nameof(snapshotSource));
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _hostSource =
            hostSource ?? throw new ArgumentNullException(nameof(hostSource));
        _diagnostics =
            diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    public async Task<DriverCompatibility> CheckCompatibilityAsync(
        CancellationToken cancellationToken)
    {
        WindowsInstalledDriverSnapshot snapshot =
            await _snapshotSource.ReadAsync(cancellationToken).ConfigureAwait(false);
        int windowsBuild = _hostSource.GetCurrentWindowsBuild();
        CompatibilityGateDecision decision = CompatibilityGate.Evaluate(
            _manifest,
            windowsBuild,
            new InstalledDriverEvidence(
                snapshot.Present,
                snapshot.RootDevnodeHardwareId,
                snapshot.DriverFileVersion,
                snapshot.DriverAbiProperty,
                snapshot.CatalogSigner,
                snapshot.CatalogChainValid,
                snapshot.EndpointStates.Select(static endpoint =>
                    new InstalledDriverEndpointEvidence(
                        endpoint.Role,
                        endpoint.State))));
        _diagnostics.Record(new WindowsDriverCompatibilityObservation(
            decision.Allowed,
            decision.Reason,
            decision.UpdateRecommended,
            decision.RepairAvailable));
        return decision.ToDriverCompatibility();
    }
}
