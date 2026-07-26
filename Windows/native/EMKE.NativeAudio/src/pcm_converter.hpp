#ifndef EMKE_PCM_CONVERTER_HPP
#define EMKE_PCM_CONVERTER_HPP

#include "spsc_ring.hpp"

#include <array>
#include <cstddef>
#include <cstdint>
#include <span>

namespace emke::audio {

inline constexpr std::size_t networkSampleRate = 24'000u;
inline constexpr std::size_t localSampleRate = 48'000u;
inline constexpr std::size_t networkChannelCount = 1u;
inline constexpr std::size_t firTapCount = 127u;
inline constexpr std::size_t firGroupDelaySamplesAt48k = 63u;

enum class PcmConversionStatus {
  ok,
  misalignedStereo,
  misalignedPcm16,
  insufficientOutput,
};

struct PcmConversionResult {
  PcmConversionStatus status = PcmConversionStatus::ok;
  std::size_t output_count = 0u;
};

class PcmEncoder {
 public:
  [[nodiscard]] PcmConversionResult process(
      std::span<const float> interleaved_stereo_48khz,
      std::span<std::uint8_t> mono_pcm16_24khz_little_endian) noexcept;
  void reset() noexcept;

 private:
  bool has_pending_mono_frame_ = false;
  float pending_mono_frame_ = 0.0f;
};

class PcmDecoder {
 public:
  PcmDecoder() noexcept;

  [[nodiscard]] PcmConversionResult process(
      std::span<const std::uint8_t> mono_pcm16_24khz_little_endian,
      std::span<float> interleaved_stereo_48khz) noexcept;

  void reset() noexcept;

 private:
  [[nodiscard]] float convolve(
      std::span<const float> coefficients) const noexcept;

  std::array<float, (firTapCount + 1u) / 2u> even_phase_{};
  std::array<float, firTapCount / 2u> odd_phase_{};
  std::array<float, (firTapCount + 1u) / 2u> history_{};
  std::size_t newest_index_ = history_.size() - 1u;
};

}  // namespace emke::audio

#endif
