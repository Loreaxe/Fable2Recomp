// THE PORTED SCREENS (S7): the bodies several handler vtables share. Each is a full replacement that
// serves every vtable it sits in; the image was searched for every dword holding the address.
#include <atomic>
#include <cstdint>

#include <rex/logging.h>

#include "core/guest_build.h"
#include "core/refii_mod.h"
#include "hooks/gui_screen_stub_support.h"
#include "hooks/gui_screen_stubs.h"
#include "hooks/ppc_externs.h"
#include "vfs_hooks_private.h"

using namespace refii::vfs::stub;
using refii::mods::LoadU32;

// The base class's slot +4, TU1 0x826D5BD8 / disc 0x82583428: one body in the +4 of 21 / 20 screen
// handler vtables (TU1 0x820A8D70, 0x820A8DE4, 0x820A8E10, 0x820AE3F0, 0x820C0E08, 0x820CDE18,
// 0x820CDEA4, 0x820CE068, 0x820CE708, 0x820CEA58, 0x820CECEC, 0x820CED18, 0x820CED6C, 0x820CF4BC,
// 0x820CF584, 0x820CF704, 0x820CF944, 0x820CFEB0, 0x820CFEDC, 0x820D0110, 0x820D013C), called by each
// guest OnEnter as `h` vf+4 and by the +8 thunk 0x82C8D1A0 / 0x82583480 (`lwz r11,0(r3); lwz r10,4(r11);
// mtctr r10; bctr`). The whole body is `sub_82371E80(&ev, G, 0x83497DEC "SE_GUI_PREVIOUS_PAGE", 0)`
// (0x826D5BFC / disc `sub_82306098` with 0x83498730) and the event's release `ev+4` vf+4 (0x826D5C18):
// a UI sound. Nothing kept.
REX_EXTERN(rex_lhGuiScreen_OpenSound) {
  REX_FUNC_PROLOGUE();
  static std::atomic<bool> said{false};
  if (!said.exchange(true)) {
    Said("every screen", "open sound (+4)", ctx.r3.u32);
  }
}

// OnMenuEvent (+20) of upgrade furniture (26), decoration (27) and the co-op screen (24), TU1
// 0x826B65F0 / disc 0x8259C768: one body in three vtables (TU1 0x820CECEC, 0x820CED18, 0x820B3C8C;
// disc 0x820C18E8, 0x820C1914, 0x820B4DC0). `lwz r11,4(r4)`; type 141 → `sub_82371E80(&ev, G,
// 0x83497DDC "SE_GUI_SLIDE_MENU_DOWN", 0)` (0x826B6628), type 140 → the same with 0x83497DD8
// "SE_GUI_SLIDE_MENU_UP" (0x826B665C), each released by `sub_82C05EF8`; any other type returns. Nothing
// kept.
REX_EXTERN(rex_lhGuiScreen_OnMenuEventNavSoundsOnly) {
  REX_FUNC_PROLOGUE();
  Said("furniture / decoration / co-op", "OnMenuEvent", ctx.r3.u32, LoadU32(base, ctx.r4.u32 + 4u),
       LoadU32(base, ctx.r4.u32 + 8u));
}

// OnExit (+36) of gift giving (25), upgrade furniture (26) and decoration (27), TU1 0x826B6190 / disc
// 0x8259B380: one body in three vtables (TU1 0x820CF584, 0x820CECEC, 0x820CED18; disc 0x820C1DF8,
// 0x820C18E8, 0x820C1914). Kept whole: the base exit `sub_826D5C30(h)` (0x826B61A4 / 0x82583490: `h`
// vf+12, the child vector h+12..h+16 erased, the row list's rows destroyed, the tab strip's hide) and
// the preview's release `sub_828C3FD8(h+128)` (0x826B61AC / 0x82834DA0: its "shown" byte +64 cleared,
// its view id +68 queued on G+144's free list and set to -1 — the hooked `@PreviewItem.Hide`
// statement). All three screens keep their preview at h+128.
REX_EXTERN(rex_lhGuiPreviewScreen_OnExit) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  Call(ctx, base, s.frame, 0x826D5C30u, {h});
  Call(ctx, base, s.frame, 0x828C3FD8u, {h + 0x80u});
  Said("gift / furniture / decoration", "OnExit", h);
}

// The villager card's close helper `sub_826CCC10(h)` / disc `sub_82583938` — not a vtable slot: the
// card's OnExit (+36, TU1 0x826CC9A0 / disc 0x825836C8) is `b sub_826CCC10`, and `lhGuiManager_ExitScreen`
// (0x8236EF4C / 0x8233714C) and the `Query:<p>:close` lever call it. Kept: behind the "open" byte h+24
// (0x826CCC20), h+24 = 0 (0x826CCC34) and the card's registration undone,
// `sub_827F1A48(*(*(G+32)+32), *(*(G+56)+52))` (0x826CCC4C / disc `sub_827CE400`: the registry's
// listeners' vf+8 on the card presentation's root). Dropped: `sub_82371E80(&ev, G, 0x83497EA4
// "SE_GUI_PREVIOUS_PAGE", 0)` and its release (0x826CCC64 / 0x826CCC6C; disc 0x8258398C).
REX_EXTERN(rex_lhGuiQueryScreen_Close) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  if (base[h + 24u] == 0u) {
    return;
  }
  base[h + 24u] = 0u;
  refii::vfs::JoinerCardClosed(h);
  const Scratch s = Claim(ctx);
  const uint32_t g = Gui(base);
  Call(ctx, base, s.frame, 0x827F1A48u,
       {LoadU32(base, LoadU32(base, g + 32u) + 32u), LoadU32(base, LoadU32(base, g + 56u) + 52u)});
  Said("villager card (10)", "close (OnExit)", h);
}

// THE OPENER'S PLAYER, for the screens whose openers state no owner of the GUI (the pause root's +4,
// StateGuiOwner): the gift (25) from the expression wheel and upgrade furniture (26) / decoration (27)
// from the player-interact ability. Each opener is a player's own act; it is noted here and stated by
// the screen's OnEnter (TakeOpenerPlayer). Local player two's act is player two's screen; an online
// joiner's own act is player one's on its own machine (its input object is App+0x14), as the guest's
// own statements make it. The guest's handlers of these screens take player one's input alone
// (`cmplwi cr6, r5, 1`); Refii lets their owner drive them instead (both players are principals).
extern "C" uint8_t* RefiiGuestBase(void);

namespace {

/// The wheel's owner read before its close, waiting for the gift it opens. Guest tick thread only.
uint32_t g_wheel_gift_player = 0u;

uint32_t OpenerPlayer(uint8_t* base, bool player_two) {
  return player_two && base != nullptr && refii_mod_coop_mode(base) == 1 ? 2u : 1u;
}

}  // namespace

// The expression wheel's perform `sub_826AA790(W, …)` (disc `sub_8258DED0`), W in r24 (`mr r24, r3`
// 0x826AA79C / 0x8258DEDC), on a giftable item: the wheel's close `lhGuiManager_CloseExpressionWheel`
// (0x826AAC2C / 0x8258E36C) clears its owner W+24 (`sub_826A9E58`: `li r11, -1` 0x826AA018 / `stw r11,
// 0x18(r30)` 0x826AA020; disc 0x8258D760), the player id its open stored (`sub_826A9A60` 0x826A9A80). So
// the owner is read at the close's first instruction, 0x826AAC20 / 0x8258E360.
void RefiiWheelGiftOwner(PPCRegister& r24) {
  uint8_t* base = RefiiGuestBase();
  const uint32_t wheel = r24.u32;
  const bool two = base != nullptr && Plausible(wheel) && LoadU32(base, wheel + 24u) == 2u;
  g_wheel_gift_player = OpenerPlayer(base, two);
}

// … and noted when the gift opens: `bl sub_823C4868` 0x826AAC50 / `bl sub_824F9A98` 0x8258E390 (the
// branch on `sub_8230F3A0` 0x826AAC34 / `sub_82302FF8` 0x8258E374 between them opens nothing).
void RefiiWheelGiftOpens() {
  NoteOpenerPlayer(g_wheel_gift_player);
  g_wheel_gift_player = 0u;
}

// The player-interact ability `sub_82976588(ability, actor)` (disc `sub_829A5B20`), the actor in r22
// (`mr r22, r4` 0x82976598 / 0x829A5B30, its one writer), at its `lhGuiManager_SetScreen(QM, 0x1A, 0)`
// 0x82977654 (disc 0x19 at 0x829A6C6C) and `(QM, 0x1B, 0)` 0x829777A4 (disc 0x1A at 0x829A6DBC).
void RefiiInteractScreenOwner(PPCRegister& r22) {
  uint8_t* base = RefiiGuestBase();
  const bool two = base != nullptr && r22.u32 != 0u && r22.u32 == refii_mod_player_hero(base, 2);
  NoteOpenerPlayer(OpenerPlayer(base, two));
}
