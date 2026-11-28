// refii — AN ONLINE JOINER'S HERO SHOPS (Fable 2 co-op). The host runs the shop; the joiner's screen shows it.
//
// WHY THE HOST. A network client builds neither the villager (26) nor the shopkeeper (27) — the client switch
// sends both to its default arm (`jpt_8238DF3C` cases 25 / 26 → 0x8238E438, TU1; `jpt_8239370C`, disc) — nor the
// shop entity's stock (124), and every routine of the shop screen reads the keeper's 27 inline (the component
// mask `lwz r11,0x24(keeper)` / `extrwi r10,r11,1,4` and the map at +0x48, e.g. the buy entry `sub_826AFF48`
// 0x826AFFC4..0x826B00DC, which returns at `beq loc_826B0760` without one) and its stock through
// `sub_82873570(comp27)` → comp27+16 → the shop entity's 124. So the shop is only where the keeper is whole: the
// host. The joiner's own screen 22 shows what the host's run of it says.
//
// THE OPENING. The joiner's A on a shopkeeper reaches the host as `villuse` (coop_villager_use.cpp), whose
// replay `CECVillager_OnUse(comp26, hero two)` used to stop at the henchman's refusal (`clrlwi r10,r27,24` /
// `beq 0x827E1324`, 0x827E12F0 / disc 0x827A8298). For that replay (a hero joiner only — the replay is never
// made for the henchman, ReplayEstateIntent) the refusal is lifted (coop_hero_hooks.cpp), so the shipped path
// runs: the keeper's comp27, the shop's hours `sub_82874128(comp27)` (0x827E143C), then the opener
// `sub_823C4060(*(QM+48), keeper)` (0x827E1460 / disc `sub_824F9290` 0x827A8408). The opener is taken here
// (shop_keeper_stated.cpp): no screen opens on the host; the session begins instead. A closed shop takes the
// other arm: the HUD line `sub_8230C908(*(*(GUI+0x20)+0x58), 10, &message, 1)` (0x827E14E8 / disc
// `sub_82332D48` 0x827A8490), skipped on the host for the replay and made on the joiner
// (RefiiCoopJoinerShopClosed), and the keeper's closed line `sub_828742E8` → `sub_825D7060` (0x827E14FC..
// 0x827E151C), which the guest's own say packet carries to the joiner.
//
// THE HOST'S RUN. The screen-22 handler h (vtable 0x820CE708 / disc 0x820C11F8) is registered whether or not
// the screen is up. For each step the host lends it the keeper as the opener does — `{off_82000B7C, 0}`,
// `sub_82214898(&weak, keeper)`, `sub_824D1EC8(h+0x190, &weak)`, `sub_82193E38(&weak)` (0x823C40AC..0x823C40F0;
// disc off_820F1664, `sub_822149C8`, `sub_82978470`, `sub_821940C8`) — answers "the local player" with player two
// (`sub_821C7CE0`, the shop's buyer and seller, and the price / tattoo sites' hero one: coop_hero_hooks.cpp
// RefiiCoopShopPlayer / RefiiCoopShopHero, the same seams local player two's shop uses), and runs the shop's
// own routine for the step:
//   open      the population `h+24` vf+4 = `lhGuiShopScreen_PopulateMenu` (0x82994218: the stock fill
//             `sub_8296ACB8(comp124)`, the buy rows `sub_82995B00`, the sell rows over hero two's comp13
//             `sub_829955B0`, the folders, the OUT_OF_STOCK box), with h+441 = 0, h+396 = 0 and the transacted
//             map h+444 emptied (`sub_822F8640`), then the keeper's greeting (`CECShopkeeper_Greet(&key,
//             comp27)` → `sub_826B1558(h, key)`, as the screen's Update makes it: 0x826AE3BC / 0x826AE3F0);
//   buy/sell  the event copied to QM+80..+88 (`lhCharString_Assign`, 0x826AE5E8..0x826AE5F8) and the buy entry
//             `sub_826AFF48(h, event)` / the sell entry `sub_826B0D10(h, event)` (0x826AE668 / 0x826AE654);
//   header    with h+441 set: h+441 = 0, the population, the transacted map cleared (0x826AE6D0 / 0x826AE6D8);
//   answer    the question's helper by its id — 17 `sub_826AF430`, 19 `sub_826AFB98`, 20 `sub_826AF888` —
//             with the code (0x826AE834..0x826AE864);
//   close     the screen's OnExit `sub_826AE090(h)` (the preview's release, the visit's `sub_8296AF98(comp124)`).
// What those spawn is taken by the spawn sink (guest_tick_pump_state.cpp), the boxes they open by the modal
// sink (display_box_hooks.cpp: stated here, never opened), their menu Lua is recorded nowhere
// (resource_manager_open.cpp); their keeper lines (`sub_826B1558`: the keeper's comp100, `sub_825D7060`) are
// said on the host and reach the joiner by the guest's own say packet. Each row's asking price is the screen's
// own: `CECShopkeeper_BuyPrice` / `SellPrice(comp27, &item)` on the row's ticket (`sub_82880BC0`), then
// `sub_8230AE78`, `L"$"` + it (`lhWideString_CtorFromWStr(word_820CEE80)`, `sub_82B3EE50`) — the item page's
// price write `sub_82992F90` 0x82993148..0x8299317C (disc `sub_828E4958` 0x828E4B10..0x828E4B40).
// What the run gives or takes from hero two goes to the joiner's own hero (`invadd` / `invdel`); the gold goes by
// the guest's money message (CECMoney_Modify's host send, `sub_825CCBE0`).
//
// THE STATEMENT: `refii.shop`, a blob of player one's store (core/joiner_shop_statement.h). Dropped when the
// session ends.
//
// THE JOINER. On `shopopen` and the statement it names, its own opener with its copy of the keeper; its screen
// stub (gui_screen_stub_shop.cpp) takes the rows from the statement where the population ran, the asking price
// of the highlighted row as the row edge states it (`@PageFill 22 row <ticket>`), each new box through its own
// `lhGuiManager_OpenModal`, and sends the buy, the sell, the header and the answers to the host; a newer list goes
// in where the screen's Update repopulates (a header highlighted, no box up). Its B is the guest's own; when its
// screen is gone the host's session closes.
//
// LOCAL CO-OP is untouched: local player two's shop is the shipped screen on that machine
// (coop_hero_hooks.cpp g_shopping_player_two). Nothing here runs outside an online session with a hero joiner.

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <optional>
#include <string>
#include <unordered_map>
#include <vector>

#include <rex/logging.h>

#include "core/guest_build.h"
#include "core/guest_state.h"
#include "core/gui_native_calls.h"
#include "core/joiner_shop_statement.h"
#include "core/payload_bridge_fable2.h"
#include "core/refii_mod.h"
#include "core/refii_mod_requests.h"
#include "hooks/gui_screen_stub_support.h"
#include "hooks/page_fills_stated.h"
#include "hooks/ppc_externs.h"
#include "vfs_hooks_private.h"

extern "C" int RefiiCoopGetOwnHero(void);
extern "C" uint8_t* RefiiGuestBase(void);
extern "C" uint32_t refii_household_resolve_uid(PPCContext* ctx, uint8_t* base, uint64_t uid);
extern "C" uint64_t refii_household_shared_uid(uint8_t* base, uint32_t entity);
/// Calls made for player two: "the local player" answers player two (coop_hero_hooks.cpp).
extern "C" void RefiiCoopLocalPlayerTwoCall(int on);

PPC_EXTERN_FUNC(rex_lhWideString_Dtor);
PPC_EXTERN_FUNC(rex_lhGuiManager_OpenModal);
PPC_EXTERN_FUNC(rex_lhGuiShopScreen_Open);

namespace {

namespace stub = refii::vfs::stub;
using refii::coop::ShopModal;
using refii::coop::ShopRow;
using refii::coop::ShopStatement;
using refii::guest::Address;
using refii::mods::LoadU32;
using refii::mods::LoadU8;
using refii::mods::StoreU32;
using refii::mods::StoreU8;

constexpr char kBlobKey[] = "refii.shop";
/// The front page's statements for the joiner: {session, serial (u32 little-endian each), the captured calls}.
constexpr char kFrontKey[] = "refii.shopfront";
/// The item pane of the joiner's highlighted row: {session, serial, type, record (u32 LE each), the captured calls}.
constexpr char kLookKey[] = "refii.shoplook";
/// A highlight held this long is asked for (fast scrolling sends nothing until it stops).
constexpr double kLookSettle = 0.25;
constexpr uint32_t kShopVtable = 0x820CE708u;
constexpr uint32_t kWeakHandleVtable = 0x82000B7Cu;
constexpr uint32_t kKeeper = 27u;
constexpr uint32_t kVillager = 26u;
constexpr uint32_t kInventory = 13u;
// The row edge's price element, as the shop's item page names it and Godot reads it
// (Engine_UiItemPage.ShopPricePath).
constexpr char kPricePath[] = "Foreground.Book.Pages.Page1.Contents.Text.TextElements.PriceValue";
constexpr size_t kModalsKept = 8u;

bool Heap(uint32_t at) { return at >= 0x10000u && at < 0xE0000000u; }

/// The UID both machines know `entity` by (mod_events.cpp SharedUid: the UID on the host, a replica's net id,
/// comp91 +120, on a client): the keeper of every statement and intent.
uint64_t Uid(uint8_t* base, uint32_t entity) { return Heap(entity) ? refii_household_shared_uid(base, entity) : 0u; }

bool OnlineJoiner() { return refii_mod_player_here(1) == 0 && refii_mod_player_here(2) != 0; }

bool HeroJoiner(uint8_t* base) { return RefiiCoopGetOwnHero() != 0 && refii_mod_player_hero(base, 2) != 0u; }

bool OnlineHost(uint8_t* base) { return refii_mod_coop_mode(base) == 2 && refii_mod_player_here(1) != 0 && HeroJoiner(base); }

uint32_t Qm(uint8_t* base) {
  const uint32_t gui = stub::Gui(base);
  return Heap(gui) ? LoadU32(base, gui + 28u) : 0u;
}

/// The in-game screen up is the shop: `*(GUI+56)` +4 (as coop_hero_hooks.cpp ShopScreenUp reads it).
bool ShopScreenUp(uint8_t* base) {
  const uint32_t gui = stub::Gui(base);
  const uint32_t screens = Heap(gui) ? LoadU32(base, gui + 56u) : 0u;
  const uint32_t id = Heap(screens) ? LoadU32(base, screens + 4u) : 0xFFFFFFFFu;
  return id != 0xFFFFFFFFu && refii::guest::ScreenIdToTu1(id) == 22u;
}

/// Scratch under a screen stub's own (the joiner's calls are made from inside its slots, whose scratch is live).
stub::Scratch Below(const PPCContext& ctx) {
  PPCContext deeper = ctx;
  deeper.r1.u32 = ctx.r1.u32 - 0x800u;
  return stub::Claim(deeper);
}

void Post(const char* action, uint64_t uid, int32_t value) {
  char text[96];
  std::snprintf(text, sizeof(text), "%s:%016llX:%d", action, static_cast<unsigned long long>(uid), value);
  refii_bridge_estate_intent_post(text);
}

/// Hero two's inventory as (record → count): the comp13 list at +16 (sentinel), item at node+8, record at
/// item+4, count at item+12 (guest_tick_pump.cpp HeldCount reads the same).
std::unordered_map<uint32_t, uint32_t> Held(uint8_t* base) {
  std::unordered_map<uint32_t, uint32_t> held;
  const uint32_t two = refii_mod_player_hero(base, 2);
  const uint32_t inventory = two != 0u ? refii_mod_component(base, two, kInventory) : 0u;
  const uint32_t sentinel = Heap(inventory) ? LoadU32(base, inventory + 16u) : 0u;
  uint32_t node = Heap(sentinel) ? LoadU32(base, sentinel) : 0u;
  for (uint32_t hops = 0u; hops < 4096u && Heap(node) && node != sentinel; ++hops) {
    const uint32_t item = LoadU32(base, node + 8u);
    if (Heap(item)) {
      held[LoadU32(base, item + 4u)] += LoadU32(base, item + 12u);
    }
    node = LoadU32(base, node);
  }
  return held;
}

// ---- THE HOST ----------------------------------------------------------------------------------------

struct Host {
  bool open = false;
  uint32_t session = 0u;
  uint64_t keeper = 0u;
  uint32_t population = 0u;
  uint32_t flags = 0u;
  std::vector<ShopRow> rows;
  std::vector<std::pair<uint32_t, std::string>> prices;
  std::vector<ShopModal> modals;
  uint32_t modal_serial = 0u;
  std::string written;
  std::vector<std::pair<std::string, int32_t>> waiting;  // steps the host could not run yet
  uint32_t asked_record = 0u;  // the record the joiner's next buy / sale names (`shoprow`)
  uint32_t front_serial = 0u;
  uint32_t look_serial = 0u;
};

Host g_host;
uint32_t g_sessions = 0u;
thread_local int t_running = 0;
thread_local bool t_replay = false;
thread_local uint32_t t_replay_keeper = 0u;
thread_local bool t_replay_closed = false;

/// The front page's writes by name while the host runs its fill for the joiner (RelayFront): kind, path, value.
struct NamedWrite {
  const char* kind;
  std::string path;
  std::string value;
};
thread_local bool t_relay = false;
thread_local std::vector<NamedWrite> t_named;

/// The row the host's run of the highlight edge answers (ShowRow; JoinerShopLookRow).
struct Look {
  bool on = false;
  uint32_t type = 0u;
  uint32_t id = 0u;
  std::string name;
};
thread_local Look t_look;

/// One step of the host's run for hero two: "the local player" is player two for its length.
struct Run {
  Run() {
    ++t_running;
    RefiiCoopLocalPlayerTwoCall(1);
  }
  ~Run() {
    RefiiCoopLocalPlayerTwoCall(0);
    --t_running;
  }
  Run(const Run&) = delete;
  Run& operator=(const Run&) = delete;
};

/// The handler's keeper, as the opener files it (see the file note).
void Lend(PPCContext& ctx, uint8_t* base, const stub::Scratch& s, uint32_t h, uint32_t keeper) {
  const uint32_t weak = s.data + 0x3E0u;
  StoreU32(base, weak, Address(kWeakHandleVtable));
  StoreU32(base, weak + 4u, 0u);
  stub::Call(ctx, base, s.frame, 0x82214898u, {weak, keeper});
  stub::Call(ctx, base, s.frame, 0x824D1EC8u, {h + 0x190u, weak});
  StoreU32(base, weak, Address(kWeakHandleVtable));
  stub::Call(ctx, base, s.frame, 0x82193E38u, {weak});
}

/// The asking price of the row with `ticket`, as the item page writes it; empty when the ticket names no item.
std::string PriceText(PPCContext& ctx, uint8_t* base, const stub::Scratch& s, uint32_t comp27, uint32_t ticket,
                      bool buy) {
  const uint32_t qm = Qm(base);
  const uint32_t menu = Heap(qm) ? LoadU32(base, qm + 60u) : 0u;
  if (!Heap(menu)) {
    return std::string();
  }
  const uint32_t handle = s.data + 0x340u;
  const uint32_t number = s.data + 0x360u;
  const uint32_t dollar = s.data + 0x370u;
  for (uint32_t i = 0u; i < 16u; i += 4u) {
    StoreU32(base, handle + i, 0u);
  }
  const bool found = (stub::Call(ctx, base, s.frame, 0x82880BC0u, {menu + 4u, ticket, handle}) & 0xFFu) != 0u;
  const uint32_t item = LoadU32(base, handle);
  if (!found || !Heap(item)) {
    return std::string();
  }
  const uint32_t price = stub::Call(ctx, base, s.frame, buy ? 0x828727B8u : 0x82872910u, {comp27, handle});
  StoreU32(base, number, 0u);
  StoreU32(base, dollar, 0u);
  const uint32_t made = stub::Call(ctx, base, s.frame, 0x8230AE78u, {number, price});
  PPCContext make = ctx;
  make.r1.u32 = s.frame;
  make.r3.u64 = dollar;
  make.r4.u64 = Address(0x820CEE80u);
  rex_lhWideString_CtorFromWStr(make, base);
  stub::Call(ctx, base, s.frame, 0x82B3EE50u, {dollar, Heap(made) ? made : number});
  std::string text = refii::vfs::WideTextUtf8(base, dollar);
  for (const uint32_t wide : {dollar, number}) {
    PPCContext drop = ctx;
    drop.r1.u32 = s.frame;
    drop.r3.u64 = wide;
    rex_lhWideString_Dtor(drop, base);
  }
  stub::Call(ctx, base, s.frame, 0x82764DC0u, {item, handle});  // lhTrackedObject_RemoveHandleRef
  return text;
}

/// The screen's own population, into the statement.
void Populate(PPCContext& ctx, uint8_t* base, const stub::Scratch& s, uint32_t h, uint32_t comp27) {
  refii::vfs::OpenMenuSpawnSink();
  stub::CallSlot(ctx, base, s.frame, h + 24u, 1u, {});
  std::vector<ShopRow> rows = refii::vfs::CloseMenuSpawnSink();
  g_host.prices.clear();
  for (const ShopRow& row : rows) {
    if (row.id != 0u && (row.event_type == 30 || row.event_type == 31)) {
      std::string text = PriceText(ctx, base, s, comp27, row.id, row.event_type == 30);
      if (!text.empty()) {
        g_host.prices.emplace_back(row.id, std::move(text));
      }
    }
  }
  g_host.rows = std::move(rows);
  ++g_host.population;
  REXLOG_INFO("Refii co-op shop: the joiner's list here: {} row(s), {} priced (population {})", g_host.rows.size(),
              g_host.prices.size(), g_host.population);
}

/// The keeper's greeting, as the screen's Update makes it (gui_screen_stub_shop.cpp).
void Greet(PPCContext& ctx, uint8_t* base, const stub::Scratch& s, uint32_t h, uint32_t comp27) {
  const uint32_t key = s.data + 0x300u;
  const uint32_t said = s.data + 0x304u;
  StoreU32(base, key, 0u);
  stub::Call(ctx, base, s.frame, 0x828731A8u, {key, comp27});
  if ((stub::Call(ctx, base, s.frame, 0x8229B1B8u, {key, Address(0x82000CA0u)}) & 0xFFu) != 0u) {
    StoreU32(base, said, 0u);
    const uint32_t line = stub::Call(ctx, base, s.frame, 0x821F0240u, {said, key});
    stub::Call(ctx, base, s.frame, 0x826B1558u, {h, line});
    REXLOG_INFO("Refii co-op shop: the keeper greets the joiner's hero here");
  }
  stub::DropString(ctx, base, s.frame, key);
}

void Write() {
  ShopStatement statement;
  statement.session = g_host.session;
  statement.keeper = g_host.keeper;
  statement.population = g_host.population;
  statement.flags = g_host.flags;
  statement.rows = g_host.rows;
  statement.prices = g_host.prices;
  statement.modals = g_host.modals;
  std::string bytes = refii::coop::Encode(statement);
  if (bytes == g_host.written) {
    return;
  }
  refii_bridge_player_blob_set(1, kBlobKey, reinterpret_cast<const uint8_t*>(bytes.data()),
                               static_cast<uint32_t>(bytes.size()));
  g_host.written = std::move(bytes);
}

void End(const char* why) {
  if (g_host.open) {
    REXLOG_INFO("Refii co-op shop: the joiner's shop {:016X} closes here ({})", g_host.keeper, why);
  }
  if (!g_host.written.empty()) {
    refii_bridge_player_blob_set(1, kBlobKey, nullptr, 0u);
  }
  g_host = Host{};
}

/// The host can run a step now: its own shop is not up (the handler is the host's then) and no box of its own
/// is (`QM+40 == 1`: the answer helpers close the question up, `sub_8236FEC8(QM, 3)`).
bool HostReady(uint8_t* base) {
  const uint32_t qm = Qm(base);
  return !ShopScreenUp(base) && Heap(qm) && LoadU32(base, qm + 40u) == 1u;
}

/// The keeper, its comp27 and the handler; false when any is gone.
bool Parts(PPCContext& ctx, uint8_t* base, const stub::Scratch& s, uint32_t* keeper, uint32_t* comp27, uint32_t* h) {
  *keeper = g_host.keeper != 0u ? refii_household_resolve_uid(&ctx, base, g_host.keeper) : 0u;
  *comp27 = *keeper != 0u && refii_mod_entity_alive(base, *keeper) != 0 ? refii_mod_component(base, *keeper, kKeeper) : 0u;
  *h = stub::ScreenHandler(ctx, base, s.frame, 22u, kShopVtable);
  return *comp27 != 0u && *h != 0u;
}

/// What the step gave or took from hero two, to the joiner's own hero.
void SendInventoryChange(const std::unordered_map<uint32_t, uint32_t>& before,
                         const std::unordered_map<uint32_t, uint32_t>& after) {
  for (const auto& [record, count] : after) {
    const auto was = before.find(record);
    const uint32_t had = was != before.end() ? was->second : 0u;
    if (count > had) {
      Post("invadd", record, static_cast<int32_t>(std::min<uint32_t>(count - had, 99u)));
      REXLOG_INFO("Refii co-op shop: hero two gained {} x 0x{:08X} here; sent to the joiner", count - had, record);
    }
  }
  for (const auto& [record, had] : before) {
    const auto now = after.find(record);
    const uint32_t count = now != after.end() ? now->second : 0u;
    if (count < had) {
      Post("invdel", record, static_cast<int32_t>(std::min<uint32_t>(had - count, 99u)));
      REXLOG_INFO("Refii co-op shop: hero two lost {} x 0x{:08X} here; sent to the joiner", had - count, record);
    }
  }
}

/// The item the menu's ticket table files under `ticket` (`sub_82880BC0(*(QM+60)+4, ticket, &handle)`), its
/// reference released; 0 for none.
uint32_t TicketItem(PPCContext& ctx, uint8_t* base, const stub::Scratch& s, uint32_t ticket) {
  const uint32_t qm = Qm(base);
  const uint32_t menu = Heap(qm) ? LoadU32(base, qm + 60u) : 0u;
  if (!Heap(menu)) {
    return 0u;
  }
  const uint32_t handle = s.data + 0x340u;
  for (uint32_t i = 0u; i < 16u; i += 4u) {
    StoreU32(base, handle + i, 0u);
  }
  const bool found = (stub::Call(ctx, base, s.frame, 0x82880BC0u, {menu + 4u, ticket, handle}) & 0xFFu) != 0u;
  const uint32_t item = LoadU32(base, handle);
  if (Heap(item)) {
    stub::Call(ctx, base, s.frame, 0x82764DC0u, {item, handle});  // lhTrackedObject_RemoveHandleRef
  }
  return found && Heap(item) ? item : 0u;
}

/// `item` is in the keeper's stock: the shop entity's list `sub_8296B078(sub_82873570(comp27), &vector)`, 16-byte
/// entries with the item first, freed by `sub_823134C0` (as the buy rows read it, `sub_82995B00`).
bool InStock(PPCContext& ctx, uint8_t* base, const stub::Scratch& s, uint32_t comp27, uint32_t item) {
  const uint32_t shop = stub::Call(ctx, base, s.frame, 0x82873570u, {comp27});
  if (!Heap(shop)) {
    return false;
  }
  const uint32_t vector = s.data + 0x320u;
  for (uint32_t i = 0u; i < 16u; i += 4u) {
    StoreU32(base, vector + i, 0u);
  }
  const uint32_t count = stub::Call(ctx, base, s.frame, 0x8296B078u, {shop, vector});
  const uint32_t first = LoadU32(base, vector + 4u);
  bool found = false;
  for (uint32_t i = 0u; Heap(first) && i < count && i < 512u && !found; ++i) {
    found = LoadU32(base, first + i * 16u) == item;
  }
  stub::Call(ctx, base, s.frame, 0x823134C0u, {vector});
  return found;
}

/// `item` is one of hero two's own (the comp13 list Held walks).
bool HeroTwoHolds(uint8_t* base, uint32_t item) {
  const uint32_t two = refii_mod_player_hero(base, 2);
  const uint32_t inventory = two != 0u ? refii_mod_component(base, two, kInventory) : 0u;
  const uint32_t sentinel = Heap(inventory) ? LoadU32(base, inventory + 16u) : 0u;
  uint32_t node = Heap(sentinel) ? LoadU32(base, sentinel) : 0u;
  for (uint32_t hops = 0u; hops < 4096u && Heap(node) && node != sentinel; ++hops) {
    if (LoadU32(base, node + 8u) == item) {
      return true;
    }
    node = LoadU32(base, node);
  }
  return false;
}

/// The joiner's row, by this machine's ticket for it now. The ticket table is the menu's, which an item book's
/// population empties and renumbers (`sub_82880CE0`: `*a1 = 0` and the map cleared; its callers are the item
/// books' populates sub_829B0A58 .. sub_829B2A20), and every population files a fresh ticket per item
/// (`sub_82880AC8`: `++*a1` — the sell rows file one for EVERY inventory item, `bl sub_82880AC8` 0x829959E4 before
/// the eligibility test `bl sub_82996A50` 0x82995A1C; disc 0x828E0974 / 0x828E09AC — then the buy rows):
/// the joiner's row can come from an earlier list than the one standing here. So the row is the RECORD the joiner
/// states with it (`shoprow`, the record of the row it pushed, from the lists this session stated to it); the
/// ticket answers when it still names an item of that record where the row has it (the stock for a buy, hero two's
/// inventory for a sale); else this list's row of that record, else the list made again and its row. 0 when it is
/// gone (bought out, sold) or no record is known; every such answer is logged.
uint32_t RowTicket(PPCContext& ctx, uint8_t* base, const stub::Scratch& s, uint32_t h, uint32_t comp27,
                   uint32_t type, uint32_t ticket, uint32_t asked) {
  const char* what = type == 30u ? "buy" : "sale";
  uint32_t record = 0u;
  for (const ShopRow& row : g_host.rows) {
    if (row.id == ticket && row.event_type == static_cast<int32_t>(type)) {
      record = row.record;
      break;
    }
  }
  if (asked != 0u && record != asked) {
    REXLOG_INFO("Refii co-op shop: the joiner's {} names ticket {} of record 0x{:08X}; this list's ticket {} is {}; "
                "the row is taken by its record",
                what, ticket, asked, ticket, record != 0u ? "another row" : "not in it");
    record = asked;
  }
  if (record == 0u) {
    REXLOG_INFO("Refii co-op shop: the joiner's {} of ticket {} names no row here and no record came with it; "
                "nothing runs", what, ticket);
    return 0u;
  }
  const auto holds = [&](uint32_t id) {
    const uint32_t item = TicketItem(ctx, base, s, id);
    return item != 0u && LoadU32(base, item + 4u) == record &&
           (type == 30u ? InStock(ctx, base, s, comp27, item) : HeroTwoHolds(base, item));
  };
  if (holds(ticket)) {
    return ticket;
  }
  for (const ShopRow& row : g_host.rows) {
    if (row.event_type == static_cast<int32_t>(type) && row.record == record && row.id != 0u && row.id != ticket &&
        holds(row.id)) {
      REXLOG_INFO("Refii co-op shop: the joiner's {} of record 0x{:08X} is this list's ticket {} (it sent {})", what,
                  record, row.id, ticket);
      return row.id;
    }
  }
  REXLOG_INFO("Refii co-op shop: ticket {} no longer names the joiner's row here; the list is made again", ticket);
  Populate(ctx, base, s, h, comp27);
  for (const ShopRow& row : g_host.rows) {
    if (row.event_type == static_cast<int32_t>(type) && row.record == record && row.id != 0u) {
      return row.id;
    }
  }
  REXLOG_INFO("Refii co-op shop: no {} row of record 0x{:08X} is left here (bought out or sold); nothing runs", what,
              record);
  return 0u;
}

/// The front page as the shop's own fill states it for hero two: `sub_82991800` (TU1; disc `sub_828E31B0`, the same
/// 1002 instructions; the front page's vf+4, run by OnEnter) reads the keeper `**(h+404)` and its 27 / 124 — which a
/// client has none of, so on the joiner it writes nothing and the page keeps its file's text ("Example Shop Name") —
/// and states every line by name through three wrappers, the text `sub_8246B888(pres, &name, &text)` (16 calls),
/// the slide `sub_82426D18(&name, pres, slide)` (3) and the shown flag `sub_82426328(&name, pres, on)` (6) (disc
/// `sub_828374B8`, `sub_823F91B0`, `sub_823F87C0`). Each wrapper finds its element in the current screen's
/// presentation and writes it; on the host there is no shop presentation, so while this runs the wrappers' calls are
/// taken (below) and not run, and stated as the page fill `@PageFill 22 front` the joiner's screen shows — made
/// under this thread's GUI capture (refii_bridge_gui_capture_begin, the card's relay) and kept as player one's blob
/// kFrontKey.
void RelayFront(PPCContext& ctx, uint8_t* base, const stub::Scratch& s, uint32_t h) {
  t_named.clear();
  t_relay = true;
  stub::Call(ctx, base, s.frame, 0x82991800u, {h + 0x58u});
  t_relay = false;
  refii::vfs::GuiNativeCall call;
  call.name = "@PageFill";
  const auto add = [&call](std::string text) {
    refii::vfs::GuiNativeArgument argument;
    argument.kind = refii::vfs::GuiNativeArgument::Kind::kString;
    argument.text = std::move(text);
    call.arguments.push_back(std::move(argument));
  };
  add("22");
  add("front");
  add("0");
  for (NamedWrite& write : t_named) {
    add(write.kind);
    add(std::move(write.path));
    add(std::move(write.value));
  }
  const size_t writes = t_named.size();
  t_named.clear();
  refii_bridge_gui_capture_begin();
  if (writes != 0u) {
    refii::vfs::PostGuiNativeCall(std::move(call));
  }
  std::vector<uint8_t> blob(8u);
  std::vector<uint8_t> calls(0x10000u);
  const uint32_t size = refii_bridge_gui_capture_end(calls.data(), static_cast<uint32_t>(calls.size()));
  calls.resize(size <= calls.size() ? size : 0u);
  ++g_host.front_serial;
  for (int i = 0; i < 4; ++i) {
    blob[i] = static_cast<uint8_t>(g_host.session >> (8 * i));
    blob[4 + i] = static_cast<uint8_t>(g_host.front_serial >> (8 * i));
  }
  blob.insert(blob.end(), calls.begin(), calls.end());
  refii_bridge_player_blob_set(1, kFrontKey, blob.data(), static_cast<uint32_t>(blob.size()));
  REXLOG_INFO("Refii co-op shop: the front page for the joiner: {} write(s) by name stated (serial {}, {} byte(s))",
              writes, g_host.front_serial, calls.size());
}

/// THE ITEM PANE FOR THE JOINER'S HIGHLIGHTED ROW. The shop's edge `sub_826B13B8(h, &row)` (TU1; disc `sub_825958B8`,
/// 103 instructions, identical), on a highlight whose id differs from h+408 (`lwz r11,0x198(r31)` / `cmpw`,
/// 0x826B13D0..0x826B13DC), stores it and runs the three pages: the front page's edge `sub_82991720(h+88)`
/// (0x826B1400), the item page `sub_82992998(h+128)` (0x826B1408) and the stats page `sub_82993408(h+368)`
/// (0x826B1410) (disc 0x82595900 / 0x82595908 / 0x82595910). Each page reads the highlighted row itself
/// (`lhGuiMenu_GetHighlightedItemData`, answered here with the joiner's row: JoinerShopLookRow) and the item behind
/// its ticket (`sub_826AF370(h, ticket, &item)` → `sub_82880BC0`, the menu's ticket table — this machine's). What they
/// write — through the element finder `sub_82426798` and the writers SetText, SetTextFromTag, GoToSlide, the shown
/// flag, the tint, the by-name wrappers, the preview's Set / Hide — is the item pane; the stats page also moves two
/// elements (`sub_824252E8` / `sub_824251E0`), which is layout. Run here inside a page fill RELAY
/// (page_fills_stated.cpp: the finder answers stand-ins, the presentation is never touched) and the fill scope "row"
/// the local shop's Update opens around the same edge (gui_screen_stub_shop.cpp), under this thread's GUI capture,
/// and kept as player one's blob kLookKey. h+408 is set to no row first so the edge's change arm runs.
void ShowRow(PPCContext& ctx, uint8_t* base, const stub::Scratch& s, uint32_t h, uint32_t type, uint32_t record) {
  const ShopRow* found = nullptr;
  for (const ShopRow& row : g_host.rows) {
    if (row.event_type == static_cast<int32_t>(type) && row.record == record && row.id != 0u) {
      found = &row;
      break;
    }
  }
  if (found == nullptr) {
    REXLOG_INFO("Refii co-op shop: the joiner's highlighted row (type {}, record 0x{:08X}) is in no list here; "
                "no pane", type, record);
    return;
  }
  if (TicketItem(ctx, base, s, found->id) == 0u) {
    REXLOG_INFO("Refii co-op shop: the joiner's highlighted row's ticket {} names no item here; no pane", found->id);
    return;
  }
  const uint32_t row = s.data + 0x180u;   // {type, id, String1, String2}
  const uint32_t copy = s.data + 0x190u;  // the edge's own copy, which it releases
  StoreU32(base, row, type);
  StoreU32(base, row + 4u, found->id);
  stub::MakeString(ctx, base, s.frame, row + 8u, s.data + 0x1A0u, "");
  stub::MakeString(ctx, base, s.frame, row + 12u, s.data + 0x1A0u, "");
  StoreU32(base, h + 0x198u, 0xFFFFFFFFu);
  t_look = Look{true, type, found->id, found->name};
  refii_bridge_gui_capture_begin();
  refii::vfs::OpenPageFillRelay(ctx, base);
  refii::vfs::OpenPageFill(22u, "row");
  stub::Call(ctx, base, s.frame, 0x826B13B8u, {h, stub::Call(ctx, base, s.frame, 0x826AE560u, {copy, row})});
  refii::vfs::ClosePageFill(found->id);
  refii::vfs::ClosePageFillRelay();
  std::vector<uint8_t> calls(0x10000u);
  const uint32_t size = refii_bridge_gui_capture_end(calls.data(), static_cast<uint32_t>(calls.size()));
  calls.resize(size <= calls.size() ? size : 0u);
  t_look = Look{};
  stub::DropString(ctx, base, s.frame, row + 12u);
  stub::DropString(ctx, base, s.frame, row + 8u);
  ++g_host.look_serial;
  std::vector<uint8_t> blob(16u);
  const uint32_t head[4] = {g_host.session, g_host.look_serial, type, record};
  for (int w = 0; w < 4; ++w) {
    for (int i = 0; i < 4; ++i) {
      blob[4 * w + i] = static_cast<uint8_t>(head[w] >> (8 * i));
    }
  }
  blob.insert(blob.end(), calls.begin(), calls.end());
  refii_bridge_player_blob_set(1, kLookKey, blob.data(), static_cast<uint32_t>(blob.size()));
  const refii::vfs::PageFillClosed& closed = refii::vfs::LastPageFillClosed();
  REXLOG_INFO("Refii co-op shop: the item pane for the joiner's row (type {}, record 0x{:08X}, ticket {} here): {} "
              "byte(s) stated (serial {}; {})",
              type, record, found->id, calls.size(), g_host.look_serial, closed.posted ? closed.counts : "no writes");
}

void Begin(PPCContext& ctx, uint8_t* base, uint32_t keeper) {
  if (!OnlineHost(base)) {
    return;
  }
  if (!HostReady(base)) {
    REXLOG_INFO("Refii co-op shop: the joiner's shop at {:08X} does not open: this machine's own shop or box is up",
                keeper);
    return;
  }
  if (g_host.open) {
    End("another shop opens");
  }
  const stub::Scratch s = stub::Claim(ctx);
  const uint32_t comp27 = refii_mod_component(base, keeper, kKeeper);
  const uint32_t h = stub::ScreenHandler(ctx, base, s.frame, 22u, kShopVtable);
  if (comp27 == 0u || h == 0u) {
    REXLOG_WARN("Refii co-op shop: the joiner's shop at {:08X} has no keeper (27) or no shop screen here", keeper);
    return;
  }
  g_host.open = true;
  g_host.session = ++g_sessions;
  g_host.keeper = Uid(base, keeper);
  const uint32_t comp26 = refii_mod_component(base, keeper, kVillager);
  g_host.flags = comp26 != 0u && LoadU32(base, comp26 + 156u) == 31u ? refii::coop::kShopTitles : 0u;
  {
    Run run;
    Lend(ctx, base, s, h, keeper);
    StoreU8(base, h + 0x1B9u, 0u);
    StoreU32(base, h + 0x18Cu, 0u);
    stub::Call(ctx, base, s.frame, 0x822F8640u, {h + 0x1BCu});
    Populate(ctx, base, s, h, comp27);
    Greet(ctx, base, s, h, comp27);
    RelayFront(ctx, base, s, h);
  }
  Write();
  Post("shopopen", g_host.keeper, static_cast<int32_t>(g_host.session));
  REXLOG_INFO("Refii co-op shop: the joiner's hero shops at keeper {:08X} ({:016X}); its list is stated (session {})",
              keeper, g_host.keeper, g_host.session);
}

/// One step for the joiner, as named by its intent. False when it has to wait.
bool Step(PPCContext& ctx, uint8_t* base, const std::string& action, int32_t value) {
  if (action == "shoprow") {
    g_host.asked_record = static_cast<uint32_t>(value);
    return true;
  }
  if (!HostReady(base)) {
    return false;
  }
  const stub::Scratch s = stub::Claim(ctx);
  uint32_t keeper = 0u, comp27 = 0u, h = 0u;
  if (!Parts(ctx, base, s, &keeper, &comp27, &h)) {
    End("the keeper is gone");
    return true;
  }
  const std::unordered_map<uint32_t, uint32_t> before = Held(base);
  const uint32_t qm = Qm(base);
  {
    Run run;
    Lend(ctx, base, s, h, keeper);
    bool transacted = false;
    if (action == "shopbuy" || action == "shopsell") {
      const uint32_t type = action == "shopbuy" ? 30u : 31u;
      const uint32_t asked = g_host.asked_record;
      g_host.asked_record = 0u;
      value = static_cast<int32_t>(RowTicket(ctx, base, s, h, comp27, type, static_cast<uint32_t>(value), asked));
    }
    if (action == "shoplookbuy" || action == "shoplooksell") {
      ShowRow(ctx, base, s, h, action == "shoplookbuy" ? 30u : 31u, static_cast<uint32_t>(value));
    } else if ((action == "shopbuy" || action == "shopsell") && value != 0) {
      transacted = true;
      const uint32_t type = action == "shopbuy" ? 30u : 31u;
      const uint32_t event = s.data + 0x380u;  // {name, type, id}
      stub::MakeString(ctx, base, s.frame, event, s.data + 0x3A0u, "");
      StoreU32(base, event + 4u, type);
      StoreU32(base, event + 8u, static_cast<uint32_t>(value));
      stub::Call(ctx, base, s.frame, 0x822651A0u, {qm + 80u, event});
      StoreU32(base, qm + 84u, type);
      StoreU32(base, qm + 88u, static_cast<uint32_t>(value));
      stub::Call(ctx, base, s.frame, type == 30u ? 0x826AFF48u : 0x826B0D10u, {h, event});
      stub::DropString(ctx, base, s.frame, event);
      REXLOG_INFO("Refii co-op shop: the joiner's {} of ticket {} ran here for hero two", type == 30u ? "buy" : "sale",
                  value);
    } else if (action == "shopbuy" || action == "shopsell") {
      // RowTicket said why.
    } else if (action == "shophead") {
      if (LoadU8(base, h + 0x1B9u) != 0u) {
        StoreU8(base, h + 0x1B9u, 0u);
        Populate(ctx, base, s, h, comp27);
        stub::Call(ctx, base, s.frame, 0x822F8640u, {h + 0x1BCu});
      } else {
        REXLOG_INFO("Refii co-op shop: the joiner's header asks for a new list; none is pending here (h+441 clear)");
      }
    } else if (action == "shopans") {
      const uint32_t box = refii::coop::AnswerBox(value);
      const uint32_t code = refii::coop::AnswerCode(value);
      const uint32_t helper = box == 17u ? 0x826AF430u : box == 19u ? 0x826AFB98u : box == 20u ? 0x826AF888u : 0u;
      if (helper != 0u) {
        stub::Call(ctx, base, s.frame, helper, {h, code, 0u});
        REXLOG_INFO("Refii co-op shop: the joiner's answer {} to question {} ran here", code, box);
        transacted = true;
      } else {
        REXLOG_INFO("Refii co-op shop: the joiner's answer {} names question {}, which has no helper here; nothing runs",
                    code, box);
      }
    } else if (action == "shopclose") {
      stub::Call(ctx, base, s.frame, 0x826AE090u, {h});
    } else {
      REXLOG_INFO("Refii co-op shop: the joiner's '{}' is no step of the shop here; dropped", action);
    }
    // After a transaction the guest states a new list is pending (h+441, set by the buy and sale trees); its
    // screen makes it at the next header highlight (Update 0x826AE520..0x826AE544: h+441 = 0, the population, the
    // transacted map cleared). The host's run has no cursor: it makes it now, the same three steps, so the
    // joiner's next list is already the one after its transaction (core/joiner_shop_statement.h InstallsAfterOwn). The front page's
    // wealth line moved with the gold: restated.
    if (transacted && LoadU8(base, h + 0x1B9u) != 0u) {
      StoreU8(base, h + 0x1B9u, 0u);
      Populate(ctx, base, s, h, comp27);
      stub::Call(ctx, base, s.frame, 0x822F8640u, {h + 0x1BCu});
    }
    if (transacted) {
      RelayFront(ctx, base, s, h);
    }
  }
  SendInventoryChange(before, Held(base));
  if (action == "shopclose") {
    End("the joiner closed it");
    return true;
  }
  g_host.flags = (g_host.flags & ~refii::coop::kShopRepopulate) |
                 (LoadU8(base, h + 0x1B9u) != 0u ? refii::coop::kShopRepopulate : 0u);
  Write();
  return true;
}

// ---- THE JOINER ----------------------------------------------------------------------------------------

struct Joiner {
  uint64_t pending_keeper = 0u;
  uint32_t pending_session = 0u;
  double pending_age = 0.0;
  bool active = false;
  bool seen_up = false;
  double since = 0.0;
  uint32_t session = 0u;
  uint64_t keeper = 0u;
  uint32_t installed = 0u;
  uint32_t asked_head = 0u;
  uint32_t modal_seen = 0u;
  uint32_t price_ticket = 0u;
  uint32_t missing = 0u;
  bool awaiting_list = false;   // this joiner's buy / sale / answer went: the next list is its own transaction's
  uint32_t front_seen = 0u;     // the host's front page serial posted here
  uint64_t look_want = 0u;      // (type << 32 | record) highlighted now
  double look_since = 0.0;      // when it was first highlighted (steady clock, seconds)
  uint64_t look_sent = 0u;      // the last one asked of the host
  uint32_t look_seen = 0u;      // the host's pane serial taken here
  std::unordered_map<uint64_t, std::vector<uint8_t>> panes;  // (type << 32 | record) -> the host's pane calls
  std::unordered_map<uint64_t, uint32_t> records;  // (type << 32 | ticket) -> record, every list this session
};

Joiner g_joiner;

std::optional<ShopStatement> Stated() {
  const int32_t size = refii_bridge_player_blob_get(1, kBlobKey, nullptr, 0u);
  if (size <= 0) {
    return std::nullopt;
  }
  std::string bytes(static_cast<size_t>(size), '\0');
  refii_bridge_player_blob_get(1, kBlobKey, reinterpret_cast<uint8_t*>(bytes.data()), static_cast<uint32_t>(size));
  return refii::coop::Decode(bytes);
}

/// The host's statement of this joiner's open shop; nothing when it states another or none.
std::optional<ShopStatement> Ours() {
  std::optional<ShopStatement> statement = Stated();
  if (!statement || statement->session != g_joiner.session || statement->keeper != g_joiner.keeper) {
    return std::nullopt;
  }
  return statement;
}

/// The stated rows into this machine's capture, where the guest's population would have spawned them.
void Install(const ShopStatement& statement) {
  uint32_t screen = 0u;
  refii_bridge_menu_spawn_begin_for(refii::vfs::StatedInGameScreen(&screen) ? screen : 0xFFFFFFFFu);
  for (const ShopRow& row : statement.rows) {
    refii_bridge_menu_spawn_row_labelled(row.name.c_str(), row.parent.c_str(), row.id, row.event_type, row.rating,
                                         row.label.c_str());
    refii_bridge_menu_spawn_row_item(row.icon.c_str(), row.item_flags, row.record, 0u, row.count, row.category);
    if (row.emphasis != 0) {
      refii_bridge_menu_row_emphasis_last(row.emphasis);
    }
  }
  for (const ShopRow& row : statement.rows) {
    if (row.id != 0u && row.record != 0u) {
      g_joiner.records[(static_cast<uint64_t>(static_cast<uint32_t>(row.event_type)) << 32) | row.id] = row.record;
    }
  }
  g_joiner.installed = statement.population;
  g_joiner.price_ticket = 0u;
  g_joiner.panes.clear();
  g_joiner.look_sent = 0u;
  // Godot's open list re-reads its sources only when the guest's hidden presentation changes or the bridge's UI
  // data generation moves (GameUi.cs "THE LIST FOLLOWS THE GAME'S DATA" → Engine_UiScreenMenu.Resource()); a stated
  // list touches neither (no population Lua runs here), so the list it showed stayed the first one. Moved here.
  refii_bridge_ui_data_changed();
  REXLOG_INFO("Refii co-op shop: the host's list is up here: {} row(s) (population {})", statement.rows.size(),
              statement.population);
}

/// The record of the row this joiner pushed as (type, ticket): any list the host stated this session.
uint32_t RowRecord(uint32_t type, uint32_t ticket) {
  const auto found = g_joiner.records.find((static_cast<uint64_t>(type) << 32) | ticket);
  return found != g_joiner.records.end() ? found->second : 0u;
}

/// The asking price the host stated for the row (type, ticket): its own, else that of the standing list's row of
/// the same record (the row may come from an earlier list than the one standing).
const std::string* PriceOf(const ShopStatement& statement, uint32_t type, uint32_t ticket) {
  uint32_t id = ticket;
  for (int pass = 0; pass < 2; ++pass) {
    for (const auto& [priced, text] : statement.prices) {
      if (priced == id) {
        return &text;
      }
    }
    const uint32_t record = RowRecord(type, ticket);
    id = 0u;
    for (const ShopRow& row : statement.rows) {
      if (record != 0u && row.record == record && row.event_type == static_cast<int32_t>(type)) {
        id = row.id;
        break;
      }
    }
    if (id == 0u || id == ticket) {
      return nullptr;
    }
  }
  return nullptr;
}

/// The host's front page for this session (RelayFront), posted to this machine's host once per serial.
void PostFront() {
  const int32_t size = refii_bridge_player_blob_get(1, kFrontKey, nullptr, 0u);
  if (size < 8) {
    return;
  }
  std::vector<uint8_t> blob(static_cast<size_t>(size));
  refii_bridge_player_blob_get(1, kFrontKey, blob.data(), static_cast<uint32_t>(blob.size()));
  uint32_t session = 0u, serial = 0u;
  for (int i = 0; i < 4; ++i) {
    session |= static_cast<uint32_t>(blob[i]) << (8 * i);
    serial |= static_cast<uint32_t>(blob[4 + i]) << (8 * i);
  }
  if (session != g_joiner.session || serial == g_joiner.front_seen) {
    return;
  }
  g_joiner.front_seen = serial;
  uint32_t posted = 0u;
  for (size_t at = 8u; at + 2u <= blob.size();) {
    const size_t length = static_cast<size_t>(blob[at]) | (static_cast<size_t>(blob[at + 1u]) << 8);
    at += 2u;
    if (length == 0u || at + length > blob.size()) {
      break;
    }
    refii_bridge_post_gui_native_call(blob.data() + at, length);
    at += length;
    ++posted;
  }
  REXLOG_INFO("Refii co-op shop: this joiner's front page takes the host's ({} statement(s), serial {})", posted,
              serial);
}

double Now() {
  return std::chrono::duration<double>(std::chrono::steady_clock::now().time_since_epoch()).count();
}

/// The host's pane calls posted to this machine's host, the row fill keyed by THIS joiner's id for the row (the
/// host's ticket names its own list's row; Godot keeps the row fill for the highlighted id).
uint32_t PostPane(const std::vector<uint8_t>& calls, uint32_t id) {
  uint32_t posted = 0u;
  for (size_t at = 0u; at + 2u <= calls.size();) {
    const size_t length = static_cast<size_t>(calls[at]) | (static_cast<size_t>(calls[at + 1u]) << 8);
    at += 2u;
    if (length == 0u || at + length > calls.size()) {
      break;
    }
    refii::vfs::GuiNativeCall call;
    if (refii::vfs::blob::ReadCall(calls.data() + at, length, call)) {
      if (call.name == "@PageFill" && call.arguments.size() >= 3u && call.arguments[0].text == "22" &&
          call.arguments[1].text == "row") {
        call.arguments[2].text = std::to_string(id);
      }
      refii::vfs::PostGuiNativeCall(std::move(call));
      ++posted;
    }
    at += length;
  }
  return posted;
}

/// The highlighted row's pane: asked of the host once the highlight has settled, posted when the host's answer for
/// it is here (or was, this list).
void DriveLook(uint32_t type, uint32_t id) {
  const uint32_t record = (type == 30u || type == 31u) ? RowRecord(type, id) : 0u;
  const uint64_t want = record != 0u ? (static_cast<uint64_t>(type) << 32) | record : 0u;
  const double now = Now();
  if (want != g_joiner.look_want) {
    g_joiner.look_want = want;
    g_joiner.look_since = now;
    if (want != 0u) {
      if (const auto cached = g_joiner.panes.find(want); cached != g_joiner.panes.end()) {
        PostPane(cached->second, id);
      }
    }
  }
  if (want == 0u) {
    return;
  }
  if (want != g_joiner.look_sent && now - g_joiner.look_since >= kLookSettle &&
      g_joiner.panes.find(want) == g_joiner.panes.end()) {
    g_joiner.look_sent = want;
    Post(type == 30u ? "shoplookbuy" : "shoplooksell", g_joiner.keeper, static_cast<int32_t>(record));
    REXLOG_INFO("Refii co-op shop: this joiner asks the host for the pane of its row (type {}, record 0x{:08X})", type,
                record);
  }
  const int32_t size = refii_bridge_player_blob_get(1, kLookKey, nullptr, 0u);
  if (size < 16) {
    return;
  }
  std::vector<uint8_t> blob(static_cast<size_t>(size));
  refii_bridge_player_blob_get(1, kLookKey, blob.data(), static_cast<uint32_t>(blob.size()));
  uint32_t head[4] = {};
  for (int w = 0; w < 4; ++w) {
    for (int i = 0; i < 4; ++i) {
      head[w] |= static_cast<uint32_t>(blob[4 * w + i]) << (8 * i);
    }
  }
  if (head[0] != g_joiner.session || head[1] == g_joiner.look_seen) {
    return;
  }
  g_joiner.look_seen = head[1];
  const uint64_t stated = (static_cast<uint64_t>(head[2]) << 32) | head[3];
  std::vector<uint8_t> calls(blob.begin() + 16, blob.end());
  const uint32_t posted = stated == want ? PostPane(calls, id) : 0u;
  g_joiner.panes[stated] = std::move(calls);
  REXLOG_INFO("Refii co-op shop: the host's pane for row (type {}, record 0x{:08X}) is here (serial {}); {}", head[2],
              head[3], head[1],
              stated == want ? std::to_string(posted) + " statement(s) posted" : std::string("kept (highlight moved on)"));
}

/// The closed shop's line on this joiner's HUD, made as the villager use makes it (0x827E1490..0x827E14F0):
/// `sub_821F0240(&tag, unk_8349BA30)`, `sub_8230E300(&tag)`, the copy again, `sub_8230B560(&message, 0, &tag,
/// 4000.0)` (`flt_82000C2C`, disc `flt_82000C24`, both 0x457A0000), `sub_8230C908(*(*(GUI+0x20)+0x58), 10,
/// &message, 1)`, `Render_FreeD3DObject(message+8)`.
void ShowClosed(PPCContext& ctx, uint8_t* base) {
  const stub::Scratch s = stub::Claim(ctx);
  const uint32_t tag = s.data;
  const uint32_t message = s.data + 0x20u;
  const uint32_t gui = LoadU32(base, Address(0x83496B08u));
  const uint32_t hud = Heap(gui) ? LoadU32(base, gui + 0x20u) : 0u;
  const uint32_t feed = Heap(hud) ? LoadU32(base, hud + 0x58u) : 0u;
  if (!Heap(feed)) {
    return;
  }
  StoreU32(base, tag, 0u);
  stub::Call(ctx, base, s.frame, 0x821F0240u, {tag, Address(0x8349BA30u)});
  stub::Call(ctx, base, s.frame, 0x8230E300u, {tag});
  StoreU32(base, tag, 0u);
  stub::Call(ctx, base, s.frame, 0x821F0240u, {tag, Address(0x8349BA30u)});
  PPCContext build = ctx;
  build.r1.u32 = s.frame;
  build.r3.u64 = message;
  build.r4.u64 = 0u;
  build.r5.u64 = tag;
  build.f1.f64 = 4000.0;
  refii::mods::CallGuest(build, base, Address(0x8230B560u));
  stub::Call(ctx, base, s.frame, 0x8230C908u, {feed, 10u, message, 1u});
  stub::Call(ctx, base, s.frame, 0x8221BD38u, {LoadU32(base, message + 8u)});  // Render_FreeD3DObject
  REXLOG_INFO("Refii co-op shop: the host's keeper is closed; the closed line is on this HUD");
}

void OpenOnJoiner(PPCContext& ctx, uint8_t* base, double seconds) {
  g_joiner.pending_age += seconds;
  if (g_joiner.pending_age > 10.0) {
    REXLOG_WARN("Refii co-op shop: the host's shop {:016X} never came with its list; not opened",
                g_joiner.pending_keeper);
    g_joiner.pending_keeper = 0u;
    return;
  }
  const std::optional<ShopStatement> statement = Stated();
  if (!statement || statement->session != g_joiner.pending_session || statement->keeper != g_joiner.pending_keeper) {
    return;
  }
  const uint32_t qm = Qm(base);
  if (ShopScreenUp(base) || !Heap(qm) || LoadU32(base, qm + 40u) != 1u) {
    return;  // a shop or a box is up here: wait
  }
  const uint32_t keeper = refii_household_resolve_uid(&ctx, base, g_joiner.pending_keeper);
  if (keeper == 0u || refii_mod_entity_alive(base, keeper) == 0) {
    return;
  }
  Joiner fresh;
  fresh.active = true;
  fresh.session = g_joiner.pending_session;
  fresh.keeper = g_joiner.pending_keeper;
  g_joiner = fresh;
  PPCContext open = refii::mods::Frame(ctx, 0x200u);
  open.r3.u64 = LoadU32(base, qm + 0x30u);
  open.r4.u64 = keeper;
  rex_lhGuiShopScreen_Open(open, base);
  REXLOG_INFO("Refii co-op shop: the host's shop opens here on keeper {:08X} (session {})", keeper, g_joiner.session);
}

void DriveJoiner(PPCContext& ctx, uint8_t* base, double seconds) {
  if (g_joiner.pending_keeper != 0u) {
    OpenOnJoiner(ctx, base, seconds);
  }
  if (!g_joiner.active) {
    return;
  }
  if (ShopScreenUp(base)) {
    g_joiner.seen_up = true;
    return;
  }
  if (g_joiner.seen_up) {
    Post("shopclose", g_joiner.keeper, static_cast<int32_t>(g_joiner.session));
    REXLOG_INFO("Refii co-op shop: this joiner's shop closed; the host's run closes");
  } else if ((g_joiner.since += seconds) < 5.0) {
    return;
  }
  g_joiner = Joiner{};
}

}  // namespace

namespace refii::vfs {

bool JoinerShopRunning() { return t_running > 0; }

bool JoinerShopLookRow(uint32_t* type, uint32_t* id, std::string* name) {
  if (!t_look.on) {
    return false;
  }
  *type = t_look.type;
  *id = t_look.id;
  *name = t_look.name;
  return true;
}

uint32_t JoinerShopLastSpawnOrdinal() {
  return g_host.rows.empty() ? 0u : static_cast<uint32_t>(g_host.rows.size() - 1u);
}

void JoinerShopModal(uint32_t kind, uint32_t id, const std::string& text) {
  g_host.modals.push_back(ShopModal{++g_host.modal_serial, kind, id, refii::coop::Clean(text)});
  if (g_host.modals.size() > kModalsKept) {
    g_host.modals.erase(g_host.modals.begin());
  }
  REXLOG_INFO("Refii co-op shop: the run opened box {} (kind {}) for the joiner: '{}'", id, kind, text);
}

void JoinerShopReplay(bool on) {
  t_replay = on;
  if (on) {
    t_replay_keeper = 0u;
    t_replay_closed = false;
  }
}

bool JoinerShopOpensOnHost(uint32_t keeper) {
  if (!t_replay) {
    return false;
  }
  t_replay_keeper = keeper;
  return true;
}

void JoinerShopAfterUse(PPCContext& ctx, uint8_t* base, uint32_t keeper_entity) {
  if (t_replay_closed) {
    t_replay_closed = false;
    Post("shopclosed", Uid(base, keeper_entity), 0);
    REXLOG_INFO("Refii co-op shop: the joiner's keeper {:08X} is closed now; its line goes to the joiner",
                keeper_entity);
  }
  const uint32_t keeper = t_replay_keeper;
  t_replay_keeper = 0u;
  if (keeper != 0u) {
    Begin(ctx, base, keeper);
  }
}

bool JoinerShopIntent(PPCContext& ctx, uint8_t* base, std::string_view action, uint64_t uid, int32_t value) {
  if (action.rfind("shop", 0) != 0) {
    return false;
  }
  if (action == "shopopen") {
    if (OnlineJoiner()) {
      g_joiner.pending_keeper = uid;
      g_joiner.pending_session = static_cast<uint32_t>(value);
      g_joiner.pending_age = 0.0;
    }
    return true;
  }
  if (action == "shopclosed") {
    if (OnlineJoiner()) {
      ShowClosed(ctx, base);
    }
    return true;
  }
  if (refii_mod_player_here(1) == 0 || !g_host.open || uid != g_host.keeper) {
    REXLOG_INFO("Refii co-op shop: '{}' for {:016X} names no open shop here; dropped", action, uid);
    return true;
  }
  const std::string step(action);
  // A newer highlight replaces a pane still waiting: only the row the joiner is on now is worth running.
  if (step.rfind("shoplook", 0) == 0) {
    for (auto it = g_host.waiting.begin(); it != g_host.waiting.end();) {
      it = it->first.rfind("shoplook", 0) == 0 ? g_host.waiting.erase(it) : it + 1;
    }
  }
  if (!g_host.waiting.empty() || !Step(ctx, base, step, value)) {
    g_host.waiting.emplace_back(step, value);
    REXLOG_INFO("Refii co-op shop: the joiner's '{}' waits: this machine's own shop or box is up", step);
  }
  return true;
}

void DriveJoinerShop(PPCContext& ctx, uint8_t* base, float seconds) {
  if (OnlineJoiner()) {
    DriveJoiner(ctx, base, seconds);
    return;
  }
  g_joiner = Joiner{};
  if (!g_host.open) {
    return;
  }
  if (!OnlineHost(base)) {
    End("the joiner's hero is gone");
    return;
  }
  while (!g_host.waiting.empty() && g_host.open) {
    const auto [step, value] = g_host.waiting.front();
    if (!Step(ctx, base, step, value)) {
      break;
    }
    if (!g_host.waiting.empty()) {
      g_host.waiting.erase(g_host.waiting.begin());
    }
  }
}

bool JoinerShopStated() { return g_joiner.active; }

bool JoinerShopTitles() {
  const std::optional<ShopStatement> statement = Ours();
  return statement && (statement->flags & refii::coop::kShopTitles) != 0u;
}

void JoinerShopEnter() {
  g_joiner.seen_up = true;
  if (const std::optional<ShopStatement> statement = Ours()) {
    Install(*statement);
  }
}

void JoinerShopUpdate(PPCContext& ctx, uint8_t* base, uint32_t type, uint32_t id) {
  const std::optional<ShopStatement> statement = Ours();
  const uint32_t qm = Qm(base);
  if (!statement || !Heap(qm)) {
    // The host's session is over (the keeper gone, the host's own shop up first): the screen goes as its
    // ReturnToGame does, `lhGuiManager_ExitScreen(QM, 1)` (0x826AE754).
    if (++g_joiner.missing == 90u && Heap(qm)) {
      const stub::Scratch s = Below(ctx);
      stub::Call(ctx, base, s.frame, 0x8236ED98u, {qm, 1u});
      REXLOG_INFO("Refii co-op shop: the host states this shop no more; the screen closes");
    }
    return;
  }
  g_joiner.missing = 0u;
  const uint32_t mode = LoadU32(base, qm + 40u);
  if (statement->population != g_joiner.installed &&
      (g_joiner.installed == 0u || refii::coop::InstallsPopulation(true, type, mode) ||
       refii::coop::InstallsAfterOwn(g_joiner.awaiting_list, mode))) {
    if (g_joiner.awaiting_list && type != 301u) {
      REXLOG_INFO("Refii co-op shop: the list after this joiner's transaction goes in (no box up)");
    }
    g_joiner.awaiting_list = false;
    Install(*statement);
  }
  PostFront();
  if ((statement->flags & refii::coop::kShopRepopulate) != 0u && type == 301u && mode == 1u &&
      g_joiner.asked_head != statement->population) {
    g_joiner.asked_head = statement->population;
    Post("shophead", g_joiner.keeper, 0);
  }
  if (id != g_joiner.price_ticket && (type == 30u || type == 31u)) {
    g_joiner.price_ticket = id;
    if (const std::string* text = PriceOf(*statement, type, id)) {
      refii::vfs::StatePageFillTexts(22u, "row", id, {{kPricePath, *text}});
    } else {
      REXLOG_INFO("Refii co-op shop: the host stated no price for this joiner's row (type {}, ticket {})", type, id);
    }
  }
  // After the price-only fill: Godot keeps one row fill per screen, and the pane (which holds the price too)
  // must be the one that stays.
  DriveLook(type, id);
  if (mode != 1u) {
    return;
  }
  for (const ShopModal& modal : statement->modals) {
    if (modal.serial <= g_joiner.modal_seen) {
      continue;
    }
    g_joiner.modal_seen = modal.serial;
    const stub::Scratch s = Below(ctx);
    const uint32_t wide = s.data;
    StoreU32(base, wide, 0u);
    PPCContext frame = ctx;
    frame.r1.u32 = s.frame;
    if (!refii::vfs::WideFromText(frame, base, wide, modal.text)) {
      continue;
    }
    PPCContext open = frame;
    open.r3.u64 = qm;
    open.r4.u64 = wide;
    open.r5.u64 = modal.kind;
    open.r6.u64 = modal.id;
    open.r7.u64 = 0u;
    open.r8.u64 = 0u;
    open.r9.u64 = 0u;
    rex_lhGuiManager_OpenModal(open, base);  // the opener releases the text
    REXLOG_INFO("Refii co-op shop: the host's box {} (kind {}) is up here", modal.id, modal.kind);
    break;  // one box at a time, as the router takes them
  }
}

void JoinerShopEvent(PPCContext& ctx, uint8_t* base, uint32_t type, uint32_t id) {
  (void)ctx;
  if (type == 30u || type == 31u) {
    const uint32_t record = RowRecord(type, id);
    if (record == 0u) {
      REXLOG_INFO("Refii co-op shop: this joiner's row (type {}, ticket {}) is in no list the host stated", type, id);
    }
    Post("shoprow", g_joiner.keeper, static_cast<int32_t>(record));
    Post(type == 30u ? "shopbuy" : "shopsell", g_joiner.keeper, static_cast<int32_t>(id));
    g_joiner.awaiting_list = true;
    REXLOG_INFO("Refii co-op shop: this joiner's {} of ticket {} (record 0x{:08X}) goes to the host",
                type == 30u ? "buy" : "sale", id, record);
  } else if (type == 301u) {
    const std::optional<ShopStatement> statement = Ours();
    const uint32_t qm = Qm(base);
    if (statement && Heap(qm)) {
      const uint32_t mode = LoadU32(base, qm + 40u);
      if (refii::coop::InstallsPopulation(statement->population != g_joiner.installed, 301u, mode)) {
        Install(*statement);
      }
      if ((statement->flags & refii::coop::kShopRepopulate) != 0u && mode == 1u &&
          g_joiner.asked_head != statement->population) {
        g_joiner.asked_head = statement->population;
        Post("shophead", g_joiner.keeper, 0);
      }
    }
  }
}

void JoinerShopAnswer(PPCContext& ctx, uint8_t* base, uint32_t box, uint32_t code) {
  Post("shopans", g_joiner.keeper, refii::coop::AnswerValue(box, code));
  g_joiner.awaiting_list = true;
  const int closer = refii::coop::QuestionCloser(box, code);
  const uint32_t qm = Qm(base);
  if (closer != 0 && Heap(qm)) {
    const stub::Scratch s = Below(ctx);
    stub::Call(ctx, base, s.frame, closer == 1 ? 0x8236FEC8u : 0x8236FE38u, {qm, 3u});
  }
  REXLOG_INFO("Refii co-op shop: this joiner's answer {} to question {} goes to the host", code, box);
}

}  // namespace refii::vfs

// The shopkeeper's lifted refusal reads this for the host's replay of a joiner's villager use.
extern "C" int RefiiCoopJoinerShopReplay(void) { return t_replay ? 1 : 0; }

// Before `bl sub_8230C908` in the villager use's closed-shop arm (TU1 0x827E14E8 / disc 0x827A8490): in the host's
// replay of the joiner's use the line belongs on the joiner's HUD (ShowClosed); true skips the call here (to
// 0x827E14EC / 0x827A8494, the message's own free, which follows either way).
bool RefiiCoopJoinerShopClosed() {
  if (!t_replay) {
    return false;
  }
  t_replay_closed = true;
  return true;
}

// THE FRONT PAGE'S WRITES BY NAME, taken while the host runs the fill for the online joiner (RelayFront): the text
// `sub_8246B888(pres r3, &name r4, &text r5)` (TU1; disc `sub_828374B8`, 24 instructions, identical: the name copied
// `bl sub_821F0240` 0x8246B8B0, found `bl sub_82426798` 0x8246B8B8, `bl lhGuiElement_SetText` 0x8246B8CC with r5), the
// slide `sub_82426D18(&name r3, pres r4, slide r5)` (disc `sub_823F91B0`; `bl lhGuiElement_GoToSlideAndRestart`
// 0x82426D60) and the shown flag `sub_82426328(&name r3, pres r4, on r5)` (disc `sub_823F87C0`; `bl sub_82426260`
// 0x82426370). None of the three frees an argument. Outside a relay each runs as shipped.
REX_EXTERN(rex_lhGuiElement_TextByName) {
  REX_FUNC_PROLOGUE();
  if (refii::vfs::PageFillRelaying()) {  // the item pane's relay (ShowRow): noted as the fill's, not run
    refii::vfs::NotePageFillNamed("text", refii::vfs::ReadGuestLhString(base, ctx.r4.u32),
                                  refii::vfs::WideTextUtf8(base, ctx.r5.u32));
    return;
  }
  if (!t_relay) {
    __imp__rex_lhGuiElement_TextByName(ctx, base);
    return;
  }
  t_named.push_back(NamedWrite{"text", refii::vfs::ReadGuestLhString(base, ctx.r4.u32),
                               refii::vfs::WideTextUtf8(base, ctx.r5.u32)});
}

REX_EXTERN(rex_lhGuiElement_SlideByName) {
  REX_FUNC_PROLOGUE();
  if (refii::vfs::PageFillRelaying()) {  // GoToSlideAndRestart would reach the presentation (vf+0x24, 0x82426CE0)
    refii::vfs::NotePageFillNamed("slide", refii::vfs::ReadGuestLhString(base, ctx.r3.u32),
                                  std::to_string(static_cast<int32_t>(ctx.r5.u32)));
    return;
  }
  if (!t_relay) {
    __imp__rex_lhGuiElement_SlideByName(ctx, base);
    return;
  }
  t_named.push_back(NamedWrite{"slide", refii::vfs::ReadGuestLhString(base, ctx.r3.u32),
                               std::to_string(static_cast<int32_t>(ctx.r5.u32))});
}

REX_EXTERN(rex_lhGuiElement_ShownByName) {
  REX_FUNC_PROLOGUE();
  if (refii::vfs::PageFillRelaying()) {
    refii::vfs::NotePageFillNamed("shown", refii::vfs::ReadGuestLhString(base, ctx.r3.u32),
                                  (ctx.r5.u32 & 0xFFu) != 0u ? "1" : "0");
    return;
  }
  if (!t_relay) {
    __imp__rex_lhGuiElement_ShownByName(ctx, base);
    return;
  }
  t_named.push_back(NamedWrite{"shown", refii::vfs::ReadGuestLhString(base, ctx.r3.u32),
                               (ctx.r5.u32 & 0xFFu) != 0u ? "1" : "0"});
}
