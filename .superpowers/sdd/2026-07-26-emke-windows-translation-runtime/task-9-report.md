# Task 9 Report: Windows Translation Runtime Integration and Safety

## Status

Implementation complete against review baseline
`90dac629eefc6b27b0346bf0561cb6ce7b2718a6`, with required runtime
prerequisites `78713339c36767016aacaa0f0d367386450420ef` (transport-close
reconnect) and `90dac62` (directional caption isolation).

Task 9 commit: this report is included in the commit with message
`test: verify Windows Translation runtime safety`. The resolved hash is
reported in the Task 9 handoff because a commit cannot contain its own hash.

## TDD evidence

### Caption isolation blocker

RED:

```text
/tmp/emke-runtime-task1-dotnet-sdk-x64/dotnet test \
  Windows/tests/EMKE.Integration.Tests/EMKE.Integration.Tests.csproj \
  --configuration Release -m:1 --disable-build-servers \
  --filter FullyQualifiedName~LoopbackSessionsKeepInboundAndOutboundCaptionsIsolated
```

Observed: 0 passed / 1 failed. Expected `SourceCaption =
meeting-source-caption`; actual was `local-outbound-caption`. The retained
loopback test became GREEN 1/1 after the independently reviewed `90dac62`
runtime fix.

### Deterministic server scenarios

Handshake RED: 2 passed / 4 failed; fragmented, Binary, 401, 403, and unknown
model behavior was not yet implemented. GREEN: 6/6.

Close RED: 1 passed / 2 failed; blocked-close timeout and late delta delivery
failed. GREEN: 3/3 after deterministic delayed/blocked/tail behavior was
implemented.

### Business flows

RED: 0 passed / 6 failed against test adapters lacking connection counts,
captured PCM injection, native queue capture, server error, and disconnect
injection.

GREEN: two-language Running, same-language one-socket bypass, 9,600-byte Text
JSON PCM, direction-specific native queues, caption isolation, and
single-channel failure independence all pass. The disconnect case first
produced a precise runtime RED (`OutboundChannelState = Failed`, no third
connection); independently reviewed prerequisite `7871333` made it GREEN.

### 100 deterministic seeds

RED: the one matrix test failed and reported all 100 seeds, with every one of
the seven injection types present and 100/100 pending harness failures.

GREEN:

```text
/tmp/emke-runtime-task1-dotnet-sdk-x64/dotnet test \
  Windows/tests/EMKE.Integration.Tests/EMKE.Integration.Tests.csproj \
  --configuration Release -m:1 --disable-build-servers \
  --filter FullyQualifiedName~OneHundredDeterministicSeeds
```

Observed: 1/1 test passed. Internally: 100/100 deterministic seeds asserted;
15 outbound disconnect, 15 server error, and 14 each send failure, receive
failure, queue full, translated-audio underrun, and close timeout. Non-zero
virtual microphone outputs: 0.

## Scenario and business-flow matrix

| Requirement | Test evidence |
| --- | --- |
| Normal handshake | connected TranslationSession |
| Two simultaneous sessions | two sockets, Running runtime |
| Same-language path | one socket, OriginalBypass |
| Fragmented Text | two server events, two fragments each |
| Binary injection | protocol rejection |
| 401 / 403 rejection | both upgrade failures |
| Unknown model | upgrade rejection |
| Delayed close | deterministic release gate |
| Blocked close | stable CloseTimeout |
| Late transcript/audio | tail visible before close |
| Disconnect/reconnect | third connection, returns Connected |
| Server error | outbound muted, inbound connected |
| 9,600-byte PCM | one Text-frame JSON event |
| Native translated queues | inbound/outbound bytes isolated |
| Captions | source/translated captions isolated |
| Failure independence | outbound failure does not stop inbound |

## Fresh verification

Commands use:

```text
PATH=/tmp/emke-pwsh-7bLsUP:$PATH
dotnet=/tmp/emke-runtime-task1-dotnet-sdk-x64/dotnet
```

Observed results:

- Integration Release: 39 passed / 8 expected skipped / 0 failed.
- Solution Release build: 0 warnings / 0 errors.
- Solution Release test: 308 passed / 10 expected skipped / 0 failed.
  - Application: 56/56.
  - Core: 50/50.
  - Realtime: 96/96.
  - Routing: 50/50.
  - Contract: 17 passed / 2 expected skipped.
  - Integration: 39 passed / 8 expected skipped.

The initial Integration preflight without the required PowerShell PATH had one
environment failure (`pwsh` missing). Re-running with the brief-mandated PATH
produced the clean result above.

## Files changed

- `Windows/tests/EMKE.Integration.Tests/EMKE.Integration.Tests.csproj`
- `Windows/tests/EMKE.Integration.Tests/MockTranslationServer.cs`
- `Windows/tests/EMKE.Integration.Tests/TranslationRuntimeIntegrationTests.cs`
- `Windows/tests/EMKE.Integration.Tests/FailureSafetyTests.cs`
- `Windows/tests/EMKE.Integration.Tests/TestAudioEngine.cs`
- `docs/quality/windows-runtime-evidence.md`
- `.superpowers/sdd/2026-07-26-emke-windows-translation-runtime/task-9-report.md`

## Concerns and proof boundary

No known Task 9 automated-test failure remains. This work does not verify the
real Translation service, an installed signed driver, live endpoints, a real
meeting, or human listening. The eight Integration skips require a Windows
x64 native artifact/host; the two Contract skips remain owned-adapter gaps.
