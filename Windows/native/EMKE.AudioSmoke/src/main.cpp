#include "emke_native_audio.h"

#include <algorithm>
#include <array>
#include <chrono>
#include <cstdint>
#include <cstdlib>
#include <iostream>
#include <span>
#include <string>
#include <string_view>
#include <thread>
#include <utility>
#include <vector>

#if defined(_WIN32)
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#endif

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
  std::u16string physical_input_override;
  std::u16string physical_output_override;
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

bool utf8_to_utf16(std::string_view source, std::u16string& destination) {
  destination.clear();
  for (std::size_t index = 0u; index < source.size();) {
    const std::uint8_t first = static_cast<std::uint8_t>(source[index++]);
    std::uint32_t code_point = 0u;
    std::size_t continuation_count = 0u;
    if (first <= 0x7fu) {
      code_point = first;
    } else if ((first & 0xe0u) == 0xc0u) {
      code_point = first & 0x1fu;
      continuation_count = 1u;
    } else if ((first & 0xf0u) == 0xe0u) {
      code_point = first & 0x0fu;
      continuation_count = 2u;
    } else if ((first & 0xf8u) == 0xf0u) {
      code_point = first & 0x07u;
      continuation_count = 3u;
    } else {
      return false;
    }
    if (index + continuation_count > source.size()) {
      return false;
    }
    for (std::size_t continuation = 0u;
         continuation < continuation_count;
         ++continuation) {
      const std::uint8_t value = static_cast<std::uint8_t>(source[index++]);
      if ((value & 0xc0u) != 0x80u) {
        return false;
      }
      code_point = (code_point << 6u) | (value & 0x3fu);
    }
    if ((continuation_count == 1u && code_point < 0x80u) ||
        (continuation_count == 2u && code_point < 0x800u) ||
        (continuation_count == 3u && code_point < 0x10000u) ||
        (code_point >= 0xd800u && code_point <= 0xdfffu) ||
        code_point > 0x10ffffu || code_point == 0u) {
      return false;
    }
    if (code_point <= 0xffffu) {
      destination.push_back(static_cast<char16_t>(code_point));
    } else {
      const std::uint32_t value = code_point - 0x10000u;
      destination.push_back(static_cast<char16_t>(0xd800u + (value >> 10u)));
      destination.push_back(static_cast<char16_t>(0xdc00u + (value & 0x3ffu)));
    }
  }
  return !destination.empty();
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
      if (!utf8_to_utf16(value, options.physical_input_override)) {
        return false;
      }
    } else if (argument == "--physical-output") {
      if (!utf8_to_utf16(value, options.physical_output_override)) {
        return false;
      }
    } else {
      return false;
    }
  }
  return true;
}

bool copy_endpoint_id(
    std::u16string_view source,
    std::uint16_t* destination) noexcept {
  if (source.empty() || source.size() >= EMKE_AUDIO_ENDPOINT_ID_CAPACITY) {
    return false;
  }
  for (std::size_t index = 0u; index < source.size(); ++index) {
    if (source[index] == u'\0') {
      return false;
    }
    destination[index] = static_cast<std::uint16_t>(source[index]);
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
                                  : copy_endpoint_id(
                                        options.physical_input_override,
                                        config.physical_input_endpoint_id);
  const bool physical_output = options.physical_output_override.empty()
                                   ? copy_snapshot_endpoint_id(
                                         snapshot.physical_output_endpoint_id,
                                         snapshot.physical_output_endpoint_id_length,
                                         config.physical_output_endpoint_id)
                                   : copy_endpoint_id(
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

int run_main(int argc, char** argv) {
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
  switch (snapshot.discovery_status) {
    case EMKE_AUDIO_ENDPOINT_DISCOVERY_READY:
      break;
    case EMKE_AUDIO_ENDPOINT_DISCOVERY_PHYSICAL_INPUT_MISSING:
      if (options.physical_input_override.empty()) {
        return 4;
      }
      break;
    case EMKE_AUDIO_ENDPOINT_DISCOVERY_PHYSICAL_OUTPUT_MISSING:
      if (options.physical_output_override.empty()) {
        return 4;
      }
      break;
    case EMKE_AUDIO_ENDPOINT_DISCOVERY_DRIVER_MISSING:
    case EMKE_AUDIO_ENDPOINT_DISCOVERY_VIRTUAL_ENDPOINTS_PARTIAL:
    case EMKE_AUDIO_ENDPOINT_DISCOVERY_SOURCE_ERROR:
    default:
      return 4;
  }
  if (options.scenario == Scenario::enumerate) {
    if (snapshot.discovery_status != EMKE_AUDIO_ENDPOINT_DISCOVERY_READY) {
      return 4;
    }
    std::cout << "result=ready\n";
    return 0;
  }
  return run_audio_scenario(options, snapshot);
}

#if !defined(EMKE_AUDIO_SMOKE_NO_ENTRYPOINT) && defined(_WIN32)
int wmain(int argc, wchar_t** argv) {
  std::vector<std::string> utf8_arguments;
  utf8_arguments.reserve(static_cast<std::size_t>(argc));
  for (int index = 0; index < argc; ++index) {
    const int required = WideCharToMultiByte(
        CP_UTF8, WC_ERR_INVALID_CHARS, argv[index], -1, nullptr, 0, nullptr,
        nullptr);
    if (required <= 0) {
      return 2;
    }
    std::string converted(static_cast<std::size_t>(required), '\0');
    if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, argv[index], -1,
                            converted.data(), required, nullptr, nullptr) <= 0) {
      return 2;
    }
    converted.resize(static_cast<std::size_t>(required - 1));
    utf8_arguments.push_back(std::move(converted));
  }
  std::vector<char*> narrow_arguments;
  narrow_arguments.reserve(utf8_arguments.size());
  for (std::string& argument : utf8_arguments) {
    narrow_arguments.push_back(argument.data());
  }
  return run_main(argc, narrow_arguments.data());
}
#elif !defined(EMKE_AUDIO_SMOKE_NO_ENTRYPOINT)
int main(int argc, char** argv) {
  return run_main(argc, argv);
}
#endif
