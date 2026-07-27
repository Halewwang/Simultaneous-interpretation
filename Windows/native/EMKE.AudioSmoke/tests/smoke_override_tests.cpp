#include "emke_native_audio.h"

#include <array>
#include <cstdint>
#include <cstring>
#include <iostream>

int run_main(int argc, char** argv);

namespace {

emke_audio_config observed_config{};
std::uint32_t discovery_status =
    EMKE_AUDIO_ENDPOINT_DISCOVERY_PHYSICAL_INPUT_MISSING;
int create_calls = 0;

void copy_id(std::uint16_t* destination, const char16_t* value) {
  std::size_t length = 0u;
  while (value[length] != u'\0') {
    destination[length] = static_cast<std::uint16_t>(value[length]);
    ++length;
  }
  destination[length] = 0u;
}

void set_endpoint(emke_audio_discovered_endpoint& endpoint,
                  std::uint32_t role,
                  const char16_t* id) {
  endpoint.size = sizeof(endpoint);
  endpoint.abi_version = EMKE_AUDIO_ABI_VERSION;
  endpoint.role = role;
  endpoint.data_flow = role % 2u == 0u
                           ? EMKE_AUDIO_ENDPOINT_DATA_FLOW_RENDER
                           : EMKE_AUDIO_ENDPOINT_DATA_FLOW_CAPTURE;
  endpoint.state = 1u;
  std::size_t length = 0u;
  while (id[length] != u'\0') {
    endpoint.endpoint_id[length] = static_cast<std::uint16_t>(id[length]);
    ++length;
  }
  endpoint.endpoint_id[length] = 0u;
  endpoint.endpoint_id_length = static_cast<std::uint32_t>(length);
}

}  // namespace

extern "C" emke_audio_status emke_audio_discover_endpoints(
    emke_audio_endpoint_snapshot* snapshot) {
  *snapshot = {};
  snapshot->size = sizeof(*snapshot);
  snapshot->abi_version = EMKE_AUDIO_ABI_VERSION;
  snapshot->discovery_status = discovery_status;
  for (std::uint32_t role = 0u; role < EMKE_AUDIO_DISCOVERED_ENDPOINT_COUNT; ++role) {
    set_endpoint(snapshot->virtual_endpoints[role], role, u"{virtual}");
  }
  copy_id(snapshot->physical_output_endpoint_id, u"{physical-output}");
  snapshot->physical_output_endpoint_id_length = 17u;
  if (discovery_status != EMKE_AUDIO_ENDPOINT_DISCOVERY_PHYSICAL_INPUT_MISSING) {
    copy_id(snapshot->physical_input_endpoint_id, u"{physical-input}");
    snapshot->physical_input_endpoint_id_length = 16u;
  }
  return EMKE_AUDIO_OK;
}

extern "C" emke_audio_status emke_audio_create(const emke_audio_config* config,
                                                 emke_audio_handle** out_handle) {
  observed_config = *config;
  ++create_calls;
  *out_handle = reinterpret_cast<emke_audio_handle*>(1u);
  return EMKE_AUDIO_OK;
}
extern "C" void emke_audio_destroy(emke_audio_handle*) {}
extern "C" emke_audio_status emke_audio_start(emke_audio_handle*) { return EMKE_AUDIO_OK; }
extern "C" emke_audio_status emke_audio_stop(emke_audio_handle*) { return EMKE_AUDIO_OK; }
extern "C" emke_audio_status emke_audio_set_inbound_route(emke_audio_handle*, emke_audio_route) { return EMKE_AUDIO_OK; }
extern "C" emke_audio_status emke_audio_set_outbound_route(emke_audio_handle*, emke_audio_route) { return EMKE_AUDIO_OK; }
extern "C" emke_audio_status emke_audio_enqueue_inbound_translation(emke_audio_handle*, const int16_t*, uint32_t) { return EMKE_AUDIO_OK; }
extern "C" emke_audio_status emke_audio_enqueue_outbound_translation(emke_audio_handle*, const int16_t*, uint32_t) { return EMKE_AUDIO_OK; }
extern "C" emke_audio_status emke_audio_poll_event(emke_audio_handle*, emke_audio_event*, int16_t*, uint32_t) { return EMKE_AUDIO_OK; }
extern "C" emke_audio_status emke_audio_get_diagnostics(emke_audio_handle*, emke_audio_diagnostics* diagnostics) {
  diagnostics->size = sizeof(*diagnostics);
  diagnostics->abi_version = EMKE_AUDIO_ABI_VERSION;
  return EMKE_AUDIO_OK;
}

int main() {
  // U+975E is the UTF-16 code unit 0x975E; its UTF-8 bytes are E9 9D 9E.
  constexpr char non_ascii_physical_input[] = "\xE9\x9D\x9E" "ASCII";
  std::array<char*, 8u> argv = {
      const_cast<char*>("smoke"), const_cast<char*>("--scenario"),
      const_cast<char*>("inbound-original"), const_cast<char*>("--seconds"),
      const_cast<char*>("1"), const_cast<char*>("--physical-input"),
      const_cast<char*>(non_ascii_physical_input), nullptr,
  };
  if (run_main(7, argv.data()) != 0) {
    return 1;
  }
  if (!(observed_config.physical_input_endpoint_id[0] == 0x975eu &&
        observed_config.physical_input_endpoint_id[1] == u'A' &&
        observed_config.physical_input_endpoint_id[6] == 0u)) {
    return 1;
  }

  discovery_status = 999u;
  create_calls = 0;
  std::array<char*, 6u> unknown_argv = {
      const_cast<char*>("smoke"), const_cast<char*>("--scenario"),
      const_cast<char*>("inbound-original"), const_cast<char*>("--seconds"),
      const_cast<char*>("1"), nullptr,
  };
  return run_main(5, unknown_argv.data()) == 4 && create_calls == 0 ? 0 : 1;
}
