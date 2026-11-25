// refii — the guest tick pump's Fable 2 state: what the pump samples and publishes.
//
// THIS FILE MUST BE IN librefii_vfs, NOT IN THE PPC HOOK SET, for exactly the reason
// bink_frame_state.cpp is: the two ends of this state live in different libraries. The PAYLOAD
// publishes (the pump hook in Fable2/src/vfs/src/hooks/guest_tick_pump.cpp, on the guest's own
// tick thread) and the HOST reads (the host bridge, on Godot's thread). Both link refii_vfs, so
// this is the one place they can agree. The queue the host posts onto the tick thread, and its
// counters, are shared: src/runtime/src/core/guest_tick_queue.cpp, in refii_runtime.
//
// IT USED TO LIVE BESIDE THE PUMP, which left the host bridge naming five symbols only the
// payload defined. That resolves on Linux and cannot resolve on Windows, and the difference
// is not a build flag: the payload is dlopen'd RTLD_GLOBAL, so an undefined reference in
// librefii_hostbridge.so binds to it at load time, while a Windows DLL is linked closed and
// has no global scope to fall back on. lld-link says so as five undefined symbols. Linux was
// never right either — refii_payload/payload.h states that NOTHING WE SHIP MAY LINK AGAINST
// THE PAYLOAD, because the payload is built from the player's own game image and is absent
// on a build machine. Windows is just the platform that enforces it.
//
// The host reaches these as ordinary C++ symbols; both sides of that call are our build. The
// PAYLOAD reaches them through the C entries in core/payload_bridge_fable2.h, because its side
// of that call is the player's compiler — see core/payload_bridge.h for the contract.

#include <algorithm>
#include <array>
#include <atomic>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <cstdio>
#include <string>
#include <unordered_map>
#include <vector>

#include <rex/kernel/xboxkrnl/threading.h>
#include <rex/logging.h>
#include <rex/system/xmemory.h>

#include "core/payload_bridge_fable2.h"
#include "core/traced_mutex.h"
#include "core/hero_derived.h"
#include "core/guest_build.h"

namespace refii::vfs {

static std::atomic<uint64_t> g_newGameResult{0};
static std::atomic<uint32_t> g_newGameGeneration{0};

extern "C" void refii_bridge_new_game_result(uint32_t success) {
  const uint64_t sequence = (g_newGameResult.load(std::memory_order_relaxed) >> 1) + 1;
  g_newGameResult.store((sequence << 1) | (success ? 1u : 0u), std::memory_order_release);
}

extern "C" void refii_bridge_new_game_generation(void) {
  g_newGameGeneration.fetch_add(1u, std::memory_order_release);
}

uint64_t NewGameTransitionResult(uint32_t* generation) {
  if (generation) *generation = g_newGameGeneration.load(std::memory_order_acquire);
  return g_newGameResult.load(std::memory_order_acquire);
}

namespace {

/// The saved-game count, sampled by the payload calling the guest's own probe on the tick
/// thread.
///
/// -1 means "not asked yet" and is NOT the same as 0. A host that cannot tell them apart draws
/// a menu with Load Game missing and no way to know whether that is the answer or the absence
/// of one — which is exactly the bug this replaces, where an empty vector stood in for both.
std::atomic<int32_t> g_save_count{-1};

/// See FrontEndSubtitles().
std::atomic<int32_t> g_subtitles{-1};

/// See PauseTabsMask(). 0 means "the pump has not asked the guest yet" and is NOT the same as
/// "no tabs": the host fails open to all-available on 0, because a pause book with every tab
/// missing is unusable, whereas an extra tab merely pushes a screen the guest would have
/// skipped.
std::atomic<uint32_t> g_pause_tabs{0};

/// Set to ask for a fresh sample. Starts true so the first front-end tick takes one, and is set
/// again whenever the host knows the answer has changed — after a game is started, say.
///
/// Sampled rather than polled every tick because the probe is not free: it allocates a 46 320
/// byte guest stack frame and walks up to 150 content entries. The guest itself asks exactly
/// once, at menu spawn.
std::atomic<bool> g_resample{true};

/// Guest memory, big-endian, for the loading-screen finisher below.
///
/// Local copies rather than core/guest_state.h's, which are defined in
/// internal_state_guest.cpp — a PAYLOAD-only translation unit that refii_vfs does not
/// compile and must not import from. They are the same plain loads and stores; the payload's
/// StoreGuestU8 carries one extra guard, for the synthetic startup frame's failure byte at
/// frame+244, and that is not an address this file writes.
uint8_t ReadU8(uint8_t* base, uint32_t at) {
  return *rex::memory::GuestPtr<uint8_t*>(base, at);
}

uint32_t ReadU32(uint8_t* base, uint32_t at) {
  const auto* ptr = rex::memory::GuestPtr<uint8_t*>(base, at);
  return (uint32_t(ptr[0]) << 24) | (uint32_t(ptr[1]) << 16) | (uint32_t(ptr[2]) << 8) |
         uint32_t(ptr[3]);
}

void WriteU8(uint8_t* base, uint32_t at, uint8_t value) {
  *rex::memory::GuestPtr<uint8_t*>(base, at) = value;
}

void WriteU32(uint8_t* base, uint32_t at, uint32_t value) {
  auto* ptr = rex::memory::GuestPtr<uint8_t*>(base, at);
  ptr[0] = static_cast<uint8_t>((value >> 24) & 0xFF);
  ptr[1] = static_cast<uint8_t>((value >> 16) & 0xFF);
  ptr[2] = static_cast<uint8_t>((value >> 8) & 0xFF);
  ptr[3] = static_cast<uint8_t>(value & 0xFF);
}

bool IsGuestPointer(uint32_t value) { return value >= 0x10000 && value < 0x90000000; }

/// Copies into the caller's buffer and returns the length that WOULD have been written, so a
/// caller can size a buffer with a null probe — the convention the whole payload ABI uses.
size_t WriteOut(const std::string& text, char* out, size_t capacity) {
  if (out != nullptr && capacity > 0) {
    const size_t copied = text.size() < capacity - 1 ? text.size() : capacity - 1;
    std::memcpy(out, text.data(), copied);
    out[copied] = '\0';
  }
  return text.size();
}

}  // namespace

/// The HeroState version last APPLIED to each player's body, written by the pump (hooks
/// archive, guest side) and read by the host bridge: like the counters, it lives here because
/// the pump's own globals are not linked into the bridge. [0] player one, [1] player two.
std::atomic<uint32_t> g_herostate_applied_version[2] = {0u, 0u};

/// C linkage: the pump sits in the payload, which may import only C symbols from a host
/// library (Fable2/src/hooks/CMakeLists.txt checks it), like the other `refii_bridge_publish_*`.
extern "C" void refii_bridge_publish_herostate_applied(uint32_t player, uint32_t version) {
  if (player == 1u || player == 2u) {
    g_herostate_applied_version[player - 1u].store(version, std::memory_order_relaxed);
  }
}

uint32_t GuestTickPumpHeroStateApplied(uint32_t player) {
  return player == 1u || player == 2u
             ? g_herostate_applied_version[player - 1u].load(std::memory_order_relaxed)
             : 0u;
}

/// The last debug-call/debug-lua result the pump recorded. One slot, not a ring: calls are
/// serialized on the tick thread and the host polls the sequence number, so a caller that
/// posts, then waits for seq to advance, reads its own answer.
namespace {
std::mutex g_debug_result_mutex;
uint64_t g_debug_result_seq = 0;
uint32_t g_debug_result_addr = 0;
uint64_t g_debug_result_r3 = 0;
std::string g_debug_result_note;
}  // namespace

uint32_t DebugCallResult(uint64_t* out_seq, uint32_t* out_addr, uint64_t* out_r3,
                         char* out_note, uint32_t note_capacity) {
  std::lock_guard<std::mutex> lock(g_debug_result_mutex);
  if (out_seq != nullptr) *out_seq = g_debug_result_seq;
  if (out_addr != nullptr) *out_addr = g_debug_result_addr;
  if (out_r3 != nullptr) *out_r3 = g_debug_result_r3;
  WriteOut(g_debug_result_note, out_note, note_capacity);
  return g_debug_result_seq != 0 ? 1u : 0u;
}

/// The subtitles setting as the guest last stated it. -1 until the pump's first sample.
int32_t FrontEndSubtitles() { return g_subtitles.load(std::memory_order_relaxed); }

/// Which pause tabs the guest's own predicate said it would offer, as the pump last sampled
/// them. Bit N = tab N available for N in 2..8; 0 until the first sample.
uint32_t PauseTabsMask() { return g_pause_tabs.load(std::memory_order_relaxed); }

/// The saved-game count as last sampled, or -1 if it has not been sampled yet.
int32_t GuestSavedGameCount() { return g_save_count.load(std::memory_order_relaxed); }

/// Asks for a fresh sample on the next front-end tick. Safe from any thread.
void RefreshGuestSavedGameCount() { g_resample.store(true, std::memory_order_relaxed); }

/// The in-game loading screen the guest stood up, or 0 when none is up.
///
/// Same three-root walk the host bridge's `loading_screen_object` uses — `sub_82388380`'s own
/// fallback chain (0x83496AB8, then 0x83496AC0, then 0x8335C4C4) -> app -> +8 context -> +36
/// manager -> +4 screen. `manager+4` is 0 when there is no screen, which is the ordinary case
/// out of game and during the front end.
///
/// Here rather than in the pump because BOTH sides walk it: the payload's own finisher on the
/// in-game tick and the host's step finisher below. One copy, so they cannot disagree about
/// which object is the screen.
uint32_t ResolveInGameLoadingScreen(uint8_t* base) {
  if (base == nullptr) {
    return 0;
  }
  for (uint32_t root : {refii::guest::Address(0x83496AB8u), refii::guest::Address(0x83496AC0u), refii::guest::Address(0x8335C4C4u)}) {
    const uint32_t app = ReadU32(base, root);
    if (!IsGuestPointer(app)) continue;
    const uint32_t owner = ReadU32(base, app + 8u);
    if (!IsGuestPointer(owner)) continue;
    const uint32_t manager = ReadU32(base, owner + 36u);
    if (!IsGuestPointer(manager)) continue;
    const uint32_t screen = ReadU32(base, manager + 4u);
    if (IsGuestPointer(screen)) return screen;
  }
  return 0;
}

/// DELIBERATE REPLACEMENT, with its writes: the take-down `sub_8235E5A0` makes inline at
/// 0x8235E7C4 (disc `sub_8235ABA0` 0x8235ADD0), on the render side this build does not run.
void FinishStuckInGameLoadingScreenFromHost(uint8_t* base) {
  if (base == nullptr) {
    return;
  }

  // NEVER CACHE THIS HANDLE, AND NEVER SIGNAL ONE THE SCREEN DID NOT JUST NAME.
  // `screen+180` is a CreateEventA handle (0x8235CB58) that ~CLoadingScreen closes
  // (CloseHandle, 0x8235D10C), and ObjectTable::FindFreeSlot reuses freed slots
  // (`handle = kHandleBase + (slot << 2)`), so a handle held past its screen comes to name a
  // different kernel object. Measured in two core dumps: handles 0xF8000240 and 0xF8000144,
  // kept in a ring here and set every step, had become the XEvents backing the Kynapse world
  // lock (0x83498D14) and the VFS request-manager lock (0x42100018). Setting one releases a
  // critical-section waiter that does not own the lock, and RtlEnterCriticalSection aborts on
  // `owning_thread == 0` (xboxkrnl_rtl.cpp:405).
  const uint32_t screen = ResolveInGameLoadingScreen(base);

  static uint32_t watched = 0;
  static uint32_t stuck_steps = 0;

  // Once the guest has activated the destination world (`World+0xA8 != 0`), hold the final
  // loading-screen teardown while Godot's world build is still pending so `CmdLoadSaveGame`
  // stays parked in its loading screen rather than entering world simulation and audio early.
  // Before `World+0xA8 != 0`, intermediate preload screens must still finish so the guest can
  // reach entity activation.
  if (refii_bridge_world_build_pending() != 0) {
    const auto hop = [&](uint32_t at, uint32_t offset) -> uint32_t {
      return IsGuestPointer(at) ? ReadU32(base, at + offset) : 0u;
    };
    uint32_t world = ReadU32(base, refii::guest::Address(0x83496AB8u));
    world = hop(world, 0x0Cu);
    world = hop(world, 0x58u);
    world = hop(world, 0x04u);
    world = hop(world, 0x00u);
    if (IsGuestPointer(world) && ReadU8(base, world + 0xA8u) != 0u) {
      watched = 0;
      stuck_steps = 0;
      return;
    }
  }

  // NOT gated on the world being up. The guest's game thread blocks on the loading
  // screen's fade-out, which only its present loop ticks — off in this headless build —
  // so the level change that would activate the world can never advance while the block
  // holds. Waiting for the world before breaking the wait is the deadlock, not a guard.
  // The stuck test below is the specificity: `sub_82361458` sets fade=1.0 (+172) and
  // shutdown requested (+176) and then blocks on the event (+180) until the render side
  // latches done (+177). Stuck = requested set, done not latched, 30 host steps.
  // `+176` ALONE. `+177` was in this test and must not be: it is write-once (only the ctor
  // `sub_8235CA90` clears it) and the screen object is reused between loads, so a `done` left
  // set by a previous teardown made every later load read as "not stuck" and never released.
  const bool stuck = screen != 0 && ReadU8(base, screen + 176u) != 0u;
  if (!stuck) {
    watched = 0;
    stuck_steps = 0;
    return;
  }

  if (screen != watched) {
    watched = screen;
    stuck_steps = 0;
  }

  constexpr uint32_t kStuckSteps = 30u;
  if (stuck_steps < kStuckSteps) {
    ++stuck_steps;
    return;
  }

  if (ReadU32(base, screen + 168u) < 10u) {
    WriteU32(base, screen + 168u, 10u);
  }
  WriteU32(base, screen + 172u, 0u);
  WriteU8(base, screen + 176u, 0u);
  WriteU8(base, screen + 177u, 1u);
  refii_bridge_loading_screen_updated(screen, 0.0f, 0, 1);

  // The take-down `sub_8235E5A0` performs at 0x8235E7C4: clear requested, latch done, release
  // the game thread parked in `sub_82361458`'s WaitForSingleObjectEx(+180, INFINITE). Set
  // rather than pulse — the wait need not be entered yet, and an auto-reset event holds a set
  // where the guest's PulseEvent would drop it.
  const uint32_t event = ReadU32(base, screen + 180u);
  if (event != 0u) {
    rex::kernel::xboxkrnl::xeNtSetEvent(event, nullptr);
  }

  watched = 0;
  stuck_steps = 0;

  static std::atomic<uint32_t> logged{0};
  if (logged.fetch_add(1, std::memory_order_relaxed) < 8u) {
    REXLOG_INFO("Refii in-game loading screen finished from host step "
                "(screen=0x{:08X} event=0x{:08X})",
                screen, event);
  }
}

/// "No screen states this population" — the front end, or a boundary that could not read one.
/// Deliberately not 0: 0 is the pause root, a real screen id.
constexpr uint32_t kMenuScreenUnknown = 0xFFFFFFFFu;

/// One spawned menu row, as `sub_826CE8D0` was handed it.
struct MenuRow {
  std::string name;
  std::string parent;

  // String2 — the already-localised display text, decorations included. UTF-16BE in the
  // guest; decoded at capture.
  std::string label;
  uint32_t id = 0;
  int32_t event_type = 0;

  // Star or pip rating from `sub_826CE230`, SpawnButton's ninth argument (first stack slot,
  // caller_SP+0x54). Bimodal by magnitude: below 100 a star slide (2 x the GDB "Rating", so
  // 0..10, 11 meaning empty); 100 and above a five-digit base-10 pip word, each digit 1 for an
  // empty augment slot and 2 for a filled one.
  int32_t rating = 0;
};

refii::TracedMutex g_menu_mutex{"guest menu state"};

/// The list being built by the population currently running.
std::vector<MenuRow> g_menu_building;

/// The last published copy — what the host reads.
std::vector<MenuRow> g_menu_rows;

/// The screen the published capture belongs to, or `kMenuScreenUnknown`.
///
/// THE CAPTURE IS ONE LIST AND IT NEEDED A KEY. A menu template sourced `capture:Root` was
/// handed whatever was spawned last, whoever spawned it: binding the real-estate screen that
/// way put the FRONT END's `New Game`, `Continue`, `Language` and `Subtitles` above its own
/// `Sell` and `Adjust Prices`. `MenuRow` carries no screen, so a consumer had no way to tell.
///
/// The key is the guest's own: `manager+4` off the in-game GUI is the id of the screen whose
/// population is running, read at the boundary that clears the list. The FRONT END has a
/// different GUI object, so it reads `kMenuScreenUnknown` and an in-game screen rejects it —
/// which is the whole point.
uint32_t g_menu_screen_id = kMenuScreenUnknown;

/// Bumped on every row, so a host can tell "still the same list" from "read it again".
std::atomic<uint64_t> g_menu_generation{0};

/// True from `DeleteAllSpawnedMenuItems` until the next guest tick — the window in which the
/// capture is partial. There is no end marker: the in-game lists never call
/// `UpdateAfterSpawning`. A population runs to completion inside the GUI update that began it,
/// so the next guest tick is the first moment the list is whole; hence cleared from the tick,
/// not from a row.
std::atomic<bool> g_menu_populating{false};

// The hero's derived state, sampled on the guest's own thread. The logbook's numbers are
// accessor returns, not fields: morality is `sub_8223D320` (raw + GDB script constants +
// real-estate and worship terms), renown `sub_821C7D30` (field + modifier 10, clamped), and
// the personality bands at 0.375/0.625 name the wrong title if fed the raw field. The host
// bridge runs on Godot's thread and cannot call them; the pump is already on the guest tick
// thread, so it samples and stashes the answer here.

namespace {

refii::TracedMutex g_hero_mutex{"guest hero snapshot"};
HeroDerived g_hero;

std::mutex g_region_mutex;
WorldMapRegion g_region;
std::mutex g_estate_mutex;
RealEstate g_estate;
std::mutex g_furniture_mutex;
Furniture g_furniture;
std::mutex g_coop_mutex;
CoopScreen g_coop;
std::mutex g_shop_front_mutex;
ShopFront g_shop_front;
std::mutex g_journey_mutex;
QuestTravel g_journey;
std::atomic<uint32_t> g_journey_wanted{0};

std::mutex g_shop_price_mutex;
ShopPrice g_shop_price;

/// Ticket in the low 31 bits, the sell flag in the top — one atomic, so the pump can never
/// read a ticket paired with the other operation's flag.
std::atomic<uint32_t> g_shop_price_wanted{0};
std::atomic<uint32_t> g_region_wanted{0};

std::mutex g_quest_mutex;
QuestEntry g_quests[kMaxCompletedQuests];
uint32_t g_quest_count = 0;

std::mutex g_family_mutex;
FamilyMember g_family[kMaxFamilyMembers];
uint32_t g_family_count = 0;

}  // namespace

extern "C" void refii_bridge_publish_hero_derived(const void* sampled) {
  if (sampled == nullptr) {
    return;
  }
  std::lock_guard lock(g_hero_mutex);
  g_hero = *static_cast<const HeroDerived*>(sampled);
}

extern "C" void refii_bridge_hero_derived_snapshot(void* out) {
  if (out == nullptr) {
    return;
  }
  std::lock_guard lock(g_hero_mutex);
  *static_cast<HeroDerived*>(out) = g_hero;
}

namespace {
std::mutex g_inventory_mutex;
std::vector<uint32_t> g_inventory;
/// The item instance of each g_inventory record, in the same order; empty when the publish
/// that wrote g_inventory stated none.
std::vector<uint32_t> g_inventory_items;
}  // namespace

extern "C" void refii_bridge_publish_hero_inventory(const uint32_t* words, uint32_t count) {
  const size_t n = static_cast<size_t>(count) * 4u;
  bool changed = false;
  {
    std::lock_guard lock(g_inventory_mutex);
    changed = g_inventory.size() != n ||
              (n != 0 && std::memcmp(g_inventory.data(), words, n * sizeof(uint32_t)) != 0);
    if (changed) {
      g_inventory.assign(words, words + n);
      g_inventory_items.clear();
    }
  }
  if (changed) {
    refii_bridge_ui_data_changed();
  }
}

extern "C" void refii_bridge_publish_hero_inventory_items(const uint32_t* words, uint32_t count) {
  std::vector<uint32_t> records;
  std::vector<uint32_t> items;
  records.reserve(static_cast<size_t>(count) * 4u);
  items.reserve(count);
  for (uint32_t i = 0; i < count && words != nullptr; ++i) {
    records.insert(records.end(), words + 5u * i, words + 5u * i + 4u);
    items.push_back(words[5u * i + 4u]);
  }
  bool changed = false;
  {
    std::lock_guard lock(g_inventory_mutex);
    changed = g_inventory != records || g_inventory_items != items;
    if (changed) {
      g_inventory = std::move(records);
      g_inventory_items = std::move(items);
    }
  }
  if (changed) {
    refii_bridge_ui_data_changed();
  }
}

extern "C" uint32_t refii_bridge_hero_inventory_items_snapshot(uint32_t* out, uint32_t word_capacity) {
  std::lock_guard lock(g_inventory_mutex);
  const uint32_t records = static_cast<uint32_t>(g_inventory.size() / 4u);
  const bool paired = g_inventory_items.size() == records;
  if (out != nullptr) {
    const uint32_t fits = std::min(records, word_capacity / 5u);
    for (uint32_t i = 0; i < fits; ++i) {
      std::memcpy(out + 5u * i, g_inventory.data() + 4u * i, 4u * sizeof(uint32_t));
      out[5u * i + 4u] = paired ? g_inventory_items[i] : 0u;
    }
  }
  return records;
}

namespace {
std::mutex g_player_inventory_mutex;
std::vector<uint32_t> g_player_inventory[2];
}  // namespace

extern "C" void refii_bridge_publish_player_inventory(int32_t player, const uint32_t* words, uint32_t count) {
  if (player != 1 && player != 2) {
    return;
  }
  std::lock_guard lock(g_player_inventory_mutex);
  g_player_inventory[player - 1].assign(words, words + static_cast<size_t>(count) * 4u);
}

extern "C" uint32_t refii_bridge_player_inventory_snapshot(int32_t player, uint32_t* out, uint32_t word_capacity) {
  if (player != 1 && player != 2) {
    return 0u;
  }
  std::lock_guard lock(g_player_inventory_mutex);
  const std::vector<uint32_t>& held = g_player_inventory[player - 1];
  const uint32_t words = static_cast<uint32_t>(held.size());
  if (out != nullptr && word_capacity != 0u) {
    const uint32_t n = std::min(words, word_capacity - (word_capacity % 4u));
    if (n != 0u) {
      std::memcpy(out, held.data(), n * sizeof(uint32_t));
    }
  }
  return words / 4u;
}

void RequestQuestTravel(uint32_t record) { g_journey_wanted.store(record); }

extern "C" uint32_t refii_bridge_requested_quest_travel() {
  return g_journey_wanted.load();
}

extern "C" void refii_bridge_publish_quest_travel(const void* travel) {
  if (travel == nullptr) {
    return;
  }
  std::lock_guard<std::mutex> lock(g_journey_mutex);
  g_journey = *static_cast<const QuestTravel*>(travel);
}

QuestTravel QuestTravelSnapshot() {
  std::lock_guard<std::mutex> lock(g_journey_mutex);
  return g_journey;
}

void RequestShopPrice(uint32_t ticket, bool sell) {
  g_shop_price_wanted.store((ticket & 0x7FFFFFFFu) | (sell ? 0x80000000u : 0u));
}

extern "C" uint32_t refii_bridge_requested_shop_price(int32_t* sell) {
  const uint32_t wanted = g_shop_price_wanted.load();
  if (sell != nullptr) {
    *sell = (wanted & 0x80000000u) != 0u ? 1 : 0;
  }
  return wanted & 0x7FFFFFFFu;
}

extern "C" void refii_bridge_publish_shop_price(const void* price) {
  if (price == nullptr) {
    return;
  }
  std::lock_guard<std::mutex> lock(g_shop_price_mutex);
  g_shop_price = *static_cast<const ShopPrice*>(price);
}

ShopPrice ShopPriceSnapshot() {
  std::lock_guard<std::mutex> lock(g_shop_price_mutex);
  return g_shop_price;
}

void RequestWorldMapRegion(uint32_t index) { g_region_wanted.store(index); }

extern "C" uint32_t refii_bridge_requested_world_map_region() {
  return g_region_wanted.load();
}

extern "C" void refii_bridge_publish_world_map_region(const void* region) {
  if (region == nullptr) {
    return;
  }
  std::lock_guard<std::mutex> lock(g_region_mutex);
  g_region = *static_cast<const WorldMapRegion*>(region);
}

WorldMapRegion WorldMapRegionSnapshot() {
  std::lock_guard<std::mutex> lock(g_region_mutex);
  return g_region;
}

extern "C" void refii_bridge_publish_real_estate(const void* estate) {
  if (estate == nullptr) {
    return;
  }
  std::lock_guard<std::mutex> lock(g_estate_mutex);
  g_estate = *static_cast<const RealEstate*>(estate);
}

RealEstate RealEstateSnapshot() {
  std::lock_guard<std::mutex> lock(g_estate_mutex);
  return g_estate;
}

extern "C" void refii_bridge_publish_furniture(const void* furniture) {
  if (furniture == nullptr) {
    return;
  }
  std::lock_guard<std::mutex> lock(g_furniture_mutex);
  g_furniture = *static_cast<const Furniture*>(furniture);
}

Furniture FurnitureSnapshot() {
  std::lock_guard<std::mutex> lock(g_furniture_mutex);
  return g_furniture;
}

extern "C" void refii_bridge_publish_coop_screen(const void* coop) {
  if (coop == nullptr) {
    return;
  }
  std::lock_guard<std::mutex> lock(g_coop_mutex);
  g_coop = *static_cast<const CoopScreen*>(coop);
}

CoopScreen CoopScreenSnapshot() {
  std::lock_guard<std::mutex> lock(g_coop_mutex);
  return g_coop;
}

namespace {
std::mutex g_highlight_mutex;
uint32_t g_highlight_screen = 0;
int32_t g_highlight_event = 0;
int32_t g_highlight_id = 0;
char g_highlight_name[128] = {};
char g_highlight_label[128] = {};
}  // namespace

extern "C" void refii_bridge_set_stated_highlight(uint32_t screen, int32_t event_type, int32_t id,
                                                  const char* name, const char* label) {
  std::lock_guard<std::mutex> lock(g_highlight_mutex);
  g_highlight_screen = screen;
  g_highlight_event = event_type;
  g_highlight_id = id;
  std::snprintf(g_highlight_name, sizeof(g_highlight_name), "%s", name != nullptr ? name : "");
  std::snprintf(g_highlight_label, sizeof(g_highlight_label), "%s", label != nullptr ? label : "");
}

extern "C" bool refii_bridge_get_stated_highlight(uint32_t* screen, int32_t* event_type, int32_t* id,
                                                  char* name, char* label, uint32_t capacity) {
  std::lock_guard<std::mutex> lock(g_highlight_mutex);
  if (g_highlight_screen == 0u) {
    return false;
  }
  if (screen != nullptr) *screen = g_highlight_screen;
  if (event_type != nullptr) *event_type = g_highlight_event;
  if (id != nullptr) *id = g_highlight_id;
  if (name != nullptr && capacity != 0u) std::snprintf(name, capacity, "%s", g_highlight_name);
  if (label != nullptr && capacity != 0u) std::snprintf(label, capacity, "%s", g_highlight_label);
  return true;
}

extern "C" bool refii_bridge_menu_last_spawned_row_name(int32_t event_type, uint32_t id, char* name,
                                                        uint32_t capacity) {
  std::lock_guard lock(refii::vfs::g_menu_mutex);
  if (name == nullptr || capacity == 0u) {
    return false;
  }
  for (auto row = refii::vfs::g_menu_rows.rbegin(); row != refii::vfs::g_menu_rows.rend(); ++row) {
    if (row->event_type != event_type) {
      break;
    }
    if (row->id == id) {
      std::snprintf(name, capacity, "%s", row->name.c_str());
      return true;
    }
  }
  return false;
}

namespace {
std::mutex g_fold_mutex;
std::unordered_map<uint32_t, uint32_t> g_fold_depth;
}  // namespace

extern "C" void refii_bridge_set_list_fold_depth(uint32_t screen, uint32_t depth) {
  std::lock_guard<std::mutex> lock(g_fold_mutex);
  if (depth == 0u) {
    g_fold_depth.erase(screen);
  } else {
    g_fold_depth[screen] = depth;
  }
}

extern "C" uint32_t refii_bridge_list_fold_depth(uint32_t screen) {
  std::lock_guard<std::mutex> lock(g_fold_mutex);
  const auto found = g_fold_depth.find(screen);
  return found == g_fold_depth.end() ? 0u : found->second;
}

extern "C" void refii_bridge_publish_shop_front(const void* front) {
  if (front == nullptr) {
    return;
  }
  std::lock_guard<std::mutex> lock(g_shop_front_mutex);
  g_shop_front = *static_cast<const ShopFront*>(front);
}

ShopFront ShopFrontSnapshot() {
  std::lock_guard<std::mutex> lock(g_shop_front_mutex);
  return g_shop_front;
}

namespace {
std::atomic<uint64_t> g_property_estate_wanted{0};
std::mutex g_property_estate_mutex;
RealEstate g_property_estate;
uint64_t g_property_estate_uid = 0u;
}  // namespace

void RequestPropertyEstate(uint64_t uid) { g_property_estate_wanted.store(uid); }

extern "C" uint64_t refii_bridge_requested_property_estate(void) { return g_property_estate_wanted.load(); }

extern "C" void refii_bridge_publish_property_estate(uint64_t uid, const void* estate) {
  if (estate == nullptr) {
    return;
  }
  std::lock_guard<std::mutex> lock(g_property_estate_mutex);
  g_property_estate = *static_cast<const RealEstate*>(estate);
  g_property_estate_uid = uid;
}

RealEstate PropertyEstateSnapshot(uint64_t* uid) {
  std::lock_guard<std::mutex> lock(g_property_estate_mutex);
  if (uid != nullptr) {
    *uid = g_property_estate_uid;
  }
  return g_property_estate;
}

extern "C" void refii_bridge_publish_quests(const void* entries, uint32_t count) {
  const QuestEntry* typed = static_cast<const QuestEntry*>(entries);
  std::lock_guard<std::mutex> lock(g_quest_mutex);
  g_quest_count = count < kMaxCompletedQuests ? count : kMaxCompletedQuests;
  if (typed == nullptr) {
    g_quest_count = 0;
    return;
  }
  for (uint32_t i = 0; i < g_quest_count; ++i) {
    g_quests[i] = typed[i];
  }
}

uint32_t QuestSnapshot(QuestEntry* out, uint32_t capacity) {
  std::lock_guard<std::mutex> lock(g_quest_mutex);
  const uint32_t copied = capacity < g_quest_count ? capacity : g_quest_count;
  for (uint32_t i = 0; i < copied; ++i) {
    out[i] = g_quests[i];
  }

  // The total, not the copied count: a caller sizes its buffer from the first call.
  return g_quest_count;
}

extern "C" void refii_bridge_publish_family(const void* members, uint32_t count) {
  const FamilyMember* typed = static_cast<const FamilyMember*>(members);
  const uint32_t kept = typed == nullptr ? 0u : count < kMaxFamilyMembers ? count : kMaxFamilyMembers;
  bool changed = false;
  {
    std::lock_guard<std::mutex> lock(g_family_mutex);
    changed = kept != g_family_count ||
              (kept != 0u && std::memcmp(g_family, typed, kept * sizeof(FamilyMember)) != 0);
    g_family_count = kept;
    for (uint32_t i = 0; i < kept; ++i) {
      g_family[i] = typed[i];
    }
  }
  // The world map's family list is built from this snapshot: a changed one moves the UI data
  // generation, which is what the host's open list re-reads on.
  if (changed) {
    refii_bridge_ui_data_published();
  }
}

uint32_t FamilySnapshot(FamilyMember* out, uint32_t capacity) {
  std::lock_guard<std::mutex> lock(g_family_mutex);
  const uint32_t copied = capacity < g_family_count ? capacity : g_family_count;
  for (uint32_t i = 0; i < copied; ++i) {
    out[i] = g_family[i];
  }

  return g_family_count;
}

/// The screen the published capture belongs to. See `g_menu_screen_id`.
uint32_t MenuScreenId() {
  std::lock_guard lock(g_menu_mutex);
  return g_menu_screen_id;
}

uint32_t MenuRowLabel(uint32_t index, char* out_label, size_t label_capacity) {
  std::lock_guard lock(g_menu_mutex);
  if (out_label != nullptr && label_capacity > 0) {
    out_label[0] = '\0';
    if (index < g_menu_rows.size()) {
      const std::string& label = g_menu_rows[index].label;
      const size_t take =
          label.size() < label_capacity - 1 ? label.size() : label_capacity - 1;
      std::memcpy(out_label, label.data(), take);
      out_label[take] = '\0';
    }
  }

  return static_cast<uint32_t>(g_menu_rows.size());
}

uint32_t MenuRows(uint32_t index, char* out_name, size_t name_capacity, char* out_parent,
                  size_t parent_capacity, uint32_t* out_id, int32_t* out_event_type,
                  uint64_t* out_generation, int32_t* out_rating) {
  std::lock_guard lock(g_menu_mutex);
  if (out_generation != nullptr) {
    *out_generation = g_menu_generation.load(std::memory_order_relaxed);
  }

  if (index >= g_menu_rows.size()) {
    return static_cast<uint32_t>(g_menu_rows.size());
  }

  const MenuRow& row = g_menu_rows[index];
  if (out_name != nullptr && name_capacity > 0) {
    const size_t take = row.name.size() < name_capacity - 1 ? row.name.size() : name_capacity - 1;
    std::memcpy(out_name, row.name.data(), take);
    out_name[take] = '\0';
  }

  if (out_parent != nullptr && parent_capacity > 0) {
    const size_t take =
        row.parent.size() < parent_capacity - 1 ? row.parent.size() : parent_capacity - 1;
    std::memcpy(out_parent, row.parent.data(), take);
    out_parent[take] = '\0';
  }

  if (out_id != nullptr) {
    *out_id = row.id;
  }

  if (out_rating != nullptr) {
    *out_rating = row.rating;
  }

  if (out_event_type != nullptr) {
    *out_event_type = row.event_type;
  }

  return static_cast<uint32_t>(g_menu_rows.size());
}

namespace {

/// THE PAUSE MINIMAP'S QUEST MARKERS, as the guest states them: one per call of the marker
/// placer `sub_826A3FD8` (disc `sub_82410390`) in the pass `sub_826A3150` (disc `sub_8240F508`)
/// — the token it names and the world point it projects. The pass runs once per pause open
/// (OnEnter clears `screen0+100`, the pass sets it), and the payload empties this table as the
/// pass begins (hooks/gui_pause_hooks.cpp).
struct PauseMarker {
  uint32_t token = 0;
  float world[3] = {};
};

std::mutex g_pause_markers_mutex;
std::vector<PauseMarker> g_pause_markers;

}  // namespace

uint32_t PauseMarkers(uint32_t* out_tokens, float* out_world, uint32_t capacity) {
  std::lock_guard lock(g_pause_markers_mutex);
  const uint32_t count = static_cast<uint32_t>(
      std::min<std::size_t>(g_pause_markers.size(), capacity));
  for (uint32_t i = 0; i < count; ++i) {
    if (out_tokens != nullptr) {
      out_tokens[i] = g_pause_markers[i].token;
    }
    if (out_world != nullptr) {
      std::memcpy(out_world + (i * 3u), g_pause_markers[i].world, sizeof(float) * 3u);
    }
  }
  return count;
}

}  // namespace refii::vfs

// --- The payload's side. C only; see core/payload_bridge.h. --------------------------------

extern "C" {

void refii_bridge_pause_markers_begin(void) {
  std::lock_guard lock(refii::vfs::g_pause_markers_mutex);
  refii::vfs::g_pause_markers.clear();
}

void refii_bridge_pause_marker_note(uint32_t token, float x, float y, float z) {
  std::lock_guard lock(refii::vfs::g_pause_markers_mutex);
  // The pass places the dog's trigger target (Quest or QuestExit) and one QuestGiver per giver
  // of the primary quest; bounded so a runaway list cannot grow.
  if (refii::vfs::g_pause_markers.size() >= 16u) {
    return;
  }
  refii::vfs::PauseMarker marker;
  marker.token = token;
  marker.world[0] = x;
  marker.world[1] = y;
  marker.world[2] = z;
  refii::vfs::g_pause_markers.push_back(marker);
}

void refii_bridge_front_end_note_subtitles(int32_t state) {
  refii::vfs::g_subtitles.store(state, std::memory_order_relaxed);
}

void refii_bridge_note_pause_tabs(uint32_t mask) {
  refii::vfs::g_pause_tabs.store(mask, std::memory_order_relaxed);
}

void refii_bridge_menu_spawn_begin_for(uint32_t screen_id) {
  std::lock_guard lock(refii::vfs::g_menu_mutex);
  refii::vfs::g_menu_building.clear();
  refii::vfs::g_menu_screen_id = screen_id;

  // The published copy goes too: `DeleteAllSpawnedMenuItems` runs before every repopulation
  // and publication is per row, so clearing only the builder leaves the host reading the
  // previous screen's population in full. Empty is the honest answer for that window — an
  // unnamed row falls back to the guest's own A press.
  refii::vfs::g_menu_rows.clear();
  refii::vfs::g_menu_generation.fetch_add(1, std::memory_order_relaxed);

  // Mark it partial from here: "empty" and "this screen has no rows" are otherwise the same
  // value, and a consumer would rebuild its model from a list it is about to be given again.
  refii::vfs::g_menu_populating.store(true, std::memory_order_release);
}

namespace {
/// 1 while player two owns the open in-game GUI (pause book and what it opens), else 0.
std::atomic<int32_t> g_menu_owner_two{0};
}  // namespace

/// The GUI's owner as the payload states it each tick (guest_tick_pump.cpp StateMenuOwner).
void refii_bridge_set_menu_owner_two(int32_t two) {
  g_menu_owner_two.store(two != 0 ? 1 : 0, std::memory_order_relaxed);
}

int32_t refii_bridge_menu_owner_two(void) { return g_menu_owner_two.load(std::memory_order_relaxed); }

namespace {
/// The player whose expression wheel is open, 0 for none.
std::atomic<int32_t> g_expression_wheel_player{0};
}  // namespace

/// Set by the open hook when the quick menu's +35 (+34 disc) goes 0 -> 1 (`stb` TU1 0x8236F31C /
/// disc 0x8233751C), to the record's +36 the guest hands `sub_826A9A60` / `sub_8258D1A0`
/// (`lwz r4,0x24(r30)` 0x8236F324 / 0x82337524); cleared by the close hook when it clears the byte
/// (`stb` 0x8236F374 / 0x82337574). Those and the quick menu's constructor (`sub_8236D078`
/// 0x8236D0A8 / `sub_82335218` 0x82335248, 0) are the byte's only writers.
void refii_bridge_set_expression_wheel_player(int32_t player) {
  g_expression_wheel_player.store(player, std::memory_order_relaxed);
}

int32_t refii_bridge_expression_wheel_player(void) {
  return g_expression_wheel_player.load(std::memory_order_relaxed);
}

namespace {
/// Players a region exit refused since the host last asked (bit 0 player one, bit 1 player two).
std::atomic<int32_t> g_exit_refused{0};
}  // namespace

void refii_bridge_note_exit_refused(int32_t player) {
  if (player == 1 || player == 2) {
    g_exit_refused.fetch_or(player == 1 ? 1 : 2, std::memory_order_relaxed);
  }
}

int32_t refii_bridge_take_exit_refused(void) { return g_exit_refused.exchange(0, std::memory_order_relaxed); }

namespace {
constexpr int32_t kPromptsTwo = 64;
std::mutex g_prompts_two_mutex;
std::array<uint32_t, kPromptsTwo> g_prompts_two{};
std::array<uint8_t, kPromptsTwo> g_prompt_masks{};
int32_t g_prompts_two_count = 0;
}  // namespace

void refii_bridge_set_prompt_masks(const uint32_t* things, const uint8_t* masks, int32_t count) {
  std::lock_guard<std::mutex> lock(g_prompts_two_mutex);
  g_prompts_two_count = things == nullptr || masks == nullptr ? 0 : std::clamp(count, 0, kPromptsTwo);
  for (int32_t i = 0; i < g_prompts_two_count; ++i) {
    g_prompts_two[static_cast<std::size_t>(i)] = things[i];
    g_prompt_masks[static_cast<std::size_t>(i)] = masks[i];
  }
}

namespace {
std::atomic<int32_t> g_exit_waiting{0};
}  // namespace

void refii_bridge_set_exit_waiting(int32_t waiting) {
  g_exit_waiting.store(waiting != 0 ? 1 : 0, std::memory_order_relaxed);
}

int32_t refii_bridge_exit_waiting(void) { return g_exit_waiting.load(std::memory_order_relaxed); }

namespace {
std::mutex g_exit_request_mutex;
uint32_t g_exit_request_seq = 0;
float g_exit_request_at[3] = {0.0f, 0.0f, 0.0f};
}  // namespace

void refii_bridge_set_exit_request(float x, float y, float z) {
  std::lock_guard<std::mutex> lock(g_exit_request_mutex);
  ++g_exit_request_seq;
  g_exit_request_at[0] = x;
  g_exit_request_at[1] = y;
  g_exit_request_at[2] = z;
}

uint32_t refii_bridge_exit_request(float* out_xyz) {
  std::lock_guard<std::mutex> lock(g_exit_request_mutex);
  if (out_xyz != nullptr) {
    std::memcpy(out_xyz, g_exit_request_at, sizeof(g_exit_request_at));
  }
  return g_exit_request_seq;
}

namespace {
int32_t PromptsWith(uint32_t* out, int32_t capacity, uint8_t want, uint8_t value) {
  std::lock_guard<std::mutex> lock(g_prompts_two_mutex);
  int32_t written = 0;
  for (int32_t i = 0; out != nullptr && i < g_prompts_two_count && written < capacity; ++i) {
    if ((g_prompt_masks[static_cast<std::size_t>(i)] & want) == value) {
      out[written++] = g_prompts_two[static_cast<std::size_t>(i)];
    }
  }
  return written;
}
}  // namespace

int32_t refii_bridge_prompts_two(uint32_t* out, int32_t capacity) { return PromptsWith(out, capacity, 2u, 2u); }

int32_t refii_bridge_prompts_one_hidden(uint32_t* out, int32_t capacity) { return PromptsWith(out, capacity, 1u, 0u); }

namespace {
std::mutex g_hold_two_mutex;
float g_hold_two_fraction = 0.0f;
int32_t g_hold_two_pressed = 0;
}  // namespace

void refii_bridge_player_two_hold_state(float fraction, int32_t pressed) {
  std::lock_guard<std::mutex> lock(g_hold_two_mutex);
  g_hold_two_fraction = fraction;
  g_hold_two_pressed = pressed;
}

void refii_bridge_player_two_hold(float* fraction, int32_t* pressed) {
  std::lock_guard<std::mutex> lock(g_hold_two_mutex);
  if (fraction != nullptr) *fraction = g_hold_two_fraction;
  if (pressed != nullptr) *pressed = g_hold_two_pressed;
}

namespace {
bool g_hold_prompt_two_up = false;
float g_hold_prompt_two_at[3] = {};
std::string g_hold_prompt_two_title;
}  // namespace

namespace {
std::mutex g_property_notices_mutex;
std::vector<std::pair<int32_t, uint64_t>> g_property_notices;
}  // namespace

void refii_bridge_property_notice_post_kind(int32_t kind, uint64_t uid) {
  std::lock_guard<std::mutex> lock(g_property_notices_mutex);
  if (g_property_notices.size() < 64u) {
    g_property_notices.emplace_back(kind, uid);
  }
}

void refii_bridge_property_notice_post(uint64_t uid) { refii_bridge_property_notice_post_kind(0, uid); }

int32_t refii_bridge_property_notice_take(int32_t* kind, uint64_t* uid) {
  std::lock_guard<std::mutex> lock(g_property_notices_mutex);
  if (g_property_notices.empty()) {
    return 0;
  }
  if (kind != nullptr) *kind = g_property_notices.front().first;
  if (uid != nullptr) *uid = g_property_notices.front().second;
  g_property_notices.erase(g_property_notices.begin());
  return 1;
}

namespace {
std::mutex g_estate_intents_mutex;
std::vector<std::string> g_estate_intents;
}  // namespace

void refii_bridge_estate_intent_post(const char* text) {
  std::lock_guard<std::mutex> lock(g_estate_intents_mutex);
  if (text != nullptr && g_estate_intents.size() < 64u) {
    g_estate_intents.emplace_back(text);
  }
}

uint32_t refii_bridge_estate_intent_take(char* out, uint32_t capacity) {
  std::lock_guard<std::mutex> lock(g_estate_intents_mutex);
  if (g_estate_intents.empty() || out == nullptr || capacity <= g_estate_intents.front().size()) {
    return 0u;
  }
  const std::string text = g_estate_intents.front();
  g_estate_intents.erase(g_estate_intents.begin());
  std::memcpy(out, text.c_str(), text.size() + 1u);
  return static_cast<uint32_t>(text.size());
}

namespace {
std::mutex g_prompt_titles_mutex;
std::string g_prompt_titles;
}  // namespace

void refii_bridge_set_prompt_titles(const char* text) {
  std::lock_guard<std::mutex> lock(g_prompt_titles_mutex);
  g_prompt_titles = text != nullptr ? text : "";
}

uint32_t refii_bridge_prompt_titles(char* out, uint32_t capacity) {
  std::lock_guard<std::mutex> lock(g_prompt_titles_mutex);
  if (out == nullptr || capacity == 0u || g_prompt_titles.size() + 1u > capacity) {
    return 0u;
  }
  std::memcpy(out, g_prompt_titles.c_str(), g_prompt_titles.size() + 1u);
  return static_cast<uint32_t>(g_prompt_titles.size());
}

void refii_bridge_player_two_hold_prompt(int32_t up, const float* at, const char* title) {
  std::lock_guard<std::mutex> lock(g_hold_two_mutex);
  g_hold_prompt_two_up = up != 0 && at != nullptr;
  if (!g_hold_prompt_two_up) {
    return;
  }
  for (int i = 0; i < 3; ++i) g_hold_prompt_two_at[i] = at[i];
  g_hold_prompt_two_title = title != nullptr ? title : "";
}

int32_t refii_bridge_player_two_hold_prompt_get(float* at, char* title, uint32_t capacity) {
  std::lock_guard<std::mutex> lock(g_hold_two_mutex);
  if (!g_hold_prompt_two_up) {
    return 0;
  }
  if (at != nullptr) {
    for (int i = 0; i < 3; ++i) at[i] = g_hold_prompt_two_at[i];
  }
  if (title != nullptr && capacity != 0u) {
    std::snprintf(title, capacity, "%s", g_hold_prompt_two_title.c_str());
  }
  return 1;
}

/// Clears the partial-population flag. Called once per guest tick.
void refii_bridge_menu_settle(void) {
  refii::vfs::g_menu_populating.store(false, std::memory_order_release);
}

/// True while a population is in flight and the captured rows are not yet whole.
int32_t refii_bridge_menu_populating(void) {
  return refii::vfs::g_menu_populating.load(std::memory_order_acquire) ? 1 : 0;
}

void refii_bridge_menu_spawn_row_labelled(const char* name, const char* parent, uint32_t id,
                                          int32_t event_type, int32_t rating,
                                          const char* label) {
  refii::vfs::MenuRow row;
  row.name = name != nullptr ? name : "";
  row.parent = parent != nullptr ? parent : "";
  row.label = label != nullptr ? label : "";
  row.id = id;
  row.event_type = event_type;
  row.rating = rating;

  std::lock_guard lock(refii::vfs::g_menu_mutex);

  // Every row the guest spawns is kept: a population is bounded by its own boundary
  // (`refii_bridge_menu_spawn_begin_for` clears both lists), and a list longer than any fixed
  // count is ordinary — the shop spawns every sell row before its first buy row
  // (`sub_82994218` 0x8299491C then 0x82994938 / disc `sub_828DF1A8`).
  //
  // Published per row, not at the end: the in-game lists never call `UpdateAfterSpawning`,
  // so there is no end marker to wait for. The published list is the builder row for row (both
  // are cleared together and restated together), so the row is appended to each.
  refii::vfs::g_menu_rows.push_back(row);
  refii::vfs::g_menu_building.push_back(std::move(row));
  refii::vfs::g_menu_generation.fetch_add(1, std::memory_order_relaxed);
}

// C++ linkage: this sits inside the file's `extern "C"` block, and its caller declares it mangled.
extern "C++" {
namespace refii::vfs {

/// One captured row restated IN PLACE: the guest's own statements after a transaction, which
/// rewrite a row without respawning it — `RefreshChildsButtonText` (the row `name` under
/// `parent`), `RefreshSelectedButtonText` and `RenameSelectedButton` (the row `name` with id
/// `*id`). The shop defers its repopulation until a header is highlighted (`sub_826AE270` on
/// `screen+441`), so until then these are the only statement of the row's caption and name; a
/// capture left at its spawn-time text made the host's list disagree with its own source on
/// every refresh. Exactly one of `parent` and `id` selects the row; a null `new_name` or
/// `new_label` leaves that field. False when the capture holds no such row.
bool RestateCapturedMenuRow(const std::string& name, const std::string* parent, const uint32_t* id,
                            const std::string* new_name, const std::string* new_label) {
  std::lock_guard lock(g_menu_mutex);
  bool restated = false;
  for (std::vector<MenuRow>* rows : {&g_menu_building, &g_menu_rows}) {
    for (MenuRow& row : *rows) {
      if (row.name != name || (parent != nullptr && row.parent != *parent) ||
          (id != nullptr && row.id != *id)) {
        continue;
      }
      if (new_label != nullptr) row.label = *new_label;
      if (new_name != nullptr) row.name = *new_name;
      restated = true;
      break;
    }
  }
  if (restated) {
    g_menu_generation.fetch_add(1, std::memory_order_relaxed);
  }
  return restated;
}

}  // namespace refii::vfs
}  // extern "C++"

int32_t refii_bridge_guest_tick_pump_wants_save_count(void) {
  return refii::vfs::g_resample.load(std::memory_order_relaxed) ? 1 : 0;
}

void refii_bridge_guest_tick_pump_clear_save_count_request(void) {
  refii::vfs::g_resample.store(false, std::memory_order_relaxed);
}

void refii_bridge_guest_tick_pump_note_save_count(int32_t count) {
  refii::vfs::g_save_count.store(count, std::memory_order_relaxed);
}

void refii_bridge_guest_tick_pump_note_result(uint32_t addr, uint64_t r3, const char* note,
                                              size_t length) {
  std::lock_guard<std::mutex> lock(refii::vfs::g_debug_result_mutex);
  ++refii::vfs::g_debug_result_seq;
  refii::vfs::g_debug_result_addr = addr;
  refii::vfs::g_debug_result_r3 = r3;
  refii::vfs::g_debug_result_note.assign(note != nullptr ? note : "",
                                         note != nullptr ? length : 0);
}

void refii_bridge_guest_tick_pump_request_save_count(void) {
  refii::vfs::RefreshGuestSavedGameCount();
}

uint32_t refii_bridge_resolve_in_game_loading_screen(uint8_t* base) {
  return refii::vfs::ResolveInGameLoadingScreen(base);
}
}  // extern "C"
