// refii - the census of the pad's road into Fable 3's GUI, counted where the two GUI input receivers
// (CGUIInput, player one's and player two's `vf1`) hand an action on. OBSERVE ONLY: both functions
// run as the guest has them. The mask that withholds the pad from them goes in here once every
// surface behind them has its lever; until then the two counters say how much pad still arrives.
// TU3 / disc; each has exactly two callers, the two `vf1`s.
//   - THE SCREEN ROUTE sub_826019F0 / disc sub_825D6A08 (recv, action*): START's pause (`bl
//     sub_82332FB8`), the modal input and the box router (`bl sub_825F9408` 0x82601B74 / disc
//     sub_825CE380 0x825D6B8C, `bl sub_825F38F8` 0x82601BB4 / disc sub_825C8B68 0x825D6BCC) and the
//     open screen object's slot 5 (`bctrl` 0x82601C60 / 0x825D6C78). Callers 0x8260113C, 0x82B5160C /
//     disc 0x825D6158, 0x82B13714.
//   - THE CHANNEL DISPATCHER sub_825FC860 / disc sub_825D1710 (D, action*, player): walks the nine
//     registered channels in order and stops at the first that answers non-zero. Callers 0x82601178,
//     0x82B51648 / disc 0x825D6194, 0x82B13750.
// One line a minute while either moved: the pad actions handed to each since the last line.

#include <atomic>
#include <chrono>
#include <cstdint>

#include <rex/logging.h>
#include <rex/ppc.h>

REX_EXTERN(__imp__rex_GuiInput_Route);
REX_EXTERN(__imp__rex_GuiInput_Dispatch);

namespace {

std::atomic<uint64_t> g_routed{0u};
std::atomic<uint64_t> g_dispatched{0u};
std::atomic<uint64_t> g_reported_routed{0u};
std::atomic<uint64_t> g_reported_dispatched{0u};
std::atomic<int64_t> g_last_report{0};

void Report() {
  const int64_t now = std::chrono::duration_cast<std::chrono::seconds>(
                          std::chrono::steady_clock::now().time_since_epoch())
                          .count();
  int64_t last = g_last_report.load();
  if (now - last < 60 || !g_last_report.compare_exchange_strong(last, now)) {
    return;
  }
  const uint64_t routed = g_routed.load();
  const uint64_t dispatched = g_dispatched.load();
  const uint64_t was_routed = g_reported_routed.exchange(routed);
  const uint64_t was_dispatched = g_reported_dispatched.exchange(dispatched);
  if (routed != was_routed || dispatched != was_dispatched) {
    REXLOG_INFO("[fable3-input] pad census (observe only, nothing withheld): {} action(s) to the GUI screen route, "
                "{} to the nine-channel dispatcher in the last minute (run totals {} / {})",
                routed - was_routed, dispatched - was_dispatched, routed, dispatched);
  }
}

}  // namespace

/// (recv, action*): counted.
REX_EXTERN(rex_GuiInput_Route) {
  g_routed.fetch_add(1u);
  __imp__rex_GuiInput_Route(ctx, base);
  Report();
}

/// (D, action*, player): counted.
REX_EXTERN(rex_GuiInput_Dispatch) {
  g_dispatched.fetch_add(1u);
  __imp__rex_GuiInput_Dispatch(ctx, base);
}
