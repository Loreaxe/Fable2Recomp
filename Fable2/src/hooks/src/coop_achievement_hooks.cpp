// refii — AN ACHIEVEMENT IS WRITTEN ONLY FOR A HERO WITH A PLAYER.
//
// The contract, read in the binary (TU1 / disc):
//
//   CECStats_UnlockAchievement (0x824D8F88 / 0x824A0100) writes the achievement for BOTH heroes of
//   the entity registry (`*(*(*(game+0x58)+4))`): the id-1 entity at +0x68 (sub_8222C1B8) and the
//   id-2 entity at +0x70 (sub_828E5A78), each when alive (entity+0x90 bit 0x40), through
//   sub_824D96E8 / sub_824A0850 on that hero's stats (component 57). That body takes the hero's
//   player record (component 33) and reads the Xbox user index through its Player handle, with no
//   test of the handle:
//       TU1  824D9A68  lwz r11, 0x14(r10)     ; r10 = component 33, +0x14 = the Player
//            824D9A74  lwz r31, 0x28(r11)     ; Player+0x28 = the user index
//       disc 824A0A70  lwz r11, 0x14(r10)
//            824A0A7C  lwz r29, 0x28(r11)
//   The handle is 0 from the component's constructor (`stw r30,0x14(r31)`, r30 = 0, 0x823E0164) and
//   has ONE writer, sub_823E0628 / sub_823BEFF0 (`stw r4,0x14(r31)`), called only from the Player's
//   SetupCamera (0x82389F5C / 0x8238F72C).
//
//   Hero two is registered at +0x70 the moment it is created: lhNetManager_SetupHenchman
//   (0x8233D090), called from the co-op screen's join (sub_82437168, 0x8243720C), creates it as
//   entity id 2 (sub_82315030 -> sub_823176C8(.., 2) -> sub_8238B9C8 -> sub_8238B860, which writes
//   the id at entity+0x14, 0x8238B8CC, and adds it, 0x8238B8DC); lhEntityManager_Add files id 2 at
//   +0x70 (0x82363C70..0x82363C9C). For a local host its Player is bound later, on a frame update that
//   sees the setup's pending byte (NET+0xF0, set at 0x8233D128): sub_82195F10 0x821961F0 ->
//   sub_8233D820 -> SetupCamera(Player 2, hero two, 1) at 0x8233D8E0. Between the two, hero two is
//   alive with a 0 handle, and any achievement statement for either hero reads guest 0x28.
//
// The guest's other readers of the handle test it first (CECPlayer_vf15 0x823EE188,
// Lua_Player_IsAbleToSaveGame 0x823F3664, sub_824DF018 0x824DF148, the rumble start sub_823EAA58
// 0x823EAAE0); this one does not. A hero with no Player has no profile to write to, so the
// statement for that hero ends where the body ends when it writes nothing (TU1 0x824D9B78, which
// returns r29 = 0: "not written"; disc 0x824A0C3C). The other hero's write is untouched, and once
// SetupCamera has bound the Player every statement runs the shipped path.

#include <cstdint>

#include <rex/logging.h>
#include <rex/ppc/context.h>

#include "core/guest_state.h"

extern "C" uint8_t* RefiiGuestBase(void);

namespace {

bool g_said_unbound = false;

}  // namespace

// Before `lwz r11,0x14(r10)` (r10 = the hero's component 33): true = the hero has no Player bound,
// and the body's exit is taken.
bool RefiiAchievementNoPlayer(PPCRegister& r10) {
  uint8_t* base = RefiiGuestBase();
  if (base == nullptr || r10.u32 < 0x10000u || r10.u32 >= 0xC0000000u) {
    return false;
  }
  if (refii::vfs::LoadGuestU32(base, r10.u32 + 0x14u) != 0u) {
    return false;
  }
  if (!g_said_unbound) {
    g_said_unbound = true;
    REXLOG_INFO("Refii achievements: a hero with no Player bound yet (component 33 at 0x{:08X}) is not written to",
                r10.u32);
  }
  return true;
}

// The rumble fade (TU1 sub_823EAB80 / disc sub_823C95A8; callers Lua_Player_FadeRumbleFromTable
// 0x823EB384, sub_82392250 0x8239BEA0, sub_823EAF48, sub_82776FF0, sub_82777C78, sub_8287BAA0) reads
// the same handle with no test:
//     TU1  823EAB9C  lwz r10, 0x14(r3)  /  823EABAC  lwz r9, 0x28(r10)
//     disc 823C95C4  lwz r10, 0x14(r3)  /  823C95D4  lwz r9, 0x28(r10)
// and matches the user index against the live rumbles. An unbound hero has no pad and no rumble, so
// the fade ends where it ends when no rumble matches (TU1 0x823EAC1C / disc 0x823C9644, the epilogue).
// Before `lwz r9,0x28(r10)`: true = the hero has no Player bound.
bool RefiiRumbleFadeNoPlayer(PPCRegister& r10) {
  return r10.u32 == 0u;
}
