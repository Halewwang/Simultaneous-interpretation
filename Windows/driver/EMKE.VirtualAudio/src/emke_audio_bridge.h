#ifndef EMKE_AUDIO_BRIDGE_H
#define EMKE_AUDIO_BRIDGE_H

#include "emke_endpoint_contract.h"

#if defined(_KERNEL_MODE)
using EmkeSize = SIZE_T;
using EmkeUInt32 = ULONG;
using EmkeAtomic32 = LONG;
using EmkeAtomic64 = LONGLONG;
#else
#include <cstddef>
#include <cstdint>

using EmkeSize = std::size_t;
using EmkeUInt32 = std::uint32_t;
#if defined(_MSC_VER)
using EmkeAtomic32 = long;
using EmkeAtomic64 = __int64;
#else
using EmkeAtomic32 = std::int32_t;
using EmkeAtomic64 = std::int64_t;
#endif
#endif

#define EMKE_AUDIO_BRIDGE_CAPACITY_FRAMES 4800u

enum class EmkeBridgeEndpoint : EmkeUInt32 {
  meetingSpeakerRender = 0u,
  appSpeakerCapture = 1u,
  appMicrophoneRender = 2u,
  meetingMicrophoneCapture = 3u,
};

struct EmkeAudioBridge {
  alignas(8) volatile EmkeAtomic64 read_frame;
  alignas(8) volatile EmkeAtomic64 write_frame;
  volatile EmkeAtomic32 access_state;
  float samples[EMKE_AUDIO_BRIDGE_CAPACITY_FRAMES * EMKE_AUDIO_CHANNEL_COUNT];
};

struct EmkeAudioBridgeSet {
  EmkeAudioBridge meeting_speaker_to_app_capture;
  EmkeAudioBridge app_microphone_to_meeting_capture;
};

extern EmkeAudioBridgeSet g_EmkeAudioBridges;

void EmkeAudioBridgeInitialize(EmkeAudioBridgeSet* bridges) noexcept;

EmkeSize EmkeAudioBridgeWrite(
    EmkeAudioBridgeSet* bridges,
    EmkeBridgeEndpoint endpoint,
    const float* samples,
    EmkeSize frame_count) noexcept;

EmkeSize EmkeAudioBridgeRead(
    EmkeAudioBridgeSet* bridges,
    EmkeBridgeEndpoint endpoint,
    float* samples,
    EmkeSize frame_count) noexcept;

void EmkeAudioBridgeReset(
    EmkeAudioBridgeSet* bridges,
    EmkeBridgeEndpoint endpoint) noexcept;

#endif
