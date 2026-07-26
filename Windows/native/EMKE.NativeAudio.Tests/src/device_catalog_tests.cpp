#include "device_catalog.hpp"
#include "device_notifications.hpp"

#include <array>
#include <atomic>
#include <cstdint>
#include <iostream>
#include <limits>
#include <memory>
#include <optional>
#include <string>
#include <string_view>
#include <thread>
#include <utility>
#include <vector>

namespace {

class TestContext {
 public:
  void expect(bool condition, std::string_view expression, int line) {
    if (condition) {
      return;
    }
    ++failures_;
    std::cerr << line << ": expected " << expression << '\n';
  }

  [[nodiscard]] int failures() const {
    return failures_;
  }

 private:
  int failures_ = 0;
};

#define EXPECT(context, expression) \
  (context).expect((expression), #expression, __LINE__)

using emke::audio::DeviceDataFlow;
using emke::audio::DeviceEndpoint;
using emke::audio::EndpointRole;

DeviceEndpoint endpoint(
    std::u16string id,
    DeviceDataFlow flow,
    std::optional<EndpointRole> role = std::nullopt,
    std::uint32_t state = emke::audio::deviceStateActive) {
  return DeviceEndpoint{
      .id = std::move(id),
      .state = state,
      .data_flow = flow,
      .role = role,
      .has_emke_role_property = role.has_value(),
  };
}

std::vector<DeviceEndpoint> complete_virtual_endpoints() {
  return {
      endpoint(
          u"{opaque}.meeting.render",
          DeviceDataFlow::render,
          EndpointRole::meetingSpeakerRender),
      endpoint(
          u"{opaque}.app.capture",
          DeviceDataFlow::capture,
          EndpointRole::appSpeakerCapture),
      endpoint(
          u"{opaque}.app.render",
          DeviceDataFlow::render,
          EndpointRole::appMicrophoneRender),
      endpoint(
          u"{opaque}.meeting.capture",
          DeviceDataFlow::capture,
          EndpointRole::meetingMicrophoneCapture),
  };
}

class FakeDeviceSource final : public emke::audio::DeviceSource {
 public:
  emke::audio::DeviceEnumeration enumerate() override {
    ++enumeration_count;
    if (fail_enumeration) {
      return emke::audio::DeviceEnumeration{
          .error = emke::audio::DeviceCatalogError{
              .operation =
                  emke::audio::DeviceCatalogOperation::enumerateEndpoints,
              .native_code = -9,
          },
      };
    }
    return emke::audio::DeviceEnumeration{.endpoints = next_endpoints};
  }

  emke::audio::DefaultEndpointResult default_endpoint_id(
      DeviceDataFlow flow) override {
    ++default_request_count;
    const std::optional<std::u16string>& value =
        flow == DeviceDataFlow::render ? default_render : default_capture;
    if (!value.has_value()) {
      return emke::audio::DefaultEndpointResult{
          .error = emke::audio::DeviceCatalogError{
              .operation =
                  emke::audio::DeviceCatalogOperation::getDefaultEndpoint,
              .native_code = -1,
          },
      };
    }
    return emke::audio::DefaultEndpointResult{.endpoint_id = *value};
  }

  std::vector<DeviceEndpoint> next_endpoints;
  std::optional<std::u16string> default_render;
  std::optional<std::u16string> default_capture;
  bool fail_enumeration = false;
  int enumeration_count = 0;
  int default_request_count = 0;
};

struct RegistrationProbe {
  int unregister_calls = 0;
  int release_calls = 0;
  int failures_before_success = 0;
};

class FakeRegistrationBackend final
    : public emke::audio::DeviceNotificationRegistrationBackend {
 public:
  FakeRegistrationBackend(
      std::shared_ptr<emke::audio::DeviceNotificationState> state,
      std::shared_ptr<RegistrationProbe> probe)
      : receiver_(std::move(state)), probe_(std::move(probe)) {}

  ~FakeRegistrationBackend() override {
    ++probe_->release_calls;
  }

  std::optional<emke::audio::DeviceCatalogError> unregister() noexcept override {
    ++probe_->unregister_calls;
    if (probe_->unregister_calls <= probe_->failures_before_success) {
      return emke::audio::DeviceCatalogError{
          .operation =
              emke::audio::DeviceCatalogOperation::unregisterNotifications,
          .native_code = -17,
      };
    }
    return std::nullopt;
  }

  [[nodiscard]] bool emit_added() noexcept {
    return receiver_.on_added(u"{registered-callback}");
  }

 private:
  emke::audio::DeviceNotificationReceiver receiver_;
  std::shared_ptr<RegistrationProbe> probe_;
};

class FakeNotificationRegistrar final
    : public emke::audio::DeviceNotificationRegistrar {
 public:
  explicit FakeNotificationRegistrar(std::shared_ptr<RegistrationProbe> probe)
      : probe_(std::move(probe)) {}

  std::unique_ptr<emke::audio::DeviceNotificationRegistrationBackend>
  register_notifications(
      std::shared_ptr<emke::audio::DeviceNotificationState> state,
      emke::audio::DeviceCatalogError&) noexcept override {
    ++register_calls;
    try {
      auto backend =
          std::make_unique<FakeRegistrationBackend>(state, probe_);
      last_backend = backend.get();
      registered_state = state;
      return backend;
    } catch (...) {
      return nullptr;
    }
  }

  FakeRegistrationBackend* last_backend = nullptr;
  std::weak_ptr<emke::audio::DeviceNotificationState> registered_state;
  int register_calls = 0;

 private:
  std::shared_ptr<RegistrationProbe> probe_;
};

void test_four_distinct_virtual_roles_are_required(TestContext& context) {
  const auto endpoints = complete_virtual_endpoints();
  const auto assessment = emke::audio::assess_virtual_endpoints(endpoints);

  EXPECT(context, assessment.ready);
  EXPECT(
      context,
      assessment.problem == emke::audio::VirtualEndpointProblem::none);
}

void test_duplicate_role_blocks_readiness(TestContext& context) {
  auto endpoints = complete_virtual_endpoints();
  endpoints.push_back(endpoint(
      u"{second}.meeting.render",
      DeviceDataFlow::render,
      EndpointRole::meetingSpeakerRender));

  const auto assessment = emke::audio::assess_virtual_endpoints(endpoints);
  EXPECT(context, !assessment.ready);
  EXPECT(
      context,
      assessment.problem ==
          emke::audio::VirtualEndpointProblem::duplicateRole);
  EXPECT(
      context,
      assessment.role == EndpointRole::meetingSpeakerRender);
  EXPECT(context, assessment.matching_endpoint_count == 2u);
}

void test_missing_role_blocks_readiness(TestContext& context) {
  auto endpoints = complete_virtual_endpoints();
  endpoints.erase(endpoints.begin() + 2);

  const auto assessment = emke::audio::assess_virtual_endpoints(endpoints);
  EXPECT(context, !assessment.ready);
  EXPECT(
      context,
      assessment.problem == emke::audio::VirtualEndpointProblem::missingRole);
  EXPECT(context, assessment.role == EndpointRole::appMicrophoneRender);
  EXPECT(context, assessment.matching_endpoint_count == 0u);
}

void test_wrong_role_flow_blocks_readiness(TestContext& context) {
  auto endpoints = complete_virtual_endpoints();
  endpoints[3].data_flow = DeviceDataFlow::render;

  const auto assessment = emke::audio::assess_virtual_endpoints(endpoints);
  EXPECT(context, !assessment.ready);
  EXPECT(
      context,
      assessment.problem ==
          emke::audio::VirtualEndpointProblem::wrongDataFlow);
  EXPECT(context, assessment.role == EndpointRole::meetingMicrophoneCapture);
  EXPECT(context, assessment.expected_flow == DeviceDataFlow::capture);
  EXPECT(context, assessment.observed_flow == DeviceDataFlow::render);
}

void test_inactive_and_unknown_virtual_roles_block_readiness(
    TestContext& context) {
  auto inactive = complete_virtual_endpoints();
  inactive[1].state = 2u;
  auto assessment = emke::audio::assess_virtual_endpoints(inactive);
  EXPECT(context, !assessment.ready);
  EXPECT(
      context,
      assessment.problem ==
          emke::audio::VirtualEndpointProblem::inactiveRole);
  EXPECT(context, assessment.role == EndpointRole::appSpeakerCapture);

  auto invalid = complete_virtual_endpoints();
  auto invalid_role = endpoint(u"{unknown-role}", DeviceDataFlow::render);
  invalid_role.has_emke_role_property = true;
  invalid.push_back(std::move(invalid_role));
  assessment = emke::audio::assess_virtual_endpoints(invalid);
  EXPECT(context, !assessment.ready);
  EXPECT(
      context,
      assessment.problem ==
          emke::audio::VirtualEndpointProblem::invalidRole);
}

void test_role_strings_are_stable_and_not_display_names(TestContext& context) {
  constexpr std::array expected = {
      std::string_view{"emke.meeting-speaker.render"},
      std::string_view{"emke.app-speaker.capture"},
      std::string_view{"emke.app-microphone.render"},
      std::string_view{"emke.meeting-microphone.capture"},
  };
  constexpr std::array roles = {
      EndpointRole::meetingSpeakerRender,
      EndpointRole::appSpeakerCapture,
      EndpointRole::appMicrophoneRender,
      EndpointRole::meetingMicrophoneCapture,
  };

  for (std::size_t index = 0u; index < roles.size(); ++index) {
    EXPECT(context, emke::audio::endpoint_role_string(roles[index]) ==
                        expected[index]);
    EXPECT(context, emke::audio::parse_endpoint_role(expected[index]) ==
                        roles[index]);
  }
  EXPECT(
      context,
      !emke::audio::parse_endpoint_role("EMKE Virtual Microphone").has_value());
}

void test_physical_endpoint_id_resolves_after_reenumeration(
    TestContext& context) {
  FakeDeviceSource source;
  emke::audio::DeviceCatalog catalog(source);
  source.next_endpoints = {
      endpoint(u"{physical-render-a}", DeviceDataFlow::render),
  };
  EXPECT(context, catalog.refresh().ok);

  const emke::audio::PhysicalEndpointSelection selection{
      .mode = emke::audio::PhysicalEndpointMode::fixedEndpoint,
      .data_flow = DeviceDataFlow::render,
      .saved_endpoint_id = u"{physical-render-b}:opaque/full/id",
  };
  EXPECT(
      context,
      catalog.resolve_physical(selection).status ==
          emke::audio::PhysicalResolutionStatus::missing);

  source.next_endpoints = {
      endpoint(u"{physical-render-b}:opaque/full/id", DeviceDataFlow::render),
  };
  EXPECT(context, catalog.refresh().ok);
  const auto resolution = catalog.resolve_physical(selection);
  EXPECT(
      context,
      resolution.status ==
          emke::audio::PhysicalResolutionStatus::resolved);
  EXPECT(context, resolution.endpoint != nullptr);
  EXPECT(
      context,
      resolution.endpoint != nullptr &&
          resolution.endpoint->id == u"{physical-render-b}:opaque/full/id");
}

void test_missing_saved_physical_endpoint_does_not_fallback(
    TestContext& context) {
  FakeDeviceSource source;
  source.next_endpoints = {
      endpoint(u"{current-default}", DeviceDataFlow::render),
  };
  source.default_render = u"{current-default}";
  emke::audio::DeviceCatalog catalog(source);
  EXPECT(context, catalog.refresh().ok);

  const emke::audio::PhysicalEndpointSelection selection{
      .mode = emke::audio::PhysicalEndpointMode::fixedEndpoint,
      .data_flow = DeviceDataFlow::render,
      .saved_endpoint_id = u"{saved-but-missing}",
  };
  const auto resolution = catalog.resolve_physical(selection);
  EXPECT(
      context,
      resolution.status == emke::audio::PhysicalResolutionStatus::missing);
  EXPECT(context, resolution.endpoint == nullptr);
  EXPECT(context, source.default_request_count == 0);
}

void test_inactive_physical_endpoint_is_unavailable_without_fallback(
    TestContext& context) {
  FakeDeviceSource source;
  source.next_endpoints = {
      endpoint(
          u"{disabled-fixed}", DeviceDataFlow::render, std::nullopt, 2u),
      endpoint(
          u"{inactive-default}", DeviceDataFlow::capture, std::nullopt, 8u),
      endpoint(u"{other-active}", DeviceDataFlow::render),
  };
  source.default_render = u"{other-active}";
  source.default_capture = u"{inactive-default}";
  emke::audio::DeviceCatalog catalog(source);
  EXPECT(context, catalog.refresh().ok);

  const emke::audio::PhysicalEndpointSelection fixed{
      .mode = emke::audio::PhysicalEndpointMode::fixedEndpoint,
      .data_flow = DeviceDataFlow::render,
      .saved_endpoint_id = u"{disabled-fixed}",
  };
  auto resolution = catalog.resolve_physical(fixed);
  EXPECT(
      context,
      resolution.status ==
          emke::audio::PhysicalResolutionStatus::unavailable);
  EXPECT(context, resolution.endpoint == nullptr);
  EXPECT(context, source.default_request_count == 0);

  const emke::audio::PhysicalEndpointSelection follow{
      .mode = emke::audio::PhysicalEndpointMode::followDefault,
      .data_flow = DeviceDataFlow::capture,
  };
  resolution = catalog.resolve_physical(follow);
  EXPECT(
      context,
      resolution.status ==
          emke::audio::PhysicalResolutionStatus::unavailable);
  EXPECT(context, resolution.endpoint == nullptr);
}

void test_follow_default_permits_migration_but_rejects_virtual(
    TestContext& context) {
  FakeDeviceSource source;
  source.next_endpoints = {
      endpoint(u"{physical-one}", DeviceDataFlow::capture),
      endpoint(u"{physical-two}", DeviceDataFlow::capture),
      endpoint(
          u"{virtual}",
          DeviceDataFlow::capture,
          EndpointRole::appSpeakerCapture),
  };
  source.default_capture = u"{physical-one}";
  emke::audio::DeviceCatalog catalog(source);
  EXPECT(context, catalog.refresh().ok);

  const emke::audio::PhysicalEndpointSelection selection{
      .mode = emke::audio::PhysicalEndpointMode::followDefault,
      .data_flow = DeviceDataFlow::capture,
  };
  auto resolution = catalog.resolve_physical(selection);
  EXPECT(
      context,
      resolution.status ==
          emke::audio::PhysicalResolutionStatus::resolved);
  EXPECT(
      context,
      resolution.endpoint != nullptr &&
          resolution.endpoint->id == u"{physical-one}");

  source.default_capture = u"{physical-two}";
  resolution = catalog.resolve_physical(selection);
  EXPECT(
      context,
      resolution.status ==
          emke::audio::PhysicalResolutionStatus::resolved);
  EXPECT(
      context,
      resolution.endpoint != nullptr &&
          resolution.endpoint->id == u"{physical-two}");

  source.default_capture = u"{virtual}";
  resolution = catalog.resolve_physical(selection);
  EXPECT(
      context,
      resolution.status ==
          emke::audio::PhysicalResolutionStatus::virtualEndpoint);
  EXPECT(context, resolution.endpoint == nullptr);

  auto unknown_driver_role =
      endpoint(u"{unknown-driver-role}", DeviceDataFlow::capture);
  unknown_driver_role.has_emke_role_property = true;
  source.next_endpoints.push_back(std::move(unknown_driver_role));
  source.default_capture = u"{unknown-driver-role}";
  resolution = catalog.resolve_physical(selection);
  EXPECT(
      context,
      resolution.status ==
          emke::audio::PhysicalResolutionStatus::missing);
  EXPECT(context, catalog.refresh().ok);
  resolution = catalog.resolve_physical(selection);
  EXPECT(
      context,
      resolution.status ==
          emke::audio::PhysicalResolutionStatus::virtualEndpoint);
}

void test_catalog_snapshots_and_resolutions_survive_refresh(
    TestContext& context) {
  FakeDeviceSource source;
  source.next_endpoints = {
      endpoint(u"{first}", DeviceDataFlow::render),
  };
  emke::audio::DeviceCatalog catalog(source);
  EXPECT(context, catalog.refresh().ok);
  const auto old_snapshot = catalog.snapshot();
  const emke::audio::PhysicalEndpointSelection selection{
      .mode = emke::audio::PhysicalEndpointMode::fixedEndpoint,
      .data_flow = DeviceDataFlow::render,
      .saved_endpoint_id = u"{first}",
  };
  const auto old_resolution = catalog.resolve_physical(selection);
  EXPECT(context, old_resolution.endpoint != nullptr);

  source.next_endpoints = {
      endpoint(u"{second}", DeviceDataFlow::render),
  };
  EXPECT(context, catalog.refresh().ok);
  EXPECT(context, catalog.snapshot() != old_snapshot);
  EXPECT(context, old_snapshot->size() == 1u);
  EXPECT(context, old_snapshot->endpoint_at(0u).id == u"{first}");
  EXPECT(
      context,
      old_resolution.endpoint != nullptr &&
          old_resolution.endpoint->id == u"{first}");
  EXPECT(
      context,
      catalog.resolve_physical(selection).status ==
          emke::audio::PhysicalResolutionStatus::missing);
}

class ConcurrentDeviceSource final : public emke::audio::DeviceSource {
 public:
  emke::audio::DeviceEnumeration enumerate() override {
    const std::uint64_t generation =
        next_generation_.fetch_add(1u, std::memory_order_relaxed);
    return emke::audio::DeviceEnumeration{
        .endpoints = {
            endpoint(
                generation % 2u == 0u ? u"{even}" : u"{odd}",
                DeviceDataFlow::render),
        },
    };
  }

  emke::audio::DefaultEndpointResult default_endpoint_id(
      DeviceDataFlow) override {
    return {.endpoint_id = u"{even}"};
  }

 private:
  std::atomic<std::uint64_t> next_generation_ = 0u;
};

void test_catalog_snapshot_publish_supports_concurrent_readers(
    TestContext& context) {
  ConcurrentDeviceSource source;
  emke::audio::DeviceCatalog catalog(source);
  EXPECT(context, catalog.refresh().ok);
  std::atomic<bool> failed = false;
  std::thread writer([&] {
    for (std::size_t iteration = 0u; iteration < 1'000u; ++iteration) {
      if (!catalog.refresh().ok) {
        failed.store(true, std::memory_order_release);
      }
    }
  });
  std::array<std::thread, 3u> readers;
  for (std::thread& reader : readers) {
    reader = std::thread([&] {
      for (std::size_t iteration = 0u; iteration < 2'000u; ++iteration) {
        const auto snapshot = catalog.snapshot();
        if (snapshot == nullptr || snapshot->size() != 1u) {
          failed.store(true, std::memory_order_release);
          return;
        }
        const auto id = snapshot->endpoint_at(0u).id;
        if (id != u"{even}" && id != u"{odd}") {
          failed.store(true, std::memory_order_release);
          return;
        }
      }
    });
  }
  writer.join();
  for (std::thread& reader : readers) {
    reader.join();
  }
  EXPECT(context, !failed.load(std::memory_order_acquire));
}

void test_notification_callback_copies_without_enumeration(
    TestContext& context) {
  FakeDeviceSource source;
  source.next_endpoints = complete_virtual_endpoints();
  emke::audio::DeviceCatalog catalog(source);
  EXPECT(context, catalog.refresh().ok);
  const int enumeration_count_before_callback = source.enumeration_count;

  emke::audio::DeviceNotificationQueue queue(4u);
  emke::audio::DeviceNotificationReceiver receiver(queue);
  EXPECT(
      context,
      receiver.on_state_changed(u"{opaque-notification-id}", 8u));
  EXPECT(context, source.enumeration_count == enumeration_count_before_callback);

  emke::audio::DeviceNotificationEvent event;
  EXPECT(context, queue.try_pop(event));
  EXPECT(
      context,
      event.kind == emke::audio::DeviceNotificationKind::stateChanged);
  EXPECT(context, event.endpoint_id_view() == u"{opaque-notification-id}");
  EXPECT(context, event.has_new_state);
  EXPECT(context, event.new_state == 8u);
  EXPECT(context, event.sequence == 1u);
}

void test_notification_pump_coalesces_and_preserves_snapshot_on_failure(
    TestContext& context) {
  FakeDeviceSource source;
  source.next_endpoints = {
      endpoint(u"{initial}", DeviceDataFlow::render),
  };
  emke::audio::DeviceCatalog catalog(source);
  EXPECT(context, catalog.refresh().ok);
  const int initial_enumeration_count = source.enumeration_count;
  const auto valid_snapshot = catalog.snapshot();

  emke::audio::DeviceNotificationQueue queue(8u);
  emke::audio::DeviceNotificationReceiver receiver(queue);
  emke::audio::DeviceNotificationPump pump(queue, catalog);
  EXPECT(context, receiver.on_added(u"{one}"));
  EXPECT(context, receiver.on_removed(u"{two}"));
  EXPECT(
      context,
      receiver.on_state_changed(u"{three}", emke::audio::deviceStateActive));
  EXPECT(context, source.enumeration_count == initial_enumeration_count);

  source.next_endpoints = {
      endpoint(u"{after-burst}", DeviceDataFlow::render),
  };
  auto result = pump.drain_and_refresh();
  EXPECT(context, result.events_drained == 3u);
  EXPECT(context, result.sequence_valid);
  EXPECT(context, result.refresh_attempted);
  EXPECT(context, result.refresh.ok);
  EXPECT(context, source.enumeration_count == initial_enumeration_count + 1);
  EXPECT(
      context,
      catalog.snapshot()->endpoint_at(0u).id == u"{after-burst}");

  result = pump.drain_and_refresh();
  EXPECT(context, result.events_drained == 0u);
  EXPECT(context, !result.refresh_attempted);
  EXPECT(context, source.enumeration_count == initial_enumeration_count + 1);

  const auto snapshot_before_failure = catalog.snapshot();
  source.fail_enumeration = true;
  EXPECT(context, receiver.on_property_changed(u"{failure-trigger}"));
  result = pump.drain_and_refresh();
  EXPECT(context, result.events_drained == 1u);
  EXPECT(context, result.sequence_valid);
  EXPECT(context, result.refresh_attempted);
  EXPECT(context, !result.refresh.ok);
  EXPECT(context, result.refresh.error.has_value());
  EXPECT(context, catalog.snapshot() == snapshot_before_failure);
}

void test_null_default_id_is_an_event_and_invalid_ids_are_distinct(
    TestContext& context) {
  emke::audio::DeviceNotificationQueue queue(4u);
  emke::audio::DeviceNotificationReceiver receiver(queue);

  EXPECT(context, receiver.on_default_changed(std::nullopt));
  EXPECT(context, !receiver.on_added(std::nullopt));
  EXPECT(context, queue.dropped_invalid_id() == 1u);
  EXPECT(context, queue.dropped_overlong_id() == 0u);

  emke::audio::DeviceNotificationEvent event;
  EXPECT(context, queue.try_pop(event));
  EXPECT(
      context,
      event.kind == emke::audio::DeviceNotificationKind::defaultChanged);
  EXPECT(context, !event.has_endpoint_id);
  EXPECT(context, event.endpoint_id_view().empty());
  EXPECT(context, event.sequence == 1u);
  EXPECT(context, !queue.try_pop(event));
}

#if defined(_WIN32) && defined(EMKE_NATIVE_AUDIO_DEVICE_TESTS)
void test_actual_mm_notification_client_translates_callback_arguments(
    TestContext& context) {
  emke::audio::DeviceNotificationQueue queue(4u);
  EXPECT(
      context,
      emke::audio::exercise_mm_notification_client_for_testing(queue));
  EXPECT(context, queue.dropped_invalid_id() == 1u);
  EXPECT(context, queue.dropped_overlong_id() == 1u);

  emke::audio::DeviceNotificationEvent event;
  EXPECT(context, queue.try_pop(event));
  EXPECT(
      context,
      event.kind == emke::audio::DeviceNotificationKind::defaultChanged);
  EXPECT(context, !event.has_endpoint_id);
  EXPECT(context, queue.try_pop(event));
  EXPECT(
      context,
      event.kind == emke::audio::DeviceNotificationKind::stateChanged);
  EXPECT(context, event.has_endpoint_id);
  EXPECT(context, event.endpoint_id_view() == u"{actual-mm-client}");
  EXPECT(context, event.has_new_state);
  EXPECT(context, event.new_state == 8u);
  EXPECT(context, !queue.try_pop(event));
}
#endif

void test_notification_queue_is_bounded_and_ordered(TestContext& context) {
  emke::audio::DeviceNotificationQueue queue(2u);
  emke::audio::DeviceNotificationReceiver receiver(queue);

  EXPECT(context, receiver.on_added(u"{one}"));
  EXPECT(context, receiver.on_removed(u"{two}"));
  EXPECT(context, !receiver.on_property_changed(u"{three}"));
  EXPECT(context, queue.dropped_full() == 1u);

  emke::audio::DeviceNotificationEvent first;
  emke::audio::DeviceNotificationEvent second;
  EXPECT(context, queue.try_pop(first));
  EXPECT(context, queue.try_pop(second));
  EXPECT(context, !queue.try_pop(first));
  EXPECT(
      context,
      first.kind == emke::audio::DeviceNotificationKind::added);
  EXPECT(context, first.has_endpoint_id);
  EXPECT(context, first.endpoint_id_view() == u"{one}");
  EXPECT(context, !first.has_new_state);
  EXPECT(
      context,
      second.kind == emke::audio::DeviceNotificationKind::removed);
  EXPECT(context, second.endpoint_id_view() == u"{two}");
  EXPECT(context, first.sequence < second.sequence);
}

void test_notification_queue_drops_overlong_ids(TestContext& context) {
  emke::audio::DeviceNotificationQueue queue(2u);
  emke::audio::DeviceNotificationReceiver receiver(queue);
  const std::u16string overlong(
      emke::audio::notificationEndpointIdCapacity,
      u'x');

  EXPECT(context, !receiver.on_added(overlong));
  EXPECT(context, queue.dropped_overlong_id() == 1u);
  EXPECT(context, queue.dropped_full() == 0u);
  emke::audio::DeviceNotificationEvent event;
  EXPECT(context, !queue.try_pop(event));
}

void test_notification_sequence_stops_before_wrap(TestContext& context) {
  constexpr std::uint64_t last_usable_sequence =
      (std::numeric_limits<std::uint64_t>::max)() - 1u;
  emke::audio::DeviceNotificationQueue queue(2u, last_usable_sequence);
  emke::audio::DeviceNotificationReceiver receiver(queue);

  EXPECT(context, receiver.on_added(u"{last-sequence}"));
  EXPECT(context, !receiver.on_added(u"{must-not-wrap}"));
  EXPECT(context, queue.dropped_sequence_exhausted() == 1u);

  emke::audio::DeviceNotificationEvent event;
  EXPECT(context, queue.try_pop(event));
  EXPECT(context, event.sequence == last_usable_sequence);
  EXPECT(context, !queue.try_pop(event));
}

void test_registration_close_retries_and_queue_state_outlives_wrapper(
    TestContext& context) {
  auto probe = std::make_shared<RegistrationProbe>();
  probe->failures_before_success = 1;
  FakeNotificationRegistrar registrar(probe);
  emke::audio::DeviceCatalogError error;
  std::unique_ptr<emke::audio::MmDeviceNotificationRegistration> registration;

  {
    emke::audio::DeviceNotificationQueue queue(2u);
    registration =
        emke::audio::MmDeviceNotificationRegistration::create_with_registrar(
            queue, registrar, error);
    EXPECT(context, registration != nullptr);
    EXPECT(context, !registrar.registered_state.expired());
  }

  EXPECT(context, !registrar.registered_state.expired());
  EXPECT(context, registrar.last_backend != nullptr);
  EXPECT(
      context,
      registrar.last_backend != nullptr &&
          registrar.last_backend->emit_added());

  auto close = registration->close();
  EXPECT(context, !close.closed);
  EXPECT(context, close.error.has_value());
  EXPECT(context, probe->unregister_calls == 1);
  EXPECT(context, probe->release_calls == 0);
  EXPECT(context, !registrar.registered_state.expired());

  close = registration->close();
  EXPECT(context, close.closed);
  EXPECT(context, !close.error.has_value());
  EXPECT(context, probe->unregister_calls == 2);
  EXPECT(context, probe->release_calls == 1);
  EXPECT(context, registrar.registered_state.expired());
}

#if defined(EMKE_NATIVE_AUDIO_DEVICE_TESTS)
void test_registration_shell_oom_prevents_registrar_side_effect(
    TestContext& context) {
  auto probe = std::make_shared<RegistrationProbe>();
  FakeNotificationRegistrar registrar(probe);
  emke::audio::DeviceNotificationQueue queue(2u);
  emke::audio::DeviceCatalogError error;

  emke::audio::
      fail_next_notification_registration_shell_allocation_for_testing();
  auto registration =
      emke::audio::MmDeviceNotificationRegistration::create_with_registrar(
          queue, registrar, error);
  EXPECT(context, registration == nullptr);
  EXPECT(
      context,
      error.operation == emke::audio::DeviceCatalogOperation::outOfMemory);
  EXPECT(context, registrar.register_calls == 0);
  EXPECT(context, probe->unregister_calls == 0);
  EXPECT(context, probe->release_calls == 0);

  registration =
      emke::audio::MmDeviceNotificationRegistration::create_with_registrar(
          queue, registrar, error);
  EXPECT(context, registration != nullptr);
  EXPECT(context, registrar.register_calls == 1);
  const auto close = registration->close();
  EXPECT(context, close.closed);
  EXPECT(context, probe->unregister_calls == 1);
  EXPECT(context, probe->release_calls == 1);
}
#endif

void test_registration_destructor_retains_state_after_unregister_failure(
    TestContext& context) {
  auto probe = std::make_shared<RegistrationProbe>();
  probe->failures_before_success = 2;
  FakeNotificationRegistrar registrar(probe);
  {
    emke::audio::DeviceNotificationQueue queue(2u);
    emke::audio::DeviceCatalogError error;
    auto registration =
        emke::audio::MmDeviceNotificationRegistration::create_with_registrar(
            queue, registrar, error);
    EXPECT(context, registration != nullptr);
  }

  EXPECT(context, probe->unregister_calls == 1);
  EXPECT(context, probe->release_calls == 0);
  EXPECT(context, !registrar.registered_state.expired());
  EXPECT(context, registrar.last_backend != nullptr);
  EXPECT(
      context,
      registrar.last_backend != nullptr &&
          registrar.last_backend->emit_added());
}

void test_concurrent_notification_callbacks_remain_ordered(
    TestContext& context) {
  constexpr std::size_t producer_count = 4u;
  constexpr std::size_t attempts_per_producer = 1'000u;
  constexpr std::size_t total_attempts =
      producer_count * attempts_per_producer;
  emke::audio::DeviceNotificationQueue queue(total_attempts);
  emke::audio::DeviceNotificationReceiver receiver(queue);
  std::atomic<std::size_t> accepted = 0u;
  std::array<std::thread, producer_count> producers;

  for (std::size_t producer = 0u; producer < producer_count; ++producer) {
    producers[producer] = std::thread([&] {
      for (std::size_t attempt = 0u; attempt < attempts_per_producer;
           ++attempt) {
        if (receiver.on_added(u"{concurrent-opaque-id}")) {
          accepted.fetch_add(1u, std::memory_order_relaxed);
        }
      }
    });
  }
  for (std::thread& producer : producers) {
    producer.join();
  }

  std::size_t popped = 0u;
  std::uint64_t prior_sequence = 0u;
  emke::audio::DeviceNotificationEvent event;
  while (queue.try_pop(event)) {
    EXPECT(context, event.sequence > prior_sequence);
    prior_sequence = event.sequence;
    ++popped;
  }
  EXPECT(context, popped == accepted.load(std::memory_order_relaxed));
  EXPECT(context, queue.dropped_full() == 0u);
  EXPECT(context, queue.dropped_overlong_id() == 0u);
  EXPECT(
      context,
      accepted.load(std::memory_order_relaxed) +
              queue.dropped_contention() ==
          total_attempts);
}

}  // namespace

int run_device_catalog_tests() {
  TestContext context;
  test_four_distinct_virtual_roles_are_required(context);
  test_duplicate_role_blocks_readiness(context);
  test_missing_role_blocks_readiness(context);
  test_wrong_role_flow_blocks_readiness(context);
  test_inactive_and_unknown_virtual_roles_block_readiness(context);
  test_role_strings_are_stable_and_not_display_names(context);
  test_physical_endpoint_id_resolves_after_reenumeration(context);
  test_missing_saved_physical_endpoint_does_not_fallback(context);
  test_inactive_physical_endpoint_is_unavailable_without_fallback(context);
  test_follow_default_permits_migration_but_rejects_virtual(context);
  test_catalog_snapshots_and_resolutions_survive_refresh(context);
  test_catalog_snapshot_publish_supports_concurrent_readers(context);
  test_notification_callback_copies_without_enumeration(context);
  test_notification_pump_coalesces_and_preserves_snapshot_on_failure(context);
  test_null_default_id_is_an_event_and_invalid_ids_are_distinct(context);
#if defined(_WIN32) && defined(EMKE_NATIVE_AUDIO_DEVICE_TESTS)
  test_actual_mm_notification_client_translates_callback_arguments(context);
#endif
  test_notification_queue_is_bounded_and_ordered(context);
  test_notification_queue_drops_overlong_ids(context);
  test_notification_sequence_stops_before_wrap(context);
  test_registration_close_retries_and_queue_state_outlives_wrapper(context);
#if defined(EMKE_NATIVE_AUDIO_DEVICE_TESTS)
  test_registration_shell_oom_prevents_registrar_side_effect(context);
#endif
  test_registration_destructor_retains_state_after_unregister_failure(context);
  test_concurrent_notification_callbacks_remain_ordered(context);
  return context.failures();
}
