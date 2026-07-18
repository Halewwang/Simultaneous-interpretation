#ifndef EMKE_AUDIO_ROUTES_H
#define EMKE_AUDIO_ROUTES_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct EMKEAudioRoutes EMKEAudioRoutes;

EMKEAudioRoutes *EMKEAudioRoutesCreate(
    uint32_t capacityFrames,
    uint32_t channelCount
);

void EMKEAudioRoutesDestroy(EMKEAudioRoutes *routes);

uint32_t EMKEAudioRoutesWriteSpeaker(
    EMKEAudioRoutes *routes,
    const float *interleavedFrames,
    uint32_t frameCount
);

uint32_t EMKEAudioRoutesReadSpeaker(
    EMKEAudioRoutes *routes,
    float *interleavedFrames,
    uint32_t frameCount
);

uint32_t EMKEAudioRoutesWriteMicrophone(
    EMKEAudioRoutes *routes,
    const float *interleavedFrames,
    uint32_t frameCount
);

uint32_t EMKEAudioRoutesReadMicrophone(
    EMKEAudioRoutes *routes,
    float *interleavedFrames,
    uint32_t frameCount
);

uint64_t EMKEAudioRoutesSpeakerDroppedFrames(const EMKEAudioRoutes *routes);
uint64_t EMKEAudioRoutesMicrophoneZeroFilledFrames(const EMKEAudioRoutes *routes);
void EMKEAudioRoutesReset(EMKEAudioRoutes *routes);

#ifdef __cplusplus
}
#endif

#endif
