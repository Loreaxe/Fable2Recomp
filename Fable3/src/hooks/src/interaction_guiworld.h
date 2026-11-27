// refii - the Sanctuary's interaction mode (mode 3, CInteractionModeGUIWorld) as Godot drives it
// (interaction_guiworld.cpp): its pad cut, its levers, and the vtable test the shared list input
// and the row writer make.

#pragma once

#include <cstdint>

#include <rex/ppc.h>

namespace refii::fable3 {

/// `guiworld:<p>:<verb>` — the Sanctuary's input for local player p:
///   `a` `b` `x` `y` `start` (74 75 76 77 14) and `lb` `rb` `lt` `rt` (the release codes 96 97 94 95)
///   through the interaction manager's own input, as the pad hands them;
///   `cursor:<row>` (0-based) the list's cursor, then the mode's selection-changed slot (event 137);
///   `stick:<left|right|up|down|none>` and `rs:<left|right|up|down|none>` a stick held in a direction
///   (or let go), stamped each step as the list input stamps it;
///   `flick:<left|right|up|down>` one flick gesture;
///   `dye-value:<f>` the dye bar's value.
inline constexpr char kGuiWorldAction[] = "guiworld:";
bool AnswerGuiWorld(PPCContext& ctx, uint8_t* base, const char* action);
/// One guest step of the held sticks.
void PumpGuiWorld(PPCContext& ctx, uint8_t* base);

/// Whether `mode` is a CInteractionModeGUIWorld (its vtable word).
bool IsGuiWorld(const uint8_t* base, uint32_t mode);
/// The shared list input sub_82972ED8 (disc sub_82964248) for a CInteractionModeGUIWorld: true when
/// it answered (r3 set), false for any other class.
bool GuiWorldListInput(PPCContext& ctx, uint8_t* base);

}  // namespace refii::fable3
