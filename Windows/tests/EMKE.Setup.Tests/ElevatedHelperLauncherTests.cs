using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using EMKE.Setup.Elevated;

namespace EMKE.Setup.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.
#pragma warning disable CA2007 // MSTest owns the test synchronization context.
#pragma warning disable CA2000 // Session/launcher wrappers own the fake channels.

[TestClass]
public sealed class ElevatedHelperLauncherTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);
    private static readonly SetupExecutableIdentity SetupIdentity = new(
        "C:\\Program Files\\EMKE\\EMKE-Translation-Setup.exe",
        0x11223344,
        0x55667788,
        0x99aabbcc);

    [TestMethod]
    public async Task SuccessfulLaunchUsesOnlyTheConstrainedRunAsContract()
    {
        LauncherFixture fixture = new();

        SetupElevationLaunchResult result = await fixture.Launcher.LaunchAsync(
            fixture.Request,
            CancellationToken.None);

        Assert.AreEqual(SetupElevationLaunchOutcome.Succeeded, result.Outcome);
        ProcessStartInfo startInfo = fixture.ProcessStarter.StartInfo
            ?? throw new InvalidOperationException("The helper was not launched.");
        Assert.AreEqual(SetupIdentity.FullPath, startInfo.FileName);
        Assert.AreEqual("runas", startInfo.Verb);
        Assert.IsTrue(startInfo.UseShellExecute);
        CollectionAssert.AreEqual(
            new[]
            {
                SetupElevatedHelperArguments.FixedSwitch,
                fixture.SecretSource.PipeName,
                fixture.Request.Nonce,
            },
            startInfo.ArgumentList.ToArray());
        Assert.IsTrue(fixture.Channel.ExchangeCalled);
        Assert.IsTrue(fixture.Process.WaitForExitCalled);
        Assert.IsFalse(fixture.Process.TerminateCalled);
    }

    [TestMethod]
    public async Task PreparedMachineChangesStayInOneUacSessionUntilRollback()
    {
        LauncherFixture fixture = new();
        fixture.Channel.CreateResult = (key, _) =>
            SetupElevationResultCodec.EncodeAuthenticated(
                new SetupElevatedHelperResult(
                    fixture.Request.TransactionId,
                    fixture.Request.Nonce,
                    SetupElevatedHelperOutcome.Succeeded,
                    certificateCreated: true,
                    driverPackageCreated: true,
                    driverDeviceCreated: true),
                key.Span);

        SetupElevationPreparationResult prepared =
            await fixture.Launcher.PrepareAsync(
                fixture.Request,
                CancellationToken.None);

        Assert.IsNotNull(prepared.Session);
        Assert.IsNotNull(prepared.Receipt);
        Assert.AreEqual(
            new SetupMachineCreatedState(true, true, true),
            prepared.Receipt.CreatedState);
        Assert.IsFalse(fixture.Channel.FinalizeCalled);
        Assert.IsFalse(fixture.Process.WaitForExitCalled);

        await using SetupElevationPreparedSession session = prepared.Session;
        Assert.IsTrue(await session.RollbackAsync(CancellationToken.None));
        Assert.IsTrue(fixture.Channel.FinalizeCalled);
        Assert.IsTrue(fixture.Process.WaitForExitCalled);
        Assert.IsTrue(fixture.SecretSource.MacKey.All(static value => value == 0));
    }

    [TestMethod]
    public async Task RequestAndMacKeyNeverEnterDiskArgumentsOrEnvironmentAndKeyIsCleared()
    {
        string absentRoot = Path.Combine(
            Path.GetTempPath(),
            "EMKE.Setup.LeakAudit",
            Guid.NewGuid().ToString("N"));
        SetupElevationRequest request = CreateRequest(absentRoot);
        LauncherFixture fixture = new(request: request);
        string keyHex = Convert.ToHexString(fixture.SecretSource.MacKey);
        Assert.IsFalse(Directory.Exists(absentRoot));

        _ = await fixture.Launcher.LaunchAsync(
            fixture.Request,
            CancellationToken.None);

        ProcessStartInfo startInfo = fixture.ProcessStarter.StartInfo!;
        string arguments = string.Join('|', startInfo.ArgumentList);
        Assert.IsFalse(arguments.Contains(keyHex, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(arguments.Contains(
            fixture.Request.ManifestSha256,
            StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(arguments.Contains(
            fixture.Request.TransactionId.ToString("D"),
            StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(startInfo.Environment.Values.Any(value =>
            value is not null
            && (value.Contains(keyHex, StringComparison.OrdinalIgnoreCase)
                || value.Contains(
                    fixture.Request.ManifestSha256,
                    StringComparison.OrdinalIgnoreCase))));
        Assert.IsFalse(Directory.Exists(absentRoot));
        Assert.IsTrue(fixture.SecretSource.MacKey.All(static value => value == 0));
    }

    [TestMethod]
    public async Task HelperPidMismatchRejectsBeforeSendingTheRequestOrKey()
    {
        LauncherFixture fixture = new();
        fixture.Channel.ClientProcessId = fixture.Process.Id + 1;

        SetupElevationLaunchResult result = await fixture.Launcher.LaunchAsync(
            fixture.Request,
            CancellationToken.None);

        Assert.AreEqual(SetupElevationLaunchOutcome.Rejected, result.Outcome);
        Assert.AreEqual("helperPidMismatch", result.FailureCode);
        Assert.IsFalse(fixture.Channel.ExchangeCalled);
        Assert.IsFalse(fixture.SecretSource.KeyCreated);
        Assert.IsTrue(fixture.Process.TerminateCalled);
    }

    [TestMethod]
    public async Task DifferentHelperImageRejectsBeforeSendingTheRequestOrKey()
    {
        LauncherFixture fixture = new();
        fixture.ImageProbe.PeerIdentity = new SetupExecutableIdentity(
            "C:\\untrusted\\helper.exe",
            1,
            2,
            3);

        SetupElevationLaunchResult result = await fixture.Launcher.LaunchAsync(
            fixture.Request,
            CancellationToken.None);

        Assert.AreEqual(SetupElevationLaunchOutcome.Rejected, result.Outcome);
        Assert.AreEqual("helperImageMismatch", result.FailureCode);
        Assert.IsFalse(fixture.Channel.ExchangeCalled);
        Assert.IsFalse(fixture.SecretSource.KeyCreated);
        Assert.IsTrue(fixture.Process.TerminateCalled);
    }

    [TestMethod]
    public async Task UacCancellationReturnsCancelledWithoutCreatingAKey()
    {
        LauncherFixture fixture = new();
        fixture.ProcessStarter.StartException = new Win32Exception(1223);

        SetupElevationLaunchResult result = await fixture.Launcher.LaunchAsync(
            fixture.Request,
            CancellationToken.None);

        Assert.AreEqual(SetupElevationLaunchOutcome.UacCancelled, result.Outcome);
        Assert.AreEqual("uacCancelled", result.FailureCode);
        Assert.IsFalse(fixture.SecretSource.KeyCreated);
        Assert.IsFalse(fixture.Channel.ExchangeCalled);
    }

    [TestMethod]
    public async Task HelperConnectionTimeoutIsBoundedAndTerminatesTheProcess()
    {
        LauncherFixture fixture = new(timeout: TimeSpan.FromMilliseconds(25));
        fixture.Channel.WaitUntilCancelled = true;

        SetupElevationLaunchResult result = await fixture.Launcher.LaunchAsync(
            fixture.Request,
            CancellationToken.None);

        Assert.AreEqual(SetupElevationLaunchOutcome.TimedOut, result.Outcome);
        Assert.AreEqual("helperTimedOut", result.FailureCode);
        Assert.IsTrue(fixture.Process.TerminateCalled);
        Assert.IsFalse(fixture.SecretSource.KeyCreated);
    }

    [TestMethod]
    public async Task ChangedResultTransactionIdIsRejected()
    {
        LauncherFixture fixture = new();
        fixture.Channel.CreateResult = (key, _) =>
            SetupElevationResultCodec.EncodeAuthenticated(
                new SetupElevatedHelperResult(
                    Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                    fixture.Request.Nonce,
                    SetupElevatedHelperOutcome.Succeeded),
                key.Span);

        SetupElevationLaunchResult result = await fixture.Launcher.LaunchAsync(
            fixture.Request,
            CancellationToken.None);

        Assert.AreEqual(SetupElevationLaunchOutcome.Rejected, result.Outcome);
        Assert.AreEqual("resultTransactionMismatch", result.FailureCode);
        Assert.IsTrue(fixture.SecretSource.MacKey.All(static value => value == 0));
    }

    [TestMethod]
    public async Task ChangedResultNonceIsRejected()
    {
        LauncherFixture fixture = new();
        fixture.Channel.CreateResult = (key, _) =>
            SetupElevationResultCodec.EncodeAuthenticated(
                new SetupElevatedHelperResult(
                    fixture.Request.TransactionId,
                    DifferentNonce,
                    SetupElevatedHelperOutcome.Succeeded),
                key.Span);

        SetupElevationLaunchResult result = await fixture.Launcher.LaunchAsync(
            fixture.Request,
            CancellationToken.None);

        Assert.AreEqual(SetupElevationLaunchOutcome.Rejected, result.Outcome);
        Assert.AreEqual("resultNonceMismatch", result.FailureCode);
        Assert.IsTrue(fixture.SecretSource.MacKey.All(static value => value == 0));
    }

    [TestMethod]
    public async Task ChangedResultMacIsRejected()
    {
        LauncherFixture fixture = new();
        fixture.Channel.CreateResult = (key, _) =>
        {
            byte[] result = SetupElevationResultCodec.EncodeAuthenticated(
                new SetupElevatedHelperResult(
                    fixture.Request.TransactionId,
                    fixture.Request.Nonce,
                    SetupElevatedHelperOutcome.Succeeded),
                key.Span);
            result[^1] ^= 0x01;
            return result;
        };

        SetupElevationLaunchResult launchResult =
            await fixture.Launcher.LaunchAsync(
                fixture.Request,
                CancellationToken.None);

        Assert.AreEqual(
            SetupElevationLaunchOutcome.Rejected,
            launchResult.Outcome);
        Assert.AreEqual("resultAuthenticationFailed", launchResult.FailureCode);
        Assert.IsTrue(fixture.SecretSource.MacKey.All(static value => value == 0));
    }

    [TestMethod]
    public async Task ChangedFinalizationAcknowledgementMacIsRejectedAndTerminated()
    {
        LauncherFixture fixture = new();
        Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>, byte[]> original =
            fixture.Channel.CreateFinalizationResult;
        fixture.Channel.CreateFinalizationResult = (key, message) =>
        {
            byte[] result = original(key, message);
            result[^1] ^= 0x01;
            return result;
        };

        SetupElevationLaunchResult result = await fixture.Launcher.LaunchAsync(
            fixture.Request,
            CancellationToken.None);

        Assert.AreEqual(SetupElevationLaunchOutcome.Rejected, result.Outcome);
        Assert.AreEqual("helperCommitRejected", result.FailureCode);
        Assert.IsTrue(fixture.Process.TerminateCalled);
        Assert.IsTrue(fixture.SecretSource.MacKey.All(static value => value == 0));
    }

    [TestMethod]
    public void ChangedSwitchOrExtraArgumentIsNotAnElevatedHelperCommand()
    {
        Assert.IsFalse(SetupElevatedHelperArguments.TryParse(
            ["--install", ValidPipeName, Nonce],
            out _));
        Assert.IsFalse(SetupElevatedHelperArguments.TryParse(
            [SetupElevatedHelperArguments.FixedSwitch, ValidPipeName, Nonce, "C:\\extra"],
            out _));
        Assert.IsFalse(SetupElevatedHelperArguments.TryParse(
            [SetupElevatedHelperArguments.FixedSwitch, "C:\\payload", Nonce],
            out _));
    }

    [TestMethod]
    public void ExactHelperCommandParsesOnlyPipeNameAndNonce()
    {
        bool parsed = SetupElevatedHelperArguments.TryParse(
            [SetupElevatedHelperArguments.FixedSwitch, ValidPipeName, Nonce],
            out SetupElevatedHelperArguments? arguments);

        Assert.IsTrue(parsed);
        Assert.IsNotNull(arguments);
        Assert.AreEqual(ValidPipeName, arguments.PipeName);
        Assert.AreEqual(Nonce, arguments.Nonce);
    }

    [TestMethod]
    public void PipeAclAllowsOnlyInvokerAdministratorsAndSystem()
    {
        SecurityIdentifier invokingSid = new(
            WellKnownSidType.BuiltinUsersSid,
            domainSid: null);

        PipeSecurity security = WindowsOneShotElevationChannelFactory
            .CreatePipeSecurity(invokingSid);
        AuthorizationRuleCollection rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: false,
            typeof(SecurityIdentifier));
        SecurityIdentifier[] allowed = rules.Cast<PipeAccessRule>()
            .Where(static rule => rule.AccessControlType == AccessControlType.Allow)
            .Select(static rule => (SecurityIdentifier)rule.IdentityReference)
            .ToArray();

        CollectionAssert.AreEquivalent(
            new[]
            {
                invokingSid,
                new SecurityIdentifier(
                    WellKnownSidType.BuiltinAdministratorsSid,
                    domainSid: null),
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, domainSid: null),
            },
            allowed);
        Assert.IsFalse(rules.Cast<PipeAccessRule>().Any(static rule =>
            rule.AccessControlType == AccessControlType.Deny));
    }

    [TestMethod]
    public async Task ProductionPipeBindsTheConnectedClientProcessId()
    {
        SecurityIdentifier invokingSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current SID is unavailable.");
        string pipeName = new CryptographicSetupElevationSecretSource()
            .CreatePipeName();
        await using IOneShotElevationChannel channel =
            new WindowsOneShotElevationChannelFactory().Create(
                pipeName,
                invokingSid);
        using NamedPipeClientStream client = new(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        Task wait = channel.WaitForConnectionAsync(CancellationToken.None);

        await client.ConnectAsync(CancellationToken.None);
        await wait;

        Assert.AreEqual(Environment.ProcessId, channel.GetClientProcessId());
    }

    [TestMethod]
    public void ProductionImageProbeBindsCurrentPidToTheSameFileIdentity()
    {
        WindowsSetupProcessImageProbe probe = WindowsSetupProcessImageProbe.Instance;

        SetupExecutableIdentity current = probe.GetCurrentIdentity();
        SetupExecutableIdentity byPid = probe.GetProcessIdentity(Environment.ProcessId);

        Assert.AreEqual(current, byPid);
    }

    [TestMethod]
    public async Task HelperRejectsDifferentParentImageBeforeReceivingSecrets()
    {
        FakeElevationClientChannel client = new();
        FakeProcessImageProbe imageProbe = new(SetupIdentity)
        {
            PeerIdentity = new SetupExecutableIdentity(
                "C:\\untrusted\\parent.exe",
                1,
                2,
                3),
        };
        ElevatedHelperSession session = CreateHelperSession(client, imageProbe);
        RecordingRequestHandler handler = new();

        SetupElevatedHelperSessionResult result = await session.RunAsync(
            HelperArguments(Nonce),
            handler,
            CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("parentImageMismatch", result.FailureCode);
        Assert.IsFalse(client.ReceiveCalled);
        Assert.IsFalse(handler.Called);
    }

    [TestMethod]
    public async Task HelperAuthenticatesTypedRequestReturnsMacAndClearsKey()
    {
        SetupElevationRequest request = CreateRequest();
        byte[] key = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();
        FakeElevationClientChannel client = new()
        {
            Key = key,
            AuthenticatedRequest = SetupElevationRequestCodec.EncodeAuthenticated(
                request,
                key),
            AuthenticatedFinalization =
                SetupElevationFinalizationCodec.EncodeAuthenticated(
                    request.TransactionId,
                    request.Nonce,
                    SetupElevationFinalizationAction.Commit,
                    succeeded: false,
                    key),
        };
        ElevatedHelperSession session = CreateHelperSession(
            client,
            new FakeProcessImageProbe(SetupIdentity));
        RecordingRequestHandler handler = new();

        SetupElevatedHelperSessionResult result = await session.RunAsync(
            HelperArguments(request.Nonce),
            handler,
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded, result.FailureCode);
        Assert.IsTrue(handler.Called);
        Assert.AreEqual(request, handler.Request);
        Assert.IsNotNull(client.AuthenticatedResult);
        SetupElevatedHelperResult helperResult =
            SetupElevationResultCodec.DecodeAuthenticated(
                client.AuthenticatedResult,
                Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray(),
                request.TransactionId,
                request.Nonce);
        Assert.AreEqual(SetupElevatedHelperOutcome.Succeeded, helperResult.Outcome);
        Assert.IsTrue(key.All(static value => value == 0));
    }

    [TestMethod]
    public async Task HelperRejectsCommandNonceMismatchWithoutCallingHandlerAndClearsKey()
    {
        SetupElevationRequest request = CreateRequest();
        byte[] key = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();
        FakeElevationClientChannel client = new()
        {
            Key = key,
            AuthenticatedRequest = SetupElevationRequestCodec.EncodeAuthenticated(
                request,
                key),
        };
        ElevatedHelperSession session = CreateHelperSession(
            client,
            new FakeProcessImageProbe(SetupIdentity));
        RecordingRequestHandler handler = new();

        SetupElevatedHelperSessionResult result = await session.RunAsync(
            HelperArguments(DifferentNonce),
            handler,
            CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("helperNonceMismatch", result.FailureCode);
        Assert.IsFalse(handler.Called);
        Assert.IsTrue(key.All(static value => value == 0));
    }

    [TestMethod]
    public async Task HelperRollsBackPreparedStateWhenFinalizationIsTampered()
    {
        SetupElevationRequest request = CreateRequest();
        byte[] key = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();
        FakeElevationClientChannel client = new()
        {
            Key = key,
            AuthenticatedRequest = SetupElevationRequestCodec.EncodeAuthenticated(
                request,
                key),
            AuthenticatedFinalization = [1, 2, 3],
        };
        ElevatedHelperSession session = CreateHelperSession(
            client,
            new FakeProcessImageProbe(SetupIdentity));
        RecordingRequestHandler handler = new();

        SetupElevatedHelperSessionResult result = await session.RunAsync(
            HelperArguments(request.Nonce),
            handler,
            CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("finalizationAuthenticationFailed", result.FailureCode);
        Assert.AreEqual(
            SetupElevationFinalizationAction.Rollback,
            handler.FinalizationAction);
        Assert.IsTrue(key.All(static value => value == 0));
    }

    [TestMethod]
    public async Task ProductionClientBindsTheConnectedServerProcessId()
    {
        SecurityIdentifier invokingSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current SID is unavailable.");
        string pipeName = new CryptographicSetupElevationSecretSource()
            .CreatePipeName();
        await using IOneShotElevationChannel server =
            new WindowsOneShotElevationChannelFactory().Create(pipeName, invokingSid);
        await using IOneShotElevationClientChannel client =
            new WindowsOneShotElevationClientChannelFactory().Create(pipeName);
        Task serverWait = server.WaitForConnectionAsync(CancellationToken.None);

        await client.ConnectAsync(CancellationToken.None);
        await serverWait;

        Assert.AreEqual(Environment.ProcessId, client.GetServerProcessId());
    }

    private static ElevatedHelperSession CreateHelperSession(
        FakeElevationClientChannel client,
        ISetupProcessImageProbe imageProbe)
    {
        return new ElevatedHelperSession(
            new FakeClientChannelFactory(client),
            imageProbe,
            new FixedTimeProvider(Now),
            new SetupElevationReplayGuard(),
            TimeSpan.FromSeconds(2));
    }

    private static SetupElevatedHelperArguments HelperArguments(string nonce)
    {
        Assert.IsTrue(SetupElevatedHelperArguments.TryParse(
            [SetupElevatedHelperArguments.FixedSwitch, ValidPipeName, nonce],
            out SetupElevatedHelperArguments? arguments));
        return arguments
            ?? throw new InvalidOperationException("Helper arguments were not parsed.");
    }

    private sealed class LauncherFixture
    {
        public LauncherFixture(
            TimeSpan? timeout = null,
            SetupElevationRequest? request = null)
        {
            Request = request ?? CreateRequest();
            Process = new FakeElevatedProcess(4242);
            ProcessStarter = new FakeProcessStarter(Process);
            ImageProbe = new FakeProcessImageProbe(SetupIdentity);
            SecretSource = new FakeSecretSource();
            Channel = new FakeElevationChannel
            {
                ClientProcessId = Process.Id,
                CreateResult = (key, _) =>
                    SetupElevationResultCodec.EncodeAuthenticated(
                        new SetupElevatedHelperResult(
                            Request.TransactionId,
                            Request.Nonce,
                            SetupElevatedHelperOutcome.Succeeded),
                        key.Span),
                CreateFinalizationResult = (key, message) =>
                {
                    (SetupElevationFinalizationAction action, _) =
                        SetupElevationFinalizationCodec.DecodeAuthenticated(
                            message.Span,
                            key.Span,
                            Request.TransactionId,
                            Request.Nonce);
                    return SetupElevationFinalizationCodec.EncodeAuthenticated(
                        Request.TransactionId,
                        Request.Nonce,
                        action,
                        succeeded: true,
                        key.Span);
                },
            };
            Launcher = new ElevatedHelperLauncher(
                ProcessStarter,
                new FakeChannelFactory(Channel),
                ImageProbe,
                SecretSource,
                new FixedTimeProvider(Now),
                timeout ?? TimeSpan.FromSeconds(2),
                new SecurityIdentifier(
                    WellKnownSidType.BuiltinUsersSid,
                    domainSid: null));
        }

        public SetupElevationRequest Request { get; }

        public FakeElevatedProcess Process { get; }

        public FakeProcessStarter ProcessStarter { get; }

        public FakeProcessImageProbe ImageProbe { get; }

        public FakeSecretSource SecretSource { get; }

        public FakeElevationChannel Channel { get; }

        public ElevatedHelperLauncher Launcher { get; }
    }

    private sealed class FakeProcessStarter(FakeElevatedProcess process)
        : ISetupElevatedProcessStarter
    {
        public ProcessStartInfo? StartInfo { get; private set; }

        public Exception? StartException { get; set; }

        public ISetupElevatedProcess Start(ProcessStartInfo startInfo)
        {
            StartInfo = startInfo;
            if (StartException is not null)
            {
                throw StartException;
            }
            return process;
        }
    }

    private sealed class FakeElevatedProcess(int id) : ISetupElevatedProcess
    {
        public int Id { get; } = id;

        public bool WaitForExitCalled { get; private set; }

        public bool TerminateCalled { get; private set; }

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WaitForExitCalled = true;
            return Task.CompletedTask;
        }

        public void TryTerminate() => TerminateCalled = true;

        public void Dispose()
        {
        }
    }

    private sealed class FakeChannelFactory(FakeElevationChannel channel)
        : IOneShotElevationChannelFactory
    {
        public IOneShotElevationChannel Create(
            string pipeName,
            SecurityIdentifier invokingSid)
        {
            Assert.AreEqual(ValidPipeName, pipeName);
            Assert.IsNotNull(invokingSid);
            return channel;
        }
    }

    private sealed class FakeElevationChannel : IOneShotElevationChannel
    {
        public int ClientProcessId { get; set; }

        public bool WaitUntilCancelled { get; set; }

        public bool ExchangeCalled { get; private set; }

        public bool FinalizeCalled { get; private set; }

        public Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>, byte[]> CreateResult
        {
            get;
            set;
        } = null!;

        public Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>, byte[]>
            CreateFinalizationResult
        {
            get;
            set;
        } = null!;

        public async Task WaitForConnectionAsync(CancellationToken cancellationToken)
        {
            if (WaitUntilCancelled)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }

        public int GetClientProcessId() => ClientProcessId;

        public Task<byte[]> ExchangeAsync(
            ReadOnlyMemory<byte> key,
            ReadOnlyMemory<byte> authenticatedRequest,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExchangeCalled = true;
            return Task.FromResult(CreateResult(key, authenticatedRequest));
        }

        public Task<byte[]> FinalizeAsync(
            ReadOnlyMemory<byte> authenticatedFinalization,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FinalizeCalled = true;
            return Task.FromResult(CreateFinalizationResult(
                Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray(),
                authenticatedFinalization));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeProcessImageProbe : ISetupProcessImageProbe
    {
        private readonly SetupExecutableIdentity _current;

        public FakeProcessImageProbe(SetupExecutableIdentity current)
        {
            _current = current;
            PeerIdentity = current;
        }

        public SetupExecutableIdentity PeerIdentity { get; set; }

        public SetupExecutableIdentity GetCurrentIdentity() => _current;

        public SetupExecutableIdentity GetProcessIdentity(int processId)
        {
            Assert.IsGreaterThan(0, processId);
            return PeerIdentity;
        }
    }

    private sealed class FakeSecretSource : ISetupElevationSecretSource
    {
        public string PipeName { get; } = ValidPipeName;

        public byte[] MacKey { get; } = Enumerable.Range(1, 32)
            .Select(static value => (byte)value)
            .ToArray();

        public bool KeyCreated { get; private set; }

        public string CreatePipeName() => PipeName;

        public byte[] CreateMacKey()
        {
            KeyCreated = true;
            return MacKey;
        }
    }

    private sealed class FakeClientChannelFactory(FakeElevationClientChannel client)
        : IOneShotElevationClientChannelFactory
    {
        public IOneShotElevationClientChannel Create(string pipeName)
        {
            Assert.AreEqual(ValidPipeName, pipeName);
            return client;
        }
    }

    private sealed class FakeElevationClientChannel
        : IOneShotElevationClientChannel
    {
        public int ServerProcessId { get; set; } = 31337;

        public byte[] Key { get; set; } = new byte[32];

        public byte[] AuthenticatedRequest { get; set; } = [];

        public byte[]? AuthenticatedResult { get; private set; }

        public byte[] AuthenticatedFinalization { get; set; } = [];

        public byte[]? AuthenticatedFinalizationResult { get; private set; }

        public bool ReceiveCalled { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public int GetServerProcessId() => ServerProcessId;

        public Task<SetupElevationIncomingMessage> ReceiveAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReceiveCalled = true;
            return Task.FromResult(new SetupElevationIncomingMessage(
                Key,
                AuthenticatedRequest));
        }

        public Task SendResultAsync(
            ReadOnlyMemory<byte> authenticatedResult,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AuthenticatedResult = authenticatedResult.ToArray();
            return Task.CompletedTask;
        }

        public Task<byte[]> ReceiveFinalizationAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(AuthenticatedFinalization);
        }

        public Task SendFinalizationResultAsync(
            ReadOnlyMemory<byte> authenticatedResult,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AuthenticatedFinalizationResult = authenticatedResult.ToArray();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingRequestHandler : ISetupElevatedRequestHandler
    {
        public bool Called { get; private set; }

        public SetupElevationRequest? Request { get; private set; }

        public SetupElevationFinalizationAction? FinalizationAction
        {
            get;
            private set;
        }

        public Task<SetupElevatedHelperOutcome> HandleAsync(
            SetupElevationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Called = true;
            Request = request;
            return Task.FromResult(SetupElevatedHelperOutcome.Succeeded);
        }

        public Task<bool> FinalizeAsync(
            SetupElevatedPreparedChange prepared,
            SetupElevationFinalizationAction action,
            Guid transactionId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FinalizationAction = action;
            return Task.FromResult(true);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static SetupElevationRequest CreateRequest(string? extractionRoot = null)
    {
        return new SetupElevationRequest(
            "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff",
            new Guid("00112233-4455-6677-8899-aabbccddeeff"),
            new SetupExtractionRootIdentity(
                extractionRoot
                    ?? "C:\\ProgramData\\EMKE\\Setup\\0.2.0-00112233445566778899aabbccddeeff",
                0x11223344,
                0x55667788,
                0x99aabbcc,
                0x10),
            Now.AddMinutes(1),
            Nonce,
            "33E9992B08919BA6522F8A16B95CC2AA5DA6BB98",
            "ROOT\\EMKEVIRTUALAUDIO",
            new Version(1, 0, 0, 2),
            new SetupElevationPayloadHashes(
                new string('1', 64),
                new string('2', 64),
                new string('3', 64),
                new string('4', 64),
                new string('5', 64)));
    }

    private const string ValidPipeName =
        "emke-setup-00112233445566778899aabbccddeeff";
    private const string Nonce =
        "102132435465768798a9bacbdcedfe0f102132435465768798a9bacbdcedfe0f";
    private const string DifferentNonce =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
}

#pragma warning restore CA1515
#pragma warning restore CA2007
#pragma warning restore CA2000
