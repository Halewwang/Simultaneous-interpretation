#include "EMKEAudioHAL.h"

#include "EMKEAudioRingBuffer.h"

#include <stdbool.h>
#include <stdatomic.h>
#include <stddef.h>
#include <stdlib.h>
#include <string.h>

enum {
    kEMKEHALChannelCount = 2,
};

struct EMKEHALInput {
    AudioUnit unit;
    EMKEAudioRingBuffer *buffer;
    float *scratch;
    uint32_t scratchCapacityFrames;
    double clientSampleRate;
    _Atomic bool started;
    _Atomic uint64_t callbackCount;
    _Atomic uint32_t lastCallbackFrameCount;
    _Atomic uint64_t renderedFrameCount;
    _Atomic uint64_t writtenFrameCount;
    _Atomic uint64_t renderErrorCount;
    _Atomic uint64_t oversizedCallbackCount;
    _Atomic int32_t lastRenderStatus;
};

struct EMKEHALOutput {
    AudioUnit unit;
    EMKEAudioRingBuffer *buffer;
    _Atomic bool started;
};

static AudioStreamBasicDescription EMKEHALTransportFormat(
    double sampleRate
) {
    AudioStreamBasicDescription format = {0};
    format.mSampleRate = sampleRate;
    format.mFormatID = kAudioFormatLinearPCM;
    format.mFormatFlags = kAudioFormatFlagIsFloat |
        kAudioFormatFlagIsPacked |
        kAudioFormatFlagsNativeEndian;
    format.mBytesPerPacket = kEMKEHALChannelCount * sizeof(float);
    format.mFramesPerPacket = 1;
    format.mBytesPerFrame = kEMKEHALChannelCount * sizeof(float);
    format.mChannelsPerFrame = kEMKEHALChannelCount;
    format.mBitsPerChannel = 8 * sizeof(float);
    return format;
}

static OSStatus EMKEHALCreateUnit(AudioUnit *outUnit) {
    if (outUnit == NULL) {
        return kAudioUnitErr_InvalidPropertyValue;
    }
    *outUnit = NULL;

    AudioComponentDescription description = {0};
    description.componentType = kAudioUnitType_Output;
    description.componentSubType = kAudioUnitSubType_HALOutput;
    description.componentManufacturer = kAudioUnitManufacturer_Apple;
    AudioComponent component = AudioComponentFindNext(NULL, &description);
    if (component == NULL) {
        return kAudioUnitErr_FailedInitialization;
    }
    return AudioComponentInstanceNew(component, outUnit);
}

static OSStatus EMKEHALSetEnabledIO(
    AudioUnit unit,
    AudioUnitScope scope,
    AudioUnitElement element,
    UInt32 enabled
) {
    return AudioUnitSetProperty(
        unit,
        kAudioOutputUnitProperty_EnableIO,
        scope,
        element,
        &enabled,
        sizeof(enabled)
    );
}

static OSStatus EMKEHALSetDevice(AudioUnit unit, AudioObjectID deviceID) {
    return AudioUnitSetProperty(
        unit,
        kAudioOutputUnitProperty_CurrentDevice,
        kAudioUnitScope_Global,
        0,
        &deviceID,
        sizeof(deviceID)
    );
}

static OSStatus EMKEHALSetFormat(
    AudioUnit unit,
    AudioUnitScope scope,
    AudioUnitElement element,
    double sampleRate
) {
    AudioStreamBasicDescription format = EMKEHALTransportFormat(sampleRate);
    return AudioUnitSetProperty(
        unit,
        kAudioUnitProperty_StreamFormat,
        scope,
        element,
        &format,
        sizeof(format)
    );
}

static OSStatus EMKEHALGetFormat(
    AudioUnit unit,
    AudioUnitScope scope,
    AudioUnitElement element,
    AudioStreamBasicDescription *outFormat
) {
    if (outFormat == NULL) {
        return kAudioUnitErr_InvalidPropertyValue;
    }
    UInt32 size = sizeof(*outFormat);
    return AudioUnitGetProperty(
        unit,
        kAudioUnitProperty_StreamFormat,
        scope,
        element,
        outFormat,
        &size
    );
}

static OSStatus EMKEHALSetInputChannelMap(
    AudioUnit unit,
    UInt32 deviceChannelCount
) {
    if (deviceChannelCount == 0) {
        return kAudioUnitErr_FormatNotSupported;
    }
    const SInt32 channelMap[kEMKEHALChannelCount] = {
        0,
        deviceChannelCount > 1 ? 1 : 0,
    };
    return AudioUnitSetProperty(
        unit,
        kAudioOutputUnitProperty_ChannelMap,
        kAudioUnitScope_Output,
        1,
        channelMap,
        sizeof(channelMap)
    );
}

static OSStatus EMKEHALInputCallback(
    void *inRefCon,
    AudioUnitRenderActionFlags *ioActionFlags,
    const AudioTimeStamp *inTimeStamp,
    UInt32 inBusNumber,
    UInt32 inNumberFrames,
    AudioBufferList *ioData
) {
    (void)inBusNumber;
    (void)ioData;
    EMKEHALInput *input = inRefCon;
    if (input == NULL || input->scratch == NULL) {
        return kAudioUnitErr_Uninitialized;
    }
    atomic_fetch_add_explicit(
        &input->callbackCount,
        1,
        memory_order_relaxed
    );
    atomic_store_explicit(
        &input->lastCallbackFrameCount,
        inNumberFrames,
        memory_order_relaxed
    );
    if (inNumberFrames > input->scratchCapacityFrames) {
        atomic_fetch_add_explicit(
            &input->oversizedCallbackCount,
            1,
            memory_order_relaxed
        );
        atomic_store_explicit(
            &input->lastRenderStatus,
            kAudioUnitErr_TooManyFramesToProcess,
            memory_order_relaxed
        );
        return kAudioUnitErr_TooManyFramesToProcess;
    }

    AudioBufferList bufferList = {0};
    bufferList.mNumberBuffers = 1;
    bufferList.mBuffers[0].mNumberChannels = kEMKEHALChannelCount;
    bufferList.mBuffers[0].mDataByteSize =
        inNumberFrames * kEMKEHALChannelCount * sizeof(float);
    bufferList.mBuffers[0].mData = input->scratch;
    const OSStatus status = AudioUnitRender(
        input->unit,
        ioActionFlags,
        inTimeStamp,
        1,
        inNumberFrames,
        &bufferList
    );
    if (status != noErr) {
        atomic_fetch_add_explicit(
            &input->renderErrorCount,
            1,
            memory_order_relaxed
        );
        atomic_store_explicit(
            &input->lastRenderStatus,
            status,
            memory_order_relaxed
        );
        return status;
    }

    atomic_store_explicit(
        &input->lastRenderStatus,
        noErr,
        memory_order_relaxed
    );
    atomic_fetch_add_explicit(
        &input->renderedFrameCount,
        inNumberFrames,
        memory_order_relaxed
    );

    const uint32_t writtenFrames = EMKEAudioRingBufferWrite(
        input->buffer,
        input->scratch,
        inNumberFrames
    );
    atomic_fetch_add_explicit(
        &input->writtenFrameCount,
        writtenFrames,
        memory_order_relaxed
    );
    return noErr;
}

static void EMKEHALInputResetDiagnostics(EMKEHALInput *input) {
    atomic_store_explicit(&input->callbackCount, 0, memory_order_relaxed);
    atomic_store_explicit(
        &input->lastCallbackFrameCount,
        0,
        memory_order_relaxed
    );
    atomic_store_explicit(&input->renderedFrameCount, 0, memory_order_relaxed);
    atomic_store_explicit(&input->writtenFrameCount, 0, memory_order_relaxed);
    atomic_store_explicit(&input->renderErrorCount, 0, memory_order_relaxed);
    atomic_store_explicit(
        &input->oversizedCallbackCount,
        0,
        memory_order_relaxed
    );
    atomic_store_explicit(&input->lastRenderStatus, noErr, memory_order_relaxed);
}

static void EMKEHALZeroAudioBufferList(AudioBufferList *bufferList) {
    if (bufferList == NULL) {
        return;
    }
    for (UInt32 index = 0; index < bufferList->mNumberBuffers; index++) {
        AudioBuffer *buffer = &bufferList->mBuffers[index];
        if (buffer->mData != NULL && buffer->mDataByteSize > 0) {
            memset(buffer->mData, 0, buffer->mDataByteSize);
        }
    }
}

static OSStatus EMKEHALOutputCallback(
    void *inRefCon,
    AudioUnitRenderActionFlags *ioActionFlags,
    const AudioTimeStamp *inTimeStamp,
    UInt32 inBusNumber,
    UInt32 inNumberFrames,
    AudioBufferList *ioData
) {
    (void)ioActionFlags;
    (void)inTimeStamp;
    (void)inBusNumber;
    EMKEHALOutput *output = inRefCon;
    if (output == NULL || ioData == NULL || ioData->mNumberBuffers != 1) {
        EMKEHALZeroAudioBufferList(ioData);
        return noErr;
    }

    AudioBuffer *buffer = &ioData->mBuffers[0];
    if (buffer->mData == NULL) {
        return noErr;
    }
    const uint32_t bufferCapacityFrames = buffer->mDataByteSize /
        (kEMKEHALChannelCount * sizeof(float));
    const uint32_t requestedFrames = inNumberFrames < bufferCapacityFrames
        ? inNumberFrames
        : bufferCapacityFrames;
    const uint32_t transferred = EMKEAudioRingBufferRead(
        output->buffer,
        buffer->mData,
        requestedFrames
    );
    if (transferred < requestedFrames) {
        memset(
            (float *)buffer->mData
                + (size_t)transferred * kEMKEHALChannelCount,
            0,
            (size_t)(requestedFrames - transferred)
                * kEMKEHALChannelCount * sizeof(float)
        );
    }
    return noErr;
}

static void EMKEHALDisposeUnit(AudioUnit unit) {
    if (unit == NULL) {
        return;
    }
    AudioUnitUninitialize(unit);
    AudioComponentInstanceDispose(unit);
}

OSStatus EMKEHALInputCreate(
    AudioObjectID deviceID,
    uint32_t capacityFrames,
    EMKEHALInput **outInput
) {
    if (outInput == NULL) {
        return kAudioUnitErr_InvalidPropertyValue;
    }
    *outInput = NULL;
    if (deviceID == kAudioObjectUnknown || capacityFrames == 0) {
        return kAudioUnitErr_InvalidPropertyValue;
    }

    EMKEHALInput *input = calloc(1, sizeof(*input));
    if (input == NULL) {
        return kAudio_MemFullError;
    }
    input->buffer = EMKEAudioRingBufferCreate(
        capacityFrames,
        kEMKEHALChannelCount
    );
    if (input->buffer == NULL) {
        free(input);
        return kAudio_MemFullError;
    }
    atomic_init(&input->started, false);
    atomic_init(&input->callbackCount, 0);
    atomic_init(&input->lastCallbackFrameCount, 0);
    atomic_init(&input->renderedFrameCount, 0);
    atomic_init(&input->writtenFrameCount, 0);
    atomic_init(&input->renderErrorCount, 0);
    atomic_init(&input->oversizedCallbackCount, 0);
    atomic_init(&input->lastRenderStatus, noErr);

    OSStatus status = EMKEHALCreateUnit(&input->unit);
    if (status == noErr) {
        status = EMKEHALSetEnabledIO(
            input->unit,
            kAudioUnitScope_Input,
            1,
            1
        );
    }
    if (status == noErr) {
        status = EMKEHALSetEnabledIO(
            input->unit,
            kAudioUnitScope_Output,
            0,
            0
        );
    }
    if (status == noErr) {
        status = EMKEHALSetDevice(input->unit, deviceID);
    }
    if (status == noErr) {
        AudioStreamBasicDescription deviceFormat = {0};
        status = EMKEHALGetFormat(
            input->unit,
            kAudioUnitScope_Input,
            1,
            &deviceFormat
        );
        if (status == noErr && deviceFormat.mSampleRate <= 0) {
            status = kAudioUnitErr_InvalidPropertyValue;
        }
        if (status == noErr) {
            input->clientSampleRate = deviceFormat.mSampleRate;
        }
    }
    if (status == noErr) {
        status = EMKEHALSetFormat(
            input->unit,
            kAudioUnitScope_Output,
            1,
            input->clientSampleRate
        );
    }
    if (status == noErr) {
        AudioStreamBasicDescription deviceFormat = {0};
        status = EMKEHALGetFormat(
            input->unit,
            kAudioUnitScope_Input,
            1,
            &deviceFormat
        );
        if (status == noErr) {
            status = EMKEHALSetInputChannelMap(
                input->unit,
                deviceFormat.mChannelsPerFrame
            );
        }
    }
    if (status == noErr) {
        AURenderCallbackStruct callback = {
            .inputProc = EMKEHALInputCallback,
            .inputProcRefCon = input,
        };
        status = AudioUnitSetProperty(
            input->unit,
            kAudioOutputUnitProperty_SetInputCallback,
            kAudioUnitScope_Global,
            0,
            &callback,
            sizeof(callback)
        );
    }
    if (status == noErr) {
        status = AudioUnitInitialize(input->unit);
    }

    UInt32 maximumFrames = 0;
    UInt32 maximumFramesSize = sizeof(maximumFrames);
    if (status == noErr) {
        status = AudioUnitGetProperty(
            input->unit,
            kAudioUnitProperty_MaximumFramesPerSlice,
            kAudioUnitScope_Global,
            0,
            &maximumFrames,
            &maximumFramesSize
        );
    }
    if (status == noErr && maximumFrames == 0) {
        status = kAudioUnitErr_InvalidPropertyValue;
    }
    if (status == noErr) {
        input->scratch = calloc(
            (size_t)maximumFrames * kEMKEHALChannelCount,
            sizeof(float)
        );
        if (input->scratch == NULL) {
            status = kAudio_MemFullError;
        } else {
            input->scratchCapacityFrames = maximumFrames;
        }
    }

    if (status != noErr) {
        free(input->scratch);
        EMKEHALDisposeUnit(input->unit);
        EMKEAudioRingBufferDestroy(input->buffer);
        free(input);
        return status;
    }

    *outInput = input;
    return noErr;
}

OSStatus EMKEHALInputStart(EMKEHALInput *input) {
    if (input == NULL) {
        return kAudioUnitErr_InvalidPropertyValue;
    }
    if (atomic_load_explicit(&input->started, memory_order_acquire)) {
        return noErr;
    }
    EMKEAudioRingBufferReset(input->buffer);
    EMKEHALInputResetDiagnostics(input);
    const OSStatus status = AudioOutputUnitStart(input->unit);
    if (status == noErr) {
        atomic_store_explicit(&input->started, true, memory_order_release);
    }
    return status;
}

OSStatus EMKEHALInputStop(EMKEHALInput *input) {
    if (input == NULL) {
        return kAudioUnitErr_InvalidPropertyValue;
    }
    OSStatus status = noErr;
    if (atomic_load_explicit(&input->started, memory_order_acquire)) {
        status = AudioOutputUnitStop(input->unit);
        if (status == noErr) {
            atomic_store_explicit(
                &input->started,
                false,
                memory_order_release
            );
        }
    }
    EMKEAudioRingBufferReset(input->buffer);
    return status;
}

uint32_t EMKEHALInputRead(
    EMKEHALInput *input,
    float *interleavedFrames,
    uint32_t frameCount
) {
    return input == NULL
        ? 0
        : EMKEAudioRingBufferRead(
            input->buffer,
            interleavedFrames,
            frameCount
        );
}

uint32_t EMKEHALInputReadableFrames(const EMKEHALInput *input) {
    return input == NULL
        ? 0
        : EMKEAudioRingBufferReadableFrames(input->buffer);
}

void EMKEHALInputGetDiagnostics(
    const EMKEHALInput *input,
    EMKEHALInputDiagnostics *outDiagnostics
) {
    if (outDiagnostics == NULL) {
        return;
    }
    memset(outDiagnostics, 0, sizeof(*outDiagnostics));
    if (input == NULL) {
        return;
    }
    outDiagnostics->isStarted = atomic_load_explicit(
        &input->started,
        memory_order_acquire
    );
    outDiagnostics->callbackCount = atomic_load_explicit(
        &input->callbackCount,
        memory_order_relaxed
    );
    outDiagnostics->lastCallbackFrameCount = atomic_load_explicit(
        &input->lastCallbackFrameCount,
        memory_order_relaxed
    );
    outDiagnostics->renderedFrameCount = atomic_load_explicit(
        &input->renderedFrameCount,
        memory_order_relaxed
    );
    outDiagnostics->writtenFrameCount = atomic_load_explicit(
        &input->writtenFrameCount,
        memory_order_relaxed
    );
    outDiagnostics->renderErrorCount = atomic_load_explicit(
        &input->renderErrorCount,
        memory_order_relaxed
    );
    outDiagnostics->oversizedCallbackCount = atomic_load_explicit(
        &input->oversizedCallbackCount,
        memory_order_relaxed
    );
    outDiagnostics->lastRenderStatus = atomic_load_explicit(
        &input->lastRenderStatus,
        memory_order_relaxed
    );
    outDiagnostics->scratchCapacityFrames = input->scratchCapacityFrames;
    outDiagnostics->clientSampleRate = input->clientSampleRate;
}

void EMKEHALInputDestroy(EMKEHALInput *input) {
    if (input == NULL) {
        return;
    }
    EMKEHALInputStop(input);
    EMKEHALDisposeUnit(input->unit);
    EMKEAudioRingBufferDestroy(input->buffer);
    free(input->scratch);
    free(input);
}

OSStatus EMKEHALOutputCreate(
    AudioObjectID deviceID,
    uint32_t capacityFrames,
    EMKEHALOutput **outOutput
) {
    if (outOutput == NULL) {
        return kAudioUnitErr_InvalidPropertyValue;
    }
    *outOutput = NULL;
    if (deviceID == kAudioObjectUnknown || capacityFrames == 0) {
        return kAudioUnitErr_InvalidPropertyValue;
    }

    EMKEHALOutput *output = calloc(1, sizeof(*output));
    if (output == NULL) {
        return kAudio_MemFullError;
    }
    output->buffer = EMKEAudioRingBufferCreate(
        capacityFrames,
        kEMKEHALChannelCount
    );
    if (output->buffer == NULL) {
        free(output);
        return kAudio_MemFullError;
    }
    atomic_init(&output->started, false);

    OSStatus status = EMKEHALCreateUnit(&output->unit);
    if (status == noErr) {
        status = EMKEHALSetEnabledIO(
            output->unit,
            kAudioUnitScope_Output,
            0,
            1
        );
    }
    if (status == noErr) {
        status = EMKEHALSetEnabledIO(
            output->unit,
            kAudioUnitScope_Input,
            1,
            0
        );
    }
    if (status == noErr) {
        status = EMKEHALSetDevice(output->unit, deviceID);
    }
    if (status == noErr) {
        status = EMKEHALSetFormat(
            output->unit,
            kAudioUnitScope_Input,
            0,
            48000.0
        );
    }
    if (status == noErr) {
        AURenderCallbackStruct callback = {
            .inputProc = EMKEHALOutputCallback,
            .inputProcRefCon = output,
        };
        status = AudioUnitSetProperty(
            output->unit,
            kAudioUnitProperty_SetRenderCallback,
            kAudioUnitScope_Input,
            0,
            &callback,
            sizeof(callback)
        );
    }
    if (status == noErr) {
        status = AudioUnitInitialize(output->unit);
    }
    if (status != noErr) {
        EMKEHALDisposeUnit(output->unit);
        EMKEAudioRingBufferDestroy(output->buffer);
        free(output);
        return status;
    }

    *outOutput = output;
    return noErr;
}

OSStatus EMKEHALOutputStart(EMKEHALOutput *output) {
    if (output == NULL) {
        return kAudioUnitErr_InvalidPropertyValue;
    }
    if (atomic_load_explicit(&output->started, memory_order_acquire)) {
        return noErr;
    }
    EMKEAudioRingBufferReset(output->buffer);
    const OSStatus status = AudioOutputUnitStart(output->unit);
    if (status == noErr) {
        atomic_store_explicit(&output->started, true, memory_order_release);
    }
    return status;
}

OSStatus EMKEHALOutputStop(EMKEHALOutput *output) {
    if (output == NULL) {
        return kAudioUnitErr_InvalidPropertyValue;
    }
    OSStatus status = noErr;
    if (atomic_load_explicit(&output->started, memory_order_acquire)) {
        status = AudioOutputUnitStop(output->unit);
        if (status == noErr) {
            atomic_store_explicit(
                &output->started,
                false,
                memory_order_release
            );
        }
    }
    EMKEAudioRingBufferReset(output->buffer);
    return status;
}

uint32_t EMKEHALOutputWrite(
    EMKEHALOutput *output,
    const float *interleavedFrames,
    uint32_t frameCount
) {
    return output == NULL
        ? 0
        : EMKEAudioRingBufferWrite(
            output->buffer,
            interleavedFrames,
            frameCount
        );
}

uint32_t EMKEHALOutputQueuedFrames(const EMKEHALOutput *output) {
    return output == NULL
        ? 0
        : EMKEAudioRingBufferReadableFrames(output->buffer);
}

void EMKEHALOutputDestroy(EMKEHALOutput *output) {
    if (output == NULL) {
        return;
    }
    EMKEHALOutputStop(output);
    EMKEHALDisposeUnit(output->unit);
    EMKEAudioRingBufferDestroy(output->buffer);
    free(output);
}
