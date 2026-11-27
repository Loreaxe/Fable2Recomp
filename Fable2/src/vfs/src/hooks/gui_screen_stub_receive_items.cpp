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
#include <cstdio>
#include <cstring>
#include <initializer_list>
#include <string>

#include <rex/logging.h>

#include "core/guest_build.h"
#include "core/mod_values.h"
#include "core/refii_mod.h"
#include "hooks/gui_screen_stub_support.h"
#include "hooks/gui_screen_stubs.h"
#include "hooks/ppc_externs.h"
#include "vfs_hooks_private.h"


using namespace refii::vfs::stub;
using refii::guest::Address;
using refii::mods::LoadU32;
using refii::mods::StoreU32;

// ---- THE RECEIVED-ITEM CARD (8 on both builds) — vtable 0x820C0E08 / 0x820AEE60 --------------
//
// The card is pushed by five openers, all the guest's: an item `sub_823C4340` (SetScreen(8),
// SetActiveScreen, `CGUIReceiveItems_SetItem`, EnterGuiMode), the co-op partner's item `sub_823C4408`
// (`SetPeerItem`), money `sub_823C44E8`, a discount `sub_823C45E8` and "work harder" `sub_823C46B0`
// (disc 0x824F9570 / 0x824F9638 / 0x824F9718 / 0x824F9818 / 0x824F98E0). Each fills the handler —
// +52 the side (1 front), +56 the icon's resource hash, +60 / +64 the title and description text keys,
// +68 the extra line's key and +72 its amount (money, discount), +76 the NameAndIcon line, +80 "can be
// used now", +96 the 3D view (its +0 the record, +124 the model), +324 "the partner's card" — and leaves
// +81 = 0, "not drawn": SetItem and SetPeerItem then call the card's draw `sub_82538910(h)` themselves
// (0x8253824C / 0x82538348), the other three leave it to the first Update (0x82537058). That draw is the
// one call that presents every card, and it is where the card is stated.
//
// OnLeave (TU1 0x82536F70 / disc 0x82402710) runs on close and on replace, and states the card down.

namespace {

/// Host-only record kind; the host side names it Engine_UiReceiveItemsSurface.StateKind.
constexpr int32_t kCardKind = 1006;

constexpr uint32_t kCardVtable = 0x820C0E08u;

/// The NameAndIcon line at `handle`: a guest string, two hops, NUL-terminated. Quotes, commas and line
/// breaks are dropped so the value stays one quoted token on the wire.
std::string NameLine(const uint8_t* base, uint32_t handle) {
  const uint32_t block = LoadU32(base, handle);
  const uint32_t chars = Plausible(block) ? LoadU32(base, block) : 0u;
  std::string out;
  for (uint32_t i = 0; Plausible(chars) && i < 128u; ++i) {
    const char c = static_cast<char>(base[chars + i]);
    if (c == 0) break;
    if (c == '\'' || c == ',' || c == '\n' || c == '\r') continue;
    out.push_back(c);
  }
  return out;
}

}  // namespace

// THE CARD'S DRAW, `sub_82538910(h)` (TU1 0x82538910 / disc 0x824040B0): the guest's presentation of
// the handler it is handed — the title `SetText(TextManager_GetString(h+60))` (0x82538984), the icon
// element's texture h+56 (0x825389F0), the description h+64 (0x82538A44), the line h+76 (0x82538A78),
// the use prompt's slide by h+80 on the side h+52 (0x82538AC0 / 0x82538B08), "GUI_DONE" on the partner's
// card h+324 (0x82538B70). Replaced in full: its argument is stated as the card record (kind 1006) and
// none of those elements is written. Kept: h+81 = 1 (0x82538BD0) and the scroll region's
// `sub_828C3358(h+24)` (0x82538BD8 / 0x82833D40).
REX_EXTERN(rex_CGUIReceiveItems_DrawCard) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const uint32_t preview = h + 96u;
  char fields[512];
  std::snprintf(fields, sizeof(fields),
                "up=1,peer=%u,front=1,usable=%u,record=%u,model=%u,icon=%u,title=%u,desc=%u,extra=%u,amount=%u,"
                "name='%s'",
                base[h + 324u] != 0u ? 1u : 0u, base[h + 80u] != 0u ? 1u : 0u, LoadU32(base, preview),
                LoadU32(base, preview + 124u), LoadU32(base, h + 56u), LoadU32(base, h + 60u),
                LoadU32(base, h + 64u), LoadU32(base, h + 68u), LoadU32(base, h + 72u),
                NameLine(base, h + 76u).c_str());
  refii::mods::PostEvent(kCardKind, fields);
  base[h + 81u] = 1u;
  const Scratch s = Claim(ctx);
  Call(ctx, base, s.frame, 0x828C3358u, {h + 24u});
  REXLOG_INFO("Refii screen stub: received item (8) — the card stated ({})", fields);
}

// OnEnter TU1 0x82536E68 / disc 0x82402608. Kept: the description scroll region's reset
// `sub_828C2F38(h+24)` and bar `sub_828C33F0(h+24, 0x834986BC)` (0x82536ED0 / 0x82536EE0; disc 0x82833920 /
// 0x82833DD8 with 0x83497A2C), the region the kept draw and OnLeave act on; h+52 = 1, the front
// (0x82536EE4); the dismiss gate `sub_828C36D0(h+248, 1.0)` (0x82536F60 / 0x82834498: one second before
// Done is taken); h+324 = 0 (0x82536F64). Dropped: the spin's state h+284..h+301 and its idle timer
// (0x82536E94..0x82536EB0), the open sound vf+4 (0x82536EC4), the root's slide 1 (0x82536F48), the flip
// timer h+224 (0x82536F54).
REX_EXTERN(rex_lhGuiReceiveItemsScreen_OnEnter) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  Call(ctx, base, s.frame, 0x828C2F38u, {h + 24u});
  Call(ctx, base, s.frame, 0x828C33F0u, {h + 24u, Address(0x834986BCu)});
  base[h + 52u] = 1u;
  {
    PPCContext arm = ctx;
    arm.r1.u32 = s.frame;
    arm.r3.u32 = h + 248u;
    arm.f1.f64 = 1.0;
    refii::mods::CallGuest(arm, base, Address(0x828C36D0u));
  }
  base[h + 324u] = 0u;
  Said("received item (8)", "OnEnter", h);
}

// Update TU1 0x82536FB8 / disc 0x82402758. Kept: the first draw — h+81 clear → h+81 = 1 and
// `sub_82538910(h)` (0x82537050 / 0x82537058), which is what states the money, discount and work-harder
// cards (their openers never call SetItem). Dropped: the spin and the anchor's turn (0x82536FD0..
// 0x82537028), the child tick `sub_826D5D00` (0x82537030), the scroll tick `sub_828C2F98` (0x82537038), the
// redraw after a flip on the timer h+236 (0x82537060..0x82537090), the 3D view's draw `sub_828C3C78`
// (0x825370E4) and the flip timer's end (0x825370F0).
REX_EXTERN(rex_lhGuiReceiveItemsScreen_Update) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  if (base[h + 81u] == 0u) {
    base[h + 81u] = 1u;
    PPCContext draw = ctx;
    draw.r1.u32 = Claim(ctx).frame;
    draw.r3.u32 = h;
    rex_CGUIReceiveItems_DrawCard(draw, base);
  }
}

// OnScreenAction TU1 0x82537128 / disc 0x824028C8 (h, code, player): 27 the spin, the scroll input
// `sub_828C30A8`, 39 the flip (slide, sound, h+52 toggled, redraw) — Godot turns its own card; 36 Done
// and 75 Equip now are the levers `Card:<p>:dismiss|equip` (DispatchCard). Nothing kept.
REX_EXTERN(rex_lhGuiReceiveItemsScreen_OnScreenAction) {
  REX_FUNC_PROLOGUE();
  Said("received item (8)", "OnScreenAction", ctx.r3.u32, ctx.r4.u32, ctx.r5.u32);
}

// OnLeave (+36) TU1 0x82536F70 / disc 0x82402710, kept whole in the guest's order: the base exit
// `sub_826D5C30(h)` (0x82536F84 / 0x82583490); the scroll region's nodes freed `sub_827A0E18(h+32)`
// (0x82536F8C / 0x828581B0); the preview's release `sub_828C3FD8(h+96)` (0x82536F94 / 0x82834DA0); h+325
// = 0 (0x82536F9C). Then the card stated down — on close, and on replace before the next item's card.
REX_EXTERN(rex_CGUIReceiveItems_OnLeave) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  Call(ctx, base, s.frame, 0x826D5C30u, {h});
  Call(ctx, base, s.frame, 0x827A0E18u, {h + 32u});
  Call(ctx, base, s.frame, 0x828C3FD8u, {h + 96u});
  base[h + 325u] = 0u;
  refii::mods::PostEvent(kCardKind, "up=0");
  Said("received item (8)", "OnLeave", h);
}

// ---- THE LEVERS -----------------------------------------------------------------------------

namespace refii::vfs {

bool DispatchCard(PPCContext& ctx, uint8_t* base, uint32_t player, const std::string& verb) {
  // The handler's two closing arms (`sub_82537128`): 36 Done, behind the one-second gate
  // `sub_828C3748(h+248)` (0x82537400 / disc 0x82834510), and 75 Equip now, behind h+80 alone
  // (0x825371D8) — `sub_8246FFF0(*(h+96))` (0x825371E8 / 0x8283BB90: the record found in the local hero's
  // inventory and activated by category). Both end in the dismiss `sub_82538BF0(h, 1)` (0x825374D4 /
  // 0x825372B0; disc 0x82404390): `ExitScreen(QM, 1)`, the preview's Hide, the host's close packet to the
  // partner, a joiner's own-press pause. Dropped: the root's slide and the sounds around it.
  const Scratch s = Claim(ctx);
  const uint32_t h = ScreenHandler(ctx, base, s.frame, 8u, kCardVtable);
  if (h == 0u) {
    REXLOG_INFO("Refii guest-tick pump: Card:{}:{} — no card handler, dropped", player, verb);
    return true;
  }
  if (verb == "dismiss") {
    if ((Call(ctx, base, s.frame, 0x828C3748u, {h + 248u}) & 0xFFu) == 0u) {
      REXLOG_INFO("Refii guest-tick pump: Card:{}:dismiss — inside the card's first second, refused", player);
      return true;
    }
    Call(ctx, base, s.frame, 0x82538BF0u, {h, 1u});
    REXLOG_INFO("Refii guest-tick pump: Card:{}:dismiss — the card dismissed (sub_82538BF0)", player);
    return true;
  }
  if (verb == "equip") {
    if (base[h + 80u] == 0u) {
      REXLOG_INFO("Refii guest-tick pump: Card:{}:equip — the card's item cannot be used now, refused", player);
      return true;
    }
    const uint32_t record = LoadU32(base, h + 96u);
    Call(ctx, base, s.frame, 0x8246FFF0u, {record});
    Call(ctx, base, s.frame, 0x82538BF0u, {h, 1u});
    REXLOG_INFO("Refii guest-tick pump: Card:{}:equip — record 0x{:08X} activated, the card dismissed", player,
                record);
    return true;
  }
  REXLOG_INFO("Refii guest-tick pump: Card:{}:{} is not dismiss or equip — dropped", player, verb);
  return true;
}

}  // namespace refii::vfs
