#include "emke_native_audio.h"

#include "audio_runtime.hpp"
#include "device_catalog.hpp"
#include "endpoint_snapshot.hpp"

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
#include <limits>
#include <new>
#include <span>
#include <thread>
#include <vector>

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

class ScopedMtaComApartment {
 public:
  ScopedMtaComApartment() noexcept
      : result_(CoInitializeEx(nullptr, COINIT_MULTITHREADED)) {}

  ~ScopedMtaComApartment() {
    if (SUCCEEDED(result_)) {
      CoUninitialize();
    }
  }

  [[nodiscard]] bool usable() const noexcept {
    return SUCCEEDED(result_);
  }

 private:
  HRESULT result_ = E_FAIL;
};
#endif

struct EndpointEnumeration {
  emke_audio_status status = EMKE_AUDIO_INTERNAL_ERROR;
  std::vector<emke::audio::DeviceEndpoint> endpoints;
  std::u16string default_physical_input_id;
  std::u16string default_physical_output_id;
};

bool is_bounded_utf16(std::u16string_view value,
                      std::size_t capacity) noexcept {
  return !value.empty() && value.size() < capacity &&
         value.find(u'\0') == std::u16string_view::npos;
}

template <std::size_t Capacity>
bool copy_bounded_utf16(std::u16string_view source,
                        std::uint16_t (&destination)[Capacity]) noexcept {
  if (!is_bounded_utf16(source, Capacity)) {
    return false;
  }
  for (std::size_t index = 0u; index < source.size(); ++index) {
    destination[index] = static_cast<std::uint16_t>(source[index]);
  }
  destination[source.size()] = 0u;
  return true;
}

bool copy_role(std::string_view source,
               std::uint16_t (&destination)[EMKE_AUDIO_ENDPOINT_ROLE_CAPACITY])
    noexcept {
  if (source.empty() || source.size() >= EMKE_AUDIO_ENDPOINT_ROLE_CAPACITY) {
    return false;
  }
  for (std::size_t index = 0u; index < source.size(); ++index) {
    destination[index] = static_cast<std::uint16_t>(source[index]);
  }
  destination[source.size()] = 0u;
  return true;
}

bool write_endpoint_descriptor(
    const emke::audio::DeviceEndpoint& endpoint,
    bool is_default,
    emke_audio_endpoint_descriptor_v1& destination) noexcept {
  destination = {};
  destination.size = sizeof(destination);
  destination.direction = endpoint.data_flow == emke::audio::DeviceDataFlow::render
                              ? EMKE_AUDIO_ENDPOINT_DATA_FLOW_RENDER
                              : EMKE_AUDIO_ENDPOINT_DATA_FLOW_CAPTURE;
  destination.flags = EMKE_AUDIO_ENDPOINT_FLAG_ACTIVE;
  if (endpoint.has_emke_role_property) {
    if (!endpoint.role.has_value() ||
        endpoint.data_flow != emke::audio::endpoint_role_data_flow(*endpoint.role) ||
        !copy_role(emke::audio::endpoint_role_string(*endpoint.role),
                   destination.role)) {
      return false;
    }
    destination.flags |= EMKE_AUDIO_ENDPOINT_FLAG_VIRTUAL_ROLE;
  } else if (is_default) {
    destination.flags |= EMKE_AUDIO_ENDPOINT_FLAG_PHYSICAL_DEFAULT;
  }
  return copy_bounded_utf16(endpoint.id, destination.id) &&
         copy_bounded_utf16(endpoint.friendly_name, destination.name);
}

EndpointEnumeration enumerate_active_endpoints_on_mta_worker() noexcept {
  EndpointEnumeration result;
#if defined(_WIN32)
  try {
    std::thread worker([&result] {
      try {
        const ScopedMtaComApartment com;
        if (!com.usable()) {
          result.status = EMKE_AUDIO_INTERNAL_ERROR;
          return;
        }

        emke::audio::DeviceCatalogError creation_error;
        std::unique_ptr<emke::audio::DeviceSource> source =
            emke::audio::create_mm_device_source(creation_error);
        if (source == nullptr) {
          result.status = EMKE_AUDIO_INTERNAL_ERROR;
          return;
        }

        emke::audio::DeviceCatalog catalog(*source);
        const emke::audio::CatalogRefreshResult refresh = catalog.refresh();
        if (!refresh.ok) {
          result.status = EMKE_AUDIO_INTERNAL_ERROR;
          return;
        }

        const emke::audio::VirtualEndpointAssessment virtuals =
            catalog.virtual_endpoint_assessment();
        if (!virtuals.ready) {
          result.status = EMKE_AUDIO_DEVICE_MISSING;
          return;
        }

        const emke::audio::PhysicalEndpointResolution input =
            catalog.resolve_physical({
                .mode = emke::audio::PhysicalEndpointMode::followDefault,
                .data_flow = emke::audio::DeviceDataFlow::capture,
            });
        const emke::audio::PhysicalEndpointResolution output =
            catalog.resolve_physical({
                .mode = emke::audio::PhysicalEndpointMode::followDefault,
                .data_flow = emke::audio::DeviceDataFlow::render,
            });
        if (input.status != emke::audio::PhysicalResolutionStatus::resolved ||
            output.status != emke::audio::PhysicalResolutionStatus::resolved ||
            input.endpoint == nullptr || output.endpoint == nullptr) {
          result.status = EMKE_AUDIO_DEVICE_MISSING;
          return;
        }

        result.default_physical_input_id = input.endpoint->id;
        result.default_physical_output_id = output.endpoint->id;
        const auto snapshot = catalog.snapshot();
        for (std::size_t index = 0u; index < snapshot->size(); ++index) {
          const emke::audio::DeviceEndpoint endpoint = snapshot->endpoint_at(index);
          if (endpoint.state == emke::audio::deviceStateActive) {
            result.endpoints.push_back(endpoint);
          }
        }
        result.status = EMKE_AUDIO_OK;
      } catch (...) {
        result.status = EMKE_AUDIO_INTERNAL_ERROR;
      }
    });
    worker.join();
  } catch (...) {
    result.status = EMKE_AUDIO_INTERNAL_ERROR;
  }
#else
  result.status = EMKE_AUDIO_INTERNAL_ERROR;
#endif
  return result;
}

}  // namespace

extern "C" {

EMKE_AUDIO_API std::uint32_t emke_audio_get_abi_version(void) {
  return EMKE_AUDIO_ABI_VERSION;
}

EMKE_AUDIO_API std::uint32_t emke_audio_sizeof_config(void) {
  return static_cast<std::uint32_t>(sizeof(emke_audio_config));
}

EMKE_AUDIO_API std::uint32_t emke_audio_sizeof_event(void) {
  return static_cast<std::uint32_t>(sizeof(emke_audio_event));
}

EMKE_AUDIO_API std::uint32_t emke_audio_sizeof_diagnostics(void) {
  return static_cast<std::uint32_t>(sizeof(emke_audio_diagnostics));
}

EMKE_AUDIO_API std::uint32_t emke_audio_sizeof_discovered_endpoint(void) {
  return static_cast<std::uint32_t>(
      sizeof(emke_audio_discovered_endpoint));
}

EMKE_AUDIO_API std::uint32_t emke_audio_sizeof_endpoint_snapshot(void) {
  return static_cast<std::uint32_t>(
      sizeof(emke_audio_endpoint_snapshot));
}

EMKE_AUDIO_API std::uint32_t emke_audio_sizeof_endpoint_descriptor_v1(void) {
  return static_cast<std::uint32_t>(
      sizeof(emke_audio_endpoint_descriptor_v1));
}

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
      (void)emke::audio::write_endpoint_snapshot(
          emke::audio::EndpointDiscoveryResult{
              .status = emke::audio::EndpointDiscoveryStatus::sourceError,
              .error = emke::audio::DeviceCatalogError{
                  .operation = emke::audio::DeviceCatalogOperation::createEnumerator,
                  .native_code = com.error_code(),
              },
          },
          *out_snapshot);
      return EMKE_AUDIO_OK;
    }
#endif

    emke::audio::DeviceCatalogError creation_error;
    std::unique_ptr<emke::audio::DeviceSource> source =
        emke::audio::create_mm_device_source(creation_error);
    if (source == nullptr) {
      (void)emke::audio::write_endpoint_snapshot(
          emke::audio::EndpointDiscoveryResult{
              .status = emke::audio::EndpointDiscoveryStatus::sourceError,
              .error = creation_error,
          },
          *out_snapshot);
      return EMKE_AUDIO_OK;
    }

    emke::audio::DeviceCatalog catalog(*source);
    const emke::audio::EndpointDiscoveryResult result =
        emke::audio::discover_endpoints(catalog);
    (void)emke::audio::write_endpoint_snapshot(result, *out_snapshot);
    return EMKE_AUDIO_OK;
  } catch (...) {
    if (out_snapshot != nullptr) {
      *out_snapshot = {};
      out_snapshot->size = sizeof(*out_snapshot);
      out_snapshot->abi_version = EMKE_AUDIO_ABI_VERSION;
      (void)emke::audio::write_endpoint_snapshot(
          emke::audio::EndpointDiscoveryResult{}, *out_snapshot);
    }
    return EMKE_AUDIO_OK;
  }
}

EMKE_AUDIO_API emke_audio_status emke_audio_enumerate_endpoints_v1(
    emke_audio_endpoint_descriptor_v1* items,
    std::uint32_t capacity,
    std::uint32_t* required_count) {
  if (required_count == nullptr || (items == nullptr && capacity != 0u) ||
      (items != nullptr && capacity == 0u)) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }

  try {
    const EndpointEnumeration enumeration =
        enumerate_active_endpoints_on_mta_worker();
    if (enumeration.status != EMKE_AUDIO_OK) {
      return enumeration.status;
    }
    if (enumeration.endpoints.size() >
        static_cast<std::size_t>((std::numeric_limits<std::uint32_t>::max)())) {
      return EMKE_AUDIO_INTERNAL_ERROR;
    }

    const std::uint32_t required =
        static_cast<std::uint32_t>(enumeration.endpoints.size());
    *required_count = required;
    if (items == nullptr) {
      return EMKE_AUDIO_OK;
    }
    if (capacity < required) {
      return EMKE_AUDIO_INVALID_ARGUMENT;
    }

    std::vector<emke_audio_endpoint_descriptor_v1> descriptors(required);
    for (std::size_t index = 0u; index < enumeration.endpoints.size(); ++index) {
      const emke::audio::DeviceEndpoint& endpoint = enumeration.endpoints[index];
      const bool is_default = !endpoint.has_emke_role_property &&
          (endpoint.id == enumeration.default_physical_input_id ||
           endpoint.id == enumeration.default_physical_output_id);
      if (!write_endpoint_descriptor(endpoint, is_default, descriptors[index])) {
        return EMKE_AUDIO_INVALID_ARGUMENT;
      }
    }
    std::copy(descriptors.begin(), descriptors.end(), items);
    return EMKE_AUDIO_OK;
  } catch (...) {
    return EMKE_AUDIO_INTERNAL_ERROR;
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
