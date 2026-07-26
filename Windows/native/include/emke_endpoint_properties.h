#ifndef EMKE_ENDPOINT_PROPERTIES_H
#define EMKE_ENDPOINT_PROPERTIES_H

/*
 * Frozen product property key shared by the native host and the virtual-audio
 * driver. Endpoint roles are stored as DEVPROP_TYPE_STRING values.
 *
 * {3FA64F16-18AF-4E9E-B538-91C1140EC142}, pid 2
 */
#if defined(_WIN32)
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
    2);
#endif

#endif
