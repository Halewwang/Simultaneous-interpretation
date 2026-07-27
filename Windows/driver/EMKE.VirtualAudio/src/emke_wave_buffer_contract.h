#ifndef EMKE_WAVE_BUFFER_CONTRACT_H
#define EMKE_WAVE_BUFFER_CONTRACT_H

#include "emke_audio_bridge.h"

[[nodiscard]] inline constexpr bool EmkeIsNotificationBufferValid(
    EmkeUInt32 requested_size,
    EmkeUInt32 notification_count,
    EmkeUInt32 block_align) noexcept {
  if (requested_size == 0u || notification_count == 0u ||
      block_align == 0u || requested_size < block_align ||
      requested_size % block_align != 0u ||
      requested_size % notification_count != 0u) {
    return false;
  }
  const EmkeUInt32 packet_size =
      requested_size / notification_count;
  return packet_size >= block_align &&
      packet_size % block_align == 0u;
}

#endif
