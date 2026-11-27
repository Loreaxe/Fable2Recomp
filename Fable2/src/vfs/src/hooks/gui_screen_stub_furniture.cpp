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
#include "hooks/page_fills_stated.h"
#include "hooks/ppc_externs.h"
#include "vfs_hooks_private.h"

using namespace refii::vfs::stub;
using refii::guest::Address;
using refii::mods::LoadU32;
using refii::mods::StoreU32;

// ---- UPGRADE FURNITURE (26; disc 25) — vtable 0x820CECEC / 0x820C18E8 ------------------------
//
// h+24 the row list (vt 0x820EBE7C: +4 `sub_8299D5B0` / 0x828F3158 → `sub_8299D910` / 0x828F34B8, one
// row per level — the installed one and each the hero holds an unlock card for — its id the level,
// its rating the stars, the installed row emphasised; +8 a nullsub), h+44 the book, h+96 page 1 (its
// preview at h+128), h+320 page 2, h+348 the last highlighted level (-1 none). Same offsets on both
// builds. The piece is the local player's use target (`sub_826B61C8` / 0x8259B3B8), never an argument.
//
// Every slot is ported: OnEnter, Update and OnScreenAction below; OnMenuEvent (0x826B65F0 / 0x8259C768,
// one body shared with decoration 27 and the co-op screen 24) and OnExit (0x826B6190 / 0x8259B380, one
// body shared with decoration 27 and gift 25) for every vtable they sit in (gui_screen_stub_shared.cpp).
// The dtor (0x826B5FE8 / 0x8259C150, shared with 27) frees and nothing else.

namespace {

constexpr uint32_t kFurnitureVtable = 0x820CECECu;

bool g_furniture_update_said = false;

/// Set by a done upgrade; the next Update forgets the highlighted level, as the guest's upgrade does.
bool g_furniture_refill = false;

}  // namespace

// OnEnter TU1 0x826B6098 / disc 0x8259B288. Kept, in the guest's order: the two pages h+96 and h+320
// pushed (`sub_82B49750` 0x826B60E8 / 0x826B60FC; disc 0x8228A900) and the book
// `sub_82990610(h+44, &pages, "Foreground.Book", pres, 1)` (0x826B612C / 0x828CE060), which runs each
// page's +4; the rows, h+24 vf+4 (0x826B6150: `sub_8299D5B0` → `sub_8299D910`); the gold text
// `sub_8246B5E8(pres, 0x8349AE9C, 1)` (0x826B6164 / 0x82837218 with 0x83498D58); h+348 = -1
// (0x826B6170). `pres` is `*(*(G+56)+156)` (disc +152, 0x826B60BC / 0x8259B2AC). Dropped: the open
// sound, `h` vf+4 (0x826B60C4), and the book's slide `sub_82990A98(h+44, pres, 1)` (0x826B613C /
// 0x828CE4E8: `lhGuiElement_GoToSlideAndRestart(*(book+4), pres, 1)`, the sound 0x83497DF0
// "SE_GUI_BLADE_SLIDE_IN", and book+40 = h+84 = 1). h+84 stays 0, as the registration leaves it
// (`stb r26, 0x28(r27)` 0x82990720): its only readers are the slide itself (0x82990AA4), the page input
// `sub_829907F0` and its `sub_829911D0` (0x829908C8 / 0x82991214; disc 0x828CE318 / 0x828CEC64) and
// OnScreenAction's own test (0x826B6E78 / 0x8259BFE0) — all reached from OnScreenAction alone, ported
// empty. ADDED, first: the GUI's owner stated as the opener's player (StateGuiOwner). The one opener,
// the player-interact ability (`lhGuiManager_SetScreen(QM, 0x1A, 0)` 0x82977654 / disc 0x19 at
// 0x829A6C6C), states no owner; its actor is noted there (gui_screen_stub_shared.cpp
// RefiiInteractScreenOwner) — without it the screen inherited the last statement. A DELIBERATE DEPARTURE
// from the guest: the handler takes player one's input alone (OnScreenAction `cmplwi cr6, r5, 1` / `bne`
// 0x826B6DAC / disc 0x8259BF14); here the screen's owner drives it, player two included. OnEnter and
// Update run for the owner (OwnerScope): the targeted piece and the pages read "the local player"
// inlined (`sub_8299B6D8` 0x8299B780 / 0x828F12A8, `sub_8299C710` 0x8299C780 / 0x828F2328) and the cards
// the hero-one slot (`sub_825CC2F0` 0x825CC32C).
REX_EXTERN(rex_lhGuiSelectionScreen_OnEnter) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  const uint32_t pages = s.data;         // {?, begin, end, capacity}
  const uint32_t item = s.data + 0x10u;  // the page pointer handed to the push, then the book's name
  const uint32_t pres = BookPresentation(base);
  g_furniture_update_said = false;
  StateGuiOwner(ctx, base, s.frame, TakeOpenerPlayer() == 2u);
  OwnerScope owner(base, 26u);

  for (uint32_t i = 0; i < 16u; i += 4u) {
    StoreU32(base, pages + i, 0u);
  }
  for (uint32_t page : {0x60u, 0x140u}) {
    StoreU32(base, item, h + page);
    Call(ctx, base, s.frame, 0x82B49750u, {pages, item});
  }
  MakeString(ctx, base, s.frame, item, s.data + 0x40u, "Foreground.Book");
  Call(ctx, base, s.frame, 0x82990610u, {h + 0x2Cu, pages, item, pres, 1u});
  CallSlot(ctx, base, s.frame, h + 24u, 1u, {});
  Call(ctx, base, s.frame, 0x8246B5E8u, {pres, Address(0x8349AE9Cu), 1u});
  StoreU32(base, h + 0x15Cu, 0xFFFFFFFFu);
  DropString(ctx, base, s.frame, item);
  const uint32_t storage = LoadU32(base, pages + 4u);
  if (storage != 0u) {
    Call(ctx, base, s.frame, 0x8221BD38u, {storage});  // Render_FreeD3DObject
  }
  Said("upgrade furniture (26)", "OnEnter", h);
}

// Update TU1 0x826B65B0 / disc 0x8259B7A0. Kept: the highlight edge `sub_826B64D8(h)` / 0x8259B6C8
// whole (0x826B65CC) — the highlighted row (`lhGuiMenu_GetHighlightedItemData` 0x826B655C, answered
// natively from the row Godot states) and, on a level unlike h+348 (0x826B656C), the two page fills
// `sub_8299B6D8(h+96)` / `sub_8299C710(h+320)` (0x826B6574 / 0x826B657C; disc 0x828F1200 /
// 0x828F22B8: the preview's Set, the level's name, description and value change) and h+348 = the
// level (0x826B6580). Dropped: the child tick `sub_826D5D00` (0x826B65C4; this list's vf+8 is a
// nullsub) and the book's tick `sub_82990730(h+44)` (0x826B65D4: the preview's per-frame update).
REX_EXTERN(rex_lhGuiSelectionScreen_Update) {
  REX_FUNC_PROLOGUE();
  OwnerScope owner(base, 26u);
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  // The two page fills state the level's pages (page_fills_stated.cpp): scope "row", keyed by the
  // level the edge leaves at h+348 (0x826B6580).
  // The guest's upgrade ends with h+348 = -1 (`li r11,-1 ; stw r11,0x15C(r27)` 0x826B6D58 / disc
  // 0x8259BEC0), so the edge refills both pages for the level now installed.
  if (g_furniture_refill) {
    g_furniture_refill = false;
    StoreU32(base, h + 0x15Cu, 0xFFFFFFFFu);
  }
  refii::vfs::OpenPageFill(26u, "row");
  Call(ctx, base, s.frame, 0x826B64D8u, {h});
  refii::vfs::ClosePageFill(LoadU32(base, h + 0x15Cu));
  // OnEnter states hero one's gold (0x826B6158 `li r5,1` / disc 0x8259B348); player two's purse.
  RestatePurseForPlayerTwo(ctx, base, s.frame, BookPresentation(base), 0x8349AE9Cu);
  if (!g_furniture_update_said) {
    g_furniture_update_said = true;
    Said("upgrade furniture (26)", "Update", h, LoadU32(base, h + 0x15Cu));
  }
}

// OnScreenAction TU1 0x826B6D98 / disc 0x8259BF00 (the guest's: player one only, `cmplwi r5, 1; bne` 0x826B6DAC;
// jump table 0x826B6DD8 / 0x8259BF40, index code − 26, read from the image): 26 / 27 the open page's
// input (behind h+84, `sub_829907F0` 0x826B6E9C), 36 a sound and `lhGuiFurnitureScreen_Upgrade(h)`
// (0x826B6E30), 37 `ExitScreen(QM, 1)` and the preview's Hide (0x826B6E58 / 0x826B6E60). Godot posts A
// as `Furniture:<p>:upgrade:<level>` (the pump's DispatchFurnitureAction → RefiiFurnitureUpgrade) and
// B as `Furniture:<p>:close` (DispatchFurnitureClose). Nothing kept.
REX_EXTERN(rex_lhGuiSelectionScreen_OnScreenAction) {
  REX_FUNC_PROLOGUE();
  Said("upgrade furniture (26)", "OnScreenAction", ctx.r3.u32, ctx.r4.u32, ctx.r5.u32);
}

// ---- THE LEVERS -----------------------------------------------------------------------------

namespace refii::vfs {

void FurnitureUpgraded() { g_furniture_refill = true; }

bool DispatchFurnitureClose(PPCContext& ctx, uint8_t* base, uint32_t player) {
  // The handler's B — the guest's for player one alone (0x826B6DAC / disc 0x8259BF14), the owner's here
  // (the departure stated at OnEnter; Godot routes the owner's pad alone): `ExitScreen(QM, 1)`
  // (0x826B6E58 / disc 0x8259BFC0), then the preview's Hide `sub_828C3FD8(h+128)` (0x826B6E60 /
  // 0x82834DA0).
  const Scratch s = Claim(ctx);
  const uint32_t h = ScreenHandler(ctx, base, s.frame, 26u, kFurnitureVtable);
  if (h == 0u) {
    REXLOG_INFO("Refii guest-tick pump: Furniture:{}:close — no furniture handler, dropped", player);
    return true;
  }
  Call(ctx, base, s.frame, 0x8236ED98u, {LoadU32(base, Gui(base) + 28u), 1u});
  Call(ctx, base, s.frame, 0x828C3FD8u, {h + 0x80u});
  REXLOG_INFO("Refii guest-tick pump: Furniture:{}:close — the screen left, the preview hidden", player);
  return true;
}

}  // namespace refii::vfs
