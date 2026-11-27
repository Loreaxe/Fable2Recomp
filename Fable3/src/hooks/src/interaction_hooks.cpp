// refii - Fable 3's interaction mode, stated at its own writers for Godot's interaction document:
// the mode set, its close, and the info panel id the panel's slide is.
//
// Every address is the Fable 3 TU3 IDB ("disc" marks one TU3 lacks); each offset names its base.
//
// THE INTERACTION MODE IM (`*(GUI+0x48)`, GUI = *0x83596F84). The mode set
// sub_825EF1D8(IM, mode, a3, a4, table, a6) — GUI.OpenInteractionMode and the C++ openers — clears
// the old mode through sub_825EED90 (disc 0x825C44B4; IM+64 = 1 at 0x825EEE9C), stores IM+20 = mode
// (disc 0x825C458C) and the mode's object at IM+24 (disc 0x825C4630; 0 for a mode it does not build,
// 0x825EF6D4). Its close sub_825F0CC8(IM, keepTarget) — the tick on IM+6 (disc 0x825C3DAC) and the
// modes' own exits — drops IM+24 (disc 0x825C5FCC) and rests the panel at 1 (disc 0x825C62F0); IM+20 is
// left as it was, so the guest's own read-backs (GUI.GetEntityPlayerIsInteractingWith…
// 0x82552B88, GUI.IsInInteractionModeMenuDecoration 0x825535EC) test IM+24 first. Both run as
// the guest has them.
//
// THE INFO PANEL is IM+64: sub_82971CC8(mode object, id) follows the object's IM (`*(obj+20)`,
// 0x82971CD4) and stores the id (0x82971D04) with IM+9 = 1 (0x82971D00); the screen tick then
// writes it as `LowerElements.InfoBox`'s state (dword_83599590, sub_8227BC50 at 0x825EE9A4) —
// 1 closed, 5 NPC, 6 weapon, 9 save, 7 world map, 13/15 the Lua text panels, … Its InfoBoxOpen
// toggle (id > 1, 0x82971D38) is a registry write hud_hooks.cpp states. The id is stated here
// at each of its writers, as the writer passes it: the stager as its r4 asks (its own store is
// behind IM+0), and every direct store of IM+64 on the store itself (`RefiiF3InteractionPanelStored`,
// the value register) — the clear's `stw r27,0x40(r29)` 0x825EEE9C (r27 = 1, `li r27,1` 0x825EEE68;
// disc 0x825C400C), the close's `stw r23,0x40(r30)` 0x825F1358 (r23 = 1, `li r23,1` 0x825F0CEC; disc
// `stw r24,0x40(r29)` 0x825C62F0, `li r24,1` 0x825C5D2C), both on the one path to the function's end,
// and the modes that store it themselves (IM = `lwz r11,0x14(r31)`): decoration vf1 0x82B6248C /
// 0x82B624AC (r10 = 2), 0x82B624C0 / 0x82B624D4 (r30), vf2 0x82B62898 (r10 = 1), sub_82B67DE8
// 0x82B67F58 (r9 = 13), the shop's vf1 0x8295B6E0 (r26), the pawn shop's 0x82B6BB8C (r30):
// `@Interaction.Panel(id)`. Nothing reads IM+64 back.
//
// THE ROOT. The tick sub_825EE908(IM) writes InteractionMode.bgf's root (`GuiPresentation_Root(IM+0)`)
// on the edges of two IM flags it owns: IM+7 (a screen over the mode — sub_821F7F98, the GUI's
// +28 -> +56 flag, or screen 11/12/13/20 on top, disc 0x825C3C50..0x825EEB40) set 0 -> 1 states 2
// (`stb r27,7(r31)` 0x825EED3C, SetState 0x825EED58); cleared once the root's own check passes
// (sub_828774A8 at 0x825EEB78, `stb r28,7(r31)` 0x825EEB88) states 1 (0x825EEBA0); then IM+8
// (sub_82275830 of the GUI's +28 -> +48 at 0x825EEBB0) set states 2 (`stb r27,8(r31)` 0x825EEBD0,
// 0x825EEBEC) and cleared states 1 (`stb r28,8(r31)` 0x825EEC00, 0x825EEC1C). Each write is behind
// IM+0 (0x825EED44, 0x825EEBD8, 0x825EEC08). Each write is stated on the tick's own call
// (`RefiiF3InteractionRootState`: r4 the presentation, r5 the slide, at the `bl GuiElement_SetState`
// 0x825EEBA0, 0x825EEBEC, 0x825EEC1C, 0x825EED58; disc 0x825C3D10, 0x825C3D5C, 0x825C3D8C,
// 0x825C3EC8), in the tick's order: `@Ui.State(pres, root, "Scene", slide)`. Slide 1 is the file's
// `Main` (LowerElements at 100), 2 its `Slide1` (both layers fade out). No IM flag is read.
//
// THE CLEAR sub_825EED90(IM) — the mode set's (disc 0x825C44B4) and four other openers' — loads the
// presentation when IM+0 is empty (sub_825EF150 at 0x825EEDAC), activates the root unless it is
// (sub_828773E8(root, pres, 1, 0) at 0x825EEDF0, stated by the active setter's own hook) and puts it
// on slide 1 (`li r5,1; bl GuiElement_SetState` 0x825EEE88..0x825EEE8C, disc 0x825C3FFC) without
// touching IM+7 / IM+8: the same statement on that call (`RefiiF3InteractionRootCleared`).
// The tick's countdown IM+16 deactivates the root again at 1 (0x825EE9E8), the same setter.
//
// THE HOLD ICON sub_8296FED0(mode, button, hold) — the hold tick sub_829709B0 (0x82970A64) and
// the slot reset sub_82969938 (0x829699A4) — puts the button's `Icon` (dword_8359CEB4) and its
// `NewTab.Foreground.LeftSection` (dword_8359CEC8) on 2 `FillButton` for a hold, else 1
// `NormalButton` (0x8296FF28..0x8296FF34; SetState 0x8296FF40 / 0x8296FF70), behind `**(mode+20)`
// and the button (0x8296FEFC), then shows the button (0x8296FF8C, a toggle already stated): both
// slides stated at entry as `@Ui.State` on the button's path. The tick reads the Icon's state
// back (GetState 0x82970A44 == 2 arms the hold), which the headless element answers itself.
//
// THE TRIGGER AND BUMPER BARS, COverlayButtonsBase (vtables 0x820D4940 `UpperElements.BumperButtons`
// and 0x820D49B8 `UpperElements.TriggerButtons`, their element tables built by vf5 sub_82B551A8 /
// sub_82B55408: +8 root, +12 `.Text`, +16 / +20 the two `_Complete` sides, +4 the presentation).
// vf0 sub_82B73C40(bar, shown) does nothing when `shown` equals +26 (0x82B73C64 / beq 0x82B73C68);
// else it sets the root to 1 hidden (0x82B73D0C) or, shown, 2 `TopBoxOn` while a TopBox channel holds
// a message and neither of the TopBox manager's +0 / +1 bytes is set, 3 `TopBoxOff` otherwise — the
// manager `*(*(GUI+32)+76)` (0x82B73C80 / 0x82B73C84), its channels +76..+80 each empty when
// `(end - begin) & ~3` is 0 (0x82B73C98..0x82B73CB0), the bytes at 0x82B73CD0 / 0x82B73CDC —
// SetState(+8, +4, slide) at 0x82B73D18 (disc 0x82B3EAF0), +26 stored at 0x82B73D1C. The slide is
// stated on that call with the registers it is made with (`RefiiF3OverlayBarState`: r3 the root
// element, r4 the presentation, r5 the slide — `li r5,2` 0x82B73C78, `li r5,3` 0x82B73CFC, `li r5,1`
// 0x82B73D0C; disc 0x82B3EA50, 0x82B3EAD4, 0x82B3EAE4): `@Ui.State`. vf1 sub_82B73D50(bar, LHString*) commits the text straight into
// `.Text`'s box (`b GuiTextBox_Commit` 0x82B73D80, behind +12 / +4 / the box) — the localised label
// GUI.Set*Text built from its tag name (sub_82545848 -> sub_823CBB30 at 0x825458E4): `@Ui.Text`. The
// sides (vf3 -> sub_828777E8 at 0x82B73DC4) and the tag form (vf2 -> SetTextTag at 0x82B73D44) are
// the generic writers' statements already.

#include <atomic>
#include <cstdint>
#include <string>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "box_answers.h"
#include "gui_statements.h"
#include "mod_lua.h"
#include "mod_seam.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_InteractionMode_Set);
REX_EXTERN(__imp__rex_InteractionMode_Close);
REX_EXTERN(__imp__rex_InteractionMode_SetPanel);
REX_EXTERN(__imp__rex_InteractionMode_Clear);
REX_EXTERN(__imp__rex_InteractionButton_Hold);
REX_EXTERN(__imp__rex_OverlayBar_CommitText);
REX_EXTERN(__imp__rex_InteractionMode_OpenRealEstate);
REX_EXTERN(__imp__rex_InteractionMode_OpenShop);
REX_EXTERN(__imp__rex_InteractionMode_OpenPawnShop);
REX_EXTERN(__imp__rex_InteractionMode_OpenOrb);

namespace {

using namespace refii::fable3;

constexpr uint32_t kImMode = 20u;
constexpr uint32_t kImObject = 24u;
/// The mode object's IM (sub_82971CC8 at 0x82971CD4).
constexpr uint32_t kObjectIm = 20u;
/// The panel id the IM is built and closed with (disc 0x825C3698, disc 0x825C62F0).
constexpr int32_t kPanelClosed = 1;

constexpr uint32_t kImPresentation = 0u;
constexpr uint32_t kBarPresentation = 4u;
constexpr uint32_t kBarText = 12u;

std::atomic<int32_t> g_panel{kPanelClosed};
/// Each call-site statement's first run, for the log.
std::atomic<bool> g_root_stated{false};
std::atomic<bool> g_root_cleared{false};
std::atomic<bool> g_bar_stated{false};

/// interaction_mode (catalog kind 135).
constexpr int32_t kInteractionModeEvent = 135;
/// IM+4, the open flag: set by the clear every opener runs first (`li r27,1` 0x825EEE68, `stb
/// r27,4(r29)` 0x825EEE90, r29 = IM `mr r29,r3` 0x825EEDA0; disc 0x825C3FD8 / 0x825C4000 / 0x825C3F10),
/// cleared by the close on its one exit path (`li r26,0` 0x825F0CE8, `stb r26,4(r30)` 0x825F134C, r30 =
/// IM; disc `li r28,0` 0x825C5D28, `stb r28,4(r29)` 0x825C62E4).
constexpr uint32_t kImOpenFlag = 4u;
/// The hero's (IM+0x2C) and the target's (IM+0x34) smart pointers, each opener's stores (Set `addi
/// r23,r28,0x2C` 0x825EF39C / `addi r22,r28,0x34` 0x825EF364, disc 0x825C450C / 0x825C44D4; the
/// others listed at their hooks) and the guest's getters (0x823B528C, 0x82289B14).
constexpr uint32_t kImHeroRef = 0x2Cu;
constexpr uint32_t kImTargetRef = 0x34u;
/// The clears run so far. The clear sub_825EED90 has exactly five callers — Set and the four openers
/// below — and each calls it as its body's first act, past its own refusals (GUI+0x88, the hero's
/// interaction test), then stores IM+0x14 = the mode with no return between: a clear during an
/// opener is that opener opening.
uint32_t g_im_clears = 0u;

/// The mode an opener left in IM, raised as opened; `was_open`/`previous` as IM stood before it.
void RaiseModeOpened(const uint8_t* base, uint32_t im, bool was_open, int32_t previous) {
  if (!modlua::Wants(kInteractionModeEvent)) {
    return;
  }
  const uint32_t hero = seam::Deref(base, im + kImHeroRef);
  modlua::QueueEvent(kInteractionModeEvent,
                     seam::Fields("hero=%u,player=%d,target=%u,mode=%d,previous=%d,open=true", seam::Handle(base, hero),
                                  modlua::PlayerOf(base, hero), seam::Handle(base, seam::Deref(base, im + kImTargetRef)),
                                  static_cast<int32_t>(LoadGuestU32(base, im + kImMode)), was_open ? previous : -1));
}

/// Runs an opener and raises it when its body ran (a clear happened inside it).
void OpenAndRaise(PPCContext& ctx, uint8_t* base, PPCFunc* original) {
  const uint32_t im = ctx.r3.u32;
  const bool readable = base != nullptr && Plausible(im);
  const bool was_open = readable && base[im + kImOpenFlag] != 0u;
  const int32_t previous = readable ? static_cast<int32_t>(LoadGuestU32(base, im + kImMode)) : -1;
  const uint32_t clears = g_im_clears;
  original(ctx, base);
  if (g_im_clears != clears) {
    NoteInteractionModeOpenedByOpener(im);
  }
  if (readable && g_im_clears != clears) {
    RaiseModeOpened(base, im, was_open, previous);
  }
}

void StatePanelId(int32_t panel) {
  if (g_panel.exchange(panel) == panel) {
    return;
  }
  Statement("@Interaction.Panel").Add(Number(static_cast<float>(panel))).Post();
}

}  // namespace

// (IM, mode, a3, a4, table, a6).
REX_EXTERN(rex_InteractionMode_Set) {
  const uint32_t im = ctx.r3.u32;
  const uint32_t mode = ctx.r4.u32;
  const bool readable = base != nullptr && Plausible(im);
  const bool was_open = readable && base[im + kImOpenFlag] != 0u;
  const int32_t previous = readable ? static_cast<int32_t>(LoadGuestU32(base, im + kImMode)) : -1;
  const uint32_t clears = g_im_clears;
  // The old mode's object goes with the set: its list takes no more steps (interaction_list_step.cpp).
  ForgetInteractionList();
  __imp__rex_InteractionMode_Set(ctx, base);
  if (g_im_clears != clears) {
    // The clear ran: the set is past its refusals and stores its second argument as the mode.
    NoteInteractionModeSet(im, static_cast<int32_t>(mode));
  }
  if (base == nullptr || !Plausible(im)) {
    return;
  }
  if (LoadGuestU32(base, im + kImMode) == mode) {
    REXLOG_INFO("Refii interaction mode: mode {} ({})", mode,
                LoadGuestU32(base, im + kImObject) != 0u ? "open" : "no mode object");
  }
  if (g_im_clears != clears) {
    RaiseModeOpened(base, im, was_open, previous);
  }
}

// (IM, keepTarget).
REX_EXTERN(rex_InteractionMode_Close) {
  const uint32_t im = ctx.r3.u32;
  const bool open = base != nullptr && Plausible(im) && LoadGuestU32(base, im + kImObject) != 0u;
  const bool flagged = base != nullptr && Plausible(im) && base[im + kImOpenFlag] != 0u;
  const bool raise = flagged && modlua::Wants(kInteractionModeEvent);
  const uint32_t hero = raise ? seam::Deref(base, im + kImHeroRef) : 0u;
  const uint32_t target = raise ? seam::Deref(base, im + kImTargetRef) : 0u;
  const int32_t mode = raise ? static_cast<int32_t>(LoadGuestU32(base, im + kImMode)) : -1;
  ForgetInteractionList();
  NoteInteractionModeClosed(im);
  __imp__rex_InteractionMode_Close(ctx, base);
  if (open) {
    REXLOG_INFO("Refii interaction mode: mode {} closed", LoadGuestU32(base, im + kImMode));
  }
  if (raise) {
    modlua::QueueEvent(kInteractionModeEvent,
                       seam::Fields("hero=%u,player=%d,target=%u,mode=%d,previous=-1,open=false",
                                    seam::Handle(base, hero), modlua::PlayerOf(base, hero),
                                    seam::Handle(base, target), mode));
  }
}

// (mode object, id): the id as asked, at entry — its store `stw r4,0x40(r11)` (0x82971D04) sits
// behind the IM's presentation (`lwz r29,0(r11)` 0x82971CE4 / beq 0x82971CEC), so IM+64 read
// after the call is the id only while InteractionMode.bgf is built.
REX_EXTERN(rex_InteractionMode_SetPanel) {
  const uint32_t object = ctx.r3.u32;
  const int32_t panel = static_cast<int32_t>(ctx.r4.u32);
  const uint32_t im = base != nullptr && Plausible(object) ? LoadGuestU32(base, object + kObjectIm) : 0u;
  __imp__rex_InteractionMode_SetPanel(ctx, base);
  if (Plausible(im)) {
    StatePanelId(panel);
  }
}

namespace {

/// The element's name as the element hooks know it; empty when nothing named it.
std::string PathOf(uint32_t element) {
  uint32_t presentation = 0u;
  std::string path;
  return Plausible(element) && ElementName(element, presentation, path) ? path : std::string();
}

}  // namespace

/// The interaction root's slide on the tick's own SetState calls: the presentation and the slide each
/// is called with.
void RefiiF3InteractionRootState(PPCRegister& presentation, PPCRegister& slide) {
  if (presentation.u32 != 0u) {
    if (!g_root_stated.exchange(true)) {
      REXLOG_INFO("Refii interaction mode: the root's slide {} stated on the tick's own SetState call", slide.u32);
    }
    StateState(presentation.u32, 0u, "Scene", static_cast<int32_t>(slide.u32), "825C3A78");
  }
}

/// The same on the clear's SetState call (slide 1).
void RefiiF3InteractionRootCleared(PPCRegister& presentation, PPCRegister& slide) {
  if (presentation.u32 != 0u) {
    if (!g_root_cleared.exchange(true)) {
      REXLOG_INFO("Refii interaction mode: the root's slide {} stated on the clear's own SetState call", slide.u32);
    }
    StateState(presentation.u32, 0u, "Scene", static_cast<int32_t>(slide.u32), "825C3F00");
  }
}

// (IM): every opener's first act.
REX_EXTERN(rex_InteractionMode_Clear) {
  ++g_im_clears;
  ForgetInteractionList();
  __imp__rex_InteractionMode_Clear(ctx, base);
}

// interaction_mode (135): the four dedicated openers. Each runs the clear first, past its own refusals, then
// stores IM+0x14 its mode, +0x2C the hero and +0x34 the target: a clear inside one is it opening.
REX_EXTERN(rex_InteractionMode_OpenRealEstate) { OpenAndRaise(ctx, base, __imp__rex_InteractionMode_OpenRealEstate); }
REX_EXTERN(rex_InteractionMode_OpenShop) { OpenAndRaise(ctx, base, __imp__rex_InteractionMode_OpenShop); }
REX_EXTERN(rex_InteractionMode_OpenPawnShop) { OpenAndRaise(ctx, base, __imp__rex_InteractionMode_OpenPawnShop); }
REX_EXTERN(rex_InteractionMode_OpenOrb) { OpenAndRaise(ctx, base, __imp__rex_InteractionMode_OpenOrb); }

/// A store of IM+64 (the value register at the `stw rX,0x40(rY)`): the clear's, the close's, a mode's own.
void RefiiF3InteractionPanelStored(PPCRegister& value) { StatePanelId(static_cast<int32_t>(value.u32)); }

// (mode, button, hold).
REX_EXTERN(rex_InteractionButton_Hold) {
  const uint32_t mode = ctx.r3.u32;
  const uint32_t button = ctx.r4.u32;
  if (base != nullptr && Plausible(mode) && Plausible(button)) {
    const uint32_t im = LoadGuestU32(base, mode + kObjectIm);
    const uint32_t presentation = Plausible(im) ? LoadGuestU32(base, im + kImPresentation) : 0u;
    const std::string path = PathOf(button);
    if (presentation != 0u && !path.empty() && path[0] != '#') {
      const int32_t slide = (ctx.r5.u32 & 0xFFu) != 0u ? 2 : 1;
      StateState(presentation, 0u, path + ".Icon", slide, "82961278");
      StateState(presentation, 0u, path + ".NewTab.Foreground.LeftSection", slide, "82961278");
    }
  }
  __imp__rex_InteractionButton_Hold(ctx, base);
}

/// A bar's root slide on vf0's own SetState call (0x82B73D18 / disc 0x82B3EAF0): the element, the
/// presentation and the slide it is called with.
void RefiiF3OverlayBarState(PPCRegister& element, PPCRegister& presentation, PPCRegister& slide) {
  const std::string path = PathOf(element.u32);
  if (Plausible(presentation.u32) && !path.empty()) {
    if (!g_bar_stated.exchange(true)) {
      REXLOG_INFO("Refii interaction mode: bar '{}' slide {} stated on its own SetState call", path, slide.u32);
    }
    StateState(presentation.u32, element.u32, path, static_cast<int32_t>(slide.u32), "82B3EA18");
  }
}

// (bar, LHString*).
REX_EXTERN(rex_OverlayBar_CommitText) {
  const uint32_t bar = ctx.r3.u32;
  if (base != nullptr && Plausible(bar)) {
    const uint32_t presentation = LoadGuestU32(base, bar + kBarPresentation);
    const uint32_t text = LoadGuestU32(base, bar + kBarText);
    const std::string path = PathOf(text);
    if (Plausible(presentation) && !path.empty()) {
      StateText(presentation, text, path, ReadLhString(base, ctx.r4.u32), "82B3EB28");
    }
  }
  __imp__rex_OverlayBar_CommitText(ctx, base);
}
