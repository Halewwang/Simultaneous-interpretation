#ifndef EMKE_ENDPOINT_SNAPSHOT_HPP
#define EMKE_ENDPOINT_SNAPSHOT_HPP

#include "device_catalog.hpp"
#include "emke_native_audio.h"

namespace emke::audio {

// Copies a fully owned discovery result into the fixed public ABI snapshot.
// Invalid or overlong IDs are reported as SOURCE_ERROR and are never truncated.
[[nodiscard]] bool write_endpoint_snapshot(
    const EndpointDiscoveryResult& result,
    emke_audio_endpoint_snapshot& snapshot) noexcept;

}  // namespace emke::audio

#endif
