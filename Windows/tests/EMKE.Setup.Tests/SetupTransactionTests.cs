using EMKE.Setup;

namespace EMKE.Setup.Tests;

[TestClass]
public sealed class SetupTransactionTests
{
    [TestMethod]
    public void RollbackActionsAreReverseMutationOrder()
    {
        SetupTransaction transaction = new(
            certificatePreExisting: false,
            driverPackagePreExisting: false,
            driverDevicePreExisting: false,
            userPackagePreExisting: false);
        transaction.RecordCertificateCreated();
        transaction.RecordDriverPackageCreated();
        transaction.RecordDriverDeviceCreated();
        transaction.RecordUserPackageCreated();

        CollectionAssert.AreEqual(
            new[]
            {
                SetupRollbackAction.RemoveUserPackage,
                SetupRollbackAction.RemoveDriverDevice,
                SetupRollbackAction.RemoveDriverPackage,
                SetupRollbackAction.RemoveCertificate,
            },
            transaction.GetRollbackActions().ToArray());
    }

    [TestMethod]
    public void PreExistingComponentsNeverProduceRollbackActions()
    {
        SetupTransaction transaction = new(
            certificatePreExisting: true,
            driverPackagePreExisting: true,
            driverDevicePreExisting: true,
            userPackagePreExisting: true);

        Assert.AreEqual(0, transaction.GetRollbackActions().Count);
        Assert.ThrowsExactly<InvalidOperationException>(
            transaction.RecordCertificateCreated);
        Assert.ThrowsExactly<InvalidOperationException>(
            transaction.RecordDriverPackageCreated);
        Assert.ThrowsExactly<InvalidOperationException>(
            transaction.RecordDriverDeviceCreated);
        Assert.ThrowsExactly<InvalidOperationException>(
            transaction.RecordUserPackageCreated);
    }

    [TestMethod]
    public void AComponentCannotBeRecordedTwice()
    {
        SetupTransaction transaction = new(
            certificatePreExisting: false,
            driverPackagePreExisting: false,
            driverDevicePreExisting: false,
            userPackagePreExisting: false);

        transaction.RecordDriverPackageCreated();

        Assert.ThrowsExactly<InvalidOperationException>(
            transaction.RecordDriverPackageCreated);
    }
}
