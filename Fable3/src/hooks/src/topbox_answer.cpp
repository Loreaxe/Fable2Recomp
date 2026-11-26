// refii - the TopBox's A and Y from Godot (`topbox:<p>:<a|y>:<down|up>`), handed to the TopBox's own
// input on the guest's thread, so its own gates, its press-or-hold rule and its answer stay the guest's.
//
// THE GUEST'S INPUT (TU3 addresses; the disc's body is the same instruction for instruction, its
// press answer taking M as an extra first argument). M = *(H+0x4C), H = *(GUI+0x20). The dispatcher's
// channel 4 (registered at 0x824D073C) reaches sub_824D0A18(M, ev*, player) (disc sub_824B5C28) through
// the thunk at 0x824D2198 (`mr r5,r4; mr r4,r3; mr r3,r11; b sub_824D0A18`). It:
//   - takes the message up, the back of the first non-empty channel (sub_82194538 at 0x824D0A30), and
//     leaves (answer 0) when there is none or it shows neither A (+0x1C) nor Y (+0x1F);
//   - classes the action: A is 74 / 82 / 79 while A shows (0x824D0A6C..0x824D0A84), Y is 77 / 85 / 81
//     while Y shows (0x824D0AA4..0x824D0ABC); anything else leaves;
//   - takes player one's pad alone (`lwz r11,0x24(r3)` / `cmpw r11,r30` / `bne` 0x824D0B0C..0x824D0B14;
//     with no player-one record, only while channel 0 holds a message, 0x824D0B80..0x824D0B9C);
//   - turns 74 / 77 into a press (class 0xD), 82 / 85 into a hold (0xE), the rest into a release
//     (0xF) (0x824D0BA0..0x824D0BD4) — and a press or a hold into a release while the hero's movement
//     is past 1.0 unless the top game mode is 1 or 8 (0x824D0B18..0x824D0B70, 0x824D0BE4..0x824D0C1C);
//   - hands A to TopBox_PressAnswer(holdA +0x1D, &M+3, slide, 1, class) and Y to it with (+0x20, &M+4,
//     slide, 4, class) (0x824D0C4C..0x824D0C60, 0x824D0C88..0x824D0CB0; disc sub_824B7038 with M first);
//   - answers whether it took the press (`addic` / `subfe` 0x824D0CB8..0x824D0CBC), which stops the
//     dispatcher's later channels (emotion choices, interaction, world icons).
// The press answer sub_824D1B08 (disc sub_824B7038) is the only place a press does anything: class
// 0xD sets the button's byte (M+3 / M+4) to 1 and slides its prompt on, and for a button that is not
// held posts the answer, PostButtonAnswer(answer) (`bl sub_824D1BC0` 0x824D1BA0; disc sub_824B70E0,
// which posts event 64 or, with channel 0 non-empty, sends it to the session); class 0xF clears the
// byte and slides the prompt off; 0xE does nothing. A held button's answer is the TopBox update's,
// which fills while the byte is 1 (sub_82188458 → sub_824D1910 → sub_824D1BC0 at 0x824D1A0C).
//
// THE CUT. The guest's own pad still runs the input — its routing and its "taken" answer are the
// guest's, so nothing behind the TopBox fires while it shows — but the press answer does nothing
// for it (rex_TopBox_PressAnswer). Godot's press, hold and release run the same input for the
// player who pressed, with the guest's codes; while a button is down its held code goes once a tick,
// as the pad's does, so the input's moving-hero rule still turns a hold into a release. A press the
// dispatcher would have given to a surface ahead of channel 4 (PadTakenAheadOfTopBox) is not handed.

#include <cstdint>
#include <cstring>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "box_answers.h"
#include "gui_statements.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(rex_TopBox_HandleInput);
REX_EXTERN(__imp__rex_TopBox_PressAnswer);

namespace {

using namespace refii::fable3;

const refii::guest::Addr kGuiManager{0x83596F84u};
constexpr uint32_t kGuiHud = 0x20u;
constexpr uint32_t kHudTopBox = 0x4Cu;

/// By button (0 A, 1 Y): the codes the TopBox's input classes as a press, a hold and a release.
constexpr uint32_t kPressed[2] = {74u, 77u};
constexpr uint32_t kHeld[2] = {82u, 85u};
constexpr uint32_t kReleased[2] = {79u, 81u};
constexpr const char* kButtonName[2] = {"A", "Y"};

/// Set while Godot's press runs the input: the press answer acts only then.
thread_local bool t_godot_press = false;

/// A button Godot holds down, and for whom. The guest's thread alone touches it (the drain and the
/// tick both run on it).
struct Held {
  bool down = false;
  uint32_t player = 0u;
};
Held g_held[2];

uint32_t TopBoxManager(const uint8_t* base) {
  const uint32_t gui = LoadGuestU32(base, kGuiManager);
  const uint32_t hud = Plausible(gui) ? LoadGuestU32(base, gui + kGuiHud) : 0u;
  return Plausible(hud) ? LoadGuestU32(base, hud + kHudTopBox) : 0u;
}

void StoreGuestU32(uint8_t* base, uint32_t address, uint32_t value) {
  const uint32_t swapped = __builtin_bswap32(value);
  std::memcpy(base + address, &swapped, sizeof(swapped));
}

/// The TopBox's input with `action` for `player`; its answer (1 when it took the press), or 0 with no
/// TopBox, or when a surface ahead of it takes the press.
uint32_t Run(PPCContext& ctx, uint8_t* base, uint32_t action, uint32_t player) {
  const uint32_t manager = TopBoxManager(base);
  if (!Plausible(manager) || PadTakenAheadOfTopBox(base, action, player)) {
    return 0u;
  }
  PPCContext work = ctx;
  work.r1.u32 = (ctx.r1.u32 - 0x200u) & ~0xFu;
  const uint32_t event = work.r1.u32 + 0x100u;
  StoreGuestU32(base, event, action);
  StoreGuestU32(base, event + 4u, 0u);
  StoreGuestU32(base, event + 8u, 0u);
  StoreGuestU32(base, event + 12u, 0u);
  work.r3.u64 = manager;
  work.r4.u64 = event;
  work.r5.u64 = player;
  t_godot_press = true;
  rex_TopBox_HandleInput(work, base);
  t_godot_press = false;
  return work.r3.u32;
}

}  // namespace

namespace refii::fable3 {

bool AnswerTopBox(PPCContext& ctx, uint8_t* base, const char* action) {
  if (base == nullptr || action == nullptr || std::strncmp(action, kTopBoxAction, sizeof(kTopBoxAction) - 1u) != 0) {
    return false;
  }
  const char* at = action + sizeof(kTopBoxAction) - 1u;
  if ((at[0] != '1' && at[0] != '2') || at[1] != ':' || (at[2] != 'a' && at[2] != 'y') || at[3] != ':') {
    REXLOG_WARN("[fable3-topbox] '{}' is not topbox:<p>:<a|y>:<down|up>; dropped", action);
    return false;
  }
  const uint32_t player = static_cast<uint32_t>(at[0] - '0');
  const int button = at[2] == 'a' ? 0 : 1;
  const bool down = std::strcmp(at + 4, "down") == 0;
  if (!down && std::strcmp(at + 4, "up") != 0) {
    REXLOG_WARN("[fable3-topbox] '{}' is not topbox:<p>:<a|y>:<down|up>; dropped", action);
    return false;
  }
  if (down) {
    g_held[button] = Held{true, player};
    const uint32_t took = Run(ctx, base, kPressed[button], player);
    REXLOG_INFO("[fable3-topbox] {} down for player {}: the TopBox's input {}", kButtonName[button], player,
                took != 0u ? "took it" : "left it");
    return true;
  }
  const Held was = g_held[button];
  g_held[button] = Held{};
  if (!was.down) {
    return true;
  }
  const uint32_t took = Run(ctx, base, kReleased[button], was.player);
  REXLOG_INFO("[fable3-topbox] {} up for player {}: the TopBox's input {}", kButtonName[button], was.player,
              took != 0u ? "took it" : "left it");
  return true;
}

void PumpTopBox(PPCContext& ctx, uint8_t* base) {
  if (base == nullptr) {
    return;
  }
  for (int button = 0; button < 2; ++button) {
    if (g_held[button].down) {
      Run(ctx, base, kHeld[button], g_held[button].player);
    }
  }
}

}  // namespace refii::fable3

/// TopBox_PressAnswer (TU3 sub_824D1B08 / disc sub_824B7038, the input's only callee that acts):
/// Godot's press only; the guest's pad reaches it through the same input and does nothing here.
REX_EXTERN(rex_TopBox_PressAnswer) {
  if (!t_godot_press) {
    return;
  }
  __imp__rex_TopBox_PressAnswer(ctx, base);
}
