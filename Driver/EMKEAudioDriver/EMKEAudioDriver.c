/*
 * Copyright (c) 2026 EMKE Translation contributors.
 * Portions follow the interface structure of Apple's "Creating an Audio
 * Server Driver Plug-in" sample. See LICENSE-Apple-Sample.txt.
 */

#include <CoreAudio/AudioServerPlugIn.h>
#include <CoreAudio/HostTime.h>
#include <CoreFoundation/CoreFoundation.h>

#include <stdatomic.h>
#include <stddef.h>
#include <stdint.h>
#include <string.h>

#include "EMKEAudioBridge.h"

enum {
    kEMKEChannelCount = 2,
    kEMKEZeroTimeStampPeriod = 512,
    kEMKERouteCapacityFrames = 96000,
};

static const Float64 kEMKESampleRate = 48000.0;

static HRESULT EMKEQueryInterface(void *inDriver, REFIID inUUID, LPVOID *outInterface);
static ULONG EMKEAddRef(void *inDriver);
static ULONG EMKERelease(void *inDriver);
static OSStatus EMKEInitialize(AudioServerPlugInDriverRef inDriver, AudioServerPlugInHostRef inHost);
static OSStatus EMKECreateDevice(AudioServerPlugInDriverRef inDriver, CFDictionaryRef inDescription, const AudioServerPlugInClientInfo *inClientInfo, AudioObjectID *outDeviceObjectID);
static OSStatus EMKEDestroyDevice(AudioServerPlugInDriverRef inDriver, AudioObjectID inDeviceObjectID);
static OSStatus EMKEAddDeviceClient(AudioServerPlugInDriverRef inDriver, AudioObjectID inDeviceObjectID, const AudioServerPlugInClientInfo *inClientInfo);
static OSStatus EMKERemoveDeviceClient(AudioServerPlugInDriverRef inDriver, AudioObjectID inDeviceObjectID, const AudioServerPlugInClientInfo *inClientInfo);
static OSStatus EMKEPerformDeviceConfigurationChange(AudioServerPlugInDriverRef inDriver, AudioObjectID inDeviceObjectID, UInt64 inChangeAction, void *inChangeInfo);
static OSStatus EMKEAbortDeviceConfigurationChange(AudioServerPlugInDriverRef inDriver, AudioObjectID inDeviceObjectID, UInt64 inChangeAction, void *inChangeInfo);
static Boolean EMKEHasProperty(AudioServerPlugInDriverRef inDriver, AudioObjectID inObjectID, pid_t inClientProcessID, const AudioObjectPropertyAddress *inAddress);
static OSStatus EMKEIsPropertySettable(AudioServerPlugInDriverRef inDriver, AudioObjectID inObjectID, pid_t inClientProcessID, const AudioObjectPropertyAddress *inAddress, Boolean *outIsSettable);
static OSStatus EMKEGetPropertyDataSize(AudioServerPlugInDriverRef inDriver, AudioObjectID inObjectID, pid_t inClientProcessID, const AudioObjectPropertyAddress *inAddress, UInt32 inQualifierDataSize, const void *inQualifierData, UInt32 *outDataSize);
static OSStatus EMKEGetPropertyData(AudioServerPlugInDriverRef inDriver, AudioObjectID inObjectID, pid_t inClientProcessID, const AudioObjectPropertyAddress *inAddress, UInt32 inQualifierDataSize, const void *inQualifierData, UInt32 inDataSize, UInt32 *outDataSize, void *outData);
static OSStatus EMKESetPropertyData(AudioServerPlugInDriverRef inDriver, AudioObjectID inObjectID, pid_t inClientProcessID, const AudioObjectPropertyAddress *inAddress, UInt32 inQualifierDataSize, const void *inQualifierData, UInt32 inDataSize, const void *inData);
static OSStatus EMKEStartIO(AudioServerPlugInDriverRef inDriver, AudioObjectID inDeviceObjectID, UInt32 inClientID);
static OSStatus EMKEStopIO(AudioServerPlugInDriverRef inDriver, AudioObjectID inDeviceObjectID, UInt32 inClientID);
static OSStatus EMKEGetZeroTimeStamp(AudioServerPlugInDriverRef inDriver, AudioObjectID inDeviceObjectID, UInt32 inClientID, Float64 *outSampleTime, UInt64 *outHostTime, UInt64 *outSeed);
static OSStatus EMKEWillDoIOOperation(AudioServerPlugInDriverRef inDriver, AudioObjectID inDeviceObjectID, UInt32 inClientID, UInt32 inOperationID, Boolean *outWillDo, Boolean *outWillDoInPlace);
static OSStatus EMKEBeginIOOperation(AudioServerPlugInDriverRef inDriver, AudioObjectID inDeviceObjectID, UInt32 inClientID, UInt32 inOperationID, UInt32 inIOBufferFrameSize, const AudioServerPlugInIOCycleInfo *inIOCycleInfo);
static OSStatus EMKEDoIOOperation(AudioServerPlugInDriverRef inDriver, AudioObjectID inDeviceObjectID, AudioObjectID inStreamObjectID, UInt32 inClientID, UInt32 inOperationID, UInt32 inIOBufferFrameSize, const AudioServerPlugInIOCycleInfo *inIOCycleInfo, void *ioMainBuffer, void *ioSecondaryBuffer);
static OSStatus EMKEEndIOOperation(AudioServerPlugInDriverRef inDriver, AudioObjectID inDeviceObjectID, UInt32 inClientID, UInt32 inOperationID, UInt32 inIOBufferFrameSize, const AudioServerPlugInIOCycleInfo *inIOCycleInfo);

static AudioServerPlugInDriverInterface gEMKEDriverInterface = {
    NULL,
    EMKEQueryInterface,
    EMKEAddRef,
    EMKERelease,
    EMKEInitialize,
    EMKECreateDevice,
    EMKEDestroyDevice,
    EMKEAddDeviceClient,
    EMKERemoveDeviceClient,
    EMKEPerformDeviceConfigurationChange,
    EMKEAbortDeviceConfigurationChange,
    EMKEHasProperty,
    EMKEIsPropertySettable,
    EMKEGetPropertyDataSize,
    EMKEGetPropertyData,
    EMKESetPropertyData,
    EMKEStartIO,
    EMKEStopIO,
    EMKEGetZeroTimeStamp,
    EMKEWillDoIOOperation,
    EMKEBeginIOOperation,
    EMKEDoIOOperation,
    EMKEEndIOOperation,
};

static AudioServerPlugInDriverInterface *gEMKEDriverInterfacePointer = &gEMKEDriverInterface;
static AudioServerPlugInDriverRef gEMKEDriverRef = &gEMKEDriverInterfacePointer;
static AudioServerPlugInHostRef gEMKEHost = NULL;
static EMKEAudioRoutes *gEMKERoutes = NULL;
static _Atomic UInt32 gEMKEReferenceCount = 1;
static _Atomic UInt32 gEMKESpeakerRunningClients = 0;
static _Atomic UInt32 gEMKEMicrophoneRunningClients = 0;
static _Atomic UInt64 gEMKESpeakerClockSeed = 1;
static _Atomic UInt64 gEMKEMicrophoneClockSeed = 1;
static _Atomic UInt64 gEMKESpeakerAnchorHostTime = 0;
static _Atomic UInt64 gEMKEMicrophoneAnchorHostTime = 0;

static Boolean EMKEIsDriver(AudioServerPlugInDriverRef inDriver) {
    return inDriver == gEMKEDriverRef;
}

static Boolean EMKEIsDevice(AudioObjectID objectID) {
    return objectID == EMKEAudioObjectIDSpeakerDevice ||
        objectID == EMKEAudioObjectIDMicrophoneDevice;
}

static Boolean EMKEStreamBelongsToDevice(AudioObjectID streamID, AudioObjectID deviceID) {
    const EMKEAudioObjectDescriptor *descriptor = EMKEAudioDriverObjectForID(streamID);
    return descriptor != NULL && descriptor->kind == EMKEAudioObjectKindStream &&
        descriptor->ownerObjectID == deviceID;
}

static AudioClassID EMKEClassForObject(const EMKEAudioObjectDescriptor *descriptor) {
    switch (descriptor->kind) {
        case EMKEAudioObjectKindPlugin:
            return kAudioPlugInClassID;
        case EMKEAudioObjectKindDevice:
            return kAudioDeviceClassID;
        case EMKEAudioObjectKindStream:
            return kAudioStreamClassID;
    }
    return kAudioObjectClassID;
}

static CFStringRef EMKENameForObject(AudioObjectID objectID) {
    switch (objectID) {
        case EMKEAudioObjectIDPlugin:
            return CFSTR("EMKE Audio Driver");
        case EMKEAudioObjectIDSpeakerDevice:
            return CFSTR("EMKE Virtual Speaker");
        case EMKEAudioObjectIDSpeakerInputStream:
            return CFSTR("EMKE Speaker App Capture");
        case EMKEAudioObjectIDSpeakerOutputStream:
            return CFSTR("EMKE Speaker Meeting Output");
        case EMKEAudioObjectIDMicrophoneDevice:
            return CFSTR("EMKE Virtual Microphone");
        case EMKEAudioObjectIDMicrophoneInputStream:
            return CFSTR("EMKE Microphone Meeting Input");
        case EMKEAudioObjectIDMicrophoneOutputStream:
            return CFSTR("EMKE Microphone App Translation");
        default:
            return CFSTR("EMKE Audio Object");
    }
}

static CFStringRef EMKEUIDForDevice(AudioObjectID objectID) {
    return objectID == EMKEAudioObjectIDSpeakerDevice
        ? CFSTR("com.emke.translation.virtual-speaker")
        : CFSTR("com.emke.translation.virtual-microphone");
}

static AudioStreamBasicDescription EMKEStreamFormat(void) {
    AudioStreamBasicDescription format = {0};
    format.mSampleRate = kEMKESampleRate;
    format.mFormatID = kAudioFormatLinearPCM;
    format.mFormatFlags = kAudioFormatFlagIsFloat |
        kAudioFormatFlagsNativeEndian |
        kAudioFormatFlagIsPacked;
    format.mBytesPerPacket = kEMKEChannelCount * sizeof(Float32);
    format.mFramesPerPacket = 1;
    format.mBytesPerFrame = kEMKEChannelCount * sizeof(Float32);
    format.mChannelsPerFrame = kEMKEChannelCount;
    format.mBitsPerChannel = 8 * sizeof(Float32);
    return format;
}

static UInt32 EMKEOwnedObjectCount(
    const EMKEAudioObjectDescriptor *descriptor,
    AudioObjectPropertyScope scope
) {
    if (descriptor->kind == EMKEAudioObjectKindPlugin) {
        return 2;
    }
    if (descriptor->kind == EMKEAudioObjectKindDevice) {
        return scope == kAudioObjectPropertyScopeGlobal ? 2 : 1;
    }
    return 0;
}

static UInt32 EMKECopyOwnedObjects(
    const EMKEAudioObjectDescriptor *descriptor,
    AudioObjectPropertyScope scope,
    UInt32 capacity,
    AudioObjectID *output
) {
    AudioObjectID values[2] = {kAudioObjectUnknown, kAudioObjectUnknown};
    UInt32 count = 0;

    if (descriptor->kind == EMKEAudioObjectKindPlugin) {
        values[0] = EMKEAudioObjectIDSpeakerDevice;
        values[1] = EMKEAudioObjectIDMicrophoneDevice;
        count = 2;
    } else if (descriptor->kind == EMKEAudioObjectKindDevice) {
        const Boolean isSpeaker = descriptor->objectID == EMKEAudioObjectIDSpeakerDevice;
        const AudioObjectID inputID = isSpeaker
            ? EMKEAudioObjectIDSpeakerInputStream
            : EMKEAudioObjectIDMicrophoneInputStream;
        const AudioObjectID outputID = isSpeaker
            ? EMKEAudioObjectIDSpeakerOutputStream
            : EMKEAudioObjectIDMicrophoneOutputStream;
        if (scope == kAudioObjectPropertyScopeInput) {
            values[0] = inputID;
            count = 1;
        } else if (scope == kAudioObjectPropertyScopeOutput) {
            values[0] = outputID;
            count = 1;
        } else {
            values[0] = inputID;
            values[1] = outputID;
            count = 2;
        }
    }

    if (count > capacity) {
        count = capacity;
    }
    if (count > 0) {
        memcpy(output, values, count * sizeof(AudioObjectID));
    }
    return count;
}

static OSStatus EMKEWriteScalar(
    const void *value,
    UInt32 valueSize,
    UInt32 inDataSize,
    UInt32 *outDataSize,
    void *outData
) {
    if (inDataSize < valueSize) {
        return kAudioHardwareBadPropertySizeError;
    }
    memcpy(outData, value, valueSize);
    *outDataSize = valueSize;
    return noErr;
}

static OSStatus EMKEWriteCFString(
    CFStringRef value,
    UInt32 inDataSize,
    UInt32 *outDataSize,
    void *outData
) {
    if (inDataSize < sizeof(CFStringRef)) {
        return kAudioHardwareBadPropertySizeError;
    }
    CFRetain(value);
    *((CFStringRef *)outData) = value;
    *outDataSize = sizeof(CFStringRef);
    return noErr;
}

void *EMKEAudioDriver_Create(CFAllocatorRef inAllocator, CFUUIDRef inRequestedTypeUUID) {
    (void)inAllocator;
    if (inRequestedTypeUUID != NULL &&
        CFEqual(inRequestedTypeUUID, kAudioServerPlugInTypeUUID)) {
        return gEMKEDriverRef;
    }
    return NULL;
}

static HRESULT EMKEQueryInterface(void *inDriver, REFIID inUUID, LPVOID *outInterface) {
    if (inDriver != gEMKEDriverRef) {
        return kAudioHardwareBadObjectError;
    }
    if (outInterface == NULL) {
        return kAudioHardwareIllegalOperationError;
    }

    CFUUIDRef requestedUUID = CFUUIDCreateFromUUIDBytes(NULL, inUUID);
    if (requestedUUID == NULL) {
        return kAudioHardwareIllegalOperationError;
    }

    HRESULT result = E_NOINTERFACE;
    if (CFEqual(requestedUUID, IUnknownUUID) ||
        CFEqual(requestedUUID, kAudioServerPlugInDriverInterfaceUUID)) {
        atomic_fetch_add_explicit(&gEMKEReferenceCount, 1, memory_order_relaxed);
        *outInterface = gEMKEDriverRef;
        result = S_OK;
    }
    CFRelease(requestedUUID);
    return result;
}

static ULONG EMKEAddRef(void *inDriver) {
    if (inDriver != gEMKEDriverRef) {
        return 0;
    }
    return atomic_fetch_add_explicit(
        &gEMKEReferenceCount,
        1,
        memory_order_relaxed
    ) + 1;
}

static ULONG EMKERelease(void *inDriver) {
    if (inDriver != gEMKEDriverRef) {
        return 0;
    }

    UInt32 current = atomic_load_explicit(&gEMKEReferenceCount, memory_order_relaxed);
    while (current > 0 && !atomic_compare_exchange_weak_explicit(
        &gEMKEReferenceCount,
        &current,
        current - 1,
        memory_order_relaxed,
        memory_order_relaxed
    )) {
    }
    return current > 0 ? current - 1 : 0;
}

static OSStatus EMKEInitialize(
    AudioServerPlugInDriverRef inDriver,
    AudioServerPlugInHostRef inHost
) {
    if (!EMKEIsDriver(inDriver)) {
        return kAudioHardwareBadObjectError;
    }
    if (gEMKERoutes == NULL) {
        gEMKERoutes = EMKEAudioRoutesCreate(kEMKERouteCapacityFrames, kEMKEChannelCount);
        if (gEMKERoutes == NULL) {
            return kAudioHardwareUnspecifiedError;
        }
    }
    gEMKEHost = inHost;
    const UInt64 anchorHostTime = AudioGetCurrentHostTime();
    atomic_store_explicit(
        &gEMKESpeakerAnchorHostTime,
        anchorHostTime,
        memory_order_relaxed
    );
    atomic_store_explicit(
        &gEMKEMicrophoneAnchorHostTime,
        anchorHostTime,
        memory_order_relaxed
    );
    return noErr;
}

static OSStatus EMKECreateDevice(
    AudioServerPlugInDriverRef inDriver,
    CFDictionaryRef inDescription,
    const AudioServerPlugInClientInfo *inClientInfo,
    AudioObjectID *outDeviceObjectID
) {
    (void)inDescription;
    (void)inClientInfo;
    (void)outDeviceObjectID;
    return EMKEIsDriver(inDriver)
        ? kAudioHardwareUnsupportedOperationError
        : kAudioHardwareBadObjectError;
}

static OSStatus EMKEDestroyDevice(
    AudioServerPlugInDriverRef inDriver,
    AudioObjectID inDeviceObjectID
) {
    (void)inDeviceObjectID;
    return EMKEIsDriver(inDriver)
        ? kAudioHardwareUnsupportedOperationError
        : kAudioHardwareBadObjectError;
}

static OSStatus EMKEAddDeviceClient(
    AudioServerPlugInDriverRef inDriver,
    AudioObjectID inDeviceObjectID,
    const AudioServerPlugInClientInfo *inClientInfo
) {
    (void)inClientInfo;
    return EMKEIsDriver(inDriver) && EMKEIsDevice(inDeviceObjectID)
        ? noErr
        : kAudioHardwareBadObjectError;
}

static OSStatus EMKERemoveDeviceClient(
    AudioServerPlugInDriverRef inDriver,
    AudioObjectID inDeviceObjectID,
    const AudioServerPlugInClientInfo *inClientInfo
) {
    (void)inClientInfo;
    return EMKEIsDriver(inDriver) && EMKEIsDevice(inDeviceObjectID)
        ? noErr
        : kAudioHardwareBadObjectError;
}

static OSStatus EMKEPerformDeviceConfigurationChange(
    AudioServerPlugInDriverRef inDriver,
    AudioObjectID inDeviceObjectID,
    UInt64 inChangeAction,
    void *inChangeInfo
) {
    (void)inChangeAction;
    (void)inChangeInfo;
    return EMKEIsDriver(inDriver) && EMKEIsDevice(inDeviceObjectID)
        ? noErr
        : kAudioHardwareBadObjectError;
}

static OSStatus EMKEAbortDeviceConfigurationChange(
    AudioServerPlugInDriverRef inDriver,
    AudioObjectID inDeviceObjectID,
    UInt64 inChangeAction,
    void *inChangeInfo
) {
    (void)inChangeAction;
    (void)inChangeInfo;
    return EMKEIsDriver(inDriver) && EMKEIsDevice(inDeviceObjectID)
        ? noErr
        : kAudioHardwareBadObjectError;
}

static Boolean EMKEHasPluginProperty(AudioObjectPropertySelector selector) {
    switch (selector) {
        case kAudioObjectPropertyBaseClass:
        case kAudioObjectPropertyClass:
        case kAudioObjectPropertyOwner:
        case kAudioObjectPropertyName:
        case kAudioObjectPropertyManufacturer:
        case kAudioObjectPropertyOwnedObjects:
        case kAudioPlugInPropertyDeviceList:
        case kAudioPlugInPropertyTranslateUIDToDevice:
        case kAudioPlugInPropertyResourceBundle:
            return true;
        default:
            return false;
    }
}

static Boolean EMKEHasDeviceProperty(
    AudioObjectPropertySelector selector,
    AudioObjectPropertyScope scope
) {
    switch (selector) {
        case kAudioObjectPropertyBaseClass:
        case kAudioObjectPropertyClass:
        case kAudioObjectPropertyOwner:
        case kAudioObjectPropertyName:
        case kAudioObjectPropertyManufacturer:
        case kAudioObjectPropertyOwnedObjects:
        case kAudioDevicePropertyDeviceUID:
        case kAudioDevicePropertyModelUID:
        case kAudioDevicePropertyTransportType:
        case kAudioDevicePropertyRelatedDevices:
        case kAudioDevicePropertyClockDomain:
        case kAudioDevicePropertyDeviceIsAlive:
        case kAudioDevicePropertyDeviceIsRunning:
        case kAudioDevicePropertyNominalSampleRate:
        case kAudioDevicePropertyAvailableNominalSampleRates:
        case kAudioDevicePropertyIsHidden:
        case kAudioDevicePropertyZeroTimeStampPeriod:
        case kAudioDevicePropertyStreams:
        case kAudioObjectPropertyControlList:
            return true;
        case kAudioDevicePropertyDeviceCanBeDefaultDevice:
        case kAudioDevicePropertyDeviceCanBeDefaultSystemDevice:
        case kAudioDevicePropertyLatency:
        case kAudioDevicePropertySafetyOffset:
        case kAudioDevicePropertyPreferredChannelsForStereo:
        case kAudioDevicePropertyPreferredChannelLayout:
            return scope == kAudioObjectPropertyScopeInput ||
                scope == kAudioObjectPropertyScopeOutput;
        default:
            return false;
    }
}

static Boolean EMKEHasStreamProperty(AudioObjectPropertySelector selector) {
    switch (selector) {
        case kAudioObjectPropertyBaseClass:
        case kAudioObjectPropertyClass:
        case kAudioObjectPropertyOwner:
        case kAudioObjectPropertyName:
        case kAudioObjectPropertyOwnedObjects:
        case kAudioStreamPropertyIsActive:
        case kAudioStreamPropertyDirection:
        case kAudioStreamPropertyTerminalType:
        case kAudioStreamPropertyStartingChannel:
        case kAudioStreamPropertyLatency:
        case kAudioStreamPropertyVirtualFormat:
        case kAudioStreamPropertyPhysicalFormat:
        case kAudioStreamPropertyAvailableVirtualFormats:
        case kAudioStreamPropertyAvailablePhysicalFormats:
            return true;
        default:
            return false;
    }
}

static Boolean EMKEHasProperty(
    AudioServerPlugInDriverRef inDriver,
    AudioObjectID inObjectID,
    pid_t inClientProcessID,
    const AudioObjectPropertyAddress *inAddress
) {
    (void)inClientProcessID;
    if (!EMKEIsDriver(inDriver) || inAddress == NULL) {
        return false;
    }
    const EMKEAudioObjectDescriptor *descriptor = EMKEAudioDriverObjectForID(inObjectID);
    if (descriptor == NULL) {
        return false;
    }
    switch (descriptor->kind) {
        case EMKEAudioObjectKindPlugin:
            return EMKEHasPluginProperty(inAddress->mSelector);
        case EMKEAudioObjectKindDevice:
            return EMKEHasDeviceProperty(inAddress->mSelector, inAddress->mScope);
        case EMKEAudioObjectKindStream:
            return EMKEHasStreamProperty(inAddress->mSelector);
    }
    return false;
}

static OSStatus EMKEIsPropertySettable(
    AudioServerPlugInDriverRef inDriver,
    AudioObjectID inObjectID,
    pid_t inClientProcessID,
    const AudioObjectPropertyAddress *inAddress,
    Boolean *outIsSettable
) {
    (void)inClientProcessID;
    if (!EMKEIsDriver(inDriver)) {
        return kAudioHardwareBadObjectError;
    }
    if (inAddress == NULL || outIsSettable == NULL) {
        return kAudioHardwareIllegalOperationError;
    }
    if (!EMKEHasProperty(inDriver, inObjectID, 0, inAddress)) {
        return EMKEAudioDriverObjectForID(inObjectID) == NULL
            ? kAudioHardwareBadObjectError
            : kAudioHardwareUnknownPropertyError;
    }
    *outIsSettable = false;
    return noErr;
}

static OSStatus EMKEGetPropertyDataSize(
    AudioServerPlugInDriverRef inDriver,
    AudioObjectID inObjectID,
    pid_t inClientProcessID,
    const AudioObjectPropertyAddress *inAddress,
    UInt32 inQualifierDataSize,
    const void *inQualifierData,
    UInt32 *outDataSize
) {
    (void)inClientProcessID;
    (void)inQualifierDataSize;
    (void)inQualifierData;
    if (!EMKEIsDriver(inDriver)) {
        return kAudioHardwareBadObjectError;
    }
    if (inAddress == NULL || outDataSize == NULL) {
        return kAudioHardwareIllegalOperationError;
    }
    const EMKEAudioObjectDescriptor *descriptor = EMKEAudioDriverObjectForID(inObjectID);
    if (descriptor == NULL) {
        return kAudioHardwareBadObjectError;
    }
    if (!EMKEHasProperty(inDriver, inObjectID, 0, inAddress)) {
        return kAudioHardwareUnknownPropertyError;
    }

    switch (inAddress->mSelector) {
        case kAudioObjectPropertyBaseClass:
        case kAudioObjectPropertyClass:
            *outDataSize = sizeof(AudioClassID);
            return noErr;
        case kAudioObjectPropertyOwner:
            *outDataSize = sizeof(AudioObjectID);
            return noErr;
        case kAudioObjectPropertyName:
        case kAudioObjectPropertyManufacturer:
        case kAudioPlugInPropertyResourceBundle:
        case kAudioDevicePropertyDeviceUID:
        case kAudioDevicePropertyModelUID:
            *outDataSize = sizeof(CFStringRef);
            return noErr;
        case kAudioObjectPropertyOwnedObjects:
        case kAudioDevicePropertyStreams:
            *outDataSize = EMKEOwnedObjectCount(descriptor, inAddress->mScope) *
                sizeof(AudioObjectID);
            return noErr;
        case kAudioPlugInPropertyDeviceList:
            *outDataSize = 2 * sizeof(AudioObjectID);
            return noErr;
        case kAudioPlugInPropertyTranslateUIDToDevice:
            *outDataSize = sizeof(AudioObjectID);
            return noErr;
        case kAudioDevicePropertyRelatedDevices:
            *outDataSize = sizeof(AudioObjectID);
            return noErr;
        case kAudioDevicePropertyTransportType:
        case kAudioDevicePropertyClockDomain:
        case kAudioDevicePropertyDeviceIsAlive:
        case kAudioDevicePropertyDeviceIsRunning:
        case kAudioDevicePropertyDeviceCanBeDefaultDevice:
        case kAudioDevicePropertyDeviceCanBeDefaultSystemDevice:
        case kAudioDevicePropertyLatency:
        case kAudioDevicePropertySafetyOffset:
        case kAudioDevicePropertyIsHidden:
        case kAudioDevicePropertyZeroTimeStampPeriod:
        case kAudioStreamPropertyIsActive:
        case kAudioStreamPropertyDirection:
        case kAudioStreamPropertyTerminalType:
        case kAudioStreamPropertyStartingChannel:
            *outDataSize = sizeof(UInt32);
            return noErr;
        case kAudioObjectPropertyControlList:
            *outDataSize = 0;
            return noErr;
        case kAudioDevicePropertyNominalSampleRate:
            *outDataSize = sizeof(Float64);
            return noErr;
        case kAudioDevicePropertyAvailableNominalSampleRates:
            *outDataSize = sizeof(AudioValueRange);
            return noErr;
        case kAudioDevicePropertyPreferredChannelsForStereo:
            *outDataSize = 2 * sizeof(UInt32);
            return noErr;
        case kAudioDevicePropertyPreferredChannelLayout:
            *outDataSize = sizeof(AudioChannelLayout);
            return noErr;
        case kAudioStreamPropertyVirtualFormat:
        case kAudioStreamPropertyPhysicalFormat:
            *outDataSize = sizeof(AudioStreamBasicDescription);
            return noErr;
        case kAudioStreamPropertyAvailableVirtualFormats:
        case kAudioStreamPropertyAvailablePhysicalFormats:
            *outDataSize = sizeof(AudioStreamRangedDescription);
            return noErr;
        default:
            return kAudioHardwareUnknownPropertyError;
    }
}

static OSStatus EMKEGetPropertyData(
    AudioServerPlugInDriverRef inDriver,
    AudioObjectID inObjectID,
    pid_t inClientProcessID,
    const AudioObjectPropertyAddress *inAddress,
    UInt32 inQualifierDataSize,
    const void *inQualifierData,
    UInt32 inDataSize,
    UInt32 *outDataSize,
    void *outData
) {
    (void)inClientProcessID;
    if (!EMKEIsDriver(inDriver)) {
        return kAudioHardwareBadObjectError;
    }
    if (inAddress == NULL || outDataSize == NULL || outData == NULL) {
        return kAudioHardwareIllegalOperationError;
    }
    const EMKEAudioObjectDescriptor *descriptor = EMKEAudioDriverObjectForID(inObjectID);
    if (descriptor == NULL) {
        return kAudioHardwareBadObjectError;
    }
    if (!EMKEHasProperty(inDriver, inObjectID, 0, inAddress)) {
        return kAudioHardwareUnknownPropertyError;
    }

    switch (inAddress->mSelector) {
        case kAudioObjectPropertyBaseClass: {
            const AudioClassID value = descriptor->kind == EMKEAudioObjectKindStream
                ? kAudioObjectClassID
                : kAudioObjectClassID;
            return EMKEWriteScalar(&value, sizeof(value), inDataSize, outDataSize, outData);
        }
        case kAudioObjectPropertyClass: {
            const AudioClassID value = EMKEClassForObject(descriptor);
            return EMKEWriteScalar(&value, sizeof(value), inDataSize, outDataSize, outData);
        }
        case kAudioObjectPropertyOwner: {
            const AudioObjectID value = descriptor->ownerObjectID == 0
                ? kAudioObjectUnknown
                : descriptor->ownerObjectID;
            return EMKEWriteScalar(&value, sizeof(value), inDataSize, outDataSize, outData);
        }
        case kAudioObjectPropertyName:
            return EMKEWriteCFString(
                EMKENameForObject(inObjectID),
                inDataSize,
                outDataSize,
                outData
            );
        case kAudioObjectPropertyManufacturer:
            return EMKEWriteCFString(CFSTR("EMKE"), inDataSize, outDataSize, outData);
        case kAudioObjectPropertyOwnedObjects:
        case kAudioDevicePropertyStreams:
        case kAudioPlugInPropertyDeviceList: {
            const UInt32 capacity = inDataSize / sizeof(AudioObjectID);
            const UInt32 count = EMKECopyOwnedObjects(
                descriptor,
                inAddress->mScope,
                capacity,
                (AudioObjectID *)outData
            );
            *outDataSize = count * sizeof(AudioObjectID);
            return noErr;
        }
        case kAudioPlugInPropertyTranslateUIDToDevice: {
            if (inQualifierDataSize != sizeof(CFStringRef) || inQualifierData == NULL) {
                return kAudioHardwareBadPropertySizeError;
            }
            const CFStringRef requestedUID = *((CFStringRef *)inQualifierData);
            AudioObjectID value = kAudioObjectUnknown;
            if (CFEqual(requestedUID, CFSTR("com.emke.translation.virtual-speaker"))) {
                value = EMKEAudioObjectIDSpeakerDevice;
            } else if (CFEqual(requestedUID, CFSTR("com.emke.translation.virtual-microphone"))) {
                value = EMKEAudioObjectIDMicrophoneDevice;
            }
            return EMKEWriteScalar(&value, sizeof(value), inDataSize, outDataSize, outData);
        }
        case kAudioPlugInPropertyResourceBundle:
            return EMKEWriteCFString(CFSTR(""), inDataSize, outDataSize, outData);
        case kAudioDevicePropertyDeviceUID:
            return EMKEWriteCFString(
                EMKEUIDForDevice(inObjectID),
                inDataSize,
                outDataSize,
                outData
            );
        case kAudioDevicePropertyModelUID:
            return EMKEWriteCFString(
                CFSTR("com.emke.translation.virtual-audio"),
                inDataSize,
                outDataSize,
                outData
            );
        case kAudioDevicePropertyRelatedDevices: {
            const AudioObjectID value = inObjectID;
            return EMKEWriteScalar(&value, sizeof(value), inDataSize, outDataSize, outData);
        }
        case kAudioDevicePropertyTransportType: {
            const UInt32 value = kAudioDeviceTransportTypeVirtual;
            return EMKEWriteScalar(&value, sizeof(value), inDataSize, outDataSize, outData);
        }
        case kAudioDevicePropertyClockDomain:
        case kAudioDevicePropertyLatency:
        case kAudioDevicePropertySafetyOffset:
        case kAudioDevicePropertyIsHidden:
        {
            const UInt32 value = 0;
            return EMKEWriteScalar(&value, sizeof(value), inDataSize, outDataSize, outData);
        }
        case kAudioDevicePropertyDeviceIsAlive:
        case kAudioDevicePropertyDeviceCanBeDefaultDevice:
        case kAudioDevicePropertyDeviceCanBeDefaultSystemDevice:
        case kAudioStreamPropertyIsActive: {
            const UInt32 value = 1;
            return EMKEWriteScalar(&value, sizeof(value), inDataSize, outDataSize, outData);
        }
        case kAudioDevicePropertyDeviceIsRunning: {
            const _Atomic UInt32 *counter = inObjectID == EMKEAudioObjectIDSpeakerDevice
                ? &gEMKESpeakerRunningClients
                : &gEMKEMicrophoneRunningClients;
            const UInt32 value = atomic_load_explicit(counter, memory_order_relaxed) > 0;
            return EMKEWriteScalar(&value, sizeof(value), inDataSize, outDataSize, outData);
        }
        case kAudioDevicePropertyNominalSampleRate:
            return EMKEWriteScalar(
                &kEMKESampleRate,
                sizeof(kEMKESampleRate),
                inDataSize,
                outDataSize,
                outData
            );
        case kAudioDevicePropertyAvailableNominalSampleRates: {
            const AudioValueRange value = {kEMKESampleRate, kEMKESampleRate};
            return EMKEWriteScalar(&value, sizeof(value), inDataSize, outDataSize, outData);
        }
        case kAudioDevicePropertyZeroTimeStampPeriod: {
            const UInt32 value = kEMKEZeroTimeStampPeriod;
            return EMKEWriteScalar(&value, sizeof(value), inDataSize, outDataSize, outData);
        }
        case kAudioObjectPropertyControlList:
            *outDataSize = 0;
            return noErr;
        case kAudioDevicePropertyPreferredChannelsForStereo: {
            const UInt32 value[2] = {1, 2};
            return EMKEWriteScalar(&value, sizeof(value), inDataSize, outDataSize, outData);
        }
        case kAudioDevicePropertyPreferredChannelLayout: {
            AudioChannelLayout value = {0};
            value.mChannelLayoutTag = kAudioChannelLayoutTag_Stereo;
            return EMKEWriteScalar(&value, sizeof(value), inDataSize, outDataSize, outData);
        }
        case kAudioStreamPropertyDirection: {
            const UInt32 value = descriptor->direction == EMKEAudioStreamDirectionInput ? 1 : 0;
            return EMKEWriteScalar(&value, sizeof(value), inDataSize, outDataSize, outData);
        }
        case kAudioStreamPropertyTerminalType: {
            const UInt32 value = descriptor->direction == EMKEAudioStreamDirectionInput
                ? kAudioStreamTerminalTypeMicrophone
                : kAudioStreamTerminalTypeSpeaker;
            return EMKEWriteScalar(&value, sizeof(value), inDataSize, outDataSize, outData);
        }
        case kAudioStreamPropertyStartingChannel: {
            const UInt32 value = 1;
            return EMKEWriteScalar(&value, sizeof(value), inDataSize, outDataSize, outData);
        }
        case kAudioStreamPropertyVirtualFormat:
        case kAudioStreamPropertyPhysicalFormat: {
            const AudioStreamBasicDescription value = EMKEStreamFormat();
            return EMKEWriteScalar(&value, sizeof(value), inDataSize, outDataSize, outData);
        }
        case kAudioStreamPropertyAvailableVirtualFormats:
        case kAudioStreamPropertyAvailablePhysicalFormats: {
            AudioStreamRangedDescription value = {0};
            value.mFormat = EMKEStreamFormat();
            value.mSampleRateRange.mMinimum = kEMKESampleRate;
            value.mSampleRateRange.mMaximum = kEMKESampleRate;
            return EMKEWriteScalar(&value, sizeof(value), inDataSize, outDataSize, outData);
        }
        default:
            return kAudioHardwareUnknownPropertyError;
    }
}

static OSStatus EMKESetPropertyData(
    AudioServerPlugInDriverRef inDriver,
    AudioObjectID inObjectID,
    pid_t inClientProcessID,
    const AudioObjectPropertyAddress *inAddress,
    UInt32 inQualifierDataSize,
    const void *inQualifierData,
    UInt32 inDataSize,
    const void *inData
) {
    (void)inClientProcessID;
    (void)inQualifierDataSize;
    (void)inQualifierData;
    (void)inDataSize;
    (void)inData;
    if (!EMKEIsDriver(inDriver) || EMKEAudioDriverObjectForID(inObjectID) == NULL) {
        return kAudioHardwareBadObjectError;
    }
    if (inAddress == NULL) {
        return kAudioHardwareIllegalOperationError;
    }
    return EMKEHasProperty(inDriver, inObjectID, 0, inAddress)
        ? kAudioHardwareUnsupportedOperationError
        : kAudioHardwareUnknownPropertyError;
}

static OSStatus EMKEStartIO(
    AudioServerPlugInDriverRef inDriver,
    AudioObjectID inDeviceObjectID,
    UInt32 inClientID
) {
    (void)inClientID;
    if (!EMKEIsDriver(inDriver) || !EMKEIsDevice(inDeviceObjectID)) {
        return kAudioHardwareBadObjectError;
    }
    if (gEMKERoutes == NULL) {
        return kAudioHardwareNotReadyError;
    }

    _Atomic UInt32 *counter = inDeviceObjectID == EMKEAudioObjectIDSpeakerDevice
        ? &gEMKESpeakerRunningClients
        : &gEMKEMicrophoneRunningClients;
    _Atomic UInt64 *clockSeed = inDeviceObjectID == EMKEAudioObjectIDSpeakerDevice
        ? &gEMKESpeakerClockSeed
        : &gEMKEMicrophoneClockSeed;
    _Atomic UInt64 *anchorHostTime = inDeviceObjectID == EMKEAudioObjectIDSpeakerDevice
        ? &gEMKESpeakerAnchorHostTime
        : &gEMKEMicrophoneAnchorHostTime;
    const UInt32 previous = atomic_fetch_add_explicit(counter, 1, memory_order_relaxed);
    if (previous == 0) {
        if (inDeviceObjectID == EMKEAudioObjectIDSpeakerDevice) {
            EMKEAudioRoutesResetSpeaker(gEMKERoutes);
        } else {
            EMKEAudioRoutesResetMicrophone(gEMKERoutes);
        }
        atomic_fetch_add_explicit(clockSeed, 1, memory_order_relaxed);
        atomic_store_explicit(
            anchorHostTime,
            AudioGetCurrentHostTime(),
            memory_order_relaxed
        );
    }
    return noErr;
}

static OSStatus EMKEStopIO(
    AudioServerPlugInDriverRef inDriver,
    AudioObjectID inDeviceObjectID,
    UInt32 inClientID
) {
    (void)inClientID;
    if (!EMKEIsDriver(inDriver) || !EMKEIsDevice(inDeviceObjectID)) {
        return kAudioHardwareBadObjectError;
    }

    _Atomic UInt32 *counter = inDeviceObjectID == EMKEAudioObjectIDSpeakerDevice
        ? &gEMKESpeakerRunningClients
        : &gEMKEMicrophoneRunningClients;
    UInt32 current = atomic_load_explicit(counter, memory_order_relaxed);
    while (current > 0 && !atomic_compare_exchange_weak_explicit(
        counter,
        &current,
        current - 1,
        memory_order_relaxed,
        memory_order_relaxed
    )) {
    }
    if (current == 1 && gEMKERoutes != NULL) {
        if (inDeviceObjectID == EMKEAudioObjectIDSpeakerDevice) {
            EMKEAudioRoutesResetSpeaker(gEMKERoutes);
        } else {
            EMKEAudioRoutesResetMicrophone(gEMKERoutes);
        }
    }
    return current == 0 ? kAudioHardwareIllegalOperationError : noErr;
}

static OSStatus EMKEGetZeroTimeStamp(
    AudioServerPlugInDriverRef inDriver,
    AudioObjectID inDeviceObjectID,
    UInt32 inClientID,
    Float64 *outSampleTime,
    UInt64 *outHostTime,
    UInt64 *outSeed
) {
    (void)inClientID;
    if (!EMKEIsDriver(inDriver) || !EMKEIsDevice(inDeviceObjectID)) {
        return kAudioHardwareBadObjectError;
    }
    if (outSampleTime == NULL || outHostTime == NULL || outSeed == NULL) {
        return kAudioHardwareIllegalOperationError;
    }

    const _Atomic UInt64 *clockSeed = inDeviceObjectID == EMKEAudioObjectIDSpeakerDevice
        ? &gEMKESpeakerClockSeed
        : &gEMKEMicrophoneClockSeed;
    const _Atomic UInt64 *anchor = inDeviceObjectID == EMKEAudioObjectIDSpeakerDevice
        ? &gEMKESpeakerAnchorHostTime
        : &gEMKEMicrophoneAnchorHostTime;
    const UInt64 anchorHostTime = atomic_load_explicit(anchor, memory_order_relaxed);
    const UInt64 now = AudioGetCurrentHostTime();
    const UInt64 elapsedNanos = AudioConvertHostTimeToNanos(now - anchorHostTime);
    const UInt64 elapsedFrames = (elapsedNanos * 48) / 1000000;
    const UInt64 periodCount = elapsedFrames / kEMKEZeroTimeStampPeriod;
    const UInt64 sampleTime = periodCount * kEMKEZeroTimeStampPeriod;
    const UInt64 sampleNanos = (sampleTime * 1000000000ULL) / 48000ULL;

    *outSampleTime = (Float64)sampleTime;
    *outHostTime = anchorHostTime + AudioConvertNanosToHostTime(sampleNanos);
    *outSeed = atomic_load_explicit(clockSeed, memory_order_relaxed);
    return noErr;
}

static OSStatus EMKEWillDoIOOperation(
    AudioServerPlugInDriverRef inDriver,
    AudioObjectID inDeviceObjectID,
    UInt32 inClientID,
    UInt32 inOperationID,
    Boolean *outWillDo,
    Boolean *outWillDoInPlace
) {
    (void)inClientID;
    if (!EMKEIsDriver(inDriver) || !EMKEIsDevice(inDeviceObjectID)) {
        return kAudioHardwareBadObjectError;
    }
    if (outWillDo == NULL || outWillDoInPlace == NULL) {
        return kAudioHardwareIllegalOperationError;
    }
    *outWillDo = inOperationID == kAudioServerPlugInIOOperationReadInput ||
        inOperationID == kAudioServerPlugInIOOperationWriteMix;
    *outWillDoInPlace = true;
    return noErr;
}

static OSStatus EMKEBeginIOOperation(
    AudioServerPlugInDriverRef inDriver,
    AudioObjectID inDeviceObjectID,
    UInt32 inClientID,
    UInt32 inOperationID,
    UInt32 inIOBufferFrameSize,
    const AudioServerPlugInIOCycleInfo *inIOCycleInfo
) {
    (void)inClientID;
    (void)inOperationID;
    (void)inIOBufferFrameSize;
    (void)inIOCycleInfo;
    return EMKEIsDriver(inDriver) && EMKEIsDevice(inDeviceObjectID)
        ? noErr
        : kAudioHardwareBadObjectError;
}

static OSStatus EMKEDoIOOperation(
    AudioServerPlugInDriverRef inDriver,
    AudioObjectID inDeviceObjectID,
    AudioObjectID inStreamObjectID,
    UInt32 inClientID,
    UInt32 inOperationID,
    UInt32 inIOBufferFrameSize,
    const AudioServerPlugInIOCycleInfo *inIOCycleInfo,
    void *ioMainBuffer,
    void *ioSecondaryBuffer
) {
    (void)inClientID;
    (void)inIOCycleInfo;
    (void)ioSecondaryBuffer;
    if (!EMKEIsDriver(inDriver) || !EMKEIsDevice(inDeviceObjectID) ||
        !EMKEStreamBelongsToDevice(inStreamObjectID, inDeviceObjectID)) {
        return kAudioHardwareBadObjectError;
    }
    if (gEMKERoutes == NULL || ioMainBuffer == NULL) {
        return kAudioHardwareNotReadyError;
    }

    Float32 *frames = (Float32 *)ioMainBuffer;
    if (inOperationID == kAudioServerPlugInIOOperationWriteMix) {
        if (inStreamObjectID == EMKEAudioObjectIDSpeakerOutputStream) {
            EMKEAudioRoutesWriteSpeaker(gEMKERoutes, frames, inIOBufferFrameSize);
            return noErr;
        }
        if (inStreamObjectID == EMKEAudioObjectIDMicrophoneOutputStream) {
            EMKEAudioRoutesWriteMicrophone(gEMKERoutes, frames, inIOBufferFrameSize);
            return noErr;
        }
    } else if (inOperationID == kAudioServerPlugInIOOperationReadInput) {
        if (inStreamObjectID == EMKEAudioObjectIDSpeakerInputStream) {
            const UInt32 readFrames = EMKEAudioRoutesReadSpeaker(
                gEMKERoutes,
                frames,
                inIOBufferFrameSize
            );
            if (readFrames < inIOBufferFrameSize) {
                memset(
                    frames + (size_t)readFrames * kEMKEChannelCount,
                    0,
                    (size_t)(inIOBufferFrameSize - readFrames) *
                        kEMKEChannelCount * sizeof(Float32)
                );
            }
            return noErr;
        }
        if (inStreamObjectID == EMKEAudioObjectIDMicrophoneInputStream) {
            EMKEAudioRoutesReadMicrophone(gEMKERoutes, frames, inIOBufferFrameSize);
            return noErr;
        }
    }
    return kAudioHardwareUnsupportedOperationError;
}

static OSStatus EMKEEndIOOperation(
    AudioServerPlugInDriverRef inDriver,
    AudioObjectID inDeviceObjectID,
    UInt32 inClientID,
    UInt32 inOperationID,
    UInt32 inIOBufferFrameSize,
    const AudioServerPlugInIOCycleInfo *inIOCycleInfo
) {
    (void)inClientID;
    (void)inOperationID;
    (void)inIOBufferFrameSize;
    (void)inIOCycleInfo;
    return EMKEIsDriver(inDriver) && EMKEIsDevice(inDeviceObjectID)
        ? noErr
        : kAudioHardwareBadObjectError;
}
