// THE PORTED SCREENS (S7). Each handler slot below is a full replacement: it keeps the guest's
// statements of the screen's data and its game side effects, as direct calls, and drops what
// only draws or animates. Godot drives the rest through the levers at the end of each file
// (guest_tick_pump.cpp dispatches them). The handler objects stay registered.
//
// Slot layout (vtable): +8 OnEnter, +16 Update, +20 OnMenuEvent(h, event), +24
// OnScreenAction(h, code, player), +36 OnExit. TU1 / disc addresses on each function.
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

// ---- UPGRADE FURNITURE (26; disc 25) — vtable 0x820CECEC / 0x820C18E8 ------------------------
// ---- THE VILLAGER CARD (10, both builds) — vtable 0x820CF944 / 0x820C075C --------------------
//
// h+24 "open", h+28 the target entity, h+32 "keyboard pending", h+34 the keyboard's 17-unit buffer,
// h+68 / h+72 the keyboard's title and description, h+76 its XOVERLAPPED. The card has no OnEnter of
// its own (+8 is a body shared by ten vtables, 0x82C8D1A0 / 0x82583480, never replaced) and no
// OnMenuEvent: its opener `sub_826CCC88(h, entity)` / 0x825839B0 states everything it shows — six
// captions, the name line, the rename prompt's slide, traits and meters — through the text and float
// registries, once.
//
// Two slots stay the guest's. Update (TU1 0x826CC9A8 / disc 0x825836D0) is the rename itself, kept
// whole with the base child tick it starts with (`sub_826D5D00`, 0x826CC9B8): while h+32 is set and the XOVERLAPPED's result has left 997 (0x826CC9C8), `XGetOverlappedResult`
// (0x826CC9E0) — non-zero clears the latch; zero, with the target still renameable (`sub_823A3C40`
// 0x826CCB38), writes the buffer as the creature's explicit name (comp20+0x34, 0x826CCB5C), tells the
// hero's family record (`sub_823A81D8` 0x826CCB64) and restates the name line (`sub_823FEB28`
// 0x826CCB8C). OnExit (0x826CC9A0 / 0x825836C8) is the close helper `sub_826CCC10`.
//
// The keyboard the rename asks for is the platform's, and the host is the platform: keyboard_ui.cpp.

namespace {

constexpr uint32_t kQueryVtable = 0x820CF944u;

}  // namespace

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
  // B: the close helper `sub_826CCC10(h)` / 0x82583938 (h+24 cleared, the card's registration undone,
  // its close sound), then `ExitScreen(QM, 1)`.
  Call(ctx, base, s.frame, 0x826CCC10u, {h});
  Call(ctx, base, s.frame, 0x8236ED98u, {LoadU32(base, Gui(base) + 28u), 1u});
  REXLOG_INFO("Refii guest-tick pump: Query:{}:close — the card closed, the screen left", player);
  return true;
}

}  // namespace refii::vfs
