# EMKE Onboarding, Automatic Updates, Stop Responsiveness, and UI Polish

**Status:** Approved in conversation on 2026-07-24
**Implementation baseline:** `0dab621` (`fix: preserve channel layout invariants`)
**Target repository:** `https://github.com/Halewwang/Simultaneous-interpretation.git`
**Approved update direction:** Sparkle 2.9.2 with GitHub Releases and a
signed Appcast

## 1. Context

EMKE Translation is a macOS menu-bar application with a compact dashboard,
settings, a dual realtime-translation coordinator, local audio diagnostics,
and an internal PKG that installs both the app and a virtual Core Audio driver.

The current app has four related gaps:

1. first-time users receive no guided explanation before permissions and setup;
2. installed builds have no trusted update channel;
3. stopping can remain blocked while a realtime server never acknowledges a
   graceful close; and
4. several small visual and copy details are incomplete.

This design adds a versioned first-launch guide, Sparkle-based automatic update
downloads, a bounded graceful-stop path, and the requested UI polish while
preserving the provider, Keychain, model, translation, and audio-routing
contracts.

## 2. Goals

- Explain EMKE's two translation paths and meeting-app routing on first launch.
- Explain microphone access before macOS presents its system permission prompt.
- Let users skip the guide and reopen it from Settings.
- Reuse the real microphone, output, driver, and connection diagnostics.
- Check for and download trusted updates automatically.
- Publish update artifacts from the approved public GitHub repository.
- Keep the graceful tail-audio opportunity while placing a strict upper bound
  on server-dependent shutdown.
- Add the approved product logo to the dashboard title.
- Add a recognizable icon to the local audio diagnostics heading.
- Replace the dashboard footer with `Powered by Eager` in both interface
  languages.

## 3. Non-goals

- Changing the realtime provider, endpoint, model, API-key storage, translation
  language, or audio-routing behavior.
- Requesting microphone permission before the user reaches and activates the
  permission step.
- Making the onboarding mandatory.
- Automatically changing conference-app audio devices.
- Silently installing PKG updates that require administrator authorization.
- Shipping Developer ID signing, Apple notarization, or App Store distribution
  in this scope.
- Publishing signing private keys, API keys, or GitHub credentials.
- Claiming that a single bootstrap build proves a live upgrade between two
  separately installed public releases.

## 4. Considered Update Approaches

Three update approaches were considered:

1. **Sparkle 2.9.2 with GitHub Releases and an Appcast.** Sparkle owns version
   comparison, automatic checks and downloads, signature validation, update
   presentation, and installation handoff.
2. **A custom GitHub Releases client.** EMKE would need to own version parsing,
   signature verification, download recovery, installation, failure recovery,
   and future migration.
3. **Release automation with notification-only UI.** This is simpler but does
   not meet the automatic-download requirement.

The approved approach is **Sparkle 2.9.2**. Package updates may download in the
background, but macOS still requires administrator authorization to install a
PKG that updates the system-level virtual audio driver.

## 5. First-launch Onboarding

### 5.1 Window and Lifecycle

Use a dedicated, normal SwiftUI-hosted AppKit window rather than embedding the
guide in the menu-bar popover. The window:

- opens after application launch when the persisted onboarding version is
  lower than the current onboarding version;
- uses the same `MenuBarModel` as the menu-bar dashboard and Settings;
- appears in front without starting translation;
- can be closed or skipped without quitting EMKE;
- never blocks access to the menu-bar UI; and
- can be reopened from Settings.

`OnboardingWindowController` owns one window and receives the shared model.
It contains window lifecycle only. Step content, button availability, and
progress are represented by independently testable onboarding state.

### 5.2 Persisted State

Store a non-secret integer `completedOnboardingVersion` in `UserDefaults`.
The first flow version is `1`.

- A missing, invalid, or lower value shows onboarding.
- Completing the final step stores `1`.
- Choosing `Skip for now` closes the window without storing completion, so the
  guide appears again on the next application launch.
- Choosing `Do not show again` stores `1` and closes the window.
- Reopening from Settings does not erase completion and starts at step 1.

This versioned value allows a future materially changed setup flow to use a
higher version without changing existing public settings or Keychain data.

### 5.3 Four Steps

#### Step 1 — What EMKE Does

Explain:

- meeting audio enters `EMKE Virtual Speaker`, is translated, and plays through
  the selected real headphones or speaker;
- the selected real microphone is translated and sent to the meeting through
  `EMKE Virtual Microphone`; and
- audio is sent directly to the configured AI provider for realtime
  translation and is not saved by EMKE.

No permission request or device mutation occurs on this step.

#### Step 2 — Microphone Permission

Show the current state: not determined, authorized, denied, or restricted.
The primary action is explicit:

- when not determined, `Allow microphone access` calls the existing system
  permission provider;
- when authorized, the step shows success and allows the user to continue;
- when denied or restricted, the step explains how to open macOS System
  Settings while still allowing the user to continue or skip.

The custom UI explains why access is needed but does not imitate or replace the
macOS permission dialog.

#### Step 3 — Local Audio and Driver Check

Reuse the existing device inventory and local diagnostics:

- show whether the EMKE virtual driver is present;
- show the selected real microphone and real output device;
- allow the existing microphone-level diagnostic;
- allow the existing output test; and
- link to Settings when a device selection needs correction.

Tests remain user-triggered. Entering the step does not play sound or begin
recording automatically.

#### Step 4 — Provider and Meeting Setup

Reuse the existing API configuration and connection test, without displaying
or logging a stored Keychain secret. Explain that the meeting application must
use:

- speaker: `EMKE Virtual Speaker`;
- microphone: `EMKE Virtual Microphone`;
- EMKE input: the real microphone; and
- EMKE output: the real headphones or speaker.

The final action records onboarding version `1` and closes the window.
Onboarding completion does not start translation automatically.

### 5.4 Localization and Accessibility

All onboarding copy is added to the existing typed `AppCopy` layer in
Simplified Chinese and English. The content follows the active interface
language at runtime.

- Every step has a localized title and concise explanatory body.
- Progress is exposed as both visible `n / 4` text and an accessibility value.
- Permission and diagnostic results use text and symbols, never color alone.
- Buttons retain normal keyboard focus and accessible names.
- The window supports reduced motion and does not depend on animated
  illustration.

## 6. Bounded Graceful Stop

### 6.1 Root Cause

`TranslationSession.close()` currently sends the close event and suspends until
the realtime server emits `session.closed` or the socket fails. If the server
keeps the WebSocket open without either event, both coordinator close tasks
remain suspended and `MenuBarModel.isStopping` stays true indefinitely.

### 6.2 Session Close Contract

Preserve graceful closing but bound it:

1. send the existing close client event;
2. continue reading server events for up to 1 second so late translated audio
   can still be delivered;
3. if `session.closed` or a terminal socket error arrives, finish normally;
4. when the 1-second deadline expires, cancel the socket and finish the
   connection locally; and
5. resume every close/event waiter exactly once.

The timeout is an injected session dependency with a one-second production
default so tests can control the deadline deterministically without sleeping.
Timeout cancellation is considered a successful local close, not a provider
error shown to the user.

### 6.3 Coordinator and UI Result

The coordinator continues closing inbound and outbound sessions concurrently.
After both have either closed gracefully or reached the deadline, it stops the
audio engine, cancels receive tasks, clears session references and buffers,
publishes `.stopped`, and returns.

The dashboard and floating capsule may show `Stopping` during this bounded
period. They must return to idle immediately after local cleanup. Existing
protection against a stale state snapshot reviving an already stopped
presentation remains in place.

## 7. Automatic Update Architecture

### 7.1 Application Integration

Add Sparkle 2.9.2 as an exact Swift Package dependency and use
`SPUStandardUpdaterController`.

The application owns one updater controller for its lifetime. It:

- starts automatic checks after launch;
- enables automatic update download;
- uses Sparkle's standard update UI and installation handoff;
- exposes `canCheckForUpdates` and `checkForUpdates` to a Settings button; and
- remains independent of translation and audio state.

An update check or download must never stop a translation session. Installation
may occur only through Sparkle's normal user-visible flow.

### 7.2 App Configuration

The staged app `Info.plist` includes:

- `SUFeedURL` pointing to the raw HTTPS Appcast on the `gh-pages` branch of
  `Halewwang/Simultaneous-interpretation`;
- `SUPublicEDKey` containing only the public EdDSA key;
- `SUEnableAutomaticChecks = true`; and
- `SUAutomaticallyUpdate = true`.

`CFBundleShortVersionString` and `CFBundleVersion` become build inputs shared by
the app bundle, PKG, artifact name, release tag, and Appcast entry. Bundle
versions must increase monotonically.

### 7.3 Packaging

The custom staging pipeline must preserve Sparkle framework symlinks,
executables, XPC services, and permissions in
`EMKE Translation.app/Contents/Frameworks`.

Packaging verification checks:

- the expected Sparkle framework and helpers exist;
- nested executable signatures and the enclosing app pass strict verification;
- required Info.plist update keys match the release inputs;
- no private signing material enters the staged root or PKG; and
- the existing app, virtual driver, ownership, cleanup, and payload invariants
  still pass.

The current internal PKG remains ad-hoc signed at the Apple code-signing layer.
Sparkle's EdDSA signature protects the update artifact. A public production
release still requires Developer ID signing and notarization outside this
scope.

### 7.4 Signing Material

Generate one Sparkle EdDSA key pair:

- store the private key in the developer's login Keychain;
- embed only the public key in the app;
- store the CI copy of the private key as the GitHub Actions secret
  `SPARKLE_PRIVATE_KEY`; and
- never print the private key in logs or commit it.

Release tooling supplies the CI key to Sparkle signing tools through standard
input. Losing this key requires a separately planned key-rotation or bootstrap
release.

### 7.5 GitHub Release Flow

The approved public repository is the source remote and update host.

After verified implementation:

1. configure it as local `origin`;
2. push the verified `main` history;
3. create version tags in the form `vMAJOR.MINOR.PATCH`;
4. on a version tag, GitHub Actions builds and verifies the PKG;
5. sign the update artifact with Sparkle EdDSA;
6. create the matching GitHub Release and upload the PKG;
7. generate an Appcast item whose version, download URL, byte length, and
   EdDSA signature match the uploaded asset; and
8. publish the Appcast to the repository's `gh-pages` branch.

The workflow refuses to publish when the tag version and packaging version do
not match, verification fails, the signing secret is unavailable, or the
release asset already exists with different contents.

The first Sparkle-enabled build is a bootstrap version and must still be
installed manually. Automatic updating can begin only from that installed
version to a later version.

## 8. Requested UI Changes

### 8.1 Dashboard Product Logo

Place the existing approved `MenuBarLogo.image` immediately before the
`EMKE Translation` title in the dashboard's top-left product label. Use the
existing asset without redrawing or reinterpreting it.

The logo is decorative in this combined label and is hidden from accessibility;
the existing product-name text remains the accessible label.

### 8.2 Local Audio Diagnostics Icon

Add the SF Symbol `waveform.badge.magnifyingglass` to the existing localized
local-audio-diagnostics section heading. The icon is decorative; the heading
text remains authoritative for accessibility.

### 8.3 Footer Copy

Replace the current localized direct-provider footer copy with exactly:

`Powered by Eager`

The English brand line is identical in both interface languages. Its privacy
semantics move to onboarding Step 1 rather than remaining implied by the
dashboard footer.

### 8.4 Settings Update and Onboarding Actions

Settings adds:

- `Check for Updates…`, using the localized equivalent and disabled only when
  Sparkle reports that a check cannot start; and
- `Open Getting Started`, reopening onboarding at step 1.

These are secondary actions and do not alter active translation state.

## 9. Error Handling

- Onboarding persistence failure keeps the guide reopenable and does not block
  the app.
- A denied microphone permission shows remediation and never loops system
  prompts.
- Diagnostic failures use the existing localized diagnostic messages.
- A forced close after the deadline is treated as completed local shutdown.
- Failure to load an Appcast or download an update is presented by Sparkle and
  does not affect translation.
- Signature mismatch rejects the update.
- A missing release-signing secret blocks publication rather than producing an
  unsigned update.
- GitHub publication failure leaves the existing Appcast unchanged.

## 10. Test Strategy

### 10.1 Unit Tests

Add deterministic tests for:

- onboarding version migration, skip, do-not-show-again, completion, reopen,
  and step navigation;
- microphone state mapping and one-time permission request behavior;
- onboarding presentation in Chinese and English;
- reuse of device/diagnostic state without automatic test activation;
- the exact `Powered by Eager` footer copy;
- updater action availability and Settings action wiring;
- graceful close before the deadline;
- forced socket cancellation at the deadline;
- late tail audio delivered before graceful close;
- all close/event waiters resumed exactly once; and
- dashboard/settings accessibility for the new logo, icons, and actions.

Every behavior-changing production edit follows a red-green-refactor cycle.

### 10.2 Build and Packaging Tests

Run:

- the complete Swift test suite;
- a release product build;
- strict C compilation checks;
- packaging shell tests;
- the complete internal PKG build and verifier;
- static workflow validation; and
- checks that staged update metadata, framework contents, versions, and
  signatures are internally consistent.

### 10.3 Visual and Interaction Verification

Verify both Chinese and English at the existing 420 × 620 menu size, plus the
new onboarding window:

- all four steps at default size;
- denied and authorized microphone states;
- long English device and error copy;
- dashboard logo alignment;
- diagnostics title icon alignment;
- Settings update/onboarding actions; and
- stopping-to-idle transition.

Automated or screenshot verification does not prove a live macOS permission
dialog, real microphone capture, administrator-authorized PKG installation, or
an update between two public installed versions. Those boundaries must be
reported separately.

## 11. Acceptance Criteria

The work is ready to push when:

1. a fresh profile shows the four-step onboarding window;
2. microphone permission is requested only from its explained user action;
3. skip and completion persist exactly as specified;
4. Settings reopens onboarding;
5. a non-responsive session close cannot leave the UI stopping indefinitely;
6. graceful tail audio still passes before the close deadline;
7. the requested logo, diagnostics icon, and exact footer copy appear in both
   interface languages;
8. Sparkle automatically checks and downloads from the signed Appcast;
9. Settings can manually start an update check;
10. the release pipeline cannot publish an unsigned or version-mismatched
    artifact;
11. all automated test, build, and package verification gates pass;
12. the final verified commit is pushed to `main` in
    `Halewwang/Simultaneous-interpretation`; and
13. any unperformed live permission, administrator-install, or two-version
    update acceptance is identified explicitly rather than inferred.
