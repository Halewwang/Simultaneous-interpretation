#ifndef EMKE_ENDPOINT_CONTRACT_H
#define EMKE_ENDPOINT_CONTRACT_H

/*
 * Single native authority for the Windows virtual-audio ABI, endpoint property,
 * endpoint roles, and host-visible WaveRT format.
 */
#define EMKE_DRIVER_ABI 1u

#define EMKE_ENDPOINT_ROLE_PROPERTY_GUID_LITERAL "{3FA64F16-18AF-4E9E-B538-91C1140EC142}"
#define EMKE_ENDPOINT_ROLE_PROPERTY_PID 2u

#define EMKE_ROLE_MEETING_SPEAKER_RENDER_UTF8 "emke.meeting-speaker.render"
#define EMKE_ROLE_APP_SPEAKER_CAPTURE_UTF8 "emke.app-speaker.capture"
#define EMKE_ROLE_APP_MICROPHONE_RENDER_UTF8 "emke.app-microphone.render"
#define EMKE_ROLE_MEETING_MICROPHONE_CAPTURE_UTF8 "emke.meeting-microphone.capture"

#define EMKE_ROLE_MEETING_SPEAKER_RENDER_UTF16 L"emke.meeting-speaker.render"
#define EMKE_ROLE_APP_SPEAKER_CAPTURE_UTF16 L"emke.app-speaker.capture"
#define EMKE_ROLE_APP_MICROPHONE_RENDER_UTF16 L"emke.app-microphone.render"
#define EMKE_ROLE_MEETING_MICROPHONE_CAPTURE_UTF16 L"emke.meeting-microphone.capture"

#define EMKE_AUDIO_SAMPLE_RATE 48000u
#define EMKE_AUDIO_CHANNEL_COUNT 2u
#define EMKE_AUDIO_BITS_PER_SAMPLE 32u
#define EMKE_AUDIO_FORMAT_TAG 3u
#define EMKE_AUDIO_BLOCK_ALIGN \
  ((EMKE_AUDIO_CHANNEL_COUNT * EMKE_AUDIO_BITS_PER_SAMPLE) / 8u)
#define EMKE_AUDIO_AVG_BYTES_PER_SECOND \
  (EMKE_AUDIO_SAMPLE_RATE * EMKE_AUDIO_BLOCK_ALIGN)

#if defined(_WIN32)
#ifndef NOMINMAX
#define NOMINMAX
#endif
#if defined(_KERNEL_MODE)
#include <ntddk.h>
#else
#include <windows.h>
#endif
#include <devpropdef.h>

DEFINE_DEVPROPKEY(
    DEVPKEY_EMKE_EndpointRole,
    0x3fa64f16,
    0x18af,
    0x4e9e,
    0xb5,
    0x38,
    0x91,
    0xc1,
    0x14,
    0x0e,
    0xc1,
    0x42,
    EMKE_ENDPOINT_ROLE_PROPERTY_PID);
#endif

#endif
