#include "pcm_converter.hpp"

#include <algorithm>
#include <bit>
#include <cmath>
#include <limits>
#include <numbers>

namespace emke::audio {
namespace {

float safe_average(float first, float second) noexcept {
  if (std::isnan(first) || std::isnan(second)) {
    return std::numeric_limits<float>::quiet_NaN();
  }

  const bool first_is_infinite = std::isinf(first);
  const bool second_is_infinite = std::isinf(second);
  if (first_is_infinite || second_is_infinite) {
    if (first_is_infinite && second_is_infinite &&
        std::signbit(first) != std::signbit(second)) {
      return std::numeric_limits<float>::quiet_NaN();
    }
    return first_is_infinite ? first : second;
  }

  return first * 0.5f + second * 0.5f;
}

std::int16_t float_to_pcm16(float sample) noexcept {
  if (std::isnan(sample)) {
    return 0;
  }
  if (sample <= -1.0f) {
    return std::numeric_limits<std::int16_t>::min();
  }
  if (sample >= 1.0f) {
    return std::numeric_limits<std::int16_t>::max();
  }
  return static_cast<std::int16_t>(
      std::lround(sample * std::numeric_limits<std::int16_t>::max()));
}

float pcm16_to_float(std::int16_t sample) noexcept {
  if (sample == std::numeric_limits<std::int16_t>::min()) {
    return -1.0f;
  }
  return static_cast<float>(sample) /
         static_cast<float>(std::numeric_limits<std::int16_t>::max());
}

}  // namespace

PcmConversionResult PcmEncoder::process(
    std::span<const float> interleaved_stereo_48khz,
    std::span<std::uint8_t> mono_pcm16_24khz_little_endian) noexcept {
  if (interleaved_stereo_48khz.size() % localChannelCount != 0u) {
    return {PcmConversionStatus::misalignedStereo, 0u};
  }

  const std::size_t local_frames =
      interleaved_stereo_48khz.size() / localChannelCount;
  const std::size_t available_mono_frames =
      local_frames + (has_pending_mono_frame_ ? 1u : 0u);
  const std::size_t required_bytes =
      (available_mono_frames / 2u) * sizeof(std::int16_t);
  if (mono_pcm16_24khz_little_endian.size() < required_bytes) {
    return {PcmConversionStatus::insufficientOutput, 0u};
  }

  std::size_t output_index = 0u;
  for (std::size_t frame = 0u; frame < local_frames; ++frame) {
    const std::size_t input_index = frame * localChannelCount;
    const float mono_frame = safe_average(
        interleaved_stereo_48khz[input_index],
        interleaved_stereo_48khz[input_index + 1u]);
    if (!has_pending_mono_frame_) {
      pending_mono_frame_ = mono_frame;
      has_pending_mono_frame_ = true;
      continue;
    }

    const std::int16_t pcm16 =
        float_to_pcm16(safe_average(pending_mono_frame_, mono_frame));
    const std::uint16_t bits = std::bit_cast<std::uint16_t>(pcm16);
    mono_pcm16_24khz_little_endian[output_index] =
        static_cast<std::uint8_t>(bits & 0xffu);
    mono_pcm16_24khz_little_endian[output_index + 1u] =
        static_cast<std::uint8_t>((bits >> 8u) & 0xffu);
    output_index += sizeof(std::int16_t);
    has_pending_mono_frame_ = false;
  }
  return {PcmConversionStatus::ok, output_index};
}

PcmDecoder::PcmDecoder() noexcept {
  std::array<double, firTapCount> taps{};
  constexpr double midpoint =
      static_cast<double>(firTapCount - 1u) * 0.5;
  constexpr double cutoff = 0.25;
  double sum = 0.0;
  for (std::size_t index = 0u; index < firTapCount; ++index) {
    const double distance = static_cast<double>(index) - midpoint;
    const double sinc =
        distance == 0.0
            ? 2.0 * cutoff
            : std::sin(2.0 * std::numbers::pi_v<double> * cutoff * distance) /
                  (std::numbers::pi_v<double> * distance);
    const double window =
        0.42 -
        0.5 * std::cos(
                  2.0 * std::numbers::pi_v<double> *
                  static_cast<double>(index) /
                  static_cast<double>(firTapCount - 1u)) +
        0.08 * std::cos(
                   4.0 * std::numbers::pi_v<double> *
                   static_cast<double>(index) /
                   static_cast<double>(firTapCount - 1u));
    taps[index] = sinc * window;
    sum += taps[index];
  }

  const double gain = 2.0 / sum;
  for (std::size_t index = 0u; index < firTapCount; ++index) {
    if (index % 2u == 0u) {
      even_phase_[index / 2u] =
          static_cast<float>(taps[index] * gain);
    } else {
      odd_phase_[index / 2u] =
          static_cast<float>(taps[index] * gain);
    }
  }
}

PcmConversionResult PcmDecoder::process(
    std::span<const std::uint8_t> mono_pcm16_24khz_little_endian,
    std::span<float> interleaved_stereo_48khz) noexcept {
  if (mono_pcm16_24khz_little_endian.size() % sizeof(std::int16_t) != 0u) {
    return {PcmConversionStatus::misalignedPcm16, 0u};
  }
  if (mono_pcm16_24khz_little_endian.size() >
      std::numeric_limits<std::size_t>::max() / 2u) {
    return {PcmConversionStatus::insufficientOutput, 0u};
  }
  const std::size_t required_output =
      mono_pcm16_24khz_little_endian.size() * 2u;
  if (interleaved_stereo_48khz.size() < required_output) {
    return {PcmConversionStatus::insufficientOutput, 0u};
  }

  std::size_t output_index = 0u;
  for (std::size_t input_index = 0u;
       input_index < mono_pcm16_24khz_little_endian.size();
       input_index += sizeof(std::int16_t)) {
    const std::uint16_t bits =
        static_cast<std::uint16_t>(
            mono_pcm16_24khz_little_endian[input_index]) |
        static_cast<std::uint16_t>(
            static_cast<std::uint16_t>(
                mono_pcm16_24khz_little_endian[input_index + 1u])
            << 8u);
    const float sample = pcm16_to_float(std::bit_cast<std::int16_t>(bits));

    newest_index_ = (newest_index_ + 1u) % history_.size();
    history_[newest_index_] = sample;
    const float even = std::clamp(convolve(even_phase_), -1.0f, 1.0f);
    const float odd = std::clamp(convolve(odd_phase_), -1.0f, 1.0f);
    interleaved_stereo_48khz[output_index] = even;
    interleaved_stereo_48khz[output_index + 1u] = even;
    interleaved_stereo_48khz[output_index + 2u] = odd;
    interleaved_stereo_48khz[output_index + 3u] = odd;
    output_index += 4u;
  }
  return {PcmConversionStatus::ok, output_index};
}

void PcmDecoder::reset() noexcept {
  history_.fill(0.0f);
  newest_index_ = history_.size() - 1u;
}

float PcmDecoder::convolve(
    std::span<const float> coefficients) const noexcept {
  float result = 0.0f;
  for (std::size_t delay = 0u; delay < coefficients.size(); ++delay) {
    const std::size_t index =
        (newest_index_ + history_.size() - delay) % history_.size();
    result += coefficients[delay] * history_[index];
  }
  return result;
}

}  // namespace emke::audio
