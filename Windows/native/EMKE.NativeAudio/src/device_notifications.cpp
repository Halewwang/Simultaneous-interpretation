#include "device_notifications.hpp"

#include <algorithm>
#include <limits>
#include <new>
#include <utility>

#if defined(_WIN32)
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <mmdeviceapi.h>
#include <windows.h>

#include <atomic>
#endif

namespace emke::audio {

#if defined(EMKE_NATIVE_AUDIO_DEVICE_TESTS)
namespace {
std::atomic<bool> fail_next_registration_shell_allocation = false;
}

void fail_next_notification_registration_shell_allocation_for_testing()
    noexcept {
  fail_next_registration_shell_allocation.store(
      true, std::memory_order_release);
}
#endif

class DeviceNotificationState {
 public:
  DeviceNotificationState(
      std::size_t capacity,
      std::uint64_t initial_sequence)
      : capacity_(capacity),
        slots_(capacity == 0u
                   ? nullptr
                   : std::make_unique<DeviceNotificationEvent[]>(capacity)),
        next_sequence_(initial_sequence) {}

  [[nodiscard]] bool try_push(
      DeviceNotificationKind kind,
      std::optional<std::u16string_view> endpoint_id,
      std::optional<std::uint32_t> new_state) noexcept {
    if (!endpoint_id.has_value() &&
        kind != DeviceNotificationKind::defaultChanged) {
      dropped_invalid_id_.fetch_add(1u, std::memory_order_relaxed);
      return false;
    }
    if (endpoint_id.has_value() && endpoint_id->empty()) {
      dropped_invalid_id_.fetch_add(1u, std::memory_order_relaxed);
      return false;
    }
    if (endpoint_id.has_value() &&
        endpoint_id->size() >= notificationEndpointIdCapacity) {
      dropped_overlong_id_.fetch_add(1u, std::memory_order_relaxed);
      return false;
    }
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

    if (next_sequence_ ==
        (std::numeric_limits<std::uint64_t>::max)()) {
      dropped_sequence_exhausted_.fetch_add(1u, std::memory_order_relaxed);
      return false;
    }

    const std::size_t write =
        write_index_.load(std::memory_order_relaxed);
    const std::size_t read = read_index_.load(std::memory_order_acquire);
    if (write - read >= capacity_) {
      dropped_full_.fetch_add(1u, std::memory_order_relaxed);
      return false;
    }

    DeviceNotificationEvent& slot = slots_[write % capacity_];
    slot.kind = kind;
    slot.has_endpoint_id = endpoint_id.has_value();
    slot.endpoint_id_length = endpoint_id.has_value()
                                  ? static_cast<std::uint16_t>(
                                        endpoint_id->size())
                                  : 0u;
    if (endpoint_id.has_value()) {
      std::copy(
          endpoint_id->begin(),
          endpoint_id->end(),
          slot.endpoint_id.begin());
      slot.endpoint_id[endpoint_id->size()] = u'\0';
    } else {
      slot.endpoint_id[0] = u'\0';
    }
    slot.has_new_state = new_state.has_value();
    slot.new_state = new_state.value_or(0u);
    slot.sequence = next_sequence_;
    ++next_sequence_;
    write_index_.store(write + 1u, std::memory_order_release);
    return true;
  }

  [[nodiscard]] bool try_pop(DeviceNotificationEvent& event) noexcept {
    const std::size_t read = read_index_.load(std::memory_order_relaxed);
    const std::size_t write = write_index_.load(std::memory_order_acquire);
    if (read == write) {
      return false;
    }
    event = slots_[read % capacity_];
    read_index_.store(read + 1u, std::memory_order_release);
    return true;
  }

  [[nodiscard]] std::uint64_t dropped_full() const noexcept {
    return dropped_full_.load(std::memory_order_relaxed);
  }

  [[nodiscard]] std::uint64_t dropped_overlong_id() const noexcept {
    return dropped_overlong_id_.load(std::memory_order_relaxed);
  }

  [[nodiscard]] std::uint64_t dropped_invalid_id() const noexcept {
    return dropped_invalid_id_.load(std::memory_order_relaxed);
  }

  [[nodiscard]] std::uint64_t dropped_contention() const noexcept {
    return dropped_contention_.load(std::memory_order_relaxed);
  }

  [[nodiscard]] std::uint64_t dropped_sequence_exhausted() const noexcept {
    return dropped_sequence_exhausted_.load(std::memory_order_relaxed);
  }

 private:
  const std::size_t capacity_;
  std::unique_ptr<DeviceNotificationEvent[]> slots_;
  alignas(64) std::atomic<std::size_t> read_index_ = 0u;
  alignas(64) std::atomic<std::size_t> write_index_ = 0u;
  alignas(64) std::atomic_flag producer_gate_ = ATOMIC_FLAG_INIT;
  std::uint64_t next_sequence_;
  std::atomic<std::uint64_t> dropped_full_ = 0u;
  std::atomic<std::uint64_t> dropped_overlong_id_ = 0u;
  std::atomic<std::uint64_t> dropped_invalid_id_ = 0u;
  std::atomic<std::uint64_t> dropped_contention_ = 0u;
  std::atomic<std::uint64_t> dropped_sequence_exhausted_ = 0u;
};

DeviceNotificationQueue::DeviceNotificationQueue(
    std::size_t capacity,
    std::uint64_t initial_sequence)
    : state_(std::make_shared<DeviceNotificationState>(
          capacity, initial_sequence)) {}

bool DeviceNotificationQueue::try_push(
    DeviceNotificationKind kind,
    std::optional<std::u16string_view> endpoint_id,
    std::optional<std::uint32_t> new_state) noexcept {
  return state_->try_push(kind, endpoint_id, new_state);
}

bool DeviceNotificationQueue::try_pop(
    DeviceNotificationEvent& event) noexcept {
  return state_->try_pop(event);
}

std::uint64_t DeviceNotificationQueue::dropped_full() const noexcept {
  return state_->dropped_full();
}

std::uint64_t DeviceNotificationQueue::dropped_overlong_id() const noexcept {
  return state_->dropped_overlong_id();
}

std::uint64_t DeviceNotificationQueue::dropped_invalid_id() const noexcept {
  return state_->dropped_invalid_id();
}

std::uint64_t DeviceNotificationQueue::dropped_contention() const noexcept {
  return state_->dropped_contention();
}

std::uint64_t DeviceNotificationQueue::dropped_sequence_exhausted()
    const noexcept {
  return state_->dropped_sequence_exhausted();
}

DeviceNotificationReceiver::DeviceNotificationReceiver(
    DeviceNotificationQueue& queue) noexcept
    : state_(queue.state_) {}

DeviceNotificationReceiver::DeviceNotificationReceiver(
    std::shared_ptr<DeviceNotificationState> state) noexcept
    : state_(std::move(state)) {}

bool DeviceNotificationReceiver::on_state_changed(
    std::optional<std::u16string_view> endpoint_id,
    std::uint32_t new_state) noexcept {
  return state_->try_push(
      DeviceNotificationKind::stateChanged, endpoint_id, new_state);
}

bool DeviceNotificationReceiver::on_added(
    std::optional<std::u16string_view> endpoint_id) noexcept {
  return state_->try_push(DeviceNotificationKind::added, endpoint_id, std::nullopt);
}

bool DeviceNotificationReceiver::on_removed(
    std::optional<std::u16string_view> endpoint_id) noexcept {
  return state_->try_push(
      DeviceNotificationKind::removed, endpoint_id, std::nullopt);
}

bool DeviceNotificationReceiver::on_default_changed(
    std::optional<std::u16string_view> endpoint_id) noexcept {
  return state_->try_push(
      DeviceNotificationKind::defaultChanged, endpoint_id, std::nullopt);
}

bool DeviceNotificationReceiver::on_property_changed(
    std::optional<std::u16string_view> endpoint_id) noexcept {
  return state_->try_push(
      DeviceNotificationKind::propertyChanged, endpoint_id, std::nullopt);
}

DeviceNotificationPump::DeviceNotificationPump(
    DeviceNotificationQueue& queue,
    DeviceCatalog& catalog) noexcept
    : queue_(queue), catalog_(catalog) {}

DeviceNotificationPumpResult DeviceNotificationPump::drain_and_refresh()
    noexcept {
  DeviceNotificationPumpResult result;
  DeviceNotificationEvent event;
  while (queue_.try_pop(event)) {
    ++result.events_drained;
    if (last_sequence_.has_value() && event.sequence <= *last_sequence_) {
      result.sequence_valid = false;
    }
    if (!last_sequence_.has_value() || event.sequence > *last_sequence_) {
      last_sequence_ = event.sequence;
    }
  }
  if (result.events_drained == 0u) {
    return result;
  }
  result.refresh_attempted = true;
  result.refresh = catalog_.refresh();
  return result;
}

namespace {

template <
    typename RegisterCall,
    typename FailedResult,
    typename NativeCode,
    typename MarkRegistered>
std::unique_ptr<DeviceNotificationRegistrationBackend>
complete_registration_call(
    std::unique_ptr<DeviceNotificationRegistrationBackend> owner,
    RegisterCall register_call,
    FailedResult failed_result,
    NativeCode native_code,
    MarkRegistered mark_registered,
    DeviceCatalogError& error) noexcept {
  try {
    const auto result = register_call();
    if (failed_result(result)) {
      error = DeviceCatalogError{
          .operation = DeviceCatalogOperation::registerNotifications,
          .native_code = native_code(result),
      };
      return nullptr;
    }
  } catch (...) {
    error = DeviceCatalogError{
        .operation = DeviceCatalogOperation::unexpectedFailure,
        .native_code = 0,
    };
    return nullptr;
  }

  static_assert(noexcept(mark_registered()));
  mark_registered();
  return owner;
}

#if defined(EMKE_NATIVE_AUDIO_DEVICE_TESTS)
struct RegistrationReleaseCounts {
  std::uint32_t enumerator = 0u;
  std::uint32_t client = 0u;
};

class TestRegistrationCallBackend final
    : public DeviceNotificationRegistrationBackend {
 public:
  explicit TestRegistrationCallBackend(
      std::shared_ptr<RegistrationReleaseCounts> counts) noexcept
      : counts_(std::move(counts)) {}

  ~TestRegistrationCallBackend() override {
    if (!registered_) {
      ++counts_->enumerator;
      ++counts_->client;
    }
  }

  void mark_registered() noexcept {
    registered_ = true;
  }

  std::optional<DeviceCatalogError> unregister() noexcept override {
    registered_ = false;
    return std::nullopt;
  }

 private:
  std::shared_ptr<RegistrationReleaseCounts> counts_;
  bool registered_ = false;
};
#endif

}  // namespace

#if defined(EMKE_NATIVE_AUDIO_DEVICE_TESTS)
RegistrationCallTestResult
exercise_registration_call_ownership_for_testing(
    RegistrationCallTestMode mode) noexcept {
  try {
    auto counts = std::make_shared<RegistrationReleaseCounts>();
    auto concrete =
        std::make_unique<TestRegistrationCallBackend>(counts);
    TestRegistrationCallBackend* const concrete_pointer = concrete.get();
    std::unique_ptr<DeviceNotificationRegistrationBackend> owner =
        std::move(concrete);
    DeviceCatalogError error;

    auto completed = complete_registration_call(
        std::move(owner),
        [mode]() -> std::int32_t {
          if (mode == RegistrationCallTestMode::throwsException) {
            throw 7;
          }
          return mode == RegistrationCallTestMode::returnedFailure ? -42 : 0;
        },
        [](std::int32_t result) noexcept { return result != 0; },
        [](std::int32_t result) noexcept { return result; },
        [concrete_pointer]() noexcept {
          concrete_pointer->mark_registered();
        },
        error);

    const bool owner_returned = completed != nullptr;
    if (completed != nullptr) {
      static_cast<void>(completed->unregister());
      completed.reset();
    }
    return RegistrationCallTestResult{
        .owner_returned = owner_returned,
        .enumerator_releases = counts->enumerator,
        .client_releases = counts->client,
        .error = owner_returned
                     ? std::optional<DeviceCatalogError>{}
                     : std::optional<DeviceCatalogError>{error},
    };
  } catch (...) {
    return RegistrationCallTestResult{
        .error = DeviceCatalogError{
            .operation = DeviceCatalogOperation::outOfMemory,
            .native_code = 0,
        },
    };
  }
}
#endif

#if defined(_WIN32)

namespace {

enum class EndpointIdCopyStatus : std::uint8_t {
  valid,
  nullId,
  overlong,
};

struct BoundedEndpointId {
  std::array<char16_t, notificationEndpointIdCapacity> value{};
  std::size_t length = 0u;
  EndpointIdCopyStatus status = EndpointIdCopyStatus::nullId;
};

BoundedEndpointId copy_bounded_endpoint_id(
    const wchar_t* endpoint_id) noexcept {
  static_assert(sizeof(wchar_t) == sizeof(char16_t));
  BoundedEndpointId copied;
  if (endpoint_id == nullptr) {
    return copied;
  }
  while (copied.length < notificationEndpointIdCapacity &&
         endpoint_id[copied.length] != L'\0') {
    copied.value[copied.length] =
        static_cast<char16_t>(endpoint_id[copied.length]);
    ++copied.length;
  }
  copied.status =
      copied.length < notificationEndpointIdCapacity
          ? EndpointIdCopyStatus::valid
          : EndpointIdCopyStatus::overlong;
  return copied;
}

std::optional<std::u16string_view> endpoint_id_view(
    const BoundedEndpointId& id) noexcept {
  if (id.status == EndpointIdCopyStatus::nullId) {
    return std::nullopt;
  }
  return std::u16string_view(id.value.data(), id.length);
}

class MmNotificationClient final : public IMMNotificationClient {
 public:
  explicit MmNotificationClient(
      std::shared_ptr<DeviceNotificationState> state) noexcept
      : receiver_(std::move(state)) {}

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
    const BoundedEndpointId id = copy_bounded_endpoint_id(endpoint_id);
    static_cast<void>(
        receiver_.on_state_changed(endpoint_id_view(id), new_state));
    return S_OK;
  }

  HRESULT STDMETHODCALLTYPE OnDeviceAdded(
      LPCWSTR endpoint_id) noexcept override {
    const BoundedEndpointId id = copy_bounded_endpoint_id(endpoint_id);
    static_cast<void>(receiver_.on_added(endpoint_id_view(id)));
    return S_OK;
  }

  HRESULT STDMETHODCALLTYPE OnDeviceRemoved(
      LPCWSTR endpoint_id) noexcept override {
    const BoundedEndpointId id = copy_bounded_endpoint_id(endpoint_id);
    static_cast<void>(receiver_.on_removed(endpoint_id_view(id)));
    return S_OK;
  }

  HRESULT STDMETHODCALLTYPE OnDefaultDeviceChanged(
      EDataFlow,
      ERole,
      LPCWSTR endpoint_id) noexcept override {
    const BoundedEndpointId id = copy_bounded_endpoint_id(endpoint_id);
    static_cast<void>(receiver_.on_default_changed(endpoint_id_view(id)));
    return S_OK;
  }

  HRESULT STDMETHODCALLTYPE OnPropertyValueChanged(
      LPCWSTR endpoint_id,
      const PROPERTYKEY) noexcept override {
    const BoundedEndpointId id = copy_bounded_endpoint_id(endpoint_id);
    static_cast<void>(receiver_.on_property_changed(endpoint_id_view(id)));
    return S_OK;
  }

 private:
  std::atomic<ULONG> references_ = 1u;
  DeviceNotificationReceiver receiver_;
};

class MmDeviceRegistrationBackend final
    : public DeviceNotificationRegistrationBackend {
 public:
  MmDeviceRegistrationBackend(
      IMMDeviceEnumerator* enumerator,
      IMMNotificationClient* client) noexcept
      : enumerator_(enumerator), client_(client) {}

  ~MmDeviceRegistrationBackend() override {
    if (registered_) {
      return;
    }
    if (client_ != nullptr) {
      client_->Release();
      client_ = nullptr;
    }
    if (enumerator_ != nullptr) {
      enumerator_->Release();
      enumerator_ = nullptr;
    }
  }

  void mark_registered() noexcept {
    registered_ = true;
  }

  std::optional<DeviceCatalogError> unregister() noexcept override {
    if (!registered_) {
      return std::nullopt;
    }
    const HRESULT result =
        enumerator_->UnregisterEndpointNotificationCallback(client_);
    if (FAILED(result)) {
      return DeviceCatalogError{
          .operation = DeviceCatalogOperation::unregisterNotifications,
          .native_code = static_cast<std::int32_t>(result),
      };
    }
    registered_ = false;
    return std::nullopt;
  }

 private:
  IMMDeviceEnumerator* enumerator_;
  IMMNotificationClient* client_;
  bool registered_ = false;
};

class MmDeviceRegistrar final : public DeviceNotificationRegistrar {
 public:
  std::unique_ptr<DeviceNotificationRegistrationBackend>
  register_notifications(
      std::shared_ptr<DeviceNotificationState> state,
      DeviceCatalogError& error) noexcept override {
    IMMDeviceEnumerator* enumerator = nullptr;
    HRESULT result = CoCreateInstance(
        __uuidof(MMDeviceEnumerator),
        nullptr,
        CLSCTX_ALL,
        __uuidof(IMMDeviceEnumerator),
        reinterpret_cast<void**>(&enumerator));
    if (FAILED(result)) {
      if (enumerator != nullptr) {
        enumerator->Release();
      }
      error = DeviceCatalogError{
          .operation = DeviceCatalogOperation::createEnumerator,
          .native_code = static_cast<std::int32_t>(result),
      };
      return nullptr;
    }

    auto* client = new (std::nothrow) MmNotificationClient(std::move(state));
    if (client == nullptr) {
      enumerator->Release();
      error = DeviceCatalogError{
          .operation = DeviceCatalogOperation::outOfMemory,
          .native_code = 0,
      };
      return nullptr;
    }

    MmDeviceRegistrationBackend* concrete_backend_pointer = nullptr;
    std::unique_ptr<DeviceNotificationRegistrationBackend> backend;
    try {
      auto concrete_backend = std::make_unique<MmDeviceRegistrationBackend>(
          enumerator, client);
      concrete_backend_pointer = concrete_backend.get();
      backend = std::move(concrete_backend);
    } catch (const std::bad_alloc&) {
      client->Release();
      enumerator->Release();
      error = DeviceCatalogError{
          .operation = DeviceCatalogOperation::outOfMemory,
          .native_code = 0,
      };
      return nullptr;
    } catch (...) {
      client->Release();
      enumerator->Release();
      error = DeviceCatalogError{
          .operation = DeviceCatalogOperation::unexpectedFailure,
          .native_code = 0,
      };
      return nullptr;
    }

    return complete_registration_call(
        std::move(backend),
        [enumerator, client]() {
          return enumerator->RegisterEndpointNotificationCallback(client);
        },
        [](HRESULT call_result) noexcept {
          return FAILED(call_result);
        },
        [](HRESULT call_result) noexcept {
          return static_cast<std::int32_t>(call_result);
        },
        [concrete_backend_pointer]() noexcept {
          concrete_backend_pointer->mark_registered();
        },
        error);
  }
};

}  // namespace

#if defined(EMKE_NATIVE_AUDIO_DEVICE_TESTS)
bool exercise_mm_notification_client_for_testing(
    DeviceNotificationQueue& queue) noexcept {
  auto* client = new (std::nothrow) MmNotificationClient(queue.state_);
  if (client == nullptr) {
    return false;
  }

  void* queried = nullptr;
  const HRESULT query_result =
      client->QueryInterface(__uuidof(IMMNotificationClient), &queried);
  if (queried != nullptr) {
    static_cast<IMMNotificationClient*>(queried)->Release();
  }

  std::array<wchar_t, notificationEndpointIdCapacity + 1u> overlong{};
  overlong.fill(L'x');
  overlong.back() = L'\0';
  const HRESULT default_result =
      client->OnDefaultDeviceChanged(eRender, eConsole, nullptr);
  const HRESULT invalid_result = client->OnDeviceAdded(nullptr);
  const HRESULT overlong_result =
      client->OnPropertyValueChanged(overlong.data(), PROPERTYKEY{});
  const HRESULT state_result =
      client->OnDeviceStateChanged(L"{actual-mm-client}", 8u);
  client->Release();

  return query_result == S_OK && default_result == S_OK &&
         invalid_result == S_OK && overlong_result == S_OK &&
         state_result == S_OK;
}
#endif

#endif

MmDeviceNotificationRegistration::MmDeviceNotificationRegistration(
    std::unique_ptr<DeviceNotificationRegistrationBackend> backend) noexcept
    : backend_(std::move(backend)) {}

MmDeviceNotificationRegistration::~MmDeviceNotificationRegistration() {
  if (backend_ == nullptr) {
    return;
  }
  const DeviceNotificationCloseResult result = close();
  if (!result.closed) {
    static_cast<void>(backend_.release());
  }
}

std::unique_ptr<MmDeviceNotificationRegistration>
MmDeviceNotificationRegistration::create(
    DeviceNotificationQueue& queue,
    DeviceCatalogError& error) noexcept {
#if defined(_WIN32)
  MmDeviceRegistrar registrar;
  return create_with_registrar(queue, registrar, error);
#else
  static_cast<void>(queue);
  error = DeviceCatalogError{
      .operation = DeviceCatalogOperation::platformUnsupported,
      .native_code = -1,
  };
  return nullptr;
#endif
}

std::unique_ptr<MmDeviceNotificationRegistration>
MmDeviceNotificationRegistration::create_with_registrar(
    DeviceNotificationQueue& queue,
    DeviceNotificationRegistrar& registrar,
    DeviceCatalogError& error) noexcept {
#if defined(EMKE_NATIVE_AUDIO_DEVICE_TESTS)
  if (fail_next_registration_shell_allocation.exchange(
          false, std::memory_order_acq_rel)) {
    error = DeviceCatalogError{
        .operation = DeviceCatalogOperation::outOfMemory,
        .native_code = 0,
    };
    return nullptr;
  }
#endif
  auto* shell =
      new (std::nothrow) MmDeviceNotificationRegistration(nullptr);
  if (shell == nullptr) {
    error = DeviceCatalogError{
        .operation = DeviceCatalogOperation::outOfMemory,
        .native_code = 0,
    };
    return nullptr;
  }
  auto registration =
      std::unique_ptr<MmDeviceNotificationRegistration>(shell);

  auto backend = registrar.register_notifications(queue.state_, error);
  if (backend == nullptr) {
    return nullptr;
  }
  registration->backend_ = std::move(backend);
  return registration;
}

DeviceNotificationCloseResult MmDeviceNotificationRegistration::close()
    noexcept {
  if (backend_ == nullptr) {
    return {.closed = true};
  }
  const std::optional<DeviceCatalogError> error = backend_->unregister();
  if (error.has_value()) {
    return {.closed = false, .error = error};
  }
  backend_.reset();
  return {.closed = true};
}

}  // namespace emke::audio
