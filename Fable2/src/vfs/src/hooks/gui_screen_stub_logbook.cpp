// THE PORTED SCREENS (S7). Each handler slot below is a full replacement: it keeps the guest's
// statements of the screen's data and its game side effects, as direct calls, and drops what
// only draws or animates. Godot drives the rest through the levers at the end of each file
// (guest_tick_pump.cpp dispatches them). The handler objects stay registered.
//
// Slot layout (vtable): +8 OnEnter, +16 Update, +20 OnMenuEvent(h, event), +24
// OnScreenAction(h, code, player), +36 OnExit. TU1 / disc addresses on each function.
#include <algorithm>
#include <cctype>
#include <cstdint>
#include <cstring>
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

namespace {

/// The logbook opened on another player (`sub_823C48D8`, the orb's View Stats): 0 not, 1 opened
/// elsewhere, 2 opened over the world map (its B returns there). Stated by the opener's hook.
int g_logbook_orb = 0;

bool g_logbook_update_said = false;

}  // namespace

int refii::vfs::stub::LogbookOrbMode() { return g_logbook_orb; }

namespace refii::vfs {

void StateLogbookOpenedOnPlayer(bool over_world_map) { g_logbook_orb = over_world_map ? 2 : 1; }

}  // namespace refii::vfs

// ---- THE LOGBOOK (17; disc 16) — vtable 0x820D0110 / 0x820C04C4 ----------------------------
//
// OnEnter TU1 0x826D8DD8 / disc 0x82581A68. Kept, in the guest's order: the change detectors
// h+96/h+100 = -1; orb mode (h+4 == 2): the `LogbookMenuOrb` repopulate (0x826D8E50) and the stats
// fill `sub_829BAEB8(h+384, h+24)` (0x826D8E68 / disc 0x828D4298); else the tab strip
// `sub_826B8348(*(G+76), 7)` (0x826D8E88 / 0x8240B218), the hero (`lhEntity_GetModel` /
// `GetRenderObject` on h+4) into h+132/+280/+316, the stats fill on hero component 57 + 12 when
// present (0x826D9050), and the eight page objects pushed into the page vector (`sub_82B49750` /
// `sub_8228A900`); then the page registration `sub_82990610(h+52, &pages, 0x8349B454, pres, 1)`
// (0x826D90D8 / 0x828CE060 with 0x83498A3C), h+93 = 0, the row population h+32 vf+4
// (`sub_829B8648` / 0x828D1A28), the "Dog" row ghost `sub_823DF660(17, "Dog", !sub_826D99C8(h), 0)`
// (0x826D9128 / 0x8283F948, id 16 on disc) and the gold text `sub_8246B5E8(pres, 0x8349B458, 1)`
// (0x826D914C / 0x82837218 with 0x83498A40). Dropped: the open sound, vf+4 (0x826D8DF4).
REX_EXTERN(rex_lhGuiLogbookScreen_OnEnter) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  const uint32_t pages = s.data;          // {?, begin, end, capacity}
  const uint32_t item = s.data + 0x10u;   // the page pointer handed to the push
  const uint32_t text = s.data + 0x20u;   // a CCharString handle and its characters
  const uint32_t g = Gui(base);
  const uint32_t pres = BookPresentation(base);
  g_logbook_update_said = false;

  StoreU32(base, h + 96u, 0xFFFFFFFFu);
  StoreU32(base, h + 100u, 0xFFFFFFFFu);
  for (uint32_t i = 0; i < 16u; i += 4u) {
    StoreU32(base, pages + i, 0u);
  }
  const auto push = [&](uint32_t page) {
    StoreU32(base, item, page);
    Call(ctx, base, s.frame, 0x82B49750u, {pages, item});
  };

  uint32_t last = 0u;
  if (LoadU32(base, h + 4u) == 2u) {
    RunMenuLua(ctx, base, s.frame, text, "g_Menu:RepopulateMenuFromPopulationScript( 'LogbookMenuOrb' )", pres);
    Call(ctx, base, s.frame, 0x829BAEB8u, {h + 384u, LoadU32(base, h + 24u)});
    last = h + 384u;
  } else {
    Call(ctx, base, s.frame, 0x826B8348u, {LoadU32(base, g + 76u), 7u});
    const uint32_t root = LoadU32(base, Address(0x83496AB8u));
    const uint32_t game = Plausible(root) ? LoadU32(base, root + 12u) : 0u;
    const uint32_t holder = Plausible(game) ? LoadU32(base, game + 88u) : 0u;
    const uint32_t party = Plausible(holder) ? LoadU32(base, holder + 4u) : 0u;
    const uint32_t roster = Plausible(party) ? LoadU32(base, party) : 0u;
    const uint32_t hero =
        Call(ctx, base, s.frame, LoadU32(base, h + 4u) != 0u ? 0x828E5A78u : 0x8222C1B8u, {roster});
    StoreU32(base, h + 132u, hero);
    push(h + 104u);
    StoreU32(base, h + 280u, hero);
    push(h + 252u);
    StoreU32(base, h + 316u, hero);
    push(h + 288u);
    if (Plausible(hero) && (Load8(base, hero + 144u) & 0x40u) != 0u &&
        ((LoadU32(base, hero + 40u) >> 25) & 1u) != 0u) {
      const uint32_t stats = refii_mod_component(base, hero, 57u);
      Call(ctx, base, s.frame, 0x829BAEB8u, {h + 384u, stats != 0u ? stats + 12u : 0u});
    }
    push(h + 384u);
    push(h + 428u);
    push(h + 464u);
    push(h + 492u);
    last = h + 520u;
  }
  push(last);

  Call(ctx, base, s.frame, 0x82990610u, {h + 52u, pages, Address(0x8349B454u), pres, 1u});
  base[h + 93u] = 0u;
  CallSlot(ctx, base, s.frame, h + 32u, 1u, {});
  MakeString(ctx, base, s.frame, text, text + 0x10u, "Dog");
  const uint32_t dog = Call(ctx, base, s.frame, 0x826D99C8u, {h}) & 0xFFu;
  Call(ctx, base, s.frame, 0x823DF660u, {refii::guest::ScreenId(17u), text, dog == 0u ? 1u : 0u, 0u});
  DropString(ctx, base, s.frame, text);
  Call(ctx, base, s.frame, 0x8246B5E8u, {pres, Address(0x8349B458u), 1u});
  const uint32_t storage = LoadU32(base, pages + 4u);
  if (storage != 0u) {
    Call(ctx, base, s.frame, 0x8221BD38u, {storage});  // Render_FreeD3DObject
  }
  Said("logbook (17)", "OnEnter", h, LoadU32(base, h + 4u));
}

// Update TU1 0x826D9168 / disc 0x82581DF8: the base widget tick and the highlight-keyed page
// fills — drawing, driven by a guest highlight that Godot's cursor never moves. Nothing kept.
REX_EXTERN(rex_lhGuiLogbookScreen_Update) {
  REX_FUNC_PROLOGUE();
  if (!g_logbook_update_said) {
    g_logbook_update_said = true;
    Said("logbook (17)", "Update", ctx.r3.u32);
  }
}

// OnMenuEvent TU1 0x826D9620 / disc 0x825822B0: 140/141 sounds; 150 `XamShowAchievementsUI`
// (0x826D967C), whose row Godot drops (its achievements are a host screen). Nothing kept.
REX_EXTERN(rex_lhGuiLogbookScreen_OnMenuEvent) {
  REX_FUNC_PROLOGUE();
  Said("logbook (17)", "OnMenuEvent", ctx.r3.u32, LoadU32(base, ctx.r4.u32 + 4u), LoadU32(base, ctx.r4.u32 + 8u));
}

// OnScreenAction TU1 0x826D96F0 / disc 0x82582380: A is Lua OnPressA + sound, 26/27 the guest's
// menu cursor; B and LB/RB are spine calls Godot makes as `PauseBack` / `PauseCycle:<p>:…`
// (DispatchPauseBack / DispatchPauseCycle). Nothing kept.
REX_EXTERN(rex_lhGuiLogbookScreen_OnScreenAction) {
  REX_FUNC_PROLOGUE();
  Said("logbook (17)", "OnScreenAction", ctx.r3.u32, ctx.r4.u32, ctx.r5.u32);
}

// OnExit TU1 0x826D9980 / disc 0x82582610: kept whole — the base exit `sub_826D5C30` / 0x82583490
// (frees what OnEnter registered), h+24 = h+28 = h+4 = 0, the page teardown `sub_829912B0(h+52)` /
// 0x828CED00.
REX_EXTERN(rex_lhGuiLogbookScreen_OnExit) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  Call(ctx, base, s.frame, 0x826D5C30u, {h});
  StoreU32(base, h + 24u, 0u);
  StoreU32(base, h + 28u, 0u);
  StoreU32(base, h + 4u, 0u);
  Call(ctx, base, s.frame, 0x829912B0u, {h + 52u});
  g_logbook_orb = 0;
  Said("logbook (17)", "OnExit", h);
}
