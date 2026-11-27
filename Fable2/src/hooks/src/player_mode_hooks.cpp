// refii — Fable 2: A PLAYER MODE IS ENTERED ONCE.
//
// The contract, read in the binary (TU1 / disc):
//
//   Each player's control modes live in the play-game mode (CGameModePlayGame, vtable 0x8200247C, the
//   one game mode whose vf2 answers 1, so sub_82274EE0 hands it to every pusher), one {player key,
//   mode list} entry per push at +0x18..+0x1C. Entering a mode (vf14, +0x38) sets its entered byte
//   (+0x10 = 1) and registers it with the action handler registry (game+0x7C); leaving it (vf16, +0x40)
//   clears the byte and unregisters it. Every player mode does both: sub_825D6710 / sub_8248F398
//   (`li r11,1; stb r11,0x10(r31)`, then lhActionHandlerRegistry_AddHandler), CPlayerModeBase_p_vf16
//   0x826C1630 / 0x8258C3E0 (`li r11,0; stb r11,0x10(r3)`, then RemoveHandler), and the same pair in
//   every other mode class (ExtendExpression, Swimming/Falling sub_826BAFB8 / sub_826BB020, Strafe,
//   HandHolding, TargetLock, Block, UpdateFromNetwork, MagicCharge).
//
//   The play-game mode's SUSPEND (vf8, 0x823D7FC8 / 0x8245F5A8) leaves only entered modes:
//       TU1  823D8028  lbz r11,0x10(r3) ; cmplwi ; beq      disc 8245F608
//   Its RESUME (vf5, 0x823D7F28 / 0x8245F508) enters each entry's top mode through sub_823D93E8 /
//   sub_824609C8 WITHOUT that test:
//       TU1  823D9410  lwz r11,0(r30) ; mr r3,r30 ; lwz r10,0x38(r11) ; mtctr ; bctrl 823D9420
//       disc 824609F0  ...                                                 bctrl 82460A00
//   and the registry's add (0x8244B978 / 0x825BB2C0) never looks for the handler it is given, so a
//   second enter files the handler a second time and the action dispatcher (sub_821CCED8) hands each
//   GameAction to it twice.
//
//   A mode IS entered while the play-game mode is suspended: the drop-in's henchman setup
//   (sub_8233D820 / sub_823201F8, on the frame after lhNetManager_SetupHenchman sets NET+0xF0) binds
//   hero two with SetupCamera(Player 2, hero two, 1) (`li r5,1` 0x8233D8DC / 0x823202B4), whose push
//   (sub_823D8AA8 / sub_82460088, r6 = 1) enters player two's default mode at once (`bctrl`
//   0x823D8BC8 / 0x824601A8) while the co-op screen or the drop-in UI mode covers the game. When the
//   cover pops, the resume enters it again: player two's A (GameAction 6, CPlayerModeBase_p_vf1 ->
//   sub_826A1178 -> CECAbilities_PerformRequest 0x826A1438 / 0x82589688) runs the interact ability
//   twice and every use doubles, until the next pause leaves the mode (RemoveHandler's list removal
//   takes every copy) and the next resume enters it once.
//
//   So the resume enters a top mode only when it is not entered already — the suspend's own test.
//   The active types the resume then states for the player (lhActionHandlerRegistry_
//   SetActiveTypesForPlayer, 0x823D9424 on) are unchanged. A mode the resume finds not entered is
//   the shipped path.

#include <atomic>
#include <cstdint>

#include <rex/logging.h>
#include <rex/ppc/context.h>

#include "core/guest_state.h"

extern "C" uint8_t* RefiiGuestBase(void);

namespace {

std::atomic<bool> g_said_entered{false};

constexpr uint32_t kEnteredByte = 0x10u;

}  // namespace

// Before the resume's `lwz r11,0(r30)` (r30 = the entry's top mode): true = the mode is entered
// already, and its enter call is stepped over.
bool RefiiPlayerModeEnteredOnce(PPCRegister& r30) {
  uint8_t* base = RefiiGuestBase();
  const uint32_t mode = r30.u32;
  if (base == nullptr || mode < 0x10000u || mode >= 0xC0000000u) {
    return false;
  }
  if (refii::vfs::LoadGuestU8(base, mode + kEnteredByte) == 0u) {
    return false;
  }
  if (!g_said_entered.exchange(true, std::memory_order_relaxed)) {
    REXLOG_INFO("Refii player modes: the game's resume finds mode 0x{:08X} (vtable 0x{:08X}) entered "
                "already and does not enter it again",
                mode, refii::vfs::LoadGuestU32(base, mode));
  }
  return true;
}
