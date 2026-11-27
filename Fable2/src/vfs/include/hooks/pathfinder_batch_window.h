// The async pathfinder's batch window: open from the guest's SetEvent on LaunchPF until its
// SetEvent on FinishedPF, and a bounded wait for it to close. Pure logic (std only), so
// tools/tests/pathfinder_batch_window_test.cpp runs it without the guest. The guest side, the
// addresses and why the wait exists are in src/hooks/pathfinder_batch.cpp.
#pragma once

#include <algorithm>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <mutex>

namespace refii::vfs {

class PathfinderBatchWindow {
 public:
  using Clock = std::chrono::steady_clock;

  /// One world tick's arrival at the window.
  struct Entry {
    bool was_open = false;   // a batch launched earlier had not finished when the tick arrived
    bool timed_out = false;  // the bound expired with that batch still open
    double age_ms = 0.0;     // time from that batch's launch to the tick's arrival
    double waited_ms = 0.0;  // time the tick spent here
  };

  /// Run totals. Nothing resets them; a reader derives rates from deltas between two snapshots.
  struct Totals {
    uint64_t ticks = 0;       // world ticks that arrived
    uint64_t open_ticks = 0;  // ... with a batch still open
    uint64_t timeouts = 0;    // ... whose wait hit the bound
    uint64_t launches = 0;
    uint64_t finishes = 0;
    uint64_t batches = 0;     // worker batches timed by BatchRan
    double max_age_ms = 0.0;
    double max_wait_ms = 0.0;
    double total_wait_ms = 0.0;
    double max_batch_ms = 0.0;
    double total_batch_ms = 0.0;
  };

  /// The guest is about to signal LaunchPF. Call BEFORE the signal: the worker can finish
  /// before the signalling call returns.
  void Launched(Clock::time_point now) {
    std::lock_guard lock(mutex_);
    open_ = true;
    launched_at_ = now;
    ++totals_.launches;
  }

  /// The worker is about to signal FinishedPF. Call BEFORE the signal: the guest's own waiter
  /// on FinishedPF cannot pass until the signal, so its next launch is ordered after this.
  void Finished() {
    {
      std::lock_guard lock(mutex_);
      open_ = false;
      ++totals_.finishes;
    }
    closed_.notify_all();
  }

  /// A world tick arrives: wait while a batch is open, at most `bound`. A bound that expires
  /// closes the window, so a launch that never finishes costs one bound, not one per tick.
  Entry WaitClosed(Clock::duration bound) {
    const Clock::time_point arrived = Clock::now();
    Entry entry;
    std::unique_lock lock(mutex_);
    ++totals_.ticks;
    if (!open_) {
      return entry;
    }
    entry.was_open = true;
    entry.age_ms = Ms(arrived - launched_at_);
    ++totals_.open_ticks;
    if (!closed_.wait_until(lock, arrived + bound, [this] { return !open_; })) {
      entry.timed_out = true;
      open_ = false;
      ++totals_.timeouts;
    }
    entry.waited_ms = Ms(Clock::now() - arrived);
    totals_.max_age_ms = std::max(totals_.max_age_ms, entry.age_ms);
    totals_.max_wait_ms = std::max(totals_.max_wait_ms, entry.waited_ms);
    totals_.total_wait_ms += entry.waited_ms;
    return entry;
  }

  /// One worker batch's wall time.
  void BatchRan(double ms) {
    std::lock_guard lock(mutex_);
    ++totals_.batches;
    totals_.max_batch_ms = std::max(totals_.max_batch_ms, ms);
    totals_.total_batch_ms += ms;
  }

  bool IsOpen() const {
    std::lock_guard lock(mutex_);
    return open_;
  }

  Totals Snapshot() const {
    std::lock_guard lock(mutex_);
    return totals_;
  }

  static double Ms(Clock::duration d) {
    return std::chrono::duration<double, std::milli>(d).count();
  }

 private:
  mutable std::mutex mutex_;
  std::condition_variable closed_;
  bool open_ = false;
  Clock::time_point launched_at_{};
  Totals totals_{};
};

}  // namespace refii::vfs
