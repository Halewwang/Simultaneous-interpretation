#include "emke_endpoint_contract.h"
#include "virtual_audio_format.hpp"

#include <cstdio>

namespace {

int failures = 0;

void expect(bool condition, const char* message) {
  if (!condition) {
    std::fprintf(stderr, "FAIL: %s\n", message);
    ++failures;
  }
}

}  // namespace

int main() {
  using emke::audio::virtualAudioFormat;

  static_assert(
      virtualAudioFormat.sample_rate_hz == EMKE_AUDIO_SAMPLE_RATE);
  static_assert(
      virtualAudioFormat.channel_count == EMKE_AUDIO_CHANNEL_COUNT);
  static_assert(
      virtualAudioFormat.bits_per_sample == EMKE_AUDIO_BITS_PER_SAMPLE);
  static_assert(
      virtualAudioFormat.valid_bits_per_sample ==
      EMKE_AUDIO_BITS_PER_SAMPLE);
  static_assert(
      virtualAudioFormat.block_align == EMKE_AUDIO_BLOCK_ALIGN);
  static_assert(
      virtualAudioFormat.average_bytes_per_second ==
      EMKE_AUDIO_AVG_BYTES_PER_SECOND);
  static_assert(
      virtualAudioFormat.format_tag == EMKE_AUDIO_FORMAT_TAG);

  expect(
      emke::audio::matches_virtual_audio_format(virtualAudioFormat),
      "the shared virtual format must validate");

  auto mutation = virtualAudioFormat;
  ++mutation.sample_rate_hz;
  expect(
      !emke::audio::matches_virtual_audio_format(mutation),
      "sample-rate drift must fail closed");
  mutation = virtualAudioFormat;
  ++mutation.channel_count;
  expect(
      !emke::audio::matches_virtual_audio_format(mutation),
      "channel drift must fail closed");
  mutation = virtualAudioFormat;
  mutation.format_tag = 1u;
  expect(
      !emke::audio::matches_virtual_audio_format(mutation),
      "non-Float format drift must fail closed");
  mutation = virtualAudioFormat;
  mutation.bits_per_sample = 16u;
  expect(
      !emke::audio::matches_virtual_audio_format(mutation),
      "container-bit drift must fail closed");
  mutation = virtualAudioFormat;
  mutation.valid_bits_per_sample = 24u;
  expect(
      !emke::audio::matches_virtual_audio_format(mutation),
      "valid-bit drift must fail closed");
  mutation = virtualAudioFormat;
  mutation.block_align = 4u;
  expect(
      !emke::audio::matches_virtual_audio_format(mutation),
      "block-alignment drift must fail closed");
  mutation = virtualAudioFormat;
  --mutation.average_bytes_per_second;
  expect(
      !emke::audio::matches_virtual_audio_format(mutation),
      "byte-rate drift must fail closed");

  if (failures != 0) {
    return 1;
  }
  std::puts("EMKE virtual format authority tests passed.");
  return 0;
}
