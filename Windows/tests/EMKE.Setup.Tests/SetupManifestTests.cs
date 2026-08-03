using System.Runtime.InteropServices;
using EMKE.Setup;

namespace EMKE.Setup.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.

[TestClass]
public sealed class SetupManifestTests
{
    private const string Hash =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [TestMethod]
    public void CanonicalInternalManifestIsAcceptedAndCopied()
    {
        List<SetupPayload> mutable = CanonicalPayloads();

        SetupManifest manifest = CreateManifest(mutable);
        mutable.Clear();

        Assert.HasCount(5, manifest.Payloads);
        CollectionAssert.AreEquivalent(
            Enum.GetValues<SetupPayloadKind>(),
            manifest.Payloads.Select(payload => payload.Kind).ToArray());
    }

    [TestMethod]
    public void PayloadRejectsUnsafeOrNonCanonicalFields()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new SetupPayload(
            " ", "app.msix", 1, Hash, SetupPayloadKind.Msix));
        Assert.ThrowsExactly<ArgumentException>(() => new SetupPayload(
            "app", "nested/app.msix", 1, Hash, SetupPayloadKind.Msix));
        Assert.ThrowsExactly<ArgumentException>(() => new SetupPayload(
            "app", "..", 1, Hash, SetupPayloadKind.Msix));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SetupPayload(
            "app", "app.msix", 0, Hash, SetupPayloadKind.Msix));
        Assert.ThrowsExactly<ArgumentException>(() => new SetupPayload(
            "app", "app.msix", 1, "not-a-sha256", SetupPayloadKind.Msix));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SetupPayload(
            "app", "app.msix", 1, Hash, (SetupPayloadKind)99));
    }

    [TestMethod]
    public void ManifestRejectsMissingDuplicateAndMismatchedPayloads()
    {
        List<SetupPayload> missing = CanonicalPayloads();
        missing.RemoveAt(0);
        Assert.ThrowsExactly<ArgumentException>(() => CreateManifest(missing));

        List<SetupPayload> duplicateKind = CanonicalPayloads();
        duplicateKind.Add(new SetupPayload(
            "second-msix",
            "second.msix",
            1,
            Hash,
            SetupPayloadKind.Msix));
        Assert.ThrowsExactly<ArgumentException>(
            () => CreateManifest(duplicateKind));

        List<SetupPayload> duplicateLogicalName = CanonicalPayloads();
        duplicateLogicalName[1] = new SetupPayload(
            duplicateLogicalName[0].LogicalName,
            duplicateLogicalName[1].FileName,
            1,
            Hash,
            SetupPayloadKind.Certificate);
        Assert.ThrowsExactly<ArgumentException>(
            () => CreateManifest(duplicateLogicalName));

        List<SetupPayload> duplicateFileName = CanonicalPayloads();
        duplicateFileName[1] = new SetupPayload(
            duplicateFileName[1].LogicalName,
            duplicateFileName[0].FileName,
            1,
            Hash,
            SetupPayloadKind.Certificate);
        Assert.ThrowsExactly<ArgumentException>(
            () => CreateManifest(duplicateFileName));

        List<SetupPayload> wrongDriverName = CanonicalPayloads();
        wrongDriverName[2] = new SetupPayload(
            "driver-inf",
            "other.inf",
            1,
            Hash,
            SetupPayloadKind.DriverInf);
        Assert.ThrowsExactly<ArgumentException>(
            () => CreateManifest(wrongDriverName));
    }

    [TestMethod]
    public void ManifestRejectsIdentityOutsideTheFrozenRelease()
    {
        List<SetupPayload> payloads = CanonicalPayloads();

        Assert.ThrowsExactly<ArgumentException>(() => CreateManifest(
            payloads,
            channel: "stable"));
        Assert.ThrowsExactly<ArgumentException>(() => CreateManifest(
            payloads,
            packageFamilyName: "Other.Product_123456789abcd"));
        Assert.ThrowsExactly<ArgumentException>(() => CreateManifest(
            payloads,
            packageFamilyName: "EMKE.Translation.Internal_kvab4te83cr7q"));
        Assert.ThrowsExactly<ArgumentException>(() => CreateManifest(
            payloads,
            publisher: "CN=Other Publisher"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreateManifest(
            payloads,
            minimumWindowsBuild: 26200));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreateManifest(
            payloads,
            architecture: Architecture.Arm64));
        Assert.ThrowsExactly<ArgumentException>(() => CreateManifest(
            payloads,
            driverHardwareId: "ROOT\\OTHER"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreateManifest(
            payloads,
            productVersion: new Version(0, 2, 1, 0)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreateManifest(
            payloads,
            driverVersion: new Version(1, 0, 0, 3)));
    }

    private static SetupManifest CreateManifest(
        IReadOnlyList<SetupPayload> payloads,
        string channel = "internal",
        Version? productVersion = null,
        string packageFamilyName =
            "EMKE.Translation.Internal_kvab4te83cr7p",
        string publisher = "CN=EMKE Internal Test",
        int minimumWindowsBuild = 19045,
        Architecture architecture = Architecture.X64,
        string driverHardwareId = "ROOT\\EMKEVIRTUALAUDIO",
        Version? driverVersion = null)
    {
        return new SetupManifest(
            channel,
            productVersion ?? new Version(0, 2, 0, 0),
            packageFamilyName,
            publisher,
            minimumWindowsBuild,
            architecture,
            driverHardwareId,
            driverVersion ?? new Version(1, 0, 0, 2),
            payloads);
    }

    private static List<SetupPayload> CanonicalPayloads()
    {
        return
        [
            new SetupPayload(
                "application-msix",
                "EMKE-Translation-Windows-0.2.0-internal-x64.msix",
                1,
                Hash,
                SetupPayloadKind.Msix),
            new SetupPayload(
                "application-certificate",
                "EMKE-Translation-Windows-0.2.0-internal-x64.cer",
                1,
                Hash,
                SetupPayloadKind.Certificate),
            new SetupPayload(
                "driver-inf",
                "EMKE.VirtualAudio.inf",
                1,
                Hash,
                SetupPayloadKind.DriverInf),
            new SetupPayload(
                "driver-sys",
                "EMKE.VirtualAudio.sys",
                1,
                Hash,
                SetupPayloadKind.DriverSys),
            new SetupPayload(
                "driver-catalog",
                "EMKE.VirtualAudio.cat",
                1,
                Hash,
                SetupPayloadKind.DriverCatalog),
        ];
    }
}

#pragma warning restore CA1515
