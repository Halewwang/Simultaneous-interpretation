#ifndef EMKE_SPSC_RING_HPP
#define EMKE_SPSC_RING_HPP

#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <vector>

namespace emke::audio {

inline constexpr std::size_t localBlockFrames = 480u;
inline constexpr std::size_t localChannelCount = 2u;
inline constexpr std::size_t captureCapacityLocalFrames = 4'800u;
inline constexpr std::size_t translatedPlaybackCapacityLocalFrames = 96'000u;
inline constexpr std::size_t captureRingBlockCapacity =
    captureCapacityLocalFrames / localBlockFrames;
inline constexpr std::size_t translatedPlaybackRingBlockCapacity =
    translatedPlaybackCapacityLocalFrames / localBlockFrames;

struct PcmBlock {
  std::array<float, localBlockFrames * localChannelCount> interleaved_stereo{};
  std::uint32_t frame_count = 0u;
  std::uint64_t timestamp = 0u;
};

class SpscBlockRing {
 public:
  explicit SpscBlockRing(std::size_t capacity_blocks)
      : storage_(capacity_blocks) {}

  SpscBlockRing(const SpscBlockRing&) = delete;
  SpscBlockRing& operator=(const SpscBlockRing&) = delete;

  [[nodiscard]] std::size_t capacity() const noexcept {
    return storage_.size();
  }

  [[nodiscard]] bool push(const PcmBlock& block) noexcept {
    const std::size_t write = write_index_.load(std::memory_order_relaxed);
    const std::size_t read = read_index_.load(std::memory_order_acquire);
    if (storage_.empty() || write - read >= storage_.size()) {
      return false;
    }

    storage_[write % storage_.size()] = block;
    write_index_.store(write + 1u, std::memory_order_release);
    return true;
  }

  [[nodiscard]] bool pop(PcmBlock& block) noexcept {
    const std::size_t read = read_index_.load(std::memory_order_relaxed);
    const std::size_t write = write_index_.load(std::memory_order_acquire);
    if (read == write) {
      return false;
    }

    block = storage_[read % storage_.size()];
    read_index_.store(read + 1u, std::memory_order_release);
    return true;
  }

  void clear() noexcept {
    read_index_.store(0u, std::memory_order_release);
    write_index_.store(0u, std::memory_order_release);
  }

 private:
  std::vector<PcmBlock> storage_;
  alignas(64) std::atomic<std::size_t> read_index_{0u};
  alignas(64) std::atomic<std::size_t> write_index_{0u};
};

static_assert(captureCapacityLocalFrames % localBlockFrames == 0u);
static_assert(translatedPlaybackCapacityLocalFrames % localBlockFrames == 0u);

}  // namespace emke::audio

#endif
