using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using EMKE.Setup.Elevated;
using EMKE.Setup.Platform;

namespace EMKE.Setup;

internal static class Program
{
    private const string VerifySelfSwitch = "--verify-self-v1";

#pragma warning disable CA1031 // Main is the final fail-closed process boundary.
    public static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        try
        {
            return MainAsync(args).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            Console.Error.WriteLine("EMKE Setup failed closed.");
            return 1;
        }
    }
#pragma warning restore CA1031

    private static async Task<int> MainAsync(string[] args)
    {
        if (SetupElevatedHelperArguments.TryParse(
                args,
                out SetupElevatedHelperArguments? helperArguments))
        {
            SetupElevatedHelperSessionResult helper =
                await new ElevatedHelperSession().RunAsync(
                    helperArguments!,
                    new ElevatedMachineInstaller(),
                    CancellationToken.None).ConfigureAwait(false);
            return helper.Succeeded ? 0 : 1;
        }
        if (args.Length == 1
            && string.Equals(args[0], VerifySelfSwitch, StringComparison.Ordinal))
        {
            EmbeddedSetupBundle bundle = EmbeddedSetupBundle.LoadFromAssembly(
                Assembly.GetExecutingAssembly());
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new
            {
                status = "verified",
                inventorySha256 = bundle.InventorySha256,
                payloadCount = bundle.Payloads.Count,
            })).ConfigureAwait(false);
            return 0;
        }
        if (args.Length != 0)
        {
            return 2;
        }
        return await RunSetupAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<int> RunSetupAsync(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || WindowsTokenElevation.IsElevated())
        {
            return 1;
        }
        EmbeddedSetupBundle bundle = EmbeddedSetupBundle.LoadFromAssembly(
            Assembly.GetExecutingAssembly());
        SetupPreflightDecision preflight = new SetupPreflight().Evaluate(
            bundle.Manifest);
        if (!preflight.Allowed)
        {
            await Console.Error.WriteLineAsync(preflight.FailureCode)
                .ConfigureAwait(false);
            return 1;
        }
        using SetupPayloadVerificationResult verification =
            new SetupPayloadVerifier().VerifyAndExtract(
                bundle.Manifest,
                bundle.Payloads);
        if (!verification.IsValid || verification.Attempt is null)
        {
            await Console.Error.WriteLineAsync(
                    verification.FailureCode ?? "payloadVerificationFailed")
                .ConfigureAwait(false);
            return 1;
        }
        SetupPayloadVerificationAttempt attempt = verification.Attempt;
        WindowsSetupResumeRecordStore recovery = new();
        IReadOnlyList<Guid> pending = recovery.FindPendingTransactionIds();
        if (pending.Count > 1)
        {
            await Console.Error.WriteLineAsync("multipleRecoveryRecordsRejected")
                .ConfigureAwait(false);
            return 1;
        }
        bool resume = pending.Count == 1;
        Guid transactionId = resume ? pending[0] : Guid.NewGuid();
        SetupOrchestrationRequest request = CreateRequest(
            bundle,
            attempt,
            transactionId);
#pragma warning disable CA2007 // The coordinator owns the live helper session.
        await using ElevatedSetupMachineChangeCoordinator machine = new();
#pragma warning restore CA2007
        SetupOrchestrator orchestrator = new(
            machine,
            new PackageInstaller(),
            new EndpointVerifier(),
            WindowsSetupApplicationLauncher.Instance,
            recovery);
        SetupResult result = resume
            ? await orchestrator.ResumeAsync(request, cancellationToken)
                .ConfigureAwait(false)
            : await orchestrator.ExecuteAsync(request, cancellationToken)
                .ConfigureAwait(false);
        if (result.Outcome == SetupOutcome.Succeeded)
        {
            return 0;
        }
        await Console.Error.WriteLineAsync(
                result.Detail ?? result.Outcome.ToString())
            .ConfigureAwait(false);
        return result.Outcome == SetupOutcome.RebootRequired ? 3010 : 1;
    }

    private static SetupOrchestrationRequest CreateRequest(
        EmbeddedSetupBundle bundle,
        SetupPayloadVerificationAttempt attempt,
        Guid transactionId)
    {
        VerifiedSetupPayload msix = Find(
            attempt.Payloads,
            SetupPayloadKind.Msix);
        VerifiedSetupPayload certificate = Find(
            attempt.Payloads,
            SetupPayloadKind.Certificate);
        SetupElevationPayloadHashes hashes = new(
            msix.Sha256,
            certificate.Sha256,
            Find(attempt.Payloads, SetupPayloadKind.DriverInf).Sha256,
            Find(attempt.Payloads, SetupPayloadKind.DriverSys).Sha256,
            Find(attempt.Payloads, SetupPayloadKind.DriverCatalog).Sha256);
        DateTimeOffset expires = DateTimeOffset.FromUnixTimeSeconds(
            DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeSeconds());
        SetupElevationRequest elevation = new(
            bundle.InventorySha256,
            transactionId,
            WindowsSetupDirectoryIdentity.Read(attempt.RootPath),
            expires,
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
            ReadCertificateSha1(certificate),
            bundle.Manifest.DriverHardwareId,
            bundle.Manifest.DriverVersion,
            hashes);
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string invokingSid = identity.User?.Value
            ?? throw new InvalidOperationException(
                "The invoking Windows SID is unavailable.");
        PackageInstallContract packageContract = new(
            "EMKE.Translation.Internal",
            "EMKE.Translation.Internal_kvab4te83cr7p",
            "EMKE.Translation.Internal_0.2.0.0_x64__kvab4te83cr7p",
            "CN=EMKE Internal Test",
            new Version(0, 2, 0, 0),
            System.Runtime.InteropServices.Architecture.X64);
        return new SetupOrchestrationRequest(
            elevation,
            msix,
            packageContract,
            invokingSid);
    }

    private static VerifiedSetupPayload Find(
        IReadOnlyList<VerifiedSetupPayload> payloads,
        SetupPayloadKind kind) => payloads.Single(
            payload => payload.ManifestPayload.Kind == kind);

    private static string ReadCertificateSha1(
        VerifiedSetupPayload certificate)
    {
        byte[] bytes = new byte[checked((int)certificate.Length)];
        using Stream source = certificate.Lease.OpenReadView();
        source.ReadExactly(bytes);
        using X509Certificate2 parsed = X509CertificateLoader.LoadCertificate(bytes);
        return Convert.ToHexString(parsed.GetCertHash(HashAlgorithmName.SHA1));
    }
}
