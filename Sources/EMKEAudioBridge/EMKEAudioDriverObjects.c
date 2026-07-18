#include "EMKEAudioDriverObjects.h"

#include <stddef.h>

static const double kEMKESampleRate = 48000.0;
static const uint32_t kEMKEChannelCount = 2;

static const EMKEAudioObjectDescriptor kEMKEAudioObjects[] = {
    {
        EMKEAudioObjectIDPlugin,
        0,
        EMKEAudioObjectKindPlugin,
        EMKEAudioStreamDirectionNone,
        EMKEAudioStreamRoleNone,
        "EMKE Audio Driver",
        "com.emke.translation.audio-driver",
        0,
        0,
    },
    {
        EMKEAudioObjectIDSpeakerDevice,
        EMKEAudioObjectIDPlugin,
        EMKEAudioObjectKindDevice,
        EMKEAudioStreamDirectionNone,
        EMKEAudioStreamRoleNone,
        "EMKE Virtual Speaker",
        "com.emke.translation.virtual-speaker",
        kEMKESampleRate,
        kEMKEChannelCount,
    },
    {
        EMKEAudioObjectIDSpeakerInputStream,
        EMKEAudioObjectIDSpeakerDevice,
        EMKEAudioObjectKindStream,
        EMKEAudioStreamDirectionInput,
        EMKEAudioStreamRoleAppFacing,
        "EMKE Speaker App Capture",
        "com.emke.translation.virtual-speaker.input",
        kEMKESampleRate,
        kEMKEChannelCount,
    },
    {
        EMKEAudioObjectIDSpeakerOutputStream,
        EMKEAudioObjectIDSpeakerDevice,
        EMKEAudioObjectKindStream,
        EMKEAudioStreamDirectionOutput,
        EMKEAudioStreamRoleMeetingFacing,
        "EMKE Speaker Meeting Output",
        "com.emke.translation.virtual-speaker.output",
        kEMKESampleRate,
        kEMKEChannelCount,
    },
    {
        EMKEAudioObjectIDMicrophoneDevice,
        EMKEAudioObjectIDPlugin,
        EMKEAudioObjectKindDevice,
        EMKEAudioStreamDirectionNone,
        EMKEAudioStreamRoleNone,
        "EMKE Virtual Microphone",
        "com.emke.translation.virtual-microphone",
        kEMKESampleRate,
        kEMKEChannelCount,
    },
    {
        EMKEAudioObjectIDMicrophoneInputStream,
        EMKEAudioObjectIDMicrophoneDevice,
        EMKEAudioObjectKindStream,
        EMKEAudioStreamDirectionInput,
        EMKEAudioStreamRoleMeetingFacing,
        "EMKE Microphone Meeting Input",
        "com.emke.translation.virtual-microphone.input",
        kEMKESampleRate,
        kEMKEChannelCount,
    },
    {
        EMKEAudioObjectIDMicrophoneOutputStream,
        EMKEAudioObjectIDMicrophoneDevice,
        EMKEAudioObjectKindStream,
        EMKEAudioStreamDirectionOutput,
        EMKEAudioStreamRoleAppFacing,
        "EMKE Microphone App Translation",
        "com.emke.translation.virtual-microphone.output",
        kEMKESampleRate,
        kEMKEChannelCount,
    },
};

uint32_t EMKEAudioDriverObjectCount(void) {
    return (uint32_t)(sizeof(kEMKEAudioObjects) / sizeof(kEMKEAudioObjects[0]));
}

const EMKEAudioObjectDescriptor *EMKEAudioDriverObjectAtIndex(uint32_t index) {
    if (index >= EMKEAudioDriverObjectCount()) {
        return NULL;
    }
    return &kEMKEAudioObjects[index];
}

const EMKEAudioObjectDescriptor *EMKEAudioDriverObjectForID(uint32_t objectID) {
    for (uint32_t index = 0; index < EMKEAudioDriverObjectCount(); index += 1) {
        if (kEMKEAudioObjects[index].objectID == objectID) {
            return &kEMKEAudioObjects[index];
        }
    }
    return NULL;
}
