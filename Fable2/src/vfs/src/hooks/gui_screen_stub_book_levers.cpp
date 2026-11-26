// THE PORTED BOOKS' SHARED LEVERS (S7): B at a book's root and LB / RB, the spine calls the abilities
// (14), items (15), clothing (16), logbook (17), options (18) and world map (23) handlers made.
// guest_tick_pump.cpp dispatches them.
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

PPC_EXTERN_FUNC(rex_lhGuiManager_FindScreenHandler);

using namespace refii::vfs::stub;
using refii::guest::Address;
using refii::mods::LoadU32;
using refii::mods::StoreU32;

namespace refii::vfs {

bool DispatchPauseBack(PPCContext& ctx, uint8_t* base, uint32_t screen) {
  const uint32_t g = Gui(base);
  const uint32_t qm = Plausible(g) ? LoadU32(base, g + 28u) : 0u;
  PPCContext find = ctx;
  find.r3.u32 = qm;
  find.r4.u32 = refii::guest::ScreenId(screen);
  rex_lhGuiManager_FindScreenHandler(find, base);
  const uint32_t h = find.r3.u32;
  if (!Plausible(qm) || !Plausible(h)) {
    REXLOG_INFO("Refii guest-tick pump: PauseBack on screen {} — no handler, dropped", screen);
    return true;
  }
  const Scratch s = Claim(ctx);
  // The logbook opened on another player (`sub_823C48D8`: h+4 = 2 through vf+32, h+28 = 2 over the world
  // map `*(PS+4) == 23` else 1, 0x823C4924..0x823C4970) is told apart as its own B tells it: `lwz r11,
  // 4(r31)` / `cmpwi r11, 2` (0x826D9858), then `lwz r11, 0x1C(r31)` / `cmpwi r11, 2` (0x826D98B8; disc
  // 0x825824E8 / 0x82582548). Its OnExit zeroes both.
  const bool orb = screen == 17u && LoadU32(base, h + 4u) == 2u;
  if (orb && LoadU32(base, h + 28u) == 2u) {
    // The orb book over the world map (0x826D98C4..0x826D9960): SetScreen(QM, 23, 0), the map's
    // OnEnter, EnterGuiMode, the map's Update, `g_Menu:PopMenuState()`.
    PPCContext map = ctx;
    map.r3.u32 = qm;
    map.r4.u32 = refii::guest::ScreenId(23u);
    rex_lhGuiManager_FindScreenHandler(map, base);
    const uint32_t world = map.r3.u32;
    Call(ctx, base, s.frame, 0x8236E630u, {qm, refii::guest::ScreenId(23u), 0u});
    CallSlot(ctx, base, s.frame, world, 2u, {});
    Call(ctx, base, s.frame, 0x8236EB68u, {qm});
    CallSlot(ctx, base, s.frame, world, 4u, {});
    RunMenuLua(ctx, base, s.frame, s.data, "g_Menu:PopMenuState()", BookPresentation(base));
    REXLOG_INFO("Refii guest-tick pump: PauseBack — the orb logbook back to the world map");
    return true;
  }
  if (orb) {
    Call(ctx, base, s.frame, 0x826D5A60u, {h});  // 0x826D9970: ExitScreen at the menu root
    REXLOG_INFO("Refii guest-tick pump: PauseBack — the orb logbook closed");
    return true;
  }
  // The book's back at its root (logbook 0x826D9870..0x826D9890, abilities 0x826C2ADC..0x826C2AFC /
  // disc 0x8259EA8C..0x8259EAAC, items 0x826D27DC..0x826D2814, clothing 0x826D53A8..0x826D53D8, the world
  // map 0x826C6508..0x826C653C, options 0x826DA4B0..0x826DA4BC): the tab strip's hide `sub_826B8130(*(G+76))` (every book but options only
  // when `sub_826C1D98(*(QM+60))` says the menu is at its root) and, on the items screen, the preview's
  // Hide `sub_828C3FD8(h+128)` (0x826D280C / disc 0x82834DA0); then `sub_826D5AF0(h)`, which reopens the
  // pause root `sub_826A2C88`. The clothing arm also destroys its preview mannequin at the root,
  // `sub_826D7E90(SCENE, &h+44)` (0x826D53D0 / disc 0x823FDA78). The logbook's arm then falls through
  // into the book's input `sub_829907F0(h+52, 37, player, pres)` (0x826D9894..0x826D98AC / disc
  // 0x82582524..0x8258253C, no branch between), which returns at once while the book's clock
  // `*(float*)(h+52)` is not past its threshold (0x82990814..0x82990824); only the dropped book tick
  // advances that clock, so the call is not made.
  const bool at_root = screen == 18u || (Call(ctx, base, s.frame, 0x826C1D98u, {LoadU32(base, qm + 60u)}) & 0xFFu) != 0u;
  if (at_root) {
    Call(ctx, base, s.frame, 0x826B8130u, {LoadU32(base, g + 76u)});
    if (screen == 15u) {
      Call(ctx, base, s.frame, 0x828C3FD8u, {h + 0x80u});
    } else if (screen == 16u) {
      Call(ctx, base, s.frame, 0x826D7E90u, {LoadU32(base, g + 8u), h + 0x2Cu});
    }
  }
  Call(ctx, base, s.frame, 0x826D5AF0u, {h});
  REXLOG_INFO("Refii guest-tick pump: PauseBack on screen {} — back to the book's root ({})", screen,
              at_root ? "at root" : "not at root");
  return true;
}

bool DispatchPauseCycle(PPCContext& ctx, uint8_t* base, uint32_t screen, uint32_t player, bool next) {
  // Player one only, and not the orb book (logbook `cmplwi r28,1` 0x826D97FC / 0x826D9828; abilities
  // `a3 == 1` 0x826C2AAC / 0x826C2A90; options 0x826DA300 / 0x826DA32C): 72 (LB) `sub_826B8780`
  // (logbook 0x826D983C, abilities 0x826C2ABC, options 0x826DA340), 73 (RB) `sub_826B8878` (0x826D9810,
  // 0x826C2AA0, 0x826DA314), on `*(G+76)`.
  //
  // The item books' gates are their own: items takes either player while its augment sub-list is
  // closed (h+420 == 0, 0x826D2654 / 0x826D2678; disc 0x8257E08C / 0x8257E0B0), clothing player one
  // outside dye mode (h+220 != 5, 0x826D5240..0x826D5250 / 0x826D526C..0x826D527C).
  const Scratch s = Claim(ctx);
  bool refused = screen != 15u && player != 1u;
  if (screen == 17u && !refused) {
    const uint32_t h = ScreenHandler(ctx, base, s.frame, 17u, 0x820D0110u);
    refused = h == 0u || LoadU32(base, h + 4u) == 2u;  // the orb book has no tabs
  } else if (screen == 15u) {
    const uint32_t h = ScreenHandler(ctx, base, s.frame, 15u, 0x820CFEB0u);
    refused = h == 0u || LoadU32(base, h + 0x1A4u) != 0u;
  } else if (screen == 16u && !refused) {
    const uint32_t h = ScreenHandler(ctx, base, s.frame, 16u, 0x820CFEDCu);
    refused = h == 0u || LoadU32(base, h + 0xDCu) == 5u;
  }
  if (refused) {
    REXLOG_INFO("Refii guest-tick pump: PauseCycle on screen {} for player {} — refused", screen, player);
    return true;
  }
  const uint32_t g = Gui(base);
  Call(ctx, base, s.frame, next ? 0x826B8878u : 0x826B8780u, {LoadU32(base, g + 76u)});
  REXLOG_INFO("Refii guest-tick pump: PauseCycle on screen {} — {}", screen, next ? "next" : "previous");
  return true;
}

}  // namespace refii::vfs
