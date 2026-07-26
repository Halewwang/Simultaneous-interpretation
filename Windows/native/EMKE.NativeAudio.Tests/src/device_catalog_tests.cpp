#include "device_catalog.hpp"
#include "device_notifications.hpp"

#include <array>
#include <atomic>
#include <cstdint>
#include <iostream>
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
    std::optional<EndpointRole> role = std::nullopt) {
  return DeviceEndpoint{
      .id = std::move(id),
      .state = 1u,
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
  int enumeration_count = 0;
  int default_request_count = 0;
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
  test_role_strings_are_stable_and_not_display_names(context);
  test_physical_endpoint_id_resolves_after_reenumeration(context);
  test_missing_saved_physical_endpoint_does_not_fallback(context);
  test_follow_default_permits_migration_but_rejects_virtual(context);
  test_notification_callback_copies_without_enumeration(context);
  test_notification_queue_is_bounded_and_ordered(context);
  test_notification_queue_drops_overlong_ids(context);
  test_concurrent_notification_callbacks_remain_ordered(context);
  return context.failures();
}
