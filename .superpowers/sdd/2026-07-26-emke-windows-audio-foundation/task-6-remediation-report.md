# Task 6 remediation report

## Status

`DONE_WITH_CONCERNS`

Task-review fix round 4 is locally complete in implementation commit
`8a3daeb4af4f7962bc8d995e9096674d96cc539c`. Every new production behavior has
an observed compiled or executable RED and a fresh local GREEN. The prior
authorized hosted proof remains valid historical evidence for base commit
`44c3d08c0380d1bf48684034b42f4349ce14c398`, but it is not remote proof for the
new round-4 commit. A controller push and a fresh Windows hosted run are still
required for MSVC, WDK, CTest, package, catalog-mutation, and artifact gates.

This is build/package evidence, not release or runtime acceptance. The catalog
is explicitly unsigned; no driver or Windows application was installed, no
virtual endpoint was loaded or exercised, no meeting behavior was tested, and
the artifact is not a Windows application installer.

## Design and provenance

- The imported Microsoft source provenance remains
  `https://github.com/microsoft/Windows-driver-samples.git` at
  `2ee527bfeb0aeb6be11f0a8b6dce4011b358ce89` under the Microsoft Public
  License. The original derivative is based on SimpleAudioSample; current
  SYSVAD bounded stream-state conventions were reviewed at the same pinned
  commit.
- The actual cross-endpoint bridge is new EMKE code, not a claim that a
  same-filter physical topology connection or SYSVAD loopback pin crosses
  endpoint miniports.
- Two statically allocated SPSC bridges are initialized before endpoint
  creation:
  - meeting-speaker render to app-speaker capture;
  - app-microphone render to meeting-microphone capture.
- Each bridge holds exactly 4,800 stereo Float32 frames (100 ms at 48 kHz).
  Render callbacks copy only into available capacity; overflow drops newest
  frames and never overwrites unread frames. Capture callbacks zero the complete
  request before copying available frames.
- The WaveRT render/capture data-movement callbacks call the bridge directly.
  Their realtime path has no heap allocation, blocking lock, file I/O, network,
  JSON, or managed callback. The control-path reset uses exclusive atomic state
  only after the stream is stopped and queued DPC work is drained.
- Four unique miniport device identities select the four bridge roles. STOP is
  published while holding the stream position lock before the paired bridge and
  capture DMA are reset, preventing a late position callback from republishing
  prior-session data. ACQUIRE also resets fail-closed.
- The sample tone generator and render SaveData/file-worker implementation were
  removed. Empty capture returns zeros.
- Both render and capture WaveRT descriptors are exactly 48,000 Hz, stereo,
  32-bit IEEE Float32.
- `Windows/shared/emke_endpoint_contract.h` is the one compiled native
  authority for the property GUID/PID, driver ABI, four role strings, and
  virtual format. Both the driver and native host include it. An executable
  validator rejects INF role, property-key, or ABI divergence.
- Staging copies the exact single INF and SYS emitted by WDK
  `DriverPackageTarget`, rejects unresolved stamp tokens, and refuses an
  artifact target outside `Windows/artifacts` or through an existing
  symlink/reparse point. Cleanup is rechecked immediately before recursion.
- Catalog membership uses the Windows catalog APIs `CryptCATOpen`,
  `CryptCATEnumerateMember`, `CryptCATAdminAcquireContext2`, and
  `CryptCATAdminCalcHashFromFileHandle2`. It requires the exact unordered
  four-reference-tag multiset: SHA-1 and SHA-256 hashes for each staged INF and
  SYS. It does not depend on the four empty Inf2Cat v2 `pwszFileName` fields,
  and it fails closed on missing, duplicate, unknown, or extra tags. A
  Windows-only integration test requires the original package to pass and
  independently mutated INF and SYS copies to fail.

Microsoft API references consulted:

- <https://learn.microsoft.com/en-us/windows/win32/api/mscat/nf-mscat-cryptcatopen>
- <https://learn.microsoft.com/en-us/windows/win32/api/mscat/nf-mscat-cryptcatenumeratemember>
- <https://learn.microsoft.com/en-us/windows/win32/api/mscat/nf-mscat-cryptcatadminacquirecontext2>
- <https://learn.microsoft.com/en-us/windows/win32/api/mscat/nf-mscat-cryptcatadmincalchashfromfilehandle2>

## TDD evidence

### RED: compiled bridge, routing, underrun, capacity, reset, and format

The compiled behavior test was added before the bridge implementation.

Command:

```text
clang++ -std=c++17 -Wall -Wextra -Werror -c Windows/driver/tests/bridge-behavior-tests.cpp -o /tmp/emke-driver-bridge-tests.o
```

Expected pre-fix result:

```text
Windows/driver/tests/bridge-behavior-tests.cpp:1:10: fatal error:
'../EMKE.VirtualAudio/src/emke_audio_bridge.h' file not found
1 error generated.
```

This single compiled suite covers both exact bridge directions, rejection of
wrong capture endpoints, zero-filled underrun, the 4,800-frame bound with
drop-newest behavior, reset isolation, and the compiled Float32/role/ABI
constants. The pre-fix source had no bridge API, so none of those behaviors
could compile.

### GREEN: compiled bridge behavior

Command:

```text
clang++ -std=c++17 -Wall -Wextra -Werror \
  Windows/driver/tests/bridge-behavior-tests.cpp \
  Windows/driver/EMKE.VirtualAudio/src/emke_audio_bridge.cpp \
  -I Windows/shared \
  -I Windows/driver/EMKE.VirtualAudio/src \
  -o /tmp/emke-driver-bridge-tests &&
  /tmp/emke-driver-bridge-tests
```

Result:

```text
EMKE driver bridge behavior tests passed (6 cases).
```

The same sources are registered as `EMKE.DriverBridge.Tests` in CMake/CTest for
the hosted Windows build.

### RED: stamped-byte staging and shared INF boundary

The executable package-boundary tests were added before the staging and INF
validator tools.

Command:

```text
node --test Windows/driver/tests/package-boundary.test.mjs
```

Expected pre-fix result:

```text
tests 7
pass 0
fail 7
```

Each failure reported `MODULE_NOT_FOUND` for
`Windows/tools/stage-driver-package.mjs` or
`Windows/tools/validate-driver-contract.mjs`. The old script copied the source
INF and directly removed the artifact directory, so it could not satisfy the
exact-byte, stamped-INF, divergent-contract, outside-root, or symlink tests.

### GREEN: executable package and INF boundaries

Command:

```text
node --test \
  Windows/driver/tests/driver-contract.test.mjs \
  Windows/driver/tests/package-boundary.test.mjs
```

Result:

```text
tests 17
pass 17
fail 0
```

The seven executable package tests prove exact INF/SYS byte copying, unresolved
token rejection, symlink referent preservation, outside-root rejection,
acceptance of the real shared-header/INF boundary, and rejection of role,
property-key, and ABI mutations. The ten static contract tests remain a
supplement for project/workflow source boundaries.

### RED: driver catalog membership

The prior authorized Windows run is the pre-fix executable RED evidence:

```text
GitHub Actions run 30208348363
driver job 89810241681
Test-FileCatalog: Unable to open catalog file
```

That run had already passed pinned restore, WDK compile, Universal
ApiValidator, and Inf2Cat. It proved the previous `Test-FileCatalog` mechanism
could not verify the generated unsigned driver catalog. No package artifact was
uploaded.

The new Windows-only integration test cannot execute on this macOS host. It is
therefore a required remote gate, not a local success claim.

### GREEN pending remotely: catalog original/mutations

Required command after controller push:

```powershell
pwsh Windows/driver/tests/package-verifier.integration.ps1 `
  -PackageDirectory Windows/artifacts/driver/x64/Release `
  -Verifier Windows/tools/verify-driver-package.ps1
```

Expected:

```text
Package verifier integration tests passed: original valid; mutated INF/SYS rejected.
```

This result is not yet claimed.

## Files changed

- Driver data path and lifecycle:
  - added `Windows/driver/EMKE.VirtualAudio/src/emke_audio_bridge.h`
  - added `Windows/driver/EMKE.VirtualAudio/src/emke_audio_bridge.cpp`
  - updated adapter, miniport-pair, WaveRT miniport/stream, topology, common,
    project, and format-table sources
  - removed `ToneGenerator.*` and `savedata.*`
- Shared driver/native authority:
  - added `Windows/shared/emke_endpoint_contract.h`
  - updated native CMake and device-catalog sources
  - removed the duplicate driver role and native property headers
- Executable/compiled tests:
  - added `Windows/driver/tests/bridge-behavior-tests.cpp`
  - added `Windows/driver/tests/package-boundary.test.mjs`
  - added `Windows/driver/tests/package-verifier.integration.ps1`
  - updated `Windows/driver/tests/driver-contract.test.mjs`
  - registered the bridge test in native CTest
- Packaging and workflow:
  - added `Windows/tools/stage-driver-package.mjs`
  - added `Windows/tools/validate-driver-contract.mjs`
  - updated `build-driver.ps1`, `verify-driver-package.ps1`, and
    `.github/workflows/windows-audio.yml`
- Provenance:
  - updated `Windows/driver/THIRD_PARTY_NOTICES.md`

Implementation commit:

```text
1030d50f174c9d83e38588eeaeb361930150d1c6
fix: implement bounded Windows audio bridges
```

This report is committed separately so its own commit cannot be embedded in
itself; the controller handoff includes that exact report commit SHA.

## Fresh local verification

All commands ran from
`/Users/hale/Documents/Eager DEV/Emke Translation/.worktrees/windows-audio-foundation`.

```text
bridge behavior executable
  PASS: EMKE driver bridge behavior tests passed (6 cases).

node driver/package tests
  PASS: 17 tests, 17 pass, 0 fail

node Scripts/validate-shared-contracts.mjs
  PASS: contract v1: 3 schemas, 8 fixtures

clang++ -std=c++20 -Wall -Wextra -Werror ... device_catalog.cpp
  PASS: exit 0

xmllint --noout Windows/driver/EMKE.VirtualAudio/EMKE.VirtualAudio.vcxproj
  PASS: exit 0

git diff --check
  PASS: no output
```

`cmake` and `pwsh` are not installed on this macOS host. No local result is
inferred for MSVC, WDK, PowerShell parsing/execution, Catalog APIs, or CTest on
Windows.

## Remote checks completed

Authorized run `30232259380` closes the Task 6 build/package gates: locked
WDK/SDK restore, Release x64 WDK build, Universal ApiValidator, native CTest,
deterministic stamped INF and matching SYS, Inf2Cat CAT generation, strict
original-package verification, independent INF/SYS mutation rejection, and
three-file Actions artifact upload all passed.

Driver signing, installation, loading, removal, device enumeration, live
WaveRT timing, endpoint audio routing, meeting-app behavior, Windows
application packaging/installation, and release acceptance remain outside this
proof.

## Risks and concerns

- The final authorized Windows run passed compilation/linking, deterministic
  StampInf, package generation, CryptCAT enumeration, SHA-1/SHA-256 exact
  multiset matching, original verification, and independent INF/SYS mutation
  rejection. This still proves package integrity only, not signing trust,
  installability, driver loading, or runtime audio behavior.
- The compiled portable test proves bridge semantics, but not PortCls/WaveRT
  scheduling, DMA timing, memory ordering on a live Windows audio stack, or
  audible meeting behavior.
- Resetting either endpoint intentionally discards the entire paired bridge.
  This is the fail-closed session boundary, but live validation should confirm
  it produces acceptable transition silence.
- The fixed 100 ms capacity and drop-newest overflow policy meet this task's
  bounded deterministic contract; production latency/glitch behavior still
  needs live measurement.
- No install/live proof is implied by a successful package build or retained
  Actions artifact.

## Safety boundary

No signing, driver installation, driver loading, driver removal, administrator
elevation, secret creation/use, self-hosted runner, public GitHub Release, push,
or public publication occurred during this remediation.

## Remediation cycle 2 — fix round 1

### Remote RED

Authorized GitHub Actions run `30230340453` tested report commit
`7c3d38de59e44e582113c42123d22893289ce28f`.

- `hosted-toolchain-proof`, job `89867894383`: passed.
- `driver-build-proof`, job `89867894346`: failed during WDK C++ compilation,
  before link/package/catalog verification.
- MSBuild summary: `20 Warning(s)`, `42 Error(s)`.
- `adapter.cpp:641` failed because `g_EmkeAudioBridges` and
  `EmkeAudioBridgeInitialize` were undeclared at their use.
- `emke_audio_bridge.h` unconditionally included `<cstddef>` and `<cstdint>`.
  Under WDK `/kernel /W4 /WX`, that pulled user-mode Visual C++ runtime headers
  into the kernel CRT include environment, produced macro-redefinition
  warnings promoted to errors, and left `std::size_t` unavailable.

This is a source defect, not a hosted-toolchain failure. Microsoft documents
that `/kernel` predefines `_KERNEL_MODE=1`, which is the boundary used by the
fix:
<https://learn.microsoft.com/en-us/cpp/build/reference/kernel-create-kernel-mode-binary?view=msvc-170>.

### Root cause and minimal production fix

- `adapter.cpp` used the bridge global/function without directly including
  `emke_audio_bridge.h`; the existing miniport include graph did not provide
  that declaration.
- The bridge header/source exposed user-mode standard-library types in every
  build mode. The kernel branch now uses only WDK-native `SIZE_T`, `ULONG`,
  `LONG`, and `LONGLONG`. The portable branch retains `<cstddef>`, `<cstdint>`,
  and `std::*` types.
- The bridge implementation now uses the cross-mode `EmkeSize` alias rather
  than `std::size_t`, so no standard namespace appears in the kernel-compiled
  implementation.
- No bridge routing, capacity, overrun, underrun, reset, format, package,
  catalog, signing, or installation behavior changed.

### TDD RED: kernel include boundary

Before the production change, four test shim headers were added:

- `Windows/driver/tests/kernel-compile-shim/ntddk.h`
- `Windows/driver/tests/kernel-compile-shim/devpropdef.h`
- `Windows/driver/tests/kernel-compile-shim/cstddef`
- `Windows/driver/tests/kernel-compile-shim/cstdint`

The two standard-header shims fail compilation if the kernel bridge reaches
user-mode `<cstddef>` or `<cstdint>`.

Command:

```text
clang++ -std=c++17 -Wall -Wextra -Werror \
  -D_WIN32 -D_KERNEL_MODE \
  -I Windows/driver/tests/kernel-compile-shim \
  -I Windows/shared \
  -I Windows/driver/EMKE.VirtualAudio/src \
  -c Windows/driver/EMKE.VirtualAudio/src/emke_audio_bridge.cpp \
  -o /tmp/emke-kernel-bridge.o
```

Expected RED:

```text
kernel-compile-shim/cstddef: error: kernel bridge compilation must not include
the user-mode C++ cstddef header
kernel-compile-shim/cstdint: error: kernel bridge compilation must not include
the user-mode C++ cstdint header
emke_audio_bridge.h: error: use of undeclared identifier 'std'
20 errors generated.
```

### GREEN and regression boundary

The same kernel-shaped compile command now exits `0` with no output.

`EMKE.DriverBridge.KernelBoundary` was added to native CMake. It recompiles the
production bridge with `_WIN32`, `_KERNEL_MODE`, the rejecting shims, and
`/W4 /WX` on MSVC (`-Wall -Wextra -Werror` elsewhere). The actual WDK
`/kernel` build remains authoritative.

The static driver-contract suite additionally requires:

- an explicit standard-library-free `_KERNEL_MODE` branch;
- no `std::` use in `emke_audio_bridge.cpp`;
- a direct bridge include at the adapter use site.

The portable compiled behavior test remains intact:

```text
EMKE driver bridge behavior tests passed (6 cases).
```

### Fresh local verification after fix round 1

```text
kernel-shaped production bridge compile
  PASS: exit 0, no output

portable production bridge behavior executable
  PASS: 6 cases

driver/package Node suites
  PASS: 18 tests, 18 pass, 0 fail

shared contracts
  PASS: contract v1: 3 schemas, 8 fixtures

portable device_catalog compile
  PASS: exit 0

driver project XML
  PASS: xmllint exit 0

git diff --check
  PASS: no output
```

`cmake`, `pwsh`, MSVC, and WDK remain unavailable on this macOS host. The
fix-round commit SHA is supplied in the controller handoff because a commit
cannot embed its own SHA.

### Remaining remote gate and safety boundary

A new controller-pushed run must prove the pinned Release x64 WDK build passes
the prior compiler boundary and then continue through Universal ApiValidator,
stamped INF/SYS staging, Inf2Cat, exact catalog membership, mutation rejection,
and seven-day Actions artifact upload. No such success is claimed locally.

No signing, installation, loading, removal, elevation, secret use, push, or
public release occurred in fix round 1.

### Controller follow-up: cross-compiler shim correction

Controller review correctly required the new CMake kernel boundary's test-only
`SIZE_T` definition to be unambiguously valid under real MSVC rather than
depending on a Clang/GCC-only builtin or host-dependent type inference. The
correction makes that compiler boundary explicit. A narrower compiled
type-width test simultaneously found another real cross-ABI defect: on the
local LP64 host, the shim's `long` and `unsigned long` aliases were 8 bytes
rather than the Windows 4-byte `LONG`/`ULONG` contract.

The test was added first:

```text
clang++ -std=c++17 -Wall -Wextra -Werror \
  -I Windows/driver/tests/kernel-compile-shim \
  -c Windows/driver/tests/kernel-compile-shim-types.cpp \
  -o /tmp/emke-kernel-shim-types.o
```

RED:

```text
static assertion failed: Windows ULONG must be 32-bit
expression evaluates to '8 == 4'
static assertion failed: Windows LONG must be 32-bit
expression evaluates to '8 == 4'
2 errors generated.
```

The test-only shim now has explicit compiler branches:

- MSVC/clang-cl: x64 `SIZE_T = unsigned __int64`, `ULONG = unsigned long`,
  `LONG = long`, and `LONGLONG = __int64`;
- non-MSVC portable boundary: `SIZE_T = __SIZE_TYPE__`,
  `ULONG = unsigned int`, `LONG = int`, and `LONGLONG = long long`.

`kernel-compile-shim-types.cpp` asserts the frozen x64/Windows widths
`8/4/4/8` and is compiled as part of
`EMKE.DriverBridge.KernelBoundary`, so the hosted MSVC native build checks the
MSVC branch directly.

GREEN:

```text
kernel shim type-width compile
  PASS: exit 0

kernel-shaped production bridge compile
  PASS: exit 0

portable bridge behavior
  PASS: 6 cases
```

This correction changes only test infrastructure. No driver production source,
runtime behavior, package logic, signing boundary, or install boundary changed.

## Remediation cycle 2 — fix round 2

### Remote RED after the kernel fix

Authorized GitHub Actions run `30230988562` tested commit
`2ac205ec827e800fe2ab3130fe7d3e4f63ee16f2`.

- `hosted-toolchain-proof`, job `89869720558`: passed completely.
- `driver-build-proof`, job `89869720549`: passed pinned WDK restore,
  `/kernel` compilation and linking, `ApiValidator='Universal'`,
  `DriverPackageTarget`, and Inf2Cat signability/CAT generation.
- The strict package verifier then failed at
  `Windows/tools/verify-driver-package.ps1:299`:

```text
DriverVer 1.58.51.568 does not agree with FileVersion 1.0.0.1.
```

The build log identifies the cause before compilation:

```text
StampInf:
stampinf.exe -d "*" -a "amd64" -v "*" -k "1.15" ... EMKE.VirtualAudio.inf
Stamping [Version] section with DriverVer=07/27/2026,1.58.51.568
```

Thus the source INF's frozen `DriverVer=07/26/2026,1.0.0.1` was copied for
stamping and then overwritten by the WDK project's default wildcard StampInf
arguments. The compiled SYS correctly retained `FileVersion 1.0.0.1`. The
verifier is working as designed and was not weakened.

Microsoft documents that the real `Inf` project item supplies StampInf
parameters through item metadata. `SpecifyDriverVerDirectiveDate` enables
`-d`, `DateStamp` supplies the date, `SpecifyDriverVerDirectiveVersion` enables
`-v`, and `TimeStamp` supplies the four-part version:

- <https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/stampinf-task>
- <https://learn.microsoft.com/en-us/windows-hardware/drivers/develop/stampinf-properties-for-driver-projects>

The official package locked by this project was also inspected directly.
WDK `10.0.28000.2526` defines the metadata defaults in
`WindowsDriver.LateEvaluation.props` and maps
`%(Inf.SpecifyDriverVerDirectiveVersion)` plus `%(Inf.TimeStamp)` into the
StampInf task in `WindowsDriver.Common.targets`. This resolves the inconsistent
shorter spelling shown in one Learn table cell in favor of the metadata the
actual pinned WDK consumes.

### TDD RED: real project StampInf metadata

A project-boundary test was added first against the actual
`EMKE.VirtualAudio.vcxproj` `Inf` item. It requires the enabling metadata and
the exact reproducible values `07/26/2026` and `1.0.0.1`, and rejects wildcard
metadata.

Command:

```text
node --test Windows/driver/tests/driver-contract.test.mjs
```

RED:

```text
tests 12
pass 11
fail 1
AssertionError: the real driver INF item must declare StampInf metadata
```

The existing project used the self-closing item
`<Inf Include="EMKE.VirtualAudio.inf" />`, so the failure was specific to the
remote root cause.

### Minimal production fix and GREEN

Only the real INF item gained the official StampInf metadata:

```xml
<Inf Include="EMKE.VirtualAudio.inf">
  <SpecifyDriverVerDirectiveDate>true</SpecifyDriverVerDirectiveDate>
  <DateStamp>07/26/2026</DateStamp>
  <SpecifyDriverVerDirectiveVersion>true</SpecifyDriverVerDirectiveVersion>
  <TimeStamp>1.0.0.1</TimeStamp>
</Inf>
```

This makes the expected WDK invocation deterministic as
`-d "07/26/2026" -v "1.0.0.1"`. The date is valid, reproducible, and matches
the source INF; the four-part version matches both the source INF and SYS
resource. `verify-driver-package.ps1`, staging, catalog validation, signing,
installation, audio runtime, and shared contracts are unchanged.

GREEN:

```text
driver/package Node suites
  PASS: 19 tests, 19 pass, 0 fail

kernel shim type-width compile
  PASS: exit 0

kernel-shaped production bridge compile
  PASS: exit 0

portable bridge behavior
  PASS: 6 cases

shared contracts
  PASS: contract v1: 3 schemas, 8 fixtures

portable device_catalog compile
  PASS: exit 0

driver project XML
  PASS: xmllint exit 0

git diff --check
  PASS: no output
```

The fix-round commit SHA is supplied in the controller handoff because a commit
cannot embed its own SHA.

### Remaining gate and safety boundary

A controller-pushed run must still show StampInf using the fixed date/version,
then pass the unchanged strict package verifier, independent INF/SYS mutation
rejection, and seven-day Actions artifact upload. No remote GREEN for this
metadata change is claimed yet.

No signing, installation, loading, removal, elevation, secret use, push, or
public release occurred in fix round 2.

## Remediation cycle 2 — fix round 3

### Remote RED after deterministic stamping

Authorized GitHub Actions run `30231445659` tested commit
`f40e5cf99aca08a78246741484d9b688046b3a4e`.

- `hosted-toolchain-proof`, job `89870987057`: passed completely.
- `driver-build-proof`, job `89870987108`: passed fixed StampInf
  (`-d "07/26/2026" -v "1.0.0.1"`), `/kernel` compilation/linking,
  Universal ApiValidator, DriverPackageTarget, Inf2Cat signability/CAT
  generation, and packaged INF contract validation.
- The unchanged strict verifier then failed at line 321:

```text
Catalog must contain exactly the packaged INF and SYS.
```

That message did not expose whether the defect was the C# structure/P/Invoke,
PowerShell array handling, or the assumed catalog member model. No fix was made
from that ambiguous RED.

### Diagnostic run and actual Inf2Cat v2 model

Diagnostics-only commit `f3939a5a2b8a05c06cbc985c517ee46108bb166e`
retained the original failure condition and added safe output for the C# array
length, PowerShell count, and each member's basename/reference tag. Control
characters are replaced and values are bounded to 512 characters.

Authorized diagnostic run `30231802598` produced:

- `hosted-toolchain-proof`, job `89871964361`: passed completely.
- `driver-build-proof`, job `89871964339`: passed the same WDK/package gates,
  then reported:

```text
Catalog enumeration diagnostic: C# member count=4; PowerShell wrapper count=4.
Catalog member[0]: filename='<empty>'; referenceTag='ECDFF0C81259205802827D29D92CBA23DD3F7A86'.
Catalog member[1]: filename='<empty>'; referenceTag='D00E70465D4BDC1AD386CAB5A516CDB923245270F173371F54F544CDB7318362'.
Catalog member[2]: filename='<empty>'; referenceTag='528B7BFC5184DECCC159D005D056AA70ED92D7CF74812B04ABB774E7F23291E0'.
Catalog member[3]: filename='<empty>'; referenceTag='1EDDD9B478C972346ABE98818C7D734659FA655E'.
```

This disproves both the nested PowerShell-array hypothesis and a structure
marshalling failure. CryptCAT successfully enumerates four Inf2Cat v2 members.
The 40/64/64/40 hexadecimal lengths establish SHA-1 and SHA-256 reference
members for each of the two packaged files, while `pwszFileName` is empty for
all four. That evidence rejected the pre-fix assumption of one named member per
packaged file; the final verifier uses the four-reference-tag model above.

The official API model used for the correction is:

- `CRYPTCATMEMBER.pwszReferenceTag` is the member reference tag, while
  `pwszFileName` is a separate nullable pointer-backed field:
  <https://learn.microsoft.com/en-us/windows/win32/api/mscat/ns-mscat-cryptcatmember>
- `CryptCATEnumerateMember` returns each member and advances using the prior
  returned pointer:
  <https://learn.microsoft.com/en-us/windows/win32/api/mscat/nf-mscat-cryptcatenumeratemember>
- `CryptCATAdminAcquireContext2` selects the catalog hash algorithm, and
  `CryptCATAdminCalcHashFromFileHandle2` hashes the exact open file bytes:
  <https://learn.microsoft.com/en-us/windows/win32/api/mscat/nf-mscat-cryptcatadminacquirecontext2>
  and
  <https://learn.microsoft.com/en-us/windows/win32/api/mscat/nf-mscat-cryptcatadmincalchashfromfilehandle2>.

### TDD RED: C# array to exact reference-tag multiset

`Windows/driver/tests/catalog-reference-set.test.ps1` is a locally executable
PowerShell boundary test. Its C# fixture returns the same public member shape
as production, including empty file names, and PowerShell passes that CLR array
to the production matcher. The hand-derived fixture contains two SHA-1-shaped
and two SHA-256-shaped literal tags.

Command:

```text
pwsh -NoProfile -File Windows/driver/tests/catalog-reference-set.test.ps1
```

RED before the production matcher existed:

```text
catalog-reference-set.test.ps1: The term
'Windows/tools/catalog-reference-set.ps1' is not recognized as a name of a
cmdlet, function, script file, or executable program.
```

The test requires order-independent acceptance of the exact four-tag multiset
and rejection of a duplicate replacing a required tag, an unknown replacement,
a missing tag, and an extra unknown tag.

### Minimal strict fix and local GREEN

- `CryptCATAdminAcquireContext2` and
  `CryptCATAdminCalcHashFromFileHandle2` now calculate both SHA-1 and SHA-256
  catalog hashes for the exact staged INF and SYS bytes.
- The production matcher normalizes case, sorts both multisets, requires equal
  cardinality, and compares every position. Missing, duplicate, or additional
  catalog members therefore fail closed.
- Matching depends only on the four computed reference tags. It does not depend
  on the empty Inf2Cat v2 `pwszFileName`, and it does not use regex catalog
  dumps, `Get-FileHash`, `certutil`, or `Test-FileCatalog`.
- Safe count/member diagnostics remain available on both success and failure.
- The hosted workflow runs the PowerShell/C# boundary test before building.
- The existing Windows-only integration still requires the original package to
  pass and independently mutated INF and SYS bytes to fail.

Local GREEN:

```text
catalog reference-set PowerShell/C# boundary
  PASS: exact unordered set accepted; duplicate/unknown/missing/extra rejected

driver/package Node suites
  PASS: 19 tests, 19 pass, 0 fail

PowerShell 7.6.4 parser
  PASS: verifier, matcher, boundary test, and mutation integration parse cleanly

kernel shim type-width compile
  PASS: exit 0

kernel-shaped production bridge compile
  PASS: exit 0

portable bridge behavior
  PASS: 6 cases

shared contracts
  PASS: contract v1: 3 schemas, 8 fixtures

portable device_catalog compile
  PASS: exit 0

driver project XML
  PASS: xmllint exit 0

git diff --check
  PASS: no output
```

The final fix-round commit SHA and complete fresh local gate results are
supplied in the controller handoff because a commit cannot embed its own SHA.

### Remote gate requested after local GREEN

A controller-pushed run must execute the two hash algorithms against the real
staged INF/SYS, accept the original catalog, reject both independent mutations,
and upload only the seven-day unsigned Actions artifact. No final remote GREEN
or artifact is claimed yet.

No signing, installation, loading, removal, elevation, secret use, or public
release occurred in fix round 3.

The requested gate was subsequently closed by final run `30232259380`; the
evidence and artifact handoff are recorded below.

## Final remote GREEN and artifact handoff

### Authorized hosted proof

GitHub Actions run `30232259380` tested final implementation commit
`44c3d08c0380d1bf48684034b42f4349ce14c398` and completed successfully.

- `hosted-toolchain-proof`, job `89873213950`: passed. The Windows native
  scaffold built, the kernel-shaped boundary compiled under MSVC, and all 10
  CTest cases passed, including `EMKE.DriverBridge.Behavior`.
- `driver-build-proof`, job `89873213931`: passed. Its evidence includes:
  - the PowerShell/C# reference-set boundary accepted the unordered exact set
    and rejected duplicate, unknown, missing, and extra members;
  - StampInf ran with `-d "07/26/2026" -v "1.0.0.1"` and emitted
    `DriverVer=07/26/2026,1.0.0.1`;
  - the Release x64 WDK `/kernel` build completed with `0 Warning(s)` and
    `0 Error(s)`;
  - ApiValidator reported `Driver is 'Universal'`;
  - DriverPackageTarget and Inf2Cat generated the package and catalog;
  - the shared INF contract passed for ABI 1 and all four endpoint roles;
  - CryptCAT enumerated four members and the exact staged INF/SYS
    SHA-1/SHA-256 reference multiset passed;
  - the original package passed strict verification with
    `signature status: NotSigned`;
  - independently mutated INF and SYS packages were both rejected, after which
    the integration test reported
    `original valid; mutated INF/SYS rejected`;
  - `actions/upload-artifact` uploaded exactly three files.

### Actions run artifact

```text
name:
  emke-virtual-audio-unsigned-44c3d08c0380d1bf48684034b42f4349ce14c398
artifact ID:
  8640495603
uploaded ZIP size:
  27200 bytes
uploaded ZIP SHA-256:
  6afd054093cdb9aefe9924716b023ff3492e7e01cc1715a352b4559e2ba4f45a
expires:
  2026-08-03T02:31:10Z
```

GitHub reports the artifact as unexpired. It is an Actions artifact attached to
the run, not a public GitHub Release asset, and has seven-day retention.

### Controller download and file verification

The controller downloaded the artifact contents to:

```text
.superpowers/sdd/2026-07-26-emke-windows-audio-foundation/artifacts/emke-virtual-audio-unsigned-44c3d08/
```

Fresh local size and SHA-256 verification:

| File | Size (bytes) | SHA-256 |
| --- | ---: | --- |
| `EMKE.VirtualAudio.sys` | 56,320 | `67029f12f54af3b29b3c78dc0b8f985970ae63667304fd8f2d3693885b68f986` |
| `EMKE.VirtualAudio.inf` | 8,210 | `d00e70465d4bdc1ad386cab5a516cdb923245270f173371f54f544cdb7318362` |
| `emke.virtualaudio.cat` | 1,188 | `82d4cf9a602396e2b7330cdb9860f3d991c04493408c400e480b85054fd983ba` |

The downloaded directory contains the raw unsigned driver package files only.
It is not an MSIX, EXE, MSI, or other Windows application installer.

### Final proof boundary

- No signing certificate was used; the verifier observed `NotSigned`.
- No driver installation, loading, removal, administrator elevation, or
  endpoint enumeration occurred.
- No virtual endpoint audio routing, WaveRT live timing, conference client, or
  meeting behavior was exercised.
- No distributable Windows application installer was built or installed as part
  of this artifact.
- No public GitHub Release or public artifact publication occurred.

Within those boundaries, Task 6 is ready for task review.

## Task-review fix round 4

### Status and implementation commit

`DONE_WITH_CONCERNS`

Implementation commit:

```text
8a3daeb4af4f7962bc8d995e9096674d96cc539c
fix: close Windows audio driver review gaps
```

The five Important findings are resolved locally. The two Minor findings are
explicitly deferred below because neither can be changed with trustworthy local
behavioral proof on this macOS host without increasing package-verifier or
staging risk. The report is committed separately, so its commit SHA is supplied
in the controller handoff rather than embedded in itself.

### Important 1: one numeric property-GUID authority

`Windows/shared/emke_endpoint_contract.h` no longer contains an independently
editable GUID string. Its eleven numeric GUID components are the sole
authority: `DEFINE_DEVPROPKEY` consumes those macros directly, and
`validate-driver-contract.mjs` reads the same numeric macros, range-checks
them, and formats the INF comparison value.

Executable RED before the production change:

```text
node --test --test-name-pattern='INF validator derives the GUID' \
  Windows/driver/tests/package-boundary.test.mjs

FAIL: mutating compiled GUID component 0 must invalidate the INF
stdout: Driver INF contract validation passed: ABI 1, 4 endpoint roles.
tests 1; pass 0; fail 1
```

Fresh GREEN:

```text
node --test --test-name-pattern='INF validator derives the GUID' \
  Windows/driver/tests/package-boundary.test.mjs

tests 1; pass 1; fail 0
```

The executable regression iterates all eleven compiled components. For each
component it mutates the actual numeric authority used by
`DEFINE_DEVPROPKEY`, runs the real INF validator, and requires fail-closed
rejection.

### Important 2: shared virtual-format authority reaches WASAPI

`virtual_audio_format.hpp` defines the native host's exact virtual-format value
directly from `emke_endpoint_contract.h`: 48,000 Hz, two channels, 32
container/valid bits, eight-byte block alignment, 384,000 bytes/second, and
IEEE Float tag 3. `is_exact_virtual_format` delegates to that value, and the
Windows `WAVEFORMATEXTENSIBLE` construction uses the same fields. The prior
independent `2u`, `32u`, and `8u` construction literals are removed.

Compiled RED before the production boundary existed:

```text
clang++ -std=c++20 -Wall -Wextra -Werror \
  Windows/native/EMKE.NativeAudio.Tests/src/virtual_format_contract_tests.cpp \
  -I Windows/shared -I Windows/native/EMKE.NativeAudio/src \
  -o /tmp/emke-virtual-format-contract-tests

fatal error: 'virtual_audio_format.hpp' file not found
```

Fresh GREEN:

```text
EMKE virtual format authority tests passed.
```

The compiled test asserts every construction field against the shared macros
and behaviorally rejects sample-rate, channel, format-tag, container-bit,
valid-bit, block-align, and byte-rate mutations. The production
`wasapi_stream.cpp` also compiles locally with `-std=c++20 -Wall -Wextra
-Werror`.

### Important 3: reset progress, ownership, and realtime fail-fast

The reset owner now atomically adds `bridge_reset_pending` to the current state
before waiting. That successful CAS both publishes the realtime gate and
selects the owner. New producer/consumer acquisition returns immediately while
the bit is present. The owner waits only for already-active producer/consumer
bits, resets the frame boundary, and releases the gate. Concurrent reset
callers observe the existing owner, wait for its completion, and return without
competing for idle before pending is visible.

Deterministic compiled RED against the old order:

```text
clang++ -std=c++17 -Wall -Wextra -Werror \
  Windows/driver/tests/bridge-behavior-tests.cpp \
  Windows/driver/EMKE.VirtualAudio/src/emke_audio_bridge.cpp \
  -I Windows/shared -I Windows/driver/EMKE.VirtualAudio/src \
  -o /tmp/emke-driver-bridge-tests &&
  /tmp/emke-driver-bridge-tests

FAIL: reset must publish pending while an existing producer is active
1 bridge behavior assertion(s) failed
```

The test is deterministic: it holds the real bridge's producer-active state,
starts reset, and observes the state boundary. It does not depend on a
probabilistic large-copy race. On RED it atomically releases the held bit before
joining, so the test itself cannot strand the old implementation.

Fresh GREEN:

```text
EMKE driver bridge behavior tests passed (7 cases).
```

GREEN additionally proves a new capture attempt returns immediately with
silence while reset is pending, two concurrent reset callers join without
deadlock after the held producer drains, the bridge returns to idle, and only
post-reset frames reach the new capture session.

### Important 4: notification buffer and packet frame alignment

`AllocateBufferWithNotification` now calls the production
`EmkeIsNotificationBufferValid` boundary before allocating. It rejects an
unaligned requested buffer and requires both total-buffer divisibility and
every notification packet to be an integral number of complete frames. It no
longer truncates the buffer after checking notification divisibility.

Compiled RED before the production boundary existed:

```text
clang++ -std=c++17 -Wall -Wextra -Werror \
  Windows/driver/tests/wave-buffer-contract-tests.cpp \
  -I Windows/shared -I Windows/driver/EMKE.VirtualAudio/src \
  -o /tmp/emke-wave-buffer-contract-tests

fatal error: '../EMKE.VirtualAudio/src/emke_wave_buffer_contract.h' file not found
```

Fresh GREEN:

```text
EMKE WaveRT buffer contract tests passed.
```

The executable includes the exact old-order counterexample: 18 requested bytes
are divisible by three notifications but are not aligned to the eight-byte
Float32 stereo frame. It must fail closed rather than truncate to 16 bytes and
create non-frame-aligned notification packets. Zero counts/alignment,
unaligned total buffers, and incomplete-frame packets are also rejected.

### Important 5: production identity mapping and DMA routing

The real `eDeviceType` definition and mapping now live in the lightweight
production unit `emke_bridge_routing.{h,cpp}`. `CMiniportWaveRT` calls that
mapping and returns `STATUS_INVALID_PARAMETER` for an invalid identity; the old
meeting-speaker fallback is removed. `CMiniportWaveRTStream::ReadBytes` and
`WriteBytes` now call the production `EmkeBridgeTransferDma` unit, so compiled
tests traverse the same mapping and frame-wrap loop as the driver hookup.

Compiled RED before the production unit existed:

```text
clang++ -std=c++17 -Wall -Wextra -Werror \
  Windows/driver/tests/bridge-routing-tests.cpp \
  Windows/driver/EMKE.VirtualAudio/src/emke_audio_bridge.cpp \
  Windows/driver/EMKE.VirtualAudio/src/emke_bridge_routing.cpp \
  -I Windows/shared -I Windows/driver/EMKE.VirtualAudio/src \
  -o /tmp/emke-driver-routing-tests

error: no such file or directory:
'Windows/driver/EMKE.VirtualAudio/src/emke_bridge_routing.cpp'
```

Fresh GREEN:

```text
EMKE production bridge routing tests passed (3 cases).
```

The compiled suite covers all four `eDeviceType` identities, forward and
reverse production mapping, invalid values without output mutation, both DMA
directions, Bridge A render/capture frame order across DMA wrap, Bridge B
render/capture movement, capture silence after reset, and post-reset new-session
delivery. The WDK project and hosted CMake/CTest graph compile the same
production routing unit; there is no test-only mapping.

### Minor adjudication

1. **Catalog cleanup return values — deferred.**
   `CryptCATClose` and `CryptCATAdminReleaseContext` return values remain
   unchecked. The calls remain in `finally`, and their return values are
   ignored, so a cleanup failure cannot replace the primary catalog
   enumeration/hash exception. The residual risk is a silently unreported
   handle-release failure in the short-lived verifier process. A trustworthy
   RED requires Windows Catalog APIs/PowerShell, which are unavailable on this
   host. This does not weaken strict four-tag SHA-1/SHA-256 membership,
   original-package acceptance, or independent INF/SYS mutation rejection and
   does not block a single-invocation hosted verifier. A later Windows-only
   change should use SafeHandle or report cleanup failure only when no primary
   exception is active.

2. **Staging await-based TOCTOU — deferred.**
   `stage-driver-package.mjs` retains the final repo-bound, resolved-path, and
   reparse/symlink checks before recursive cleanup. A safe atomic replacement
   must also preserve those checks for the unique staging directory, every
   parent segment, the destination at swap time, same-volume rename, and
   rollback/cleanup. A simple atomic rename would create a new opportunity to
   cross the repo/reparse boundary and was therefore not introduced. The
   residual risk is an attacker with concurrent filesystem-write access
   swapping a checked path during an `await`. The authorized GitHub-hosted job
   is single-tenant, uses a repository-owned artifact root, and exposes no
   concurrent untrusted writer; staged bytes are subsequently checked by the
   INF contract, exact catalog membership, and independent INF/SYS mutations.
   The residual local-adversary race therefore does not invalidate this hosted
   single-tenant build proof.

### Files changed in fix round 4

- Shared authority and validators:
  - `Windows/shared/emke_endpoint_contract.h`
  - `Windows/tools/validate-driver-contract.mjs`
  - `Windows/native/EMKE.NativeAudio/src/virtual_audio_format.hpp`
  - `Windows/native/EMKE.NativeAudio/src/wasapi_stream.cpp`
- Driver bridge, lifecycle, routing, and buffer validation:
  - `Windows/driver/EMKE.VirtualAudio/src/emke_audio_bridge.cpp`
  - `Windows/driver/EMKE.VirtualAudio/src/emke_bridge_routing.h`
  - `Windows/driver/EMKE.VirtualAudio/src/emke_bridge_routing.cpp`
  - `Windows/driver/EMKE.VirtualAudio/src/emke_wave_buffer_contract.h`
  - `Windows/driver/EMKE.VirtualAudio/src/common.h`
  - `Windows/driver/EMKE.VirtualAudio/src/minwavert.h`
  - `Windows/driver/EMKE.VirtualAudio/src/minwavertstream.cpp`
  - `Windows/driver/EMKE.VirtualAudio/EMKE.VirtualAudio.vcxproj`
- Compiled/executable tests and hosted registration:
  - `Windows/driver/tests/bridge-behavior-tests.cpp`
  - `Windows/driver/tests/bridge-routing-tests.cpp`
  - `Windows/driver/tests/wave-buffer-contract-tests.cpp`
  - `Windows/driver/tests/package-boundary.test.mjs`
  - `Windows/driver/tests/driver-contract.test.mjs`
  - `Windows/native/EMKE.NativeAudio.Tests/src/virtual_format_contract_tests.cpp`
  - `Windows/native/EMKE.NativeAudio.Tests/CMakeLists.txt`

### Fresh local verification after implementation

All commands ran from the required worktree. Results:

```text
compiled bridge behavior
  PASS: EMKE driver bridge behavior tests passed (7 cases).

compiled production identity/DMA routing
  PASS: EMKE production bridge routing tests passed (3 cases).

compiled notification buffer contract
  PASS: EMKE WaveRT buffer contract tests passed.

compiled shared/native virtual format
  PASS: EMKE virtual format authority tests passed.

production wasapi_stream.cpp portable compile
  PASS: clang++ C++20 -Wall -Wextra -Werror, exit 0

kernel-shaped emke_audio_bridge.cpp compile
  PASS: clang++ C++17 -D_WIN32 -D_KERNEL_MODE -Wall -Wextra -Werror, exit 0

kernel-shaped emke_bridge_routing.cpp compile
  PASS: clang++ C++17 -D_WIN32 -D_KERNEL_MODE -Wall -Wextra -Werror, exit 0

kernel shim type-width compile
  PASS: exit 0

driver/package Node suites
  PASS: 20 tests, 20 pass, 0 fail

shared contracts
  PASS: contract v1: 3 schemas, 8 fixtures

portable device_catalog.cpp compile
  PASS: clang++ C++20 -Wall -Wextra -Werror, exit 0

driver project XML
  PASS: xmllint exit 0

git diff --check
  PASS: no output
```

`cmake`, `pwsh`, MSVC, WDK, and Windows Catalog APIs are unavailable on this
macOS host. No result is inferred for those tools.

### Remaining remote and live gates

A controller push must run the unchanged authorized workflow against
`8a3daeb4af4f7962bc8d995e9096674d96cc539c` plus this report commit and prove:

- hosted MSVC builds and all CTest targets, including the new format, reset,
  routing/DMA, and WaveRT buffer-contract executables;
- pinned Release x64 WDK `/kernel` compile/link with zero errors;
- deterministic StampInf `1.0.0.1`, Universal ApiValidator,
  DriverPackageTarget, and Inf2Cat;
- exact flat INF/SYS/CAT staging;
- strict four-tag SHA-1/SHA-256 catalog membership;
- independent mutated-INF and mutated-SYS rejection;
- exactly the three unsigned package files in a private seven-day Actions
  artifact.

Signing, installation, driver loading/removal, Windows 11 25H2 physical-machine
acceptance, live endpoint enumeration/routing, WaveRT timing, human listening,
conference application behavior, and real meeting acceptance remain pending
and cannot be inferred from hosted build/package proof.

### Safety boundary for fix round 4

No push, signing, driver installation, driver loading/removal, administrator
elevation, secret creation/use, self-hosted runner, public GitHub Release, or
public artifact publication occurred in fix round 4.
