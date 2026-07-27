#ifndef EMKE_VIRTUAL_AUDIO_FORMAT_HPP
#define EMKE_VIRTUAL_AUDIO_FORMAT_HPP

#include "emke_endpoint_contract.h"

#include <cstdint>

namespace emke::audio {

struct VirtualAudioFormat {
  std::uint32_t sample_rate_hz;
  std::uint16_t channel_count;
  std::uint16_t bits_per_sample;
  std::uint16_t valid_bits_per_sample;
  std::uint16_t block_align;
  std::uint32_t average_bytes_per_second;
  std::uint16_t format_tag;
};

inline constexpr VirtualAudioFormat virtualAudioFormat{
    .sample_rate_hz = EMKE_AUDIO_SAMPLE_RATE,
    .channel_count = EMKE_AUDIO_CHANNEL_COUNT,
    .bits_per_sample = EMKE_AUDIO_BITS_PER_SAMPLE,
    .valid_bits_per_sample = EMKE_AUDIO_BITS_PER_SAMPLE,
    .block_align = EMKE_AUDIO_BLOCK_ALIGN,
    .average_bytes_per_second = EMKE_AUDIO_AVG_BYTES_PER_SECOND,
    .format_tag = EMKE_AUDIO_FORMAT_TAG,
};

[[nodiscard]] constexpr bool matches_virtual_audio_format(
    const VirtualAudioFormat& format) noexcept {
  return format.sample_rate_hz == virtualAudioFormat.sample_rate_hz &&
      format.channel_count == virtualAudioFormat.channel_count &&
      format.bits_per_sample == virtualAudioFormat.bits_per_sample &&
      format.valid_bits_per_sample ==
          virtualAudioFormat.valid_bits_per_sample &&
      format.block_align == virtualAudioFormat.block_align &&
      format.average_bytes_per_second ==
          virtualAudioFormat.average_bytes_per_second &&
      format.format_tag == virtualAudioFormat.format_tag;
}

}  // namespace emke::audio

#endif
