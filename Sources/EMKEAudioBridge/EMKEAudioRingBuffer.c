#include "EMKEAudioRingBuffer.h"

#include <stdatomic.h>
#include <stddef.h>
#include <stdlib.h>
#include <string.h>

struct EMKEAudioRingBuffer {
    uint32_t capacityFrames;
    uint32_t channelCount;
    _Atomic uint64_t readPosition;
    _Atomic uint64_t writePosition;
    float storage[];
};

static uint32_t EMKEMinUInt32(uint32_t lhs, uint32_t rhs) {
    return lhs < rhs ? lhs : rhs;
}

EMKEAudioRingBuffer *EMKEAudioRingBufferCreate(
    uint32_t capacityFrames,
    uint32_t channelCount
) {
    if (capacityFrames == 0 || channelCount == 0) {
        return NULL;
    }

    const uint64_t sampleCount = (uint64_t)capacityFrames * channelCount;
    if (sampleCount > (SIZE_MAX - sizeof(EMKEAudioRingBuffer)) / sizeof(float)) {
        return NULL;
    }

    const size_t byteCount = sizeof(EMKEAudioRingBuffer) +
        (size_t)sampleCount * sizeof(float);
    EMKEAudioRingBuffer *buffer = calloc(1, byteCount);
    if (buffer == NULL) {
        return NULL;
    }

    buffer->capacityFrames = capacityFrames;
    buffer->channelCount = channelCount;
    atomic_init(&buffer->readPosition, 0);
    atomic_init(&buffer->writePosition, 0);
    return buffer;
}

void EMKEAudioRingBufferDestroy(EMKEAudioRingBuffer *buffer) {
    free(buffer);
}

uint32_t EMKEAudioRingBufferReadableFrames(const EMKEAudioRingBuffer *buffer) {
    if (buffer == NULL) {
        return 0;
    }

    const uint64_t readPosition = atomic_load_explicit(
        &buffer->readPosition,
        memory_order_acquire
    );
    const uint64_t writePosition = atomic_load_explicit(
        &buffer->writePosition,
        memory_order_acquire
    );
    const uint64_t readable = writePosition - readPosition;
    return readable > buffer->capacityFrames
        ? buffer->capacityFrames
        : (uint32_t)readable;
}

uint32_t EMKEAudioRingBufferWritableFrames(const EMKEAudioRingBuffer *buffer) {
    if (buffer == NULL) {
        return 0;
    }
    return buffer->capacityFrames - EMKEAudioRingBufferReadableFrames(buffer);
}

uint32_t EMKEAudioRingBufferWrite(
    EMKEAudioRingBuffer *buffer,
    const float *interleavedFrames,
    uint32_t frameCount
) {
    if (buffer == NULL || interleavedFrames == NULL || frameCount == 0) {
        return 0;
    }

    const uint64_t writePosition = atomic_load_explicit(
        &buffer->writePosition,
        memory_order_relaxed
    );
    const uint64_t readPosition = atomic_load_explicit(
        &buffer->readPosition,
        memory_order_acquire
    );
    const uint64_t used = writePosition - readPosition;
    const uint32_t writable = used >= buffer->capacityFrames
        ? 0
        : buffer->capacityFrames - (uint32_t)used;
    const uint32_t transferred = EMKEMinUInt32(frameCount, writable);
    if (transferred == 0) {
        return 0;
    }

    const uint32_t startFrame = (uint32_t)(writePosition % buffer->capacityFrames);
    const uint32_t firstFrames = EMKEMinUInt32(
        transferred,
        buffer->capacityFrames - startFrame
    );
    const size_t channels = buffer->channelCount;
    memcpy(
        buffer->storage + (size_t)startFrame * channels,
        interleavedFrames,
        (size_t)firstFrames * channels * sizeof(float)
    );

    const uint32_t secondFrames = transferred - firstFrames;
    if (secondFrames > 0) {
        memcpy(
            buffer->storage,
            interleavedFrames + (size_t)firstFrames * channels,
            (size_t)secondFrames * channels * sizeof(float)
        );
    }

    atomic_store_explicit(
        &buffer->writePosition,
        writePosition + transferred,
        memory_order_release
    );
    return transferred;
}

uint32_t EMKEAudioRingBufferRead(
    EMKEAudioRingBuffer *buffer,
    float *interleavedFrames,
    uint32_t frameCount
) {
    if (buffer == NULL || interleavedFrames == NULL || frameCount == 0) {
        return 0;
    }

    const uint64_t readPosition = atomic_load_explicit(
        &buffer->readPosition,
        memory_order_relaxed
    );
    const uint64_t writePosition = atomic_load_explicit(
        &buffer->writePosition,
        memory_order_acquire
    );
    const uint64_t available = writePosition - readPosition;
    const uint32_t readable = available > buffer->capacityFrames
        ? buffer->capacityFrames
        : (uint32_t)available;
    const uint32_t transferred = EMKEMinUInt32(frameCount, readable);
    if (transferred == 0) {
        return 0;
    }

    const uint32_t startFrame = (uint32_t)(readPosition % buffer->capacityFrames);
    const uint32_t firstFrames = EMKEMinUInt32(
        transferred,
        buffer->capacityFrames - startFrame
    );
    const size_t channels = buffer->channelCount;
    memcpy(
        interleavedFrames,
        buffer->storage + (size_t)startFrame * channels,
        (size_t)firstFrames * channels * sizeof(float)
    );

    const uint32_t secondFrames = transferred - firstFrames;
    if (secondFrames > 0) {
        memcpy(
            interleavedFrames + (size_t)firstFrames * channels,
            buffer->storage,
            (size_t)secondFrames * channels * sizeof(float)
        );
    }

    atomic_store_explicit(
        &buffer->readPosition,
        readPosition + transferred,
        memory_order_release
    );
    return transferred;
}

void EMKEAudioRingBufferReset(EMKEAudioRingBuffer *buffer) {
    if (buffer == NULL) {
        return;
    }

    atomic_store_explicit(&buffer->readPosition, 0, memory_order_relaxed);
    atomic_store_explicit(&buffer->writePosition, 0, memory_order_release);
}
