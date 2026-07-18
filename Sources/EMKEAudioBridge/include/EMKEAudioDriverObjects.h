#ifndef EMKE_AUDIO_DRIVER_OBJECTS_H
#define EMKE_AUDIO_DRIVER_OBJECTS_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef enum EMKEAudioObjectKind {
    EMKEAudioObjectKindPlugin = 1,
    EMKEAudioObjectKindDevice = 2,
    EMKEAudioObjectKindStream = 3,
} EMKEAudioObjectKind;

typedef enum EMKEAudioStreamDirection {
    EMKEAudioStreamDirectionNone = 0,
    EMKEAudioStreamDirectionInput = 1,
    EMKEAudioStreamDirectionOutput = 2,
} EMKEAudioStreamDirection;

typedef enum EMKEAudioStreamRole {
    EMKEAudioStreamRoleNone = 0,
    EMKEAudioStreamRoleMeetingFacing = 1,
    EMKEAudioStreamRoleAppFacing = 2,
} EMKEAudioStreamRole;

enum {
    EMKEAudioObjectIDPlugin = 1,
    EMKEAudioObjectIDSpeakerDevice = 2,
    EMKEAudioObjectIDSpeakerInputStream = 3,
    EMKEAudioObjectIDSpeakerOutputStream = 4,
    EMKEAudioObjectIDMicrophoneDevice = 5,
    EMKEAudioObjectIDMicrophoneInputStream = 6,
    EMKEAudioObjectIDMicrophoneOutputStream = 7,
};

typedef struct EMKEAudioObjectDescriptor {
    uint32_t objectID;
    uint32_t ownerObjectID;
    EMKEAudioObjectKind kind;
    EMKEAudioStreamDirection direction;
    EMKEAudioStreamRole role;
    const char *name;
    const char *uid;
    double sampleRate;
    uint32_t channelCount;
} EMKEAudioObjectDescriptor;

uint32_t EMKEAudioDriverObjectCount(void);

const EMKEAudioObjectDescriptor *EMKEAudioDriverObjectAtIndex(uint32_t index);

const EMKEAudioObjectDescriptor *EMKEAudioDriverObjectForID(uint32_t objectID);

#ifdef __cplusplus
}
#endif

#endif
