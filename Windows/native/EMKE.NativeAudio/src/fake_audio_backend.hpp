#ifndef EMKE_FAKE_AUDIO_BACKEND_HPP
#define EMKE_FAKE_AUDIO_BACKEND_HPP

#include "emke_native_audio.h"

#include <cstddef>
#include <cstdint>
#include <deque>
#include <span>
#include <vector>

namespace emke::audio {

enum class Direction {
  Inbound,
  Outbound,
};

struct AudioEvent {
  emke_audio_event_kind kind = EMKE_AUDIO_EVENT_NONE;
  emke_audio_status status = EMKE_AUDIO_OK;
  emke_audio_route route = EMKE_AUDIO_ROUTE_STOPPED;
  std::uint64_t sequence = 0;
  std::vector<std::int16_t> pcm16;
};

class FakeAudioBackend {
 public:
  explicit FakeAudioBackend(
      std::size_t translation_queue_capacity_frames = 4'800u,
      std::size_t event_queue_capacity = 8u);

  emke_audio_status start();
  emke_audio_status stop();

  emke_audio_status set_route(Direction direction, emke_audio_route route);
  [[nodiscard]] emke_audio_route route(Direction direction) const;
  [[nodiscard]] bool is_running() const;

  emke_audio_status accept_synthetic_block(
      Direction direction,
      std::span<const float> interleaved_stereo_48khz);
  emke_audio_status enqueue_translation(
      Direction direction,
      std::span<const std::int16_t> mono_pcm16_24khz);
  emke_audio_status render_translation(
      Direction direction,
      std::span<std::int16_t> mono_pcm16_24khz);
  emke_audio_status poll_event(AudioEvent& event);

  void inject_device_failure();
  void inject_inbound_translation_failure();
  void inject_outbound_underrun();

  void write_diagnostics(emke_audio_diagnostics& diagnostics) const;

 private:
  [[nodiscard]] std::deque<std::int16_t>& translation_queue(
      Direction direction);
  [[nodiscard]] const std::deque<std::int16_t>& translation_queue(
      Direction direction) const;
  [[nodiscard]] emke_audio_route& mutable_route(Direction direction);
  emke_audio_status render_original_inbound(
      std::span<std::int16_t> destination);
  emke_audio_status render_outbound_zeros(
      std::span<std::int16_t> destination);

  std::size_t translation_queue_capacity_frames_;
  std::size_t event_queue_capacity_;
  bool running_ = false;
  bool fail_next_start_ = false;
  bool fail_next_inbound_translation_ = false;
  bool underrun_next_outbound_translation_ = false;
  emke_audio_route inbound_route_ = EMKE_AUDIO_ROUTE_STOPPED;
  emke_audio_route outbound_route_ = EMKE_AUDIO_ROUTE_STOPPED;
  std::uint64_t next_event_sequence_ = 1u;
  std::deque<std::int16_t> inbound_translation_;
  std::deque<std::int16_t> outbound_translation_;
  std::vector<std::int16_t> latest_inbound_original_;
  std::deque<AudioEvent> events_;
  std::uint64_t captured_inbound_frames_ = 0u;
  std::uint64_t captured_outbound_frames_ = 0u;
  std::uint64_t consumed_inbound_translation_frames_ = 0u;
  std::uint64_t consumed_outbound_translation_frames_ = 0u;
  std::uint64_t dropped_frames_ = 0u;
  std::uint64_t queue_full_events_ = 0u;
  std::uint64_t outbound_underruns_ = 0u;
  std::uint64_t inbound_translation_failures_ = 0u;
  std::uint64_t device_failures_ = 0u;
};

}  // namespace emke::audio

#endif
