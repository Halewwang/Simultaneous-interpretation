# EMKE Local Audio Engine Contract

This document is the implementation boundary between the installed Core Audio driver, the local macOS app, and the later Translation-session coordinator.

## Device Roles

The app resolves devices by UID every time it starts. It never changes the macOS default input or output device.

| App endpoint | Stable UID / selected device | Direction opened by EMKE | Purpose |
| --- | --- | --- | --- |
| Virtual-speaker capture | `com.emke.translation.virtual-speaker` | Input | Read meeting output written to `EMKE Virtual Speaker`. |
| Physical-microphone capture | User-selected physical UID | Input | Read the local user's microphone. |
| Physical output | User-selected physical UID | Output | Play inbound original or translated audio to the user's headphones. |
| Virtual-microphone render | `com.emke.translation.virtual-microphone` | Output | Supply translated or explicitly bypassed audio to the meeting input. |

Both EMKE virtual UIDs must exist before the menu-bar model reports ready. Virtual devices are excluded from the physical input/output pickers to prevent a feedback route.

## Audio Formats

- The device-facing AUHAL input uses the selected device's native sample rate and at most two native channels. A mono input is duplicated into stereo in the preallocated callback scratch buffer after `AudioUnitRender` succeeds.
- The ring-buffer and Swift engine boundary remains two-channel, interleaved Float32 PCM. Output endpoints use 48,000 Hz stereo; input resampling to that transport rate remains outside the AUHAL callback.
- Each worker cycle processes at most 480 frames, or 10 ms at 48 kHz.
- The default endpoint ring capacity is 4,800 frames, or 100 ms.
- Translation input/output uses 24,000 Hz, mono, signed little-endian PCM16.
- Encoding downmixes stereo, applies a two-frame averaging filter, and downsamples 2:1. Decoding converts PCM16 to stereo Float32 and uses a streaming 127-tap Blackman-windowed half-band FIR to interpolate 24 kHz to 48 kHz. The filter adds about 1.31 ms of fixed group delay and suppresses the high-frequency image that zero-order sample repetition would make audible.
- Conversion runs on the Swift audio worker, never in an AUHAL callback.

## Real-Time Boundary

`EMKEAudioHAL` owns all AUHAL callbacks and preallocates its scratch/ring storage before start. Input callbacks call `AudioUnitRender`, expand mono frames to the fixed stereo transport layout in place when needed, and bounded-write an SPSC ring. Output callbacks bounded-read an SPSC ring and zero-fill every underrun.

Callbacks do not call Swift, allocate memory, acquire locks, access files or the network, parse JSON, update UI, or emit logs. Swift polls or writes the C endpoint buffers outside the real-time callback.

## Routing and Safety

Inbound modes:

- `translated`: only explicitly supplied translated PCM reaches the physical output.
- `originalFailOpen`: meeting capture reaches the physical output after an inbound connection failure.
- `originalBypass`: meeting capture reaches the physical output after explicit user action.
- `stopped`: no inbound audio is written by the app.

Outbound modes:

- `translated`: only explicitly supplied translated PCM reaches `EMKE Virtual Microphone`.
- `mutedFailClosed`: captured microphone PCM is never written to the virtual microphone.
- `originalBypass`: captured microphone PCM reaches the virtual microphone only after explicit user action.
- `stopped`: no outbound audio is written by the app.

Captured inbound and outbound PCM generate independent network events. The event queue holds at most 64 items; later events are dropped and counted instead of allowing unbounded memory growth. A partial render write generates an output-backpressure event with its dropped-frame count.

## Lifecycle

The engine creates virtual-speaker input, physical-microphone input, physical output, and virtual-microphone output endpoints. It starts outputs before inputs so capture cannot accumulate before a safe render destination exists. A start failure stops only endpoints that already started and identifies the failed role.

Stop cancels the worker, stops outputs before inputs, resets both PCM encoders/decoders, clears capture and event buffers, and resumes pending event consumers with `.stopped`. C endpoint destruction repeats a safe stop before disposing AUHAL resources.

## Current Product Boundary

`EMKEMenuBarApp` now connects the four local endpoints to two independently managed Translation sessions. API credentials, public settings, language preferences, utterance-level mother-language gating, subtitles, manual bypass, capability probing, graceful close, and bounded reconnect behavior are defined in [`docs/translation-coordinator-contract.md`](translation-coordinator-contract.md).

The remaining boundary is real provider and meeting-app interoperability, performance acceptance, output speed/voice controls, and signed/notarized packaging. The development executable must not be represented as a distributable `.app`.

For local development:

```bash
swift build --product EMKEMenuBarApp
EMKE_RUN_LIVE_AUDIO_TESTS=1 swift test --filter liveVirtualEndpointsStartAndStop
```

The SwiftPM executable is a development shell, not a signed/notarized distributable `.app`.
