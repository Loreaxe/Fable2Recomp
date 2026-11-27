// The pathfinder batch window (Fable2/src/vfs/include/hooks/pathfinder_batch_window.h): open from
// LaunchPF to FinishedPF, a world tick waits while it is open, at most the bound, and a bound that
// expires closes it.
// Build: c++ -std=c++20 -pthread -I Fable2/src/vfs/include tools/tests/pathfinder_batch_window_test.cpp && ./a.out

#include <cassert>
#include <chrono>
#include <cstdio>
#include <thread>

#include "hooks/pathfinder_batch_window.h"

using namespace std::chrono_literals;
using refii::vfs::PathfinderBatchWindow;

int main() {
    // No batch launched: the tick passes at once and is counted.
    {
        PathfinderBatchWindow w;
        const auto e = w.WaitClosed(2000ms);
        assert(!e.was_open && !e.timed_out && e.waited_ms == 0.0);
        const auto t = w.Snapshot();
        assert(t.ticks == 1u && t.open_ticks == 0u && t.timeouts == 0u);
    }

    // Launched and finished before the tick: closed, no wait.
    {
        PathfinderBatchWindow w;
        w.Launched(PathfinderBatchWindow::Clock::now());
        assert(w.IsOpen());
        w.Finished();
        assert(!w.IsOpen());
        const auto e = w.WaitClosed(2000ms);
        assert(!e.was_open);
        const auto t = w.Snapshot();
        assert(t.launches == 1u && t.finishes == 1u && t.open_ticks == 0u);
    }

    // The tick arrives with the batch open; the worker finishes 40 ms later: the tick waits for it,
    // well inside the bound, and the age is measured from the launch.
    {
        PathfinderBatchWindow w;
        const auto launched = PathfinderBatchWindow::Clock::now();
        w.Launched(launched);
        std::thread worker([&w] {
            std::this_thread::sleep_for(40ms);
            w.Finished();
        });
        std::this_thread::sleep_for(10ms);
        const auto e = w.WaitClosed(2000ms);
        worker.join();
        assert(e.was_open && !e.timed_out);
        assert(e.age_ms >= 9.0);
        assert(e.waited_ms >= 20.0 && e.waited_ms < 1000.0);
        const auto t = w.Snapshot();
        assert(t.open_ticks == 1u && t.timeouts == 0u);
        assert(t.max_wait_ms == e.waited_ms && t.total_wait_ms == e.waited_ms);
        assert(t.max_age_ms == e.age_ms);
        assert(!w.IsOpen());
    }

    // A launch that never finishes: the bound ends the wait, the window closes, and the next tick
    // does not pay the bound again.
    {
        PathfinderBatchWindow w;
        w.Launched(PathfinderBatchWindow::Clock::now());
        const auto e = w.WaitClosed(30ms);
        assert(e.was_open && e.timed_out);
        assert(e.waited_ms >= 29.0);
        assert(!w.IsOpen());
        const auto again = w.WaitClosed(30ms);
        assert(!again.was_open && !again.timed_out);
        const auto t = w.Snapshot();
        assert(t.ticks == 2u && t.open_ticks == 1u && t.timeouts == 1u);
    }

    // A finish with no launch (the first wake after a rebuilt manager) is harmless.
    {
        PathfinderBatchWindow w;
        w.Finished();
        assert(!w.IsOpen());
        assert(!w.WaitClosed(30ms).was_open);
    }

    // A launch after a time-out reopens the window; the totals keep counting across both.
    {
        PathfinderBatchWindow w;
        w.Launched(PathfinderBatchWindow::Clock::now());
        assert(w.WaitClosed(5ms).timed_out);
        w.Launched(PathfinderBatchWindow::Clock::now());
        assert(w.IsOpen());
        std::thread worker([&w] {
            std::this_thread::sleep_for(5ms);
            w.Finished();
        });
        const auto e = w.WaitClosed(2000ms);
        worker.join();
        assert(e.was_open && !e.timed_out);
        const auto t = w.Snapshot();
        assert(t.launches == 2u && t.finishes == 1u && t.open_ticks == 2u && t.timeouts == 1u);
    }

    // Batch timings: count, longest and total.
    {
        PathfinderBatchWindow w;
        w.BatchRan(12.5);
        w.BatchRan(31.0);
        w.BatchRan(3.5);
        const auto t = w.Snapshot();
        assert(t.batches == 3u && t.max_batch_ms == 31.0 && t.total_batch_ms == 47.0);
    }

    // Many launch/finish rounds against a waiting tick on another thread: every tick returns,
    // none times out, and launches equal finishes.
    {
        PathfinderBatchWindow w;
        constexpr int kRounds = 200;
        std::thread worker([&w] {
            for (int i = 0; i < kRounds; ++i) {
                w.Launched(PathfinderBatchWindow::Clock::now());
                std::this_thread::sleep_for(200us);
                w.Finished();
            }
        });
        for (int i = 0; i < kRounds; ++i) {
            assert(!w.WaitClosed(2000ms).timed_out);
        }
        worker.join();
        const auto t = w.Snapshot();
        assert(t.launches == static_cast<uint64_t>(kRounds) && t.finishes == t.launches);
        assert(t.timeouts == 0u && t.ticks == static_cast<uint64_t>(kRounds));
    }

    std::puts("pathfinder_batch_window_test: ok");
    return 0;
}
