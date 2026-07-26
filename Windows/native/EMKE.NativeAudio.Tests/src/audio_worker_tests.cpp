#include "audio_worker.hpp"
#include "wasapi_stream.hpp"

#include <algorithm>
#include <array>
#include <atomic>
#include <bit>
#include <chrono>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <iostream>
#include <limits>
#include <span>
#include <string>
#include <string_view>
#include <thread>
#include <vector>

namespace {

static_assert(emke::audio::networkBatchFrames == 4'800u);
static_assert(emke::audio::networkBatchBytes == 9'600u);
static_assert(
    emke::audio::networkBatchFrames ==
    EMKE_AUDIO_NETWORK_SAMPLE_RATE_HZ / 5u);

class TestContext {
 public:
  void expect(bool condition, std::string_view expression, int line) {
    if (condition) {
      return;
    }
    ++failures_;
    std::cerr << line << ": expected " << expression << '\n';
  }

  [[nodiscard]] int failures() const noexcept {
    return failures_;
  }

 private:
  int failures_ = 0;
};

#define EXPECT(context, expression) \
  (context).expect((expression), #expression, __LINE__)

class OrderedComponent final : public emke::audio::Startable {
 public:
  OrderedComponent(std::string name,
                   std::vector<std::string>& calls,
                   bool fail_start = false)
      : name_(std::move(name)), calls_(calls), fail_start_(fail_start) {}

  emke_audio_status start() override {
    calls_.push_back("start:" + name_);
    if (fail_start_) {
      fail_start_ = false;
      running_ = false;
      return EMKE_AUDIO_DEVICE_MISSING;
    }
    running_ = true;
    return EMKE_AUDIO_OK;
  }

  emke_audio_status stop() override {
    calls_.push_back("stop:" + name_);
    running_ = false;
    return EMKE_AUDIO_OK;
  }

  [[nodiscard]] bool running() const noexcept override {
    return running_;
  }

 private:
  std::string name_;
  std::vector<std::string>& calls_;
  bool fail_start_ = false;
  bool running_ = false;
};

void test_lifecycle_start_stop_order(TestContext& context) {
  std::vector<std::string> calls;
  OrderedComponent physical_output{"physical-output", calls};
  OrderedComponent app_microphone{"app-microphone", calls};
  OrderedComponent app_speaker{"app-speaker", calls};
  OrderedComponent physical_microphone{"physical-microphone", calls};
  OrderedComponent worker{"worker", calls};
  emke::audio::AudioPipelineLifecycle lifecycle{
      physical_output,
      app_microphone,
      app_speaker,
      physical_microphone,
      worker,
  };

  EXPECT(context, lifecycle.start() == EMKE_AUDIO_OK);
  EXPECT(context, lifecycle.start() == EMKE_AUDIO_OK);
  EXPECT(context, lifecycle.stop() == EMKE_AUDIO_OK);
  EXPECT(context, lifecycle.stop() == EMKE_AUDIO_OK);

  const std::vector<std::string> expected = {
      "start:physical-output",
      "start:app-microphone",
      "start:app-speaker",
      "start:physical-microphone",
      "start:worker",
      "stop:worker",
      "stop:physical-microphone",
      "stop:app-speaker",
      "stop:app-microphone",
      "stop:physical-output",
  };
  EXPECT(context, calls == expected);
}

void test_lifecycle_rolls_back_only_started_components(TestContext& context) {
  for (std::size_t failure = 0u; failure < 5u; ++failure) {
    std::vector<std::string> calls;
    OrderedComponent physical_output{
        "physical-output", calls, failure == 0u};
    OrderedComponent app_microphone{
        "app-microphone", calls, failure == 1u};
    OrderedComponent app_speaker{"app-speaker", calls, failure == 2u};
    OrderedComponent physical_microphone{
        "physical-microphone", calls, failure == 3u};
    OrderedComponent worker{"worker", calls, failure == 4u};
    emke::audio::AudioPipelineLifecycle lifecycle{
        physical_output,
        app_microphone,
        app_speaker,
        physical_microphone,
        worker,
    };

    EXPECT(context, lifecycle.start() == EMKE_AUDIO_DEVICE_MISSING);
    EXPECT(context, !lifecycle.running());
    EXPECT(context, lifecycle.stop() == EMKE_AUDIO_OK);

    const std::array names = {
        std::string_view{"physical-output"},
        std::string_view{"app-microphone"},
        std::string_view{"app-speaker"},
        std::string_view{"physical-microphone"},
        std::string_view{"worker"},
    };
    std::vector<std::string> expected;
    for (std::size_t index = 0u; index <= failure; ++index) {
      expected.push_back("start:" + std::string(names[index]));
    }
    for (std::size_t index = failure; index > 0u; --index) {
      expected.push_back("stop:" + std::string(names[index - 1u]));
    }
    EXPECT(context, calls == expected);

    EXPECT(context, lifecycle.start() == EMKE_AUDIO_OK);
    EXPECT(context, lifecycle.stop() == EMKE_AUDIO_OK);
    EXPECT(context, !lifecycle.running());
  }
}

emke::audio::AudioFormat float_stereo_format() {
  return {
      .sample_rate_hz = 48'000u,
      .channel_count = 2u,
      .sample_type = emke::audio::NativeSampleType::ieeeFloat32,
      .bits_per_sample = 32u,
      .valid_bits_per_sample = 32u,
      .block_align = 8u,
      .channel_mask = 3u,
      .has_channel_mask = true,
  };
}

std::vector<std::byte> float_packet(
    std::size_t frames,
    float left,
    float right) {
  std::vector<std::byte> bytes(frames * 2u * sizeof(float));
  for (std::size_t frame = 0u; frame < frames; ++frame) {
    const std::array samples = {left, right};
    std::memcpy(
        bytes.data() + frame * 2u * sizeof(float),
        samples.data(),
        sizeof(samples));
  }
  return bytes;
}

std::vector<float> packet_floats(const emke::audio::RawAudioPacket& packet) {
  std::vector<float> result(packet.byte_count / sizeof(float));
  std::memcpy(result.data(), packet.bytes.data(), packet.byte_count);
  return result;
}

struct WorkerHarness {
  WorkerHarness()
      : physical_output(
            emke::audio::StreamRole::physicalOutput,
            emke::audio::StreamDirection::render,
            float_stereo_format(),
            16u,
            256u),
        app_microphone(
            emke::audio::StreamRole::appMicrophoneRender,
            emke::audio::StreamDirection::render,
            float_stereo_format(),
            16u,
            256u),
        app_speaker(
            emke::audio::StreamRole::appSpeakerCapture,
            emke::audio::StreamDirection::capture,
            float_stereo_format(),
            256u,
            16u),
        physical_microphone(
            emke::audio::StreamRole::physicalMicrophoneCapture,
            emke::audio::StreamDirection::capture,
            float_stereo_format(),
            256u,
            16u),
        worker(
            physical_output,
            app_microphone,
            app_speaker,
            physical_microphone) {}

  emke::audio::FakeAudioStream physical_output;
  emke::audio::FakeAudioStream app_microphone;
  emke::audio::FakeAudioStream app_speaker;
  emke::audio::FakeAudioStream physical_microphone;
  emke::audio::AudioWorker worker;
};

void test_worker_thread_start_failure_rolls_back_to_stopped(
    TestContext& context) {
  WorkerHarness harness;
  const std::vector<std::int16_t> primed(240u, 8'000);
  EXPECT(
      context,
      harness.worker.set_route(
          emke::audio::Direction::Inbound,
          EMKE_AUDIO_ROUTE_ORIGINAL_BYPASS) == EMKE_AUDIO_OK);
  EXPECT(
      context,
      harness.worker.enqueue_translation(
          emke::audio::Direction::Inbound, primed) == EMKE_AUDIO_OK);
  EXPECT(
      context,
      harness.worker.set_route(
          emke::audio::Direction::Inbound,
          EMKE_AUDIO_ROUTE_TRANSLATED) == EMKE_AUDIO_OK);
  harness.worker.fail_next_thread_start_for_test();

  emke::audio::AudioPipelineLifecycle lifecycle{
      harness.physical_output,
      harness.app_microphone,
      harness.app_speaker,
      harness.physical_microphone,
      harness.worker,
  };
  EXPECT(context, lifecycle.start() == EMKE_AUDIO_INTERNAL_ERROR);
  EXPECT(context, !lifecycle.running());
  EXPECT(context, !harness.worker.running());
  EXPECT(context, !harness.physical_output.running());
  EXPECT(context, !harness.app_microphone.running());
  EXPECT(context, !harness.app_speaker.running());
  EXPECT(context, !harness.physical_microphone.running());

  emke_audio_diagnostics diagnostics{};
  harness.worker.write_diagnostics(diagnostics);
  EXPECT(context, diagnostics.is_running == 0u);
  EXPECT(context, diagnostics.inbound_route == EMKE_AUDIO_ROUTE_STOPPED);
  EXPECT(context, diagnostics.outbound_route == EMKE_AUDIO_ROUTE_STOPPED);
  EXPECT(context, diagnostics.queued_inbound_translation_frames == 240u);

  emke::audio::AudioEvent event;
  EXPECT(
      context,
      harness.worker.poll_event(event, 0u) == EMKE_AUDIO_NOT_RUNNING);
  EXPECT(context, event.kind == EMKE_AUDIO_EVENT_NONE);

  EXPECT(context, lifecycle.start() == EMKE_AUDIO_OK);
  harness.worker.write_diagnostics(diagnostics);
  EXPECT(context, diagnostics.is_running == 1u);
  EXPECT(
      context,
      diagnostics.inbound_route == EMKE_AUDIO_ROUTE_TRANSLATED);
  EXPECT(context, diagnostics.queued_inbound_translation_frames == 240u);
  EXPECT(context, lifecycle.stop() == EMKE_AUDIO_OK);
}

void test_virtual_format_contract(TestContext& context) {
  const emke::audio::AudioFormat expected{
      .sample_rate_hz = 48'000u,
      .channel_count = 2u,
      .sample_type = emke::audio::NativeSampleType::ieeeFloat32,
      .bits_per_sample = 32u,
      .valid_bits_per_sample = 32u,
      .block_align = 8u,
  };
  EXPECT(context, emke::audio::is_exact_virtual_format(expected));

  auto wrong = expected;
  wrong.sample_rate_hz = 44'100u;
  EXPECT(context, !emke::audio::is_exact_virtual_format(wrong));
  wrong = expected;
  wrong.sample_type = emke::audio::NativeSampleType::pcm32;
  EXPECT(context, !emke::audio::is_exact_virtual_format(wrong));
}

void test_fixed_packet_storage_rejects_oversized_negotiated_formats(
    TestContext& context) {
  const auto normal = float_stereo_format();
  EXPECT(
      context,
      emke::audio::stream_format_fits_fixed_storage(
          normal, 480u, emke::audio::StreamDirection::capture));
  EXPECT(
      context,
      emke::audio::stream_format_fits_fixed_storage(
          normal, 480u, emke::audio::StreamDirection::render));
  EXPECT(
      context,
      emke::audio::stream_format_fits_fixed_storage(
          normal, 4'800u, emke::audio::StreamDirection::render));
  EXPECT(
      context,
      !emke::audio::stream_format_fits_fixed_storage(
          normal, 1'921u, emke::audio::StreamDirection::capture));

  const emke::audio::AudioFormat wide_192khz{
      .sample_rate_hz = 192'000u,
      .channel_count = 32u,
      .sample_type = emke::audio::NativeSampleType::pcm32,
      .bits_per_sample = 32u,
      .valid_bits_per_sample = 32u,
      .block_align = 128u,
  };
  EXPECT(
      context,
      emke::audio::stream_format_fits_fixed_storage(
          wide_192khz, 480u, emke::audio::StreamDirection::capture));
  EXPECT(
      context,
      !emke::audio::stream_format_fits_fixed_storage(
          wide_192khz, 480u, emke::audio::StreamDirection::render));
  EXPECT(
      context,
      !emke::audio::stream_format_fits_fixed_storage(
          wide_192khz, 1'920u, emke::audio::StreamDirection::capture));

  emke::audio::RawPacketQueue queue;
  const std::vector<std::byte> oversized(
      1'921u * emke::audio::bytes_per_frame(normal));
  EXPECT(
      context,
      !queue.push(normal, 1'921u, 1u, oversized));
}

void test_native_converter_supports_common_formats_and_chunk_continuity(
    TestContext& context) {
  const emke::audio::AudioFormat pcm16_mono{
      .sample_rate_hz = 16'000u,
      .channel_count = 1u,
      .sample_type = emke::audio::NativeSampleType::pcm16,
      .bits_per_sample = 16u,
      .valid_bits_per_sample = 16u,
      .block_align = 2u,
  };
  const std::array<std::int16_t, 8u> input = {
      -32'768, -24'000, -12'000, 0, 12'000, 24'000, 30'000, 32'767};
  std::array<std::byte, input.size() * sizeof(std::int16_t)> bytes{};
  std::memcpy(bytes.data(), input.data(), bytes.size());

  emke::audio::NativeFormatConverter contiguous{pcm16_mono};
  std::array<float, 64u> contiguous_output{};
  const auto contiguous_result = contiguous.process(
      bytes, input.size(), contiguous_output);
  EXPECT(
      context,
      contiguous_result.status == emke::audio::NativeFormatStatus::ok);

  emke::audio::NativeFormatConverter chunked{pcm16_mono};
  std::array<float, 64u> chunked_output{};
  const auto first = chunked.process(
      std::span<const std::byte>{bytes}.first(6u),
      3u,
      chunked_output);
  const auto second = chunked.process(
      std::span<const std::byte>{bytes}.subspan(6u),
      5u,
      std::span<float>{chunked_output}.subspan(first.output_frames * 2u));
  EXPECT(context, first.status == emke::audio::NativeFormatStatus::ok);
  EXPECT(context, second.status == emke::audio::NativeFormatStatus::ok);
  EXPECT(
      context,
      first.output_frames + second.output_frames ==
          contiguous_result.output_frames);
  for (std::size_t index = 0u;
       index < contiguous_result.output_frames * 2u;
       ++index) {
    EXPECT(
        context,
        std::abs(contiguous_output[index] - chunked_output[index]) <= 1e-6f);
  }
  EXPECT(context, contiguous_output.front() == -1.0f);
  EXPECT(context, contiguous_output[1u] == -1.0f);

  const std::array formats = {
      emke::audio::AudioFormat{
          .sample_rate_hz = 44'100u,
          .channel_count = 2u,
          .sample_type = emke::audio::NativeSampleType::pcm24,
          .bits_per_sample = 24u,
          .valid_bits_per_sample = 24u,
          .block_align = 6u,
      },
      emke::audio::AudioFormat{
          .sample_rate_hz = 96'000u,
          .channel_count = 6u,
          .sample_type = emke::audio::NativeSampleType::pcm32,
          .bits_per_sample = 32u,
          .valid_bits_per_sample = 24u,
          .block_align = 24u,
          .channel_mask = 0x3fu,
          .has_channel_mask = true,
      },
      float_stereo_format(),
  };
  for (const auto& format : formats) {
    EXPECT(context, emke::audio::NativeFormatConverter(format).supported());
  }

  auto invalid = float_stereo_format();
  invalid.sample_rate_hz = 200'000u;
  EXPECT(context, !emke::audio::NativeFormatConverter(invalid).supported());
}

void test_converter_rejects_without_partial_state_mutation(
    TestContext& context) {
  const auto format = float_stereo_format();
  const auto bytes = float_packet(4u, 0.25f, -0.25f);
  emke::audio::NativeFormatConverter converter{format};
  std::array<float, 2u> too_small = {9.0f, 9.0f};
  const auto rejected = converter.process(bytes, 4u, too_small);
  EXPECT(
      context,
      rejected.status ==
          emke::audio::NativeFormatStatus::insufficientOutput);
  EXPECT(context, too_small[0u] == 9.0f);
  EXPECT(context, too_small[1u] == 9.0f);

  std::array<float, 8u> after{};
  std::array<float, 8u> fresh{};
  emke::audio::NativeFormatConverter baseline{format};
  const auto after_result = converter.process(bytes, 4u, after);
  const auto fresh_result = baseline.process(bytes, 4u, fresh);
  EXPECT(context, after_result.status == emke::audio::NativeFormatStatus::ok);
  EXPECT(context, fresh_result.status == emke::audio::NativeFormatStatus::ok);
  EXPECT(context, after == fresh);
}

void drain_output(emke::audio::FakeAudioStream& stream) {
  emke::audio::RawAudioPacket discarded;
  while (stream.output_packets().pop(discarded)) {
  }
}

void feed_inbound(
    WorkerHarness& harness,
    std::span<const std::byte> packet,
    std::uint64_t timestamp = 1u) {
  static_cast<void>(harness.app_speaker.emit_capture(
      packet, 480u, timestamp));
  static_cast<void>(harness.worker.process_once());
}

void feed_outbound(
    WorkerHarness& harness,
    std::span<const std::byte> packet,
    std::uint64_t timestamp = 1u,
    bool silent = false) {
  static_cast<void>(harness.physical_microphone.emit_capture(
      packet, 480u, timestamp, silent));
  static_cast<void>(harness.worker.process_once());
}

void test_worker_batches_exactly_9600_bytes_and_keeps_remainder(
    TestContext& context) {
  WorkerHarness harness;
  EXPECT(
      context,
      harness.worker.set_route(
          emke::audio::Direction::Inbound,
          EMKE_AUDIO_ROUTE_ORIGINAL_BYPASS) == EMKE_AUDIO_OK);
  const auto packet = float_packet(479u, 0.25f, -0.25f);
  for (std::size_t index = 0u; index < 21u; ++index) {
    EXPECT(
        context,
        harness.app_speaker.emit_capture(packet, 479u, index));
    EXPECT(context, harness.worker.process_once());
    drain_output(harness.physical_output);
  }

  emke::audio::AudioEvent event;
  EXPECT(
      context,
      harness.worker.poll_event(event, 4'799u) ==
          EMKE_AUDIO_INVALID_ARGUMENT);
  EXPECT(context, event.pcm16.size() == 4'800u);
  const std::uint64_t sequence = event.sequence;
  EXPECT(
      context,
      harness.worker.poll_event(event, 4'800u) == EMKE_AUDIO_OK);
  EXPECT(context, event.kind == EMKE_AUDIO_EVENT_INBOUND_PCM16);
  EXPECT(context, event.pcm16.size() == 4'800u);
  EXPECT(context, event.sequence == sequence);

  emke_audio_diagnostics diagnostics{};
  harness.worker.write_diagnostics(diagnostics);
  EXPECT(context, diagnostics.captured_inbound_frames == 5'029u);
  EXPECT(context, diagnostics.dropped_frames == 0u);
}

void test_worker_preserves_complete_400ms_translation(
    TestContext& context) {
  WorkerHarness harness;
  std::vector<std::int16_t> translated(9'600u, 12'000);
  EXPECT(
      context,
      harness.worker.enqueue_translation(
          emke::audio::Direction::Inbound, translated) == EMKE_AUDIO_OK);
  const auto original = float_packet(480u, -0.75f, -0.75f);
  std::size_t rendered_frames = 0u;
  for (std::size_t index = 0u; index < 40u; ++index) {
    feed_inbound(harness, original, index);
    emke::audio::RawAudioPacket rendered;
    EXPECT(context, harness.physical_output.output_packets().pop(rendered));
    rendered_frames += rendered.frame_count;
  }
  EXPECT(context, rendered_frames == 19'200u);

  emke_audio_diagnostics diagnostics{};
  harness.worker.write_diagnostics(diagnostics);
  EXPECT(context, diagnostics.queued_inbound_translation_frames == 0u);
  EXPECT(
      context,
      diagnostics.consumed_inbound_translation_frames == 9'600u);
}

void test_routes_apply_once_per_block_and_outbound_fails_closed(
    TestContext& context) {
  WorkerHarness harness;
  const auto original = float_packet(480u, 0.5f, -0.5f);
  EXPECT(
      context,
      harness.worker.set_route(
          emke::audio::Direction::Outbound,
          EMKE_AUDIO_ROUTE_ORIGINAL_BYPASS) == EMKE_AUDIO_OK);
  feed_outbound(harness, original);
  emke::audio::RawAudioPacket first_packet;
  EXPECT(context, harness.app_microphone.output_packets().pop(first_packet));
  const auto first = packet_floats(first_packet);
  EXPECT(context, first.size() == 960u);
  EXPECT(context, first.front() == 0.5f);
  EXPECT(context, first.back() == -0.5f);

  EXPECT(
      context,
      harness.worker.set_route(
          emke::audio::Direction::Outbound,
          EMKE_AUDIO_ROUTE_MUTED_FAIL_CLOSED) == EMKE_AUDIO_OK);
  feed_outbound(harness, original, 2u);
  emke::audio::RawAudioPacket second_packet;
  EXPECT(context, harness.app_microphone.output_packets().pop(second_packet));
  const auto second = packet_floats(second_packet);
  EXPECT(
      context,
      std::all_of(
          second.begin(), second.end(),
          [](float value) { return value == 0.0f; }));

  WorkerHarness underrun;
  feed_outbound(underrun, original);
  emke::audio::RawAudioPacket underrun_packet;
  EXPECT(
      context,
      underrun.app_microphone.output_packets().pop(underrun_packet));
  const auto zeros = packet_floats(underrun_packet);
  EXPECT(
      context,
      std::all_of(
          zeros.begin(), zeros.end(),
          [](float value) { return value == 0.0f; }));
  emke_audio_diagnostics diagnostics{};
  underrun.worker.write_diagnostics(diagnostics);
  EXPECT(context, diagnostics.outbound_underruns == 1u);
  EXPECT(
      context,
      diagnostics.outbound_route ==
          EMKE_AUDIO_ROUTE_MUTED_FAIL_CLOSED);

  WorkerHarness inbound_missing;
  feed_inbound(inbound_missing, original);
  emke::audio::RawAudioPacket fail_open_packet;
  EXPECT(
      context,
      inbound_missing.physical_output.output_packets().pop(
          fail_open_packet));
  const auto fail_open = packet_floats(fail_open_packet);
  EXPECT(context, fail_open.size() == 960u);
  EXPECT(context, fail_open.front() == 0.5f);
  EXPECT(context, fail_open.back() == -0.5f);
  inbound_missing.worker.write_diagnostics(diagnostics);
  EXPECT(context, diagnostics.inbound_translation_failures == 1u);
  EXPECT(
      context,
      diagnostics.inbound_route ==
          EMKE_AUDIO_ROUTE_ORIGINAL_FAIL_OPEN);
}

void test_route_priming_commits_at_block_boundary_without_dropping_new_data(
    TestContext& context) {
  WorkerHarness harness;
  emke_audio_diagnostics diagnostics{};
  harness.worker.write_diagnostics(diagnostics);
  EXPECT(context, diagnostics.inbound_route == EMKE_AUDIO_ROUTE_STOPPED);

  const std::vector<std::int16_t> canceled(240u, 1'000);
  EXPECT(
      context,
      harness.worker.enqueue_translation(
          emke::audio::Direction::Inbound, canceled) == EMKE_AUDIO_OK);
  EXPECT(
      context,
      harness.worker.set_route(
          emke::audio::Direction::Inbound,
          EMKE_AUDIO_ROUTE_ORIGINAL_BYPASS) == EMKE_AUDIO_OK);

  const std::vector<std::int16_t> primed(240u, 12'000);
  EXPECT(
      context,
      harness.worker.enqueue_translation(
          emke::audio::Direction::Inbound, primed) == EMKE_AUDIO_OK);
  harness.worker.write_diagnostics(diagnostics);
  EXPECT(context, diagnostics.inbound_route == EMKE_AUDIO_ROUTE_STOPPED);
  EXPECT(context, diagnostics.queued_inbound_translation_frames == 240u);
  EXPECT(context, diagnostics.dropped_frames == 240u);

  EXPECT(
      context,
      harness.worker.set_route(
          emke::audio::Direction::Inbound,
          EMKE_AUDIO_ROUTE_TRANSLATED) == EMKE_AUDIO_OK);
  const auto original = float_packet(480u, -0.75f, -0.75f);
  feed_inbound(harness, original);

  emke::audio::RawAudioPacket rendered;
  EXPECT(context, harness.physical_output.output_packets().pop(rendered));
  harness.worker.write_diagnostics(diagnostics);
  EXPECT(
      context,
      diagnostics.inbound_route == EMKE_AUDIO_ROUTE_TRANSLATED);
  EXPECT(context, diagnostics.queued_inbound_translation_frames == 0u);
  EXPECT(context, diagnostics.consumed_inbound_translation_frames == 240u);
  EXPECT(context, diagnostics.inbound_translation_failures == 0u);

  WorkerHarness started;
  EXPECT(
      context,
      started.worker.set_route(
          emke::audio::Direction::Inbound,
          EMKE_AUDIO_ROUTE_ORIGINAL_BYPASS) == EMKE_AUDIO_OK);
  EXPECT(
      context,
      started.worker.enqueue_translation(
          emke::audio::Direction::Inbound, primed) == EMKE_AUDIO_OK);
  EXPECT(
      context,
      started.worker.set_route(
          emke::audio::Direction::Inbound,
          EMKE_AUDIO_ROUTE_TRANSLATED) == EMKE_AUDIO_OK);
  EXPECT(context, started.worker.start() == EMKE_AUDIO_OK);
  started.worker.write_diagnostics(diagnostics);
  EXPECT(
      context,
      diagnostics.inbound_route == EMKE_AUDIO_ROUTE_TRANSLATED);
  EXPECT(context, diagnostics.queued_inbound_translation_frames == 240u);
  EXPECT(context, started.worker.stop() == EMKE_AUDIO_OK);
}

void test_native_backend_accepts_explicit_prestart_priming(
    TestContext& context) {
  emke_audio_config config{};
  emke::audio::NativeAudioBackend backend{config};
  const std::vector<std::int16_t> primed(240u, 9'000);

  EXPECT(
      context,
      backend.set_route(
          emke::audio::Direction::Inbound,
          EMKE_AUDIO_ROUTE_ORIGINAL_BYPASS) == EMKE_AUDIO_OK);
  EXPECT(
      context,
      backend.enqueue_translation(
          emke::audio::Direction::Inbound, primed) == EMKE_AUDIO_OK);
  EXPECT(
      context,
      backend.set_route(
          emke::audio::Direction::Inbound,
          EMKE_AUDIO_ROUTE_TRANSLATED) == EMKE_AUDIO_OK);

  emke_audio_diagnostics diagnostics{};
  backend.write_diagnostics(diagnostics);
  EXPECT(context, diagnostics.is_running == 0u);
  EXPECT(context, diagnostics.inbound_route == EMKE_AUDIO_ROUTE_STOPPED);
  EXPECT(context, diagnostics.queued_inbound_translation_frames == 240u);
}

void test_translation_capacity_is_atomic(TestContext& context) {
  WorkerHarness harness;
  std::vector<std::int16_t> maximum(48'000u, 1'000);
  EXPECT(
      context,
      harness.worker.enqueue_translation(
          emke::audio::Direction::Outbound, maximum) == EMKE_AUDIO_OK);
  const std::array<std::int16_t, 1u> extra = {2'000};
  EXPECT(
      context,
      harness.worker.enqueue_translation(
          emke::audio::Direction::Outbound, extra) ==
          EMKE_AUDIO_QUEUE_FULL);
  emke_audio_diagnostics diagnostics{};
  harness.worker.write_diagnostics(diagnostics);
  EXPECT(
      context,
      diagnostics.queued_outbound_translation_frames == 48'000u);
  EXPECT(context, diagnostics.queue_full_events == 1u);
  EXPECT(context, diagnostics.dropped_frames == 1u);

  WorkerHarness oversized;
  const std::vector<std::int16_t> too_large(48'001u, 3'000);
  EXPECT(
      context,
      oversized.worker.enqueue_translation(
          emke::audio::Direction::Inbound, too_large) ==
          EMKE_AUDIO_QUEUE_FULL);
  oversized.worker.write_diagnostics(diagnostics);
  EXPECT(context, diagnostics.queued_inbound_translation_frames == 0u);
  EXPECT(context, diagnostics.queue_full_events == 1u);
  EXPECT(context, diagnostics.dropped_frames == too_large.size());
}

void test_stop_resets_partial_capture_batch(TestContext& context) {
  WorkerHarness harness;
  EXPECT(
      context,
      harness.worker.set_route(
          emke::audio::Direction::Inbound,
          EMKE_AUDIO_ROUTE_ORIGINAL_BYPASS) == EMKE_AUDIO_OK);
  const auto packet = float_packet(480u, 0.25f, 0.25f);
  for (std::size_t block = 0u; block < 10u; ++block) {
    feed_inbound(harness, packet, block);
    drain_output(harness.physical_output);
  }

  EXPECT(context, harness.worker.stop() == EMKE_AUDIO_OK);
  for (std::size_t block = 0u; block < 10u; ++block) {
    feed_inbound(harness, packet, 10u + block);
    drain_output(harness.physical_output);
  }
  emke::audio::AudioEvent event;
  EXPECT(
      context,
      harness.worker.poll_event(event, 4'800u) ==
          EMKE_AUDIO_NOT_RUNNING);
  EXPECT(context, event.kind == EMKE_AUDIO_EVENT_NONE);

  for (std::size_t block = 0u; block < 10u; ++block) {
    feed_inbound(harness, packet, 20u + block);
    drain_output(harness.physical_output);
  }
  EXPECT(
      context,
      harness.worker.poll_event(event, 4'800u) == EMKE_AUDIO_OK);
  EXPECT(context, event.kind == EMKE_AUDIO_EVENT_INBOUND_PCM16);
  EXPECT(context, event.pcm16.size() == 4'800u);
}

void test_async_stream_failure_is_bounded_and_restartable(
    TestContext& context) {
  WorkerHarness harness;
  EXPECT(context, harness.worker.start() == EMKE_AUDIO_OK);
  harness.app_speaker.inject_async_failure(
      emke::audio::StreamFailure::Operation::getBuffer, -1'234);
  const auto deadline =
      std::chrono::steady_clock::now() + std::chrono::seconds(1);
  while (harness.worker.running() &&
         std::chrono::steady_clock::now() < deadline) {
    std::this_thread::yield();
  }
  EXPECT(context, !harness.worker.running());

  emke::audio::AudioEvent event;
  EXPECT(context, harness.worker.poll_event(event, 0u) == EMKE_AUDIO_OK);
  EXPECT(context, event.kind == EMKE_AUDIO_EVENT_STREAM_ERROR);
  EXPECT(context, event.status == EMKE_AUDIO_INTERNAL_ERROR);
  EXPECT(
      context,
      event.endpoint_role ==
          static_cast<std::uint32_t>(
              emke::audio::StreamRole::appSpeakerCapture));
  EXPECT(context, event.native_code == -1'234);

  emke_audio_diagnostics diagnostics{};
  harness.worker.write_diagnostics(diagnostics);
  EXPECT(context, diagnostics.is_running == 0u);
  EXPECT(context, diagnostics.device_failures == 1u);
  EXPECT(context, harness.worker.process_once());
  EXPECT(
      context,
      harness.worker.poll_event(event, 0u) == EMKE_AUDIO_NOT_RUNNING);
  EXPECT(context, event.kind == EMKE_AUDIO_EVENT_NONE);

  EXPECT(context, harness.worker.stop() == EMKE_AUDIO_OK);
  EXPECT(context, harness.app_speaker.start() == EMKE_AUDIO_OK);
  EXPECT(
      context,
      harness.app_speaker.last_failure().operation ==
          emke::audio::StreamFailure::Operation::none);
  EXPECT(context, harness.worker.start() == EMKE_AUDIO_OK);
  EXPECT(context, harness.worker.running());
  EXPECT(context, harness.worker.stop() == EMKE_AUDIO_OK);
}

void test_production_async_failure_snapshot_is_coherent_under_concurrency(
    TestContext& context) {
  emke::audio::AsyncStreamFailureState state;
  constexpr std::uint32_t rounds = 20'000u;
  std::atomic<std::uint32_t> phase{0u};
  std::atomic<std::uint32_t> reader_ready{0u};
  std::atomic<std::uint32_t> acknowledged{0u};
  std::atomic<std::uint32_t> violations{0u};
  std::atomic<std::uint32_t> rejected_first_records{0u};

  std::thread writer([&] {
    for (std::uint32_t round = 1u; round <= rounds; ++round) {
      while (acknowledged.load(std::memory_order_acquire) != round - 1u) {
        std::this_thread::yield();
      }
      state.reset();
      phase.store(round, std::memory_order_release);
      while (reader_ready.load(std::memory_order_acquire) != round) {
        std::this_thread::yield();
      }
      const auto operation =
          round % 2u == 0u
              ? emke::audio::StreamFailure::Operation::getBuffer
              : emke::audio::StreamFailure::Operation::releaseBuffer;
      if (!state.record_first(
              operation, -static_cast<std::int32_t>(round))) {
        rejected_first_records.fetch_add(1u, std::memory_order_relaxed);
      }
    }
  });

  std::thread reader([&] {
    for (std::uint32_t round = 1u; round <= rounds; ++round) {
      while (phase.load(std::memory_order_acquire) != round) {
        std::this_thread::yield();
      }
      reader_ready.store(round, std::memory_order_release);
      emke::audio::StreamFailure observed;
      do {
        observed = state.snapshot(
            emke::audio::StreamRole::appSpeakerCapture);
        if (observed.operation ==
            emke::audio::StreamFailure::Operation::none) {
          std::this_thread::yield();
        }
      } while (
          observed.operation ==
          emke::audio::StreamFailure::Operation::none);
      const auto expected_operation =
          round % 2u == 0u
              ? emke::audio::StreamFailure::Operation::getBuffer
              : emke::audio::StreamFailure::Operation::releaseBuffer;
      if (observed.operation != expected_operation ||
          observed.role !=
              emke::audio::StreamRole::appSpeakerCapture ||
          observed.native_code !=
              -static_cast<std::int32_t>(round)) {
        violations.fetch_add(1u, std::memory_order_relaxed);
      }
      acknowledged.store(round, std::memory_order_release);
    }
  });

  writer.join();
  reader.join();
  EXPECT(context, violations.load(std::memory_order_relaxed) == 0u);
  EXPECT(
      context,
      rejected_first_records.load(std::memory_order_relaxed) == 0u);

  state.reset();
  std::atomic<bool> launch_competitors{false};
  std::atomic<std::uint32_t> winner_count{0u};
  std::atomic<std::uint32_t> winner_id{0u};
  const auto compete = [&](std::uint32_t id,
                           emke::audio::StreamFailure::Operation operation,
                           std::int32_t native_code) {
    while (!launch_competitors.load(std::memory_order_acquire)) {
      std::this_thread::yield();
    }
    if (state.record_first(operation, native_code)) {
      winner_id.store(id, std::memory_order_relaxed);
      winner_count.fetch_add(1u, std::memory_order_relaxed);
    }
  };
  std::thread first_competitor{
      compete,
      1u,
      emke::audio::StreamFailure::Operation::getBuffer,
      -111};
  std::thread second_competitor{
      compete,
      2u,
      emke::audio::StreamFailure::Operation::waitForEvent,
      -222};
  launch_competitors.store(true, std::memory_order_release);
  first_competitor.join();
  second_competitor.join();

  const emke::audio::StreamFailure first = state.snapshot(
      emke::audio::StreamRole::appSpeakerCapture);
  EXPECT(context, winner_count.load(std::memory_order_relaxed) == 1u);
  if (winner_id.load(std::memory_order_relaxed) == 1u) {
    EXPECT(
        context,
        first.operation ==
            emke::audio::StreamFailure::Operation::getBuffer);
    EXPECT(context, first.native_code == -111);
  } else {
    EXPECT(context, winner_id.load(std::memory_order_relaxed) == 2u);
    EXPECT(
        context,
        first.operation ==
            emke::audio::StreamFailure::Operation::waitForEvent);
    EXPECT(context, first.native_code == -222);
  }
  EXPECT(
      context,
      !state.record_first(
          emke::audio::StreamFailure::Operation::waitForEvent, -99));
  const emke::audio::StreamFailure retained = state.snapshot(
      emke::audio::StreamRole::appSpeakerCapture);
  EXPECT(context, retained.operation == first.operation);
  EXPECT(context, retained.native_code == first.native_code);
  state.reset();
  const emke::audio::StreamFailure reset = state.snapshot(
      emke::audio::StreamRole::physicalOutput);
  EXPECT(
      context,
      reset.operation ==
          emke::audio::StreamFailure::Operation::none);
  EXPECT(context, reset.role == emke::audio::StreamRole::physicalOutput);
  EXPECT(context, reset.native_code == 0);
}

void test_event_queue_is_fixed_at_64(TestContext& context) {
  WorkerHarness harness;
  EXPECT(
      context,
      harness.worker.set_route(
          emke::audio::Direction::Inbound,
          EMKE_AUDIO_ROUTE_ORIGINAL_BYPASS) == EMKE_AUDIO_OK);
  const auto packet = float_packet(480u, 0.0f, 0.0f);
  for (std::size_t event = 0u; event < 65u; ++event) {
    for (std::size_t block = 0u; block < 20u; ++block) {
      feed_inbound(harness, packet, event * 20u + block);
      drain_output(harness.physical_output);
    }
  }
  std::size_t event_count = 0u;
  std::uint64_t previous = 0u;
  emke::audio::AudioEvent event;
  while (harness.worker.poll_event(event, 4'800u) == EMKE_AUDIO_OK &&
         event.kind != EMKE_AUDIO_EVENT_NONE) {
    EXPECT(context, event.sequence > previous);
    previous = event.sequence;
    ++event_count;
  }
  EXPECT(context, event_count == 64u);
  emke_audio_diagnostics diagnostics{};
  harness.worker.write_diagnostics(diagnostics);
  EXPECT(context, diagnostics.queue_full_events == 1u);
  EXPECT(context, diagnostics.dropped_frames == 4'800u);
}

void test_raw_callbacks_are_bounded_and_silent_safe(TestContext& context) {
  emke::audio::FakeAudioStream capture{
      emke::audio::StreamRole::physicalMicrophoneCapture,
      emke::audio::StreamDirection::capture,
      float_stereo_format(),
      1u,
      1u};
  const auto packet = float_packet(480u, 0.5f, 0.5f);
  EXPECT(context, capture.emit_capture(packet, 480u, 1u));
  EXPECT(context, !capture.emit_capture(packet, 480u, 2u));
  EXPECT(context, capture.dropped_network_frames() == 240u);
  EXPECT(context, capture.queue_full_events() == 1u);

  emke::audio::RawAudioPacket captured;
  EXPECT(context, capture.input_packets().pop(captured));
  EXPECT(context, capture.emit_capture({}, 480u, 3u, true));
  EXPECT(context, capture.input_packets().pop(captured));
  EXPECT(
      context,
      std::all_of(
          captured.bytes.begin(),
          captured.bytes.begin() +
              static_cast<std::ptrdiff_t>(captured.byte_count),
          [](std::byte value) { return value == std::byte{0}; }));

  emke::audio::FakeAudioStream render{
      emke::audio::StreamRole::physicalOutput,
      emke::audio::StreamDirection::render,
      float_stereo_format()};
  std::vector<std::byte> destination(packet.size(), std::byte{0x7f});
  EXPECT(context, render.render(destination, 480u));
  EXPECT(
      context,
      std::all_of(
          destination.begin(), destination.end(),
          [](std::byte value) { return value == std::byte{0}; }));
}

class RecordingWasapiAdapter final
    : public emke::audio::WasapiClientAdapter {
 public:
  emke::audio::WasapiCallResult activate_client3() noexcept override {
    calls.push_back("activate3");
    return client3_unavailable
               ? emke::audio::WasapiCallResult{
                     .status = EMKE_AUDIO_OK,
                     .native_code = -1,
                     .unavailable = true}
               : result("activate3");
  }
  emke::audio::WasapiCallResult activate_client() noexcept override {
    calls.push_back("activate1");
    return result("activate1");
  }
  emke::audio::WasapiCallResult prepare_format(bool exact) noexcept override {
    calls.push_back(exact ? "format:virtual" : "format:physical");
    return result("format");
  }
  emke::audio::WasapiCallResult create_event_handles() noexcept override {
    calls.push_back("create-events:nonsignaled");
    return result("create-events");
  }
  emke::audio::WasapiCallResult get_engine_period() noexcept override {
    calls.push_back("engine-period");
    return result("engine-period");
  }
  emke::audio::WasapiCallResult
  initialize_client3_event_stream() noexcept override {
    calls.push_back("initialize3:event-callback");
    return result("initialize3");
  }
  emke::audio::WasapiCallResult
  initialize_client_event_stream() noexcept override {
    calls.push_back("initialize1:event-callback");
    return result("initialize1");
  }
  emke::audio::WasapiCallResult set_event_handle() noexcept override {
    calls.push_back("set-event");
    return result("set-event");
  }
  emke::audio::WasapiCallResult get_service() noexcept override {
    calls.push_back("get-service");
    return result("get-service");
  }
  emke::audio::WasapiCallResult prepare_loop() noexcept override {
    calls.push_back("prepare-loop");
    return result("prepare-loop");
  }
  emke::audio::WasapiCallResult start_client() noexcept override {
    calls.push_back("start");
    return result("start");
  }
  void reset_after_failure() noexcept override {
    calls.push_back("reset");
  }

  emke::audio::WasapiCallResult result(std::string_view operation) {
    if (operation == fail_operation) {
      return {
          .status = EMKE_AUDIO_INTERNAL_ERROR,
          .native_code = -99,
      };
    }
    return {};
  }

  bool client3_unavailable = false;
  std::string fail_operation;
  std::vector<std::string> calls;
};

void test_wasapi_adapter_enforces_initialization_order(
    TestContext& context) {
  RecordingWasapiAdapter client3;
  emke::audio::StreamFailure failure;
  EXPECT(
      context,
      emke::audio::initialize_wasapi_stream(
          client3,
          emke::audio::StreamRole::appMicrophoneRender,
          true,
          failure) == EMKE_AUDIO_OK);
  const std::vector<std::string> expected3 = {
      "activate3",
      "format:virtual",
      "create-events:nonsignaled",
      "engine-period",
      "initialize3:event-callback",
      "set-event",
      "get-service",
      "prepare-loop",
      "start",
  };
  EXPECT(context, client3.calls == expected3);

  RecordingWasapiAdapter fallback;
  fallback.client3_unavailable = true;
  EXPECT(
      context,
      emke::audio::initialize_wasapi_stream(
          fallback,
          emke::audio::StreamRole::physicalOutput,
          false,
          failure) == EMKE_AUDIO_OK);
  const std::vector<std::string> expected1 = {
      "activate3",
      "activate1",
      "format:physical",
      "create-events:nonsignaled",
      "initialize1:event-callback",
      "set-event",
      "get-service",
      "prepare-loop",
      "start",
  };
  EXPECT(context, fallback.calls == expected1);

  RecordingWasapiAdapter failing;
  failing.fail_operation = "set-event";
  EXPECT(
      context,
      emke::audio::initialize_wasapi_stream(
          failing,
          emke::audio::StreamRole::appSpeakerCapture,
          true,
          failure) == EMKE_AUDIO_INTERNAL_ERROR);
  EXPECT(
      context,
      failure.operation ==
          emke::audio::StreamFailure::Operation::setEventHandle);
  EXPECT(context, failure.native_code == -99);
  EXPECT(context, failing.calls.back() == "reset");
}

void test_realtime_scope_covers_actual_fake_callback(
    TestContext& context) {
  emke::audio::RealtimeInstrumentation::reset();
  emke::audio::FakeAudioStream stream{
      emke::audio::StreamRole::appSpeakerCapture,
      emke::audio::StreamDirection::capture,
      float_stereo_format()};
  const auto packet = float_packet(480u, 0.0f, 0.0f);
  EXPECT(context, stream.emit_capture(packet, 480u, 1u));
  EXPECT(
      context,
      emke::audio::RealtimeInstrumentation::allocation_violations() == 0u);
  EXPECT(
      context,
      emke::audio::RealtimeInstrumentation::blocking_violations() == 0u);

  stream.set_realtime_test_probe(true, true);
  EXPECT(context, stream.emit_capture(packet, 480u, 2u));
  EXPECT(
      context,
      emke::audio::RealtimeInstrumentation::allocation_violations() == 1u);
  EXPECT(
      context,
      emke::audio::RealtimeInstrumentation::blocking_violations() == 1u);

  emke::audio::FakeAudioStream render{
      emke::audio::StreamRole::physicalOutput,
      emke::audio::StreamDirection::render,
      float_stereo_format()};
  render.set_realtime_test_probe(true, true);
  std::vector<std::byte> destination(packet.size());
  EXPECT(context, render.render(destination, 480u));
  EXPECT(
      context,
      emke::audio::RealtimeInstrumentation::allocation_violations() == 2u);
  EXPECT(
      context,
      emke::audio::RealtimeInstrumentation::blocking_violations() == 2u);
}

}  // namespace

int run_audio_lifecycle_tests() {
  TestContext context;
  test_lifecycle_start_stop_order(context);
  test_lifecycle_rolls_back_only_started_components(context);
  return context.failures();
}

int run_audio_worker_tests() {
  TestContext context;
  test_worker_thread_start_failure_rolls_back_to_stopped(context);
  test_virtual_format_contract(context);
  test_fixed_packet_storage_rejects_oversized_negotiated_formats(context);
  test_native_converter_supports_common_formats_and_chunk_continuity(context);
  test_converter_rejects_without_partial_state_mutation(context);
  test_worker_batches_exactly_9600_bytes_and_keeps_remainder(context);
  test_worker_preserves_complete_400ms_translation(context);
  test_routes_apply_once_per_block_and_outbound_fails_closed(context);
  test_route_priming_commits_at_block_boundary_without_dropping_new_data(
      context);
  test_native_backend_accepts_explicit_prestart_priming(context);
  test_translation_capacity_is_atomic(context);
  test_stop_resets_partial_capture_batch(context);
  test_async_stream_failure_is_bounded_and_restartable(context);
  test_production_async_failure_snapshot_is_coherent_under_concurrency(
      context);
  test_event_queue_is_fixed_at_64(context);
  test_raw_callbacks_are_bounded_and_silent_safe(context);
  test_wasapi_adapter_enforces_initialization_order(context);
  return context.failures();
}

int run_realtime_tests() {
  TestContext context;
  test_realtime_scope_covers_actual_fake_callback(context);
  return context.failures();
}
