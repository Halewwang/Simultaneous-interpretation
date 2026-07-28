using EMKE.Core;

namespace EMKE.Application;

public sealed record CompatibilityGateDecision(
    bool Allowed,
    string Reason,
    bool UpdateRecommended,
    bool RepairAvailable)
{
    public bool CanStart => Allowed;

    public DriverCompatibility ToDriverCompatibility()
    {
        return new DriverCompatibility(
            Allowed,
            Reason,
            UpdateRecommended,
            RepairAvailable);
    }
}

public static class CompatibilityGate
{
    private const string RootHardwareId = @"ROOT\EMKEVIRTUALAUDIO";

    private static readonly string[] EndpointRoles =
    [
        "meetingSpeakerRender",
        "appSpeakerCapture",
        "appMicrophoneRender",
        "meetingMicrophoneCapture",
    ];

    public static CompatibilityGateDecision Evaluate(
        CompatibilityManifest manifest,
        int currentWindowsBuild,
        InstalledDriverEvidence installed)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentOutOfRangeException.ThrowIfNegative(currentWindowsBuild);

        if (currentWindowsBuild < manifest.MinimumWindowsBuild)
        {
            return Deny("unsupportedWindowsBuild", manifest);
        }

        if (!installed.Present
            || !string.Equals(
                installed.RootDevnodeHardwareId,
                RootHardwareId,
                StringComparison.OrdinalIgnoreCase))
        {
            return Deny("driverMissing", manifest);
        }

        if (!installed.CatalogChainValid
            || string.IsNullOrWhiteSpace(installed.CatalogSigner))
        {
            return Deny("driverSignatureInvalid", manifest);
        }

        if (installed.DriverAbiProperty != manifest.DriverAbiVersion)
        {
            return Deny("driverAbiMismatch", manifest);
        }

        if (installed.DriverFileVersion < manifest.MinimumDriverVersion)
        {
            return Deny("driverBelowMinimum", manifest);
        }

        if (!HasRequiredEndpointRoles(installed, manifest))
        {
            return Deny("virtualEndpointsIncomplete", manifest);
        }

        if (installed.DriverFileVersion < manifest.RecommendedDriverVersion)
        {
            return new CompatibilityGateDecision(
                Allowed: true,
                "compatibleUpdateRecommended",
                UpdateRecommended: true,
                RepairAvailable: manifest.DriverPackageAvailable);
        }

        return new CompatibilityGateDecision(
            Allowed: true,
            "compatible",
            UpdateRecommended: false,
            RepairAvailable: false);
    }

    private static bool HasRequiredEndpointRoles(
        InstalledDriverEvidence installed,
        CompatibilityManifest manifest)
    {
        if (manifest.RequiredEndpointRoleCount > EndpointRoles.Length
            || installed.EndpointStates.Count
                != manifest.RequiredEndpointRoleCount)
        {
            return false;
        }

        HashSet<string> actual = new(
            installed.EndpointStates.Select(static endpoint => endpoint.Role),
            StringComparer.Ordinal);
        return actual.Count == manifest.RequiredEndpointRoleCount
            && EndpointRoles
                .Take(manifest.RequiredEndpointRoleCount)
                .All(actual.Contains);
    }

    private static CompatibilityGateDecision Deny(
        string reason,
        CompatibilityManifest manifest)
    {
        return new CompatibilityGateDecision(
            Allowed: false,
            reason,
            UpdateRecommended: true,
            RepairAvailable: manifest.DriverPackageAvailable);
    }
}
