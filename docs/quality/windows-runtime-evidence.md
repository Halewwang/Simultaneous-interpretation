# Windows Translation Runtime Automated Evidence

## Evidence identity

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
| Contract | 17 | 2 | 0 |
| Integration | 39 | 8 | 0 |
| Managed solution total | 308 | 10 | 0 |

The 10 skips are the two pre-existing owned-adapter contract gaps and eight
Windows native-DLL/production-P/Invoke checks that cannot execute on this
macOS host. Release solution build completed with 0 warnings and 0 errors.

Task 9 added 17 Integration executions. All server-to-client JSON events are
WebSocket Text messages. Binary is emitted only by the explicit protocol
negative case.

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
| Server error | Outbound becomes fail-closed while inbound remains Connected |

Business-flow tests additionally observed one 9,600-byte PCM16 batch as one
Text-frame JSON `input_audio_buffer.append`, direction-specific translated
audio queues, and isolated inbound/outbound captions.

## 100-seed safety result

All 100 deterministic seeds produced only zero-valued virtual-microphone
samples after the injected failure. No seed enabled explicit bypass before
failure.

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

Each seed renders a distinct non-zero physical-microphone probe after the
assigned failure and asserts every virtual-microphone byte is zero. The seven
failures are injected at their real runtime boundaries; the server, send,
receive, and close cases use local Translation sessions, while queue and
underrun cases use the native-audio interface seam.

## Unverified boundaries

The following are explicitly **unverified** by this automated evidence:

- the real OpenAI Translation service;
- an installed and signed Windows virtual-audio driver;
- live physical or virtual Windows audio endpoints;
- a real meeting application or meeting session;
- human listening quality or subjective audio acceptance.

These results are an integration and safety gate. They are not Windows
real-machine, driver-signing, live-service, meeting, or listening acceptance.
