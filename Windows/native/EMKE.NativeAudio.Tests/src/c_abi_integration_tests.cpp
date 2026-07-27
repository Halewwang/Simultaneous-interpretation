#include "emke_native_audio.h"
#include "native_audio_test_hooks.h"

#include <algorithm>
#include <array>
#include <cstdint>
#include <iostream>
#include <string_view>
#include <vector>

namespace {

class TestContext {
 public:
  void expect(bool condition, std::string_view expression, int line) {
    if (condition) {
      return;
    }
    ++failures_;
    std::cerr << line << ": expected " << expression << '\n';
  }

  [[nodiscard]] int failures() const {
    return failures_;
  }

 private:
  int failures_ = 0;
};

#define EXPECT(context, expression) \
  (context).expect((expression), #expression, __LINE__)

emke_audio_config valid_config() {
  emke_audio_config config{};
  config.size = sizeof(config);
  config.abi_version = EMKE_AUDIO_ABI_VERSION;
  return config;
}

emke_audio_event valid_event() {
  emke_audio_event event{};
  event.size = sizeof(event);
  event.abi_version = EMKE_AUDIO_ABI_VERSION;
  return event;
}

emke_audio_diagnostics valid_diagnostics() {
  emke_audio_diagnostics diagnostics{};
  diagnostics.size = sizeof(diagnostics);
  diagnostics.abi_version = EMKE_AUDIO_ABI_VERSION;
  return diagnostics;
}

emke_audio_endpoint_snapshot valid_endpoint_snapshot() {
  emke_audio_endpoint_snapshot snapshot{};
  snapshot.size = sizeof(snapshot);
  snapshot.abi_version = EMKE_AUDIO_ABI_VERSION;
  return snapshot;
}

std::vector<float> stereo_block(float sample) {
  return std::vector<float>(
      EMKE_AUDIO_LOCAL_CYCLE_FRAMES * 2u, sample);
}

emke_audio_handle* started_handle(TestContext& context) {
  auto config = valid_config();
  emke_audio_handle* handle = nullptr;
  EXPECT(context, emke_audio_create(&config, &handle) == EMKE_AUDIO_OK);
  EXPECT(context, handle != nullptr);
  EXPECT(context, emke_audio_start(handle) == EMKE_AUDIO_OK);
  return handle;
}

void test_poll_capacity_retry_preserves_event(TestContext& context) {
  emke_audio_handle* handle = started_handle(context);
  const auto local = stereo_block(0.25f);
  EXPECT(context,
         emke_audio_test_accept_synthetic_float32(
             handle,
             EMKE_AUDIO_TEST_DIRECTION_INBOUND,
             local.data(),
             EMKE_AUDIO_LOCAL_CYCLE_FRAMES) == EMKE_AUDIO_OK);

  auto event = valid_event();
  EXPECT(context,
         emke_audio_poll_event(handle, &event, nullptr, 0u) ==
             EMKE_AUDIO_INVALID_ARGUMENT);
  EXPECT(context, event.kind == EMKE_AUDIO_EVENT_INBOUND_PCM16);
  EXPECT(context, event.frame_count == 240u);
  const std::uint64_t sequence = event.sequence;

  std::array<std::int16_t, 240> pcm16{};
  event = valid_event();
  EXPECT(context,
         emke_audio_poll_event(
             handle, &event, pcm16.data(), pcm16.size()) == EMKE_AUDIO_OK);
  EXPECT(context, event.kind == EMKE_AUDIO_EVENT_INBOUND_PCM16);
  EXPECT(context, event.sequence == sequence);
  EXPECT(context, event.frame_count == 240u);
  EXPECT(context, pcm16.front() == 8192);
  EXPECT(context, pcm16.back() == 8192);

  auto diagnostics = valid_diagnostics();
  EXPECT(context,
         emke_audio_get_diagnostics(handle, &diagnostics) == EMKE_AUDIO_OK);
  EXPECT(context, diagnostics.dropped_frames == 0u);
  EXPECT(context, diagnostics.queue_full_events == 0u);

  event = valid_event();
  EXPECT(context,
         emke_audio_poll_event(handle, &event, nullptr, 0u) == EMKE_AUDIO_OK);
  EXPECT(context, event.kind == EMKE_AUDIO_EVENT_NONE);
  emke_audio_destroy(handle);
}

void test_public_outbound_safety_and_bypass(TestContext& context) {
  emke_audio_handle* handle = started_handle(context);
  const auto local = stereo_block(0.25f);
  EXPECT(context,
         emke_audio_test_accept_synthetic_float32(
             handle,
             EMKE_AUDIO_TEST_DIRECTION_OUTBOUND,
             local.data(),
             EMKE_AUDIO_LOCAL_CYCLE_FRAMES) == EMKE_AUDIO_OK);
  EXPECT(context,
         emke_audio_set_outbound_route(
             handle, EMKE_AUDIO_ROUTE_ORIGINAL_FAIL_OPEN) ==
             EMKE_AUDIO_INVALID_ARGUMENT);
  EXPECT(context,
         emke_audio_set_inbound_route(
             handle, EMKE_AUDIO_ROUTE_MUTED_FAIL_CLOSED) ==
             EMKE_AUDIO_INVALID_ARGUMENT);

  const std::array<std::int16_t, 1> translated = {77};
  EXPECT(context,
         emke_audio_enqueue_outbound_translation(
             handle, translated.data(), translated.size()) == EMKE_AUDIO_OK);
  std::array<std::int16_t, 1> rendered{};
  EXPECT(context,
         emke_audio_test_render_pcm16(
             handle,
             EMKE_AUDIO_TEST_DIRECTION_OUTBOUND,
             rendered.data(),
             rendered.size()) == EMKE_AUDIO_OK);
  EXPECT(context, rendered[0] == 77);

  rendered[0] = 1;
  EXPECT(context,
         emke_audio_test_render_pcm16(
             handle,
             EMKE_AUDIO_TEST_DIRECTION_OUTBOUND,
             rendered.data(),
             rendered.size()) == EMKE_AUDIO_OK);
  EXPECT(context, rendered[0] == 0);
  EXPECT(context,
         emke_audio_enqueue_outbound_translation(
             handle, translated.data(), translated.size()) == EMKE_AUDIO_OK);
  rendered[0] = 1;
  EXPECT(context,
         emke_audio_test_render_pcm16(
             handle,
             EMKE_AUDIO_TEST_DIRECTION_OUTBOUND,
             rendered.data(),
             rendered.size()) == EMKE_AUDIO_OK);
  EXPECT(context, rendered[0] == 0);

  EXPECT(context,
         emke_audio_set_outbound_route(
             handle, EMKE_AUDIO_ROUTE_ORIGINAL_BYPASS) == EMKE_AUDIO_OK);
  rendered[0] = 0;
  EXPECT(context,
         emke_audio_test_render_pcm16(
             handle,
             EMKE_AUDIO_TEST_DIRECTION_OUTBOUND,
             rendered.data(),
             rendered.size()) == EMKE_AUDIO_OK);
  EXPECT(context, rendered[0] == 8192);

  auto diagnostics = valid_diagnostics();
  EXPECT(context,
         emke_audio_get_diagnostics(handle, &diagnostics) == EMKE_AUDIO_OK);
  EXPECT(context, diagnostics.outbound_underruns == 1u);
  EXPECT(context, diagnostics.consumed_outbound_translation_frames == 1u);
  EXPECT(context, diagnostics.queued_outbound_translation_frames == 0u);
  emke_audio_destroy(handle);
}

void test_public_inbound_fail_open_persists(TestContext& context) {
  emke_audio_handle* handle = started_handle(context);
  const auto local = stereo_block(0.25f);
  EXPECT(context,
         emke_audio_test_accept_synthetic_float32(
             handle,
             EMKE_AUDIO_TEST_DIRECTION_INBOUND,
             local.data(),
             EMKE_AUDIO_LOCAL_CYCLE_FRAMES) == EMKE_AUDIO_OK);
  const std::array<std::int16_t, 1> translated = {-1234};
  EXPECT(context,
         emke_audio_enqueue_inbound_translation(
             handle, translated.data(), translated.size()) == EMKE_AUDIO_OK);
  EXPECT(context,
         emke_audio_test_inject_failure(
             handle, EMKE_AUDIO_TEST_FAILURE_INBOUND_TRANSLATION) ==
             EMKE_AUDIO_OK);

  std::array<std::int16_t, 1> rendered{};
  EXPECT(context,
         emke_audio_test_render_pcm16(
             handle,
             EMKE_AUDIO_TEST_DIRECTION_INBOUND,
             rendered.data(),
             rendered.size()) == EMKE_AUDIO_OK);
  EXPECT(context, rendered[0] == 8192);
  EXPECT(context,
         emke_audio_enqueue_inbound_translation(
             handle, translated.data(), translated.size()) == EMKE_AUDIO_OK);
  rendered[0] = 0;
  EXPECT(context,
         emke_audio_test_render_pcm16(
             handle,
             EMKE_AUDIO_TEST_DIRECTION_INBOUND,
             rendered.data(),
             rendered.size()) == EMKE_AUDIO_OK);
  EXPECT(context, rendered[0] == 8192);

  auto diagnostics = valid_diagnostics();
  EXPECT(context,
         emke_audio_get_diagnostics(handle, &diagnostics) == EMKE_AUDIO_OK);
  EXPECT(context,
         diagnostics.inbound_route == EMKE_AUDIO_ROUTE_ORIGINAL_FAIL_OPEN);
  EXPECT(context, diagnostics.queued_inbound_translation_frames == 0u);
  EXPECT(context, diagnostics.consumed_inbound_translation_frames == 0u);
  emke_audio_destroy(handle);
}

void test_running_device_failure_is_observable(TestContext& context) {
  emke_audio_handle* handle = started_handle(context);
  EXPECT(context,
         emke_audio_test_inject_failure(
             handle, EMKE_AUDIO_TEST_FAILURE_DEVICE) == EMKE_AUDIO_OK);

  auto diagnostics = valid_diagnostics();
  EXPECT(context,
         emke_audio_get_diagnostics(handle, &diagnostics) == EMKE_AUDIO_OK);
  EXPECT(context, diagnostics.is_running == 0u);
  EXPECT(context, diagnostics.inbound_route == EMKE_AUDIO_ROUTE_STOPPED);
  EXPECT(context, diagnostics.outbound_route == EMKE_AUDIO_ROUTE_STOPPED);
  EXPECT(context, diagnostics.device_failures == 1u);

  auto event = valid_event();
  EXPECT(context,
         emke_audio_poll_event(handle, &event, nullptr, 0u) == EMKE_AUDIO_OK);
  EXPECT(context, event.kind == EMKE_AUDIO_EVENT_DEVICE_CHANGED);
  EXPECT(context, event.status == EMKE_AUDIO_DEVICE_MISSING);
  const std::array<std::int16_t, 1> translated = {1};
  EXPECT(context,
         emke_audio_enqueue_outbound_translation(
             handle, translated.data(), translated.size()) ==
             EMKE_AUDIO_NOT_RUNNING);
  EXPECT(context, emke_audio_start(handle) == EMKE_AUDIO_OK);
  emke_audio_destroy(handle);
}

void test_public_same_route_reassertion_preserves_audio(
    TestContext& context) {
  emke_audio_handle* handle = started_handle(context);
  const std::array<std::int16_t, 2> inbound = {31, 32};
  const std::array<std::int16_t, 2> outbound = {41, 42};
  EXPECT(context,
         emke_audio_enqueue_inbound_translation(
             handle, inbound.data(), inbound.size()) == EMKE_AUDIO_OK);
  EXPECT(context,
         emke_audio_enqueue_outbound_translation(
             handle, outbound.data(), outbound.size()) == EMKE_AUDIO_OK);

  EXPECT(context,
         emke_audio_set_inbound_route(
             handle, EMKE_AUDIO_ROUTE_TRANSLATED) == EMKE_AUDIO_OK);
  EXPECT(context,
         emke_audio_set_outbound_route(
             handle, EMKE_AUDIO_ROUTE_TRANSLATED) == EMKE_AUDIO_OK);

  auto diagnostics = valid_diagnostics();
  EXPECT(context,
         emke_audio_get_diagnostics(handle, &diagnostics) == EMKE_AUDIO_OK);
  EXPECT(context, diagnostics.queued_inbound_translation_frames == 2u);
  EXPECT(context, diagnostics.queued_outbound_translation_frames == 2u);
  EXPECT(context, diagnostics.dropped_frames == 0u);

  std::array<std::int16_t, 2> rendered_inbound{};
  std::array<std::int16_t, 2> rendered_outbound{};
  EXPECT(context,
         emke_audio_test_render_pcm16(
             handle,
             EMKE_AUDIO_TEST_DIRECTION_INBOUND,
             rendered_inbound.data(),
             rendered_inbound.size()) == EMKE_AUDIO_OK);
  EXPECT(context,
         emke_audio_test_render_pcm16(
             handle,
             EMKE_AUDIO_TEST_DIRECTION_OUTBOUND,
             rendered_outbound.data(),
             rendered_outbound.size()) == EMKE_AUDIO_OK);
  EXPECT(context, rendered_inbound == inbound);
  EXPECT(context, rendered_outbound == outbound);

  diagnostics = valid_diagnostics();
  EXPECT(context,
         emke_audio_get_diagnostics(handle, &diagnostics) == EMKE_AUDIO_OK);
  EXPECT(context, diagnostics.consumed_inbound_translation_frames == 2u);
  EXPECT(context, diagnostics.consumed_outbound_translation_frames == 2u);
  EXPECT(context, diagnostics.dropped_frames == 0u);
  emke_audio_destroy(handle);
}

void test_discovery_validates_snapshot_and_reports_platform_source_error(
    TestContext& context) {
  auto snapshot = valid_endpoint_snapshot();
  snapshot.size = sizeof(snapshot) - 1u;
  EXPECT(
      context,
      emke_audio_discover_endpoints(&snapshot) == EMKE_AUDIO_INVALID_ARGUMENT);

  snapshot = valid_endpoint_snapshot();
  snapshot.abi_version = EMKE_AUDIO_ABI_VERSION + 1u;
  EXPECT(
      context,
      emke_audio_discover_endpoints(&snapshot) == EMKE_AUDIO_ABI_MISMATCH);

  snapshot = valid_endpoint_snapshot();
  EXPECT(context, emke_audio_discover_endpoints(&snapshot) == EMKE_AUDIO_OK);
#if defined(_WIN32)
  // Physical-lab status is intentionally exercised only by the Windows lab.
  EXPECT(
      context,
      snapshot.discovery_status <= EMKE_AUDIO_ENDPOINT_DISCOVERY_SOURCE_ERROR);
#else
  EXPECT(
      context,
      snapshot.discovery_status == EMKE_AUDIO_ENDPOINT_DISCOVERY_SOURCE_ERROR);
#endif
}

}  // namespace

int main() {
  TestContext context;
  test_poll_capacity_retry_preserves_event(context);
  test_public_outbound_safety_and_bypass(context);
  test_public_inbound_fail_open_persists(context);
  test_running_device_failure_is_observable(context);
  test_public_same_route_reassertion_preserves_audio(context);
  test_discovery_validates_snapshot_and_reports_platform_source_error(context);
  return std::min(context.failures(), 255);
}
