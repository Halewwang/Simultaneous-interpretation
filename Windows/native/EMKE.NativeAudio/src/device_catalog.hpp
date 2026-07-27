#ifndef EMKE_DEVICE_CATALOG_HPP
#define EMKE_DEVICE_CATALOG_HPP

#include <array>
#include <cstddef>
#include <cstdint>
#include <memory>
#include <mutex>
#include <optional>
#include <span>
#include <string>
#include <string_view>
#include <vector>

#include "emke_endpoint_contract.h"

namespace emke::audio {

inline constexpr std::uint32_t deviceStateActive = 0x00000001u;

enum class DeviceDataFlow : std::uint8_t {
  render,
  capture,
};

enum class EndpointRole : std::uint8_t {
  meetingSpeakerRender,
  appSpeakerCapture,
  appMicrophoneRender,
  meetingMicrophoneCapture,
};

inline constexpr std::string_view meetingSpeakerRenderRole =
    EMKE_ROLE_MEETING_SPEAKER_RENDER_UTF8;
inline constexpr std::string_view appSpeakerCaptureRole =
    EMKE_ROLE_APP_SPEAKER_CAPTURE_UTF8;
inline constexpr std::string_view appMicrophoneRenderRole =
    EMKE_ROLE_APP_MICROPHONE_RENDER_UTF8;
inline constexpr std::string_view meetingMicrophoneCaptureRole =
    EMKE_ROLE_MEETING_MICROPHONE_CAPTURE_UTF8;

[[nodiscard]] constexpr std::string_view endpoint_role_string(
    EndpointRole role) noexcept {
  switch (role) {
    case EndpointRole::meetingSpeakerRender:
      return meetingSpeakerRenderRole;
    case EndpointRole::appSpeakerCapture:
      return appSpeakerCaptureRole;
    case EndpointRole::appMicrophoneRender:
      return appMicrophoneRenderRole;
    case EndpointRole::meetingMicrophoneCapture:
      return meetingMicrophoneCaptureRole;
  }
  return {};
}

[[nodiscard]] constexpr std::optional<EndpointRole> parse_endpoint_role(
    std::string_view value) noexcept {
  if (value == meetingSpeakerRenderRole) {
    return EndpointRole::meetingSpeakerRender;
  }
  if (value == appSpeakerCaptureRole) {
    return EndpointRole::appSpeakerCapture;
  }
  if (value == appMicrophoneRenderRole) {
    return EndpointRole::appMicrophoneRender;
  }
  if (value == meetingMicrophoneCaptureRole) {
    return EndpointRole::meetingMicrophoneCapture;
  }
  return std::nullopt;
}

[[nodiscard]] constexpr DeviceDataFlow endpoint_role_data_flow(
    EndpointRole role) noexcept {
  switch (role) {
    case EndpointRole::meetingSpeakerRender:
    case EndpointRole::appMicrophoneRender:
      return DeviceDataFlow::render;
    case EndpointRole::appSpeakerCapture:
    case EndpointRole::meetingMicrophoneCapture:
      return DeviceDataFlow::capture;
  }
  return DeviceDataFlow::render;
}

struct DeviceEndpoint {
  std::u16string id;
  std::uint32_t state = 0u;
  DeviceDataFlow data_flow = DeviceDataFlow::render;
  std::optional<EndpointRole> role;
  bool has_emke_role_property = false;
};

enum class DeviceCatalogOperation : std::uint8_t {
  createEnumerator,
  enumerateEndpoints,
  getCollectionCount,
  getCollectionItem,
  getEndpointId,
  getEndpointState,
  getEndpointDataFlow,
  openPropertyStore,
  readRoleProperty,
  getDefaultEndpoint,
  registerNotifications,
  unregisterNotifications,
  platformUnsupported,
  outOfMemory,
  unexpectedFailure,
};

struct DeviceCatalogError {
  DeviceCatalogOperation operation =
      DeviceCatalogOperation::unexpectedFailure;
  std::int32_t native_code = 0;
};

struct DeviceEnumeration {
  std::vector<DeviceEndpoint> endpoints;
  std::optional<DeviceCatalogError> error;
};

struct DefaultEndpointResult {
  std::u16string endpoint_id;
  std::optional<DeviceCatalogError> error;
};

class DeviceSource {
 public:
  virtual ~DeviceSource() = default;

  [[nodiscard]] virtual DeviceEnumeration enumerate() = 0;
  [[nodiscard]] virtual DefaultEndpointResult default_endpoint_id(
      DeviceDataFlow flow) = 0;
};

enum class VirtualEndpointProblem : std::uint8_t {
  none,
  missingRole,
  duplicateRole,
  wrongDataFlow,
  inactiveRole,
  invalidRole,
};

struct VirtualEndpointAssessment {
  bool ready = false;
  VirtualEndpointProblem problem = VirtualEndpointProblem::missingRole;
  std::optional<EndpointRole> role = EndpointRole::meetingSpeakerRender;
  std::size_t matching_endpoint_count = 0u;
  DeviceDataFlow expected_flow = DeviceDataFlow::render;
  DeviceDataFlow observed_flow = DeviceDataFlow::render;
};

[[nodiscard]] VirtualEndpointAssessment assess_virtual_endpoints(
    std::span<const DeviceEndpoint> endpoints) noexcept;

enum class PhysicalEndpointMode : std::uint8_t {
  fixedEndpoint,
  followDefault,
};

struct PhysicalEndpointSelection {
  PhysicalEndpointMode mode = PhysicalEndpointMode::fixedEndpoint;
  DeviceDataFlow data_flow = DeviceDataFlow::render;
  std::u16string saved_endpoint_id;
};

enum class PhysicalResolutionStatus : std::uint8_t {
  resolved,
  missing,
  wrongDataFlow,
  virtualEndpoint,
  unavailable,
  sourceError,
};

enum class EndpointDiscoveryStatus : std::uint8_t {
  ready,
  driverMissing,
  virtualEndpointsPartial,
  physicalInputMissing,
  physicalOutputMissing,
  sourceError,
};

struct EndpointDiscoveryResult {
  EndpointDiscoveryStatus status = EndpointDiscoveryStatus::sourceError;
  bool virtual_endpoints_ready = false;
  std::array<DeviceEndpoint, 4u> virtual_endpoints{};
  std::u16string default_physical_input_id;
  std::u16string default_physical_output_id;
  std::optional<DeviceCatalogError> error;
};

class DeviceCatalogSnapshot {
 public:
  explicit DeviceCatalogSnapshot(std::vector<DeviceEndpoint> endpoints);

  [[nodiscard]] std::size_t size() const noexcept;
  [[nodiscard]] DeviceEndpoint endpoint_at(std::size_t index) const;

 private:
  friend class DeviceCatalog;

  std::vector<DeviceEndpoint> endpoints_;
};

struct PhysicalEndpointResolution {
  PhysicalResolutionStatus status = PhysicalResolutionStatus::missing;
  std::shared_ptr<const DeviceEndpoint> endpoint;
  std::optional<DeviceCatalogError> error;
};

struct CatalogRefreshResult {
  bool ok = false;
  std::optional<DeviceCatalogError> error;
};

class DeviceCatalog {
 public:
  explicit DeviceCatalog(DeviceSource& source);

  [[nodiscard]] CatalogRefreshResult refresh() noexcept;
  [[nodiscard]] std::shared_ptr<const DeviceCatalogSnapshot> snapshot()
      const noexcept;
  [[nodiscard]] VirtualEndpointAssessment virtual_endpoint_assessment()
      const noexcept;
  [[nodiscard]] PhysicalEndpointResolution resolve_physical(
      const PhysicalEndpointSelection& selection) noexcept;

 private:
  [[nodiscard]] static const DeviceEndpoint* endpoint_with_id(
      const DeviceCatalogSnapshot& snapshot,
      std::u16string_view id) noexcept;

  DeviceSource& source_;
  std::mutex source_mutex_;
  std::shared_ptr<const DeviceCatalogSnapshot> snapshot_;
};

/*
 * Refreshes the catalog and returns one self-contained discovery result. A
 * default changing after the refresh is treated as missing rather than silently
 * migrating to another endpoint.
 */
[[nodiscard]] EndpointDiscoveryResult discover_endpoints(
    DeviceCatalog& catalog) noexcept;

/*
 * Creates the real MMDevice source. The caller owns COM apartment
 * initialization for the thread that creates and uses the source.
 */
[[nodiscard]] std::unique_ptr<DeviceSource> create_mm_device_source(
    DeviceCatalogError& error) noexcept;

}  // namespace emke::audio

#endif
