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

PPC_EXTERN_FUNC(rex_lhGuiMenu_GetHighlightedItemData);

// Local player two's shop (coop_hero_hooks.cpp): the shop's opener states no owner for it.
extern "C" int RefiiCoopShoppingPlayerTwo(void);

using namespace refii::vfs::stub;
using refii::guest::Address;
using refii::mods::LoadU32;
using refii::mods::StoreU32;

// ---- THE SHOP (22; disc 21) — vtable 0x820CE708 / 0x820C11F8 --------------------------------
//
// h+24 the row list (vt 0x820EAF1C / 0x820E4B54: +4 `lhGuiShopScreen_PopulateMenu` 0x82994218 /
// 0x828DF1A8, the rows AND the only caller of the shop's stock fill), h+44 the book, pages h+88 (front) /
// h+128 (item) / h+368 (stats), h+396 the ticket of the row in a pending confirm, h+400 the keeper's weak
// handle (its entity `**(h+404)`), h+408 the last highlighted event type, h+440 "greeting pending", h+441
// "repopulate pending after a transaction", h+444 the rows transacted this visit. Same offsets on both
// builds.
//
// Four slots stay the guest's. OnEnter (TU1 0x826ADDC0 / disc 0x82592340) is all statement and state —
// PopulateMenu, the transacted map's erase, the book and its pages' readers, the titles-shop header, the
// greeting flag — around one open sound. OnMenuEvent (0x826AE5C0 / 0x82592AE0) and OnModalAnswer
// (0x826AE7E8 / 0x82592D08) are the buy and sell decision trees with the guest's own boxes (0, 16..22),
// run on the row Godot pushes by its ticket (`MenuEvent`, types 30 / 31). OnExit (0x826AE090 /
// 0x82592610) releases the preview and applies the visit's wealth drift and title announcement,
// `sub_8296AF98(shop)`.

namespace {

constexpr uint32_t kShopVtable = 0x820CE708u;

bool g_shop_update_said = false;

}  // namespace

// Update TU1 0x826AE270 / disc 0x825927F0. Kept, in the guest's order: the keeper's greeting, once per
// entry — h+440 set → cleared, the keeper `**(h+404)` (`lhSmartPtr_Deref(h+400)` 0x826AE2A0), its
// component 27, `CECShopkeeper_Greet(&key, comp27)` (0x826AE3BC; 0x828731A8 / 0x827AFF90) and, for a key
// that is not empty (`sub_8229B1B8(&key, "")` 0x826AE3CC / 0x8229AD78), the keeper's line
// `sub_826B1558(h, copy of key)` (0x826AE3F0 / 0x82595A58); the highlighted row
// (`lhGuiMenu_GetHighlightedItemData` 0x826AE464, answered natively from the row Godot states), copied
// (`sub_826AE560` 0x826AE470 / 0x8257D1F0) and handed to the highlight edge `sub_826B13B8(h, &row)`
// (0x826AE47C / 0x825958B8: h+408, the item page, price and stats fills, the preview's Set, the keeper's
// "cannot afford" line), which releases the copy; the gold text `sub_8246B5E8(pres, 0x8349ADD8, 1)`
// (0x826AE4E8 / 0x82837218 with 0x83498C94); the deferred repopulate — h+441 set, the highlight a header
// (type 301) and no box up (`QM+40 == 1`, 0x826AE520) → h+441 = 0, the list's vf+4 (PopulateMenu) and the
// transacted map cleared `sub_822F8640(h+444)` (0x826AE544 / 0x82367A90). Dropped: the child tick
// `sub_826D5D00` (0x826AE280), the book's slide and tick `sub_82990A98` / `sub_82990730` (0x826AE4B0 /
// 0x826AE4B8) and the stats page's tick h+368 vf+12 (0x826AE4CC).
REX_EXTERN(rex_lhGuiShopScreen_Update) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  const uint32_t key = s.data;            // the greeting's key
  const uint32_t said = s.data + 4u;      // its copy, consumed by the line
  const uint32_t row = s.data + 0x10u;    // {type, id, String1, String2}
  const uint32_t copy = s.data + 0x20u;   // the edge's own copy of the row
  const uint32_t g = Gui(base);
  const uint32_t qm = LoadU32(base, g + 28u);
  const uint32_t pres = BookPresentation(base);

  if (base[h + 0x1B8u] != 0u) {
    base[h + 0x1B8u] = 0u;
    const uint32_t holder = LoadU32(base, h + 0x194u);
    const uint32_t keeper = Plausible(holder) ? LoadU32(base, holder) : 0u;
    const uint32_t comp27 = Plausible(keeper) ? refii_mod_component(base, keeper, 27u) : 0u;
    if (comp27 != 0u) {
      StoreU32(base, key, 0u);
      Call(ctx, base, s.frame, 0x828731A8u, {key, comp27});
      if ((Call(ctx, base, s.frame, 0x8229B1B8u, {key, Address(0x82000CA0u)}) & 0xFFu) != 0u) {
        StoreU32(base, said, 0u);
        const uint32_t line = Call(ctx, base, s.frame, 0x821F0240u, {said, key});
        Call(ctx, base, s.frame, 0x826B1558u, {h, line});
        REXLOG_INFO("Refii screen stub: shop (22) — the keeper's greeting ran");
      }
      DropString(ctx, base, s.frame, key);
    }
  }

  StoreU32(base, row, 0u);
  StoreU32(base, row + 4u, 0u);
  MakeString(ctx, base, s.frame, row + 8u, s.data + 0x40u, "");
  MakeString(ctx, base, s.frame, row + 12u, s.data + 0x40u, "");
  {
    PPCContext ask = ctx;
    ask.r1.u32 = s.frame;
    ask.r3.u32 = LoadU32(base, qm + 60u);
    ask.r4.u32 = row;
    rex_lhGuiMenu_GetHighlightedItemData(ask, base);
  }
  const uint32_t type = LoadU32(base, row);
  Call(ctx, base, s.frame, 0x826B13B8u, {h, Call(ctx, base, s.frame, 0x826AE560u, {copy, row})});
  Call(ctx, base, s.frame, 0x8246B5E8u, {pres, Address(0x8349ADD8u), 1u});
  RestatePurseForPlayerTwo(ctx, base, s.frame, pres, 0x8349ADD8u, RefiiCoopShoppingPlayerTwo() != 0);
  if (base[h + 0x1B9u] != 0u && type == 301u && LoadU32(base, qm + 40u) == 1u) {
    base[h + 0x1B9u] = 0u;
    CallSlot(ctx, base, s.frame, h + 24u, 1u, {});
    Call(ctx, base, s.frame, 0x822F8640u, {h + 0x1BCu});
    REXLOG_INFO("Refii screen stub: shop (22) — repopulated after a transaction");
  }
  DropString(ctx, base, s.frame, row + 12u);
  DropString(ctx, base, s.frame, row + 8u);
  if (!g_shop_update_said) {
    g_shop_update_said = true;
    Said("shop (22)", "Update", h, type, LoadU32(base, row + 4u));
  }
}

// OnScreenAction TU1 0x826AE760 / disc 0x82592C80 (jump table index code − 26): 26 / 27 the book's page
// turn (behind h+84), 36 Lua OnPressA + sound (`sub_826D58F0`), 37 B `sub_826D5A60` — `ExitScreen(QM, 1)`
// at the list's root (0x826D5AA4), else the fold's own back. Godot posts B as `Shop:<p>:close`
// (DispatchShopClose). Nothing kept.
REX_EXTERN(rex_lhGuiShopScreen_OnScreenAction) {
  REX_FUNC_PROLOGUE();
  Said("shop (22)", "OnScreenAction", ctx.r3.u32, ctx.r4.u32, ctx.r5.u32);
}

// ---- THE LEVERS -----------------------------------------------------------------------------

namespace refii::vfs {

bool DispatchShopClose(PPCContext& ctx, uint8_t* base, uint32_t player) {
  // The handler's B, the guest's own helper `sub_826D5A60(h)` / 0x825832B0 whole (the jump table's case
  // 37 is `b sub_826D5A60`): at the list's root (`sub_826C1D98` 0x826D5A88, answered from Godot's fold)
  // `ExitScreen(QM, 1)` (0x826D5AA4), whose OnExit runs `sub_8296AF98(shop)`; else the fold's own back
  // `sub_826D59C8` (0x826D5AB0).
  const Scratch s = Claim(ctx);
  const uint32_t h = ScreenHandler(ctx, base, s.frame, 22u, kShopVtable);
  if (h == 0u) {
    REXLOG_INFO("Refii guest-tick pump: Shop:{}:close — no shop handler, dropped", player);
    return true;
  }
  Call(ctx, base, s.frame, 0x826D5A60u, {h});
  REXLOG_INFO("Refii guest-tick pump: Shop:{}:close — the guest's B ran (sub_826D5A60)", player);
  return true;
}

}  // namespace refii::vfs
