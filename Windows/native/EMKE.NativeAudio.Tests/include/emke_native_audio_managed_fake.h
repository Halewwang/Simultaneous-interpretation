#ifndef EMKE_NATIVE_AUDIO_MANAGED_FAKE_H
#define EMKE_NATIVE_AUDIO_MANAGED_FAKE_H

#include <stdint.h>

#if defined(_WIN32)
#if defined(EMKE_NATIVE_AUDIO_MANAGED_FAKE_EXPORTS)
#define EMKE_MANAGED_FAKE_API __declspec(dllexport)
#else
#define EMKE_MANAGED_FAKE_API __declspec(dllimport)
#endif
#else
#define EMKE_MANAGED_FAKE_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

EMKE_MANAGED_FAKE_API uint32_t emke_audio_managed_fake_reset(void);
EMKE_MANAGED_FAKE_API void emke_audio_managed_fake_set_abi_version(
    uint32_t version);
EMKE_MANAGED_FAKE_API void emke_audio_managed_fake_set_create_behavior(
    int32_t status,
    int32_t return_handle);
EMKE_MANAGED_FAKE_API void emke_audio_managed_fake_queue_two_frame_pcm(
    uint32_t kind,
    uint32_t route,
    uint64_t sequence,
    int16_t sample0,
    int16_t sample1);
EMKE_MANAGED_FAKE_API uint32_t
emke_audio_managed_fake_get_create_count(void);
EMKE_MANAGED_FAKE_API uint32_t
emke_audio_managed_fake_get_start_count(void);
EMKE_MANAGED_FAKE_API uint32_t
emke_audio_managed_fake_get_stop_count(void);
EMKE_MANAGED_FAKE_API uint32_t
emke_audio_managed_fake_get_destroy_count(void);
EMKE_MANAGED_FAKE_API uint32_t
emke_audio_managed_fake_get_poll_count(void);
EMKE_MANAGED_FAKE_API uint32_t
emke_audio_managed_fake_get_pcm_probe_count(void);
EMKE_MANAGED_FAKE_API uint32_t
emke_audio_managed_fake_get_pcm_copy_count(void);
EMKE_MANAGED_FAKE_API uint32_t
emke_audio_managed_fake_get_live_handle_count(void);

#ifdef __cplusplus
}
#endif

#endif
