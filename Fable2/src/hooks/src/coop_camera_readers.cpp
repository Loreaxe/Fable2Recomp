// refii — Fable 2: each player's camera for the sim's readers that act for a player (camera stage C2c).
//
// THE GETTER. Every sim reader asks for "the" camera through sub_821F87C8(players) (disc sub_821F8760):
// the player map's Player for key 2 on an online joiner (net+0x34 && net+0x36, `li r4,2; bl sub_82264230`
// 0x821F8820..28 / disc 0x821F87B8..C0), else key 1 (`li r4,1` 0x821F8848 / disc 0x821F87E0), and answers
// that Player's +4, its camera holder (holder+4 the manager; lhCamera_ScreenBlock sub_821DD3B0 reads it
// `lwz r3,4(r4)` 0x821DD400). While code runs for player two (RefiiCoopPartnerScreen, coop_hero_hooks.cpp)
// and player two has a camera manager of their own (split screen), the getter takes the guest's own key-2
// path: the same lookup sub_82264230(players, 2) (disc sub_822641F0) and the same +4. Outside that, and
// with no manager of player two's, it is the shipped function. The cached block (lhCamera_CurrentBlock)
// and lhCamera_ScreenBlock answer the same bracket in coop_hero_hooks.cpp.
//
// THE BRACKETS: code that acts for one entity runs for player two when that entity is player two's hero
// and player two's view is on this machine (RefiiCoopPartnerViewHere). Each wraps the whole function, so
// every camera read inside it is that player's:
//   - the stick to the world: CInputProcessCharacterMovement_vf1 (TU1 0x826E70C8 / disc 0x82743E48, one
//     vtable slot each, 0x82123950 / 0x82124188) and CPlayerActionReceiver_ProcessAction (mod_events.cpp),
//     both reaching sub_8283DF30 / sub_827ECDA0 (`bl sub_821F87C8` 0x8283DFC8 / 0x827ECE38); the entity
//     is the process's weak handle at +8, *(*(this+0xC)) (lhSmartPtr_Deref 0x82173E38: +4 the cell, the
//     cell's word the entity);
//   - the stick jab: CInputProcessStickJabDetection_vf6 (0x826E95A8 / 0x82746328, one slot each,
//     0x821239A0 / 0x821241D8) reaching sub_8283E058 / sub_827ECEC8; the same handle (`lwz r11,0xC(r25);
//     lwz r10,0(r11)` 0x826E95C4..D0);
//   - the first-person target search: sub_821A9478 (disc sub_821A95E0; r4 the searching entity, stored
//     `stw r30,0x18(r27)` 0x821A94B4), whose FIRST_PERSON branch takes its origin and direction from the
//     camera (`bl sub_821F87C8` 0x821A99FC / 0x821A9B64);
//   - the look-at ray: sub_823EE918 (disc sub_823CD340; r3 the Player component, the hero at +4,
//     `lwz r11,4(r24)` 0x823EE938), CECPlayer_vf14's update, reaching sub_823F1F50 / sub_823D0978;
//   - the ability probes: sub_8227D588 (disc sub_8227D400; r3 the probe set, its entity at +4 — the
//     entity's physics `*(*(r3+4)+0x7C)`), reaching the ±45° cone about the camera's forward sub_826179A0
//     / sub_8262C740 (`bl sub_821F87C8` 0x82617A28 / 0x8262C7C8);
//   - Player.GetPosition/ForwardOfCameraAssociatedWithPlayer (0x823E4F28, 0x823E4F80 / 0x823C38F0,
//     0x823C3948): r3 the out vector, r4 the Player component the script named (sub_823EF300 passes
//     `mr r4,r21` 0x823EF3B0, its +4 compared with hero one); the shipped body ignores r4.
// THE AI'S 50 M FILTER, either view: Kaim's CEntityInfoFilterF2Visibility_vf5 and F2Sight_vf5 (0x82626848,
// 0x82626AF8 / 0x8263B110, 0x8263B360, one vtable slot each) answer 1 — the entity info is filtered —
// when it lies farther than 50 m from the cached block's position (`fcmpu cr6,f6,f11` against 2500.0
// 0x82626910 / 0x8263B1D8). When the shipped answer filters and player two's view is here, the filter is
// asked again for player two's view; the info is filtered only when both views filter it. The deltas the
// filters store (flt_834C3C80..98 / disc flt_834C3914..) have no reader but the filters.
//
// SINGLE PLAYER IS THE SHIPPED GAME: with no player two there is no bracket and no partner view, so every
// wrapper calls the shipped function once with its own arguments and the getter is the shipped getter.

#include <atomic>
#include <cstdint>

#include <rex/logging.h>
#include <rex/ppc/context.h>
#include <rex/ppc/func.h>

#include "core/guest_build.h"
#include "core/guest_state.h"
#include "core/refii_mod.h"

extern "C" REX_FUNC(__imp__rex_lhPlayers_Camera);
extern "C" REX_FUNC(__imp__rex_CInputProcessCharacterMovement_ProcessInput);
extern "C" REX_FUNC(__imp__rex_CInputProcessStickJabDetection_ProcessInput);
extern "C" REX_FUNC(__imp__rex_Targeting_SearchContext);
extern "C" REX_FUNC(__imp__rex_CECPlayer_LookAtUpdate);
extern "C" REX_FUNC(__imp__rex_CECProbeSet_Probe);
extern "C" REX_FUNC(__imp__rex_Lua_Player_GetPositionOfCameraAssociatedWithPlayer);
extern "C" REX_FUNC(__imp__rex_Lua_Player_GetForwardOfCameraAssociatedWithPlayer);
extern "C" REX_FUNC(__imp__rex_Kaim_CEntityInfoFilterF2Visibility_Filter);
extern "C" REX_FUNC(__imp__rex_Kaim_CEntityInfoFilterF2Sight_Filter);

extern "C" void RefiiCoopPartnerScreen(int on);
extern "C" int RefiiCoopPartnerScreenDepth(void);
extern "C" int RefiiCoopPartnerViewHere(void);
extern "C" int RefiiCoopSplitScreen(void);

namespace {

std::atomic<bool> g_said_holder{false};
std::atomic<bool> g_said_bracket{false};
std::atomic<bool> g_said_either{false};

void Once(std::atomic<bool>& said, const char* what) {
  if (!said.exchange(true, std::memory_order_relaxed)) {
    REXLOG_INFO("Refii co-op cameras: {}", what);
  }
}

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

/// The entity a weak handle at `handle` names (lhSmartPtr_Deref: +4 the cell, the cell's word), 0 for none.
uint32_t Handle(uint8_t* base, uint32_t handle) {
  const uint32_t cell = refii::vfs::LoadGuestU32(base, handle + 4u);
  return Plausible(cell) ? refii::vfs::LoadGuestU32(base, cell) : 0u;
}

/// The calls of one function made for player two: entered when `entity` is player two's hero and player
/// two's view is on this machine.
class ForPlayerTwo {
 public:
  ForPlayerTwo(uint8_t* base, uint32_t entity) {
    if (Plausible(entity) && entity == refii_mod_player_hero(base, 2) && RefiiCoopPartnerViewHere() != 0) {
      on_ = true;
      RefiiCoopPartnerScreen(1);
      Once(g_said_bracket, "player two's own actions read player two's camera");
    }
  }
  ~ForPlayerTwo() {
    if (on_) {
      RefiiCoopPartnerScreen(0);
    }
  }
  ForPlayerTwo(const ForPlayerTwo&) = delete;
  ForPlayerTwo& operator=(const ForPlayerTwo&) = delete;

 private:
  bool on_ = false;
};

}  // namespace

extern "C" int RefiiCoopPartnerCameraFor(uint8_t* base, uint32_t entity) {
  return base != nullptr && Plausible(entity) && entity == refii_mod_player_hero(base, 2) &&
                 RefiiCoopPartnerViewHere() != 0
             ? 1
             : 0;
}

// sub_821F87C8(players): r3 the player map (game+0x9C); answers r3 the camera holder.
REX_EXTERN(rex_lhPlayers_Camera) {
  if (RefiiCoopPartnerScreenDepth() > 0 && RefiiCoopSplitScreen() != 0) {
    static PPCFunc* const find = refii::vfs::FindGuestFunction(refii::guest::Address(0x82264230u));
    if (find != nullptr) {
      PPCContext call = ctx;
      call.r4.u64 = 2u;
      find(call, base);
      const uint32_t player = call.r3.u32;
      const uint32_t holder = Plausible(player) ? refii::vfs::LoadGuestU32(base, player + 4u) : 0u;
      if (Plausible(holder) && Plausible(refii::vfs::LoadGuestU32(base, holder + 4u))) {
        ctx.r3.u64 = holder;
        Once(g_said_holder, "the camera asked for player two is player two's");
        return;
      }
    }
  }
  __imp__rex_lhPlayers_Camera(ctx, base);
}

REX_EXTERN(rex_CInputProcessCharacterMovement_ProcessInput) {
  const ForPlayerTwo scope(base, Handle(base, ctx.r3.u32 + 8u));
  __imp__rex_CInputProcessCharacterMovement_ProcessInput(ctx, base);
}

REX_EXTERN(rex_CInputProcessStickJabDetection_ProcessInput) {
  const ForPlayerTwo scope(base, Handle(base, ctx.r3.u32 + 8u));
  __imp__rex_CInputProcessStickJabDetection_ProcessInput(ctx, base);
}

REX_EXTERN(rex_Targeting_SearchContext) {
  const ForPlayerTwo scope(base, ctx.r4.u32);
  __imp__rex_Targeting_SearchContext(ctx, base);
}

REX_EXTERN(rex_CECPlayer_LookAtUpdate) {
  const uint32_t component = ctx.r3.u32;
  const ForPlayerTwo scope(base, Plausible(component) ? refii::vfs::LoadGuestU32(base, component + 4u) : 0u);
  __imp__rex_CECPlayer_LookAtUpdate(ctx, base);
}

REX_EXTERN(rex_CECProbeSet_Probe) {
  const uint32_t set = ctx.r3.u32;
  const ForPlayerTwo scope(base, Plausible(set) ? refii::vfs::LoadGuestU32(base, set + 4u) : 0u);
  __imp__rex_CECProbeSet_Probe(ctx, base);
}

REX_EXTERN(rex_Lua_Player_GetPositionOfCameraAssociatedWithPlayer) {
  const uint32_t component = ctx.r4.u32;
  const ForPlayerTwo scope(base, Plausible(component) ? refii::vfs::LoadGuestU32(base, component + 4u) : 0u);
  __imp__rex_Lua_Player_GetPositionOfCameraAssociatedWithPlayer(ctx, base);
}

REX_EXTERN(rex_Lua_Player_GetForwardOfCameraAssociatedWithPlayer) {
  const uint32_t component = ctx.r4.u32;
  const ForPlayerTwo scope(base, Plausible(component) ? refii::vfs::LoadGuestU32(base, component + 4u) : 0u);
  __imp__rex_Lua_Player_GetForwardOfCameraAssociatedWithPlayer(ctx, base);
}

namespace {

/// A Kaim 50 m filter (r3 the filter, r4, r5 the entity info; r3 back 1 filtered), asked for either view.
template <void (*Shipped)(PPCContext&, uint8_t*)>
void EitherView(PPCContext& ctx, uint8_t* base) {
  const PPCContext entry = ctx;
  Shipped(ctx, base);
  if (ctx.r3.u32 == 0u || RefiiCoopPartnerScreenDepth() > 0 || RefiiCoopPartnerViewHere() == 0) {
    return;
  }
  PPCContext again = entry;
  RefiiCoopPartnerScreen(1);
  Shipped(again, base);
  RefiiCoopPartnerScreen(0);
  if (again.r3.u32 == 0u) {
    ctx.r3.u64 = 0u;
    Once(g_said_either, "the AI perceives within 50 m of either player's camera");
  }
}

void ShippedVisibility(PPCContext& ctx, uint8_t* base) { __imp__rex_Kaim_CEntityInfoFilterF2Visibility_Filter(ctx, base); }
void ShippedSight(PPCContext& ctx, uint8_t* base) { __imp__rex_Kaim_CEntityInfoFilterF2Sight_Filter(ctx, base); }

}  // namespace

REX_EXTERN(rex_Kaim_CEntityInfoFilterF2Visibility_Filter) { EitherView<&ShippedVisibility>(ctx, base); }

REX_EXTERN(rex_Kaim_CEntityInfoFilterF2Sight_Filter) { EitherView<&ShippedSight>(ctx, base); }
