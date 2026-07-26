#include "emke_native_audio.h"
#include "fake_audio_backend.hpp"

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <iostream>
#include <span>
#include <string_view>
#include <type_traits>
#include <vector>

struct RegisteredTest {
  const char* group;
  const char* name;
  int (*function)();
};

namespace {

template <typename Type, typename = void>
struct IsComplete : std::false_type {};

template <typename Type>
struct IsComplete<Type, std::void_t<decltype(sizeof(Type))>> : std::true_type {
};

class TestContext {
 public:
  void expect(bool condition,
              std::string_view expression,
              std::string_view test_name,
              int line) {
    if (condition) {
      return;
    }

    ++failed_assertions_;
    std::cerr << test_name << ':' << line << ": expected " << expression << '\n';
  }

  [[nodiscard]] int failed_assertions() const {
    return failed_assertions_;
  }

 private:
  int failed_assertions_ = 0;
};

#define EXPECT(context, expression, test_name) \
  (context).expect((expression), #expression, (test_name), __LINE__)

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

std::vector<float> stereo_block(std::size_t local_frames, float sample) {
  return std::vector<float>(local_frames * 2u, sample);
}

void test_public_abi_layout(TestContext& context) {
  constexpr std::string_view name = "public ABI layout";

  EXPECT(context, EMKE_AUDIO_ABI_VERSION == 1u, name);
  EXPECT(context, EMKE_AUDIO_LOCAL_SAMPLE_RATE_HZ == 48'000u, name);
  EXPECT(context, EMKE_AUDIO_NETWORK_SAMPLE_RATE_HZ == 24'000u, name);
  EXPECT(context, EMKE_AUDIO_LOCAL_CYCLE_FRAMES == 480u, name);
  EXPECT(context,
         EMKE_AUDIO_CAPTURE_CAPACITY_LOCAL_FRAMES == 4'800u,
         name);
  EXPECT(context,
         EMKE_AUDIO_TRANSLATED_PLAYBACK_CAPACITY_LOCAL_FRAMES == 96'000u,
         name);
  EXPECT(context,
         EMKE_AUDIO_TRANSLATED_QUEUE_CAPACITY_NETWORK_FRAMES == 48'000u,
         name);
  EXPECT(context,
         EMKE_AUDIO_TRANSLATED_QUEUE_CAPACITY_NETWORK_FRAMES *
                 EMKE_AUDIO_LOCAL_SAMPLE_RATE_HZ ==
             EMKE_AUDIO_TRANSLATED_PLAYBACK_CAPACITY_LOCAL_FRAMES *
                 EMKE_AUDIO_NETWORK_SAMPLE_RATE_HZ,
         name);
  EXPECT(context, EMKE_AUDIO_OK == 0, name);
  EXPECT(context, EMKE_AUDIO_INVALID_ARGUMENT == 1, name);
  EXPECT(context, EMKE_AUDIO_ABI_MISMATCH == 2, name);
  EXPECT(context, EMKE_AUDIO_DEVICE_MISSING == 3, name);
  EXPECT(context, EMKE_AUDIO_FORMAT_UNSUPPORTED == 4, name);
  EXPECT(context, EMKE_AUDIO_QUEUE_FULL == 5, name);
  EXPECT(context, EMKE_AUDIO_NOT_RUNNING == 6, name);
  EXPECT(context, EMKE_AUDIO_INTERNAL_ERROR == 7, name);
  EXPECT(context, EMKE_AUDIO_ROUTE_STOPPED == 0, name);
  EXPECT(context, EMKE_AUDIO_ROUTE_TRANSLATED == 1, name);
  EXPECT(context, EMKE_AUDIO_ROUTE_ORIGINAL_FAIL_OPEN == 2, name);
  EXPECT(context, EMKE_AUDIO_ROUTE_ORIGINAL_BYPASS == 3, name);
  EXPECT(context, EMKE_AUDIO_ROUTE_MUTED_FAIL_CLOSED == 4, name);
  EXPECT(context, EMKE_AUDIO_EVENT_NONE == 0, name);
  EXPECT(context, EMKE_AUDIO_EVENT_INBOUND_PCM16 == 1, name);
  EXPECT(context, EMKE_AUDIO_EVENT_OUTBOUND_PCM16 == 2, name);
  EXPECT(context, EMKE_AUDIO_EVENT_DEVICE_CHANGED == 3, name);
  EXPECT(context, EMKE_AUDIO_EVENT_STREAM_ERROR == 4, name);
  EXPECT(context, EMKE_AUDIO_EVENT_BACKPRESSURE == 5, name);
  EXPECT(context, sizeof(emke_audio_status) == sizeof(std::int32_t), name);
  EXPECT(context, sizeof(emke_audio_route) == sizeof(std::int32_t), name);
  EXPECT(context, sizeof(emke_audio_event_kind) == sizeof(std::int32_t), name);

  EXPECT(context, offsetof(emke_audio_config, size) == 0u, name);
  EXPECT(context,
         offsetof(emke_audio_config, abi_version) == sizeof(std::uint32_t),
         name);
  EXPECT(context,
         offsetof(emke_audio_config, physical_input_endpoint_id) == 8u,
         name);
  EXPECT(context,
         offsetof(emke_audio_config, physical_output_endpoint_id) == 1'032u,
         name);
  EXPECT(context,
         offsetof(emke_audio_config, virtual_speaker_render_endpoint_id) ==
             2'056u,
         name);
  EXPECT(context,
         offsetof(emke_audio_config, virtual_speaker_capture_endpoint_id) ==
             3'080u,
         name);
  EXPECT(context,
         offsetof(emke_audio_config, virtual_microphone_render_endpoint_id) ==
             4'104u,
         name);
  EXPECT(context,
         offsetof(emke_audio_config, virtual_microphone_capture_endpoint_id) ==
             5'128u,
         name);
  EXPECT(context, offsetof(emke_audio_event, size) == 0u, name);
  EXPECT(context,
         offsetof(emke_audio_event, abi_version) == sizeof(std::uint32_t),
         name);
  EXPECT(context, offsetof(emke_audio_event, kind) == 8u, name);
  EXPECT(context, offsetof(emke_audio_event, status) == 12u, name);
  EXPECT(context, offsetof(emke_audio_event, route) == 16u, name);
  EXPECT(context, offsetof(emke_audio_event, frame_count) == 20u, name);
  EXPECT(context, offsetof(emke_audio_event, sequence) == 24u, name);
  EXPECT(context, offsetof(emke_audio_diagnostics, size) == 0u, name);
  EXPECT(context,
         offsetof(emke_audio_diagnostics, abi_version) ==
             sizeof(std::uint32_t),
         name);
  EXPECT(context, offsetof(emke_audio_diagnostics, is_running) == 8u, name);
  EXPECT(context,
         offsetof(emke_audio_diagnostics, inbound_route) == 12u,
         name);
  EXPECT(context,
         offsetof(emke_audio_diagnostics, outbound_route) == 16u,
         name);
  EXPECT(context,
         offsetof(emke_audio_diagnostics,
                  queued_inbound_translation_frames) == 20u,
         name);
  EXPECT(context,
         offsetof(emke_audio_diagnostics,
                  queued_outbound_translation_frames) == 24u,
         name);
  EXPECT(context,
         offsetof(emke_audio_diagnostics, captured_inbound_frames) == 32u,
         name);
  EXPECT(context,
         offsetof(emke_audio_diagnostics, device_failures) == 96u,
         name);

  EXPECT(context, std::is_standard_layout_v<emke_audio_config>, name);
  EXPECT(context, std::is_standard_layout_v<emke_audio_event>, name);
  EXPECT(context, std::is_standard_layout_v<emke_audio_diagnostics>, name);
  EXPECT(context, !IsComplete<emke_audio_handle>::value, name);
  EXPECT(context,
         sizeof(emke_audio_config) ==
             8u + (6u * EMKE_AUDIO_ENDPOINT_ID_CAPACITY *
                   sizeof(std::uint16_t)),
         name);
  EXPECT(context, sizeof(emke_audio_event) == 32u, name);
  EXPECT(context, sizeof(emke_audio_diagnostics) == 104u, name);

  using CreateFunction = emke_audio_status (*)(
      const emke_audio_config*, emke_audio_handle**);
  using DestroyFunction = void (*)(emke_audio_handle*);
  using LifecycleFunction = emke_audio_status (*)(emke_audio_handle*);
  using RouteFunction =
      emke_audio_status (*)(emke_audio_handle*, emke_audio_route);
  using EnqueueFunction = emke_audio_status (*)(
      emke_audio_handle*, const std::int16_t*, std::uint32_t);
  using PollFunction = emke_audio_status (*)(
      emke_audio_handle*,
      emke_audio_event*,
      std::int16_t*,
      std::uint32_t);
  using DiagnosticsFunction =
      emke_audio_status (*)(emke_audio_handle*, emke_audio_diagnostics*);

  EXPECT(context,
         (std::is_same_v<decltype(&emke_audio_create), CreateFunction>),
         name);
  EXPECT(context,
         (std::is_same_v<decltype(&emke_audio_destroy), DestroyFunction>),
         name);
  EXPECT(context,
         (std::is_same_v<decltype(&emke_audio_start), LifecycleFunction>),
         name);
  EXPECT(context,
         (std::is_same_v<decltype(&emke_audio_stop), LifecycleFunction>),
         name);
  EXPECT(context,
         (std::is_same_v<decltype(&emke_audio_set_inbound_route),
                         RouteFunction>),
         name);
  EXPECT(context,
         (std::is_same_v<decltype(&emke_audio_set_outbound_route),
                         RouteFunction>),
         name);
  EXPECT(context,
         (std::is_same_v<decltype(&emke_audio_enqueue_inbound_translation),
                         EnqueueFunction>),
         name);
  EXPECT(context,
         (std::is_same_v<decltype(&emke_audio_enqueue_outbound_translation),
                         EnqueueFunction>),
         name);
  EXPECT(context,
         (std::is_same_v<decltype(&emke_audio_poll_event), PollFunction>),
         name);
  EXPECT(context,
         (std::is_same_v<decltype(&emke_audio_get_diagnostics),
                         DiagnosticsFunction>),
         name);
}

void test_struct_validation(TestContext& context) {
  constexpr std::string_view name = "public struct validation";

  auto config = valid_config();
  config.size = sizeof(config) - 1u;
  auto* failed_handle =
      reinterpret_cast<emke_audio_handle*>(static_cast<std::uintptr_t>(1u));
  EXPECT(context,
         emke_audio_create(&config, &failed_handle) ==
             EMKE_AUDIO_INVALID_ARGUMENT,
         name);
  EXPECT(context, failed_handle == nullptr, name);
  emke_audio_destroy(failed_handle);

  config = valid_config();
  config.abi_version = EMKE_AUDIO_ABI_VERSION + 1u;
  EXPECT(context,
         emke_audio_create(&config, &failed_handle) ==
             EMKE_AUDIO_ABI_MISMATCH,
         name);
  EXPECT(context, failed_handle == nullptr, name);

  config = valid_config();
  emke_audio_handle* handle = nullptr;
  EXPECT(context, emke_audio_create(&config, &handle) == EMKE_AUDIO_OK, name);
  EXPECT(context, handle != nullptr, name);
  EXPECT(context, emke_audio_start(handle) == EMKE_AUDIO_OK, name);

  auto event = valid_event();
  event.size = sizeof(event) - 1u;
  EXPECT(context,
         emke_audio_poll_event(handle, &event, nullptr, 0u) ==
             EMKE_AUDIO_INVALID_ARGUMENT,
         name);
  event = valid_event();
  event.abi_version = EMKE_AUDIO_ABI_VERSION + 1u;
  EXPECT(context,
         emke_audio_poll_event(handle, &event, nullptr, 0u) ==
             EMKE_AUDIO_ABI_MISMATCH,
         name);

  auto diagnostics = valid_diagnostics();
  diagnostics.size = sizeof(diagnostics) - 1u;
  EXPECT(context,
         emke_audio_get_diagnostics(handle, &diagnostics) ==
             EMKE_AUDIO_INVALID_ARGUMENT,
         name);
  diagnostics = valid_diagnostics();
  diagnostics.abi_version = EMKE_AUDIO_ABI_VERSION + 1u;
  EXPECT(context,
         emke_audio_get_diagnostics(handle, &diagnostics) ==
             EMKE_AUDIO_ABI_MISMATCH,
         name);

  EXPECT(context, emke_audio_stop(handle) == EMKE_AUDIO_OK, name);
  emke_audio_destroy(handle);
}

void test_handle_lifecycle_and_runtime_state(TestContext& context) {
  constexpr std::string_view name = "handle lifecycle and runtime state";

  emke_audio_destroy(nullptr);

  auto config = valid_config();
  emke_audio_handle* handle = nullptr;
  EXPECT(context, emke_audio_create(&config, &handle) == EMKE_AUDIO_OK, name);
  EXPECT(context, handle != nullptr, name);
  EXPECT(context, emke_audio_start(handle) == EMKE_AUDIO_OK, name);
  EXPECT(context, emke_audio_start(handle) == EMKE_AUDIO_OK, name);

  EXPECT(context,
         emke_audio_set_inbound_route(handle, EMKE_AUDIO_ROUTE_TRANSLATED) ==
             EMKE_AUDIO_OK,
         name);
  EXPECT(context,
         emke_audio_set_outbound_route(
             handle, EMKE_AUDIO_ROUTE_ORIGINAL_BYPASS) == EMKE_AUDIO_OK,
         name);

  auto diagnostics = valid_diagnostics();
  EXPECT(context,
         emke_audio_get_diagnostics(handle, &diagnostics) == EMKE_AUDIO_OK,
         name);
  EXPECT(context, diagnostics.is_running == 1u, name);
  EXPECT(context,
         diagnostics.inbound_route == EMKE_AUDIO_ROUTE_TRANSLATED,
         name);
  EXPECT(context,
         diagnostics.outbound_route == EMKE_AUDIO_ROUTE_ORIGINAL_BYPASS,
         name);

  EXPECT(context, emke_audio_stop(handle) == EMKE_AUDIO_OK, name);
  EXPECT(context, emke_audio_stop(handle) == EMKE_AUDIO_OK, name);

  const std::array<std::int16_t, 2> translated = {100, -100};
  EXPECT(context,
         emke_audio_enqueue_inbound_translation(
             handle, translated.data(), translated.size()) ==
             EMKE_AUDIO_NOT_RUNNING,
         name);
  emke_audio_destroy(handle);
}

void test_c_api_copies_translation_input(TestContext& context) {
  constexpr std::string_view name = "C API copies translation input";

  auto config = valid_config();
  emke_audio_handle* handle = nullptr;
  EXPECT(context, emke_audio_create(&config, &handle) == EMKE_AUDIO_OK, name);
  EXPECT(context, emke_audio_start(handle) == EMKE_AUDIO_OK, name);

  std::array<std::int16_t, 3> inbound = {100, 200, 300};
  EXPECT(context,
         emke_audio_enqueue_inbound_translation(
             handle, inbound.data(), inbound.size()) == EMKE_AUDIO_OK,
         name);
  inbound.fill(0);

  auto diagnostics = valid_diagnostics();
  EXPECT(context,
         emke_audio_get_diagnostics(handle, &diagnostics) == EMKE_AUDIO_OK,
         name);
  EXPECT(context, diagnostics.queued_inbound_translation_frames == 3u, name);

  EXPECT(context, emke_audio_stop(handle) == EMKE_AUDIO_OK, name);
  emke_audio_destroy(handle);
}

void test_fake_capture_conversion_and_events(TestContext& context) {
  constexpr std::string_view name = "fake capture conversion and events";

  emke::audio::FakeAudioBackend backend(8u, 480u);
  EXPECT(context, backend.start() == EMKE_AUDIO_OK, name);

  auto inbound_stereo = stereo_block(480u, 0.5f);
  std::fill(inbound_stereo.begin() + 480, inbound_stereo.end(), -0.5f);
  EXPECT(context,
         backend.accept_synthetic_block(
             emke::audio::Direction::Inbound, inbound_stereo) == EMKE_AUDIO_OK,
         name);

  emke::audio::AudioEvent event{};
  EXPECT(context, backend.poll_event(event) == EMKE_AUDIO_OK, name);
  EXPECT(context, event.kind == EMKE_AUDIO_EVENT_INBOUND_PCM16, name);
  EXPECT(context, event.pcm16.size() == 240u, name);
  EXPECT(context, event.pcm16[0] == 16384, name);
  EXPECT(context, event.pcm16[239] == -16384, name);

  const auto outbound_stereo = stereo_block(480u, 0.25f);
  EXPECT(context,
         backend.accept_synthetic_block(
             emke::audio::Direction::Outbound, outbound_stereo) ==
             EMKE_AUDIO_OK,
         name);
  EXPECT(context, backend.poll_event(event) == EMKE_AUDIO_OK, name);
  EXPECT(context, event.kind == EMKE_AUDIO_EVENT_OUTBOUND_PCM16, name);
  EXPECT(context, event.pcm16.size() == 240u, name);
  EXPECT(context, event.pcm16[0] == 8192, name);
}

void test_fake_translation_queue_and_queue_full(TestContext& context) {
  constexpr std::string_view name = "fake translation queue and queue full";

  emke::audio::FakeAudioBackend backend(3u, 480u);
  EXPECT(context, backend.start() == EMKE_AUDIO_OK, name);

  const std::array<std::int16_t, 3> translated = {7, 8, 9};
  EXPECT(context,
         backend.enqueue_translation(
             emke::audio::Direction::Inbound, translated) == EMKE_AUDIO_OK,
         name);

  const std::array<std::int16_t, 1> overflow = {10};
  EXPECT(context,
         backend.enqueue_translation(
             emke::audio::Direction::Inbound, overflow) ==
             EMKE_AUDIO_QUEUE_FULL,
         name);

  std::array<std::int16_t, 3> rendered{};
  EXPECT(context,
         backend.render_translation(
             emke::audio::Direction::Inbound, rendered) == EMKE_AUDIO_OK,
         name);
  EXPECT(context, rendered == translated, name);

  auto diagnostics = valid_diagnostics();
  backend.write_diagnostics(diagnostics);
  EXPECT(context, diagnostics.queue_full_events == 1u, name);
  EXPECT(context, diagnostics.dropped_frames == 1u, name);
  EXPECT(context, diagnostics.consumed_inbound_translation_frames == 3u, name);
}

void test_fake_outbound_underrun_is_zero_filled(TestContext& context) {
  constexpr std::string_view name = "fake outbound underrun is zero filled";

  emke::audio::FakeAudioBackend backend(8u, 480u);
  EXPECT(context, backend.start() == EMKE_AUDIO_OK, name);
  const std::array<std::int16_t, 2> translated = {1000, -1000};
  EXPECT(context,
         backend.enqueue_translation(
             emke::audio::Direction::Outbound, translated) == EMKE_AUDIO_OK,
         name);

  backend.inject_outbound_underrun();
  std::array<std::int16_t, 2> rendered = {1, 1};
  EXPECT(context,
         backend.render_translation(
             emke::audio::Direction::Outbound, rendered) == EMKE_AUDIO_OK,
         name);
  EXPECT(context, std::ranges::all_of(rendered, [](auto value) {
           return value == 0;
         }),
         name);
  EXPECT(context,
         backend.route(emke::audio::Direction::Outbound) ==
             EMKE_AUDIO_ROUTE_MUTED_FAIL_CLOSED,
         name);

  auto diagnostics = valid_diagnostics();
  backend.write_diagnostics(diagnostics);
  EXPECT(context, diagnostics.outbound_underruns == 1u, name);
  EXPECT(context, diagnostics.dropped_frames == 2u, name);
}

void test_fake_inbound_failure_routes_original(TestContext& context) {
  constexpr std::string_view name = "fake inbound failure routes original";

  emke::audio::FakeAudioBackend backend(8u, 480u);
  EXPECT(context, backend.start() == EMKE_AUDIO_OK, name);
  const auto original_stereo = stereo_block(480u, 0.25f);
  EXPECT(context,
         backend.accept_synthetic_block(
             emke::audio::Direction::Inbound, original_stereo) ==
             EMKE_AUDIO_OK,
         name);
  const std::array<std::int16_t, 1> translated = {-1234};
  EXPECT(context,
         backend.enqueue_translation(
             emke::audio::Direction::Inbound, translated) == EMKE_AUDIO_OK,
         name);

  backend.inject_inbound_translation_failure();
  std::array<std::int16_t, 1> rendered{};
  EXPECT(context,
         backend.render_translation(
             emke::audio::Direction::Inbound, rendered) == EMKE_AUDIO_OK,
         name);
  EXPECT(context, rendered[0] == 8192, name);
  EXPECT(context,
         backend.route(emke::audio::Direction::Inbound) ==
             EMKE_AUDIO_ROUTE_ORIGINAL_FAIL_OPEN,
         name);

  auto diagnostics = valid_diagnostics();
  backend.write_diagnostics(diagnostics);
  EXPECT(context, diagnostics.inbound_translation_failures == 1u, name);
}

void test_fake_device_failure_is_deterministic(TestContext& context) {
  constexpr std::string_view name = "fake device failure is deterministic";

  emke::audio::FakeAudioBackend backend(8u, 480u);
  backend.inject_device_failure();
  EXPECT(context, backend.start() == EMKE_AUDIO_DEVICE_MISSING, name);
  EXPECT(context, !backend.is_running(), name);
  EXPECT(context, backend.start() == EMKE_AUDIO_OK, name);
  EXPECT(context, backend.is_running(), name);

  auto diagnostics = valid_diagnostics();
  backend.write_diagnostics(diagnostics);
  EXPECT(context, diagnostics.device_failures == 1u, name);
}

void test_fake_event_queue_full_is_counted(TestContext& context) {
  constexpr std::string_view name = "fake event queue full is counted";

  emke::audio::FakeAudioBackend backend(8u, 480u);
  EXPECT(context, backend.start() == EMKE_AUDIO_OK, name);
  const auto stereo = stereo_block(480u, 0.0f);
  EXPECT(context,
         backend.accept_synthetic_block(
             emke::audio::Direction::Inbound, stereo) == EMKE_AUDIO_OK,
         name);
  EXPECT(context,
         backend.accept_synthetic_block(
             emke::audio::Direction::Outbound, stereo) ==
             EMKE_AUDIO_QUEUE_FULL,
         name);

  auto diagnostics = valid_diagnostics();
  backend.write_diagnostics(diagnostics);
  EXPECT(context, diagnostics.queue_full_events == 1u, name);
  EXPECT(context, diagnostics.dropped_frames == 240u, name);
}

void test_fake_routes_are_direction_safe_and_persistent(TestContext& context) {
  constexpr std::string_view name =
      "fake routes are direction safe and persistent";

  emke::audio::FakeAudioBackend inbound;
  EXPECT(context, inbound.start() == EMKE_AUDIO_OK, name);
  EXPECT(context,
         inbound.set_route(emke::audio::Direction::Inbound,
                           EMKE_AUDIO_ROUTE_MUTED_FAIL_CLOSED) ==
             EMKE_AUDIO_INVALID_ARGUMENT,
         name);
  EXPECT(context,
         inbound.set_route(emke::audio::Direction::Outbound,
                           EMKE_AUDIO_ROUTE_ORIGINAL_FAIL_OPEN) ==
             EMKE_AUDIO_INVALID_ARGUMENT,
         name);

  const auto inbound_original = stereo_block(480u, 0.25f);
  EXPECT(context,
         inbound.accept_synthetic_block(
             emke::audio::Direction::Inbound, inbound_original) ==
             EMKE_AUDIO_OK,
         name);
  const std::array<std::int16_t, 1> stale_inbound = {-1234};
  EXPECT(context,
         inbound.enqueue_translation(
             emke::audio::Direction::Inbound, stale_inbound) == EMKE_AUDIO_OK,
         name);
  inbound.inject_inbound_translation_failure();

  std::array<std::int16_t, 1> inbound_render{};
  EXPECT(context,
         inbound.render_translation(
             emke::audio::Direction::Inbound, inbound_render) == EMKE_AUDIO_OK,
         name);
  EXPECT(context, inbound_render[0] == 8192, name);
  EXPECT(context,
         inbound.enqueue_translation(
             emke::audio::Direction::Inbound, stale_inbound) == EMKE_AUDIO_OK,
         name);
  inbound_render[0] = 0;
  EXPECT(context,
         inbound.render_translation(
             emke::audio::Direction::Inbound, inbound_render) == EMKE_AUDIO_OK,
         name);
  EXPECT(context, inbound_render[0] == 8192, name);

  auto diagnostics = valid_diagnostics();
  inbound.write_diagnostics(diagnostics);
  EXPECT(context, diagnostics.queued_inbound_translation_frames == 0u, name);
  EXPECT(context, diagnostics.consumed_inbound_translation_frames == 0u, name);

  emke::audio::FakeAudioBackend outbound;
  EXPECT(context, outbound.start() == EMKE_AUDIO_OK, name);
  const auto outbound_original = stereo_block(480u, 0.25f);
  EXPECT(context,
         outbound.accept_synthetic_block(
             emke::audio::Direction::Outbound, outbound_original) ==
             EMKE_AUDIO_OK,
         name);
  const std::array<std::int16_t, 1> translated = {1000};
  EXPECT(context,
         outbound.enqueue_translation(
             emke::audio::Direction::Outbound, translated) == EMKE_AUDIO_OK,
         name);
  EXPECT(context,
         outbound.set_route(emke::audio::Direction::Outbound,
                            EMKE_AUDIO_ROUTE_ORIGINAL_BYPASS) == EMKE_AUDIO_OK,
         name);
  std::array<std::int16_t, 1> outbound_render{};
  EXPECT(context,
         outbound.render_translation(
             emke::audio::Direction::Outbound, outbound_render) ==
             EMKE_AUDIO_OK,
         name);
  EXPECT(context, outbound_render[0] == 8192, name);

  EXPECT(context,
         outbound.set_route(emke::audio::Direction::Outbound,
                            EMKE_AUDIO_ROUTE_TRANSLATED) == EMKE_AUDIO_OK,
         name);
  EXPECT(context,
         outbound.enqueue_translation(
             emke::audio::Direction::Outbound, translated) == EMKE_AUDIO_OK,
         name);
  outbound.inject_outbound_underrun();
  outbound_render[0] = 1;
  EXPECT(context,
         outbound.render_translation(
             emke::audio::Direction::Outbound, outbound_render) ==
             EMKE_AUDIO_OK,
         name);
  EXPECT(context, outbound_render[0] == 0, name);
  EXPECT(context,
         outbound.enqueue_translation(
             emke::audio::Direction::Outbound, translated) == EMKE_AUDIO_OK,
         name);
  outbound_render[0] = 1;
  EXPECT(context,
         outbound.render_translation(
             emke::audio::Direction::Outbound, outbound_render) ==
             EMKE_AUDIO_OK,
         name);
  EXPECT(context, outbound_render[0] == 0, name);

  diagnostics = valid_diagnostics();
  outbound.write_diagnostics(diagnostics);
  EXPECT(context, diagnostics.queued_outbound_translation_frames == 0u, name);
  EXPECT(context,
         diagnostics.consumed_outbound_translation_frames == 0u,
         name);
}

void test_fake_default_capacity_units(TestContext& context) {
  constexpr std::string_view name = "fake default capacity units";

  emke::audio::FakeAudioBackend translated;
  EXPECT(context, translated.start() == EMKE_AUDIO_OK, name);
  const std::vector<std::int16_t> two_seconds_at_24khz(48'000u, 7);
  EXPECT(context,
         translated.enqueue_translation(
             emke::audio::Direction::Inbound, two_seconds_at_24khz) ==
             EMKE_AUDIO_OK,
         name);
  const std::array<std::int16_t, 1> overflow = {8};
  EXPECT(context,
         translated.enqueue_translation(
             emke::audio::Direction::Inbound, overflow) ==
             EMKE_AUDIO_QUEUE_FULL,
         name);

  auto diagnostics = valid_diagnostics();
  translated.write_diagnostics(diagnostics);
  EXPECT(context,
         diagnostics.queued_inbound_translation_frames == 48'000u,
         name);

  emke::audio::FakeAudioBackend capture;
  EXPECT(context, capture.start() == EMKE_AUDIO_OK, name);
  const auto full_capture = stereo_block(4'800u, 0.0f);
  EXPECT(context,
         capture.accept_synthetic_block(
             emke::audio::Direction::Inbound, full_capture) == EMKE_AUDIO_OK,
         name);
  const auto one_cycle = stereo_block(480u, 0.0f);
  EXPECT(context,
         capture.accept_synthetic_block(
             emke::audio::Direction::Inbound, one_cycle) ==
             EMKE_AUDIO_QUEUE_FULL,
         name);

  emke::audio::AudioEvent event;
  EXPECT(context, capture.poll_event(event) == EMKE_AUDIO_OK, name);
  EXPECT(context, event.pcm16.size() == 2'400u, name);
  EXPECT(context,
         capture.accept_synthetic_block(
             emke::audio::Direction::Inbound, one_cycle) == EMKE_AUDIO_OK,
         name);

  emke::audio::FakeAudioBackend cycle;
  EXPECT(context, cycle.start() == EMKE_AUDIO_OK, name);
  const auto invalid_cycle = stereo_block(720u, 0.0f);
  EXPECT(context,
         cycle.accept_synthetic_block(
             emke::audio::Direction::Inbound, invalid_cycle) ==
             EMKE_AUDIO_FORMAT_UNSUPPORTED,
         name);
  const auto boundary_481 = stereo_block(481u, 0.0f);
  EXPECT(context,
         cycle.accept_synthetic_block(
             emke::audio::Direction::Inbound, boundary_481) ==
             EMKE_AUDIO_FORMAT_UNSUPPORTED,
         name);
}

}  // namespace

int run_abi_tests() {
  TestContext context;
  test_public_abi_layout(context);
  test_struct_validation(context);
  test_handle_lifecycle_and_runtime_state(context);
  test_c_api_copies_translation_input(context);
  return context.failed_assertions();
}

int run_fake_backend_tests() {
  TestContext context;
  test_fake_capture_conversion_and_events(context);
  test_fake_translation_queue_and_queue_full(context);
  test_fake_outbound_underrun_is_zero_filled(context);
  test_fake_inbound_failure_routes_original(context);
  test_fake_device_failure_is_deterministic(context);
  test_fake_event_queue_full_is_counted(context);
  test_fake_routes_are_direction_safe_and_persistent(context);
  test_fake_default_capacity_units(context);
  return context.failed_assertions();
}

std::span<const RegisteredTest> registered_tests() {
  static constexpr std::array tests = {
      RegisteredTest{"Abi", "versioned C ABI", &run_abi_tests},
      RegisteredTest{
          "FakeBackend", "deterministic fake backend", &run_fake_backend_tests},
  };
  return tests;
}
