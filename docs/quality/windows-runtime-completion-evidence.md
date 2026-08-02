# Windows runtime completion evidence

Date: 2026-08-02
Scope: Task 5 managed runtime safety only. Baseline: `3adfda221d19b8b701178c40d84799a6207c888b`.

This evidence does not claim physical-driver installation, package signing/install,
provider-account, meeting-client, endpoint, or human-listening acceptance. Those
remain real-Windows acceptance gates.

## Production failure matrix

All listed tests exercise the production type at the listed seam. Fakes are
limited to operating-system, socket, native-audio, or credential boundaries.
Each reference asserts the stable category/code/recovery action where that
boundary creates the error, safe inbound/original and outbound/muted route (or
preflight `Stopped` routes), empty/allowlisted diagnostics, and cleanup or
ownership where the boundary owns a resource.

| Required case | Production seam and evidence | Stable result and safety/ownership evidence |
| --- | --- | --- |
| Unsupported build / product type | `WindowsHostCompatibilityProbeTests.MetadataBackedHostGateAdmitsOnlySupportedWorkstations`; `CompatibilityGateTests.HostGateFailureStopsBeforeDriverSecretAudioOrNetwork` | `configuration` / `unsupportedWindowsBuild` or `unsupportedWindowsProductType` / `reportCompatibility`; no parameters, stopped routes, and no driver, audio, secret, or network allocation. |
| Driver missing, signature, ABI, version, endpoints | `CompatibilityGateTests.EvaluationUsesStableReasonsInFailClosedOrder`, `EveryRequiredEndpointRoleMustBeActive`, `DuplicateRequiredEndpointRoleFailsClosed`, and `DriverMissingBlocksStartBeforeNetworkOrNativeAudio`; `TranslationRuntimeTests.MissingDriverOrEndpointsDoNotReachTheSessionFactory` | Compatibility reasons are deterministic. Runtime maps preflight to `driver` / `translationRuntime.driverIncompatible` / install-or-report action; device/endpoint loss maps to `device` / `translationRuntime.defaultPhysicalDeviceMissing` / `selectDevice`; empty diagnostics, stopped routes, and no session/audio ownership. |
| Selected/default device loss | `TranslationRuntimeTests.DeviceChangedControlEventStopsAndRetainsStableError`, `MissingDriverOrEndpointsDoNotReachTheSessionFactory`, and `StaleDeviceRefreshCannotOverwriteStoppedGeneration` | `device` / `translationRuntime.deviceChanged` / `selectDevice`; safety shutdown stops both routes and stale refresh cannot regain ownership. |
| Missing/invalid API key and provider rejection | `TranslationSessionFactoryTests.FactoryRejectsMissingSecretAsStableSecretFreeAuthenticationError`, `FactoryRejectsEmptySecretAsStableSecretFreeAuthenticationError`, `FactoryDisposesPartialSocketAndLeaseWhenHeaderConfigurationFails`; `TranslationRuntimeTests.FactoryAuthenticationFailureUsesTheStableRuntimeCategory`; `TranslationRuntimeIntegrationTests.MockServerRejectsConfiguredHandshake` | Local credential errors are `authentication` / `translationSessionFactory.*` / `updateApiKey`, with empty parameters, zero leaked secret, disposed lease/socket, stopped routes; hosted provider rejection remains stable socket-connect network behavior. |
| DNS/TLS/socket connect/send/receive | `TranslationSocketTests.ConnectAndAdapterFailuresReturnStableSecretFreeErrors`, `ConnectCancellationAndSendExceptionReturnStableErrors`, `ReceiveAdapterExceptionReturnsStableNetworkError`; `ChannelSupervisorTests.TransientSendFailureStartsReconnectWhileNonRetryableDoesNot` | `network` / `translationSocket.connectFailed`, `sendFailed`, or `receiveFailed` / `retry`; empty parameters, reconnect generation isolation, and outbound fail-closed path. DNS/TLS are mapped by the same `WebSocketException` production adapter boundary. |
| Alias, malformed event, server close | `TranslationEventCodecTests.DecodeRejectsLegacyPreV024EventNames`, malformed and closed-registry rows; `TranslationRuntimeIntegrationTests.LoopbackUsesFrozenTranslationEventsAndRejectsUnregisteredAliases`; `TranslationSessionTests.ConnectedTransportCloseBecomesRetryableNetworkFailure` | Aliases/malformed frames are `protocol` / `translationEvent.*` / `retry` and allocate no PCM lease; close is `network` / `translationSession.unexpectedSocketClose` / `retry`, then safe routing/reconnect. |
| Queue backpressure, underrun, overflow | `TranslationRuntimeTests.OutboundEnqueueFailureCommitsFailClosedRouteThroughActor`, `InboundEnqueueFailureCommitsFailOpenRouteThroughActor`, `BackpressureControlEventPreservesExplicitOutboundBypass`; `NativeAudioPollingTests.FullEventChannelDropsAndDisposesPcmLease`; `TranslationSessionTests.BoundedChannelAppliesBackpressureAndDeliversTailBeforeCloseCompletes` | `backpressure` / `translationRuntime.audioBackpressure` / `retry`; inbound keeps original audio, outbound mutes, explicit bypass is preserved, and rejected/dropped PCM owners are disposed exactly once. |
| Reconnect exhaustion and successful handshake | `ChannelSupervisorTests.TransientNetworkFailureUsesExactBoundedBackoffSchedule`, `ClosingDuringReconnectPreventsOldGenerationFromReopening`; `ProductionFailureMatrixTests.ReconnectResumesTranslatedRouteOnlyAfterNewHandshakeForOneHundredIterations` | `network` / `translationRuntime.reconnectExhausted` / `retry`; 250/500/1000/2000/5000 ms controlled schedule, fresh factory requests, old generation cannot reopen, translated route resumes only after handshake. |
| Bounded shutdown and cleanup | `ProductionFailureMatrixTests.ShutdownCompletesBeforeControlledDeadlineForOneHundredIterations`; `TranslationRuntimeTests.StopUsesControllableOneSecondLocalDeadline`, timeout/drain tests, and dispose tests; `TranslationSessionTests.DisposeDrainsQueuedAudioLeaseAndAwaitsReceiveBeforeTransportRelease` | `closeTimeout` / `translationRuntime.localCloseTimeout` / `retry`; local completion is bounded, restarts remain fenced until ownership drains, sessions/audio/queues/workers are disposed or quarantined deterministically. |

## Deterministic repeated proof

`ProductionFailureMatrixTests` runs five independent contracts exactly 100
times each, and its `SafetyAudit` counters assert every iteration completed:

| Contract | Executions | Evidence |
| --- | ---: | --- |
| Inbound session loss preserves original meeting audio | 100 | `InboundSessionLossKeepsOriginalAudioFailOpenForOneHundredIterations` |
| Outbound failure mutes virtual microphone | 100 | `OutboundSessionLossKeepsVirtualMicrophoneFailClosedForOneHundredIterations` |
| Explicit bypass remains explicit | 100 | `ExplicitBypassRemainsExplicitAfterInboundFaultForOneHundredIterations` |
| Reconnect requires a new successful handshake | 100 | `ReconnectResumesTranslatedRouteOnlyAfterNewHandshakeForOneHundredIterations` |
| Shutdown stays bounded and releases runtime state | 100 | `ShutdownCompletesBeforeControlledDeadlineForOneHundredIterations` |
| **Total** | **500** | Controlled clocks/barriers; `WaitAsync` is only a hang guard. |

## Protocol and diagnostic contract

The frozen runtime accepts exactly these five service forms:

the `session.audio.output.language` member of `session.update`,
`session.input_audio_buffer.append`,
`session.output_audio.delta`, `session.input_transcript.delta`, and
`session.output_transcript.delta`.

The four event-type constants plus the nested language member are source-guarded;
`session.completed`, `session.audio.delta`, and legacy aliases are rejected.
The source settings keep 24 kHz mono PCM16 for network audio and 48 kHz local
audio; no `voice`, `speed`, or `instructions` protocol controls are present.

`RuntimeError` permits only `build`, `driverVersion`, `endpointRole`,
`retryCount`, and `duration`, each structurally validated. Runtime logging
emits only stable `code`. Hostile codes, exception summaries, test aggregation,
credentials, credential-bearing URIs, raw endpoint IDs, captions, and PCM are
rejected or excluded by `RuntimeErrorTests`, `TranslationRuntimeTests`,
`TranslationSessionFactoryTests`, and `FailureSafetyTests`.

Local source guards on the evidence commit passed:

- `node Scripts/validate-shared-contracts.mjs` — `contract v1: 3 schemas, 8 fixtures`.
- The exact protocol guard found the four allowed event-type constants and no
  `session.completed` or `session.audio.delta` in Windows realtime sources.
- No `voice`, `speed`, or `instructions` setting/control exists in the Windows
  translation protocol sources; the only `voice` matches are internal VAD
  implementation identifiers.
- `git diff --check` passed.

## Prior RED/GREEN evidence

| Slice | RED evidence | GREEN evidence |
| --- | --- | --- |
| Runtime parameter allowlist | product tests `bcbd25a` / `46d85d8`; PR #15 closed unmerged | `6782126`, `16527b9`, `c7568fe` |
| Reconnect exhaustion | `b7aa49a`; PR #16 closed unmerged | `8420b03` |
| Five 100-iteration contracts | `c26ed25`; PRs #17 and #18 closed unmerged | `4b6fe4f`, `8235a4b`, `f94f2a4`, `c78477c`, `da8866f`, `1297c45`, `106143a` |
| Runtime log and hostile diagnostic codes | Runtime behavior RED run `30739089347` / job `91473119672`; hostile-code RED run `30739275302` / job `91473638707`; PR #19 closed unmerged | `8a1e542`, `b5afc38`, `135c762`, `3cc5cfd`; Runtime run `30739715270` / job `91474809921`; MSIX `30739715253` / job `91474821740` |

Runtime run `30739715270` passed all managed suites (Core 62, Contract 18,
Application 100, Realtime 109, Routing 53, Windows App 148, Integration 106
with one platform skip). Its only failure was the pre-existing owned-native PCM
fixture executable path mismatch. Internal MSIX run `30739715253` passed.

The final matrix-audit evidence PR records the strengthened cross-layer
assertions and this report; it is closed unmerged after its Windows checks are
captured. The local product branch is never pushed.

## Hosted boundary

Hosted CI validates source, managed runtime, native unit tests, and unsigned
package construction. It cannot prove signed driver installation, the exact
driver package and endpoint enumeration on a target machine, real provider
credentials/session behavior, meeting-client interoperability, or listening
quality. Those require the separate Windows 10/11 acceptance plan.
