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
#include "hooks/page_fills_stated.h"
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
// Every slot but two is ported here. OnExit (TU1 0x826AE090 / disc 0x82592610) stays the guest's: the
// preview's release and the visit's wealth drift and title announcement, `sub_8296AF98(shop)` — game
// logic, no element, no sound. The dtor (0x826ADCA0 / 0x82592220) frees. The buy and sell decision
// trees behind OnMenuEvent and OnModalAnswer (`sub_826AFF48`, `sub_826B0D10`, `sub_826AF430`,
// `sub_826AFB98`, `sub_826AF888`) are the guest's own helpers, called whole, with the guest's own boxes
// (0, 16..22), on the row Godot pushes by its ticket (`MenuEvent`, types 30 / 31).
//
// AN ONLINE JOINER'S SHOP IS THE HOST'S (coop_shop_for_joiner.cpp): its keeper here is a copy with no 26, 27 or
// stock, so where this machine's screen shows the host's statement (JoinerShopStated) the population, the row
// edge and the transactions are the host's run of these same routines — the rows go in where the population
// ran, the highlighted row's asking price is stated as the edge states it, the buy, sale, header and question
// answers go to the host, and the host's boxes open here.

namespace {

constexpr uint32_t kShopVtable = 0x820CE708u;

bool g_shop_update_said = false;

}  // namespace

// OnEnter TU1 0x826ADDC0 / disc 0x82592340. Kept, in the guest's order: the rows and the stock fill,
// h+24 vf+4 = `lhGuiShopScreen_PopulateMenu` (0x826ADDF0); h+441 = 0 (0x826ADDFC); the transacted map
// h+444 emptied — `Erase(h+444, head->parent)` (0x826ADE08; 0x82B54BC0 / disc 0x82367AE8), head->parent
// = head, h+452 = 0, head->left = head->right = head (0x826ADE10..0x826ADE30); the two pages h+128 and
// h+368 pushed (`sub_82B49750` 0x826ADE44 / 0x826ADE58; disc 0x8228A900) and the book
// `sub_82990610(h+44, &pages, "Foreground.Book", pres, 1)` (0x826ADE90 / 0x828CE060; pres `*(*(G+56)+156)`,
// disc +152), which runs each page's +4; the front page's fill h+88 vf+4 (0x826ADEA4: the hooked
// `lhGuiShopFrontPage_Fill`, stated as "front") and the stats page's h+368 vf+4 (0x826ADEB8); h+440 = 1,
// the greeting pending (0x826ADEC4); the keeper `**(h+404)` (a dead handle reset, `sub_82193E38(h+400)`
// 0x826ADEEC / 0x821940C8), its component 26 (0x826ADEF4..0x826ADFEC) and, for a titles shop
// (`comp26+156 == 31`, 0x826AE008), the header `sub_8246B888(pres, 0x8349ADE0, localised
// "GUI_SCREEN_TITLE_TITLES")` (0x826AE04C / 0x828374B8 with 0x83498C9C; the tag through `sub_8230E0E8`
// 0x826AE038); h+396 = 0 (0x826AE058); the timer `sub_828C36D0(h+424, 0.0)` (0x826AE068 / 0x82834498:
// two bytes and two floats off `QueryPerformanceCounter`), which the kept highlight edge reads
// (`sub_828C3748(h+424)` in `sub_826B13B8`, 0x826B1488 / disc 0x82595988). Dropped: the open sound,
// `h` vf+4 (0x826ADDDC / 0x8259235C). The guest reads a keeper that is gone through a null pointer
// (0x826ADEF0 `li r11,0` → 0x826ADEF4 `lwz r10,0x24(r11)`); here that is "not a titles shop".
REX_EXTERN(rex_lhGuiShopScreen_OnEnter) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  const uint32_t pages = s.data;          // {?, begin, end, capacity}
  const uint32_t item = s.data + 0x10u;   // the page pointer handed to the push
  const uint32_t book = s.data + 0x14u;   // "Foreground.Book"
  const uint32_t tag = s.data + 0x18u;    // the header's tag, consumed by the localiser
  const uint32_t wide = s.data + 0x1Cu;   // its localised text
  const uint32_t pres = BookPresentation(base);
  g_shop_update_said = false;

  const bool stated = refii::vfs::JoinerShopStated();
  if (stated) {
    refii::vfs::JoinerShopEnter();
  } else {
    CallSlot(ctx, base, s.frame, h + 24u, 1u, {});
  }
  base[h + 0x1B9u] = 0u;
  const uint32_t head = LoadU32(base, h + 0x1C0u);
  Call(ctx, base, s.frame, 0x82B54BC0u, {h + 0x1BCu, LoadU32(base, head + 4u)});
  StoreU32(base, LoadU32(base, h + 0x1C0u) + 4u, LoadU32(base, h + 0x1C0u));
  StoreU32(base, h + 0x1C4u, 0u);
  StoreU32(base, LoadU32(base, h + 0x1C0u), LoadU32(base, h + 0x1C0u));
  StoreU32(base, LoadU32(base, h + 0x1C0u) + 8u, LoadU32(base, h + 0x1C0u));

  for (uint32_t i = 0; i < 16u; i += 4u) {
    StoreU32(base, pages + i, 0u);
  }
  for (uint32_t page : {0x80u, 0x170u}) {
    StoreU32(base, item, h + page);
    Call(ctx, base, s.frame, 0x82B49750u, {pages, item});
  }
  MakeString(ctx, base, s.frame, book, s.data + 0x40u, "Foreground.Book");
  Call(ctx, base, s.frame, 0x82990610u, {h + 0x2Cu, pages, book, pres, 1u});
  CallSlot(ctx, base, s.frame, h + 0x58u, 1u, {});
  CallSlot(ctx, base, s.frame, h + 0x170u, 1u, {});
  base[h + 0x1B8u] = 1u;

  uint32_t keeper = 0u;
  const uint32_t holder = LoadU32(base, h + 0x194u);
  if (holder != 0u) {
    keeper = LoadU32(base, holder);
    if (keeper == 0u) {
      Call(ctx, base, s.frame, 0x82193E38u, {h + 0x190u});
    }
  }
  const uint32_t comp26 = Plausible(keeper) ? refii_mod_component(base, keeper, 26u) : 0u;
  const bool titles = stated ? refii::vfs::JoinerShopTitles() : comp26 != 0u && LoadU32(base, comp26 + 156u) == 31u;
  if (titles) {
    MakeString(ctx, base, s.frame, tag, s.data + 0x80u, "GUI_SCREEN_TITLE_TITLES");
    const uint32_t said = Call(ctx, base, s.frame, 0x8230E0E8u, {wide, tag});
    Call(ctx, base, s.frame, 0x8246B888u, {pres, Address(0x8349ADE0u), said});
    Call(ctx, base, s.frame, 0x821C67F8u, {wide});
  }
  StoreU32(base, h + 0x18Cu, 0u);
  {
    PPCContext work = ctx;
    work.r1.u32 = s.frame;
    work.r3.u32 = h + 0x1A8u;
    work.f1.f64 = 0.0;
    refii::mods::CallGuest(work, base, Address(0x828C36D0u));
  }
  DropString(ctx, base, s.frame, book);
  const uint32_t storage = LoadU32(base, pages + 4u);
  if (storage != 0u) {
    Call(ctx, base, s.frame, 0x8221BD38u, {storage});  // Render_FreeD3DObject
  }
  Said("shop (22)", "OnEnter", h, keeper, titles ? 1u : 0u);
}

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
  // The edge's fills state the row's pages (page_fills_stated.cpp): scope "row", keyed by the row's
  // ticket, the id the edge compares with h+408 (0x826B13D0..0x826B13DC).
  const bool stated = refii::vfs::JoinerShopStated();
  if (stated) {
    refii::vfs::JoinerShopUpdate(ctx, base, type, LoadU32(base, row + 4u));
  } else {
    refii::vfs::OpenPageFill(22u, "row");
    Call(ctx, base, s.frame, 0x826B13B8u, {h, Call(ctx, base, s.frame, 0x826AE560u, {copy, row})});
    refii::vfs::ClosePageFill(LoadU32(base, row + 4u));
  }
  Call(ctx, base, s.frame, 0x8246B5E8u, {pres, Address(0x8349ADD8u), 1u});
  // The purse is the shopper's: local player two's, and an online joiner's own hero (hero two here) — the
  // guest's `li r5,1` asks hero one, the host's hero's copy on a joiner.
  RestatePurseForPlayerTwo(ctx, base, s.frame, pres, 0x8349ADD8u, RefiiCoopShoppingPlayerTwo() != 0 || stated);
  if (!stated && base[h + 0x1B9u] != 0u && type == 301u && LoadU32(base, qm + 40u) == 1u) {
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

// OnMenuEvent TU1 0x826AE5C0 / disc 0x82592AE0 (r4 the event: +0 name, +4 type, +8 id). Kept: the
// event copied to QM+80 — `lhCharString_Assign(QM+80, event)` (0x826AE5E8; 0x822651A0 / 0x82265160),
// then its type and id (0x826AE5F0 / 0x826AE5F8) — which the buy and sell answers read; by type
// (compares, 0x826AE600..0x826AE6A8): 30 the buy entry `sub_826AFF48(h, event)` (0x826AE668 / disc
// 0x82594468), 31 the sell entry `sub_826B0D10(h, event)` (0x826AE654 / 0x82595218) — their gates,
// `g_Menu:SetExpandableMenuDisabled(true)` and boxes are the guest's; 301 with h+441 set the deferred
// repopulate: h+441 = 0, the list's vf+4 (0x826AE6D0) and the transacted map cleared
// `sub_822F8640(h+444)` (0x826AE6D8 / 0x82367A90); 201 with "ReturnToGame" in the event's name (`strstr`
// 0x826AE724, an empty name for a null string) `lhGuiManager_ExitScreen(QM, 1)` (0x826AE754 /
// 0x82336F98). Dropped: 140 / 141, `sub_82371E80(&ev, G, "SE_GUI_SLIDE_MENU_UP" 0x83497DD8 / "_DOWN"
// 0x83497DDC, 0)` and its release (0x826AE638 / 0x826AE688; disc 0x82592B58 / 0x82592BA8).
REX_EXTERN(rex_lhGuiShopScreen_OnMenuEvent) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const uint32_t event = ctx.r4.u32;
  const Scratch s = Claim(ctx);
  const uint32_t qm = LoadU32(base, Gui(base) + 28u);
  Call(ctx, base, s.frame, 0x822651A0u, {qm + 80u, event});
  StoreU32(base, qm + 84u, LoadU32(base, event + 4u));
  StoreU32(base, qm + 88u, LoadU32(base, event + 8u));
  const uint32_t type = LoadU32(base, event + 4u);
  Said("shop (22)", "OnMenuEvent", h, type, LoadU32(base, event + 8u));
  if (refii::vfs::JoinerShopStated() && (type == 30u || type == 31u || type == 301u)) {
    refii::vfs::JoinerShopEvent(ctx, base, type, LoadU32(base, event + 8u));
  } else if (type == 30u) {
    Call(ctx, base, s.frame, 0x826AFF48u, {h, event});
  } else if (type == 31u) {
    Call(ctx, base, s.frame, 0x826B0D10u, {h, event});
  } else if (type == 301u) {
    if (base[h + 0x1B9u] != 0u) {
      base[h + 0x1B9u] = 0u;
      CallSlot(ctx, base, s.frame, h + 24u, 1u, {});
      Call(ctx, base, s.frame, 0x822F8640u, {h + 0x1BCu});
    }
  } else if (type == 201u) {
    const uint32_t handle = LoadU32(base, event);
    const uint32_t chars = handle != 0u ? LoadU32(base, handle) : 0u;
    if (Plausible(chars) && std::strstr(reinterpret_cast<const char*>(base + chars), "ReturnToGame") != nullptr) {
      Call(ctx, base, s.frame, 0x8236ED98u, {LoadU32(base, Gui(base) + 28u), 1u});
    }
  }
}

// OnModalAnswer TU1 0x826AE7E8 / disc 0x82592D08 (r4 the answer's code). Kept: with a plain box up
// (`*(QM+40) == 2`) and code 36, the box answered (`sub_8236FEC8(QM, 2)` 0x826AE88C, without its
// sound: AnswerBox) and `g_Menu:SetExpandableMenuDisabled(false)` on the book's presentation
// (0x826AE8B4); with a question up (`*(QM+40) == 3`), by its id `*(QM+44)` (0x826AE81C..0x826AE830): 17
// the tattoo removal's answer `sub_826AF430` (0x826AE864 / disc 0x82593950), 19 the worn item's sale
// `sub_826AFB98` (0x826AE84C / 0x825940B8), 20 buy-and-wear `sub_826AF888` (0x826AE834 / 0x82593DA8),
// each with the slot's own registers (h, code, r5). Dropped: "SE_GUI_MENU_BOX_SELECT" inside
// `sub_8236FEC8` (0x8236FEFC / 0x823380F4). The three helpers are the guest's, whole.
REX_EXTERN(rex_lhGuiShopScreen_OnModalAnswer) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const uint32_t code = ctx.r4.u32;
  const Scratch s = Claim(ctx);
  const uint32_t qm = LoadU32(base, Gui(base) + 28u);
  const uint32_t mode = LoadU32(base, qm + 40u);
  const uint32_t box = LoadU32(base, qm + 44u);
  Said("shop (22)", "OnModalAnswer", h, code, mode == 3u ? box : mode);
  if (mode == 2u) {
    if (code == 36u) {
      AnswerBox(ctx, base, s.frame, qm, 2u);
      RunMenuLua(ctx, base, s.frame, s.data, "g_Menu:SetExpandableMenuDisabled(false)", BookPresentation(base));
    }
  } else if (mode == 3u && refii::vfs::JoinerShopStated()) {
    refii::vfs::JoinerShopAnswer(ctx, base, box, code);
  } else if (mode == 3u) {
    if (box == 17u) {
      Call(ctx, base, s.frame, 0x826AF430u, {h, code, ctx.r5.u32});
    } else if (box == 19u) {
      Call(ctx, base, s.frame, 0x826AFB98u, {h, code, ctx.r5.u32});
    } else if (box == 20u) {
      Call(ctx, base, s.frame, 0x826AF888u, {h, code, ctx.r5.u32});
    }
  }
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
