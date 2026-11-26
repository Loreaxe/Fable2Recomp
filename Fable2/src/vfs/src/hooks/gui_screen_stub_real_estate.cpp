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
PPC_EXTERN_FUNC(rex_lhGuiRealEstateScreen_OnMenuEvent);

// ---- REAL ESTATE (28; disc 27) — vtable 0x820CEA58 / 0x820C1654 ------------------------------
//
// h+24 the row list (vt 0x820EB114: +4 `sub_82998300` / 0x828ECC10, the offered options — each row
// `sub_826CE848(list, &textTag, icon, "Root", 0, event, pres, 0)` / 0x82408338, its id 0 and its
// identity the EVENT r8, 110..120; +8 a nullsub), h+44 the book, h+88 page 1 (vt 0x820EB80C: +4 the
// fill `sub_82998FB0`, +12 `sub_8299A728`, the rent / profit line restated), h+116 page 2, h+144..h+168
// the binding `sub_826B1A08` fills at OnEnter (hero, its comp69 and comp77, the sale sign, its comp142,
// the building, its comp67). Same offsets on both builds. No slot body is shared with another vtable.
//
// A row's name (String1) is its LOCALISED label cut to one byte a character (`sub_826CE848`:
// `sub_8230E0E8` localises the tag, `sub_82B3F898` / 0x82B398A8 keeps each unit's low byte, 0x82B3F904 /
// 0x82B3F910), so it is no identity outside Latin text (GitHub #81). The handler never reads it:
// OnMenuEvent takes the event's type alone (`lwz r4, 4(r4)` 0x826B2224 / 0x825974D4), and the row's
// lever carries that.
//
// Four slots stay the guest's. OnEnter (TU1 0x826B18D0 / disc 0x82596B80) is the pause root's owner,
// the book and its pages' fills, the rows and the binding around one open sound and one slide.
// OnMenuEvent (0x826B2210 / 0x825974C0) and OnModalAnswer (0x826B25E0 / 0x82597880) are the decision
// tree with the guest's own boxes — the confirms 29..34, the buy-denied notice, the numeric boxes 35..37
// with the limits `sub_826B1FC8` states — and every consequence; both run under the screen's household
// wrappers (mod_events.cpp). OnExit (0x826B19D0 / 0x82596C80) is the base exit and the pages' release.

namespace {

constexpr uint32_t kRealEstateVtable = 0x820CEA58u;

bool g_real_estate_update_said = false;

}  // namespace

namespace refii::vfs {

// Update TU1 0x826B1EF0 / disc 0x825971A0, run by its household wrapper (mod_events.cpp
// rex_lhGuiRealEstateScreen_Update) with r3 the handler. Kept: the book's tick `sub_82990730(h+44)`
// (0x826B1F0C / 0x828CE180) — the standing page's vf+12, on page 1 `sub_8299A728`, the one restatement
// of the rent / profit line after a numeric box commits — and the gold text `sub_8246B5E8(pres,
// 0x8349AE3C, 1)` (0x826B1F2C / 0x82837218 with 0x83498CF8). Dropped: the child tick `sub_826D5D00`
// (0x826B1F04; this list's vf+8 is a nullsub). Godot's page numbers are stated here, from the handler's
// binding, each Update the guest runs (PublishBoundRealEstate).
void RealEstateScreenUpdate(PPCContext& ctx, uint8_t* base) {
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  Call(ctx, base, s.frame, 0x82990730u, {h + 0x2Cu});
  Call(ctx, base, s.frame, 0x8246B5E8u, {BookPresentation(base), Address(0x8349AE3Cu), 1u});
  RestatePurseForPlayerTwo(ctx, base, s.frame, BookPresentation(base), 0x8349AE3Cu);
  PublishBoundRealEstate(ctx, base, h);
  if (!g_real_estate_update_said) {
    g_real_estate_update_said = true;
    Said("real estate (28)", "Update", h, LoadU32(base, h + 0x90u), LoadU32(base, h + 0xA4u));
  }
}

}  // namespace refii::vfs

// OnScreenAction TU1 0x826B2A18 / disc 0x82597CB8 (no player test; jump table 0x826B2A50 / 0x82597CF0,
// index code − 26, read from the image): 26 / 27 the open page's input (behind h+84, `sub_829907F0`
// 0x826B2B08), 36 a sound and the Lua OnPressA helper `sub_826D58F0` (0x826B2AA8), 37
// `ExitScreen(QM, 1)` (0x826B2AD0). Godot posts a row's A as `RealEstate:<p>:event:<110..120>`
// (DispatchRealEstateEvent) and B as `RealEstate:<p>:close` (DispatchRealEstateClose). Nothing kept.
REX_EXTERN(rex_lhGuiRealEstateScreen_OnScreenAction) {
  REX_FUNC_PROLOGUE();
  Said("real estate (28)", "OnScreenAction", ctx.r3.u32, ctx.r4.u32, ctx.r5.u32);
}

// ---- THE LEVERS -----------------------------------------------------------------------------

namespace refii::vfs {

bool DispatchRealEstateEvent(PPCContext& ctx, uint8_t* base, uint32_t player, uint32_t type) {
  // A row's commit, by the event its spawn stated (`sub_826CE848` r8): the handler's own OnMenuEvent
  // on `{name, type, id}` with that type — 110 sell, 111 buy, 112 rent out, 113 / 114 redecorating on
  // / off, 115 budget, 116 marital home, 117 rent, 118 profits, 119 evict, 120 back (jump table
  // 0x826B2250 / 0x82597500, read from the image). Its name is left empty and its id 0: neither is
  // read. The confirm, the numeric box and the consequence are the guest's.
  const Scratch s = Claim(ctx);
  const uint32_t h = ScreenHandler(ctx, base, s.frame, 28u, kRealEstateVtable);
  if (h == 0u || type < 110u || type > 120u) {
    REXLOG_INFO("Refii guest-tick pump: RealEstate:{}:event:{} — {}", player, type,
                h == 0u ? "no real-estate handler, dropped" : "not a real-estate row's event (110..120), dropped");
    return true;
  }
  const uint32_t event = s.data;
  MakeString(ctx, base, s.frame, event, s.data + 0x40u, "");
  StoreU32(base, event + 4u, type);
  StoreU32(base, event + 8u, 0u);
  {
    PPCContext work = ctx;
    work.r1.u32 = s.frame;
    work.r3.u32 = h;
    work.r4.u32 = event;
    rex_lhGuiRealEstateScreen_OnMenuEvent(work, base);
  }
  DropString(ctx, base, s.frame, event);
  REXLOG_INFO("Refii guest-tick pump: RealEstate:{}:event:{} — the handler's own answer ran (buyer 0x{:08X}, "
              "building 0x{:08X})",
              player, type, LoadU32(base, h + 0x90u), LoadU32(base, h + 0xA4u));
  return true;
}

bool DispatchRealEstateClose(PPCContext& ctx, uint8_t* base, uint32_t player) {
  // The handler's B, any player's (0x826B2AC0 / disc 0x82597D60): `ExitScreen(QM, 1)`.
  const Scratch s = Claim(ctx);
  const uint32_t h = ScreenHandler(ctx, base, s.frame, 28u, kRealEstateVtable);
  if (h == 0u) {
    REXLOG_INFO("Refii guest-tick pump: RealEstate:{}:close — no real-estate handler, dropped", player);
    return true;
  }
  Call(ctx, base, s.frame, 0x8236ED98u, {LoadU32(base, Gui(base) + 28u), 1u});
  REXLOG_INFO("Refii guest-tick pump: RealEstate:{}:close — the screen left", player);
  return true;
}

}  // namespace refii::vfs
