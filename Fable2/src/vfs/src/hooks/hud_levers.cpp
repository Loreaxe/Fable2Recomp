// THE HUD'S TWO LEVERS (S7 B10): the safety toggle and the target's villager card, each the guest's
// own call with the guest's own gates. Neither is a screen; the guest tick pump dispatches them
// (guest_tick_pump.cpp) and declares them with the ported screens' levers (hooks/gui_screen_stubs.h).
#include <cstdint>
#include <cstring>
#include <initializer_list>

#include <rex/logging.h>

#include "core/guest_build.h"
#include "core/refii_mod.h"
#include "hooks/gui_screen_stub_support.h"
#include "hooks/gui_screen_stubs.h"
#include "hooks/ppc_externs.h"
#include "vfs_hooks_private.h"

using namespace refii::vfs::stub;
using refii::mods::LoadU32;
using refii::mods::StoreU32;

namespace refii::vfs {

bool DispatchSafetyToggle(PPCContext& ctx, uint8_t* base, uint32_t player) {
  // The expression wheel's own branch for its top item 6, `lhGuiExpressionWheel_Accept` TU1
  // 0x826AA344..0x826AA4DC / disc 0x8258DA84..0x8258DC1C: HERO ONE (`sub_8222C1B8(W)` 0x826AA35C,
  // W = `*(*(GAME+0x58)+4)`), alive (0x826AA36C); nothing unless the wheel's owner is player one
  // (r4, 0x826AA390); its component 33; "may toggle" `sub_823E0B50(comp33)` (0x826AA4B0 /
  // 0x823BF518: 0 for hero two's component, else comp33+0x1A); then the safety setter
  // `sub_823E0788(comp33, !sub_821EE348(comp33))` (0x826AA4C4 / 0x826AA4DC; 0x823BF150, 0x821EE210),
  // which does nothing on no change and else stores comp33+0x19, raises tutorial 86 on the way off,
  // pushes the network state and plays SE_SAFETY_ON / SE_SAFETY_OFF. Safety is hero one's alone:
  // player two's wheel only closes.
  const Scratch s = Claim(ctx);
  if (player != 1u) {
    REXLOG_INFO("Refii guest-tick pump: SafetyToggle:{} — safety is hero one's alone, refused", player);
    return true;
  }
  const uint32_t game = Game(base);
  const uint32_t holder = Plausible(game) ? LoadU32(base, game + 0x58u) : 0u;
  const uint32_t world = Plausible(holder) ? LoadU32(base, holder + 4u) : 0u;
  const uint32_t hero = Plausible(world) ? Call(ctx, base, s.frame, 0x8222C1B8u, {LoadU32(base, world)}) : 0u;
  const uint32_t comp33 =
      Plausible(hero) && (base[hero + 0x90u] & 0x40u) != 0u ? refii_mod_component(base, hero, 33u) : 0u;
  if (comp33 == 0u || (Call(ctx, base, s.frame, 0x823E0B50u, {comp33}) & 0xFFu) == 0u) {
    REXLOG_INFO("Refii guest-tick pump: SafetyToggle:{} — {}", player,
                comp33 == 0u ? "no live hero one, dropped" : "the guest's gate (sub_823E0B50) refuses it");
    return true;
  }
  const uint32_t on = (Call(ctx, base, s.frame, 0x821EE348u, {comp33}) & 0xFFu) == 0u ? 1u : 0u;
  Call(ctx, base, s.frame, 0x823E0788u, {comp33, on});
  REXLOG_INFO("Refii guest-tick pump: SafetyToggle:{} — safety set {} (sub_823E0788 on comp33 0x{:08X})", player,
              on != 0u ? "ON" : "OFF", comp33);
  return true;
}

bool DispatchTargetQuery(PPCContext& ctx, uint8_t* base, uint32_t player) {
  // The HUD's raw-Y arm, `sub_823FDA78(T, event)` TU1 / disc `sub_8256D1A8`, called by the GUI's raw
  // input router with T = `*(*(G+0x20)+0x70)` (0x8240369C / disc 0x823B48DC). It reads three fields
  // of the event and nothing else (r28 at 0x823FDC68, 0x823FDC74, 0x823FDC90): +0x28 the pad's user,
  // which must be player-map key 1's (`record+0x28`, 0x823FDC64..0x823FDC70) — player one's pad
  // alone; +0x2C 13, 14 or 15 (0x823FDC78..0x823FDC8C); byte +8 == 14, Y (0x823FDC94). Every other
  // gate is the guest's and reads its own state: the HUD shows a target or the hero's use list holds
  // it, the target is alive with component 26 or 65, the hero is alive and standing still; then the
  // card's handler and its opener `sub_826CCC88(h, *(T+4))` (0x823FDD8C). The event here is those
  // three fields with player one's own user, so the target and every gate stay the guest's.
  const Scratch s = Claim(ctx);
  if (player != 1u) {
    REXLOG_INFO("Refii guest-tick pump: TargetQuery:{} — the guest takes player one's pad alone, refused", player);
    return true;
  }
  const uint32_t g = Gui(base);
  const uint32_t hud = Plausible(g) ? LoadU32(base, g + 0x20u) : 0u;
  const uint32_t target = Plausible(hud) ? LoadU32(base, hud + 0x70u) : 0u;
  const uint32_t game = Game(base);
  const uint32_t record =
      Plausible(game) ? Call(ctx, base, s.frame, 0x82264230u, {LoadU32(base, game + 0x9Cu), 1u}) : 0u;
  if (!Plausible(target) || !Plausible(record)) {
    REXLOG_INFO("Refii guest-tick pump: TargetQuery:{} — {}", player,
                !Plausible(target) ? "no HUD target object, dropped" : "no player one on this machine, dropped");
    return true;
  }
  const uint32_t event = s.data;
  std::memset(base + event, 0, 0x40u);
  base[event + 8u] = 14u;
  StoreU32(base, event + 0x28u, LoadU32(base, record + 0x28u));
  StoreU32(base, event + 0x2Cu, 13u);
  Call(ctx, base, s.frame, 0x823FDA78u, {target, event});
  REXLOG_INFO("Refii guest-tick pump: TargetQuery:{} — sub_823FDA78 ran on HUD target 0x{:08X}; the stated screen "
              "is now {}",
              player, target, refii_bridge_stated_screen_now());
  return true;
}

}  // namespace refii::vfs
