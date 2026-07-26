# EMKE macOS Baseline Convergence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Produce one clean macOS baseline that combines the v0.2.2 release line with the reviewed FIR, late-tail, and language-probability fixes before Windows consumes contract v1.

**Architecture:** Work from `origin/main` in `codex/macos-contract-v1`; leave the dirty local `main` and `codex/internal-pkg-installer` worktrees untouched. Recreate each uncommitted audio fix through focused failing tests and minimal Swift changes, then run source, driver, packaging, installed-package, and real-meeting gates as separate evidence.

**Tech Stack:** Swift 6.2, Swift Testing, SwiftPM, Core Audio/AUHAL, C11, Sparkle 2.9.2, Bash, macOS 14+ Apple Silicon

## Global Constraints

- Source branch starts from verified `origin/main` at or after v0.2.2.
- Do not commit, clean, reset, or otherwise mutate `.worktrees/internal-pkg-installer`.
- Preserve current WPF/Windows design work in its own branch; this plan changes macOS files only.
- Network audio remains 24,000 Hz mono signed little-endian PCM16.
- Local normalized audio remains 48,000 Hz stereo Float32.
- Capture capacity remains 4,800 frames; playback capacity remains 96,000 frames.
- FIR interpolation uses 127 Blackman-windowed taps and preserves streaming state across chunks.
- Inbound VAD tail waits at least 500 ms and restarts that window for late audio/transcript deltas.
- BCP-47 probabilities aggregate by primary tag and clamp to 1.0.
- Keep interface-language fixtures deterministic; do not inherit the runner's locale in explicit zh-Hans/en tests.
- Automated proof never substitutes for installed-package or real-meeting proof.

---

### Task 1: Freeze Inputs and Prove the Starting Baseline

**Files:**
- Create: `docs/quality/macos-contract-v1-source-audit.md`
- Verify: `Package.swift`
- Verify: `.github/workflows/release.yml`
- Verify: `.worktrees/internal-pkg-installer/`

**Interfaces:**
- Consumes: `origin/main`, v0.2.2, and read-only diffs from `codex/internal-pkg-installer`.
- Produces: a source audit with exact commit IDs and the nine intended files.

- [ ] **Step 1: Verify the execution worktree**

Run:

```bash
git status --short --branch
git rev-parse HEAD
git merge-base --is-ancestor 8629617 HEAD
```

Expected: branch is `codex/macos-contract-v1`, status is clean, and the v0.2.2 merge is an ancestor.

- [ ] **Step 2: Record the read-only source worktree status**

Run:

```bash
git -C ../internal-pkg-installer status --short --branch
git -C ../internal-pkg-installer diff --name-only
```

Expected file set:

```text
Sources/EMKEAudioEngine/NetworkPCMConverter.swift
Sources/EMKECoordinator/TranslationCoordinator.swift
Sources/EMKERouting/InboundLanguageGate.swift
Tests/EMKEAudioEngineTests/LocalAudioEngineTests.swift
Tests/EMKEAudioEngineTests/NetworkPCMConverterTests.swift
Tests/EMKECoordinatorTests/TranslationCoordinatorTests.swift
Tests/EMKERoutingTests/InboundLanguageGateTests.swift
docs/local-audio-engine-contract.md
docs/translation-coordinator-contract.md
```

- [ ] **Step 3: Run the starting source suite**

Run:

```bash
swift test
```

Expected: all tests pass. Record the observed count without changing it in source.

- [ ] **Step 4: Create the source audit**

Create `docs/quality/macos-contract-v1-source-audit.md`:

```markdown
# macOS Contract v1 Source Audit

- Baseline commit: `8629617`
- Baseline tag: `v0.2.2`
- Audio reference commit: `514ac2d`
- Reference worktree modifications: 9 files
- Reference worktree was read only: yes

## Intended fixes

1. Aggregate BCP-47 probabilities by primary tag.
2. Interpolate 24 kHz translation PCM with a streaming 127-tap FIR.
3. Extend the inbound 500 ms finish window for late server deltas.

## Excluded

- Provider, Base URL, model, Keychain, UI layout, Sparkle, and driver behavior.
- Any real key, device inventory, recording, or Authorization value.
```

- [ ] **Step 5: Commit the audit**

```bash
git add docs/quality/macos-contract-v1-source-audit.md
git commit -m "docs: freeze macOS contract v1 inputs"
```

### Task 2: Aggregate Primary-Language Probabilities

**Files:**
- Modify: `Sources/EMKERouting/InboundLanguageGate.swift`
- Modify: `Tests/EMKERoutingTests/InboundLanguageGateTests.swift`

**Interfaces:**
- Consumes: `[String: Double]` BCP-47 language probabilities.
- Produces: `LanguageHypotheses.confidenceByPrimaryTag` with summed, clamped values.

- [ ] **Step 1: Write the failing aggregation tests**

Add to `InboundLanguageGateTests.swift`:

```swift
@Test
func regionalLanguageTagsAggregateIntoPrimaryTagConfidence() {
    let hypotheses = LanguageHypotheses([
        "en-US": 0.52,
        "en-GB": 0.81,
        "de-DE": 0.19,
    ])
    #expect(hypotheses.confidenceByPrimaryTag["en"] == 1)
    #expect(hypotheses.confidenceByPrimaryTag["de"] == 0.19)
}

@Test
func scriptVariantsContributeToOnePrimaryLanguageDecision() {
    let hypotheses = LanguageHypotheses([
        "zh-Hans": 0.411,
        "zh-Hant": 0.565,
        "ja": 0.024,
    ])
    var gate = InboundLanguageGate(motherLanguage: .chinese)

    #expect(hypotheses.confidenceByPrimaryTag["zh"] == 0.976)
    #expect(gate.observe(hypotheses) == .original)
}
```

- [ ] **Step 2: Run the focused tests and verify failure**

Run:

```bash
swift test --filter regionalLanguageTagsAggregateIntoPrimaryTagConfidence
swift test --filter scriptVariantsContributeToOnePrimaryLanguageDecision
```

Expected: at least one assertion fails because the current implementation keeps only the maximum variant confidence.

- [ ] **Step 3: Implement summed and clamped aggregation**

Replace the primary-tag assignment in `LanguageHypotheses.init` with:

```swift
result[primaryTag] = min(
    1,
    result[primaryTag, default: 0] + item.value
)
```

- [ ] **Step 4: Run routing tests**

Run:

```bash
swift test --filter EMKERoutingTests
```

Expected: all routing tests pass.

- [ ] **Step 5: Commit**

```bash
git add Sources/EMKERouting/InboundLanguageGate.swift \
  Tests/EMKERoutingTests/InboundLanguageGateTests.swift
git commit -m "fix: aggregate primary language confidence"
```

### Task 3: Add Streaming FIR Translation Playback

**Files:**
- Modify: `Sources/EMKEAudioEngine/NetworkPCMConverter.swift`
- Modify: `Tests/EMKEAudioEngineTests/NetworkPCMConverterTests.swift`
- Modify: `Tests/EMKEAudioEngineTests/LocalAudioEngineTests.swift`
- Modify: `docs/local-audio-engine-contract.md`

**Interfaces:**
- Consumes: aligned 24 kHz mono PCM16 chunks.
- Produces: continuous 48 kHz stereo Float32 samples with the 24 kHz image band suppressed.

- [ ] **Step 1: Write the failing spectral and chunk-continuity tests**

Replace the existing decoder shape test and add the spectral regression:

```swift
@Test
func decoderSuppressesThe24kUpsamplingImageBand() throws {
    let sourceFrequency = 10_560.0
    let imageFrequency = 24_000.0 - sourceFrequency
    var pcm16 = Data()
    for frame in 0..<12_000 {
        let phase = 2 * Double.pi * sourceFrequency
            * Double(frame) / 24_000
        var sample = Int16(
            (sin(phase) * 0.5 * Double(Int16.max)).rounded()
        ).littleEndian
        withUnsafeBytes(of: &sample) { pcm16.append(contentsOf: $0) }
    }
    var decoder = NetworkPCMDecoder()

    let decoded = try decoder.append24kMonoPCM16(pcm16)
    let desiredMagnitude = toneMagnitude(
        decoded,
        frequency: sourceFrequency,
        sampleRate: 48_000
    )
    let imageMagnitude = toneMagnitude(
        decoded,
        frequency: imageFrequency,
        sampleRate: 48_000
    )

    #expect(desiredMagnitude > 0)
    #expect(imageMagnitude < desiredMagnitude * 0.01)
}

private func toneMagnitude(
    _ interleavedStereo: [Float],
    frequency: Double,
    sampleRate: Double
) -> Double {
    let mono = stride(from: 0, to: interleavedStereo.count, by: 2)
        .map { Double(interleavedStereo[$0]) }
    let start = min(512, mono.count / 4)
    let count = mono.count - start
    guard count > 1 else { return 0 }

    var real = 0.0
    var imaginary = 0.0
    for offset in 0..<count {
        let window = 0.5 - 0.5 * cos(
            2 * Double.pi * Double(offset) / Double(count - 1)
        )
        let phase = 2 * Double.pi * frequency
            * Double(start + offset) / sampleRate
        real += mono[start + offset] * window * cos(phase)
        imaginary -= mono[start + offset] * window * sin(phase)
    }
    return hypot(real, imaginary)
}

@Test
func decoderProducesTwoStereoFramesPer24kSample() throws {
    var decoder = NetworkPCMDecoder()
    let decoded = try decoder.append24kMonoPCM16(
        Data([0x00, 0x00, 0xff, 0x7f])
    )

    #expect(decoded.count == 8)
    #expect(stride(from: 0, to: decoded.count, by: 2).allSatisfy {
        decoded[$0] == decoded[$0 + 1]
    })
}
```

Keep the existing `decoderPreservesResultsAcrossChunkBoundaries` test. It is
the streaming-state regression and already uses deterministic in-memory PCM.

- [ ] **Step 2: Run focused tests and verify failure**

Run:

```bash
swift test --filter decoderSuppressesThe24kUpsamplingImageBand
swift test --filter decoderPreservesResultsAcrossChunkBoundaries
```

Expected: zero-order repetition fails the image-band assertion.

- [ ] **Step 3: Implement the 127-tap polyphase interpolator**

Add `StreamingMonoInterpolator2x` to `NetworkPCMConverter.swift` with:

```swift
private static let phases: (even: [Float], odd: [Float]) = {
    let tapCount = 127
    let midpoint = Double(tapCount - 1) * 0.5
    let cutoff = 0.25
    var taps = (0..<tapCount).map { index -> Double in
        let distance = Double(index) - midpoint
        let sinc = distance == 0
            ? 2 * cutoff
            : sin(2 * Double.pi * cutoff * distance)
                / (Double.pi * distance)
        let window = 0.42
            - 0.5 * cos(2 * .pi * Double(index) / Double(tapCount - 1))
            + 0.08 * cos(4 * .pi * Double(index) / Double(tapCount - 1))
        return sinc * window
    }
    let gain = 2 / taps.reduce(0, +)
    taps = taps.map { $0 * gain }
    return (
        stride(from: 0, to: tapCount, by: 2).map { Float(taps[$0]) },
        stride(from: 1, to: tapCount, by: 2).map { Float(taps[$0]) }
    )
}()
```

Store 64 samples of history, advance one input sample at a time, and emit one even-phase plus one odd-phase output. `NetworkPCMDecoder` owns one interpolator and duplicates each interpolated mono sample to left/right channels.

- [ ] **Step 4: Preserve realistic translated burst playback**

Update the existing 400 ms assertion and the constant-sample tests so filter
warm-up does not pretend that the first sample is already at full amplitude:

```swift
@Test
func translatedOutputAcceptsARealistic400MillisecondAudioChunk() async throws {
    let harness = makeHarness()
    try await harness.engine.start(
        configuration: AudioEngineConfiguration(
            selection: harness.selection
        )
    )
    await harness.engine.setRouting(
        inbound: .translated,
        outbound: .translated
    )
    let pcm16 = Data(repeating: 0, count: 9_600 * 2)

    try await harness.engine.enqueueInboundTranslation(pcm16)
    try await harness.engine.enqueueOutboundTranslation(pcm16)

    let expectedStereoSampleCount = pcm16.count / 2 * 4
    #expect(
        harness.factory.physicalOutput.writes.first?.count
            == expectedStereoSampleCount
    )
    #expect(
        harness.factory.virtualMicrophoneOutput.writes.first?.count
            == expectedStereoSampleCount
    )
    await harness.engine.stop()
}

private func constantPCM16(_ sample: Int16, count: Int) -> Data {
    var result = Data()
    result.reserveCapacity(count * 2)
    for _ in 0..<count {
        var littleEndian = sample.littleEndian
        withUnsafeBytes(of: &littleEndian) {
            result.append(contentsOf: $0)
        }
    }
    return result
}
```

Use `constantPCM16(.max, count: 80)` and
`constantPCM16(.min, count: 80)` in the existing translated-mode tests, then
assert 320 interleaved output samples and a settled final amplitude within
`0.0001` of ±1. The 400 ms fixture produces 19,200 local frames, or 38,400
interleaved stereo samples.

- [ ] **Step 5: Run focused audio tests**

Run:

```bash
swift test --filter NetworkPCMConverterTests
swift test --filter translatedOutputAcceptsARealistic400MillisecondAudioChunk
```

Expected: all pass.

- [ ] **Step 6: Update the audio contract**

Replace the decoder sentence in `docs/local-audio-engine-contract.md` with:

```markdown
Decoding converts PCM16 to stereo Float32 and uses a streaming 127-tap
Blackman-windowed half-band FIR to interpolate 24 kHz to 48 kHz. The filter
adds about 1.31 ms of fixed group delay and suppresses the high-frequency image
that zero-order sample repetition would make audible.
```

- [ ] **Step 7: Commit**

```bash
git add Sources/EMKEAudioEngine/NetworkPCMConverter.swift \
  Tests/EMKEAudioEngineTests/NetworkPCMConverterTests.swift \
  Tests/EMKEAudioEngineTests/LocalAudioEngineTests.swift \
  docs/local-audio-engine-contract.md
git commit -m "fix: suppress translation upsampling artifacts"
```

### Task 4: Preserve Late Translation Tail Deltas

**Files:**
- Modify: `Sources/EMKECoordinator/TranslationCoordinator.swift`
- Modify: `Tests/EMKECoordinatorTests/TranslationCoordinatorTests.swift`
- Modify: `docs/translation-coordinator-contract.md`

**Interfaces:**
- Consumes: inbound audio, input transcript, and output transcript deltas received after VAD enters silence.
- Produces: a restarted 500 ms finish window until no late delta arrives.

- [ ] **Step 1: Write the failing late-delta test**

Use the existing `CoordinatorHarness`, `voicedPCM16`, `audioDelta`,
`transcriptDelta`, and `eventually` helpers:

```swift
@Test
func lateContinuousTranslationDeltasExtendTheInboundTailWindow() async throws {
    let harness = CoordinatorHarness()
    try await harness.coordinator.start(configuration: harness.configuration)

    await harness.audio.emit(.inboundNetworkAudio(voicedPCM16()))
    #expect(
        await eventually {
            await harness.inbound.appended.count == 1
        }
    )
    await harness.inbound.emit(
        .success(.outputAudio(audioDelta(Data([1, 1]))))
    )
    await harness.inbound.emit(
        .success(.inputTranscript(transcriptDelta("Deutsch")))
    )
    #expect(
        await eventually {
            await harness.audio.inboundPlayback == [Data([1, 1])]
        }
    )

    for _ in 0..<30 {
        await harness.audio.emit(
            .inboundNetworkAudio(Data(repeating: 0, count: 9_600))
        )
    }
    #expect(
        await eventually {
            await harness.inbound.appended.count == 31
        }
    )

    try await Task.sleep(for: .milliseconds(350))
    await harness.inbound.emit(
        .success(.outputAudio(audioDelta(Data([2, 2]))))
    )
    #expect(
        await eventually {
            await harness.audio.inboundPlayback.last == Data([2, 2])
        }
    )

    try await Task.sleep(for: .milliseconds(250))
    await harness.inbound.emit(
        .success(.outputAudio(audioDelta(Data([3, 3]))))
    )
    #expect(
        await eventually {
            await harness.audio.inboundPlayback.last == Data([3, 3])
        }
    )
    await harness.coordinator.stop()
}
```

Also bound the existing `eventually` helper to two seconds with 1 ms polling:

```swift
for _ in 0..<2_000 {
    if await condition() { return true }
    try? await Task.sleep(for: .milliseconds(1))
}
```

- [ ] **Step 2: Run the focused test and verify failure**

Run:

```bash
swift test --filter lateContinuousTranslationDeltasExtendTheInboundTailWindow
```

Expected: the current fixed deadline finishes the utterance before the late delta's new 500 ms window.

- [ ] **Step 3: Extend the finish window on every relevant delta**

Add:

```swift
private func extendInboundFinishWindowIfDraining() {
    guard inboundUtteranceActive, !inboundVAD.isSpeaking else { return }
    scheduleInboundFinish()
}
```

Call it after:

- `.outputAudio`;
- `.inputTranscript` when hypotheses are observed;
- `.outputTranscript`.

- [ ] **Step 4: Run coordinator tests**

Run:

```bash
swift test --filter EMKECoordinatorTests
```

Expected: all coordinator tests pass.

- [ ] **Step 5: Update the coordinator contract**

Document:

```markdown
VAD end retains at least a 500 ms tail. Every later server audio or transcript
delta restarts that 500 ms silence window so continuous-protocol tail data is
not discarded.
```

- [ ] **Step 6: Commit**

```bash
git add Sources/EMKECoordinator/TranslationCoordinator.swift \
  Tests/EMKECoordinatorTests/TranslationCoordinatorTests.swift \
  docs/translation-coordinator-contract.md
git commit -m "fix: retain late translation tail deltas"
```

### Task 5: Run the Complete macOS Automated Gate

**Files:**
- Create: `docs/quality/macos-contract-v1-automated-evidence.md`
- Verify: `Package.swift`
- Verify: `Driver/`
- Verify: `Packaging/`

**Interfaces:**
- Consumes: Tasks 1-4.
- Produces: source/build/driver/package evidence tied to one commit.

- [ ] **Step 1: Run Swift tests**

Run:

```bash
swift test
```

Expected: zero failures. Record the exact pass and skip counts.

- [ ] **Step 2: Run a Release build**

Run:

```bash
swift build -c release --product EMKEMenuBarApp
```

Expected: exit 0.

- [ ] **Step 3: Build and verify the Core Audio driver**

Run:

```bash
make -C Driver clean all verify
```

Expected: exit 0 and both virtual device contracts pass the verifier.

- [ ] **Step 4: Run the deterministic packaging suite**

Run:

```bash
env -u EMKE_VERSION -u EMKE_BUILD_NUMBER \
  bash Packaging/Tests/run-all.sh
```

Expected: `PASS: all packaging tests`.

- [ ] **Step 5: Build and verify the actual internal PKG**

Run:

```bash
bash Packaging/build-internal-pkg.sh
pkg=".build/distribution/EMKE-Translation-0.2.2-internal.pkg"
bash Packaging/verify-internal-pkg.sh "$pkg"
shasum -a 256 "$pkg"
stat -f '%z' "$pkg"
```

Expected: verifier passes; record the actual SHA-256 and byte count.

- [ ] **Step 6: Write automated evidence**

Create `docs/quality/macos-contract-v1-automated-evidence.md` with:

```markdown
# macOS Contract v1 Automated Evidence

- Commit:
- Swift tests:
- Hardware skips:
- Release build:
- Driver verify:
- Packaging suite:
- PKG path:
- PKG bytes:
- PKG SHA-256:

## Not proved here

- Administrator installation
- Installed-app upgrade
- Live virtual endpoints
- Real meeting routing
- Human listening
```

Fill each field with the observed command output; do not write `passed` before the command succeeds.

- [ ] **Step 7: Commit automated evidence**

```bash
git add docs/quality/macos-contract-v1-automated-evidence.md
git commit -m "docs: record macOS contract v1 automated evidence"
```

### Task 6: Complete Installed and Real-Meeting Baseline

**Files:**
- Create: `docs/quality/macos-contract-v1-live-acceptance.md`
- Do not commit: recordings, device serials, API keys, Authorization headers

**Interfaces:**
- Consumes: the exact PKG SHA-256 from Task 5.
- Produces: the macOS live baseline that Windows must match.

- [ ] **Step 1: Create the acceptance record before testing**

Create `docs/quality/macos-contract-v1-live-acceptance.md`:

```markdown
# macOS Contract v1 Live Acceptance

- Commit:
- PKG SHA-256:
- Installed app version:
- Installed driver identity:
- Physical input:
- Physical output:

## Local endpoints
- [ ] EMKE Virtual Speaker is present.
- [ ] EMKE Virtual Microphone is present.
- [ ] Physical microphone produces level.
- [ ] Physical output plays the test tone.

## Meeting
- [ ] Meeting speaker is EMKE Virtual Speaker.
- [ ] Meeting microphone is EMKE Virtual Microphone.
- [ ] Inbound translation is audible.
- [ ] Outbound translation is audible remotely.
- [ ] Inbound fail-open preserves meeting audio.
- [ ] Outbound fail-closed sends silence.
- [ ] Inbound original bypass works and restores.
- [ ] Outbound original bypass works and restores.
- [ ] Same-language outbound uses local direct path.
- [ ] Stop drains tail audio and becomes inactive.

## Evidence boundary
- Human listener:
- Meeting application:
- Build timestamp:
- Recording retained outside repository:
```

- [ ] **Step 2: Install only after explicit administrator authorization**

Quit EMKE and active meeting applications. Install the exact PKG from Task 5 using the existing documented package workflow. Record the installed app and driver identities before reopening the meeting app.

- [ ] **Step 3: Verify local devices and diagnostics**

Use the application onboarding/settings diagnostics to verify:

1. both EMKE virtual endpoints;
2. real microphone level;
3. real output test tone;
4. no stale saved physical device ID.

Mark only observed boxes.

- [ ] **Step 4: Run the real meeting matrix**

Run one complete session in each available target:

```text
Feishu
DingTalk
Microsoft Teams
```

If a target is unavailable, leave its result explicitly unverified; do not infer it from another meeting application.

- [ ] **Step 5: Commit only the written acceptance result**

```bash
git add docs/quality/macos-contract-v1-live-acceptance.md
git commit -m "docs: record macOS contract v1 live acceptance"
```

- [ ] **Step 6: Verify the completed baseline is clean**

Run:

```bash
git status --short --branch
git log --oneline --decorate -8
```

Expected: clean status and a reviewable sequence of focused commits.
