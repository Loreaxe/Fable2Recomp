// refii — Fable 3: each couch player's camera for the sim's readers while the screen is split (camera C2f).
//
// "THE" CAMERA AND "THE OTHER SCREEN'S". Every reader asks the selector (TU3 sub_82208EA0 / disc
// sub_82209680: key 2's holder on an online client, else key 1's), which coop_camera_hooks.cpp answers
// with player two's holder inside a player-two bracket. Fable 3 also has the other screen of a two-
// machine game: sub_82560180 (disc sub_8252FC70) caches, per world frame (stamp dword_832DEA9C / disc
// dword_8329E948), the selector's manager's +0x46 ? the live block (`bl sub_821CB2E0` 0x82560220) : the
// partner's block as received (`bl sub_8256CC48` 0x82560228: manager+0xF0, which net message 387 fills),
// the `lbz r10,0x46(r11); cmplwi; beq` at TU3 0x82560214..0x8256021C / disc 0x8252FD04..0x8252FD0C.
// Its consumers take "either screen" / "the nearer" themselves, each behind a test that the session has
// two screens:
//   - the LOD tier sub_821F1988 (disc sub_821F2358, from sub_821DCB28: navigation, entity updates): the
//     lower tier of the two cameras (`bl sub_821DB388` ×2, `cmpw; blt`) unless the mode is the shared screen
//     (`lwz r9,0x80(r10); cmpwi cr6,r9,1; bne` TU3 0x821F19D8..0x821F19E0 / disc `0x7C` 0x821F23A8..0x821F23B0);
//   - the visibility test sub_821FE0C8 (disc sub_821FE8D8; CECLook, IcyBreath, unsheathing): either camera
//     (`bl sub_821FC1A0` ×2), the same test at TU3 0x821FE1BC..0x821FE1C4 / disc 0x821FE9CC..0x821FE9D4;
//   - the LOD centre's second point sub_82194010 (disc sub_82193D48) for sub_822056A8 (talk, vault, the AI
//     distance natives): the nearer of the camera and the other screen's (`fcmpu cr6,f0,f1; bge; fmr f1,f0`
//     0x82205778..0x82205780), unless the mode is the shared screen (`cmpwi cr6,r9,1; beq` TU3
//     0x82194034..0x82194038 / disc 0x82193D6C..0x82193D70);
//   - the per-entity second slot sub_82209398 (disc sub_82209DB8: distance and frustum of the other screen,
//     sub_826B42F8 → entity+0x70 / +0xEF) and the biped mode's frustum test in sub_82233560 (disc
//     sub_822347B8), behind Network.IsInLiveGame (`bl Lua_Network_IsInLiveGame; clrlwi; cmplwi; beq` TU3
//     0x822093B4..0x822093C0, 0x82233950..0x8223395C / disc 0x82209DD4..0x82209DE0, 0x82234B98..0x82234BA4);
//   - disc only: the world update sub_821D6EB8 hands its pass the other screen's frustum too (`cmpwi
//     cr6,r9,1; beq` 0x821D6FA0..0x821D6FA4, `bl sub_8252FD90` 0x821D7004); TU3's sub_821D6CE8 passes one.
// Each of those two-screen tests ANSWERS "two screens" while each couch player has their own camera
// (RefiiF3TwoScreens), and the other screen's camera is player two's: sub_82560180 runs inside a player-two
// bracket, so the selector names player two's holder, and its +0x46 test takes the live read for that
// holder (RefiiF3OtherScreenIsHere): the partner's camera is on this machine, not received.
//
// THE BRACKETS (each wraps the whole function, so every camera read inside is player two's):
//   - the stick jab: CInputProcessStickJabDetection_vf6 (TU3 0x82934470 / disc 0x828DB6B8, one vtable slot
//     each, 0x82001C28 / 0x82001C8C), whose sub_828F3AE8 / sub_829F2490 asks the selector (TU3 0x828F3B20);
//     its entity the smart pointer at this+8 (`lwz r11,0xC(r3)` … `addi r28,r3,8; bl lhSmartPtr_Deref`
//     0x8293447C..0x829344B4), as the movement's (coop_camera_hooks.cpp).
// "THE" CAMERA'S CACHED BLOCK. sub_82221610 (disc sub_82222728) caches the selector's block per world frame
// (stamp dword_832DEA98 / disc dword_8329E944, block unk_832E1D50 / unk_832A1290). Inside a bracket it
// answers the other screen's cached block, which in a split couch game is player two's (above): the frame
// cache of player one's camera is neither read nor written for player two.
// THE AI'S 50 M FILTERS, either view: Kaim's CEntityInfoFilterF2Visibility_vf5 (TU3 0x8275ACF8 / disc
// 0x82726978) and F2Sight_vf5 (0x8275AFA8 / 0x82726C28) answer 1 — the info is filtered — when it lies
// farther than 50 m from the cached block's position (`fcmpu cr6,f6,f11` against 2500.0, disc 0x82726A40 /
// 0x82726E20; their stored deltas flt_835D8780..88 / flt_835D8844..4C have no other reader, both builds).
// When the shipped answer filters and the screen is split, the filter is asked again for player two's
// view; the info is filtered only when both views filter it.
//
// SINGLE PLAYER IS THE SHIPPED GAME: every answer needs SplitInEffect (the shell's split, a couch game —
// the session mode 1 — and a key-2 Player with a camera manager) or a bracket, which is only entered
// under the same test. With no player two every wrapper calls the shipped function once with its own
// registers and every two-screen test runs its shipped instruction.

#include <atomic>
#include <cstdint>

#include <rex/logging.h>
#include <rex/ppc.h>
#include <rex/ppc/context.h>
#include <rex/system/kernel_state.h>
#include <rex/system/xmemory.h>

#include "coop_camera.h"
#include "gui_statements.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_CInputProcessStickJabDetection_ProcessInput);
REX_EXTERN(__imp__rex_Camera_CachedBlock);
REX_EXTERN(__imp__rex_Camera_OtherScreenBlock);
REX_EXTERN(__imp__rex_Kaim_CEntityInfoFilterF2Visibility_Filter);
REX_EXTERN(__imp__rex_Kaim_CEntityInfoFilterF2Sight_Filter);

namespace {

namespace coopcam = refii::fable3::coopcam;
using refii::fable3::LoadGuestU32;
using refii::fable3::Plausible;

std::atomic<bool> g_said_jab{false};
std::atomic<bool> g_said_screens{false};
std::atomic<bool> g_said_either{false};

void Once(std::atomic<bool>& said, const char* what) {
  if (!said.exchange(true, std::memory_order_relaxed)) {
    REXLOG_INFO("Refii couch camera: {}", what);
  }
}

uint8_t* GuestBase() {
  rex::system::KernelState* state = rex::system::kernel_state();
  rex::memory::Memory* memory = state != nullptr ? state->memory() : nullptr;
  return memory != nullptr ? memory->virtual_membase() : nullptr;
}

/// The entity a weak handle at `handle` names (lhSmartPtr_Deref TU3 0x82691588: +4 the cell, the cell's word).
uint32_t Handle(const uint8_t* base, uint32_t handle) {
  const uint32_t cell = Plausible(handle) ? LoadGuestU32(base, handle + 4u) : 0u;
  return Plausible(cell) ? LoadGuestU32(base, cell) : 0u;
}

}  // namespace

// A two-screen test (the mode's shared-screen compare, or Network.IsInLiveGame's): taken as two screens
// while each couch player has their own camera.
bool RefiiF3TwoScreens() {
  if (!coopcam::SplitInEffect(GuestBase())) {
    return false;
  }
  Once(g_said_screens, "the sim's other screen is player two's camera (either view, the nearer)");
  return true;
}

// The other screen's +0x46 test (r4 the selector's holder): the live read for player two's holder in a
// split couch game, inside the player-two bracket the other screen's block runs in.
bool RefiiF3OtherScreenIsHere(PPCRegister& r4) {
  const uint8_t* base = GuestBase();
  return coopcam::PartnerDepth() > 0u && coopcam::SplitInEffect(base) && r4.u32 != 0u &&
         r4.u32 == coopcam::PartnerHolder(base);
}

// The other screen's cached block: asked for player two's camera while split.
REX_EXTERN(rex_Camera_OtherScreenBlock) {
  const coopcam::PartnerScope scope(coopcam::PartnerDepth() == 0u && coopcam::SplitInEffect(base));
  __imp__rex_Camera_OtherScreenBlock(ctx, base);
}

// "The" camera's cached block: inside a player-two bracket while split, the other screen's (player two's).
REX_EXTERN(rex_Camera_CachedBlock) {
  if (coopcam::PartnerDepth() > 0u && coopcam::SplitInEffect(base)) {
    __imp__rex_Camera_OtherScreenBlock(ctx, base);
    return;
  }
  __imp__rex_Camera_CachedBlock(ctx, base);
}

// The stick jab (r3 the process, its entity the smart pointer at +8): player two's reads player two's camera.
REX_EXTERN(rex_CInputProcessStickJabDetection_ProcessInput) {
  const uint32_t entity = Plausible(ctx.r3.u32) ? Handle(base, ctx.r3.u32 + 8u) : 0u;
  const bool partner = coopcam::SplitInEffect(base) && coopcam::IsPartnerHero(base, entity);
  if (partner) {
    Once(g_said_jab, "player two's stick jab reads player two's camera");
  }
  const coopcam::PartnerScope scope(partner);
  __imp__rex_CInputProcessStickJabDetection_ProcessInput(ctx, base);
}

namespace {

/// A Kaim 50 m filter (r3 the filter, r5 the entity info; r3 back 1 filtered), asked for either view.
template <void (*Shipped)(PPCContext&, uint8_t*)>
void EitherView(PPCContext& ctx, uint8_t* base) {
  const PPCContext entry = ctx;
  Shipped(ctx, base);
  if (ctx.r3.u32 == 0u || coopcam::PartnerDepth() > 0u || !coopcam::SplitInEffect(base)) {
    return;
  }
  PPCContext again = entry;
  {
    const coopcam::PartnerScope scope(true);
    Shipped(again, base);
  }
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
