# Task 4 report — replace pending production composition

Baseline: `d8377c13f65c56d75e59fdfd6fd84cd718d538da`
Initial Task 4 head: `57933dd7ef8827aca96f18b8756ed13b0b5049d7`
Independent-review corrective code head: `7aa7d6c7cd09e5909c02ff8de83837f1008d2242`

## Result

`ProductionAppAdapterFactory` now constructs the concrete Windows production graph:

- `WindowsAudioDeviceCatalog`
- `TranslationSessionFactory` backed by the composition-owned `CredentialManagerSecretStore`
- `OfflineLanguageClassifier`
- `NativeAudioEngine`
- `WindowsDriverManager`
- `WindowsSettingsStore`

The same `TranslationSessionFactory` is passed to the runtime and to the settings diagnostic probe. The composition test asserts the identities of the settings stores and the factory's secret store. The three pending production adapters are deleted; the source guard also rejects their names and the old unavailable-composition message.

`OfflineLanguageClassifier` has an internal stream seam and now rejects a missing or malformed embedded profile with `InvalidDataException`; it cannot silently pick a fallback language. `AppCompositionRoot` now stops the runtime before diagnostics, with the ordering tested at the composition boundary.

`TranslationConnectionProbe` preserves a `TranslationSessionException` stable code, maps `IOException` to `translationProbe.networkFailed`, leaves other failures at the existing generic code, and continues to propagate cancellation.

## TDD evidence

The relevant RED commits were executed on the temporary evidence branch before the production changes:

- shutdown ordering: Runtime run `30734445155`, job `91460546413` (the test observed diagnostics before runtime);
- concrete composition/source/profile/secret/probe seams: Runtime runs `30734601744`, `30734851934`, `30734996890`, and `30735393925`;
- stable diagnostic failures: Runtime run `30735718282`, job `91464058958`, built successfully and had exactly the two new failure-mapping assertions fail (`TranslationSessionException` code and `IOException` network code), while the remaining managed suites passed.

GREEN evidence uses `57933dd` on PR #12. Windows Internal MSIX run `30735831991`, build-test job `91464356445`, succeeded: locked restore, native Release build/test, and managed Release product build/test all passed. Signing/package installation jobs were intentionally skipped for the evidence PR.

The first Windows Translation Runtime attempt `30735831974` compiled with zero warnings/errors and passed Application 98, Core 50, Contract 18, Routing 52, Windows App 148, and Integration 100 with 1 existing skip. It failed only the unrelated pre-existing `BoundedChannelAppliesBackpressureAndDeliversTailBeforeCloseCompletes` timing assertion in Realtime (108/109). Its failed-job rerun rebuilt with zero warnings/errors and passed every managed suite: Application 98, Core 50, Contract 18, Realtime 109, Routing 52, Windows App 148, and Integration 100 with 1 skip. The workflow then failed only the existing owned-native PCM adapter path expectation: `Windows/out/native/x64-release/integration/Release/EMKE.NativeAudio.Tests.exe` was absent. This is outside Task 4's managed composition change.

### Independent-review follow-up

Review baseline `134db2311d9b5f7acae07216bd664879439f5c09` exposed one fail-closed profile gap and one source-guard scope gap. RED commit `0d3fc4a` on evidence PR #13 produced Runtime run `30736312306`, job `91465622319`: the Release build had zero warnings/errors; all managed suites except the new Routing assertion passed; and the new schema-invalid profile test observed a bare `JsonException` rather than the required `InvalidDataException`.

GREEN commit `7aa7d6c` wraps `JsonDocument.Parse` and model deserialization in the same `JsonException` handler while leaving existing `InvalidDataException` failures unwrapped. It preserves the inner exception and returns only the stable profile-invalid message. The production source guard now locates `Windows/src`, recursively scans controlled `.cs` files, and excludes `bin`, `obj`, `artifacts`, and `out` directories. It does not scan test or generated output trees.

Runtime run `30736432006`, job `91465932671`, built with zero warnings/errors and passed Application 98, Core 50, Contract 18, Realtime 109, Routing 53, Windows App 148, and Integration 100 with 1 skip. It then failed only the existing owned-native PCM fixture path. Internal MSIX run `30736432009`, build-test job `91465932858`, succeeded (locked restore, native Release build/test, and managed Release build/test); signing/install preview were skipped for the evidence PR.

## Local guards

- The whole-source guard scanned 61 controlled `Windows/src/**/*.cs` files, excluding `bin`, `obj`, `artifacts`, and `out`; it found no deleted pending adapter name or `composition is not available`.
- The codec still owns `session.update`, audio append, audio delta, input transcript delta, and output transcript delta; Task 4 added no protocol event, `session.completed`, or unsupported translation voice/speed/instructions control.
- `git diff --check` passed.

## Evidence branches and limits

The original RED/GREEN (#11/#12) and independent-review RED/GREEN (#13/#14) branches are evidence-only; none is the product branch and all are closed unmerged after verification. No Task 4 product commit was pushed.

Hosted CI proves compilation, managed tests, and the Internal MSIX build/test job. It does not prove a signed/installed package, a physical driver or endpoints, actual Credential Manager credentials/provider session, WPF interaction, meeting-app interoperability, or human listening acceptance.
