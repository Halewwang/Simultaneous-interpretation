#ifndef EMKE_AUDIO_HAL_H
#define EMKE_AUDIO_HAL_H

#include <AudioUnit/AudioUnit.h>
#include <CoreAudio/CoreAudio.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct EMKEHALInput EMKEHALInput;
typedef struct EMKEHALOutput EMKEHALOutput;

OSStatus EMKEHALInputCreate(
    AudioObjectID deviceID,
    uint32_t capacityFrames,
    EMKEHALInput **outInput
);
OSStatus EMKEHALInputStart(EMKEHALInput *input);
OSStatus EMKEHALInputStop(EMKEHALInput *input);
uint32_t EMKEHALInputRead(
    EMKEHALInput *input,
    float *interleavedFrames,
    uint32_t frameCount
);
uint32_t EMKEHALInputReadableFrames(const EMKEHALInput *input);
void EMKEHALInputDestroy(EMKEHALInput *input);

OSStatus EMKEHALOutputCreate(
    AudioObjectID deviceID,
    uint32_t capacityFrames,
    EMKEHALOutput **outOutput
);
OSStatus EMKEHALOutputStart(EMKEHALOutput *output);
OSStatus EMKEHALOutputStop(EMKEHALOutput *output);
uint32_t EMKEHALOutputWrite(
    EMKEHALOutput *output,
    const float *interleavedFrames,
    uint32_t frameCount
);
uint32_t EMKEHALOutputQueuedFrames(const EMKEHALOutput *output);
void EMKEHALOutputDestroy(EMKEHALOutput *output);

#ifdef __cplusplus
}
#endif

#endif
