#include "endpoint_snapshot.hpp"

#include <cstddef>
#include <span>

namespace emke::audio {
namespace {

std::uint32_t public_status(EndpointDiscoveryStatus status) noexcept {
  switch (status) {
    case EndpointDiscoveryStatus::ready:
      return EMKE_AUDIO_ENDPOINT_DISCOVERY_READY;
    case EndpointDiscoveryStatus::driverMissing:
      return EMKE_AUDIO_ENDPOINT_DISCOVERY_DRIVER_MISSING;
    case EndpointDiscoveryStatus::virtualEndpointsPartial:
      return EMKE_AUDIO_ENDPOINT_DISCOVERY_VIRTUAL_ENDPOINTS_PARTIAL;
    case EndpointDiscoveryStatus::physicalInputMissing:
      return EMKE_AUDIO_ENDPOINT_DISCOVERY_PHYSICAL_INPUT_MISSING;
    case EndpointDiscoveryStatus::physicalOutputMissing:
      return EMKE_AUDIO_ENDPOINT_DISCOVERY_PHYSICAL_OUTPUT_MISSING;
    case EndpointDiscoveryStatus::sourceError:
      return EMKE_AUDIO_ENDPOINT_DISCOVERY_SOURCE_ERROR;
  }
  return EMKE_AUDIO_ENDPOINT_DISCOVERY_SOURCE_ERROR;
}

bool copy_id(std::span<const char16_t> source,
             std::uint16_t* destination,
             std::uint32_t& length,
             bool required) noexcept {
  if ((required && source.empty()) ||
      source.size() >= EMKE_AUDIO_ENDPOINT_ID_CAPACITY) {
    return false;
  }
  for (std::size_t index = 0u; index < source.size(); ++index) {
    if (source[index] == u'\0') {
      return false;
    }
    destination[index] = static_cast<std::uint16_t>(source[index]);
  }
  length = static_cast<std::uint32_t>(source.size());
  destination[source.size()] = 0u;
  return true;
}

bool copy_virtual_endpoint(const DeviceEndpoint& source,
                           std::size_t role,
                           emke_audio_discovered_endpoint& destination) noexcept {
  destination = {};
  destination.size = sizeof(destination);
  destination.abi_version = EMKE_AUDIO_ABI_VERSION;
  destination.role = static_cast<std::uint32_t>(role);
  destination.data_flow = source.data_flow == DeviceDataFlow::render
                              ? EMKE_AUDIO_ENDPOINT_DATA_FLOW_RENDER
                              : EMKE_AUDIO_ENDPOINT_DATA_FLOW_CAPTURE;
  destination.state = source.state;
  return copy_id(source.id, destination.endpoint_id,
                 destination.endpoint_id_length, true);
}

void write_source_error(emke_audio_endpoint_snapshot& snapshot,
                        const std::optional<DeviceCatalogError>& error) noexcept {
  snapshot.discovery_status = EMKE_AUDIO_ENDPOINT_DISCOVERY_SOURCE_ERROR;
  if (error.has_value()) {
    snapshot.source_operation = static_cast<std::uint32_t>(error->operation);
    snapshot.source_native_code = error->native_code;
  }
}

}  // namespace

bool write_endpoint_snapshot(const EndpointDiscoveryResult& result,
                             emke_audio_endpoint_snapshot& snapshot) noexcept {
  snapshot = {};
  snapshot.size = sizeof(snapshot);
  snapshot.abi_version = EMKE_AUDIO_ABI_VERSION;
  snapshot.discovery_status = public_status(result.status);
  if (result.status == EndpointDiscoveryStatus::sourceError) {
    write_source_error(snapshot, result.error);
    return true;
  }
  if (!result.virtual_endpoints_ready) {
    return true;
  }
  for (std::size_t index = 0u; index < result.virtual_endpoints.size(); ++index) {
    if (!copy_virtual_endpoint(result.virtual_endpoints[index], index,
                               snapshot.virtual_endpoints[index])) {
      write_source_error(snapshot, std::nullopt);
      return false;
    }
  }
  if (!copy_id(result.default_physical_input_id,
               snapshot.physical_input_endpoint_id,
               snapshot.physical_input_endpoint_id_length, false) ||
      !copy_id(result.default_physical_output_id,
               snapshot.physical_output_endpoint_id,
               snapshot.physical_output_endpoint_id_length, false)) {
    write_source_error(snapshot, std::nullopt);
    return false;
  }
  return true;
}

}  // namespace emke::audio
