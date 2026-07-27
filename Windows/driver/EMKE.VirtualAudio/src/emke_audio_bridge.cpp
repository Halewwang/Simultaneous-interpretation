#include "emke_audio_bridge.h"

#if defined(_MSC_VER)
#include <intrin.h>
#endif

namespace {

constexpr EmkeAtomic32 bridge_idle = 0;
constexpr EmkeAtomic32 bridge_producer_active = 1;
constexpr EmkeAtomic32 bridge_consumer_active = 2;
constexpr EmkeAtomic32 bridge_reset_active = 4;

EmkeAtomic32 atomic_load_32(volatile EmkeAtomic32* value) noexcept {
#if defined(_MSC_VER)
  return _InterlockedCompareExchange(value, 0, 0);
#else
  return __atomic_load_n(value, __ATOMIC_ACQUIRE);
#endif
}

void atomic_store_32(
    volatile EmkeAtomic32* value,
    EmkeAtomic32 desired) noexcept {
#if defined(_MSC_VER)
  static_cast<void>(_InterlockedExchange(value, desired));
#else
  __atomic_store_n(value, desired, __ATOMIC_RELEASE);
#endif
}

bool atomic_compare_exchange_32(
    volatile EmkeAtomic32* value,
    EmkeAtomic32 expected,
    EmkeAtomic32 desired) noexcept {
#if defined(_MSC_VER)
  return _InterlockedCompareExchange(value, desired, expected) == expected;
#else
  return __atomic_compare_exchange_n(
      value,
      &expected,
      desired,
      false,
      __ATOMIC_ACQ_REL,
      __ATOMIC_ACQUIRE);
#endif
}

bool acquire_realtime_access(
    volatile EmkeAtomic32* state,
    EmkeAtomic32 access_bit) noexcept {
  for (;;) {
    const EmkeAtomic32 current = atomic_load_32(state);
    if ((current & (bridge_reset_active | access_bit)) != 0) {
      return false;
    }
    if (atomic_compare_exchange_32(
        state,
        current,
        current | access_bit)) {
      return true;
    }
  }
}

void release_realtime_access(
    volatile EmkeAtomic32* state,
    EmkeAtomic32 access_bit) noexcept {
  for (;;) {
    const EmkeAtomic32 current = atomic_load_32(state);
    if (atomic_compare_exchange_32(
        state,
        current,
        current & ~access_bit)) {
      return;
    }
  }
}

EmkeAtomic64 atomic_load_64(volatile EmkeAtomic64* value) noexcept {
#if defined(_MSC_VER)
  return _InterlockedCompareExchange64(value, 0, 0);
#else
  return __atomic_load_n(value, __ATOMIC_ACQUIRE);
#endif
}

void atomic_store_64(
    volatile EmkeAtomic64* value,
    EmkeAtomic64 desired) noexcept {
#if defined(_MSC_VER)
  static_cast<void>(_InterlockedExchange64(value, desired));
#else
  __atomic_store_n(value, desired, __ATOMIC_RELEASE);
#endif
}

EmkeAudioBridge* bridge_for_endpoint(
    EmkeAudioBridgeSet* bridges,
    EmkeBridgeEndpoint endpoint) noexcept {
  if (bridges == nullptr) {
    return nullptr;
  }
  switch (endpoint) {
    case EmkeBridgeEndpoint::meetingSpeakerRender:
    case EmkeBridgeEndpoint::appSpeakerCapture:
      return &bridges->meeting_speaker_to_app_capture;
    case EmkeBridgeEndpoint::appMicrophoneRender:
    case EmkeBridgeEndpoint::meetingMicrophoneCapture:
      return &bridges->app_microphone_to_meeting_capture;
  }
  return nullptr;
}

bool is_render_producer(EmkeBridgeEndpoint endpoint) noexcept {
  return endpoint == EmkeBridgeEndpoint::meetingSpeakerRender ||
      endpoint == EmkeBridgeEndpoint::appMicrophoneRender;
}

bool is_capture_consumer(EmkeBridgeEndpoint endpoint) noexcept {
  return endpoint == EmkeBridgeEndpoint::appSpeakerCapture ||
      endpoint == EmkeBridgeEndpoint::meetingMicrophoneCapture;
}

void initialize_bridge(EmkeAudioBridge* bridge) noexcept {
  atomic_store_32(&bridge->access_state, bridge_reset_active);
  atomic_store_64(&bridge->read_frame, 0);
  atomic_store_64(&bridge->write_frame, 0);
  for (EmkeSize index = 0;
       index < EMKE_AUDIO_BRIDGE_CAPACITY_FRAMES * EMKE_AUDIO_CHANNEL_COUNT;
       ++index) {
    bridge->samples[index] = 0.0f;
  }
  atomic_store_32(&bridge->access_state, bridge_idle);
}

}  // namespace

EmkeAudioBridgeSet g_EmkeAudioBridges{};

void EmkeAudioBridgeInitialize(EmkeAudioBridgeSet* bridges) noexcept {
  if (bridges == nullptr) {
    return;
  }
  initialize_bridge(&bridges->meeting_speaker_to_app_capture);
  initialize_bridge(&bridges->app_microphone_to_meeting_capture);
}

EmkeSize EmkeAudioBridgeWrite(
    EmkeAudioBridgeSet* bridges,
    EmkeBridgeEndpoint endpoint,
    const float* samples,
    EmkeSize frame_count) noexcept {
  EmkeAudioBridge* bridge = bridge_for_endpoint(bridges, endpoint);
  if (bridge == nullptr || !is_render_producer(endpoint) ||
      samples == nullptr || frame_count == 0u ||
      !acquire_realtime_access(
          &bridge->access_state,
          bridge_producer_active)) {
    return 0u;
  }

  const EmkeAtomic64 read_frame = atomic_load_64(&bridge->read_frame);
  const EmkeAtomic64 write_frame = atomic_load_64(&bridge->write_frame);
  const EmkeAtomic64 used_frames = write_frame - read_frame;
  if (used_frames < 0 ||
      used_frames > static_cast<EmkeAtomic64>(
                        EMKE_AUDIO_BRIDGE_CAPACITY_FRAMES)) {
    release_realtime_access(
        &bridge->access_state,
        bridge_producer_active);
    return 0u;
  }

  const EmkeSize available_frames =
      EMKE_AUDIO_BRIDGE_CAPACITY_FRAMES -
      static_cast<EmkeSize>(used_frames);
  const EmkeSize accepted_frames =
      frame_count < available_frames ? frame_count : available_frames;
  for (EmkeSize frame = 0; frame < accepted_frames; ++frame) {
    const EmkeSize destination_frame =
        (static_cast<EmkeSize>(write_frame) + frame) %
        EMKE_AUDIO_BRIDGE_CAPACITY_FRAMES;
    for (EmkeSize channel = 0; channel < EMKE_AUDIO_CHANNEL_COUNT;
         ++channel) {
      bridge->samples[
          destination_frame * EMKE_AUDIO_CHANNEL_COUNT + channel] =
          samples[frame * EMKE_AUDIO_CHANNEL_COUNT + channel];
    }
  }

  atomic_store_64(
      &bridge->write_frame,
      write_frame + static_cast<EmkeAtomic64>(accepted_frames));
  release_realtime_access(
      &bridge->access_state,
      bridge_producer_active);
  return accepted_frames;
}

EmkeSize EmkeAudioBridgeRead(
    EmkeAudioBridgeSet* bridges,
    EmkeBridgeEndpoint endpoint,
    float* samples,
    EmkeSize frame_count) noexcept {
  if (samples == nullptr) {
    return 0u;
  }
  for (EmkeSize index = 0;
       index < frame_count * EMKE_AUDIO_CHANNEL_COUNT;
       ++index) {
    samples[index] = 0.0f;
  }

  EmkeAudioBridge* bridge = bridge_for_endpoint(bridges, endpoint);
  if (bridge == nullptr || !is_capture_consumer(endpoint) ||
      frame_count == 0u ||
      !acquire_realtime_access(
          &bridge->access_state,
          bridge_consumer_active)) {
    return 0u;
  }

  const EmkeAtomic64 read_frame = atomic_load_64(&bridge->read_frame);
  const EmkeAtomic64 write_frame = atomic_load_64(&bridge->write_frame);
  const EmkeAtomic64 available = write_frame - read_frame;
  if (available < 0 ||
      available > static_cast<EmkeAtomic64>(
                      EMKE_AUDIO_BRIDGE_CAPACITY_FRAMES)) {
    release_realtime_access(
        &bridge->access_state,
        bridge_consumer_active);
    return 0u;
  }

  const EmkeSize delivered_frames =
      frame_count < static_cast<EmkeSize>(available)
      ? frame_count
      : static_cast<EmkeSize>(available);
  for (EmkeSize frame = 0; frame < delivered_frames; ++frame) {
    const EmkeSize source_frame =
        (static_cast<EmkeSize>(read_frame) + frame) %
        EMKE_AUDIO_BRIDGE_CAPACITY_FRAMES;
    for (EmkeSize channel = 0; channel < EMKE_AUDIO_CHANNEL_COUNT;
         ++channel) {
      samples[frame * EMKE_AUDIO_CHANNEL_COUNT + channel] =
          bridge->samples[
              source_frame * EMKE_AUDIO_CHANNEL_COUNT + channel];
    }
  }

  atomic_store_64(
      &bridge->read_frame,
      read_frame + static_cast<EmkeAtomic64>(delivered_frames));
  release_realtime_access(
      &bridge->access_state,
      bridge_consumer_active);
  return delivered_frames;
}

void EmkeAudioBridgeReset(
    EmkeAudioBridgeSet* bridges,
    EmkeBridgeEndpoint endpoint) noexcept {
  EmkeAudioBridge* bridge = bridge_for_endpoint(bridges, endpoint);
  if (bridge == nullptr) {
    return;
  }

  while (!atomic_compare_exchange_32(
      &bridge->access_state,
      bridge_idle,
      bridge_reset_active)) {
  }
  atomic_store_64(
      &bridge->read_frame,
      atomic_load_64(&bridge->write_frame));
  atomic_store_32(&bridge->access_state, bridge_idle);
}
