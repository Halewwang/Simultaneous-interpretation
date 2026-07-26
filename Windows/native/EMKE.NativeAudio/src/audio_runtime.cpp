#include "audio_runtime.hpp"

namespace emke::audio {

AudioRuntime::AudioRuntime(const emke_audio_config& config) : config_(config) {}

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

emke_audio_status AudioRuntime::poll_event(AudioEvent& event) {
  return backend_.poll_event(event);
}

void AudioRuntime::write_diagnostics(
    emke_audio_diagnostics& diagnostics) const {
  backend_.write_diagnostics(diagnostics);
}

}  // namespace emke::audio
