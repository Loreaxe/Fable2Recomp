// refii - the save menu's row from outside: Godot's pointer states a row and the menu's own steps
// take its cursor there, to be accepted there (`savemenu-row:<p>:<row>[:accept]`).
//
// THE GUEST'S MECHANISM (SV = *(SM+0x38); TU3 addresses, the disc's bodies are the same instruction
// for instruction). The menu takes no row as an argument: its cursor is SV+12 (1 Save, 2 Save and
// Quit, 3 Quit, 4 Character Management) and every move is one of two steps of its input
// sub_829B5160(SV, ev*, player) (disc sub_8296C918):
//   - up sub_829B5480 (disc sub_8296CC38): behind the menu's pace (SV+16 against the clock and
//     flt_8206CBF4, 0x829B54BC..0x829B54DC) and only while saving is allowed (`lbz r11,1(r31)` / `beq`
//     0x829B54E0..0x829B54EC), `row - 1` clamped to 1..4 into SV+12 (`addi r11,r11,-1` ..
//     `stw r6,0xC(r31)` 0x829B54F8..0x829B5524), then the redraw sub_829B5350(SV) (0x829B5528);
//   - down sub_829B5540 (disc sub_8296CCF8): the pace, `row + 1` clamped (`addi r11,r11,1` ..
//     `stw r6,0xC(r31)` 0x829B55AC..0x829B55D8), the redraw (0x829B55DC). It has no `allowed` test.
// The redraw reads SV+12 alone (`lwz r5,0xC(r3)` 0x829B535C). The input itself runs only while
// the menu is open (`lbz r11,0(r3)` 0x829B5174) and for player one's record (`lwz r11,0x24(r3)` /
// `cmpw r11,r5` / `bne` 0x829B51A8..0x829B51B0; disc 0x8296C960..0x8296C968); 72 plays the select
// cue and runs the accept sub_829B55F8(SV), which reads the row at SV+12 (`lwz r11,0xC(r3)`
// 0x829B5608; disc 0x8296CDC0).
//
// The pace is 0.3 s on both builds (flt_8206CBF4, disc flt_8206CBA0 = 0.3) against the game's
// clock (sub_822099A0 less qword_835976D8 over dbl_835976D0); a refused step leaves before any
// store (`blt` 0x829B54DC / 0x829B559C), so it changes nothing, the pace's stamp included.
//
// THE LEVER writes nothing of the menu's. The stated row is a target, and each guest tick the
// menu's own step toward it is called (up or down, by the cursor the menu holds); the step's own
// tests decide, so the cursor moves a row each 0.3 s, as fast as a held stick moves it, and a row
// the menu refuses (above the cursor while saving is not allowed) is never reached. The steps are
// called directly, not through the input, because the input plays its move cue before the step's
// pace test (0x829B51DC..0x829B51F8): a refused try a tick would sound every tick. So a stepped
// row is silent. `:accept` hands 72 to the menu's own input with the player once the cursor is on
// the row, so its open and player tests, its cue and the confirmation guard
// (save_menu_confirm.cpp) all run as for a pad's A. The target is dropped when the menu closes,
// when it is reached, or after 5 s.

#include <cstdint>
#include <cstdlib>
#include <cstring>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "box_answers.h"
#include "gui_statements.h"

REX_EXTERN(rex_SaveMenu_StepUp);
REX_EXTERN(rex_SaveMenu_StepDown);
REX_EXTERN(rex_SaveMenu_Input);

namespace {

using namespace refii::fable3;

constexpr uint32_t kSaveMenuOpen = 0u;
constexpr uint32_t kSaveMenuRow = 12u;
constexpr long kFirstRow = 1;
constexpr long kLastRow = 4;

/// GUI_SELECT, the action the menu's input accepts on (`cmpwi r3,0x48` 0x829B52C0).
constexpr uint32_t kActionSelect = 72u;

void StoreU32(uint8_t* base, uint32_t address, uint32_t value) {
  const uint32_t swapped = __builtin_bswap32(value);
  std::memcpy(base + address, &swapped, sizeof(swapped));
}

PPCContext Frame(const PPCContext& ctx) {
  PPCContext work = ctx;
  work.r1.u32 = (ctx.r1.u32 - 0x200u) & ~0xFu;
  return work;
}

/// The row the pointer stated, 0 for none; whether to accept there, for whom; ticks left.
long g_target = 0;
bool g_accept = false;
uint32_t g_player = 1u;
int g_ticks = 0;
constexpr int kTicks = 150;

}  // namespace

namespace refii::fable3 {

void PumpSaveMenuRow(PPCContext& ctx, uint8_t* base) {
  if (g_target == 0) {
    return;
  }
  const uint32_t menu = OpenSaveMenu();
  if (base == nullptr || menu == 0u || !Plausible(menu) || base[menu + kSaveMenuOpen] == 0u || --g_ticks < 0) {
    g_target = 0;
    return;
  }
  const long row = static_cast<int32_t>(LoadGuestU32(base, menu + kSaveMenuRow));
  PPCContext work = Frame(ctx);
  work.r3.u64 = menu;
  if (row < g_target) {
    rex_SaveMenu_StepDown(work, base);
    return;
  }
  if (row > g_target) {
    rex_SaveMenu_StepUp(work, base);
    return;
  }
  const bool accept = g_accept;
  g_target = 0;
  if (!accept) {
    return;
  }
  const uint32_t event = work.r1.u32 + 0x100u;
  StoreU32(base, event, kActionSelect);
  StoreU32(base, event + 4u, 0u);
  StoreU32(base, event + 8u, 0u);
  StoreU32(base, event + 12u, 0u);
  work.r4.u64 = event;
  work.r5.u64 = g_player;
  rex_SaveMenu_Input(work, base);
  REXLOG_INFO("[fable3-savemenu] row {} accepted for player {}: the menu's input answered {}", row, g_player,
              work.r3.u32);
}

bool AnswerSaveMenuRow(PPCContext& ctx, uint8_t* base, const char* action) {
  const uint32_t menu = OpenSaveMenu();
  if (base == nullptr || menu == 0u || !Plausible(menu) || base[menu + kSaveMenuOpen] == 0u) {
    REXLOG_WARN("[fable3-savemenu] '{}' with no save menu open; dropped", action);
    return false;
  }
  const char* field = action + sizeof(kSaveMenuRowAction) - 1u;
  if ((field[0] != '1' && field[0] != '2') || field[1] != ':') {
    REXLOG_WARN("[fable3-savemenu] '{}' names no player; dropped", action);
    return false;
  }
  const uint32_t player = static_cast<uint32_t>(field[0] - '0');
  char* end = nullptr;
  const long row = std::strtol(field + 2, &end, 10);
  const bool accept = end != nullptr && std::strcmp(end, ":accept") == 0;
  if (row < kFirstRow || row > kLastRow) {
    REXLOG_WARN("[fable3-savemenu] '{}' is no row of the save menu; dropped", action);
    return false;
  }
  // A hover that restates the row a click is being stepped to keeps the click's accept.
  g_accept = accept || (g_target == row && g_accept);
  g_target = row;
  g_player = player;
  g_ticks = kTicks;
  return true;
}

}  // namespace refii::fable3
