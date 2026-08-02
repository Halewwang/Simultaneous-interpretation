# Task 5 report — Windows runtime safety completion

Task 5 completed the managed-runtime safety evidence specified by
`task-5-brief.md`. The implementation remains a Windows-native WPF/WASAPI
product path; this task changed no macOS protocol, driver ABI, package signing,
or release-promotion behavior.

## Result

- Failure diagnostics are centralized through `RuntimeError`: only structured
  safe fields are accepted and error codes must be stable identifiers.
- Runtime logging records only the stable error code. Test failure aggregation
  keeps seed, injection name, and exception type, never exception text.
- Reconnect exhaustion has a deterministic `translationRuntime.reconnectExhausted`
  network error and bounded retry schedule.
- Five production-path safety contracts execute 100 times each (500 total),
  with controlled clocks and explicit iteration counters.
- The review follow-up classifies native QueueFull as stable backpressure,
  classifies collected provider 401/403/404 HTTP statuses without parsing
  exception text, and verifies the Runtime's safe stopped-route cleanup.
- Every repeated shutdown now checks mock session/socket, poll worker, event
  and translation queues, and tracked PCM lease ownership. A blocked remote
  close remains explicitly reported as `localCloseTimeout` quarantine.
- The row-by-row production seam, error mapping, safe routes, diagnostics, and
  ownership evidence are in
  `docs/quality/windows-runtime-completion-evidence.md`.

## Evidence workflow

All Task 5 validation branches were evidence-only and closed without merge:

| Pull request | Purpose | State |
| --- | --- | --- |
| #15 | Runtime parameter privacy boundary | Closed unmerged |
| #16 | Reconnect exhaustion code | Closed unmerged |
| #17 | Initial 100-iteration matrix | Closed unmerged |
| #18 | Stabilized matrix completion | Closed unmerged |
| #19 | Runtime logs, hostile codes, failure aggregation | Closed unmerged |
| #20 | Cross-layer mapping and final report | Closed unmerged |
| #21 | Review follow-up: QueueFull, provider HTTP status, PCM probes, ownership | Closed unmerged after Runtime `30742018521` / job `91480993354` and MSIX `30742018519` / job `91480993337` evidence |

The product branch `codex/windows-internal-msix` remains local only. The sole
pre-existing dirty file, `progress.md`, is intentionally neither altered nor
committed by this task.

## Final validation record

The final evidence PR must record the actual run/job IDs for:

1. Shared contract validator (with Swift tools 6.2 requirement versus hosted
   runner Swift 6.1 called out as an external environment failure if unchanged).
2. Native CTest.
3. Full managed Release tests.
4. Internal MSIX build/test.
5. Exact five-event and alias-rejection source guard.
6. Privacy/source guards and `git diff --check`.

Final accepted Windows evidence is Runtime `30740206479` / job `91476128799`
and MSIX `30740206477` / job `91476128864`. Runtime passed every managed suite;
its isolated owned-native PCM fixture failure is the known fixture executable
path mismatch, not a Task 5 regression. MSIX passed. The prior Runtime
`30740047560` / job `91475720709` was a fixture-only RED caused by an injected
authentication error using `retry` instead of the production factory's
`updateApiKey`; commit `3463c9c` corrected that test fixture.

Shared Contract `30740206482` passed its file validator and Windows contract
job, but remains overall red because the macOS runner has Swift 6.1 while the
repository requires Swift tools 6.2. Windows Audio Foundation `30740206460`
also remains red because an unrelated workflow guard expects exactly seven
`NativeAudioNativeFake` tests while the native suite reports eight. Neither is
changed by Task 5.

Review follow-up evidence extends the accepted managed boundary: Runtime
`30742018521` / job `91480993354` passed Core 62, Contract 18, Application
101, Realtime 113, Routing 53, Windows App 148, and Integration 113 with one
platform skip. Its only failure is the pre-existing owned-native PCM fixture
executable path mismatch. Internal MSIX `30742018519` / job `91480993337`
succeeded. The Audio Foundation run `30742018517` confirms native CTest 18/18,
the new 19-test managed seam, and native-fake 8/8; it still ends on the
independent native-fake 8-vs-7 guard.

## Acceptance boundary

This is hosted automated evidence only. It does not assert driver signature
trust, real hardware/endpoints, installation, provider account/session,
meeting interoperability, or human listening acceptance. Those remain
separate target-machine gates.
