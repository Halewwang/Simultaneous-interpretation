using System.Runtime.InteropServices;
using EMKE.Setup;
using EMKE.Setup.Platform;

namespace EMKE.Setup.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.

[TestClass]
public sealed class SetupPreflightTests
{
    [DataTestMethod]
    [DataRow(19044, Architecture.X64, false, "windowsBuildUnsupported")]
    [DataRow(19045, Architecture.Arm64, false, "architectureUnsupported")]
    [DataRow(19045, Architecture.X64, true, "windowsServerUnsupported")]
    public void HostOutsideSupportedWorkstationContractIsRejected(
        int build,
        Architecture architecture,
        bool isServer,
        string expectedFailure)
    {
        SetupPreflight preflight = new(new StaticHostProbe(
            new SetupHostInfo(build, architecture, isServer)));

        SetupPreflightDecision decision = preflight.Evaluate(Manifest());

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual(expectedFailure, decision.FailureCode);
    }

    [TestMethod]
    public void Build19045X64WorkstationIsAdmittedBeforeAnyMachineChange()
    {
        SetupPreflight preflight = new(new StaticHostProbe(
            new SetupHostInfo(19045, Architecture.X64, isServer: false)));

        SetupPreflightDecision decision = preflight.Evaluate(Manifest());

        Assert.IsTrue(decision.Allowed);
        Assert.IsNull(decision.FailureCode);
    }

    private static SetupManifest Manifest()
    {
        return new SetupManifest(
            "internal",
            new Version(0, 2, 0, 0),
            "EMKE.Translation.Internal_kvab4te83cr7p",
            "CN=EMKE Internal Test",
            19045,
            Architecture.X64,
            "ROOT\\EMKEVIRTUALAUDIO",
            new Version(1, 0, 0, 2),
            Payloads());
    }

    private static IReadOnlyList<SetupPayload> Payloads()
    {
        return
        [
            new("application-msix", "EMKE-Translation-Windows-0.2.0-internal-x64.msix", 16, "3519c43beb231dcbab153b916b232a2daf913552ef1cfeca4ca83bdbfb05b78e", SetupPayloadKind.Msix),
            new("application-certificate", "EMKE-Translation-Windows-0.2.0-internal-x64.cer", 23, "de6d47c98e8cc925adb5c33c64ce76321978ba7b1ba2ded1eef0d9417d01ef85", SetupPayloadKind.Certificate),
            new("driver-inf", "EMKE.VirtualAudio.inf", 10, "4dedc6dd0667ef467bcbdd8316b47e2619d0fa65a2f894a0428c87074a7eaa2d", SetupPayloadKind.DriverInf),
            new("driver-sys", "EMKE.VirtualAudio.sys", 10, "3ef98b837865f6da47d5e75f0261b11e2eaf9eade9d2347b15ba9d5429a1b0ae", SetupPayloadKind.DriverSys),
            new("driver-catalog", "EMKE.VirtualAudio.cat", 14, "24be15e1705c3debf4d6515daea506d70327452c6769585235e89b614cd1e68a", SetupPayloadKind.DriverCatalog),
        ];
    }

    private sealed class StaticHostProbe(SetupHostInfo host) : ISetupHostProbe
    {
        public SetupHostInfo Read() => host;
    }
}

#pragma warning restore CA1515
