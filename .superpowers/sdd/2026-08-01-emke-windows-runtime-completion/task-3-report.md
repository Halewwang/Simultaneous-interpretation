# Task 3 report — native-backed Windows audio device catalog

## Scope and commits

- Baseline: `90e75791546437695635469d8da06ce9ac84bd23`
- RED tests: `d60debd6b497912a6314887aee8b8dfbeeefa256`
  (`test: define native-backed device catalog contract`)
- GREEN implementation: `cc76b3bf102bf58d4e3bf5fd1cddb68c8addfeb5`
  (`feat: expose native Windows audio device catalog`)
- Windows SDK correction: `dc3be33b7b8680b95fc3bbbba93b60b6093e52ad`,
  `2ab864389a1161558d6b19b9dc8febec9511e0e1`
- Managed fixture corrections: `8f38a3e7b86e0d13b885cce6738a83773faf525f`,
  `c548cd690ecf39b8a14c4437c0a85605e89afeb1`, and
  `cb3479cdec53e3001aec93ed0abd956f1a5fc8ae`

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
  `8f38a3e` through `cb3479c`; those failures are not presented as behavior
  evidence.
- GREEN evidence: evidence-only PR #8 head `cb3479c` (closed unmerged), Windows Internal
  MSIX run `30733201383`, job `91457028235`, succeeded. Its native release
  gate passed and its managed suite reports Core 50/50, Contract 18/18,
  Application 94/94, Realtime 109/109, Routing 50/50, Windows App 144/144,
  and Integration 99 passed + 1 intentional non-Windows skip (100 total).
  The Windows Translation Runtime run `30733201382`, job `91457021568`, also
  passed native CTest 18/18 and that same managed suite result. Its final
  owned-PCM adapter subgate nevertheless failed because the workflow looks
  for a missing `Windows/out/native/x64-release/integration/Release/EMKE.NativeAudio.Tests.exe`;
  this is recorded as a fixture-path gate failure, not catalog behavior.
- Windows Audio Foundation run `30733201401`, hosted-toolchain job
  `91457021715`, passed native CTest 18/18 plus managed seam 18/18 and native
  fake 7/7. It then failed only its existing output-directory evidence guard
  (`Managed native-audio integration output directory is missing`), after
  those tests had completed.
  Product branch has not been pushed.

## Remaining boundary

Hosted CI proves ABI/fake/contract and managed mapping behavior only. No
installed EMKE driver, physical endpoint, meeting application, provider
session, or human listening acceptance was exercised.
