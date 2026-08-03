#include "emke_bridge_routing.h"

bool EmkeBridgeEndpointForDeviceType(
    eDeviceType device_type,
    EmkeBridgeEndpoint* endpoint) noexcept {
  if (endpoint == nullptr) {
    return false;
  }
  switch (device_type) {
    case eMeetingSpeakerRenderDevice:
      *endpoint = EmkeBridgeEndpoint::meetingSpeakerRender;
      return true;
    case eAppSpeakerCaptureDevice:
      *endpoint = EmkeBridgeEndpoint::appSpeakerCapture;
      return true;
    case eAppMicrophoneRenderDevice:
      *endpoint = EmkeBridgeEndpoint::appMicrophoneRender;
      return true;
    case eMeetingMicrophoneCaptureDevice:
      *endpoint = EmkeBridgeEndpoint::meetingMicrophoneCapture;
      return true;
    case eMaxDeviceType:
      return false;
  }
  return false;
}

bool EmkeDeviceTypeForBridgeEndpoint(
    EmkeBridgeEndpoint endpoint,
    eDeviceType* device_type) noexcept {
  if (device_type == nullptr) {
    return false;
  }
  switch (endpoint) {
    case EmkeBridgeEndpoint::meetingSpeakerRender:
      *device_type = eMeetingSpeakerRenderDevice;
      return true;
    case EmkeBridgeEndpoint::appSpeakerCapture:
      *device_type = eAppSpeakerCaptureDevice;
      return true;
    case EmkeBridgeEndpoint::appMicrophoneRender:
      *device_type = eAppMicrophoneRenderDevice;
      return true;
    case EmkeBridgeEndpoint::meetingMicrophoneCapture:
      *device_type = eMeetingMicrophoneCaptureDevice;
      return true;
  }
  return false;
}

EmkeSize EmkeBridgeTransferDma(
    EmkeAudioBridgeSet* bridges,
    EmkeBridgeEndpoint endpoint,
    EmkeBridgeDmaDirection direction,
    unsigned char* dma_buffer,
    EmkeSize dma_buffer_size_bytes,
    EmkeSize linear_position_bytes,
    EmkeSize byte_displacement) noexcept {
  if (bridges == nullptr || dma_buffer == nullptr ||
      dma_buffer_size_bytes == 0u ||
      dma_buffer_size_bytes % EMKE_AUDIO_BLOCK_ALIGN != 0u ||
      linear_position_bytes % EMKE_AUDIO_BLOCK_ALIGN != 0u ||
      byte_displacement % EMKE_AUDIO_BLOCK_ALIGN != 0u) {
    return 0u;
  }

  EmkeSize buffer_offset =
      linear_position_bytes % dma_buffer_size_bytes;
  EmkeSize transferred_frames = 0u;
  while (byte_displacement > 0u) {
    const EmkeSize contiguous_bytes =
        dma_buffer_size_bytes - buffer_offset;
    const EmkeSize run_bytes =
        byte_displacement < contiguous_bytes
        ? byte_displacement
        : contiguous_bytes;
    const EmkeSize frame_count =
        run_bytes / EMKE_AUDIO_BLOCK_ALIGN;
    if (direction == EmkeBridgeDmaDirection::renderToBridge) {
      transferred_frames += EmkeAudioBridgeWrite(
          bridges,
          endpoint,
          reinterpret_cast<const float*>(
              dma_buffer + buffer_offset),
          frame_count);
    } else if (
        direction == EmkeBridgeDmaDirection::bridgeToCapture) {
      transferred_frames += EmkeAudioBridgeRead(
          bridges,
          endpoint,
          reinterpret_cast<float*>(dma_buffer + buffer_offset),
          frame_count);
    } else {
      return 0u;
    }
    buffer_offset =
        (buffer_offset + run_bytes) % dma_buffer_size_bytes;
    byte_displacement -= run_bytes;
  }
  return transferred_frames;
}
