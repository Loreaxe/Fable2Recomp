// refii - an emotion choice's button from Godot (`emotion:<p>:<code>`), handed to the choice UI's own
// input on the guest's thread; the guest's own pad no longer reaches that input.
//
// THE GUEST'S INPUT (TU3 / disc). E = *(H+0x1C) (hud_levers.h). The dispatcher's channel 5 reaches
// sub_826103E8(E, action*, player) / sub_825E91E0 through the thunk 0x82613478 / 0x825EBFF8 (`b
// sub_826103E8` 0x82613490 / `b sub_825E91E0` 0x825EC010) and from nowhere else (the one code
// reference on each build). It:
//   - takes the player's hero controller and hero (`mr r4,r5; lwz r3,0x40(r11); bl sub_821FF0A8`
//     0x82610404..0x82610420, `bl sub_82A1D6E8` 0x8261042C; disc 0x825E91F8..0x825E920C, 0x825E9218)
//     and leaves with 0 when there is none;
//   - leaves while the world frame is not past E+0x24 (`lwz r10,0x24(r22); cmpw; bge` 0x82610448..
//     0x82610450), while the fader fades or is faded (sub_82183498, sub_82600558 of *(GUI+0x3C),
//     0x82610464 / 0x82610478) and while the box manager shows a box (sub_82275830, 0x82610490);
//   - finds the record whose action (rec+0) is the action's code (`lwz r8,0(r18); lwz r11,0(r10);
//     cmpw; bne` 0x826104C0..0x826104D0) and whose owner uid (rec+8, 64 bits) is null or the hero's;
//   - commits a plain choice (`bl sub_82612D70` 0x82610D28 / disc `bl sub_825EB880` 0x825E9830:
//     message 187 with the record's kind, or the record's Name as a named message with the hero);
//     starts a holdable one (the record's action becomes its release code through sub_82612948,
//     rec+0x3E = 1, the `EmotionUIHoldingImmobilise` script rules, the ring, `Name_PRESSED`
//     0x82610F84 / 0x825E9A94); ends a held one on its release code (`Name_SUCCESS` 0x82610A10 /
//     0x825E9514, `Name_FAILURE` 0x82610B70 / 0x825E9678, or the tick's finish for a non-manual one).
// The codes are the game's own EGameAction (miscellaneous/gameactionenum.lua):
// GAME_ACTION_EMOTION_UI_CHOICE_1..5_PRESSED = 103..107, _RELEASED = 108..112, _HELD = 113..117.
//
// THE CUT. rex_EmotionChoices_Input, the channel's handler, answers 0 without running for the guest's
// pad: nothing of a choice happens on a pad action, and the channels behind it see the action as they
// mostly did (the handler answers 1 only on the two-phase swap and a manual hold's failure). The
// lever runs the handler's body for the player who pressed, with the code Godot states: the pressed
// code on the button going down, the released code on its going up. Every gate above, the ownership
// test and every consequence are the guest's. The ring's clock stays the guest's
// (rex_EmotionIcon_RingTick): it decides the expression's outcome.

#include <atomic>
#include <cstdint>
#include <cstring>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "hud_levers.h"

REX_EXTERN(__imp__rex_EmotionChoices_Input);

namespace {

using namespace refii::fable3;

/// GAME_ACTION_EMOTION_UI_CHOICE_1_PRESSED .. GAME_ACTION_EMOTION_UI_CHOICE_5_HELD.
constexpr uint32_t kFirstChoiceAction = 103u;
constexpr uint32_t kLastChoiceAction = 117u;

std::atomic<uint64_t> g_pad_actions{0u};

}  // namespace

namespace refii::fable3 {

bool AnswerEmotionChoice(PPCContext& ctx, uint8_t* base, const char* action) {
  if (base == nullptr || action == nullptr) {
    return false;
  }
  uint32_t player = 0u;
  uint32_t code = 0u;
  const char* at = LeverPlayer(action + sizeof(kEmotionChoiceAction) - 1u, player);
  at = at != nullptr ? LeverNumber(at, code) : nullptr;
  if (at == nullptr || *at != '\0' || code < kFirstChoiceAction || code > kLastChoiceAction) {
    REXLOG_WARN("[fable3-emotion] '{}' is not emotion:<p>:<103..117>; dropped", action);
    return false;
  }
  const uint32_t choices = HudObject(base, kHudEmotionChoices);
  if (choices == 0u) {
    REXLOG_WARN("[fable3-emotion] '{}' with no HUD; dropped", action);
    return false;
  }
  const uint32_t took = CallWithAction(ctx, base, __imp__rex_EmotionChoices_Input, choices, code, player);
  REXLOG_INFO("[fable3-emotion] action {} for player {}: the choices' input answered {}", code, player, took);
  return true;
}

}  // namespace refii::fable3

/// (E, action*, player), reached only from the dispatcher's thunk: the guest's pad. Answers 0.
REX_EXTERN(rex_EmotionChoices_Input) {
  if (g_pad_actions.fetch_add(1u) == 0u) {
    refii::fable3::SayCutHolds("fable3-emotion", "choices' input", "emotion:", refii::fable3::HandedCode(base, ctx.r4.u32));
  }
  ctx.r3.u64 = 0u;
}
