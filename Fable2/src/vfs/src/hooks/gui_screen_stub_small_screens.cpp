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

/// Screen 13, the next-episode advert, exists in TU1 only (the disc has no screen 13): its slots and
/// its close `sub_823D05A8` bind only there, so they are weak here.
extern "C" __attribute__((weak)) REX_FUNC(__imp__rex_lhGuiNextEpisodeScreen_Close);
extern "C" __attribute__((weak)) REX_FUNC(__imp__rex_lhGuiNextEpisodeScreen_Update);
extern "C" __attribute__((weak)) REX_FUNC(__imp__rex_lhGuiNextEpisodeScreen_HandleAction);

using namespace refii::vfs::stub;
using refii::guest::Address;
using refii::mods::LoadU32;
using refii::mods::StoreU32;

// ---- PHOTO MODE (1) — vtable 0x820CDEA4 / 0x820C0308; no opener on either build ------------
//
// Update TU1 0x826A88D8 / disc 0x825815A0: base tick + the upload dialog's update — drawing.
// OnScreenAction TU1 0x826A8918 / disc 0x825815E0: the dialog's input, and A's Live screenshot
// upload request `sub_8298FA38` (0x826A8960 / disc `sub_828460C0`) — a platform upload with no
// service here. Nothing kept.
REX_EXTERN(rex_lhGuiPhotoScreen_Update) {
  REX_FUNC_PROLOGUE();
}

REX_EXTERN(rex_lhGuiPhotoScreen_OnScreenAction) {
  REX_FUNC_PROLOGUE();
  Said("photo (1)", "OnScreenAction", ctx.r3.u32, ctx.r4.u32, ctx.r5.u32);
}

// ---- CREDITS (29; disc 28) — vtable 0x820CED6C / 0x820C1968 ---------------------------------
//
// Update TU1 0x826B7D00 / disc 0x8259CFA0: the scroll (SetText / position per line) and the close
// at the roll's end `sub_82370178(QM)` (0x826B7E3C). Godot rolls the lines and posts `CreditsEnd`
// at the same moment (DispatchCreditsEnd). Nothing kept.
REX_EXTERN(rex_lhGuiCreditsScreen_Update) {
  REX_FUNC_PROLOGUE();
}

// OnMenuEvent TU1 0x826B7E50 / disc 0x8259D0F0: type 201 named `ReturnToGame` → the quiet exit
// `sub_82370178(QM)` (0x826B7EE0 / disc 0x82338370). Kept.
REX_EXTERN(rex_lhGuiCreditsScreen_OnMenuEvent) {
  REX_FUNC_PROLOGUE();
  const uint32_t event = ctx.r4.u32;
  const uint32_t name = LoadU32(base, event);
  const uint32_t chars = Plausible(name) ? LoadU32(base, name) : 0u;
  const bool ret = LoadU32(base, event + 4u) == 201u && Plausible(chars) &&
                   std::strstr(reinterpret_cast<const char*>(base + chars), "ReturnToGame") != nullptr;
  Said("credits (29)", "OnMenuEvent", ctx.r3.u32, LoadU32(base, event + 4u), ret ? 1u : 0u);
  if (ret) {
    const Scratch s = Claim(ctx);
    Call(ctx, base, s.frame, 0x82370178u, {LoadU32(base, Gui(base) + 28u)});
  }
}

// OnScreenAction TU1 0x826B7EF8 / disc 0x8259D198: codes 36..37 only. Online co-op (game+284 == 1):
// the host closes `sub_82370178(QM)` (0x826B7F6C) and tells the peer, message {vt 0x820A6600 /
// 0x820A4A24, byte 0} through `sub_8255C0D8(net, &msg, 0)` (0x826B7F90 / disc `sub_82526B78`
// 0x8259D230); a joiner does nothing. Otherwise the close (0x826B7FB4). Kept whole.
REX_EXTERN(rex_lhGuiCreditsScreen_OnScreenAction) {
  REX_FUNC_PROLOGUE();
  const uint32_t code = ctx.r4.u32;
  Said("credits (29)", "OnScreenAction", ctx.r3.u32, code, ctx.r5.u32);
  if (code < 36u || code > 37u) {
    return;
  }
  const Scratch s = Claim(ctx);
  const uint32_t qm = LoadU32(base, Gui(base) + 28u);
  const uint32_t root = LoadU32(base, Address(0x83496AB8u));
  const uint32_t game = LoadU32(base, root + 12u);
  if (LoadU32(base, game + 0x11Cu) != 1u) {
    Call(ctx, base, s.frame, 0x82370178u, {qm});
    return;
  }
  const uint32_t net = LoadU32(base, game + 0x8Cu);
  if (Load8(base, net + 0x34u) == 0u || Load8(base, net + 0x35u) == 0u) {
    return;
  }
  Call(ctx, base, s.frame, 0x82370178u, {qm});
  StoreU32(base, s.data, Address(0x820A6600u));
  base[s.data + 4u] = 0u;
  Call(ctx, base, s.frame, 0x8255C0D8u, {net, s.data, 0u});
}

// ---- MINIGAME COMPLETED (11) — vtable 0x820AE3F0 / 0x820C09D4 -------------------------------
//
// Update TU1 0x823CDBA0 / disc 0x8258CAD8: base tick + the count-up texts — drawing (Godot counts
// up from the stated amounts). Nothing kept.
REX_EXTERN(rex_lhGuiMinigameCompletedScreen_Update) {
  REX_FUNC_PROLOGUE();
}

// HandleAction (+24) TU1 0x823CDC08 / disc 0x8258CB40: code 6 → the close `sub_823CDF10` /
// `sub_8258CE48` (0x823CDCA0): h+40 = 0, the rows' toggles off, the presentation unregistered,
// `ExitScreen(QM, 1)`. The count-up snap before it is Godot's; Godot sends 6 once its rows are
// settled. OnExit +36 is the same close (`b sub_823CDF10`) and stays the guest's.
REX_EXTERN(rex_lhGuiMinigameCompletedScreen_HandleAction) {
  REX_FUNC_PROLOGUE();
  Said("minigame (11)", "HandleAction", ctx.r3.u32, ctx.r4.u32, ctx.r5.u32);
  if (ctx.r4.u32 == 6u) {
    const Scratch s = Claim(ctx);
    Call(ctx, base, s.frame, 0x823CDF10u, {ctx.r3.u32});
  }
}

// ---- QUEST COMPLETION (12) — vtable 0x820A8DE4 / 0x820A5ABC ---------------------------------
//
// HandleAction (+24) TU1 0x823D3F98 / disc 0x824F2348: code 6; player 1 always, player 2 only on
// a joiner (host = net+0x34 && +0x35, 0x823D3FB8..0x823D3FE0) → the close `sub_823D4750` /
// `sub_824F2B58` (host: packet 0xE6 to the peer; ExitScreen; joiner: `sub_8232B2B0`). Kept whole.
// Update is the shared base and OnExit (base exit + the same close) stays the guest's.
REX_EXTERN(rex_lhGuiQuestCompletionScreen_HandleAction) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const uint32_t code = ctx.r4.u32;
  const uint32_t player = ctx.r5.u32;
  Said("quest completion (12)", "HandleAction", h, code, player);
  if (code != 6u) {
    return;
  }
  if (player != 1u) {
    const uint32_t root = LoadU32(base, Address(0x83496AB8u));
    const uint32_t net = LoadU32(base, LoadU32(base, root + 12u) + 0x8Cu);
    if (Load8(base, net + 0x34u) != 0u && Load8(base, net + 0x35u) != 0u) {
      return;
    }
  }
  const Scratch s = Claim(ctx);
  Call(ctx, base, s.frame, 0x823D4750u, {h});
}

// ---- NEXT EPISODE ADVERT (13, TU1 only — the disc has no such screen) — vtable 0x820A8E10 ----
//
// Update TU1 0x823D0268: while open (h+24), the countdown h+40 steps down; at 0 it reveals the
// prompt (`lhGuiRegistry_SetToggle`, 0x823D02D0) — drawing. The countdown is kept: it gates input.
REX_EXTERN(rex_lhGuiNextEpisodeScreen_Update) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  if (Load8(base, h + 24u) == 0u) {
    return;
  }
  const int32_t left = static_cast<int32_t>(LoadU32(base, h + 40u));
  if (left > 0) {
    StoreU32(base, h + 40u, static_cast<uint32_t>(left - 1));
  }
}

// HandleAction (+24) TU1 0x823D00E8: refused while h+40 > 0 (0x823D0100); player 2 only on a
// joiner (0x823D0104..0x823D0144); code 6 → the close `sub_823D05A8` (0x823D0150; h+24 = 0, the
// presentation unregistered, `ExitScreen(QM, 1)`). Kept. Code 38's
// `XamShowMarketplaceUI` (0x823D01B4) is the console store, absent here: dropped.
REX_EXTERN(rex_lhGuiNextEpisodeScreen_HandleAction) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  Said("next episode (13)", "HandleAction", h, ctx.r4.u32, ctx.r5.u32);
  if (static_cast<int32_t>(LoadU32(base, h + 40u)) > 0 || ctx.r4.u32 != 6u) {
    return;
  }
  if (ctx.r5.u32 != 1u) {
    const uint32_t root = LoadU32(base, Address(0x83496AB8u));
    const uint32_t net = LoadU32(base, LoadU32(base, root + 12u) + 0x8Cu);
    if (Load8(base, net + 0x34u) != 0u && Load8(base, net + 0x35u) != 0u) {
      return;
    }
  }
  if (&__imp__rex_lhGuiNextEpisodeScreen_Close != nullptr) {
    PPCContext work = ctx;
    work.r1.u32 = Claim(ctx).frame;
    work.r3.u32 = h;
    __imp__rex_lhGuiNextEpisodeScreen_Close(work, base);
  }
}

namespace refii::vfs {

bool DispatchCreditsEnd(PPCContext& ctx, uint8_t* base) {
  const uint32_t g = Gui(base);
  const Scratch s = Claim(ctx);
  Call(ctx, base, s.frame, 0x82370178u, {LoadU32(base, g + 28u)});
  REXLOG_INFO("Refii guest-tick pump: CreditsEnd — the roll ended, quiet exit");
  return true;
}

}  // namespace refii::vfs
