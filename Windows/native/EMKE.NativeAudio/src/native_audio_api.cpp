#include "emke_native_audio.h"

#include "audio_runtime.hpp"
#include "device_catalog.hpp"

#if defined(_WIN32)
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#endif

#if defined(EMKE_NATIVE_AUDIO_TEST_HOOKS)
#include "native_audio_test_hooks.h"
#endif

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <new>
#include <span>

struct emke_audio_handle {
  explicit emke_audio_handle(const emke_audio_config& config)
      : runtime(config) {}

  emke::audio::AudioRuntime runtime;
};

namespace {

template <typename Struct>
emke_audio_status validate_abi_struct(const Struct* value) {
  if (value == nullptr || value->size < sizeof(Struct)) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }
  if (value->abi_version != EMKE_AUDIO_ABI_VERSION) {
    return EMKE_AUDIO_ABI_MISMATCH;
  }
  return EMKE_AUDIO_OK;
}

std::span<const std::int16_t> input_pcm_span(const std::int16_t* pcm16,
                                             std::uint32_t frame_count) {
  return {pcm16, static_cast<std::size_t>(frame_count)};
}

std::uint32_t public_discovery_status(
    emke::audio::EndpointDiscoveryStatus status) noexcept {
  using emke::audio::EndpointDiscoveryStatus;
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

std::uint32_t public_data_flow(emke::audio::DeviceDataFlow flow) noexcept {
  return flow == emke::audio::DeviceDataFlow::render
             ? EMKE_AUDIO_ENDPOINT_DATA_FLOW_RENDER
             : EMKE_AUDIO_ENDPOINT_DATA_FLOW_CAPTURE;
}

bool copy_endpoint_id(
    std::span<const char16_t> source,
    std::uint16_t* destination,
    std::uint32_t& out_length) noexcept {
  if (source.size() >= EMKE_AUDIO_ENDPOINT_ID_CAPACITY) {
    return false;
  }
  out_length = static_cast<std::uint32_t>(source.size());
  for (std::size_t index = 0u; index < source.size(); ++index) {
    destination[index] = static_cast<std::uint16_t>(source[index]);
  }
  destination[source.size()] = 0u;
  return true;
}

bool write_discovered_endpoint(
    emke_audio_discovered_endpoint& destination,
    const emke::audio::DeviceEndpoint& source,
    std::uint32_t role) noexcept {
  destination = {};
  destination.size = sizeof(destination);
  destination.abi_version = EMKE_AUDIO_ABI_VERSION;
  destination.role = role;
  destination.data_flow = public_data_flow(source.data_flow);
  destination.state = source.state;
  return copy_endpoint_id(
      source.id,
      destination.endpoint_id,
      destination.endpoint_id_length);
}

void write_source_error(
    emke_audio_endpoint_snapshot& snapshot,
    const std::optional<emke::audio::DeviceCatalogError>& error) noexcept {
  snapshot.discovery_status = EMKE_AUDIO_ENDPOINT_DISCOVERY_SOURCE_ERROR;
  if (error.has_value()) {
    snapshot.source_operation = static_cast<std::uint32_t>(error->operation);
    snapshot.source_native_code = error->native_code;
  }
}

#if defined(_WIN32)
class ScopedComApartment {
 public:
  ScopedComApartment() noexcept
      : result_(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED)) {}

  ~ScopedComApartment() {
    if (SUCCEEDED(result_)) {
      CoUninitialize();
    }
  }

  [[nodiscard]] bool usable() const noexcept {
    return SUCCEEDED(result_) || result_ == RPC_E_CHANGED_MODE;
  }

  [[nodiscard]] std::int32_t error_code() const noexcept {
    return static_cast<std::int32_t>(result_);
  }

 private:
  HRESULT result_ = E_FAIL;
};
#endif

}  // namespace

extern "C" {

EMKE_AUDIO_API emke_audio_status emke_audio_create(
    const emke_audio_config* config,
    emke_audio_handle** out_handle) {
  if (out_handle == nullptr) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }
  *out_handle = nullptr;

  const emke_audio_status validation = validate_abi_struct(config);
  if (validation != EMKE_AUDIO_OK) {
    return validation;
  }

  try {
    auto* handle = new (std::nothrow) emke_audio_handle(*config);
    if (handle == nullptr) {
      return EMKE_AUDIO_INTERNAL_ERROR;
    }
    *out_handle = handle;
    return EMKE_AUDIO_OK;
  } catch (...) {
    return EMKE_AUDIO_INTERNAL_ERROR;
  }
}

EMKE_AUDIO_API void emke_audio_destroy(emke_audio_handle* handle) {
  delete handle;
}

EMKE_AUDIO_API emke_audio_status emke_audio_start(
    emke_audio_handle* handle) {
  if (handle == nullptr) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }
  try {
    return handle->runtime.start();
  } catch (...) {
    return EMKE_AUDIO_INTERNAL_ERROR;
  }
}

EMKE_AUDIO_API emke_audio_status emke_audio_stop(
    emke_audio_handle* handle) {
  if (handle == nullptr) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }
  try {
    return handle->runtime.stop();
  } catch (...) {
    return EMKE_AUDIO_INTERNAL_ERROR;
  }
}

EMKE_AUDIO_API emke_audio_status emke_audio_set_inbound_route(
    emke_audio_handle* handle,
    emke_audio_route route) {
  if (handle == nullptr) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }
  try {
    return handle->runtime.set_inbound_route(route);
  } catch (...) {
    return EMKE_AUDIO_INTERNAL_ERROR;
  }
}

EMKE_AUDIO_API emke_audio_status emke_audio_set_outbound_route(
    emke_audio_handle* handle,
    emke_audio_route route) {
  if (handle == nullptr) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }
  try {
    return handle->runtime.set_outbound_route(route);
  } catch (...) {
    return EMKE_AUDIO_INTERNAL_ERROR;
  }
}

EMKE_AUDIO_API emke_audio_status emke_audio_enqueue_inbound_translation(
    emke_audio_handle* handle,
    const int16_t* pcm16,
    uint32_t frame_count) {
  if (handle == nullptr || pcm16 == nullptr || frame_count == 0u) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }
  try {
    return handle->runtime.enqueue_inbound_translation(
        input_pcm_span(pcm16, frame_count));
  } catch (...) {
    return EMKE_AUDIO_INTERNAL_ERROR;
  }
}

EMKE_AUDIO_API emke_audio_status emke_audio_enqueue_outbound_translation(
    emke_audio_handle* handle,
    const int16_t* pcm16,
    uint32_t frame_count) {
  if (handle == nullptr || pcm16 == nullptr || frame_count == 0u) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }
  try {
    return handle->runtime.enqueue_outbound_translation(
        input_pcm_span(pcm16, frame_count));
  } catch (...) {
    return EMKE_AUDIO_INTERNAL_ERROR;
  }
}

EMKE_AUDIO_API emke_audio_status emke_audio_poll_event(
    emke_audio_handle* handle,
    emke_audio_event* out_event,
    int16_t* pcm16,
    uint32_t pcm_capacity_frames) {
  if (handle == nullptr) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }
  const emke_audio_status validation = validate_abi_struct(out_event);
  if (validation != EMKE_AUDIO_OK) {
    return validation;
  }

  try {
    emke::audio::AudioEvent event;
    const std::size_t effective_capacity =
        pcm16 == nullptr ? 0u
                         : static_cast<std::size_t>(pcm_capacity_frames);
    const emke_audio_status status =
        handle->runtime.poll_event(event, effective_capacity);

    out_event->kind = event.kind;
    out_event->status = event.status;
    out_event->route = event.route;
    out_event->frame_count =
        static_cast<std::uint32_t>(event.pcm16.size());
    out_event->sequence = event.sequence;

    if (status != EMKE_AUDIO_OK) {
      return status;
    }
    if (!event.pcm16.empty()) {
      std::copy(event.pcm16.begin(), event.pcm16.end(), pcm16);
    }
    return EMKE_AUDIO_OK;
  } catch (...) {
    return EMKE_AUDIO_INTERNAL_ERROR;
  }
}

EMKE_AUDIO_API emke_audio_status emke_audio_get_diagnostics(
    emke_audio_handle* handle,
    emke_audio_diagnostics* out_diagnostics) {
  if (handle == nullptr) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }
  const emke_audio_status validation =
      validate_abi_struct(out_diagnostics);
  if (validation != EMKE_AUDIO_OK) {
    return validation;
  }

  try {
    handle->runtime.write_diagnostics(*out_diagnostics);
    return EMKE_AUDIO_OK;
  } catch (...) {
    return EMKE_AUDIO_INTERNAL_ERROR;
  }
}

EMKE_AUDIO_API emke_audio_status emke_audio_discover_endpoints(
    emke_audio_endpoint_snapshot* out_snapshot) {
  const emke_audio_status validation = validate_abi_struct(out_snapshot);
  if (validation != EMKE_AUDIO_OK) {
    return validation;
  }

  try {
    *out_snapshot = {};
    out_snapshot->size = sizeof(*out_snapshot);
    out_snapshot->abi_version = EMKE_AUDIO_ABI_VERSION;

#if defined(_WIN32)
    const ScopedComApartment com;
    if (!com.usable()) {
      write_source_error(
          *out_snapshot,
          emke::audio::DeviceCatalogError{
              .operation = emke::audio::DeviceCatalogOperation::createEnumerator,
              .native_code = com.error_code(),
          });
      return EMKE_AUDIO_OK;
    }
#endif

    emke::audio::DeviceCatalogError creation_error;
    std::unique_ptr<emke::audio::DeviceSource> source =
        emke::audio::create_mm_device_source(creation_error);
    if (source == nullptr) {
      write_source_error(*out_snapshot, creation_error);
      return EMKE_AUDIO_OK;
    }

    emke::audio::DeviceCatalog catalog(*source);
    const emke::audio::EndpointDiscoveryResult result =
        emke::audio::discover_endpoints(catalog);
    out_snapshot->discovery_status = public_discovery_status(result.status);
    if (result.status == emke::audio::EndpointDiscoveryStatus::sourceError) {
      write_source_error(*out_snapshot, result.error);
      return EMKE_AUDIO_OK;
    }
    if (result.status != emke::audio::EndpointDiscoveryStatus::ready) {
      return EMKE_AUDIO_OK;
    }

    for (std::size_t index = 0u;
         index < EMKE_AUDIO_DISCOVERED_ENDPOINT_COUNT;
         ++index) {
      if (!write_discovered_endpoint(
              out_snapshot->virtual_endpoints[index],
              result.virtual_endpoints[index],
              static_cast<std::uint32_t>(index))) {
        write_source_error(*out_snapshot, std::nullopt);
        return EMKE_AUDIO_OK;
      }
    }
    if (!copy_endpoint_id(
            result.default_physical_input_id,
            out_snapshot->physical_input_endpoint_id,
            out_snapshot->physical_input_endpoint_id_length) ||
        !copy_endpoint_id(
            result.default_physical_output_id,
            out_snapshot->physical_output_endpoint_id,
            out_snapshot->physical_output_endpoint_id_length)) {
      write_source_error(*out_snapshot, std::nullopt);
      return EMKE_AUDIO_OK;
    }
    return EMKE_AUDIO_OK;
  } catch (...) {
    if (out_snapshot != nullptr) {
      *out_snapshot = {};
      out_snapshot->size = sizeof(*out_snapshot);
      out_snapshot->abi_version = EMKE_AUDIO_ABI_VERSION;
      write_source_error(*out_snapshot, std::nullopt);
    }
    return EMKE_AUDIO_OK;
  }
}

#if defined(EMKE_NATIVE_AUDIO_TEST_HOOKS)
EMKE_AUDIO_TEST_API emke_audio_status
emke_audio_test_accept_synthetic_float32(
    emke_audio_handle* handle,
    emke_audio_test_direction direction,
    const float* interleaved_stereo,
    uint32_t local_frame_count) {
  if (handle == nullptr || interleaved_stereo == nullptr ||
      local_frame_count == 0u) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }
  if (direction != EMKE_AUDIO_TEST_DIRECTION_INBOUND &&
      direction != EMKE_AUDIO_TEST_DIRECTION_OUTBOUND) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }

  try {
    const auto runtime_direction =
        direction == EMKE_AUDIO_TEST_DIRECTION_INBOUND
            ? emke::audio::Direction::Inbound
            : emke::audio::Direction::Outbound;
    return handle->runtime.test_accept_synthetic(
        runtime_direction,
        {interleaved_stereo,
         static_cast<std::size_t>(local_frame_count) * 2u});
  } catch (...) {
    return EMKE_AUDIO_INTERNAL_ERROR;
  }
}

EMKE_AUDIO_TEST_API emke_audio_status emke_audio_test_render_pcm16(
    emke_audio_handle* handle,
    emke_audio_test_direction direction,
    int16_t* mono_pcm16,
    uint32_t network_frame_count) {
  if (handle == nullptr || mono_pcm16 == nullptr ||
      network_frame_count == 0u) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }
  if (direction != EMKE_AUDIO_TEST_DIRECTION_INBOUND &&
      direction != EMKE_AUDIO_TEST_DIRECTION_OUTBOUND) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }

  try {
    const auto runtime_direction =
        direction == EMKE_AUDIO_TEST_DIRECTION_INBOUND
            ? emke::audio::Direction::Inbound
            : emke::audio::Direction::Outbound;
    return handle->runtime.test_render(
        runtime_direction,
        {mono_pcm16, static_cast<std::size_t>(network_frame_count)});
  } catch (...) {
    return EMKE_AUDIO_INTERNAL_ERROR;
  }
}

EMKE_AUDIO_TEST_API emke_audio_status emke_audio_test_inject_failure(
    emke_audio_handle* handle,
    emke_audio_test_failure failure) {
  if (handle == nullptr) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }

  switch (failure) {
    case EMKE_AUDIO_TEST_FAILURE_DEVICE:
      handle->runtime.test_inject_device_failure();
      return EMKE_AUDIO_OK;
    case EMKE_AUDIO_TEST_FAILURE_INBOUND_TRANSLATION:
      handle->runtime.test_inject_inbound_translation_failure();
      return EMKE_AUDIO_OK;
    case EMKE_AUDIO_TEST_FAILURE_OUTBOUND_UNDERRUN:
      handle->runtime.test_inject_outbound_underrun();
      return EMKE_AUDIO_OK;
  }
  return EMKE_AUDIO_INVALID_ARGUMENT;
}
#endif

}  // extern "C"
