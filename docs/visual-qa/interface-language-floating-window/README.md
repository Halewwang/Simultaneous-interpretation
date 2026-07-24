# Interface language and floating window visual QA

This evidence set is deterministic and contains no API key, Keychain value,
real device inventory, or provider response. Dashboard and floating-window
images use pure presentations. Settings images use an in-memory
`AppSettingsStoring`, an inert `SecretStore`, an inert device provider, and
`deferInitialDeviceReload: true`.

## Status

| Area | Status | Evidence |
| --- | --- | --- |
| Automated regression and build gates | Passed | 284 tests passed; two live, opt-in hardware tests skipped; strict concurrency, Release app build, and driver verification passed. |
| Deterministic static visual inspection | Passed | All eight TIFFs were inspected at original pixel size after temporary PNG conversion. |
| Real macOS runtime acceptance | Not verified | Computer Use reported that the Mac was locked. The already-running `/Applications` build was not touched, and opening the current app menu was also avoided because it automatically reads the shared Keychain service. |

## Reproduce the captures

Run from the repository root:

```sh
EMKE_CAPTURE_UI=1 swift test
```

The command creates `/tmp/emke-interface-floating-qa` only when
`EMKE_CAPTURE_UI=1` is present. A normal `swift test` does not write captures.
The directory contains exactly these files:

| File | Pixel size |
| --- | --- |
| `/tmp/emke-interface-floating-qa/dashboard-ready-zh.tiff` | 840 x 1240 |
| `/tmp/emke-interface-floating-qa/dashboard-ready-en.tiff` | 840 x 1240 |
| `/tmp/emke-interface-floating-qa/settings-zh.tiff` | 840 x 1240 |
| `/tmp/emke-interface-floating-qa/settings-en.tiff` | 840 x 1240 |
| `/tmp/emke-interface-floating-qa/floating-connecting.tiff` | 528 x 104 |
| `/tmp/emke-interface-floating-qa/floating-running.tiff` | 528 x 104 |
| `/tmp/emke-interface-floating-qa/floating-degraded.tiff` | 528 x 104 |
| `/tmp/emke-interface-floating-qa/floating-stopping.tiff` | 528 x 104 |

The settings renderer hosts the real `TranslationSettingsView`, finds its
actual `NSScrollView`, scrolls it to the bottom, and only then caches the
display. This makes the real full-width quit control reproducible without a
composite image or production-code change.

For 1:1 inspection, convert a TIFF to a temporary PNG without resizing:

```sh
sips -s format png \
  /tmp/emke-interface-floating-qa/settings-en.tiff \
  --out /tmp/settings-en.png
```

## Automated results

| Command | Status | Evidence |
| --- | --- | --- |
| `swift test` | Passed | 284 tests passed; `liveVirtualEndpointsStartAndStop` and `installedDriverMatchesExpectedState` were skipped because their opt-in environment variables were not set. |
| `swift test -Xswiftc -strict-concurrency=complete -Xswiftc -warnings-as-errors` | Passed | 284 tests passed with the same two documented opt-in skips and no warning-as-error failure. |
| `swift build -c release --product EMKEMenuBarApp -Xswiftc -warnings-as-errors` | Passed | Release product build completed. |
| `make -C Driver clean all verify` | Passed | Fresh repository-equivalent Driver gate: the Makefile cleaned and rebuilt the bundle before verifying arm64, bundle id, factory symbol, and factory smoke cases, ending in `PASS`. |
| `git diff --check 514ac2d...HEAD` | Passed | No whitespace errors were reported. |
| `EMKE_CAPTURE_UI=1 swift test` | Passed | 284 tests passed with the same two opt-in skips and exactly eight required TIFFs were written. |

## Static visual inspection

| Check | Status | Evidence |
| --- | --- | --- |
| Chinese dashboard keeps the approved Pass 6 hierarchy | Passed | `dashboard-ready-zh.tiff` preserves the header, waveform/status, language hierarchy, two channel rows, primary action, and privacy footer. |
| English dashboard has no clipped copy | Passed | `dashboard-ready-en.tiff` shows complete language names, directions, channel actions, primary action, and footer at 1:1 pixels. |
| Settings language selector contract is localized | Passed | Automated tests verify the real interface-language menu, all three stable preferences, and non-empty Chinese/English copy; the two dashboard captures verify both resolved interface languages. |
| Chinese settings quit control is clear | Passed | Bottom-scrolled `settings-zh.tiff` shows the real full-width bordered/background control, power icon, and complete `退出 EMKE` label. |
| English settings quit control is clear | Passed | Bottom-scrolled `settings-en.tiff` shows the real full-width bordered/background control, power icon, and complete `Quit EMKE` label without clipping. |
| Capsule matches approved direction A | Passed | All four floating captures are 264 x 52 pt at 2x, with capsule geometry and the 32 pt stop target. |
| Connecting, running, degraded, and stopping are distinguishable | Passed | Visible copy is respectively `Connecting`, `Translating`, `Muted`, and `Stopping...`; neutral, healthy, degraded, and neutral tones are visually distinct where specified, not color-only. |
| Real waveform is visible | Passed | All four pure presentations render the non-flat fixture level (`max(0.42, 0.68)`) as a visible 99 pt waveform. |
| Stop target is clear | Passed | The red stop glyph and circular target are visible; disabled connecting/stopping states are also visibly dimmer than running/degraded. |

## Manual runtime checklist

Static render evidence is not used to mark any runtime item as Passed.

| Item | Status | Evidence or reason |
| --- | --- | --- |
| Panel opens at bottom center after Start: | Not verified | The Mac was locked and the current worktree app menu was not opened. |
| Panel can be dragged without stealing keyboard focus: | Not verified | No current-worktree runtime panel was available for interaction. |
| Panel remains visible across Spaces: | Not verified | Would require unlocked, non-disruptive Space switching with a live panel. |
| Panel remains visible over a full-screen meeting app: | Not verified | No meeting app or full-screen state was changed. |
| Real waveform continues after menu popover closes: | Not verified | No live session was started. |
| Stop button stops the session and hides the panel: | Not verified | No live session was started. |
| Follow System / 中文 / English persist after relaunch: | Not verified | The language menu was not opened and no runtime preference was changed. |
| Installed internal package includes and runs the feature: | Not verified | No package was installed. The already-running `/Applications` binary predates this worktree and was left untouched. |
| Settings switch immediately between 中文 and English: | Not verified | Computer Use could not proceed while the Mac was locked. |
| Settings quit button is clear and exits the app: | Not verified | Static clarity passed, but the runtime button was not clicked because the app UI could not be safely opened. |

## Scope audit

| Check | Status | Evidence |
| --- | --- | --- |
| Endpoint, realtime transport, Keychain/security, routing, conversion, and driver source remain unchanged | Passed | `git diff --name-only 514ac2d...HEAD` contains no files under `EMKECore`, `EMKESecurity`, `EMKERealtime`, `EMKERouting`, `EMKEAudioEngine`, `EMKEAudioHAL`, `EMKEAudioBridge`, or `Driver`. |
| Public settings change is limited to interface-language persistence | Passed | `AppSettingsStore.swift` only adds the `emke.interfaceLanguage` UserDefaults value and does not change API-key storage or Keychain code. |
| Task 10 edits are limited to the approved files | Passed | Only the two render-test files and this README are changed for this task. |
| Unrelated `internal-pkg-installer` worktree remains untouched | Passed | Its pre-existing dirty files were observed read-only and were not modified by this task. |
