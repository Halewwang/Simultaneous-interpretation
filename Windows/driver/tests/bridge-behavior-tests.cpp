#include "../EMKE.VirtualAudio/src/emke_audio_bridge.h"
#include "../../shared/emke_endpoint_contract.h"

#include <array>
#include <cstddef>
#include <cstdio>
#include <string_view>
#include <type_traits>

namespace {

int failures = 0;

void expect(bool condition, const char* message) {
  if (!condition) {
    std::fprintf(stderr, "FAIL: %s\n", message);
    ++failures;
  }
}

template <std::size_t Size>
void expect_samples(
    const std::array<float, Size>& actual,
    const std::array<float, Size>& expected,
    const char* message) {
  for (std::size_t index = 0; index < Size; ++index) {
    if (actual[index] != expected[index]) {
      std::fprintf(
          stderr,
          "FAIL: %s at sample %zu (expected %.3f, got %.3f)\n",
          message,
          index,
          expected[index],
          actual[index]);
      ++failures;
      return;
    }
  }
}

template <std::size_t Size>
void expect_zero(
    const std::array<float, Size>& actual,
    const char* message) {
  for (std::size_t index = 0; index < Size; ++index) {
    if (actual[index] != 0.0f) {
      std::fprintf(
          stderr,
          "FAIL: %s at sample %zu (got %.3f)\n",
          message,
          index,
          actual[index]);
      ++failures;
      return;
    }
  }
}

void test_bridge_a_routes_only_to_app_speaker_capture() {
  static EmkeAudioBridgeSet bridges;
  EmkeAudioBridgeInitialize(&bridges);

  constexpr std::array<float, 6> input{
      0.125f, -0.125f, 0.25f, -0.25f, 0.5f, -0.5f};
  expect(
      EmkeAudioBridgeWrite(
          &bridges,
          EmkeBridgeEndpoint::meetingSpeakerRender,
          input.data(),
          3u) == 3u,
      "Bridge A must accept meeting-speaker render frames");

  std::array<float, 6> wrong_capture;
  wrong_capture.fill(9.0f);
  expect(
      EmkeAudioBridgeRead(
          &bridges,
          EmkeBridgeEndpoint::meetingMicrophoneCapture,
          wrong_capture.data(),
          3u) == 0u,
      "Bridge A must not route into meeting-microphone capture");
  expect_zero(
      wrong_capture,
      "a capture endpoint fed from the wrong bridge must receive silence");

  std::array<float, 6> output{};
  expect(
      EmkeAudioBridgeRead(
          &bridges,
          EmkeBridgeEndpoint::appSpeakerCapture,
          output.data(),
          3u) == 3u,
      "Bridge A must deliver all frames to app-speaker capture");
  expect_samples(output, input, "Bridge A must preserve Float32 stereo samples");
}

void test_bridge_b_routes_only_to_meeting_microphone_capture() {
  static EmkeAudioBridgeSet bridges;
  EmkeAudioBridgeInitialize(&bridges);

  constexpr std::array<float, 8> input{
      -0.75f, 0.75f, -0.5f, 0.5f, -0.25f, 0.25f, -0.1f, 0.1f};
  expect(
      EmkeAudioBridgeWrite(
          &bridges,
          EmkeBridgeEndpoint::appMicrophoneRender,
          input.data(),
          4u) == 4u,
      "Bridge B must accept app-microphone render frames");

  std::array<float, 8> output{};
  expect(
      EmkeAudioBridgeRead(
          &bridges,
          EmkeBridgeEndpoint::meetingMicrophoneCapture,
          output.data(),
          4u) == 4u,
      "Bridge B must deliver all frames to meeting-microphone capture");
  expect_samples(output, input, "Bridge B must preserve Float32 stereo samples");

  std::array<float, 2> wrong_capture{4.0f, 4.0f};
  expect(
      EmkeAudioBridgeRead(
          &bridges,
          EmkeBridgeEndpoint::appSpeakerCapture,
          wrong_capture.data(),
          1u) == 0u,
      "Bridge B must not route into app-speaker capture");
  expect_zero(
      wrong_capture,
      "the unrelated capture path must stay silent");
}

void test_capture_underrun_is_zero_filled() {
  static EmkeAudioBridgeSet bridges;
  EmkeAudioBridgeInitialize(&bridges);

  std::array<float, 10> output;
  output.fill(1.0f);
  expect(
      EmkeAudioBridgeRead(
          &bridges,
          EmkeBridgeEndpoint::appSpeakerCapture,
          output.data(),
          5u) == 0u,
      "an empty bridge must report zero delivered frames");
  expect_zero(output, "an empty bridge must zero the complete capture request");
}

void test_overrun_drops_newest_frames_without_overwriting_unread_data() {
  static EmkeAudioBridgeSet bridges;
  EmkeAudioBridgeInitialize(&bridges);

  std::array<float, (EMKE_AUDIO_BRIDGE_CAPACITY_FRAMES + 2u) *
                        EMKE_AUDIO_CHANNEL_COUNT>
      input{};
  for (std::size_t frame = 0; frame < input.size() / 2u; ++frame) {
    input[frame * 2u] = static_cast<float>(frame);
    input[frame * 2u + 1u] = -static_cast<float>(frame);
  }

  expect(
      EmkeAudioBridgeWrite(
          &bridges,
          EmkeBridgeEndpoint::meetingSpeakerRender,
          input.data(),
          EMKE_AUDIO_BRIDGE_CAPACITY_FRAMES + 2u) ==
          EMKE_AUDIO_BRIDGE_CAPACITY_FRAMES,
      "the bounded bridge must deterministically drop newest overflow frames");

  std::array<float, EMKE_AUDIO_BRIDGE_CAPACITY_FRAMES *
                        EMKE_AUDIO_CHANNEL_COUNT>
      output{};
  expect(
      EmkeAudioBridgeRead(
          &bridges,
          EmkeBridgeEndpoint::appSpeakerCapture,
          output.data(),
          EMKE_AUDIO_BRIDGE_CAPACITY_FRAMES) ==
          EMKE_AUDIO_BRIDGE_CAPACITY_FRAMES,
      "the bridge must retain exactly its fixed capacity");
  for (std::size_t index = 0; index < output.size(); ++index) {
    if (output[index] != input[index]) {
      expect(false, "overflow must not overwrite unread frames");
      break;
    }
  }

  std::array<float, 4> tail{7.0f, 7.0f, 7.0f, 7.0f};
  expect(
      EmkeAudioBridgeRead(
          &bridges,
          EmkeBridgeEndpoint::appSpeakerCapture,
          tail.data(),
          2u) == 0u,
      "dropped overflow frames must not reappear later");
  expect_zero(tail, "post-overrun underrun must be silence");
}

void test_reset_discards_prior_session_samples() {
  static EmkeAudioBridgeSet bridges;
  EmkeAudioBridgeInitialize(&bridges);

  constexpr std::array<float, 4> old_session{0.8f, -0.8f, 0.6f, -0.6f};
  expect(
      EmkeAudioBridgeWrite(
          &bridges,
          EmkeBridgeEndpoint::appMicrophoneRender,
          old_session.data(),
          2u) == 2u,
      "the old session must reach the bridge before reset");

  EmkeAudioBridgeReset(
      &bridges,
      EmkeBridgeEndpoint::meetingMicrophoneCapture);

  std::array<float, 4> after_reset;
  after_reset.fill(2.0f);
  expect(
      EmkeAudioBridgeRead(
          &bridges,
          EmkeBridgeEndpoint::meetingMicrophoneCapture,
          after_reset.data(),
          2u) == 0u,
      "reset must discard unread prior-session frames");
  expect_zero(after_reset, "reset must fail closed to silence");

  constexpr std::array<float, 2> new_session{0.2f, -0.2f};
  expect(
      EmkeAudioBridgeWrite(
          &bridges,
          EmkeBridgeEndpoint::appMicrophoneRender,
          new_session.data(),
          1u) == 1u,
      "the bridge must accept frames after reset");
  std::array<float, 2> output{};
  expect(
      EmkeAudioBridgeRead(
          &bridges,
          EmkeBridgeEndpoint::meetingMicrophoneCapture,
          output.data(),
          1u) == 1u,
      "the capture session must receive only post-reset frames");
  expect_samples(output, new_session, "reset must not corrupt the new session");
}

void test_shared_format_and_role_contract() {
  static_assert(std::is_standard_layout_v<EmkeAudioBridge>);
  static_assert(std::is_trivially_destructible_v<EmkeAudioBridge>);
  static_assert(noexcept(EmkeAudioBridgeWrite(
      static_cast<EmkeAudioBridgeSet*>(nullptr),
      EmkeBridgeEndpoint::meetingSpeakerRender,
      static_cast<const float*>(nullptr),
      0u)));
  static_assert(noexcept(EmkeAudioBridgeRead(
      static_cast<EmkeAudioBridgeSet*>(nullptr),
      EmkeBridgeEndpoint::appSpeakerCapture,
      static_cast<float*>(nullptr),
      0u)));

  expect(EMKE_DRIVER_ABI == 1u, "driver ABI must remain 1");
  expect(
      EMKE_AUDIO_SAMPLE_RATE == 48'000u,
      "all virtual endpoints must remain at 48 kHz");
  expect(
      EMKE_AUDIO_CHANNEL_COUNT == 2u,
      "all virtual endpoints must remain stereo");
  expect(
      EMKE_AUDIO_BITS_PER_SAMPLE == 32u,
      "all virtual endpoints must remain 32-bit");
  expect(
      EMKE_AUDIO_FORMAT_TAG == 3u,
      "all virtual endpoints must remain IEEE Float32");
  expect(
      EMKE_AUDIO_BLOCK_ALIGN == 8u,
      "Float32 stereo block alignment must remain eight bytes");
  expect(
      EMKE_AUDIO_AVG_BYTES_PER_SECOND == 384'000u,
      "Float32 stereo byte rate must remain exact");
  expect(
      std::string_view{EMKE_ROLE_MEETING_SPEAKER_RENDER_UTF8} ==
          "emke.meeting-speaker.render",
      "meeting-speaker role must come from the shared header");
  expect(
      std::string_view{EMKE_ROLE_APP_SPEAKER_CAPTURE_UTF8} ==
          "emke.app-speaker.capture",
      "app-speaker role must come from the shared header");
  expect(
      std::string_view{EMKE_ROLE_APP_MICROPHONE_RENDER_UTF8} ==
          "emke.app-microphone.render",
      "app-microphone role must come from the shared header");
  expect(
      std::string_view{EMKE_ROLE_MEETING_MICROPHONE_CAPTURE_UTF8} ==
          "emke.meeting-microphone.capture",
      "meeting-microphone role must come from the shared header");
}

}  // namespace

int main() {
  test_bridge_a_routes_only_to_app_speaker_capture();
  test_bridge_b_routes_only_to_meeting_microphone_capture();
  test_capture_underrun_is_zero_filled();
  test_overrun_drops_newest_frames_without_overwriting_unread_data();
  test_reset_discards_prior_session_samples();
  test_shared_format_and_role_contract();

  if (failures != 0) {
    std::fprintf(stderr, "%d bridge behavior assertion(s) failed\n", failures);
    return 1;
  }
  std::puts("EMKE driver bridge behavior tests passed (6 cases).");
  return 0;
}
