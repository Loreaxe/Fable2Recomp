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

// ---- GIFT GIVING (25; disc 24) — vtable 0x820CF584 / 0x820C1DF8 ------------------------------
//
// h+24 the row list (`lhGuiGiftGoldScreen_PopulateMenu` 0x82430468 / 0x823A45F8), h+44 its filter (1
// gifts, 5 orb), h+48 the mode the openers write (0 gift, 1 orb), h+52 the book, h+96 the item page (its
// preview at h+128), h+352 the last highlighted row id, h+356 the orb recipient's name. Same offsets on
// both builds.
//
// Four slots stay the guest's. OnEnter (TU1 0x826C4318 / disc 0x825A02B8) is the filter, PopulateMenu and
// the book around one open sound. OnMenuEvent (0x826C45C8 / 0x825A0568) and OnModalAnswer (0x826C4CC0 /
// 0x825A0C58) are the gift's decision tree with the guest's own boxes — query 15 for an item, the numeric
// picker for gold, then the commit and `ExitScreen` — run on the row Godot names (`Gift:<p>:item`, and
// the gold row's `MenuEvent` type 7). OnExit (0x826B6190 / 0x8259B380) is ONE BODY SHARED with screens 26
// and 27 and is never replaced.

namespace {

constexpr uint32_t kGiftVtable = 0x820CF584u;

bool g_gift_update_said = false;

}  // namespace

// Update TU1 0x826C4568 / disc 0x825A0508. Kept: the highlight edge `sub_826C4468` / 0x825A0408 — the
// highlighted row (`lhGuiMenu_GetHighlightedItemData` 0x826C44E4, answered natively from the row Godot
// states); on a new id the item page's fill `sub_829AA588(h+96)` (0x826C44FC / 0x828FC2F8: name,
// description, value, the preview's Set) and h+352 = the id (0x826C4500); on a category row (type 301)
// the preview's Hide `sub_828C3FD8(h+128)` (0x826C4534 / 0x82834DA0) — and the gold text
// `sub_8246B5E8(pres, 0x8349B088, 1)` (0x826C45AC / 0x82837218 with 0x83498ED8). Dropped: the child tick
// `sub_826D5D00` (0x826C457C), the book's slide `sub_82990A98` (0x826C454C) and tick `sub_82990730`
// (0x826C458C).
REX_EXTERN(rex_lhGuiGiftGivingScreen_Update) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  uint32_t type = 0u;
  uint32_t id = 0u;
  HighlightedRow(ctx, base, s.frame, s.data, s.data + 0x40u, &type, &id);
  if (id != LoadU32(base, h + 0x160u)) {
    Call(ctx, base, s.frame, 0x829AA588u, {h + 0x60u});
    StoreU32(base, h + 0x160u, id);
  }
  if (type == 301u) {
    Call(ctx, base, s.frame, 0x828C3FD8u, {h + 0x80u});
  }
  Call(ctx, base, s.frame, 0x8246B5E8u, {BookPresentation(base), Address(0x8349B088u), 1u});
  RestatePurseForPlayerTwo(ctx, base, s.frame, BookPresentation(base), 0x8349B088u);
  if (!g_gift_update_said) {
    g_gift_update_said = true;
    Said("gift (25)", "Update", h, type, id);
  }
}

// OnScreenAction TU1 0x826C4BC0 / disc 0x825A0B58 (player one only, `cmplwi r5, 1; bnelr`; jump table
// index code − 26): 26 / 27 the book's page turn (behind h+92), 36 Lua OnPressA + sound, 37 B
// `sub_826C4C50`. Godot posts B as `Gift:<p>:close` (DispatchGiftClose). Nothing kept.
REX_EXTERN(rex_lhGuiGiftGivingScreen_OnScreenAction) {
  REX_FUNC_PROLOGUE();
  Said("gift (25)", "OnScreenAction", ctx.r3.u32, ctx.r4.u32, ctx.r5.u32);
}

// ---- THE LEVERS -----------------------------------------------------------------------------

namespace refii::vfs {

bool DispatchGiftClose(PPCContext& ctx, uint8_t* base, uint32_t player) {
  // The handler's B, for player one alone (0x826C4BC0 `cmplwi r5, 1`): the guest's own helper
  // `sub_826C4C50(h)` / 0x825A0BE8 whole — at the list's root (`sub_826C1D98` 0x826C4C78)
  // `ExitScreen(QM, 1)` and the preview's Hide `sub_828C3FD8(h+128)` (0x826C4C94 / 0x826C4C9C), then the
  // shared back `sub_826D5AF0(h)` (0x826C4CA4).
  const Scratch s = Claim(ctx);
  const uint32_t h = ScreenHandler(ctx, base, s.frame, 25u, kGiftVtable);
  if (h == 0u || player != 1u) {
    REXLOG_INFO("Refii guest-tick pump: Gift:{}:close — {}", player,
                h == 0u ? "no gift handler, dropped" : "the screen takes player one's B alone, refused");
    return true;
  }
  Call(ctx, base, s.frame, 0x826C4C50u, {h});
  REXLOG_INFO("Refii guest-tick pump: Gift:{}:close — the guest's B ran (sub_826C4C50)", player);
  return true;
}

}  // namespace refii::vfs
