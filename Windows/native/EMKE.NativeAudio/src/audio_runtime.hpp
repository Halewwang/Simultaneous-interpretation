#ifndef EMKE_AUDIO_RUNTIME_HPP
#define EMKE_AUDIO_RUNTIME_HPP

#include "emke_native_audio.h"
#if defined(_WIN32) && !defined(EMKE_NATIVE_AUDIO_TEST_HOOKS)
#include "audio_worker.hpp"
#else
#include "fake_audio_backend.hpp"
#endif

#include <cstdint>
#include <span>

namespace emke::audio {

class AudioRuntime {
 public:
  explicit AudioRuntime(const emke_audio_config& config);

  emke_audio_status start();
  emke_audio_status stop();
  emke_audio_status set_inbound_route(emke_audio_route route);
  emke_audio_status set_outbound_route(emke_audio_route route);
  emke_audio_status enqueue_inbound_translation(
      std::span<const std::int16_t> pcm16);
  emke_audio_status enqueue_outbound_translation(
      std::span<const std::int16_t> pcm16);
  emke_audio_status poll_event(
      AudioEvent& event,
      std::size_t pcm_capacity_network_frames);
  void write_diagnostics(emke_audio_diagnostics& diagnostics) const;

#if defined(EMKE_NATIVE_AUDIO_TEST_HOOKS)
  emke_audio_status test_accept_synthetic(
      Direction direction,
      std::span<const float> interleaved_stereo);
  emke_audio_status test_render(
      Direction direction,
      std::span<std::int16_t> mono_pcm16);
  void test_inject_device_failure();
  void test_inject_inbound_translation_failure();
  void test_inject_outbound_underrun();
#endif

 private:
  [[maybe_unused]] emke_audio_config config_;
#if defined(_WIN32) && !defined(EMKE_NATIVE_AUDIO_TEST_HOOKS)
  NativeAudioBackend backend_;
#else
  FakeAudioBackend backend_;
#endif
};

}  // namespace emke::audio

#endif
