#include "fake_audio_backend.hpp"

#include <algorithm>
#include <cmath>
#include <limits>

namespace emke::audio {
namespace {

bool is_known_route(emke_audio_route route) {
  return route >= EMKE_AUDIO_ROUTE_STOPPED &&
         route <= EMKE_AUDIO_ROUTE_MUTED_FAIL_CLOSED;
}

std::int16_t float_to_pcm16(float sample) {
  if (sample >= 1.0f) {
    return std::numeric_limits<std::int16_t>::max();
  }
  if (sample <= -1.0f) {
    return std::numeric_limits<std::int16_t>::min();
  }
  return static_cast<std::int16_t>(std::lround(sample * 32768.0f));
}

}  // namespace

FakeAudioBackend::FakeAudioBackend(
    std::size_t translation_queue_capacity_frames,
    std::size_t event_queue_capacity)
    : translation_queue_capacity_frames_(translation_queue_capacity_frames),
      event_queue_capacity_(event_queue_capacity) {}

emke_audio_status FakeAudioBackend::start() {
  if (running_) {
    return EMKE_AUDIO_OK;
  }
  if (fail_next_start_) {
    fail_next_start_ = false;
    ++device_failures_;
    return EMKE_AUDIO_DEVICE_MISSING;
  }

  running_ = true;
  inbound_route_ = EMKE_AUDIO_ROUTE_TRANSLATED;
  outbound_route_ = EMKE_AUDIO_ROUTE_TRANSLATED;
  return EMKE_AUDIO_OK;
}

emke_audio_status FakeAudioBackend::stop() {
  if (!running_) {
    return EMKE_AUDIO_OK;
  }

  running_ = false;
  inbound_route_ = EMKE_AUDIO_ROUTE_STOPPED;
  outbound_route_ = EMKE_AUDIO_ROUTE_STOPPED;
  inbound_translation_.clear();
  outbound_translation_.clear();
  latest_inbound_original_.clear();
  events_.clear();
  return EMKE_AUDIO_OK;
}

emke_audio_status FakeAudioBackend::set_route(Direction direction,
                                              emke_audio_route route_value) {
  if (!running_) {
    return EMKE_AUDIO_NOT_RUNNING;
  }
  if (!is_known_route(route_value)) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }

  mutable_route(direction) = route_value;
  return EMKE_AUDIO_OK;
}

emke_audio_route FakeAudioBackend::route(Direction direction) const {
  return direction == Direction::Inbound ? inbound_route_ : outbound_route_;
}

bool FakeAudioBackend::is_running() const {
  return running_;
}

emke_audio_status FakeAudioBackend::accept_synthetic_block(
    Direction direction,
    std::span<const float> interleaved_stereo_48khz) {
  if (!running_) {
    return EMKE_AUDIO_NOT_RUNNING;
  }
  if (interleaved_stereo_48khz.empty() ||
      interleaved_stereo_48khz.size() % 4u != 0u) {
    return EMKE_AUDIO_FORMAT_UNSUPPORTED;
  }

  std::vector<std::int16_t> converted;
  converted.reserve(interleaved_stereo_48khz.size() / 4u);
  for (std::size_t index = 0; index < interleaved_stereo_48khz.size();
       index += 4u) {
    const float mono_sample =
        (interleaved_stereo_48khz[index] +
         interleaved_stereo_48khz[index + 1u] +
         interleaved_stereo_48khz[index + 2u] +
         interleaved_stereo_48khz[index + 3u]) /
        4.0f;
    converted.push_back(float_to_pcm16(mono_sample));
  }

  if (events_.size() >= event_queue_capacity_) {
    dropped_frames_ += converted.size();
    ++queue_full_events_;
    return EMKE_AUDIO_QUEUE_FULL;
  }

  AudioEvent event;
  event.kind = direction == Direction::Inbound
                   ? EMKE_AUDIO_EVENT_INBOUND_PCM16
                   : EMKE_AUDIO_EVENT_OUTBOUND_PCM16;
  event.status = EMKE_AUDIO_OK;
  event.route = route(direction);
  event.sequence = next_event_sequence_++;
  event.pcm16 = converted;
  events_.push_back(std::move(event));

  if (direction == Direction::Inbound) {
    latest_inbound_original_ = std::move(converted);
    captured_inbound_frames_ += latest_inbound_original_.size();
  } else {
    captured_outbound_frames_ += converted.size();
  }
  return EMKE_AUDIO_OK;
}

emke_audio_status FakeAudioBackend::enqueue_translation(
    Direction direction,
    std::span<const std::int16_t> mono_pcm16_24khz) {
  if (!running_) {
    return EMKE_AUDIO_NOT_RUNNING;
  }
  if (mono_pcm16_24khz.empty()) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }

  auto& queue = translation_queue(direction);
  if (mono_pcm16_24khz.size() >
      translation_queue_capacity_frames_ - std::min(
                                               translation_queue_capacity_frames_,
                                               queue.size())) {
    dropped_frames_ += mono_pcm16_24khz.size();
    ++queue_full_events_;
    return EMKE_AUDIO_QUEUE_FULL;
  }

  queue.insert(
      queue.end(), mono_pcm16_24khz.begin(), mono_pcm16_24khz.end());
  return EMKE_AUDIO_OK;
}

emke_audio_status FakeAudioBackend::render_translation(
    Direction direction,
    std::span<std::int16_t> mono_pcm16_24khz) {
  if (!running_) {
    return EMKE_AUDIO_NOT_RUNNING;
  }
  if (mono_pcm16_24khz.empty()) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }

  if (direction == Direction::Inbound &&
      fail_next_inbound_translation_) {
    fail_next_inbound_translation_ = false;
    return render_original_inbound(mono_pcm16_24khz);
  }
  if (direction == Direction::Outbound &&
      underrun_next_outbound_translation_) {
    underrun_next_outbound_translation_ = false;
    return render_outbound_zeros(mono_pcm16_24khz);
  }

  auto& queue = translation_queue(direction);
  if (queue.size() < mono_pcm16_24khz.size()) {
    return direction == Direction::Inbound
               ? render_original_inbound(mono_pcm16_24khz)
               : render_outbound_zeros(mono_pcm16_24khz);
  }

  for (auto& destination_sample : mono_pcm16_24khz) {
    destination_sample = queue.front();
    queue.pop_front();
  }
  if (direction == Direction::Inbound) {
    consumed_inbound_translation_frames_ += mono_pcm16_24khz.size();
  } else {
    consumed_outbound_translation_frames_ += mono_pcm16_24khz.size();
  }
  return EMKE_AUDIO_OK;
}

emke_audio_status FakeAudioBackend::poll_event(AudioEvent& event) {
  if (!running_) {
    return EMKE_AUDIO_NOT_RUNNING;
  }
  if (events_.empty()) {
    event = {};
    return EMKE_AUDIO_OK;
  }

  event = std::move(events_.front());
  events_.pop_front();
  return EMKE_AUDIO_OK;
}

void FakeAudioBackend::inject_device_failure() {
  fail_next_start_ = true;
}

void FakeAudioBackend::inject_inbound_translation_failure() {
  fail_next_inbound_translation_ = true;
}

void FakeAudioBackend::inject_outbound_underrun() {
  underrun_next_outbound_translation_ = true;
}

void FakeAudioBackend::write_diagnostics(
    emke_audio_diagnostics& diagnostics) const {
  diagnostics = {};
  diagnostics.size = sizeof(diagnostics);
  diagnostics.abi_version = EMKE_AUDIO_ABI_VERSION;
  diagnostics.is_running = running_ ? 1u : 0u;
  diagnostics.inbound_route = inbound_route_;
  diagnostics.outbound_route = outbound_route_;
  diagnostics.queued_inbound_translation_frames =
      static_cast<std::uint32_t>(inbound_translation_.size());
  diagnostics.queued_outbound_translation_frames =
      static_cast<std::uint32_t>(outbound_translation_.size());
  diagnostics.captured_inbound_frames = captured_inbound_frames_;
  diagnostics.captured_outbound_frames = captured_outbound_frames_;
  diagnostics.consumed_inbound_translation_frames =
      consumed_inbound_translation_frames_;
  diagnostics.consumed_outbound_translation_frames =
      consumed_outbound_translation_frames_;
  diagnostics.dropped_frames = dropped_frames_;
  diagnostics.queue_full_events = queue_full_events_;
  diagnostics.outbound_underruns = outbound_underruns_;
  diagnostics.inbound_translation_failures =
      inbound_translation_failures_;
  diagnostics.device_failures = device_failures_;
}

std::deque<std::int16_t>& FakeAudioBackend::translation_queue(
    Direction direction) {
  return direction == Direction::Inbound ? inbound_translation_
                                         : outbound_translation_;
}

const std::deque<std::int16_t>& FakeAudioBackend::translation_queue(
    Direction direction) const {
  return direction == Direction::Inbound ? inbound_translation_
                                         : outbound_translation_;
}

emke_audio_route& FakeAudioBackend::mutable_route(Direction direction) {
  return direction == Direction::Inbound ? inbound_route_ : outbound_route_;
}

emke_audio_status FakeAudioBackend::render_original_inbound(
    std::span<std::int16_t> destination) {
  inbound_route_ = EMKE_AUDIO_ROUTE_ORIGINAL_FAIL_OPEN;
  ++inbound_translation_failures_;

  const std::size_t copied =
      std::min(destination.size(), latest_inbound_original_.size());
  std::copy_n(latest_inbound_original_.begin(), copied, destination.begin());
  std::fill(destination.begin() + static_cast<std::ptrdiff_t>(copied),
            destination.end(),
            0);
  return EMKE_AUDIO_OK;
}

emke_audio_status FakeAudioBackend::render_outbound_zeros(
    std::span<std::int16_t> destination) {
  outbound_route_ = EMKE_AUDIO_ROUTE_MUTED_FAIL_CLOSED;
  ++outbound_underruns_;
  dropped_frames_ += destination.size();
  std::fill(destination.begin(), destination.end(), 0);
  return EMKE_AUDIO_OK;
}

}  // namespace emke::audio
