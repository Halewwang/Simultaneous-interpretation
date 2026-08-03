#include "fake_audio_backend.hpp"

#include <algorithm>
#include <cmath>
#include <limits>

namespace emke::audio {
namespace {

bool is_direction_safe_route(Direction direction, emke_audio_route route) {
  if (direction == Direction::Inbound) {
    return route == EMKE_AUDIO_ROUTE_TRANSLATED ||
           route == EMKE_AUDIO_ROUTE_ORIGINAL_FAIL_OPEN ||
           route == EMKE_AUDIO_ROUTE_ORIGINAL_BYPASS;
  }
  return route == EMKE_AUDIO_ROUTE_TRANSLATED ||
         route == EMKE_AUDIO_ROUTE_ORIGINAL_BYPASS ||
         route == EMKE_AUDIO_ROUTE_MUTED_FAIL_CLOSED;
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
    std::size_t translation_queue_capacity_network_frames,
    std::size_t capture_capacity_local_frames)
    : translation_queue_capacity_network_frames_(
          translation_queue_capacity_network_frames),
      capture_capacity_local_frames_(capture_capacity_local_frames) {}

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
  latest_outbound_original_.clear();
  events_.clear();
  queued_capture_local_frames_ = 0u;
  return EMKE_AUDIO_OK;
}

emke_audio_status FakeAudioBackend::set_route(Direction direction,
                                              emke_audio_route route_value) {
  if (!running_) {
    return EMKE_AUDIO_NOT_RUNNING;
  }
  if (!is_direction_safe_route(direction, route_value)) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }
  if (route(direction) == route_value) {
    return EMKE_AUDIO_OK;
  }

  discard_translation(direction);
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
      interleaved_stereo_48khz.size() % 2u != 0u) {
    return EMKE_AUDIO_FORMAT_UNSUPPORTED;
  }

  const std::size_t local_frames = interleaved_stereo_48khz.size() / 2u;
  if (local_frames % EMKE_AUDIO_LOCAL_CYCLE_FRAMES != 0u) {
    return EMKE_AUDIO_FORMAT_UNSUPPORTED;
  }
  if (local_frames > capture_capacity_local_frames_ ||
      local_frames >
          capture_capacity_local_frames_ -
              std::min(capture_capacity_local_frames_,
                       queued_capture_local_frames_)) {
    dropped_frames_ += local_frames / 2u;
    ++queue_full_events_;
    return EMKE_AUDIO_QUEUE_FULL;
  }

  std::vector<std::int16_t> converted;
  converted.reserve(local_frames / 2u);
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

  AudioEvent event;
  event.kind = direction == Direction::Inbound
                   ? EMKE_AUDIO_EVENT_INBOUND_PCM16
                   : EMKE_AUDIO_EVENT_OUTBOUND_PCM16;
  event.status = EMKE_AUDIO_OK;
  event.route = route(direction);
  event.sequence = next_event_sequence_++;
  event.pcm16 = converted;
  events_.push_back(std::move(event));
  queued_capture_local_frames_ += local_frames;

  if (direction == Direction::Inbound) {
    latest_inbound_original_ = std::move(converted);
    captured_inbound_frames_ += latest_inbound_original_.size();
  } else {
    latest_outbound_original_ = std::move(converted);
    captured_outbound_frames_ += latest_outbound_original_.size();
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

  if (route(direction) != EMKE_AUDIO_ROUTE_TRANSLATED) {
    dropped_frames_ += mono_pcm16_24khz.size();
    return EMKE_AUDIO_OK;
  }

  auto& queue = translation_queue(direction);
  if (mono_pcm16_24khz.size() >
      translation_queue_capacity_network_frames_ -
          std::min(translation_queue_capacity_network_frames_, queue.size())) {
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

  if (direction == Direction::Inbound) {
    if (fail_next_inbound_translation_) {
      fail_next_inbound_translation_ = false;
      enter_inbound_fail_open();
    }
    if (inbound_route_ == EMKE_AUDIO_ROUTE_ORIGINAL_FAIL_OPEN ||
        inbound_route_ == EMKE_AUDIO_ROUTE_ORIGINAL_BYPASS) {
      return render_original(Direction::Inbound, mono_pcm16_24khz);
    }
  } else {
    if (underrun_next_outbound_translation_) {
      underrun_next_outbound_translation_ = false;
      enter_outbound_fail_closed();
    }
    if (outbound_route_ == EMKE_AUDIO_ROUTE_ORIGINAL_BYPASS) {
      return render_original(Direction::Outbound, mono_pcm16_24khz);
    }
    if (outbound_route_ == EMKE_AUDIO_ROUTE_MUTED_FAIL_CLOSED) {
      return render_outbound_zeros(mono_pcm16_24khz);
    }
  }

  auto& queue = translation_queue(direction);
  if (queue.size() < mono_pcm16_24khz.size()) {
    if (direction == Direction::Inbound) {
      enter_inbound_fail_open();
      return render_original(Direction::Inbound, mono_pcm16_24khz);
    }
    enter_outbound_fail_closed();
    return render_outbound_zeros(mono_pcm16_24khz);
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
  return poll_event(event, std::numeric_limits<std::size_t>::max());
}

emke_audio_status FakeAudioBackend::poll_event(
    AudioEvent& event,
    std::size_t pcm_capacity_network_frames) {
  if (events_.empty()) {
    event = {};
    return running_ ? EMKE_AUDIO_OK : EMKE_AUDIO_NOT_RUNNING;
  }

  event = events_.front();
  if (event.pcm16.size() > pcm_capacity_network_frames) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }

  events_.pop_front();
  queued_capture_local_frames_ -= event.pcm16.size() * 2u;
  return EMKE_AUDIO_OK;
}

void FakeAudioBackend::inject_device_failure() {
  if (!running_) {
    fail_next_start_ = true;
    return;
  }

  ++device_failures_;
  discard_translation(Direction::Inbound);
  discard_translation(Direction::Outbound);
  dropped_frames_ += queued_capture_local_frames_ / 2u;
  events_.clear();
  queued_capture_local_frames_ = 0u;
  latest_inbound_original_.clear();
  latest_outbound_original_.clear();
  running_ = false;
  inbound_route_ = EMKE_AUDIO_ROUTE_STOPPED;
  outbound_route_ = EMKE_AUDIO_ROUTE_STOPPED;

  AudioEvent event;
  event.kind = EMKE_AUDIO_EVENT_DEVICE_CHANGED;
  event.status = EMKE_AUDIO_DEVICE_MISSING;
  event.route = EMKE_AUDIO_ROUTE_STOPPED;
  event.sequence = next_event_sequence_++;
  events_.push_back(std::move(event));
}

void FakeAudioBackend::inject_inbound_translation_failure() {
  if (running_) {
    enter_inbound_fail_open();
  } else {
    fail_next_inbound_translation_ = true;
  }
}

void FakeAudioBackend::inject_outbound_underrun() {
  if (running_) {
    enter_outbound_fail_closed();
  } else {
    underrun_next_outbound_translation_ = true;
  }
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

void FakeAudioBackend::discard_translation(Direction direction) {
  auto& queue = translation_queue(direction);
  dropped_frames_ += queue.size();
  queue.clear();
}

void FakeAudioBackend::enter_inbound_fail_open() {
  if (inbound_route_ != EMKE_AUDIO_ROUTE_ORIGINAL_FAIL_OPEN) {
    ++inbound_translation_failures_;
    discard_translation(Direction::Inbound);
    inbound_route_ = EMKE_AUDIO_ROUTE_ORIGINAL_FAIL_OPEN;
  }
}

void FakeAudioBackend::enter_outbound_fail_closed() {
  if (outbound_route_ != EMKE_AUDIO_ROUTE_MUTED_FAIL_CLOSED) {
    ++outbound_underruns_;
    discard_translation(Direction::Outbound);
    outbound_route_ = EMKE_AUDIO_ROUTE_MUTED_FAIL_CLOSED;
  }
}

emke_audio_status FakeAudioBackend::render_original(
    Direction direction,
    std::span<std::int16_t> destination) {
  const auto& original = direction == Direction::Inbound
                             ? latest_inbound_original_
                             : latest_outbound_original_;

  const std::size_t copied =
      std::min(destination.size(), original.size());
  std::copy_n(original.begin(), copied, destination.begin());
  std::fill(destination.begin() + static_cast<std::ptrdiff_t>(copied),
            destination.end(),
            0);
  return EMKE_AUDIO_OK;
}

emke_audio_status FakeAudioBackend::render_outbound_zeros(
    std::span<std::int16_t> destination) {
  std::fill(destination.begin(), destination.end(), 0);
  return EMKE_AUDIO_OK;
}

}  // namespace emke::audio
