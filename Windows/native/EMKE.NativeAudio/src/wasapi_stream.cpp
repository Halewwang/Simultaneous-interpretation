#include "wasapi_stream.hpp"
#include "virtual_audio_format.hpp"

#include <algorithm>
#include <bit>
#include <cassert>
#include <cstring>
#include <limits>
#include <new>
#include <optional>
#include <string>
#include <thread>
#include <utility>

#if defined(_WIN32)
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <audioclient.h>
#include <ksmedia.h>
#include <mmdeviceapi.h>

#include <cwchar>
#endif

namespace emke::audio {
namespace {

#if defined(EMKE_NATIVE_AUDIO_REALTIME_INSTRUMENTATION) || !defined(NDEBUG)
thread_local bool realtime_scope_active = false;
std::atomic<std::uint64_t> allocation_violation_count{0u};
std::atomic<std::uint64_t> blocking_violation_count{0u};
#define EMKE_REALTIME_CALLBACK_SCOPE(name) \
  RealtimeInstrumentation::Scope name
#else
#define EMKE_REALTIME_CALLBACK_SCOPE(name) static_cast<void>(0)
#endif

StreamFailure::Operation operation_for_step(std::size_t step) noexcept {
  constexpr std::array operations = {
      StreamFailure::Operation::activateClient3,
      StreamFailure::Operation::activateClient,
      StreamFailure::Operation::getFormat,
      StreamFailure::Operation::createEvents,
      StreamFailure::Operation::getEnginePeriod,
      StreamFailure::Operation::initializeClient3,
      StreamFailure::Operation::initializeClient,
      StreamFailure::Operation::setEventHandle,
      StreamFailure::Operation::getService,
      StreamFailure::Operation::prepareLoop,
      StreamFailure::Operation::startClient,
  };
  return step < operations.size() ? operations[step]
                                  : StreamFailure::Operation::none;
}

}  // namespace

bool is_exact_virtual_format(const AudioFormat& format) noexcept {
  return matches_virtual_audio_format({
      .sample_rate_hz = format.sample_rate_hz,
      .channel_count = format.channel_count,
      .bits_per_sample = format.bits_per_sample,
      .valid_bits_per_sample = format.valid_bits_per_sample,
      .block_align = format.block_align,
      .average_bytes_per_second =
          format.sample_rate_hz * format.block_align,
      .format_tag = static_cast<std::uint16_t>(
          format.sample_type == NativeSampleType::ieeeFloat32
              ? EMKE_AUDIO_FORMAT_TAG
              : 0u),
  });
}

std::size_t bytes_per_frame(const AudioFormat& format) noexcept {
  if (format.channel_count == 0u || format.block_align == 0u) {
    return 0u;
  }
  return format.block_align;
}

bool stream_format_fits_fixed_storage(
    const AudioFormat& format,
    std::uint32_t native_buffer_frames,
    StreamDirection direction) noexcept {
  const std::size_t frame_bytes = bytes_per_frame(format);
  if (format.sample_rate_hz < 8'000u ||
      format.sample_rate_hz > 192'000u || frame_bytes == 0u ||
      native_buffer_frames == 0u) {
    return false;
  }

  if (direction == StreamDirection::capture) {
    if (native_buffer_frames > nativePacketFrameCapacity ||
        native_buffer_frames >
            rawPacketByteCapacity / frame_bytes) {
      return false;
    }
    const std::uint64_t normalized_frames =
        (static_cast<std::uint64_t>(native_buffer_frames) *
             EMKE_AUDIO_LOCAL_SAMPLE_RATE_HZ +
         format.sample_rate_hz - 1u) /
        format.sample_rate_hz;
    return normalized_frames <= normalizedPacketFrameCapacity;
  }

  const std::uint64_t rendered_native_frames =
      (EMKE_AUDIO_LOCAL_CYCLE_FRAMES *
           static_cast<std::uint64_t>(format.sample_rate_hz) +
       EMKE_AUDIO_LOCAL_SAMPLE_RATE_HZ - 1u) /
      EMKE_AUDIO_LOCAL_SAMPLE_RATE_HZ;
  return rendered_native_frames <= nativePacketFrameCapacity &&
         rendered_native_frames <=
             rawPacketByteCapacity / frame_bytes;
}

std::uint64_t network_frames_for_native(
    std::uint32_t native_frames,
    std::uint32_t native_sample_rate_hz) noexcept {
  if (native_frames == 0u || native_sample_rate_hz == 0u) {
    return 0u;
  }
  return (
             static_cast<std::uint64_t>(native_frames) *
                 EMKE_AUDIO_NETWORK_SAMPLE_RATE_HZ +
             native_sample_rate_hz - 1u) /
         native_sample_rate_hz;
}

RawPacketQueue::RawPacketQueue(std::size_t capacity) : storage_(capacity) {}

bool RawPacketQueue::reserve_push(
    const AudioFormat& format,
    std::uint32_t frame_count,
    std::uint64_t timestamp,
    std::size_t byte_count,
    RawAudioPacket*& packet) noexcept {
  packet = nullptr;
  const std::size_t frame_bytes = bytes_per_frame(format);
  if (frame_count == 0u ||
      frame_count > nativePacketFrameCapacity || frame_bytes == 0u ||
      frame_count > std::numeric_limits<std::size_t>::max() / frame_bytes ||
      static_cast<std::size_t>(frame_count) * frame_bytes != byte_count ||
      byte_count > rawPacketByteCapacity) {
    return false;
  }

  const std::size_t write = write_index_.load(std::memory_order_relaxed);
  const std::size_t read = read_index_.load(std::memory_order_acquire);
  if (storage_.empty() || write - read >= storage_.size()) {
    return false;
  }
  packet = &storage_[write % storage_.size()];
  packet->format = format;
  packet->frame_count = frame_count;
  packet->byte_count = static_cast<std::uint32_t>(byte_count);
  packet->timestamp = timestamp;
  return true;
}

bool RawPacketQueue::push(
    const AudioFormat& format,
    std::uint32_t frame_count,
    std::uint64_t timestamp,
    std::span<const std::byte> bytes) noexcept {
  RawAudioPacket* packet = nullptr;
  if (!reserve_push(
          format, frame_count, timestamp, bytes.size(), packet)) {
    return false;
  }
  std::memcpy(packet->bytes.data(), bytes.data(), bytes.size());
  const std::size_t write = write_index_.load(std::memory_order_relaxed);
  write_index_.store(write + 1u, std::memory_order_release);
  return true;
}

bool RawPacketQueue::push_silence(
    const AudioFormat& format,
    std::uint32_t frame_count,
    std::uint64_t timestamp) noexcept {
  const std::size_t frame_bytes = bytes_per_frame(format);
  if (frame_bytes == 0u ||
      frame_count > std::numeric_limits<std::size_t>::max() / frame_bytes) {
    return false;
  }
  const std::size_t byte_count =
      static_cast<std::size_t>(frame_count) * frame_bytes;
  RawAudioPacket* packet = nullptr;
  if (!reserve_push(
          format, frame_count, timestamp, byte_count, packet)) {
    return false;
  }
  std::memset(packet->bytes.data(), 0, byte_count);
  const std::size_t write = write_index_.load(std::memory_order_relaxed);
  write_index_.store(write + 1u, std::memory_order_release);
  return true;
}

bool RawPacketQueue::pop(RawAudioPacket& packet) noexcept {
  const std::size_t read = read_index_.load(std::memory_order_relaxed);
  const std::size_t write = write_index_.load(std::memory_order_acquire);
  if (read == write) {
    return false;
  }
  packet = storage_[read % storage_.size()];
  read_index_.store(read + 1u, std::memory_order_release);
  return true;
}

void RawPacketQueue::clear() noexcept {
  const std::size_t write = write_index_.load(std::memory_order_acquire);
  read_index_.store(write, std::memory_order_release);
}

std::size_t RawPacketQueue::capacity() const noexcept {
  return storage_.size();
}

std::size_t RawPacketQueue::size() const noexcept {
  const std::size_t read = read_index_.load(std::memory_order_acquire);
  const std::size_t write = write_index_.load(std::memory_order_acquire);
  return write - read;
}

bool AsyncStreamFailureState::record_first(
    StreamFailure::Operation operation,
    std::int32_t native_code) noexcept {
  if (operation == StreamFailure::Operation::none) {
    return false;
  }
  const std::uint64_t encoded =
      (static_cast<std::uint64_t>(
           std::bit_cast<std::uint32_t>(native_code))
       << 32u) |
      static_cast<std::uint8_t>(operation);
  std::uint64_t expected = 0u;
  return snapshot_.compare_exchange_strong(
      expected,
      encoded,
      std::memory_order_release,
      std::memory_order_relaxed);
}

void AsyncStreamFailureState::reset() noexcept {
  snapshot_.store(0u, std::memory_order_release);
}

StreamFailure AsyncStreamFailureState::snapshot(
    StreamRole role) const noexcept {
  const std::uint64_t encoded =
      snapshot_.load(std::memory_order_acquire);
  if (encoded == 0u) {
    return {
        .operation = StreamFailure::Operation::none,
        .role = role,
        .native_code = 0,
    };
  }
  return {
      .operation = static_cast<StreamFailure::Operation>(
          static_cast<std::uint8_t>(encoded)),
      .role = role,
      .native_code = std::bit_cast<std::int32_t>(
          static_cast<std::uint32_t>(encoded >> 32u)),
  };
}

RealtimeInstrumentation::Scope::Scope() noexcept {
#if defined(EMKE_NATIVE_AUDIO_REALTIME_INSTRUMENTATION) || !defined(NDEBUG)
  previous_ = realtime_scope_active;
  realtime_scope_active = true;
#endif
}

RealtimeInstrumentation::Scope::~Scope() {
#if defined(EMKE_NATIVE_AUDIO_REALTIME_INSTRUMENTATION) || !defined(NDEBUG)
  realtime_scope_active = previous_;
#endif
}

void RealtimeInstrumentation::project_allocation_hook() noexcept {
#if defined(EMKE_NATIVE_AUDIO_REALTIME_INSTRUMENTATION) || !defined(NDEBUG)
  if (realtime_scope_active) {
    allocation_violation_count.fetch_add(1u, std::memory_order_relaxed);
#if !defined(EMKE_NATIVE_AUDIO_REALTIME_INSTRUMENTATION)
    assert(false && "project allocation attempted in realtime scope");
#endif
  }
#endif
}

void RealtimeInstrumentation::project_blocking_lock_hook() noexcept {
#if defined(EMKE_NATIVE_AUDIO_REALTIME_INSTRUMENTATION) || !defined(NDEBUG)
  if (realtime_scope_active) {
    blocking_violation_count.fetch_add(1u, std::memory_order_relaxed);
#if !defined(EMKE_NATIVE_AUDIO_REALTIME_INSTRUMENTATION)
    assert(false && "blocking lock attempted in realtime scope");
#endif
  }
#endif
}

void RealtimeInstrumentation::reset() noexcept {
#if defined(EMKE_NATIVE_AUDIO_REALTIME_INSTRUMENTATION) || !defined(NDEBUG)
  allocation_violation_count.store(0u, std::memory_order_relaxed);
  blocking_violation_count.store(0u, std::memory_order_relaxed);
#endif
}

std::uint64_t RealtimeInstrumentation::allocation_violations() noexcept {
#if defined(EMKE_NATIVE_AUDIO_REALTIME_INSTRUMENTATION) || !defined(NDEBUG)
  return allocation_violation_count.load(std::memory_order_relaxed);
#else
  return 0u;
#endif
}

std::uint64_t RealtimeInstrumentation::blocking_violations() noexcept {
#if defined(EMKE_NATIVE_AUDIO_REALTIME_INSTRUMENTATION) || !defined(NDEBUG)
  return blocking_violation_count.load(std::memory_order_relaxed);
#else
  return 0u;
#endif
}

bool RealtimeInstrumentation::in_realtime_scope() noexcept {
#if defined(EMKE_NATIVE_AUDIO_REALTIME_INSTRUMENTATION) || !defined(NDEBUG)
  return realtime_scope_active;
#else
  return false;
#endif
}

FakeAudioStream::FakeAudioStream(
    StreamRole role,
    StreamDirection direction,
    AudioFormat format,
    std::size_t input_capacity,
    std::size_t output_capacity)
    : role_(role),
      direction_(direction),
      format_(format),
      input_packets_(input_capacity),
      output_packets_(output_capacity) {}

emke_audio_status FakeAudioStream::start() {
  if (running_.load(std::memory_order_acquire)) {
    return EMKE_AUDIO_OK;
  }
  const emke_audio_status result = next_start_status_;
  next_start_status_ = EMKE_AUDIO_OK;
  const bool running = result == EMKE_AUDIO_OK;
  running_.store(running, std::memory_order_release);
  if (running) {
    failure_code_.store(0, std::memory_order_relaxed);
    failure_operation_.store(
        StreamFailure::Operation::none, std::memory_order_release);
  }
  return result;
}

emke_audio_status FakeAudioStream::stop() {
  running_.store(false, std::memory_order_release);
  input_packets_.clear();
  output_packets_.clear();
  return EMKE_AUDIO_OK;
}

bool FakeAudioStream::running() const noexcept {
  return running_.load(std::memory_order_acquire);
}

StreamRole FakeAudioStream::role() const noexcept {
  return role_;
}

StreamDirection FakeAudioStream::direction() const noexcept {
  return direction_;
}

const AudioFormat& FakeAudioStream::format() const noexcept {
  return format_;
}

RawPacketQueue& FakeAudioStream::input_packets() noexcept {
  return input_packets_;
}

RawPacketQueue& FakeAudioStream::output_packets() noexcept {
  return output_packets_;
}

StreamFailure FakeAudioStream::last_failure() const noexcept {
  return {
      .operation = failure_operation_.load(std::memory_order_acquire),
      .role = role_,
      .native_code = failure_code_.load(std::memory_order_acquire),
  };
}

std::uint64_t FakeAudioStream::dropped_network_frames() const noexcept {
  return dropped_network_frames_.load(std::memory_order_relaxed);
}

std::uint64_t FakeAudioStream::queue_full_events() const noexcept {
  return queue_full_events_.load(std::memory_order_relaxed);
}

void FakeAudioStream::fail_next_start(emke_audio_status status) noexcept {
  next_start_status_ = status;
}

void FakeAudioStream::inject_async_failure(
    StreamFailure::Operation operation,
    std::int32_t native_code) noexcept {
  failure_code_.store(native_code, std::memory_order_relaxed);
  failure_operation_.store(operation, std::memory_order_release);
  running_.store(false, std::memory_order_release);
}

void FakeAudioStream::set_realtime_test_probe(
    bool allocation,
    bool blocking_lock) noexcept {
  probe_allocation_ = allocation;
  probe_blocking_lock_ = blocking_lock;
}

bool FakeAudioStream::emit_capture(
    std::span<const std::byte> bytes,
    std::uint32_t frame_count,
    std::uint64_t timestamp,
    bool silent) noexcept {
  EMKE_REALTIME_CALLBACK_SCOPE(realtime_scope);
#if defined(EMKE_NATIVE_AUDIO_REALTIME_INSTRUMENTATION) || !defined(NDEBUG)
  if (probe_allocation_) {
    RealtimeInstrumentation::project_allocation_hook();
  }
  if (probe_blocking_lock_) {
    RealtimeInstrumentation::project_blocking_lock_hook();
  }
#endif
  const bool pushed = silent
                          ? input_packets_.push_silence(
                                format_, frame_count, timestamp)
                          : input_packets_.push(
                                format_, frame_count, timestamp, bytes);
  if (!pushed) {
    dropped_network_frames_.fetch_add(
        network_frames_for_native(frame_count, format_.sample_rate_hz),
        std::memory_order_relaxed);
    queue_full_events_.fetch_add(1u, std::memory_order_relaxed);
  }
  return pushed;
}

bool FakeAudioStream::render(
    std::span<std::byte> destination,
    std::uint32_t frame_count) noexcept {
  EMKE_REALTIME_CALLBACK_SCOPE(realtime_scope);
#if defined(EMKE_NATIVE_AUDIO_REALTIME_INSTRUMENTATION) || !defined(NDEBUG)
  if (probe_allocation_) {
    RealtimeInstrumentation::project_allocation_hook();
  }
  if (probe_blocking_lock_) {
    RealtimeInstrumentation::project_blocking_lock_hook();
  }
#endif
  const std::size_t required =
      static_cast<std::size_t>(frame_count) * bytes_per_frame(format_);
  if (required != destination.size()) {
    return false;
  }

  RawAudioPacket packet;
  if (!output_packets_.pop(packet) || packet.format.sample_rate_hz !=
                                           format_.sample_rate_hz ||
      packet.byte_count > destination.size()) {
    std::fill(destination.begin(), destination.end(), std::byte{0});
    return true;
  }
  std::memcpy(destination.data(), packet.bytes.data(), packet.byte_count);
  std::fill(
      destination.begin() + static_cast<std::ptrdiff_t>(packet.byte_count),
      destination.end(),
      std::byte{0});
  return true;
}

emke_audio_status initialize_wasapi_stream(
    WasapiClientAdapter& adapter,
    StreamRole role,
    bool exact_virtual,
    StreamFailure& failure) noexcept {
  failure = {};
  failure.role = role;

  auto fail = [&](const WasapiCallResult& result,
                  StreamFailure::Operation operation) {
    failure.operation = operation;
    failure.native_code = result.native_code;
    adapter.reset_after_failure();
    return result.status == EMKE_AUDIO_OK ? EMKE_AUDIO_INTERNAL_ERROR
                                          : result.status;
  };

  bool uses_client3 = true;
  WasapiCallResult result = adapter.activate_client3();
  if (result.unavailable) {
    uses_client3 = false;
    result = adapter.activate_client();
    if (result.status != EMKE_AUDIO_OK) {
      return fail(result, operation_for_step(1u));
    }
  } else if (result.status != EMKE_AUDIO_OK) {
    return fail(result, operation_for_step(0u));
  }

  result = adapter.prepare_format(exact_virtual);
  if (result.status != EMKE_AUDIO_OK) {
    return fail(result, operation_for_step(2u));
  }
  result = adapter.create_event_handles();
  if (result.status != EMKE_AUDIO_OK) {
    return fail(result, operation_for_step(3u));
  }

  if (uses_client3) {
    result = adapter.get_engine_period();
    if (result.status != EMKE_AUDIO_OK) {
      return fail(result, operation_for_step(4u));
    }
    result = adapter.initialize_client3_event_stream();
    if (result.status != EMKE_AUDIO_OK) {
      return fail(result, operation_for_step(5u));
    }
  } else {
    result = adapter.initialize_client_event_stream();
    if (result.status != EMKE_AUDIO_OK) {
      return fail(result, operation_for_step(6u));
    }
  }

  result = adapter.set_event_handle();
  if (result.status != EMKE_AUDIO_OK) {
    return fail(result, operation_for_step(7u));
  }
  result = adapter.get_service();
  if (result.status != EMKE_AUDIO_OK) {
    return fail(result, operation_for_step(8u));
  }
  result = adapter.prepare_loop();
  if (result.status != EMKE_AUDIO_OK) {
    return fail(result, operation_for_step(9u));
  }
  result = adapter.start_client();
  if (result.status != EMKE_AUDIO_OK) {
    return fail(result, operation_for_step(10u));
  }
  return EMKE_AUDIO_OK;
}

#if defined(_WIN32)
namespace {

template <typename Interface>
class ComPtr {
 public:
  ComPtr() = default;
  ~ComPtr() {
    reset();
  }
  ComPtr(const ComPtr&) = delete;
  ComPtr& operator=(const ComPtr&) = delete;

  [[nodiscard]] Interface* get() const noexcept {
    return value_;
  }
  [[nodiscard]] Interface** put() noexcept {
    reset();
    return &value_;
  }
  [[nodiscard]] Interface* operator->() const noexcept {
    return value_;
  }
  void reset() noexcept {
    if (value_ != nullptr) {
      value_->Release();
      value_ = nullptr;
    }
  }

 private:
  Interface* value_ = nullptr;
};

class UniqueHandle {
 public:
  UniqueHandle() = default;
  ~UniqueHandle() {
    reset();
  }
  UniqueHandle(const UniqueHandle&) = delete;
  UniqueHandle& operator=(const UniqueHandle&) = delete;

  [[nodiscard]] HANDLE get() const noexcept {
    return value_;
  }
  void reset(HANDLE replacement = nullptr) noexcept {
    if (value_ != nullptr) {
      CloseHandle(value_);
    }
    value_ = replacement;
  }

 private:
  HANDLE value_ = nullptr;
};

class CoTaskMemFormat {
 public:
  ~CoTaskMemFormat() {
    reset();
  }
  CoTaskMemFormat(const CoTaskMemFormat&) = delete;
  CoTaskMemFormat& operator=(const CoTaskMemFormat&) = delete;
  CoTaskMemFormat() = default;

  [[nodiscard]] WAVEFORMATEX* get() const noexcept {
    return value_;
  }
  [[nodiscard]] WAVEFORMATEX** put() noexcept {
    reset();
    return &value_;
  }
  void reset() noexcept {
    if (value_ != nullptr) {
      CoTaskMemFree(value_);
      value_ = nullptr;
    }
  }

 private:
  WAVEFORMATEX* value_ = nullptr;
};

WasapiCallResult hresult(
    HRESULT result,
    emke_audio_status status = EMKE_AUDIO_INTERNAL_ERROR) noexcept {
  return {
      .status = SUCCEEDED(result) ? EMKE_AUDIO_OK : status,
      .native_code = static_cast<std::int32_t>(result),
  };
}

std::optional<AudioFormat> parse_wave_format(
    const WAVEFORMATEX& wave) noexcept {
  AudioFormat format{
      .sample_rate_hz = wave.nSamplesPerSec,
      .channel_count = wave.nChannels,
      .bits_per_sample = wave.wBitsPerSample,
      .valid_bits_per_sample = wave.wBitsPerSample,
      .block_align = wave.nBlockAlign,
  };
  if (wave.wFormatTag == WAVE_FORMAT_IEEE_FLOAT &&
      wave.wBitsPerSample == 32u) {
    format.sample_type = NativeSampleType::ieeeFloat32;
  } else if (wave.wFormatTag == WAVE_FORMAT_PCM) {
    if (wave.wBitsPerSample == 16u) {
      format.sample_type = NativeSampleType::pcm16;
    } else if (wave.wBitsPerSample == 24u) {
      format.sample_type = NativeSampleType::pcm24;
    } else if (wave.wBitsPerSample == 32u) {
      format.sample_type = NativeSampleType::pcm32;
    } else {
      return std::nullopt;
    }
  } else if (
      wave.wFormatTag == WAVE_FORMAT_EXTENSIBLE &&
      wave.cbSize >= sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX)) {
    const auto& extensible =
        reinterpret_cast<const WAVEFORMATEXTENSIBLE&>(wave);
    format.valid_bits_per_sample =
        extensible.Samples.wValidBitsPerSample;
    format.channel_mask = extensible.dwChannelMask;
    format.has_channel_mask = extensible.dwChannelMask != 0u;
    if (extensible.SubFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT &&
        wave.wBitsPerSample == 32u) {
      format.sample_type = NativeSampleType::ieeeFloat32;
    } else if (extensible.SubFormat == KSDATAFORMAT_SUBTYPE_PCM) {
      if (wave.wBitsPerSample == 16u) {
        format.sample_type = NativeSampleType::pcm16;
      } else if (wave.wBitsPerSample == 24u) {
        format.sample_type = NativeSampleType::pcm24;
      } else if (wave.wBitsPerSample == 32u) {
        format.sample_type = NativeSampleType::pcm32;
      } else {
        return std::nullopt;
      }
    } else {
      return std::nullopt;
    }
  } else {
    return std::nullopt;
  }

  const std::size_t expected =
      static_cast<std::size_t>(format.channel_count) *
      (format.bits_per_sample / 8u);
  if (format.sample_rate_hz < 8'000u ||
      format.sample_rate_hz > 192'000u ||
      format.channel_count == 0u || format.bits_per_sample % 8u != 0u ||
      expected != format.block_align) {
    return std::nullopt;
  }
  return format;
}

}  // namespace
#endif

class WasapiStream::Impl final : public WasapiClientAdapter {
 public:
  Impl(
      StreamRole role,
      StreamDirection direction,
      std::span<const std::uint16_t> endpoint_id,
      bool exact_virtual)
      : role_(role),
        direction_(direction),
        exact_virtual_(exact_virtual),
        input_packets_(defaultRawPacketQueueCapacity),
        output_packets_(defaultRawPacketQueueCapacity) {
#if defined(_WIN32)
    const auto terminator =
        std::find(endpoint_id.begin(), endpoint_id.end(), 0u);
    endpoint_id_.reserve(
        static_cast<std::size_t>(terminator - endpoint_id.begin()));
    for (auto current = endpoint_id.begin(); current != terminator; ++current) {
      endpoint_id_.push_back(static_cast<wchar_t>(*current));
    }
#else
    static_cast<void>(endpoint_id);
#endif
  }

  ~Impl() override {
    static_cast<void>(stop());
  }

  emke_audio_status start() {
    if (running_.load(std::memory_order_acquire)) {
      return EMKE_AUDIO_OK;
    }
#if defined(_WIN32)
    stop_event_loop();
    if (audio_client() != nullptr && client_started_) {
      static_cast<void>(audio_client()->Stop());
      client_started_ = false;
    }
    release_platform_resources();
#endif
    failure_cache_ = {};
    failure_cache_.role = role_;
    async_failure_.reset();
    return initialize_wasapi_stream(
        *this, role_, exact_virtual_, failure_cache_);
  }

  emke_audio_status stop() {
#if defined(_WIN32)
    stop_event_loop();
    HRESULT result = S_OK;
    if (audio_client() != nullptr && client_started_) {
      result = audio_client()->Stop();
      client_started_ = false;
    }
    running_.store(false, std::memory_order_release);
    release_platform_resources();
    if (FAILED(result)) {
      record_failure(StreamFailure::Operation::stopClient, result);
      return EMKE_AUDIO_INTERNAL_ERROR;
    }
#else
    running_.store(false, std::memory_order_release);
#endif
    return EMKE_AUDIO_OK;
  }

  [[nodiscard]] bool running() const noexcept {
    return running_.load(std::memory_order_acquire);
  }

  WasapiCallResult activate_client3() noexcept override {
#if defined(_WIN32)
    const HRESULT device_result = ensure_device();
    if (FAILED(device_result)) {
      return hresult(device_result, EMKE_AUDIO_DEVICE_MISSING);
    }
    const HRESULT result = device_->Activate(
        __uuidof(IAudioClient3),
        CLSCTX_ALL,
        nullptr,
        reinterpret_cast<void**>(client3_.put()));
    if (result == E_NOINTERFACE) {
      return {
          .status = EMKE_AUDIO_OK,
          .native_code = static_cast<std::int32_t>(result),
          .unavailable = true,
      };
    }
    return hresult(result);
#else
    return {
        .status = EMKE_AUDIO_INTERNAL_ERROR,
        .native_code = 0,
    };
#endif
  }

  WasapiCallResult activate_client() noexcept override {
#if defined(_WIN32)
    const HRESULT device_result = ensure_device();
    if (FAILED(device_result)) {
      return hresult(device_result, EMKE_AUDIO_DEVICE_MISSING);
    }
    return hresult(device_->Activate(
        __uuidof(IAudioClient),
        CLSCTX_ALL,
        nullptr,
        reinterpret_cast<void**>(client_.put())));
#else
    return {.status = EMKE_AUDIO_INTERNAL_ERROR};
#endif
  }

  WasapiCallResult prepare_format(bool exact_virtual) noexcept override {
#if defined(_WIN32)
    IAudioClient* client = audio_client();
    if (client == nullptr) {
      return hresult(E_UNEXPECTED);
    }
    if (exact_virtual) {
      virtual_format_ = {};
      virtual_format_.Format.wFormatTag = WAVE_FORMAT_EXTENSIBLE;
      virtual_format_.Format.nChannels =
          virtualAudioFormat.channel_count;
      virtual_format_.Format.nSamplesPerSec =
          virtualAudioFormat.sample_rate_hz;
      virtual_format_.Format.wBitsPerSample =
          virtualAudioFormat.bits_per_sample;
      virtual_format_.Format.nBlockAlign =
          virtualAudioFormat.block_align;
      virtual_format_.Format.nAvgBytesPerSec =
          virtualAudioFormat.average_bytes_per_second;
      virtual_format_.Format.cbSize =
          sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX);
      virtual_format_.Samples.wValidBitsPerSample =
          virtualAudioFormat.valid_bits_per_sample;
      virtual_format_.dwChannelMask =
          SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT;
      virtual_format_.SubFormat = KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;

      WAVEFORMATEX* closest = nullptr;
      const HRESULT result = client->IsFormatSupported(
          AUDCLNT_SHAREMODE_SHARED,
          &virtual_format_.Format,
          &closest);
      if (closest != nullptr) {
        CoTaskMemFree(closest);
      }
      if (result != S_OK) {
        return hresult(
            FAILED(result) ? result : AUDCLNT_E_UNSUPPORTED_FORMAT,
            EMKE_AUDIO_FORMAT_UNSUPPORTED);
      }
      active_wave_format_ = &virtual_format_.Format;
    } else {
      const HRESULT result = client->GetMixFormat(native_format_.put());
      if (FAILED(result)) {
        return hresult(result, EMKE_AUDIO_FORMAT_UNSUPPORTED);
      }
      active_wave_format_ = native_format_.get();
    }

    const auto parsed = parse_wave_format(*active_wave_format_);
    if (!parsed.has_value() ||
        (exact_virtual && !is_exact_virtual_format(*parsed))) {
      return hresult(
          AUDCLNT_E_UNSUPPORTED_FORMAT,
          EMKE_AUDIO_FORMAT_UNSUPPORTED);
    }
    format_ = *parsed;
    return {};
#else
    static_cast<void>(exact_virtual);
    return {.status = EMKE_AUDIO_INTERNAL_ERROR};
#endif
  }

  WasapiCallResult create_event_handles() noexcept override {
#if defined(_WIN32)
    event_.reset(CreateEventW(nullptr, FALSE, FALSE, nullptr));
    if (event_.get() == nullptr) {
      return hresult(HRESULT_FROM_WIN32(GetLastError()));
    }
    stop_event_.reset(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    if (stop_event_.get() == nullptr) {
      return hresult(HRESULT_FROM_WIN32(GetLastError()));
    }
    return {};
#else
    return {.status = EMKE_AUDIO_INTERNAL_ERROR};
#endif
  }

  WasapiCallResult get_engine_period() noexcept override {
#if defined(_WIN32)
    if (client3_.get() == nullptr) {
      return hresult(E_UNEXPECTED);
    }
    UINT32 fundamental = 0u;
    UINT32 minimum = 0u;
    UINT32 maximum = 0u;
    const HRESULT result = client3_->GetSharedModeEnginePeriod(
        active_wave_format_,
        &engine_period_frames_,
        &fundamental,
        &minimum,
        &maximum);
    if (FAILED(result)) {
      return hresult(result);
    }
    if (engine_period_frames_ < minimum ||
        engine_period_frames_ > maximum ||
        fundamental == 0u) {
      return hresult(E_UNEXPECTED);
    }
    return {};
#else
    return {.status = EMKE_AUDIO_INTERNAL_ERROR};
#endif
  }

  WasapiCallResult initialize_client3_event_stream() noexcept override {
#if defined(_WIN32)
    const HRESULT result = client3_->InitializeSharedAudioStream(
        AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
        engine_period_frames_,
        active_wave_format_,
        nullptr);
    return hresult(
        result,
        result == AUDCLNT_E_UNSUPPORTED_FORMAT
            ? EMKE_AUDIO_FORMAT_UNSUPPORTED
            : EMKE_AUDIO_INTERNAL_ERROR);
#else
    return {.status = EMKE_AUDIO_INTERNAL_ERROR};
#endif
  }

  WasapiCallResult initialize_client_event_stream() noexcept override {
#if defined(_WIN32)
    const HRESULT result = client_->Initialize(
        AUDCLNT_SHAREMODE_SHARED,
        AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
        0,
        0,
        active_wave_format_,
        nullptr);
    return hresult(
        result,
        result == AUDCLNT_E_UNSUPPORTED_FORMAT
            ? EMKE_AUDIO_FORMAT_UNSUPPORTED
            : EMKE_AUDIO_INTERNAL_ERROR);
#else
    return {.status = EMKE_AUDIO_INTERNAL_ERROR};
#endif
  }

  WasapiCallResult set_event_handle() noexcept override {
#if defined(_WIN32)
    return hresult(audio_client()->SetEventHandle(event_.get()));
#else
    return {.status = EMKE_AUDIO_INTERNAL_ERROR};
#endif
  }

  WasapiCallResult get_service() noexcept override {
#if defined(_WIN32)
    if (direction_ == StreamDirection::capture) {
      return hresult(audio_client()->GetService(
          __uuidof(IAudioCaptureClient),
          reinterpret_cast<void**>(capture_client_.put())));
    }
    return hresult(audio_client()->GetService(
        __uuidof(IAudioRenderClient),
        reinterpret_cast<void**>(render_client_.put())));
#else
    return {.status = EMKE_AUDIO_INTERNAL_ERROR};
#endif
  }

  WasapiCallResult prepare_loop() noexcept override {
#if defined(_WIN32)
    const HRESULT result = audio_client()->GetBufferSize(&buffer_frames_);
    if (FAILED(result)) {
      return hresult(result);
    }
    if (!stream_format_fits_fixed_storage(
            format_, buffer_frames_, direction_)) {
      return hresult(
          AUDCLNT_E_UNSUPPORTED_FORMAT,
          EMKE_AUDIO_FORMAT_UNSUPPORTED);
    }
    try {
      loop_thread_ = std::thread([this] { event_loop(); });
    } catch (...) {
      return hresult(E_OUTOFMEMORY);
    }
    return {};
#else
    return {.status = EMKE_AUDIO_INTERNAL_ERROR};
#endif
  }

  WasapiCallResult start_client() noexcept override {
#if defined(_WIN32)
    const HRESULT result = audio_client()->Start();
    if (FAILED(result)) {
      return hresult(result);
    }
    client_started_ = true;
    running_.store(true, std::memory_order_release);
    return {};
#else
    return {.status = EMKE_AUDIO_INTERNAL_ERROR};
#endif
  }

  void reset_after_failure() noexcept override {
#if defined(_WIN32)
    stop_event_loop();
    if (audio_client() != nullptr && client_started_) {
      static_cast<void>(audio_client()->Stop());
    }
    client_started_ = false;
    release_platform_resources();
#endif
    running_.store(false, std::memory_order_release);
  }

  [[nodiscard]] StreamFailure last_failure() const noexcept {
    StreamFailure result = failure_cache_;
    result.role = role_;
    const StreamFailure async = async_failure_.snapshot(role_);
    if (async.operation != StreamFailure::Operation::none) {
      return async;
    }
    return result;
  }

  StreamRole role_;
  StreamDirection direction_;
  bool exact_virtual_;
  AudioFormat format_{};
  RawPacketQueue input_packets_;
  RawPacketQueue output_packets_;
  std::atomic<bool> running_{false};
  std::atomic<std::uint64_t> dropped_network_frames_{0u};
  std::atomic<std::uint64_t> queue_full_events_{0u};
  StreamFailure failure_cache_{};
  AsyncStreamFailureState async_failure_;

#if defined(_WIN32)
  [[nodiscard]] IAudioClient* audio_client() const noexcept {
    if (client3_.get() != nullptr) {
      return client3_.get();
    }
    return client_.get();
  }

  HRESULT ensure_device() noexcept {
    if (device_.get() != nullptr) {
      return S_OK;
    }
    if (endpoint_id_.empty()) {
      return E_INVALIDARG;
    }
    HRESULT result = CoCreateInstance(
        __uuidof(MMDeviceEnumerator),
        nullptr,
        CLSCTX_ALL,
        __uuidof(IMMDeviceEnumerator),
        reinterpret_cast<void**>(enumerator_.put()));
    if (FAILED(result)) {
      return result;
    }
    return enumerator_->GetDevice(endpoint_id_.c_str(), device_.put());
  }

  void record_failure(
      StreamFailure::Operation operation,
      HRESULT result) noexcept {
    static_cast<void>(async_failure_.record_first(
        operation, static_cast<std::int32_t>(result)));
  }

  void event_loop() noexcept {
    const std::array handles = {stop_event_.get(), event_.get()};
    for (;;) {
      const DWORD wait = WaitForMultipleObjects(
          static_cast<DWORD>(handles.size()),
          handles.data(),
          FALSE,
          INFINITE);
      if (wait == WAIT_OBJECT_0) {
        break;
      }
      if (wait != WAIT_OBJECT_0 + 1u) {
        record_failure(
            StreamFailure::Operation::waitForEvent,
            HRESULT_FROM_WIN32(GetLastError()));
        break;
      }
      if (direction_ == StreamDirection::capture) {
        capture_event();
      } else {
        render_event();
      }
      if (async_failure_.snapshot(role_).operation !=
          StreamFailure::Operation::none) {
        break;
      }
    }
    running_.store(false, std::memory_order_release);
  }

  void capture_event() noexcept {
    EMKE_REALTIME_CALLBACK_SCOPE(realtime_scope);
    UINT32 packet_frames = 0u;
    HRESULT result = capture_client_->GetNextPacketSize(&packet_frames);
    if (FAILED(result)) {
      record_failure(
          StreamFailure::Operation::getNextPacketSize, result);
      return;
    }
    while (packet_frames > 0u) {
      BYTE* data = nullptr;
      UINT32 frame_count = 0u;
      DWORD flags = 0u;
      UINT64 device_position = 0u;
      UINT64 qpc_position = 0u;
      result = capture_client_->GetBuffer(
          &data,
          &frame_count,
          &flags,
          &device_position,
          &qpc_position);
      if (FAILED(result)) {
        record_failure(StreamFailure::Operation::getBuffer, result);
        return;
      }

      bool queued = false;
      const bool silent = (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0u;
      const std::size_t byte_count =
          static_cast<std::size_t>(frame_count) * bytes_per_frame(format_);
      if (silent) {
        queued = input_packets_.push_silence(
            format_, frame_count, qpc_position);
      } else if (data != nullptr) {
        queued = input_packets_.push(
            format_,
            frame_count,
            qpc_position,
            {reinterpret_cast<const std::byte*>(data), byte_count});
      }
      if (!queued) {
        dropped_network_frames_.fetch_add(
            network_frames_for_native(frame_count, format_.sample_rate_hz),
            std::memory_order_relaxed);
        queue_full_events_.fetch_add(1u, std::memory_order_relaxed);
      }

      result = capture_client_->ReleaseBuffer(frame_count);
      if (FAILED(result)) {
        record_failure(StreamFailure::Operation::releaseBuffer, result);
        return;
      }
      result = capture_client_->GetNextPacketSize(&packet_frames);
      if (FAILED(result)) {
        record_failure(
            StreamFailure::Operation::getNextPacketSize, result);
        return;
      }
    }
  }

  void render_event() noexcept {
    EMKE_REALTIME_CALLBACK_SCOPE(realtime_scope);
    UINT32 padding = 0u;
    HRESULT result = audio_client()->GetCurrentPadding(&padding);
    if (FAILED(result) || padding > buffer_frames_) {
      record_failure(
          StreamFailure::Operation::getPadding,
          FAILED(result) ? result : E_UNEXPECTED);
      return;
    }
    const UINT32 available = buffer_frames_ - padding;
    if (available == 0u) {
      return;
    }

    BYTE* destination = nullptr;
    result = render_client_->GetBuffer(available, &destination);
    if (FAILED(result)) {
      record_failure(StreamFailure::Operation::getBuffer, result);
      return;
    }
    const std::size_t byte_count =
        static_cast<std::size_t>(available) * bytes_per_frame(format_);
    std::memset(destination, 0, byte_count);

    std::size_t output_offset = 0u;
    while (output_offset < byte_count) {
      if (!has_render_packet_) {
        has_render_packet_ = output_packets_.pop(render_packet_);
        render_packet_offset_ = 0u;
        if (!has_render_packet_) {
          break;
        }
        if (render_packet_.format.sample_rate_hz != format_.sample_rate_hz ||
            render_packet_.format.channel_count != format_.channel_count ||
            render_packet_.format.sample_type != format_.sample_type ||
            render_packet_.format.block_align != format_.block_align) {
          has_render_packet_ = false;
          dropped_network_frames_.fetch_add(
              network_frames_for_native(
                  render_packet_.frame_count,
                  render_packet_.format.sample_rate_hz),
              std::memory_order_relaxed);
          continue;
        }
      }
      const std::size_t remaining =
          render_packet_.byte_count - render_packet_offset_;
      const std::size_t copied =
          std::min(remaining, byte_count - output_offset);
      std::memcpy(
          destination + output_offset,
          render_packet_.bytes.data() + render_packet_offset_,
          copied);
      output_offset += copied;
      render_packet_offset_ += copied;
      if (render_packet_offset_ == render_packet_.byte_count) {
        has_render_packet_ = false;
      }
    }

    result = render_client_->ReleaseBuffer(available, 0u);
    if (FAILED(result)) {
      record_failure(StreamFailure::Operation::releaseBuffer, result);
    }
  }

  void stop_event_loop() noexcept {
    if (stop_event_.get() != nullptr) {
      SetEvent(stop_event_.get());
    }
    if (loop_thread_.joinable()) {
      loop_thread_.join();
    }
  }

  void release_platform_resources() noexcept {
    render_client_.reset();
    capture_client_.reset();
    client3_.reset();
    client_.reset();
    device_.reset();
    enumerator_.reset();
    native_format_.reset();
    active_wave_format_ = nullptr;
    event_.reset();
    stop_event_.reset();
    input_packets_.clear();
    output_packets_.clear();
    has_render_packet_ = false;
  }

  std::wstring endpoint_id_;
  ComPtr<IMMDeviceEnumerator> enumerator_;
  ComPtr<IMMDevice> device_;
  ComPtr<IAudioClient3> client3_;
  ComPtr<IAudioClient> client_;
  ComPtr<IAudioCaptureClient> capture_client_;
  ComPtr<IAudioRenderClient> render_client_;
  CoTaskMemFormat native_format_;
  WAVEFORMATEXTENSIBLE virtual_format_{};
  WAVEFORMATEX* active_wave_format_ = nullptr;
  UniqueHandle event_;
  UniqueHandle stop_event_;
  std::thread loop_thread_;
  UINT32 engine_period_frames_ = 0u;
  UINT32 buffer_frames_ = 0u;
  bool client_started_ = false;
  bool has_render_packet_ = false;
  RawAudioPacket render_packet_{};
  std::size_t render_packet_offset_ = 0u;
#endif
};

WasapiStream::WasapiStream(
    StreamRole role,
    StreamDirection direction,
    std::span<const std::uint16_t> endpoint_id,
    bool exact_virtual_format)
    : impl_(std::make_unique<Impl>(
          role, direction, endpoint_id, exact_virtual_format)) {}

WasapiStream::~WasapiStream() = default;

emke_audio_status WasapiStream::start() {
  return impl_->start();
}

emke_audio_status WasapiStream::stop() {
  return impl_->stop();
}

bool WasapiStream::running() const noexcept {
  return impl_->running();
}

StreamRole WasapiStream::role() const noexcept {
  return impl_->role_;
}

StreamDirection WasapiStream::direction() const noexcept {
  return impl_->direction_;
}

const AudioFormat& WasapiStream::format() const noexcept {
  return impl_->format_;
}

RawPacketQueue& WasapiStream::input_packets() noexcept {
  return impl_->input_packets_;
}

RawPacketQueue& WasapiStream::output_packets() noexcept {
  return impl_->output_packets_;
}

StreamFailure WasapiStream::last_failure() const noexcept {
  return impl_->last_failure();
}

std::uint64_t WasapiStream::dropped_network_frames() const noexcept {
  return impl_->dropped_network_frames_.load(std::memory_order_relaxed);
}

std::uint64_t WasapiStream::queue_full_events() const noexcept {
  return impl_->queue_full_events_.load(std::memory_order_relaxed);
}

}  // namespace emke::audio
