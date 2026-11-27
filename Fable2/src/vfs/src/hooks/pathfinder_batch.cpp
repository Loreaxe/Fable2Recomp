// The async pathfinder's batch, kept to the console's timing (addresses TU1 / disc).
//
// THE GUEST'S HANDSHAKE. The world sim tick (sub_82275F68 / sub_82275F00) takes the world lock
// (stru_83498D14 / unk_83499138) and, holding it, waits for the pathfinder worker with no timeout:
//
//     82276184 / 8227611C  bl lhScopedLock_Acquire          ; r5 = 0, blocking
//     822761A0 / 82276138  bl sub_8229E648 / sub_8229E2D8   ; -> sub_822BA7D0 / sub_822BA4F0
//       822BA838 / 822BA558  bl sub_82E812A0 / sub_82E79D88 ; budget := 1e-6 ms ("stop")
//       822BA860 / 822BA580  bl sub_8217A310 / sub_82179FB0 ; wait FinishedPF, r6 = -1 (INFINITE)
//       822BA8B0 / 822BA5D0  bl sub_82E812A0 / sub_82E79D88 ; budget := 30.0 ms
//       822BA8BC / 822BA5DC  bl rex_SetEvent                ; LaunchPF
//     822761B4 / 8227614C  bl RtlLeaveCriticalSection
//
// The worker (sub_826194D0 / sub_8262DDD0) waits LaunchPF (82619508 / 8262DE08), runs the batch
// (sub_82619570 / sub_8262DE70, every queued search's vf37) and signals FinishedPF (8261951C /
// 8262DE1C) through the same rex_SetEvent. A search's brain callback (sub_826270F8 /
// sub_8263B960) takes the SAME world lock, blocking (8262711C / 8263B984). LaunchPF and FinishedPF
// are auto-reset (CreateEventA(0,0,0,..) at 822CFEAC/822CFED0 / 822D062C/822D0650), every wake of
// the worker ends in exactly one FinishedPF, and the title never closes the two handles.
//
// The inversion is the guest's own. The console never closes it because of time alone: the batch
// is budgeted by Kaim's CTimeManager (+40), which GrantAperiodicTask_0 (82E81758 / 82E7A240,
// called at each search and inside the graph traversals) compares with KsGetIntraFrameTime
// (822BDCB0 / 822BD9D0), a QueryPerformanceCounter (`mftb`) accumulator the batch restarts through
// sub_82171B10 at 82619598 / 8262DE98 (Kaim's callback block dword_832FDAC0 / dword_832FD980,
// +8 and +0xC: the skeleton's +0x28/+0x2C, read by sub_82EAF780 / sub_82EA8FD0 and copied by
// sub_82E7F6E8 / sub_82E78FF8). The batch stops within 30 ms of wall time, and game ticks are at
// least one 33.3 ms HF period apart in wall time (lhGameLoop_Tick advances one step per pass and
// re-bases, 82276FA4 / 82276EC4), so the worker is idle before the next tick takes the lock.
//
// Our driven time base breaks both halves: `mftb` holds still within a step, so the 30 ms never
// elapse, and granted steps are released back to back, so the next tick takes the lock while a
// search is still running and the search's callback then queues on the lock forever. Observed
// 2026-11-27 11:24:52 (Bowerstone Market, int7): game thread at 822BA860, worker in sub_826270F8's
// acquire, lock = one owner (recursion 1) plus one waiter. Both fixes below are load-bearing for
// that, and nothing else changes:
//
// 1. The batch runs on the real clock (refii_bridge_rex_time_base_real_slice, this thread only),
//    so the guest's own 30 ms budget and its 1e-6 "stop" mean what they say.
// 2. The tick waits, BEFORE the guest's body and so outside the world lock, while the batch the
//    previous tick launched is still open. No lock is held at that entry: CGameModePlayGame vf6
//    leaves the world lock at 821D467C / 821D442C before calling the tick (821D49D8 / 821D4788),
//    lhGameLoop_Tick leaves its own at 82276EAC / 82276DCC, and the cutscene caller (sub_823D9898 /
//    sub_82460E78) takes none. The wait is what the console spends between two ticks anyway; the
//    guest's own wait at 822BA860 then returns at once.
#include "hooks/pathfinder_batch.h"

#include <chrono>
#include <cstdint>

#include <rex/logging.h>

#include "core/guest_build.h"
#include "core/guest_state.h"
#include "core/rex_payload_bridge.h"
#include "hooks/pathfinder_batch_window.h"
#include "hooks/ppc_externs.h"
#include "vfs_hooks_private.h"

namespace {

using refii::vfs::PathfinderBatchWindow;

constexpr uint32_t kLaunchPf = 0x83496CB4u;        // disc 0x83496AEC, HANDLE
constexpr uint32_t kFinishedPf = 0x83496CB8u;      // disc 0x83496AF0, HANDLE
constexpr uint32_t kQueuedSearches = 0x83496CC4u;  // disc 0x83496AFC, the batch's count

// Ends only OUR wait: past it the guest's own unbounded wait at 822BA860 follows, as today. A
// batch on the real clock ends within its 30 ms budget plus one search; the longest frame seen
// under the frozen clock was 685 ms. Two seconds is far past both, and short enough that a
// launch with no finish is one visible hitch, logged.
constexpr auto kWaitBound = std::chrono::milliseconds(2000);

// Every arrival that found the batch open is logged up to this many; after it, only a new
// longest age and every time-out. The summary carries the complete counts.
constexpr uint64_t kWarnEach = 256u;
constexpr auto kSummaryEvery = std::chrono::seconds(10);
constexpr auto kQuietSummaryEvery = std::chrono::seconds(60);

PathfinderBatchWindow g_window;

// Game thread only (WaitForPathfinderBatch).
PathfinderBatchWindow::Totals g_last_summary{};
PathfinderBatchWindow::Clock::time_point g_last_summary_at{};
double g_longest_age_ms = 0.0;

void Summarise(PathfinderBatchWindow::Clock::time_point now) {
  if (g_last_summary_at == PathfinderBatchWindow::Clock::time_point{}) {
    g_last_summary_at = now;
    return;
  }
  const PathfinderBatchWindow::Totals t = g_window.Snapshot();
  const bool opened = t.open_ticks != g_last_summary.open_ticks;
  if (now - g_last_summary_at < (opened ? kSummaryEvery : kQuietSummaryEvery)) {
    return;
  }
  const double secs = PathfinderBatchWindow::Ms(now - g_last_summary_at) / 1000.0;
  const uint64_t batches = t.batches - g_last_summary.batches;
  const double batch_ms = t.total_batch_ms - g_last_summary.total_batch_ms;
  REXLOG_INFO(
      "[pf-batch] {:.1f}s: ticks {} (+{}), entered with the batch open {} (+{}), waited {:.1f} ms "
      "(+{:.1f}, longest {:.1f}), time-outs {} (+{}), batches {} (+{}, mean {:.2f} ms, longest "
      "{:.2f}), launches {} finishes {}, longest age {:.1f} ms",
      secs, t.ticks, t.ticks - g_last_summary.ticks, t.open_ticks,
      t.open_ticks - g_last_summary.open_ticks, t.total_wait_ms,
      t.total_wait_ms - g_last_summary.total_wait_ms, t.max_wait_ms, t.timeouts,
      t.timeouts - g_last_summary.timeouts, t.batches, batches,
      batches != 0u ? batch_ms / static_cast<double>(batches) : 0.0, t.max_batch_ms, t.launches,
      t.finishes, t.max_age_ms);
  g_last_summary = t;
  g_last_summary_at = now;
}

}  // namespace

namespace refii::vfs {

void WaitForPathfinderBatch(uint8_t* base) {
  const uint32_t queued = g_window.IsOpen()
                              ? LoadGuestU32(base, refii::guest::Address(kQueuedSearches))
                              : 0u;
  const PathfinderBatchWindow::Entry entry = g_window.WaitClosed(kWaitBound);
  if (entry.was_open) {
    const uint64_t n = g_window.Snapshot().open_ticks;
    const bool longest = entry.age_ms > g_longest_age_ms;
    if (longest) {
      g_longest_age_ms = entry.age_ms;
    }
    if (entry.timed_out) {
      REXLOG_WARN(
          "[pf-batch] #{}: the world tick waited {:.1f} ms and the pathfinder batch launched "
          "{:.1f} ms earlier ({} searches queued) has not signalled FinishedPF; the tick goes on "
          "into the guest's own wait",
          n, entry.waited_ms, entry.age_ms, queued);
    } else if (n <= kWarnEach || longest) {
      REXLOG_WARN(
          "[pf-batch] #{}: the world tick arrived {:.1f} ms after the pathfinder batch's launch "
          "with it still open ({} searches queued); waited {:.1f} ms outside the world lock{}",
          n, entry.age_ms, queued, entry.waited_ms, longest ? " (longest so far)" : "");
    }
  }
  Summarise(PathfinderBatchWindow::Clock::now());
}

}  // namespace refii::vfs

// TU1 sub_822C9388 / disc sub_822C9380, the title's SetEvent (`bl NtSetEvent` at 822C9398 /
// 822C9390; IDA calls it PulseEvent). Every guest SetEvent passes here; the two pathfinder handles
// are read at each call, so a manager rebuilt with new events is followed.
REX_EXTERN(rex_SetEvent) {
  REX_FUNC_PROLOGUE();
  const uint32_t handle = ctx.r3.u32;
  if (handle != 0u) {
    if (handle == refii::vfs::LoadGuestU32(base, refii::guest::Address(kLaunchPf))) {
      g_window.Launched(PathfinderBatchWindow::Clock::now());
    } else if (handle == refii::vfs::LoadGuestU32(base, refii::guest::Address(kFinishedPf))) {
      g_window.Finished();
    }
  }
  __imp__rex_SetEvent(ctx, base);
}

// TU1 sub_82619570 / disc sub_8262DE70, the worker's batch: its only caller is the worker loop
// (82619514 / 8262DE14). It restarts Kaim's intra-frame clock first (82619598 / 8262DE98), so
// inside the slice every budget read is wall time since the batch began.
REX_EXTERN(rex_AIAsyncPathfinder_RunBatch) {
  REX_FUNC_PROLOGUE();
  const auto started = PathfinderBatchWindow::Clock::now();
  refii_bridge_rex_time_base_real_slice(1);
  __imp__rex_AIAsyncPathfinder_RunBatch(ctx, base);
  refii_bridge_rex_time_base_real_slice(0);
  g_window.BatchRan(PathfinderBatchWindow::Ms(PathfinderBatchWindow::Clock::now() - started));
}
