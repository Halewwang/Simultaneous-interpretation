#include "audio_worker.hpp"

#include "pcm_converter.hpp"
#include "spsc_ring.hpp"

#include <algorithm>
#include <array>
#include <atomic>
#include <bit>
#include <chrono>
#include <cmath>
#include <cstring>
#include <limits>
#include <mutex>
#include <optional>
#include <thread>
#include <utility>
#include <vector>

namespace emke::audio {
namespace {

constexpr std::uint32_t speakerFrontLeft = 0x0000'0001u;
constexpr std::uint32_t speakerFrontRight = 0x0000'0002u;

bool valid_route(Direction direction, emke_audio_route route) noexcept {
  if (direction == Direction::Inbound) {
    return route == EMKE_AUDIO_ROUTE_TRANSLATED ||
           route == EMKE_AUDIO_ROUTE_ORIGINAL_FAIL_OPEN ||
           route == EMKE_AUDIO_ROUTE_ORIGINAL_BYPASS;
  }
  return route == EMKE_AUDIO_ROUTE_TRANSLATED ||
         route == EMKE_AUDIO_ROUTE_ORIGINAL_BYPASS ||
         route == EMKE_AUDIO_ROUTE_MUTED_FAIL_CLOSED;
}

float clamp_sample(float sample) noexcept {
  if (std::isnan(sample)) {
    return 0.0f;
  }
  return std::clamp(sample, -1.0f, 1.0f);
}

std::uint32_t read_little_u32(const std::byte* bytes) noexcept {
  return static_cast<std::uint32_t>(std::to_integer<std::uint8_t>(bytes[0])) |
         (static_cast<std::uint32_t>(
              std::to_integer<std::uint8_t>(bytes[1]))
          << 8u) |
         (static_cast<std::uint32_t>(
              std::to_integer<std::uint8_t>(bytes[2]))
          << 16u) |
         (static_cast<std::uint32_t>(
              std::to_integer<std::uint8_t>(bytes[3]))
          << 24u);
}

std::int32_t sign_extend_24(std::uint32_t value) noexcept {
  if ((value & 0x0080'0000u) != 0u) {
    value |= 0xff00'0000u;
  }
  return std::bit_cast<std::int32_t>(value);
}

float decode_sample(
    const std::byte* sample,
    NativeSampleType type,
    std::uint16_t valid_bits) noexcept {
  switch (type) {
    case NativeSampleType::ieeeFloat32:
      return clamp_sample(std::bit_cast<float>(read_little_u32(sample)));
    case NativeSampleType::pcm16: {
      const std::uint16_t bits =
          static_cast<std::uint16_t>(
              std::to_integer<std::uint8_t>(sample[0])) |
          static_cast<std::uint16_t>(
              static_cast<std::uint16_t>(
                  std::to_integer<std::uint8_t>(sample[1]))
              << 8u);
      const std::int16_t value = std::bit_cast<std::int16_t>(bits);
      return value == std::numeric_limits<std::int16_t>::min()
                 ? -1.0f
                 : static_cast<float>(value) / 32'767.0f;
    }
    case NativeSampleType::pcm24: {
      const std::uint32_t bits =
          static_cast<std::uint32_t>(
              std::to_integer<std::uint8_t>(sample[0])) |
          (static_cast<std::uint32_t>(
               std::to_integer<std::uint8_t>(sample[1]))
           << 8u) |
          (static_cast<std::uint32_t>(
               std::to_integer<std::uint8_t>(sample[2]))
           << 16u);
      return static_cast<float>(sign_extend_24(bits)) / 8'388'608.0f;
    }
    case NativeSampleType::pcm32: {
      std::int32_t value = std::bit_cast<std::int32_t>(
          read_little_u32(sample));
      const std::uint16_t effective_bits =
          valid_bits == 0u ? 32u : valid_bits;
      if (effective_bits < 32u) {
        value >>= (32u - effective_bits);
      }
      const double denominator =
          std::ldexp(1.0, static_cast<int>(effective_bits - 1u));
      return static_cast<float>(
          static_cast<double>(value) / denominator);
    }
  }
  return 0.0f;
}

std::size_t sample_bytes(NativeSampleType type) noexcept {
  switch (type) {
    case NativeSampleType::pcm16:
      return 2u;
    case NativeSampleType::pcm24:
      return 3u;
    case NativeSampleType::ieeeFloat32:
    case NativeSampleType::pcm32:
      return 4u;
  }
  return 0u;
}

class LocalOutputConverter {
 public:
  explicit LocalOutputConverter(AudioFormat format) noexcept
      : format_(format) {}

  [[nodiscard]] bool supported() const noexcept {
    return NativeFormatConverter(format_).supported();
  }

  [[nodiscard]] std::size_t required_frames(
      std::size_t local_frames) const noexcept {
    if (local_frames == 0u || format_.sample_rate_hz == 0u) {
      return 0u;
    }
    const double end =
        static_cast<double>(input_cursor_ + local_frames - 1u);
    if (next_output_position_ > end) {
      return 0u;
    }
    const double step =
        static_cast<double>(EMKE_AUDIO_LOCAL_SAMPLE_RATE_HZ) /
        static_cast<double>(format_.sample_rate_hz);
    return static_cast<std::size_t>(
               std::floor((end - next_output_position_) / step)) +
           1u;
  }

  [[nodiscard]] bool process(
      std::span<const float> local_stereo,
      std::span<std::byte> output,
      std::uint32_t& output_frames) noexcept {
    if (!supported() || local_stereo.size() % 2u != 0u) {
      return false;
    }
    const std::size_t local_frames = local_stereo.size() / 2u;
    const std::size_t required = required_frames(local_frames);
    const std::size_t required_bytes =
        required * bytes_per_frame(format_);
    if (required_bytes > output.size() ||
        required > std::numeric_limits<std::uint32_t>::max()) {
      return false;
    }

    std::size_t written = 0u;
    const double step =
        static_cast<double>(EMKE_AUDIO_LOCAL_SAMPLE_RATE_HZ) /
        static_cast<double>(format_.sample_rate_hz);
    for (std::size_t frame = 0u; frame < local_frames; ++frame) {
      const float current_left = clamp_sample(local_stereo[frame * 2u]);
      const float current_right =
          clamp_sample(local_stereo[frame * 2u + 1u]);
      const std::uint64_t current_index = input_cursor_;
      if (!has_previous_) {
        previous_left_ = current_left;
        previous_right_ = current_right;
        has_previous_ = true;
      }
      while (next_output_position_ <=
             static_cast<double>(current_index)) {
        const double alpha =
            current_index == 0u
                ? 1.0
                : std::clamp(
                      next_output_position_ -
                          static_cast<double>(current_index - 1u),
                      0.0,
                      1.0);
        const float left = static_cast<float>(
            static_cast<double>(previous_left_) * (1.0 - alpha) +
            static_cast<double>(current_left) * alpha);
        const float right = static_cast<float>(
            static_cast<double>(previous_right_) * (1.0 - alpha) +
            static_cast<double>(current_right) * alpha);
        encode_frame(output, written, left, right);
        ++written;
        next_output_position_ += step;
      }
      previous_left_ = current_left;
      previous_right_ = current_right;
      ++input_cursor_;
    }
    output_frames = static_cast<std::uint32_t>(written);
    return written == required;
  }

 private:
  void encode_integer(
      std::byte* destination,
      std::size_t bytes,
      std::int64_t value) const noexcept {
    for (std::size_t index = 0u; index < bytes; ++index) {
      destination[index] = static_cast<std::byte>(
          static_cast<std::uint64_t>(value) >> (index * 8u));
    }
  }

  void encode_sample(std::byte* destination, float sample) const noexcept {
    sample = clamp_sample(sample);
    switch (format_.sample_type) {
      case NativeSampleType::ieeeFloat32: {
        const std::uint32_t bits = std::bit_cast<std::uint32_t>(sample);
        encode_integer(destination, 4u, bits);
        break;
      }
      case NativeSampleType::pcm16: {
        const auto value = static_cast<std::int16_t>(std::lround(
            sample < 0.0f ? sample * 32'768.0f
                          : sample * 32'767.0f));
        encode_integer(destination, 2u, value);
        break;
      }
      case NativeSampleType::pcm24: {
        const auto value = static_cast<std::int32_t>(std::llround(
            sample < 0.0f ? sample * 8'388'608.0
                          : sample * 8'388'607.0));
        encode_integer(destination, 3u, value);
        break;
      }
      case NativeSampleType::pcm32: {
        const std::uint16_t bits =
            format_.valid_bits_per_sample == 0u
                ? 32u
                : format_.valid_bits_per_sample;
        const double negative_scale =
            std::ldexp(1.0, static_cast<int>(bits - 1u));
        const double positive_scale = negative_scale - 1.0;
        std::int64_t value = std::llround(
            sample < 0.0f ? sample * negative_scale
                          : sample * positive_scale);
        if (bits < 32u) {
          value <<= (32u - bits);
        }
        encode_integer(destination, 4u, value);
        break;
      }
    }
  }

  void encode_frame(
      std::span<std::byte> output,
      std::size_t frame,
      float left,
      float right) const noexcept {
    const std::size_t frame_offset =
        frame * bytes_per_frame(format_);
    std::byte* destination = output.data() + frame_offset;
    std::memset(destination, 0, bytes_per_frame(format_));
    const std::size_t width = sample_bytes(format_.sample_type);
    std::size_t left_index = 0u;
    std::size_t right_index =
        format_.channel_count == 1u ? 0u : 1u;
    if (format_.has_channel_mask) {
      std::size_t preceding = 0u;
      bool found_left = false;
      bool found_right = false;
      for (std::uint32_t bit = 1u; bit != 0u; bit <<= 1u) {
        if ((format_.channel_mask & bit) == 0u) {
          continue;
        }
        if (bit == speakerFrontLeft) {
          left_index = preceding;
          found_left = true;
        }
        if (bit == speakerFrontRight) {
          right_index = preceding;
          found_right = true;
        }
        ++preceding;
      }
      if (!found_left || !found_right ||
          left_index >= format_.channel_count ||
          right_index >= format_.channel_count) {
        left_index = 0u;
        right_index = format_.channel_count == 1u ? 0u : 1u;
      }
    }
    encode_sample(destination + left_index * width, left);
    if (format_.channel_count == 1u) {
      encode_sample(destination, (left + right) * 0.5f);
    } else {
      encode_sample(destination + right_index * width, right);
    }
  }

  AudioFormat format_;
  std::uint64_t input_cursor_ = 0u;
  double next_output_position_ = 0.0;
  float previous_left_ = 0.0f;
  float previous_right_ = 0.0f;
  bool has_previous_ = false;
};

struct PendingWorkerEvent {
  emke_audio_event_kind kind = EMKE_AUDIO_EVENT_NONE;
  emke_audio_status status = EMKE_AUDIO_OK;
  emke_audio_route route = EMKE_AUDIO_ROUTE_STOPPED;
  std::uint64_t sequence = 0u;
  std::uint32_t endpoint_role = 0u;
  std::int32_t native_code = 0;
  std::uint32_t frame_count = 0u;
  std::array<std::int16_t, networkBatchFrames> pcm16{};
};

class PendingEventRing {
 public:
  [[nodiscard]] bool push(const PendingWorkerEvent& event) noexcept {
    const std::size_t write = write_.load(std::memory_order_relaxed);
    const std::size_t read = read_.load(std::memory_order_acquire);
    if (write - read >= events_.size()) {
      return false;
    }
    events_[write % events_.size()] = event;
    write_.store(write + 1u, std::memory_order_release);
    return true;
  }

  [[nodiscard]] const PendingWorkerEvent* front() const noexcept {
    const std::size_t read = read_.load(std::memory_order_relaxed);
    const std::size_t write = write_.load(std::memory_order_acquire);
    return read == write ? nullptr : &events_[read % events_.size()];
  }

  void pop() noexcept {
    const std::size_t read = read_.load(std::memory_order_relaxed);
    if (read != write_.load(std::memory_order_acquire)) {
      read_.store(read + 1u, std::memory_order_release);
    }
  }

  void clear() noexcept {
    const std::size_t write = write_.load(std::memory_order_acquire);
    read_.store(write, std::memory_order_release);
  }

  [[nodiscard]] std::size_t size() const noexcept {
    return write_.load(std::memory_order_acquire) -
           read_.load(std::memory_order_acquire);
  }

 private:
  std::array<PendingWorkerEvent, pendingEventCapacity> events_{};
  alignas(64) std::atomic<std::size_t> read_{0u};
  alignas(64) std::atomic<std::size_t> write_{0u};
};

struct DirectionState {
  PcmEncoder encoder;
  std::array<std::uint8_t, localBlockFrames> encoded_bytes{};
  std::array<std::uint8_t, networkBatchBytes> batch_bytes{};
  std::size_t batch_size = 0u;
  SpscBlockRing captures{captureRingBlockCapacity};
  SpscBlockRing translations{translatedPlaybackRingBlockCapacity};
  PcmDecoder translation_decoder;
  std::vector<std::uint8_t> translation_bytes =
      std::vector<std::uint8_t>(
          EMKE_AUDIO_TRANSLATED_QUEUE_CAPACITY_NETWORK_FRAMES *
          sizeof(std::int16_t));
  std::vector<float> translation_float =
      std::vector<float>(
          EMKE_AUDIO_TRANSLATED_PLAYBACK_CAPACITY_LOCAL_FRAMES * 2u);
  std::atomic<std::uint64_t> queued_local_frames{0u};
  emke_audio_route requested_route = EMKE_AUDIO_ROUTE_TRANSLATED;
  std::uint64_t requested_generation = 1u;
  std::atomic<emke_audio_route> active_route{
      EMKE_AUDIO_ROUTE_STOPPED};
  std::uint64_t active_generation = 0u;
  std::optional<NativeFormatConverter> input_converter;
  PcmBlock current_translation{};
  std::size_t current_translation_offset = 0u;
  bool has_current_translation = false;
  std::mutex translation_mutex;
};

}  // namespace

NativeFormatConverter::NativeFormatConverter(AudioFormat format) noexcept
    : format_(format) {}

bool NativeFormatConverter::supported() const noexcept {
  if (format_.sample_rate_hz < 8'000u ||
      format_.sample_rate_hz > 192'000u ||
      format_.channel_count == 0u ||
      format_.channel_count > 32u ||
      format_.block_align == 0u) {
    return false;
  }
  const std::size_t width = sample_bytes(format_.sample_type);
  if (width == 0u ||
      static_cast<std::size_t>(format_.channel_count) * width !=
          format_.block_align) {
    return false;
  }
  switch (format_.sample_type) {
    case NativeSampleType::ieeeFloat32:
      return format_.bits_per_sample == 32u &&
             format_.valid_bits_per_sample == 32u;
    case NativeSampleType::pcm16:
      return format_.bits_per_sample == 16u &&
             (format_.valid_bits_per_sample == 0u ||
              format_.valid_bits_per_sample == 16u);
    case NativeSampleType::pcm24:
      return format_.bits_per_sample == 24u &&
             (format_.valid_bits_per_sample == 0u ||
              format_.valid_bits_per_sample == 24u);
    case NativeSampleType::pcm32:
      return format_.bits_per_sample == 32u &&
             format_.valid_bits_per_sample >= 16u &&
             format_.valid_bits_per_sample <= 32u;
  }
  return false;
}

std::size_t NativeFormatConverter::channel_index(
    std::uint32_t speaker_bit,
    std::size_t fallback) const noexcept {
  if (!format_.has_channel_mask ||
      (format_.channel_mask & speaker_bit) == 0u) {
    return fallback;
  }
  std::size_t index = 0u;
  for (std::uint32_t bit = 1u; bit != speaker_bit; bit <<= 1u) {
    if ((format_.channel_mask & bit) != 0u) {
      ++index;
    }
  }
  return index < format_.channel_count ? index : fallback;
}

bool NativeFormatConverter::decode_frame(
    std::span<const std::byte> bytes,
    std::size_t frame,
    float& left,
    float& right) const noexcept {
  const std::size_t width = sample_bytes(format_.sample_type);
  const std::size_t frame_offset =
      frame * static_cast<std::size_t>(format_.block_align);
  if (frame_offset > bytes.size() ||
      format_.block_align > bytes.size() - frame_offset) {
    return false;
  }
  const std::size_t left_index = channel_index(speakerFrontLeft, 0u);
  const std::size_t right_index = channel_index(
      speakerFrontRight, format_.channel_count == 1u ? 0u : 1u);
  if (left_index >= format_.channel_count ||
      right_index >= format_.channel_count) {
    return false;
  }
  left = decode_sample(
      bytes.data() + frame_offset + left_index * width,
      format_.sample_type,
      format_.valid_bits_per_sample);
  right = format_.channel_count == 1u
              ? left
              : decode_sample(
                    bytes.data() + frame_offset + right_index * width,
                    format_.sample_type,
                    format_.valid_bits_per_sample);
  return true;
}

std::size_t NativeFormatConverter::required_output_frames(
    std::uint32_t native_frames) const noexcept {
  if (!supported() || native_frames == 0u) {
    return 0u;
  }
  const double end = static_cast<double>(
      input_frame_cursor_ + native_frames - 1u);
  if (next_output_position_ > end) {
    return 0u;
  }
  const double step =
      static_cast<double>(format_.sample_rate_hz) /
      static_cast<double>(EMKE_AUDIO_LOCAL_SAMPLE_RATE_HZ);
  return static_cast<std::size_t>(
             std::floor((end - next_output_position_) / step)) +
         1u;
}

NativeFormatResult NativeFormatConverter::process(
    std::span<const std::byte> native_bytes,
    std::uint32_t native_frames,
    std::span<float> interleaved_stereo_48khz) noexcept {
  if (!supported()) {
    return {.status = NativeFormatStatus::invalidFormat};
  }
  const std::size_t expected =
      static_cast<std::size_t>(native_frames) * format_.block_align;
  if (native_frames == 0u || expected != native_bytes.size()) {
    return {.status = NativeFormatStatus::malformedPacket};
  }
  const std::size_t required = required_output_frames(native_frames);
  if (required > interleaved_stereo_48khz.size() / 2u) {
    return {.status = NativeFormatStatus::insufficientOutput};
  }

  std::size_t written = 0u;
  const double step =
      static_cast<double>(format_.sample_rate_hz) /
      static_cast<double>(EMKE_AUDIO_LOCAL_SAMPLE_RATE_HZ);
  for (std::uint32_t frame = 0u; frame < native_frames; ++frame) {
    float current_left = 0.0f;
    float current_right = 0.0f;
    if (!decode_frame(
            native_bytes, frame, current_left, current_right)) {
      return {.status = NativeFormatStatus::malformedPacket};
    }
    const std::uint64_t current_index = input_frame_cursor_;
    if (!has_previous_) {
      previous_left_ = current_left;
      previous_right_ = current_right;
      has_previous_ = true;
    }
    while (next_output_position_ <=
           static_cast<double>(current_index)) {
      const double alpha =
          current_index == 0u
              ? 1.0
              : std::clamp(
                    next_output_position_ -
                        static_cast<double>(current_index - 1u),
                    0.0,
                    1.0);
      const float left = static_cast<float>(
          static_cast<double>(previous_left_) * (1.0 - alpha) +
          static_cast<double>(current_left) * alpha);
      const float right = static_cast<float>(
          static_cast<double>(previous_right_) * (1.0 - alpha) +
          static_cast<double>(current_right) * alpha);
      interleaved_stereo_48khz[written * 2u] = left;
      interleaved_stereo_48khz[written * 2u + 1u] = right;
      ++written;
      next_output_position_ += step;
    }
    previous_left_ = current_left;
    previous_right_ = current_right;
    ++input_frame_cursor_;
  }
  return {
      .status = NativeFormatStatus::ok,
      .output_frames = written,
  };
}

void NativeFormatConverter::reset() noexcept {
  input_frame_cursor_ = 0u;
  next_output_position_ = 0.0;
  previous_left_ = 0.0f;
  previous_right_ = 0.0f;
  has_previous_ = false;
}

AudioPipelineLifecycle::AudioPipelineLifecycle(
    Startable& physical_output,
    Startable& app_microphone_render,
    Startable& app_speaker_capture,
    Startable& physical_microphone_capture,
    Startable& worker) noexcept
    : components_{
          &physical_output,
          &app_microphone_render,
          &app_speaker_capture,
          &physical_microphone_capture,
          &worker,
      } {}

emke_audio_status AudioPipelineLifecycle::start() noexcept {
  if (running_) {
    return EMKE_AUDIO_OK;
  }
  started_count_ = 0u;
  for (Startable* component : components_) {
    const emke_audio_status status = component->start();
    if (status != EMKE_AUDIO_OK) {
      while (started_count_ > 0u) {
        --started_count_;
        static_cast<void>(components_[started_count_]->stop());
      }
      return status;
    }
    ++started_count_;
  }
  running_ = true;
  return EMKE_AUDIO_OK;
}

emke_audio_status AudioPipelineLifecycle::stop() noexcept {
  if (!running_ && started_count_ == 0u) {
    return EMKE_AUDIO_OK;
  }
  emke_audio_status first_failure = EMKE_AUDIO_OK;
  while (started_count_ > 0u) {
    --started_count_;
    const emke_audio_status status = components_[started_count_]->stop();
    if (first_failure == EMKE_AUDIO_OK && status != EMKE_AUDIO_OK) {
      first_failure = status;
    }
  }
  running_ = false;
  return first_failure;
}

bool AudioPipelineLifecycle::running() const noexcept {
  return running_;
}

class AudioWorker::Impl {
 public:
  Impl(
      AudioStream& physical_output,
      AudioStream& app_microphone_render,
      AudioStream& app_speaker_capture,
      AudioStream& physical_microphone_capture)
      : physical_output_(physical_output),
        app_microphone_render_(app_microphone_render),
        app_speaker_capture_(app_speaker_capture),
        physical_microphone_capture_(physical_microphone_capture),
        normalized_scratch_(maxNormalizedPacketFrames * 2u),
        physical_output_bytes_(rawPacketByteCapacity) {}

  emke_audio_status start() {
    if (running_.load(std::memory_order_acquire)) {
      return EMKE_AUDIO_OK;
    }
    if (worker_thread_.joinable()) {
      worker_thread_.join();
    }
    prepare_direction_for_start(inbound_);
    prepare_direction_for_start(outbound_);
    physical_output_converter_.reset();
    app_microphone_converter_.reset();
    events_.clear();
    reported_stream_failures_.fill(StreamFailure{});
    stop_requested_.store(false, std::memory_order_release);
    running_.store(true, std::memory_order_release);
    try {
      worker_thread_ = std::thread([this] {
        while (!stop_requested_.load(std::memory_order_acquire)) {
          if (publish_stream_failures()) {
            break;
          }
          if (!process_once()) {
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
          }
        }
        running_.store(false, std::memory_order_release);
      });
    } catch (...) {
      running_.store(false, std::memory_order_release);
      return EMKE_AUDIO_INTERNAL_ERROR;
    }
    return EMKE_AUDIO_OK;
  }

  emke_audio_status stop() {
    stop_requested_.store(true, std::memory_order_release);
    if (worker_thread_.joinable()) {
      worker_thread_.join();
    }
    running_.store(false, std::memory_order_release);
    reset_direction_after_stop(inbound_);
    reset_direction_after_stop(outbound_);
    physical_output_converter_.reset();
    app_microphone_converter_.reset();
    events_.clear();
    reported_stream_failures_.fill(StreamFailure{});
    return EMKE_AUDIO_OK;
  }

  [[nodiscard]] bool running() const noexcept {
    return running_.load(std::memory_order_acquire);
  }

  [[nodiscard]] bool process_once() noexcept {
    if (publish_stream_failures()) {
      running_.store(false, std::memory_order_release);
      return true;
    }
    bool processed = process_capture(
        Direction::Inbound,
        app_speaker_capture_,
        physical_output_,
        inbound_);
    processed =
        process_capture(
            Direction::Outbound,
            physical_microphone_capture_,
            app_microphone_render_,
            outbound_) ||
        processed;
    return processed;
  }

  emke_audio_status set_route(
      Direction direction,
      emke_audio_route route) noexcept {
    if (!valid_route(direction, route)) {
      return EMKE_AUDIO_INVALID_ARGUMENT;
    }
    DirectionState& current = state(direction);
    const std::lock_guard translation_lock(current.translation_mutex);
    if (current.requested_route == route) {
      return EMKE_AUDIO_OK;
    }
    if (current.requested_route == EMKE_AUDIO_ROUTE_TRANSLATED &&
        route != EMKE_AUDIO_ROUTE_TRANSLATED) {
      discard_translation_generation(current);
    }
    current.requested_route = route;
    return EMKE_AUDIO_OK;
  }

  emke_audio_status enqueue_translation(
      Direction direction,
      std::span<const std::int16_t> pcm16) noexcept {
    if (pcm16.empty()) {
      return EMKE_AUDIO_INVALID_ARGUMENT;
    }
    if (pcm16.size() >
        EMKE_AUDIO_TRANSLATED_QUEUE_CAPACITY_NETWORK_FRAMES) {
      dropped_frames_.fetch_add(pcm16.size(), std::memory_order_relaxed);
      queue_full_events_.fetch_add(1u, std::memory_order_relaxed);
      return EMKE_AUDIO_QUEUE_FULL;
    }
    DirectionState& current = state(direction);
    const std::lock_guard translation_lock(current.translation_mutex);

    const std::size_t local_frames = pcm16.size() * 2u;
    const std::uint64_t queued =
        current.queued_local_frames.load(std::memory_order_acquire);
    const std::size_t blocks =
        (local_frames + localBlockFrames - 1u) / localBlockFrames;
    if (queued + local_frames >
            EMKE_AUDIO_TRANSLATED_PLAYBACK_CAPACITY_LOCAL_FRAMES ||
        current.translations.remaining_capacity() < blocks) {
      dropped_frames_.fetch_add(pcm16.size(), std::memory_order_relaxed);
      queue_full_events_.fetch_add(1u, std::memory_order_relaxed);
      return EMKE_AUDIO_QUEUE_FULL;
    }

    for (std::size_t index = 0u; index < pcm16.size(); ++index) {
      const std::uint16_t bits =
          std::bit_cast<std::uint16_t>(pcm16[index]);
      current.translation_bytes[index * 2u] =
          static_cast<std::uint8_t>(bits & 0xffu);
      current.translation_bytes[index * 2u + 1u] =
          static_cast<std::uint8_t>((bits >> 8u) & 0xffu);
    }
    const PcmConversionResult conversion =
        current.translation_decoder.process(
            {current.translation_bytes.data(), pcm16.size() * 2u},
            {current.translation_float.data(), local_frames * 2u});
    if (conversion.status != PcmConversionStatus::ok ||
        conversion.output_count != local_frames * 2u) {
      return EMKE_AUDIO_INTERNAL_ERROR;
    }

    std::size_t frame_offset = 0u;
    while (frame_offset < local_frames) {
      const std::size_t count =
          std::min(localBlockFrames, local_frames - frame_offset);
      PcmBlock block;
      block.frame_count = static_cast<std::uint32_t>(count);
      block.timestamp = 0u;
      std::copy_n(
          current.translation_float.begin() +
              static_cast<std::ptrdiff_t>(frame_offset * 2u),
          count * 2u,
          block.interleaved_stereo.begin());
      if (!current.translations.push(block)) {
        return EMKE_AUDIO_INTERNAL_ERROR;
      }
      frame_offset += count;
    }
    current.queued_local_frames.fetch_add(
        local_frames, std::memory_order_release);
    return EMKE_AUDIO_OK;
  }

  emke_audio_status poll_event(
      AudioEvent& event,
      std::size_t capacity) {
    const PendingWorkerEvent* pending = events_.front();
    if (pending == nullptr) {
      event = {};
      return running() ? EMKE_AUDIO_OK : EMKE_AUDIO_NOT_RUNNING;
    }
    event.kind = pending->kind;
    event.status = pending->status;
    event.route = pending->route;
    event.sequence = pending->sequence;
    event.endpoint_role = pending->endpoint_role;
    event.native_code = pending->native_code;
    if (pending->frame_count > capacity) {
      event.pcm16.assign(
          pending->pcm16.begin(),
          pending->pcm16.begin() +
              static_cast<std::ptrdiff_t>(pending->frame_count));
      return EMKE_AUDIO_INVALID_ARGUMENT;
    }
    event.pcm16.assign(
        pending->pcm16.begin(),
        pending->pcm16.begin() +
            static_cast<std::ptrdiff_t>(pending->frame_count));
    events_.pop();
    return EMKE_AUDIO_OK;
  }

  void write_diagnostics(
      emke_audio_diagnostics& diagnostics) const noexcept {
    diagnostics = {};
    diagnostics.size = sizeof(diagnostics);
    diagnostics.abi_version = EMKE_AUDIO_ABI_VERSION;
    diagnostics.is_running = running() ? 1u : 0u;
    diagnostics.inbound_route =
        inbound_.active_route.load(std::memory_order_acquire);
    diagnostics.outbound_route =
        outbound_.active_route.load(std::memory_order_acquire);
    diagnostics.queued_inbound_translation_frames =
        static_cast<std::uint32_t>(
            inbound_.queued_local_frames.load(std::memory_order_acquire) /
            2u);
    diagnostics.queued_outbound_translation_frames =
        static_cast<std::uint32_t>(
            outbound_.queued_local_frames.load(std::memory_order_acquire) /
            2u);
    diagnostics.captured_inbound_frames =
        captured_inbound_frames_.load(std::memory_order_relaxed);
    diagnostics.captured_outbound_frames =
        captured_outbound_frames_.load(std::memory_order_relaxed);
    diagnostics.consumed_inbound_translation_frames =
        consumed_inbound_frames_.load(std::memory_order_relaxed);
    diagnostics.consumed_outbound_translation_frames =
        consumed_outbound_frames_.load(std::memory_order_relaxed);
    diagnostics.dropped_frames =
        dropped_frames_.load(std::memory_order_relaxed);
    diagnostics.queue_full_events =
        queue_full_events_.load(std::memory_order_relaxed);
    diagnostics.outbound_underruns =
        outbound_underruns_.load(std::memory_order_relaxed);
    diagnostics.inbound_translation_failures =
        inbound_failures_.load(std::memory_order_relaxed);
    diagnostics.device_failures =
        device_failures_.load(std::memory_order_relaxed);
  }

 private:
  [[nodiscard]] DirectionState& state(Direction direction) noexcept {
    return direction == Direction::Inbound ? inbound_ : outbound_;
  }

  void prepare_direction_for_start(DirectionState& state) noexcept {
    const std::lock_guard translation_lock(state.translation_mutex);
    state.encoder.reset();
    state.batch_size = 0u;
    state.captures.clear();
    state.input_converter.reset();
    state.active_route.store(
        state.requested_route, std::memory_order_release);
    state.active_generation = state.requested_generation;
  }

  void reset_direction_after_stop(DirectionState& state) noexcept {
    const std::lock_guard translation_lock(state.translation_mutex);
    state.encoder.reset();
    state.batch_size = 0u;
    state.captures.clear();
    state.input_converter.reset();
    state.translations.clear();
    state.translation_decoder.reset();
    state.has_current_translation = false;
    state.current_translation_offset = 0u;
    state.queued_local_frames.store(0u, std::memory_order_release);
    if (state.requested_generation !=
        std::numeric_limits<std::uint64_t>::max()) {
      ++state.requested_generation;
    }
    state.active_route.store(
        EMKE_AUDIO_ROUTE_STOPPED, std::memory_order_release);
    state.active_generation = 0u;
  }

  void discard_translation_generation(DirectionState& state) noexcept {
    const std::uint64_t discarded =
        state.queued_local_frames.exchange(0u, std::memory_order_acq_rel);
    state.translations.clear();
    state.translation_decoder.reset();
    state.has_current_translation = false;
    state.current_translation_offset = 0u;
    if (state.requested_generation !=
        std::numeric_limits<std::uint64_t>::max()) {
      ++state.requested_generation;
    }
    dropped_frames_.fetch_add(discarded / 2u, std::memory_order_relaxed);
  }

  [[nodiscard]] bool ensure_converter(
      DirectionState& state,
      const AudioFormat& format) noexcept {
    if (!state.input_converter.has_value()) {
      state.input_converter.emplace(format);
    }
    return state.input_converter->supported();
  }

  void apply_route_boundary(DirectionState& state) noexcept {
    const emke_audio_route requested = state.requested_route;
    const emke_audio_route active =
        state.active_route.load(std::memory_order_acquire);
    if (requested == active &&
        state.requested_generation == state.active_generation) {
      return;
    }
    state.active_route.store(requested, std::memory_order_release);
    state.active_generation = state.requested_generation;
  }

  void enter_translation_failure(
      Direction direction,
      DirectionState& state) noexcept {
    discard_translation_generation(state);
    if (direction == Direction::Inbound) {
      state.requested_route = EMKE_AUDIO_ROUTE_ORIGINAL_FAIL_OPEN;
      state.active_route.store(
          EMKE_AUDIO_ROUTE_ORIGINAL_FAIL_OPEN,
          std::memory_order_release);
      inbound_failures_.fetch_add(1u, std::memory_order_relaxed);
    } else {
      state.requested_route = EMKE_AUDIO_ROUTE_MUTED_FAIL_CLOSED;
      state.active_route.store(
          EMKE_AUDIO_ROUTE_MUTED_FAIL_CLOSED,
          std::memory_order_release);
      outbound_underruns_.fetch_add(1u, std::memory_order_relaxed);
    }
    state.active_generation = state.requested_generation;
  }

  [[nodiscard]] bool publish_stream_failures() noexcept {
    const std::array<AudioStream*, 4u> streams = {
        &physical_output_,
        &app_microphone_render_,
        &app_speaker_capture_,
        &physical_microphone_capture_,
    };
    bool any_failure = false;
    for (std::size_t index = 0u; index < streams.size(); ++index) {
      const StreamFailure failure = streams[index]->last_failure();
      if (failure.operation == StreamFailure::Operation::none) {
        continue;
      }
      any_failure = true;
      const StreamFailure& reported = reported_stream_failures_[index];
      if (reported.operation == failure.operation &&
          reported.native_code == failure.native_code) {
        continue;
      }
      reported_stream_failures_[index] = failure;
      device_failures_.fetch_add(1u, std::memory_order_relaxed);

      PendingWorkerEvent event;
      event.kind = EMKE_AUDIO_EVENT_STREAM_ERROR;
      event.status = EMKE_AUDIO_INTERNAL_ERROR;
      event.route =
          failure.role == StreamRole::physicalOutput ||
                  failure.role == StreamRole::appSpeakerCapture
              ? inbound_.active_route.load(std::memory_order_acquire)
              : outbound_.active_route.load(std::memory_order_acquire);
      event.endpoint_role = static_cast<std::uint32_t>(failure.role);
      event.native_code = failure.native_code;
      if (next_event_sequence_ ==
          std::numeric_limits<std::uint64_t>::max()) {
        queue_full_events_.fetch_add(1u, std::memory_order_relaxed);
        continue;
      }
      event.sequence = next_event_sequence_++;
      if (!events_.push(event)) {
        queue_full_events_.fetch_add(1u, std::memory_order_relaxed);
      }
    }
    return any_failure;
  }

  bool process_capture(
      Direction direction,
      AudioStream& capture,
      AudioStream& render,
      DirectionState& state) noexcept {
    RawAudioPacket packet;
    if (!capture.input_packets().pop(packet)) {
      return false;
    }
    if (!ensure_converter(state, packet.format)) {
      device_failures_.fetch_add(1u, std::memory_order_relaxed);
      dropped_frames_.fetch_add(
          network_frames_for_native(
              packet.frame_count, packet.format.sample_rate_hz),
          std::memory_order_relaxed);
      return true;
    }
    const std::size_t required =
        state.input_converter->required_output_frames(packet.frame_count);
    if (required == 0u || required > maxNormalizedPacketFrames) {
      dropped_frames_.fetch_add(
          network_frames_for_native(
              packet.frame_count, packet.format.sample_rate_hz),
          std::memory_order_relaxed);
      queue_full_events_.fetch_add(1u, std::memory_order_relaxed);
      return true;
    }
    const NativeFormatResult result = state.input_converter->process(
        {packet.bytes.data(), packet.byte_count},
        packet.frame_count,
        {normalized_scratch_.data(), required * 2u});
    if (result.status != NativeFormatStatus::ok) {
      device_failures_.fetch_add(1u, std::memory_order_relaxed);
      return true;
    }

    std::size_t offset = 0u;
    while (offset < result.output_frames) {
      const std::size_t frames =
          std::min(localBlockFrames, result.output_frames - offset);
      PcmBlock captured;
      captured.frame_count = static_cast<std::uint32_t>(frames);
      captured.timestamp = packet.timestamp;
      std::copy_n(
          normalized_scratch_.begin() +
              static_cast<std::ptrdiff_t>(offset * 2u),
          frames * 2u,
          captured.interleaved_stereo.begin());
      if (!state.captures.push(captured)) {
        queue_full_events_.fetch_add(1u, std::memory_order_relaxed);
        dropped_frames_.fetch_add(
            frames / 2u, std::memory_order_relaxed);
        offset += frames;
        continue;
      }
      PcmBlock ready;
      if (state.captures.pop(ready)) {
        process_block(
            direction,
            render,
            state,
            {ready.interleaved_stereo.data(),
             static_cast<std::size_t>(ready.frame_count) * 2u},
            ready.timestamp);
      }
      offset += frames;
    }
    return true;
  }

  void process_block(
      Direction direction,
      AudioStream& render,
      DirectionState& state,
      std::span<const float> original,
      std::uint64_t timestamp) noexcept {
    const std::size_t frames = original.size() / 2u;
    PcmBlock selected;
    selected.frame_count = static_cast<std::uint32_t>(frames);
    selected.timestamp = timestamp;
    const std::unique_lock translation_lock(
        state.translation_mutex, std::try_to_lock);
    if (!translation_lock.owns_lock()) {
      if (direction == Direction::Inbound) {
        std::copy(
            original.begin(),
            original.end(),
            selected.interleaved_stereo.begin());
        inbound_failures_.fetch_add(1u, std::memory_order_relaxed);
      } else {
        std::fill_n(
            selected.interleaved_stereo.begin(), original.size(), 0.0f);
        outbound_underruns_.fetch_add(1u, std::memory_order_relaxed);
      }
      queue_full_events_.fetch_add(1u, std::memory_order_relaxed);
      publish_network_audio(direction, state, original);
      write_render_packet(render, selected);
      return;
    }
    apply_route_boundary(state);
    publish_network_audio(direction, state, original);
    const emke_audio_route route =
        state.active_route.load(std::memory_order_acquire);
    if (route == EMKE_AUDIO_ROUTE_ORIGINAL_BYPASS ||
        route == EMKE_AUDIO_ROUTE_ORIGINAL_FAIL_OPEN) {
      std::copy(original.begin(), original.end(),
                selected.interleaved_stereo.begin());
    } else if (
        route == EMKE_AUDIO_ROUTE_MUTED_FAIL_CLOSED) {
      std::fill_n(
          selected.interleaved_stereo.begin(), original.size(), 0.0f);
    } else {
      if (take_translation(state, frames, selected)) {
        if (direction == Direction::Inbound) {
          consumed_inbound_frames_.fetch_add(
              frames / 2u, std::memory_order_relaxed);
        } else {
          consumed_outbound_frames_.fetch_add(
              frames / 2u, std::memory_order_relaxed);
        }
      } else if (direction == Direction::Inbound) {
        std::copy(original.begin(), original.end(),
                  selected.interleaved_stereo.begin());
        enter_translation_failure(direction, state);
      } else {
        std::fill_n(
            selected.interleaved_stereo.begin(), original.size(), 0.0f);
        enter_translation_failure(direction, state);
      }
    }
    write_render_packet(render, selected);
  }

  [[nodiscard]] bool take_translation(
      DirectionState& state,
      std::size_t frames,
      PcmBlock& output) noexcept {
    if (state.queued_local_frames.load(std::memory_order_acquire) < frames) {
      return false;
    }
    output.frame_count = static_cast<std::uint32_t>(frames);
    std::size_t output_offset = 0u;
    while (output_offset < frames) {
      if (!state.has_current_translation) {
        if (!state.translations.pop(state.current_translation)) {
          return false;
        }
        state.current_translation_offset = 0u;
        state.has_current_translation = true;
      }
      const std::size_t available =
          state.current_translation.frame_count -
          state.current_translation_offset;
      const std::size_t copied =
          std::min(available, frames - output_offset);
      std::copy_n(
          state.current_translation.interleaved_stereo.begin() +
              static_cast<std::ptrdiff_t>(
                  state.current_translation_offset * 2u),
          copied * 2u,
          output.interleaved_stereo.begin() +
              static_cast<std::ptrdiff_t>(output_offset * 2u));
      output_offset += copied;
      state.current_translation_offset += copied;
      if (state.current_translation_offset ==
          state.current_translation.frame_count) {
        state.has_current_translation = false;
      }
    }
    state.queued_local_frames.fetch_sub(
        frames, std::memory_order_acq_rel);
    return true;
  }

  void publish_network_audio(
      Direction direction,
      DirectionState& state,
      std::span<const float> original) noexcept {
    const PcmConversionResult conversion = state.encoder.process(
        original, state.encoded_bytes);
    if (conversion.status != PcmConversionStatus::ok) {
      device_failures_.fetch_add(1u, std::memory_order_relaxed);
      return;
    }
    const std::uint64_t produced_frames = conversion.output_count / 2u;
    if (direction == Direction::Inbound) {
      captured_inbound_frames_.fetch_add(
          produced_frames, std::memory_order_relaxed);
    } else {
      captured_outbound_frames_.fetch_add(
          produced_frames, std::memory_order_relaxed);
    }

    std::size_t input_offset = 0u;
    while (input_offset < conversion.output_count) {
      const std::size_t copied = std::min(
          conversion.output_count - input_offset,
          state.batch_bytes.size() - state.batch_size);
      std::copy_n(
          state.encoded_bytes.begin() +
              static_cast<std::ptrdiff_t>(input_offset),
          copied,
          state.batch_bytes.begin() +
              static_cast<std::ptrdiff_t>(state.batch_size));
      input_offset += copied;
      state.batch_size += copied;
      if (state.batch_size != networkBatchBytes) {
        continue;
      }

      PendingWorkerEvent event;
      event.kind = direction == Direction::Inbound
                       ? EMKE_AUDIO_EVENT_INBOUND_PCM16
                       : EMKE_AUDIO_EVENT_OUTBOUND_PCM16;
      event.status = EMKE_AUDIO_OK;
      event.route = state.active_route.load(std::memory_order_acquire);
      event.frame_count = networkBatchFrames;
      if (next_event_sequence_ ==
          std::numeric_limits<std::uint64_t>::max()) {
        queue_full_events_.fetch_add(1u, std::memory_order_relaxed);
        dropped_frames_.fetch_add(
            networkBatchFrames, std::memory_order_relaxed);
        state.batch_size = 0u;
        continue;
      }
      event.sequence = next_event_sequence_++;
      for (std::size_t index = 0u; index < networkBatchFrames; ++index) {
        const std::uint16_t bits =
            static_cast<std::uint16_t>(
                state.batch_bytes[index * 2u]) |
            static_cast<std::uint16_t>(
                static_cast<std::uint16_t>(
                    state.batch_bytes[index * 2u + 1u])
                << 8u);
        event.pcm16[index] = std::bit_cast<std::int16_t>(bits);
      }
      if (!events_.push(event)) {
        queue_full_events_.fetch_add(1u, std::memory_order_relaxed);
        dropped_frames_.fetch_add(
            networkBatchFrames, std::memory_order_relaxed);
      }
      state.batch_size = 0u;
    }
  }

  void write_render_packet(
      AudioStream& render,
      const PcmBlock& block) noexcept {
    LocalOutputConverter* converter = nullptr;
    if (&render == &physical_output_) {
      if (!physical_output_converter_.has_value()) {
        physical_output_converter_.emplace(render.format());
      }
      converter = &*physical_output_converter_;
    } else {
      if (!app_microphone_converter_.has_value()) {
        app_microphone_converter_.emplace(render.format());
      }
      converter = &*app_microphone_converter_;
    }
    if (!converter->supported()) {
      device_failures_.fetch_add(1u, std::memory_order_relaxed);
      return;
    }
    std::uint32_t output_frames = 0u;
    if (!converter->process(
            {block.interleaved_stereo.data(),
             static_cast<std::size_t>(block.frame_count) * 2u},
            physical_output_bytes_,
            output_frames)) {
      device_failures_.fetch_add(1u, std::memory_order_relaxed);
      return;
    }
    const std::size_t byte_count =
        static_cast<std::size_t>(output_frames) *
        bytes_per_frame(render.format());
    if (!render.output_packets().push(
            render.format(),
            output_frames,
            block.timestamp,
            {physical_output_bytes_.data(), byte_count})) {
      queue_full_events_.fetch_add(1u, std::memory_order_relaxed);
      dropped_frames_.fetch_add(
          block.frame_count / 2u, std::memory_order_relaxed);
    }
  }

  AudioStream& physical_output_;
  AudioStream& app_microphone_render_;
  AudioStream& app_speaker_capture_;
  AudioStream& physical_microphone_capture_;
  DirectionState inbound_;
  DirectionState outbound_;
  PendingEventRing events_;
  std::vector<float> normalized_scratch_;
  std::vector<std::byte> physical_output_bytes_;
  std::optional<LocalOutputConverter> physical_output_converter_;
  std::optional<LocalOutputConverter> app_microphone_converter_;
  std::array<StreamFailure, 4u> reported_stream_failures_{};
  std::thread worker_thread_;
  std::atomic<bool> running_{false};
  std::atomic<bool> stop_requested_{false};
  std::uint64_t next_event_sequence_ = 1u;
  std::atomic<std::uint64_t> captured_inbound_frames_{0u};
  std::atomic<std::uint64_t> captured_outbound_frames_{0u};
  std::atomic<std::uint64_t> consumed_inbound_frames_{0u};
  std::atomic<std::uint64_t> consumed_outbound_frames_{0u};
  std::atomic<std::uint64_t> dropped_frames_{0u};
  std::atomic<std::uint64_t> queue_full_events_{0u};
  std::atomic<std::uint64_t> outbound_underruns_{0u};
  std::atomic<std::uint64_t> inbound_failures_{0u};
  std::atomic<std::uint64_t> device_failures_{0u};
};

AudioWorker::AudioWorker(
    AudioStream& physical_output,
    AudioStream& app_microphone_render,
    AudioStream& app_speaker_capture,
    AudioStream& physical_microphone_capture)
    : impl_(std::make_unique<Impl>(
          physical_output,
          app_microphone_render,
          app_speaker_capture,
          physical_microphone_capture)) {}

AudioWorker::~AudioWorker() {
  static_cast<void>(impl_->stop());
}

emke_audio_status AudioWorker::start() {
  return impl_->start();
}

emke_audio_status AudioWorker::stop() {
  return impl_->stop();
}

bool AudioWorker::running() const noexcept {
  return impl_->running();
}

bool AudioWorker::process_once() noexcept {
  return impl_->process_once();
}

emke_audio_status AudioWorker::set_route(
    Direction direction,
    emke_audio_route route) noexcept {
  return impl_->set_route(direction, route);
}

emke_audio_status AudioWorker::enqueue_translation(
    Direction direction,
    std::span<const std::int16_t> pcm16) noexcept {
  return impl_->enqueue_translation(direction, pcm16);
}

emke_audio_status AudioWorker::poll_event(
    AudioEvent& event,
    std::size_t pcm_capacity_network_frames) {
  return impl_->poll_event(event, pcm_capacity_network_frames);
}

void AudioWorker::write_diagnostics(
    emke_audio_diagnostics& diagnostics) const noexcept {
  impl_->write_diagnostics(diagnostics);
}

class NativeAudioBackend::Impl {
 public:
  explicit Impl(const emke_audio_config& config)
      : physical_output_(
            StreamRole::physicalOutput,
            StreamDirection::render,
            config.physical_output_endpoint_id,
            false),
        app_microphone_render_(
            StreamRole::appMicrophoneRender,
            StreamDirection::render,
            config.virtual_microphone_render_endpoint_id,
            true),
        app_speaker_capture_(
            StreamRole::appSpeakerCapture,
            StreamDirection::capture,
            config.virtual_speaker_capture_endpoint_id,
            true),
        physical_microphone_capture_(
            StreamRole::physicalMicrophoneCapture,
            StreamDirection::capture,
            config.physical_input_endpoint_id,
            false),
        worker_(
            physical_output_,
            app_microphone_render_,
            app_speaker_capture_,
            physical_microphone_capture_),
        lifecycle_(
            physical_output_,
            app_microphone_render_,
            app_speaker_capture_,
            physical_microphone_capture_,
            worker_) {}

  [[nodiscard]] bool has_stream_failure() const noexcept {
    const std::array<const WasapiStream*, 4u> streams = {
        &physical_output_,
        &app_microphone_render_,
        &app_speaker_capture_,
        &physical_microphone_capture_,
    };
    return std::any_of(
        streams.begin(), streams.end(), [](const WasapiStream* stream) {
          return stream->last_failure().operation !=
                 StreamFailure::Operation::none;
        });
  }

  [[nodiscard]] bool healthy_running() const noexcept {
    return lifecycle_.running() && worker_.running() &&
           !has_stream_failure();
  }

  WasapiStream physical_output_;
  WasapiStream app_microphone_render_;
  WasapiStream app_speaker_capture_;
  WasapiStream physical_microphone_capture_;
  AudioWorker worker_;
  AudioPipelineLifecycle lifecycle_;
};

NativeAudioBackend::NativeAudioBackend(const emke_audio_config& config)
    : impl_(std::make_unique<Impl>(config)) {}

NativeAudioBackend::~NativeAudioBackend() {
  static_cast<void>(impl_->lifecycle_.stop());
}

emke_audio_status NativeAudioBackend::start() {
  if (impl_->lifecycle_.running()) {
    if (impl_->healthy_running()) {
      return EMKE_AUDIO_OK;
    }
    static_cast<void>(impl_->lifecycle_.stop());
  }
  return impl_->lifecycle_.start();
}

emke_audio_status NativeAudioBackend::stop() {
  return impl_->lifecycle_.stop();
}

emke_audio_status NativeAudioBackend::set_route(
    Direction direction,
    emke_audio_route route) {
  if (impl_->lifecycle_.running() && !impl_->healthy_running()) {
    return EMKE_AUDIO_INTERNAL_ERROR;
  }
  return impl_->worker_.set_route(direction, route);
}

emke_audio_status NativeAudioBackend::enqueue_translation(
    Direction direction,
    std::span<const std::int16_t> pcm16) {
  if (impl_->lifecycle_.running() && !impl_->healthy_running()) {
    return EMKE_AUDIO_INTERNAL_ERROR;
  }
  return impl_->worker_.enqueue_translation(direction, pcm16);
}

emke_audio_status NativeAudioBackend::poll_event(
    AudioEvent& event,
    std::size_t pcm_capacity_network_frames) {
  return impl_->worker_.poll_event(event, pcm_capacity_network_frames);
}

void NativeAudioBackend::write_diagnostics(
    emke_audio_diagnostics& diagnostics) const {
  impl_->worker_.write_diagnostics(diagnostics);
  diagnostics.dropped_frames +=
      impl_->physical_output_.dropped_network_frames() +
      impl_->app_microphone_render_.dropped_network_frames() +
      impl_->app_speaker_capture_.dropped_network_frames() +
      impl_->physical_microphone_capture_.dropped_network_frames();
  diagnostics.queue_full_events +=
      impl_->physical_output_.queue_full_events() +
      impl_->app_microphone_render_.queue_full_events() +
      impl_->app_speaker_capture_.queue_full_events() +
      impl_->physical_microphone_capture_.queue_full_events();
  const std::array streams = {
      &impl_->physical_output_,
      &impl_->app_microphone_render_,
      &impl_->app_speaker_capture_,
      &impl_->physical_microphone_capture_,
  };
  std::uint64_t current_stream_failures = 0u;
  for (const WasapiStream* stream : streams) {
    if (stream->last_failure().operation !=
        StreamFailure::Operation::none) {
      ++current_stream_failures;
    }
  }
  diagnostics.device_failures =
      std::max(diagnostics.device_failures, current_stream_failures);
  if (!impl_->healthy_running()) {
    diagnostics.is_running = 0u;
  }
}

}  // namespace emke::audio
