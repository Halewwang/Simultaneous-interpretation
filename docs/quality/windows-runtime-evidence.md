# Windows Translation Runtime Automated Evidence

## Evidence identity

- Tested Task 10 runtime CI implementation source commit:
  `775b84ff1349a217208183e32b94a12b3e13ab58`
- Tested Task 9 implementation source commit:
  `a478c5985bc0c3b269b11f6e6d7bf65b606e5232`
- Task 9 evidence commit and Task 10 implementation baseline:
  `63eda23f8be04722c53c94d33d9d72d71ab04901`
- Review baseline source commit: `90dac629eefc6b27b0346bf0561cb6ce7b2718a6`
- Required transport-close fix: `78713339c36767016aacaa0f0d367386450420ef`
- Target: Windows 11 25H2+ x64, managed TFM `net10.0-windows10.0.26100.0`
- .NET SDK: `10.0.302` (`35b593bebf`)
- .NET host: `10.0.10`, x64
- Managed/native audio ABI: `EMKE_AUDIO_ABI_VERSION = 1`
- Driver endpoint ABI: `EMKE_DRIVER_ABI = 1`
- Shared contract version: `1` (`frozen`)

The integration server binds only `127.0.0.1` on an ephemeral port and
returns its resolved `ws://127.0.0.1:<port>/realtime/translations` URI to
tests. It neither resolves nor contacts an external host. The settings seam
uses the non-secret literal `local-test-placeholder`; no real API key is
loaded, logged, or transmitted.

## Observed automated results

Fresh Release solution results on 2026-07-28:

| Suite | Passed | Expected skip | Failed |
| --- | ---: | ---: | ---: |
| Application unit | 56 | 0 | 0 |
| Core unit | 50 | 0 | 0 |
| Realtime unit | 96 | 0 | 0 |
| Routing unit | 50 | 0 | 0 |
| Contract | 18 | 0 | 0 |
| Integration | 44 | 0 | 0 |
| Managed solution total | 314 | 0 | 0 |

Release solution build completed with 0 warnings and 0 errors. The contract
suite now executes the production settings migration and driver-compatibility
policies against both canonical settings fixtures. The default solution gate
does not discover the nine Windows-isolated tests: one owned native PCM
contract adapter, seven native-fake P/Invoke tests, and one real-DLL ABI test.
The Windows workflow runs those categories explicitly after building native
artifacts.

The shared contract validator passed with 3 schemas and 8 canonical fixtures.
The routing language corpus is separately parsed and validated as a named
auxiliary vector rather than being misclassified as a ninth canonical fixture.
The incomplete-source scan over `Windows/src` and `Windows/tests` produced no
matches.

All server-to-client JSON events are WebSocket Text messages. Binary is
emitted only by the explicit protocol negative case.

## Task 10 CI gate

`.github/workflows/windows-runtime.yml` is independent of macOS release and
Windows packaging workflows. It targets the Windows x64 hosted runner with
.NET 10 and Node 24, validates the shared contract, performs locked restore,
builds and runs native CTest, builds and tests the Release solution, runs the
owned native PCM adapter, then runs native-fake and real-DLL managed tests in
separate processes. Every managed test command writes TRX, and the workflow
uploads the complete TRX set even when a preceding gate fails.

The isolated launcher requires Windows x64 before resolving artifacts. Its
behavior test observed that a non-Windows environment fails and that the
Windows x64 path launches exactly two filtered processes with exact 7-test and
1-test TRX requirements. On this macOS host, explicitly selecting the native
PCM contract category failed with the required Windows x64 diagnostic; it did
not report a pass or skip.

The workflow YAML and its PowerShell run block passed local static parsing.
The hosted Windows workflow itself, native CTest, owned native PCM adapter,
native-fake DLL, and real production DLL were not executed locally and remain
pending remote CI observation for source commit
`775b84ff1349a217208183e32b94a12b3e13ab58`.

## Scenario coverage

| Scenario | Automated observation |
| --- | --- |
| Normal handshake | `session.created` → Text `session.update` → `session.updated`; connected |
| Two simultaneous sessions | Two independent loopback sockets; runtime reaches Running |
| Same-language path | One socket; outbound is Bypassed with OriginalBypass |
| Fragmented Text | Both handshake server events split across two Text frames |
| Binary injection | Stable `binaryTranslationEvent` Protocol failure |
| 401 / 403 equivalent rejection | Both reject WebSocket upgrade as stable Network connect failure |
| Unknown model | Unknown query model rejects upgrade |
| Delayed close | Close remains pending until the deterministic release gate |
| Blocked close | Stable `translationSession.closeTimeout` |
| Late transcript/audio | Both Text deltas are observed before close completion |
| Disconnect/reconnect | Abrupt loopback transport close creates a third connection and returns outbound to Connected |
| Reconnect wait window | A send issued after removal waits for and reaches the replacement connection |
| Server error | Outbound becomes fail-closed while inbound remains Connected |

Business-flow tests additionally observed one WebSocket Text message whose
JSON `input_audio_buffer.append` carried a 9,600-byte PCM16 payload,
direction-specific translated-audio queues, and isolated inbound/outbound
captions. The capture seam proves message type and decoded payload; it does
not claim a physical WebSocket frame boundary.

## 100-seed safety result

All 100 deterministic seeds are independent iterations. Every seed creates a
new loopback server, audio adapter, fault plan, and runtime; receives its
assigned boundary injection; and produces only zero-valued
virtual-microphone samples after that failure. Before injection, every seed
asserts that neither runtime nor adapter uses `OriginalBypass`, then proves an
exact-length, byte-equal, non-zero translated control. After injection, every
seed asserts an exact-length output whose bytes are all zero.

| Injection | Seeds | Non-zero outputs |
| --- | ---: | ---: |
| Outbound disconnect | 15 | 0 |
| Server error | 15 | 0 |
| Send failure | 14 | 0 |
| Receive failure | 14 | 0 |
| Queue full | 14 | 0 |
| Translated-audio underrun | 14 | 0 |
| Close timeout | 14 | 0 |
| Total | 100 | 0 |

The explicit-bypass positive control separately starts the runtime with
outbound bypass enabled and proves that the adapter renders a non-zero probe,
so an always-zero fake cannot satisfy the safety test. The seven failures are
injected at their runtime boundaries; the server, send, receive, and close
cases use local Translation sessions, while queue full uses an actually full
capacity-one adapter queue and underrun uses the native-audio interface seam.
Every close-timeout seed also asserts the stable
`translationRuntime.localCloseTimeout` error.

## Unverified boundaries

The following are explicitly **unverified** by this automated evidence:

- the real OpenAI Translation service;
- an installed and signed Windows virtual-audio driver;
- live physical or virtual Windows audio endpoints;
- a real meeting application or meeting session;
- human listening quality or subjective audio acceptance.

These results are an integration and safety gate. They are not Windows
real-machine, driver-signing, live-service, meeting, or listening acceptance.
