// refii — THE HOST WORLD'S QUESTS, SALES AND TRAVEL STOPS REACH AN ONLINE JOINER (Fable 2, TU1 + disc).
//
// The world map's five guest-filled rows (lhGuiWorldMapScreen_PopulateMenu TU1 0x829A2F40 / disc
// 0x828F3EC0; the counts `sub_826CFD48` / `sub_82409828`, all pairs identical) are ghosted on a network
// client, whatever hero it brought, because the client holds none of the world's lists:
//   - QUESTS / JOBS: lhWorldMap_BuildQuestList / BuildJobList (0x8246E800 / 0x8246EA08, disc 0x8283A420 /
//     0x8283A628) take the local player's record (`li r4,1` on a session's host, else `li r4,2`, then
//     `bl sub_82264230` 0x8246E864 / 0x8246EA6C, disc `bl sub_822641F0` 0x8283A484 / 0x8283A68C), test its
//     comp 102 bit (`(entity+48 >> 6) & 1`) and list that CECQuestTracker's quests (`sub_8251CA50`: the
//     12-byte entries at tracker +16..+20). A client never builds comp 102 (the client kind switch
//     0x8238DF3C / disc 0x8239370C sends kind 102, index 101, to the default), and its quests are
//     registered by the host's scripts on QuestManager.HeroEntity = GetPlayerHero() (gameflow.lua) —
//     hero one, world state — which a client never runs (it skips every Lua update).
//   - SALES: lhWorldMap_BuildSalesList (0x8246EC10 / 0x8283A830) walks the world's sales-and-shortages
//     manager, `*(cell+100)` (cell = `*(*(*(*(0x83496AB8)+12)+88)+4)`), whose entries the host's
//     simulation makes: none on a client.
//   - REGIONS: a region is listed when `sub_82518380` finds an active stop in it (byte +0 of the stop,
//     written only by FastTravel.SetStopAsActive / SetAllStopsAsActive, `sub_82518228` / `sub_82518260`,
//     from the host's scripts), in the fast-travel manager `*(*(*(cell+8)+4)+12)`: none active on a client.
//   - FAMILIES is the joiner's own hero's comp 136 (the household the host keeps, household_persist.cpp);
//     not stated here.
//
// Each of the three world parts has the guest's own serializer, the one the world save writes it with
// (lhSaveManager_WriteSave / SaveGame_DeserializeAndLoadLevel): CECQuestTracker slot 9 (TU1
// 0x82520428 / disc 0x825FCDE8, vtable 0x82003268 / 0x82003288 +0x24; `(comp, archive, 1, saving)`, the
// load inserting each quest it reads, `sub_821B6660(tracker+12, …)`), the fast-travel manager
// `sub_8251A060` / `sub_8260A0E8` (`(manager, archive, saving)`, called with `lwz r3,0xC(r6)` 0x82446174
// / 0x824E9764; its load finds each region, level and stop by name and reads the stop's flag into it)
// and the sales manager `sub_82604568` / `sub_8260FBA0` (`(manager, archive, saving)`, `lwz r3,0x64(r9)`
// 0x824461E0 / 0x824E97D0; its load empties the list first, `sub_82353480(manager+20)`). So the host
// writes hero one's tracker, the stops and the sales into one guest archive (the household's chain,
// GuestArchiveSave) every two seconds, a saved blob of the world `refii.world.one` in player one's store,
// sent when it changes; the joiner loads it onto its copies — the quests onto the tracker its own map's
// lists read, emptied first the way Deregister drops an entry (`sub_821B7B18(entry+4)` per entry, then
// end = begin; `sub_8251BDF8`), the stops and the sales in place. Not loaded while the joiner's map is
// up: its rows are the lists' ordinals.
// WHICH TRACKER ON EACH MACHINE is the guest's own pick, the one the three lists make (`lwz r11,0x8C(r10)
// ; lbz r9,0x34(r11) ; … lbz r11,0x35(r11) ; … lwz r3,0x9C(r10) ; li r4,1 … li r4,2 ; bl sub_82264230`,
// TU1 0x8246E82C..0x8246E864 / 0x8246EA34..0x8246EA6C / 0x826CFD80..0x826CFDA4, disc 0x8283A44C..0x8283A484 /
// 0x8283A654..0x8283A68C / 0x82409860..0x82409884): the record with key 1 on the session's host (+0x34
// and +0x35 set) — hero one, QuestManager.HeroEntity, the source — and key 2 elsewhere. A joiner's players
// map holds only key 2 (its own hero; a peek in game: one entry, 'player 2'), and `sub_82264230` answers 0
// for a key it does not hold, so the joiner's lists read its own hero's comp 102 (built on a client for a
// hero, coop_client_components_hooks.cpp; absent → the lists' own empty exit `(entity+48 >> 6) & 1`), and
// that is where the world's quests are loaded. The picks themselves are the guest's, untouched.
//
// ONLY PLAYER ONE FAST-TRAVELS (user decision 2026-11-28): neither an online joiner nor local player two.
// Every travel the world map offers asks the guest's own permission first, lhQuestTrail_HasNoDestination
// (TU1 0x82471438 / disc 0x8283CFD8, identical): the fast-travel manager's blocked text empty and
// `sub_82517010(manager)` (disc `sub_82607090`, identical) — its "Enabled" byte +24, or 0 when hero one's
// comp 33 has bit 5 of +124. Its callers are all the world map's (xrefs, TU1): the A-button captions
// `sub_824708D8` / `sub_82470A78` ("…MISC_TRAVEL" / "…MISC_TRAIL_AND_TRAVEL" only when it holds, else
// "…MISC_TRAIL" or none) and the row answers — the region row `sub_826C7E90` (its stops) and the quest, job,
// sale and family travel questions `sub_826C86A0` / `sub_826C87E8` / `sub_826C8930` / `sub_826C8A80`
// (boxes 8..11) — each `if (!HasNoDestination()) return sub_826C8FE8();`, the refusal (TU1 0x826C8FE8 /
// disc 0x825A4F18): the blocked text's box 13, or `if (!sub_82517010(manager))` the box 12 with
// "GUI_SCREEN_WORLD_MAP_CANNOT_FAST_TRAVEL" (`lhGuiManager_OpenModal(QM, text, 2, 12, 1)`). The shipped
// game has no player test anywhere on that path (on the 360 a client never had a stop or a quest to
// travel to). So for a player who is not player one the permission and the refusal run with the manager
// answering "not enabled": the guest's own captions, its own refusal box, its own text in every language.

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include <rex/logging.h>
#include <rex/ppc/context.h>

#include "core/guest_build.h"
#include "core/joiner_world_rule.h"
#include "core/payload_bridge_fable2.h"
#include "core/refii_mod.h"
#include "hooks/ppc_externs.h"
#include "vfs_hooks_private.h"

/// Player two brought a hero (a character from a save) rather than standing as the plain henchman.
extern "C" int RefiiCoopGetOwnHero(void);

namespace {

using refii::mods::CallGuest;
using refii::mods::CallVirtual;
using refii::mods::LoadU32;

constexpr uint32_t kQuestTracker = 102u;
constexpr uint32_t kSerialize = 9u;  // a component's slot 9: (component, archive, kind, saving)
constexpr uint32_t kWorldKind = 1u;  // the world save's kind, the only one the tracker writes for (`a3 == 1`)
constexpr uint32_t kEnter = 1u;      // the archive's +4 (writer)
constexpr uint32_t kTryEnter = 2u;   // +8 (reader)
constexpr uint32_t kLeave = 3u;      // +12
constexpr uint32_t kWorldMapScreen = 23u;
constexpr uint32_t kTrackerEntry = 12u;
constexpr double kStateSeconds = 2.0;
constexpr double kTakeSeconds = 0.5;
constexpr char kWorldBlob[] = "refii.world.one";
constexpr char kTop[] = "RefiiWorld";

bool Heap(uint32_t at) { return at >= 0x10000u && at < 0xE0000000u; }

bool OnlineJoiner() { return refii_mod_player_here(1) == 0 && refii_mod_player_here(2) != 0; }

/// `*(*(*(*(0x83496AB8)+12)+88)+4)`, the world record both managers hang off.
uint32_t WorldCell(uint8_t* base) {
  const uint32_t root = LoadU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = Heap(root) ? LoadU32(base, root + 12u) : 0u;
  const uint32_t manager = Heap(game) ? LoadU32(base, game + 88u) : 0u;
  return Heap(manager) ? LoadU32(base, manager + 4u) : 0u;
}

/// The fast-travel manager, `*(*(*(cell+8)+4)+12)` (the map's count 0x826CFF94..; the save's 0x82446160..).
uint32_t TravelManager(uint8_t* base) {
  const uint32_t cell = WorldCell(base);
  const uint32_t a = Heap(cell) ? LoadU32(base, cell + 8u) : 0u;
  const uint32_t b = Heap(a) ? LoadU32(base, a + 4u) : 0u;
  const uint32_t manager = Heap(b) ? LoadU32(base, b + 12u) : 0u;
  return Heap(manager) ? manager : 0u;
}

/// The sales-and-shortages manager, `*(cell+100)` (BuildSalesList; the save's `lwz r3,0x64(r9)`).
uint32_t SalesManager(uint8_t* base) {
  const uint32_t cell = WorldCell(base);
  const uint32_t manager = Heap(cell) ? LoadU32(base, cell + 100u) : 0u;
  return Heap(manager) ? manager : 0u;
}

/// The quest tracker the world map's lists read on this machine: the guest's pick (key 1 when the net
/// context +0x8C has +0x34 and +0x35 set, else 2), its record by `sub_82264230(*(game+0x9C), key)`, the
/// record's hero (`v8 = record + 12; entity = *v8[1]`, the lists' read) and that hero's comp 102; 0 when any
/// is missing — the lists' own "no tracker" answer. `key_out` gets the key.
uint32_t PickedTracker(const PPCContext& ctx, uint8_t* base, uint32_t* key_out) {
  const uint32_t root = LoadU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = Heap(root) ? LoadU32(base, root + 12u) : 0u;
  const uint32_t net = Heap(game) ? LoadU32(base, game + 0x8Cu) : 0u;
  const uint32_t players = Heap(game) ? LoadU32(base, game + 0x9Cu) : 0u;
  const uint32_t key = Heap(net) && base[net + 0x34u] != 0u && base[net + 0x35u] != 0u ? 1u : 2u;
  if (key_out != nullptr) {
    *key_out = key;
  }
  if (!Heap(players)) {
    return 0u;
  }
  PPCContext work = refii::mods::Frame(ctx, 0x100u);
  work.r3.u64 = players;
  work.r4.u64 = key;
  const uint32_t record = CallGuest(work, base, refii::guest::Address(0x82264230u)) ? work.r3.u32 : 0u;
  const uint32_t cell = Heap(record) ? LoadU32(base, record + 16u) : 0u;
  const uint32_t hero = Heap(cell) ? LoadU32(base, cell) : 0u;
  return Heap(hero) && refii_mod_entity_alive(base, hero) != 0 ? refii_mod_component(base, hero, kQuestTracker) : 0u;
}

uint32_t QuestCount(uint8_t* base, uint32_t tracker) {
  const uint32_t begin = LoadU32(base, tracker + 16u);
  const uint32_t end = LoadU32(base, tracker + 20u);
  return Heap(begin) && end >= begin && (end - begin) % kTrackerEntry == 0u ? (end - begin) / kTrackerEntry : 0u;
}

uint32_t CallAt(const refii::vfs::GuestArchive& a, uint8_t* base, uint32_t tu1, uint32_t r3, uint32_t r4,
                uint32_t r5) {
  PPCContext work = a.frame;
  work.r3.u64 = r3;
  work.r4.u64 = r4;
  work.r5.u64 = r5;
  return CallGuest(work, base, refii::guest::Address(tu1)) ? work.r3.u32 : 0u;
}

uint32_t Slot(const refii::vfs::GuestArchive& a, uint8_t* base, uint32_t object, uint32_t slot, uint32_t r4 = 0u,
              uint32_t r5 = 0u, uint32_t r6 = 0u) {
  PPCContext work = a.frame;
  work.r4.u64 = r4;
  work.r5.u64 = r5;
  work.r6.u64 = r6;
  return Heap(object) && CallVirtual(work, base, object, slot) ? work.r3.u32 : 0u;
}

/// The element names, in the body's guest scratch.
uint32_t QuestsTag(const refii::vfs::GuestArchive& a) { return a.names; }
uint32_t SalesTag(const refii::vfs::GuestArchive& a) { return a.names + 0x10u; }
void Names(const refii::vfs::GuestArchive& a, uint8_t* base) {
  std::memcpy(base + QuestsTag(a), "C102", 5u);
  std::memcpy(base + SalesTag(a), "Sales", 6u);
}

/// The joiner's copy of hero one's tracker emptied as Deregister drops one entry: each entry's quest
/// reference released (`sub_821B7B18(entry + 4)`), then the vector's end back to its begin.
void EmptyTracker(const refii::vfs::GuestArchive& a, uint8_t* base, uint32_t tracker) {
  const uint32_t begin = LoadU32(base, tracker + 16u);
  const uint32_t count = QuestCount(base, tracker);
  if (count > 4096u) {
    return;
  }
  for (uint32_t i = 0; i < count; ++i) {
    CallAt(a, base, 0x821B7B18u, begin + i * kTrackerEntry + 4u, 0u, 0u);
  }
  refii::mods::StoreU32(base, tracker + 20u, begin);
}

struct WorldState {
  double state_in = 0.0;
  double take_in = 0.0;
  bool stated = false;               // the host set the blob this session
  std::vector<uint8_t> loaded;       // the joiner's last loaded statement
  uint32_t loaded_onto = 0u;         // the tracker it was loaded onto
};
WorldState g_world;

/// Host: hero one's quests, the stops and the sales, as the world save writes them.
void StateWorld(PPCContext& ctx, uint8_t* base) {
  uint32_t key = 0u;
  const uint32_t tracker = PickedTracker(ctx, base, &key);  // key 1 here: hero one's
  const uint32_t travel = TravelManager(base);
  const uint32_t sales = SalesManager(base);
  if (tracker == 0u && travel == 0u && sales == 0u) {
    return;
  }
  const std::vector<uint8_t> bytes =
      refii::vfs::GuestArchiveSave(ctx, base, kTop, [&](const refii::vfs::GuestArchive& a) {
        Names(a, base);
        if (tracker != 0u) {
          Slot(a, base, a.archive, kEnter, QuestsTag(a));
          Slot(a, base, tracker, kSerialize, a.archive, kWorldKind, 1u);
          Slot(a, base, a.archive, kLeave);
        }
        if (travel != 0u) {
          CallAt(a, base, 0x8251A060u, travel, a.archive, 1u);  // enters "FastTravelManager" itself
        }
        if (sales != 0u) {
          Slot(a, base, a.archive, kEnter, SalesTag(a));
          CallAt(a, base, 0x82604568u, sales, a.archive, 1u);
          Slot(a, base, a.archive, kLeave);
        }
      });
  if (bytes.empty()) {
    return;
  }
  std::vector<uint8_t> held(static_cast<size_t>(std::max(0, refii_bridge_player_blob_get(1, kWorldBlob, nullptr, 0u))));
  if (!held.empty()) {
    refii_bridge_player_blob_get(1, kWorldBlob, held.data(), static_cast<uint32_t>(held.size()));
  }
  if (held != bytes) {
    refii_bridge_player_blob_set(1, kWorldBlob, bytes.data(), static_cast<uint32_t>(bytes.size()));
    REXLOG_INFO("Refii co-op world: the host states its world to the joiner ({} bytes): record {}'s {} quest(s), "
                "the travel stops{}, the sales{}",
                bytes.size(), key, tracker != 0u ? QuestCount(base, tracker) : 0u, travel != 0u ? "" : " (no manager)",
                sales != 0u ? "" : " (no manager)");
  }
  g_world.stated = true;
}

/// Joiner: the host's statement onto this machine's copies.
void TakeWorld(PPCContext& ctx, uint8_t* base) {
  const int32_t size = refii_bridge_player_blob_get(1, kWorldBlob, nullptr, 0u);
  if (size <= 0) {
    return;
  }
  std::vector<uint8_t> bytes(static_cast<size_t>(size));
  refii_bridge_player_blob_get(1, kWorldBlob, bytes.data(), static_cast<uint32_t>(bytes.size()));
  uint32_t key = 0u;
  const uint32_t tracker = PickedTracker(ctx, base, &key);  // key 2 on a joiner: its own hero's
  if (bytes == g_world.loaded && tracker == g_world.loaded_onto) {
    return;
  }
  if (!refii::coop::WorldStateLoadable(refii_bridge_stated_screen_now(), kWorldMapScreen)) {
    return;  // the map's rows are the lists' ordinals: after it closes
  }
  const uint32_t travel = TravelManager(base);
  const uint32_t sales = SalesManager(base);
  bool quests = false;
  bool stops = false;
  bool sold = false;
  const bool found = refii::vfs::GuestArchiveLoad(ctx, base, kTop, bytes, [&](const refii::vfs::GuestArchive& a) {
    Names(a, base);
    if ((Slot(a, base, a.archive, kTryEnter, QuestsTag(a)) & 0xFFu) != 0u) {
      if (tracker != 0u) {
        EmptyTracker(a, base, tracker);
        Slot(a, base, tracker, kSerialize, a.archive, kWorldKind, 0u);
        quests = true;
      }
      Slot(a, base, a.archive, kLeave);
    }
    if (travel != 0u) {
      CallAt(a, base, 0x8251A060u, travel, a.archive, 0u);
      stops = true;
    }
    if ((Slot(a, base, a.archive, kTryEnter, SalesTag(a)) & 0xFFu) != 0u) {
      if (sales != 0u) {
        CallAt(a, base, 0x82604568u, sales, a.archive, 0u);
        sold = true;
      }
      Slot(a, base, a.archive, kLeave);
    }
  });
  if (!found) {
    REXLOG_WARN("Refii co-op world: the host's world ({} bytes) did not load here", bytes.size());
  } else {
    REXLOG_INFO("Refii co-op world: the host's world loaded here ({} bytes): {} quest(s) on the tracker of player "
                "record {}{}, travel stops {}, sales {}",
                bytes.size(), quests ? QuestCount(base, tracker) : 0u, key,
                tracker != 0u ? "" : " (that record's hero has no tracker here: the map lists none)",
                stops ? "loaded" : "not loaded", sold ? "loaded" : "not loaded");
  }
  g_world.loaded = std::move(bytes);
  g_world.loaded_onto = tracker;
}

}  // namespace

namespace refii::vfs {
/// Local player two owns the GUI (hooks/gui_screen_stub_support.cpp).
bool MenusActForPlayerTwo(uint8_t* base);
}  // namespace refii::vfs

PPC_EXTERN_FUNC(__imp__rex_lhQuestTrail_HasNoDestination);
PPC_EXTERN_FUNC(__imp__rex_lhWorldMap_FastTravelRefused);
PPC_EXTERN_FUNC(__imp__rex_FastTravelManager_IsEnabled);
PPC_EXTERN_FUNC(__imp__rex_lhWorldMap_QuestActionCaption);
PPC_EXTERN_FUNC(__imp__rex_lhWorldMap_TargetActionCaption);

namespace {
// Inside the map's travel permission or its refusal, for a player who is not player one.
thread_local bool t_travel_refused = false;

struct RefusedScope {
  bool previous;
  explicit RefusedScope(bool on) : previous(t_travel_refused) { t_travel_refused = on || previous; }
  ~RefusedScope() { t_travel_refused = previous; }
};
}  // namespace

// The world map's travel permission (no arguments; r3 the answer). TU1 0x82471438 / disc 0x8283CFD8.
REX_EXTERN(rex_lhQuestTrail_HasNoDestination) {
  REX_FUNC_PROLOGUE();
  const RefusedScope scope(refii::vfs::MapTravelRefusedNow(base));
  __imp__rex_lhQuestTrail_HasNoDestination(ctx, base);
}

// The world map's travel refusal, its boxes 13 / 12. TU1 0x826C8FE8 / disc 0x825A4F18.
REX_EXTERN(rex_lhWorldMap_FastTravelRefused) {
  REX_FUNC_PROLOGUE();
  const bool refused = refii::vfs::MapTravelRefusedNow(base);
  if (refused) {
    REXLOG_INFO("Refii co-op world: {}'s fast travel from the map is refused with the game's own box: only player "
                "one travels",
                OnlineJoiner() ? "this joiner" : "local player two");
  }
  const RefusedScope scope(refused);
  __imp__rex_lhWorldMap_FastTravelRefused(ctx, base);
}

// The fast-travel manager's "enabled" (r3 the manager). TU1 0x82517010 / disc 0x82607090. Answered 0
// inside the two above for a player who is not player one; everywhere else as shipped.
REX_EXTERN(rex_FastTravelManager_IsEnabled) {
  REX_FUNC_PROLOGUE();
  if (t_travel_refused) {
    ctx.r3.u64 = 0u;
    return;
  }
  __imp__rex_FastTravelManager_IsEnabled(ctx, base);
}

// ONLY PLAYER ONE SELECTS QUESTS AND TRAVELS (user decisions 2026-11-28): a page's A-button caption.
// The quest and job pages' `sub_824708D8(quest r3, pres r4, &name r5, element r6)` (TU1 / disc
// `sub_8283C478`, identical 104; callers the quest fill 0x829AF7C8 and the job fill 0x829B019C) and the sale
// and family pages' `sub_82470A78(pres r3, &name r4, element r5, …)` (disc `sub_8283C618`, identical 149;
// callers `sub_829AACE0` 0x829AADB0, `sub_829B08A8` 0x829B096C) choose "…MISC_TRAVEL" / "…MISC_TRAIL_AND_
// TRAVEL" when `lhWorldMap_CanFastTravelTo` holds (0x82470908 / 0x82470AAC) and "…MISC_TRAIL" otherwise —
// travel, or the trail a primary quest sets — and each has its own no-action exit, the A-button hidden:
// `sub_82426328(a3, a2, 0)` / `sub_82426328(a2, a1, 0)` (`&name, pres, 0`; TU1 0x82426328 / disc 0x823F87C0,
// identical 25), taken when there is no element (r6 / r5 zero) returning at once. For a player who is not
// player one that exit is taken: the page offers no action (and the row's A answers nothing,
// gui_screen_stub_world_map.cpp OnMenuEvent). The hide is a write the page fill states (page_fills_stated.cpp).
REX_EXTERN(rex_lhWorldMap_QuestActionCaption) {
  REX_FUNC_PROLOGUE();
  if (refii::vfs::MapTravelRefusedNow(base)) {
    if (ctx.r6.u32 != 0u) {
      PPCContext work = refii::mods::Frame(ctx, 0x100u);
      work.r3.u64 = ctx.r5.u32;
      work.r4.u64 = ctx.r4.u32;
      work.r5.u64 = 0u;
      CallGuest(work, base, refii::guest::Address(0x82426328u));
    }
    return;
  }
  __imp__rex_lhWorldMap_QuestActionCaption(ctx, base);
}

REX_EXTERN(rex_lhWorldMap_TargetActionCaption) {
  REX_FUNC_PROLOGUE();
  if (refii::vfs::MapTravelRefusedNow(base)) {
    if (ctx.r5.u32 != 0u) {
      PPCContext work = refii::mods::Frame(ctx, 0x100u);
      work.r3.u64 = ctx.r4.u32;
      work.r4.u64 = ctx.r3.u32;
      work.r5.u64 = 0u;
      CallGuest(work, base, refii::guest::Address(0x82426328u));
    }
    return;
  }
  __imp__rex_lhWorldMap_TargetActionCaption(ctx, base);
}

namespace refii::vfs {

void DriveWorldState(PPCContext& ctx, uint8_t* base, float seconds) {
  if (base == nullptr) {
    return;
  }
  const bool host = refii_mod_coop_mode(base) == 2 && refii_mod_player_here(1) != 0;
  if (host) {
    g_world.loaded.clear();
    g_world.loaded_onto = 0u;
    g_world.state_in -= seconds;
    if (g_world.state_in <= 0.0) {
      g_world.state_in = kStateSeconds;
      StateWorld(ctx, base);
    }
    return;
  }
  if (OnlineJoiner()) {
    g_world.stated = false;
    if (RefiiCoopGetOwnHero() == 0) {
      return;  // the plain henchman keeps the 360's map
    }
    g_world.take_in -= seconds;
    if (g_world.take_in <= 0.0) {
      g_world.take_in = kTakeSeconds;
      TakeWorld(ctx, base);
    }
    return;
  }
  // Out of an online session: no statement of the world stays in player one's store (it would be
  // written beside the next save and sent to a later joiner before the host restates).
  g_world.loaded.clear();
  g_world.loaded_onto = 0u;
  if (g_world.stated || refii_bridge_player_blob_get(1, kWorldBlob, nullptr, 0u) > 0) {
    refii_bridge_player_blob_set(1, kWorldBlob, nullptr, 0u);
    g_world.stated = false;
  }
}

bool SessionTravelItemRefused(PPCContext& ctx, uint8_t* base, uint32_t user, uint32_t holder, uint32_t script_at) {
  if (base == nullptr || user == 0u || user != refii_mod_player_hero(base, 2)) {
    return false;  // player one's use, as shipped
  }
  // The script the use runs (lhEntity_UseInventoryItem TU1 0x823E0FF8 / disc 0x823BF9C0, identical): the
  // override r5 when it has text (`sub_821F0240(&v26, a3)`, `v26[0][1]` its length), else the item's
  // definition's InventoryItemComponent "OnCarriedActionUseScriptName" (`sub_821B24F8(&{item+4, item+8})`
  // 0x823E1078, `sub_822A99E8(entry, "InventoryItemComponent")` 0x823E1084, `sub_82323540(component, name)`
  // 0x823E10A8; the name is the global 0x83497D60 its initializer `sub_8324EAB0` builds from that string).
  char script[72] = {};
  const auto copy = [&](uint32_t text, uint32_t length) {
    for (uint32_t i = 0; Heap(text) && i < length && i + 1u < sizeof(script); ++i) {
      script[i] = static_cast<char>(base[text + i]);
    }
  };
  const uint32_t body = Heap(script_at) ? LoadU32(base, script_at) : 0u;
  const uint32_t length = Heap(body) ? LoadU32(base, body + 4u) : 0u;
  if (Heap(body) && length != 0u && length < 0x10000u) {
    copy(LoadU32(base, body), length);
  } else {
    const uint32_t item = Heap(holder) ? LoadU32(base, holder) : 0u;
    if (!Heap(item)) {
      return false;
    }
    PPCContext frame = refii::mods::Frame(ctx, 0x200u);
    const uint32_t area = (frame.r1.u32 + 0x80u) & ~0xFu;
    refii::mods::StoreU32(base, area, LoadU32(base, item + 4u));
    refii::mods::StoreU32(base, area + 4u, LoadU32(base, item + 8u));
    std::memcpy(base + area + 0x10u, "InventoryItemComponent", 23u);
    std::memcpy(base + area + 0x30u, "OnCarriedActionUseScriptName", 29u);
    const auto call = [&](uint32_t tu1, uint32_t r3, uint32_t r4) {
      PPCContext work = frame;
      work.r3.u64 = r3;
      work.r4.u64 = r4;
      return CallGuest(work, base, refii::guest::Address(tu1)) ? work.r3.u32 : 0u;
    };
    const uint32_t entry = LoadU32(base, area) != 0u ? call(0x821B24F8u, area, 0u) : 0u;
    const uint32_t component = Heap(entry) ? call(0x822A99E8u, entry, area + 0x10u) : 0u;
    const uint32_t name = Heap(component) ? call(0x82323540u, component, area + 0x30u) : 0u;
    copy(name, sizeof(script) - 1u);
  }
  const std::string_view refusal = refii::coop::SessionTravelRefusal(script);
  if (refusal.empty()) {
    return false;
  }
  char text[96];
  std::snprintf(text, sizeof(text), "{%.*s}", static_cast<int>(refusal.size()), refusal.data());
  refii_mod_message_box(2, "", text, nullptr, nullptr);
  REXLOG_INFO("Refii co-op world: player two's {} is refused ({}): only player one moves the session", script,
              OnlineJoiner() ? "this joiner" : refii_mod_player_here(1) != 0 && refii_mod_coop_mode(base) == 2
                                                    ? "the joiner's run here"
                                                    : "local player two");
  return true;
}

bool MapTravelRefusedNow(uint8_t* base) {
  return base != nullptr && refii::coop::MapTravelRefused(OnlineJoiner(), MenusActForPlayerTwo(base));
}

}  // namespace refii::vfs
