#include "emke_native_audio.h"
#include "emke_native_audio_managed_fake.h"

#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <mutex>
#include <new>
#include <string_view>

struct emke_audio_handle {
  bool running = false;
};

namespace {

std::atomic<std::uint32_t> abi_version{EMKE_AUDIO_ABI_VERSION};
std::atomic<std::int32_t> create_status{EMKE_AUDIO_OK};
std::atomic<bool> return_handle_on_create_failure{false};
std::atomic<std::uint32_t> create_count{0};
std::atomic<std::uint32_t> start_count{0};
std::atomic<std::uint32_t> stop_count{0};
std::atomic<std::uint32_t> destroy_count{0};
std::atomic<std::uint32_t> poll_count{0};
std::atomic<std::uint32_t> pcm_probe_count{0};
std::atomic<std::uint32_t> pcm_copy_count{0};
std::atomic<std::uint32_t> live_handle_count{0};

struct PendingPcm {
  bool has_value = false;
  std::uint32_t kind = EMKE_AUDIO_EVENT_NONE;
  std::uint32_t route = EMKE_AUDIO_ROUTE_STOPPED;
  std::uint64_t sequence = 0;
  std::array<std::int16_t, 2> samples{};
};

std::mutex pcm_mutex;
PendingPcm pending_pcm;

template <std::size_t Capacity>
std::uint32_t write_id(std::uint16_t (&destination)[Capacity],
                       std::string_view value) {
  if (value.size() >= Capacity) {
    return 0;
  }
  for (std::size_t index = 0; index < value.size(); ++index) {
    destination[index] = static_cast<std::uint16_t>(value[index]);
  }
  destination[value.size()] = 0;
  return static_cast<std::uint32_t>(value.size());
}

emke_audio_discovered_endpoint endpoint(emke_audio_endpoint_role role,
                                        emke_audio_endpoint_data_flow flow,
                                        std::string_view id) {
  emke_audio_discovered_endpoint result{};
  result.size = sizeof(result);
  result.abi_version = EMKE_AUDIO_ABI_VERSION;
  result.role = role;
  result.data_flow = flow;
  result.state = 1;
  result.endpoint_id_length = write_id(result.endpoint_id, id);
  return result;
}

template <typename Struct>
emke_audio_status validate_struct(const Struct* value) {
  if (value == nullptr || value->size < sizeof(Struct)) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }
  if (value->abi_version != EMKE_AUDIO_ABI_VERSION) {
    return EMKE_AUDIO_ABI_MISMATCH;
  }
  return EMKE_AUDIO_OK;
}

}  // namespace

extern "C" {

EMKE_AUDIO_API std::uint32_t emke_audio_get_abi_version(void) {
  return abi_version.load();
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

EMKE_AUDIO_API emke_audio_status emke_audio_discover_endpoints(
    emke_audio_endpoint_snapshot* out_snapshot) {
  const emke_audio_status validation = validate_struct(out_snapshot);
  if (validation != EMKE_AUDIO_OK) {
    return validation;
  }

  *out_snapshot = {};
  out_snapshot->size = sizeof(*out_snapshot);
  out_snapshot->abi_version = EMKE_AUDIO_ABI_VERSION;
  out_snapshot->discovery_status = EMKE_AUDIO_ENDPOINT_DISCOVERY_READY;
  out_snapshot->virtual_endpoints[0] = endpoint(
      EMKE_AUDIO_ENDPOINT_ROLE_MEETING_SPEAKER_RENDER,
      EMKE_AUDIO_ENDPOINT_DATA_FLOW_RENDER,
      "fake-virtual-speaker-render");
  out_snapshot->virtual_endpoints[1] = endpoint(
      EMKE_AUDIO_ENDPOINT_ROLE_APP_SPEAKER_CAPTURE,
      EMKE_AUDIO_ENDPOINT_DATA_FLOW_CAPTURE,
      "fake-virtual-speaker-capture");
  out_snapshot->virtual_endpoints[2] = endpoint(
      EMKE_AUDIO_ENDPOINT_ROLE_APP_MICROPHONE_RENDER,
      EMKE_AUDIO_ENDPOINT_DATA_FLOW_RENDER,
      "fake-virtual-microphone-render");
  out_snapshot->virtual_endpoints[3] = endpoint(
      EMKE_AUDIO_ENDPOINT_ROLE_MEETING_MICROPHONE_CAPTURE,
      EMKE_AUDIO_ENDPOINT_DATA_FLOW_CAPTURE,
      "fake-virtual-microphone-capture");
  out_snapshot->physical_input_endpoint_id_length = write_id(
      out_snapshot->physical_input_endpoint_id, "fake-physical-input");
  out_snapshot->physical_output_endpoint_id_length = write_id(
      out_snapshot->physical_output_endpoint_id, "fake-physical-output");
  return EMKE_AUDIO_OK;
}

EMKE_AUDIO_API emke_audio_status emke_audio_create(
    const emke_audio_config* config,
    emke_audio_handle** out_handle) {
  if (out_handle == nullptr) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }
  *out_handle = nullptr;
  create_count.fetch_add(1);

  const emke_audio_status validation = validate_struct(config);
  if (validation != EMKE_AUDIO_OK) {
    return validation;
  }

  const auto configured_status =
      static_cast<emke_audio_status>(create_status.load());
  if (configured_status != EMKE_AUDIO_OK &&
      !return_handle_on_create_failure.load()) {
    return configured_status;
  }

  auto* handle = new (std::nothrow) emke_audio_handle{};
  if (handle == nullptr) {
    return EMKE_AUDIO_INTERNAL_ERROR;
  }
  *out_handle = handle;
  live_handle_count.fetch_add(1);
  return configured_status;
}

EMKE_AUDIO_API void emke_audio_destroy(emke_audio_handle* handle) {
  if (handle == nullptr) {
    return;
  }
  destroy_count.fetch_add(1);
  live_handle_count.fetch_sub(1);
  delete handle;
}

EMKE_AUDIO_API emke_audio_status emke_audio_start(
    emke_audio_handle* handle) {
  if (handle == nullptr) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }
  start_count.fetch_add(1);
  handle->running = true;
  return EMKE_AUDIO_OK;
}

EMKE_AUDIO_API emke_audio_status emke_audio_stop(
    emke_audio_handle* handle) {
  if (handle == nullptr) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }
  stop_count.fetch_add(1);
  handle->running = false;
  return EMKE_AUDIO_OK;
}

EMKE_AUDIO_API emke_audio_status emke_audio_set_inbound_route(
    emke_audio_handle* handle,
    emke_audio_route route) {
  if (handle == nullptr || route < EMKE_AUDIO_ROUTE_STOPPED ||
      route > EMKE_AUDIO_ROUTE_ORIGINAL_BYPASS) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }
  return EMKE_AUDIO_OK;
}

EMKE_AUDIO_API emke_audio_status emke_audio_set_outbound_route(
    emke_audio_handle* handle,
    emke_audio_route route) {
  if (handle == nullptr || route < EMKE_AUDIO_ROUTE_STOPPED ||
      route > EMKE_AUDIO_ROUTE_MUTED_FAIL_CLOSED ||
      route == EMKE_AUDIO_ROUTE_ORIGINAL_FAIL_OPEN) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }
  return EMKE_AUDIO_OK;
}

EMKE_AUDIO_API emke_audio_status emke_audio_enqueue_inbound_translation(
    emke_audio_handle* handle,
    const std::int16_t* pcm16,
    std::uint32_t frame_count) {
  return handle != nullptr && pcm16 != nullptr && frame_count != 0
             ? EMKE_AUDIO_OK
             : EMKE_AUDIO_INVALID_ARGUMENT;
}

EMKE_AUDIO_API emke_audio_status emke_audio_enqueue_outbound_translation(
    emke_audio_handle* handle,
    const std::int16_t* pcm16,
    std::uint32_t frame_count) {
  return handle != nullptr && pcm16 != nullptr && frame_count != 0
             ? EMKE_AUDIO_OK
             : EMKE_AUDIO_INVALID_ARGUMENT;
}

EMKE_AUDIO_API emke_audio_status emke_audio_poll_event(
    emke_audio_handle* handle,
    emke_audio_event* out_event,
    std::int16_t* pcm16,
    std::uint32_t pcm_capacity_frames) {
  if (handle == nullptr) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }
  const emke_audio_status validation = validate_struct(out_event);
  if (validation != EMKE_AUDIO_OK) {
    return validation;
  }
  poll_count.fetch_add(1);

  std::scoped_lock lock(pcm_mutex);
  if (!pending_pcm.has_value) {
    out_event->kind = EMKE_AUDIO_EVENT_NONE;
    out_event->status = EMKE_AUDIO_OK;
    out_event->route = EMKE_AUDIO_ROUTE_STOPPED;
    out_event->frame_count = 0;
    out_event->sequence = 0;
    return EMKE_AUDIO_OK;
  }

  out_event->kind = pending_pcm.kind;
  out_event->status = EMKE_AUDIO_OK;
  out_event->route = pending_pcm.route;
  out_event->frame_count = pending_pcm.samples.size();
  out_event->sequence = pending_pcm.sequence;
  if (pcm16 == nullptr ||
      pcm_capacity_frames < pending_pcm.samples.size()) {
    pcm_probe_count.fetch_add(1);
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }

  pcm16[0] = pending_pcm.samples[0];
  pcm16[1] = pending_pcm.samples[1];
  pending_pcm.has_value = false;
  pcm_copy_count.fetch_add(1);
  return EMKE_AUDIO_OK;
}

EMKE_AUDIO_API emke_audio_status emke_audio_get_diagnostics(
    emke_audio_handle* handle,
    emke_audio_diagnostics* out_diagnostics) {
  if (handle == nullptr) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }
  const emke_audio_status validation = validate_struct(out_diagnostics);
  if (validation != EMKE_AUDIO_OK) {
    return validation;
  }
  *out_diagnostics = {};
  out_diagnostics->size = sizeof(*out_diagnostics);
  out_diagnostics->abi_version = EMKE_AUDIO_ABI_VERSION;
  out_diagnostics->is_running = handle->running ? 1u : 0u;
  return EMKE_AUDIO_OK;
}

EMKE_MANAGED_FAKE_API std::uint32_t emke_audio_managed_fake_reset(void) {
  const std::uint32_t previous_live_handles = live_handle_count.load();
  abi_version.store(EMKE_AUDIO_ABI_VERSION);
  create_status.store(EMKE_AUDIO_OK);
  return_handle_on_create_failure.store(false);
  create_count.store(0);
  start_count.store(0);
  stop_count.store(0);
  destroy_count.store(0);
  poll_count.store(0);
  pcm_probe_count.store(0);
  pcm_copy_count.store(0);
  {
    std::scoped_lock lock(pcm_mutex);
    pending_pcm = {};
  }
  return previous_live_handles;
}

EMKE_MANAGED_FAKE_API void emke_audio_managed_fake_set_abi_version(
    std::uint32_t version) {
  abi_version.store(version);
}

EMKE_MANAGED_FAKE_API void emke_audio_managed_fake_set_create_behavior(
    std::int32_t status,
    std::int32_t return_handle) {
  create_status.store(status);
  return_handle_on_create_failure.store(return_handle != 0);
}

EMKE_MANAGED_FAKE_API void
emke_audio_managed_fake_queue_two_frame_pcm(
    std::uint32_t kind,
    std::uint32_t route,
    std::uint64_t sequence,
    std::int16_t sample0,
    std::int16_t sample1) {
  std::scoped_lock lock(pcm_mutex);
  pending_pcm = PendingPcm{
      .has_value = true,
      .kind = kind,
      .route = route,
      .sequence = sequence,
      .samples = {sample0, sample1},
  };
}

EMKE_MANAGED_FAKE_API std::uint32_t
emke_audio_managed_fake_get_create_count(void) {
  return create_count.load();
}

EMKE_MANAGED_FAKE_API std::uint32_t
emke_audio_managed_fake_get_start_count(void) {
  return start_count.load();
}

EMKE_MANAGED_FAKE_API std::uint32_t
emke_audio_managed_fake_get_stop_count(void) {
  return stop_count.load();
}

EMKE_MANAGED_FAKE_API std::uint32_t
emke_audio_managed_fake_get_destroy_count(void) {
  return destroy_count.load();
}

EMKE_MANAGED_FAKE_API std::uint32_t
emke_audio_managed_fake_get_poll_count(void) {
  return poll_count.load();
}

EMKE_MANAGED_FAKE_API std::uint32_t
emke_audio_managed_fake_get_pcm_probe_count(void) {
  return pcm_probe_count.load();
}

EMKE_MANAGED_FAKE_API std::uint32_t
emke_audio_managed_fake_get_pcm_copy_count(void) {
  return pcm_copy_count.load();
}

EMKE_MANAGED_FAKE_API std::uint32_t
emke_audio_managed_fake_get_live_handle_count(void) {
  return live_handle_count.load();
}

}  // extern "C"
