#include "device_notifications.hpp"

#include <algorithm>
#include <new>
#include <utility>

#if defined(_WIN32)
#include <mmdeviceapi.h>
#include <windows.h>

#include <atomic>
#include <cwchar>
#endif

namespace emke::audio {

DeviceNotificationQueue::DeviceNotificationQueue(std::size_t capacity)
    : capacity_(capacity),
      slots_(capacity == 0u
                 ? nullptr
                 : std::make_unique<DeviceNotificationEvent[]>(capacity)) {}

bool DeviceNotificationQueue::try_push(
    DeviceNotificationKind kind,
    std::u16string_view endpoint_id,
    std::optional<std::uint32_t> new_state) noexcept {
  if (producer_gate_.test_and_set(std::memory_order_acquire)) {
    dropped_contention_.fetch_add(1u, std::memory_order_relaxed);
    return false;
  }

  struct GateRelease {
    std::atomic_flag& gate;
    ~GateRelease() {
      gate.clear(std::memory_order_release);
    }
  } release{producer_gate_};

  if (endpoint_id.size() >= notificationEndpointIdCapacity) {
    dropped_overlong_id_.fetch_add(1u, std::memory_order_relaxed);
    return false;
  }

  const std::size_t write = write_index_.load(std::memory_order_relaxed);
  const std::size_t read = read_index_.load(std::memory_order_acquire);
  if (write - read >= capacity_) {
    dropped_full_.fetch_add(1u, std::memory_order_relaxed);
    return false;
  }

  DeviceNotificationEvent& slot = slots_[write % capacity_];
  slot.kind = kind;
  std::copy(endpoint_id.begin(), endpoint_id.end(), slot.endpoint_id.begin());
  slot.endpoint_id[endpoint_id.size()] = u'\0';
  slot.endpoint_id_length =
      static_cast<std::uint16_t>(endpoint_id.size());
  slot.has_new_state = new_state.has_value();
  slot.new_state = new_state.value_or(0u);
  slot.sequence = next_sequence_++;
  write_index_.store(write + 1u, std::memory_order_release);
  return true;
}

bool DeviceNotificationQueue::try_pop(
  DeviceNotificationEvent& event) noexcept {
  const std::size_t read = read_index_.load(std::memory_order_relaxed);
  const std::size_t write = write_index_.load(std::memory_order_acquire);
  if (read == write) {
    return false;
  }
  event = slots_[read % capacity_];
  read_index_.store(read + 1u, std::memory_order_release);
  return true;
}

std::uint64_t DeviceNotificationQueue::dropped_full() const noexcept {
  return dropped_full_.load(std::memory_order_relaxed);
}

std::uint64_t DeviceNotificationQueue::dropped_overlong_id() const noexcept {
  return dropped_overlong_id_.load(std::memory_order_relaxed);
}

std::uint64_t DeviceNotificationQueue::dropped_contention() const noexcept {
  return dropped_contention_.load(std::memory_order_relaxed);
}

DeviceNotificationReceiver::DeviceNotificationReceiver(
    DeviceNotificationQueue& queue) noexcept
    : queue_(queue) {}

bool DeviceNotificationReceiver::on_state_changed(
    std::u16string_view endpoint_id,
    std::uint32_t new_state) noexcept {
  return queue_.try_push(
      DeviceNotificationKind::stateChanged, endpoint_id, new_state);
}

bool DeviceNotificationReceiver::on_added(
    std::u16string_view endpoint_id) noexcept {
  return queue_.try_push(DeviceNotificationKind::added, endpoint_id);
}

bool DeviceNotificationReceiver::on_removed(
    std::u16string_view endpoint_id) noexcept {
  return queue_.try_push(DeviceNotificationKind::removed, endpoint_id);
}

bool DeviceNotificationReceiver::on_default_changed(
    std::u16string_view endpoint_id) noexcept {
  return queue_.try_push(DeviceNotificationKind::defaultChanged, endpoint_id);
}

bool DeviceNotificationReceiver::on_property_changed(
    std::u16string_view endpoint_id) noexcept {
  return queue_.try_push(DeviceNotificationKind::propertyChanged, endpoint_id);
}

struct MmDeviceNotificationRegistration::Impl {
#if defined(_WIN32)
  IMMDeviceEnumerator* enumerator = nullptr;
  IMMNotificationClient* client = nullptr;
  bool registered = false;

  ~Impl() {
    if (registered && enumerator != nullptr && client != nullptr) {
      enumerator->UnregisterEndpointNotificationCallback(client);
      registered = false;
    }
    if (client != nullptr) {
      client->Release();
      client = nullptr;
    }
    if (enumerator != nullptr) {
      enumerator->Release();
      enumerator = nullptr;
    }
  }
#endif
};

#if defined(_WIN32)

namespace {

struct BoundedEndpointId {
  std::array<char16_t, notificationEndpointIdCapacity> value{};
  std::size_t length = notificationEndpointIdCapacity;
};

BoundedEndpointId copy_bounded_endpoint_id(
    const wchar_t* endpoint_id,
    bool& valid) noexcept {
  static_assert(sizeof(wchar_t) == sizeof(char16_t));
  BoundedEndpointId copied;
  if (endpoint_id == nullptr) {
    valid = false;
    return copied;
  }
  std::size_t length = 0u;
  while (length < notificationEndpointIdCapacity &&
         endpoint_id[length] != L'\0') {
    copied.value[length] = static_cast<char16_t>(endpoint_id[length]);
    ++length;
  }
  valid = length < notificationEndpointIdCapacity;
  copied.length = valid ? length : notificationEndpointIdCapacity;
  return copied;
}

class MmNotificationClient final : public IMMNotificationClient {
 public:
  explicit MmNotificationClient(DeviceNotificationQueue& queue) noexcept
      : receiver_(queue) {}

  HRESULT STDMETHODCALLTYPE QueryInterface(
      REFIID interface_id,
      void** object) noexcept override {
    if (object == nullptr) {
      return E_POINTER;
    }
    if (IsEqualIID(interface_id, IID_IUnknown) ||
        IsEqualIID(interface_id, __uuidof(IMMNotificationClient))) {
      *object = static_cast<IMMNotificationClient*>(this);
      AddRef();
      return S_OK;
    }
    *object = nullptr;
    return E_NOINTERFACE;
  }

  ULONG STDMETHODCALLTYPE AddRef() noexcept override {
    return references_.fetch_add(1u, std::memory_order_relaxed) + 1u;
  }

  ULONG STDMETHODCALLTYPE Release() noexcept override {
    const ULONG remaining =
        references_.fetch_sub(1u, std::memory_order_acq_rel) - 1u;
    if (remaining == 0u) {
      delete this;
    }
    return remaining;
  }

  HRESULT STDMETHODCALLTYPE OnDeviceStateChanged(
      LPCWSTR endpoint_id,
      DWORD new_state) noexcept override {
    bool valid = false;
    const BoundedEndpointId id = copy_bounded_endpoint_id(endpoint_id, valid);
    if (valid) {
      static_cast<void>(receiver_.on_state_changed(
          std::u16string_view(id.value.data(), id.length), new_state));
    } else {
      static_cast<void>(receiver_.on_state_changed(
          std::u16string_view(id.value.data(), id.length),
          new_state));
    }
    return S_OK;
  }

  HRESULT STDMETHODCALLTYPE OnDeviceAdded(
      LPCWSTR endpoint_id) noexcept override {
    copy_id(endpoint_id, DeviceNotificationKind::added);
    return S_OK;
  }

  HRESULT STDMETHODCALLTYPE OnDeviceRemoved(
      LPCWSTR endpoint_id) noexcept override {
    copy_id(endpoint_id, DeviceNotificationKind::removed);
    return S_OK;
  }

  HRESULT STDMETHODCALLTYPE OnDefaultDeviceChanged(
      EDataFlow,
      ERole,
      LPCWSTR endpoint_id) noexcept override {
    copy_id(endpoint_id, DeviceNotificationKind::defaultChanged);
    return S_OK;
  }

  HRESULT STDMETHODCALLTYPE OnPropertyValueChanged(
      LPCWSTR endpoint_id,
      const PROPERTYKEY) noexcept override {
    copy_id(endpoint_id, DeviceNotificationKind::propertyChanged);
    return S_OK;
  }

 private:
  void copy_id(
      LPCWSTR endpoint_id,
      DeviceNotificationKind kind) noexcept {
    bool valid = false;
    const BoundedEndpointId id = copy_bounded_endpoint_id(endpoint_id, valid);
    const std::u16string_view id_view(id.value.data(), id.length);
    if (valid) {
      switch (kind) {
        case DeviceNotificationKind::added:
          static_cast<void>(receiver_.on_added(id_view));
          break;
        case DeviceNotificationKind::removed:
          static_cast<void>(receiver_.on_removed(id_view));
          break;
        case DeviceNotificationKind::defaultChanged:
          static_cast<void>(receiver_.on_default_changed(id_view));
          break;
        case DeviceNotificationKind::propertyChanged:
          static_cast<void>(receiver_.on_property_changed(id_view));
          break;
        case DeviceNotificationKind::stateChanged:
          break;
      }
      return;
    }

    switch (kind) {
      case DeviceNotificationKind::added:
        static_cast<void>(receiver_.on_added(id_view));
        break;
      case DeviceNotificationKind::removed:
        static_cast<void>(receiver_.on_removed(id_view));
        break;
      case DeviceNotificationKind::defaultChanged:
        static_cast<void>(receiver_.on_default_changed(id_view));
        break;
      case DeviceNotificationKind::propertyChanged:
        static_cast<void>(receiver_.on_property_changed(id_view));
        break;
      case DeviceNotificationKind::stateChanged:
        break;
    }
  }

  std::atomic<ULONG> references_ = 1u;
  DeviceNotificationReceiver receiver_;
};

}  // namespace

#endif

MmDeviceNotificationRegistration::MmDeviceNotificationRegistration(
    std::unique_ptr<Impl> impl) noexcept
    : impl_(std::move(impl)) {}

MmDeviceNotificationRegistration::~MmDeviceNotificationRegistration() =
    default;

std::unique_ptr<MmDeviceNotificationRegistration>
MmDeviceNotificationRegistration::create(
    DeviceNotificationQueue& queue,
    DeviceCatalogError& error) noexcept {
#if defined(_WIN32)
  try {
    auto impl = std::make_unique<Impl>();
    HRESULT result = CoCreateInstance(
        __uuidof(MMDeviceEnumerator),
        nullptr,
        CLSCTX_ALL,
        __uuidof(IMMDeviceEnumerator),
        reinterpret_cast<void**>(&impl->enumerator));
    if (FAILED(result)) {
      error = DeviceCatalogError{
          .operation = DeviceCatalogOperation::createEnumerator,
          .native_code = static_cast<std::int32_t>(result),
      };
      return nullptr;
    }

    impl->client = new (std::nothrow) MmNotificationClient(queue);
    if (impl->client == nullptr) {
      error = DeviceCatalogError{
          .operation = DeviceCatalogOperation::outOfMemory,
          .native_code = 0,
      };
      return nullptr;
    }

    result =
        impl->enumerator->RegisterEndpointNotificationCallback(impl->client);
    if (FAILED(result)) {
      error = DeviceCatalogError{
          .operation = DeviceCatalogOperation::registerNotifications,
          .native_code = static_cast<std::int32_t>(result),
      };
      return nullptr;
    }
    impl->registered = true;
    return std::unique_ptr<MmDeviceNotificationRegistration>(
        new MmDeviceNotificationRegistration(std::move(impl)));
  } catch (const std::bad_alloc&) {
    error = DeviceCatalogError{
        .operation = DeviceCatalogOperation::outOfMemory,
        .native_code = 0,
    };
    return nullptr;
  } catch (...) {
    error = DeviceCatalogError{
        .operation = DeviceCatalogOperation::unexpectedFailure,
        .native_code = 0,
    };
    return nullptr;
  }
#else
  static_cast<void>(queue);
  error = DeviceCatalogError{
      .operation = DeviceCatalogOperation::platformUnsupported,
      .native_code = -1,
  };
  return nullptr;
#endif
}

}  // namespace emke::audio
