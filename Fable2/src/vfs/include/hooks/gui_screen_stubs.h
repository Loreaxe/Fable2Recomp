// The ported screens' levers (gui_screen_stubs.cpp), dispatched by the guest tick pump.
#ifndef REFII_VFS_HOOKS_GUI_SCREEN_STUBS_H
#define REFII_VFS_HOOKS_GUI_SCREEN_STUBS_H

#include <cstdint>

#include <rex/ppc/context.h>

namespace refii::vfs {

/// The logbook's opener on another player ran (`sub_823C48D8`); `over_world_map` when the world map
/// was the screen up, which is where its B returns.
void StateLogbookOpenedOnPlayer(bool over_world_map);

/// `PauseBack` — a ported book's B at its root (TU1 screen id `screen`).
bool DispatchPauseBack(PPCContext& ctx, uint8_t* base, uint32_t screen);

/// `PauseCycle:<p>:prev|next` — a ported book's LB / RB.
bool DispatchPauseCycle(PPCContext& ctx, uint8_t* base, uint32_t screen, uint32_t player, bool next);

/// `CreditsEnd` — Godot's credits roll ended: the quiet exit.
bool DispatchCreditsEnd(PPCContext& ctx, uint8_t* base);

}  // namespace refii::vfs

#endif
