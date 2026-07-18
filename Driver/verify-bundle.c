#include <CoreAudio/AudioServerPlugIn.h>
#include <CoreFoundation/CoreFoundation.h>

#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

typedef void *(*EMKEFactoryFunction)(CFAllocatorRef, CFUUIDRef);

static int EMKEReadName(
    AudioServerPlugInDriverRef driver,
    AudioObjectID objectID,
    const char *expectedName
) {
    const AudioObjectPropertyAddress address = {
        kAudioObjectPropertyName,
        kAudioObjectPropertyScopeGlobal,
        kAudioObjectPropertyElementMain,
    };
    UInt32 dataSize = sizeof(CFStringRef);
    UInt32 outputSize = 0;
    CFStringRef name = NULL;
    const OSStatus status = (*driver)->GetPropertyData(
        driver,
        objectID,
        0,
        &address,
        0,
        NULL,
        dataSize,
        &outputSize,
        &name
    );
    if (status != noErr || outputSize != sizeof(CFStringRef) || name == NULL) {
        return 1;
    }

    char value[128] = {0};
    const Boolean converted = CFStringGetCString(
        name,
        value,
        sizeof(value),
        kCFStringEncodingUTF8
    );
    CFRelease(name);
    return !converted || strcmp(value, expectedName) != 0;
}

static int EMKEBuffersEqual(const Float32 *lhs, const Float32 *rhs, UInt32 count) {
    for (UInt32 index = 0; index < count; index += 1) {
        if (fabsf(lhs[index] - rhs[index]) > 0.000001f) {
            return 0;
        }
    }
    return 1;
}

int main(int argc, char **argv) {
    if (argc != 2) {
        return 2;
    }

    const UInt8 *path = (const UInt8 *)argv[1];
    CFURLRef url = CFURLCreateFromFileSystemRepresentation(
        NULL,
        path,
        (CFIndex)strlen(argv[1]),
        true
    );
    if (url == NULL) {
        return 3;
    }

    CFBundleRef bundle = CFBundleCreate(NULL, url);
    CFRelease(url);
    CFErrorRef loadError = NULL;
    if (bundle == NULL || !CFBundleLoadExecutableAndReturnError(bundle, &loadError)) {
        if (loadError != NULL) {
            CFShow(loadError);
            CFRelease(loadError);
        }
        if (bundle != NULL) {
            CFRelease(bundle);
        }
        return 4;
    }

    EMKEFactoryFunction factory = (EMKEFactoryFunction)
        CFBundleGetFunctionPointerForName(bundle, CFSTR("EMKEAudioDriver_Create"));
    if (factory == NULL) {
        CFRelease(bundle);
        return 5;
    }

    AudioServerPlugInDriverRef driver = factory(NULL, kAudioServerPlugInTypeUUID);
    if (driver == NULL || *driver == NULL) {
        CFRelease(bundle);
        return 6;
    }
    const AudioServerPlugInHostInterface host = {0};
    const AudioServerPlugInIOCycleInfo cycle = {0};
    if ((*driver)->Initialize(driver, &host) != noErr) {
        CFRelease(bundle);
        return 7;
    }
    if (EMKEReadName(driver, 2, "EMKE Virtual Speaker") ||
        EMKEReadName(driver, 5, "EMKE Virtual Microphone")) {
        CFRelease(bundle);
        return 8;
    }

    const UInt32 frames = 2;
    const UInt32 samples = frames * 2;
    const Float32 speakerInput[4] = {0.1f, -0.1f, 0.2f, -0.2f};
    Float32 speakerOutput[4] = {0};
    if ((*driver)->StartIO(driver, 2, 10) != noErr ||
        (*driver)->DoIOOperation(driver, 2, 4, 10,
            kAudioServerPlugInIOOperationWriteMix, frames, &cycle,
            (void *)speakerInput, NULL) != noErr ||
        (*driver)->DoIOOperation(driver, 2, 3, 10,
            kAudioServerPlugInIOOperationReadInput, frames, &cycle,
            speakerOutput, NULL) != noErr ||
        !EMKEBuffersEqual(speakerInput, speakerOutput, samples) ||
        (*driver)->StopIO(driver, 2, 10) != noErr) {
        CFRelease(bundle);
        return 9;
    }

    Float32 microphoneSilence[4] = {1, 1, 1, 1};
    const Float32 silence[4] = {0, 0, 0, 0};
    const Float32 microphoneInput[4] = {0.3f, -0.3f, 0.4f, -0.4f};
    Float32 microphoneOutput[4] = {0};
    if ((*driver)->StartIO(driver, 5, 20) != noErr ||
        (*driver)->DoIOOperation(driver, 5, 6, 20,
            kAudioServerPlugInIOOperationReadInput, frames, &cycle,
            microphoneSilence, NULL) != noErr ||
        !EMKEBuffersEqual(microphoneSilence, silence, samples) ||
        (*driver)->DoIOOperation(driver, 5, 7, 20,
            kAudioServerPlugInIOOperationWriteMix, frames, &cycle,
            (void *)microphoneInput, NULL) != noErr ||
        (*driver)->DoIOOperation(driver, 5, 6, 20,
            kAudioServerPlugInIOOperationReadInput, frames, &cycle,
            microphoneOutput, NULL) != noErr ||
        !EMKEBuffersEqual(microphoneInput, microphoneOutput, samples) ||
        (*driver)->StopIO(driver, 5, 20) != noErr) {
        CFRelease(bundle);
        return 10;
    }

    CFBundleUnloadExecutable(bundle);
    CFRelease(bundle);
    puts("factory-smoke: speaker-loopback microphone-silence microphone-loopback");
    return 0;
}
