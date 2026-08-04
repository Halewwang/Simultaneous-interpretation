using System.Runtime.InteropServices;
using EMKE.Setup.Platform;

namespace EMKE.Setup.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.
#pragma warning disable CA2007 // MSTest owns the test synchronization context.

[TestClass]
public sealed class PackageInstallerTests
{
    private const string InvokingSid = "S-1-5-21-1000-1001-1002-1003";

    [TestMethod]
    [DataRow(true, InvokingSid, "packageMustRunUnelevated")]
    [DataRow(false, "S-1-5-21-2000-2001-2002-2003", "invokingSidChanged")]
    public async Task ElevatedOrAlternateUserParentCannotInstallPackage(
        bool elevated,
        string currentSid,
        string expectedFailure)
    {
        using Task4PayloadFixture payload = PackageTestData.Payload();
        MemoryPackageDeploymentApi api = new()
        {
            IsCurrentProcessElevated = elevated,
            CurrentUserSid = currentSid,
            PayloadIdentity = PackageTestData.PayloadIdentity(),
            PackageAfterAdd = PackageTestData.ExactPackage(currentSid),
        };
        PackageInstaller installer = new(api, new RecordingRecoveryWriter());

        PackageInstallResult result = await installer.InstallAsync(
            payload.Payload,
            PackageTestData.Contract(),
            InvokingSid,
            CancellationToken.None);

        Assert.AreEqual(PackageInstallOutcome.Blocked, result.Outcome);
        Assert.AreEqual(expectedFailure, result.FailureCode);
        Assert.IsEmpty(api.InstalledPackages);
    }

    [TestMethod]
    public async Task ExactCompatiblePackageIsPreservedWithoutDeployment()
    {
        using Task4PayloadFixture payload = PackageTestData.Payload();
        InstalledUserPackage exact = PackageTestData.ExactPackage(InvokingSid);
        MemoryPackageDeploymentApi api = PackageTestData.Api(exact);
        PackageInstaller installer = new(api, new RecordingRecoveryWriter());

        PackageInstallResult result = await installer.InstallAsync(
            payload.Payload,
            PackageTestData.Contract(),
            InvokingSid,
            CancellationToken.None);

        Assert.AreEqual(PackageInstallOutcome.Succeeded, result.Outcome);
        Assert.IsNotNull(result.Receipt);
        Assert.IsFalse(result.Receipt.CreatedByAttempt);
        Assert.IsFalse(result.Receipt.UpgradedByAttempt);
        Assert.HasCount(1, api.InstalledPackages);
        Assert.AreEqual(exact, api.InstalledPackages[0]);
    }

    [TestMethod]
    public async Task UnexpectedPublisherIsBlockedAndPreserved()
    {
        using Task4PayloadFixture payload = PackageTestData.Payload();
        InstalledUserPackage conflicting = PackageTestData.ExactPackage(
            InvokingSid) with
        {
            Publisher = "CN=Unexpected Publisher",
            FullName = "EMKE.Translation.Internal_bad_x64__unexpected",
        };
        MemoryPackageDeploymentApi api = PackageTestData.Api(conflicting);
        PackageInstaller installer = new(api, new RecordingRecoveryWriter());

        PackageInstallResult result = await installer.InstallAsync(
            payload.Payload,
            PackageTestData.Contract(),
            InvokingSid,
            CancellationToken.None);

        Assert.AreEqual(PackageInstallOutcome.Blocked, result.Outcome);
        Assert.AreEqual("packagePublisherConflict", result.FailureCode);
        Assert.HasCount(1, api.InstalledPackages);
        Assert.AreEqual(conflicting, api.InstalledPackages[0]);
    }

    [TestMethod]
    public async Task ExactPackageIsInstalledAndPostInstallIdentityIsVerified()
    {
        using Task4PayloadFixture payload = PackageTestData.Payload();
        MemoryPackageDeploymentApi api = PackageTestData.Api();
        PackageInstaller installer = new(api, new RecordingRecoveryWriter());

        PackageInstallResult result = await installer.InstallAsync(
            payload.Payload,
            PackageTestData.Contract(),
            InvokingSid,
            CancellationToken.None);

        Assert.AreEqual(PackageInstallOutcome.Succeeded, result.Outcome);
        Assert.IsNotNull(result.Receipt);
        Assert.IsTrue(result.Receipt.CreatedByAttempt);
        Assert.IsFalse(result.Receipt.UpgradedByAttempt);
        Assert.HasCount(1, api.InstalledPackages);
        Assert.AreEqual(PackageTestData.ExactPackage(InvokingSid), api.InstalledPackages[0]);
    }

    [TestMethod]
    public async Task SidChangeAfterDeploymentFailsWithRollbackReceipt()
    {
        using Task4PayloadFixture payload = PackageTestData.Payload();
        MemoryPackageDeploymentApi api = PackageTestData.Api();
        api.CurrentUserSidAfterAdd = "S-1-5-21-9999-9998-9997-9996";
        PackageInstaller installer = new(api, new RecordingRecoveryWriter());

        PackageInstallResult result = await installer.InstallAsync(
            payload.Payload,
            PackageTestData.Contract(),
            InvokingSid,
            CancellationToken.None);

        Assert.AreEqual(PackageInstallOutcome.Failed, result.Outcome);
        Assert.AreEqual("invokingSidChanged", result.FailureCode);
        Assert.IsNotNull(result.Receipt);
        Assert.IsTrue(result.Receipt.CreatedByAttempt);
    }

    [TestMethod]
    public async Task RollbackRemovesOnlyTheExactPackageCreatedByTransaction()
    {
        using Task4PayloadFixture payload = PackageTestData.Payload();
        MemoryPackageDeploymentApi api = PackageTestData.Api();
        RecordingRecoveryWriter recovery = new();
        PackageInstaller installer = new(api, recovery);
        PackageInstallResult installed = await installer.InstallAsync(
            payload.Payload,
            PackageTestData.Contract(),
            InvokingSid,
            CancellationToken.None);
        Assert.IsNotNull(installed.Receipt);

        api.InstalledPackages[0] = api.InstalledPackages[0] with
        {
            FullName = "EMKE.Translation.Internal_changed_x64__kvab4te83cr7p",
        };
        PackageRollbackResult result = await installer.RollbackAsync(
            installed.Receipt,
            Guid.Parse("5e968388-2402-4be2-b87f-59e444604bc3"),
            CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.IsFalse(result.Removed);
        Assert.HasCount(1, api.InstalledPackages);
        Assert.HasCount(1, recovery.Records);
        Assert.AreEqual("packageRollbackIdentityChanged", recovery.Records[0].FailureCode);
    }

    [TestMethod]
    public async Task RollbackRemovesExactUpgradedTransactionPackage()
    {
        using Task4PayloadFixture payload = PackageTestData.Payload();
        InstalledUserPackage older = PackageTestData.ExactPackage(InvokingSid) with
        {
            Version = new Version(0, 1, 0, 0),
            FullName = "EMKE.Translation.Internal_0.1.0.0_x64__kvab4te83cr7p",
        };
        MemoryPackageDeploymentApi api = PackageTestData.Api(older);
        PackageInstaller installer = new(api, new RecordingRecoveryWriter());
        PackageInstallResult installed = await installer.InstallAsync(
            payload.Payload,
            PackageTestData.Contract(),
            InvokingSid,
            CancellationToken.None);
        Assert.IsNotNull(installed.Receipt);
        Assert.IsTrue(installed.Receipt.UpgradedByAttempt);

        PackageRollbackResult result = await installer.RollbackAsync(
            installed.Receipt,
            Guid.Parse("4a4974f0-e70e-493a-9202-a504967a88d5"),
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsTrue(result.Removed);
        Assert.IsEmpty(api.InstalledPackages);
    }
}

internal static class PackageTestData
{
    public const string Sid = "S-1-5-21-1000-1001-1002-1003";
    public const string Name = "EMKE.Translation.Internal";
    public const string Family = "EMKE.Translation.Internal_kvab4te83cr7p";
    public const string FullName =
        "EMKE.Translation.Internal_0.2.0.0_x64__kvab4te83cr7p";
    public const string Publisher = "CN=EMKE Internal Test";

    public static Task4PayloadFixture Payload() => Task4PayloadFixture.Create(
        SetupPayloadKind.Msix,
        "application-msix",
        "EMKE-Translation-Windows-0.2.0-internal-x64.msix");

    public static PackageInstallContract Contract() => new(
        Name,
        Family,
        FullName,
        Publisher,
        new Version(0, 2, 0, 0),
        Architecture.X64);

    public static PackagePayloadIdentity PayloadIdentity() => new(
        Name,
        Publisher,
        new Version(0, 2, 0, 0),
        Architecture.X64,
        SignatureValid: true);

    public static InstalledUserPackage ExactPackage(string sid) => new(
        Name,
        Family,
        FullName,
        Publisher,
        new Version(0, 2, 0, 0),
        Architecture.X64,
        @"C:\Program Files\WindowsApps\EMKE.Translation.Internal",
        sid,
        InstallLocationTrusted: true,
        SignatureValid: true);

    public static MemoryPackageDeploymentApi Api(
        params InstalledUserPackage[] existing)
    {
        MemoryPackageDeploymentApi api = new()
        {
            CurrentUserSid = Sid,
            PayloadIdentity = PayloadIdentity(),
            PackageAfterAdd = ExactPackage(Sid),
        };
        api.InstalledPackages.AddRange(existing);
        return api;
    }
}

internal sealed class MemoryPackageDeploymentApi : IPackageDeploymentApi
{
    public bool IsCurrentProcessElevated { get; set; }

    public string CurrentUserSid { get; set; } = PackageTestData.Sid;

    public string? CurrentUserSidAfterAdd { get; set; }

    public PackagePayloadIdentity PayloadIdentity { get; set; } =
        PackageTestData.PayloadIdentity();

    public InstalledUserPackage PackageAfterAdd { get; set; } =
        PackageTestData.ExactPackage(PackageTestData.Sid);

    public List<InstalledUserPackage> InstalledPackages { get; } = [];

    public PackagePayloadIdentity InspectPayload(VerifiedSetupPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return PayloadIdentity;
    }

    public IReadOnlyList<InstalledUserPackage> FindPackages(
        string userSid,
        string familyName) => InstalledPackages
        .Where(package => string.Equals(package.UserSid, userSid, StringComparison.Ordinal)
            && string.Equals(package.FamilyName, familyName, StringComparison.Ordinal))
        .ToArray();

    public Task AddPackageAsync(
        VerifiedSetupPayload payload,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InstalledPackages.RemoveAll(package => string.Equals(
            package.Name,
            PackageAfterAdd.Name,
            StringComparison.Ordinal));
        InstalledPackages.Add(PackageAfterAdd);
        if (CurrentUserSidAfterAdd is not null)
        {
            CurrentUserSid = CurrentUserSidAfterAdd;
        }
        return Task.CompletedTask;
    }

    public Task RemovePackageAsync(
        string packageFullName,
        string userSid,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InstalledPackages.RemoveAll(package => string.Equals(
                package.FullName,
                packageFullName,
                StringComparison.Ordinal)
            && string.Equals(package.UserSid, userSid, StringComparison.Ordinal));
        return Task.CompletedTask;
    }
}

#pragma warning restore CA1515
#pragma warning restore CA2007
