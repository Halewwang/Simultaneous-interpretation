using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace EMKE.Setup.Elevated;

internal sealed record SetupElevatedHelperArguments
{
    public const string FixedSwitch = "--elevated-helper-v1";
    private const string PipePrefix = "emke-setup-";

    private SetupElevatedHelperArguments(string pipeName, string nonce)
    {
        PipeName = pipeName;
        Nonce = nonce;
    }

    public string PipeName { get; }

    public string Nonce { get; }

    public static bool TryParse(
        IReadOnlyList<string> arguments,
        out SetupElevatedHelperArguments? parsed)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        parsed = null;
        if (arguments.Count != 3
            || !string.Equals(arguments[0], FixedSwitch, StringComparison.Ordinal)
            || !IsPipeName(arguments[1])
            || !IsLowercaseHex(arguments[2], 64))
        {
            return false;
        }

        parsed = new SetupElevatedHelperArguments(arguments[1], arguments[2]);
        return true;
    }

    internal static bool IsPipeName(string value)
    {
        return value.Length == PipePrefix.Length + 32
            && value.StartsWith(PipePrefix, StringComparison.Ordinal)
            && IsLowercaseHex(value[PipePrefix.Length..], 32);
    }

    private static bool IsLowercaseHex(string value, int length)
    {
        return value.Length == length
            && value.All(static character => character is
                >= '0' and <= '9' or >= 'a' and <= 'f');
    }
}

internal sealed record SetupExecutableIdentity
{
    public SetupExecutableIdentity(
        string fullPath,
        uint volumeSerialNumber,
        uint fileIndexHigh,
        uint fileIndexLow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        if (!Path.IsPathFullyQualified(fullPath))
        {
            throw new ArgumentException(
                "An executable identity requires an absolute path.",
                nameof(fullPath));
        }
        FullPath = Path.GetFullPath(fullPath);
        VolumeSerialNumber = volumeSerialNumber;
        FileIndexHigh = fileIndexHigh;
        FileIndexLow = fileIndexLow;
    }

    public string FullPath { get; }

    public uint VolumeSerialNumber { get; }

    public uint FileIndexHigh { get; }

    public uint FileIndexLow { get; }
}

internal interface ISetupProcessImageProbe
{
    SetupExecutableIdentity GetCurrentIdentity();

    SetupExecutableIdentity GetProcessIdentity(int processId);
}

internal interface ISetupElevatedProcess : IDisposable
{
    int Id { get; }

    Task WaitForExitAsync(CancellationToken cancellationToken);

    void TryTerminate();
}

internal interface ISetupElevatedProcessStarter
{
    ISetupElevatedProcess Start(ProcessStartInfo startInfo);
}

internal interface IOneShotElevationChannel : IAsyncDisposable
{
    Task WaitForConnectionAsync(CancellationToken cancellationToken);

    int GetClientProcessId();

    Task<byte[]> ExchangeAsync(
        ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> authenticatedRequest,
        CancellationToken cancellationToken);

    Task<byte[]> FinalizeAsync(
        ReadOnlyMemory<byte> authenticatedFinalization,
        CancellationToken cancellationToken);
}

internal interface IOneShotElevationChannelFactory
{
    IOneShotElevationChannel Create(
        string pipeName,
        SecurityIdentifier invokingSid);
}

internal sealed class SetupElevationIncomingMessage
{
    public SetupElevationIncomingMessage(
        byte[] macKey,
        byte[] authenticatedRequest)
    {
        ArgumentNullException.ThrowIfNull(macKey);
        ArgumentNullException.ThrowIfNull(authenticatedRequest);
        if (macKey.Length != 32 || authenticatedRequest.Length == 0)
        {
            throw new ArgumentException("The incoming elevation message is invalid.");
        }
        MacKey = macKey;
        AuthenticatedRequest = authenticatedRequest;
    }

    public byte[] MacKey { get; }

    public byte[] AuthenticatedRequest { get; }
}

internal interface IOneShotElevationClientChannel : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken cancellationToken);

    int GetServerProcessId();

    Task<SetupElevationIncomingMessage> ReceiveAsync(
        CancellationToken cancellationToken);

    Task SendResultAsync(
        ReadOnlyMemory<byte> authenticatedResult,
        CancellationToken cancellationToken);

    Task<byte[]> ReceiveFinalizationAsync(CancellationToken cancellationToken);

    Task SendFinalizationResultAsync(
        ReadOnlyMemory<byte> authenticatedResult,
        CancellationToken cancellationToken);
}

internal interface IOneShotElevationClientChannelFactory
{
    IOneShotElevationClientChannel Create(string pipeName);
}

internal interface ISetupElevatedRequestHandler
{
    Task<SetupElevatedHelperOutcome> HandleAsync(
        SetupElevationRequest request,
        CancellationToken cancellationToken);

    async Task<SetupElevatedPreparedChange> PrepareAsync(
        SetupElevationRequest request,
        CancellationToken cancellationToken) => new(
            await HandleAsync(request, cancellationToken).ConfigureAwait(false),
            new SetupMachineCreatedState(false, false, false),
            State: null);

    Task<bool> FinalizeAsync(
        SetupElevatedPreparedChange prepared,
        SetupElevationFinalizationAction action,
        Guid transactionId,
        CancellationToken cancellationToken) => Task.FromResult(true);
}

internal sealed record SetupElevatedPreparedChange(
    SetupElevatedHelperOutcome Outcome,
    SetupMachineCreatedState CreatedState,
    object? State);

internal interface ISetupElevationSecretSource
{
    string CreatePipeName();

    byte[] CreateMacKey();
}

internal enum SetupElevationLaunchOutcome
{
    Succeeded,
    RebootRequired,
    UacCancelled,
    TimedOut,
    Rejected,
}

internal sealed record SetupElevationLaunchResult
{
    private SetupElevationLaunchResult(
        SetupElevationLaunchOutcome outcome,
        string? failureCode)
    {
        Outcome = outcome;
        FailureCode = failureCode;
    }

    public SetupElevationLaunchOutcome Outcome { get; }

    public string? FailureCode { get; }

    public static SetupElevationLaunchResult Completed(
        SetupElevationLaunchOutcome outcome)
    {
        if (outcome is not (
            SetupElevationLaunchOutcome.Succeeded
            or SetupElevationLaunchOutcome.RebootRequired))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }
        return new SetupElevationLaunchResult(outcome, failureCode: null);
    }

    public static SetupElevationLaunchResult Failed(
        SetupElevationLaunchOutcome outcome,
        string failureCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        if (outcome is SetupElevationLaunchOutcome.Succeeded
            or SetupElevationLaunchOutcome.RebootRequired)
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }
        return new SetupElevationLaunchResult(outcome, failureCode);
    }
}

internal sealed record SetupElevatedHelperSessionResult
{
    private SetupElevatedHelperSessionResult(bool succeeded, string? failureCode)
    {
        Succeeded = succeeded;
        FailureCode = failureCode;
    }

    public bool Succeeded { get; }

    public string? FailureCode { get; }

    public static SetupElevatedHelperSessionResult Completed { get; } =
        new(true, failureCode: null);

    public static SetupElevatedHelperSessionResult Rejected(string failureCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        return new SetupElevatedHelperSessionResult(false, failureCode);
    }
}

internal sealed record SetupElevationPreparationResult(
    SetupElevationLaunchOutcome Outcome,
    string? FailureCode,
    SetupMachineChangeReceipt? Receipt,
    SetupElevationPreparedSession? Session);

internal sealed class SetupElevationPreparedSession : IAsyncDisposable
{
    private readonly IOneShotElevationChannel _channel;
    private readonly ISetupElevatedProcess _process;
    private readonly byte[] _key;
    private readonly SetupElevationRequest _request;
    private readonly TimeSpan _timeout;
    private bool _finished;

    internal SetupElevationPreparedSession(
        IOneShotElevationChannel channel,
        ISetupElevatedProcess process,
        byte[] key,
        SetupElevationRequest request,
        TimeSpan timeout)
    {
        _channel = channel;
        _process = process;
        _key = key;
        _request = request;
        _timeout = timeout;
    }

    public Task<bool> CommitAsync(CancellationToken cancellationToken) =>
        FinishAsync(SetupElevationFinalizationAction.Commit, cancellationToken);

    public Task<bool> RollbackAsync(CancellationToken cancellationToken) =>
        FinishAsync(SetupElevationFinalizationAction.Rollback, cancellationToken);

    private async Task<bool> FinishAsync(
        SetupElevationFinalizationAction action,
        CancellationToken cancellationToken)
    {
        if (_finished)
        {
            return false;
        }
        _finished = true;
        bool succeeded = false;
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            byte[] message = SetupElevationFinalizationCodec.EncodeAuthenticated(
                _request.TransactionId,
                _request.Nonce,
                action,
                succeeded: false,
                _key);
            byte[] response = await _channel.FinalizeAsync(
                    message,
                    timeout.Token)
                .ConfigureAwait(false);
            (SetupElevationFinalizationAction responseAction, bool accepted) =
                SetupElevationFinalizationCodec.DecodeAuthenticated(
                    response,
                    _key,
                    _request.TransactionId,
                    _request.Nonce);
            if (responseAction != action || !accepted)
            {
                return false;
            }
            await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            succeeded = true;
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(_key);
            if (!succeeded)
            {
                _process.TryTerminate();
            }
            await _channel.DisposeAsync().ConfigureAwait(false);
            _process.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_finished)
        {
            _ = await RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}

internal sealed class ElevatedHelperLauncher
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);
    private readonly ISetupElevatedProcessStarter _processStarter;
    private readonly IOneShotElevationChannelFactory _channelFactory;
    private readonly ISetupProcessImageProbe _imageProbe;
    private readonly ISetupElevationSecretSource _secretSource;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _timeout;
    private readonly SecurityIdentifier _invokingSid;

    public ElevatedHelperLauncher()
        : this(
            WindowsSetupElevatedProcessStarter.Instance,
            new WindowsOneShotElevationChannelFactory(),
            WindowsSetupProcessImageProbe.Instance,
            new CryptographicSetupElevationSecretSource(),
            TimeProvider.System,
            DefaultTimeout,
            GetCurrentUserSid())
    {
    }

    internal ElevatedHelperLauncher(
        ISetupElevatedProcessStarter processStarter,
        IOneShotElevationChannelFactory channelFactory,
        ISetupProcessImageProbe imageProbe,
        ISetupElevationSecretSource secretSource,
        TimeProvider timeProvider,
        TimeSpan timeout,
        SecurityIdentifier invokingSid)
    {
        _processStarter = processStarter
            ?? throw new ArgumentNullException(nameof(processStarter));
        _channelFactory = channelFactory
            ?? throw new ArgumentNullException(nameof(channelFactory));
        _imageProbe = imageProbe
            ?? throw new ArgumentNullException(nameof(imageProbe));
        _secretSource = secretSource
            ?? throw new ArgumentNullException(nameof(secretSource));
        _timeProvider = timeProvider
            ?? throw new ArgumentNullException(nameof(timeProvider));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        _timeout = timeout;
        _invokingSid = invokingSid
            ?? throw new ArgumentNullException(nameof(invokingSid));
    }

    public async Task<SetupElevationLaunchResult> LaunchAsync(
        SetupElevationRequest request,
        CancellationToken cancellationToken)
    {
        SetupElevationPreparationResult prepared = await PrepareAsync(
                request,
                cancellationToken)
            .ConfigureAwait(false);
        if (prepared.Session is null)
        {
            return SetupElevationLaunchResult.Failed(
                prepared.Outcome,
                prepared.FailureCode ?? "helperRejected");
        }
#pragma warning disable CA2007 // The prepared session owns native resources.
        await using SetupElevationPreparedSession session = prepared.Session;
#pragma warning restore CA2007
        bool committed;
        try
        {
            committed = await session.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SetupElevationProtocolException)
        {
            committed = false;
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            committed = false;
        }
        return committed
            ? SetupElevationLaunchResult.Completed(prepared.Outcome)
            : SetupElevationLaunchResult.Failed(
                SetupElevationLaunchOutcome.Rejected,
                "helperCommitRejected");
    }

    public async Task<SetupElevationPreparationResult> PrepareAsync(
        SetupElevationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        SetupElevationRequestCodec.ValidateLifetime(
            request,
            _timeProvider.GetUtcNow());
        string pipeName = _secretSource.CreatePipeName();
        if (!SetupElevatedHelperArguments.IsPipeName(pipeName))
        {
            throw new InvalidOperationException("The pipe-name source is invalid.");
        }
        IOneShotElevationChannel channel =
            _channelFactory.Create(pipeName, _invokingSid);
        ISetupElevatedProcess? process = null;
        byte[]? key = null;
        bool transferred = false;
        try
        {
            SetupExecutableIdentity currentIdentity = _imageProbe.GetCurrentIdentity();
            try
            {
                process = _processStarter.Start(CreateStartInfo(
                    currentIdentity.FullPath,
                    pipeName,
                    request.Nonce));
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
            {
                return FailedPreparation(
                    SetupElevationLaunchOutcome.UacCancelled,
                    "uacCancelled");
            }
            using CancellationTokenSource timeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);
            await channel.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            if (channel.GetClientProcessId() != process.Id)
            {
                return FailedPreparation(
                    SetupElevationLaunchOutcome.Rejected,
                    "helperPidMismatch");
            }
            if (_imageProbe.GetProcessIdentity(process.Id) != currentIdentity)
            {
                return FailedPreparation(
                    SetupElevationLaunchOutcome.Rejected,
                    "helperImageMismatch");
            }
            key = _secretSource.CreateMacKey();
            if (key.Length != 32)
            {
                throw new InvalidOperationException(
                    "The MAC-key source did not return 256 bits.");
            }
            byte[] requestBytes =
                SetupElevationRequestCodec.EncodeAuthenticated(request, key);
            byte[] resultBytes = await channel.ExchangeAsync(
                    key,
                    requestBytes,
                    timeout.Token)
                .ConfigureAwait(false);
            SetupElevatedHelperResult result =
                SetupElevationResultCodec.DecodeAuthenticated(
                    resultBytes,
                    key,
                    request.TransactionId,
                    request.Nonce);
            if (result.Outcome == SetupElevatedHelperOutcome.Failed)
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                return FailedPreparation(
                    SetupElevationLaunchOutcome.Rejected,
                    "helperRejected");
            }
            SetupElevationLaunchOutcome outcome = result.Outcome ==
                SetupElevatedHelperOutcome.RebootRequired
                ? SetupElevationLaunchOutcome.RebootRequired
                : SetupElevationLaunchOutcome.Succeeded;
            SetupMachineChangeReceipt receipt = new(new SetupMachineCreatedState(
                result.CertificateCreated,
                result.DriverPackageCreated,
                result.DriverDeviceCreated));
#pragma warning disable CA2000 // Ownership transfers to the preparation result.
            SetupElevationPreparedSession session = new(
                channel,
                process,
                key,
                request,
                _timeout);
#pragma warning restore CA2000
            transferred = true;
            return new SetupElevationPreparationResult(
                outcome,
                FailureCode: null,
                receipt,
                session);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FailedPreparation(
                SetupElevationLaunchOutcome.TimedOut,
                "helperTimedOut");
        }
        catch (SetupElevationProtocolException exception)
        {
            return FailedPreparation(
                SetupElevationLaunchOutcome.Rejected,
                exception.FailureCode);
        }
        finally
        {
            if (!transferred)
            {
                if (key is not null)
                {
                    CryptographicOperations.ZeroMemory(key);
                }
                process?.TryTerminate();
                process?.Dispose();
                await channel.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static SetupElevationPreparationResult FailedPreparation(
        SetupElevationLaunchOutcome outcome,
        string failureCode) => new(
            outcome,
            failureCode,
            Receipt: null,
            Session: null);

    private static ProcessStartInfo CreateStartInfo(
        string setupExecutable,
        string pipeName,
        string nonce)
    {
        ProcessStartInfo startInfo = new(setupExecutable)
        {
            Verb = "runas",
            UseShellExecute = true,
        };
        startInfo.ArgumentList.Add(SetupElevatedHelperArguments.FixedSwitch);
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add(nonce);
        return startInfo;
    }

    private static SecurityIdentifier GetCurrentUserSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User
            ?? throw new InvalidOperationException(
                "The invoking Windows SID is unavailable.");
    }
}

internal sealed class ElevatedHelperSession
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(15);
    private readonly IOneShotElevationClientChannelFactory _channelFactory;
    private readonly ISetupProcessImageProbe _imageProbe;
    private readonly TimeProvider _timeProvider;
    private readonly SetupElevationReplayGuard _replayGuard;
    private readonly TimeSpan _timeout;

    public ElevatedHelperSession()
        : this(
            new WindowsOneShotElevationClientChannelFactory(),
            WindowsSetupProcessImageProbe.Instance,
            TimeProvider.System,
            new SetupElevationReplayGuard(),
            DefaultTimeout)
    {
    }

    internal ElevatedHelperSession(
        IOneShotElevationClientChannelFactory channelFactory,
        ISetupProcessImageProbe imageProbe,
        TimeProvider timeProvider,
        SetupElevationReplayGuard replayGuard,
        TimeSpan timeout)
    {
        _channelFactory = channelFactory
            ?? throw new ArgumentNullException(nameof(channelFactory));
        _imageProbe = imageProbe
            ?? throw new ArgumentNullException(nameof(imageProbe));
        _timeProvider = timeProvider
            ?? throw new ArgumentNullException(nameof(timeProvider));
        _replayGuard = replayGuard
            ?? throw new ArgumentNullException(nameof(replayGuard));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        _timeout = timeout;
    }

#pragma warning disable CA1031 // A lost parent must trigger best-effort exact rollback.
    public async Task<SetupElevatedHelperSessionResult> RunAsync(
        SetupElevatedHelperArguments arguments,
        ISetupElevatedRequestHandler handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(handler);
#pragma warning disable CA2007 // The interface is disposed asynchronously below.
        await using IOneShotElevationClientChannel channel =
            _channelFactory.Create(arguments.PipeName);
#pragma warning restore CA2007
        byte[]? key = null;
        SetupElevationRequest? acceptedRequest = null;
        SetupElevatedPreparedChange? preparedChange = null;
        bool finalizationAttempted = false;
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            await channel.ConnectAsync(timeout.Token).ConfigureAwait(false);
            SetupExecutableIdentity currentIdentity = _imageProbe.GetCurrentIdentity();
            SetupExecutableIdentity parentIdentity =
                _imageProbe.GetProcessIdentity(channel.GetServerProcessId());
            if (parentIdentity != currentIdentity)
            {
                return SetupElevatedHelperSessionResult.Rejected(
                    "parentImageMismatch");
            }

            SetupElevationIncomingMessage incoming =
                await channel.ReceiveAsync(timeout.Token).ConfigureAwait(false);
            key = incoming.MacKey;
            SetupElevationRequest request =
                SetupElevationRequestCodec.DecodeAuthenticated(
                    incoming.AuthenticatedRequest,
                    key,
                    _timeProvider.GetUtcNow(),
                    _replayGuard);
            acceptedRequest = request;
            if (!string.Equals(
                    request.Nonce,
                    arguments.Nonce,
                    StringComparison.Ordinal))
            {
                return SetupElevatedHelperSessionResult.Rejected(
                    "helperNonceMismatch");
            }

            SetupElevatedPreparedChange prepared = await handler.PrepareAsync(
                request,
                timeout.Token).ConfigureAwait(false);
            preparedChange = prepared;
            SetupElevatedHelperResult result = new(
                request.TransactionId,
                request.Nonce,
                prepared.Outcome,
                prepared.CreatedState.CertificateCreated,
                prepared.CreatedState.DriverPackageCreated,
                prepared.CreatedState.DriverDeviceCreated);
            byte[] authenticatedResult =
                SetupElevationResultCodec.EncodeAuthenticated(result, key);
            await channel.SendResultAsync(authenticatedResult, timeout.Token)
                .ConfigureAwait(false);
            if (prepared.Outcome == SetupElevatedHelperOutcome.Failed)
            {
                return SetupElevatedHelperSessionResult.Completed;
            }
            byte[] authenticatedFinalization =
                await channel.ReceiveFinalizationAsync(timeout.Token)
                    .ConfigureAwait(false);
            (SetupElevationFinalizationAction action, bool requestSucceeded) =
                SetupElevationFinalizationCodec.DecodeAuthenticated(
                    authenticatedFinalization,
                    key,
                    request.TransactionId,
                    request.Nonce);
            if (requestSucceeded)
            {
                return SetupElevatedHelperSessionResult.Rejected(
                    "invalidFinalizationRequest");
            }
            bool finalized = await handler.FinalizeAsync(
                    prepared,
                    action,
                    request.TransactionId,
                    timeout.Token)
                .ConfigureAwait(false);
            finalizationAttempted = true;
            byte[] finalizationResult =
                SetupElevationFinalizationCodec.EncodeAuthenticated(
                    request.TransactionId,
                    request.Nonce,
                    action,
                    finalized,
                    key);
            await channel.SendFinalizationResultAsync(
                    finalizationResult,
                    timeout.Token)
                .ConfigureAwait(false);
            return SetupElevatedHelperSessionResult.Completed;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SetupElevatedHelperSessionResult.Rejected("helperTimedOut");
        }
        catch (SetupElevationProtocolException exception)
        {
            return SetupElevatedHelperSessionResult.Rejected(exception.FailureCode);
        }
        finally
        {
            if (!finalizationAttempted
                && preparedChange is not null
                && preparedChange.Outcome != SetupElevatedHelperOutcome.Failed
                && acceptedRequest is not null)
            {
                try
                {
                    _ = await handler.FinalizeAsync(
                            preparedChange,
                            SetupElevationFinalizationAction.Rollback,
                            acceptedRequest.TransactionId,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Concrete installers persist exact recovery evidence.
                }
            }
            if (key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }
#pragma warning restore CA1031
}

internal sealed class CryptographicSetupElevationSecretSource
    : ISetupElevationSecretSource
{
    public string CreatePipeName()
    {
        return string.Concat(
            "emke-setup-",
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)));
    }

    public byte[] CreateMacKey() => RandomNumberGenerator.GetBytes(32);
}

internal sealed class WindowsOneShotElevationChannelFactory
    : IOneShotElevationChannelFactory
{
    public IOneShotElevationChannel Create(
        string pipeName,
        SecurityIdentifier invokingSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(invokingSid);
#pragma warning disable CA2000 // Ownership transfers to WindowsOneShotElevationChannel.
        NamedPipeServerStream pipe = NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            inBufferSize: 4096,
            outBufferSize: 4096,
            CreatePipeSecurity(invokingSid),
            HandleInheritability.None,
            (PipeAccessRights)0);
        return new WindowsOneShotElevationChannel(pipe);
#pragma warning restore CA2000
    }

    internal static PipeSecurity CreatePipeSecurity(SecurityIdentifier invokingSid)
    {
        ArgumentNullException.ThrowIfNull(invokingSid);
        PipeSecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddFullControl(security, invokingSid);
        AddFullControl(
            security,
            new SecurityIdentifier(
                WellKnownSidType.BuiltinAdministratorsSid,
                domainSid: null));
        AddFullControl(
            security,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, domainSid: null));
        return security;
    }

    private static void AddFullControl(
        PipeSecurity security,
        SecurityIdentifier identity)
    {
        security.AddAccessRule(new PipeAccessRule(
            identity,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
    }
}

internal sealed class WindowsOneShotElevationClientChannelFactory
    : IOneShotElevationClientChannelFactory
{
    public IOneShotElevationClientChannel Create(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (!SetupElevatedHelperArguments.IsPipeName(pipeName))
        {
            throw new ArgumentException(
                "The helper pipe name is invalid.",
                nameof(pipeName));
        }
#pragma warning disable CA2000 // Ownership transfers to the client-channel wrapper.
        return new WindowsOneShotElevationClientChannel(
            new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.WriteThrough));
#pragma warning restore CA2000
    }
}

internal sealed class WindowsOneShotElevationClientChannel(
    NamedPipeClientStream pipe) : IOneShotElevationClientChannel
{
    private const int MaximumMessageLength = 65536;
    private readonly NamedPipeClientStream _pipe = pipe
        ?? throw new ArgumentNullException(nameof(pipe));

    public Task ConnectAsync(CancellationToken cancellationToken) =>
        _pipe.ConnectAsync(cancellationToken);

    public int GetServerProcessId()
    {
        if (!GetNamedPipeServerProcessId(_pipe.SafePipeHandle, out uint processId)
            || processId > int.MaxValue)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        return checked((int)processId);
    }

    public async Task<SetupElevationIncomingMessage> ReceiveAsync(
        CancellationToken cancellationToken)
    {
        byte[] key = new byte[32];
        try
        {
            await _pipe.ReadExactlyAsync(key, cancellationToken).ConfigureAwait(false);
            byte[] length = new byte[sizeof(uint)];
            await _pipe.ReadExactlyAsync(length, cancellationToken).ConfigureAwait(false);
            uint unsignedLength = BinaryPrimitives.ReadUInt32LittleEndian(length);
            if (unsignedLength is 0 or > MaximumMessageLength)
            {
                throw new SetupElevationProtocolException("invalidRequestLength");
            }
            byte[] request = new byte[checked((int)unsignedLength)];
            await _pipe.ReadExactlyAsync(request, cancellationToken)
                .ConfigureAwait(false);
            SetupElevationIncomingMessage incoming = new(key, request);
            key = [];
            return incoming;
        }
        finally
        {
            if (key.Length != 0)
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    public async Task SendResultAsync(
        ReadOnlyMemory<byte> authenticatedResult,
        CancellationToken cancellationToken)
    {
        if (authenticatedResult.Length is <= 0 or > MaximumMessageLength)
        {
            throw new ArgumentException(
                "The authenticated helper result is outside its bounds.",
                nameof(authenticatedResult));
        }
        byte[] length = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(
            length,
            checked((uint)authenticatedResult.Length));
        await _pipe.WriteAsync(length, cancellationToken).ConfigureAwait(false);
        await _pipe.WriteAsync(authenticatedResult, cancellationToken)
            .ConfigureAwait(false);
        await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<byte[]> ReceiveFinalizationAsync(
        CancellationToken cancellationToken) =>
        ReadMessageAsync("invalidFinalizationLength", cancellationToken);

    public Task SendFinalizationResultAsync(
        ReadOnlyMemory<byte> authenticatedResult,
        CancellationToken cancellationToken) =>
        WriteMessageAsync(authenticatedResult, cancellationToken);

    private async Task<byte[]> ReadMessageAsync(
        string failureCode,
        CancellationToken cancellationToken)
    {
        byte[] length = new byte[sizeof(uint)];
        await _pipe.ReadExactlyAsync(length, cancellationToken).ConfigureAwait(false);
        uint unsignedLength = BinaryPrimitives.ReadUInt32LittleEndian(length);
        if (unsignedLength is 0 or > MaximumMessageLength)
        {
            throw new SetupElevationProtocolException(failureCode);
        }
        byte[] message = new byte[checked((int)unsignedLength)];
        await _pipe.ReadExactlyAsync(message, cancellationToken).ConfigureAwait(false);
        return message;
    }

    private async Task WriteMessageAsync(
        ReadOnlyMemory<byte> message,
        CancellationToken cancellationToken)
    {
        if (message.Length is <= 0 or > MaximumMessageLength)
        {
            throw new ArgumentException("The elevation message is outside its bounds.");
        }
        byte[] length = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(
            length,
            checked((uint)message.Length));
        await _pipe.WriteAsync(length, cancellationToken).ConfigureAwait(false);
        await _pipe.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _pipe.DisposeAsync();

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetNamedPipeServerProcessId",
        SetLastError = true,
        ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(
        SafePipeHandle pipe,
        out uint serverProcessId);
}

internal sealed class WindowsOneShotElevationChannel(
    NamedPipeServerStream pipe) : IOneShotElevationChannel
{
    private const int MaximumMessageLength = 65536;
    private readonly NamedPipeServerStream _pipe = pipe
        ?? throw new ArgumentNullException(nameof(pipe));

    public Task WaitForConnectionAsync(CancellationToken cancellationToken) =>
        _pipe.WaitForConnectionAsync(cancellationToken);

    public int GetClientProcessId()
    {
        if (!GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out uint processId)
            || processId > int.MaxValue)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        return checked((int)processId);
    }

    public async Task<byte[]> ExchangeAsync(
        ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> authenticatedRequest,
        CancellationToken cancellationToken)
    {
        if (key.Length != 32
            || authenticatedRequest.Length is <= 0 or > MaximumMessageLength)
        {
            throw new ArgumentException("The elevation message is outside its bounds.");
        }
        await _pipe.WriteAsync(key, cancellationToken).ConfigureAwait(false);
        byte[] length = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(
            length,
            checked((uint)authenticatedRequest.Length));
        await _pipe.WriteAsync(length, cancellationToken).ConfigureAwait(false);
        await _pipe.WriteAsync(authenticatedRequest, cancellationToken)
            .ConfigureAwait(false);
        await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);

        await _pipe.ReadExactlyAsync(length, cancellationToken).ConfigureAwait(false);
        uint unsignedLength = BinaryPrimitives.ReadUInt32LittleEndian(length);
        if (unsignedLength is 0 or > MaximumMessageLength)
        {
            throw new SetupElevationProtocolException("invalidResultLength");
        }
        byte[] result = new byte[checked((int)unsignedLength)];
        await _pipe.ReadExactlyAsync(result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<byte[]> FinalizeAsync(
        ReadOnlyMemory<byte> authenticatedFinalization,
        CancellationToken cancellationToken)
    {
        if (authenticatedFinalization.Length is <= 0 or > MaximumMessageLength)
        {
            throw new ArgumentException(
                "The authenticated finalization is outside its bounds.",
                nameof(authenticatedFinalization));
        }
        byte[] length = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(
            length,
            checked((uint)authenticatedFinalization.Length));
        await _pipe.WriteAsync(length, cancellationToken).ConfigureAwait(false);
        await _pipe.WriteAsync(authenticatedFinalization, cancellationToken)
            .ConfigureAwait(false);
        await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        await _pipe.ReadExactlyAsync(length, cancellationToken).ConfigureAwait(false);
        uint unsignedLength = BinaryPrimitives.ReadUInt32LittleEndian(length);
        if (unsignedLength is 0 or > MaximumMessageLength)
        {
            throw new SetupElevationProtocolException(
                "invalidFinalizationResultLength");
        }
        byte[] result = new byte[checked((int)unsignedLength)];
        await _pipe.ReadExactlyAsync(result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public ValueTask DisposeAsync() => _pipe.DisposeAsync();

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetNamedPipeClientProcessId",
        SetLastError = true,
        ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out uint clientProcessId);
}

internal sealed class WindowsSetupProcessImageProbe : ISetupProcessImageProbe
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int MaximumPathLength = 32768;

    private WindowsSetupProcessImageProbe()
    {
    }

    public static WindowsSetupProcessImageProbe Instance { get; } = new();

    public SetupExecutableIdentity GetCurrentIdentity() =>
        GetProcessIdentity(Environment.ProcessId);

    public SetupExecutableIdentity GetProcessIdentity(int processId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        using SafeProcessHandle process = OpenProcess(
            ProcessQueryLimitedInformation,
            inheritHandle: false,
            checked((uint)processId));
        if (process.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        char[] path = new char[MaximumPathLength];
        uint pathLength = checked((uint)path.Length);
        if (!QueryFullProcessImageName(
                process,
                flags: 0,
                path,
                ref pathLength))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        string fullPath = new(path, 0, checked((int)pathLength));
        using SafeFileHandle image = File.OpenHandle(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            FileOptions.None);
        if (!GetFileInformationByHandle(image, out ByHandleFileInformation information))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        return new SetupExecutableIdentity(
            fullPath,
            information.VolumeSerialNumber,
            information.FileIndexHigh,
            information.FileIndexLow);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "OpenProcess",
        SetLastError = true,
        ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "QueryFullProcessImageNameW",
        SetLastError = true,
        CharSet = CharSet.Unicode,
        ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        [Out] char[] executableName,
        ref uint size);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFileInformationByHandle",
        SetLastError = true,
        ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);
}

internal sealed class WindowsSetupElevatedProcessStarter
    : ISetupElevatedProcessStarter
{
    private WindowsSetupElevatedProcessStarter()
    {
    }

    public static WindowsSetupElevatedProcessStarter Instance { get; } = new();

    public ISetupElevatedProcess Start(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The elevated helper did not start.");
        return new WindowsSetupElevatedProcess(process);
    }
}

internal sealed class WindowsSetupElevatedProcess(Process process)
    : ISetupElevatedProcess
{
    private readonly Process _process = process
        ?? throw new ArgumentNullException(nameof(process));

    public int Id => _process.Id;

    public Task WaitForExitAsync(CancellationToken cancellationToken) =>
        _process.WaitForExitAsync(cancellationToken);

#pragma warning disable CA1031 // Termination is best-effort on an already failing boundary.
    public void TryTerminate()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
        }
    }
#pragma warning restore CA1031

    public void Dispose() => _process.Dispose();
}
