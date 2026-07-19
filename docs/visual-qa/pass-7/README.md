# Pass 7 language controls and menu-bar logo

Pass 7 addresses the installed-build mismatch reported on 2026-07-19. The
approved running dashboard remains unchanged at exact 840 x 1240 px. The new
ready-state capture proves that the editable mother-language and meeting-output
controls use the same 22 pt plain-text hierarchy as the locked running state,
instead of the compact macOS `Picker(.menu)` chrome.

The menu-bar image is a real 36 x 36 px RGBA asset derived from the already
approved `Packaging/Assets/EMKE-AppIcon-Approved.png`. Runtime code presents it
at 18 x 18 pt with `isTemplate = true`, so macOS controls its light/dark tint.

## Reproduce

```sh
EMKE_CAPTURE_UI=1 swift test --filter captureRunningDashboardForVisualReview
EMKE_CAPTURE_UI=1 swift test --filter captureReadyDashboardForVisualReview
sips -s format png /tmp/emke-running-dashboard.tiff \
  --out /tmp/emke-running-dashboard.png
sips -s format png /tmp/emke-ready-dashboard.tiff \
  --out /tmp/emke-ready-dashboard.png
swift docs/visual-qa/pass-6/render-pass-6.swift \
  '/path/to/the/1033x1523-approved-source.png' \
  /tmp/emke-running-dashboard.png \
  docs/visual-qa/pass-7/artifacts
swift docs/visual-qa/pass-7/render-language-control-comparison.swift \
  docs/visual-qa/pass-7/artifacts/source-normalized.png \
  /tmp/emke-ready-dashboard.png \
  docs/visual-qa/pass-7/artifacts/comparison-language-controls.png
```

## Evidence

- `artifacts/comparison-surface.png`: approved running source and current
  running implementation, same state and 1:1 implementation pixels.
- `artifacts/implementation-ready.png`: current editable ready state at
  840 x 1240 px.
- `artifacts/comparison-language-controls.png`: 1:1 focused control comparison;
  approved locked values are left and current editable values are right.
- `artifacts/installed-dashboard.jpeg`: the installed `/Applications` build,
  captured from its real 420 x 620 pt menu-bar panel after Keychain approval.
- `artifacts/installed-language-menu.jpeg`: the installed mother-language
  popover with Chinese, English, and German options and the selected checkmark.
- `artifacts/installed-status-bar-logo.png`: the installed template Logo on the
  real macOS menu bar while its panel is selected.
- `../../../Sources/EMKEMenuBarApp/Resources/EMKE-MenuBarIcon.png`: packaged
  transparent template asset.

The focused comparison isolates only the language slot. Both sides use the same
viewport, selected values, typography, spacing, and chevrons; the difference in
interaction lock state is intentionally invisible until the user clicks.
