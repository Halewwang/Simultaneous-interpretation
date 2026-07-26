#ifndef EMKE_DEVICE_NOTIFICATIONS_HPP
#define EMKE_DEVICE_NOTIFICATIONS_HPP

#include "device_catalog.hpp"
#include "emke_native_audio.h"

#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <memory>
#include <optional>
#include <string_view>

namespace emke::audio {

class DeviceNotificationState;

inline constexpr std::size_t notificationEndpointIdCapacity =
    EMKE_AUDIO_ENDPOINT_ID_CAPACITY;

enum class DeviceNotificationKind : std::uint8_t {
  stateChanged,
  added,
  removed,
  defaultChanged,
  propertyChanged,
};

struct DeviceNotificationEvent {
  DeviceNotificationKind kind = DeviceNotificationKind::propertyChanged;
  std::array<char16_t, notificationEndpointIdCapacity> endpoint_id{};
  std::uint16_t endpoint_id_length = 0u;
  bool has_endpoint_id = false;
  bool has_new_state = false;
  std::uint32_t new_state = 0u;
  std::uint64_t sequence = 0u;

  [[nodiscard]] std::u16string_view endpoint_id_view() const noexcept {
    return {endpoint_id.data(), endpoint_id_length};
  }
};

/*
 * Callback producers are serialized with a single non-spinning try-gate.
 * A concurrent producer is dropped rather than waiting. The background
 * consumer is the only queue reader.
 */
class DeviceNotificationQueue {
 public:
  explicit DeviceNotificationQueue(
      std::size_t capacity,
      std::uint64_t initial_sequence = 1u);

  DeviceNotificationQueue(const DeviceNotificationQueue&) = delete;
  DeviceNotificationQueue& operator=(const DeviceNotificationQueue&) = delete;

  [[nodiscard]] bool try_push(
      DeviceNotificationKind kind,
      std::optional<std::u16string_view> endpoint_id,
      std::optional<std::uint32_t> new_state = std::nullopt) noexcept;
  [[nodiscard]] bool try_pop(DeviceNotificationEvent& event) noexcept;

  [[nodiscard]] std::uint64_t dropped_full() const noexcept;
  [[nodiscard]] std::uint64_t dropped_overlong_id() const noexcept;
  [[nodiscard]] std::uint64_t dropped_invalid_id() const noexcept;
  [[nodiscard]] std::uint64_t dropped_contention() const noexcept;
  [[nodiscard]] std::uint64_t dropped_sequence_exhausted() const noexcept;

 private:
  friend class DeviceNotificationReceiver;
  friend class MmDeviceNotificationRegistration;
#if defined(_WIN32) && defined(EMKE_NATIVE_AUDIO_DEVICE_TESTS)
  friend bool exercise_mm_notification_client_for_testing(
      DeviceNotificationQueue& queue) noexcept;
#endif

  std::shared_ptr<DeviceNotificationState> state_;
};

class DeviceNotificationReceiver {
 public:
  explicit DeviceNotificationReceiver(DeviceNotificationQueue& queue) noexcept;
  explicit DeviceNotificationReceiver(
      std::shared_ptr<DeviceNotificationState> state) noexcept;

  [[nodiscard]] bool on_state_changed(
      std::optional<std::u16string_view> endpoint_id,
      std::uint32_t new_state) noexcept;
  [[nodiscard]] bool on_added(
      std::optional<std::u16string_view> endpoint_id) noexcept;
  [[nodiscard]] bool on_removed(
      std::optional<std::u16string_view> endpoint_id) noexcept;
  [[nodiscard]] bool on_default_changed(
      std::optional<std::u16string_view> endpoint_id) noexcept;
  [[nodiscard]] bool on_property_changed(
      std::optional<std::u16string_view> endpoint_id) noexcept;

 private:
  std::shared_ptr<DeviceNotificationState> state_;
};

struct DeviceNotificationPumpResult {
  std::size_t events_drained = 0u;
  bool sequence_valid = true;
  bool refresh_attempted = false;
  CatalogRefreshResult refresh;
};

class DeviceNotificationPump {
 public:
  DeviceNotificationPump(
      DeviceNotificationQueue& queue,
      DeviceCatalog& catalog) noexcept;

  [[nodiscard]] DeviceNotificationPumpResult drain_and_refresh() noexcept;

 private:
  DeviceNotificationQueue& queue_;
  DeviceCatalog& catalog_;
  std::optional<std::uint64_t> last_sequence_;
};

class DeviceNotificationRegistrationBackend {
 public:
  virtual ~DeviceNotificationRegistrationBackend() = default;

  [[nodiscard]] virtual std::optional<DeviceCatalogError> unregister()
      noexcept = 0;
};

class DeviceNotificationRegistrar {
 public:
  virtual ~DeviceNotificationRegistrar() = default;

  [[nodiscard]] virtual
      std::unique_ptr<DeviceNotificationRegistrationBackend>
      register_notifications(
          std::shared_ptr<DeviceNotificationState> state,
          DeviceCatalogError& error) noexcept = 0;
};

struct DeviceNotificationCloseResult {
  bool closed = false;
  std::optional<DeviceCatalogError> error;
};

/*
 * Successful creation owns an IMMDeviceEnumerator registration and retains the
 * notification client until UnregisterEndpointNotificationCallback completes.
 * Callers own COM apartment initialization.
 */
class MmDeviceNotificationRegistration {
 public:
  ~MmDeviceNotificationRegistration();

  MmDeviceNotificationRegistration(
      const MmDeviceNotificationRegistration&) = delete;
  MmDeviceNotificationRegistration& operator=(
      const MmDeviceNotificationRegistration&) = delete;

  [[nodiscard]] static std::unique_ptr<MmDeviceNotificationRegistration> create(
      DeviceNotificationQueue& queue,
      DeviceCatalogError& error) noexcept;
  [[nodiscard]] static std::unique_ptr<MmDeviceNotificationRegistration>
  create_with_registrar(
      DeviceNotificationQueue& queue,
      DeviceNotificationRegistrar& registrar,
      DeviceCatalogError& error) noexcept;

  [[nodiscard]] DeviceNotificationCloseResult close() noexcept;

 private:
  explicit MmDeviceNotificationRegistration(
      std::unique_ptr<DeviceNotificationRegistrationBackend> backend) noexcept;

  std::unique_ptr<DeviceNotificationRegistrationBackend> backend_;
};

#if defined(_WIN32) && defined(EMKE_NATIVE_AUDIO_DEVICE_TESTS)
[[nodiscard]] bool exercise_mm_notification_client_for_testing(
    DeviceNotificationQueue& queue) noexcept;
#endif

#if defined(EMKE_NATIVE_AUDIO_DEVICE_TESTS)
void fail_next_notification_registration_shell_allocation_for_testing()
    noexcept;
#endif

}  // namespace emke::audio

#endif
