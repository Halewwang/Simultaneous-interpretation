/*++

Copyright (c) Microsoft Corporation All Rights Reserved

Module Name:

    minipairs.h

Abstract:

    Local audio endpoint filter definitions.
--*/

#ifndef _SIMPLEAUDIOSAMPLE_MINIPAIRS_H_
#define _SIMPLEAUDIOSAMPLE_MINIPAIRS_H_

#include "emke_endpoint_contract.h"

#include "speakertopo.h"
#include "speakertoptable.h"
#include "speakerwavtable.h"

#include "micarraytopo.h"
#include "micarray1toptable.h"
#include "micarraywavtable.h"
C_ASSERT(EMKE_DRIVER_ABI == 1);
C_ASSERT(SPEAKER_MAX_INPUT_SYSTEM_STREAMS == 1);
C_ASSERT(MICARRAY_MAX_INPUT_STREAMS == 1);

NTSTATUS
CreateMiniportWaveRTSimpleAudioSample
(
    _Out_       PUNKNOWN *,
    _In_        REFCLSID,
    _In_opt_    PUNKNOWN,
    _In_        POOL_FLAGS,
    _In_        PUNKNOWN,
    _In_opt_    PVOID,
    _In_        PENDPOINT_MINIPAIR
);

NTSTATUS
CreateMiniportTopologySimpleAudioSample
(
    _Out_       PUNKNOWN *,
    _In_        REFCLSID,
    _In_opt_    PUNKNOWN,
    _In_        POOL_FLAGS,
    _In_        PUNKNOWN,
    _In_opt_    PVOID,
    _In_        PENDPOINT_MINIPAIR
);

//
// Render miniports.
//

/*********************************************************************
* Topology/Wave bridge connection for speaker (internal)             *
*                                                                    *
*              +------+                +------+                      *
*              | Wave |                | Topo |                      *
*              |      |                |      |                      *
* System   --->|0    1|--------------->|0    1|---> Line Out         *
*              |      |                |      |                      *
*              +------+                +------+                      *
*********************************************************************/
static
PHYSICALCONNECTIONTABLE SpeakerTopologyPhysicalConnections[] =
{
    {
        KSPIN_TOPO_WAVEOUT_SOURCE,  // TopologyIn
        KSPIN_WAVE_RENDER3_SOURCE,   // WaveOut
        CONNECTIONTYPE_WAVE_OUTPUT
    }
};

// Role: emke.meeting-speaker.render
// User-facing friendly endpoint: EMKE Virtual Speaker
static
ENDPOINT_MINIPAIR MeetingSpeakerMiniports =
{
    eMeetingSpeakerRenderDevice,
    L"TopologyMeetingSpeaker",
    NULL,                                                   // optional template name
    CreateMiniportTopologySimpleAudioSample,
    &SpeakerTopoMiniportFilterDescriptor,
    0, NULL,                                                // Interface properties
    L"WaveMeetingSpeaker",
    NULL,                                                   // optional template name
    CreateMiniportWaveRTSimpleAudioSample,
    &SpeakerWaveMiniportFilterDescriptor,
    0,                                                      // Interface properties
    NULL,
    SPEAKER_DEVICE_MAX_CHANNELS,
    SpeakerPinDeviceFormatsAndModes,
    SIZEOF_ARRAY(SpeakerPinDeviceFormatsAndModes),
    SpeakerTopologyPhysicalConnections,
    SIZEOF_ARRAY(SpeakerTopologyPhysicalConnections),
    ENDPOINT_NO_FLAGS,
};

// Role: emke.app-microphone.render
// Internal endpoint: EMKE Internal Microphone Render
static
ENDPOINT_MINIPAIR AppMicrophoneRenderMiniports =
{
    eAppMicrophoneRenderDevice,
    L"TopologyAppMicrophoneRender",
    NULL,
    CreateMiniportTopologySimpleAudioSample,
    &SpeakerTopoMiniportFilterDescriptor,
    0, NULL,
    L"WaveAppMicrophoneRender",
    NULL,
    CreateMiniportWaveRTSimpleAudioSample,
    &SpeakerWaveMiniportFilterDescriptor,
    0,
    NULL,
    SPEAKER_DEVICE_MAX_CHANNELS,
    SpeakerPinDeviceFormatsAndModes,
    SIZEOF_ARRAY(SpeakerPinDeviceFormatsAndModes),
    SpeakerTopologyPhysicalConnections,
    SIZEOF_ARRAY(SpeakerTopologyPhysicalConnections),
    ENDPOINT_NO_FLAGS,
};

//
// Capture miniports.
//

/*********************************************************************
* Topology/Wave bridge connection for mic array  1 (front)           *
*                                                                    *
*              +------+    +------+                                  *
*              | Topo |    | Wave |                                  *
*              |      |    |      |                                  *
*  Mic in  --->|0    1|===>|0    1|---> Capture Host Pin             *
*              |      |    |      |                                  *
*              +------+    +------+                                  *
*********************************************************************/
static
PHYSICALCONNECTIONTABLE MicArray1TopologyPhysicalConnections[] =
{
    {
        KSPIN_TOPO_BRIDGE,          // TopologyOut
        KSPIN_WAVE_BRIDGE,          // WaveIn
        CONNECTIONTYPE_TOPOLOGY_OUTPUT
    }
};

// Role: emke.app-speaker.capture
// Internal endpoint: EMKE Internal Speaker Capture
static
ENDPOINT_MINIPAIR AppSpeakerCaptureMiniports =
{
    eAppSpeakerCaptureDevice,
    L"TopologyAppSpeakerCapture",
    NULL,                                   // optional template name
    CreateMicArrayMiniportTopology,
    &MicArray1TopoMiniportFilterDescriptor,
    0, NULL,                                // Interface properties
    L"WaveAppSpeakerCapture",
    NULL,                                   // optional template name
    CreateMiniportWaveRTSimpleAudioSample,
    &MicArrayWaveMiniportFilterDescriptor,
    0,                                      // Interface properties
    NULL,
    MICARRAY_DEVICE_MAX_CHANNELS,
    MicArrayPinDeviceFormatsAndModes,
    SIZEOF_ARRAY(MicArrayPinDeviceFormatsAndModes),
    MicArray1TopologyPhysicalConnections,
    SIZEOF_ARRAY(MicArray1TopologyPhysicalConnections),
    ENDPOINT_NO_FLAGS,
};

// Role: emke.meeting-microphone.capture
// User-facing friendly endpoint: EMKE Virtual Microphone
static
ENDPOINT_MINIPAIR MeetingMicrophoneMiniports =
{
    eMeetingMicrophoneCaptureDevice,
    L"TopologyMeetingMicrophone",
    NULL,
    CreateMicArrayMiniportTopology,
    &MicArray1TopoMiniportFilterDescriptor,
    0, NULL,
    L"WaveMeetingMicrophone",
    NULL,
    CreateMiniportWaveRTSimpleAudioSample,
    &MicArrayWaveMiniportFilterDescriptor,
    0,
    NULL,
    MICARRAY_DEVICE_MAX_CHANNELS,
    MicArrayPinDeviceFormatsAndModes,
    SIZEOF_ARRAY(MicArrayPinDeviceFormatsAndModes),
    MicArray1TopologyPhysicalConnections,
    SIZEOF_ARRAY(MicArray1TopologyPhysicalConnections),
    ENDPOINT_NO_FLAGS,
};


//=============================================================================
//
// Render miniport pairs. NOTE: the split of render and capture is arbitrary and
// unnessary, this array could contain capture endpoints.
//
static
PENDPOINT_MINIPAIR  g_RenderEndpoints[] =
{
    &MeetingSpeakerMiniports,
    &AppMicrophoneRenderMiniports,
};

#define g_cRenderEndpoints 2
C_ASSERT(SIZEOF_ARRAY(g_RenderEndpoints) == g_cRenderEndpoints);

//=============================================================================
//
// Capture miniport pairs. NOTE: the split of render and capture is arbitrary and
// unnessary, this array could contain render endpoints.
//
static
PENDPOINT_MINIPAIR  g_CaptureEndpoints[] =
{
    &AppSpeakerCaptureMiniports,
    &MeetingMicrophoneMiniports,
};

#define g_cCaptureEndpoints 2
C_ASSERT(SIZEOF_ARRAY(g_CaptureEndpoints) == g_cCaptureEndpoints);

//=============================================================================
//
// Total miniports = # endpoints * 2 (topology + wave).
//
#define g_MaxMiniports  ((g_cRenderEndpoints + g_cCaptureEndpoints) * 2)

#endif // _SIMPLEAUDIOSAMPLE_MINIPAIRS_H_
