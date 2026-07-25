# EMKE Unified Onboarding UI and Permission Lifecycle Design

**Date:** 2026-07-25  
**Status:** User-approved visual direction, pending written-spec review  
**Scope:** First-launch onboarding window only

## 1. Context

The current four-step onboarding works, but its presentation is visually split
into a macOS title bar, a separate EMKE header, a content region, and a divided
footer. The result feels closer to an unfinished form than a guided desktop
setup experience.

The user selected the visual direction with a persistent left-hand step rail and
asked for the title bar and horizontal separator to be removed so the window
reads as one continuous surface.

The user also reported that clicking the microphone-permission action causes the
onboarding page to disappear.

## 2. Goals

- Present the existing four onboarding steps as one cohesive, polished window.
- Keep the selected left-hand step navigation visible throughout the flow.
- Remove visible system title-bar chrome and all full-width header/footer
  separators.
- Preserve Chinese and English localization without clipping.
- Keep the onboarding window visible and frontmost after the macOS microphone
  permission prompt resolves.
- Preserve all existing permission, audio diagnostic, provider, routing,
  persistence, and reopen semantics.

## 3. Non-goals

- Changing translation providers, API-key storage, model selection, base URL,
  audio routing, virtual-driver behavior, or translation-session behavior.
- Requesting microphone permission before the user activates the explained
  permission action.
- Automatically starting translation after onboarding.
- Redesigning the menu-bar dashboard, settings page, or floating translation
  panel.
- Changing onboarding completion or suppression persistence.

## 4. Confirmed Visual Direction

### 4.1 Window geometry and chrome

- Increase the content area from `560 × 620` to `680 × 560` points.
- Retain a titled `NSWindow` internally so it can become key, but use
  `.fullSizeContentView`, transparent title-bar appearance, hidden title text,
  and hidden standard traffic-light buttons.
- Extend SwiftUI content through the title-bar region so no separate title-bar
  band remains visible.
- Keep `.closable` behavior for Command-W and delegate handling even though the
  standard close button is hidden.
- Enable moving the window by dragging unused background regions.
- Preserve a standard window shadow and rounded outer shape.

This avoids the focus limitations of a truly borderless `NSWindow` while
producing the approved titlebar-free appearance.

### 4.2 Unified layout

The window is split into two continuous regions without a horizontal chrome
divider:

- **Left step rail:** approximately 156 points wide, using a quiet cool-gray
  surface.
- **Main content:** flexible width, using the existing panel background.

The left rail contains:

1. compact EMKE brand lockup;
2. four persistent numbered steps;
3. completed, current, and future visual states;
4. `Powered by Eager`;
5. the existing `Do Not Show Again` action.

The main region contains:

1. a small step eyebrow and `n / 4` counter;
2. step title and supporting explanation;
3. one focused group of step-specific controls;
4. an integrated bottom action row with Back, Skip for Now, and
   Continue/Finish as applicable.

There is no duplicate `Start Using EMKE` header above the step content.

### 4.3 Visual hierarchy

- Continue using the system font and the existing EMKE neutral palette.
- Use the existing activity blue only for the current step, primary action, and
  active informational icons.
- Use soft filled cards with subtle one-pixel borders instead of large empty
  vertical gaps.
- Use green, warning, and failure tones only for explicit status, always paired
  with text and a symbol.
- Keep content density appropriate for a setup assistant rather than a
  dashboard.

## 5. Step Designs

### 5.1 Step 1 — Work flow

- Title: the existing localized two-way translation overview.
- Show two compact route cards:
  - real microphone → provider → `EMKE Virtual Microphone`;
  - `EMKE Virtual Speaker` → provider → real headphones/speakers.
- Keep the existing privacy statement visible in a quiet trust callout.
- No permission request or audio operation runs on this step.

### 5.2 Step 2 — Microphone permission

- Explain the purpose before the action.
- Present permission state, privacy boundaries, and action in one focused card.
- When the state is undetermined, show `Get Microphone Permission`.
- While the request is in flight, disable repeated activation and show a waiting
  label.
- On authorization, keep the user on Step 2, restore the onboarding window, and
  show the authorized state.
- On denial, keep the user on Step 2, restore the onboarding window, and offer
  the existing System Settings action.
- Restricted state remains explanatory and does not loop the system prompt.

### 5.3 Step 3 — Audio devices

- Keep virtual-device availability, real microphone/output selectors, and local
  microphone/output diagnostics.
- Group device selection and testing into separate compact sections.
- Preserve the existing live selection lock and diagnostic cleanup rules.

### 5.4 Step 4 — Meeting setup

- Keep provider summary/editing, connection test, and explicit meeting endpoint
  labels.
- Preserve `EMKE Virtual Speaker` for the meeting speaker and
  `EMKE Virtual Microphone` for the meeting microphone.
- Finishing persists onboarding version `1` and closes the window without
  starting translation.

## 6. Permission-window Root Cause and Lifecycle

### 6.1 Evidence

- `OnboardingView` directly awaits
  `MenuBarModel.requestMicrophonePermissionForOnboarding()`.
- The model correctly coalesces concurrent permission requests and publishes the
  returned state.
- `OnboardingWindowController.show()` presents the window only when onboarding
  first opens or is manually reopened.
- No code restores window ordering after the external macOS permission prompt
  finishes.
- The packaged app sets `LSUIElement = true`, so it is a menu-bar application
  rather than a normal Dock application.

The reported disappearance is therefore at the application/window presentation
boundary, not in the permission-state reducer: the TCC prompt temporarily owns
focus, and the menu-bar app never reorders its onboarding window when the prompt
returns.

### 6.2 Lifecycle repair

Add a controller-level operation that restores the existing onboarding window
without restarting the flow:

```swift
func restoreAfterExternalPrompt() {
    guard isVisible else { return }
    window?.bringToFront()
}
```

The presentation interface separates initial presentation from restoration:

```swift
protocol OnboardingWindowPresenting: AnyObject {
    func show()
    func bringToFront()
    func hide()
}
```

After the permission request resolves, the view asks the controller to restore
the window. The restoration must:

- keep the current step unchanged;
- keep progress persistence unchanged;
- do nothing if the user has already dismissed the guide;
- order the window above other windows even if the accessory app is not active;
- make it key once activation permits.

The AppKit presenter will use `orderFrontRegardless()` for deterministic
ordering, followed by the existing application activation/key-window behavior.

## 7. Component Boundaries

### `OnboardingWindowController`

- Owns onboarding visibility and flow position.
- Decides whether a post-prompt restoration is still valid.
- Never invokes the permission provider.

### `OnboardingAppWindowPresenter`

- Owns AppKit window construction and visual chrome.
- Implements initial show, post-prompt front ordering, and hide.
- Does not mutate onboarding flow or persistence.

### `OnboardingView`

- Owns the approved SwiftUI layout.
- Invokes the existing model permission request.
- Shows in-flight button state.
- Requests controller restoration after the awaited external prompt returns.

### `MenuBarModel`

- Continues to own permission request coalescing and published permission state.
- Receives no provider/audio/configuration behavior changes for this work.

## 8. Failure and Race Handling

- Multiple fast permission-button activations still share the model's single
  provider request; the UI additionally disables the button while awaiting it.
- If onboarding becomes hidden before the permission operation resolves,
  restoration is ignored.
- A denied permission never triggers a second system prompt; the existing System
  Settings action is shown.
- Restoring the window never calls `show()` on the controller, so the current
  step cannot reset to Step 1.
- Closing with Command-W continues to map to `Skip for Now`.
- Opening System Settings remains an intentional external navigation; when the
  app becomes active again, Step 2 refreshes permission state through the
  existing path.

## 9. Testing and Verification

### 9.1 Failing regression tests first

- A visible controller restores the attached presenter after an external prompt.
- Restoration leaves the current onboarding step unchanged.
- A hidden controller ignores late post-prompt restoration.
- The permission action awaits the request and then invokes controller
  restoration.
- Window construction preserves a key-capable titled window while hiding visible
  title-bar chrome.

### 9.2 Existing behavior tests

- Four bounded steps and restart behavior.
- Skip, suppress, finish, and settings reopen persistence.
- Permission request coalescing and one-time state publication.
- Device selection locks and diagnostic cleanup.
- Explicit meeting endpoint labels and progress accessibility.

### 9.3 Visual verification

Capture Chinese and English renders for all four steps at `680 × 560`, checking:

- no visible title bar or full-width horizontal chrome divider;
- stable sidebar and footer alignment across steps;
- Chinese and English text fits without clipping;
- permission states and primary actions remain visible;
- device and meeting controls do not overflow;
- keyboard focus and accessibility labels remain meaningful.

### 9.4 Runtime boundary

Automated tests can prove controller ordering requests and render geometry, but
they cannot prove the live macOS TCC transition. Final acceptance requires a
fresh or reset microphone-permission state:

1. launch the packaged app with onboarding incomplete;
2. go to Step 2;
3. click the explained permission action;
4. choose Allow and separately validate Deny;
5. confirm onboarding returns to Step 2, remains visible, and updates status.

## 10. Acceptance Criteria

1. The selected left-step-rail layout is implemented.
2. No separate system title-bar band, title, traffic-light buttons, or header
   divider is visible.
3. All four steps remain functional in Chinese and English.
4. The microphone action is still user-initiated from Step 2.
5. After Allow or Deny, a still-active onboarding flow returns to the same step
   and remains frontmost.
6. A guide dismissed while permission is pending is not reopened.
7. Existing audio, provider, routing, persistence, update, and translation
   behavior remains unchanged.
8. Focused tests, the full Swift test suite, release build, and visual captures
   pass.
