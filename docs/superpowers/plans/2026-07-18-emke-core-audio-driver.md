# EMKE Core Audio Virtual Driver Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a loadable macOS Audio Server Driver Plug-in that publishes `EMKE Virtual Speaker` and `EMKE Virtual Microphone`, moves audio through real-time-safe in-memory loopback buffers, and defaults the virtual microphone to silence when no translated audio is available.

**Architecture:** The driver publishes two fixed 48 kHz stereo Float32 Core Audio devices. Each device owns an internal single-producer/single-consumer ring buffer and a pair of streams: the meeting-facing stream and an app-facing companion stream. `EMKE Virtual Speaker` loops meeting output into an app-readable input; `EMKE Virtual Microphone` loops app output into a meeting-readable input. This keeps audio samples inside `coreaudiod`, avoids real-time XPC or filesystem access, and lets the later menu-bar app use ordinary AUHAL/AudioDevice I/O.

**Tech Stack:** C11 atomics, CoreAudio `AudioServerPlugInDriverInterface`, CoreFoundation CFPlugIn bundles, Swift Package Manager C targets, Swift Testing 6.2.3, macOS 14+, Apple Silicon.

## Global Constraints

- Target Apple Silicon and macOS 14 or later.
- Publish exactly two device names: `EMKE Virtual Speaker` and `EMKE Virtual Microphone`.
- Use 48,000 Hz, two-channel, interleaved Float32 PCM at the HAL boundary for the MVP driver.
- Audio callbacks may only perform bounded arithmetic, atomic index updates, `memcpy`, and zero-fill operations.
- Audio callbacks must not allocate, acquire locks, call Swift or Objective-C, perform network or file I/O, or emit logs.
- The speaker route may drop newest frames when its buffer is full; it must never block the HAL thread.
- The microphone route must zero-fill every underrun so a stopped or crashed app produces silence.
- Do not install into `/Library/Audio/Plug-Ins/HAL`, restart `coreaudiod`, or change system audio devices as part of automated tests.
- Keep the Apple sample license notice for source derived from Apple’s `Creating an Audio Server Driver Plug-in` sample.
- Use test-first red-green-refactor cycles and commit each independently verified task.

## Plan Boundary

This plan implements the second subsystem from the approved product design:

1. Core foundation — complete.
2. Core Audio virtual speaker and microphone driver — this document.
3. Menu-bar app and physical-device audio engine — consumes the driver devices through Core Audio.
4. Signed installer, meeting-app compatibility, latency, and privacy validation — installs and validates the integrated app.

The automated boundary ends with a built, inspectable `.driver` bundle. Installing the bundle and restarting Core Audio are explicit manual integration actions because they require administrator authority and interrupt all current audio clients.

## File Structure

```text
Package.swift
Sources/
  EMKEAudioBridge/
    include/EMKEAudioRingBuffer.h   # C API imported by Swift and the HAL adapter
    EMKEAudioRingBuffer.c           # bounded lock-free SPSC frame storage
    include/EMKEAudioRoutes.h       # speaker/microphone route API
    EMKEAudioRoutes.c               # two route buffers and fail-safe read behavior
Tests/
  EMKEAudioBridgeTests/
    AudioRingBufferTests.swift      # wraparound, capacity, reset, partial I/O
    AudioRoutesTests.swift          # independent routes and microphone zero-fill
Driver/
  EMKEAudioDriver/
    EMKEAudioDriver.c               # AudioServerPlugIn interface and object properties
    EMKEAudioDriverObjects.h        # stable object IDs and fixed format constants
    Info.plist                      # CFPlugIn factory and bundle metadata
    LICENSE-Apple-Sample.txt        # required upstream license notice
  Makefile                          # deterministic arm64 driver bundle build
  verify-bundle.sh                  # plist, architecture, linkage, and symbol checks
```

---

### Task 1: Real-Time SPSC Audio Ring Buffer

**Files:**
- Modify: `Package.swift`
- Create: `Sources/EMKEAudioBridge/include/EMKEAudioRingBuffer.h`
- Create: `Sources/EMKEAudioBridge/EMKEAudioRingBuffer.c`
- Create: `Tests/EMKEAudioBridgeTests/AudioRingBufferTests.swift`

**Interfaces:**
- Consumes: interleaved `Float32` frames with a fixed channel count.
- Produces: `EMKEAudioRingBufferCreate`, `EMKEAudioRingBufferDestroy`, `EMKEAudioRingBufferWrite`, `EMKEAudioRingBufferRead`, `EMKEAudioRingBufferReset`, and readable/writable frame counters.

- [ ] **Step 1: Add the C target and write failing wraparound tests**

Add `.library(name: "EMKEAudioBridge", targets: ["EMKEAudioBridge"])`, a C target with `publicHeadersPath: "include"`, and an `EMKEAudioBridgeTests` target depending on `EMKEAudioBridge` plus Swift Testing. The tests create a four-frame, two-channel buffer, write frames `1...4`, read two frames, write frames `5...6`, and expect the remaining read order to be `3, 4, 5, 6` on both channels. Separate tests verify that a full buffer reports zero writable frames and reset restores the full capacity.

- [ ] **Step 2: Run the focused tests and verify RED**

Run: `swift test --filter AudioRingBufferTests`

Expected: compilation fails because `EMKEAudioRingBufferCreate` and the other C symbols do not exist.

- [ ] **Step 3: Implement the minimal bounded SPSC buffer**

Define an opaque `EMKEAudioRingBuffer`. Allocate its interleaved `float` storage only in `Create`. Store monotonically increasing `_Atomic uint64_t` read and write positions, use acquire/release ordering between producer and consumer, copy at most the current free/readable frame count in at most two `memcpy` segments, and never overwrite unread frames.

The public read/write contract is:

```c
uint32_t EMKEAudioRingBufferWrite(
    EMKEAudioRingBuffer *buffer,
    const float *interleavedFrames,
    uint32_t frameCount);

uint32_t EMKEAudioRingBufferRead(
    EMKEAudioRingBuffer *buffer,
    float *interleavedFrames,
    uint32_t frameCount);
```

Both functions return the number of frames actually transferred and return `0` for null arguments or a zero frame request.

- [ ] **Step 4: Run focused and full tests and verify GREEN**

Run: `swift test --filter AudioRingBufferTests && swift test --parallel`

Expected: all ring-buffer tests and the existing 27 tests pass.

- [ ] **Step 5: Commit**

```bash
git add Package.swift Sources/EMKEAudioBridge Tests/EMKEAudioBridgeTests
git -c user.name='Codex' -c user.email='codex@local' commit -m "feat: add realtime audio ring buffer"
```

---

### Task 2: Speaker and Microphone Route Safety

**Files:**
- Create: `Sources/EMKEAudioBridge/include/EMKEAudioRoutes.h`
- Create: `Sources/EMKEAudioBridge/EMKEAudioRoutes.c`
- Create: `Tests/EMKEAudioBridgeTests/AudioRoutesTests.swift`

**Interfaces:**
- Consumes: Task 1 ring buffers.
- Produces: opaque `EMKEAudioRoutes`, speaker capture write/read functions, microphone translation write/read functions, reset, and dropped/underrun frame counters.

- [ ] **Step 1: Write failing route-isolation and fail-safe tests**

Create tests that prove:

- speaker frames can only be read from the speaker route;
- microphone frames can only be read from the microphone route;
- a microphone request larger than available translated audio returns the available audio and zero-fills the remainder;
- an empty microphone route returns the requested frame count as silence;
- a full speaker route drops newest frames and increments its dropped-frame counter.

- [ ] **Step 2: Run the focused tests and verify RED**

Run: `swift test --filter AudioRoutesTests`

Expected: compilation fails because `EMKEAudioRoutesCreate` and the route functions do not exist.

- [ ] **Step 3: Implement two independent real-time routes**

Use two Task 1 buffers inside `EMKEAudioRoutes`. `EMKEAudioRoutesReadMicrophone` always returns the requested frame count after zero-filling any unread tail. Maintain `_Atomic uint64_t` counters for speaker frames dropped and microphone frames zero-filled. `Reset` clears both buffers and all counters outside the HAL real-time callback.

- [ ] **Step 4: Run focused and full tests and verify GREEN**

Run: `swift test --filter AudioRoutesTests && swift test --parallel`

Expected: all route tests and existing tests pass.

- [ ] **Step 5: Commit**

```bash
git add Sources/EMKEAudioBridge Tests/EMKEAudioBridgeTests/AudioRoutesTests.swift
git -c user.name='Codex' -c user.email='codex@local' commit -m "feat: enforce virtual audio route safety"
```

---

### Task 3: Stable HAL Object Model

**Files:**
- Create: `Driver/EMKEAudioDriver/EMKEAudioDriverObjects.h`
- Create: `Tests/EMKEAudioBridgeTests/AudioDriverObjectTests.swift`
- Modify: `Package.swift`

**Interfaces:**
- Consumes: fixed device names and 48 kHz stereo Float32 format.
- Produces: C constants/functions describing plug-in object `1`, speaker device `2`, speaker input/output streams `3/4`, microphone device `5`, and microphone input/output streams `6/7`.

- [ ] **Step 1: Expose object descriptors through the C target and write failing tests**

Tests must assert unique nonzero IDs, exact device UIDs `com.emke.translation.virtual-speaker` and `com.emke.translation.virtual-microphone`, exact visible names, 48,000 Hz, two channels, and distinct stream directions. The speaker output is meeting-facing and speaker input is app-facing; microphone output is app-facing and microphone input is meeting-facing.

- [ ] **Step 2: Run the tests and verify RED**

Run: `swift test --filter AudioDriverObjectTests`

Expected: compilation fails because the object descriptor API is absent.

- [ ] **Step 3: Implement immutable object descriptors**

Add a plain C `EMKEAudioObjectDescriptor` table and lookup functions. Keep this header CoreAudio-independent so Swift tests can validate the driver contract without loading a plug-in into `coreaudiod`.

- [ ] **Step 4: Run focused and full tests and verify GREEN**

Run: `swift test --filter AudioDriverObjectTests && swift test --parallel`

Expected: all object model tests and existing tests pass.

- [ ] **Step 5: Commit**

```bash
git add Package.swift Driver/EMKEAudioDriver/EMKEAudioDriverObjects.h Tests/EMKEAudioBridgeTests/AudioDriverObjectTests.swift
git -c user.name='Codex' -c user.email='codex@local' commit -m "feat: define virtual audio device model"
```

---

### Task 4: Audio Server Plug-in Adapter

**Files:**
- Create: `Driver/EMKEAudioDriver/EMKEAudioDriver.c`
- Create: `Driver/EMKEAudioDriver/LICENSE-Apple-Sample.txt`

**Interfaces:**
- Consumes: Tasks 2 and 3 plus Apple’s `AudioServerPlugInDriverInterface` contract.
- Produces: exported `EMKEAudioDriver_Create` CFPlugIn factory and a complete static `AudioServerPlugInDriverInterface`.

- [ ] **Step 1: Add a compile-only driver test command and verify RED**

Run:

```bash
mkdir -p .build/driver-objects
xcrun clang -std=c11 -arch arm64 -mmacosx-version-min=14.0 -fPIC -Wall -Wextra -Werror \
  -ISources/EMKEAudioBridge/include -IDriver/EMKEAudioDriver \
  -c Driver/EMKEAudioDriver/EMKEAudioDriver.c -o .build/driver-objects/EMKEAudioDriver.o
```

Expected: compilation fails because `EMKEAudioDriver.c` does not exist.

- [ ] **Step 2: Implement the CFPlugIn factory and object properties**

Implement all `AudioServerPlugInDriverInterface` callbacks. The plug-in object publishes both devices. Each device publishes its two streams, fixed nominal/available sample rate, latency, safety offset, zero-time timestamp period, preferred channel layout, and alive/running state. Each stream publishes direction, starting channel, terminal type, virtual/physical formats, and available virtual/physical formats. Reject unsupported property mutations with the correct Core Audio error instead of silently accepting them.

Use Apple’s `Creating an Audio Server Driver Plug-in` sample as the behavioral reference and include its MIT-style license notice in `LICENSE-Apple-Sample.txt` and the derived source header.

- [ ] **Step 3: Wire I/O callbacks to the route buffers**

`kAudioServerPlugInIOOperationWriteMix` on the speaker output writes to the speaker route. `ReadInput` on the speaker input reads captured meeting audio and zero-fills an underrun tail. `WriteMix` on the microphone output writes translated audio to the microphone route. `ReadInput` on the microphone input uses the fail-safe read that always zero-fills missing frames. `StartIO` lazily creates/reset buffers before the first client starts; `StopIO` resets the microphone route after the final client stops.

- [ ] **Step 4: Compile with warnings as errors and verify GREEN**

Run the Step 1 command plus compilation of `EMKEAudioRingBuffer.c` and `EMKEAudioRoutes.c`.

Expected: all three sources compile for arm64 with no warnings.

- [ ] **Step 5: Commit**

```bash
git add Driver/EMKEAudioDriver
git -c user.name='Codex' -c user.email='codex@local' commit -m "feat: implement dual virtual audio driver"
```

---

### Task 5: Deterministic Driver Bundle Build

**Files:**
- Create: `Driver/EMKEAudioDriver/Info.plist`
- Create: `Driver/Makefile`
- Create: `Driver/verify-bundle.sh`
- Modify: `.gitignore`

**Interfaces:**
- Consumes: Task 4 C sources.
- Produces: `.build/driver/EMKEAudioDriver.driver` with executable `Contents/MacOS/EMKEAudioDriver`.

- [ ] **Step 1: Write the bundle verifier and verify RED**

The verifier must fail unless the bundle has package type `BNDL`, identifier `com.emke.translation.audio-driver`, the expected CFPlugIn type/factory UUID mapping, an arm64 Mach-O executable, linked CoreAudio/CoreFoundation frameworks, and exported symbol `_EMKEAudioDriver_Create`.

Run: `bash Driver/verify-bundle.sh .build/driver/EMKEAudioDriver.driver`

Expected: nonzero exit because the bundle does not exist.

- [ ] **Step 2: Add the plist and Makefile build**

The Makefile creates the bundle directory, compiles the three C sources with `-std=c11 -arch arm64 -mmacosx-version-min=14.0 -fPIC -Wall -Wextra -Werror`, links a bundle with CoreAudio/CoreFoundation, and copies the static plist and license into `Contents`.

- [ ] **Step 3: Build and verify GREEN**

Run: `make -C Driver clean all && bash Driver/verify-bundle.sh .build/driver/EMKEAudioDriver.driver`

Expected: bundle verification prints the identifier, architecture, exported factory, and `PASS`.

- [ ] **Step 4: Verify the bundle factory is loadable outside coreaudiod**

Use `/usr/bin/codesign --sign - --force` for an ad-hoc local test signature, then run a small `CFBundleLoadExecutable` smoke command from the verifier that resolves `EMKEAudioDriver_Create` and requests the `kAudioServerPlugInTypeUUID` interface. Expected: non-null driver reference and exit `0`.

- [ ] **Step 5: Commit**

```bash
git add .gitignore Driver
git -c user.name='Codex' -c user.email='codex@local' commit -m "build: package virtual audio driver"
```

---

### Task 6: Driver/App Stream Contract Documentation

**Files:**
- Create: `docs/audio-driver-contract.md`
- Modify: `docs/superpowers/specs/2026-07-18-emke-translation-macos-mvp-design.md`

**Interfaces:**
- Consumes: the exact device/stream model from Tasks 3–5.
- Produces: the stream-selection contract required by the menu-bar audio engine plan.

- [ ] **Step 1: Document the four stream roles and safety behavior**

Record that meetings write to the speaker output and read from the microphone input, while EMKE reads the speaker input and writes the microphone output. State that the opposite-direction companion streams are implementation transport endpoints, not user-selected meeting endpoints. Document fixed 48 kHz stereo Float32 format, buffer overflow/drop policy, microphone zero-fill policy, and the fact that no audio crosses XPC or disk.

- [ ] **Step 2: Align the approved design’s driver paragraph**

Clarify that the shared ring buffers are internal to the HAL plug-in and the app accesses them through companion Core Audio streams. Do not change any product-facing device names or fail-open/fail-closed decisions.

- [ ] **Step 3: Run the documentation contract scan**

Run:

```bash
rg -n "EMKE Virtual Speaker|EMKE Virtual Microphone|48 kHz|zero-fill|companion" \
  docs/audio-driver-contract.md docs/superpowers/specs/2026-07-18-emke-translation-macos-mvp-design.md
```

Expected: both device names and every safety/format term are present.

- [ ] **Step 4: Commit**

```bash
git add docs
git -c user.name='Codex' -c user.email='codex@local' commit -m "docs: define virtual audio stream contract"
```

---

### Task 7: Final Automated Verification

**Files:**
- Modify: `docs/superpowers/plans/2026-07-18-emke-core-audio-driver.md`

**Interfaces:**
- Consumes: all prior tasks.
- Produces: a reviewable driver branch ready for explicit install/meeting integration approval.

- [ ] **Step 1: Run all Swift tests**

Run: `swift test --parallel`

Expected: all existing 27 tests plus every new audio bridge/object-model test pass.

- [ ] **Step 2: Build release libraries and driver bundle**

Run: `swift build -c release && make -C Driver clean all`

Expected: both commands exit `0` with no warnings.

- [ ] **Step 3: Verify bundle and source hygiene**

Run:

```bash
bash Driver/verify-bundle.sh .build/driver/EMKEAudioDriver.driver
git diff --check
rg -n "malloc|calloc|realloc|free|pthread_mutex|dispatch_|NSLog|printf|open\(|write\(" \
  Driver/EMKEAudioDriver/EMKEAudioDriver.c Sources/EMKEAudioBridge
```

Expected: verifier passes; diff check is empty; any allocation matches appear only in create/destroy paths and no lock, logging, file, or dispatch calls appear in HAL I/O callbacks.

- [ ] **Step 4: Mark every completed checkbox and commit the plan state**

```bash
git add docs/superpowers/plans/2026-07-18-emke-core-audio-driver.md
git -c user.name='Codex' -c user.email='codex@local' commit -m "docs: complete virtual audio driver plan"
```

## Manual Integration Gate After This Plan

The next action is not automated. With explicit approval, copy the ad-hoc signed bundle to `/Library/Audio/Plug-Ins/HAL`, restart Core Audio or reboot, verify both devices in Audio MIDI Setup, and run bidirectional tone/loopback tests. Production distribution still requires Developer ID signing, notarization, a privileged installer package, and clean-machine macOS 14+ validation.
