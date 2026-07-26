#ifndef EMKE_DRIVER_ENDPOINT_ROLES_H
#define EMKE_DRIVER_ENDPOINT_ROLES_H

#include <devpropdef.h>

// Frozen product property key shared with Windows/native.
// {3FA64F16-18AF-4E9E-B538-91C1140EC142}, pid 2
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
    2);

#define EMKE_DRIVER_ABI 1
#define EMKE_ROLE_MEETING_SPEAKER_RENDER L"emke.meeting-speaker.render"
#define EMKE_ROLE_APP_SPEAKER_CAPTURE L"emke.app-speaker.capture"
#define EMKE_ROLE_APP_MICROPHONE_RENDER L"emke.app-microphone.render"
#define EMKE_ROLE_MEETING_MICROPHONE_CAPTURE \
  L"emke.meeting-microphone.capture"

#endif
