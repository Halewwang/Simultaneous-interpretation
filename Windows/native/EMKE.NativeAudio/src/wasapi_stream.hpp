#ifndef EMKE_WASAPI_STREAM_HPP
#define EMKE_WASAPI_STREAM_HPP

#include "emke_native_audio.h"

#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <memory>
#include <span>
#include <string_view>
#include <vector>

namespace emke::audio {

enum class StreamRole : std::uint8_t {
  physicalOutput,
  appMicrophoneRender,
  appSpeakerCapture,
  physicalMicrophoneCapture,
};

enum class StreamDirection : std::uint8_t {
  render,
  capture,
};

enum class NativeSampleType : std::uint8_t {
  ieeeFloat32,
  pcm16,
  pcm24,
  pcm32,
};

struct AudioFormat {
  std::uint32_t sample_rate_hz = 0u;
  std::uint16_t channel_count = 0u;
  NativeSampleType sample_type = NativeSampleType::ieeeFloat32;
  std::uint16_t bits_per_sample = 0u;
  std::uint16_t valid_bits_per_sample = 0u;
  std::uint16_t block_align = 0u;
  std::uint32_t channel_mask = 0u;
  bool has_channel_mask = false;
};

[[nodiscard]] bool is_exact_virtual_format(
    const AudioFormat& format) noexcept;
[[nodiscard]] std::size_t bytes_per_frame(
    const AudioFormat& format) noexcept;

inline constexpr std::size_t rawPacketByteCapacity = 65'536u;
inline constexpr std::size_t defaultRawPacketQueueCapacity = 16u;

struct RawAudioPacket {
  AudioFormat format{};
  std::uint32_t frame_count = 0u;
  std::uint32_t byte_count = 0u;
  std::uint64_t timestamp = 0u;
  std::array<std::byte, rawPacketByteCapacity> bytes{};
};

class RawPacketQueue {
 public:
  explicit RawPacketQueue(
      std::size_t capacity = defaultRawPacketQueueCapacity);

  RawPacketQueue(const RawPacketQueue&) = delete;
  RawPacketQueue& operator=(const RawPacketQueue&) = delete;

  [[nodiscard]] bool push(
      const AudioFormat& format,
      std::uint32_t frame_count,
      std::uint64_t timestamp,
      std::span<const std::byte> bytes) noexcept;
  [[nodiscard]] bool push_silence(
      const AudioFormat& format,
      std::uint32_t frame_count,
      std::uint64_t timestamp) noexcept;
  [[nodiscard]] bool pop(RawAudioPacket& packet) noexcept;
  void clear() noexcept;

  [[nodiscard]] std::size_t capacity() const noexcept;
  [[nodiscard]] std::size_t size() const noexcept;

 private:
  [[nodiscard]] bool reserve_push(
      const AudioFormat& format,
      std::uint32_t frame_count,
      std::uint64_t timestamp,
      std::size_t byte_count,
      RawAudioPacket*& packet) noexcept;

  std::vector<RawAudioPacket> storage_;
  alignas(64) std::atomic<std::size_t> read_index_{0u};
  alignas(64) std::atomic<std::size_t> write_index_{0u};
};

class Startable {
 public:
  virtual ~Startable() = default;
  virtual emke_audio_status start() = 0;
  virtual emke_audio_status stop() = 0;
  [[nodiscard]] virtual bool running() const noexcept = 0;
};

struct StreamFailure {
  enum class Operation : std::uint8_t {
    none,
    activateClient3,
    activateClient,
    getFormat,
    validateFormat,
    createEvents,
    getEnginePeriod,
    initializeClient3,
    initializeClient,
    setEventHandle,
    getService,
    prepareLoop,
    startClient,
    waitForEvent,
    getPadding,
    getNextPacketSize,
    getBuffer,
    releaseBuffer,
    stopClient,
  };

  Operation operation = Operation::none;
  StreamRole role = StreamRole::physicalOutput;
  std::int32_t native_code = 0;
};

class RealtimeInstrumentation {
 public:
  class Scope {
   public:
    Scope() noexcept;
    ~Scope();
    Scope(const Scope&) = delete;
    Scope& operator=(const Scope&) = delete;

   private:
    bool previous_ = false;
  };

  static void project_allocation_hook() noexcept;
  static void project_blocking_lock_hook() noexcept;
  static void reset() noexcept;
  [[nodiscard]] static std::uint64_t allocation_violations() noexcept;
  [[nodiscard]] static std::uint64_t blocking_violations() noexcept;
  [[nodiscard]] static bool in_realtime_scope() noexcept;
};

class AudioStream : public Startable {
 public:
  ~AudioStream() override = default;

  [[nodiscard]] virtual StreamRole role() const noexcept = 0;
  [[nodiscard]] virtual StreamDirection direction() const noexcept = 0;
  [[nodiscard]] virtual const AudioFormat& format() const noexcept = 0;
  [[nodiscard]] virtual RawPacketQueue& input_packets() noexcept = 0;
  [[nodiscard]] virtual RawPacketQueue& output_packets() noexcept = 0;
  [[nodiscard]] virtual StreamFailure last_failure() const noexcept = 0;
  [[nodiscard]] virtual std::uint64_t dropped_packets() const noexcept = 0;
};

class FakeAudioStream final : public AudioStream {
 public:
  FakeAudioStream(
      StreamRole role,
      StreamDirection direction,
      AudioFormat format,
      std::size_t input_capacity = defaultRawPacketQueueCapacity,
      std::size_t output_capacity = defaultRawPacketQueueCapacity);

  emke_audio_status start() override;
  emke_audio_status stop() override;
  [[nodiscard]] bool running() const noexcept override;
  [[nodiscard]] StreamRole role() const noexcept override;
  [[nodiscard]] StreamDirection direction() const noexcept override;
  [[nodiscard]] const AudioFormat& format() const noexcept override;
  [[nodiscard]] RawPacketQueue& input_packets() noexcept override;
  [[nodiscard]] RawPacketQueue& output_packets() noexcept override;
  [[nodiscard]] StreamFailure last_failure() const noexcept override;
  [[nodiscard]] std::uint64_t dropped_packets() const noexcept override;

  void fail_next_start(emke_audio_status status) noexcept;
  [[nodiscard]] bool emit_capture(
      std::span<const std::byte> bytes,
      std::uint32_t frame_count,
      std::uint64_t timestamp,
      bool silent = false) noexcept;
  [[nodiscard]] bool render(
      std::span<std::byte> destination,
      std::uint32_t frame_count) noexcept;

 private:
  StreamRole role_;
  StreamDirection direction_;
  AudioFormat format_;
  RawPacketQueue input_packets_;
  RawPacketQueue output_packets_;
  emke_audio_status next_start_status_ = EMKE_AUDIO_OK;
  bool running_ = false;
  std::uint64_t dropped_packets_ = 0u;
};

struct WasapiCallResult {
  emke_audio_status status = EMKE_AUDIO_OK;
  std::int32_t native_code = 0;
  bool unavailable = false;
};

class WasapiClientAdapter {
 public:
  virtual ~WasapiClientAdapter() = default;
  virtual WasapiCallResult activate_client3() noexcept = 0;
  virtual WasapiCallResult activate_client() noexcept = 0;
  virtual WasapiCallResult prepare_format(bool exact_virtual) noexcept = 0;
  virtual WasapiCallResult create_event_handles() noexcept = 0;
  virtual WasapiCallResult get_engine_period() noexcept = 0;
  virtual WasapiCallResult initialize_client3_event_stream() noexcept = 0;
  virtual WasapiCallResult initialize_client_event_stream() noexcept = 0;
  virtual WasapiCallResult set_event_handle() noexcept = 0;
  virtual WasapiCallResult get_service() noexcept = 0;
  virtual WasapiCallResult prepare_loop() noexcept = 0;
  virtual WasapiCallResult start_client() noexcept = 0;
  virtual void reset_after_failure() noexcept = 0;
};

[[nodiscard]] emke_audio_status initialize_wasapi_stream(
    WasapiClientAdapter& adapter,
    StreamRole role,
    bool exact_virtual,
    StreamFailure& failure) noexcept;

class WasapiStream final : public AudioStream {
 public:
  WasapiStream(
      StreamRole role,
      StreamDirection direction,
      std::span<const std::uint16_t> endpoint_id,
      bool exact_virtual_format);
  ~WasapiStream() override;

  WasapiStream(const WasapiStream&) = delete;
  WasapiStream& operator=(const WasapiStream&) = delete;

  emke_audio_status start() override;
  emke_audio_status stop() override;
  [[nodiscard]] bool running() const noexcept override;
  [[nodiscard]] StreamRole role() const noexcept override;
  [[nodiscard]] StreamDirection direction() const noexcept override;
  [[nodiscard]] const AudioFormat& format() const noexcept override;
  [[nodiscard]] RawPacketQueue& input_packets() noexcept override;
  [[nodiscard]] RawPacketQueue& output_packets() noexcept override;
  [[nodiscard]] StreamFailure last_failure() const noexcept override;
  [[nodiscard]] std::uint64_t dropped_packets() const noexcept override;

 private:
  class Impl;
  std::unique_ptr<Impl> impl_;
};

}  // namespace emke::audio

#endif
