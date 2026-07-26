#include "emke_native_audio.h"

#include "audio_runtime.hpp"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <new>
#include <span>

struct emke_audio_handle {
  explicit emke_audio_handle(const emke_audio_config& config)
      : runtime(config) {}

  emke::audio::AudioRuntime runtime;
};

namespace {

template <typename Struct>
emke_audio_status validate_abi_struct(const Struct* value) {
  if (value == nullptr || value->size < sizeof(Struct)) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }
  if (value->abi_version != EMKE_AUDIO_ABI_VERSION) {
    return EMKE_AUDIO_ABI_MISMATCH;
  }
  return EMKE_AUDIO_OK;
}

std::span<const std::int16_t> input_pcm_span(const std::int16_t* pcm16,
                                             std::uint32_t frame_count) {
  return {pcm16, static_cast<std::size_t>(frame_count)};
}

}  // namespace

extern "C" {

EMKE_AUDIO_API emke_audio_status emke_audio_create(
    const emke_audio_config* config,
    emke_audio_handle** out_handle) {
  if (out_handle == nullptr) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }
  *out_handle = nullptr;

  const emke_audio_status validation = validate_abi_struct(config);
  if (validation != EMKE_AUDIO_OK) {
    return validation;
  }

  try {
    auto* handle = new (std::nothrow) emke_audio_handle(*config);
    if (handle == nullptr) {
      return EMKE_AUDIO_INTERNAL_ERROR;
    }
    *out_handle = handle;
    return EMKE_AUDIO_OK;
  } catch (...) {
    return EMKE_AUDIO_INTERNAL_ERROR;
  }
}

EMKE_AUDIO_API void emke_audio_destroy(emke_audio_handle* handle) {
  delete handle;
}

EMKE_AUDIO_API emke_audio_status emke_audio_start(
    emke_audio_handle* handle) {
  if (handle == nullptr) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }
  try {
    return handle->runtime.start();
  } catch (...) {
    return EMKE_AUDIO_INTERNAL_ERROR;
  }
}

EMKE_AUDIO_API emke_audio_status emke_audio_stop(
    emke_audio_handle* handle) {
  if (handle == nullptr) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }
  try {
    return handle->runtime.stop();
  } catch (...) {
    return EMKE_AUDIO_INTERNAL_ERROR;
  }
}

EMKE_AUDIO_API emke_audio_status emke_audio_set_inbound_route(
    emke_audio_handle* handle,
    emke_audio_route route) {
  if (handle == nullptr) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }
  try {
    return handle->runtime.set_inbound_route(route);
  } catch (...) {
    return EMKE_AUDIO_INTERNAL_ERROR;
  }
}

EMKE_AUDIO_API emke_audio_status emke_audio_set_outbound_route(
    emke_audio_handle* handle,
    emke_audio_route route) {
  if (handle == nullptr) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }
  try {
    return handle->runtime.set_outbound_route(route);
  } catch (...) {
    return EMKE_AUDIO_INTERNAL_ERROR;
  }
}

EMKE_AUDIO_API emke_audio_status emke_audio_enqueue_inbound_translation(
    emke_audio_handle* handle,
    const int16_t* pcm16,
    uint32_t frame_count) {
  if (handle == nullptr || pcm16 == nullptr || frame_count == 0u) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }
  try {
    return handle->runtime.enqueue_inbound_translation(
        input_pcm_span(pcm16, frame_count));
  } catch (...) {
    return EMKE_AUDIO_INTERNAL_ERROR;
  }
}

EMKE_AUDIO_API emke_audio_status emke_audio_enqueue_outbound_translation(
    emke_audio_handle* handle,
    const int16_t* pcm16,
    uint32_t frame_count) {
  if (handle == nullptr || pcm16 == nullptr || frame_count == 0u) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }
  try {
    return handle->runtime.enqueue_outbound_translation(
        input_pcm_span(pcm16, frame_count));
  } catch (...) {
    return EMKE_AUDIO_INTERNAL_ERROR;
  }
}

EMKE_AUDIO_API emke_audio_status emke_audio_poll_event(
    emke_audio_handle* handle,
    emke_audio_event* out_event,
    int16_t* pcm16,
    uint32_t pcm_capacity_frames) {
  if (handle == nullptr) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }
  const emke_audio_status validation = validate_abi_struct(out_event);
  if (validation != EMKE_AUDIO_OK) {
    return validation;
  }

  try {
    emke::audio::AudioEvent event;
    const emke_audio_status status = handle->runtime.poll_event(event);
    if (status != EMKE_AUDIO_OK) {
      return status;
    }

    out_event->kind = event.kind;
    out_event->status = event.status;
    out_event->route = event.route;
    out_event->frame_count =
        static_cast<std::uint32_t>(event.pcm16.size());
    out_event->sequence = event.sequence;

    if (!event.pcm16.empty()) {
      if (pcm16 == nullptr || pcm_capacity_frames < event.pcm16.size()) {
        return EMKE_AUDIO_INVALID_ARGUMENT;
      }
      std::copy(event.pcm16.begin(), event.pcm16.end(), pcm16);
    }
    return EMKE_AUDIO_OK;
  } catch (...) {
    return EMKE_AUDIO_INTERNAL_ERROR;
  }
}

EMKE_AUDIO_API emke_audio_status emke_audio_get_diagnostics(
    emke_audio_handle* handle,
    emke_audio_diagnostics* out_diagnostics) {
  if (handle == nullptr) {
    return EMKE_AUDIO_INVALID_ARGUMENT;
  }
  const emke_audio_status validation =
      validate_abi_struct(out_diagnostics);
  if (validation != EMKE_AUDIO_OK) {
    return validation;
  }

  try {
    handle->runtime.write_diagnostics(*out_diagnostics);
    return EMKE_AUDIO_OK;
  } catch (...) {
    return EMKE_AUDIO_INTERNAL_ERROR;
  }
}

}  // extern "C"
