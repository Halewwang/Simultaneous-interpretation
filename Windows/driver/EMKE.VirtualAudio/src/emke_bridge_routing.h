#ifndef EMKE_BRIDGE_ROUTING_H
#define EMKE_BRIDGE_ROUTING_H

#include "emke_audio_bridge.h"

typedef enum {
  eMeetingSpeakerRenderDevice = 0,
  eAppSpeakerCaptureDevice,
  eAppMicrophoneRenderDevice,
  eMeetingMicrophoneCaptureDevice,
  eMaxDeviceType,
} eDeviceType;

enum class EmkeBridgeDmaDirection : EmkeUInt32 {
  renderToBridge = 0u,
  bridgeToCapture = 1u,
};

[[nodiscard]] bool EmkeBridgeEndpointForDeviceType(
    eDeviceType device_type,
    EmkeBridgeEndpoint* endpoint) noexcept;

[[nodiscard]] bool EmkeDeviceTypeForBridgeEndpoint(
    EmkeBridgeEndpoint endpoint,
    eDeviceType* device_type) noexcept;

[[nodiscard]] EmkeSize EmkeBridgeTransferDma(
    EmkeAudioBridgeSet* bridges,
    EmkeBridgeEndpoint endpoint,
    EmkeBridgeDmaDirection direction,
    unsigned char* dma_buffer,
    EmkeSize dma_buffer_size_bytes,
    EmkeSize linear_position_bytes,
    EmkeSize byte_displacement) noexcept;

#endif
