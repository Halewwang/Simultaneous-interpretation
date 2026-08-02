# Task 3 report — native-backed Windows audio device catalog

## Scope and commits

- Baseline: `90e75791546437695635469d8da06ce9ac84bd23`
- RED tests: `d60debd6b497912a6314887aee8b8dfbeeefa256`
  (`test: define native-backed device catalog contract`)
- GREEN implementation: `cc76b3bf102bf58d4e3bf5fd1cddb68c8addfeb5`
  (`feat: expose native Windows audio device catalog`)
- Windows SDK correction: `dc3be33b7b8680b95fc3bbbba93b60b6093e52ad`,
  `2ab864389a1161558d6b19b9dc8febec9511e0e1`
- Managed fixture correction: `8f38a3e7b86e0d13b885cce6738a83773faf525f`

## Delivered boundary

- Native discovery retains the existing `DeviceSource`/`DeviceCatalog`, stable
  role property values, physical-default resolution, and old discovery
  snapshot ABI. It now obtains bounded friendly names from the same property
  store and rejects duplicate/empty endpoint IDs while refreshing the catalog.
- ABI v1 adds a fixed UTF-16 endpoint descriptor, explicit active/default/
  virtual flags, descriptor size query, and a count/fill enumeration call.
  It uses an owned MTA worker and never exposes COM or native allocations to
  managed callers. Count calls do not write descriptors; insufficient storage
  returns the fresh count before any descriptor copy.
- `WindowsAudioDeviceCatalog` maps the production P/Invoke seam into immutable
  Core descriptors. It bounds count to 128, retries one count growth, checks
  ABI and item size, terminators, flags, directions, blank text, duplicate IDs,
  exact roles, and role/data-flow consistency. Native and validation failures
  are `NativeAudioException`; they are never converted into successful empty
  snapshots.
- Task 4 composition is unchanged: `PendingAudioDeviceCatalog` remains in
  production composition for that task.

## Evidence status

- Honest RED: evidence-only draft PR #7, Windows Translation Runtime
  `30732342873`, job `91454836026`, CTest 17/18 pass and Device fails the new
  duplicate-ID assertion at lines 1004/1005/1010. This is a native behavior
  failure, not a fake/compiler/analyzer failure. PR #7 was closed unmerged.
- First GREEN attempt: Runtime `30732644824` / job `91455596775` stopped at
  an absent Windows SDK friendly-name property declaration; the correction is
  recorded above and this is not GREEN proof.
- Second attempt: Runtime `30732753733` and Internal MSIX `30732753777`
  reached native build/test successfully. Managed compilation then found only
  test-fixture initializer/fixed-buffer/analyzer defects, corrected in
  `8f38a3e`; those failures are not presented as behavior evidence.
- Current final evidence run is the evidence-only draft PR #8 head
  `8f38a3e`; its completion is recorded in the ledger after its workflows
  finish. Product branch has not been pushed.

## Remaining boundary

Hosted CI proves ABI/fake/contract and managed mapping behavior only. No
installed EMKE driver, physical endpoint, meeting application, provider
session, or human listening acceptance was exercised.
