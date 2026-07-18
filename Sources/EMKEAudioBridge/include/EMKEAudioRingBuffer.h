#ifndef EMKE_AUDIO_RING_BUFFER_H
#define EMKE_AUDIO_RING_BUFFER_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct EMKEAudioRingBuffer EMKEAudioRingBuffer;

EMKEAudioRingBuffer *EMKEAudioRingBufferCreate(
    uint32_t capacityFrames,
    uint32_t channelCount
);

void EMKEAudioRingBufferDestroy(EMKEAudioRingBuffer *buffer);

uint32_t EMKEAudioRingBufferWrite(
    EMKEAudioRingBuffer *buffer,
    const float *interleavedFrames,
    uint32_t frameCount
);

uint32_t EMKEAudioRingBufferRead(
    EMKEAudioRingBuffer *buffer,
    float *interleavedFrames,
    uint32_t frameCount
);

uint32_t EMKEAudioRingBufferReadableFrames(const EMKEAudioRingBuffer *buffer);
uint32_t EMKEAudioRingBufferWritableFrames(const EMKEAudioRingBuffer *buffer);
void EMKEAudioRingBufferReset(EMKEAudioRingBuffer *buffer);

#ifdef __cplusplus
}
#endif

#endif
