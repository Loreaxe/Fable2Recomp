// THE LOADING SCREEN, STATED. The manager makes one 400-byte screen per load and the 3D thread
// ticks it; the host draws its own from what these calls state (the shared registry,
// src/runtime/src/core/stated_game_state.cpp), reading nothing out of guest memory.
//
//   made      `sub_8235CA90(screen, strA, strB, type, tip, fixedArt)` (disc `sub_82359090`), from
//             the manager's Show `sub_82361200(mgr, strA, strB, type)`; and the text-only
//             `sub_8235CED8(screen, text, 15, …)` (disc `sub_823594D8`), from `sub_82361388`.
//   art       `sub_8235F2F8(screen)` (disc `sub_8235B8F8`): every exit assigns the art name to
//             `screen + 272`, and sets the quit fade `screen + 310` when its two strings match.
//   ticked    `lhLoadingScreen_UpdateAndRender(screen, …)`: counts the fade `+172` down once the
//             take-down is requested (`+176`) and latches done (`+177`) with its event.
//   gone      `sub_8235D0F8(screen)` (disc `sub_823596F8`), the destructor body.
//
// ITS TEXT (src/core/loading_text_state.cpp, the host's RefiiHostGetLoadingScreenText):
//   switches  the constructors' own constants: tips on in all three (`*(a1 + 313) = 1`), the
//             banner on only in the level screen's (`*(a1 + 314) = 1` 0x8235cc58 / 0x82359258; 0 in
//             the text-only one 0x8235d09c / 0x8235969c).
//   level     the level screen's `strB`, copied to `screen + 268` (0x8235cbac / 0x823591ac).
//   overlay   the text-only constructor's `text`, copied to `screen + 280` (0x8235d060 /
//             0x82359660), and the manager's setter `sub_82361188(mgr, text)` (disc
//             `sub_8235D788`): `AssignIfDifferent(*(mgr + 4) + 280, text)` (0x823611c8 / 0x8235d7c8).
//   tip       `lhLoadingScreen_DrawTip(screen, …)`, which the tick calls for the first tip and
//             again each time its own ten-second clock moves `screen + 300` on (0x8235ea00 ..
//             0x8235ea40 / 0x8235b040): the one tag it resolves through TextManager_GetString
//             (0x82360590 / 0x8235cb90) is the tip, stated when the draw returns 1, with the index
//             and count it selected from (`screen + 300`, `+ 388`). The guest rotates; the host
//             runs no tip clock.
//   spinner   `lhLoadingScreen_DrawLoadingAnimation(screen)`: once when the screen's first frame
//             is built (0x8235e914 / 0x8235af14) and once after each `++*(screen + 168)`
//             (0x8235eaa4 .. 0x8235eab0 / 0x8235b0a4 .. 0x8235b0b0), so the counter is the calls
//             made less one.
// The third constructor `sub_8235CCB8` (disc `sub_823592B8`, the manager's message screen
// `sub_823612C8`) is not hooked: its screen is not stated as made, so none of this is for it.
#include <cstdint>
#include <cstring>
#include <string>

#include "vfs_hooks_private.h"
#include "core/guest_state.h"
#include "core/payload_bridge_fable2.h"
#include "hooks/ppc_externs.h"

namespace {

constexpr uint32_t kFade = 172u;
constexpr uint32_t kRequested = 176u;
constexpr uint32_t kDone = 177u;
constexpr uint32_t kArt = 272u;
constexpr uint32_t kType = 304u;
constexpr uint32_t kQuitFade = 310u;

// Stated-value keys (payload_bridge.h).
constexpr uint32_t kLoadingFixedArt = 4u;
constexpr uint32_t kLoadingTextOnly = 5u;
constexpr uint32_t kLoadingQuitFade = 6u;

thread_local uint32_t t_manager = 0u;

// The DrawTip in flight on this thread and the tag it has resolved.
thread_local uint32_t t_tip_screen = 0u;
thread_local uint32_t t_tip_key = 0u;

float GuestFloat(uint8_t* base, uint32_t at) {
    const uint32_t raw = refii::vfs::LoadGuestU32(base, at);
    float value = 0.0f;
    std::memcpy(&value, &raw, sizeof(value));
    return value;
}

void StateMade(uint32_t screen, uint32_t kind, uint32_t fixed_art, uint32_t text_only) {
    refii_bridge_stated_value(kLoadingFixedArt, fixed_art);
    refii_bridge_stated_value(kLoadingTextOnly, text_only);
    refii_bridge_stated_value(kLoadingQuitFade, 0u);
    refii_bridge_loading_screen_created(t_manager, screen, kind);
    REXLOG_INFO("Refii loading screen: {:08X} made by manager {:08X}, type {}, fixed art {}, text only {}", screen,
                t_manager, kind, fixed_art, text_only);
}

}  // namespace

REX_EXTERN(rex_lhLoadingScreenManager_Show) {
    REX_FUNC_PROLOGUE();
    const uint32_t outer = t_manager;
    t_manager = ctx.r3.u32;
    __imp__rex_lhLoadingScreenManager_Show(ctx, base);
    t_manager = outer;
}

REX_EXTERN(rex_lhLoadingScreenManager_ShowText) {
    REX_FUNC_PROLOGUE();
    const uint32_t outer = t_manager;
    t_manager = ctx.r3.u32;
    __imp__rex_lhLoadingScreenManager_ShowText(ctx, base);
    t_manager = outer;
}

REX_EXTERN(rex_lhLoadingScreen_Ctor) {
    REX_FUNC_PROLOGUE();
    const uint32_t screen = ctx.r3.u32;
    const uint32_t kind = ctx.r6.u32;
    const uint32_t fixed_art = ctx.r8.u32 & 0xFFu;
    const std::string level = refii::vfs::ReadGuestLhString(base, ctx.r5.u32);
    __imp__rex_lhLoadingScreen_Ctor(ctx, base);
    StateMade(screen, kind, fixed_art != 0u ? 1u : 0u, 0u);
    REXLOG_INFO("Refii loading screen: {:08X} level '{}'", screen, level);
    refii_bridge_loading_text_made(t_manager, screen, 1, level.c_str(), "");
}

REX_EXTERN(rex_lhLoadingScreen_CtorText) {
    REX_FUNC_PROLOGUE();
    const uint32_t screen = ctx.r3.u32;
    const uint32_t kind = ctx.r5.u32;
    const uint32_t fixed_art = ctx.r7.u32 & 0xFFu;
    const std::string text = refii::vfs::ReadGuestWideString(base, ctx.r4.u32);
    __imp__rex_lhLoadingScreen_CtorText(ctx, base);
    StateMade(screen, kind, fixed_art != 0u ? 1u : 0u, 1u);
    REXLOG_INFO("Refii loading screen: {:08X} text '{}'", screen, text);
    refii_bridge_loading_text_made(t_manager, screen, 0, "", text.c_str());
}

REX_EXTERN(rex_lhLoadingScreen_ChooseArt) {
    REX_FUNC_PROLOGUE();
    const uint32_t screen = ctx.r3.u32;
    __imp__rex_lhLoadingScreen_ChooseArt(ctx, base);
    const uint32_t body = refii::vfs::LoadGuestU32(base, screen + kArt);
    const std::string art = refii::vfs::ReadGuestLhString(base, screen + kArt);
    const uint32_t kind = refii::vfs::LoadGuestU32(base, screen + kType);
    refii_bridge_stated_value(kLoadingQuitFade, base[screen + kQuitFade] != 0u ? 1u : 0u);
    refii_bridge_loading_screen_art(screen, kind, body, art.data(), static_cast<uint32_t>(art.size()));
    REXLOG_INFO("Refii loading screen: {:08X} chose '{}' (type {}, quit fade {})", screen, art, kind,
                base[screen + kQuitFade]);
}

REX_EXTERN(rex_lhLoadingScreen_UpdateAndRender) {
    REX_FUNC_PROLOGUE();
    const uint32_t screen = ctx.r3.u32;
    __imp__rex_lhLoadingScreen_UpdateAndRender(ctx, base);
    refii_bridge_loading_screen_updated(screen, GuestFloat(base, screen + kFade), base[screen + kRequested] != 0u ? 1 : 0,
                                        base[screen + kDone] != 0u ? 1 : 0);
}

REX_EXTERN(rex_lhLoadingScreen_Dtor) {
    REX_FUNC_PROLOGUE();
    refii_bridge_loading_screen_destroyed(ctx.r3.u32);
    refii_bridge_loading_text_gone(ctx.r3.u32);
    __imp__rex_lhLoadingScreen_Dtor(ctx, base);
}

REX_EXTERN(rex_lhLoadingScreenManager_SetText) {
    REX_FUNC_PROLOGUE();
    const uint32_t manager = ctx.r3.u32;
    const std::string text = refii::vfs::ReadGuestWideString(base, ctx.r4.u32);
    __imp__rex_lhLoadingScreenManager_SetText(ctx, base);
    if (refii_bridge_loading_text_overlay(manager, text.c_str()) != 0) {
        REXLOG_INFO("Refii loading screen: manager {:08X} set the text '{}'", manager, text);
    }
}

REX_EXTERN(rex_lhLoadingScreen_DrawTip) {
    REX_FUNC_PROLOGUE();
    const uint32_t screen = ctx.r3.u32;
    const uint32_t outer_screen = t_tip_screen;
    const uint32_t outer_key = t_tip_key;
    t_tip_screen = screen;
    t_tip_key = 0u;
    __imp__rex_lhLoadingScreen_DrawTip(ctx, base);
    const uint32_t key = t_tip_key;
    t_tip_screen = outer_screen;
    t_tip_key = outer_key;
    if ((ctx.r3.u32 & 0xFFu) == 0u || key == 0u) {
        return;
    }
    const uint32_t index = refii::vfs::LoadGuestU32(base, screen + 300u);
    const uint32_t count = refii::vfs::LoadGuestU32(base, screen + 388u);
    refii_bridge_loading_text_tip(screen, key, index, count);
    REXLOG_INFO("Refii loading screen: {:08X} tip {}/{} tag {:08X}", screen, index + 1u, count, key);
}

// (out, textManager, key*): the tag DrawTip resolves is its tip.
REX_EXTERN(rex_lhTextManager_GetString) {
    REX_FUNC_PROLOGUE();
    if (t_tip_screen != 0u && t_tip_key == 0u) {
        t_tip_key = refii::vfs::LoadGuestU32(base, ctx.r5.u32);
    }
    __imp__rex_lhTextManager_GetString(ctx, base);
}

REX_EXTERN(rex_lhLoadingScreen_DrawLoadingAnimation) {
    REX_FUNC_PROLOGUE();
    const uint32_t screen = ctx.r3.u32;
    __imp__rex_lhLoadingScreen_DrawLoadingAnimation(ctx, base);
    refii_bridge_loading_text_spinner(screen);
}
