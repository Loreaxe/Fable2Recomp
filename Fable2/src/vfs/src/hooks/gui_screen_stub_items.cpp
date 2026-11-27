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
#include "core/guest_state.h"
#include "core/refii_mod.h"
#include "hooks/gui_screen_stub_support.h"
#include "hooks/gui_screen_stubs.h"
#include "hooks/item_rows_stated.h"
#include "hooks/page_fills_stated.h"
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
// back from the dye screen), h+376 the mode last entered, h+380 the held augment / remover handle, h+396 the
// current item handle, h+416 the last highlighted row id, h+420 the target sub-list (1 augment targets, 2
// remover targets). Same offsets on both builds.
//
// Every own slot is ported. OnMenuEvent (TU1 0x826D1800 / disc 0x8257D250): its type 3 arm is the row's
// A, which `Items:<p>:use` runs on the row Godot names (guest_tick_pump.cpp CommitItemThroughScreen).
// OnModalAnswer (0x826D28B8 / 0x8257E2F0) and OnExit (0x826D1618 / 0x8257D008) are at the end of the file.

namespace {

bool g_items_update_said = false;

}  // namespace

namespace refii::vfs {

// OnEnter TU1 0x826D13D0 / disc 0x8257CDC0, inside the co-op wrapper that lends hero two
// (coop_hero_hooks.cpp), and under the listed hero (ListedHeroScope: the hero of the player record the
// populates pick — a joiner's own, item_rows_stated.cpp). Kept, in the guest's order: mode 2 becomes 1 and is remembered as "back"
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
  ListedHeroScope listed(base, 15u);
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
  RestatePurseForPlayerTwo(ctx, base, s.frame, pres, 0x8349B28Cu);
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
  ListedHeroScope listed(base, 15u);
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
    // Stated as screen 15's "row" fill for the row's ticket (page_fills_stated.cpp): the name, the info line,
    // the dye swatch (shown, its tint), the A prompt's text and shown, the stars, the value line, the
    // description with its augment lines.
    OpenPageFill(15u, "row");
    Call(ctx, base, s.frame, 0x829B5160u, {h + 0x60u});
    Call(ctx, base, s.frame, 0x829B6B50u, {h + 0x150u});
    ClosePageFill(id);
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

// OnMenuEvent TU1 0x826D1800 / disc 0x8257D250 (h, event; the type at event+4; jump table 0x826D185C /
// 0x8257D2AC, index type - 3: 3 → 0x826D19A8 / 0x8257D3F8, 80 / 81 → 0x826D1DDC, 83 → 0x826D1E9C, 85 →
// 0x826D1F34; 140, 141, 310, 311 by compare). KEPT, the type 3 arm whole, in the guest's order
// (0x826D19A8..0x826D1DD8 / 0x8257D3F8..0x8257D824):
//   the current item `*(h+396)` set (0x826D19B4) and usable, `sub_827649C8(item)` (0x826D19B8 /
//   0x827417E0); its definition `sub_821B24F8(&{item+4, item+8})` (0x826D1A00 / 0x821B2710) and that
//   definition's "InventoryItemComponent" `sub_822A99E8` (0x826D1A08 / 0x822A97A8), its "Category"
//   `sub_82430FA8(component, "Category", &category)` (0x826D1A24 / 0x823A5128);
//   the local hero `sub_8229EE78(sub_821C7CE0(G))` (0x826D1A30 / 0x826D1A34) and its component 137
//   (present bit `hero+0x34` bit 9, 0x826D1A44; the pair table 0x826D1A48..0x826D1B30): swimming
//   (`lbz r11, 0x49(r31)` 0x826D1B4C) with a weapon (category 1, 0x826D1B58) → the refusal
//   `lhGuiManager_OpenModal(QM, localised "GUI_CANNOT_EQUIP_WEAPON_WHILE_SWIMMING", 2, 38, 1, 0, 0)`
//   (0x826D1BA4; disc 0x8257D5F0 passes six, no r9) and nothing else;
//   else tutorial 188 completed once displayed — `Lua_TutorialManager_HasDisplayedTutorial(*(GAME+168),
//   188)` then `Lua_TutorialManager_CompleteTutorial` with the same two (0x826D1BC8 / 0x826D1BDC: the
//   test is a leaf that keeps r4 and r7) — `lhGuiInventory_ActivateItemByCategory(*(G+52), h+396, 1)`
//   (0x826D1BEC / 0x825A5A40), and for a weapon the rows again, h+24 vf+4 (0x826D1C08);
//   an expression book — the item's kind `*(item+16) == 16` (0x826D1C1C), its definition's
//   "InventoryItemComponent" > "BonusEffects" > "ExpressionToUnlock" (0x826D1C2C / 0x826D1C44 /
//   0x826D1C5C) with an "ExpressionCategory" (`sub_823F5D00` 0x826D1C74 / 0x823D4FC8) that is neither 0
//   nor 7 — queues the learned box: localised "GUI_SCREEN_EXPRESSIONS_LEARNED" (0x826D1C9C / 0x826D1CA8)
//   with L"%1" (0x820A3118 / 0x820A2D6C) replaced (`sub_82B3EB68` 0x826D1D64 / 0x82B38B78) by the
//   localised "NameTag" of the expression (`sub_82323540` 0x826D1D0C; "" without one, 0x826D1D48), a
//   48-byte `CMessageBox_ctor(box, 0x8349B288 "Unnamed", &text, -1, 0)` (0x826D1D88 / 0x826D1DA8; disc
//   0x823BD140 with 0x834988E4), wrapped `sub_82A04CC0(&box)` (0x826D1DBC / 0x8254C780) and queued on
//   the GUI owner `sub_826B99D8(*(QM+52)+4, wrapped)` (0x826D1DC8 / 0x8240D440).
// KEPT, the arms the guest's own rows send (their types are spawned by `sub_826CEEF0`: 80 a dye, 83 an
// augment, 310 a remover; 85 / 311 the target rows of `sub_829B0BF0` / `sub_829B10A8`), in the guest's order:
//   80 / 81 (0x826D1DDC / 0x8257D828): the preview's Hide `sub_828C3FD8(h+128)` (0x826D1DE0 / 0x82834DA0);
//   `g_Menu:PushMenuState()` on the book presentation (0x826D1E08); the dye's handle `sub_82880BC0(*(QM+60)+4,
//   id, h+380)` (0x826D1E24 / 0x827BE1E8); the clothing handler `*sub_8243BD60(*(G+28)+68, &16)` (0x826D1E40 /
//   0x8247D010, key `li r9,0x10` / disc `0xF`) takes it at +228 (`sub_8218DF38` 0x826D1E58 / 0x8218DF70) and
//   mode 5 at +220 (`li r10,5 ; stw r10,0xDC(r30)` 0x826D1E5C / 0x826D1E68); `lhGuiManager_SetScreen(QM, 16, 0)`
//   (0x826D1E74 / 0x82336838), `SetActiveScreen(QM, 16)` (0x826D1E84 / 0x82336598), `EnterGuiMode(QM)`
//   (0x826D1E90 / 0x82336D70) — the clothing book opens on the dye's targets (gui_screen_stub_clothing.cpp);
//   83 (0x826D1E9C / 0x8257D8E8) and 310 (0x826D24A0 / 0x8257DEDC): h+420 = 1 / 2 (`stw r11,0x1A4(r29)`
//   0x826D1EA4 / 0x826D24A8); PushMenuState (0x826D1EC8 / 0x826D24CC); the held handle into h+380 (0x826D1EF4
//   / 0x826D24F8); the target list `sub_829B0BF0(h+24)` / `sub_829B10A8(h+24)` (0x826D1EFC / 0x826D2500; disc
//   0x828C9D10 / 0x828CA1C8); the tab strip's title `sub_826B81C0(*(G+76), "GUI_SCREEN_TITLE_AUGMENT" |
//   "GUI_SCREEN_AUGMENT_REMOVAL_TITLE")` (0x826D1F20 / 0x826D2524; disc 0x8240B090);
//   85 (0x826D1F34 / 0x8257D980) and 311 (0x826D2244 / 0x8257DC88): the localised name of the held item and of
//   the target (`sub_82764688` 0x826D1F78 / 0x826D1FD4 / 0x826D2284, disc 0x827414A0; `sub_8230E0E8`), then
//   `sub_826CA298(INV, h+380, h+396)` (0x826D2004 / 0x825A6220) or `sub_826CA840(INV, h+380, h+396)`
//   (0x826D22B8 / 0x825A67C8); on success the chunk `g_Menu:RepopulateMenuFromPopulationScript( '<the
//   screen's script>' )` (`dword_8349B29C` / disc `0x834988F8`, 0x826D2040, run 0x826D207C), the rows
//   `sub_829B0A58(h+24, 7)` (0x826D2088), h+420 = 0 and h+380..+392 = 0 (0x826D208C..0x826D20A8); 311 then the
//   tab strip's title back `sub_826B8218(*(G+76))` (0x826D2348 / 0x8240B0E8), h+85 = 1 and the description
//   fill `sub_829B5160(h+96)` (0x826D236C / 0x828CF168); the result box — "GUI_SCREEN_AUGMENT_APPLIED" with %1
//   the augment and %2 the target, `OpenModal(QM, text, 2, 38, 0, 0, 0)` (0x826D2140); "…_FAILED_FULL" with %1
//   the target, `(…, 1, 0, 0)` (0x826D21E4); "…_REMOVED_SUCCESS" / "…_REMOVED_FAIL" with %1 the target, flag 1
//   (0x826D23E8 / 0x826D2484) — each text through `sub_82287C28` (0x826D2120.. / 0x82287710). Box 38 is
//   answered by the ported OnModalAnswer (patch B).
// DROPPED: 140 / 141, `sub_82371E80(&ev, G, 0x83497DD8 "SE_GUI_SLIDE_MENU_UP" | 0x83497DDC
// "SE_GUI_SLIDE_MENU_DOWN", 0)` (0x826D2218 / 0x826D2548); the book's slide back `sub_82990A18(h+44, pres)` in
// 83 / 310 (0x826D1EE4 / 0x826D24E8: `GoToSlideAndRestart(…, 2)`, book+40 = 0 — already 0, see OnExit — and
// "SE_GUI_BLADE_SLIDE_OUT").
// The whole slot runs with the hero-one slot holding the listed hero (ListedHeroScope, item_rows_stated.cpp).
namespace {

/// `sub_82764688(&tag, item)` then `sub_8230E0E8(&wide, tag)`: the item's localised name at `wide` (an
/// empty wide text when the handle holds none).
void LocalisedItemName(PPCContext& ctx, uint8_t* base, uint32_t frame, uint32_t handle, uint32_t wide,
                       uint32_t tag, uint32_t loc) {
  Call(ctx, base, frame, 0x822D6408u, {wide, Address(0x82000AF8u)});  // lhWideString_CtorFromWStr L""
  const uint32_t item = LoadU32(base, handle);
  if (item == 0u) {
    return;
  }
  const uint32_t name = Call(ctx, base, frame, 0x82764688u, {tag, item});
  const uint32_t said = Call(ctx, base, frame, 0x8230E0E8u, {loc, name});
  Call(ctx, base, frame, 0x822B88A8u, {wide, said});  // lhCharString_AssignIfDifferent
  Call(ctx, base, frame, 0x821C67F8u, {loc});
}

/// The localised `key` with %1 (and %2 when `second` is set) replaced (`sub_82B3EB68`, markers 0x820A3118 /
/// 0x820A3FFC), opened as box 38 in mode 2 with `flag` (`sub_82287C28` then `lhGuiManager_OpenModal`).
void ResultBox(PPCContext& ctx, uint8_t* base, uint32_t frame, uint32_t data, const char* key, uint32_t first,
               uint32_t second, uint32_t flag) {
  const uint32_t tag = data;
  const uint32_t text = data + 0x04u;
  const uint32_t mark = data + 0x08u;
  const uint32_t boxed = data + 0x10u;
  MakeString(ctx, base, frame, tag, data + 0x40u, key);
  Call(ctx, base, frame, 0x8230E0E8u, {text, tag});
  Call(ctx, base, frame, 0x822D6408u, {mark, Address(0x820A3118u)});
  Call(ctx, base, frame, 0x82B3EB68u, {text, mark, first});
  Call(ctx, base, frame, 0x821C67F8u, {mark});
  if (second != 0u) {
    Call(ctx, base, frame, 0x822D6408u, {mark, Address(0x820A3FFCu)});
    Call(ctx, base, frame, 0x82B3EB68u, {text, mark, second});
    Call(ctx, base, frame, 0x821C67F8u, {mark});
  }
  const uint32_t qm = LoadU32(base, Gui(base) + 28u);
  const uint32_t said = Call(ctx, base, frame, 0x82287C28u, {boxed, text});
  Call(ctx, base, frame, 0x8236FA68u, {qm, said, 2u, 38u, flag, 0u, 0u});  // lhGuiManager_OpenModal
  Call(ctx, base, frame, 0x821C67F8u, {text});
}

/// The items arms other than the row's A. True when `type` was one of them.
bool ItemsGuestArm(PPCContext& ctx, uint8_t* base, uint32_t h, uint32_t event, uint32_t type) {
  const Scratch s = Claim(ctx);
  const uint32_t g = Gui(base);
  const uint32_t qm = LoadU32(base, g + 28u);
  const uint32_t pres = BookPresentation(base);
  const uint32_t id = LoadU32(base, event + 8u);
  const uint32_t registry = LoadU32(base, qm + 60u) + 4u;
  const uint32_t held = h + 0x17Cu;
  if (type == 80u || type == 81u) {
    Call(ctx, base, s.frame, 0x828C3FD8u, {h + 0x80u});
    RunMenuLua(ctx, base, s.frame, s.data, "g_Menu:PushMenuState()", pres);
    Call(ctx, base, s.frame, 0x82880BC0u, {registry, id, held});
    const uint32_t key = s.data + 0x60u;
    StoreU32(base, key, refii::guest::ScreenId(16u));
    const uint32_t clothing = LoadU32(base, Call(ctx, base, s.frame, 0x8243BD60u, {qm + 68u, key}));
    if (Plausible(clothing)) {
      if (clothing + 0xE4u != held) {
        Call(ctx, base, s.frame, 0x8218DF38u, {clothing + 0xE4u, held});
      }
      StoreU32(base, clothing + 0xDCu, 5u);
    }
    Call(ctx, base, s.frame, 0x8236E630u, {qm, refii::guest::ScreenId(16u), 0u});  // lhGuiManager_SetScreen
    Call(ctx, base, s.frame, 0x8236E388u, {qm, refii::guest::ScreenId(16u)});      // SetActiveScreen
    Call(ctx, base, s.frame, 0x8236EB68u, {qm});                                    // EnterGuiMode
    REXLOG_INFO("Refii screen stub: items (15) — a dye (event {}): the clothing book opens on its targets", type);
    return true;
  }
  if (type == 83u || type == 310u) {
    StoreU32(base, h + 0x1A4u, type == 83u ? 1u : 2u);
    RunMenuLua(ctx, base, s.frame, s.data, "g_Menu:PushMenuState()", pres);
    Call(ctx, base, s.frame, 0x82880BC0u, {registry, id, held});
    Call(ctx, base, s.frame, type == 83u ? 0x829B0BF0u : 0x829B10A8u, {h + 24u});
    const uint32_t title = s.data + 0x60u;
    MakeString(ctx, base, s.frame, title, s.data + 0x80u,
               type == 83u ? "GUI_SCREEN_TITLE_AUGMENT" : "GUI_SCREEN_AUGMENT_REMOVAL_TITLE");
    Call(ctx, base, s.frame, 0x826B81C0u, {LoadU32(base, g + 76u), title});
    DropString(ctx, base, s.frame, title);
    REXLOG_INFO("Refii screen stub: items (15) — {} (event {}): its target list", type == 83u ? "an augment" : "a remover",
                type);
    return true;
  }
  if (type != 85u && type != 311u) {
    return false;
  }
  const uint32_t first = s.data + 0x100u;   // the held item's name (85) / unused (311)
  const uint32_t target = s.data + 0x104u;  // the target's name
  const uint32_t tag = s.data + 0x108u;
  const uint32_t loc = s.data + 0x10Cu;
  if (type == 85u) {
    LocalisedItemName(ctx, base, s.frame, held, first, tag, loc);
  }
  LocalisedItemName(ctx, base, s.frame, h + 0x18Cu, target, tag, loc);
  const uint32_t inv = LoadU32(base, g + 52u);
  const bool done =
      (Call(ctx, base, s.frame, type == 85u ? 0x826CA298u : 0x826CA840u, {inv, held, h + 0x18Cu}) & 0xFFu) != 0u;
  if (done) {
    const uint32_t script = Address(0x8349B29Cu);
    std::string chunk = "g_Menu:RepopulateMenuFromPopulationScript( '" +
                        (LoadU32(base, script) != 0u ? refii::vfs::ReadGuestLhString(base, script) : std::string()) +
                        "' )";
    RunMenuLua(ctx, base, s.frame, s.data, chunk.c_str(), pres);
    Call(ctx, base, s.frame, 0x829B0A58u, {h + 24u, 7u});
    StoreU32(base, h + 0x1A4u, 0u);
    for (uint32_t i = 0; i < 16u; i += 4u) {
      StoreU32(base, held + i, 0u);
    }
    if (type == 311u) {
      Call(ctx, base, s.frame, 0x826B8218u, {LoadU32(base, g + 76u)});
      base[h + 0x55u] = 1u;
      refii::vfs::OpenPageFill(15u, "row");
      Call(ctx, base, s.frame, 0x829B5160u, {h + 0x60u});
      refii::vfs::ClosePageFill(LoadU32(base, h + 0x1A0u));
    }
  }
  if (type == 85u) {
    if (done) {
      ResultBox(ctx, base, s.frame, s.data + 0x140u, "GUI_SCREEN_AUGMENT_APPLIED", first, target, 0u);
    } else {
      ResultBox(ctx, base, s.frame, s.data + 0x140u, "GUI_SCREEN_AUGMENT_FAILED_FULL", target, 0u, 1u);
    }
    Call(ctx, base, s.frame, 0x821C67F8u, {first});
  } else {
    ResultBox(ctx, base, s.frame, s.data + 0x140u, done ? "GUI_SCREEN_AUGMENT_REMOVED_SUCCESS" : "GUI_SCREEN_AUGMENT_REMOVED_FAIL",
              target, 0u, 1u);
  }
  Call(ctx, base, s.frame, 0x821C67F8u, {target});
  REXLOG_INFO("Refii screen stub: items (15) — {} (event {}) {}: the guest's result box 38 opened",
              type == 85u ? "an augment's attach" : "a remover's strip", type, done ? "succeeded" : "failed");
  return true;
}

}  // namespace

REX_EXTERN(rex_lhGuiItemsScreen_OnMenuEvent) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const uint32_t type = LoadU32(base, ctx.r4.u32 + 4u);
  Said("items (15)", "OnMenuEvent", h, type, LoadU32(base, ctx.r4.u32 + 8u));
  refii::vfs::ListedHeroScope listed(base, 15u);
  if (ItemsGuestArm(ctx, base, h, ctx.r4.u32, type) || type != 3u) {
    return;
  }
  const Scratch s = Claim(ctx);
  const uint32_t ref = s.data;            // {item+4, item+8}, the definition's key
  const uint32_t value = s.data + 0x08u;  // "Category", then "ExpressionCategory"
  const uint32_t tag = s.data + 0x10u;    // a text tag, consumed by the localiser
  const uint32_t wide = s.data + 0x14u;   // the refusal's text, consumed by the opener
  const uint32_t text = s.data + 0x18u;   // the learned box's text
  const uint32_t mark = s.data + 0x1Cu;   // L"%1"
  const uint32_t key = s.data + 0x20u;    // the expression's name tag, consumed by the localiser
  const uint32_t raw = s.data + 0x24u;    // the tag as read
  const uint32_t said = s.data + 0x28u;   // its localised text
  const uint32_t slot = s.data + 0x30u;   // the box pointer handed to the wrapper
  const uint32_t chars = s.data + 0x40u;
  const uint32_t g = Gui(base);
  const uint32_t qm = LoadU32(base, g + 28u);
  const uint32_t holder = h + 0x18Cu;

  const uint32_t item = LoadU32(base, holder);
  if (item == 0u || (Call(ctx, base, s.frame, 0x827649C8u, {item}) & 0xFFu) == 0u) {
    return;
  }
  StoreU32(base, ref, LoadU32(base, item + 4u));
  StoreU32(base, ref + 4u, LoadU32(base, item + 8u));
  const uint32_t definition = Call(ctx, base, s.frame, 0x821B24F8u, {ref});
  const uint32_t component = Call(ctx, base, s.frame, 0x822A99E8u, {definition, Address(0x820AF11Cu)});
  if (component == 0u) {
    return;
  }
  StoreU32(base, value, 0u);
  Call(ctx, base, s.frame, 0x82430FA8u, {component, Address(0x820B29BCu), value});
  const uint32_t category = LoadU32(base, value);
  const uint32_t hero = Call(ctx, base, s.frame, 0x8229EE78u, {Call(ctx, base, s.frame, 0x821C7CE0u, {g})});
  const uint32_t comp137 = refii_mod_component(base, hero, 137u);
  if (comp137 != 0u && base[comp137 + 0x49u] != 0u && category == 1u) {
    MakeString(ctx, base, s.frame, tag, chars, "GUI_CANNOT_EQUIP_WEAPON_WHILE_SWIMMING");
    const uint32_t refusal = Call(ctx, base, s.frame, 0x8230E0E8u, {wide, tag});
    Call(ctx, base, s.frame, 0x8236FA68u, {qm, refusal, 2u, 38u, 1u, 0u, 0u});  // lhGuiManager_OpenModal
    REXLOG_INFO("Refii screen stub: items (15) — a weapon while swimming: the guest's refusal box 38 opened");
    return;
  }

  const uint32_t tutorials = Tutorials(base);
  if ((Call(ctx, base, s.frame, 0x82450870u, {tutorials, 188u}) & 0xFFu) != 0u) {
    Call(ctx, base, s.frame, 0x82450918u, {tutorials, 188u});
  }
  Call(ctx, base, s.frame, 0x826C9AB8u, {LoadU32(base, g + 52u), holder, 1u});
  if (category == 1u) {
    CallSlot(ctx, base, s.frame, h + 24u, 1u, {});
  }

  // The four lookups are the guest's own, made twice as it makes them (0x826D1C24.. and 0x826D1CD0..).
  const auto expression = [&]() -> uint32_t {
    const uint32_t now = LoadU32(base, holder);
    if (LoadU32(base, now + 16u) != 16u) {
      return 0u;
    }
    uint32_t at = Call(ctx, base, s.frame, 0x821B24F8u, {now + 4u});
    for (uint32_t name : {0x820AF11Cu, 0x820AF134u, 0x820AF144u}) {
      at = Call(ctx, base, s.frame, 0x822A99E8u, {at, Address(name)});
      if (at == 0u) {
        return 0u;
      }
    }
    return at;
  };
  StoreU32(base, value, 0u);
  const uint32_t unlock = expression();
  if (unlock == 0u) {
    return;
  }
  Call(ctx, base, s.frame, 0x823F5D00u, {unlock, Address(0x820AF158u), value});
  const uint32_t learned = LoadU32(base, value);
  if (learned == 0u || learned == 7u) {
    return;
  }
  MakeString(ctx, base, s.frame, tag, chars, "GUI_SCREEN_EXPRESSIONS_LEARNED");
  Call(ctx, base, s.frame, 0x8230E0E8u, {text, tag});
  Call(ctx, base, s.frame, 0x822D6408u, {mark, Address(0x820A3118u)});  // lhWideString_CtorFromWStr
  const uint32_t named = expression();
  if (named != 0u) {
    const uint32_t name = Call(ctx, base, s.frame, 0x82323540u, {named, Address(0x820AA1C4u)});
    StoreU32(base, raw, 0u);
    Call(ctx, base, s.frame, 0x8222CED0u, {raw, name, 0xFFFFFFFFu});
    Call(ctx, base, s.frame, 0x821F0240u, {key, raw});
    DropString(ctx, base, s.frame, raw);
  } else {
    MakeString(ctx, base, s.frame, key, chars, "");
  }
  const uint32_t name_text = Call(ctx, base, s.frame, 0x8230E0E8u, {said, key});
  Call(ctx, base, s.frame, 0x82B3EB68u, {text, mark, name_text});
  Call(ctx, base, s.frame, 0x821C67F8u, {said});
  Call(ctx, base, s.frame, 0x821C67F8u, {mark});
  const uint32_t owner = LoadU32(base, qm + 52u);
  uint32_t box = Call(ctx, base, s.frame, 0x8221F258u, {48u});  // Render_AllocD3DObject
  if (box != 0u) {
    box = Call(ctx, base, s.frame, 0x823CEA78u, {box, Address(0x8349B288u), text, 0xFFFFFFFFu, 0u});
    if (box != 0u) {
      StoreU32(base, slot, box);
      Call(ctx, base, s.frame, 0x826B99D8u, {owner + 4u, Call(ctx, base, s.frame, 0x82A04CC0u, {slot})});
      REXLOG_INFO("Refii screen stub: items (15) — an expression book: the guest's learned box queued");
    }
  }
  Call(ctx, base, s.frame, 0x821C67F8u, {text});
}

// OnScreenAction TU1 0x826D2560 / disc 0x8257DF98 (jump table index code − 26): 26 / 27 the book and the
// preview's spin, 36 Lua OnPressA + sound, 59 the zoom; B (37) and LB / RB (72 / 73, gated only on the
// augment sub-list h+420 being closed, 0x826D2654 / 0x826D2678) are the spine calls Godot posts as
// `PauseBack` / `PauseCycle:<p>:…`. Nothing kept.
REX_EXTERN(rex_lhGuiItemsScreen_OnScreenAction) {
  REX_FUNC_PROLOGUE();
  Said("items (15)", "OnScreenAction", ctx.r3.u32, ctx.r4.u32, ctx.r5.u32);
}

// OnModalAnswer(h, code) TU1 0x826D28B8 / disc 0x8257E2F0 — the whole body: `QM = *(G+28)`; with a box
// up in mode 2 (`lwz r10,0x28(r3); cmpwi r10,2`) and code 36, `b sub_8236FEC8(QM, 2)` (0x826D28DC / disc
// `sub_823380C0`). Kept: the box cleared (AnswerBox). Dropped: that helper's "SE_GUI_MENU_BOX_SELECT".
REX_EXTERN(rex_lhGuiItemsScreen_OnModalAnswer) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const uint32_t code = ctx.r4.u32;
  const Scratch s = Claim(ctx);
  const uint32_t qm = LoadU32(base, Gui(base) + 28u);
  const uint32_t mode = LoadU32(base, qm + 40u);
  if (mode == 2u && code == 36u) {
    AnswerBox(ctx, base, s.frame, qm, 2u);
  }
  Said("items (15)", "OnModalAnswer", h, code, mode);
}

// OnExit TU1 0x826D1618 / disc 0x8257D008. Kept, in the guest's order: the base exit `sub_826D5C30(h)`
// (0x826D1628 / 0x82583490), h+85 = 1 and h+420 = 0 (0x826D1634 / 0x826D163C), the preview's release
// `sub_828C3FD8(h+128)` (0x826D1640 / 0x82834DA0), h+376 = 0 (0x826D168C). Dropped: the arm behind h+84
// (`lbz r10,0x54(r31)` 0x826D1644) — the book's pop `sub_82990FD8(h+44, pres)` when its page stack holds
// more than one (0x826D167C / 0x828CEA28) and its slide back `sub_82990A18(h+44, pres)` (0x826D1688 /
// 0x828CE468: `GoToSlideAndRestart(…, 2)`, book+40 = 0, "SE_GUI_BLADE_SLIDE_OUT"). h+84 is the book's
// own +40 (the book is h+44): the registration writes it 0 (`stb r26,0x28(r27)` 0x82990720, r26 = 0) and
// only the slide `sub_82990A98` sets it (`stb r5,0x28(r3)` 0x82990AB4), which the ported Update no
// longer calls — the arm cannot be entered.
REX_EXTERN(rex_lhGuiItemsScreen_OnExit) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  Call(ctx, base, s.frame, 0x826D5C30u, {h});
  base[h + 0x55u] = 1u;
  StoreU32(base, h + 0x1A4u, 0u);
  Call(ctx, base, s.frame, 0x828C3FD8u, {h + 0x80u});
  StoreU32(base, h + 0x178u, 0u);
  Said("items (15)", "OnExit", h, base[h + 0x54u]);
}
