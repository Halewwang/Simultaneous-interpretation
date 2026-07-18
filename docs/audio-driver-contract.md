# EMKE Virtual Audio Driver Contract

## Purpose

The EMKE Audio Server Plug-in publishes two fixed Core Audio devices. Meeting software and the future EMKE menu-bar app use opposite streams on each device, while audio samples remain inside `coreaudiod` and move through bounded, lock-free ring buffers.

The driver does not translate, resample, access the network, write files, or call Swift. It only publishes Core Audio objects, transfers frames, and applies the microphone silence safety policy.

## Device and Stream Roles

| Device | Stream | Consumer | Role |
| --- | --- | --- | --- |
| `EMKE Virtual Speaker` | Output, object `4` | Feishu, DingTalk, Teams | Meeting output is written into EMKE instead of a physical speaker. |
| `EMKE Virtual Speaker` | Input, object `3` | EMKE app | Companion stream from which EMKE captures the meeting output for translation. |
| `EMKE Virtual Microphone` | Output, object `7` | EMKE app | Companion stream into which EMKE writes translated or explicitly bypassed microphone audio. |
| `EMKE Virtual Microphone` | Input, object `6` | Feishu, DingTalk, Teams | Meeting input that receives only audio supplied by EMKE. |

The meeting configuration contract is therefore:

- Speaker/output device: `EMKE Virtual Speaker`.
- Microphone/input device: `EMKE Virtual Microphone`.

The EMKE app opens the opposite-direction companion streams automatically. Users do not select companion streams inside the meeting. Because both virtual devices are duplex transport devices, some meeting device menus may show both names in both input and output lists; product onboarding must highlight the correct pair above.

## Object Identity

| Object | ID | UID |
| --- | ---: | --- |
| Plug-in | `1` | `com.emke.translation.audio-driver` |
| Virtual speaker device | `2` | `com.emke.translation.virtual-speaker` |
| Speaker app-capture input | `3` | `com.emke.translation.virtual-speaker.input` |
| Speaker meeting output | `4` | `com.emke.translation.virtual-speaker.output` |
| Virtual microphone device | `5` | `com.emke.translation.virtual-microphone` |
| Microphone meeting input | `6` | `com.emke.translation.virtual-microphone.input` |
| Microphone app-translation output | `7` | `com.emke.translation.virtual-microphone.output` |

The numeric IDs are internal to the plug-in and stable for its loaded lifetime. Application code must discover devices by UID rather than persisting numeric `AudioObjectID` values between boots or driver reloads.

## Audio Format

Every driver stream has one fixed HAL format:

- Sample rate: `48 kHz`.
- Channels: two, interleaved stereo.
- Sample representation: native-endian packed `Float32` linear PCM.
- Frames per packet: `1`.
- Bytes per frame and packet: `8`.

The later application audio engine owns all conversion between this HAL format, physical device formats, and the Realtime Translation protocol’s 24 kHz mono PCM16 format.

## Buffering and Safety

The driver allocates both ring buffers during `Initialize`; HAL I/O callbacks never allocate or free memory. Each buffer has a two-second capacity at 48 kHz.

- Speaker overflow: reject and count newest frames that do not fit. Never overwrite unread meeting audio and never block the HAL callback.
- Speaker underrun: the app-facing input receives available frames followed by zero-fill for the rest of the HAL request.
- Microphone overflow: reject newest translated frames that do not fit. The app audio engine must monitor pacing and avoid writing faster than the meeting consumes.
- Microphone underrun: always zero-fill the missing tail and report the entire HAL request as satisfied. If EMKE stops or crashes, the meeting receives silence rather than the user’s untranslated physical microphone.
- Start/stop isolation: speaker lifecycle changes reset only the speaker route; microphone lifecycle changes reset only the microphone route.

No audio or transcript content crosses XPC, Mach services, disk, logs, or EMKE servers in this driver layer.

## Build and Verification

Build without a full Xcode project:

```bash
make -C Driver clean all
bash Driver/verify-bundle.sh .build/driver/EMKEAudioDriver.driver
```

The verifier checks plist metadata, CFPlugIn UUID registration, arm64 architecture, framework links, the exported factory, local ad-hoc signing, dynamic Bundle loading, both device names, speaker loopback, microphone zero-fill, and microphone loopback.

For an already signed package payload, use the read-only mode:

```bash
bash Driver/verify-bundle.sh --read-only /path/to/EMKEAudioDriver.driver
```

Read-only mode verifies the packaged bundle signature without clearing xattrs
or re-signing packaged bytes. It may still rebuild and locally sign the separate
`.build/driver/verify-bundle` smoke executable.

Automated verification does not copy the driver into `/Library/Audio/Plug-Ins/HAL`, restart `coreaudiod`, or change system audio settings. Those actions require explicit approval because they interrupt active audio clients and need administrator authorization.
