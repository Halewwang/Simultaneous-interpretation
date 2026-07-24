# EMKE Onboarding, Automatic Updates, Stop Responsiveness, and UI Polish Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a four-step first-launch guide, bounded graceful shutdown, Sparkle-signed automatic update downloads, GitHub release automation, and the requested dashboard/settings polish.

**Architecture:** Keep `MenuBarModel` as the only owner of translation, device, permission, and diagnostic state. Add a separate versioned onboarding flow and AppKit window controller around that shared model, bound realtime session close with an injected deadline, and keep Sparkle ownership in a focused update controller that is independent of translation. Extend the existing deterministic packaging pipeline so one version input drives the app, PKG, signed Appcast, GitHub Release, and update feed.

**Tech Stack:** Swift 6.2, SwiftUI, AppKit, AVFoundation, Combine, Swift Testing, SwiftPM, Sparkle 2.9.2, Bash, GitHub Actions, macOS 14+

## Global Constraints

- Preserve the current provider, endpoint, model, Keychain, translation-language, and audio-routing contracts.
- Request microphone permission only after the user activates the explained onboarding permission action.
- Use onboarding version `1`, persisted as non-secret `completedOnboardingVersion` in `UserDefaults`.
- Give graceful realtime close exactly 1 second before forced local socket cancellation.
- Use exact dashboard footer copy `Powered by Eager` in both interface languages.
- Reuse the approved `MenuBarLogo.image`; do not redraw or reinterpret the asset.
- Use `waveform.badge.magnifyingglass` for the local audio diagnostics title.
- Pin Sparkle to exact version `2.9.2`.
- Store only the Sparkle public EdDSA key in the repository and app; keep the private key in Keychain and the GitHub Actions secret `SPARKLE_PRIVATE_KEY`.
- Package updates may download automatically but must retain macOS administrator authorization for PKG installation.
- Keep the existing macOS 14 minimum and arm64 internal-package boundary.
- Report automated evidence separately from live permission, live microphone, administrator-install, and two-version installed-update acceptance.

---

## File Structure

### New production files

- `Sources/EMKEMenuBarApp/OnboardingProgressStore.swift`
  - Owns onboarding version constants and `UserDefaults` persistence.
- `Sources/EMKEMenuBarApp/OnboardingPresentation.swift`
  - Owns the four step identifiers, navigation rules, and microphone-state presentation.
- `Sources/EMKEMenuBarApp/OnboardingView.swift`
  - Renders localized onboarding content against the shared `MenuBarModel`.
- `Sources/EMKEMenuBarApp/OnboardingWindowController.swift`
  - Owns one first-launch/reopen AppKit window and completion/skip lifecycle.
- `Sources/EMKEMenuBarApp/AppUpdateController.swift`
  - Owns one `SPUStandardUpdaterController`, its KVO bridge, and manual-check action.
- `Packaging/Scripts/render-appcast.sh`
  - Renders one signed PKG Appcast entry from explicit release inputs.
- `Packaging/Tests/release-metadata-test.sh`
  - Verifies version propagation, Appcast escaping, and private-key exclusion.
- `.github/workflows/release.yml`
  - Builds, verifies, signs, releases, and publishes the Appcast on version tags.

### New test files

- `Tests/EMKEAudioEngineTests/OnboardingProgressStoreTests.swift`
- `Tests/EMKEAudioEngineTests/OnboardingPresentationTests.swift`
- `Tests/EMKEAudioEngineTests/OnboardingWindowControllerTests.swift`
- `Tests/EMKEAudioEngineTests/AppUpdateControllerTests.swift`

### Modified production files

- `Package.swift`
  - Adds exact Sparkle dependency and links the app target.
- `Package.resolved`
  - Records Sparkle 2.9.2.
- `Sources/EMKERealtime/TranslationSession.swift`
  - Adds injected close-deadline waiting and exactly-once forced finish.
- `Sources/EMKEMenuBarApp/AppLocalization.swift`
  - Adds bilingual onboarding/update actions and exact brand copy.
- `Sources/EMKEMenuBarApp/MenuBarModel.swift`
  - Publishes microphone permission state and exposes an onboarding-safe request.
- `Sources/EMKEMenuBarApp/TranslationDashboardView.swift`
  - Adds the approved logo to the product title.
- `Sources/EMKEMenuBarApp/TranslationSettingsView.swift`
  - Adds the diagnostics icon, onboarding reopen action, and update check action.
- `Sources/EMKEMenuBarApp/MenuBarRootView.swift`
  - Injects update and onboarding actions into Settings.
- `Sources/EMKEMenuBarApp/EMKEMenuBarApp.swift`
  - Owns one updater and one onboarding window controller.
- `Packaging/App/Info.plist`
  - Adds Sparkle feed, public key, and automatic-check/download configuration.
- `Packaging/App/EMKETranslation.entitlements`
  - Allows the current ad-hoc internal build to load the embedded Sparkle
    framework while hardened runtime remains enabled.
- `Packaging/Scripts/build-app-bundle.sh`
  - Accepts version inputs, embeds Sparkle, signs nested helpers, and stages the app.
- `Packaging/build-internal-pkg.sh`
  - Uses the shared version/build inputs for PKG naming and metadata.
- `Packaging/Tests/app-bundle-test.sh`
  - Verifies Sparkle framework, update keys, versions, and strict signatures.
- `Packaging/Tests/package-pipeline-test.sh`
  - Verifies PKG version and versioned artifact name.
- `Packaging/Tests/run-all.sh`
  - Runs the release metadata test.
- `Packaging/README.md`
  - Documents bootstrap installation, signed update publishing, and acceptance boundaries.

### Modified test files

- `Tests/EMKERealtimeTests/TranslationSessionTests.swift`
- `Tests/EMKEAudioEngineTests/AppLocalizationTests.swift`
- `Tests/EMKEAudioEngineTests/MenuBarTranslationModelTests.swift`
- `Tests/EMKEAudioEngineTests/TranslationDashboardAccessibilityTests.swift`
- `Tests/EMKEAudioEngineTests/TranslationDashboardRenderTests.swift`

## Task 1: Bound Realtime Session Close Without Dropping Tail Audio

**Files:**

- Modify: `Sources/EMKERealtime/TranslationSession.swift`
- Modify: `Tests/EMKERealtimeTests/TranslationSessionTests.swift`

**Interfaces:**

- Consumes: `TranslationSocket.send(_:)`, `TranslationSocket.receive()`, and `TranslationSocket.cancel()`.
- Produces: `TranslationSession.init(..., closeDeadline: @escaping @Sendable () async -> Void = { try? await Task.sleep(for: .seconds(1)) })` and an unchanged `close() async throws` public behavior with a one-second production deadline.

- [ ] **Step 1: Make the fake socket optionally withhold `session.closed`**

Add the constructor flag and conditional close response:

```swift
private actor FakeSocket: TranslationSocket {
    private let acknowledgesClose: Bool

    init(
        incoming: [Data] = [],
        acknowledgesClose: Bool = true
    ) {
        self.incoming = incoming
        self.acknowledgesClose = acknowledgesClose
    }

    func send(_ data: Data) async throws {
        sent.append(data)
        if acknowledgesClose,
           String(decoding: data, as: UTF8.self).contains("session.close") {
            enqueue(Data(#"{"type":"session.closed"}"#.utf8))
        }
    }
}
```

- [ ] **Step 2: Write failing forced-close and graceful-tail tests**

Add a deterministic gate:

```swift
private actor CloseDeadlineGate {
    private var fired = false
    private var waiter: CheckedContinuation<Void, Never>?

    func wait() async {
        guard !fired else { return }
        await withCheckedContinuation { waiter = $0 }
    }

    func fire() {
        guard !fired else { return }
        fired = true
        waiter?.resume()
        waiter = nil
    }
}
```

Add these tests:

```swift
@Test
func closeCancelsSocketWhenServerDoesNotAcknowledgeDeadline() async throws {
    let deadline = CloseDeadlineGate()
    let socket = FakeSocket(
        incoming: handshakeEvents,
        acknowledgesClose: false
    )
    let session = makeSession(socket: socket) {
        await deadline.wait()
    }
    try await session.connect()

    let closeTask = Task { try await session.close() }
    #expect(await eventually { await socket.sent.count == 2 })
    await deadline.fire()
    try await closeTask.value

    #expect(await eventually { await socket.wasCancelled })
}

@Test
func serverCloseBeforeDeadlineKeepsTailAudioAndDoesNotWaitForDeadline()
    async throws
{
    let deadline = CloseDeadlineGate()
    let socket = FakeSocket(incoming: handshakeEvents + [tailAudioEvent])
    let session = makeSession(socket: socket) {
        await deadline.wait()
    }
    try await session.connect()

    async let event = session.nextEvent()
    try await session.close()

    #expect(try await event == expectedTailAudio)
    #expect(await socket.wasCancelled)
    await deadline.fire()
}
```

Add the concrete private test helpers:

```swift
private let handshakeEvents = [
    Data(
        #"{"type":"session.created","session":{"model":"gpt-realtime-translate"}}"#.utf8
    ),
    Data(#"{"type":"session.updated"}"#.utf8),
]

private let tailAudioEvent = Data(
    #"{"type":"session.output_audio.delta","delta":"AAEC","sample_rate":24000,"channels":1,"format":"pcm16"}"#.utf8
)

private let expectedTailAudio = TranslationServerEvent.outputAudio(
    TranslationAudioDelta(
        data: Data([0, 1, 2]),
        sampleRate: 24_000,
        channels: 1,
        format: "pcm16",
        elapsedMilliseconds: nil
    )
)

private func makeSession(
    socket: FakeSocket,
    closeDeadline: @escaping TranslationSession.CloseDeadline
) -> TranslationSession {
    TranslationSession(
        configuration: .default,
        sessionConfiguration: TranslationSessionConfiguration(
            targetLanguage: .chinese
        ),
        apiKey: "secret",
        factory: FakeFactory(socket: socket),
        closeDeadline: closeDeadline
    )
}

private func eventually(
    _ condition: @escaping @Sendable () async -> Bool
) async -> Bool {
    for _ in 0..<100 {
        if await condition() { return true }
        await Task.yield()
    }
    return false
}
```

- [ ] **Step 3: Run the focused tests and verify red**

Run:

```bash
swift test --filter closeCancelsSocketWhenServerDoesNotAcknowledgeDeadline
swift test --filter serverCloseBeforeDeadlineKeepsTailAudioAndDoesNotWaitForDeadline
```

Expected: compilation fails because the injected `closeDeadline` initializer
does not exist.

- [ ] **Step 4: Implement a deadline race that finishes exactly once**

Add this production dependency and timeout task:

```swift
public actor TranslationSession {
    public typealias CloseDeadline = @Sendable () async -> Void

    private let closeDeadline: CloseDeadline
    private var closeDeadlineTask: Task<Void, Never>?

    public init(
        configuration: APIConfiguration,
        sessionConfiguration: TranslationSessionConfiguration,
        apiKey: String,
        factory: any TranslationSocketFactory,
        closeDeadline: @escaping CloseDeadline = {
            try? await Task.sleep(for: .seconds(1))
        }
    ) {
        self.configuration = configuration
        self.sessionConfiguration = sessionConfiguration
        self.apiKey = apiKey
        self.factory = factory
        self.closeDeadline = closeDeadline
    }
}
```

When `close()` starts its first graceful close, capture the dependency and
schedule:

```swift
let deadline = closeDeadline
closeDeadlineTask = Task { [weak self] in
    await deadline()
    guard !Task.isCancelled else { return }
    await self?.forceFinishClose(connectionID: id, socket: socket)
}
```

Implement actor-isolated forced finish:

```swift
private func forceFinishClose(
    connectionID id: UUID,
    socket: any TranslationSocket
) async {
    guard connectionID == id, isClosing else { return }
    finishConnection(connectionID: id, error: nil)
    await socket.cancel()
}
```

At the start of `finishConnection`, cancel and clear `closeDeadlineTask`.
The existing `connectionID` guard remains the exactly-once gate for reader,
deadline, and socket-error races.

- [ ] **Step 5: Run focused and module tests**

Run:

```bash
swift test --filter TranslationSessionTests
swift test --filter stopDrainsTailTranslationAudioBeforeStoppingTheEngine
swift test --filter staleStopSnapshotCannotReviveSessionAfterStoppedEvent
```

Expected: all selected tests pass with zero failures.

- [ ] **Step 6: Commit the bounded close**

```bash
git add Sources/EMKERealtime/TranslationSession.swift \
  Tests/EMKERealtimeTests/TranslationSessionTests.swift
git commit -m "fix: bound realtime session shutdown"
```

## Task 2: Add Exact Brand Copy and Requested Dashboard/Settings Icons

**Files:**

- Modify: `Sources/EMKEMenuBarApp/AppLocalization.swift`
- Modify: `Sources/EMKEMenuBarApp/TranslationDashboardView.swift`
- Modify: `Sources/EMKEMenuBarApp/TranslationSettingsView.swift`
- Modify: `Tests/EMKEAudioEngineTests/AppLocalizationTests.swift`
- Modify: `Tests/EMKEAudioEngineTests/TranslationDashboardAccessibilityTests.swift`
- Modify: `Tests/EMKEAudioEngineTests/TranslationDashboardRenderTests.swift`

**Interfaces:**

- Consumes: `MenuBarLogo.image`, `AppCopy.text(_:)`, and existing dashboard/settings section builders.
- Produces: exact `AppCopyKey.audioDirectToProvider == "Powered by Eager"` in both languages and source-visible approved icon contracts.

- [ ] **Step 1: Write failing copy and source-contract tests**

Add:

```swift
@Test
func dashboardBrandFooterIsExactInBothLanguages() {
    #expect(
        AppCopy(language: .zhHans).text(.audioDirectToProvider)
            == "Powered by Eager"
    )
    #expect(
        AppCopy(language: .english).text(.audioDirectToProvider)
            == "Powered by Eager"
    )
}

@Test
func dashboardHeaderUsesApprovedProductLogo() throws {
    let source = try sourceText("TranslationDashboardView.swift")
    #expect(source.contains("Image(nsImage: MenuBarLogo.image)"))
    #expect(source.contains(".accessibilityHidden(true)"))
}

@Test
func localAudioDiagnosticsUsesRequestedTitleIcon() throws {
    let source = try sourceText("TranslationSettingsView.swift")
    #expect(source.contains("waveform.badge.magnifyingglass"))
    #expect(source.contains("copy.text(.localAudioDiagnostics)"))
}
```

Use the existing source-loading helper already present in
`TranslationDashboardAccessibilityTests.swift`.

- [ ] **Step 2: Run the tests and verify red**

Run:

```bash
swift test --filter dashboardBrandFooterIsExactInBothLanguages
swift test --filter dashboardHeaderUsesApprovedProductLogo
swift test --filter localAudioDiagnosticsUsesRequestedTitleIcon
```

Expected: the footer assertion and both source-contract assertions fail.

- [ ] **Step 3: Implement the minimal UI and copy changes**

Change `.audioDirectToProvider` to:

```swift
case .audioDirectToProvider:
    "Powered by Eager"
```

Change the dashboard title to:

```swift
HStack(spacing: 7) {
    Image(nsImage: MenuBarLogo.image)
        .resizable()
        .frame(width: 18, height: 18)
        .accessibilityHidden(true)
    Text("EMKE Translation")
        .font(
            .system(
                size: EMKEDashboardMetrics.headerTitleSize,
                weight: .semibold
            )
        )
}
```

Change the local diagnostics title block to:

```swift
Label(
    copy.text(.localAudioDiagnostics),
    systemImage: "waveform.badge.magnifyingglass"
)
.font(.system(size: 13, weight: .semibold))
```

- [ ] **Step 4: Run focused tests and dashboard render checks**

Run:

```bash
swift test --filter dashboardBrandFooterIsExactInBothLanguages
swift test --filter TranslationDashboardAccessibilityTests
swift test --filter TranslationDashboardRenderTests
```

Expected: all selected tests pass and existing render-bound assertions remain
green.

- [ ] **Step 5: Commit the UI polish**

```bash
git add Sources/EMKEMenuBarApp/AppLocalization.swift \
  Sources/EMKEMenuBarApp/TranslationDashboardView.swift \
  Sources/EMKEMenuBarApp/TranslationSettingsView.swift \
  Tests/EMKEAudioEngineTests/AppLocalizationTests.swift \
  Tests/EMKEAudioEngineTests/TranslationDashboardAccessibilityTests.swift
git commit -m "feat: polish EMKE dashboard branding"
```

## Task 3: Add Versioned Onboarding State and Permission Presentation

**Files:**

- Create: `Sources/EMKEMenuBarApp/OnboardingProgressStore.swift`
- Create: `Sources/EMKEMenuBarApp/OnboardingPresentation.swift`
- Create: `Tests/EMKEAudioEngineTests/OnboardingProgressStoreTests.swift`
- Create: `Tests/EMKEAudioEngineTests/OnboardingPresentationTests.swift`

**Interfaces:**

- Consumes: `MicrophonePermissionState` and `UserDefaults`.
- Produces: `OnboardingVersion.current == 1`,
  `OnboardingProgressStoring`, `UserDefaultsOnboardingProgressStore`,
  `OnboardingStep`, `OnboardingFlowState`, and
  `OnboardingMicrophonePresentation`.

- [ ] **Step 1: Write failing persistence tests**

```swift
import Foundation
import Testing
@testable import EMKEMenuBarApp

@Test @MainActor
func missingAndInvalidOnboardingVersionsRequirePresentation() {
    let suite = "OnboardingProgressStoreTests.\(UUID())"
    let defaults = UserDefaults(suiteName: suite)!
    defer { defaults.removePersistentDomain(forName: suite) }
    let store = UserDefaultsOnboardingProgressStore(defaults: defaults)

    #expect(store.shouldPresent(currentVersion: 1))
    defaults.set("invalid", forKey: "completedOnboardingVersion")
    #expect(store.shouldPresent(currentVersion: 1))
}

@Test @MainActor
func completionSuppressesCurrentVersionButNotFutureVersion() {
    let suite = "OnboardingProgressStoreTests.\(UUID())"
    let defaults = UserDefaults(suiteName: suite)!
    defer { defaults.removePersistentDomain(forName: suite) }
    let store = UserDefaultsOnboardingProgressStore(defaults: defaults)

    store.markCompleted(version: 1)

    #expect(!store.shouldPresent(currentVersion: 1))
    #expect(store.shouldPresent(currentVersion: 2))
}
```

- [ ] **Step 2: Write failing flow and permission-presentation tests**

```swift
@Test
func onboardingFlowHasFourBoundedSteps() {
    var flow = OnboardingFlowState()
    #expect(flow.step == .overview)
    #expect(!flow.canMoveBackward)

    flow.moveForward()
    #expect(flow.step == .microphone)
    flow.moveForward()
    #expect(flow.step == .audioSetup)
    flow.moveForward()
    #expect(flow.step == .meetingSetup)
    #expect(!flow.canMoveForward)
}

@Test
func microphonePresentationNeverOffersRepeatPromptAfterDenial() {
    #expect(
        OnboardingMicrophonePresentation.make(.notDetermined).action
            == .requestAccess
    )
    #expect(
        OnboardingMicrophonePresentation.make(.authorized).action
            == .continueFlow
    )
    #expect(
        OnboardingMicrophonePresentation.make(.denied).action
            == .openSystemSettings
    )
    #expect(
        OnboardingMicrophonePresentation.make(.restricted).action
            == .continueFlow
    )
}
```

- [ ] **Step 3: Run tests and verify red**

Run:

```bash
swift test --filter OnboardingProgressStoreTests
swift test --filter OnboardingPresentationTests
```

Expected: compilation fails because the onboarding types do not exist.

- [ ] **Step 4: Implement version persistence**

```swift
import Foundation

enum OnboardingVersion {
    static let current = 1
}

@MainActor
protocol OnboardingProgressStoring {
    func shouldPresent(currentVersion: Int) -> Bool
    func markCompleted(version: Int)
}

@MainActor
struct UserDefaultsOnboardingProgressStore: OnboardingProgressStoring {
    private let defaults: UserDefaults
    private let key = "completedOnboardingVersion"

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    func shouldPresent(currentVersion: Int) -> Bool {
        guard let value = defaults.object(forKey: key) as? Int else {
            return true
        }
        return value < currentVersion
    }

    func markCompleted(version: Int) {
        defaults.set(version, forKey: key)
    }
}
```

- [ ] **Step 5: Implement pure flow and permission mapping**

```swift
enum OnboardingStep: Int, CaseIterable, Sendable {
    case overview
    case microphone
    case audioSetup
    case meetingSetup
}

struct OnboardingFlowState: Equatable, Sendable {
    private(set) var step: OnboardingStep = .overview

    var canMoveBackward: Bool { step.rawValue > 0 }
    var canMoveForward: Bool {
        step.rawValue < OnboardingStep.allCases.count - 1
    }

    mutating func moveForward() {
        guard canMoveForward,
              let next = OnboardingStep(rawValue: step.rawValue + 1) else {
            return
        }
        step = next
    }

    mutating func moveBackward() {
        guard canMoveBackward,
              let previous = OnboardingStep(rawValue: step.rawValue - 1) else {
            return
        }
        step = previous
    }

    mutating func restart() { step = .overview }
}

enum OnboardingMicrophoneAction: Equatable, Sendable {
    case requestAccess
    case openSystemSettings
    case continueFlow
}

struct OnboardingMicrophonePresentation: Equatable, Sendable {
    let action: OnboardingMicrophoneAction

    static func make(
        _ state: MicrophonePermissionState
    ) -> OnboardingMicrophonePresentation {
        switch state {
        case .notDetermined:
            .init(action: .requestAccess)
        case .denied:
            .init(action: .openSystemSettings)
        case .restricted, .authorized:
            .init(action: .continueFlow)
        }
    }
}
```

- [ ] **Step 6: Run onboarding state tests**

Run:

```bash
swift test --filter OnboardingProgressStoreTests
swift test --filter OnboardingPresentationTests
```

Expected: all onboarding state tests pass.

- [ ] **Step 7: Commit onboarding state**

```bash
git add Sources/EMKEMenuBarApp/OnboardingProgressStore.swift \
  Sources/EMKEMenuBarApp/OnboardingPresentation.swift \
  Tests/EMKEAudioEngineTests/OnboardingProgressStoreTests.swift \
  Tests/EMKEAudioEngineTests/OnboardingPresentationTests.swift
git commit -m "feat: add versioned onboarding state"
```

## Task 4: Expose Onboarding-safe Permission and Diagnostic State

**Files:**

- Modify: `Sources/EMKEMenuBarApp/MenuBarModel.swift`
- Modify: `Tests/EMKEAudioEngineTests/MenuBarTranslationModelTests.swift`

**Interfaces:**

- Consumes: `MicrophonePermissionProviding.authorizationStatus()` and
  `requestAccess()`.
- Produces: published read-only `microphonePermissionState`,
  `refreshMicrophonePermissionState() async`, and
  `requestMicrophonePermissionForOnboarding() async`.

- [ ] **Step 1: Write failing permission-state tests**

```swift
@Test @MainActor
func onboardingRefreshesMicrophoneStateWithoutRequestingPermission() async {
    let permission = MicrophonePermissionStub(state: .notDetermined)
    let model = makeTranslationMenuModel(
        microphonePermissionProvider: permission
    )

    await model.refreshMicrophonePermissionState()

    #expect(model.microphonePermissionState == .notDetermined)
    #expect(await permission.requestCount == 0)
}

@Test @MainActor
func onboardingPermissionActionRequestsOnceAndPublishesResult() async {
    let permission = MicrophonePermissionStub(
        state: .notDetermined,
        requestResult: true
    )
    let model = makeTranslationMenuModel(
        microphonePermissionProvider: permission
    )

    await model.requestMicrophonePermissionForOnboarding()
    await model.requestMicrophonePermissionForOnboarding()

    #expect(model.microphonePermissionState == .authorized)
    #expect(await permission.requestCount == 1)
}
```

Extend the existing stub with `requestCount` and make the test factory accept
the injected permission provider.

- [ ] **Step 2: Run focused tests and verify red**

Run:

```bash
swift test --filter onboardingRefreshesMicrophoneStateWithoutRequestingPermission
swift test --filter onboardingPermissionActionRequestsOnceAndPublishesResult
```

Expected: compilation fails because the model APIs do not exist.

- [ ] **Step 3: Implement state refresh and guarded request**

Add:

```swift
@Published private(set) var microphonePermissionState:
    MicrophonePermissionState = .notDetermined

func refreshMicrophonePermissionState() async {
    microphonePermissionState =
        await microphonePermissionProvider.authorizationStatus()
}

func requestMicrophonePermissionForOnboarding() async {
    await refreshMicrophonePermissionState()
    guard microphonePermissionState == .notDetermined else { return }
    let granted = await microphonePermissionProvider.requestAccess()
    microphonePermissionState = granted ? .authorized : .denied
}
```

Update the existing start-time `requireMicrophonePermission()` to assign the
same published state after checking or requesting, without changing its thrown
configuration errors.

- [ ] **Step 4: Run permission and existing start tests**

Run:

```bash
swift test --filter onboardingRefreshesMicrophoneStateWithoutRequestingPermission
swift test --filter onboardingPermissionActionRequestsOnceAndPublishesResult
swift test --filter startRequestsUndeterminedMicrophonePermissionBeforeAudio
swift test --filter startStopsBeforeAudioWhenMicrophonePermissionIsDenied
```

Expected: all four tests pass.

- [ ] **Step 5: Commit model permission support**

```bash
git add Sources/EMKEMenuBarApp/MenuBarModel.swift \
  Tests/EMKEAudioEngineTests/MenuBarTranslationModelTests.swift
git commit -m "feat: expose onboarding permission state"
```

## Task 5: Build and Wire the Four-step Onboarding Window

**Files:**

- Create: `Sources/EMKEMenuBarApp/OnboardingView.swift`
- Create: `Sources/EMKEMenuBarApp/OnboardingWindowController.swift`
- Create: `Tests/EMKEAudioEngineTests/OnboardingWindowControllerTests.swift`
- Modify: `Sources/EMKEMenuBarApp/AppLocalization.swift`
- Modify: `Sources/EMKEMenuBarApp/TranslationSettingsView.swift`
- Modify: `Sources/EMKEMenuBarApp/MenuBarRootView.swift`
- Modify: `Sources/EMKEMenuBarApp/EMKEMenuBarApp.swift`
- Modify: `Tests/EMKEAudioEngineTests/AppLocalizationTests.swift`
- Modify: `Tests/EMKEAudioEngineTests/TranslationDashboardAccessibilityTests.swift`

**Interfaces:**

- Consumes: `OnboardingProgressStoring`, `OnboardingFlowState`, shared
  `MenuBarModel`, local diagnostics, and connection-test APIs.
- Produces: `OnboardingWindowController.showIfNeeded()`, `show()`,
  `skipForNow()`, `doNotShowAgain()`, and `complete()`.

- [ ] **Step 1: Write failing controller lifecycle tests**

Add exact in-memory adapters:

```swift
@MainActor
private final class OnboardingProgressStoreStub:
    OnboardingProgressStoring
{
    var shouldPresentValue: Bool
    private(set) var completedVersions: [Int] = []

    init(shouldPresent: Bool = true) {
        shouldPresentValue = shouldPresent
    }

    func shouldPresent(currentVersion: Int) -> Bool {
        shouldPresentValue
    }

    func markCompleted(version: Int) {
        completedVersions.append(version)
        shouldPresentValue = false
    }
}

@MainActor
private final class OnboardingWindowPresenterStub:
    OnboardingWindowPresenting
{
    private(set) var showCount = 0
    private(set) var hideCount = 0
    func show() { showCount += 1 }
    func hide() { hideCount += 1 }
}

@MainActor
private func makeOnboardingController(
    store: OnboardingProgressStoreStub =
        OnboardingProgressStoreStub()
) -> OnboardingWindowController {
    let controller = OnboardingWindowController(progressStore: store)
    controller.attachWindow(OnboardingWindowPresenterStub())
    return controller
}
```

Then add:

```swift
@Test @MainActor
func onboardingShowsOnlyWhenCurrentVersionIsIncomplete() {
    let store = OnboardingProgressStoreStub(shouldPresent: true)
    let controller = makeOnboardingController(store: store)

    controller.showIfNeeded()

    #expect(controller.isVisible)
    #expect(controller.flow.step == .overview)
}

@Test @MainActor
func skipForNowDoesNotCompleteButDoNotShowAgainDoes() {
    let store = OnboardingProgressStoreStub(shouldPresent: true)
    let controller = makeOnboardingController(store: store)

    controller.show()
    controller.skipForNow()
    #expect(store.completedVersions.isEmpty)

    controller.show()
    controller.doNotShowAgain()
    #expect(store.completedVersions == [1])
}

@Test @MainActor
func settingsReopenRestartsAtFirstStep() {
    let controller = makeOnboardingController()
    controller.show()
    controller.moveForward()
    controller.moveForward()

    controller.show()

    #expect(controller.flow.step == .overview)
    #expect(controller.isVisible)
}
```

Inject a lightweight window adapter into the controller tests so they verify
visibility calls without showing a real window.

- [ ] **Step 2: Add failing localization completeness and source wiring tests**

Add copy keys for:

```swift
case gettingStarted
case openGettingStarted
case onboardingSkipForNow
case onboardingDoNotShowAgain
case onboardingBack
case onboardingContinue
case onboardingFinish
case onboardingOverviewTitle
case onboardingOverviewBody
case onboardingMicrophoneTitle
case onboardingMicrophoneBody
case onboardingAllowMicrophone
case onboardingOpenSystemSettings
case onboardingAuthorized
case onboardingDenied
case onboardingRestricted
case onboardingAudioTitle
case onboardingAudioBody
case onboardingMeetingTitle
case onboardingMeetingBody
case onboardingProgress
```

The existing exhaustive `everyStaticCopyKeyHasChineseAndEnglishText` test must
fail until both languages are populated. Add source assertions that
`EMKEMenuBarApp` owns `OnboardingWindowController` and Settings contains
`copy.text(.openGettingStarted)`.

- [ ] **Step 3: Run tests and verify red**

Run:

```bash
swift test --filter OnboardingWindowControllerTests
swift test --filter everyStaticCopyKeyHasChineseAndEnglishText
swift test --filter menuBarAppOwnsOnboardingWindow
```

Expected: compilation or source-contract failure for the missing controller,
view, and copy.

- [ ] **Step 4: Implement the controller with an injectable window adapter**

Use this public shape:

```swift
@MainActor
protocol OnboardingWindowPresenting: AnyObject {
    func show()
    func hide()
}

@MainActor
final class OnboardingWindowController: ObservableObject {
    @Published private(set) var flow = OnboardingFlowState()
    @Published private(set) var isVisible = false

    private let progressStore: any OnboardingProgressStoring
    private var window: (any OnboardingWindowPresenting)?

    init(progressStore: any OnboardingProgressStoring) {
        self.progressStore = progressStore
    }

    func attachWindow(_ window: any OnboardingWindowPresenting) {
        precondition(self.window == nil)
        self.window = window
    }

    func showIfNeeded() {
        guard progressStore.shouldPresent(
            currentVersion: OnboardingVersion.current
        ) else { return }
        show()
    }

    func show() {
        flow.restart()
        isVisible = true
        window?.show()
    }

    func moveForward() {
        flow.moveForward()
    }

    func moveBackward() {
        flow.moveBackward()
    }

    func skipForNow() {
        isVisible = false
        window?.hide()
    }

    func doNotShowAgain() {
        finishAndHide()
    }

    func complete() {
        finishAndHide()
    }

    private func finishAndHide() {
        progressStore.markCompleted(version: OnboardingVersion.current)
        isVisible = false
        window?.hide()
    }
}
```

Implement `OnboardingAppWindowPresenter` as the production adapter. Its
initializer receives `AnyView`, builds an `NSHostingController`, and assigns
it to an `NSWindow` using `.titled`, `.closable`, and `.miniaturizable` style
masks with a 560 × 620 pt initial content size:

```swift
@MainActor
final class OnboardingAppWindowPresenter:
    NSObject,
    OnboardingWindowPresenting,
    NSWindowDelegate
{
    private let window: NSWindow
    private let closeAction: () -> Void

    init(rootView: AnyView, closeAction: @escaping () -> Void) {
        self.closeAction = closeAction
        window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 560, height: 620),
            styleMask: [.titled, .closable, .miniaturizable],
            backing: .buffered,
            defer: false
        )
        super.init()
        window.title = "EMKE Translation"
        window.contentViewController = NSHostingController(rootView: rootView)
        window.isReleasedWhenClosed = false
        window.center()
        window.delegate = self
    }

    func show() {
        NSApplication.shared.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
    }

    func hide() {
        window.orderOut(nil)
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        closeAction()
        return false
    }
}
```

- [ ] **Step 5: Implement the four localized view states**

`OnboardingView` accepts:

```swift
struct OnboardingView: View {
    @ObservedObject var model: MenuBarModel
    @ObservedObject var controller: OnboardingWindowController
}
```

Render:

- Step 1: the two real/virtual audio paths and provider-processing notice.
- Step 2: permission explanation and action selected from
  `OnboardingMicrophonePresentation`.
- Step 3: current driver/device names plus existing microphone/output
  diagnostic buttons and result text.
- Step 4: existing provider fields, connection-test action, and exact meeting
  routing labels.

Use a shared footer containing Back, `n / 4`, Skip for now, Do not show again,
Continue, and Finish according to the current step. Entering a step only calls
state refresh; it does not request permission or start audio tests.

For denied permission, open:

```swift
URL(string:
    "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone"
)
```

through `NSWorkspace.shared.open(_:)`.

- [ ] **Step 6: Wire launch and Settings reopen**

Create one shared model and one onboarding controller in `EMKEMenuBarApp`.
After
`NSApplication.didFinishLaunchingNotification`, call:

```swift
Task { @MainActor in
    await model.loadConfiguration()
    await model.reloadDevicesAsync()
    await model.refreshMicrophonePermissionState()
    onboardingWindowController.showIfNeeded()
}
```

Construct and attach the production window after the controller exists:

```swift
let onboardingController = OnboardingWindowController(
    progressStore: UserDefaultsOnboardingProgressStore()
)
let onboardingWindow = OnboardingAppWindowPresenter(
    rootView: AnyView(
        OnboardingView(
            model: model,
            controller: onboardingController
        )
    ),
    closeAction: { [weak onboardingController] in
        onboardingController?.skipForNow()
    }
)
onboardingController.attachWindow(onboardingWindow)
```

Pass `onboardingWindowController.show` through `MenuBarRootView` to
`TranslationSettingsView` and add the localized secondary action.

- [ ] **Step 7: Add deterministic onboarding captures to the existing renderer**

Extend `CaptureArtifacts.requiredFilenames` with:

```swift
"onboarding-overview-zh.tiff",
"onboarding-microphone-zh.tiff",
"onboarding-audio-zh.tiff",
"onboarding-meeting-zh.tiff",
"onboarding-overview-en.tiff",
"onboarding-microphone-en.tiff",
"onboarding-audio-en.tiff",
"onboarding-meeting-en.tiff",
```

Add a real-view render test:

```swift
@Test @MainActor
func onboardingRendersEveryStepInBothLanguages() async throws {
    for language in [
        AppInterfaceLanguage.zhHans,
        AppInterfaceLanguage.english,
    ] {
        for step in OnboardingStep.allCases {
            let bitmap = try await onboardingBitmap(
                language: language,
                step: step,
                microphoneState: step == .microphone ? .denied : .authorized
            )
            #expect(bitmap.pixelsWide == 1_120)
            #expect(bitmap.pixelsHigh == 1_240)
            try writeQACapture(
                bitmap,
                named:
                    "onboarding-\(step.captureName)-\(language == .zhHans ? "zh" : "en").tiff"
            )
        }
    }
}
```

Add `OnboardingStep.captureName` with exact values `overview`, `microphone`,
`audio`, and `meeting` in the test target. Implement `onboardingBitmap` by
constructing `MenuBarModel` with the same inert device, secret, settings, and
diagnostic test doubles already used by `settingsRender`, refreshing the
injected microphone state, moving the controller to the requested step, and
rendering:

```swift
let renderer = ImageRenderer(
    content: OnboardingView(model: model, controller: controller)
        .frame(width: 560, height: 620)
        .environment(\.colorScheme, .light)
)
renderer.scale = 2
let data = try #require(renderer.nsImage?.tiffRepresentation)
return try #require(NSBitmapImageRep(data: data))
```

Update `captureArtifactDirectoryMatchesExactExpectedSet` to call the
onboarding capture test before comparing the directory with the exact filename
set.

- [ ] **Step 8: Run onboarding, localization, model, source, and render tests**

Run:

```bash
swift test --filter Onboarding
swift test --filter everyStaticCopyKeyHasChineseAndEnglishText
swift test --filter MenuBarTranslationModelTests
swift test --filter TranslationDashboardAccessibilityTests
EMKE_CAPTURE_UI=1 swift test --filter TranslationDashboardRenderTests
```

Expected: all selected tests pass.

- [ ] **Step 9: Commit the onboarding window**

```bash
git add Sources/EMKEMenuBarApp/OnboardingView.swift \
  Sources/EMKEMenuBarApp/OnboardingWindowController.swift \
  Sources/EMKEMenuBarApp/AppLocalization.swift \
  Sources/EMKEMenuBarApp/TranslationSettingsView.swift \
  Sources/EMKEMenuBarApp/MenuBarRootView.swift \
  Sources/EMKEMenuBarApp/EMKEMenuBarApp.swift \
  Tests/EMKEAudioEngineTests/OnboardingWindowControllerTests.swift \
  Tests/EMKEAudioEngineTests/AppLocalizationTests.swift \
  Tests/EMKEAudioEngineTests/TranslationDashboardAccessibilityTests.swift \
  Tests/EMKEAudioEngineTests/TranslationDashboardRenderTests.swift
git commit -m "feat: add first-launch onboarding"
```

## Task 6: Integrate Sparkle Runtime and Settings Update Action

**Files:**

- Modify: `Package.swift`
- Modify: `Package.resolved`
- Create: `Sources/EMKEMenuBarApp/AppUpdateController.swift`
- Create: `Tests/EMKEAudioEngineTests/AppUpdateControllerTests.swift`
- Modify: `Sources/EMKEMenuBarApp/AppLocalization.swift`
- Modify: `Sources/EMKEMenuBarApp/TranslationSettingsView.swift`
- Modify: `Sources/EMKEMenuBarApp/MenuBarRootView.swift`
- Modify: `Sources/EMKEMenuBarApp/EMKEMenuBarApp.swift`
- Modify: `Tests/EMKEAudioEngineTests/AppLocalizationTests.swift`
- Modify: `Tests/EMKEAudioEngineTests/TranslationDashboardAccessibilityTests.swift`

**Interfaces:**

- Consumes: Sparkle `SPUStandardUpdaterController` and
  `SPUUpdater.canCheckForUpdates`.
- Produces: `AppUpdateController.canCheckForUpdates` and
  `checkForUpdates()`.

- [ ] **Step 1: Write the failing update-controller seam test**

```swift
import Combine
import Testing
@testable import EMKEMenuBarApp

@MainActor
private final class UpdaterDriverStub: AppUpdateDriving {
    let availability = CurrentValueSubject<Bool, Never>(false)
    private(set) var checkCount = 0

    var canCheckForUpdates: Bool { availability.value }
    var canCheckForUpdatesPublisher: AnyPublisher<Bool, Never> {
        availability.eraseToAnyPublisher()
    }

    func checkForUpdates() { checkCount += 1 }
}

@Test @MainActor
func updateControllerMirrorsAvailabilityAndForwardsManualCheck() {
    let driver = UpdaterDriverStub()
    let controller = AppUpdateController(driver: driver)

    driver.availability.send(true)
    controller.refreshAvailability()
    controller.checkForUpdates()

    #expect(controller.canCheckForUpdates)
    #expect(driver.checkCount == 1)
}
```

Add source-contract assertions that Settings contains
`copy.text(.checkForUpdates)` and disables the button with
`!updateController.canCheckForUpdates`.

- [ ] **Step 2: Run focused tests and verify red**

Run:

```bash
swift test --filter updateControllerMirrorsAvailabilityAndForwardsManualCheck
swift test --filter settingsWiresManualUpdateCheck
```

Expected: compilation fails because update types and copy do not exist.

- [ ] **Step 3: Pin and link Sparkle**

Add:

```swift
.package(
    url: "https://github.com/sparkle-project/Sparkle",
    exact: "2.9.2"
),
```

and add this executable dependency:

```swift
.product(name: "Sparkle", package: "Sparkle"),
```

Run:

```bash
swift package resolve
```

Expected: `Package.resolved` records Sparkle version `2.9.2`.

- [ ] **Step 4: Implement the test seam and Sparkle driver**

```swift
import Combine
import Sparkle

@MainActor
protocol AppUpdateDriving: AnyObject {
    var canCheckForUpdates: Bool { get }
    var canCheckForUpdatesPublisher: AnyPublisher<Bool, Never> { get }
    func checkForUpdates()
}

@MainActor
final class SparkleUpdateDriver: AppUpdateDriving {
    let controller = SPUStandardUpdaterController(
        startingUpdater: true,
        updaterDelegate: nil,
        userDriverDelegate: nil
    )

    var canCheckForUpdates: Bool {
        controller.updater.canCheckForUpdates
    }

    var canCheckForUpdatesPublisher: AnyPublisher<Bool, Never> {
        controller.updater
            .publisher(for: \.canCheckForUpdates)
            .eraseToAnyPublisher()
    }

    func checkForUpdates() {
        controller.checkForUpdates(nil)
    }
}

@MainActor
final class AppUpdateController: ObservableObject {
    @Published private(set) var canCheckForUpdates = false
    private let driver: any AppUpdateDriving
    private var availabilityCancellable: AnyCancellable?

    init(driver: any AppUpdateDriving = SparkleUpdateDriver()) {
        self.driver = driver
        refreshAvailability()
        availabilityCancellable = driver.canCheckForUpdatesPublisher
            .receive(on: RunLoop.main)
            .sink { [weak self] canCheck in
                self?.canCheckForUpdates = canCheck
            }
    }

    func refreshAvailability() {
        canCheckForUpdates = driver.canCheckForUpdates
    }

    func checkForUpdates() {
        guard canCheckForUpdates else { return }
        driver.checkForUpdates()
        refreshAvailability()
    }
}
```

- [ ] **Step 5: Add localized Settings action and app ownership**

Add `checkForUpdates` copy:

```swift
case .checkForUpdates:
    localized(zhHans: "检查更新…", english: "Check for Updates…")
```

Own one `@StateObject` update controller in `EMKEMenuBarApp`, pass it to
`MenuBarRootView` and `TranslationSettingsView`, and render:

```swift
Button(copy.text(.checkForUpdates)) {
    updateController.checkForUpdates()
}
.disabled(!updateController.canCheckForUpdates)
```

Keep this action outside provider/audio selection locking.

- [ ] **Step 6: Run update, localization, and app wiring tests**

Run:

```bash
swift test --filter AppUpdateControllerTests
swift test --filter everyStaticCopyKeyHasChineseAndEnglishText
swift test --filter settingsWiresManualUpdateCheck
swift build --product EMKEMenuBarApp
```

Expected: selected tests pass and the product build exits 0.

- [ ] **Step 7: Commit Sparkle runtime integration**

```bash
git add Package.swift Package.resolved \
  Sources/EMKEMenuBarApp/AppUpdateController.swift \
  Sources/EMKEMenuBarApp/AppLocalization.swift \
  Sources/EMKEMenuBarApp/TranslationSettingsView.swift \
  Sources/EMKEMenuBarApp/MenuBarRootView.swift \
  Sources/EMKEMenuBarApp/EMKEMenuBarApp.swift \
  Tests/EMKEAudioEngineTests/AppUpdateControllerTests.swift \
  Tests/EMKEAudioEngineTests/AppLocalizationTests.swift \
  Tests/EMKEAudioEngineTests/TranslationDashboardAccessibilityTests.swift
git commit -m "feat: add Sparkle update checks"
```

## Task 7: Make Packaging Versioned and Sparkle-aware

**Files:**

- Modify: `Packaging/App/Info.plist`
- Modify: `Packaging/App/EMKETranslation.entitlements`
- Modify: `Packaging/Scripts/build-app-bundle.sh`
- Modify: `Packaging/build-internal-pkg.sh`
- Create: `Packaging/Scripts/render-appcast.sh`
- Create: `Packaging/Tests/release-metadata-test.sh`
- Modify: `Packaging/Tests/app-bundle-test.sh`
- Modify: `Packaging/Tests/package-pipeline-test.sh`
- Modify: `Packaging/Tests/run-all.sh`
- Modify: `Packaging/README.md`

**Interfaces:**

- Consumes: `EMKE_VERSION`, `EMKE_BUILD_NUMBER`, Sparkle framework artifact,
  and an EdDSA signature.
- Produces: versioned app/PKG artifacts and deterministic
  `render-appcast.sh VERSION BUILD URL SIGNATURE LENGTH OUTPUT`.

- [ ] **Step 1: Write failing app-bundle and version-propagation assertions**

Extend `app-bundle-test.sh`:

```bash
export EMKE_VERSION=9.8.7
export EMKE_BUILD_NUMBER=987
bash "$ROOT/Packaging/Scripts/build-app-bundle.sh" "$APP"

test "$(/usr/libexec/PlistBuddy -c \
  'Print :CFBundleShortVersionString' "$PLIST")" = "9.8.7"
test "$(/usr/libexec/PlistBuddy -c \
  'Print :CFBundleVersion' "$PLIST")" = "987"
test "$(/usr/libexec/PlistBuddy -c \
  'Print :SUEnableAutomaticChecks' "$PLIST")" = true
test "$(/usr/libexec/PlistBuddy -c \
  'Print :SUAutomaticallyUpdate' "$PLIST")" = true
test -d "$APP/Contents/Frameworks/Sparkle.framework"
/usr/bin/codesign --verify --deep --strict --verbose=2 "$APP"
```

Add an assertion that `SUPublicEDKey` is non-empty and `SUFeedURL` equals:

```text
https://raw.githubusercontent.com/Halewwang/Simultaneous-interpretation/gh-pages/appcast.xml
```

- [ ] **Step 2: Assert the internal ad-hoc library-validation entitlement**

After extracting app entitlements in `app-bundle-test.sh`, add:

```bash
test "$(/usr/libexec/PlistBuddy -c \
  'Print :com.apple.security.cs.disable-library-validation' \
  "$ENTITLEMENTS")" = true
```

This entitlement is limited to the existing ad-hoc internal distribution. The
future Developer ID/notarized packaging project must remove it and sign all
embedded code with the production team identity.

- [ ] **Step 3: Write failing Appcast renderer tests**

Create `release-metadata-test.sh`:

```bash
#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd -P)"
TEMP="$(mktemp -d "${TMPDIR:-/tmp}/emke-release-test.XXXXXX")"
trap 'rm -rf "$TEMP"' EXIT
OUTPUT="$TEMP/appcast.xml"

bash "$ROOT/Packaging/Scripts/render-appcast.sh" \
  "1.2.3" "123" \
  "https://github.com/Halewwang/Simultaneous-interpretation/releases/download/v1.2.3/EMKE-Translation-1.2.3.pkg" \
  "base64-signature" "4567" "$OUTPUT"

/usr/bin/xmllint --noout "$OUTPUT"
/usr/bin/grep -Fq 'sparkle:shortVersionString="1.2.3"' "$OUTPUT"
/usr/bin/grep -Fq 'sparkle:version="123"' "$OUTPUT"
/usr/bin/grep -Fq 'sparkle:edSignature="base64-signature"' "$OUTPUT"
/usr/bin/grep -Fq 'length="4567"' "$OUTPUT"
! /usr/bin/grep -Fq 'SPARKLE_PRIVATE_KEY' "$OUTPUT"
test -z "$(/usr/bin/find "$ROOT/Packaging" -type f \
  \( -iname '*private*key*' -o -iname '*sparkle*secret*' \) -print)"
echo "PASS: release metadata"
```

- [ ] **Step 4: Run packaging tests and verify red**

Run:

```bash
bash Packaging/Tests/app-bundle-test.sh
bash Packaging/Tests/release-metadata-test.sh
```

Expected: the app test fails on Sparkle/version metadata and the release test
fails because `render-appcast.sh` does not exist.

- [ ] **Step 5: Parameterize app and PKG versions**

At the top of both build scripts, define:

```bash
EMKE_VERSION="${EMKE_VERSION:-0.2.0}"
EMKE_BUILD_NUMBER="${EMKE_BUILD_NUMBER:-2000}"
case "$EMKE_VERSION" in
  *[!0-9.]*|"") echo "invalid EMKE_VERSION" >&2; exit 64 ;;
esac
case "$EMKE_BUILD_NUMBER" in
  *[!0-9]*|"") echo "invalid EMKE_BUILD_NUMBER" >&2; exit 64 ;;
esac
```

After copying `Info.plist`, set:

```bash
/usr/libexec/PlistBuddy -c \
  "Set :CFBundleShortVersionString $EMKE_VERSION" \
  "$APP/Contents/Info.plist"
/usr/libexec/PlistBuddy -c \
  "Set :CFBundleVersion $EMKE_BUILD_NUMBER" \
  "$APP/Contents/Info.plist"
```

Use `--version "$EMKE_VERSION"` in `pkgbuild` and name the artifact
`EMKE-Translation-$EMKE_VERSION-internal.pkg`.

- [ ] **Step 6: Generate the application-specific signing key**

Locate Sparkle's resolved tool and generate or reuse the application-specific
Keychain item:

```bash
SPARKLE_ACCOUNT="com.emke.translation.app"
GENERATE_KEYS="$(find .build/artifacts -type f -name generate_keys \
  -perm -111 | head -n 1)"
test -x "$GENERATE_KEYS"
"$GENERATE_KEYS" --account "$SPARKLE_ACCOUNT"
SPARKLE_PUBLIC_KEY="$("$GENERATE_KEYS" \
  --account "$SPARKLE_ACCOUNT" -p)"
test -n "$SPARKLE_PUBLIC_KEY"
```

Add `SUPublicEDKey` to `Packaging/App/Info.plist` with that public value, along
with the exact feed URL and automatic-check/download booleans. Do not export
the private key in this task.

Add this internal-build entitlement:

```xml
<key>com.apple.security.cs.disable-library-validation</key>
<true/>
```

- [ ] **Step 7: Embed and sign Sparkle**

Add the four Info.plist keys from Global Constraints. Locate the resolved
framework below the release binary directory, copy it with `/usr/bin/ditto`
into `Contents/Frameworks`, preserve symlinks, and sign inside-out:

```bash
mkdir -p "$APP/Contents/Frameworks"
SPARKLE_FRAMEWORK="$(find "$ROOT/.build/artifacts" \
  -type d -name Sparkle.framework | head -n 1)"
test -d "$SPARKLE_FRAMEWORK"
/usr/bin/ditto "$SPARKLE_FRAMEWORK" \
  "$APP/Contents/Frameworks/Sparkle.framework"

if ! /usr/bin/otool -l "$APP/Contents/MacOS/EMKEMenuBarApp" \
  | /usr/bin/grep -Fq '@executable_path/../Frameworks'; then
  /usr/bin/install_name_tool -add_rpath \
    '@executable_path/../Frameworks' \
    "$APP/Contents/MacOS/EMKEMenuBarApp"
fi

while IFS= read -r nested; do
  /usr/bin/codesign --force --sign - --options runtime --timestamp=none \
    "$nested"
done < <(
  /usr/bin/find "$APP/Contents/Frameworks/Sparkle.framework" \
    -type d \( -name '*.xpc' -o -name '*.app' \) -print | \
    /usr/bin/sort -r
)

/usr/bin/codesign --force --sign - --options runtime --timestamp=none \
  "$APP/Contents/Frameworks/Sparkle.framework"
```

Then sign the app with its existing audio-input entitlement and verify the
framework plus app strictly.

- [ ] **Step 8: Implement deterministic Appcast rendering**

Create an executable script that validates all six arguments and writes:

```xml
<?xml version="1.0" encoding="utf-8"?>
<rss version="2.0"
  xmlns:sparkle="http://www.andymatuschak.org/xml-namespaces/sparkle">
  <channel>
    <title>EMKE Translation Updates</title>
    <link>https://github.com/Halewwang/Simultaneous-interpretation</link>
    <description>Signed EMKE Translation updates</description>
    <language>en</language>
    <item>
      <title>EMKE Translation VERSION</title>
      <pubDate>DATE</pubDate>
      <enclosure
        url="URL"
        sparkle:version="BUILD"
        sparkle:shortVersionString="VERSION"
        sparkle:edSignature="SIGNATURE"
        length="LENGTH"
        type="application/octet-stream" />
    </item>
  </channel>
</rss>
```

Replace the uppercase tokens with XML-escaped explicit inputs and an RFC 2822
UTC date. Reject versions/builds/lengths outside numeric formats, non-HTTPS
URLs, empty signatures, unsafe output paths, and output paths outside a
validated parent directory.

- [ ] **Step 9: Run complete packaging tests**

Run:

```bash
bash Packaging/Tests/run-all.sh
EMKE_VERSION=0.2.0 EMKE_BUILD_NUMBER=2000 \
  bash Packaging/build-internal-pkg.sh
```

Expected: every packaging test passes and the verified delivery path ends in
`EMKE-Translation-0.2.0-internal.pkg`.

- [ ] **Step 10: Commit versioned signed-update packaging**

```bash
git add Packaging/App/Info.plist \
  Packaging/App/EMKETranslation.entitlements \
  Packaging/Scripts/build-app-bundle.sh \
  Packaging/build-internal-pkg.sh \
  Packaging/Scripts/render-appcast.sh \
  Packaging/Tests/release-metadata-test.sh \
  Packaging/Tests/app-bundle-test.sh \
  Packaging/Tests/package-pipeline-test.sh \
  Packaging/Tests/run-all.sh Packaging/README.md
git commit -m "feat: package signed Sparkle updates"
```

## Task 8: Add Tag-driven GitHub Release and Appcast Publication

**Files:**

- Create: `.github/workflows/release.yml`
- Modify: `Packaging/Tests/release-metadata-test.sh`
- Modify: `Packaging/README.md`

**Interfaces:**

- Consumes: tag `vMAJOR.MINOR.PATCH`,
  `SPARKLE_PRIVATE_KEY`, packaging scripts, and Sparkle `sign_update`.
- Produces: GitHub Release PKG asset and `gh-pages:appcast.xml`.

- [ ] **Step 1: Add failing workflow contract checks**

Extend `release-metadata-test.sh`:

```bash
WORKFLOW="$ROOT/.github/workflows/release.yml"
test -s "$WORKFLOW"
/usr/bin/grep -Fq "tags:" "$WORKFLOW"
/usr/bin/grep -Fq "SPARKLE_PRIVATE_KEY" "$WORKFLOW"
/usr/bin/grep -Fq "Packaging/Tests/run-all.sh" "$WORKFLOW"
/usr/bin/grep -Fq "Packaging/build-internal-pkg.sh" "$WORKFLOW"
/usr/bin/grep -Fq "sign_update" "$WORKFLOW"
/usr/bin/grep -Fq "render-appcast.sh" "$WORKFLOW"
/usr/bin/grep -Fq "gh release create" "$WORKFLOW"
/usr/bin/grep -Fq "gh-pages" "$WORKFLOW"
```

- [ ] **Step 2: Run the test and verify red**

Run:

```bash
bash Packaging/Tests/release-metadata-test.sh
```

Expected: failure because `.github/workflows/release.yml` does not exist.

- [ ] **Step 3: Implement the tag workflow**

Use:

```yaml
name: Release EMKE Translation

on:
  push:
    tags:
      - "v[0-9]*.[0-9]*.[0-9]*"

permissions:
  contents: write

jobs:
  release:
    runs-on: macos-26
    steps:
      - uses: actions/checkout@v6
        with:
          fetch-depth: 0
      - name: Resolve version
        shell: bash
        run: |
          set -euo pipefail
          version="${GITHUB_REF_NAME#v}"
          [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]
          IFS=. read -r major minor patch <<< "$version"
          test "$major" -lt 1000
          test "$minor" -lt 1000
          test "$patch" -lt 1000
          build_number="$((10#$major * 1000000 + 10#$minor * 1000 + 10#$patch))"
          echo "EMKE_VERSION=$version" >> "$GITHUB_ENV"
          echo "EMKE_BUILD_NUMBER=$build_number" >> "$GITHUB_ENV"
      - name: Test
        run: swift test
      - name: Verify packaging
        run: bash Packaging/Tests/run-all.sh
      - name: Build package
        run: bash Packaging/build-internal-pkg.sh
      - name: Sign update and render Appcast
        env:
          SPARKLE_PRIVATE_KEY: ${{ secrets.SPARKLE_PRIVATE_KEY }}
        shell: bash
        run: |
          set -euo pipefail
          test -n "$SPARKLE_PRIVATE_KEY"
          pkg=".build/distribution/EMKE-Translation-${EMKE_VERSION}-internal.pkg"
          sign_tool="$(find .build/artifacts -type f -name sign_update -perm -111 | head -n 1)"
          test -x "$sign_tool"
          key_file="$(mktemp "$RUNNER_TEMP/emke-sparkle-key.XXXXXX")"
          trap 'rm -f "$key_file"' EXIT
          chmod 600 "$key_file"
          printf '%s' "$SPARKLE_PRIVATE_KEY" > "$key_file"
          signing="$("$sign_tool" --ed-key-file "$key_file" "$pkg")"
          signature="$(sed -E 's/.*sparkle:edSignature="([^"]+)".*/\1/' <<< "$signing")"
          length="$(stat -f %z "$pkg")"
          url="https://github.com/${GITHUB_REPOSITORY}/releases/download/${GITHUB_REF_NAME}/$(basename "$pkg")"
          bash Packaging/Scripts/render-appcast.sh \
            "$EMKE_VERSION" "$EMKE_BUILD_NUMBER" "$url" \
            "$signature" "$length" "$RUNNER_TEMP/appcast.xml"
      - name: Publish release
        env:
          GH_TOKEN: ${{ github.token }}
        shell: bash
        run: |
          set -euo pipefail
          pkg=".build/distribution/EMKE-Translation-${EMKE_VERSION}-internal.pkg"
          gh release create "$GITHUB_REF_NAME" "$pkg" \
            --title "EMKE Translation ${EMKE_VERSION}" \
            --generate-notes
      - name: Publish Appcast branch
        shell: bash
        run: |
          set -euo pipefail
          publish="$RUNNER_TEMP/emke-appcast-branch"
          git clone "https://x-access-token:${GITHUB_TOKEN}@github.com/${GITHUB_REPOSITORY}.git" "$publish"
          cd "$publish"
          if git show-ref --verify --quiet refs/remotes/origin/gh-pages; then
            git checkout -B gh-pages origin/gh-pages
          else
            git checkout --orphan gh-pages
            git rm -rf . || true
          fi
          cp "$RUNNER_TEMP/appcast.xml" appcast.xml
          git add appcast.xml
          git -c user.name=github-actions \
            -c user.email=41898282+github-actions[bot]@users.noreply.github.com \
            commit -m "release: publish ${EMKE_VERSION} appcast"
          git push origin gh-pages
        env:
          GITHUB_TOKEN: ${{ github.token }}
```

Keep cleanup targets inside `$RUNNER_TEMP`. The release must be created before
the Appcast branch is updated, so the feed never advertises a missing asset.

- [ ] **Step 4: Run local workflow and metadata validation**

Run:

```bash
bash Packaging/Tests/release-metadata-test.sh
ruby -e 'require "yaml"; YAML.load_file(".github/workflows/release.yml"); puts "PASS: release workflow yaml"'
```

Expected: release metadata and YAML parsing pass.

- [ ] **Step 5: Commit release automation**

```bash
git add .github/workflows/release.yml \
  Packaging/Tests/release-metadata-test.sh Packaging/README.md
git commit -m "ci: publish signed EMKE updates"
```

## Task 9: Verify, Configure GitHub, Publish Bootstrap Source, and Report Boundaries

**Files:**

- Verify all changed files.
- No additional production file is required unless verification exposes a
  scoped defect.

**Interfaces:**

- Consumes: all prior tasks, GitHub repository
  `Halewwang/Simultaneous-interpretation`, local Keychain, and Git credential
  helper.
- Produces: verified `main`, configured `origin`, Sparkle signing secret,
  pushed source history, and bootstrap tag `v0.2.0` after all gates pass.

- [ ] **Step 1: Run the complete Swift suite**

Run:

```bash
swift test
```

Expected: zero test failures. Record the executed and skipped counts exactly.

- [ ] **Step 2: Run release/product and strict C builds**

Run:

```bash
swift build -c release --product EMKEMenuBarApp
clang -std=c11 -Wall -Wextra -Werror -fsyntax-only \
  -I Sources/EMKEAudioBridge/include \
  Sources/EMKEAudioBridge/EMKEAudioRingBuffer.c \
  Sources/EMKEAudioBridge/EMKEAudioRoutes.c
```

Expected: both commands exit 0 without compiler errors.

- [ ] **Step 3: Run complete packaging and PKG verification**

Run:

```bash
bash Packaging/Tests/run-all.sh
EMKE_VERSION=0.2.0 EMKE_BUILD_NUMBER=2000 \
  bash Packaging/build-internal-pkg.sh
bash Packaging/verify-internal-pkg.sh \
  .build/distribution/EMKE-Translation-0.2.0-internal.pkg
```

Expected: every packaging test and final verifier passes.

- [ ] **Step 4: Render Chinese and English visual evidence**

Run the existing deterministic capture suite, now extended in Task 5:

```bash
EMKE_CAPTURE_UI=1 swift test --filter TranslationDashboardRenderTests
test "$(find /tmp/emke-interface-floating-qa -type f -name '*.tiff' \
  | wc -l | tr -d ' ')" = "16"
```

Convert without resizing into a fresh bounded temporary directory:

```bash
VISUAL_DIR="$(mktemp -d "${TMPDIR:-/tmp}/emke-visual-review.XXXXXX")"
for source in /tmp/emke-interface-floating-qa/*.tiff; do
  target="$VISUAL_DIR/$(basename "${source%.tiff}").png"
  sips -s format png "$source" --out "$target" >/dev/null
done
test "$(find "$VISUAL_DIR" -type f -name '*.png' | wc -l | tr -d ' ')" = "16"
```

Inspect all 16 PNGs at original resolution with the local image viewer. Confirm
the dashboard logo/footer, Settings diagnostics/update/onboarding actions, and
all four onboarding steps in both languages have no clipping, overlap, or
unexpected dashboard geometry regression. Remove only `VISUAL_DIR` after
inspection. Do not add a validation-only application entry point.

- [ ] **Step 5: Verify the committed public key matches the Keychain key**

Locate `generate_keys` and compare only public values:

```bash
SPARKLE_ACCOUNT="com.emke.translation.app"
GENERATE_KEYS="$(find .build/artifacts -type f -name generate_keys \
  -perm -111 | head -n 1)"
test -x "$GENERATE_KEYS"
KEYCHAIN_PUBLIC_KEY="$("$GENERATE_KEYS" --account "$SPARKLE_ACCOUNT" -p)"
PLIST_PUBLIC_KEY="$(/usr/libexec/PlistBuddy -c \
  'Print :SUPublicEDKey' Packaging/App/Info.plist)"
test "$KEYCHAIN_PUBLIC_KEY" = "$PLIST_PUBLIC_KEY"
```

Expected: the committed app key and application-specific Keychain key match.

- [ ] **Step 6: Ensure the GitHub CLI is available**

```bash
if ! command -v gh >/dev/null 2>&1; then
  brew install gh
fi
gh --version
```

Expected: `gh --version` exits 0.

- [ ] **Step 7: Export and upload the CI key without logging it**

```bash
set -euo pipefail
SPARKLE_ACCOUNT="com.emke.translation.app"
GENERATE_KEYS="$(find .build/artifacts -type f -name generate_keys \
  -perm -111 | head -n 1)"
test -x "$GENERATE_KEYS"
KEY_DIR="$(mktemp -d "${TMPDIR:-/tmp}/emke-sparkle-secret.XXXXXX")"
KEY_FILE="$KEY_DIR/private-key"
chmod 700 "$KEY_DIR"
"$GENERATE_KEYS" --account "$SPARKLE_ACCOUNT" -x "$KEY_FILE"
chmod 600 "$KEY_FILE"
test -s "$KEY_FILE"
GH_TOKEN="$(security find-internet-password -s github.com -w)"
export GH_TOKEN
gh secret set SPARKLE_PRIVATE_KEY \
  --repo Halewwang/Simultaneous-interpretation \
  < "$KEY_FILE"
unset GH_TOKEN
rm -f "$KEY_FILE"
rmdir "$KEY_DIR"
```

Never print the key file or token. The key is removed immediately after the
secret upload succeeds.

Verify only the secret name and update timestamp:

```bash
GH_TOKEN="$(security find-internet-password -s github.com -w)"
export GH_TOKEN
gh secret list --repo Halewwang/Simultaneous-interpretation \
  | awk '$1 == "SPARKLE_PRIVATE_KEY" { print $1, $2 }'
unset GH_TOKEN
```

Expected: one `SPARKLE_PRIVATE_KEY` entry; no secret value is displayed.

- [ ] **Step 8: Review final diff and repository cleanliness**

Run:

```bash
git diff 1d5b6cb..HEAD --check
git status --short --branch
git log --oneline --decorate -12
```

Expected: no whitespace errors, no uncommitted files, and the scoped commits
appear in order.

- [ ] **Step 9: Audit the complete history before making it public**

Search filenames only, never matching secret values:

```bash
SECRET_PATHS="$(
  for commit in $(git rev-list --all); do
    git grep -I -l -E \
      'sk-(proj-)?[A-Za-z0-9_-]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|BEGIN [A-Z ]*PRIVATE KEY' \
      "$commit" -- . || true
  done | sort -u
)"
test -z "$SECRET_PATHS"
```

Check for oversized historical blobs:

```bash
git rev-list --objects --all \
  | git cat-file --batch-check='%(objectname) %(objecttype) %(objectsize) %(rest)' \
  | awk '$2 == "blob" && $3 > 50000000 { print $1, $3, $4 }' \
  > /tmp/emke-large-git-blobs.txt
test ! -s /tmp/emke-large-git-blobs.txt
rm -f /tmp/emke-large-git-blobs.txt
```

Expected: no matching secret-bearing paths and no blob larger than 50 MB. If
either check fails, stop before configuring/pushing the public remote and
report only the affected paths or object identifiers, not secret content.

- [ ] **Step 10: Configure and verify `origin`**

Run:

```bash
EXPECTED_ORIGIN="https://github.com/Halewwang/Simultaneous-interpretation.git"
CURRENT_ORIGIN="$(git remote get-url origin 2>/dev/null || true)"
if test -z "$CURRENT_ORIGIN"; then
  git remote add origin "$EXPECTED_ORIGIN"
else
  test "$CURRENT_ORIGIN" = "$EXPECTED_ORIGIN"
fi
git remote -v
git ls-remote --symref origin HEAD
```

If `origin` already exists, verify it matches exactly instead of replacing it.
Expected: fetch/push URLs match the approved repository; the empty remote may
return no refs before the first push.

- [ ] **Step 11: Push verified `main`**

Run:

```bash
git push -u origin main
```

Expected: the remote `main` points to the verified local `HEAD`.

- [ ] **Step 12: Publish the bootstrap tag only after source push succeeds**

Run:

```bash
git tag -a v0.2.0 -m "EMKE Translation 0.2.0"
git push origin v0.2.0
```

Wait for the tag workflow. Inspect its jobs/logs, then verify:

- GitHub Release `v0.2.0` exists;
- the versioned PKG asset exists;
- `gh-pages:appcast.xml` exists and parses;
- its URL, version, build, length, and signature match the asset; and
- no private key appears in logs or repository content.

If the workflow fails, fix only the proved failure and add a regression check.
Do not move or delete the published tag or Release in the same turn; report the
exact failure and request approval before any destructive release retry.

- [ ] **Step 13: Final report**

Report:

- pushed branch, exact commit, tag, release URL, and Appcast URL;
- Swift test counts, build results, packaging results, and visual artifacts;
- verified automatic check/download configuration;
- that `v0.2.0` is the manually installed bootstrap version;
- whether live permission, real microphone, administrator PKG installation,
  and a later-version installed update were performed; and
- that driver-bearing PKG installation still requires administrator
  authorization.
