# Task 2 report — production authenticated WebSocket factory

## Scope and commits

- Baseline: `48b544ea2d491beabca03f017630c2b4f5bf9568`
- RED tests: `d9a557e27f4ae1815a38a6e9dca50b7a2ffa9ae0`
  (`test: define authenticated translation session factory`)
- GREEN implementation: `fde4eb7427e450e3bca0e09bf434e7e8eeb194aa`
  (`feat: compose authenticated translation sessions`)
- Final loopback coverage: `50ddfb771224a50a9e166a07842d000b5c67ea53`
  (`test: verify frozen translation loopback events`)

## Delivered Task 2 boundary

- Added the internal `IClientWebSocket.SetRequestHeader` seam and delegated it
  to `ClientWebSocket.Options.SetRequestHeader`.
- `TranslationSocket` rejects empty or CR/LF credentials before adapter use,
  scopes the unavoidable managed bearer-header string to its owned socket, and
  never includes credential material in endpoint, errors, logs, or snapshots.
- Added `TranslationSessionFactory`: validates its public endpoint first,
  acquires one fresh `translationApiKey` lease for each call, configures one
  fresh socket, disposes the lease in `finally`, and disposes partial sockets
  on construction/configuration failure. Connection stays lazy until the
  runtime calls `ConnectAsync`.
- Factory tests prove independent leases/sessions/sockets, header-before-
  connect, endpoint construction, invalid-endpoint ordering, missing/invalid
  secret handling without disclosure, and partial-socket cleanup.
- The loopback test observes both nested
  `session.audio.output.language` handshakes, client
  `session.input_audio_buffer.append`, and the three frozen delta events
  reaching the correct audio/caption consumers. It uses the existing
  `session.closed` terminal control only to stop deterministically.
  `session.completed` and `session.audio.delta` remain rejected with
  `translationEvent.unknownType`; the production codec was not expanded.

## Evidence

- The macOS host has no `dotnet`, so all managed proof used evidence-only
  draft PR #6 on `codex/evidence-windows-runtime-task2-red`. The product
  branch was not pushed.
- Honest RED: Windows Translation Runtime run `30730704380`, job
  `91450515497`, on `d9a557e`; native pre-gate passed and the managed build
  failed only on the absent `TranslationSessionFactory` and authenticated
  socket configuration API required by the test contract.
- Final managed evidence: Windows Translation Runtime run `30731937048`, job
  `91453732647`, on `50ddfb7`; Release build completed with 0 warnings and 0
  errors. Managed suites passed: Core 50/50, Contract 18/18, Application
  94/94, Realtime 109/109, Routing 50/50, Windows App 144/144, Integration
  94/95 (one existing non-Windows skip).
- That Runtime workflow then failed only its existing native-owned contract
  fixture path drift: it searched for
  `Windows/out/native/x64-release/integration/Release/EMKE.NativeAudio.Tests.exe`,
  while the native build emits the executable beneath the target directory.
  This occurred after all managed tests and is outside Task 2.
- Independent final GREEN evidence: Windows Internal MSIX run `30731937084`,
  `build-test` job `91453732818`, succeeded on `50ddfb7`. It passed portable
  contract validation and locked restore, native Release audio, the 0-warning
  managed Release build, and every managed suite above. PR-only
  `sign-package-bundle` and `install-hosted-preview` were skipped; no signing
  or installation claim is made.
- The final `git diff --check` passed before the documentation commit. Earlier
  evidence reruns caught a nullable test assertion and an incorrect ordering
  of the language-gate fixture; both were corrected in tests before this final
  evidence. They are not presented as GREEN proof.

## Remaining boundary

- Production app composition remains pending for Task 4; this factory is not
  wired into it here.
- Native endpoint catalog/C ABI (Task 3), runtime/UI diagnostics completion
  (Task 5), driver, Setup, macOS, signing, installation, and physical-device
  acceptance are out of scope and unchanged.
