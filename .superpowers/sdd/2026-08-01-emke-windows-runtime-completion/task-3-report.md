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

## Independent review remediation

- The review found two P1 holes: an OK count of zero returned an empty success
  before role validation, and the managed fake did not export the new
  descriptor-size or count/fill functions. The P2 asked for direct native C
  export verification rather than only the managed `INativeAudioApi` fake.
- Review RED is `fdfa2ae`. Its Runtime/MSIX evidence (`30733697890` /
  `91458364071` and `30733697862` / `91458371479`) compiled the catalog tests
  and failed only `EmptyNativeCatalogFailsClosedAsDeviceMissing`: the adapter
  returned success instead of a typed `DeviceMissing`. Audio Foundation
  `30733697868` / `91458364054` also ran the seven prior native-fake tests and
  then failed with `EntryPointNotFoundException` for
  `emke_audio_sizeof_endpoint_descriptor_v1`. The preceding `7c8705f` run is
  excluded as RED behavior proof because an MSTest collection-analyzer error
  prevented execution.
- GREEN is `02f973f`, with final test-only count-growth coverage at
  `b90fcdb`. The adapter now reaches `Map` for zero descriptors, so the exact
  virtual-role check raises `NativeAudioException(DeviceMissing)`. The fake
  exports a deterministic two-physical-default plus four-exact-role catalog
  through the production P/Invoke path. Existing test hooks gained only a
  test-build fixture setter; the production MMDevice/MTA path is unchanged.
  Direct C ABI coverage verifies count, NULL/capacity/required-count rejection,
  insufficient-capacity and count-growth guard bytes without partial writes,
  fresh required counts, and valid descriptor fill.
- Final evidence-only PR #10 head `b90fcdb` is closed unmerged. Runtime
  `30734111484` / `91459533731` passed native CTest 18/18 and Integration 100
  passed + 1 intentional non-Windows skip (101 total), then only failed the
  existing owned-PCM fixture-path subgate. Internal MSIX
  `30734111501` / `91459533825` succeeded with the same native 18/18 and
  Integration 100 + 1 skip result. Audio Foundation `30734111497` /
  `91459533755` passed native CTest 18/18, managed seam 18/18, and native fake
  8/8 before its existing output-directory evidence guard. Review RED PR #9
  is also closed unmerged. Product branch remains unpushed.

## Remaining boundary

Hosted CI proves ABI/fake/contract and managed mapping behavior only. No
installed EMKE driver, physical endpoint, meeting application, provider
session, or human listening acceptance was exercised.
