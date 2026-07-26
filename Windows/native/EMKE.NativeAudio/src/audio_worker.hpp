#ifndef EMKE_AUDIO_WORKER_HPP
#define EMKE_AUDIO_WORKER_HPP

#include "fake_audio_backend.hpp"
#include "wasapi_stream.hpp"

#include <array>
#include <cstddef>
#include <cstdint>
#include <memory>
#include <span>

namespace emke::audio {

enum class NativeFormatStatus : std::uint8_t {
  ok,
  invalidFormat,
  malformedPacket,
  insufficientOutput,
};

struct NativeFormatResult {
  NativeFormatStatus status = NativeFormatStatus::ok;
  std::size_t output_frames = 0u;
};

class NativeFormatConverter {
 public:
  explicit NativeFormatConverter(AudioFormat format) noexcept;

  [[nodiscard]] NativeFormatResult process(
      std::span<const std::byte> native_bytes,
      std::uint32_t native_frames,
      std::span<float> interleaved_stereo_48khz) noexcept;
  void reset() noexcept;

  [[nodiscard]] std::size_t required_output_frames(
      std::uint32_t native_frames) const noexcept;
  [[nodiscard]] bool supported() const noexcept;

 private:
  [[nodiscard]] bool decode_frame(
      std::span<const std::byte> bytes,
      std::size_t frame,
      float& left,
      float& right) const noexcept;
  [[nodiscard]] std::size_t channel_index(
      std::uint32_t speaker_bit,
      std::size_t fallback) const noexcept;

  AudioFormat format_{};
  std::uint64_t input_frame_cursor_ = 0u;
  double next_output_position_ = 0.0;
  float previous_left_ = 0.0f;
  float previous_right_ = 0.0f;
  bool has_previous_ = false;
};

class AudioPipelineLifecycle {
 public:
  AudioPipelineLifecycle(
      Startable& physical_output,
      Startable& app_microphone_render,
      Startable& app_speaker_capture,
      Startable& physical_microphone_capture,
      Startable& worker) noexcept;

  emke_audio_status start() noexcept;
  emke_audio_status stop() noexcept;
  [[nodiscard]] bool running() const noexcept;

 private:
  std::array<Startable*, 5u> components_;
  std::size_t started_count_ = 0u;
  bool running_ = false;
};

inline constexpr std::size_t networkBatchFrames = 4'800u;
inline constexpr std::size_t networkBatchBytes =
    networkBatchFrames * sizeof(std::int16_t);
inline constexpr std::size_t pendingEventCapacity = 64u;
inline constexpr std::size_t maxNormalizedPacketFrames = 11'520u;

class AudioWorker final : public Startable {
 public:
  AudioWorker(
      AudioStream& physical_output,
      AudioStream& app_microphone_render,
      AudioStream& app_speaker_capture,
      AudioStream& physical_microphone_capture);
  ~AudioWorker() override;

  AudioWorker(const AudioWorker&) = delete;
  AudioWorker& operator=(const AudioWorker&) = delete;

  emke_audio_status start() override;
  emke_audio_status stop() override;
  [[nodiscard]] bool running() const noexcept override;

  [[nodiscard]] bool process_once() noexcept;
  emke_audio_status set_route(
      Direction direction,
      emke_audio_route route) noexcept;
  emke_audio_status enqueue_translation(
      Direction direction,
      std::span<const std::int16_t> pcm16) noexcept;
  emke_audio_status poll_event(
      AudioEvent& event,
      std::size_t pcm_capacity_network_frames);
  void write_diagnostics(emke_audio_diagnostics& diagnostics) const noexcept;

 private:
  class Impl;
  std::unique_ptr<Impl> impl_;
};

class NativeAudioBackend {
 public:
  explicit NativeAudioBackend(const emke_audio_config& config);
  ~NativeAudioBackend();

  NativeAudioBackend(const NativeAudioBackend&) = delete;
  NativeAudioBackend& operator=(const NativeAudioBackend&) = delete;

  emke_audio_status start();
  emke_audio_status stop();
  emke_audio_status set_route(
      Direction direction,
      emke_audio_route route);
  emke_audio_status enqueue_translation(
      Direction direction,
      std::span<const std::int16_t> pcm16);
  emke_audio_status poll_event(
      AudioEvent& event,
      std::size_t pcm_capacity_network_frames);
  void write_diagnostics(emke_audio_diagnostics& diagnostics) const;

 private:
  class Impl;
  std::unique_ptr<Impl> impl_;
};

}  // namespace emke::audio

#endif
