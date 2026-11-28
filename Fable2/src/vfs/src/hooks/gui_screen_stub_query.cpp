// THE PORTED SCREENS (S7). Each handler slot below is a full replacement: it keeps the guest's
// statements of the screen's data and its game side effects, as direct calls, and drops what
// only draws or animates. Godot drives the rest through the levers at the end of each file
// (guest_tick_pump.cpp dispatches them). The handler objects stay registered.
//
// Slot layout (vtable): +8 OnEnter, +16 Update, +20 OnMenuEvent(h, event), +24
// OnScreenAction(h, code, player), +36 OnExit. TU1 / disc addresses on each function.
#include <atomic>
#include <cstdint>
#include <initializer_list>
#include <string>

#include <rex/logging.h>

#include "core/guest_build.h"
#include "core/refii_mod.h"
#include "hooks/gui_screen_stub_support.h"
#include "hooks/gui_screen_stubs.h"
#include "hooks/ppc_externs.h"
#include "vfs_hooks_private.h"

using namespace refii::vfs::stub;
using refii::guest::Address;
using refii::mods::LoadU32;
using refii::mods::StoreU32;

// ---- THE VILLAGER CARD (10, both builds) — vtable 0x820CF944 / 0x820C075C --------------------
//
// h+24 "open", h+28 the target entity, h+32 "keyboard pending", h+34 the keyboard's 17-unit buffer,
// h+68 / h+72 the keyboard's title and description, h+76 its XOVERLAPPED. The card has no OnEnter of
// its own (+8 is a body shared by ten vtables, 0x82C8D1A0 / 0x82583480, never replaced) and no
// OnMenuEvent: its opener `sub_826CCC88(h, entity)` / 0x825839B0 states everything it shows — six
// captions, the name line, the rename prompt's slide, traits and meters — through the text and float
// registries, once.
//
// Update (TU1 0x826CC9A8 / disc 0x825836D0) is the rename itself, ported below. OnExit (0x826CC9A0 / 0x825836C8) is `b sub_826CCC10`, the close helper, ported
// (gui_screen_stub_shared.cpp).
//
// The keyboard the rename asks for is the platform's, and the host is the platform: keyboard_ui.cpp.

namespace {

constexpr uint32_t kQueryVtable = 0x820CF944u;

}  // namespace

// Update TU1 0x826CC9A8 / disc 0x825836D0. Kept, the rename whole: while h+32 is set (`lbz r11,0x20(r30)`
// 0x826CC9BC) and the XOVERLAPPED h+76 has left 997 (`cmplwi r11,0x3E5` 0x826CC9D0),
// `XGetOverlappedResult(h+76, 0, 1)` (0x826CC9E0 / disc 0x82CBC558); on 0, the text registry
// `(*(*(*(G+32)+32)+28))[6]` (0x826CC9F8..0x826CCA24), the target's component 20 (the inline lookup on
// `*(h+28)`, 0x826CCA2C..0x826CCB1C) and, when it is renameable (`sub_823A3C40(comp20)` 0x826CCB38 / disc
// `sub_824321F8`): the keyboard's buffer as a string `lhWideString_CtorFromWStr(&name, h+34)` (0x826CCB50),
// written as the creature's explicit name `lhCharString_AssignIfDifferent(comp20+52, &name)` (0x826CCB5C),
// the hero told `Lua_Creature_InformPlayerOfNameChange(comp20)` (0x826CCB64 / 0x82436748), and the name
// line restated — `sub_823C8510(&name, *(h+28))` (0x826CCB78 / disc `sub_828C8440`) into
// `lhGuiRegistry_SetText(registry, *(0x8349B108), text)` (0x826CCB8C / disc `sub_8257C720` with
// 0x83498ADC); then h+32 = 0 whatever the result (0x826CCB98). Dropped: the base child tick
// `sub_826D5D00(h)` (0x826CC9B8 / 0x82583560: each child's vf+12 with 1/30, the row list's vf+8).
REX_EXTERN(rex_lhGuiQueryScreen_Update) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  static std::atomic<bool> said{false};
  if (!said.exchange(true)) {
    Said("villager card (10)", "Update", h, base[h + 0x20u]);
  }
  // On an online joiner the card's opinion block is the host's (coop_villager_use.cpp JoinerCardTick).
  refii::vfs::JoinerCardTick(ctx, base, h);
  if (base[h + 0x20u] == 0u || LoadU32(base, h + 0x4Cu) == 997u) {
    return;
  }
  const Scratch s = Claim(ctx);
  const uint32_t name = s.data;  // the new name, then the entity's display name
  const uint32_t result = Call(ctx, base, s.frame, 0x82CC2D80u, {h + 0x4Cu, 0u, 1u});
  bool renamed = false;
  if (result == 0u) {
    const uint32_t registries = LoadU32(base, LoadU32(base, Gui(base) + 32u) + 32u);
    const uint32_t begin = LoadU32(base, registries + 28u);
    const uint32_t end = LoadU32(base, registries + 32u);
    const uint32_t registry = Plausible(begin) && ((end - begin) >> 2) > 6u ? LoadU32(base, begin + 24u) : 0u;
    const uint32_t target = LoadU32(base, h + 28u);
    const uint32_t comp20 = Plausible(target) ? refii_mod_component(base, target, 20u) : 0u;
    if (registry != 0u && comp20 != 0u && (Call(ctx, base, s.frame, 0x823A3C40u, {comp20}) & 0xFFu) != 0u) {
      StoreU32(base, name, 0u);
      Call(ctx, base, s.frame, 0x822D6408u, {name, h + 0x22u});
      Call(ctx, base, s.frame, 0x822B88A8u, {comp20 + 0x34u, name});
      Call(ctx, base, s.frame, 0x823A81D8u, {comp20});
      Call(ctx, base, s.frame, 0x821C67F8u, {name});
      const uint32_t text = Call(ctx, base, s.frame, 0x823C8510u, {name, target});
      Call(ctx, base, s.frame, 0x823FEB28u, {registry, LoadU32(base, Address(0x8349B108u)), text});
      Call(ctx, base, s.frame, 0x821C67F8u, {name});
      renamed = true;
    }
  }
  base[h + 0x20u] = 0u;
  Said("villager card (10)", "Update (the keyboard answered)", h, result, renamed ? 1u : 0u);
}

// OnScreenAction TU1 0x826CCBA8 / disc 0x825838D0, any player's: nothing while the keyboard is
// pending (`lbz r11, 0x20(r3); bne` 0x826CCBB4); 37 B the close helper `sub_826CCC10(h)` and
// `ExitScreen(QM, 1)` (0x826CCBE4 / 0x826CCBF8); 38 Y the keyboard request `sub_826CDCD8(h)`
// (0x826CCBD0). Godot posts them as `Query:<p>:close` and `Query:<p>:rename` (DispatchQuery). Nothing
// kept.
REX_EXTERN(rex_lhGuiQueryScreen_OnScreenAction) {
  REX_FUNC_PROLOGUE();
  Said("villager card (10)", "OnScreenAction", ctx.r3.u32, ctx.r4.u32, ctx.r5.u32);
}

// ---- THE LEVERS -----------------------------------------------------------------------------

namespace refii::vfs {

bool DispatchQuery(PPCContext& ctx, uint8_t* base, uint32_t player, const std::string& verb) {
  const Scratch s = Claim(ctx);
  const uint32_t h = ScreenHandler(ctx, base, s.frame, 10u, kQueryVtable);
  if (h == 0u || base[h + 0x20u] != 0u) {
    REXLOG_INFO("Refii guest-tick pump: Query:{}:{} — {}", player, verb,
                h == 0u ? "no card handler, dropped" : "the keyboard is pending, the card takes no button");
    return true;
  }
  if (verb == "rename") {
    // Y: the guest's own request `sub_826CDCD8(h)` / 0x82584A00 whole — a target that cannot be renamed
    // (`sub_823A3C40(comp20)` 0) asks for nothing; else the current name cut to 16 units into h+34, the
    // title and description, and `XamShowKeyboardUI(user of player-map key 1, 0x20000004, h+34, title,
    // description, h+34, 17, h+76)` through its thunk (0x826CDF60); 997 sets h+32.
    Call(ctx, base, s.frame, 0x826CDCD8u, {h});
    REXLOG_INFO("Refii guest-tick pump: Query:{}:rename — sub_826CDCD8 ran, the keyboard is {}", player,
                base[h + 0x20u] != 0u ? "pending" : "not asked for (the target takes no name)");
    return true;
  }
  // B: the close helper `sub_826CCC10(h)` / 0x82583938, ported (gui_screen_stub_shared.cpp: h+24
  // cleared, the card's registration undone), then `ExitScreen(QM, 1)`.
  Call(ctx, base, s.frame, 0x826CCC10u, {h});
  Call(ctx, base, s.frame, 0x8236ED98u, {LoadU32(base, Gui(base) + 28u), 1u});
  REXLOG_INFO("Refii guest-tick pump: Query:{}:close — the card closed, the screen left", player);
  return true;
}

}  // namespace refii::vfs
