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
  explicit DeviceNotificationQueue(std::size_t capacity);

  DeviceNotificationQueue(const DeviceNotificationQueue&) = delete;
  DeviceNotificationQueue& operator=(const DeviceNotificationQueue&) = delete;

  [[nodiscard]] bool try_push(
      DeviceNotificationKind kind,
      std::u16string_view endpoint_id,
      std::optional<std::uint32_t> new_state = std::nullopt) noexcept;
  [[nodiscard]] bool try_pop(DeviceNotificationEvent& event) noexcept;

  [[nodiscard]] std::uint64_t dropped_full() const noexcept;
  [[nodiscard]] std::uint64_t dropped_overlong_id() const noexcept;
  [[nodiscard]] std::uint64_t dropped_contention() const noexcept;

 private:
  const std::size_t capacity_;
  std::unique_ptr<DeviceNotificationEvent[]> slots_;
  alignas(64) std::atomic<std::size_t> read_index_ = 0u;
  alignas(64) std::atomic<std::size_t> write_index_ = 0u;
  alignas(64) std::atomic_flag producer_gate_ = ATOMIC_FLAG_INIT;
  std::uint64_t next_sequence_ = 1u;
  std::atomic<std::uint64_t> dropped_full_ = 0u;
  std::atomic<std::uint64_t> dropped_overlong_id_ = 0u;
  std::atomic<std::uint64_t> dropped_contention_ = 0u;
};

class DeviceNotificationReceiver {
 public:
  explicit DeviceNotificationReceiver(DeviceNotificationQueue& queue) noexcept;

  [[nodiscard]] bool on_state_changed(
      std::u16string_view endpoint_id,
      std::uint32_t new_state) noexcept;
  [[nodiscard]] bool on_added(std::u16string_view endpoint_id) noexcept;
  [[nodiscard]] bool on_removed(std::u16string_view endpoint_id) noexcept;
  [[nodiscard]] bool on_default_changed(
      std::u16string_view endpoint_id) noexcept;
  [[nodiscard]] bool on_property_changed(
      std::u16string_view endpoint_id) noexcept;

 private:
  DeviceNotificationQueue& queue_;
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

 private:
  struct Impl;

  explicit MmDeviceNotificationRegistration(std::unique_ptr<Impl> impl) noexcept;

  std::unique_ptr<Impl> impl_;
};

}  // namespace emke::audio

#endif
