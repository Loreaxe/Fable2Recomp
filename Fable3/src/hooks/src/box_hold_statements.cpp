// refii - the control hold a display box puts on the heroes, stated at the two calls that apply
// and lift it: `@Box.Hold(box, held)`, for Godot's pointer (Fable3PointerStatements).
//
// Addresses are TU3 / disc; offsets are from the box.
//
// THE APPLY, BoxShown sub_82958550(box) / disc sub_8291F288, the tail of every box class's Show
// (26 callers on each build). It asks the box its type (`lwz r10,0x1C(r11)` / `bctrl`, 0x82958564..
// 0x8295856C / 0x8291F29C..0x8291F2A4) and goes on only for a type under 5 or from 9 to 28 — disc 27
// (`cmpwi r3,8` / `cmpwi r3,5` / `cmplwi r3,5` 0x82958570..0x82958584, `cmpwi r3,9` / `cmpwi r3,0x1C`
// 0x829586E0..0x829586EC; disc 0x8291F2A8..0x8291F2BC, `cmpwi r3,0x1B` 0x8291F3DC): the
// intermission (5), the accept box (6), the button box (7) and the card (8) hold nothing. Then,
// unless the box pushed the UI mode (`lbz r11,0x14(r29)` / `bne`, 0x82958598..0x829585A0 /
// 0x8291F2D0..0x8291F2D8; +0x14 is set by sub_82958388 / sub_8291F0C0, game_state_hooks.cpp) or the
// top mode is the map's 12 (`addi r4,r3,-0xC` 0x829585D4 / 0x8291F30C), it puts the control
// restriction "DisBox" (unk_8359CB7C / unk_8355BEE4) on player one's hero — `li r5,1` /
// `bl sub_823CD3B0` 0x82958620..0x8295862C / `bl sub_823AAF90` 0x8291F358..0x8291F364 — and in
// session kind 1 on the partner's (0x82958668 / 0x8291F3A0), and records it:
// `stb r10,0x15(r29)` 0x82958674 / 0x8291F3AC.
//
// THE LIFT, BoxHidden sub_82958710(box) / disc sub_8291F3F0 (every class's Hide, the base's force
// close and its default hide): only when +0x15 is set (`lbz r11,0x15(r3)` / `beq` 0x8295871C..
// 0x8295872C / 0x8291F3FC) it removes "DisBox" (`bl sub_823CD428` 0x82958770 / `bl sub_823AB008`
// 0x8291F44C) and clears the record: `stb r27,0x15(r28)` 0x82958834 / `stb r11,0x15(r29)` 0x8291F490.
//
// Each is stated as it returns, from the byte it wrote: +0x15 after the apply, 0 after the lift.
// Both bodies run unchanged; the Show stubs' own calls (display_box_stubs.cpp) come through here too.

#include <cstdint>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "gui_statements.h"

REX_EXTERN(__imp__rex_DisplayBox_Shown);
REX_EXTERN(__imp__rex_DisplayBox_Hidden);

namespace {

using namespace refii::fable3;

/// +0x15: this box holds the heroes' controls.
constexpr uint32_t kBoxHolds = 0x15u;

void State(uint32_t box, bool held) {
  Statement("@Box.Hold").Add(Text(Hex(box))).Add(Boolean(held)).Post();
  REXLOG_INFO("[fable3-boxes] box {:08X} {} the heroes' controls", box, held ? "holds" : "let go of");
}

}  // namespace

// (box).
REX_EXTERN(rex_DisplayBox_Shown) {
  const uint32_t box = ctx.r3.u32;
  __imp__rex_DisplayBox_Shown(ctx, base);
  if (base != nullptr && Plausible(box) && base[box + kBoxHolds] != 0u) {
    State(box, true);
  }
}

// (box).
REX_EXTERN(rex_DisplayBox_Hidden) {
  const uint32_t box = ctx.r3.u32;
  const bool held = base != nullptr && Plausible(box) && base[box + kBoxHolds] != 0u;
  __imp__rex_DisplayBox_Hidden(ctx, base);
  if (held) {
    State(box, false);
  }
}
