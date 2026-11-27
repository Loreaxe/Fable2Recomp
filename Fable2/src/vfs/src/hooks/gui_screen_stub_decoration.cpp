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

// ---- DECORATION (27; disc 26) — vtable 0x820CED18 / 0x820C1914 -------------------------------
//
// h+24 the row list (vt 0x820EBE7C: +4 `sub_8299D5B0` / 0x828F3158 → `sub_8299DD88` / 0x828F3930 — the
// mounted trophy's row first, id 0x0FFFFFFF and emphasised, then one row per trophy held, its id the
// guest's own ticket for the item's handle `sub_82880AC8(menu+4, &handle)`; +8 a nullsub), h+44 the
// book, h+88 the local player's target mount (component 239, filed by OnEnter's `sub_826B7128`), h+96
// page 1 (its preview at h+128), h+320 page 2, h+348 the last highlighted row id (-1 none). Same
// offsets on both builds.
//
// Every slot is ported: OnEnter, Update and OnScreenAction below; OnMenuEvent (0x826B65F0 / 0x8259C768,
// one body shared with upgrade furniture 26 and the co-op screen 24) and OnExit (0x826B6190 /
// 0x8259B380, one body shared with 26 and gift 25) for every vtable they sit in
// (gui_screen_stub_shared.cpp). The dtor (0x826B5FE8 / 0x8259C150, shared with 26) frees and nothing
// else.

namespace {

constexpr uint32_t kDecorationVtable = 0x820CED18u;

/// The mounted trophy's row id (`sub_8299DD88` 0x8299DFBC; the A arm's own test 0x826B7880).
constexpr uint32_t kMountedRow = 0x0FFFFFFFu;

bool g_decoration_update_said = false;

}  // namespace

// OnEnter TU1 0x826B6FE8 / disc 0x8259C200. Kept, in the guest's order: the two pages h+96 and h+320
// pushed (`sub_82B49750` 0x826B7038 / 0x826B704C; disc 0x8228A900) and the book
// `sub_82990610(h+44, &pages, "Foreground.Book", pres, 1)` (0x826B707C / 0x828CE060), which runs each
// page's +4; the rows, h+24 vf+4 (0x826B70A0: `sub_8299D5B0` → `sub_8299DD88`); the gold text
// `sub_8246B5E8(pres, 0x8349AE9C, 1)` (0x826B70B4 / 0x82837218 with 0x83498D58); the title
// `sub_8246B888(pres, 0x8349AEA0 "Foreground.ExpandableMenu.Title.Group.Title", localised
// "GUI_SCREEN_TITLE_DECORATION")` (0x826B70C8 / 0x826B70D4 / 0x826B70E8; disc 0x828374B8 with
// 0x83498D5C) and the text's release (0x826B70F0); the mount binding `sub_826B7128(h, h+88)`
// (0x826B70FC / 0x8259C340: the local player's use target's component 239 filed at h+88); h+348 = -1
// (0x826B7108). `pres` is `*(*(G+56)+156)` (disc +152, 0x826B700C / 0x8259C224). Dropped: the open
// sound, `h` vf+4 (0x826B7014), and the book's slide `sub_82990A98(h+44, pres, 1)` (0x826B708C /
// 0x828CE4E8: `lhGuiElement_GoToSlideAndRestart`, the sound 0x83497DF0 "SE_GUI_BLADE_SLIDE_IN", and
// book+40 = h+84 = 1). h+84 stays 0, as the registration leaves it: its only readers are the slide
// itself, the page input `sub_829907F0` / `sub_829911D0` and OnScreenAction's own test (0x826B792C /
// 0x8259CBCC) — all reached from OnScreenAction alone, ported empty. ADDED, first: the GUI's owner
// stated as the opener's player (StateGuiOwner): the one opener, the player-interact ability
// (`lhGuiManager_SetScreen(QM, 0x1B, 0)` 0x829777A4 / disc 0x1A at 0x829A6DBC), states none; its actor
// is noted there (gui_screen_stub_shared.cpp RefiiInteractScreenOwner). A DELIBERATE DEPARTURE from the
// guest: the handler takes player one's input alone (OnScreenAction `cmplwi cr6, r5, 1` / `bne`
// 0x826B7788 / disc 0x8259CA28); here the screen's owner drives it, player two included. OnEnter, Update
// and the levers run for the owner (OwnerScope): the mount binding's "local player" is inlined
// (`sub_826B7128` 0x826B718C / 0x8259C3A4: the owner's use target, the mount in the house they stand
// in), as are the pages' (`sub_8299C008` 0x8299C0AC / 0x828F1C5C, `sub_8299CF80` 0x8299CFEC /
// 0x828F2B94); the mount `sub_826B7550` takes the trophy from the local player's hero (0x826B7610) and
// the take-down `sub_8243ED88` gives it back through the hero-one slot (0x8243F19C).
REX_EXTERN(rex_lhGuiDecorationScreen_OnEnter) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  const uint32_t pages = s.data;         // {?, begin, end, capacity}
  const uint32_t item = s.data + 0x10u;  // the page pointer handed to the push, then the book's name
  const uint32_t wide = s.data + 0x14u;  // the title's localised text
  const uint32_t tag = s.data + 0x18u;   // the title's tag, consumed by the localiser
  const uint32_t pres = BookPresentation(base);
  g_decoration_update_said = false;
  StateGuiOwner(ctx, base, s.frame, TakeOpenerPlayer() == 2u);
  OwnerScope owner(base, 27u);

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
  MakeString(ctx, base, s.frame, tag, s.data + 0x80u, "GUI_SCREEN_TITLE_DECORATION");
  const uint32_t title = Call(ctx, base, s.frame, 0x8230E0E8u, {wide, tag});
  Call(ctx, base, s.frame, 0x8246B888u, {pres, Address(0x8349AEA0u), title});
  Call(ctx, base, s.frame, 0x821C67F8u, {wide});
  Call(ctx, base, s.frame, 0x826B7128u, {h, h + 0x58u});
  StoreU32(base, h + 0x15Cu, 0xFFFFFFFFu);
  DropString(ctx, base, s.frame, item);
  const uint32_t storage = LoadU32(base, pages + 4u);
  if (storage != 0u) {
    Call(ctx, base, s.frame, 0x8221BD38u, {storage});  // Render_FreeD3DObject
  }
  Said("decoration (27)", "OnEnter", h, LoadU32(base, h + 0x58u));
}

// Update TU1 0x826B7510 / disc 0x8259C728. Kept: the highlight edge `sub_826B7438(h)` / 0x8259C650
// whole (0x826B752C) — the highlighted row (`lhGuiMenu_GetHighlightedItemData` 0x826B74BC, answered
// natively from the row Godot states) and, on an id unlike h+348, the two page fills
// `sub_8299C008(h+96)` / `sub_8299CF80(h+320)` (0x826B74D4 / 0x826B74DC; disc 0x828F1BB8 /
// 0x828F2B28: the item from the row's ticket, the preview's Set, name and description) and h+348 =
// the id (0x826B74E0). Dropped: the child tick `sub_826D5D00` (0x826B7524; this list's vf+8 is a
// nullsub) and the book's tick `sub_82990730(h+44)` (0x826B7534: the preview's per-frame update).
REX_EXTERN(rex_lhGuiDecorationScreen_Update) {
  REX_FUNC_PROLOGUE();
  OwnerScope owner(base, 27u);
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  Call(ctx, base, s.frame, 0x826B7438u, {h});
  // OnEnter states hero one's gold (0x826B70B0 `li r5,1` / disc 0x8259C2C8); player two's purse.
  RestatePurseForPlayerTwo(ctx, base, s.frame, BookPresentation(base), 0x8349AE9Cu);
  if (!g_decoration_update_said) {
    g_decoration_update_said = true;
    Said("decoration (27)", "Update", h, LoadU32(base, h + 0x15Cu));
  }
}

// OnScreenAction TU1 0x826B7770 / disc 0x8259CA10 (the guest's: player one only, `cmplwi r5, 1; bne` 0x826B7788;
// jump table 0x826B77B4 / 0x8259CA54, index code − 26, read from the image): 26 / 27 the open page's
// input (behind h+84), 36 A — a sound, then on a highlighted id that is not 0x0FFFFFFF (0x826B7888)
// the mount `sub_826B7550(h)`, `ExitScreen(QM, 1)` and the preview's Hide (0x826B7894..0x826B78AC) —
// 37 B `ExitScreen(QM, 1)` and the Hide (0x826B791C / 0x826B7924), 39 Y a sound, the take-down
// `sub_8243ED88(*(h+88))`, `ExitScreen(QM, 1)` and the Hide (0x826B78EC..0x826B7904). Godot posts
// them as `Decoration:<p>:mount:<ticket>`, `Decoration:<p>:close` and `Decoration:<p>:takedown`
// (DispatchDecoration). Nothing kept.
REX_EXTERN(rex_lhGuiDecorationScreen_OnScreenAction) {
  REX_FUNC_PROLOGUE();
  Said("decoration (27)", "OnScreenAction", ctx.r3.u32, ctx.r4.u32, ctx.r5.u32);
}

// ---- THE LEVERS -----------------------------------------------------------------------------

namespace refii::vfs {

bool DispatchDecoration(PPCContext& ctx, uint8_t* base, uint32_t player, const std::string& verb, uint32_t ticket) {
  // The handler's A / B / Y — the guest's for player one alone (0x826B7788 / disc 0x8259CA28), the
  // owner's here (the departure stated at OnEnter; Godot routes the owner's pad alone).
  const Scratch s = Claim(ctx);
  OwnerScope owner(base, 27u);
  const uint32_t h = ScreenHandler(ctx, base, s.frame, 27u, kDecorationVtable);
  if (h == 0u) {
    REXLOG_INFO("Refii guest-tick pump: Decoration:{}:{} — no decoration handler, dropped", player, verb);
    return true;
  }
  const auto leave = [&] {
    // Every arm's ending: `ExitScreen(QM, 1)`, then the preview's Hide `sub_828C3FD8(h+128)`.
    Call(ctx, base, s.frame, 0x8236ED98u, {LoadU32(base, Gui(base) + 28u), 1u});
    Call(ctx, base, s.frame, 0x828C3FD8u, {h + 0x80u});
  };

  if (verb == "close") {
    leave();
    REXLOG_INFO("Refii guest-tick pump: Decoration:{}:close — the screen left, the preview hidden", player);
    return true;
  }

  if (verb == "takedown") {
    // Y (0x826B78C4 / disc 0x8259CB64): no highlight read and no "is anything mounted" test — the
    // take-down `sub_8243ED88(*(h+88))` / 0x825A91F8 does nothing on an empty mount, and the arm
    // leaves the screen either way.
    Call(ctx, base, s.frame, 0x8243ED88u, {LoadU32(base, h + 0x58u)});
    leave();
    REXLOG_INFO("Refii guest-tick pump: Decoration:{}:takedown — sub_8243ED88 ran on mount 0x{:08X}, the screen left",
                player, LoadU32(base, h + 0x58u));
    return true;
  }

  // A (0x826B77EC / disc 0x8259CA8C). The mounted trophy's row does nothing at all, not even the
  // exit (`cmplw r9, 0x0FFFFFFF; beq` 0x826B7888). Else the guest's own helper `sub_826B7550(h)` /
  // 0x8259C7F0 whole, on the row the lever names — it asks for the highlighted row (0x826B75D0),
  // answered here with the lever's ticket; takes that ticket's item handle `sub_82880BC0(menu+4,
  // ticket, &handle)` (0x826B75F0 / 0x827BE1E8); with one, mounts it `sub_8243EA98(*(h+88), &handle)`
  // (0x826B7608 / 0x825A8F08) and takes it out of the local player's inventory `sub_824F12A0(comp13,
  // &handle, 0)` (0x826B7738 / 0x8261AF88) — then `ExitScreen(QM, 1)` and the Hide.
  if (ticket == kMountedRow) {
    REXLOG_INFO("Refii guest-tick pump: Decoration:{}:mount — the mounted trophy's own row, nothing done", player);
    return true;
  }
  SetDispatchedRow(std::string(), 0, static_cast<int32_t>(ticket));
  uint32_t type = 0u;
  uint32_t id = 0u;
  HighlightedRow(ctx, base, s.frame, s.data, s.data + 0x40u, &type, &id);
  if (id != ticket) {
    ClearDispatchedRow();
    REXLOG_INFO("Refii guest-tick pump: Decoration:{}:mount ticket 0x{:08X} — no row is stated for screen 27 "
                "(the highlight answers 0x{:08X}), not mounted",
                player, ticket, id);
    return true;
  }
  Call(ctx, base, s.frame, 0x826B7550u, {h});
  ClearDispatchedRow();
  leave();
  REXLOG_INFO("Refii guest-tick pump: Decoration:{}:mount ticket 0x{:08X} — sub_826B7550 ran on mount 0x{:08X}, "
              "the screen left",
              player, ticket, LoadU32(base, h + 0x58u));
  return true;
}

}  // namespace refii::vfs
