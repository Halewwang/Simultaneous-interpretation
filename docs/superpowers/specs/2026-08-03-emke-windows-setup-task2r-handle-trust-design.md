# EMKE Translation Windows Setup Task 2R Handle-Trust Design

**Status:** Approved architecture; written specification awaiting final user review

**Baseline:** `codex/evidence-setup-task2-preflight` at `1a173f4`

**Product target:** Windows 10 22H2 build 19045+ and Windows 11 x64, product
version `0.2.0.0`, application package family
`EMKE.Translation.Internal_kvab4te83cr7p`, driver hardware ID
`ROOT\EMKEVIRTUALAUDIO`, driver version `1.0.0.2`.

## 1. Goal and scope

Task 2R replaces the blocked Task 2 extraction and trust boundary. It must
produce a verified, handle-owned payload set that later Setup tasks can consume
without reopening mutable paths. It also makes every cleanup result observable
to the future orchestrator.

Task 2R includes:

- a clean, compiling Windows MSTest baseline for the Setup project;
- atomic creation and ownership of the version-scoped extraction directory;
- immutable payload leases for MSIX, CER, INF, SYS, and CAT;
- handle-bound MSIX Authenticode/signer/Publisher verification;
- handle-bound CAT kernel trust and exact INF/SYS membership verification;
- structured cleanup outcomes for success and all rejection paths.

It does not install certificates, drivers, devices, or MSIX packages; launch an
elevated helper; change BCD/Secure Boot/test mode; or invent final Hardware Dev
Center hashes. Those remain later-task or external release gates.

## 2. Decision and rejected alternatives

The approved design is **native atomic directory ownership plus end-to-end
handle verification**.

Two alternatives are rejected:

1. **Close and reopen by path/file ID.** This creates a transition window in
   which another process can replace the object. Rechecking identity afterward
   detects some attacks but cannot prove the system trust API examined the same
   bytes.
2. **Read-only attributes or user-owned ACLs plus path verification.** The file
   owner can change attributes or the DACL, and system APIs have undocumented
   sharing behavior. This does not meet the invariant that verified bytes stay
   bound to the same object until cleanup.

## 3. Security invariants

1. No machine or user mutation occurs before all payloads pass length, SHA-256,
   identity, Publisher, signer, and catalog checks.
2. Directory creation and ownership acquisition are one native operation; there
   is no `CreateDirectoryW` then `CreateFileW` gap.
3. Each payload is created once and remains represented by one owner lease until
   verification attempt cleanup.
4. Paths are display and Windows SIP metadata only. Hashing, parsing, signer
   extraction, CAT membership, and deletion use held handles or bytes read from
   those handles.
5. No Setup-specific sharing relaxation changes the existing installed-driver
   trust path in `EMKE.Platform`.
6. Cleanup deletes only objects represented by the attempt's original handles.
   Any uncertainty retains the residual and reports a stable failure code.
7. Logs and results contain logical payload names and failure codes, never full
   local paths, certificate private material, API keys, or arbitrary native
   error text.

## 4. Component design

### 4.1 Atomic extraction root

`WindowsAtomicSetupDirectoryFactory` opens the fixed Setup-owned base directory,
verifies it and its existing ancestors are not reparse points, then calls
`NtCreateFile` relative to the base handle with:

- a cryptographically random version-scoped leaf name;
- `FILE_CREATE` and `FILE_DIRECTORY_FILE`;
- directory read/list/traverse attributes, `SYNCHRONIZE`, and `DELETE` access;
- sharing that omits delete sharing;
- a returned `SafeFileHandle` and `IO_STATUS_BLOCK.Information == FILE_CREATED`.

Name collision retries are bounded. The returned handle is validated for file
identity, non-reparse type, and final location below the fixed base before the
factory returns. The public factory never returns a path without its owner
handle, making the previous create-to-open race unrepresentable.

`NtCreateFile` is used only behind a narrow native adapter. NTSTATUS values are
mapped to stable Setup failure codes; raw paths and localized messages are not
returned.

### 4.2 Verified payload leases

`VerifiedPayloadLease` owns the original `CreateFileW(CREATE_NEW)` handle. The
handle requests read, write, attribute, synchronize, and delete access and does
not share write or delete. Copying, bounded length enforcement, SHA-256, flush,
final path validation, readonly marking, and cleanup all use this object.

The lease exposes only controlled operations:

```csharp
T UseHandle<T>(Func<SafeFileHandle, T> action);
Stream OpenReadView();
SetupCleanupOutcome Cleanup();
```

`OpenReadView` is a seekable, read-only stream implemented with offset-based
`RandomAccess.Read` over the held handle. It does not open the path, transfer
handle ownership, change the shared file position, or permit writes.

`VerifiedSetupPayload` carries its manifest descriptor, logical name, bounded
length/SHA-256 evidence, display path, and lease. Signature verifiers consume
this type instead of accepting arbitrary paths.

### 4.3 MSIX and certificate trust

`WindowsHandleAuthenticodeVerifier` calls `WinVerifyTrust` with
`WINTRUST_FILE_INFO.hFile` set to the payload lease handle. The exact display
path remains present only because the Windows structure requires it; the open
handle binds verification to the owned object.

The call uses `WTD_STATEACTION_VERIFY`. Before the mandatory
`WTD_STATEACTION_CLOSE`, signer evidence is extracted from the same state data
with `WTHelperProvDataFromStateData`,
`WTHelperGetProvSignerFromChain`, and
`WTHelperGetProvCertFromChain`. The signer certificate SHA-256 must equal the
pinned CER certificate SHA-256. Fresh internal installs may treat only
`CERT_E_UNTRUSTEDROOT` (`0x800B0109`) or `CERT_E_CHAINING` (`0x800B010A`) as
chain-only pre-trust states; digest/signature failure never passes.

The MSIX Publisher is read from `Package/Identity/@Publisher` through the
lease's read view. The CER is loaded from bounded bytes read through its lease;
subject, validity, SHA-256 thumbprint, and signer pin must all match the
manifest.

### 4.4 Catalog trust and membership

The existing path-based installed-driver verifier keeps its restrictive sharing
behavior unchanged. A new handle-input overload in `EMKE.Platform` receives the
CAT, INF, and SYS owner handles from Setup.

The CAT file itself is verified with `WinVerifyTrust` file choice and the held
CAT handle under the driver policy. CAT bytes are then read from that handle and
decoded as a CTL context with `CryptQueryObject(CERT_QUERY_OBJECT_BLOB, ...
CERT_QUERY_CONTENT_FLAG_CTL ...)`.

`CryptQueryObject` is deprecated by Microsoft but remains available on the
supported Windows 10/11 baseline. It is isolated behind the catalog native
adapter, exercised on both supported OS families, and fails closed if Windows
cannot produce the CTL context. No production fallback reopens the CAT path.

For INF and SYS:

- `CryptCATAdminCalcHashFromFileHandle2` calculates the member hash from the
  held member handle;
- `WINTRUST_CATALOG_INFO.hMemberFile` receives that same handle;
- `WINTRUST_CATALOG_INFO.pcCatalogContext` receives the decoded CAT context, so
  WinTrust does not reopen the CAT path;
- the calculated hash and exact logical member set must match the manifest and
  Hardware Dev Center submission evidence.

All native catalog, message, certificate, and trust-state handles are released
with typed safe handles or unconditional `finally` blocks.

### 4.5 Observable cleanup

`SetupCleanupOutcome` is immutable and contains:

```csharp
bool Completed;
bool ResidualRetained;
string? FailureCode;
IReadOnlyList<string> RetainedLogicalNames;
```

`SetupPayloadVerificationAttempt.Cleanup()` is idempotent and returns this
outcome. `Dispose()` calls `Cleanup()` as a fallback, while
`LastCleanupOutcome` remains observable. Rejected verification results include
the cleanup outcome produced before returning. Successful results expose the
attempt so Task 3 can explicitly cleanup and persist a recovery record when
`ResidualRetained` is true.

Payload cleanup clears readonly state and sets delete disposition on each exact
owner handle. The root is deleted through its atomic owner handle only after all
known payloads are deleted and the directory is confirmed empty. Unexpected
entries, identity uncertainty, or a failed native operation retain the root.

## 5. Data flow

1. Preflight accepts only build 19045+ x64 Windows workstations.
2. Atomically create and own the random version-scoped root.
3. Copy each embedded payload into a new owner lease while enforcing manifest
   length and SHA-256.
4. Validate final handle path and mark the payload readonly.
5. Verify CER evidence from its handle.
6. Verify MSIX signature, signer, and Publisher from its handle and WinTrust
   state.
7. Verify CAT kernel trust, decode its CTL context from held bytes, and verify
   exact INF/SYS membership from their handles.
8. Return a successful attempt containing all live leases, or cleanup and return
   a rejected result with its cleanup outcome.

## 6. Failure contract

Stable failure codes include:

- `atomicExtractionRootUnavailable`
- `extractionRootCollisionLimit`
- `rootIdentityUnavailable`
- `reparsePointDetected`
- `unsafeOutputPath`
- `tamperedPayloadLength`
- `tamperedPayloadHash`
- `msixSignatureInvalid`
- `msixSignerMismatch`
- `msixPublisherMismatch`
- `certificateEvidenceMismatch`
- `catalogKernelTrustInvalid`
- `catalogMemberMismatch`
- `payloadCleanupUncertain`
- `unexpectedExtractionEntriesRetained`
- `rootCleanupUncertain`

Native error codes may be recorded only as bounded numeric diagnostic metadata;
they do not replace the stable code or expose the full path.

## 7. Test and evidence gates

Before new production behavior, the Setup test project must compile cleanly:

- replace obsolete MSTest `DataTestMethod` usage with supported data rows;
- replace unavailable `File.CreateHardLink` with a Windows test helper around
  `CreateHardLinkW`;
- use available MSTest assertions and satisfy CA1307/CA1859/MSTEST0037 without
  suppressions.

Every production change follows RED, Windows CI RED evidence, GREEN, and an
independent task review. Required Windows-backed tests cover:

- `NtCreateFile` collision rejection and returned directory ownership;
- root move/delete rejection immediately after factory return;
- payload write/delete/hard-link/reparse replacement rejection;
- offset-based read views while the restrictive owner lease is alive;
- MSIX WinTrust and signer extraction from an ephemeral CI-signed fixture under
  the lease; its key material exists only on the Windows runner and is never
  committed or uploaded;
- CER loading under the lease;
- CAT driver-policy trust and member verification using handles against a
  dynamically discovered Microsoft-signed inbox Windows catalog/member pair;
- exact INF/SYS membership and negative kernel-trust behavior against the
  current Inf2Cat-generated, unsigned EMKE CAT;
- cleanup success, unexpected-child retention, repeated cleanup, and observable
  residual outcomes;
- mutation of each payload or manifest field rejects before mutation.

The signed inbox catalog is test evidence for the native handle-bound driver
policy path only; it is not an EMKE release input. The unsigned EMKE CAT proves
the membership parser and must still fail the kernel-trust gate. Only an exact
Hardware Dev Center-returned EMKE package can satisfy the final positive EMKE
kernel-trust and release gate.

CI evidence must distinguish production compilation, test execution, native
WinTrust/catalog behavior, and later real-machine installation. Passing Task 2R
does not claim that Microsoft-signed driver bytes exist or that a clean install,
meeting endpoint, or translation session has passed.

## 8. Compatibility and release boundary

The design remains x64-only and supports Windows 10 22H2 build 19045 through
Windows 11. It does not change the independent macOS v0.2.4 release track or the
shared Translation protocol contract. Final Setup packaging continues to require
the exact accepted MSIX and Microsoft Hardware Dev Center-signed driver bytes;
no placeholder hashes may be promoted as final evidence.

## 9. Authoritative API references

- [NtCreateFile](https://learn.microsoft.com/en-us/windows/win32/api/winternl/nf-winternl-ntcreatefile)
- [WINTRUST_FILE_INFO](https://learn.microsoft.com/en-us/windows/win32/api/wintrust/ns-wintrust-wintrust_file_info)
- [WINTRUST_DATA](https://learn.microsoft.com/en-us/windows/win32/api/wintrust/ns-wintrust-wintrust_data)
- [WINTRUST_CATALOG_INFO](https://learn.microsoft.com/en-us/windows/win32/api/wintrust/ns-wintrust-wintrust_catalog_info)
- [CryptCATAdminCalcHashFromFileHandle2](https://learn.microsoft.com/en-us/windows/win32/api/mscat/nf-mscat-cryptcatadmincalchashfromfilehandle2)
- [CryptQueryObject](https://learn.microsoft.com/en-us/windows/win32/api/wincrypt/nf-wincrypt-cryptqueryobject)
- [WTHelperGetProvSignerFromChain](https://learn.microsoft.com/en-us/windows/win32/api/wintrust/nf-wintrust-wthelpergetprovsignerfromchain)
- [WTHelperGetProvCertFromChain](https://learn.microsoft.com/en-us/windows/win32/api/wintrust/nf-wintrust-wthelpergetprovcertfromchain)
