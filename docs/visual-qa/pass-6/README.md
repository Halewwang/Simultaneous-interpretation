# Pass 6 visual acceptance

Pass 6 replaces the invalid Pass 5 evidence. It proves that both sides use the
same 840 × 1240 px content region and that the implementation is embedded into
the acceptance surface at exact 1:1 pixels.

## Local source

The approved source remains local to this workstation:

```text
/Users/hale/.codex/generated_images/019f7317-d192-73e2-93f7-ab12bdc4c5e3/exec-e64a9f1b-cec0-4e0a-aa39-ad89127eddbb.png
```

The renderer requires the exact 1033 × 1523 px approved file and rejects a
different source size. It crops the measured MenuBarExtra-style surface at
`x=24, y=22, width=984, height=1471` in top-left image coordinates, then uses
one uniform aspect-fill scale and a centered crop to create the exact
840 × 1240 px source content region. It never stretches either axis
independently. The tracked normalized source and comparisons contain no API
key, customer data, or production-service response.

## Reproduce

Render the current deterministic running fixture:

```sh
EMKE_CAPTURE_UI=1 swift test --filter captureRunningDashboardForVisualReview
sips -s format png /tmp/emke-running-dashboard.tiff --out /tmp/emke-running-dashboard-pass-6.png
```

Generate and verify the tracked evidence:

```sh
swift docs/visual-qa/pass-6/render-pass-6.swift \
  '/Users/hale/.codex/generated_images/019f7317-d192-73e2-93f7-ab12bdc4c5e3/exec-e64a9f1b-cec0-4e0a-aa39-ad89127eddbb.png' \
  /tmp/emke-running-dashboard-pass-6.png \
  docs/visual-qa/pass-6/artifacts
swift docs/visual-qa/pass-6/verify-pass-6.swift \
  docs/visual-qa/pass-6/artifacts
```

`verify-pass-6.swift` checks the manifest, every image dimension, both combined
panel registrations, and the implementation's interior pixels. The final
implementation embedding has mean sampled RGB difference `0.0` from the raw
capture.

## Canonical evidence

- `artifacts/source-normalized.png`: 840 × 1240 px
- `artifacts/implementation-raw.png`: 840 × 1240 px
- `artifacts/comparison-content.png`: 1680 × 1240 px
- `artifacts/source-panel.png`: 888 × 1288 px
- `artifacts/implementation-surface.png`: 888 × 1288 px
- `artifacts/comparison-surface.png`: 1776 × 1288 px
- `artifacts/geometry.json`: exact normalization and embedding geometry

Canonical SHA-256 values:

```text
77b27ffe53c9d4d17bc3ba448aa97ded81986d87b2282edb10841a836b8ab021  comparison-content.png
d699fca2d7d1a6324a5f8e3194a06392682bb86cd4464919ee46158fd08ab2f7  comparison-surface.png
5c92aee42f7f6e64284a9a04dcc94c9a35e36903afcaf36464fd73cf926196a2  implementation-raw.png
eb8640c9790bf86f082999bfe88156c98aed1a2f3684a237cc3dc94431903183  implementation-surface.png
```
