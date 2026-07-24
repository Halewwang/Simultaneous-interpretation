# Task 5 Recovery Report

## Pre-fix root-cause note

- Three isolated process runs of
  `onboardingRendersEveryStepInBothLanguages` passed and produced bit-identical
  hashes for all eight 1120 x 1240 TIFFs. The fixed 560 x 620 real
  `OnboardingView`, seeded state, disabled step refresh, one `NSHostingView`,
  explicit layout, and one bitmap are stable in isolation.
- The reported broken microphone TIFF and the preserved copy have the same
  SHA-256 as the stable runs. Direct TIFF previewing gave inconsistent visual
  results for those identical bytes, while lossless `sips` PNG conversion
  showed the complete brand row, localized content, footer actions, progress,
  and primary action.
- The real artifact race is the fixed global
  `/tmp/emke-interface-floating-qa` directory. While a read-only forensic
  process inspected it, another test process removed and recreated the whole
  directory; a TIFF path disappeared during hashing and reappeared later.
  `CaptureArtifacts` has only an in-process `NSLock`, so concurrent test
  processes can delete or overwrite one another's accepted files.
- One full render-suite process also starts
  `onboardingRendersEveryStepInBothLanguages` and
  `captureArtifactDirectoryMatchesExactExpectedSet` concurrently; the latter
  calls the former again. That creates two accepted renders and two writes per
  onboarding fixture even before considering other processes.
- Reloaded TIFF pixel evidence places the visible logo in the low-y hosted
  bitmap region (855 dark pixels versus 3 in the vertically mirrored region).
  The current identity mapping matches this capture path, but its generic
  bottom-origin fixture does not prove the hosted/exported coordinate
  contract.

The primary cause is therefore artifact attribution and overwrite, not the
renderer or encoder. The fix will give each process an isolated output
directory, make one test the only accepted-artifact writer, retain one real
view snapshot per accepted fixture, and add a hosted/exported top-origin probe
plus exact per-language brand-header crop equivalence. No test-only anchors
will be added to `OnboardingView`.

## Recovery implemented

- The default artifact directory is now process-isolated, while
  `EMKE_CAPTURE_OUTPUT_DIR` remains an explicit deterministic override.
  Accepted filenames are single-assignment: a duplicate write fails without
  replacing the first bytes.
- `captureArtifactDirectoryMatchesExactExpectedSet` is the sole accepted-file
  writer. Other render tests return validated artifacts and never publish
  accepted paths.
- Each onboarding fixture uses the real `OnboardingView` at 560 x 620 points,
  one `NSHostingView`, explicit layout/display readiness, one bitmap capture,
  one TIFF serialization, and validation of the exact reloaded TIFF bytes.
  There are no waits, retries, candidate selection, or test-only view anchors.
- The planned `ImageRenderer` path was replaced with `NSHostingView` because
  three isolated pre-fix runs already proved it bit-stable, and it directly
  exercises the AppKit host/export coordinate contract. A red-top/blue-bottom
  hosted TIFF probe proves the canonical top-origin mapping.
- Exact per-language brand-header crop equality is checked across all four
  steps independently of step title/body evidence. The serialized fixtures
  also prove the logo, product name, localized header, localized step copy,
  footer actions, progress, and primary action.
- Audio-input diagnostic cleanup now invalidates synchronously before
  navigation, dismissal, visibility, or completion mutation, while adapter
  start/stop operations are serialized so an old cleanup cannot stop a newer
  start.

## Verification

- Three consecutive separate acceptance processes passed at
  `/tmp/emke-task5-final-runs.DQ9fkX/run-{1,2,3}`. All eight onboarding TIFFs
  were 1120 x 1240 and bit-identical across runs.
- All eight final onboarding fixtures were visually inspected from the exact
  accepted bytes; every fixture contains the complete header, step content,
  footer controls, progress, and primary action without clipping.
- Focused matrix passed: `Onboarding` (20), static-copy localization (1),
  `MenuBarTranslationModelTests` (55), accessibility (37), and render tests
  (29).
- Fresh full `swift test` passed 361 tests; the environment-gated driver-state
  case was skipped as designed.
