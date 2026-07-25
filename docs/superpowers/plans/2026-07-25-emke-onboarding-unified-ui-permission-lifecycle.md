# EMKE Unified Onboarding UI and Permission Lifecycle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the rough four-step onboarding with the approved unified left-step-rail layout and keep the same onboarding window visible after the macOS microphone permission prompt resolves.

**Architecture:** Keep `MenuBarModel` as the sole owner of permission state and request coalescing. Add a narrow restore-after-external-prompt contract between `OnboardingView`, `OnboardingWindowController`, and the AppKit presenter; add shared onboarding geometry constants; then rebuild only the SwiftUI onboarding shell while preserving its existing step-specific controls and actions.

**Tech Stack:** Swift 6, SwiftUI, AppKit, AVFoundation, Swift Testing, Swift Package Manager

---

## File Map

- `Sources/EMKEMenuBarApp/OnboardingWindowController.swift`
  - Owns flow visibility, post-prompt restoration, and titlebar-free AppKit window presentation.
- `Sources/EMKEMenuBarApp/OnboardingLayoutMetrics.swift`
  - New focused source for the approved `680 × 560` window and left-rail geometry.
- `Sources/EMKEMenuBarApp/OnboardingView.swift`
  - Owns the unified SwiftUI step rail, main content, permission action state, and integrated footer.
- `Sources/EMKEMenuBarApp/AppLocalization.swift`
  - Adds concise localized step-rail labels and permission-waiting copy.
- `Tests/EMKEAudioEngineTests/OnboardingWindowControllerTests.swift`
  - Proves visible-only restoration without restarting flow.
- `Tests/EMKEAudioEngineTests/AppLocalizationTests.swift`
  - Proves new Chinese and English copy is complete and exact.
- `Tests/EMKEAudioEngineTests/TranslationDashboardAccessibilityTests.swift`
  - Proves permission request ordering, restoration wiring, hidden title-bar configuration, unified structure, and accessibility hooks.
- `Tests/EMKEAudioEngineTests/TranslationDashboardRenderTests.swift`
  - Preserves deterministic Chinese/English captures for all four steps at the new approved dimensions.

## Task 1: Restore the Onboarding Window After Microphone Permission

**Files:**

- Modify: `Tests/EMKEAudioEngineTests/OnboardingWindowControllerTests.swift:25-39,255`
- Modify: `Tests/EMKEAudioEngineTests/TranslationDashboardAccessibilityTests.swift:304-319`
- Modify: `Sources/EMKEMenuBarApp/OnboardingWindowController.swift:4-123`
- Modify: `Sources/EMKEMenuBarApp/OnboardingView.swift:301-316`

- [ ] **Step 1: Write controller regression tests**

Add a front-order counter to the existing presenter stub:

```swift
@MainActor
private final class OnboardingWindowPresenterStub:
    OnboardingWindowPresenting
{
    private(set) var showCount = 0
    private(set) var bringToFrontCount = 0
    private(set) var hideCount = 0

    func show() {
        showCount += 1
    }

    func bringToFront() {
        bringToFrontCount += 1
    }

    func hide() {
        hideCount += 1
    }
}
```

Append these tests:

```swift
@Test @MainActor
func permissionPromptRestoresVisibleWindowWithoutRestartingFlow() {
    let presenter = OnboardingWindowPresenterStub()
    let controller = OnboardingWindowController(
        progressStore: OnboardingProgressStoreStub()
    )
    controller.attachWindow(presenter)
    controller.show()
    controller.moveForward()

    controller.restoreAfterExternalPrompt()

    #expect(controller.isVisible)
    #expect(controller.flow.step == .microphone)
    #expect(presenter.bringToFrontCount == 1)
    #expect(presenter.showCount == 1)
}

@Test @MainActor
func latePermissionResultDoesNotReopenDismissedOnboarding() {
    let presenter = OnboardingWindowPresenterStub()
    let controller = OnboardingWindowController(
        progressStore: OnboardingProgressStoreStub()
    )
    controller.attachWindow(presenter)
    controller.show()
    controller.moveForward()
    controller.skipForNow()

    controller.restoreAfterExternalPrompt()

    #expect(!controller.isVisible)
    #expect(controller.flow.step == .microphone)
    #expect(presenter.bringToFrontCount == 0)
    #expect(presenter.showCount == 1)
}
```

- [ ] **Step 2: Write a source-order regression test for the permission action**

Extend `onboardingPermissionRequestStaysBehindTheExplainedAction()`:

```swift
let permissionRequest = try #require(
    source.range(
        of: "await model.requestMicrophonePermissionForOnboarding()"
    )
)
let restore = try #require(
    source.range(of: "controller.restoreAfterExternalPrompt()")
)

#expect(permissionRequest.upperBound <= restore.lowerBound)
```

Also require the AppKit presenter to use the stronger front-order operation:

```swift
@Test
func onboardingPresenterCanRestoreAfterExternalPrompts() throws {
    let source = try sourceFile(named: "OnboardingWindowController.swift")

    #expect(source.contains("func bringToFront()"))
    #expect(source.contains("window.orderFrontRegardless()"))
    #expect(source.contains("window.makeKey()"))
}
```

- [ ] **Step 3: Run focused tests and verify RED**

Run:

```bash
swift test --filter 'permissionPromptRestoresVisibleWindowWithoutRestartingFlow|latePermissionResultDoesNotReopenDismissedOnboarding|onboardingPermissionRequestStaysBehindTheExplainedAction|onboardingPresenterCanRestoreAfterExternalPrompts'
```

Expected: compilation fails because `bringToFront()` and
`restoreAfterExternalPrompt()` do not exist.

- [ ] **Step 4: Implement the presentation and controller contracts**

Update the protocol:

```swift
@MainActor
protocol OnboardingWindowPresenting: AnyObject {
    func show()
    func bringToFront()
    func hide()
}
```

Add to `OnboardingWindowController`:

```swift
func restoreAfterExternalPrompt() {
    guard isVisible else { return }
    window?.bringToFront()
}
```

Update the AppKit presenter:

```swift
func show() {
    bringToFront()
}

func bringToFront() {
    NSApplication.shared.activate(ignoringOtherApps: true)
    window.orderFrontRegardless()
    window.makeKey()
}
```

Do not call `OnboardingWindowController.show()` from the restoration path,
because `show()` intentionally restarts the flow at Step 1.

- [ ] **Step 5: Wire permission completion to restoration**

Add view-local in-flight state:

```swift
@State private var isRequestingMicrophonePermission = false
```

Replace the `.requestAccess` action with:

```swift
Button(copy.text(.onboardingAllowMicrophone)) {
    guard !isRequestingMicrophonePermission else { return }
    isRequestingMicrophonePermission = true
    Task {
        await model.requestMicrophonePermissionForOnboarding()
        isRequestingMicrophonePermission = false
        controller.restoreAfterExternalPrompt()
    }
}
.buttonStyle(.borderedProminent)
.disabled(isRequestingMicrophonePermission)
```

The waiting copy is added in Task 3. During this task, keep the existing
`.onboardingAllowMicrophone` label and use only the disabled state to prevent a
repeat activation.

- [ ] **Step 6: Run focused tests and verify GREEN**

Run:

```bash
swift test --filter 'OnboardingWindowControllerTests|onboardingPermissionRequestStaysBehindTheExplainedAction|onboardingPresenterCanRestoreAfterExternalPrompts|onboardingPermissionActionRequestsOnceAndPublishesResult|concurrentOnboardingPermissionActionsShareOneProviderRequest'
```

Expected: all selected tests pass with zero failures.

- [ ] **Step 7: Commit the lifecycle repair**

```bash
git add \
  Sources/EMKEMenuBarApp/OnboardingWindowController.swift \
  Sources/EMKEMenuBarApp/OnboardingView.swift \
  Tests/EMKEAudioEngineTests/OnboardingWindowControllerTests.swift \
  Tests/EMKEAudioEngineTests/TranslationDashboardAccessibilityTests.swift
git commit -m "fix: restore onboarding after microphone permission"
```

## Task 2: Add Approved Window Geometry and Hidden Title-bar Chrome

**Files:**

- Create: `Sources/EMKEMenuBarApp/OnboardingLayoutMetrics.swift`
- Modify: `Sources/EMKEMenuBarApp/OnboardingWindowController.swift:94-107`
- Modify: `Sources/EMKEMenuBarApp/OnboardingView.swift:37-75`
- Modify: `Tests/EMKEAudioEngineTests/TranslationDashboardAccessibilityTests.swift:433-442`

- [ ] **Step 1: Write failing geometry and chrome tests**

Add:

```swift
@Test
func onboardingUsesApprovedUnifiedWindowGeometry() {
    #expect(OnboardingLayoutMetrics.windowWidth == 680)
    #expect(OnboardingLayoutMetrics.windowHeight == 560)
    #expect(OnboardingLayoutMetrics.stepRailWidth == 156)
}

@Test
func onboardingWindowHidesSystemChromeWithoutBecomingBorderless() throws {
    let source = try sourceFile(named: "OnboardingWindowController.swift")

    #expect(source.contains(".titled"))
    #expect(source.contains(".fullSizeContentView"))
    #expect(source.contains("window.titleVisibility = .hidden"))
    #expect(source.contains("window.titlebarAppearsTransparent = true"))
    #expect(
        source.contains(
            "window.standardWindowButton(.closeButton)?.isHidden = true"
        )
    )
    #expect(
        source.contains(
            "window.standardWindowButton(.miniaturizeButton)?.isHidden = true"
        )
    )
    #expect(
        source.contains(
            "window.standardWindowButton(.zoomButton)?.isHidden = true"
        )
    )
    #expect(source.contains("window.isMovableByWindowBackground = true"))
    #expect(!source.contains("styleMask: [.borderless]"))
}
```

- [ ] **Step 2: Run focused tests and verify RED**

Run:

```bash
swift test --filter 'onboardingUsesApprovedUnifiedWindowGeometry|onboardingWindowHidesSystemChromeWithoutBecomingBorderless'
```

Expected: compilation fails because `OnboardingLayoutMetrics` does not exist.

- [ ] **Step 3: Add shared layout metrics**

Create `OnboardingLayoutMetrics.swift`:

```swift
import CoreGraphics

enum OnboardingLayoutMetrics {
    static let windowWidth: CGFloat = 680
    static let windowHeight: CGFloat = 560
    static let stepRailWidth: CGFloat = 156
    static let mainHorizontalPadding: CGFloat = 28
    static let mainVerticalPadding: CGFloat = 24
    static let footerHeight: CGFloat = 42
}
```

- [ ] **Step 4: Configure the AppKit window**

Replace hard-coded window geometry and visible title-bar configuration with:

```swift
window = NSWindow(
    contentRect: NSRect(
        x: 0,
        y: 0,
        width: OnboardingLayoutMetrics.windowWidth,
        height: OnboardingLayoutMetrics.windowHeight
    ),
    styleMask: [.titled, .closable, .fullSizeContentView],
    backing: .buffered,
    defer: false
)
super.init()
window.title = "EMKE Translation"
window.titleVisibility = .hidden
window.titlebarAppearsTransparent = true
window.standardWindowButton(.closeButton)?.isHidden = true
window.standardWindowButton(.miniaturizeButton)?.isHidden = true
window.standardWindowButton(.zoomButton)?.isHidden = true
window.isMovableByWindowBackground = true
window.contentViewController = NSHostingController(rootView: rootView)
window.isReleasedWhenClosed = false
window.center()
window.delegate = self
```

Keep `.closable` so Command-W still reaches `windowShouldClose` and maps to
`Skip for Now`.

- [ ] **Step 5: Move the root view to shared geometry**

At the root of `OnboardingView`, replace the hard-coded `560 × 620` frame with:

```swift
.frame(
    width: OnboardingLayoutMetrics.windowWidth,
    height: OnboardingLayoutMetrics.windowHeight
)
.ignoresSafeArea()
```

- [ ] **Step 6: Run focused tests and build**

Run:

```bash
swift test --filter 'onboardingUsesApprovedUnifiedWindowGeometry|onboardingWindowHidesSystemChromeWithoutBecomingBorderless'
swift build --product EMKEMenuBarApp
```

Expected: both commands exit zero.

- [ ] **Step 7: Commit window geometry**

```bash
git add \
  Sources/EMKEMenuBarApp/OnboardingLayoutMetrics.swift \
  Sources/EMKEMenuBarApp/OnboardingWindowController.swift \
  Sources/EMKEMenuBarApp/OnboardingView.swift \
  Tests/EMKEAudioEngineTests/TranslationDashboardAccessibilityTests.swift
git commit -m "feat: unify onboarding window chrome"
```

## Task 3: Add Concise Bilingual Step-rail and Waiting Copy

**Files:**

- Modify: `Sources/EMKEMenuBarApp/AppLocalization.swift:33-60,171-252`
- Modify: `Tests/EMKEAudioEngineTests/AppLocalizationTests.swift:59-96`
- Modify: `Sources/EMKEMenuBarApp/OnboardingView.swift:301-316`

- [ ] **Step 1: Write exact copy tests**

Add:

```swift
@Test
func onboardingStepRailAndPermissionWaitingCopyIsExact() {
    let chinese = AppCopy(language: .zhHans)
    let english = AppCopy(language: .english)

    #expect(chinese.text(.onboardingStepOverview) == "工作方式")
    #expect(english.text(.onboardingStepOverview) == "How It Works")
    #expect(chinese.text(.onboardingStepMicrophone) == "麦克风权限")
    #expect(english.text(.onboardingStepMicrophone) == "Microphone")
    #expect(chinese.text(.onboardingStepAudio) == "音频设备")
    #expect(english.text(.onboardingStepAudio) == "Audio Devices")
    #expect(chinese.text(.onboardingStepMeeting) == "会议设置")
    #expect(english.text(.onboardingStepMeeting) == "Meeting Setup")
    #expect(
        chinese.text(.onboardingWaitingForMicrophone)
            == "等待 macOS 授权…"
    )
    #expect(
        english.text(.onboardingWaitingForMicrophone)
            == "Waiting for macOS…"
    )
}
```

Add all five keys to `onboardingCopyIsCompleteInBothLanguages()`.

- [ ] **Step 2: Run the copy test and verify RED**

Run:

```bash
swift test --filter onboardingStepRailAndPermissionWaitingCopyIsExact
```

Expected: compilation fails because the five `AppCopyKey` cases do not exist.

- [ ] **Step 3: Add localization keys and values**

Add to `AppCopyKey`:

```swift
case onboardingStepOverview
case onboardingStepMicrophone
case onboardingStepAudio
case onboardingStepMeeting
case onboardingWaitingForMicrophone
```

Add to `AppCopy.text(_:)`:

```swift
case .onboardingStepOverview:
    localized(zhHans: "工作方式", english: "How It Works")
case .onboardingStepMicrophone:
    localized(zhHans: "麦克风权限", english: "Microphone")
case .onboardingStepAudio:
    localized(zhHans: "音频设备", english: "Audio Devices")
case .onboardingStepMeeting:
    localized(zhHans: "会议设置", english: "Meeting Setup")
case .onboardingWaitingForMicrophone:
    localized(
        zhHans: "等待 macOS 授权…",
        english: "Waiting for macOS…"
    )
```

Update the permission button from Task 1 to use
`.onboardingWaitingForMicrophone` while the request is in flight.

- [ ] **Step 4: Run localization tests and verify GREEN**

Run:

```bash
swift test --filter 'onboardingStepRailAndPermissionWaitingCopyIsExact|onboardingCopyIsCompleteInBothLanguages|everyStaticCopyKeyHasChineseAndEnglishText'
```

Expected: all selected tests pass.

- [ ] **Step 5: Commit localized onboarding copy**

```bash
git add \
  Sources/EMKEMenuBarApp/AppLocalization.swift \
  Sources/EMKEMenuBarApp/OnboardingView.swift \
  Tests/EMKEAudioEngineTests/AppLocalizationTests.swift
git commit -m "feat: localize unified onboarding navigation"
```

## Task 4: Implement the Unified Left-step-rail SwiftUI Layout

**Files:**

- Modify: `Sources/EMKEMenuBarApp/OnboardingView.swift:19-240,497-569`
- Modify: `Tests/EMKEAudioEngineTests/TranslationDashboardAccessibilityTests.swift:304-442`
- Modify: `Tests/EMKEAudioEngineTests/TranslationDashboardRenderTests.swift:445-665,828-885,1271-1421`

- [ ] **Step 1: Replace the old fixed-chrome source contract with a unified-layout test**

Replace `onboardingPinsChromeToTheFixedWindowCoordinateSpace()` with:

```swift
@Test
func onboardingUsesUnifiedStepRailWithoutHeaderDividers() throws {
    let source = try sourceFile(named: "OnboardingView.swift")

    #expect(source.contains("HStack(spacing: 0)"))
    #expect(source.contains("private var stepRail: some View"))
    #expect(source.contains("private var mainContent: some View"))
    #expect(source.contains("OnboardingLayoutMetrics.stepRailWidth"))
    #expect(source.contains("copy.text(.onboardingDoNotShowAgain)"))
    #expect(source.contains("copy.text(.audioDirectToProvider)"))
    #expect(!source.contains("private var header: some View"))
    #expect(!source.contains("Divider()"))
}
```

Add a semantic navigation test:

```swift
@Test
func onboardingStepRailUsesLocalizedLabelsAndCurrentStepSemantics() throws {
    let source = try sourceFile(named: "OnboardingView.swift")

    #expect(source.contains("copy.text(step.copyKey)"))
    #expect(source.contains("step == controller.flow.step"))
    #expect(source.contains(".accessibilityAddTraits(.isSelected)"))
    #expect(source.contains("copy.text(.onboardingProgress)"))
}
```

- [ ] **Step 2: Update render expectations to the approved geometry**

Change onboarding bitmap expectations:

```swift
#expect(bitmap.pixelsWide == 1_360)
#expect(bitmap.pixelsHigh == 1_120)
```

Change the capture size:

```swift
size: NSSize(
    width: OnboardingLayoutMetrics.windowWidth,
    height: OnboardingLayoutMetrics.windowHeight
),
```

Replace the old identical-header crop with a stable brand crop:

```swift
let brand = canonicalTopOriginRGBAData(
    in: bitmap,
    xRange: 24..<300,
    yRange: 20..<125
)
if let brandTemplate {
    #expect(
        brand == brandTemplate,
        "Onboarding \(step) \(language) must keep the brand rail stable"
    )
} else {
    brandTemplate = brand
}
```

Rename `headerTemplate` to `brandTemplate`.

Update pixel-evidence regions for the new two-column coordinates:

```swift
// Logo: left rail top.
xRange: 28..<96, yRange: 28..<104

// Product name: left rail top.
xRange: 88..<286, yRange: 24..<104

// Main step title.
xRange: 360..<1_300, yRange: 70..<175

// Main step body.
xRange: 360..<1_310, yRange: 150..<290

// Integrated footer.
xRange: 320..<1_330, yRange: 1_000..<1_105

// Skip action.
xRange: 340..<530, yRange: 1_015..<1_100

// Do-not-show action in the left rail.
xRange: 26..<290, yRange: 980..<1_095

// n / 4 counter at top right.
xRange: 1_150..<1_325, yRange: 32..<105

// Primary footer action.
xRange: 1_100..<1_330, yRange: 1_000..<1_105

// Audio diagnostic evidence.
xRange: 760..<1_310, yRange: 620..<925
```

Update broad regions:

```swift
// Fallback-control scan.
xRange: 320..<1_340, yRange: 40..<1_000

// Main-content ink.
xRange: 330..<1_330, yRange: 55..<990
```

Keep the opaque-boundary, edge-overflow, primary-blue, and all-eight-artifact
assertions.

- [ ] **Step 3: Run layout/render tests and verify RED**

Run:

```bash
swift test --filter 'onboardingUsesUnifiedStepRailWithoutHeaderDividers|onboardingStepRailUsesLocalizedLabelsAndCurrentStepSemantics|onboardingRendersEveryStepInBothLanguages'
```

Expected: the source-contract tests fail because the old header/divider layout
is still present, and render expectations fail at the new dimensions.

- [ ] **Step 4: Add step-label mapping**

Add near `OnboardingDeviceSelectionPolicy`:

```swift
private extension OnboardingStep {
    var copyKey: AppCopyKey {
        switch self {
        case .overview: .onboardingStepOverview
        case .microphone: .onboardingStepMicrophone
        case .audioSetup: .onboardingStepAudio
        case .meetingSetup: .onboardingStepMeeting
        }
    }

    var systemImage: String {
        switch self {
        case .overview: "arrow.triangle.branch"
        case .microphone: "mic.fill"
        case .audioSetup: "waveform"
        case .meetingSetup: "person.2.fill"
        }
    }
}
```

- [ ] **Step 5: Replace the root shell with the unified layout**

Replace the `GeometryReader`/`ZStack` body:

```swift
var body: some View {
    HStack(spacing: 0) {
        stepRail
            .frame(width: OnboardingLayoutMetrics.stepRailWidth)
        mainContent
    }
    .frame(
        width: OnboardingLayoutMetrics.windowWidth,
        height: OnboardingLayoutMetrics.windowHeight
    )
    .background(EMKEVisualStyle.panelBackground)
    .ignoresSafeArea()
    .task(id: controller.flow.step) {
        guard refreshesStateOnStepChange else { return }
        await refreshCurrentStep()
    }
}
```

Add:

```swift
private var stepRail: some View {
    VStack(alignment: .leading, spacing: 0) {
        HStack(spacing: 10) {
            Image(systemName: "waveform.path")
                .font(.system(size: 19, weight: .semibold))
                .frame(width: 30, height: 30)
                .background(
                    RoundedRectangle(cornerRadius: 9)
                        .fill(EMKEVisualStyle.primaryText)
                )
                .foregroundStyle(Color.white)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 1) {
                Text("EMKE")
                    .font(.system(size: 13, weight: .bold))
                Text("Translation")
                    .font(.system(size: 9, weight: .medium))
                    .foregroundStyle(EMKEVisualStyle.secondaryText)
            }
        }

        VStack(alignment: .leading, spacing: 18) {
            ForEach(OnboardingStep.allCases, id: \.rawValue) { step in
                stepRailItem(step)
            }
        }
        .padding(.top, 34)

        Spacer(minLength: 16)

        Text(copy.text(.audioDirectToProvider))
            .font(.system(size: 10, weight: .medium))
            .foregroundStyle(EMKEVisualStyle.secondaryText)

        Button(
            copy.text(.onboardingDoNotShowAgain),
            action: controller.doNotShowAgain
        )
        .buttonStyle(.plain)
        .font(.system(size: 10, weight: .medium))
        .foregroundStyle(EMKEVisualStyle.secondaryText)
        .padding(.top, 10)
    }
    .padding(.horizontal, 18)
    .padding(.top, 22)
    .padding(.bottom, 18)
    .frame(maxHeight: .infinity, alignment: .topLeading)
    .background(EMKEVisualStyle.surfaceBackground)
}

private func stepRailItem(_ step: OnboardingStep) -> some View {
    let isCurrent = step == controller.flow.step
    let isComplete = step.rawValue < controller.flow.step.rawValue
    return HStack(spacing: 9) {
        ZStack {
            Circle()
                .fill(
                    isCurrent
                        ? EMKEVisualStyle.activityBlue
                        : Color.white.opacity(0.75)
                )
                .overlay(
                    Circle().stroke(
                        isComplete || isCurrent
                            ? EMKEVisualStyle.activityBlue
                            : EMKEVisualStyle.separator,
                        lineWidth: 1
                    )
                )
            Image(systemName: isComplete ? "checkmark" : step.systemImage)
                .font(.system(size: 9, weight: .bold))
                .foregroundStyle(
                    isCurrent
                        ? Color.white
                        : isComplete
                            ? EMKEVisualStyle.activityBlue
                            : EMKEVisualStyle.secondaryText
                )
        }
        .frame(width: 23, height: 23)

        Text(copy.text(step.copyKey))
            .font(.system(size: 11, weight: isCurrent ? .semibold : .medium))
            .foregroundStyle(
                isCurrent
                    ? EMKEVisualStyle.primaryText
                    : EMKEVisualStyle.secondaryText
            )
            .lineLimit(2)
    }
    .accessibilityElement(children: .combine)
    .accessibilityLabel(copy.text(step.copyKey))
    .accessibilityValue(
        "\(step.rawValue + 1) / \(OnboardingStep.allCases.count)"
    )
    .accessibilityAddTraits(isCurrent ? .isSelected : [])
}
```

- [ ] **Step 6: Add the main content shell and integrated footer**

Add:

```swift
private var mainContent: some View {
    VStack(alignment: .leading, spacing: 0) {
        HStack {
            Text(copy.text(.gettingStarted))
                .font(.system(size: 10, weight: .bold))
                .foregroundStyle(EMKEVisualStyle.activityBlue)
            Spacer()
            let progressText =
                "\(controller.flow.step.rawValue + 1) / \(OnboardingStep.allCases.count)"
            Text(progressText)
                .font(.system(size: 10, weight: .semibold))
                .foregroundStyle(EMKEVisualStyle.secondaryText)
                .padding(.horizontal, 9)
                .padding(.vertical, 5)
                .background(
                    Capsule().fill(EMKEVisualStyle.surfaceBackground)
                )
                .accessibilityLabel(copy.text(.onboardingProgress))
                .accessibilityValue(progressText)
        }

        stepContent
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            .padding(.top, 14)

        footer
            .frame(height: OnboardingLayoutMetrics.footerHeight)
    }
    .padding(.horizontal, OnboardingLayoutMetrics.mainHorizontalPadding)
    .padding(.top, OnboardingLayoutMetrics.mainVerticalPadding)
    .padding(.bottom, 16)
}
```

Replace the old two-row footer with:

```swift
private var footer: some View {
    HStack(spacing: 12) {
        Button(
            copy.text(.onboardingSkipForNow),
            action: controller.skipForNow
        )
        .buttonStyle(.plain)
        .foregroundStyle(EMKEVisualStyle.secondaryText)

        if controller.flow.canMoveBackward {
            Button(
                copy.text(.onboardingBack),
                action: controller.moveBackward
            )
        }

        Spacer()

        if controller.flow.canMoveForward {
            Button(
                copy.text(.onboardingContinue),
                action: controller.moveForward
            )
            .buttonStyle(.borderedProminent)
        } else {
            Button(
                copy.text(.onboardingFinish),
                action: controller.complete
            )
            .buttonStyle(.borderedProminent)
        }
    }
    .font(.system(size: 11, weight: .medium))
}
```

Delete the old `header`, all root-level `Divider()` calls, fixed `.position(...)`
calls, and the duplicate footer progress text.

- [ ] **Step 7: Tighten step content for the shorter window**

Keep all existing actions and bindings. Apply these layout-only adjustments:

```swift
// Heading
VStack(alignment: .leading, spacing: 7)
Label(title, systemImage: systemImage)
    .font(.system(size: 21, weight: .semibold))
Text(body)
    .font(.system(size: 12))
    .lineSpacing(1.5)

// Overview
VStack(alignment: .leading, spacing: 14)

// Microphone
VStack(alignment: .leading, spacing: 16)

// Audio
VStack(alignment: .leading, spacing: 9)

// Meeting
VStack(alignment: .leading, spacing: 10)

// Shared cards
.padding(11)
.background(
    RoundedRectangle(cornerRadius: 11)
        .fill(Color.white.opacity(0.72))
        .overlay(
            RoundedRectangle(cornerRadius: 11)
                .stroke(EMKEVisualStyle.separator, lineWidth: 1)
        )
)
```

Do not alter provider, device, diagnostic, permission, or routing closures.

- [ ] **Step 8: Run focused tests and fix only approved-layout evidence**

Run:

```bash
swift test --filter 'onboardingUsesUnifiedStepRailWithoutHeaderDividers|onboardingStepRailUsesLocalizedLabelsAndCurrentStepSemantics|onboardingRendersEveryStepInBothLanguages'
```

Expected: all selected tests pass. If a pixel evidence threshold misses because
text anti-aliasing differs, inspect the generated crop and adjust only the
corresponding coordinate or threshold; do not weaken opaque-boundary,
edge-overflow, all-eight-artifact, or bilingual coverage.

- [ ] **Step 9: Export deterministic visual captures**

Run:

```bash
EMKE_CAPTURE_UI=1 \
EMKE_CAPTURE_OUTPUT_DIR=/tmp/emke-onboarding-unified-qa \
swift test --filter captureArtifactDirectoryMatchesExactExpectedSet
```

Expected: the test passes and writes exactly the required sixteen TIFF files,
including all eight onboarding captures.

Convert onboarding captures for inspection:

```bash
mkdir -p /tmp/emke-onboarding-unified-qa/png
for source in /tmp/emke-onboarding-unified-qa/onboarding-*.tiff; do
  destination="/tmp/emke-onboarding-unified-qa/png/$(basename "${source%.tiff}.png")"
  sips -s format png "$source" --out "$destination"
done
```

Inspect all eight PNG files for clipping, English pressure, stable rail geometry,
permission status visibility, and control overflow.

- [ ] **Step 10: Commit the unified SwiftUI layout**

```bash
git add \
  Sources/EMKEMenuBarApp/OnboardingView.swift \
  Tests/EMKEAudioEngineTests/TranslationDashboardAccessibilityTests.swift \
  Tests/EMKEAudioEngineTests/TranslationDashboardRenderTests.swift
git commit -m "feat: polish unified onboarding flow"
```

## Task 5: Full Regression, Build, and Scope Verification

**Files:**

- Review only: all files changed by Tasks 1-4

- [ ] **Step 1: Run the complete deterministic test suite**

Run:

```bash
swift test
```

Expected: all deterministic tests pass; any installed-driver-only tests may
report their existing explicit skip.

- [ ] **Step 2: Run a clean release product build**

Run:

```bash
swift build -c release --product EMKEMenuBarApp
```

Expected: exit zero with no compiler errors.

- [ ] **Step 3: Verify the final diff is scoped**

Run:

```bash
git diff origin/main...HEAD -- \
  Sources/EMKEMenuBarApp/OnboardingWindowController.swift \
  Sources/EMKEMenuBarApp/OnboardingLayoutMetrics.swift \
  Sources/EMKEMenuBarApp/OnboardingView.swift \
  Sources/EMKEMenuBarApp/AppLocalization.swift \
  Tests/EMKEAudioEngineTests/OnboardingWindowControllerTests.swift \
  Tests/EMKEAudioEngineTests/AppLocalizationTests.swift \
  Tests/EMKEAudioEngineTests/TranslationDashboardAccessibilityTests.swift \
  Tests/EMKEAudioEngineTests/TranslationDashboardRenderTests.swift
git status --short
```

Expected: only the approved onboarding design, permission-window lifecycle, and
their tests/docs are changed; the worktree is clean after commits.

- [ ] **Step 4: Record the live-permission boundary honestly**

Do not reset macOS microphone privacy state automatically. Automated proof covers
state publication, controller restoration requests, window ordering calls, and
all render states. If a fresh TCC state is available without changing user
settings, perform Allow and Deny manually; otherwise report live TCC acceptance
as not performed and provide the exact five-step acceptance procedure from the
design specification.
