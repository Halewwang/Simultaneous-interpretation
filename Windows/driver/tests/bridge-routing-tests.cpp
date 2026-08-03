#include "../EMKE.VirtualAudio/src/emke_bridge_routing.h"

#include <array>
#include <cstddef>
#include <cstdio>

namespace {

int failures = 0;

void expect(bool condition, const char* message) {
  if (!condition) {
    std::fprintf(stderr, "FAIL: %s\n", message);
    ++failures;
  }
}

template <std::size_t Size>
void expect_samples(
    const std::array<float, Size>& actual,
    const std::array<float, Size>& expected,
    const char* message) {
  if (actual != expected) {
    std::fprintf(stderr, "FAIL: %s\n", message);
    ++failures;
  }
}

void test_all_production_identities_map_both_ways() {
  struct Identity {
    eDeviceType device;
    EmkeBridgeEndpoint endpoint;
  };
  constexpr std::array identities{
      Identity{
          eMeetingSpeakerRenderDevice,
          EmkeBridgeEndpoint::meetingSpeakerRender},
      Identity{
          eAppSpeakerCaptureDevice,
          EmkeBridgeEndpoint::appSpeakerCapture},
      Identity{
          eAppMicrophoneRenderDevice,
          EmkeBridgeEndpoint::appMicrophoneRender},
      Identity{
          eMeetingMicrophoneCaptureDevice,
          EmkeBridgeEndpoint::meetingMicrophoneCapture},
  };

  for (const Identity& identity : identities) {
    EmkeBridgeEndpoint endpoint =
        EmkeBridgeEndpoint::meetingSpeakerRender;
    expect(
        EmkeBridgeEndpointForDeviceType(
            identity.device,
            &endpoint),
        "every production eDeviceType must map to a bridge endpoint");
    expect(
        endpoint == identity.endpoint,
        "production eDeviceType mapping must preserve identity");

    eDeviceType device = eMeetingSpeakerRenderDevice;
    expect(
        EmkeDeviceTypeForBridgeEndpoint(endpoint, &device),
        "every bridge endpoint must map back to a production eDeviceType");
    expect(
        device == identity.device,
        "reverse production mapping must preserve identity");
  }

  EmkeBridgeEndpoint endpoint =
      EmkeBridgeEndpoint::appSpeakerCapture;
  expect(
      !EmkeBridgeEndpointForDeviceType(
          static_cast<eDeviceType>(eMaxDeviceType + 1),
          &endpoint),
      "invalid eDeviceType must fail closed");
  expect(
      endpoint == EmkeBridgeEndpoint::appSpeakerCapture,
      "invalid eDeviceType must not invent a fallback endpoint");

  eDeviceType device = eAppSpeakerCaptureDevice;
  expect(
      !EmkeDeviceTypeForBridgeEndpoint(
          static_cast<EmkeBridgeEndpoint>(99u),
          &device),
      "invalid bridge endpoint must fail closed");
  expect(
      device == eAppSpeakerCaptureDevice,
      "invalid bridge endpoint must not invent a fallback identity");
}

void test_production_dma_transfer_wraps_in_frames() {
  EmkeAudioBridgeSet bridges{};
  EmkeAudioBridgeInitialize(&bridges);

  EmkeBridgeEndpoint producer{};
  EmkeBridgeEndpoint consumer{};
  expect(
      EmkeBridgeEndpointForDeviceType(
          eMeetingSpeakerRenderDevice,
          &producer),
      "meeting-speaker identity must resolve");
  expect(
      EmkeBridgeEndpointForDeviceType(
          eAppSpeakerCaptureDevice,
          &consumer),
      "app-speaker identity must resolve");

  std::array<float, 8> render_dma{
      0.1f, -0.1f,
      0.2f, -0.2f,
      0.3f, -0.3f,
      0.4f, -0.4f,
  };
  expect(
      EmkeBridgeTransferDma(
          &bridges,
          producer,
          EmkeBridgeDmaDirection::renderToBridge,
          reinterpret_cast<unsigned char*>(render_dma.data()),
          sizeof(render_dma),
          3u * EMKE_AUDIO_BLOCK_ALIGN,
          3u * EMKE_AUDIO_BLOCK_ALIGN) == 3u,
      "render DMA wrap must publish three complete frames");

  std::array<float, 8> capture_dma;
  capture_dma.fill(9.0f);
  expect(
      EmkeBridgeTransferDma(
          &bridges,
          consumer,
          EmkeBridgeDmaDirection::bridgeToCapture,
          reinterpret_cast<unsigned char*>(capture_dma.data()),
          sizeof(capture_dma),
          2u * EMKE_AUDIO_BLOCK_ALIGN,
          3u * EMKE_AUDIO_BLOCK_ALIGN) == 3u,
      "capture DMA wrap must consume three complete frames");
  constexpr std::array<float, 8> expected{
      0.2f, -0.2f,
      9.0f, 9.0f,
      0.4f, -0.4f,
      0.1f, -0.1f,
  };
  expect_samples(
      capture_dma,
      expected,
      "DMA movement must preserve frame order across both wraps");
}

void test_production_mapping_preserves_reset_lifecycle() {
  EmkeAudioBridgeSet bridges{};
  EmkeAudioBridgeInitialize(&bridges);
  EmkeBridgeEndpoint producer{};
  EmkeBridgeEndpoint consumer{};
  expect(
      EmkeBridgeEndpointForDeviceType(
          eAppMicrophoneRenderDevice,
          &producer),
      "app-microphone identity must resolve");
  expect(
      EmkeBridgeEndpointForDeviceType(
          eMeetingMicrophoneCaptureDevice,
          &consumer),
      "meeting-microphone identity must resolve");

  std::array<float, 2> render_dma{0.8f, -0.8f};
  expect(
      EmkeBridgeTransferDma(
          &bridges,
          producer,
          EmkeBridgeDmaDirection::renderToBridge,
          reinterpret_cast<unsigned char*>(render_dma.data()),
          sizeof(render_dma),
          0u,
          sizeof(render_dma)) == 1u,
      "production route must publish the old session");
  EmkeAudioBridgeReset(&bridges, consumer);

  std::array<float, 2> capture_dma{4.0f, 4.0f};
  expect(
      EmkeBridgeTransferDma(
          &bridges,
          consumer,
          EmkeBridgeDmaDirection::bridgeToCapture,
          reinterpret_cast<unsigned char*>(capture_dma.data()),
          sizeof(capture_dma),
          0u,
          sizeof(capture_dma)) == 0u,
      "reset must discard old production-route frames");
  expect_samples(
      capture_dma,
      std::array<float, 2>{0.0f, 0.0f},
      "capture DMA must fail closed after reset");

  render_dma = {0.25f, -0.25f};
  expect(
      EmkeBridgeTransferDma(
          &bridges,
          producer,
          EmkeBridgeDmaDirection::renderToBridge,
          reinterpret_cast<unsigned char*>(render_dma.data()),
          sizeof(render_dma),
          0u,
          sizeof(render_dma)) == 1u,
      "production route must accept the new session");
  expect(
      EmkeBridgeTransferDma(
          &bridges,
          consumer,
          EmkeBridgeDmaDirection::bridgeToCapture,
          reinterpret_cast<unsigned char*>(capture_dma.data()),
          sizeof(capture_dma),
          0u,
          sizeof(capture_dma)) == 1u,
      "production route must deliver the new session");
  expect_samples(
      capture_dma,
      render_dma,
      "reset lifecycle must not leak prior production-route frames");
}

}  // namespace

int main() {
  test_all_production_identities_map_both_ways();
  test_production_dma_transfer_wraps_in_frames();
  test_production_mapping_preserves_reset_lifecycle();

  if (failures != 0) {
    return 1;
  }
  std::puts("EMKE production bridge routing tests passed (3 cases).");
  return 0;
}
