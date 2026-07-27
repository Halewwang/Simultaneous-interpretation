#ifndef EMKE_AUDIO_BRIDGE_H
#define EMKE_AUDIO_BRIDGE_H

#include "emke_endpoint_contract.h"

#include <cstddef>
#include <cstdint>

#define EMKE_AUDIO_BRIDGE_CAPACITY_FRAMES 4800u

#if defined(_MSC_VER)
using EmkeAtomic32 = long;
using EmkeAtomic64 = __int64;
#else
using EmkeAtomic32 = std::int32_t;
using EmkeAtomic64 = std::int64_t;
#endif

enum class EmkeBridgeEndpoint : std::uint32_t {
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

std::size_t EmkeAudioBridgeWrite(
    EmkeAudioBridgeSet* bridges,
    EmkeBridgeEndpoint endpoint,
    const float* samples,
    std::size_t frame_count) noexcept;

std::size_t EmkeAudioBridgeRead(
    EmkeAudioBridgeSet* bridges,
    EmkeBridgeEndpoint endpoint,
    float* samples,
    std::size_t frame_count) noexcept;

void EmkeAudioBridgeReset(
    EmkeAudioBridgeSet* bridges,
    EmkeBridgeEndpoint endpoint) noexcept;

#endif
