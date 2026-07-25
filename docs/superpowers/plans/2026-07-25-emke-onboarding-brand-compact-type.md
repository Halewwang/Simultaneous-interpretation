# EMKE Onboarding Brand and Compact Type Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the onboarding lockup's temporary waveform with the approved EMKE mark and make the right-side onboarding content visibly calmer with the user-approved compact typography scale.

**Architecture:** Keep the existing `680 × 560` unified onboarding window and all controller/model behavior unchanged. Reuse `MenuBarLogo.image` for the exact approved mark, centralize main-content type and inset values in `OnboardingTypographyMetrics`, and prove both corrections through the existing real SwiftUI bitmap capture path in Chinese and English.

**Tech Stack:** Swift 6.2, SwiftUI, AppKit, Swift Testing, `NSBitmapImageRep`

## Global Constraints

- Scope is the first-launch onboarding window only.
- Keep `OnboardingLayoutMetrics.windowWidth = 680` and `windowHeight = 560`.
- Keep `OnboardingLayoutMetrics.stepRailWidth = 156`.
- Use the existing approved `EMKE-MenuBarIcon.png` resource through `MenuBarLogo.image`; do not redraw or reinterpret the mark.
- Apply the compact typography scale to the main content only; keep the left step navigation typography unchanged.
- Use an `18 pt` semibold step title, `11 pt` supporting body and values, `10–11 pt` labels/status, and approximately `10.5–11 pt` footer actions.
- Use macOS `.small` control size in the main content and a `28 pt` custom device-picker height.
- Do not change permission, audio diagnostic, provider, routing, persistence, update, or translation behavior.
- Keep Chinese and English content visible without clipping in every onboarding step.
- Do not reset macOS microphone permission as part of automated verification.

---

## File Structure

- `Sources/EMKEMenuBarApp/OnboardingView.swift`
  - Render the approved logo and consume compact visual metrics.
- `Sources/EMKEMenuBarApp/OnboardingTypographyMetrics.swift`
  - Own semantic main-content font sizes, icon size, card inset, and picker height.
- `Tests/EMKEAudioEngineTests/TranslationDashboardRenderTests.swift`
  - Verify the rendered logo matches the approved resource and the title/body
    occupy compact vertical bounds across all eight onboarding fixtures.

No permission, model, controller, audio, provider, package, or localization file
is modified by this plan.

### Task 1: Render the approved EMKE mark in the brand lockup

**Files:**
- Modify: `Tests/EMKEAudioEngineTests/TranslationDashboardRenderTests.swift:530-555,1266-1348`
- Modify: `Sources/EMKEMenuBarApp/OnboardingView.swift:84-103`

**Interfaces:**
- Consumes: `@MainActor MenuBarLogo.image: NSImage`
- Produces: the existing onboarding rail with the approved resource rendered as
  an `18 × 18` template mark inside the existing `30 × 30` black tile.

- [ ] **Step 1: Write the failing rendered-logo test**

Add a helper that compares the white mark in the fixed Retina capture region
against the alpha mask of `MenuBarLogo.image`:

```swift
@MainActor
private func onboardingApprovedLogoIntersectionOverUnion(
    in bitmap: NSBitmapImageRep
) throws -> Double {
    let approvedData = try #require(MenuBarLogo.image.tiffRepresentation)
    let approved = try #require(NSBitmapImageRep(data: approvedData))
    let captureOrigin = (x: 80, y: 56)
    var intersection = 0
    var union = 0

    for y in 0..<36 {
        for x in 0..<36 {
            let expectedInk =
                (approved.colorAt(x: x, y: y)?.alphaComponent ?? 0) > 0.5
            let actualColor = try #require(
                bitmap.colorAt(
                    x: captureOrigin.x + x,
                    y: captureOrigin.y + y
                )?.usingColorSpace(.deviceRGB)
            )
            let luminance =
                (0.2126 * actualColor.redComponent)
                + (0.7152 * actualColor.greenComponent)
                + (0.0722 * actualColor.blueComponent)
            let actualInk = luminance > 0.65
            intersection += expectedInk && actualInk ? 1 : 0
            union += expectedInk || actualInk ? 1 : 0
        }
    }

    return Double(intersection) / Double(union)
}
```

In `validatedOnboardingCaptureArtifacts()`, add:

```swift
#expect(
    try onboardingApprovedLogoIntersectionOverUnion(in: bitmap) > 0.8,
    "Onboarding \(step) \(language) must render the approved EMKE mark"
)
```

This exercises the real bundled resource and rendered SwiftUI output. The
current waveform should produce an intersection-over-union near `0.30`, so the
test detects the reported wrong-logo regression rather than merely checking
source text.

- [ ] **Step 2: Run the test to verify RED**

Run:

```bash
swift test --filter onboardingRendersEveryStepInBothLanguages
```

Expected: FAIL with `must render the approved EMKE mark`; the existing
`waveform.path` tile does not match `MenuBarLogo.image`.

- [ ] **Step 3: Replace the waveform with the approved mark**

Replace the leading image in `stepRail` with:

```swift
Image(nsImage: MenuBarLogo.image)
    .resizable()
    .renderingMode(.template)
    .frame(width: 18, height: 18)
    .frame(width: 30, height: 30)
    .background(
        RoundedRectangle(cornerRadius: 9)
            .fill(EMKEVisualStyle.primaryText)
    )
    .foregroundStyle(Color.white)
    .accessibilityHidden(true)
```

Do not change the adjacent `EMKE` / `Translation` text or rail geometry.

- [ ] **Step 4: Run the focused render test to verify GREEN**

Run:

```bash
swift test --filter onboardingRendersEveryStepInBothLanguages
```

Expected: PASS for all eight Chinese/English step fixtures.

- [ ] **Step 5: Commit the logo correction**

```bash
git add Sources/EMKEMenuBarApp/OnboardingView.swift \
  Tests/EMKEAudioEngineTests/TranslationDashboardRenderTests.swift
git commit -m "fix: use approved onboarding logo"
```

### Task 2: Apply the compact right-side typography scale

**Files:**
- Create: `Sources/EMKEMenuBarApp/OnboardingTypographyMetrics.swift`
- Modify: `Sources/EMKEMenuBarApp/OnboardingView.swift:192-230,353-715,739-783`
- Modify: `Tests/EMKEAudioEngineTests/TranslationDashboardRenderTests.swift:530-620,1266-1348`

**Interfaces:**
- Produces: `enum OnboardingTypographyMetrics` with `CGFloat` constants
  `eyebrow`, `title`, `body`, `value`, `label`, `footer`, `statusIcon`,
  `cardPadding`, and `pickerHeight`.
- Consumes: existing `OnboardingLayoutMetrics`; no window or rail geometry
  changes.

- [ ] **Step 1: Write failing compact-render assertions**

Add a real bitmap helper:

```swift
private func onboardingInkVerticalSpan(
    in bitmap: NSBitmapImageRep,
    xRange: Range<Int>,
    yRange: Range<Int>,
    maximumLuminance: Double
) -> Int {
    var minimumY: Int?
    var maximumY: Int?

    for y in yRange {
        for x in xRange {
            guard
                let color = bitmap.colorAt(x: x, y: y)?
                    .usingColorSpace(.deviceRGB)
            else { continue }
            let luminance =
                (0.2126 * color.redComponent)
                + (0.7152 * color.greenComponent)
                + (0.0722 * color.blueComponent)
            guard luminance < maximumLuminance else { continue }
            minimumY = min(minimumY ?? y, y)
            maximumY = max(maximumY ?? y, y)
        }
    }

    guard let minimumY, let maximumY else { return 0 }
    return maximumY - minimumY + 1
}
```

In `validatedOnboardingCaptureArtifacts()`, add:

```swift
#expect(
    onboardingInkVerticalSpan(
        in: bitmap,
        xRange: 360..<1_300,
        yRange: 110..<180,
        maximumLuminance: 0.7
    ) <= 40,
    "Onboarding \(step) \(language) title must use compact typography"
)
#expect(
    onboardingInkVerticalSpan(
        in: bitmap,
        xRange: 360..<1_310,
        yRange: 180..<285,
        maximumLuminance: 0.7
    ) <= 50,
    "Onboarding \(step) \(language) body must use compact typography"
)
```

The current `21 pt` title produces spans up to approximately `48` pixels and the
current body produces spans above `50` pixels, so both assertions fail before
the implementation.

- [ ] **Step 2: Run the render test to verify RED**

Run:

```bash
swift test --filter onboardingRendersEveryStepInBothLanguages
```

Expected: FAIL with compact title/body assertion messages.

- [ ] **Step 3: Add semantic typography metrics**

Create `OnboardingTypographyMetrics.swift`:

```swift
import CoreGraphics

enum OnboardingTypographyMetrics {
    static let eyebrow: CGFloat = 9
    static let title: CGFloat = 18
    static let body: CGFloat = 11
    static let value: CGFloat = 11
    static let label: CGFloat = 10
    static let footer: CGFloat = 10.5
    static let statusIcon: CGFloat = 15
    static let cardPadding: CGFloat = 9
    static let pickerHeight: CGFloat = 28
}
```

- [ ] **Step 4: Apply metrics to every right-side text/control category**

In `mainContent`, use `eyebrow` for the eyebrow and progress counter, reduce
their capsule inset to `8` horizontal / `4` vertical, reduce step-content top
padding from `14` to `12`, and apply:

```swift
.controlSize(.small)
```

to the main-content container.

Use the semantic metrics throughout the right side:

```swift
// Heading
.font(.system(size: OnboardingTypographyMetrics.title, weight: .semibold))
.font(.system(size: OnboardingTypographyMetrics.body))
.lineSpacing(1)

// Route text and status/card values
.font(.system(size: OnboardingTypographyMetrics.value, weight: .medium))

// Field labels, diagnostic status, provider labels, routing labels
.font(.system(size: OnboardingTypographyMetrics.label))

// Footer
.font(.system(size: OnboardingTypographyMetrics.footer, weight: .medium))
```

Change status icons to `statusIcon`, card/status/route padding to `cardPadding`,
provider and routing card padding to `10`, and device-picker values to `value`.
Change device-picker label font to `label`, horizontal inset to `9`, and height
to `pickerHeight`. Keep the popover editor at its existing size because it is a
separate editing surface, not the crowded onboarding content shown in the
reported screenshot.

- [ ] **Step 5: Rebalance render-presence thresholds without weakening coverage**

The smaller type intentionally contains fewer dark pixels. Keep all semantic
regions and change only the minimum presence thresholds in
`validatedOnboardingCaptureArtifacts()`:

```swift
onboardingStepTitleInkPixels(in: bitmap) > 2_500
onboardingStepBodyInkPixels(in: bitmap) > 3_500
```

Retain the existing logo, product name, footer, skip, suppression, progress,
primary action, diagnostic, fallback, edge, and opacity assertions.

- [ ] **Step 6: Run focused tests to verify GREEN**

Run:

```bash
swift test --filter 'onboardingRendersEveryStepInBothLanguages|onboardingSemanticEvidenceRejectsMalformedTIFF|onboardingUsesUnifiedStepRailWithoutHeaderDividers'
```

Expected: all selected tests PASS and all eight fixtures satisfy the compact
bounds.

- [ ] **Step 7: Commit the typography correction**

```bash
git add Sources/EMKEMenuBarApp/OnboardingTypographyMetrics.swift \
  Sources/EMKEMenuBarApp/OnboardingView.swift \
  Tests/EMKEAudioEngineTests/TranslationDashboardRenderTests.swift
git commit -m "style: compact onboarding typography"
```

### Task 3: Capture, inspect, and verify the complete correction

**Files:**
- Verify: `Sources/EMKEMenuBarApp/OnboardingView.swift`
- Verify: `Sources/EMKEMenuBarApp/OnboardingTypographyMetrics.swift`
- Verify: `Tests/EMKEAudioEngineTests/TranslationDashboardRenderTests.swift`
- Artifacts only: `/tmp/emke-onboarding-brand-type-qa/`

**Interfaces:**
- Consumes: the two committed corrections from Tasks 1 and 2.
- Produces: eight accepted bilingual onboarding screenshots plus full test and
  production-build evidence.

- [ ] **Step 1: Generate the exact eight-fixture capture set**

Run:

```bash
EMKE_CAPTURE_UI=1 \
EMKE_CAPTURE_OUTPUT_DIR=/tmp/emke-onboarding-brand-type-qa \
swift test --filter captureArtifactDirectoryMatchesExactExpectedSet
```

Expected: PASS and exactly these TIFFs:

```text
onboarding-overview-zh.tiff
onboarding-microphone-zh.tiff
onboarding-audio-zh.tiff
onboarding-meeting-zh.tiff
onboarding-overview-en.tiff
onboarding-microphone-en.tiff
onboarding-audio-en.tiff
onboarding-meeting-en.tiff
```

- [ ] **Step 2: Convert captures for inspection**

Run:

```bash
mkdir -p /tmp/emke-onboarding-brand-type-qa/png
for image_path in /tmp/emke-onboarding-brand-type-qa/onboarding-*.tiff; do
  image_name="${image_path:t:r}"
  sips -s format png "$image_path" \
    --out "/tmp/emke-onboarding-brand-type-qa/png/${image_name}.png" >/dev/null
done
```

- [ ] **Step 3: Inspect all eight screenshots**

Open each PNG and verify:

- the upper-left tile shows the white four-direction EMKE mark;
- no waveform remains in the brand lockup;
- the right-side title/body/cards/selectors/diagnostics/footer are visibly
  smaller and calmer than the reported screenshot;
- Chinese and English copy does not clip or overlap;
- all actions remain visible at `680 × 560`;
- the left rail, titlebar-free window, and four-step progress remain unchanged.

If any screenshot fails, return to Task 2 and adjust only the relevant visual
metric; do not change window size or product behavior.

- [ ] **Step 4: Run the complete Swift test suite**

Run:

```bash
swift test
```

Expected: all non-environment-gated tests PASS. The driver-state and live-audio
tests may skip unless their explicit environment variables are enabled.

- [ ] **Step 5: Build the production product**

Run:

```bash
swift build -c release --product EMKEMenuBarApp
```

Expected: `Build of product 'EMKEMenuBarApp' complete!`

- [ ] **Step 6: Verify the final branch boundary**

Run:

```bash
git diff --check
git status --short --branch
git log --oneline origin/main..HEAD
```

Expected: no whitespace errors, no uncommitted implementation files, and the
new logo/type commits appear after the existing onboarding commits. Do not
merge or push without a new explicit integration choice.

