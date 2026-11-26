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

// ---- THE WORLD MAP (23; disc 22) — vtable 0x820CF704 / 0x820C1F78 ----------------------------
//
// h+24 the row list (`lhGuiWorldMapScreen_PopulateMenu` 0x829A2F40 / 0x828F3EC0), h+44 the book and its
// six pages (h+88 region info, h+128 region minimap, h+256 quest, h+284 job, h+312 sale, h+340 family),
// h+84 / h+85 / h+384 / h+388 the book's page state (read by Update and OnScreenAction alone), h+392 "the
// region's stop chooser is up", h+396 the last highlighted row id, h+400 the category the opener asked
// for. Same offsets on both builds.
//
// Four slots stay the guest's. OnEnter (TU1 0x826C5B00 / disc 0x825A1A98) is the book's registration, the
// hero's component 250 call, PopulateMenu, the tab strip, the asked category's emphasis, tutorial 147 and
// the gold text, around one open sound. OnMenuEvent (0x826C6110 / 0x825A20A8) and OnModalAnswer
// (0x826C6580 / 0x825A2518) are the map's decision tree with the guest's own boxes 3..13 — make primary,
// trails, fast travel and its refusals, the stop chooser — run on the rows Godot pushes (`MenuEvent`,
// `Quest:select`, `Sale:select`) and answered by `QueryAnswer` / `MenuBoxAnswer`. OnExit (0x826C5AC8 /
// 0x825A1A60) tears the book down.

namespace {

bool g_world_map_update_said = false;

}  // namespace

// Update TU1 0x826C5E70 / disc 0x825A1E08. Kept: the highlighted row (`lhGuiMenu_GetHighlightedItemData`
// 0x826C5EF0, answered natively from the row Godot states) and h+396 = its id on a change (0x826C5F78);
// on a quest row (type 42) with tutorials on (`*(*(GAME+0xA8)+4)+0x39`) tutorial 148,
// `lhTutorials_Display(state, 148)` (`sub_826C6698` case 42, 0x826C67AC..0x826C67BC / disc 0x825A2754);
// THE STOP CHOOSER'S POLL while h+392 is set (0x826C6078..0x826C60F0) — the menu box controller
// `*(QM+56)`: once it is shut (+12 clear and its close timer +4 not above 0.0), h+392 = 0 and, cancelled
// (+13), `sub_8236FE38(QM, 4)`, else `sub_8236FEC8(QM, 4)` and the chosen stop's travel
// `sub_826C8C98(h, box)` (0x826C60F0 / disc 0x825A4BC8). Dropped: the child tick `sub_826D5D00`
// (0x826C5E80); the pages' scroll resets `sub_828C3358` on a new row (0x826C5F54 / 0x826C5F74); the
// book's page state for a region row against a category row (0x826C5F90..0x826C5FF4), the pages' show /
// hide `sub_826C6840` / `sub_826C6698`, the book's tick `sub_82990730` and slide `sub_82990A98`
// (0x826C5FF8 / 0x826C6038) — the page fills write presentation elements from the same providers the
// pump samples for Godot's pages.
REX_EXTERN(rex_lhGuiWorldMapScreen_Update) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  uint32_t type = 0u;
  uint32_t id = 0u;
  HighlightedRow(ctx, base, s.frame, s.data, s.data + 0x40u, &type, &id);
  // The kept OnEnter states hero one's gold (0x826C5E38 `li r5,1` / disc 0x825A1DD0); player two's purse.
  RestatePurseForPlayerTwo(ctx, base, s.frame, BookPresentation(base), 0x8349B0E4u);
  if (id != LoadU32(base, h + 0x18Cu)) {
    StoreU32(base, h + 0x18Cu, id);
  }
  if (type == 42u) {
    const uint32_t tutorials = Tutorials(base);
    const uint32_t state = Plausible(tutorials) ? LoadU32(base, tutorials + 4u) : 0u;
    if (Plausible(state) && base[state + 0x39u] != 0u) {
      Call(ctx, base, s.frame, 0x821B5260u, {state, 148u});
    }
  }
  if (LoadU32(base, h + 0x188u) != 0u) {
    const uint32_t qm = LoadU32(base, Gui(base) + 28u);
    const uint32_t box = LoadU32(base, qm + 56u);
    const bool standing =
        !Plausible(box) || base[box + 12u] != 0u || refii::mods::LoadF32(base, box + 4u) > 0.0f;
    if (!standing) {
      StoreU32(base, h + 0x188u, 0u);
      if (base[box + 13u] != 0u) {
        Call(ctx, base, s.frame, 0x8236FE38u, {qm, 4u});
        REXLOG_INFO("Refii screen stub: world map (23) — the stop chooser was cancelled");
      } else {
        Call(ctx, base, s.frame, 0x8236FEC8u, {qm, 4u});
        Call(ctx, base, s.frame, 0x826C8C98u, {h, box});
        REXLOG_INFO("Refii screen stub: world map (23) — the chosen stop's travel ran (sub_826C8C98)");
      }
    }
  }
  if (!g_world_map_update_said) {
    g_world_map_update_said = true;
    Said("world map (23)", "Update", h, type, id);
  }
}

// OnScreenAction TU1 0x826C63C8 / disc 0x825A2360 (jump table index code − 26): 26 / 27 the page's
// scroll (behind h+84), 36 Lua OnPressA + sound; B (37: `sub_826C1D98` → `sub_826B8130(*(G+76))`, then
// `sub_826D5AF0(h)`, 0x826C6508) and LB / RB (72 / 73, player one, 0x826C64E0 / 0x826C64C4) are the spine
// calls Godot posts as `PauseBack` / `PauseCycle:<p>:…`. Nothing kept.
REX_EXTERN(rex_lhGuiWorldMapScreen_OnScreenAction) {
  REX_FUNC_PROLOGUE();
  Said("world map (23)", "OnScreenAction", ctx.r3.u32, ctx.r4.u32, ctx.r5.u32);
}
