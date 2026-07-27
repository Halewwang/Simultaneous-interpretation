#ifndef EMKE_NATIVE_AUDIO_H
#define EMKE_NATIVE_AUDIO_H

#include <stdint.h>

#define EMKE_AUDIO_ABI_VERSION 1u
#define EMKE_AUDIO_ENDPOINT_ID_CAPACITY 512u
#define EMKE_AUDIO_DISCOVERED_ENDPOINT_COUNT 4u
#define EMKE_AUDIO_LOCAL_SAMPLE_RATE_HZ 48000u
#define EMKE_AUDIO_NETWORK_SAMPLE_RATE_HZ 24000u
#define EMKE_AUDIO_LOCAL_CYCLE_FRAMES 480u
#define EMKE_AUDIO_CAPTURE_CAPACITY_LOCAL_FRAMES 4800u
#define EMKE_AUDIO_TRANSLATED_PLAYBACK_CAPACITY_LOCAL_FRAMES 96000u
#define EMKE_AUDIO_TRANSLATED_QUEUE_CAPACITY_NETWORK_FRAMES 48000u

#if defined(_WIN32)
#if defined(EMKE_NATIVE_AUDIO_EXPORTS)
#define EMKE_AUDIO_API __declspec(dllexport)
#else
#define EMKE_AUDIO_API __declspec(dllimport)
#endif
#else
#define EMKE_AUDIO_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct emke_audio_handle emke_audio_handle;

typedef enum emke_audio_status {
  EMKE_AUDIO_OK = 0,
  EMKE_AUDIO_INVALID_ARGUMENT = 1,
  EMKE_AUDIO_ABI_MISMATCH = 2,
  EMKE_AUDIO_DEVICE_MISSING = 3,
  EMKE_AUDIO_FORMAT_UNSUPPORTED = 4,
  EMKE_AUDIO_QUEUE_FULL = 5,
  EMKE_AUDIO_NOT_RUNNING = 6,
  EMKE_AUDIO_INTERNAL_ERROR = 7
} emke_audio_status;

typedef enum emke_audio_route {
  EMKE_AUDIO_ROUTE_STOPPED = 0,
  EMKE_AUDIO_ROUTE_TRANSLATED = 1,
  EMKE_AUDIO_ROUTE_ORIGINAL_FAIL_OPEN = 2,
  EMKE_AUDIO_ROUTE_ORIGINAL_BYPASS = 3,
  EMKE_AUDIO_ROUTE_MUTED_FAIL_CLOSED = 4
} emke_audio_route;

typedef enum emke_audio_event_kind {
  EMKE_AUDIO_EVENT_NONE = 0,
  EMKE_AUDIO_EVENT_INBOUND_PCM16 = 1,
  EMKE_AUDIO_EVENT_OUTBOUND_PCM16 = 2,
  EMKE_AUDIO_EVENT_DEVICE_CHANGED = 3,
  EMKE_AUDIO_EVENT_STREAM_ERROR = 4,
  EMKE_AUDIO_EVENT_BACKPRESSURE = 5
} emke_audio_event_kind;

/*
 * Discovery is a control-thread operation only. Do not call it from WASAPI,
 * WaveRT, or any other realtime callback. Role values are stable driver
 * property values, never friendly names.
 */
typedef enum emke_audio_endpoint_role {
  EMKE_AUDIO_ENDPOINT_ROLE_MEETING_SPEAKER_RENDER = 0,
  EMKE_AUDIO_ENDPOINT_ROLE_APP_SPEAKER_CAPTURE = 1,
  EMKE_AUDIO_ENDPOINT_ROLE_APP_MICROPHONE_RENDER = 2,
  EMKE_AUDIO_ENDPOINT_ROLE_MEETING_MICROPHONE_CAPTURE = 3
} emke_audio_endpoint_role;

typedef enum emke_audio_endpoint_data_flow {
  EMKE_AUDIO_ENDPOINT_DATA_FLOW_RENDER = 0,
  EMKE_AUDIO_ENDPOINT_DATA_FLOW_CAPTURE = 1
} emke_audio_endpoint_data_flow;

typedef enum emke_audio_endpoint_discovery_status {
  EMKE_AUDIO_ENDPOINT_DISCOVERY_READY = 0,
  EMKE_AUDIO_ENDPOINT_DISCOVERY_DRIVER_MISSING = 1,
  EMKE_AUDIO_ENDPOINT_DISCOVERY_VIRTUAL_ENDPOINTS_PARTIAL = 2,
  EMKE_AUDIO_ENDPOINT_DISCOVERY_PHYSICAL_INPUT_MISSING = 3,
  EMKE_AUDIO_ENDPOINT_DISCOVERY_PHYSICAL_OUTPUT_MISSING = 4,
  EMKE_AUDIO_ENDPOINT_DISCOVERY_SOURCE_ERROR = 5
} emke_audio_endpoint_discovery_status;

/*
 * Endpoint IDs are fixed UTF-16 code-unit buffers. Each ID must be NUL
 * terminated when it uses fewer than EMKE_AUDIO_ENDPOINT_ID_CAPACITY units.
 * The six roles are copied by emke_audio_create; no caller storage is retained.
 */
typedef struct emke_audio_config {
  uint32_t size;
  uint32_t abi_version;
  uint16_t physical_input_endpoint_id[EMKE_AUDIO_ENDPOINT_ID_CAPACITY];
  uint16_t physical_output_endpoint_id[EMKE_AUDIO_ENDPOINT_ID_CAPACITY];
  uint16_t virtual_speaker_render_endpoint_id[EMKE_AUDIO_ENDPOINT_ID_CAPACITY];
  uint16_t virtual_speaker_capture_endpoint_id[EMKE_AUDIO_ENDPOINT_ID_CAPACITY];
  uint16_t
      virtual_microphone_render_endpoint_id[EMKE_AUDIO_ENDPOINT_ID_CAPACITY];
  uint16_t
      virtual_microphone_capture_endpoint_id[EMKE_AUDIO_ENDPOINT_ID_CAPACITY];
} emke_audio_config;

/*
 * Fixed, caller-owned discovery snapshot. endpoint_id is UTF-16 and NUL
 * terminated when shorter than EMKE_AUDIO_ENDPOINT_ID_CAPACITY. The function
 * fails closed with SOURCE_ERROR if an ID cannot fit this bounded snapshot.
 */
typedef struct emke_audio_discovered_endpoint {
  uint32_t size;
  uint32_t abi_version;
  uint32_t role;
  uint32_t data_flow;
  uint32_t state;
  uint32_t endpoint_id_length;
  uint16_t endpoint_id[EMKE_AUDIO_ENDPOINT_ID_CAPACITY];
} emke_audio_discovered_endpoint;

typedef struct emke_audio_endpoint_snapshot {
  uint32_t size;
  uint32_t abi_version;
  uint32_t discovery_status;
  uint32_t source_operation;
  int32_t source_native_code;
  uint32_t reserved;
  emke_audio_discovered_endpoint
      virtual_endpoints[EMKE_AUDIO_DISCOVERED_ENDPOINT_COUNT];
  uint32_t physical_input_endpoint_id_length;
  uint16_t physical_input_endpoint_id[EMKE_AUDIO_ENDPOINT_ID_CAPACITY];
  uint32_t physical_output_endpoint_id_length;
  uint16_t physical_output_endpoint_id[EMKE_AUDIO_ENDPOINT_ID_CAPACITY];
} emke_audio_endpoint_snapshot;

/*
 * poll_event fills metadata here and copies PCM16 into the separate caller
 * buffer. frame_count is a mono frame count at 24 kHz. If capacity is too
 * small, poll returns EMKE_AUDIO_INVALID_ARGUMENT, reports the required
 * metadata, and retains the event so the caller can retry.
 */
typedef struct emke_audio_event {
  uint32_t size;
  uint32_t abi_version;
  uint32_t kind;
  uint32_t status;
  uint32_t route;
  uint32_t frame_count;
  uint64_t sequence;
} emke_audio_event;

typedef struct emke_audio_diagnostics {
  uint32_t size;
  uint32_t abi_version;
  uint32_t is_running;
  uint32_t inbound_route;
  uint32_t outbound_route;
  uint32_t queued_inbound_translation_frames;
  uint32_t queued_outbound_translation_frames;
  uint32_t reserved;
  uint64_t captured_inbound_frames;
  uint64_t captured_outbound_frames;
  uint64_t consumed_inbound_translation_frames;
  uint64_t consumed_outbound_translation_frames;
  uint64_t dropped_frames;
  uint64_t queue_full_events;
  uint64_t outbound_underruns;
  uint64_t inbound_translation_failures;
  uint64_t device_failures;
} emke_audio_diagnostics;

/*
 * All input pointers are borrowed only for the duration of the call.
 * enqueue frame counts and queued/consumed/captured/dropped diagnostics use
 * 24 kHz mono network frames. Synthetic backend capture capacity and processing
 * cycles use 48 kHz local frames. The 48,000-network-frame translated queue
 * maps exactly to the 96,000-local-frame playback capacity.
 *
 * Enqueue functions synchronously copy PCM16 samples into native-owned queues.
 * destroy accepts NULL, so cleanup after a failed create is safe.
 */
EMKE_AUDIO_API emke_audio_status emke_audio_create(
    const emke_audio_config* config,
    emke_audio_handle** out_handle);
EMKE_AUDIO_API void emke_audio_destroy(emke_audio_handle* handle);
EMKE_AUDIO_API emke_audio_status emke_audio_start(emke_audio_handle* handle);
EMKE_AUDIO_API emke_audio_status emke_audio_stop(emke_audio_handle* handle);
EMKE_AUDIO_API emke_audio_status emke_audio_set_inbound_route(
    emke_audio_handle* handle,
    emke_audio_route route);
EMKE_AUDIO_API emke_audio_status emke_audio_set_outbound_route(
    emke_audio_handle* handle,
    emke_audio_route route);
EMKE_AUDIO_API emke_audio_status emke_audio_enqueue_inbound_translation(
    emke_audio_handle* handle,
    const int16_t* pcm16,
    uint32_t frame_count);
EMKE_AUDIO_API emke_audio_status emke_audio_enqueue_outbound_translation(
    emke_audio_handle* handle,
    const int16_t* pcm16,
    uint32_t frame_count);
EMKE_AUDIO_API emke_audio_status emke_audio_poll_event(
    emke_audio_handle* handle,
    emke_audio_event* out_event,
    int16_t* pcm16,
    uint32_t pcm_capacity_frames);
EMKE_AUDIO_API emke_audio_status emke_audio_get_diagnostics(
    emke_audio_handle* handle,
    emke_audio_diagnostics* out_diagnostics);
/*
 * Refreshes a bounded snapshot on the calling control/discovery thread. The
 * caller supplies storage and must initialize size and abi_version. A valid
 * call returns EMKE_AUDIO_OK even when discovery_status is not READY, so
 * callers can distinguish driverMissing, partial endpoints, missing physical
 * defaults, and source errors without inferring from display names.
 */
EMKE_AUDIO_API emke_audio_status emke_audio_discover_endpoints(
    emke_audio_endpoint_snapshot* out_snapshot);

#ifdef __cplusplus
}
#endif

#endif
