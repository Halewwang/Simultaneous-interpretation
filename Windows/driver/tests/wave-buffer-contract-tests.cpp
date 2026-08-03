#include "../EMKE.VirtualAudio/src/emke_wave_buffer_contract.h"
#include "../../shared/emke_endpoint_contract.h"

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
  expect(
      EmkeIsNotificationBufferValid(
          EMKE_AUDIO_BLOCK_ALIGN * 480u,
          4u,
          EMKE_AUDIO_BLOCK_ALIGN),
      "a four-packet Float32 stereo buffer must be accepted");
  expect(
      !EmkeIsNotificationBufferValid(
          18u,
          3u,
          EMKE_AUDIO_BLOCK_ALIGN),
      "an 18-byte request must not pass divisibility before truncation");
  expect(
      !EmkeIsNotificationBufferValid(
          EMKE_AUDIO_BLOCK_ALIGN * 2u,
          3u,
          EMKE_AUDIO_BLOCK_ALIGN),
      "each notification packet must contain whole frames");
  expect(
      !EmkeIsNotificationBufferValid(
          EMKE_AUDIO_BLOCK_ALIGN + 1u,
          1u,
          EMKE_AUDIO_BLOCK_ALIGN),
      "the requested buffer itself must contain whole frames");
  expect(
      !EmkeIsNotificationBufferValid(
          EMKE_AUDIO_BLOCK_ALIGN,
          0u,
          EMKE_AUDIO_BLOCK_ALIGN),
      "zero notifications must fail closed");
  expect(
      !EmkeIsNotificationBufferValid(
          EMKE_AUDIO_BLOCK_ALIGN,
          1u,
          0u),
      "zero block alignment must fail closed");

  if (failures != 0) {
    return 1;
  }
  std::puts("EMKE WaveRT buffer contract tests passed.");
  return 0;
}
