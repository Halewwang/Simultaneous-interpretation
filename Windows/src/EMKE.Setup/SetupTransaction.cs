namespace EMKE.Setup;

internal enum SetupRollbackAction
{
    RemoveCertificate,
    RemoveDriverPackage,
    RemoveDriverDevice,
    RemoveUserPackage,
}

internal sealed class SetupTransaction
{
    private readonly List<SetupRollbackAction> _createdComponents = [];
    private bool _certificateCreated;
    private bool _driverPackageCreated;
    private bool _driverDeviceCreated;
    private bool _userPackageCreated;

    public SetupTransaction(
        bool certificatePreExisting,
        bool driverPackagePreExisting,
        bool driverDevicePreExisting,
        bool userPackagePreExisting)
    {
        CertificatePreExisting = certificatePreExisting;
        DriverPackagePreExisting = driverPackagePreExisting;
        DriverDevicePreExisting = driverDevicePreExisting;
        UserPackagePreExisting = userPackagePreExisting;
    }

    public bool CertificatePreExisting { get; }

    public bool DriverPackagePreExisting { get; }

    public bool DriverDevicePreExisting { get; }

    public bool UserPackagePreExisting { get; }

    public bool CertificateCreatedByAttempt => _certificateCreated;

    public bool DriverPackageCreatedByAttempt => _driverPackageCreated;

    public bool DriverDeviceCreatedByAttempt => _driverDeviceCreated;

    public bool UserPackageCreatedByAttempt => _userPackageCreated;

    public void RecordCertificateCreated()
    {
        RecordCreated(
            CertificatePreExisting,
            ref _certificateCreated,
            SetupRollbackAction.RemoveCertificate);
    }

    public void RecordDriverPackageCreated()
    {
        RecordCreated(
            DriverPackagePreExisting,
            ref _driverPackageCreated,
            SetupRollbackAction.RemoveDriverPackage);
    }

    public void RecordDriverDeviceCreated()
    {
        RecordCreated(
            DriverDevicePreExisting,
            ref _driverDeviceCreated,
            SetupRollbackAction.RemoveDriverDevice);
    }

    public void RecordUserPackageCreated()
    {
        RecordCreated(
            UserPackagePreExisting,
            ref _userPackageCreated,
            SetupRollbackAction.RemoveUserPackage);
    }

    public IReadOnlyList<SetupRollbackAction> GetRollbackActions()
    {
        SetupRollbackAction[] actions = _createdComponents
            .AsEnumerable()
            .Reverse()
            .ToArray();
        return Array.AsReadOnly(actions);
    }

    private void RecordCreated(
        bool preExisting,
        ref bool alreadyCreated,
        SetupRollbackAction rollbackAction)
    {
        if (preExisting)
        {
            throw new InvalidOperationException(
                $"A pre-existing component cannot be marked as created: '{rollbackAction}'.");
        }
        if (alreadyCreated)
        {
            throw new InvalidOperationException(
                $"A component cannot be created twice: '{rollbackAction}'.");
        }

        alreadyCreated = true;
        _createdComponents.Add(rollbackAction);
    }
}
