#if defined(_WIN32)
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <initguid.h>
#endif

#include "device_catalog.hpp"

#include <array>
#include <atomic>
#include <new>
#include <utility>

#if defined(_WIN32)
#include <mmdeviceapi.h>
#include <propvarutil.h>
#include <propsys.h>

#include <cwchar>
#include <string_view>
#endif

namespace emke::audio {

namespace {

constexpr std::array all_roles = {
    EndpointRole::meetingSpeakerRender,
    EndpointRole::appSpeakerCapture,
    EndpointRole::appMicrophoneRender,
    EndpointRole::meetingMicrophoneCapture,
};

DeviceCatalogError allocation_error() noexcept {
  return DeviceCatalogError{
      .operation = DeviceCatalogOperation::outOfMemory,
      .native_code = 0,
  };
}

DeviceCatalogError unexpected_error() noexcept {
  return DeviceCatalogError{
      .operation = DeviceCatalogOperation::unexpectedFailure,
      .native_code = 0,
  };
}

#if defined(_WIN32)

DeviceCatalogError windows_error(
    DeviceCatalogOperation operation,
    HRESULT result) noexcept {
  return DeviceCatalogError{
      .operation = operation,
      .native_code = static_cast<std::int32_t>(result),
  };
}

template <typename Interface>
class ComPtr {
 public:
  ComPtr() = default;
  ~ComPtr() {
    reset();
  }

  ComPtr(const ComPtr&) = delete;
  ComPtr& operator=(const ComPtr&) = delete;

  ComPtr(ComPtr&& other) noexcept : value_(std::exchange(other.value_, nullptr)) {
  }

  ComPtr& operator=(ComPtr&& other) noexcept {
    if (this != &other) {
      reset();
      value_ = std::exchange(other.value_, nullptr);
    }
    return *this;
  }

  [[nodiscard]] Interface* get() const noexcept {
    return value_;
  }

  [[nodiscard]] Interface** put() noexcept {
    reset();
    return &value_;
  }

  Interface* operator->() const noexcept {
    return value_;
  }

  void reset() noexcept {
    if (value_ != nullptr) {
      value_->Release();
      value_ = nullptr;
    }
  }

 private:
  Interface* value_ = nullptr;
};

class CoTaskMemWideString {
 public:
  CoTaskMemWideString() = default;

  ~CoTaskMemWideString() {
    if (value_ != nullptr) {
      CoTaskMemFree(value_);
    }
  }

  CoTaskMemWideString(const CoTaskMemWideString&) = delete;
  CoTaskMemWideString& operator=(const CoTaskMemWideString&) = delete;

  [[nodiscard]] LPWSTR* put() noexcept {
    return &value_;
  }

  [[nodiscard]] const wchar_t* get() const noexcept {
    return value_;
  }

 private:
  LPWSTR value_ = nullptr;
};

class PropVariantValue {
 public:
  PropVariantValue() noexcept {
    PropVariantInit(&value_);
  }

  ~PropVariantValue() {
    PropVariantClear(&value_);
  }

  PropVariantValue(const PropVariantValue&) = delete;
  PropVariantValue& operator=(const PropVariantValue&) = delete;

  [[nodiscard]] PROPVARIANT* put() noexcept {
    return &value_;
  }

  [[nodiscard]] const PROPVARIANT& get() const noexcept {
    return value_;
  }

 private:
  PROPVARIANT value_{};
};

std::u16string copy_endpoint_id(const wchar_t* value) {
  static_assert(sizeof(wchar_t) == sizeof(char16_t));
  if (value == nullptr) {
    return {};
  }
  const std::size_t length = std::wcslen(value);
  std::u16string result(length, u'\0');
  for (std::size_t index = 0u; index < length; ++index) {
    result[index] = static_cast<char16_t>(value[index]);
  }
  return result;
}

std::optional<EndpointRole> parse_wide_endpoint_role(
    std::wstring_view value) noexcept {
  for (EndpointRole role : all_roles) {
    const std::string_view stable_value = endpoint_role_string(role);
    if (value.size() != stable_value.size()) {
      continue;
    }
    bool equal = true;
    for (std::size_t index = 0u; index < value.size(); ++index) {
      if (value[index] != static_cast<wchar_t>(stable_value[index])) {
        equal = false;
        break;
      }
    }
    if (equal) {
      return role;
    }
  }
  return std::nullopt;
}

PROPERTYKEY endpoint_role_property_key() noexcept {
  return PROPERTYKEY{
      .fmtid = DEVPKEY_EMKE_EndpointRole.fmtid,
      .pid = DEVPKEY_EMKE_EndpointRole.pid,
  };
}

struct EndpointReadResult {
  std::optional<DeviceEndpoint> endpoint;
  std::optional<DeviceCatalogError> error;
};

EndpointReadResult read_endpoint(IMMDevice* device) {
  CoTaskMemWideString id;
  HRESULT result = device->GetId(id.put());
  if (FAILED(result)) {
    return {
        .error =
            windows_error(DeviceCatalogOperation::getEndpointId, result),
    };
  }
  if (id.get() == nullptr || id.get()[0] == L'\0') {
    return {
        .error = windows_error(
            DeviceCatalogOperation::getEndpointId, E_UNEXPECTED),
    };
  }

  DWORD state = 0u;
  result = device->GetState(&state);
  if (FAILED(result)) {
    return {
        .error =
            windows_error(DeviceCatalogOperation::getEndpointState, result),
    };
  }

  ComPtr<IMMEndpoint> endpoint_interface;
  result = device->QueryInterface(
      __uuidof(IMMEndpoint),
      reinterpret_cast<void**>(endpoint_interface.put()));
  if (FAILED(result)) {
    return {
        .error =
            windows_error(DeviceCatalogOperation::getEndpointDataFlow, result),
    };
  }
  EDataFlow native_flow = eAll;
  result = endpoint_interface->GetDataFlow(&native_flow);
  if (FAILED(result) || (native_flow != eRender && native_flow != eCapture)) {
    return {
        .error = windows_error(
            DeviceCatalogOperation::getEndpointDataFlow,
            FAILED(result) ? result : E_UNEXPECTED),
    };
  }

  ComPtr<IPropertyStore> properties;
  result = device->OpenPropertyStore(STGM_READ, properties.put());
  if (FAILED(result)) {
    return {
        .error =
            windows_error(DeviceCatalogOperation::openPropertyStore, result),
    };
  }

  PropVariantValue role_value;
  const PROPERTYKEY role_key = endpoint_role_property_key();
  result = properties->GetValue(role_key, role_value.put());
  if (FAILED(result)) {
    return {
        .error =
            windows_error(DeviceCatalogOperation::readRoleProperty, result),
    };
  }

  std::optional<EndpointRole> role;
  bool has_role_property = false;
  const PROPVARIANT& property = role_value.get();
  if (property.vt == VT_LPWSTR && property.pwszVal != nullptr) {
    has_role_property = true;
    role = parse_wide_endpoint_role(property.pwszVal);
  } else if (property.vt != VT_EMPTY && property.vt != VT_NULL) {
    return {
        .error = windows_error(
            DeviceCatalogOperation::readRoleProperty,
            HRESULT_FROM_WIN32(ERROR_DATATYPE_MISMATCH)),
    };
  }

  return {
      .endpoint =
          DeviceEndpoint{
              .id = copy_endpoint_id(id.get()),
              .state = static_cast<std::uint32_t>(state),
              .data_flow = native_flow == eRender ? DeviceDataFlow::render
                                                  : DeviceDataFlow::capture,
              .role = role,
              .has_emke_role_property = has_role_property,
          },
  };
}

class MmDeviceSource final : public DeviceSource {
 public:
  explicit MmDeviceSource(ComPtr<IMMDeviceEnumerator> enumerator) noexcept
      : enumerator_(std::move(enumerator)) {}

  DeviceEnumeration enumerate() override {
    try {
      ComPtr<IMMDeviceCollection> collection;
      HRESULT result = enumerator_->EnumAudioEndpoints(
          eAll, DEVICE_STATEMASK_ALL, collection.put());
      if (FAILED(result)) {
        return {
            .error = windows_error(
                DeviceCatalogOperation::enumerateEndpoints, result),
        };
      }

      UINT count = 0u;
      result = collection->GetCount(&count);
      if (FAILED(result)) {
        return {
            .error = windows_error(
                DeviceCatalogOperation::getCollectionCount, result),
        };
      }

      DeviceEnumeration enumeration;
      enumeration.endpoints.reserve(count);
      for (UINT index = 0u; index < count; ++index) {
        ComPtr<IMMDevice> device;
        result = collection->Item(index, device.put());
        if (FAILED(result)) {
          return {
              .error = windows_error(
                  DeviceCatalogOperation::getCollectionItem, result),
          };
        }

        EndpointReadResult endpoint = read_endpoint(device.get());
        if (endpoint.error.has_value()) {
          return {.error = endpoint.error};
        }
        enumeration.endpoints.push_back(std::move(*endpoint.endpoint));
      }
      return enumeration;
    } catch (const std::bad_alloc&) {
      return {.error = allocation_error()};
    } catch (...) {
      return {.error = unexpected_error()};
    }
  }

  DefaultEndpointResult default_endpoint_id(DeviceDataFlow flow) override {
    try {
      ComPtr<IMMDevice> device;
      const EDataFlow native_flow =
          flow == DeviceDataFlow::render ? eRender : eCapture;
      HRESULT result = enumerator_->GetDefaultAudioEndpoint(
          native_flow, eConsole, device.put());
      if (FAILED(result)) {
        return {
            .error = windows_error(
                DeviceCatalogOperation::getDefaultEndpoint, result),
        };
      }
      CoTaskMemWideString id;
      result = device->GetId(id.put());
      if (FAILED(result)) {
        return {
            .error =
                windows_error(DeviceCatalogOperation::getEndpointId, result),
        };
      }
      return {.endpoint_id = copy_endpoint_id(id.get())};
    } catch (const std::bad_alloc&) {
      return {.error = allocation_error()};
    } catch (...) {
      return {.error = unexpected_error()};
    }
  }

 private:
  ComPtr<IMMDeviceEnumerator> enumerator_;
};

#endif

}  // namespace

VirtualEndpointAssessment assess_virtual_endpoints(
    std::span<const DeviceEndpoint> endpoints) noexcept {
  for (const DeviceEndpoint& endpoint : endpoints) {
    if (endpoint.has_emke_role_property && !endpoint.role.has_value()) {
      return {
          .ready = false,
          .problem = VirtualEndpointProblem::invalidRole,
          .role = std::nullopt,
          .matching_endpoint_count = 1u,
          .expected_flow = endpoint.data_flow,
          .observed_flow = endpoint.data_flow,
      };
    }
  }

  for (EndpointRole role : all_roles) {
    const DeviceDataFlow expected_flow = endpoint_role_data_flow(role);
    std::size_t matching_count = 0u;
    DeviceDataFlow observed_flow = expected_flow;
    for (const DeviceEndpoint& endpoint : endpoints) {
      if (endpoint.role == role) {
        ++matching_count;
        observed_flow = endpoint.data_flow;
      }
    }

    if (matching_count == 0u) {
      return {
          .ready = false,
          .problem = VirtualEndpointProblem::missingRole,
          .role = role,
          .matching_endpoint_count = 0u,
          .expected_flow = expected_flow,
          .observed_flow = expected_flow,
      };
    }
    if (matching_count > 1u) {
      return {
          .ready = false,
          .problem = VirtualEndpointProblem::duplicateRole,
          .role = role,
          .matching_endpoint_count = matching_count,
          .expected_flow = expected_flow,
          .observed_flow = observed_flow,
      };
    }
    if (observed_flow != expected_flow) {
      return {
          .ready = false,
          .problem = VirtualEndpointProblem::wrongDataFlow,
          .role = role,
          .matching_endpoint_count = 1u,
          .expected_flow = expected_flow,
          .observed_flow = observed_flow,
      };
    }
    for (const DeviceEndpoint& endpoint : endpoints) {
      if (endpoint.role == role && endpoint.state != deviceStateActive) {
        return {
            .ready = false,
            .problem = VirtualEndpointProblem::inactiveRole,
            .role = role,
            .matching_endpoint_count = 1u,
            .expected_flow = expected_flow,
            .observed_flow = observed_flow,
        };
      }
    }
  }

  return {
      .ready = true,
      .problem = VirtualEndpointProblem::none,
      .role = EndpointRole::meetingSpeakerRender,
      .matching_endpoint_count = 1u,
      .expected_flow = DeviceDataFlow::render,
      .observed_flow = DeviceDataFlow::render,
  };
}

DeviceCatalogSnapshot::DeviceCatalogSnapshot(
    std::vector<DeviceEndpoint> endpoints)
    : endpoints_(std::move(endpoints)) {}

std::size_t DeviceCatalogSnapshot::size() const noexcept {
  return endpoints_.size();
}

DeviceEndpoint DeviceCatalogSnapshot::endpoint_at(std::size_t index) const {
  return endpoints_.at(index);
}

DeviceCatalog::DeviceCatalog(DeviceSource& source)
    : source_(source),
      snapshot_(std::make_shared<const DeviceCatalogSnapshot>(
          std::vector<DeviceEndpoint>{})) {}

CatalogRefreshResult DeviceCatalog::refresh() noexcept {
  try {
    DeviceEnumeration enumeration;
    {
      const std::lock_guard lock(source_mutex_);
      enumeration = source_.enumerate();
    }
    if (enumeration.error.has_value()) {
      return {.ok = false, .error = enumeration.error};
    }
    auto replacement = std::make_shared<const DeviceCatalogSnapshot>(
        std::move(enumeration.endpoints));
    std::atomic_store_explicit(
        &snapshot_, std::move(replacement), std::memory_order_release);
    return {.ok = true};
  } catch (const std::bad_alloc&) {
    return {.ok = false, .error = allocation_error()};
  } catch (...) {
    return {.ok = false, .error = unexpected_error()};
  }
}

std::shared_ptr<const DeviceCatalogSnapshot> DeviceCatalog::snapshot()
    const noexcept {
  return std::atomic_load_explicit(&snapshot_, std::memory_order_acquire);
}

VirtualEndpointAssessment DeviceCatalog::virtual_endpoint_assessment()
    const noexcept {
  const auto current = snapshot();
  return assess_virtual_endpoints(current->endpoints_);
}

const DeviceEndpoint* DeviceCatalog::endpoint_with_id(
    const DeviceCatalogSnapshot& snapshot,
    std::u16string_view id) noexcept {
  for (const DeviceEndpoint& endpoint : snapshot.endpoints_) {
    if (endpoint.id == id) {
      return &endpoint;
    }
  }
  return nullptr;
}

PhysicalEndpointResolution DeviceCatalog::resolve_physical(
    const PhysicalEndpointSelection& selection) noexcept {
  std::u16string_view endpoint_id = selection.saved_endpoint_id;
  DefaultEndpointResult default_endpoint;
  if (selection.mode == PhysicalEndpointMode::followDefault) {
    try {
      const std::lock_guard lock(source_mutex_);
      default_endpoint = source_.default_endpoint_id(selection.data_flow);
    } catch (const std::bad_alloc&) {
      return {
          .status = PhysicalResolutionStatus::sourceError,
          .error = allocation_error(),
      };
    } catch (...) {
      return {
          .status = PhysicalResolutionStatus::sourceError,
          .error = unexpected_error(),
      };
    }
    if (default_endpoint.error.has_value()) {
      return {
          .status = PhysicalResolutionStatus::sourceError,
          .error = default_endpoint.error,
      };
    }
    endpoint_id = default_endpoint.endpoint_id;
  }

  const auto current = snapshot();
  const DeviceEndpoint* endpoint = endpoint_with_id(*current, endpoint_id);
  if (endpoint == nullptr) {
    return {.status = PhysicalResolutionStatus::missing};
  }
  if (endpoint->data_flow != selection.data_flow) {
    return {.status = PhysicalResolutionStatus::wrongDataFlow};
  }
  if (endpoint->has_emke_role_property) {
    return {.status = PhysicalResolutionStatus::virtualEndpoint};
  }
  if (endpoint->state != deviceStateActive) {
    return {.status = PhysicalResolutionStatus::unavailable};
  }
  return {
      .status = PhysicalResolutionStatus::resolved,
      .endpoint =
          std::shared_ptr<const DeviceEndpoint>(current, endpoint),
  };
}

EndpointDiscoveryResult discover_endpoints(DeviceCatalog& catalog) noexcept {
  try {
    const CatalogRefreshResult refresh = catalog.refresh();
    if (!refresh.ok) {
      return {
          .status = EndpointDiscoveryStatus::sourceError,
          .error = refresh.error,
      };
    }

    const auto current = catalog.snapshot();
    bool has_emke_role_property = false;
    for (std::size_t index = 0u; index < current->size(); ++index) {
      has_emke_role_property =
          has_emke_role_property || current->endpoint_at(index).has_emke_role_property;
    }

    const VirtualEndpointAssessment assessment =
        catalog.virtual_endpoint_assessment();
    if (!assessment.ready) {
      return {
          .status = has_emke_role_property
                        ? EndpointDiscoveryStatus::virtualEndpointsPartial
                        : EndpointDiscoveryStatus::driverMissing,
      };
    }

    EndpointDiscoveryResult result;
    for (std::size_t index = 0u; index < current->size(); ++index) {
      const DeviceEndpoint endpoint = current->endpoint_at(index);
      if (endpoint.role.has_value()) {
        result.virtual_endpoints[static_cast<std::size_t>(*endpoint.role)] =
            endpoint;
      }
    }
    result.virtual_endpoints_ready = true;

    const PhysicalEndpointSelection output_selection{
        .mode = PhysicalEndpointMode::followDefault,
        .data_flow = DeviceDataFlow::render,
    };
    const PhysicalEndpointResolution output =
        catalog.resolve_physical(output_selection);
    if (output.status == PhysicalResolutionStatus::sourceError) {
      result.status = EndpointDiscoveryStatus::sourceError;
      result.error = output.error;
      return result;
    }
    if (output.status != PhysicalResolutionStatus::resolved ||
        output.endpoint == nullptr) {
      result.status = EndpointDiscoveryStatus::physicalOutputMissing;
      return result;
    }
    result.default_physical_output_id = output.endpoint->id;

    const PhysicalEndpointSelection input_selection{
        .mode = PhysicalEndpointMode::followDefault,
        .data_flow = DeviceDataFlow::capture,
    };
    const PhysicalEndpointResolution input =
        catalog.resolve_physical(input_selection);
    if (input.status == PhysicalResolutionStatus::sourceError) {
      result.status = EndpointDiscoveryStatus::sourceError;
      result.error = input.error;
      return result;
    }
    if (input.status != PhysicalResolutionStatus::resolved ||
        input.endpoint == nullptr) {
      result.status = EndpointDiscoveryStatus::physicalInputMissing;
      return result;
    }
    result.default_physical_input_id = input.endpoint->id;
    result.status = EndpointDiscoveryStatus::ready;
    return result;
  } catch (const std::bad_alloc&) {
    return {
        .status = EndpointDiscoveryStatus::sourceError,
        .error = allocation_error(),
    };
  } catch (...) {
    return {
        .status = EndpointDiscoveryStatus::sourceError,
        .error = unexpected_error(),
    };
  }
}

std::unique_ptr<DeviceSource> create_mm_device_source(
    DeviceCatalogError& error) noexcept {
#if defined(_WIN32)
  try {
    ComPtr<IMMDeviceEnumerator> enumerator;
    const HRESULT result = CoCreateInstance(
        __uuidof(MMDeviceEnumerator),
        nullptr,
        CLSCTX_ALL,
        __uuidof(IMMDeviceEnumerator),
        reinterpret_cast<void**>(enumerator.put()));
    if (FAILED(result)) {
      error = windows_error(DeviceCatalogOperation::createEnumerator, result);
      return nullptr;
    }
    return std::make_unique<MmDeviceSource>(std::move(enumerator));
  } catch (const std::bad_alloc&) {
    error = allocation_error();
    return nullptr;
  } catch (...) {
    error = unexpected_error();
    return nullptr;
  }
#else
  error = DeviceCatalogError{
      .operation = DeviceCatalogOperation::platformUnsupported,
      .native_code = -1,
  };
  return nullptr;
#endif
}

}  // namespace emke::audio
