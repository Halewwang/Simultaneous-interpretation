#include "spsc_ring.hpp"

#include <atomic>
#include <chrono>
#include <cstddef>
#include <cstdlib>
#include <iostream>
#include <new>
#include <string_view>
#include <thread>
#include <utility>

namespace {

std::atomic<std::size_t> allocation_count = 0u;

class TestContext {
 public:
  void expect(bool condition, std::string_view expression, int line) {
    if (condition) {
      return;
    }
    ++failures_;
    std::cerr << line << ": expected " << expression << '\n';
  }

  [[nodiscard]] int failures() const {
    return failures_;
  }

 private:
  int failures_ = 0;
};

#define EXPECT(context, expression) \
  (context).expect((expression), #expression, __LINE__)

emke::audio::PcmBlock block(std::uint32_t frame_count,
                            std::uint64_t timestamp,
                            float marker) {
  emke::audio::PcmBlock value;
  value.frame_count = frame_count;
  value.timestamp = timestamp;
  value.interleaved_stereo[0] = marker;
  return value;
}

bool wait_for_start_or_stop(const std::atomic<bool>& start,
                            std::stop_token stop_token) noexcept {
  while (!start.load(std::memory_order_acquire)) {
    if (stop_token.stop_requested()) {
      return false;
    }
    std::this_thread::yield();
  }
  return !stop_token.stop_requested();
}

void test_capacity_is_fixed_after_construction(TestContext& context) {
  emke::audio::SpscBlockRing capture(emke::audio::captureRingBlockCapacity);
  emke::audio::SpscBlockRing playback(
      emke::audio::translatedPlaybackRingBlockCapacity);

  EXPECT(context, capture.capacity() == 10u);
  EXPECT(context, playback.capacity() == 200u);
  EXPECT(context,
         capture.capacity() * emke::audio::localBlockFrames == 4'800u);
  EXPECT(context,
         playback.capacity() * emke::audio::localBlockFrames == 96'000u);
}

void test_single_producer_consumer_preserves_order(TestContext& context) {
  constexpr std::size_t item_count = 4'096u;
  constexpr std::uint64_t first_timestamp = 50'000u;
  emke::audio::SpscBlockRing ring(7u);
  std::atomic<bool> start = false;
  std::atomic<bool> failed = false;
  const auto deadline =
      std::chrono::steady_clock::now() + std::chrono::seconds(5);
  std::size_t produced = 0u;
  std::size_t consumed = 0u;

  std::jthread producer([&](std::stop_token stop_token) {
    if (!wait_for_start_or_stop(start, stop_token)) {
      return;
    }
    for (std::size_t sequence = 0u; sequence < item_count; ++sequence) {
      const auto frame_count = static_cast<std::uint32_t>(
          sequence % emke::audio::localBlockFrames + 1u);
      const auto value = block(
          frame_count,
          first_timestamp + sequence,
          static_cast<float>(sequence));
      while (!ring.push(value)) {
        if (failed.load(std::memory_order_acquire) ||
            std::chrono::steady_clock::now() >= deadline) {
          failed.store(true, std::memory_order_release);
          return;
        }
        std::this_thread::yield();
      }
      ++produced;
    }
  });

  std::jthread consumer([&](std::stop_token stop_token) {
    if (!wait_for_start_or_stop(start, stop_token)) {
      return;
    }
    for (std::size_t sequence = 0u; sequence < item_count; ++sequence) {
      emke::audio::PcmBlock output;
      while (!ring.pop(output)) {
        if (failed.load(std::memory_order_acquire) ||
            std::chrono::steady_clock::now() >= deadline) {
          failed.store(true, std::memory_order_release);
          return;
        }
        std::this_thread::yield();
      }
      const auto expected_frame_count = static_cast<std::uint32_t>(
          sequence % emke::audio::localBlockFrames + 1u);
      if (output.interleaved_stereo[0] != static_cast<float>(sequence) ||
          output.frame_count != expected_frame_count ||
          output.timestamp != first_timestamp + sequence) {
        failed.store(true, std::memory_order_release);
        return;
      }
      ++consumed;
    }
  });

  start.store(true, std::memory_order_release);
  producer.join();
  consumer.join();

  EXPECT(context, !failed.load(std::memory_order_acquire));
  EXPECT(context, produced == item_count);
  EXPECT(context, consumed == item_count);
  emke::audio::PcmBlock output;
  EXPECT(context, !ring.pop(output));
}

void test_start_wait_exits_when_stop_requested_before_start(
    TestContext& context) {
  std::atomic<bool> start = false;
  std::atomic<bool> entered = false;
  std::atomic<bool> exited = false;
  std::atomic<bool> observed_start = true;
  std::jthread waiter([&](std::stop_token stop_token) {
    entered.store(true, std::memory_order_release);
    observed_start.store(
        wait_for_start_or_stop(start, stop_token),
        std::memory_order_release);
    exited.store(true, std::memory_order_release);
  });

  const auto enter_deadline =
      std::chrono::steady_clock::now() + std::chrono::seconds(1);
  while (!entered.load(std::memory_order_acquire) &&
         std::chrono::steady_clock::now() < enter_deadline) {
    std::this_thread::yield();
  }
  waiter.request_stop();
  const auto stop_deadline =
      std::chrono::steady_clock::now() + std::chrono::milliseconds(250);
  while (!exited.load(std::memory_order_acquire) &&
         std::chrono::steady_clock::now() < stop_deadline) {
    std::this_thread::yield();
  }
  const bool exited_before_start = exited.load(std::memory_order_acquire);
  if (!exited_before_start) {
    start.store(true, std::memory_order_release);
  }
  waiter.join();

  EXPECT(context, entered.load(std::memory_order_acquire));
  EXPECT(context, exited_before_start);
  EXPECT(context, exited.load(std::memory_order_acquire));
  EXPECT(context, !observed_start.load(std::memory_order_acquire));
}

void test_push_rejects_frame_counts_outside_block_boundary(
    TestContext& context) {
  emke::audio::SpscBlockRing ring(3u);

  EXPECT(context, !ring.push(block(0u, 10u, 0.0f)));
  EXPECT(context, ring.push(block(480u, 11u, 480.0f)));
  EXPECT(context, !ring.push(block(481u, 12u, 481.0f)));
  EXPECT(context, ring.push(block(1u, 13u, 1.0f)));

  emke::audio::PcmBlock output;
  EXPECT(context, ring.pop(output));
  EXPECT(context, output.frame_count == 480u);
  EXPECT(context, output.timestamp == 11u);
  EXPECT(context, output.interleaved_stereo[0] == 480.0f);
  EXPECT(context, ring.pop(output));
  EXPECT(context, output.frame_count == 1u);
  EXPECT(context, output.timestamp == 13u);
  EXPECT(context, output.interleaved_stereo[0] == 1.0f);
  EXPECT(context, !ring.pop(output));
}

void test_write_beyond_capacity_fails_without_overwrite(
    TestContext& context) {
  emke::audio::SpscBlockRing ring(2u);
  EXPECT(context, ring.push(block(480u, 1u, 1.0f)));
  EXPECT(context, ring.push(block(480u, 2u, 2.0f)));
  EXPECT(context, !ring.push(block(480u, 3u, 3.0f)));

  emke::audio::PcmBlock output;
  EXPECT(context, ring.pop(output));
  EXPECT(context, output.timestamp == 1u);
  EXPECT(context, ring.pop(output));
  EXPECT(context, output.timestamp == 2u);
  EXPECT(context, !ring.pop(output));
}

void test_read_from_empty_returns_false(TestContext& context) {
  emke::audio::SpscBlockRing ring(1u);
  emke::audio::PcmBlock output = block(77u, 99u, 4.0f);

  EXPECT(context, !ring.pop(output));
  EXPECT(context, output.frame_count == 77u);
  EXPECT(context, output.timestamp == 99u);
}

void test_wraparound_preserves_metadata(TestContext& context) {
  emke::audio::SpscBlockRing ring(2u);
  emke::audio::PcmBlock output;

  EXPECT(context, ring.push(block(480u, 100u, 1.0f)));
  EXPECT(context, ring.push(block(240u, 101u, 2.0f)));
  EXPECT(context, ring.pop(output));
  EXPECT(context, output.frame_count == 480u);
  EXPECT(context, output.timestamp == 100u);

  EXPECT(context, ring.push(block(120u, 102u, 3.0f)));
  EXPECT(context, ring.pop(output));
  EXPECT(context, output.frame_count == 240u);
  EXPECT(context, output.timestamp == 101u);
  EXPECT(context, ring.pop(output));
  EXPECT(context, output.frame_count == 120u);
  EXPECT(context, output.timestamp == 102u);
}

void test_clear_resets_indices_without_allocation(TestContext& context) {
  emke::audio::SpscBlockRing ring(2u);
  const auto first = block(480u, 1u, 1.0f);
  const auto second = block(480u, 2u, 2.0f);
  emke::audio::PcmBlock output;

  const std::size_t before = allocation_count.load(std::memory_order_relaxed);
  EXPECT(context, ring.push(first));
  EXPECT(context, ring.pop(output));
  EXPECT(context, ring.push(second));
  ring.clear();
  const std::size_t after = allocation_count.load(std::memory_order_relaxed);

  EXPECT(context, before == after);
  EXPECT(context, ring.capacity() == 2u);
  EXPECT(context, !ring.pop(output));
  EXPECT(context, ring.push(first));
  EXPECT(context, ring.pop(output));
  EXPECT(context, output.timestamp == 1u);
}

}  // namespace

void* operator new(std::size_t size) {
  allocation_count.fetch_add(1u, std::memory_order_relaxed);
  if (void* pointer = std::malloc(size == 0u ? 1u : size)) {
    return pointer;
  }
  throw std::bad_alloc();
}

void* operator new[](std::size_t size) {
  return ::operator new(size);
}

void operator delete(void* pointer) noexcept {
  std::free(pointer);
}

void operator delete[](void* pointer) noexcept {
  ::operator delete(pointer);
}

void operator delete(void* pointer, std::size_t) noexcept {
  ::operator delete(pointer);
}

void operator delete[](void* pointer, std::size_t) noexcept {
  ::operator delete(pointer);
}

static_assert(
    noexcept(std::declval<emke::audio::SpscBlockRing&>().push(
        std::declval<const emke::audio::PcmBlock&>())));
static_assert(
    noexcept(std::declval<emke::audio::SpscBlockRing&>().pop(
        std::declval<emke::audio::PcmBlock&>())));

int run_spsc_ring_tests() {
  TestContext context;
  test_capacity_is_fixed_after_construction(context);
  test_start_wait_exits_when_stop_requested_before_start(context);
  test_single_producer_consumer_preserves_order(context);
  test_push_rejects_frame_counts_outside_block_boundary(context);
  test_write_beyond_capacity_fails_without_overwrite(context);
  test_read_from_empty_returns_false(context);
  test_wraparound_preserves_metadata(context);
  test_clear_resets_indices_without_allocation(context);
  return context.failures();
}
