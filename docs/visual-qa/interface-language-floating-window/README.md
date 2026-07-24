# Interface language and floating window visual QA

This evidence set is deterministic and contains no API key, Keychain value,
real device inventory, or provider response. Dashboard and floating-window
images use pure presentations. Settings images use an in-memory
`AppSettingsStoring`, an inert `SecretStore`, an inert device provider, and
`deferInitialDeviceReload: true`.

## Status

| Area | Status | Evidence |
| --- | --- | --- |
| Automated regression and build gates | Passed | 302 tests passed; two live, opt-in hardware tests skipped; strict concurrency, Release app build, and driver verification passed. |
| Deterministic static visual inspection | Passed | All eight TIFFs were inspected at original pixel size after temporary PNG conversion. |
| Real macOS runtime acceptance | Not verified | Computer Use reported that the Mac was locked. The already-running `/Applications` build was not touched, and opening the current app menu was also avoided because it automatically reads the shared Keychain service. |

## Reproduce the captures

Run from the repository root:

```sh
EMKE_CAPTURE_UI=1 swift test
```

The command creates `/tmp/emke-interface-floating-qa` only when
`EMKE_CAPTURE_UI=1` is present. On the first capture write in each test
process, one shared locked capture session removes and recreates only that
dedicated directory; later writes use the same session and atomic replacement.
A normal `swift test` does not write captures. The capture test also compares
the final directory entries with the exact expected filename set below:

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
display. The host view and window use deterministic Aqua/light appearance.
Tests require the real scroll geometry to reach its bottom and verify the quit
control with narrow border and glyph/text regions relative to their local
background. Uniform dark and light backgrounds are rejected. This makes the
real full-width quit control reproducible without a composite image or
production-code change.

For 1:1 inspection, convert a TIFF to a temporary PNG without resizing:

```sh
sips -s format png \
  /tmp/emke-interface-floating-qa/settings-en.tiff \
  --out /tmp/settings-en.png
```

## Automated results

| Command | Status | Evidence |
| --- | --- | --- |
| `swift test` | Passed | 302 tests passed; `liveVirtualEndpointsStartAndStop` and `installedDriverMatchesExpectedState` were skipped because their opt-in environment variables were not set. A clean ordinary run left the dedicated capture directory absent. |
| `swift test -Xswiftc -strict-concurrency=complete -Xswiftc -warnings-as-errors` | Passed | 302 tests passed with the same two documented opt-in skips and no warning-as-error failure. |
| `swift build -c release --product EMKEMenuBarApp -Xswiftc -warnings-as-errors` | Passed | Release product build completed. |
| `make -C Driver clean all verify` | Passed | Fresh repository-equivalent Driver gate: the Makefile cleaned and rebuilt the bundle before verifying arm64, bundle id, factory symbol, and factory smoke cases, ending in `PASS`. |
| `git diff --check 514ac2d...HEAD` | Passed | No whitespace errors were reported. |
| `EMKE_CAPTURE_UI=1 swift test` | Passed | 302 tests passed with the same two opt-in skips; the in-test exact-set assertion accepted only the eight required TIFFs. |

## Static visual inspection

| Check | Status | Evidence |
| --- | --- | --- |
| Chinese dashboard keeps the pre-84 production geometry | Passed | `dashboard-ready-zh.tiff` preserves the header, waveform/status, language hierarchy, two compact channel rows, primary action, and privacy footer. Its separator rows are exactly `[436, 597, 782, 1143]`, matching a direct `ca2c2b2` pre-84 production-renderer capture. Every Chinese dashboard fixture remains outside the English equal-slot policy. |
| English dashboard channel rows are aligned | Passed | Ordinary ready/running rows use one 374 pt four-column compact profile: 48 pt icon, 128 pt description, 96 pt status/waveform, 78 pt trailing action, and three 8 pt gaps. The description and status/waveform blocks share the same visual center; no spacer pushes the action away from the measured profile. |
| Long English channel copy keeps a safe fallback | Passed | Bypass, reconnecting, same-language, and blocking-failure copy remains expanded. If either English row needs that path, the dashboard forces both rows into equal expanded slots so the pair stays aligned. |
| English stress copy has no overflow | Passed | Seven deterministic English ready/running/bypass/reconnecting/failure fixtures render real 840 x 1240 px bitmaps. Separator-derived row bounds contain the scanned visible ink, trailing actions remain right-aligned, and independent AppKit measurements for title, direction, status, status symbol, and action fit within two lines and the actual slot height. |
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
| English alignment and copy follow-ups are tightly scoped | Passed | The first layout follow-up established expanded fallback geometry; the copy follow-up changed only English `inboundDirection` to `Other → {target}`. The centering follow-up adds a language-resolved compact profile for ordinary English ready/running rows and a shared long-copy fallback policy. It does not change visible copy, font sizes, icons, panel dimensions, Chinese legacy arrangement, provider configuration, Keychain, audio routing, or driver code. |
| Unrelated `internal-pkg-installer` worktree remains untouched | Passed | Its pre-existing dirty files were observed read-only and were not modified by this task. |

## Pass 8 tracked evidence

| Evidence | Path |
| --- | --- |
| User-reported defect | `docs/visual-qa/interface-language-floating-window/pass-8/source-defect.jpg` |
| User copy follow-up | `docs/visual-qa/interface-language-floating-window/pass-8/source-copy-followup.jpg` |
| User centering follow-up | `docs/visual-qa/interface-language-floating-window/pass-8/source-centering-followup.jpg` |
| English pre-centering result | `docs/visual-qa/interface-language-floating-window/pass-8/before-centering-followup.png` |
| English before / after | `docs/visual-qa/interface-language-floating-window/pass-8/before-en.png` / `docs/visual-qa/interface-language-floating-window/pass-8/after-en.png` |
| English 1:1 comparison | `docs/visual-qa/interface-language-floating-window/pass-8/comparison-en.png` |
| English centering follow-up 1:1 comparison | `docs/visual-qa/interface-language-floating-window/pass-8/comparison-centering-followup.png` |
| Chinese pre-84 production baseline / current | `docs/visual-qa/interface-language-floating-window/pass-8/baseline-zh-pre84.png` / `docs/visual-qa/interface-language-floating-window/pass-8/after-zh.png` |
| Chinese pre-84/current 1:1 comparison | `docs/visual-qa/interface-language-floating-window/pass-8/comparison-zh-pre84-current.png` |

The tracked Pass 7 `docs/visual-qa/pass-7/artifacts/implementation-ready.png`
has separator rows `[467, 628, 813, 1160]`; it is a historical acceptance
artifact with layout drift, not the production regression baseline for this
follow-up. The baseline used here is the direct `ca2c2b2` production renderer
capture with rows `[436, 597, 782, 1143]`.
