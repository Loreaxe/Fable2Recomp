// THE ONLINE JOINER BUILDS THE HOUSEHOLD, THE SALE SIGNS AND EVERY USE. An entity built on a network client
// gets only the components the guest's client list names: the build loop `sub_8238DCC8` (TU1; disc
// `sub_82393498`) asks, per component of the definition, whether this machine is a client —
// `game+0x8C` (the net context) `+0x34` session and `+0x36` client (TU1 0x8238DDF8..0x8238DE14, disc
// 0x823935C8..0x823935E4; `lhNetManager_AcceptJoinAsClient` 0x82334ED8 / 0x82334EDC writes both 1) —
// and, on a client, the component's kind (`sub_82630218(registry, hash)` / disc `sub_82644968`, the
// record's +0x14; 0x8238DF04..0x8238DF1C / disc 0x823936D4..0x823936EC) goes through a 238-entry jump
// table (0x8238DF3C / disc 0x8239370C): 114 kinds reach `lhEntity_AddComponent` (0x8238E2F8 /
// 0x82393AC8), the other 124 are never built. The same list on both builds. Among the dropped:
//   69  CECPlayerProperties  the hero's properties   (table index 68 → default)
//   136 CECPlayerFamily      the hero's family        (index 135 → default)
//   78  the use component    what a hero can use      (index 77 → default)
//   142 CECBuildingSaleSign  the sign's building link (index 141 → default)
// So on the 360 a joiner could not use a sale sign at all, and its hero carried no household. Refii's
// online household is host-authoritative (household_persist.cpp: the host keeps hero two's household
// and sends it as a blob; the joiner's estate actions run on the host, mod_events.cpp EstateForwards /
// ReplayEstateIntent), and its joiner side reads these components: the mirror loads the blob onto hero
// two's 69 and 136, the sale sign's use (78) opens the sign's script, and the estate screen's binding
// (`sub_826B1A08`) finds the building through the sign's 142. The kinds below are therefore also built
// on a client — the guest's own construction, unchanged otherwise (the component's init from the
// definition, 0x8238E78C..0x8238E94C). `lhEntity_AddComponent` refuses once construction is over (it
// tests `entity+144 & 0x80`, 0x8238CDEC; cleared at 0x8238E75C), so construction is the only place.
//
// EVERY USABLE THING GETS ITS USE (78) on a client too, so the joiner's own engine prompts for it
// (CECOnActionUse's update `sub_82821E88` draws the thing's icon for the player it tests, and on an
// online joiner that is the joiner's own hero, coop_hero_hooks.cpp NearerPlayerTwo) and its own hero's
// interact and hold reach the thing's use. That use does not run there: the joiner states it to the
// host (mod_events.cpp rex_CECOnActionUse_Use, `useat`), whose copy of the thing and of hero two have
// every component the script drives; only a sale sign's runs on the joiner (its screen). The update
// reads a component only behind the entity's component bit for it (its 13 `extrwi` tests, TU1
// 0x8282203C..0x82822DD8), so a thing whose other components a client does not build stays quiet.
// A HERO (a definition that lists 69) also gets its guild messages (189) and its drunkenness (157):
// the joiner's book texts are its own hero's guild messages, and its drunk warp is read off the LOCAL
// player's 157 (`sub_822B62F0`: `sub_821C7CE0` at TU1 0x822B6360 — player two on a joiner — then that
// hero's +12 >= +36). The host's run of its use states both to it (core Lua: GuildMessages.PostWithNarrator
// and PlayerDrunkenness.SoberUp go to the joiner on THE ROUTE; a drink is the joiner's own bonus run). The definition's components are the
// build loop's own map at r1+0xA0 (`sub_8238DB28(def, r1+0xA0)` 0x8238DE44 / disc 0x82393614; head at
// r1+0xA4, the node's component hash at +0xC, walked as `sub_82300128` / the loop at 0x8238DE84 walk it).
//
// Each hook runs before `addi r11, r11, -1` (TU1 0x8238DF1C / disc 0x823936EC) with r11 the kind; true
// takes the build arm (0x8238E2F8 / disc 0x82393AC8), false lets the shipped table decide.

#include <atomic>
#include <cstdint>

#include <rex/logging.h>
#include <rex/ppc/context.h>

#include "core/guest_build.h"
#include "core/guest_state.h"

extern "C" uint8_t* RefiiGuestBase(void);

namespace {

constexpr uint32_t kProperties = 69u;
constexpr uint32_t kUse = 78u;
constexpr uint32_t kFamily = 136u;
constexpr uint32_t kSaleSign = 142u;
constexpr uint32_t kDrunkenness = 157u;
constexpr uint32_t kGuildMessages = 189u;

/// The kinds a client builds beyond the guest's list: the household on any entity that has one (only
/// the heroes do), the sale sign's link, every use, and a hero's guild messages and drunkenness.
constexpr bool ClientKeeps(uint32_t kind, bool hero) {
  return kind == kProperties || kind == kFamily || kind == kSaleSign || kind == kUse ||
         ((kind == kDrunkenness || kind == kGuildMessages) && hero);
}

static_assert(ClientKeeps(69u, false) && ClientKeeps(136u, false), "the household is built on a client");
static_assert(ClientKeeps(142u, false) && ClientKeeps(78u, false), "every use and a sale sign's link are built");
static_assert(ClientKeeps(157u, true) && ClientKeeps(189u, true) && !ClientKeeps(157u, false) && !ClientKeeps(189u, false),
              "a hero's drunkenness and guild messages, nobody else's");
static_assert(!ClientKeeps(1u, true) && !ClientKeeps(77u, true) && !ClientKeeps(0u, true) && !ClientKeeps(79u, true) &&
                  !ClientKeeps(36u, true) && !ClientKeeps(182u, true),
              "nothing else changes: the guest's table decides the rest");

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

bool IsNil(uint8_t* base, uint32_t node) { return !Plausible(node) || refii::vfs::LoadGuestU8(base, node + 0x11u) != 0u; }

/// A component hash's kind, as `sub_82630218` (TU1; disc `sub_82644968`) finds it: the registry
/// `*(*(*(*(root)+0xC)+0x58)+4)+4` holds 24-byte records [+8, +0xC) sorted by hash (+0); the kind is
/// the found record's +0x14 (the factory at +4, its +0x10). 0 when the hash is not registered.
uint32_t KindOf(uint8_t* base, uint32_t hash) {
  using refii::vfs::LoadGuestU32;
  uint32_t at = LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  for (uint32_t offset : {0xCu, 0x58u, 0x4u, 0x4u}) {
    if (!Plausible(at)) {
      return 0u;
    }
    at = LoadGuestU32(base, at + offset);
  }
  if (!Plausible(at)) {
    return 0u;
  }
  uint32_t first = LoadGuestU32(base, at + 8u);
  const uint32_t last = LoadGuestU32(base, at + 0xCu);
  if (!Plausible(first) || last < first || (last - first) % 0x18u != 0u || (last - first) > 0x18u * 4096u) {
    return 0u;
  }
  uint32_t count = (last - first) / 0x18u;
  while (count > 0u) {
    const uint32_t half = count / 2u;
    const uint32_t middle = first + half * 0x18u;
    if (LoadGuestU32(base, middle) < hash) {
      first = middle + 0x18u;
      count -= half + 1u;
    } else {
      count = half;
    }
  }
  return first != last && LoadGuestU32(base, first) == hash ? LoadGuestU32(base, first + 0x14u) : 0u;
}

/// The node after `node` in the definition's map, as `sub_82300128` steps it (left +0, parent +4,
/// right +8, nil flag +0x11): the leftmost of the right subtree, else the first parent reached from
/// a left child.
uint32_t NextNode(uint8_t* base, uint32_t node) {
  using refii::vfs::LoadGuestU32;
  uint32_t right = LoadGuestU32(base, node + 8u);
  if (!IsNil(base, right)) {
    for (uint32_t left = LoadGuestU32(base, right); !IsNil(base, left); left = LoadGuestU32(base, right)) {
      right = left;
    }
    return right;
  }
  uint32_t parent = LoadGuestU32(base, node + 4u);
  while (!IsNil(base, parent) && node == LoadGuestU32(base, parent + 8u)) {
    node = parent;
    parent = LoadGuestU32(base, parent + 4u);
  }
  return parent;
}

/// Whether the definition being built lists a component of `want`: its map at r1+0xA0, head at +4,
/// begin = head's left.
bool DefinitionLists(uint8_t* base, uint32_t sp, uint32_t want) {
  using refii::vfs::LoadGuestU32;
  const uint32_t head = LoadGuestU32(base, sp + 0xA4u);
  if (!Plausible(head)) {
    return false;
  }
  uint32_t node = LoadGuestU32(base, head);
  for (uint32_t steps = 0u; Plausible(node) && node != head && !IsNil(base, node) && steps < 1024u; ++steps) {
    if (KindOf(base, LoadGuestU32(base, node + 0xCu)) == want) {
      return true;
    }
    node = NextNode(base, node);
  }
  return false;
}

std::atomic<bool> g_said_household{false};
std::atomic<bool> g_said_use{false};
std::atomic<bool> g_said_hero{false};

}  // namespace

bool RefiiCoopClientKeepsComponent(PPCRegister& r11, PPCRegister& r1) {
  const uint32_t kind = r11.u32;
  if (kind != kProperties && kind != kFamily && kind != kSaleSign && kind != kUse && kind != kDrunkenness &&
      kind != kGuildMessages) {
    return false;
  }
  uint8_t* base = RefiiGuestBase();
  if (base == nullptr) {
    return false;
  }
  const bool hero_kind = kind == kDrunkenness || kind == kGuildMessages;
  const bool keep = ClientKeeps(kind, hero_kind && DefinitionLists(base, r1.u32, kProperties));
  if (keep) {
    std::atomic<bool>& said = kind == kProperties || kind == kFamily ? g_said_household
                              : hero_kind                            ? g_said_hero
                                                                     : g_said_use;
    if (!said.exchange(true, std::memory_order_relaxed)) {
      REXLOG_INFO("Refii co-op client: this joiner builds component {} ({})", kind,
                  kind == kProperties || kind == kFamily ? "the hero's household"
                  : hero_kind                            ? "the hero's guild messages and drunkenness"
                                                         : "a usable thing");
    }
  }
  return keep;
}
