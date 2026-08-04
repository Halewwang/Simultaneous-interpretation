using EMKE.Application;
using EMKE.Core;
using EMKE.Platform.Driver;

namespace EMKE.Setup.Platform;

internal sealed record EndpointVerificationResult
{
    private EndpointVerificationResult(
        bool ready,
        string? failureCode,
        IReadOnlyList<string> activeRoles)
    {
        Ready = ready;
        FailureCode = failureCode;
        ActiveRoles = activeRoles;
    }

    public bool Ready { get; }

    public bool LaunchAllowed => Ready;

    public string? FailureCode { get; }

    public IReadOnlyList<string> ActiveRoles { get; }

    public static EndpointVerificationResult Succeeded(
        IEnumerable<string> activeRoles)
    {
        ArgumentNullException.ThrowIfNull(activeRoles);
        return new EndpointVerificationResult(
            true,
            null,
            Array.AsReadOnly(activeRoles.ToArray()));
    }

    public static EndpointVerificationResult Rejected(string failureCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        return new EndpointVerificationResult(false, failureCode, []);
    }
}

internal interface IEndpointReadinessVerifier
{
    Task<EndpointVerificationResult> VerifyAsync(
        CancellationToken cancellationToken);
}

internal sealed class EndpointVerifier : IEndpointReadinessVerifier
{
    private readonly IWindowsDriverSnapshotSource _snapshotSource;
    private readonly CompatibilityManifest _manifest;
    private readonly IWindowsHostCompatibilitySource _hostSource;
    private readonly IDriverCatalogTrustPolicy _trustPolicy;

    public EndpointVerifier()
        : this(
            new WindowsDriverSnapshotSource(),
            CompatibilityManifest.LoadEmbedded(
                typeof(EndpointVerifier).Assembly,
                "EMKE.Setup.compatibility.internal.json"),
            new SystemSetupWindowsBuildSource(),
            MicrosoftDriverCatalogTrustPolicy.Instance)
    {
    }

    internal EndpointVerifier(
        IWindowsDriverSnapshotSource snapshotSource,
        CompatibilityManifest manifest,
        IWindowsHostCompatibilitySource hostSource,
        IDriverCatalogTrustPolicy trustPolicy)
    {
        _snapshotSource = snapshotSource
            ?? throw new ArgumentNullException(nameof(snapshotSource));
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _hostSource = hostSource
            ?? throw new ArgumentNullException(nameof(hostSource));
        _trustPolicy = trustPolicy
            ?? throw new ArgumentNullException(nameof(trustPolicy));
    }

#pragma warning disable CA1031 // Incomplete OS evidence must fail closed.
    public async Task<EndpointVerificationResult> VerifyAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            WindowsInstalledDriverSnapshot snapshot =
                await _snapshotSource.ReadAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (!snapshot.Present
                || !snapshot.CatalogChainValid
                || string.IsNullOrWhiteSpace(snapshot.CatalogSigner))
            {
                return EndpointVerificationResult.Rejected(
                    snapshot.Present
                        ? "driverSignatureInvalid"
                        : "driverMissing");
            }

            DriverCatalogTrustDecision trust = _trustPolicy.Evaluate(
                snapshot.CatalogSigner,
                kernelPolicyValid: snapshot.CatalogChainValid,
                catalogMembersValid: snapshot.CatalogChainValid);
            if (!trust.Allowed)
            {
                return EndpointVerificationResult.Rejected(
                    "driverCatalogSignerRejected");
            }

            CompatibilityGateDecision decision = CompatibilityGate.Evaluate(
                _manifest,
                _hostSource.GetCurrentWindowsBuild(),
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
            return decision.Allowed
                ? EndpointVerificationResult.Succeeded(
                    snapshot.EndpointStates.Select(static endpoint => endpoint.Role))
                : EndpointVerificationResult.Rejected(decision.Reason);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return EndpointVerificationResult.Rejected(
                "endpointEvidenceUnavailable");
        }
    }
#pragma warning restore CA1031

    private sealed class SystemSetupWindowsBuildSource
        : IWindowsHostCompatibilitySource
    {
        public int GetCurrentWindowsBuild() => OperatingSystem.IsWindows()
            ? Environment.OSVersion.Version.Build
            : 0;
    }
}
