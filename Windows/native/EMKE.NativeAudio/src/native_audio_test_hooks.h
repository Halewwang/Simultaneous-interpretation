#ifndef EMKE_NATIVE_AUDIO_TEST_HOOKS_H
#define EMKE_NATIVE_AUDIO_TEST_HOOKS_H

#include "emke_native_audio.h"

#if defined(_WIN32)
#if defined(EMKE_NATIVE_AUDIO_TEST_HOOKS_EXPORTS)
#define EMKE_AUDIO_TEST_API __declspec(dllexport)
#else
#define EMKE_AUDIO_TEST_API __declspec(dllimport)
#endif
#else
#define EMKE_AUDIO_TEST_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef enum emke_audio_test_direction {
  EMKE_AUDIO_TEST_DIRECTION_INBOUND = 0,
  EMKE_AUDIO_TEST_DIRECTION_OUTBOUND = 1
} emke_audio_test_direction;

typedef enum emke_audio_test_failure {
  EMKE_AUDIO_TEST_FAILURE_DEVICE = 0,
  EMKE_AUDIO_TEST_FAILURE_INBOUND_TRANSLATION = 1,
  EMKE_AUDIO_TEST_FAILURE_OUTBOUND_UNDERRUN = 2
} emke_audio_test_failure;

EMKE_AUDIO_TEST_API emke_audio_status
emke_audio_test_accept_synthetic_float32(
    emke_audio_handle* handle,
    emke_audio_test_direction direction,
    const float* interleaved_stereo,
    uint32_t local_frame_count);
EMKE_AUDIO_TEST_API emke_audio_status emke_audio_test_render_pcm16(
    emke_audio_handle* handle,
    emke_audio_test_direction direction,
    int16_t* mono_pcm16,
    uint32_t network_frame_count);
EMKE_AUDIO_TEST_API emke_audio_status emke_audio_test_inject_failure(
    emke_audio_handle* handle,
    emke_audio_test_failure failure);
/*
 * Configures the test-only source used by the public endpoint enumeration C
 * export. The descriptors are copied synchronously; NULL is valid only with a
 * zero count and configures an intentionally empty snapshot.
 */
EMKE_AUDIO_TEST_API emke_audio_status
emke_audio_test_set_endpoint_enumeration_fixture(
    const emke_audio_endpoint_descriptor_v1* items,
    uint32_t count);

#ifdef __cplusplus
}
#endif

#endif
