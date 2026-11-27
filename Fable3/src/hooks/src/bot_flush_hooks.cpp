// refii - the AI's deferred bot deletes and its pathfinder worker on the DISC build: the two changes
// title update 3 made to them, carried to the disc executable. TU3 runs its own code for both; these
// hooks are rows of the disc config alone (Fable3/versions/Fable3/refiii_config.toml).
//
// THE DEFERRED DELETE (disc sub_821AD4A0, TU3 sub_824605E0). Under the AI lock (disc stru_835573FC,
// TU3 stru_83597F38) it hands each queued (world, bot) pair's pathfinder to the worker's cancel list
// and deletes the bot, then clears the queue (disc 0x821AD56C..0x821AD604). The disc takes the lock
// with a try that gives up:
//   821ad4d8  lwz r11, dword_83556598 ; cmpwi cr6,r11,0xA      ; ten misses in a row ...
//   821ad4ec  bge cr6, loc_821AD53C                          ; ... -> RtlEnterCriticalSection
//   821ad4f4  bl  RtlTryEnterCriticalSection                 ; else try,
//   821ad510  bl  sub_822680B0 (1)                            ; sleep 1 ms (SleepEx), try once more,
//   821ad528  lwz/addi/stw dword_83556598                    ; and return with the queue untouched
// TU3 adds an argument that takes the blocking enter straight away:
//   82460614  clrlwi r11,r3,24 ; cmplwi cr6,r11,0 ; bne cr6, loc_82460688   ; -> RtlEnterCriticalSection
// and passes 1 at the four load, save and unload sites and 0 at the two per-frame ones:
//   TU3 0x823BCACC (LevelManager_Unload's helper sub_823BC720), 0x822F8A30 (SaveGame sub_822F8940),
//   0x8270A77C (CmdLoadSaveGame_Update), 0x82756C04 (CGameModeLoadLevel_vf6): `li r3,1`;
//   TU3 0x82206F88, 0x82207084 (sub_82206F50): `li r3,0`.
// The disc's six calls are the same six, without the argument: 0x8239A850, 0x822F50FC, 0x826DD774,
// 0x82722B08 and the per-frame 0x82207778, 0x82207870 (sub_82207740). After either enter both builds
// clear the miss counter (disc 0x821AD54C, TU3 0x824606A4); an empty queue returns before any of it
// (disc 0x821AD4D0, TU3 0x82460610).
//
// So at disc 0x821AD4EC, the `bge` that picks the blocking enter, RefiiF3BotFlushBlocks takes that
// branch when the call came from one of the four sites — TU3's `bne loc_82460688` at the same decision,
// with TU3's own condition. The return address is r12: `mflr r12` at 0x821AD4A0, which
// __savegprlr_23 only stores and nothing up to 0x821AD4EC writes. The per-frame calls keep the try.
// Without it a flush from those sites returns with the queue untouched whenever the worker holds the
// lock past the retry (its Kaim update, its pass), and the queued bots outlive the world they belong
// to, until a later flush deletes them (the queue's only drain, 0x821AD604). RefiiF3BotFlushLocked
// says, for each forced flush, how long the lock was held against it: past the retry's 1 ms the disc
// skipped it.
//
// THE WORKER'S PASS (disc sub_8238F9E8, TU3 sub_823AE230). It copies the live queue
// (array disc dword_83556564 / TU3 dword_83597058, count dword_83556568 / dword_8359705C) into a
// snapshot, then per entry, each under the lock, skips it when null or on the cancel list and calls
// its slot +0x90. TU3 also skips an entry no longer in the live queue, before the cancel walk:
//   823ae3a8  lwz r9, dword_8359705C ; beq -> 823AE3D4
//   823ae3b4  lwz r10, dword_83597058 ; 823ae3b8 lwz r8,0(r10) ; cmplw r8,r3 ; beq ; addi ; blt
//   823ae3d4  cmplw r11, r9 ; beq loc_823AE420               ; not found -> past the call
// RefiiF3PathfinderUnqueued is that test at disc 0x8238FB94 — where the disc's null and cancel
// tests have passed and the call is next (`lwz r11,0(r3)` .. `bctrl` 0x8238FB94..0x8238FBA0) — and a
// true answer goes past the call to 0x8238FBA4, as TU3's goes to 0x823AE420. The two skips are pure
// tests on the same entry under the same lock, so their order does not change which entries are called.

#include <atomic>
#include <chrono>
#include <cstdint>

#include <rex/logging.h>
#include <rex/ppc.h>
#include <rex/ppc/context.h>
#include <rex/system/kernel_state.h>
#include <rex/system/xmemory.h>

#include "gui_statements.h"
#include "../../vfs/include/fable3_guest_build.h"

namespace {

using Clock = std::chrono::steady_clock;
using refii::fable3::LoadGuestU32;

/// The four load, save and unload sites TU3 forces, by the return address of their call (the TU3
/// address names it; the disc's is the running build's).
struct ForcedSite {
  refii::guest::Addr returns;
  const char* name;
};
const ForcedSite kForcedSites[] = {
    {refii::guest::Addr{0x823BCAD8u}, "the level unload"},     // disc 0x8239A854
    {refii::guest::Addr{0x822F8A38u}, "the save"},             // disc 0x822F5100
    {refii::guest::Addr{0x8270A784u}, "the save-game load"},   // disc 0x826DD778
    {refii::guest::Addr{0x82756C0Cu}, "the level load"},       // disc 0x82722B0C
};

/// The deferred queues' counts: the (world, bot) pairs (disc dword_83557CD4, read at 0x821AD4C8) and the
/// list beside them (disc dword_83557CE0, 0x821AD4BC) the flush also drains.
const refii::guest::Addr kQueuedBots{0x835987E4u};
const refii::guest::Addr kQueuedOther{0x835987F0u};
/// The worker's live queue.
const refii::guest::Addr kQueueArray{0x83597058u};
const refii::guest::Addr kQueueCount{0x8359705Cu};

/// The disc's retry sleeps this long before its last try (sub_822680B0(1) -> KeDelayExecutionThread
/// -10000 x 100 ns); a lock held longer than this from the first try is a flush it skipped.
constexpr double kRetryMs = 1.0;

uint8_t* GuestBase() {
  rex::system::KernelState* state = rex::system::kernel_state();
  rex::memory::Memory* memory = state != nullptr ? state->memory() : nullptr;
  return memory != nullptr ? memory->virtual_membase() : nullptr;
}

/// The forced flush this thread is inside, from the branch to the enter it waits in.
struct ForcedFlush {
  const char* site = nullptr;
  Clock::time_point began{};
  uint32_t bots = 0u;
  uint32_t other = 0u;
};
thread_local ForcedFlush t_forced;

std::atomic<uint64_t> g_unqueued_skips{0};

}  // namespace

/// Disc 0x821AD4EC, before `bge cr6, loc_821AD53C`; true goes to the blocking enter (0x821AD53C).
bool RefiiF3BotFlushBlocks(PPCRegister& r12) {
  const uint32_t returns = r12.u32;
  for (const ForcedSite& site : kForcedSites) {
    if (returns != static_cast<uint32_t>(site.returns)) {
      continue;
    }
    t_forced = ForcedFlush{};
    t_forced.site = site.name;
    t_forced.began = Clock::now();
    if (const uint8_t* base = GuestBase(); base != nullptr) {
      t_forced.bots = LoadGuestU32(base, kQueuedBots);
      t_forced.other = LoadGuestU32(base, kQueuedOther);
    }
    return true;
  }
  return false;
}

/// Disc 0x821AD540, where both enters arrive with the AI lock held.
void RefiiF3BotFlushLocked() {
  if (t_forced.site == nullptr) {
    return;
  }
  const double waited = std::chrono::duration<double, std::milli>(Clock::now() - t_forced.began).count();
  if (waited >= kRetryMs) {
    REXLOG_WARN("Refii AI: {} flush waited {:.2f} ms for the AI lock with {} bot delete(s) and {} other "
                "entr(ies) queued; the disc's try-lock would have SKIPPED it (TU3's blocking enter taken)",
                t_forced.site, waited, t_forced.bots, t_forced.other);
  } else {
    REXLOG_INFO("Refii AI: {} flush took the AI lock in {:.2f} ms with {} bot delete(s) and {} other "
                "entr(ies) queued; the disc's try-lock would have run it too",
                t_forced.site, waited, t_forced.bots, t_forced.other);
  }
  t_forced = ForcedFlush{};
}

/// Disc 0x8238FB94, the worker about to call the entry's slot +0x90 (r3 the entry), the AI lock
/// held; true goes past the call (0x8238FBA4).
bool RefiiF3PathfinderUnqueued(PPCRegister& r3) {
  const uint8_t* base = GuestBase();
  if (base == nullptr) {
    return false;
  }
  const uint32_t entry = r3.u32;
  const uint32_t count = LoadGuestU32(base, kQueueCount);
  const uint32_t array = LoadGuestU32(base, kQueueArray);
  // The manager's destructor frees the array and zeroes its word before it stops the worker (disc
  // 0x8238F5BC / 0x8238F5C8): a zero array holds nothing.
  if (array != 0u) {
    for (uint32_t i = 0; i < count; ++i) {
      if (LoadGuestU32(base, array + (i * 4u)) == entry) {
        return false;
      }
    }
  }
  const uint64_t skipped = g_unqueued_skips.fetch_add(1u, std::memory_order_relaxed) + 1u;
  REXLOG_WARN("Refii AI: the pathfinder worker passed over 0x{:08X}, no longer in the live queue "
              "(array 0x{:08X}, {} queued; TU3's re-check; {} so far)",
              entry, array, count, skipped);
  return true;
}
