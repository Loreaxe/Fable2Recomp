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
// at each of its three writers: the set and close as IM+64 stands when they return, the stager
// as its r4 asks (its own store is behind IM+0), the clear as IM+64 stands when it returns
// (`stw r27,0x40(r29)` 0x825EEE9C, 1), and the modes that store IM+64 themselves on their stores
// (`RefiiF3InteractionPanelStored`, the value register, IM = `lwz r11,0x14(r31)`): decoration vf1
// 0x82B6248C / 0x82B624AC (r10 = 2), 0x82B624C0 / 0x82B624D4 (r30), vf2 0x82B62898 (r10 = 1),
// sub_82B67DE8 0x82B67F58 (r9 = 13), the shop's vf1 0x8295B6E0 (r26), the pawn shop's 0x82B6BB8C
// (r30): `@Interaction.Panel(id)`.
//
// THE ROOT. The tick sub_825EE908(IM) writes InteractionMode.bgf's root (`GuiPresentation_Root(IM+0)`)
// on the edges of two IM flags it owns: IM+7 (a screen over the mode — sub_821F7F98, the GUI's
// +28 -> +56 flag, or screen 11/12/13/20 on top, disc 0x825C3C50..0x825EEB40) set 0 -> 1 states 2
// (`stb r27,7(r31)` 0x825EED3C, SetState 0x825EED58); cleared once the root's own check passes
// (sub_828774A8 at 0x825EEB78, `stb r28,7(r31)` 0x825EEB88) states 1 (0x825EEBA0); then IM+8
// (sub_82275830 of the GUI's +28 -> +48 at 0x825EEBB0) set states 2 (`stb r27,8(r31)` 0x825EEBD0,
// 0x825EEBEC) and cleared states 1 (`stb r28,8(r31)` 0x825EEC00, 0x825EEC1C). Each write is behind
// IM+0 (0x825EED44, 0x825EEBD8, 0x825EEC08). The flags are the IM's, read on either side of the
// tick, and the edges are replayed in the tick's order: `@Ui.State(pres, root, "Scene", slide)`.
// Slide 1 is the file's `Main` (LowerElements at 100), 2 its `Slide1` (both layers fade out).
//
// THE CLEAR sub_825EED90(IM) — the mode set's (disc 0x825C44B4) and four other openers' — loads the
// presentation when IM+0 is empty (sub_825EF150 at 0x825EEDAC), activates the root unless it is
// (sub_828773E8(root, pres, 1, 0) at 0x825EEDF0, stated by the active setter's own hook) and puts it
// on slide 1 (SetState 0x825EEE8C) without touching IM+7 / IM+8: `@Ui.State(…, "Scene", 1)` after it.
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
// SetState(+8, +4, slide) at 0x82B73D18, +26 stored at 0x82B73D1C. The slide is stated as that
// body decides it: `@Ui.State`. vf1 sub_82B73D50(bar, LHString*) commits the text straight into
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
REX_EXTERN(__imp__rex_InteractionMode_Tick);
REX_EXTERN(__imp__rex_InteractionMode_Clear);
REX_EXTERN(__imp__rex_InteractionButton_Hold);
REX_EXTERN(__imp__rex_OverlayBar_Show);
REX_EXTERN(__imp__rex_OverlayBar_CommitText);
REX_EXTERN(__imp__rex_InteractionMode_OpenRealEstate);
REX_EXTERN(__imp__rex_InteractionMode_OpenShop);
REX_EXTERN(__imp__rex_InteractionMode_OpenPawnShop);
REX_EXTERN(__imp__rex_InteractionMode_OpenOrb);

namespace {

using namespace refii::fable3;

constexpr uint32_t kImMode = 20u;
constexpr uint32_t kImObject = 24u;
constexpr uint32_t kImPanel = 64u;
/// The mode object's IM (sub_82971CC8 at 0x82971CD4).
constexpr uint32_t kObjectIm = 20u;
/// The panel id the IM is built and closed with (disc 0x825C3698, disc 0x825C62F0).
constexpr int32_t kPanelClosed = 1;

constexpr uint32_t kImPresentation = 0u;
constexpr uint32_t kImScreenOver = 7u;
constexpr uint32_t kImHiddenByGui = 8u;
const refii::guest::Addr kGuiManager{0x83596F84u};
constexpr uint32_t kGuiTopBoxOwner = 32u;  // GUI+32
constexpr uint32_t kTopBoxManager = 76u;   // *(GUI+32)+76
constexpr uint32_t kTopBoxChannels = 76u;  // M+76 .. M+80
constexpr uint32_t kBarPresentation = 4u;
constexpr uint32_t kBarRoot = 8u;
constexpr uint32_t kBarText = 12u;
constexpr uint32_t kBarShown = 26u;

std::atomic<int32_t> g_panel{kPanelClosed};

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

void StatePanel(const uint8_t* base, uint32_t im) {
  if (base == nullptr || !Plausible(im)) {
    return;
  }
  const int32_t panel = static_cast<int32_t>(LoadGuestU32(base, im + kImPanel));
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
  if (base == nullptr || !Plausible(im)) {
    return;
  }
  if (LoadGuestU32(base, im + kImMode) == mode) {
    REXLOG_INFO("Refii interaction mode: mode {} ({})", mode,
                LoadGuestU32(base, im + kImObject) != 0u ? "open" : "no mode object");
  }
  StatePanel(base, im);
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
  __imp__rex_InteractionMode_Close(ctx, base);
  if (open) {
    REXLOG_INFO("Refii interaction mode: mode {} closed", LoadGuestU32(base, im + kImMode));
  }
  StatePanel(base, im);
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

/// The bar's shown slide as vf0 decides it (0x82B73C74..0x82B73CFC).
int32_t BarShownSlide(const uint8_t* base) {
  const uint32_t gui = LoadGuestU32(base, kGuiManager);
  const uint32_t owner = Plausible(gui) ? LoadGuestU32(base, gui + kGuiTopBoxOwner) : 0u;
  const uint32_t manager = Plausible(owner) ? LoadGuestU32(base, owner + kTopBoxManager) : 0u;
  if (!Plausible(manager)) {
    return 3;
  }
  const uint32_t begin = LoadGuestU32(base, manager + kTopBoxChannels);
  const uint32_t end = LoadGuestU32(base, manager + kTopBoxChannels + 4u);
  bool queued = false;
  for (uint32_t at = begin; Plausible(at) && at < end && (end - at) <= 0x400u; at += 4u) {
    const uint32_t channel = LoadGuestU32(base, at);
    if (Plausible(channel) &&
        ((LoadGuestU32(base, channel + 8u) - LoadGuestU32(base, channel + 4u)) & ~3u) != 0u) {
      queued = true;
      break;
    }
  }
  if (!queued) {
    return 3;
  }
  return base[manager] != 0u || base[manager + 1u] != 0u ? 3 : 2;
}

}  // namespace

// (IM): the root's slide on each flag edge the tick made, in its order.
REX_EXTERN(rex_InteractionMode_Tick) {
  const uint32_t im = ctx.r3.u32;
  const bool readable = base != nullptr && Plausible(im);
  const uint8_t over = readable ? base[im + kImScreenOver] : 0u;
  const uint8_t hidden = readable ? base[im + kImHiddenByGui] : 0u;
  __imp__rex_InteractionMode_Tick(ctx, base);
  if (!readable) {
    return;
  }
  const uint32_t presentation = LoadGuestU32(base, im + kImPresentation);
  if (presentation == 0u) {
    return;
  }
  const uint8_t over_now = base[im + kImScreenOver];
  const uint8_t hidden_now = base[im + kImHiddenByGui];
  int32_t slide = 0;
  if (over == 0u && over_now != 0u) {
    slide = 2;
  } else {
    if (over != 0u && over_now == 0u) {
      slide = 1;
    }
    if (hidden == 0u && hidden_now != 0u) {
      slide = 2;
    } else if (hidden != 0u && hidden_now == 0u) {
      slide = 1;
    }
  }
  if (slide != 0) {
    StateState(presentation, 0u, "Scene", slide, "825C3A78");
  }
}

// (IM): the root on slide 1.
REX_EXTERN(rex_InteractionMode_Clear) {
  ++g_im_clears;
  const uint32_t im = ctx.r3.u32;
  ForgetInteractionList();
  __imp__rex_InteractionMode_Clear(ctx, base);
  if (base == nullptr || !Plausible(im)) {
    return;
  }
  const uint32_t presentation = LoadGuestU32(base, im + kImPresentation);
  if (presentation != 0u) {
    StateState(presentation, 0u, "Scene", 1, "825C3F00");
  }
  StatePanel(base, im);
}

// interaction_mode (135): the four dedicated openers. Each runs the clear first, past its own refusals, then
// stores IM+0x14 its mode, +0x2C the hero and +0x34 the target: a clear inside one is it opening.
REX_EXTERN(rex_InteractionMode_OpenRealEstate) { OpenAndRaise(ctx, base, __imp__rex_InteractionMode_OpenRealEstate); }
REX_EXTERN(rex_InteractionMode_OpenShop) { OpenAndRaise(ctx, base, __imp__rex_InteractionMode_OpenShop); }
REX_EXTERN(rex_InteractionMode_OpenPawnShop) { OpenAndRaise(ctx, base, __imp__rex_InteractionMode_OpenPawnShop); }
REX_EXTERN(rex_InteractionMode_OpenOrb) { OpenAndRaise(ctx, base, __imp__rex_InteractionMode_OpenOrb); }

/// A mode's own IM+64 store (the value register at the `stw rX,0x40(r11)`).
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

// (bar, shown).
REX_EXTERN(rex_OverlayBar_Show) {
  const uint32_t bar = ctx.r3.u32;
  const uint8_t shown = static_cast<uint8_t>(ctx.r4.u32 & 0xFFu);
  if (base != nullptr && Plausible(bar) && base[bar + kBarShown] != shown) {
    const uint32_t presentation = LoadGuestU32(base, bar + kBarPresentation);
    const uint32_t root = LoadGuestU32(base, bar + kBarRoot);
    const std::string path = PathOf(root);
    if (Plausible(presentation) && !path.empty()) {
      StateState(presentation, root, path, shown != 0u ? BarShownSlide(base) : 1, "82B3EA18");
    }
  }
  __imp__rex_OverlayBar_Show(ctx, base);
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
