#include "endpoint_snapshot.hpp"

#include <algorithm>
#include <array>
#include <cstdint>
#include <iostream>
#include <string_view>

namespace {

class TestContext {
 public:
  void expect(bool condition, std::string_view expression, int line) {
    if (!condition) {
      ++failures_;
      std::cerr << line << ": expected " << expression << '\n';
    }
  }
  [[nodiscard]] int failures() const { return failures_; }
 private:
  int failures_ = 0;
};
#define EXPECT(context, expression) (context).expect((expression), #expression, __LINE__)

emke::audio::EndpointDiscoveryResult ready_virtuals() {
  emke::audio::EndpointDiscoveryResult result;
  result.virtual_endpoints_ready = true;
  result.status = emke::audio::EndpointDiscoveryStatus::physicalInputMissing;
  for (std::size_t index = 0; index < result.virtual_endpoints.size(); ++index) {
    auto& endpoint = result.virtual_endpoints[index];
    endpoint.id = u"{virtual-}" + std::u16string(1, static_cast<char16_t>(u'0' + index));
    endpoint.state = emke::audio::deviceStateActive;
    endpoint.data_flow = index % 2 == 0 ? emke::audio::DeviceDataFlow::render
                                         : emke::audio::DeviceDataFlow::capture;
  }
  result.virtual_endpoints[1].id = u"{非ASCII}";
  result.default_physical_output_id = u"{physical-output}";
  return result;
}

void test_virtual_ids_survive_missing_default_when_override_can_supply_it(TestContext& context) {
  auto result = ready_virtuals();
  emke_audio_endpoint_snapshot snapshot{};
  snapshot.size = sizeof(snapshot);
  snapshot.abi_version = EMKE_AUDIO_ABI_VERSION;
  EXPECT(context, emke::audio::write_endpoint_snapshot(result, snapshot));
  EXPECT(context, snapshot.discovery_status == EMKE_AUDIO_ENDPOINT_DISCOVERY_PHYSICAL_INPUT_MISSING);
  EXPECT(context, snapshot.virtual_endpoints[1].endpoint_id_length == 8u);
  EXPECT(context, snapshot.virtual_endpoints[1].endpoint_id[8] == 0u);
  EXPECT(context, snapshot.physical_output_endpoint_id_length == 17u);
  EXPECT(context, snapshot.physical_input_endpoint_id_length == 0u);
}

void test_snapshot_writer_rejects_empty_embedded_nul_and_512_code_unit_ids(TestContext& context) {
  for (const std::u16string invalid : {
           std::u16string{}, std::u16string(u"{a}\0b", 5u), std::u16string(512u, u'x')}) {
    auto result = ready_virtuals();
    result.virtual_endpoints[0].id = invalid;
    emke_audio_endpoint_snapshot snapshot{};
    snapshot.size = sizeof(snapshot);
    snapshot.abi_version = EMKE_AUDIO_ABI_VERSION;
    EXPECT(context, !emke::audio::write_endpoint_snapshot(result, snapshot));
    EXPECT(context, snapshot.discovery_status == EMKE_AUDIO_ENDPOINT_DISCOVERY_SOURCE_ERROR);
  }
  auto result = ready_virtuals();
  result.virtual_endpoints[0].id = std::u16string(511u, u'x');
  emke_audio_endpoint_snapshot snapshot{};
  snapshot.size = sizeof(snapshot);
  snapshot.abi_version = EMKE_AUDIO_ABI_VERSION;
  EXPECT(context, emke::audio::write_endpoint_snapshot(result, snapshot));
  EXPECT(context, snapshot.virtual_endpoints[0].endpoint_id_length == 511u);
  EXPECT(context, snapshot.virtual_endpoints[0].endpoint_id[511] == 0u);
}

}  // namespace

int run_endpoint_snapshot_tests() {
  TestContext context;
  test_virtual_ids_survive_missing_default_when_override_can_supply_it(context);
  test_snapshot_writer_rejects_empty_embedded_nul_and_512_code_unit_ids(context);
  return context.failures();
}
