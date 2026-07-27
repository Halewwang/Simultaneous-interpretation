# Task 6 remediation report

## Status

`DONE_WITH_CONCERNS`

The locally provable remediation is implemented and committed. Compiled bridge
behavior, package-boundary behavior, shared contracts, native-header
compatibility, project XML, and whitespace gates pass on macOS. Windows WDK
compilation, the runtime catalog P/Invoke verifier, the private Actions
artifact, driver installation, and live endpoint/meeting behavior remain
explicitly unproved.

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
  `CryptCATAdminCalcHashFromFileHandle2`. It requires exactly two catalog
  members and matches both member filename and catalog hash against the staged
  INF/SYS bytes. A Windows-only integration test requires the original package
  to pass and independently mutated INF and SYS copies to fail.

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

## Remote-only checks still pending

After the controller pushes the branch, the authorized hosted workflow must
prove:

1. locked restore of WDK/SDK `10.0.28000.2526`;
2. Release x64 WDK compile with zero errors;
3. ApiValidator reports `Universal`;
4. the compiled bridge/format CTest passes under MSVC;
5. WDK emits one stamped INF and matching SYS;
6. Inf2Cat emits one CAT for those exact bytes;
7. the flat verifier accepts the original package;
8. independently mutated INF and SYS copies are rejected by catalog
   membership;
9. the package contains exactly one INF, one SYS, and one CAT and uploads only
   as a private Actions artifact with seven-day retention.

Driver signing, installation, loading, removal, device enumeration, live
WaveRT timing, endpoint audio routing, meeting-app behavior, and release
acceptance remain pending even if the hosted build passes.

## Risks and concerns

- The WDK/MSVC build and handwritten C# catalog P/Invoke declarations are
  source-reviewed but cannot be executed on macOS. A remote failure must be
  treated as an implementation defect, not weakened validation.
- The compiled portable test proves bridge semantics, but not PortCls/WaveRT
  scheduling, DMA timing, memory ordering on a live Windows audio stack, or
  audible meeting behavior.
- Resetting either endpoint intentionally discards the entire paired bridge.
  This is the fail-closed session boundary, but live validation should confirm
  it produces acceptable transition silence.
- The fixed 100 ms capacity and drop-newest overflow policy meet this task's
  bounded deterministic contract; production latency/glitch behavior still
  needs live measurement.
- No install/live proof is implied by a successful package build or private
  artifact.

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
and private seven-day artifact upload. No such success is claimed locally.

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
