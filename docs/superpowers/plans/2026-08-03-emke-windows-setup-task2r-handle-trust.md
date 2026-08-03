# EMKE Windows Setup Task 2R Handle Trust Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the blocked Setup extraction and trust boundary with atomic
directory ownership, handle-bound payload verification, and observable cleanup
without mutating the machine.

**Architecture:** `EMKE.Setup` owns one atomically created extraction-root
handle and one non-reopenable lease per payload. `EMKE.Platform` supplies
handle-oriented WinTrust/catalog adapters while the installed-driver path keeps
its existing restrictive sharing contract. Verification results retain the
attempt lifetime until explicit cleanup and surface every residual outcome.

**Tech Stack:** .NET 10, C# 14, MSTest 4, Windows NT/Win32 P/Invoke,
WinVerifyTrust, CryptCATAdmin, CryptQueryObject, GitHub Actions PowerShell.

## Global Constraints

- Work only in `/Users/hale/Documents/Eager DEV/Emke Translation/.worktrees/setup-task2-preflight` on `codex/evidence-setup-task2-preflight`; do not push `codex/windows-internal-msix` or `main`.
- Design input baseline is commit `a69d44820681bae1fbe11334b9e3d9f3ff87713c`; preserve unrelated product and macOS work.
- Support Windows 10 22H2 build `19045+` and Windows 11 x64; reject Windows Server and non-x64 hosts.
- Keep product version `0.2.0.0`, package family `EMKE.Translation.Internal_kvab4te83cr7p`, Publisher `CN=EMKE Internal Test`, hardware ID `ROOT\EMKEVIRTUALAUDIO`, and driver version `1.0.0.2` exact.
- Task 2R performs no certificate import, driver/device/MSIX installation, elevation, BCD/Secure Boot/test-mode change, or uninstall action.
- Copy, hash, parse, signature verification, catalog membership verification, readonly marking, and deletion use the attempt's held handles or bytes read from those handles; paths are display/SIP metadata only.
- The path-based installed-driver verifier must retain `FileShare.Read | FileShare.Delete`; Setup-specific requirements use a separate handle overload and must never widen that path.
- Treat only `0x800B0109` and `0x800B010A` as chain-only MSIX pre-trust states; digest, signature, signer, Publisher, certificate, catalog, and member failures reject.
- Logs/results expose stable failure codes and logical payload names only; do not expose full local paths, certificate private material, arbitrary native messages, or API keys.
- The repository has no Microsoft-signed EMKE CAT. An inbox Microsoft catalog is native-path evidence, and the Inf2Cat-generated EMKE CAT must remain a negative kernel-trust fixture until Hardware Dev Center returns exact signed bytes.
- Every production behavior follows RED, observed expected failure, minimal GREEN, refactor, focused tests, and a commit. Do not suppress analyzer warnings to obtain GREEN.
- GitHub-hosted Windows Server evidence is not Windows 10/11 client acceptance. Client acceptance remains explicit until exact Windows 10 22H2 and Windows 11 machines run the evidence script.

---

## File structure

- `Windows/src/EMKE.Setup/SetupCleanupOutcome.cs`: immutable cleanup contract.
- `Windows/src/EMKE.Setup/WindowsAtomicSetupDirectoryFactory.cs`: safe base validation plus relative `NtCreateFile` root creation.
- `Windows/src/EMKE.Setup/VerifiedPayloadLease.cs`: payload owner handle, offset-based read view, and exact-handle cleanup.
- `Windows/src/EMKE.Setup/SetupExtractionDirectory.cs`: attempt root and payload orchestration; no WinTrust implementation.
- `Windows/src/EMKE.Setup/SetupPayloadVerifier.cs`: inventory, extraction, signature sequencing, attempt/result ownership.
- `Windows/src/EMKE.Setup/WindowsSetupSignatureProbe.cs`: MSIX Publisher and CER adapters over verified payload leases.
- `Windows/src/EMKE.Platform/Security/WindowsHandleAuthenticodeTrust.cs`: reusable handle-bound WinTrust state and signer extraction.
- `Windows/src/EMKE.Platform/Driver/WindowsHandleCatalogTrustVerifier.cs`: CAT CTL decoding, handle hashing, catalog-member trust, and Microsoft policy.
- `Windows/tests/EMKE.Setup.Tests/TestNativeFileMethods.cs`: Windows-only hard-link test helper.
- `Windows/tests/EMKE.Setup.Tests/WindowsSignedPayloadFixtureTests.cs`: native signed-MSIX/CER evidence selected by environment variables.
- `Windows/tests/EMKE.Integration.Tests/WindowsHandleCatalogTrustTests.cs`: inbox signed-catalog and unsigned-EMKE catalog evidence.
- `Windows/tools/test-setup-task2r-client.ps1`: repeatable Windows 10/11 client evidence command with bounded JSON output.
- `.github/workflows/windows-internal-msix.yml`: managed Task 2R and signed MSIX gates.
- `.github/workflows/windows-audio.yml`: unsigned EMKE catalog membership and negative kernel-trust gate.

### Task 1: Restore the Setup test baseline

**Files:**
- Create: `Windows/tests/EMKE.Setup.Tests/TestNativeFileMethods.cs`
- Modify: `Windows/tests/EMKE.Setup.Tests/SetupExtractionDirectoryTests.cs:20-140`
- Modify: `Windows/tests/EMKE.Setup.Tests/SetupPayloadVerifierTests.cs:10-380`
- Modify: `Windows/tests/EMKE.Setup.Tests/SetupPreflightTests.cs:1-80`

**Interfaces:**
- Consumes: existing Setup tests and MSTest 4.0.2.
- Produces: `TestNativeFileMethods.CreateHardLink(string linkPath, string existingPath)` and a warning-free, runnable `EMKE.Setup.Tests` baseline.

- [ ] **Step 1: Reproduce the known RED build**

Run:

```bash
dotnet build Windows/EMKE.Windows.slnx --configuration Release
```

Expected: failure names `File.CreateHardLink`, `StringAssert.DoesNotContain`,
`MSTEST0044`, `CA1307`, `MSTEST0037`, and `CA1859`. Record the exact output in
the task report; this is baseline failure evidence, not a new feature RED.

- [ ] **Step 2: Add the Windows hard-link test helper**

Create this test-only adapter:

```csharp
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace EMKE.Setup.Tests;

internal static partial class TestNativeFileMethods
{
    public static void CreateHardLink(string linkPath, string existingPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }
        if (!CreateHardLinkNative(linkPath, existingPath, nint.Zero))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateHardLinkW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkNative(
        string fileName,
        string existingFileName,
        nint securityAttributes);
}
```

- [ ] **Step 3: Make the MSTest 4/analyzer replacements**

Replace every `[DataTestMethod]` in the three named files with `[TestMethod]`.
Use these exact assertion shapes:

```csharp
StringAssert.Contains(
    Path.GetFileName(first.RootPath),
    "0.2.0",
    StringComparison.Ordinal);
```

```csharp
Assert.AreNotEqual(
    FileAttributes.None,
    File.GetAttributes(result.OutputPath) & FileAttributes.ReadOnly);
```

```csharp
Assert.IsFalse(result.DisplayDetail.Contains(privatePath, StringComparison.Ordinal));
Assert.IsFalse(result.DisplayDetail.Contains("certificate-secret", StringComparison.Ordinal));
```

Replace `File.CreateHardLink` with `TestNativeFileMethods.CreateHardLink`, change
`TrustedSignatures()` to return `StaticSignatureVerifier`, and change
`VerifiedPayloads()` to return `VerifiedSetupPayload[]`.

- [ ] **Step 4: Verify the repaired baseline**

Run:

```bash
dotnet build Windows/EMKE.Windows.slnx --configuration Release
dotnet test Windows/tests/EMKE.Setup.Tests/EMKE.Setup.Tests.csproj --configuration Release --no-build --logger "console;verbosity=normal"
```

Expected: build succeeds with zero warnings/errors; every Setup test executes
on Windows with zero skipped tests. The three test-fixture branches changed in
this task must pass. These five old production failures are accepted only as
the recorded RED baseline for Tasks 2-3 and do not block Task 1:

```text
VerifiedOutputAllowsReadOnlyVerificationWithFullSharing
VerifiedOutputRejectsWriteAndDeleteSharingUntilVerificationLeaseIsReleased
VerifiedOutputIsReadOnlyAndFinalPathRemainsInsideCreatedRoot
DisposeDeletesVerifiedPayloadAndEmptyRootThroughHeldHandles
HeldPayloadHandleRejectsReplacementAndCleanupNeverDeletesReplacementSource
```

Any additional Setup failure, any skipped Setup test, or any build warning/error
blocks Task 1. Tasks 2-3 must turn all five recorded RED cases green before Task
3 can complete.

- [ ] **Step 5: Commit the baseline repair**

```bash
git add Windows/tests/EMKE.Setup.Tests
git commit -m "test: restore Setup test baseline"
```

### Task 2: Create and own the extraction root atomically

**Files:**
- Create: `Windows/src/EMKE.Setup/WindowsAtomicSetupDirectoryFactory.cs`
- Modify: `Windows/src/EMKE.Setup/SetupExtractionDirectory.cs:102-230,350-680`
- Modify: `Windows/tests/EMKE.Setup.Tests/SetupExtractionDirectoryTests.cs`

**Interfaces:**
- Consumes: safe Setup-owned base path and product version.
- Produces: `AtomicSetupDirectory WindowsAtomicSetupDirectoryFactory.Create(string basePath, string leafName)` containing `FullPath`, `SafeFileHandle Handle`, and `SetupFileIdentity Identity`; `SetupExtractionDirectory.CreateNamedForTest(...)` returns a live owned directory or throws a stable `SetupExtractionException`.

- [ ] **Step 1: Write the atomic-root RED tests**

Add:

```csharp
[TestMethod]
public void ExistingNamedRootIsRejectedWithoutOpeningOrDeletingIt()
{
    using TemporaryDirectory temporary = new();
    string leaf = "0.2.0-existing";
    string existing = Path.Combine(temporary.Path, leaf);
    Directory.CreateDirectory(existing);
    File.WriteAllText(Path.Combine(existing, "owner.txt"), "original");

    SetupExtractionException error = Assert.ThrowsExactly<SetupExtractionException>(
        () => SetupExtractionDirectory.CreateNamedForTest(
            temporary.Path, leaf, new Version(0, 2, 0, 0)));

    Assert.AreEqual("extractionRootAlreadyExists", error.FailureCode);
    Assert.AreEqual("original", File.ReadAllText(Path.Combine(existing, "owner.txt")));
}
```

```csharp
[TestMethod]
public void FactoryReturnAlreadyBlocksRootMoveAndDelete()
{
    using TemporaryDirectory temporary = new();
    using SetupExtractionDirectory extraction =
        SetupExtractionDirectory.Create(temporary.Path, new Version(0, 2, 0, 0));

    Assert.ThrowsExactly<IOException>(() =>
        Directory.Move(extraction.RootPath, extraction.RootPath + "-moved"));
    Assert.ThrowsExactly<IOException>(() => Directory.Delete(extraction.RootPath));
}
```

Run the focused test on Windows CI. Expected: the deterministic collision API
or immediate owner invariant fails against the old
`CreateDirectoryW -> OpenRootHandle` implementation.

- [ ] **Step 2: Add the relative NtCreateFile adapter**

Implement:

```csharp
internal readonly record struct SetupFileIdentity(
    uint VolumeSerialNumber,
    uint FileIndexHigh,
    uint FileIndexLow,
    uint FileAttributes);

internal sealed record AtomicSetupDirectory(
    string FullPath,
    SafeFileHandle Handle,
    SetupFileIdentity Identity);

internal sealed class WindowsAtomicSetupDirectoryFactory
{
    internal const int MaximumCreateAttempts = 8;
    public AtomicSetupDirectory Create(string basePath, string leafName);
}
```

`Create` opens the verified base directory, marshals one relative
`UNICODE_STRING`, and calls `NtCreateFile` with:

```csharp
const uint DesiredAccess = 0x00000001 | 0x00000020 | 0x00000080
    | 0x00010000 | 0x00100000;
const uint ShareAccess = 0x00000001 | 0x00000002;
const uint FileCreate = 2;
const uint CreateOptions = 0x00000001 | 0x00000020 | 0x00200000;
const uint ObjectCaseInsensitive = 0x00000040;
const nuint FileCreated = 2;
```

Use these native entrypoints with sequential `UNICODE_STRING`,
`OBJECT_ATTRIBUTES`, and `IO_STATUS_BLOCK` layouts matching pointer size:

```csharp
[LibraryImport("ntdll.dll", EntryPoint = "NtCreateFile")]
private static partial int NtCreateFile(
    out SafeFileHandle fileHandle,
    uint desiredAccess,
    ref ObjectAttributes objectAttributes,
    out IoStatusBlock ioStatusBlock,
    nint allocationSize,
    uint fileAttributes,
    uint shareAccess,
    uint createDisposition,
    uint createOptions,
    nint eaBuffer,
    uint eaLength);

[LibraryImport("ntdll.dll", EntryPoint = "RtlNtStatusToDosError")]
private static partial uint RtlNtStatusToDosError(int status);
```

Set `OBJECT_ATTRIBUTES.RootDirectory` to the verified base handle. Accept only
NT success plus `IO_STATUS_BLOCK.Information == FileCreated`. Map collision to
`extractionRootAlreadyExists`, every other creation failure to
`atomicExtractionRootUnavailable`, and use `RtlNtStatusToDosError` only for
bounded numeric diagnostic metadata.

- [ ] **Step 3: Transfer the returned handle directly into the directory**

Replace the path constructor with:

```csharp
private SetupExtractionDirectory(AtomicSetupDirectory root)
{
    RootPath = root.FullPath;
    _rootHandle = root.Handle;
    _rootIdentity = root.Identity;
}
```

`Create` generates `0.2.0-<32 lowercase hex>` names and retries only collisions.
After eight collisions throw `extractionRootCollisionLimit`. Validate final
handle path, non-reparse attributes, and identity before returning. Remove
`TryCreateNewDirectory`, `OpenRootHandle`, and the constructor that reopens a
created path.

- [ ] **Step 4: Run root ownership tests and the full Setup suite**

Run on Windows:

```powershell
dotnet test Windows/tests/EMKE.Setup.Tests/EMKE.Setup.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~SetupExtractionDirectoryTests" `
  --logger "trx;LogFileName=task2r-atomic-root.trx"
dotnet test Windows/tests/EMKE.Setup.Tests/EMKE.Setup.Tests.csproj `
  --configuration Release --logger "console;verbosity=normal"
```

Expected: collision content survives; returned root cannot be moved/deleted;
reparse ancestors reject; all Task 2 root-ownership tests pass. The full Setup
suite must not add any failure beyond the five Task 1 RED cases named above.
Those five cases remain the binding RED baseline for Task 3, which must make the
complete Setup suite green.

- [ ] **Step 5: Commit atomic root ownership**

```bash
git add Windows/src/EMKE.Setup/WindowsAtomicSetupDirectoryFactory.cs Windows/src/EMKE.Setup/SetupExtractionDirectory.cs Windows/tests/EMKE.Setup.Tests/SetupExtractionDirectoryTests.cs
git commit -m "refactor: own Setup extraction root atomically"
```

### Task 3: Introduce handle-bound payload leases and read views

**Files:**
- Create: `Windows/src/EMKE.Setup/VerifiedPayloadLease.cs`
- Modify: `Windows/src/EMKE.Setup/SetupExtractionDirectory.cs`
- Modify: `Windows/src/EMKE.Setup/SetupPayloadVerifier.cs:32-56,326-410`
- Modify: `Windows/tests/EMKE.Setup.Tests/SetupExtractionDirectoryTests.cs`
- Modify: `Windows/tests/EMKE.Setup.Tests/SetupPayloadVerifierTests.cs`

**Interfaces:**
- Consumes: atomically owned root handle and one `SetupPayload` descriptor.
- Produces: `VerifiedPayloadLease.UseHandle<T>(Func<SafeFileHandle,T>)`, `VerifiedPayloadLease.OpenReadView()`, `VerifiedSetupPayload.Lease`, and `SetupExtractionResult.Payload`.

- [ ] **Step 1: Write payload-lease RED tests**

Add:

```csharp
[TestMethod]
public void ReadViewUsesTheOwnedFileWhileMutationStaysBlocked()
{
    using TemporaryDirectory temporary = new();
    using SetupExtractionDirectory extraction =
        SetupExtractionDirectory.Create(temporary.Path, new Version(0, 2, 0, 0));
    SetupExtractionResult result = extraction.CopyVerified(
        new MemoryStream("payload"u8.ToArray()), ExpectedPayload());
    VerifiedSetupPayload payload = result.Payload!;

    using Stream view = payload.Lease.OpenReadView();
    byte[] observed = new byte[7];
    view.ReadExactly(observed);

    CollectionAssert.AreEqual("payload"u8.ToArray(), observed);
    Assert.ThrowsExactly<IOException>(() => File.Delete(payload.DisplayPath));
    Assert.ThrowsExactly<IOException>(() =>
        File.Open(payload.DisplayPath, FileMode.Open, FileAccess.Write, FileShare.Read));
}
```

Add a second test that opens two read views, seeks them to different offsets,
and asserts their positions do not interfere. Run focused tests on Windows;
expected RED is the missing lease/read-view API.

- [ ] **Step 2: Implement VerifiedPayloadLease**

The type owns the original `CREATE_NEW` handle with read/write, attribute,
synchronize, and delete access and `FileShare.Read` only:

```csharp
internal sealed class VerifiedPayloadLease
{
    private readonly SafeFileHandle _handle;
    private bool _closed;

    public string LogicalName { get; }
    public string DisplayPath { get; }

    public T UseHandle<T>(Func<SafeFileHandle, T> action)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        ArgumentNullException.ThrowIfNull(action);
        return action(_handle);
    }

    public Stream OpenReadView()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        return new HandleReadView(this);
    }
}
```

`HandleReadView` derives from `Stream`; `CanRead`/`CanSeek` are true,
`CanWrite` is false, `Read(Span<byte>)` calls
`RandomAccess.Read(_lease._handle, buffer, _position)`, and `Seek` permits only
positions from zero through the captured file length. `Write`, `SetLength`, and
invalid seeks throw. Disposing a view never disposes or duplicates the owner
handle.

- [ ] **Step 3: Make verified payloads lease-bearing**

Use:

```csharp
internal sealed class VerifiedSetupPayload
{
    public SetupPayload ManifestPayload { get; }
    public long Length { get; }
    public string Sha256 { get; }
    public string DisplayPath => Lease.DisplayPath;
    public VerifiedPayloadLease Lease { get; }
}

internal sealed class SetupExtractionResult
{
    public bool Succeeded { get; }
    public string? FailureCode { get; }
    public VerifiedSetupPayload? Payload { get; }
}
```

Change extraction to `CopyVerified(Stream source, SetupPayload expectedPayload)`;
the manifest supplies logical name and output leaf. Copy, maximum-length
enforcement, hash, flush, final-path validation, readonly marking, and all reads
operate through the lease. Remove every success path that returns only a path.

- [ ] **Step 4: Run mutation and independent-position tests**

Run on Windows:

```powershell
dotnet test Windows/tests/EMKE.Setup.Tests/EMKE.Setup.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~SetupExtractionDirectoryTests|FullyQualifiedName~SetupPayloadVerifierTests" `
  --logger "trx;LogFileName=task2r-payload-leases.trx"
```

Expected: read views return literal bytes; independent offsets remain
independent; write/delete/move/hard-link/reparse substitution fail while the
attempt is alive; all listed tests pass.

- [ ] **Step 5: Commit payload leases**

```bash
git add Windows/src/EMKE.Setup/VerifiedPayloadLease.cs Windows/src/EMKE.Setup/SetupExtractionDirectory.cs Windows/src/EMKE.Setup/SetupPayloadVerifier.cs Windows/tests/EMKE.Setup.Tests
git commit -m "refactor: bind Setup payloads to owner handles"
```

### Task 4: Make cleanup and attempt lifetime observable

**Files:**
- Create: `Windows/src/EMKE.Setup/SetupCleanupOutcome.cs`
- Modify: `Windows/src/EMKE.Setup/VerifiedPayloadLease.cs`
- Modify: `Windows/src/EMKE.Setup/SetupExtractionDirectory.cs`
- Modify: `Windows/src/EMKE.Setup/SetupPayloadVerifier.cs:164-225,326-410`
- Modify: `Windows/tests/EMKE.Setup.Tests/SetupExtractionDirectoryTests.cs`
- Modify: `Windows/tests/EMKE.Setup.Tests/SetupPayloadVerifierTests.cs`

**Interfaces:**
- Consumes: live extraction directory and payload leases.
- Produces: `SetupCleanupOutcome`, idempotent `Cleanup()` on directory/attempt/result, `LastCleanupOutcome`, and rejection results that retain cleanup evidence.

- [ ] **Step 1: Write cleanup RED tests**

Add:

```csharp
SetupCleanupOutcome first = extraction.Cleanup();
SetupCleanupOutcome second = extraction.Cleanup();
Assert.AreSame(first, second);
Assert.IsTrue(first.Completed);
Assert.IsFalse(first.ResidualRetained);
Assert.IsEmpty(first.RetainedLogicalNames);
```

For an injected unexpected child, assert:

```csharp
Assert.IsFalse(outcome.Completed);
Assert.IsTrue(outcome.ResidualRetained);
Assert.AreEqual("unexpectedExtractionEntriesRetained", outcome.FailureCode);
CollectionAssert.AreEquivalent(
    new[] { "unexpected-entry" },
    outcome.RetainedLogicalNames.ToArray());
```

Add a verifier rejection test whose fake signature verifier rejects after all
five payloads are extracted; assert the result reports completed cleanup. Add a
successful-result test that calls `result.Attempt!.Cleanup()` and observes the
same instance through `result.LastCleanupOutcome`. Expected RED is the missing
contract/result propagation.

- [ ] **Step 2: Add the immutable cleanup contract**

Create:

```csharp
internal sealed class SetupCleanupOutcome
{
    public bool Completed { get; }
    public bool ResidualRetained { get; }
    public string? FailureCode { get; }
    public IReadOnlyList<string> RetainedLogicalNames { get; }

    public static SetupCleanupOutcome NotAttempted { get; } =
        new(false, false, null, []);
    public static SetupCleanupOutcome Cleaned { get; } =
        new(true, false, null, []);

    public static SetupCleanupOutcome Residual(
        string failureCode,
        IEnumerable<string> retainedLogicalNames);
}
```

The constructor copies, ordinal-sorts, and deduplicates logical names; a
residual requires a non-empty stable failure code. Delete
`SetupExtractionCleanupState`.

- [ ] **Step 3: Implement exact-handle, idempotent cleanup**

`VerifiedPayloadLease.Cleanup()` clears readonly state and sets delete
disposition on its owner handle. It closes only after delete disposition
succeeds; uncertainty retains the handle until directory cleanup returns a
residual. `SetupExtractionDirectory.Cleanup()`:

1. returns the cached outcome when already attempted;
2. cleans payloads in reverse creation order;
3. enumerates the root with `NtQueryDirectoryFile` on `_rootHandle` and maps
   any unknown child to the bounded logical label `unexpected-entry`; it never
   reopens `RootPath` to inspect contents;
4. deletes the root only through `_rootHandle` when payload cleanup is certain
   and the root is empty;
5. reports `payloadCleanupUncertain`,
   `unexpectedExtractionEntriesRetained`, or `rootCleanupUncertain` with logical
   names; and
6. never recursively deletes unknown entries; after a residual outcome is
   frozen, it closes remaining owner handles without another delete attempt so
   the residual stays recoverable and no handle is leaked.

`Dispose()` calls `Cleanup()` as a fallback.

Use `NtQueryDirectoryFile` with `FileNamesInformation`, `ReturnSingleEntry =
false`, and restart only on the first call. Parse each length-bounded entry from
the returned buffer, ignore `.` and `..`, and treat malformed offsets or status
other than success/`STATUS_NO_MORE_FILES` as `rootCleanupUncertain`.

- [ ] **Step 4: Propagate cleanup through attempt and result**

Use:

```csharp
internal sealed class SetupPayloadVerificationAttempt : IDisposable
{
    public SetupCleanupOutcome LastCleanupOutcome { get; private set; } =
        SetupCleanupOutcome.NotAttempted;
    public SetupCleanupOutcome Cleanup();
    public void Dispose() => _ = Cleanup();
}
```

```csharp
internal sealed class SetupPayloadVerificationResult : IDisposable
{
    public SetupPayloadVerificationAttempt? Attempt { get; }
    public SetupCleanupOutcome LastCleanupOutcome { get; private set; }
    public SetupCleanupOutcome Cleanup();
    public void Dispose() => _ = Cleanup();
}
```

Every rejection after root creation calls directory cleanup before returning
and stores that exact outcome. A successful result transfers ownership to one
attempt. `finally` must never hide a residual outcome behind `Dispose()`.

- [ ] **Step 5: Verify cleanup paths**

Run on Windows:

```powershell
dotnet test Windows/tests/EMKE.Setup.Tests/EMKE.Setup.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~Cleanup|FullyQualifiedName~VerifyAndExtract" `
  --logger "trx;LogFileName=task2r-cleanup.trx"
```

Expected: success removes exact owned objects; unexpected content remains;
repeated cleanup returns the same outcome; rejection and success results expose
the outcome; zero failures.

- [ ] **Step 6: Commit observable cleanup**

```bash
git add Windows/src/EMKE.Setup Windows/tests/EMKE.Setup.Tests
git commit -m "feat: expose Setup cleanup outcomes"
```

### Task 5: Verify MSIX and certificate evidence through held handles

**Files:**
- Create: `Windows/src/EMKE.Platform/Security/WindowsHandleAuthenticodeTrust.cs`
- Create: `Windows/src/EMKE.Setup/WindowsSetupSignatureProbe.cs`
- Modify: `Windows/src/EMKE.Setup/SetupPayloadVerifier.cs:97-162,454-744`
- Modify: `Windows/tests/EMKE.Setup.Tests/SetupPayloadVerifierTests.cs`
- Create: `Windows/tests/EMKE.Setup.Tests/WindowsSignedPayloadFixtureTests.cs`

**Interfaces:**
- Consumes: lease-bearing MSIX and CER payloads.
- Produces: `WindowsHandleTrustEvidence WindowsHandleAuthenticodeTrust.Verify(SafeFileHandle handle, string displayPath, Guid actionId)`, signer certificate bytes from the same WinTrust state, and handle-oriented `ISetupSignatureProbe` methods.

- [ ] **Step 1: Write handle-oriented signature RED tests**

Change the desired probe contract to:

```csharp
internal interface ISetupSignatureProbe
{
    SetupMsixSignatureEvidence VerifyMsix(VerifiedSetupPayload msix);
    SetupCertificateEvidence ReadCertificate(VerifiedSetupPayload certificate);
    SetupDriverCatalogEvidence VerifyDriverCatalog(
        VerifiedSetupPayload catalog,
        VerifiedSetupPayload inf,
        VerifiedSetupPayload sys);
}
```

Update the recording probe to assert object identity with the five verified
payloads, not path strings. Add a Publisher test that reads through
`payload.Lease.OpenReadView()`. Add `WindowsSignedPayloadFixtureTests` with
category `WindowsSetupSignedPayload`; it reads
`EMKE_SETUP_SIGNED_MSIX_FIXTURE` and `EMKE_SETUP_SIGNING_CER_FIXTURE`, copies
both into real leases, calls the production MSIX/CER probe methods, and asserts
signature intact, exact signer SHA-256, exact subject, valid dates, and exact
Publisher. If either variable is absent, fail with `Assert.Fail`. Expected RED
is the path-only probe/WinTrust API.

- [ ] **Step 2: Implement reusable WinTrust state ownership**

Create in `EMKE.Platform.Security`:

```csharp
internal enum WindowsHandleTrustStatus
{
    Trusted,
    ChainOnly,
    Invalid,
}

internal sealed record WindowsHandleTrustEvidence(
    WindowsHandleTrustStatus Status,
    int NativeStatus,
    byte[]? SignerCertificate);

internal static class WindowsHandleAuthenticodeTrust
{
    internal static readonly Guid GenericVerifyV2 =
        new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
    internal static readonly Guid DriverActionVerify =
        new("F750E6C3-38EE-11D1-85E5-00C04FC295EE");

    public static WindowsHandleTrustEvidence Verify(
        SafeFileHandle handle,
        string displayPath,
        Guid actionId);
}
```

Populate `WINTRUST_FILE_INFO.hFile` with the held handle, use
`WTD_STATEACTION_VERIFY`, and always issue `WTD_STATEACTION_CLOSE` in `finally`.
Before close, use `WTHelperProvDataFromStateData`,
`WTHelperGetProvSignerFromChain`, and `WTHelperGetProvCertFromChain`; copy the
encoded `CERT_CONTEXT` bytes without taking ownership of the native context.
Map `0`, `0x800B0109`, and `0x800B010A` to Trusted/ChainOnly; every other status
is Invalid. Protect `DangerousGetHandle()` with `DangerousAddRef`/release.

The helper calls use these signatures and return null evidence when any pointer
or encoded certificate length is invalid:

```csharp
[LibraryImport("wintrust.dll", EntryPoint = "WTHelperProvDataFromStateData")]
private static partial nint WTHelperProvDataFromStateData(nint stateData);

[LibraryImport("wintrust.dll", EntryPoint = "WTHelperGetProvSignerFromChain")]
private static partial nint WTHelperGetProvSignerFromChain(
    nint providerData,
    uint signerIndex,
    [MarshalAs(UnmanagedType.Bool)] bool counterSigner,
    uint counterSignerIndex);

[LibraryImport("wintrust.dll", EntryPoint = "WTHelperGetProvCertFromChain")]
private static partial nint WTHelperGetProvCertFromChain(
    nint providerSigner,
    uint certificateIndex);
```

- [ ] **Step 3: Move MSIX/CER parsing to lease reads**

`WindowsSetupSignatureProbe.VerifyMsix` calls the handle verifier with
`GenericVerifyV2`, hashes `SignerCertificate` with SHA-256, and reads
`Package/Identity/@Publisher` from a `ZipArchive` over `OpenReadView()`.
`ReadCertificate` reads bounded bytes from `OpenReadView()` and uses
`X509CertificateLoader.LoadCertificate`. Delete `ReadSignerSha256(string)`,
`IsSignatureIntact(string)`, and all Setup signature-path reopen logic.

`WindowsSetupPayloadSignatureVerifier` accepts Trusted or ChainOnly MSIX status,
then requires signer certificate SHA-256, CER SHA-256, subject, validity, and
Publisher to match exactly. Use stable failures `msixSignatureInvalid`,
`msixSignerMismatch`, `msixPublisherMismatch`, and
`certificateEvidenceMismatch`.

- [ ] **Step 4: Verify unit and malformed native paths**

Run:

```bash
dotnet test Windows/tests/EMKE.Setup.Tests/EMKE.Setup.Tests.csproj --configuration Release --filter "TestCategory!=WindowsSetupSignedPayload" --logger "console;verbosity=normal"
```

Expected: all ordinary Setup tests pass; malformed/unsigned MSIX rejects;
Publisher and CER parsing work while write/delete sharing remains blocked.

- [ ] **Step 5: Commit handle-bound MSIX/CER trust**

```bash
git add Windows/src/EMKE.Platform/Security/WindowsHandleAuthenticodeTrust.cs Windows/src/EMKE.Setup Windows/tests/EMKE.Setup.Tests
git commit -m "feat: verify Setup signatures by handle"
```

### Task 6: Verify catalog policy and members without reopening Setup paths

**Files:**
- Create: `Windows/src/EMKE.Platform/Driver/WindowsHandleCatalogTrustVerifier.cs`
- Modify: `Windows/src/EMKE.Platform/Driver/WindowsDriverManager.cs:839-988`
- Modify: `Windows/src/EMKE.Setup/WindowsSetupSignatureProbe.cs`
- Create: `Windows/tests/EMKE.Integration.Tests/WindowsHandleCatalogTrustTests.cs`
- Modify: `Windows/tests/EMKE.Setup.Tests/SetupPayloadVerifierTests.cs`

**Interfaces:**
- Consumes: CAT, INF, and SYS lease handles plus display metadata.
- Produces: `WindowsHandleCatalogEvidence WindowsHandleCatalogTrustVerifier.Verify(...)` with separate kernel/signature/member/policy evidence; Setup maps it to stable failure codes.

- [ ] **Step 1: Write catalog RED tests**

Define the desired production input:

```csharp
internal sealed record WindowsCatalogHandleMember(
    string LogicalName,
    string DisplayPath,
    SafeFileHandle Handle);

internal sealed record WindowsHandleCatalogEvidence(
    string? SignerSubject,
    bool KernelPolicyValid,
    bool CatalogEntriesMatch,
    bool MemberTrustValid,
    bool Allowed,
    string Reason);
```

Add an integration fixture helper that hashes candidate inbox members
`%SystemRoot%\System32\drivers\null.sys`, `cng.sys`, `disk.sys`, and `partmgr.sys`
with `CryptCATAdminCalcHashFromFileHandle2`, uses
`CryptCATAdminEnumCatalogFromHash` plus `CryptCATCatalogInfoFromContext` to
resolve the first registered Microsoft catalog, and fails if no pair is found.
Open the selected CAT/member with restrictive owner handles and assert
`KernelPolicyValid`, `CatalogEntriesMatch`, and `MemberTrustValid` from the
production verifier.

Add `UnsignedEmkeCatalogIsDecodedForExactMembersButFailsKernelPolicy`, category
`WindowsSetupUnsignedEmkeCatalog`, reading exact paths from
`EMKE_SETUP_UNSIGNED_CAT_FIXTURE`, `EMKE_SETUP_UNSIGNED_INF_FIXTURE`, and
`EMKE_SETUP_UNSIGNED_SYS_FIXTURE`. Assert both members are present,
`CatalogEntriesMatch` is true, `MemberTrustValid` and `KernelPolicyValid` are
false, and `Allowed` is false. Missing variables fail. Expected RED is the
missing handle catalog verifier.

- [ ] **Step 2: Decode the held CAT bytes into a CTL context**

`WindowsHandleCatalogTrustVerifier` reads bounded CAT bytes from the supplied
handle and passes one pinned `CRYPT_DATA_BLOB` to:

```csharp
CryptQueryObject(
    CertQueryObjectBlob,
    blobPointer,
    CertQueryContentFlagCtl,
    CertQueryFormatFlagBinary,
    0,
    out _,
    out _,
    out _,
    out _,
    out _,
    out nint ctlContext);
```

Wrap the returned context in a `SafeHandle` whose release calls
`CertFreeCTLContext`. If decoding fails, return reason `catalogDecodeInvalid`;
do not reopen the display path. Keep this deprecated API isolated in the new
adapter and fail closed on every exception.

- [ ] **Step 3: Verify CAT trust and member handles**

Call `WindowsHandleAuthenticodeTrust.Verify` with `DriverActionVerify` for the
CAT handle. For each INF/SYS member, call
`CryptCATAdminCalcHashFromFileHandle2` twice on its held handle, derive the
uppercase member tag, and call WinTrust with:

```csharp
WinTrustCatalogInfo info = new()
{
    CatalogFilePath = catalogDisplayPathPointer,
    MemberTag = memberTagPointer,
    MemberFilePath = memberDisplayPathPointer,
    MemberFile = memberHandle.DangerousGetHandle(),
    CalculatedFileHash = hashPointer,
    CalculatedFileHashSize = checked((uint)hash.Length),
    CatalogContext = ctlContext.DangerousGetHandle(),
    CatalogAdmin = catalogAdmin,
};
```

The logical member set must equal `driver-inf` and `driver-sys` exactly. Apply
the calculated hashes to the CTL entries and set `CatalogEntriesMatch` only
when both exact hashes are present and no expected logical member is missing.
Set `MemberTrustValid` only when both handle-based WinTrust catalog-member calls
succeed. Apply `MicrosoftDriverCatalogTrustPolicy` only after kernel signature,
local chain, CTL-entry matching, and member trust are complete; pass
`CatalogEntriesMatch && MemberTrustValid` as its member-evidence input. Release
catalog admin, CTL, trust state, and every dangerous reference in `finally`.

- [ ] **Step 4: Restore the installed-driver sharing contract**

In the existing path-based `WindowsCatalogTrustNativeApi.VerifyCatalogMember`,
restore:

```csharp
using SafeFileHandle memberFile = File.OpenHandle(
    fullMemberPath,
    FileMode.Open,
    FileAccess.Read,
    FileShare.Read | FileShare.Delete);
```

Do not route installed-driver checks through Setup leases and do not change its
public policy behavior.

- [ ] **Step 5: Connect Setup to the new handle verifier**

`WindowsSetupSignatureProbe.VerifyDriverCatalog` passes CAT/INF/SYS handles via
their leases. Map failed kernel/policy evidence to
`catalogKernelTrustInvalid`; map `CatalogEntriesMatch == false` to
`catalogMemberMismatch`. No path-based verifier call remains in Setup.

- [ ] **Step 6: Run catalog unit/native tests**

Run on Windows:

```powershell
dotnet test Windows/tests/EMKE.Integration.Tests/EMKE.Integration.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~WindowsHandleCatalogTrustTests&TestCategory!=WindowsSetupUnsignedEmkeCatalog" `
  --logger "trx;LogFileName=task2r-inbox-catalog.trx"
dotnet test Windows/tests/EMKE.Setup.Tests/EMKE.Setup.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~Catalog" `
  --logger "console;verbosity=normal"
```

Expected: a real inbox catalog/member passes handle-bound native signature and
membership evidence; malformed fixtures reject; installed-driver tests remain
green.

- [ ] **Step 7: Commit handle-bound catalog verification**

```bash
git add Windows/src/EMKE.Platform/Driver Windows/src/EMKE.Setup/WindowsSetupSignatureProbe.cs Windows/tests/EMKE.Integration.Tests/WindowsHandleCatalogTrustTests.cs Windows/tests/EMKE.Setup.Tests/SetupPayloadVerifierTests.cs
git commit -m "feat: verify Setup driver catalog by handle"
```

### Task 7: Wire signed and unsigned native evidence into Windows workflows

**Files:**
- Modify: `.github/workflows/windows-internal-msix.yml`
- Modify: `.github/workflows/windows-audio.yml`
- Modify: `Windows/tools/tests/windows-internal-msix-workflow.contract.test.mjs`
- Modify: `Windows/tools/tests/windows-audio-workflow.syntax.test.ps1`
- Create: `Windows/tools/test-setup-task2r-client.ps1`
- Create: `Windows/tools/tests/setup-task2r-client.validation.test.ps1`
- Modify: `docs/quality/windows-driver-submission-evidence.md`

**Interfaces:**
- Consumes: signed internal MSIX/CER from the signing job, unsigned EMKE INF/SYS/CAT from the driver job, and normal Windows managed builds.
- Produces: separate TRX evidence for ordinary Setup tests, signed payload tests, inbox catalog tests, unsigned EMKE membership/negative-trust tests, and a client evidence JSON schema.

- [ ] **Step 1: Write workflow/client-script RED contract tests**

The workflow tests must parse YAML/PowerShell behavior and require these exact
result names and environment variables:

```text
task2r-setup-managed.trx
task2r-signed-payload.trx
task2r-inbox-catalog.trx
task2r-unsigned-emke-catalog.trx
EMKE_SETUP_SIGNED_MSIX_FIXTURE
EMKE_SETUP_SIGNING_CER_FIXTURE
EMKE_SETUP_UNSIGNED_CAT_FIXTURE
EMKE_SETUP_UNSIGNED_INF_FIXTURE
EMKE_SETUP_UNSIGNED_SYS_FIXTURE
```

The client validation test runs the script against a fake dotnet command and
requires bounded JSON fields `schemaVersion`, `osCaption`, `osBuild`,
`architecture`, `setupTests`, `inboxCatalogTests`, `signedPayloadTests`,
`unsignedCatalogTests`, and `sourceCommit`; it rejects build below 19045,
server product type, non-AMD64, missing fixtures, skipped tests, and nonzero
exit codes. Run the tests and observe RED because these steps/script do not
exist.

- [ ] **Step 2: Add the ordinary managed and inbox gates**

In `windows-internal-msix.yml` build-test, run Setup tests separately after the
solution build and assert TRX counters show every selected test executed with
no failed/skipped tests. Exclude the two environment-fixture categories. Run
the inbox catalog test separately and write `task2r-inbox-catalog.trx`.

- [ ] **Step 3: Add the signed MSIX/CER gate**

In `sign-package-bundle`, immediately after `package-msix.ps1` and
`verify-msix.ps1`, set the two signed fixture variables to the exact generated
artifact paths and run:

```powershell
dotnet test Windows/tests/EMKE.Setup.Tests/EMKE.Setup.Tests.csproj `
  --configuration Release `
  --filter "TestCategory=WindowsSetupSignedPayload" `
  --logger "trx;LogFileName=task2r-signed-payload.trx"
```

Clear both environment variables in `finally`. The PFX/password remain existing
runner-only inputs and are never logged or uploaded.

- [ ] **Step 4: Add the unsigned EMKE CAT gate**

In `windows-audio.yml` driver-build-proof, set the three unsigned fixture
variables to `Windows/artifacts/driver/x64/Release/EMKE.VirtualAudio.cat`,
`.inf`, and `.sys`, install .NET 10 in that job, build the integration test
project with locked restore, run only
`TestCategory=WindowsSetupUnsignedEmkeCatalog`, require executed/pass counters
with no skip, then clear all three variables. This gate must assert CAT entry
hashes match while member trust, kernel trust, and final policy fail.

- [ ] **Step 5: Add the Windows client evidence script**

`test-setup-task2r-client.ps1` accepts mandatory signed/unsigned fixture paths,
`-SourceCommit`, and `-OutputPath`. Its control flow is:

```powershell
$os = Get-CimInstance -ClassName Win32_OperatingSystem
if ($os.ProductType -ne 1) { throw "Task 2R client evidence requires workstation Windows." }
if ([Environment]::OSVersion.Version.Build -lt 19045) { throw "Windows build is below 19045." }
if ($env:PROCESSOR_ARCHITECTURE -cne "AMD64") { throw "Task 2R evidence requires AMD64." }

$results = [ordered]@{}
$results.setupTests = Invoke-ExactDotnetTest -Filter "TestCategory!=WindowsSetupSignedPayload&TestCategory!=WindowsSetupUnsignedEmkeCatalog"
$results.inboxCatalogTests = Invoke-ExactDotnetTest -Filter "FullyQualifiedName~WindowsHandleCatalogTrustTests&TestCategory!=WindowsSetupUnsignedEmkeCatalog"
$results.signedPayloadTests = Invoke-ExactDotnetTest -Filter "TestCategory=WindowsSetupSignedPayload"
$results.unsignedCatalogTests = Invoke-ExactDotnetTest -Filter "TestCategory=WindowsSetupUnsignedEmkeCatalog"

[ordered]@{
    schemaVersion = 1
    osCaption = [string]$os.Caption
    osBuild = [Environment]::OSVersion.Version.Build
    architecture = "AMD64"
    setupTests = $results.setupTests
    inboxCatalogTests = $results.inboxCatalogTests
    signedPayloadTests = $results.signedPayloadTests
    unsignedCatalogTests = $results.unsignedCatalogTests
    sourceCommit = $SourceCommit
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
```

`Invoke-ExactDotnetTest` runs one filter to a unique TRX, parses counters, and
throws unless total = executed = passed and failed = notExecuted = 0. The
script performs no install, certificate import, driver mutation, or elevation.

- [ ] **Step 6: Run portable workflow/script tests**

Run:

```bash
node --test Windows/tools/tests/windows-internal-msix-workflow.contract.test.mjs
pwsh -NoProfile -File Windows/tools/tests/windows-audio-workflow.syntax.test.ps1
pwsh -NoProfile -File Windows/tools/tests/setup-task2r-client.validation.test.ps1
git diff --check
```

Expected: all contract/validation tests pass and no whitespace errors exist.

- [ ] **Step 7: Commit CI evidence wiring**

```bash
git add .github/workflows/windows-internal-msix.yml .github/workflows/windows-audio.yml Windows/tools/test-setup-task2r-client.ps1 Windows/tools/tests docs/quality/windows-driver-submission-evidence.md
git commit -m "ci: gate Setup handle trust evidence"
```

### Task 8: Run full Task 2R verification and freeze the evidence boundary

**Files:**
- Modify: `docs/quality/windows-driver-submission-evidence.md`
- Create: `docs/quality/windows-setup-task2r-evidence.md`

**Interfaces:**
- Consumes: Tasks 1-7 commits and workflow runs.
- Produces: one evidence ledger that identifies exact commit/run/job/test counts and explicitly leaves Windows 10/11 client acceptance and Microsoft-signed EMKE driver acceptance open until real evidence exists.

- [ ] **Step 1: Run the complete local portable gate**

Run:

```bash
node Scripts/validate-shared-contracts.mjs
node --test Windows/driver/tests/*.test.mjs Windows/tools/tests/*.test.mjs
dotnet build Windows/EMKE.Windows.slnx --configuration Release
git diff --check
```

Expected: all portable contracts and managed builds pass with zero warnings and
errors; no workspace changes appear except the evidence document.

- [ ] **Step 2: Push the evidence branch and run both Windows workflows**

```bash
git push origin HEAD:codex/evidence-setup-task2-preflight
gh workflow run windows-internal-msix.yml --ref codex/evidence-setup-task2-preflight -f run_hosted_install_validation=false
gh workflow run windows-audio.yml --ref codex/evidence-setup-task2-preflight
```

Wait for both runs. Record run IDs, job IDs, exact commit SHA, TRX totals,
executed/passed/failed/skipped counters, and artifact hashes. A hosted Server
run proves compilation/native API behavior only.

- [ ] **Step 3: Write the evidence ledger without overstating acceptance**

The document must contain these statuses:

```text
Task 2R managed/build evidence: passed|failed (run/job/commit)
Task 2R hosted native evidence: passed|failed (signed MSIX, inbox CAT, unsigned EMKE CAT)
Windows 10 22H2 client evidence: pending until test-setup-task2r-client.ps1 JSON is attached
Windows 11 client evidence: pending until test-setup-task2r-client.ps1 JSON is attached
Microsoft-signed EMKE CAT/release evidence: pending Hardware Dev Center exact bytes
```

Do not mark Task 3 ready unless every Task 2R managed/hosted gate passes and the
final review finds no load-bearing issue. Client/release pending statuses remain
release gates rather than fabricated Task 2R proof.

- [ ] **Step 4: Commit the evidence ledger**

```bash
git add docs/quality/windows-driver-submission-evidence.md docs/quality/windows-setup-task2r-evidence.md
git commit -m "docs: record Setup Task 2R evidence"
git push origin HEAD:codex/evidence-setup-task2-preflight
```

- [ ] **Step 5: Run the final whole-branch review**

Generate a review package from the branch merge base through `HEAD`. The final
review must check atomic root ownership, no path reopen in Setup trust, no
installed-driver sharing regression, cleanup observability, failure-code/path
redaction, test execution counts, and evidence-boundary wording. Any
Critical/Important finding gets one fix wave and one scoped re-review under the
subagent-driven development breaker rules.
