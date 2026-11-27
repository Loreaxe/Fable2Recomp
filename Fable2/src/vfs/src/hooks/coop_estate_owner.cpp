// refii — WHO OWNS A BUILDING, FOR BOTH HEROES (Fable 2 co-op).
//
// A HERO OWNS A BUILDING when it is on that hero's property list (CECPlayerProperties, comp69): the
// guest's own test `sub_828D14E8(comp69, building entity)` (TU1; disc `sub_828B0B38`) walks the list
// (`*(props+28)` the sentinel, the record at node+8) and compares the record's UID with the entity's
// (+20 / +24, 0x828D1588). Everything the property screen says about ownership asks it: its rows
// (`sub_82998300`, `bl` 0x82998830 / disc 0x828ED140), its page (`sub_82998FB0`, 0x829994E8 / disc
// 0x828EDDF8), its menu event (the ported OnMenuEvent's 112 / 116) and the sale sign's world icon
// (`CECBuildingSaleSign_vf12`, TU1 0x8292A320).
//
// THE OWNER THE PAGE NAMES is `Lua_Building_GetBuildingOwner` (`sub_8282D410`, disc `sub_8274F740`,
// r3 the CECBuilding): ONE hero, the players record's hero-one slot (`lwz r11,0x6C(r11)` TU1
// 0x8282D440 / disc 0x8274F770), when its comp69 owns the building (`bl sub_828D14E8` 0x8282D5AC /
// disc 0x8274F8DC, then `mr r3,r30` 0x8282D5BC / 0x8274F8EC), else the building's occupant (`bl
// sub_8282D5D8` 0x8282D5CC / 0x8274F8FC). Shipped there was one hero. With two, a building the other
// hero owns read as its occupant's — "Unknown" for an empty one — on either player's screen. Here the
// answer is the hero who owns it, whichever slot is lent: hero one, else a player two who brought a
// hero (the plain henchman owns nothing, RefiiCoopGetOwnHero), else the guest's own answer.
//
// ON AN ONLINE JOINER the host's hero is a replica whose properties the guest never sends (no
// CECNetwork case touches comp69); the joiner's copy carries the host's list as the host states it
// (household_persist.cpp, the properties mirror), so the same test answers there too.
//
// WHO MAY ACT (core/estate_owner_rule.h): the owner, or the other hero while the two are married or
// business partners. The property screen (real estate, 28 / disc 27) is opened for one hero, its
// buyer: the binding `sub_826B1A08` (TU1 / disc 0x82596CB8) stores the hero at h+0x90 (`stw r6,0x90(r30)`
// 0x826B1A6C / 0x82596D1C, the midasm RefiiCoopEstateBuyer), its comp69 at h+0x94 and its comp77 at
// h+0x98, and the sale sign as that hero's use target, comp77's vf+68 (`lwz r10,0x44(r11)` / `bctrl`
// 0x826B1CC8 / 0x826B1CD0, disc 0x82596F78 / 0x82596F80), at h+0x9C; the rows (`sub_82998300`) and the
// page fills read the same target through the players record's +108 hero. Every helper then acts on the
// binding. So:
//   - a partner's screen is the OWNER's: bound to the owner (RefiiEstateBuyerOpens), the owner lent
//     where the screen reads its hero, and the owner's use target answered as the sale sign the viewer
//     used while that screen's code runs (EstateTargetScope over `sub_821E66F0`, below) — the owner's
//     rows, page, purse and helpers, on the building the viewer stands at; the GUI and the pad stay the
//     viewer's;
//   - a refused viewer's screen shows the page as theirs (the owner named, coop_estate_owner's owner
//     answer) with Back as its only row (gui_screen_stub_real_estate.cpp), and its menu events other
//     than Back are dropped (mod_events.cpp); online the host refuses the same actions again.
//
// THE USE TARGET, `sub_821E66F0` (TU1, 22 instructions; disc `sub_821E62B0`, byte-identical; in the
// vtables at 0x820074B4 / 0x8200757C, disc 0x820074D4 / 0x8200759C): `lwz r11,0xC(r3)`, the target
// record's weak holder `lwz r11,0x1C(r11)`, the entity `lwz r10,0(r11)` → r3 (0x821E66FC..0x821E671C).
// Only the bound owner's comp77 is answered, and only inside a partner's screen.
//
// THE OWNER BY NAME. Page 1 names a viewer who owns the building by their title (`Lua_Stats_GetTitle`
// on the hero's comp57, TU1 0x829995DC / 0x82999690 / 0x829996A8) and any other owner through the
// entity's display name, `sub_823C8510` (disc `sub_828C8440`; `bl` 0x829996EC behind
// `Lua_Building_GetBuildingOwner` 0x829996B4): its name component (comp20, `sub_823C83B0`) and a
// villager's vocation, never a hero's title — shipped, no hero but the viewer could own one. While a
// property screen is open here, a hero's display name is their title, as the page states it for its
// own hero.

#include <cstdint>
#include <cstring>
#include <string>

#include <rex/logging.h>

#include "core/estate_owner_rule.h"
#include "core/guest_build.h"
#include "core/guest_state.h"
#include "core/payload_bridge_fable2.h"
#include "core/refii_mod.h"
#include "hooks/ppc_externs.h"
#include "vfs_hooks_private.h"

/// Player two brought a hero (a character from a save) rather than standing as the plain henchman.
extern "C" int RefiiCoopGetOwnHero(void);
/// mod_events.cpp: on the online host, hero two's use target as the joiner states it.
extern "C" int32_t refii_household_joiner_use_target(PPCContext* ctx, uint8_t* base, uint32_t comp77, uint32_t* out);

PPC_EXTERN_FUNC(__imp__rex_CECBuilding_Owner);
PPC_EXTERN_FUNC(__imp__rex_CECUseTarget_Target);
PPC_EXTERN_FUNC(__imp__rex_lhEntity_DisplayName);

namespace {

bool Heap(uint32_t at) { return at >= 0x10000u && at < 0xE0000000u; }

/// `sub_828D14E8(hero's comp69, building)`, the guest's owned test.
bool Owns(const PPCContext& ctx, uint8_t* base, uint32_t hero, uint32_t building) {
  const uint32_t props = hero != 0u ? refii_mod_component(base, hero, 69u) : 0u;
  if (props == 0u || !Heap(building)) {
    return false;
  }
  PPCContext work = refii::mods::Frame(ctx);
  work.r3.u64 = props;
  work.r4.u64 = building;
  return refii::mods::CallGuest(work, base, refii::guest::Address(0x828D14E8u)) && (work.r3.u32 & 0xFFu) != 0u;
}

/// The property screen's session: what RefiiEstateBuyerOpens decided for the screen about to open
/// (pending), then for that screen until it exits.
struct Session {
  uint32_t screen = 0u;
  refii::vfs::EstateSession facts;
};

Session g_pending;
Session g_open;

/// Inside a partner's screen: the owner's comp77 and the sign it answers.
thread_local uint32_t t_owner_use = 0u;
thread_local uint32_t t_owner_target = 0u;

}  // namespace

namespace refii::vfs {

void EstateSessionPending(const EstateSession& facts) {
  g_pending.screen = 0u;
  g_pending.facts = facts;
}

void EstateSessionBegin(uint32_t screen) {
  if (g_pending.facts.viewer == 0 && g_open.screen == screen) {
    return;  // the same screen entered again with nothing newly asked: its session stands
  }
  g_open = g_pending;
  g_open.screen = screen;
  g_pending = Session{};
}

void EstateSessionEnd(uint32_t screen) {
  if (g_open.screen == screen) {
    g_open = Session{};
  }
}

const EstateSession* EstateSessionOf(uint32_t screen) {
  return screen != 0u && g_open.screen == screen ? &g_open.facts : nullptr;
}

EstateTargetScope::EstateTargetScope(uint32_t screen) : use_(t_owner_use), target_(t_owner_target) {
  const EstateSession* facts = EstateSessionOf(screen);
  if (facts != nullptr && facts->access == static_cast<uint8_t>(refii::coop::EstateAccess::kPartner) &&
      facts->owner_use != 0u && facts->target != 0u) {
    t_owner_use = facts->owner_use;
    t_owner_target = facts->target;
  }
}

EstateTargetScope::~EstateTargetScope() {
  t_owner_use = use_;
  t_owner_target = target_;
}

int32_t EstateOwnerPlayer(const PPCContext& ctx, uint8_t* base, uint32_t building) {
  if (Owns(ctx, base, refii_mod_player_hero(base, 1), building)) {
    return 1;
  }
  if (RefiiCoopGetOwnHero() != 0 && Owns(ctx, base, refii_mod_player_hero(base, 2), building)) {
    return 2;
  }
  return 0;
}

}  // namespace refii::vfs

// TU1 0x8282D410 / disc 0x8274F740 (r3 the CECBuilding, the owner entity in r3).
REX_EXTERN(rex_CECBuilding_Owner) {
  const uint32_t record = ctx.r3.u32;
  const PPCContext entry = ctx;
  __imp__rex_CECBuilding_Owner(ctx, base);
  if (refii_mod_player_hero(base, 2) == 0u || !Heap(record)) {
    return;
  }
  const uint32_t building = refii::vfs::LoadGuestU32(base, record + 4u);
  const int32_t owner = refii::vfs::EstateOwnerPlayer(entry, base, building);
  if (owner != 0) {
    ctx.r3.u64 = refii_mod_player_hero(base, owner);
  }
}

// TU1 0x821E66F0 / disc 0x821E62B0 (r3 the comp77, the target entity in r3): inside a partner's
// property screen, the owner's use target is the sale sign the viewer used; on the online host, hero
// two's is what the joiner's own hero targets (mod_events.cpp `usetarget`) — the host's interact for
// the joiner's request (0x9E) reads it here, TU1 0x82976588's `(*(*comp77 + 68))(comp77)`.
REX_EXTERN(rex_CECUseTarget_Target) {
  if (t_owner_use != 0u && ctx.r3.u32 == t_owner_use) {
    ctx.r3.u64 = t_owner_target;
    return;
  }
  uint32_t stated = 0u;
  if (refii_household_joiner_use_target(&ctx, base, ctx.r3.u32, &stated) != 0) {
    ctx.r3.u64 = stated;
    return;
  }
  __imp__rex_CECUseTarget_Target(ctx, base);
}

// THE OWNER'S TITLE ON BOTH MACHINES. A hero's title is their stats' (comp 57) +92 title record, its
// "HeroTitleComponent" "TextTag" (`sub_824D8A10`, TU1 / disc 0x8249FB88: `if (*(a2 + 92))` 0x824D8A50,
// then the record's TextTag assigned 0x824D8A8C). It is written only by Lua_Stats_SetTitleRecord (TU1
// 0x824D8568: `*(_QWORD *)(a1 + 92) = v6` 0x824D8608; callers the title deed `sub_8296CBE8` 0x8296CD98, the
// stats load `sub_824E63C8` 0x824E7070 and its Lua binding) — no net message writes it, so the other
// machine's copy of a hero keeps the title it was made with. Each machine therefore STATES its own hero's
// title (the session text kTitleKey in that player's store: the host player one's, the joiner player
// two's), and the page names the other machine's hero by that statement.
namespace {
constexpr char kTitleKey[] = "refii.title";

/// The TextTag `sub_824D8A10` gives `hero`'s title ("" for none), read through a scratch string.
std::string TitleTag(const PPCContext& ctx, uint8_t* base, uint32_t hero) {
  const uint32_t stats = hero != 0u ? refii_mod_component(base, hero, 57u) : 0u;
  if (stats == 0u) {
    return {};
  }
  PPCContext work = refii::mods::Frame(ctx, 0x200u);
  const uint32_t out = (work.r1.u32 + 0x100u) & ~0xFu;
  refii::mods::StoreU32(base, out, 0u);
  work.r3.u64 = out;
  work.r4.u64 = stats;
  if (!refii::mods::CallGuest(work, base, refii::guest::Address(0x824D8A10u))) {
    return {};
  }
  std::string tag;
  const uint32_t rep = refii::vfs::LoadGuestU32(base, out);
  const uint32_t chars = Heap(rep) ? refii::vfs::LoadGuestU32(base, rep) : 0u;
  for (uint32_t i = 0; Heap(chars) && i < 200u && base[chars + i] != 0; ++i) {
    tag.push_back(static_cast<char>(base[chars + i]));
  }
  PPCContext dtor = refii::mods::Frame(ctx, 0x200u);
  dtor.r3.u64 = out;
  rex_lhCharString_Dtor(dtor, base);
  return tag;
}
}  // namespace

namespace refii::vfs {

void StateOwnTitle(const PPCContext& ctx, uint8_t* base) {
  const int32_t mode = refii_mod_coop_mode(base);
  const int32_t player = mode == 2 ? 1 : mode == 3 ? 2 : 0;
  const uint32_t hero = player != 0 ? refii_mod_player_hero(base, player) : 0u;
  if (hero == 0u) {
    return;
  }
  const std::string tag = TitleTag(ctx, base, hero);
  char held[REFII_MOD_PLAYER_TEXT_MAX + 1];
  if (refii_bridge_player_get_text(player, kTitleKey, sizeof(kTitleKey) - 1u, held, sizeof(held)) >= 0 && tag == held) {
    return;
  }
  refii_bridge_player_write_text(player, kTitleKey, sizeof(kTitleKey) - 1u, tag.c_str(), tag.size(), 0);
  REXLOG_INFO("Refii estate: player {}'s title stated to the other machine: '{}'", player, tag);
}

}  // namespace refii::vfs

// TU1 0x823C8510 / disc 0x828C8440 (r3 the out string, r4 the entity; the string in r3).
REX_EXTERN(rex_lhEntity_DisplayName) {
  const uint32_t out = ctx.r3.u32;
  const uint32_t entity = ctx.r4.u32;
  if (g_open.screen != 0u && entity != 0u && refii_mod_player_hero(base, 2) != 0u &&
      (entity == refii_mod_player_hero(base, 1) || entity == refii_mod_player_hero(base, 2))) {
    const uint32_t stats = refii_mod_component(base, entity, 57u);
    PPCContext work = refii::mods::Frame(ctx);
    work.r3.u64 = out;
    work.r4.u64 = stats;
    if (stats != 0u && refii::mods::CallGuest(work, base, refii::guest::Address(0x824D8A10u))) {
      // The other machine's hero: its title as that machine states it (above).
      const int32_t player = entity == refii_mod_player_hero(base, 1) ? 1 : 2;
      char stated[REFII_MOD_PLAYER_TEXT_MAX + 1];
      if (refii_mod_player_here(player) == 0 &&
          refii_bridge_player_get_text(player, kTitleKey, sizeof(kTitleKey) - 1u, stated, sizeof(stated)) >= 0) {
        PPCContext assign = refii::mods::Frame(ctx, 0x200u);
        const uint32_t text = (assign.r1.u32 + 0x100u) & ~0xFu;
        const size_t length = std::strlen(stated);
        std::memcpy(base + text, stated, length + 1u);
        assign.r3.u64 = out;
        assign.r4.u64 = text;
        rex_lhCharString_AssignCStr(assign, base);
      }
      ctx.r3.u64 = out;
      return;
    }
  }
  __imp__rex_lhEntity_DisplayName(ctx, base);
}
