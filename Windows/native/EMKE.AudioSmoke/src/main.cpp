#include "emke_native_audio.h"

#include <algorithm>
#include <array>
#include <chrono>
#include <cstdint>
#include <cstdlib>
#include <iostream>
#include <span>
#include <string_view>
#include <thread>
#include <vector>

namespace {

enum class Scenario {
  enumerate,
  inboundOriginal,
  inboundTranslated,
  outboundTranslated,
  outboundUnderrun,
  inboundFailure,
  outboundFailure,
  crashAfterMicOpen,
};

struct Options {
  Scenario scenario = Scenario::enumerate;
  std::uint32_t seconds = 10u;
  std::string_view physical_input_override;
  std::string_view physical_output_override;
};

constexpr std::string_view status_name(std::uint32_t status) noexcept {
  switch (status) {
    case EMKE_AUDIO_ENDPOINT_DISCOVERY_READY:
      return "ready";
    case EMKE_AUDIO_ENDPOINT_DISCOVERY_DRIVER_MISSING:
      return "driverMissing";
    case EMKE_AUDIO_ENDPOINT_DISCOVERY_VIRTUAL_ENDPOINTS_PARTIAL:
      return "virtualEndpointsPartial";
    case EMKE_AUDIO_ENDPOINT_DISCOVERY_PHYSICAL_INPUT_MISSING:
      return "physicalInputMissing";
    case EMKE_AUDIO_ENDPOINT_DISCOVERY_PHYSICAL_OUTPUT_MISSING:
      return "physicalOutputMissing";
    case EMKE_AUDIO_ENDPOINT_DISCOVERY_SOURCE_ERROR:
      return "sourceError";
  }
  return "sourceError";
}

bool parse_scenario(std::string_view value, Scenario& result) noexcept {
  constexpr std::array pairs = {
      std::pair{"enumerate", Scenario::enumerate},
      std::pair{"inbound-original", Scenario::inboundOriginal},
      std::pair{"inbound-translated", Scenario::inboundTranslated},
      std::pair{"outbound-translated", Scenario::outboundTranslated},
      std::pair{"outbound-underrun", Scenario::outboundUnderrun},
      std::pair{"inbound-failure", Scenario::inboundFailure},
      std::pair{"outbound-failure", Scenario::outboundFailure},
      std::pair{"crash-after-mic-open", Scenario::crashAfterMicOpen},
  };
  for (const auto& pair : pairs) {
    if (value == pair.first) {
      result = pair.second;
      return true;
    }
  }
  return false;
}

bool parse_seconds(std::string_view value, std::uint32_t& result) noexcept {
  if (value.empty()) {
    return false;
  }
  std::uint32_t parsed = 0u;
  for (const char character : value) {
    if (character < '0' || character > '9' || parsed > 60u) {
      return false;
    }
    parsed = parsed * 10u + static_cast<std::uint32_t>(character - '0');
  }
  if (parsed == 0u || parsed > 600u) {
    return false;
  }
  result = parsed;
  return true;
}

bool parse_options(int argc, char** argv, Options& options) noexcept {
  for (int index = 1; index < argc; ++index) {
    const std::string_view argument = argv[index];
    if (index + 1 >= argc) {
      return false;
    }
    const std::string_view value = argv[++index];
    if (argument == "--scenario") {
      if (!parse_scenario(value, options.scenario)) {
        return false;
      }
    } else if (argument == "--seconds") {
      if (!parse_seconds(value, options.seconds)) {
        return false;
      }
    } else if (argument == "--physical-input") {
      options.physical_input_override = value;
    } else if (argument == "--physical-output") {
      options.physical_output_override = value;
    } else {
      return false;
    }
  }
  return true;
}

bool copy_ascii_endpoint_id(
    std::string_view source,
    std::uint16_t* destination) noexcept {
  if (source.empty() || source.size() >= EMKE_AUDIO_ENDPOINT_ID_CAPACITY) {
    return false;
  }
  for (std::size_t index = 0u; index < source.size(); ++index) {
    const unsigned char character = static_cast<unsigned char>(source[index]);
    if (character > 0x7fu) {
      return false;
    }
    destination[index] = character;
  }
  destination[source.size()] = 0u;
  return true;
}

bool copy_snapshot_endpoint_id(
    const std::uint16_t* source,
    std::uint32_t length,
    std::uint16_t* destination) noexcept {
  if (length == 0u || length >= EMKE_AUDIO_ENDPOINT_ID_CAPACITY ||
      source[length] != 0u) {
    return false;
  }
  std::copy_n(source, length + 1u, destination);
  return true;
}

bool config_from_snapshot(
    const emke_audio_endpoint_snapshot& snapshot,
    const Options& options,
    emke_audio_config& config) noexcept {
  config = {};
  config.size = sizeof(config);
  config.abi_version = EMKE_AUDIO_ABI_VERSION;
  const bool physical_input = options.physical_input_override.empty()
                                  ? copy_snapshot_endpoint_id(
                                        snapshot.physical_input_endpoint_id,
                                        snapshot.physical_input_endpoint_id_length,
                                        config.physical_input_endpoint_id)
                                  : copy_ascii_endpoint_id(
                                        options.physical_input_override,
                                        config.physical_input_endpoint_id);
  const bool physical_output = options.physical_output_override.empty()
                                   ? copy_snapshot_endpoint_id(
                                         snapshot.physical_output_endpoint_id,
                                         snapshot.physical_output_endpoint_id_length,
                                         config.physical_output_endpoint_id)
                                   : copy_ascii_endpoint_id(
                                         options.physical_output_override,
                                         config.physical_output_endpoint_id);
  return physical_input && physical_output &&
         copy_snapshot_endpoint_id(
             snapshot.virtual_endpoints[
                 EMKE_AUDIO_ENDPOINT_ROLE_MEETING_SPEAKER_RENDER]
                 .endpoint_id,
             snapshot.virtual_endpoints[
                 EMKE_AUDIO_ENDPOINT_ROLE_MEETING_SPEAKER_RENDER]
                 .endpoint_id_length,
             config.virtual_speaker_render_endpoint_id) &&
         copy_snapshot_endpoint_id(
             snapshot.virtual_endpoints[
                 EMKE_AUDIO_ENDPOINT_ROLE_APP_SPEAKER_CAPTURE]
                 .endpoint_id,
             snapshot.virtual_endpoints[
                 EMKE_AUDIO_ENDPOINT_ROLE_APP_SPEAKER_CAPTURE]
                 .endpoint_id_length,
             config.virtual_speaker_capture_endpoint_id) &&
         copy_snapshot_endpoint_id(
             snapshot.virtual_endpoints[
                 EMKE_AUDIO_ENDPOINT_ROLE_APP_MICROPHONE_RENDER]
                 .endpoint_id,
             snapshot.virtual_endpoints[
                 EMKE_AUDIO_ENDPOINT_ROLE_APP_MICROPHONE_RENDER]
                 .endpoint_id_length,
             config.virtual_microphone_render_endpoint_id) &&
         copy_snapshot_endpoint_id(
             snapshot.virtual_endpoints[
                 EMKE_AUDIO_ENDPOINT_ROLE_MEETING_MICROPHONE_CAPTURE]
                 .endpoint_id,
             snapshot.virtual_endpoints[
                 EMKE_AUDIO_ENDPOINT_ROLE_MEETING_MICROPHONE_CAPTURE]
                 .endpoint_id_length,
             config.virtual_microphone_capture_endpoint_id);
}

bool set_routes_for_scenario(
    emke_audio_handle* handle,
    Scenario scenario) noexcept {
  emke_audio_route inbound = EMKE_AUDIO_ROUTE_TRANSLATED;
  emke_audio_route outbound = EMKE_AUDIO_ROUTE_TRANSLATED;
  if (scenario == Scenario::inboundOriginal) {
    inbound = EMKE_AUDIO_ROUTE_ORIGINAL_BYPASS;
  }
  if (scenario == Scenario::inboundFailure) {
    inbound = EMKE_AUDIO_ROUTE_ORIGINAL_FAIL_OPEN;
  }
  if (scenario == Scenario::outboundUnderrun ||
      scenario == Scenario::outboundFailure) {
    outbound = EMKE_AUDIO_ROUTE_MUTED_FAIL_CLOSED;
  }
  return emke_audio_set_inbound_route(handle, inbound) == EMKE_AUDIO_OK &&
         emke_audio_set_outbound_route(handle, outbound) == EMKE_AUDIO_OK;
}

bool enqueue_translated_block(
    emke_audio_handle* handle,
    bool inbound) noexcept {
  std::vector<std::int16_t> block(9'600u, 2'048);
  const emke_audio_status status = inbound
                                       ? emke_audio_enqueue_inbound_translation(
                                             handle,
                                             block.data(),
                                             block.size())
                                       : emke_audio_enqueue_outbound_translation(
                                             handle,
                                             block.data(),
                                             block.size());
  return status == EMKE_AUDIO_OK;
}

int run_audio_scenario(const Options& options,
                       const emke_audio_endpoint_snapshot& snapshot) {
  emke_audio_config config{};
  if (!config_from_snapshot(snapshot, options, config)) {
    std::cerr << "result=invalidEndpointSnapshot\n";
    return 5;
  }
  emke_audio_handle* handle = nullptr;
  if (emke_audio_create(&config, &handle) != EMKE_AUDIO_OK || handle == nullptr) {
    std::cerr << "result=createFailed\n";
    return 5;
  }
  const auto destroy = [&] { emke_audio_destroy(handle); };
  if (emke_audio_start(handle) != EMKE_AUDIO_OK) {
    std::cerr << "result=startFailed\n";
    destroy();
    return 5;
  }
  if (!set_routes_for_scenario(handle, options.scenario)) {
    std::cerr << "result=routeRejected\n";
    emke_audio_stop(handle);
    destroy();
    return 5;
  }
  if ((options.scenario == Scenario::inboundTranslated &&
       !enqueue_translated_block(handle, true)) ||
      (options.scenario == Scenario::outboundTranslated &&
       !enqueue_translated_block(handle, false))) {
    std::cerr << "result=translationQueueRejected\n";
    emke_audio_stop(handle);
    destroy();
    return 5;
  }
  if (options.scenario == Scenario::crashAfterMicOpen) {
    std::cout << "result=crashingAfterMicOpen\n";
    std::cout.flush();
    std::abort();
  }

  std::this_thread::sleep_for(std::chrono::seconds(options.seconds));
  emke_audio_diagnostics diagnostics{};
  diagnostics.size = sizeof(diagnostics);
  diagnostics.abi_version = EMKE_AUDIO_ABI_VERSION;
  const emke_audio_status diagnostic_status =
      emke_audio_get_diagnostics(handle, &diagnostics);
  emke_audio_stop(handle);
  destroy();
  if (diagnostic_status != EMKE_AUDIO_OK) {
    std::cerr << "result=diagnosticsFailed\n";
    return 5;
  }
  std::cout << "result=completed\n";
  std::cout << "inboundRoute=" << diagnostics.inbound_route << '\n';
  std::cout << "outboundRoute=" << diagnostics.outbound_route << '\n';
  std::cout << "outboundUnderruns=" << diagnostics.outbound_underruns << '\n';
  std::cout << "droppedFrames=" << diagnostics.dropped_frames << '\n';
  return 0;
}

}  // namespace

int main(int argc, char** argv) {
  Options options;
  if (!parse_options(argc, argv, options)) {
    std::cerr << "usage: EMKE.AudioSmoke --scenario <name> [--seconds 1..600] "
                 "[--physical-input <opaque-id>] [--physical-output <opaque-id>]\n";
    return 2;
  }

  emke_audio_endpoint_snapshot snapshot{};
  snapshot.size = sizeof(snapshot);
  snapshot.abi_version = EMKE_AUDIO_ABI_VERSION;
  if (emke_audio_discover_endpoints(&snapshot) != EMKE_AUDIO_OK) {
    std::cerr << "result=discoveryApiError\n";
    return 5;
  }
  std::cout << "discovery=" << status_name(snapshot.discovery_status) << '\n';
  if (snapshot.discovery_status != EMKE_AUDIO_ENDPOINT_DISCOVERY_READY) {
    return snapshot.discovery_status == EMKE_AUDIO_ENDPOINT_DISCOVERY_DRIVER_MISSING
               ? 3
               : 4;
  }
  if (options.scenario == Scenario::enumerate) {
    std::cout << "result=ready\n";
    return 0;
  }
  return run_audio_scenario(options, snapshot);
}
