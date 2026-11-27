// refii - the world map's close as the host asks for it: `world-map:close:<p>`, the guest's own
// close request made on the guest tick thread. TU3 / disc addresses; each offset names its base.
//
// THE REQUEST is WorldMap.Exit — TU3 sub_824ED768(M, pendingStop) / disc sub_824CDB60(M), M the
// map's game mode (CGameModeWorldMap, vtable TU3 0x82094918 / disc 0x82092E14). It only marks the
// mode: TU3 `stb r4,0xC0(r3)` 0x824ED778 (the pending fast-travel stop) and `stb r11,0x7D(r3)`
// 0x824ED780 (r11 = 1), disc `stb r11,0x79(r3)` 0x824CDB74, then one post through the GUI manager
// (`bl sub_823333B0` 0x824ED794 / `bl sub_8232C4A0` 0x824CDB88). The mode's own update (slot 7, TU3
// 0x824EC448 -> sub_824ED7C8 `lbz r11,0x7D(r3)` 0x824ED7D8 / disc 0x824CC9E0 `lbz r8,0x79(r31)`
// 0x824CCA4C) acts on the mark: the close TU3 sub_824ED850 / disc sub_824CDBB8 (Lua `CloseMap`, the
// switch home when the map was opened from the world, `MapScene_Present(H, 0)` 0x824ED99C /
// 0x824CDCEC, which map_table_hooks.cpp states), then the mode pops itself (`bl sub_8250AFD0`
// 0x824ED810 / `bl sub_824EAB78` 0x824CCA80). The map's Lua reaches the same function from B at
// its top level and from START (worldmap/input.lua ZoomOut, ExitToGame); those stay the pad's.
//
// WHICH MODE. The guest's own C++ callers take the TOP game mode and ask it when its slot 1 (the
// mode's id) answers 12, with `li r4,0`: TU3 sub_823A0438 (0x823A04A8..0x823A04EC), sub_8239DC98
// (0x8239DDC4..0x8239DE0C) / disc 0x82446D50..0x82446D94. The top is `*(*(*S+4)+8)`, S =
// `*(*(state+0x30)+4)`, state = `*(*0x83597034+0xC)`, alive while `state+0xBA` is set (the natives'
// gate, 0x8254D0B0). Id 12 is not the map alone — slot 1 of the town-map prototype's mode (vtable
// 0x820C5F98) is the same folded `li r3,0xC` body (0x82E0CC98 / disc 0x82C4BF28) — so the mode is
// asked only when the top IS the world map the GUI manager holds, `*(G+0x54)`, G = `*0x83596F84`
// (GUI.ToggleWorldMap pushes that pointer: `G+8+76`, 0x8254D16C / disc 0x8251D37C). With any other
// mode on top the map's update does not run and a mark would wait for the next time it does, so
// the lever refuses and says what is on top.
//
// GUI.ToggleWorldMap IS NOT A CLOSE: with mode 12 on top it is a bare pop (`li r4,0xC` / `bl
// sub_8242BDF8` 0x8254D0F8..0x8254D100 / disc `bl sub_8240E820` 0x8251D310) whose remove slot (slot
// 8, TU3 0x824EC590 / disc 0x824CCB58) only takes the map's two input processes off the input
// manager: the scene stays presented and nothing can reach the map again.

#include <cstdint>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "box_answers.h"
#include "mod_seam.h"
#include "../../vfs/include/fable3_guest_build.h"

namespace {

using refii::fable3::seam::Byte;
using refii::fable3::seam::Plausible;
using refii::fable3::seam::Word;

const refii::guest::Addr kGuiManager{0x83596F84u};
const refii::guest::Addr kGame{0x83597034u};

/// G+0x54: the world map's game mode, null until the first open makes it.
constexpr uint32_t kGuiWorldMap = 0x54u;
/// *kGame+0xC: the game state; +0xBA it is initialised, +0x30 its mode stacks.
constexpr uint32_t kGameState = 0xCu;
constexpr uint32_t kStateAlive = 0xBAu;
constexpr uint32_t kStateModes = 0x30u;

/// The mode on top of the game's mode stack, or 0.
uint32_t TopMode(const uint8_t* base) {
  const uint32_t state = Word(base, Word(base, kGame) + kGameState);
  if (!Plausible(state) || Byte(base, state + kStateAlive) == 0u) {
    return 0u;
  }
  const uint32_t stack = Word(base, Word(base, state + kStateModes) + 4u);
  if (!Plausible(stack) || Word(base, stack + 4u) == 0u) {
    return 0u;
  }
  return Word(base, Word(base, Word(base, stack) + 4u) + 8u);
}

}  // namespace

namespace refii::fable3 {

bool AnswerWorldMapClose(PPCContext& ctx, uint8_t* base, const char* action) {
  const char* player = action + sizeof(kWorldMapCloseAction) - 1u;
  if ((player[0] != '1' && player[0] != '2') || player[1] != '\0') {
    REXLOG_WARN("[fable3-world-map] '{}' names no player; dropped", action);
    return false;
  }
  const uint32_t map = seam::Word(base, seam::Word(base, kGuiManager) + kGuiWorldMap);
  const uint32_t top = TopMode(base);
  if (!seam::Plausible(map) || top != map) {
    REXLOG_INFO("[fable3-world-map] close asked by player {}: refused, the world map {:08X} is not the top "
                "game mode (top {:08X}, vtable {:08X})",
                player[0], map, top, seam::Word(base, top));
    return false;
  }
  PPCContext work = seam::Frame(ctx);
  work.r3.u64 = map;
  work.r4.u64 = 0u;
  if (!seam::CallAt(work, base, refii::guest::Pick(0x824ED768u, 0x824CDB60u))) {
    REXLOG_WARN("[fable3-world-map] close asked by player {}: WorldMap.Exit is not a recompiled function", player[0]);
    return false;
  }
  REXLOG_INFO("[fable3-world-map] close asked by player {}: WorldMap.Exit({:08X}) called", player[0], map);
  return true;
}

}  // namespace refii::fable3
