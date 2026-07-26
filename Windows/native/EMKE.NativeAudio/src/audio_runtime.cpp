#include "audio_runtime.hpp"

namespace emke::audio {

AudioRuntime::AudioRuntime(const emke_audio_config& config)
    : config_(config)
#if defined(_WIN32) && !defined(EMKE_NATIVE_AUDIO_TEST_HOOKS)
      ,
      backend_(config)
#endif
{}

emke_audio_status AudioRuntime::start() {
  return backend_.start();
}

emke_audio_status AudioRuntime::stop() {
  return backend_.stop();
}

emke_audio_status AudioRuntime::set_inbound_route(emke_audio_route route) {
  return backend_.set_route(Direction::Inbound, route);
}

emke_audio_status AudioRuntime::set_outbound_route(emke_audio_route route) {
  return backend_.set_route(Direction::Outbound, route);
}

emke_audio_status AudioRuntime::enqueue_inbound_translation(
    std::span<const std::int16_t> pcm16) {
  return backend_.enqueue_translation(Direction::Inbound, pcm16);
}

emke_audio_status AudioRuntime::enqueue_outbound_translation(
    std::span<const std::int16_t> pcm16) {
  return backend_.enqueue_translation(Direction::Outbound, pcm16);
}

emke_audio_status AudioRuntime::poll_event(
    AudioEvent& event,
    std::size_t pcm_capacity_network_frames) {
  return backend_.poll_event(event, pcm_capacity_network_frames);
}

void AudioRuntime::write_diagnostics(
    emke_audio_diagnostics& diagnostics) const {
  backend_.write_diagnostics(diagnostics);
}

#if defined(EMKE_NATIVE_AUDIO_TEST_HOOKS)
emke_audio_status AudioRuntime::test_accept_synthetic(
    Direction direction,
    std::span<const float> interleaved_stereo) {
  return backend_.accept_synthetic_block(direction, interleaved_stereo);
}

emke_audio_status AudioRuntime::test_render(
    Direction direction,
    std::span<std::int16_t> mono_pcm16) {
  return backend_.render_translation(direction, mono_pcm16);
}

void AudioRuntime::test_inject_device_failure() {
  backend_.inject_device_failure();
}

void AudioRuntime::test_inject_inbound_translation_failure() {
  backend_.inject_inbound_translation_failure();
}

void AudioRuntime::test_inject_outbound_underrun() {
  backend_.inject_outbound_underrun();
}
#endif

}  // namespace emke::audio
