// refii - THE TEXTS FABLE 3 COMMITS STRAIGHT TO A TEXT BOX, stated where the commit is guarded,
// for Godot's Fable3Elements. Every address is the Fable 3 disc IDB (port 13339); each offset
// names its base.
//
// THE COMMIT. GuiTextBox_Commit sub_82B7A488(box, LHString* text) assigns the box's shown string
// (box+0x88, 0x82B7A4C4). Its callers outside the stated writers (sub_82876D78, sub_82BDC5D8,
// sub_8296FF98, ui_stated_hooks.cpp) reach it the same way: the element `rE`, then
// `lwz r11,4(rE)` / `lwz r11,0(r11)` — the element's renderable node and its text box — each
// tested for 0, and only then `bl sub_82B7A488` with the text in r4. A headless element's +4 is 0
// (headless_presentation_hooks.cpp), so the commit is never reached and the text is said nowhere
// else. Each [[midasm_hook]] below sits ON that `lwz r11,4(rE)`, where rE is the element and the
// text is already built: in a register the caller then moves to r4, or in a stack LHString the
// caller takes the address of (`addi r4,r1,X`). The statement is `@Ui.Text(pres, elem, path,
// text, "82B45268")` — the element named by the resolver hook (ElementName), the text exactly as
// the commit would have been given it.
//
// THE SITES (guard address -> element, text), Fable3/versions/Fable3/refiii_config.toml:
//   money alert   sub_8287C3E0 (show / change) 0x8287C42C r11, r4 — `sub_82304950(&s, A+16)` "%d"
//                 at 0x8287C418; sub_8287C4B8 (the count) 0x8287C674 r11, r4 (0x8287C660);
//                 the floaties sub_8287B950 0x8287B9D8 r11, r1+0x50
//   crime timer   fine sub_8260C4D0 0x8260C524 r11, r4; seconds sub_8260C568 0x8260C5BC r11, r4
//   job           sub_82973E00 0x82973E70 r11, r4 (prefix + level, sub_82DA5E00 at 0x82973E54)
//   look-at       sub_829739D8 0x82973B68 r31, r27
//   quest summary sub_829BCA18 0x829BCC0C r3, r29; 0x829BCC60 r3, r1+0x54; 0x829BCCAC r3, r1+0x58
//   stats         sub_829B7458 0x829B75C0 r31, r1+0x50; sub_829B76A0 0x829B7D68 r3, r1+0x7C,
//                 0x829B7DE4 r31, r1+0x64, 0x829B7F30 r30, r3, 0x829B7F68 r27, r1+0x70,
//                 0x829B7FD4 r30, r1+0x80, 0x829B8018 r27, r1+0x84; sub_829B8C48 0x829B8D20
//                 r31, r23, 0x829B8D94 r31, r27, 0x829B8E08 r31, r24; sub_829B8FC0 0x829B9498
//                 r31, r1+0x50, 0x829B9634 r31, r1+0x54, 0x829B968C r31, r3
//   promise       CPromiseMessageBox_Show sub_827DA840 0x827DA8E0 r30, r3
//   divorce       sub_8297F7F8 0x8297F84C / 0x8297F8A4 r3, r30; sub_8297F8D8 0x8297F97C r31, r4,
//                 0x8297FB90 r3, r1+0x50; sub_8297FBC0 0x8297FCA0 r3, r1+0x50, 0x8297FCFC r3,
//                 r30, 0x8297FDC0 r30, r4, 0x8297FE00 r28, r4, 0x8297FED4 r27, r1+0x5C,
//                 0x8297FEFC r26, r1+0x58, 0x8297FF5C r3, r1+0x64; sub_8297FFC0 0x829800B0 r30,
//                 r4, 0x829800F4 r29, r1+0x54, 0x82980130 r27, r1+0x54, 0x82980214 r26, r1+0x54,
//                 0x82980260 r3, r1+0x58; sub_82980460 0x829804C8 r31, r4; sub_82980578
//                 0x829805CC r31, r3
//   food          CFoodItemQueryBox_Show sub_82B8C618 0x82B8C860 r29, r1+0x50, 0x82B8CA3C r31, r3
//   interaction   the text box sub_82C203B8 0x82C206E4 r30, r26 (the child it resolved: `+44` + "1",
//   text          else `+44` — CrimeInfo's `Contents.Crimes`)
//   received item sub_82452530 (CECReceivedItemQueue_vf18 0x82456F40): `ReceivedItems.ReceivedItemAlert.
//                 Group.PlayerNum` (dword_83598724, resolved in the HUD 0x824525CC) 0x82452600 r31,
//                 r1+0x50 (the number by sub_82304950 0x824525E4, or "" by sub_82DA5320 0x824525FC)
//   HUD icon      the pooled icon's slot 3 sub_82B86CF8(icon, title, opinion): `Contents.Title`
//                 (slot+16, 0x82B86D14) 0x82B86D34 r11, r4; `Contents.Opinion` (slot+12, 0x82B86D54)
//                 0x82B86D74 r11, r29 (`mr r29,r5` 0x82B86D0C)
//   credits       the roll's init sub_82B74BA8 0x82B74E18 r31, r1+0x50; its tick sub_82B74F98
//                 0x82B750B0 r30, r1+0x50 (the empty line past the end) and 0x82B75068 r30 —
//                 the next line, `*(roll+0x1C) + 4 * roll+0x14` (0x82B75080..0x82B75088), r31 the
//                 roll and r11 its index (0x82B75040).
//   mini-game     the counter sub_82716470 (bar, player, text): `Bar.Elements.Text.Counter<n>.Counter.
//   counter       Text` (dword_8359ACD8 joined 0x82716504, resolved 0x8271650C) 0x82716524 r3, r30
//                 (`mr r30,r5` 0x82716480)
//   mode button   sub_829703E8(mode, parent, tag name): the button's `Title` (resolved under r4 in
//   title         IM+0, 0x82970434) 0x82970450 r31, r3 — the tag's text sub_823CBB30 answered at
//                 0x8297044C, moved to r4 at 0x82970454
//   crowd bar     sub_82B610F0(mode): `CrowdInterestBar.CurrentRenown` (dword_8359E54C, 0x82B61134)
//                 0x82B61268 r31, r3 (the renown by sub_82304950 "%d", 0x82B61264)
//   side pane     sub_82C201B8(pane, path, text): the path under dword_8359F750 in IM+0 (sub_82C20110,
//                 0x82C201D0) 0x82C201E0 r31, r29 (`mr r29,r5` 0x82C201CC); the guest then commits
//                 only a kind-5 (text) node (0x82C201EC), which the .bgf's own element type is
//   save info     Lua_GUI_ShowSaveInfoBoxFromStatue's sub_829677D0(mode, entity, flag), then IM+64 = 9
//                 SaveGameInfo (sub_82971CC8 0x82968120), each path resolved in IM+0 (r24):
//                 dword_8359CDDC 0x82967A00 r3, r1+0x68; C290 0x82967AE4 r3, r1+0x6C; C294
//                 0x82967BA8 r3, r1+0x54; C298 0x82967C04 r3, r1+0x58; C2A0 0x82967C94 r3, r1+0x58
//                 and 0x829680E0 r3, r1+0x58; C29C 0x82967CEC r3, r31 (sub_82304950 0x82967CCC) and
//                 0x82968098 r3, r1+0x50
// A text register the caller moves to r4 after the guard (`mr r4,rS`) is not written between the
// guard and that move (the guard touches only r11/r10 and cr6).

#include <cstdint>
#include <string>

#include <rex/ppc/context.h>
#include <rex/system/kernel_state.h>
#include <rex/system/xmemory.h>

#include "gui_statements.h"

namespace {

using namespace refii::fable3;

constexpr const char* kVia = "82B45268";

uint8_t* GuestBase() {
  rex::system::KernelState* state = rex::system::kernel_state();
  rex::memory::Memory* memory = state != nullptr ? state->memory() : nullptr;
  return memory != nullptr ? memory->virtual_membase() : nullptr;
}

void StateCommit(uint32_t element, uint32_t text) {
  uint8_t* base = GuestBase();
  if (base == nullptr || !Plausible(element) || !Plausible(text)) {
    return;
  }
  uint32_t presentation = 0u;
  std::string path;
  if (!ElementName(element, presentation, path)) {
    return;
  }
  StateText(presentation, element, path, ReadLhString(base, text), kVia);
}

}  // namespace

/// The element and the text's LHString in registers.
void RefiiF3StateCommit(PPCRegister& element, PPCRegister& text) { StateCommit(element.u32, text.u32); }

// The element in a register, the text a stack LHString at r1 + the offset in the name.
#define REFII_F3_STACK_COMMIT(offset)                                                 \
  void RefiiF3StateCommitAt##offset(PPCRegister& element, PPCRegister& stack) {       \
    StateCommit(element.u32, stack.u32 + 0x##offset##u);                              \
  }
REFII_F3_STACK_COMMIT(50)
REFII_F3_STACK_COMMIT(54)
REFII_F3_STACK_COMMIT(58)
REFII_F3_STACK_COMMIT(5C)
REFII_F3_STACK_COMMIT(64)
REFII_F3_STACK_COMMIT(68)
REFII_F3_STACK_COMMIT(6C)
REFII_F3_STACK_COMMIT(70)
REFII_F3_STACK_COMMIT(7C)
REFII_F3_STACK_COMMIT(80)
REFII_F3_STACK_COMMIT(84)
#undef REFII_F3_STACK_COMMIT

/// The credits roll's next line: `*(roll+0x1C) + 4 * index` (sub_82B74F98 at 0x82B75080..0x82B75088).
void RefiiF3StateCreditsLine(PPCRegister& element, PPCRegister& roll, PPCRegister& index) {
  uint8_t* base = GuestBase();
  if (base == nullptr || !Plausible(roll.u32)) {
    return;
  }
  const uint32_t lines = LoadGuestU32(base, roll.u32 + 0x1Cu);
  if (!Plausible(lines)) {
    return;
  }
  StateCommit(element.u32, lines + 4u * index.u32);
}

/// THE CREDITS' POSITIONS, stated at the roll's own SetPosition calls (sub_82876288: r3 the
/// element, r4 the presentation, f1..f3 x, y, z): each row at the roll's init (0x82B74E78, y
/// stepping down by 32, `fsubs f31,f31,f29` 0x82B74E80) and tick (0x82B750F8, y = the read-back
/// plus the step, `fadds f31,f13,f30` 0x82B75020), the logo sheet at init (0x82B74F10) and as it
/// scrolls once the lines ran out (0x82B75144, +0x8C its y). `@Ui.Position(pres, elem, path, x,
/// y, z, via)`: Godot takes the credits' positions from these alone (Fable3Elements' cut drops
/// the setter's own `@Element.Position` for the credits document).
void RefiiF3CreditsPosition(PPCRegister& element, PPCRegister& presentation, PPCRegister& x, PPCRegister& y,
                            PPCRegister& z) {
  uint32_t named_in = 0u;
  std::string path;
  if (!Plausible(element.u32) || !ElementName(element.u32, named_in, path)) {
    return;
  }
  Statement("@Ui.Position")
      .Add(Text(PresentationKey(presentation.u32 != 0u ? presentation.u32 : named_in)))
      .Add(Text(Hex(element.u32)))
      .Add(Text(path))
      .Add(Number(static_cast<float>(x.f64)))
      .Add(Number(static_cast<float>(y.f64)))
      .Add(Number(static_cast<float>(z.f64)))
      .Add(Text("82B3FD70"))
      .Post();
}

/// THE LOOK-AT ALERT'S SLIDE, as its writer sub_829739D8(LA, record, hero) chose it from the
/// record the HUD tick sub_82268C78 polled (0x82268D8C): 2 for type 0, 4 for 1, 5 for 2 (`lwz
/// v3,+4` 0x829739E4..0x82973A1C), 8 when the record's +8 flag is set (`lbz r11,8(r27)`
/// 0x82973A20, `li r29,8` 0x82973A30), 1 while the interaction mode's IM+24 is set (0x82973A3C..
/// 0x82973A4C) — r29 at 0x82973A50, before the element's SetState (0x82973A68). The tick's hide is
/// slide 6 (`li r5,6` 0x82268DB8, SetState 0x82268DC8), reached at 0x82268DA8.
/// hud.bgf `LookAt.LookAt.LookAt` (#510): 1 hidden, 2 shown, 4/5 blink, 6 to hidden, 8 in active.
void RefiiF3LookAtSlide(PPCRegister& slide, PPCRegister& record) {
  uint8_t* base = GuestBase();
  const uint32_t type = base != nullptr && Plausible(record.u32) ? LoadGuestU32(base, record.u32 + 4u) : 0u;
  Statement("@Hud.LookAt")
      .Add(Number(static_cast<float>(static_cast<int32_t>(slide.u32))))
      .Add(Number(static_cast<float>(static_cast<int32_t>(type))))
      .Post();
}

void RefiiF3LookAtHidden() { Statement("@Hud.LookAt").Add(Number(6.0f)).Add(Number(-1.0f)).Post(); }
