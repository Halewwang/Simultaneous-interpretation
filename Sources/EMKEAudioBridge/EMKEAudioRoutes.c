#include "EMKEAudioRoutes.h"

#include "EMKEAudioRingBuffer.h"

#include <stdatomic.h>
#include <stddef.h>
#include <stdlib.h>
#include <string.h>

struct EMKEAudioRoutes {
    uint32_t channelCount;
    EMKEAudioRingBuffer *speaker;
    EMKEAudioRingBuffer *microphone;
    _Atomic uint64_t speakerDroppedFrames;
    _Atomic uint64_t microphoneZeroFilledFrames;
};

EMKEAudioRoutes *EMKEAudioRoutesCreate(
    uint32_t capacityFrames,
    uint32_t channelCount
) {
    if (capacityFrames == 0 || channelCount == 0) {
        return NULL;
    }

    EMKEAudioRoutes *routes = calloc(1, sizeof(EMKEAudioRoutes));
    if (routes == NULL) {
        return NULL;
    }

    routes->speaker = EMKEAudioRingBufferCreate(capacityFrames, channelCount);
    routes->microphone = EMKEAudioRingBufferCreate(capacityFrames, channelCount);
    if (routes->speaker == NULL || routes->microphone == NULL) {
        EMKEAudioRingBufferDestroy(routes->speaker);
        EMKEAudioRingBufferDestroy(routes->microphone);
        free(routes);
        return NULL;
    }

    routes->channelCount = channelCount;
    atomic_init(&routes->speakerDroppedFrames, 0);
    atomic_init(&routes->microphoneZeroFilledFrames, 0);
    return routes;
}

void EMKEAudioRoutesDestroy(EMKEAudioRoutes *routes) {
    if (routes == NULL) {
        return;
    }

    EMKEAudioRingBufferDestroy(routes->speaker);
    EMKEAudioRingBufferDestroy(routes->microphone);
    free(routes);
}

uint32_t EMKEAudioRoutesWriteSpeaker(
    EMKEAudioRoutes *routes,
    const float *interleavedFrames,
    uint32_t frameCount
) {
    if (routes == NULL || interleavedFrames == NULL || frameCount == 0) {
        return 0;
    }

    const uint32_t transferred = EMKEAudioRingBufferWrite(
        routes->speaker,
        interleavedFrames,
        frameCount
    );
    atomic_fetch_add_explicit(
        &routes->speakerDroppedFrames,
        frameCount - transferred,
        memory_order_relaxed
    );
    return transferred;
}

uint32_t EMKEAudioRoutesReadSpeaker(
    EMKEAudioRoutes *routes,
    float *interleavedFrames,
    uint32_t frameCount
) {
    if (routes == NULL) {
        return 0;
    }
    return EMKEAudioRingBufferRead(routes->speaker, interleavedFrames, frameCount);
}

uint32_t EMKEAudioRoutesWriteMicrophone(
    EMKEAudioRoutes *routes,
    const float *interleavedFrames,
    uint32_t frameCount
) {
    if (routes == NULL) {
        return 0;
    }
    return EMKEAudioRingBufferWrite(routes->microphone, interleavedFrames, frameCount);
}

uint32_t EMKEAudioRoutesReadMicrophone(
    EMKEAudioRoutes *routes,
    float *interleavedFrames,
    uint32_t frameCount
) {
    if (routes == NULL || interleavedFrames == NULL || frameCount == 0) {
        return 0;
    }

    const uint32_t transferred = EMKEAudioRingBufferRead(
        routes->microphone,
        interleavedFrames,
        frameCount
    );
    const uint32_t missingFrames = frameCount - transferred;
    if (missingFrames > 0) {
        memset(
            interleavedFrames + (size_t)transferred * routes->channelCount,
            0,
            (size_t)missingFrames * routes->channelCount * sizeof(float)
        );
        atomic_fetch_add_explicit(
            &routes->microphoneZeroFilledFrames,
            missingFrames,
            memory_order_relaxed
        );
    }
    return frameCount;
}

uint64_t EMKEAudioRoutesSpeakerDroppedFrames(const EMKEAudioRoutes *routes) {
    if (routes == NULL) {
        return 0;
    }
    return atomic_load_explicit(
        &routes->speakerDroppedFrames,
        memory_order_relaxed
    );
}

uint64_t EMKEAudioRoutesMicrophoneZeroFilledFrames(const EMKEAudioRoutes *routes) {
    if (routes == NULL) {
        return 0;
    }
    return atomic_load_explicit(
        &routes->microphoneZeroFilledFrames,
        memory_order_relaxed
    );
}

void EMKEAudioRoutesResetSpeaker(EMKEAudioRoutes *routes) {
    if (routes == NULL) {
        return;
    }

    EMKEAudioRingBufferReset(routes->speaker);
    atomic_store_explicit(&routes->speakerDroppedFrames, 0, memory_order_relaxed);
}

void EMKEAudioRoutesResetMicrophone(EMKEAudioRoutes *routes) {
    if (routes == NULL) {
        return;
    }

    EMKEAudioRingBufferReset(routes->microphone);
    atomic_store_explicit(
        &routes->microphoneZeroFilledFrames,
        0,
        memory_order_relaxed
    );
}

void EMKEAudioRoutesReset(EMKEAudioRoutes *routes) {
    EMKEAudioRoutesResetSpeaker(routes);
    EMKEAudioRoutesResetMicrophone(routes);
}
