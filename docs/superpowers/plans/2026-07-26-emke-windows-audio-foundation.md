# EMKE Windows Audio Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver a Windows 11 25H2 x64 virtual-audio and native-WASAPI vertical slice that proves four-endpoint routing, bounded buffers, fail-open inbound audio, and fail-closed outbound audio without requiring the Translation service or WPF UI.

**Architecture:** A SYSVAD-derived WaveRT driver owns two endpoint pairs. `EMKE.NativeAudio` owns MMDevice discovery, exclusive endpoint-role validation, WASAPI event loops, format conversion, bounded SPSC rings, route selection, and a versioned C ABI. All realtime callbacks remain native and allocation-free; a console smoke harness and native tests drive synthetic PCM and failure injection.

**Tech Stack:** Windows 11 25H2 build 26200+, x64, Visual Studio 2026, WDK 28000 family, `Microsoft.Windows.WDK.x64` 10.0.28000.2526 for CI, C++20, C17 ABI, WASAPI, MMDevice API, WaveRT/SYSVAD, CMake 4.2+, CTest, PowerShell 7

## Global Constraints

- Execute this plan on a dedicated Windows 11 25H2+ x64 builder and physical lab machine; macOS cannot prove WDK or endpoint behavior.
- Root hardware ID is `ROOT\EMKEVIRTUALAUDIO`.
- Driver ABI starts at `1`; every exported C struct begins with `size` and `abiVersion`.
- Driver exposes four technical endpoint roles: meeting speaker render, app speaker capture, app microphone render, and meeting microphone capture.
- Only `EMKE Virtual Speaker` and `EMKE Virtual Microphone` are user-facing meeting-device names.
- Virtual formats are fixed at 48 kHz stereo Float32.
- Local processing uses at most 480 frames per cycle.
- Capture rings hold 4,800 frames; translated playback rings hold 96,000 frames.
- Managed callbacks, heap allocation, network access, JSON parsing, file I/O, and blocking locks are forbidden on WaveRT and WASAPI event threads.
- Inbound failures route original meeting audio to the physical output.
- Outbound failures and underruns write zeros; physical microphone audio never leaks unless explicit bypass is active.
- Test signing is allowed only for internal lab evidence. Stable publication requires Microsoft signing and the later delivery plan.
- Do not install or remove a driver without an explicit lab step and administrator confirmation.

---

### Task 1: Scaffold the Native Solution and Reproducible Toolchain

**Files:**
- Create: `Windows/EMKE.Windows.slnx`
- Create: `Windows/Directory.Build.props`
- Create: `Windows/native/CMakeLists.txt`
- Create: `Windows/native/CMakePresets.json`
- Create: `Windows/native/EMKE.NativeAudio/CMakeLists.txt`
- Create: `Windows/native/EMKE.NativeAudio.Tests/CMakeLists.txt`
- Create: `Windows/tools/verify-toolchain.ps1`
- Create: `docs/quality/windows-audio-toolchain.md`

**Interfaces:**
- Produces: Release x64 native library and test executable.
- Build output: `Windows/artifacts/native/x64/Release/`.

- [ ] **Step 1: Verify the Windows execution environment**

Create `Windows/tools/verify-toolchain.ps1`:

```powershell
param(
    [switch]$RequireTargetOs,
    [switch]$RequireInstalledWdk
)

$ErrorActionPreference = "Stop"
$requiredBuild = 26200
$build = [Environment]::OSVersion.Version.Build
if ($RequireTargetOs -and $build -lt $requiredBuild) {
    throw "Windows build $build is below required build $requiredBuild"
}

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere.exe not found" }
$install = & $vswhere -latest -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath
if (-not $install) { throw "Visual C++ x64 tools not found" }

$cmakeText = (& cmake --version | Select-Object -First 1)
$cmakeVersion = [version]($cmakeText -replace '^cmake version\s+', '')
if ($cmakeVersion -lt [version]"4.2") {
    throw "CMake $cmakeVersion does not support Visual Studio 18 2026"
}

$wdkVersion = "NuGet-managed"
if ($RequireInstalledWdk) {
    $kitsRoot = Get-ItemPropertyValue `
        "HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots" `
        -Name KitsRoot10
    $wdkVersion = Get-ChildItem "$kitsRoot\Include" -Directory |
        Where-Object Name -Match '^10\.0\.28000\.' |
        Sort-Object Name -Descending |
        Select-Object -First 1 -ExpandProperty Name
    if (-not $wdkVersion) { throw "Installed WDK 28000 not found" }
}

[ordered]@{
    windowsBuild = $build
    visualStudio = $install
    cmake = $cmakeVersion.ToString()
    wdk = $wdkVersion
    architecture = $env:PROCESSOR_ARCHITECTURE
    targetOsEligible = ($build -ge $requiredBuild)
} | ConvertTo-Json
```

Run:

```powershell
pwsh Windows/tools/verify-toolchain.ps1 `
  -RequireTargetOs `
  -RequireInstalledWdk
```

Expected: build is at least 26200, architecture is AMD64, and WDK starts with 10.0.28000.

- [ ] **Step 2: Create native presets**

Configure `Windows/native/CMakePresets.json` with:

```text
configure preset: windows-x64-release
generator: Visual Studio 18 2026
architecture: x64
binaryDir: Windows/out/native/x64-release
CMAKE_CXX_STANDARD: 20
CMAKE_MSVC_RUNTIME_LIBRARY: MultiThreaded$<$<CONFIG:Debug>:Debug>
build preset: windows-x64-release / Release
test preset: windows-x64-release / Release / outputOnFailure
```

The root native CMake file must add:

```text
EMKE.NativeAudio
EMKE.NativeAudio.Tests
EMKE.AudioSmoke
```

- [ ] **Step 3: Create the solution**

Run from the repository root:

```powershell
dotnet new sln --format slnx --name EMKE.Windows --output Windows
cmake --preset windows-x64-release -S Windows/native
cmake --build --preset windows-x64-release
ctest --preset windows-x64-release
```

Expected: configuration succeeds and the empty test executable exits 0.

- [ ] **Step 4: Record toolchain identity**

Create `docs/quality/windows-audio-toolchain.md` with observed OS build, Visual Studio installation version, WDK version, CMake version, compiler version, and the git commit. Do not include usernames or absolute machine paths.

- [ ] **Step 5: Commit scaffolding**

```powershell
git add Windows/EMKE.Windows.slnx Windows/Directory.Build.props Windows/native Windows/tools/verify-toolchain.ps1 docs/quality/windows-audio-toolchain.md
git commit -m "build: scaffold Windows native audio foundation"
```

### Task 2: Implement Versioned C ABI and a Deterministic Fake Backend

**Files:**
- Create: `Windows/native/EMKE.NativeAudio/include/emke_native_audio.h`
- Create: `Windows/native/EMKE.NativeAudio/src/native_audio_api.cpp`
- Create: `Windows/native/EMKE.NativeAudio/src/audio_runtime.hpp`
- Create: `Windows/native/EMKE.NativeAudio/src/audio_runtime.cpp`
- Create: `Windows/native/EMKE.NativeAudio/src/fake_audio_backend.hpp`
- Create: `Windows/native/EMKE.NativeAudio/src/fake_audio_backend.cpp`
- Create: `Windows/native/EMKE.NativeAudio.Tests/src/abi_tests.cpp`
- Create: `Windows/native/EMKE.NativeAudio.Tests/src/test_main.cpp`

**Interfaces:**
- C# later consumes only `emke_native_audio.h`.
- Fake backend drives all state transitions without physical devices.

- [ ] **Step 1: Write failing ABI layout tests**

Tests must assert:

```text
EMKE_AUDIO_ABI_VERSION == 1
every public struct contains size and abi_version first
unknown smaller struct size returns EMKE_AUDIO_INVALID_ARGUMENT
unknown ABI returns EMKE_AUDIO_ABI_MISMATCH
create returns one opaque handle
destroy is safe after failed create and after stop
```

Use a local test runner whose `main` executes registered test functions and returns the number of failed assertions. Do not add a package manager or network dependency.

- [ ] **Step 2: Define the exact public ABI**

`emke_native_audio.h` must expose:

```c
#define EMKE_AUDIO_ABI_VERSION 1u

typedef struct emke_audio_handle emke_audio_handle;

typedef enum emke_audio_status {
    EMKE_AUDIO_OK = 0,
    EMKE_AUDIO_INVALID_ARGUMENT = 1,
    EMKE_AUDIO_ABI_MISMATCH = 2,
    EMKE_AUDIO_DEVICE_MISSING = 3,
    EMKE_AUDIO_FORMAT_UNSUPPORTED = 4,
    EMKE_AUDIO_QUEUE_FULL = 5,
    EMKE_AUDIO_NOT_RUNNING = 6,
    EMKE_AUDIO_INTERNAL_ERROR = 7
} emke_audio_status;

typedef enum emke_audio_route {
    EMKE_AUDIO_ROUTE_STOPPED = 0,
    EMKE_AUDIO_ROUTE_TRANSLATED = 1,
    EMKE_AUDIO_ROUTE_ORIGINAL_FAIL_OPEN = 2,
    EMKE_AUDIO_ROUTE_ORIGINAL_BYPASS = 3,
    EMKE_AUDIO_ROUTE_MUTED_FAIL_CLOSED = 4
} emke_audio_route;

typedef enum emke_audio_event_kind {
    EMKE_AUDIO_EVENT_NONE = 0,
    EMKE_AUDIO_EVENT_INBOUND_PCM16 = 1,
    EMKE_AUDIO_EVENT_OUTBOUND_PCM16 = 2,
    EMKE_AUDIO_EVENT_DEVICE_CHANGED = 3,
    EMKE_AUDIO_EVENT_STREAM_ERROR = 4,
    EMKE_AUDIO_EVENT_BACKPRESSURE = 5
} emke_audio_event_kind;
```

Configuration contains fixed-size UTF-16 endpoint-ID buffers for physical input/output and four virtual endpoint roles. No ABI struct owns a pointer after the call returns.

Export these functions with `extern "C"` and `__declspec(dllexport)`:

```text
emke_audio_create
emke_audio_destroy
emke_audio_start
emke_audio_stop
emke_audio_set_inbound_route
emke_audio_set_outbound_route
emke_audio_enqueue_inbound_translation
emke_audio_enqueue_outbound_translation
emke_audio_poll_event
emke_audio_get_diagnostics
```

- [ ] **Step 3: Implement the fake backend**

The fake backend must:

```text
own no OS handles
accept synthetic 48 kHz stereo Float32 blocks
emit 24 kHz mono PCM16 capture events
consume translated PCM16
expose deterministic device failure and underrun injection
count dropped frames and queue-full events
write zeros on outbound underrun
route original inbound on injected inbound translation failure
```

- [ ] **Step 4: Run tests**

```powershell
cmake --build --preset windows-x64-release
ctest --preset windows-x64-release -R "Abi|FakeBackend"
```

Expected: all ABI and fake backend tests pass.

- [ ] **Step 5: Commit the ABI**

```powershell
git add Windows/native/EMKE.NativeAudio Windows/native/EMKE.NativeAudio.Tests
git commit -m "feat: add versioned native audio ABI"
```

### Task 3: Implement Bounded Rings and PCM Conversion

**Files:**
- Create: `Windows/native/EMKE.NativeAudio/src/spsc_ring.hpp`
- Create: `Windows/native/EMKE.NativeAudio/src/pcm_converter.hpp`
- Create: `Windows/native/EMKE.NativeAudio/src/pcm_converter.cpp`
- Create: `Windows/native/EMKE.NativeAudio.Tests/src/spsc_ring_tests.cpp`
- Create: `Windows/native/EMKE.NativeAudio.Tests/src/pcm_converter_tests.cpp`
- Consume: `Shared/TestVectors/Audio/pcm-batching.json`
- Consume: `Shared/TestVectors/Audio/pcm-conversion.json`

**Interfaces:**
- Ring element is a fixed 480-frame block with frame count and monotonic timestamp.
- Converter boundary is 48 kHz stereo Float32 ↔ 24 kHz mono PCM16.

- [ ] **Step 1: Write failing bounded-ring tests**

Cover:

```text
capacity is fixed after construction
single producer/single consumer preserves order
write beyond capacity fails without overwrite
read from empty returns false
wraparound preserves frame count and timestamp
clear resets indices without allocation
```

Add a compile-time assertion that the callback-facing `push` and `pop` are `noexcept`.

- [ ] **Step 2: Implement the SPSC ring**

Use one preallocated contiguous vector created before stream start, cache-line-separated atomic read/write indices, acquire/release ordering, and no mutex. Never resize after construction.

- [ ] **Step 3: Write failing conversion tests**

Read both shared audio fixtures and add native-only tests for:

```text
two-frame average downsample
PCM16 clamp and little-endian bytes
127-tap Blackman-windowed streaming interpolation
chunked and contiguous output equality within 1e-6
400 ms / 19,200-byte translated block produces the complete local output
decoder reset clears FIR history
```

- [ ] **Step 4: Implement conversion**

Implementation constants:

```text
networkSampleRate = 24000
localSampleRate = 48000
localChannelCount = 2
networkChannelCount = 1
firTapCount = 127
firGroupDelaySamplesAt48k = 63
localBlockFrames = 480
```

Precompute FIR coefficients at construction. Processing functions accept spans provided by the caller and write into caller-owned spans.

- [ ] **Step 5: Run native tests and commit**

```powershell
cmake --build --preset windows-x64-release
ctest --preset windows-x64-release -R "Ring|PCM"
git add Windows/native/EMKE.NativeAudio Shared
git commit -m "feat: add bounded PCM pipeline"
```

### Task 4: Implement MMDevice Discovery and Stable Endpoint Roles

**Files:**
- Create: `Windows/native/EMKE.NativeAudio/src/device_catalog.hpp`
- Create: `Windows/native/EMKE.NativeAudio/src/device_catalog.cpp`
- Create: `Windows/native/EMKE.NativeAudio/src/device_notifications.hpp`
- Create: `Windows/native/EMKE.NativeAudio/src/device_notifications.cpp`
- Create: `Windows/native/EMKE.NativeAudio.Tests/src/device_catalog_tests.cpp`

**Interfaces:**
- Persisted identity is MMDevice endpoint ID.
- Driver endpoint role is read from a stable endpoint property, never inferred from display name.

- [ ] **Step 1: Write fake-MMDevice tests**

Cover:

```text
four distinct virtual roles are required
duplicate role blocks readiness
missing role blocks readiness
physical endpoint ID resolves after re-enumeration
missing saved physical endpoint does not silently switch while running
follow-default setting permits default-device migration
notification callback copies ID/type and returns without enumeration
```

- [ ] **Step 2: Define stable role identifiers**

Use these role strings in the driver INF property store and native catalog:

```text
emke.meeting-speaker.render
emke.app-speaker.capture
emke.app-microphone.render
emke.meeting-microphone.capture
```

Use this interface property key name in source and documentation:

```text
DEVPKEY_EMKE_EndpointRole
```

Define its GUID and property ID once in a shared native header used by driver and host. Never use friendly names for routing decisions.

- [ ] **Step 3: Implement real enumeration**

Use `IMMDeviceEnumerator::EnumAudioEndpoints`, activate property stores, read endpoint ID/state/data flow/role property, and return immutable value objects. COM pointers stay inside the catalog implementation.

- [ ] **Step 4: Implement notifications**

`IMMNotificationClient` callbacks copy only:

```text
event kind
endpoint ID
new state when present
monotonic sequence
```

into a bounded queue. Background code performs re-enumeration.

- [ ] **Step 5: Run tests and commit**

```powershell
cmake --build --preset windows-x64-release
ctest --preset windows-x64-release -R "Device"
git add Windows/native/EMKE.NativeAudio
git commit -m "feat: add Windows audio device catalog"
```

### Task 5: Implement WASAPI Event Streams and Native Worker

**Files:**
- Create: `Windows/native/EMKE.NativeAudio/src/wasapi_stream.hpp`
- Create: `Windows/native/EMKE.NativeAudio/src/wasapi_stream.cpp`
- Create: `Windows/native/EMKE.NativeAudio/src/audio_worker.hpp`
- Create: `Windows/native/EMKE.NativeAudio/src/audio_worker.cpp`
- Create: `Windows/native/EMKE.NativeAudio.Tests/src/audio_worker_tests.cpp`

**Interfaces:**
- Four WASAPI streams move endpoint-native samples only.
- One native worker performs conversion, routing, batching, and event publication.

- [ ] **Step 1: Write failing lifecycle tests**

With fake streams, prove exact start order:

```text
physical output
app microphone render
app speaker capture
physical microphone
worker
```

Prove failure at step N rolls back only steps `0..<N` in reverse order.

- [ ] **Step 2: Implement event-driven streams**

Each stream must:

```text
activate IAudioClient3 where available, otherwise IAudioClient
negotiate physical native format
use event callbacks
preallocate packet and ring memory
move samples only
record HRESULT and endpoint role on failure
avoid logging inside callbacks
```

Virtual endpoints reject any format other than 48 kHz stereo IEEE Float.

- [ ] **Step 3: Implement the worker**

The worker:

```text
normalizes physical formats to 48 kHz stereo Float32
creates 24 kHz mono PCM16 events
batches exactly 9,600 bytes
accepts full translated chunks up to the 96,000-frame capacity
applies inbound and outbound routes atomically at block boundaries
fills outbound underrun with zeros
publishes at most 64 pending control/audio events
increments diagnostics instead of growing queues
```

- [ ] **Step 4: Add allocation and blocking instrumentation**

In Debug builds, wrap callback entry/exit with a thread-local realtime marker. Assert if callback code invokes the project allocation hook or blocking-lock wrapper. Keep this instrumentation out of Release timing.

- [ ] **Step 5: Run tests and commit**

```powershell
cmake --build --preset windows-x64-release
ctest --preset windows-x64-release -R "Lifecycle|Worker|Realtime"
git add Windows/native/EMKE.NativeAudio
git commit -m "feat: add native WASAPI audio worker"
```

### Task 6: Derive and Build the Virtual Audio Driver

**Files:**
- Create: `Windows/driver/THIRD_PARTY_NOTICES.md`
- Create: `Windows/driver/EMKE.VirtualAudio/EMKE.VirtualAudio.vcxproj`
- Create: `Windows/driver/EMKE.VirtualAudio/EMKE.VirtualAudio.inf`
- Create: `Windows/driver/EMKE.VirtualAudio/src/`
- Create: `Windows/driver/EMKE.VirtualAudio/include/emke_endpoint_roles.h`
- Create: `Windows/tools/build-driver.ps1`
- Create: `Windows/tools/verify-driver-package.ps1`

**Interfaces:**
- Driver package output: `Windows/artifacts/driver/x64/Release/`.
- Four endpoint roles bridge meeting render → app capture and app render → meeting capture.

- [ ] **Step 1: Record SYSVAD provenance before copying code**

Clone the official Microsoft Windows driver samples repository outside this repository, resolve its current commit, and record:

```text
repository URL
resolved commit SHA
SYSVAD source directories used
Microsoft sample license
local modifications
```

in `Windows/driver/THIRD_PARTY_NOTICES.md`. Copy only the required SYSVAD files into `Windows/driver/EMKE.VirtualAudio/src/`; do not add the samples repository as a moving submodule.

- [ ] **Step 2: Create the driver project and INF**

Set:

```text
PlatformToolset = WindowsKernelModeDriver10.0
ConfigurationType = Driver
DriverType = KMDF
TargetVersion = Windows11
MinimumVisualStudioVersion = 18.0
Platform = x64
Root hardware ID = ROOT\EMKEVIRTUALAUDIO
Driver ABI property = 1
```

Pin this CI build dependency in the driver project:

```xml
<PackageReference
  Include="Microsoft.Windows.WDK.x64"
  Version="10.0.28000.2526"
  GeneratePathProperty="true" />
```

Commit the generated NuGet lock file. Build driver projects with MSBuild; the
official WDK guidance does not support `dotnet build` for WDK projects.

Define four endpoint miniports and the stable endpoint-role property from Task 4. The two user-facing friendly names are exactly:

```text
EMKE Virtual Speaker
EMKE Virtual Microphone
```

Internal bridge endpoints must be clearly prefixed `EMKE Internal` and excluded from onboarding meeting-device instructions.

- [ ] **Step 3: Write package build and static validation scripts**

`build-driver.ps1` invokes MSBuild Release/x64 and Inf2Cat for Windows 11 x64.

`verify-driver-package.ps1` must fail unless:

```text
exactly one INF, SYS, and CAT exist
INF includes ROOT\EMKEVIRTUALAUDIO
INF declares all four endpoint role strings
DriverVer and file version agree
driver ABI equals 1
catalog contains the built SYS and INF
no Debug binaries or PDBs enter the distributable directory
```

- [ ] **Step 4: Build without installation**

```powershell
pwsh Windows/tools/build-driver.ps1 -Configuration Release -Platform x64
pwsh Windows/tools/verify-driver-package.ps1 Windows/artifacts/driver/x64/Release
```

Expected: package structure and static checks pass. This is build proof only.

- [ ] **Step 5: Commit driver source**

```powershell
git add Windows/driver Windows/tools/build-driver.ps1 Windows/tools/verify-driver-package.ps1
git commit -m "feat: add EMKE Windows virtual audio driver"
```

### Task 7: Prove Four-Endpoint Routing on a Physical Lab Machine

**Files:**
- Create: `Windows/native/EMKE.AudioSmoke/src/main.cpp`
- Create: `Windows/tools/install-test-driver.ps1`
- Create: `Windows/tools/uninstall-test-driver.ps1`
- Create: `Windows/tools/collect-audio-evidence.ps1`
- Create: `docs/quality/windows-audio-lab-evidence.md`

**Interfaces:**
- Smoke tool uses the public C ABI only.
- Lab evidence binds source commit, driver hash/version, endpoint IDs, and test outcome.

- [ ] **Step 1: Build a red smoke scenario before installing**

`EMKE.AudioSmoke.exe --scenario enumerate` must return nonzero and report `driverMissing` when no compatible driver is installed.

Run:

```powershell
Windows\artifacts\native\x64\Release\EMKE.AudioSmoke.exe --scenario enumerate
```

Expected before installation: controlled `driverMissing`, not a crash.

- [ ] **Step 2: Install only with explicit administrator confirmation**

`install-test-driver.ps1` must:

```text
verify package SHA-256
verify test or Microsoft catalog signature
show version and hardware ID
require -ConfirmInstall
call pnputil /add-driver EMKE.VirtualAudio.inf /install
verify the devnode and all four endpoint roles
```

Run from an elevated PowerShell only after confirmation:

```powershell
pwsh Windows/tools/install-test-driver.ps1 `
  -PackagePath Windows/artifacts/driver/x64/Release `
  -ConfirmInstall
```

- [ ] **Step 3: Test local synthetic routes**

Run:

```powershell
Windows\artifacts\native\x64\Release\EMKE.AudioSmoke.exe --scenario inbound-original --seconds 10
Windows\artifacts\native\x64\Release\EMKE.AudioSmoke.exe --scenario inbound-translated --seconds 10
Windows\artifacts\native\x64\Release\EMKE.AudioSmoke.exe --scenario outbound-translated --seconds 10
Windows\artifacts\native\x64\Release\EMKE.AudioSmoke.exe --scenario outbound-underrun --seconds 10
```

Expected:

```text
inbound original and translated are mutually exclusive
translated 400 ms blocks are complete
outbound translation reaches meeting microphone capture
outbound underrun contains only zero samples
all rings remain within fixed capacity
```

- [ ] **Step 4: Inject failure and crash**

Run:

```powershell
Windows\artifacts\native\x64\Release\EMKE.AudioSmoke.exe --scenario inbound-failure --seconds 10
Windows\artifacts\native\x64\Release\EMKE.AudioSmoke.exe --scenario outbound-failure --seconds 10
Windows\artifacts\native\x64\Release\EMKE.AudioSmoke.exe --scenario crash-after-mic-open
```

Capture the meeting-microphone endpoint after each run. Expected: inbound becomes original; outbound is zero-filled; after process crash, the virtual microphone emits silence.

- [ ] **Step 5: Record evidence**

`collect-audio-evidence.ps1` writes only:

```text
source commit
OS build
driver version/ABI/hash/signature status
four anonymized endpoint-role hashes
scenario result and counters
UTC timestamps
```

Populate `docs/quality/windows-audio-lab-evidence.md`. Keep audio recordings outside git and reference their evidence bundle hash.

- [ ] **Step 6: Commit smoke tooling and observed evidence**

```powershell
git add Windows/native/EMKE.AudioSmoke Windows/tools/install-test-driver.ps1 Windows/tools/uninstall-test-driver.ps1 Windows/tools/collect-audio-evidence.ps1 docs/quality/windows-audio-lab-evidence.md
git commit -m "test: verify Windows four-endpoint audio routing"
```

### Task 8: Add Windows Audio CI and Close the Foundation Gate

**Files:**
- Create: `.github/workflows/windows-audio.yml`
- Modify: `docs/quality/windows-audio-lab-evidence.md`

**Interfaces:**
- Hosted CI proves build/static behavior.
- Physical lab proof remains separately recorded and cannot be inferred from CI.

- [ ] **Step 1: Add hosted CI**

Create a workflow triggered by:

```text
Windows/native/**
Windows/driver/**
Windows/tools/build-driver.ps1
Windows/tools/verify-driver-package.ps1
Shared/TestVectors/Audio/**
```

Use `runs-on: windows-2025-vs2026` for hosted build/static proof. That image
may have OS build 26100, so it is not live Windows 11 25H2 proof. Run:

```powershell
pwsh Windows/tools/verify-toolchain.ps1
cmake --preset windows-x64-release -S Windows/native
cmake --build --preset windows-x64-release
ctest --preset windows-x64-release
pwsh Windows/tools/build-driver.ps1 -Configuration Release -Platform x64
pwsh Windows/tools/verify-driver-package.ps1 Windows/artifacts/driver/x64/Release
```

Upload unsigned/test-signed artifacts only to restricted CI retention; do not create a public GitHub Release.

- [ ] **Step 2: Run the complete foundation gate**

```powershell
node Scripts/validate-shared-contracts.mjs
cmake --build --preset windows-x64-release
ctest --preset windows-x64-release
pwsh Windows/tools/build-driver.ps1 -Configuration Release -Platform x64
pwsh Windows/tools/verify-driver-package.ps1 Windows/artifacts/driver/x64/Release
git diff --check
git status --short
```

- [ ] **Step 3: Verify the evidence boundary**

The evidence document must contain separate statuses for:

```text
native unit tests
driver build/static verification
installed-driver verification
live endpoint verification
human listening
real meeting
```

Only the first four are expected in this plan. Human listening and real meetings remain pending.

- [ ] **Step 4: Commit the gate**

```powershell
git add .github/workflows/windows-audio.yml docs/quality/windows-audio-lab-evidence.md
git commit -m "ci: gate Windows audio foundation"
git status --porcelain
```

Expected: empty status. The resulting commit is the base for the Windows Translation runtime branch.
