# EMKE Interface Language and Translation Floating Window Design

**Status:** Approved in conversation on 2026-07-24
**Implementation baseline:** `514ac2d` (`fix: preserve realtime translation audio bursts`)
**Target branch:** `codex/interface-language-floating-window`

## 1. Context

EMKE Translation is a macOS `MenuBarExtra` application. Its current compact
dashboard and settings UI are Chinese-only. The settings-page quit action uses
a plain text button, so it does not read visually as an action. Once the menu
popover closes, users also lose the visible confirmation that the translation
session is still working.

This design adds:

1. a runtime interface-language preference with `Follow System`, `中文`, and
   `English`;
2. a clearly styled quit button in Settings; and
3. a Typeless-inspired, single-row floating status capsule while translation is
   starting, running, degraded, or stopping.

The design preserves the existing provider, Keychain, model, language-routing,
audio-routing, and translation-session contracts.

## 2. Goals

- Let users switch the complete visible application interface between Simplified
  Chinese and English without restarting.
- Default new and upgraded installations to `Follow System`.
- Make the Settings quit action unmistakably button-like.
- Keep translation health visible after the menu-bar popover closes.
- Let users stop an active translation directly from the floating capsule.
- Continue using real audio levels rather than decorative waveform animation.
- Keep one source of truth for translation state and actions.

## 3. Non-goals

- Adding more interface languages.
- Changing the input/output translation language set.
- Changing provider, API-key, endpoint, model, WebSocket, or audio-routing
  behavior.
- Adding inbound/outbound bypass controls to the floating capsule.
- Showing transcripts or translated text in the floating capsule.
- Persisting the floating-window position across application launches.
- Redesigning the existing 420 × 620 pt menu-bar dashboard.
- Public distribution, notarization, or installer changes beyond ensuring that
  the existing package continues to include the executable normally.

## 4. Considered Floating-window Directions

Three directions were reviewed:

- **A — Single-row status capsule:** lowest obstruction, combined waveform,
  overall status, timer, and stop.
- **B — Two-row status capsule:** adds separate inbound and outbound activity.
- **C — Two-channel mini card:** most diagnostic detail, but largest visual
  footprint.

The approved direction is **A — Single-row status capsule**. Detailed
inbound/outbound state remains available in the menu-bar dashboard.

## 5. Interface-language Behavior

### 5.1 Preference

Add `AppInterfaceLanguage` with three persisted values:

- `system`
- `zh-Hans`
- `en`

The Settings screen exposes these values as:

| Stored value | Chinese UI | English UI |
| --- | --- | --- |
| `system` | 跟随系统 | Follow System |
| `zh-Hans` | 中文 | 中文 |
| `en` | English | English |

The language control remains enabled while translation is active because it
does not change session, provider, model, or audio configuration.

### 5.2 System-language Resolution

When the preference is `system`, resolve the interface to Simplified Chinese
when the first preferred system language starts with `zh`; otherwise resolve
to English. Observe the macOS locale-change notification so an active app can
refresh when the system preference changes. A normal application restart may
still be required by macOS for a system-wide language change to take full
effect.

### 5.3 Runtime Update

Changing the preference updates all visible UI immediately, including:

- dashboard headings, labels, directions, statuses, actions, privacy copy, and
  accessibility labels;
- channel status and bypass copy;
- settings headings, fields, buttons, device prompts, connection-test results,
  audio-diagnostic copy, errors, and accessibility labels;
- supported-language display names in both language menus;
- floating-capsule states, timer label, stop label, and accessibility content.

The selected translation languages are unchanged. For example, switching the
interface to English changes `中文` to `Chinese` as a display label but does not
change the stored `SupportedLanguage.chinese` value.

### 5.4 Copy Architecture

Add a centralized, app-target localization layer:

- `AppInterfaceLanguage` stores the user preference.
- `ResolvedInterfaceLanguage` represents the effective `zhHans` or `english`
  language.
- `AppCopy` exposes typed copy for the effective language.
- Presentation factories accept `AppCopy` instead of embedding Chinese string
  literals.

The first version uses typed Swift copy instead of a SwiftPM resource bundle.
This avoids changing the existing internal packaging pipeline or depending on
an additional resource bundle being copied into the staged application. Tests
must cover every declared copy key in both languages.

Known application errors are mapped to localized copy. Raw system or provider
diagnostic details may remain untranslated when they have no stable local
equivalent, but surrounding labels and remediation copy must use the selected
interface language.

## 6. Settings Quit Button

Replace the current plain text action with a dedicated
`ExitApplicationButton`:

- full available width;
- 40 pt minimum height;
- power symbol;
- rounded rectangle, visible border, and surface fill;
- hover feedback;
- standard button focus and accessibility behavior;
- localized `退出 EMKE` / `Quit EMKE` label.

The button remains a secondary action and is not red because quitting is
reversible and does not delete data. Activation continues to call the standard
`NSApplication.shared.terminate(nil)` path.

## 7. Floating Translation Capsule

### 7.1 Visual Contract

Use a compact, dark, single-row capsule approximately 264 × 52 pt:

- a colored health dot;
- a two-line status block with the current state and elapsed time when
  available;
- a real, combined audio waveform;
- a circular stop control with a stop-square symbol.

The visual direction follows the approved A mockup: restrained black material,
light text, minimal color, and no title bar or extra settings.

The waveform uses `max(inboundLevel, outboundLevel)`, clamped to `0...1`.
It reflects real published audio levels. It must not run an independent fake
wave animation. With Reduce Motion enabled, state transitions do not animate;
real audio-level changes remain visible.

### 7.2 State Mapping

| Product state | Capsule | Dot | Primary copy | Stop |
| --- | --- | --- | --- | --- |
| Idle or configuration error before start | Hidden | — | — | — |
| `isStarting` and not running | Visible | neutral/mint pulse when motion is allowed | Connecting | Disabled |
| Running, both channels healthy | Visible | green | Translating · `MM:SS` | Enabled |
| Running, inbound failed open | Visible | orange | Playing original incoming audio | Enabled |
| Running, outbound failed closed | Visible | orange | Outbound muted | Enabled |
| Running with an unclassified fatal session error | Visible | red | Translation error | Enabled |
| `isStopping` | Visible | neutral | Stopping | Disabled |
| Stop completed | Hidden | — | — | — |

The capsule remains visible during a degraded but still-running session. If
startup fails before a running session exists, it hides and the detailed error
remains in the menu-bar dashboard.

### 7.3 Window Behavior

`FloatingTranslationPanelController` owns a single AppKit `NSPanel` whose
content is a SwiftUI `FloatingTranslationStatusView`.

The panel:

- is borderless and non-activating;
- does not become the key window or steal keyboard focus;
- uses floating window level;
- sets `canJoinAllSpaces` and `fullScreenAuxiliary`;
- does not appear in the Dock or standard window list;
- stays visible when another application is active;
- initially appears centered above the bottom edge of the screen containing the
  pointer when the session starts, falling back to the main screen;
- is draggable by its background;
- keeps its last position for the current app process;
- has no close or minimize control;
- automatically orders out after stop completes.

The stop button remains clickable without activating the application. It calls
the same asynchronous `MenuBarModel.stop()` action used by the dashboard. It is
disabled while connecting or stopping.

### 7.4 Ownership and Lifecycle

The application creates one `MenuBarModel` and injects it into both the
`MenuBarExtra` content and `FloatingTranslationPanelController`. The panel
controller subscribes to model state and updates visibility and presentation.
It does not create another coordinator, timer, settings store, or audio engine.

`FloatingTranslationPresentation` is a pure, equatable projection of:

- `isStarting`;
- `isStopping`;
- `TranslationCoordinatorState`;
- the effective interface language;
- `translationStartedAt`;
- current time;
- inbound and outbound levels;
- the current user-facing error.

This projection is independently unit-testable and prevents the AppKit window
controller from containing translation business logic.

## 8. Audio-level Visibility Contract

The existing menu-bar UI stops coordinator audio-level publishing when its
popover disappears. That contract must be expanded so the waveform remains
real while the floating capsule is visible.

Track the two presentation surfaces independently:

- menu-bar dashboard visibility;
- floating-capsule visibility.

Enable coordinator audio-level updates while either surface is visible. Disable
them only when both are hidden. Hiding the menu-bar popover must not zero the
levels or disable publishing while the floating capsule is on screen. Stopping
the session or hiding the last surface resets the visible levels to zero.

This preserves the existing 30 Hz bounded UI-level delivery and hidden-window
resource behavior.

## 9. Persistence and Migration

Extend `AppSettings` and `AppSettingsStoring` with the interface-language
preference. Store it in `UserDefaults`; it is not secret data.

Migration behavior:

- missing or unknown stored values resolve to `system`;
- existing API URL, model ID, translation-language, and device selections are
  untouched;
- no Keychain reads or writes occur because the interface language changes;
- no session restart occurs when the interface language changes.

The floating-window position is process-local in this version and does not add
new persistent settings.

## 10. Accessibility

- The language control exposes a localized label and selected value.
- The quit action remains a real `Button` with a localized accessible name.
- The capsule exposes one concise status element containing health, state, and
  elapsed time.
- The waveform is accessibility-hidden because it duplicates audio activity
  conveyed by status.
- The stop control exposes localized `停止翻译` / `Stop translation`.
- Dot color is never the only state indicator.
- The stop target is at least 32 × 32 pt inside the 52 pt capsule.
- Keyboard focus remains with the user's foreground application.

## 11. Error Handling

- A failure to construct or show the panel must not stop or alter translation.
- Panel ordering and localization errors are UI failures and must not mutate
  coordinator state.
- If a stop request returns while the coordinator still reports a running or
  failed session, keep the capsule visible and reflect that state. Hide it only
  after the model confirms that the session is no longer active.
- Unknown stored language values fail safely to `system`.
- Raw provider responses and secrets must not be added to localized copy,
  accessibility values, or logs.

## 12. Testing and Verification

### 12.1 Unit and Source-contract Tests

Add or extend tests for:

- `AppInterfaceLanguage` persistence, missing-value migration, and unknown-value
  fallback;
- system-language resolution for Chinese and non-Chinese preferred languages;
- Chinese and English copy coverage;
- supported-language display names in both interface languages;
- dashboard and channel presentation in both interface languages;
- floating-presentation visibility for idle, connecting, running, degraded,
  error, and stopping states;
- elapsed-time formatting;
- stop enabled/disabled rules;
- menu/floating combined audio-level visibility;
- Settings quit button accessibility and visual source contract;
- no provider, Keychain, model, translation-language, or device mutation after
  interface-language changes.

### 12.2 Build Verification

Run:

- the complete `swift test` suite;
- a Release build of `EMKEMenuBarApp`;
- existing strict C and driver checks if the implementation touches package or
  target definitions.

### 12.3 Visual and Manual Verification

Render original-size states for:

- Chinese and English dashboard;
- Chinese and English settings, including the language control and quit button;
- capsule connecting, running, degraded, and stopping states.

Use unscaled image comparisons and record exact dimensions. Do not claim
real-window behavior from a static render.

Manual runtime checks remain required for:

- panel placement and dragging;
- non-activation and keyboard-focus preservation;
- visibility across Spaces and full-screen meeting applications;
- real waveform updates after the menu-bar popover closes;
- direct stop behavior;
- package-installed application behavior.

Any macOS accessibility or automation limitation is reported as not verified,
not passed.

## 13. Acceptance Criteria

The feature is complete when:

1. `Follow System`, `中文`, and `English` are available and persist.
2. Switching language updates the complete visible UI without restarting or
   changing translation configuration.
3. The Settings quit action is visually and semantically a button.
4. Starting translation shows the single-row capsule.
5. Closing the menu popover does not freeze or fake the capsule waveform.
6. The capsule accurately distinguishes connecting, healthy, degraded, error,
   and stopping states with text as well as color.
7. The capsule stop action stops the existing session and then hides the panel.
8. Existing provider, Keychain, routing, and audio behavior remain unchanged.
9. Automated tests and Release build pass.
10. Manual-only window behaviors are reported separately and truthfully.

## 14. Integration Boundary

Implementation work occurs in an isolated worktree based on `514ac2d`. The
existing `codex/internal-pkg-installer` worktree contains unrelated uncommitted
audio changes and must not be modified, reset, staged, or committed by this
feature. The completed feature branch can later be rebased or merged after
those audio changes have an explicit integration point.
