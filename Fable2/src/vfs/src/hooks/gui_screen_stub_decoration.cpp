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
// Three slots stay the guest's. OnEnter (TU1 0x826B6FE8 / disc 0x8259C200) is the book's registration,
// the population, the gold text, the title's tag, h+88 and h+348 = -1 around one open sound and one
// slide. OnMenuEvent (0x826B65F0 / 0x8259C768) is ONE BODY SHARED with upgrade furniture 26 and the
// co-op screen 24, OnExit (0x826B6190 / 0x8259B380) ONE BODY SHARED with 26 and gift 25: neither is
// ever replaced. The dtor (0x826B5FE8 / 0x8259C150) is shared with 26.

namespace {

constexpr uint32_t kDecorationVtable = 0x820CED18u;

/// The mounted trophy's row id (`sub_8299DD88` 0x8299DFBC; the A arm's own test 0x826B7880).
constexpr uint32_t kMountedRow = 0x0FFFFFFFu;

bool g_decoration_update_said = false;

}  // namespace

// Update TU1 0x826B7510 / disc 0x8259C728. Kept: the highlight edge `sub_826B7438(h)` / 0x8259C650
// whole (0x826B752C) — the highlighted row (`lhGuiMenu_GetHighlightedItemData` 0x826B74BC, answered
// natively from the row Godot states) and, on an id unlike h+348, the two page fills
// `sub_8299C008(h+96)` / `sub_8299CF80(h+320)` (0x826B74D4 / 0x826B74DC; disc 0x828F1BB8 /
// 0x828F2B28: the item from the row's ticket, the preview's Set, name and description) and h+348 =
// the id (0x826B74E0). Dropped: the child tick `sub_826D5D00` (0x826B7524; this list's vf+8 is a
// nullsub) and the book's tick `sub_82990730(h+44)` (0x826B7534: the preview's per-frame update).
REX_EXTERN(rex_lhGuiDecorationScreen_Update) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  Call(ctx, base, s.frame, 0x826B7438u, {h});
  // The kept OnEnter states hero one's gold (0x826B70B0 `li r5,1` / disc 0x8259C2C8); player two's purse.
  RestatePurseForPlayerTwo(ctx, base, s.frame, BookPresentation(base), 0x8349AE9Cu);
  if (!g_decoration_update_said) {
    g_decoration_update_said = true;
    Said("decoration (27)", "Update", h, LoadU32(base, h + 0x15Cu));
  }
}

// OnScreenAction TU1 0x826B7770 / disc 0x8259CA10 (player one only, `cmplwi r5, 1; bne` 0x826B7788;
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
  const Scratch s = Claim(ctx);
  const uint32_t h = ScreenHandler(ctx, base, s.frame, 27u, kDecorationVtable);
  if (h == 0u || player != 1u) {
    REXLOG_INFO("Refii guest-tick pump: Decoration:{}:{} — {}", player, verb,
                h == 0u ? "no decoration handler, dropped" : "the screen takes player one's buttons alone, refused");
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
