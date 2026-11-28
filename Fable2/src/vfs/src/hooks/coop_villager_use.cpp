// refii — VILLAGERS SERVE AN ONLINE JOINER'S HERO (Fable 2 co-op): the prompt and the use.
//
// A network client builds no villager (26) and no shopkeeper (27): the client switch of the entity build
// loop sends both kinds to its default arm (`jpt_8238DF3C` cases 25 and 26 → `def_8238DF3C` 0x8238E438, TU1;
// `jpt_8239370C` the same, disc; coop_client_components_hooks.cpp). Both halves of a villager's use read 26:
//
//   THE PROMPT. The targeted thing's `Interact` world icon (type 34) is CECTargeted's (comp 23, kept on a
//   client): `sub_82175310` (disc `sub_821750D8`, the same 386 instructions), called by CECTargeted_vf14 with
//   r4 = 1 (TU1 0x8219FB38 / 0x8219FB50, disc 0x8219FBE0 / 0x8219FBF8) and by lhECTargeted_SetTargetedByPlayer,
//   r3 the component: `lbz r11,0x27(r29)` / `beq` → nothing unless this machine's player targets it
//   (0x82175320; disc 0x821750E8), `lbz r11,0x28(r29)` the icon already up (0x82175334). Its text is the
//   villager's — `bl sub_827E0C08` (0x82175468, the villager is usable: comp26 +256 and its job +156 one of 26,
//   14 coach driver, 29 ship captain, 31, 41, or a shopkeeper at its post) then `bl sub_827DA778` (0x82175480,
//   the shop record's text, else the job's) — and with no comp 26 it asks the owned-entity path only, so a
//   joiner's hero saw no prompt at a shopkeeper or a coach driver. The request: `lhWorldIcons_Request(
//   *(*(0x83496B08)+0x20)+0x1C, &"Interact", *(r29+4), &text, 34)` (0x82175870..0x821758A4), `stb r8,0x28(r29)`
//   (0x821758B4); its removal `lhWorldIcons_Remove(manager, *(r29+4), &"Interact")` (0x821758D0..0x821758FC).
//
//   THE USE. The interact action (CAbilityActionPlayerInteract vf2, TU1 0x82976588 / disc 0x829A5B20) uses a
//   villager only through its comp 26: `lwz r11,0x24(r26)` / `extrwi r10,r11,1,5` / `beq loc_82977064` (r26 the
//   target, TU1 0x82976F64..0x82976F74; disc 0x829A64F8..0x829A6508 → 0x829A65FC), then `bl sub_827E0C08` and
//   `bl CECVillager_OnUse(comp26, r22 the user)` (0x82977078 / 0x82977090; disc 0x829A6610 / 0x829A6628) and
//   `mr r27,r23` — no generic use message after it (`clrlwi r11,r27,24` / `beq` 0x82977098 skips the message
//   101, `bl sub_82279B80` 0x829770E8). The 360's client asks the host through net 0x9E, whose receive runs
//   the same action for its copy of player two — and on the host that copy has no player component (kind 33,
//   entity+0x28 bit 1: Inventory.AddItem's own test said so in every run, coop_hero_hooks.cpp
//   RefiiCoopMoneyItemForPlayerTwo), so the action stops at its first player test (`lwz r11,0x28(r22)` /
//   `extrwi r10,r11,1,30` / `beq loc_82976B78` → `beq loc_82977904`, 0x82976A70..0x82976B84) before any
//   villager: the joiner's A at a coach driver or a stall did nothing anywhere.
//
// SO, FOR AN ONLINE JOINER WHO BROUGHT A HERO (the plain henchman keeps every 360 rule):
//   - the host states the joiner's targeted villager's prompt — the guest's own `sub_827E0C08` and
//     `sub_827DA778` on the host's copy, for the target the joiner states (mod_events.cpp `usetarget`) — as
//     a session text of player one's store, kPromptKey = "<uid>|<text>" ("" for none), the text losslessly
//     as UTF-8 (WideTextUtf8);
//   - the joiner's prompt routine, for a targeted thing with no comp 26 and that statement, makes the same
//     request with the stated text (rex_lhECTargeted_InteractPrompt), and drops it when the statement goes;
//   - the joiner's interact action, at the no-villager branch (TU1 0x82977064 / disc 0x829A65FC), names the
//     villager to the host (`villuse:<uid>:0`) and takes the villager path's own `r27 = 0`
//     (RefiiCoopJoinerUsesVillager); the host runs `CECVillager_OnUse(comp26, hero two)` — what the action
//     would have run — when its own `sub_827E0C08` agrees. What the villager says goes back by the guest's
//     own say packet (`sub_825D7060` → `SendSayLinePacket` on the host, 0x825D7198). A travel agent (job 14
//     coach driver, 29 ship captain) gives every player two its shipped reject line (`clrlwi r11,r27,24`
//     0x827E1540 → `sub_825D7060`): only player one fast-travels (user decision 2026-11-28). A shopkeeper
//     serves hero two in this replay (the henchman's refusal, `clrlwi r10,r27,24` / `beq` 0x827E12F0, is
//     lifted for it): the shop runs here, where the keeper's 27 and its stock's 124 are, and the joiner's screen
//     shows it (coop_shop_for_joiner.cpp).

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <unordered_map>
#include <vector>

#include <rex/logging.h>

#include "core/guest_build.h"
#include "core/payload_bridge_fable2.h"
#include "core/refii_mod.h"
#include "core/refii_mod_requests.h"
#include "hooks/ppc_externs.h"
#include "vfs_hooks_private.h"

extern "C" int RefiiCoopGetOwnHero(void);
extern "C" uint8_t* RefiiGuestBase(void);
extern "C" uint32_t refii_household_resolve_uid(PPCContext* ctx, uint8_t* base, uint64_t uid);
/// The use target the online joiner last stated for its hero (mod_events.cpp `usetarget`): its UID, 1 none,
/// 0 nothing stated.
extern "C" uint64_t refii_household_joiner_target_uid(void);
/// The UID the other machine knows an entity by (mod_events.cpp SharedUid: a replica's net id on a client).
extern "C" uint64_t refii_household_shared_uid(uint8_t* base, uint32_t entity);
extern "C" int32_t refii_mod_opinion_enter(PPCContext* ctx, uint8_t* base, uint32_t villager, uint32_t hero);
extern "C" void refii_mod_opinion_leave(void);

PPC_EXTERN_FUNC(__imp__rex_lhECTargeted_InteractPrompt);
PPC_EXTERN_FUNC(rex_lhWorldIcons_Request);
PPC_EXTERN_FUNC(rex_lhWorldIcons_Remove);
PPC_EXTERN_FUNC(rex_lhWideString_Dtor);
PPC_EXTERN_FUNC(rex_lhCharString_CtorFromCStr);
PPC_EXTERN_FUNC(rex_lhCharString_Dtor);
PPC_EXTERN_FUNC(rex_CECVillager_OnUse);

namespace {

using refii::mods::CallGuest;
using refii::mods::Frame;
using refii::mods::LoadU32;
using refii::mods::LoadU8;

constexpr char kPromptKey[] = "refii.vprompt";
constexpr uint32_t kVillager = 26u;
constexpr uint32_t kInteractIcon = 34u;
constexpr double kStateSeconds = 0.25;

bool Heap(uint32_t at) { return at >= 0x10000u && at < 0xE0000000u; }

bool HeroJoinerSession(uint8_t* base) { return RefiiCoopGetOwnHero() != 0 && refii_mod_player_hero(base, 2) != 0u; }

bool OnlineJoiner() { return refii_mod_player_here(1) == 0 && refii_mod_player_here(2) != 0; }

/// The villager is usable now: `sub_827E0C08(comp26)` (disc `sub_827A7BB0`).
bool Usable(const PPCContext& ctx, uint8_t* base, uint32_t comp26) {
  PPCContext work = Frame(ctx, 0x200u);
  work.r3.u64 = comp26;
  return CallGuest(work, base, refii::guest::Address(0x827E0C08u)) && (work.r3.u32 & 0xFFu) != 0u;
}

/// The prompt's text: `sub_827DA778(out, comp26)` (disc `sub_827A1720`), a wide string, as UTF-8; released.
std::string PromptText(const PPCContext& ctx, uint8_t* base, uint32_t comp26) {
  PPCContext frame = Frame(ctx, 0x300u);
  const uint32_t out = (frame.r1.u32 + 0x100u) & ~0xFu;
  refii::mods::StoreU32(base, out, 0u);
  PPCContext work = frame;
  work.r3.u64 = out;
  work.r4.u64 = comp26;
  if (!CallGuest(work, base, refii::guest::Address(0x827DA778u))) {
    return std::string();
  }
  std::string text = refii::vfs::WideTextUtf8(base, out);
  PPCContext drop = frame;
  drop.r3.u64 = out;
  rex_lhWideString_Dtor(drop, base);
  return text;
}

/// The statement for `entity` on this machine (player one's session text): the stated text when it names
/// this entity, else empty.
std::string StatedPrompt(uint8_t* base, uint32_t entity) {
  char text[REFII_MOD_PLAYER_TEXT_MAX + 1];
  const int32_t length = refii_bridge_player_get_text(1, kPromptKey, sizeof(kPromptKey) - 1u, text, sizeof(text));
  if (length <= 17 || text[16] != '|') {
    return std::string();
  }
  text[16] = 0;
  const uint64_t uid = std::strtoull(text, nullptr, 16);
  return uid != 0u && uid == refii_household_shared_uid(base, entity) ? std::string(text + 17) : std::string();
}

/// The world-icon manager `*(*(*(0x83496B08)+0x20)+0x1C)`, as the prompt routine reads it (0x82175884..0x821758A0).
uint32_t IconManager(uint8_t* base) {
  const uint32_t gui = LoadU32(base, refii::guest::Address(0x83496B08u));
  const uint32_t holder = Heap(gui) ? LoadU32(base, gui + 0x20u) : 0u;
  return Heap(holder) ? LoadU32(base, holder + 0x1Cu) : 0u;
}

/// The "Interact" key as the routine makes it (`lhCharString_CtorFromCStr(key, "Interact", -1)`).
void MakeKey(const PPCContext& frame, uint8_t* base, uint32_t key, uint32_t chars) {
  static constexpr char kInteract[] = "Interact";
  std::memcpy(base + chars, kInteract, sizeof(kInteract));
  PPCContext make = frame;
  make.r3.u64 = key;
  make.r4.u64 = chars;
  make.r5.s64 = -1;
  rex_lhCharString_CtorFromCStr(make, base);
}

void DropKey(const PPCContext& frame, uint8_t* base, uint32_t key) {
  PPCContext drop = frame;
  drop.r3.u64 = key;
  rex_lhCharString_Dtor(drop, base);
}

/// The request the routine makes, with the stated text. True when it was made.
bool RaisePrompt(const PPCContext& ctx, uint8_t* base, uint32_t entity, const std::string& text) {
  const uint32_t manager = IconManager(base);
  if (!Heap(manager)) {
    return false;
  }
  PPCContext frame = Frame(ctx, 0x500u);
  const uint32_t area = (frame.r1.u32 + 0x100u) & ~0xFu;
  const uint32_t key = area;
  const uint32_t wide = area + 0x10u;
  const uint32_t chars = area + 0x20u;
  refii::mods::StoreU32(base, wide, 0u);
  if (!refii::vfs::WideFromText(frame, base, wide, text)) {
    return false;
  }
  MakeKey(frame, base, key, chars);
  PPCContext request = frame;
  request.r3.u64 = manager;
  request.r4.u64 = key;
  request.r5.u64 = entity;
  request.r6.u64 = wide;
  request.r7.u64 = kInteractIcon;
  rex_lhWorldIcons_Request(request, base);
  DropKey(frame, base, key);
  PPCContext drop = frame;
  drop.r3.u64 = wide;
  rex_lhWideString_Dtor(drop, base);
  return true;
}

void DropPrompt(const PPCContext& ctx, uint8_t* base, uint32_t entity) {
  const uint32_t manager = IconManager(base);
  if (!Heap(manager)) {
    return;
  }
  PPCContext frame = Frame(ctx, 0x300u);
  const uint32_t key = (frame.r1.u32 + 0x100u) & ~0xFu;
  MakeKey(frame, base, key, key + 0x20u);
  PPCContext remove = frame;
  remove.r3.u64 = manager;
  remove.r4.u64 = entity;
  remove.r5.u64 = key;
  rex_lhWorldIcons_Remove(remove, base);
  DropKey(frame, base, key);
}

/// The prompts this joiner raised from a statement: the CECTargeted component → the text it shows.
std::unordered_map<uint32_t, std::string> g_raised;

struct Host {
  double state_in = 0.0;
  std::string stated;  // what kPromptKey holds as this machine last wrote it
  uint64_t said_uid = 0u;
  uint64_t seen_uid = 0u;
  std::string said_text;
};
Host g_host;

void StatePrompt(uint8_t* base, const std::string& statement) {
  char held[REFII_MOD_PLAYER_TEXT_MAX + 1];
  const int32_t length = refii_bridge_player_get_text(1, kPromptKey, sizeof(kPromptKey) - 1u, held, sizeof(held));
  const std::string now = length > 0 ? std::string(held, static_cast<size_t>(length)) : std::string();
  if (now == statement && g_host.stated == statement) {
    return;
  }
  // The store drops session texts on a new hero or a re-join (core/player_state.h), so a text that is not
  // what this machine wrote is written again.
  refii_bridge_player_write_text(1, kPromptKey, sizeof(kPromptKey) - 1u, statement.c_str(), statement.size(), 0);
  g_host.stated = statement;
}

}  // namespace

namespace refii::vfs {

void DriveVillagerUse(PPCContext& ctx, uint8_t* base, float seconds) {
  const int32_t mode = refii_mod_coop_mode(base);
  if (mode != 3) {
    g_raised.clear();  // only an online joiner raises prompts from a statement
  }
  if (mode != 2 || !HeroJoinerSession(base)) {
    if (mode == 2 && !g_host.stated.empty()) {
      StatePrompt(base, std::string());
    }
    if (mode != 2) {
      g_host = Host{};
    }
    return;
  }
  g_host.state_in -= seconds;
  if (g_host.state_in > 0.0) {
    return;
  }
  g_host.state_in = kStateSeconds;
  const uint64_t uid = refii_household_joiner_target_uid();
  const uint32_t entity = uid > 1u ? refii_household_resolve_uid(&ctx, base, uid) : 0u;
  const uint32_t villager = entity != 0u ? refii_mod_component(base, entity, kVillager) : 0u;
  if (uid != g_host.seen_uid) {
    g_host.seen_uid = uid;
    if (uid > 1u && villager == 0u) {
      REXLOG_INFO("Refii co-op villager: the joiner's hero targets {:016X}: {}", static_cast<unsigned long long>(uid),
                  entity == 0u ? "nothing here by that UID" : "no villager component here");
    }
  }
  std::string text;
  // The host's copy's own gate first: the routine prompts only for a CECTargeted whose +0x27 is set (vf11's job
  // rule) and whose villager is usable now.
  const uint32_t targeted_here = entity != 0u ? refii_mod_component(base, entity, 23u) : 0u;
  if (villager != 0u && targeted_here != 0u && LoadU8(base, targeted_here + 0x27u) != 0u && Usable(ctx, base, villager)) {
    text = PromptText(ctx, base, villager);
    if (text.size() > REFII_MOD_PLAYER_TEXT_MAX - 17u) {
      text.resize(REFII_MOD_PLAYER_TEXT_MAX - 17u);  // WideFromText ends at a sequence the cut left short
    }
  }
  char head[24];
  std::snprintf(head, sizeof(head), "%016llX|", static_cast<unsigned long long>(uid));
  StatePrompt(base, text.empty() ? std::string() : std::string(head) + text);
  if (villager != 0u && (uid != g_host.said_uid || text != g_host.said_text)) {
    g_host.said_uid = uid;
    g_host.said_text = text;
    REXLOG_INFO("Refii co-op villager: the joiner's hero targets villager {:08X} (job {}): {}", entity,
                LoadU32(base, villager + 156u), text.empty() ? std::string("no prompt (not usable now)")
                                                             : "prompt '" + text + "' stated");
  }
}

void VillagerUseForJoiner(PPCContext& ctx, uint8_t* base, uint64_t uid) {
  const uint32_t two = refii_mod_player_hero(base, 2);
  if (refii_mod_player_here(1) == 0 || two == 0u || RefiiCoopGetOwnHero() == 0) {
    return;
  }
  const uint32_t entity = refii_household_resolve_uid(&ctx, base, uid);
  const uint32_t villager = entity != 0u ? refii_mod_component(base, entity, kVillager) : 0u;
  if (villager == 0u) {
    REXLOG_WARN("Refii co-op villager: the joiner's use of {:016X} names no villager here", uid);
    return;
  }
  if (!Usable(ctx, base, villager)) {
    REXLOG_INFO("Refii co-op villager: the joiner's use of villager {:08X} (job {}): not usable here now", entity,
                LoadU32(base, villager + 156u));
    return;
  }
  REXLOG_INFO("Refii co-op villager: the joiner's hero uses villager {:08X} (job {}) here for hero two", entity,
              LoadU32(base, villager + 156u));
  PPCContext work = Frame(ctx, 0x400u);
  work.r3.u64 = villager;
  work.r4.u64 = two;
  // A shopkeeper's path runs for hero two in this replay: its opener and its closed line are the joiner's
  // (coop_shop_for_joiner.cpp).
  JoinerShopReplay(true);
  rex_CECVillager_OnUse(work, base);
  JoinerShopReplay(false);
  JoinerShopAfterUse(ctx, base, entity);
}

// THE VILLAGER CARD ON AN ONLINE JOINER. The card's opener `sub_826CCC88(h, entity)` fills its opinion block in
// `sub_826CD190(h)` (disc `sub_82583EB8`, the same 132 instructions), which reads only the target `*(h+0x1C)` and
// does nothing unless the target has an OpinionReaction (comp 47: `(e[10] >> 15) & 1`, then the description
// `sub_826CD3A0` — TEXT_QUERY_INTERFACE_DOG for a dog, the personality words otherwise — likes and dislikes
// `sub_826CD5C8` / `sub_826CDB88`, the "Opinion of you" meters `sub_826CD720` and the three axes, every one a
// registry write: lhGuiRegistry_SetText / SetToggle / SetMeter). A client builds no comp 47 (the client switch's
// case 46 → its default arm), so on the joiner the block is never written and the presentation keeps its
// file's own text — the "A surly, bawdy artistic Blacksmith…" and "Blueberry pies" a dog's card showed.
// The joiner asks the host for its card's target (`cardask:<uid>:0`); the host runs that same fill for the
// target, inside player two's opinion scope (refii_mod_opinion_enter: the meters are hero two's opinion),
// with the GUI call feed captured (refii_bridge_gui_capture_begin) instead of reaching its own screens, and
// states the captured statements as player one's blob kCardKey = { uid (8 bytes, little-endian), the calls }.
// The joiner's card, while it stands (its Update), posts them to its own host once per opening.
namespace {
constexpr char kCardKey[] = "refii.card";
constexpr uint32_t kOpinion = 47u;
struct JoinerCard {
  uint32_t card = 0u;     // the open card's handler, 0 when none
  uint64_t asked = 0u;    // the target asked for this opening
  bool applied = false;   // its statements posted this opening
};
JoinerCard g_card;
}  // namespace

void CardForJoiner(PPCContext& ctx, uint8_t* base, uint64_t uid) {
  const uint32_t two = refii_mod_player_hero(base, 2);
  if (refii_mod_player_here(1) == 0 || two == 0u || RefiiCoopGetOwnHero() == 0) {
    return;
  }
  const uint32_t entity = refii_household_resolve_uid(&ctx, base, uid);
  std::vector<uint8_t> blob(8u);
  for (int i = 0; i < 8; ++i) {
    blob[i] = static_cast<uint8_t>(uid >> (8 * i));
  }
  if (entity != 0u && refii_mod_component(base, entity, kOpinion) != 0u) {
    PPCContext frame = Frame(ctx, 0x300u);
    const uint32_t fake = (frame.r1.u32 + 0x100u) & ~0xFu;  // a card's fields as the fill reads them
    std::memset(base + fake, 0, 0x40u);
    refii::mods::StoreU32(base, fake + 0x1Cu, entity);
    const int32_t scoped = refii_mod_opinion_enter(&frame, base, entity, two);
    refii_bridge_gui_capture_begin();
    PPCContext fill = frame;
    fill.r3.u64 = fake;
    CallGuest(fill, base, refii::guest::Address(0x826CD190u));
    std::vector<uint8_t> calls(0x10000u);
    const uint32_t size = refii_bridge_gui_capture_end(calls.data(), static_cast<uint32_t>(calls.size()));
    calls.resize(size <= calls.size() ? size : 0u);
    if (scoped != 0) {
      refii_mod_opinion_leave();
    }
    blob.insert(blob.end(), calls.begin(), calls.end());
  }
  refii_bridge_player_blob_set(1, kCardKey, blob.data(), static_cast<uint32_t>(blob.size()));
  REXLOG_INFO("Refii co-op villager: the joiner's card of {:016X} stated ({} byte(s) of the card's opinion block{})",
              static_cast<unsigned long long>(uid), blob.size() - 8u,
              entity == 0u ? "; nothing here by that UID" : "");
}

void JoinerCardTick(PPCContext& ctx, uint8_t* base, uint32_t card) {
  (void)ctx;
  if (!OnlineJoiner() || card < 0x10000u) {
    return;
  }
  const uint32_t target = LoadU32(base, card + 0x1Cu);
  if (refii_mod_entity_alive(base, target) == 0 || refii_mod_component(base, target, kOpinion) != 0u) {
    return;  // the guest filled it (or there is nothing to fill)
  }
  const uint64_t uid = refii_household_shared_uid(base, target);
  if (g_card.card != card || g_card.asked != uid) {
    g_card = JoinerCard{card, uid, false};
    char text[96];
    std::snprintf(text, sizeof(text), "cardask:%016llX:0", static_cast<unsigned long long>(uid));
    refii_bridge_estate_intent_post(text);
  }
  if (g_card.applied) {
    return;
  }
  const int32_t size = refii_bridge_player_blob_get(1, kCardKey, nullptr, 0u);
  if (size < 8) {
    return;
  }
  std::vector<uint8_t> blob(static_cast<size_t>(size));
  refii_bridge_player_blob_get(1, kCardKey, blob.data(), static_cast<uint32_t>(blob.size()));
  uint64_t stated = 0u;
  for (int i = 0; i < 8; ++i) {
    stated |= static_cast<uint64_t>(blob[i]) << (8 * i);
  }
  if (stated != uid) {
    return;  // the host has not answered this target yet
  }
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
  g_card.applied = true;
  REXLOG_INFO("Refii co-op villager: this joiner's card of {:08X} takes the host's opinion block ({} statement(s))", target,
              posted);
}

void JoinerCardClosed(uint32_t card) {
  if (g_card.card == card) {
    g_card = JoinerCard{};
  }
}

}  // namespace refii::vfs

// The prompt routine (r3 the CECTargeted, r4 on). TU1 0x82175310 / disc 0x821750D8. The guest's body runs
// first; then, on an online hero joiner, a targeted thing with no villager component takes the host's
// statement of it.
REX_EXTERN(rex_lhECTargeted_InteractPrompt) {
  const uint32_t targeted = ctx.r3.u32;
  const bool on = (ctx.r4.u32 & 0xFFu) != 0u;
  const PPCContext entry = ctx;
  __imp__rex_lhECTargeted_InteractPrompt(ctx, base);
  if (!Heap(targeted) || !OnlineJoiner() || !HeroJoinerSession(base)) {
    return;
  }
  const auto raised = g_raised.find(targeted);
  if (!on) {
    // The routine's own removal sits behind +0x27 (`lbz r11,0x27(r29)` / `beq` 0x82175320 → return), which a
    // client never sets for a villager (below): a prompt raised from a statement is dropped here.
    if (raised != g_raised.end()) {
      if (LoadU8(base, targeted + 0x28u) != 0u) {
        DropPrompt(entry, base, LoadU32(base, targeted + 4u));
        refii::mods::StoreU8(base, targeted + 0x28u, 0u);
      }
      g_raised.erase(raised);
    }
    return;
  }
  const uint32_t entity = LoadU32(base, targeted + 4u);
  if (refii_mod_entity_alive(base, entity) == 0 || refii_mod_component(base, entity, kVillager) != 0u) {
    return;
  }
  const std::string stated = StatedPrompt(base, entity);
  static uint32_t s_seen = 0u;
  if (entity != s_seen && refii_mod_component(base, entity, 20u) != 0u) {  // a creature: what a villager is here
    s_seen = entity;
    REXLOG_INFO("Refii co-op villager: this joiner's prompt routine runs for creature {:08X} (UID {:016X} to the host, "
                "targeted here {}); the host states {}", entity,
                static_cast<unsigned long long>(refii_household_shared_uid(base, entity)), LoadU8(base, targeted + 0x27u),
                stated.empty() ? std::string("no prompt for it") : "'" + stated + "'");
  }
  // +0x27 ("this targeted thing has an interact prompt") is set by CECTargeted_vf11 only for a thing whose
  // villager's job (+156) is 26, 29 or 14 (`lwz r11,0x9C(r10)` / `cmpwi 0x1A|0x1D|0xE` / `stb r28,0x27(r29)`,
  // TU1 0x82427FA0..0x82427FBC) — read through the villager component a client never builds, so on the joiner it
  // stays 0 for every villager. The host's statement already carries its own copy's +0x27 (DriveVillagerUse),
  // and CECTargeted_vf14 asks this routine with r4 = 1 every frame the thing is this machine's player's target
  // (`lbz 0x21` / `lbz 0x22`, 0x8219FB10..0x8219FB50), so the statement alone decides here.
  if (raised != g_raised.end()) {
    if (raised->second == stated) {
      return;
    }
    DropPrompt(entry, base, entity);
    refii::mods::StoreU8(base, targeted + 0x28u, 0u);
    g_raised.erase(raised);
  }
  if (stated.empty() || LoadU8(base, targeted + 0x28u) != 0u) {
    return;  // nothing stated, or the routine's own icon (an owned thing) is up
  }
  if (RaisePrompt(entry, base, entity, stated)) {
    refii::mods::StoreU8(base, targeted + 0x28u, 1u);
    g_raised[targeted] = stated;
    REXLOG_INFO("Refii co-op villager: this joiner's prompt at {:08X} is the host's: '{}'", entity, stated);
  }
}

// Before `mr r11,r23` at the interact action's no-villager branch (TU1 0x82977064 / disc 0x829A65FC; r26 the
// target, r22 the user, r27 "send the generic use"): on an online hero joiner, its own hero's use of a
// villager the host stated a prompt for goes to the host, and the villager path's `r27 = 0` is taken.
void RefiiCoopJoinerUsesVillager(PPCRegister& r26, PPCRegister& r22, PPCRegister& r27) {
  uint8_t* base = RefiiGuestBase();
  if (base == nullptr || !OnlineJoiner() || !HeroJoinerSession(base) || r22.u32 != refii_mod_player_hero(base, 2)) {
    return;
  }
  const uint32_t target = r26.u32;
  if (refii_mod_entity_alive(base, target) == 0 || refii_mod_component(base, target, kVillager) != 0u) {
    return;
  }
  const std::string stated = StatedPrompt(base, target);
  if (stated.empty()) {
    if (refii_mod_component(base, target, 20u) == 0u) {
      return;  // not a creature: a chest, a door — their own use (mod_events.cpp useat)
    }
    REXLOG_INFO("Refii co-op villager: this player's interact reached {:08X} (UID {:016X}), which the host states no "
                "use for; the shipped generic use goes on", target,
                static_cast<unsigned long long>(refii_household_shared_uid(base, target)));
    return;
  }
  char text[96];
  std::snprintf(text, sizeof(text), "villuse:%016llX:0", static_cast<unsigned long long>(refii_household_shared_uid(base, target)));
  refii_bridge_estate_intent_post(text);
  r27.u64 = 0u;
  REXLOG_INFO("Refii co-op villager: this player's use of villager {:08X} ('{}') goes to the host", target, stated);
}
