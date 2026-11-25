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

// ---- ITEMS / WEAPONS (15; disc 14) — vtable 0x820CFEB0 / 0x820C0250 -------------------------
//
// h+24 the row list (vt 0x820EE0C8 / 0x820E1D7C: +4 the populate `sub_829B0A58(list, 1)` / `sub_828C9B78`),
// h+44 the book, h+85 "details dirty", pages h+96 (description; its 3D view at h+128) / h+336 (stats),
// h+368 "the highlight is not a category row", h+372 the mode the openers write (1 items, 3 weapons, 2
// back from the dye screen), h+376 the mode last entered, h+396 the current item handle, h+416 the last
// highlighted row id, h+420 the augment sub-list (never entered: Godot's augment flow is its own). Same
// offsets on both builds.
//
// OnMenuEvent (TU1 0x826D1800 / disc 0x8257D250), OnModalAnswer (0x826D28B8 / 0x8257E2F0) and OnExit
// (0x826D1618 / 0x8257D008) stay the guest's. The event handler's type 3 arm is the row's A — the swim
// refusal and its box, tutorial 188, `lhGuiInventory_ActivateItemByCategory`, the weapons' repopulate and
// the expression-learned box (0x826D19A8..0x826D1DD8) — a decision tree with the guest's own boxes that
// draws nothing; `Items:<p>:use` runs it on the row Godot names (guest_tick_pump.cpp). Its other arms
// (80 / 81 dye mode, 83 / 85 / 310 / 311 augments, 140 / 141 sounds) are never sent.

namespace {

bool g_items_update_said = false;

}  // namespace

namespace refii::vfs {

// OnEnter TU1 0x826D13D0 / disc 0x8257CDC0, inside the co-op wrapper that lends hero two
// (coop_hero_hooks.cpp). Kept, in the guest's order: mode 2 becomes 1 and is remembered as "back"
// (0x826D13F8); the two pages pushed (`sub_82B49750` / 0x8228A900) and the book `sub_82990610(h+44,
// &pages, 0x8349B298, pres, 1)` (0x826D146C / 0x828CE060 with 0x834988F4), which runs each page's +4; by
// mode the tab strip `sub_826B8348(*(G+76), 4 | 5)` (0x826D1524 / 0x826D14CC; 0x8240B218) and the title's
// tag "GUI_EQUIPMENT_SCREEN_ITEMS" / "_WEAPONS" localised (`sub_8230E0E8` + `sub_82B3F898`), any other
// mode the literal "Unknown Equipment" (0x826D14B8); the header `lhGuiPresentation_SetStringParam(*(G+40),
// "String1", title, pres)` (0x826D1598 / 0x8259D6C8); the gold text `sub_8246B5E8(pres, 0x8349B28C, 1)`
// (0x826D15B4 / 0x82837218 with 0x834988E8); the rows — back: `sub_829B0A58(h+24, 8)` (0x826D15CC /
// 0x828C9B78), else h+24 vf+4 (0x826D15E0); h+416 = -1, h+376 = h+372. Dropped: the open sound vf+4
// (0x826D140C).
void ItemsScreenOnEnter(PPCContext& ctx, uint8_t* base) {
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  const uint32_t pages = s.data;          // {?, begin, end, capacity}
  const uint32_t item = s.data + 0x10u;   // the page pointer handed to the push
  const uint32_t name = s.data + 0x20u;   // "String1"
  const uint32_t tag = s.data + 0x24u;    // the title's tag, consumed by the localiser
  const uint32_t wide = s.data + 0x28u;   // its localised text
  const uint32_t title = s.data + 0x2Cu;  // the text as a CCharString
  const uint32_t g = Gui(base);
  const uint32_t pres = BookPresentation(base);
  g_items_update_said = false;

  const bool back = LoadU32(base, h + 0x174u) == 2u;
  if (back) {
    StoreU32(base, h + 0x174u, 1u);
  }
  for (uint32_t i = 0; i < 16u; i += 4u) {
    StoreU32(base, pages + i, 0u);
  }
  for (uint32_t page : {0x60u, 0x150u}) {
    StoreU32(base, item, h + page);
    Call(ctx, base, s.frame, 0x82B49750u, {pages, item});
  }
  Call(ctx, base, s.frame, 0x82990610u, {h + 0x2Cu, pages, Address(0x8349B298u), pres, 1u});

  const uint32_t mode = LoadU32(base, h + 0x174u);
  StoreU32(base, title, 0u);
  if (mode == 1u || mode == 3u) {
    Call(ctx, base, s.frame, 0x826B8348u, {LoadU32(base, g + 76u), mode == 3u ? 5u : 4u});
    MakeString(ctx, base, s.frame, tag, s.data + 0x60u,
               mode == 3u ? "GUI_EQUIPMENT_SCREEN_WEAPONS" : "GUI_EQUIPMENT_SCREEN_ITEMS");
    const uint32_t said = Call(ctx, base, s.frame, 0x8230E0E8u, {wide, tag});
    Call(ctx, base, s.frame, 0x82B3F898u, {title, said});
    Call(ctx, base, s.frame, 0x821C67F8u, {wide});
  } else {
    MakeString(ctx, base, s.frame, title, s.data + 0x60u, "Unknown Equipment");
  }
  MakeString(ctx, base, s.frame, name, s.data + 0x40u, "String1");
  Call(ctx, base, s.frame, 0x826B9510u, {LoadU32(base, g + 40u), name, title, pres});
  DropString(ctx, base, s.frame, name);

  Call(ctx, base, s.frame, 0x8246B5E8u, {pres, Address(0x8349B28Cu), 1u});
  if (back) {
    Call(ctx, base, s.frame, 0x829B0A58u, {h + 24u, 8u});
  } else {
    CallSlot(ctx, base, s.frame, h + 24u, 1u, {});
  }
  StoreU32(base, h + 0x1A0u, 0xFFFFFFFFu);
  StoreU32(base, h + 0x178u, LoadU32(base, h + 0x174u));
  DropString(ctx, base, s.frame, title);
  const uint32_t storage = LoadU32(base, pages + 4u);
  if (storage != 0u) {
    Call(ctx, base, s.frame, 0x8221BD38u, {storage});  // Render_FreeD3DObject
  }
  Said("items (15)", "OnEnter", h, mode, back ? 1u : 0u);
}

// Update TU1 0x826D1698 / disc 0x8257D088, inside the same wrapper. Kept: the highlighted row
// (`lhGuiMenu_GetHighlightedItemData` 0x826D1718, answered natively from the row Godot states); on a new
// id, the current item handle `sub_82880BC0(*(QM+60)+4, id, h+396)` (0x826D1754 / disc `sub_827BE1E8`),
// h+85 = that handle is set (0x826D1764 / 0x826D1770), the page fills `sub_829B5160(h+96)` and
// `sub_829B6B50(h+336)` (0x826D1778 / 0x826D1780; disc 0x828CF168 / 0x828D0AA0) — the description, the
// price and A-prompt, the preview's Set (`sub_828C3B30`), the stats, and the item's "viewed" byte (`stb
// r25, 0(r30)` 0x829B55BC) — h+416 = the id; h+368 = the row's type is not 301 (0x826D17B0), and on a
// category row the preview's Hide `sub_828C3FD8(h+128)` (0x826D17C0 / 0x82834DA0). Dropped: the child tick
// `sub_826D5D00` (0x826D16A8), the book's slide `sub_82990A98` (0x826D17DC) and tick `sub_82990730`
// (0x826D17E4: the page's spin and the 3D view's draw — Godot turns and draws the preview).
void ItemsScreenUpdate(PPCContext& ctx, uint8_t* base) {
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  uint32_t type = 0u;
  uint32_t id = 0u;
  HighlightedRow(ctx, base, s.frame, s.data, s.data + 0x40u, &type, &id);
  if (id != LoadU32(base, h + 0x1A0u)) {
    const uint32_t qm = LoadU32(base, Gui(base) + 28u);
    Call(ctx, base, s.frame, 0x82880BC0u, {LoadU32(base, qm + 60u) + 4u, id, h + 0x18Cu});
    const uint32_t item = LoadU32(base, h + 0x18Cu);
    base[h + 0x55u] = item != 0u ? 1u : 0u;
    const bool unviewed = Plausible(item) && base[item + 0x14u] == 0u;
    Call(ctx, base, s.frame, 0x829B5160u, {h + 0x60u});
    Call(ctx, base, s.frame, 0x829B6B50u, {h + 0x150u});
    StoreU32(base, h + 0x1A0u, id);
    if (unviewed && base[item + 0x14u] != 0u) {
      refii_bridge_ui_data_changed();  // the fill marked the item viewed: Godot restates its row
    }
  }
  base[h + 0x170u] = type != 301u ? 1u : 0u;
  if (type == 301u) {
    Call(ctx, base, s.frame, 0x828C3FD8u, {h + 0x80u});
  }
  if (!g_items_update_said) {
    g_items_update_said = true;
    Said("items (15)", "Update", h, type, id);
  }
}

}  // namespace refii::vfs

// OnScreenAction TU1 0x826D2560 / disc 0x8257DF98 (jump table index code − 26): 26 / 27 the book and the
// preview's spin, 36 Lua OnPressA + sound, 59 the zoom; B (37) and LB / RB (72 / 73, gated only on the
// augment sub-list h+420 being closed, 0x826D2654 / 0x826D2678) are the spine calls Godot posts as
// `PauseBack` / `PauseCycle:<p>:…`. Nothing kept.
REX_EXTERN(rex_lhGuiItemsScreen_OnScreenAction) {
  REX_FUNC_PROLOGUE();
  Said("items (15)", "OnScreenAction", ctx.r3.u32, ctx.r4.u32, ctx.r5.u32);
}
