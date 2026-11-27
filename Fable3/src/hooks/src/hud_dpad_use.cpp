// refii - the HUD d-pad's directions from Godot (`dpad:<p>:<code>`), handed to the d-pad widget's own
// input on the guest's thread; the guest's own pad no longer reaches that input.
//
// THE GUEST'S INPUT (TU3 / disc; the two bodies are the same instruction for instruction). The
// widgets are *(H+0x30) and *(H+0x34) (hud_levers.h), on the dispatcher's channels 12 and 13 (the
// registration passes `addi r4,r11,0xC` with r11 the widget's "second" flag, 0x82602268). Each reaches
// sub_826025D8(D, action*, player) / sub_825D75F8 through the thunk 0x82603E78 / 0x825D8E80 (`b
// sub_826025D8` 0x82603E90 / `b sub_825D75F8` 0x825D8E98) and from nowhere else. It:
//   - leaves with 0 without its selection object C = D+0x44 (`lwz r3,0x44(r3)` 0x826025F0 / 0x825D7610),
//     for a player that is not D+0x34 (`lwz r11,0x34(r31); cmpw cr6,r5,r11; bne` 0x826025FC..0x82602604
//     / 0x825D761C), and for any code outside 29..36 (`addi r11,r4,-0x1D; cmplwi cr6,r11,7; bgt`
//     0x8260260C..0x82602614 / 0x825D7630);
//   - 29..32 (GAME_ACTION_GUI_UP / _DOWN / _LEFT / _RIGHT): sub_82B7D358(C, code, open) (`bl`
//     0x8260265C / disc sub_82B48A70 0x825D767C) with open = D+0x18 is 1 or 2, which marks the
//     direction held (C+0xC) and, when the widget is open and nothing is pending, copies that slot's
//     suggestion for the use the widget's tick makes one frame later; answers `open`;
//   - 33..36 (the _RELEASED codes): clears the direction's bit (`andc r6,r9,r7; stw r6,0xC(r3)`
//     0x826026E4..0x826026F0 / 0x825D7710), cancels a pending use (sub_82B7D228 0x82602700 / disc
//     sub_82B48940 0x825D7720) or drops the busy latch when no direction is left (`stb r30,8(r3)`
//     0x82602714 / 0x825D7734); answers `open`.
//
// THE CUT. rex_HudDpad_Input, the two channels' handler, answers 0 without running for the guest's
// pad: behind channels 12 / 13 there is only channel 14 (world icons), which no pad action reaches
// either (world_icon_hold.cpp). The lever runs the handler's body with the code Godot states for the
// player who pressed, on the first widget and, when that leaves it, on the second — the dispatcher's
// own order — so the widget's own player test picks the one. The use itself stays the widget's tick
// and the guest's (sub_82B7D130 → sub_828BD968 / disc sub_82B48848 → sub_82867338). A direction a
// showing box takes first (hud_levers.h) is not handed.

#include <atomic>
#include <cstdint>
#include <cstring>
#include <initializer_list>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "hud_levers.h"

REX_EXTERN(__imp__rex_HudDpad_Input);

namespace {

using namespace refii::fable3;

/// GAME_ACTION_GUI_UP .. GAME_ACTION_GUI_RIGHT_RELEASED.
constexpr uint32_t kFirstDirection = 29u;
constexpr uint32_t kLastDirection = 36u;

std::atomic<uint64_t> g_pad_actions{0u};

}  // namespace

namespace refii::fable3 {

bool AnswerHudDpad(PPCContext& ctx, uint8_t* base, const char* action) {
  if (base == nullptr || action == nullptr) {
    return false;
  }
  uint32_t player = 0u;
  uint32_t code = 0u;
  const char* at = LeverPlayer(action + sizeof(kHudDpadAction) - 1u, player);
  at = at != nullptr ? LeverNumber(at, code) : nullptr;
  if (at == nullptr || *at != '\0' || code < kFirstDirection || code > kLastDirection) {
    REXLOG_WARN("[fable3-dpad] '{}' is not dpad:<p>:<29..36>; dropped", action);
    return false;
  }
  if (BoxTakesAction(base, code, player)) {
    REXLOG_INFO("[fable3-dpad] action {} for player {}: a showing box has the d-pad", code, player);
    return true;
  }
  uint32_t took = 0u;
  for (const uint32_t offset : {kHudDpadOne, kHudDpadTwo}) {
    const uint32_t widget = HudObject(base, offset);
    if (widget != 0u && took == 0u) {
      took = CallWithAction(ctx, base, __imp__rex_HudDpad_Input, widget, code, player);
    }
  }
  REXLOG_INFO("[fable3-dpad] action {} for player {}: the d-pad's input answered {}", code, player, took);
  return true;
}

}  // namespace refii::fable3

/// (D, action*, player), reached only from the dispatcher's thunk: the guest's pad. Answers 0.
REX_EXTERN(rex_HudDpad_Input) {
  if (g_pad_actions.fetch_add(1u) == 0u) {
    refii::fable3::SayCutHolds("fable3-dpad", "d-pad's input", "dpad:", refii::fable3::HandedCode(base, ctx.r4.u32));
  }
  ctx.r3.u64 = 0u;
}
