// refii - the interaction mode's pad from Godot (`interaction:<p>:<code>[:<value>]`), handed to the
// interaction manager's own input on the guest's thread; while the mode up is one whose pad Godot
// owns (0 NPC, 13 dog) the guest's own pad no longer reaches that input.
//
// THE GUEST'S INPUT (TU3 / disc; the two bodies are the same instruction for instruction). The
// dispatcher's channel 8 reaches sub_825F0860(IM, action*, player) / sub_825C58A0 through the thunk
// sub_825F1F78 / 0x825C7000 (`b sub_825F0860` 0x825F1F90 / `b sub_825C58A0` 0x825C7018) and from
// nowhere else. It:
//   - leaves with 0 unless IM+4 (open) and IM+0 (its presentation) (`lbz r11,4(r3)` 0x825F086C, `lwz
//     r11,0(r3)` 0x825F0884; disc 0x825C58AC, 0x825C58C4), while IM+7 (a screen over the mode, `lbz
//     r11,7(r30)` 0x825F08A4), while sub_821A38C8 of *(GUI+0x1C) holds (0x825F08BC; disc
//     sub_821A3D20) and while the byte at **(*(GUI+0x1C)+0x38) is set (0x825F08D4..0x825F08E0);
//   - hands the action to the mode object's slot 5 (`lwz r3,0x18(r30)` 0x825F08E4, behind its +0x18
//     active byte, `lwz r10,0x14(r11); bctrl` 0x825F0908..0x825F0910), which applies the mode's own
//     player gate (sub_829704A0 / disc sub_82961848) and runs the mode's own input (slot 7: the NPC's B
//     75, follow 86, weapons pane 91; the dog's B 75); a non-zero answer ends here with 1;
//   - then the script mini-game bar (IM+0x1C, behind its +8, `bl sub_82716558` 0x825F094C / disc
//     sub_826ECC00);
//   - then the mini-game controller's release watch: with IM+0x28, its +0xC set, +0x12 clear, +8 the
//     player and +4 the action's code, `stb r10,0xE(r11)` = 0 (0x825F095C..0x825F09AC);
//   - then the bumper bar's and the trigger bar's slot 4 (IM+0x20, IM+0x24, 0x825F09B0..0x825F0A00).
// The codes are the game's own EGameAction (miscellaneous/gameactionenum.lua): GUI_A / B / X / Y = 74..77,
// their _RELEASED 79 / 80 / 78 / 81 and _HELD 82 / 83 / 84 / 85, the triggers and bumpers 86..89, held
// 90..93, released 94..97, and the choices' released codes 108..112 (the mini-game controller watches
// for the one it was started with).
//
// WHICH MODE. The mode set sub_825EF1D8(IM, mode, hero, target, table, a6) / disc sub_825C4348 runs the
// clear (`bl sub_825EED90` 0x825EF344 / `bl sub_825C3F00` 0x825C44B4) past its refusals and then
// stores its second argument as the mode (`mr r29,r4` 0x825EF1EC, `stw r29,0x14(r28)` 0x825EF41C /
// 0x825C458C): a clear inside the set is the stated mode opening. The four dedicated openers (real
// estate, shop, pawn shop, orb) open modes of their own. The close sub_825F0CC8 / sub_825C5D08 ends it.
// Stated at those entries: `@Interaction.Input(owned, mode)` — whether the mode up is one whose pad is
// Godot's, and the mode (-1 for none or a dedicated opener's).
//
// THE CUT. rex_InteractionMode_Input, the channel's handler, answers 0 without running for the
// guest's pad while the stated mode is 0 or 13; for every other mode it runs as the guest has it
// (their pad is theirs until their own batches). Behind channel 8 a pad action then reaches only the
// orb status selector (channel 9), the d-pad and the world icons, the last two of which answer no
// pad action (hud_dpad_use.cpp, world_icon_hold.cpp). The lever runs the handler's body for the
// player who pressed: the gates above and each mode's own are the guest's. An action a showing box
// takes first (hud_levers.h) is not handed.

#include <atomic>
#include <cstdint>
#include <cstring>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "hud_levers.h"

REX_EXTERN(__imp__rex_InteractionMode_Input);

namespace {

using namespace refii::fable3;

/// The modes whose pad is Godot's: 0 (CInteractionModeMenuNPC), 13 (CInteractionModeMenuDog).
constexpr int32_t kModeNpc = 0;
constexpr int32_t kModeDog = 13;
/// The last code the lever takes (GAME_ACTION_RANGED_WEAPON_RANGED_AIM_HELD, the table's last).
constexpr uint32_t kLastAction = 126u;

/// The IM as the last opener or close was handed it, the mode up (-1 none or not stated) and whether
/// Godot owns its pad.
std::atomic<uint32_t> g_im{0u};
std::atomic<int32_t> g_mode{-1};
std::atomic<bool> g_owned{false};
std::atomic<uint64_t> g_pad_withheld{0u};

void State(uint32_t im, int32_t mode) {
  const bool owned = mode == kModeNpc || mode == kModeDog;
  g_im.store(im);
  const int32_t was_mode = g_mode.exchange(mode);
  const bool was_owned = g_owned.exchange(owned);
  if (was_mode == mode && was_owned == owned) {
    return;
  }
  Statement("@Interaction.Input").Add(Boolean(owned)).Add(Number(static_cast<float>(mode))).Post();
}

}  // namespace

namespace refii::fable3 {

void NoteInteractionModeSet(uint32_t im, int32_t mode) { State(im, mode); }
void NoteInteractionModeOpenedByOpener(uint32_t im) { State(im, -1); }
void NoteInteractionModeClosed(uint32_t im) { State(im, -1); }

bool AnswerInteractionInput(PPCContext& ctx, uint8_t* base, const char* action) {
  if (base == nullptr || action == nullptr) {
    return false;
  }
  uint32_t player = 0u;
  uint32_t code = 0u;
  const char* at = LeverPlayer(action + sizeof(kInteractionAction) - 1u, player);
  at = at != nullptr ? LeverNumber(at, code) : nullptr;
  float value = 0.0f;
  if (at != nullptr && *at != '\0') {
    char* end = nullptr;
    value = std::strtof(at, &end);
    if (end == at || *end != '\0') {
      at = nullptr;
    }
  }
  if (at == nullptr || code == 0u || code > kLastAction) {
    REXLOG_WARN("[fable3-interaction] '{}' is not interaction:<p>:<code>[:<value>]; dropped", action);
    return false;
  }
  const uint32_t im = g_im.load();
  if (!Plausible(im)) {
    REXLOG_WARN("[fable3-interaction] '{}' before any interaction mode opened; dropped", action);
    return false;
  }
  if (BoxTakesAction(base, code, player)) {
    REXLOG_INFO("[fable3-interaction] action {} for player {}: a showing box has the pad", code, player);
    return true;
  }
  const uint32_t took = CallWithAction(ctx, base, __imp__rex_InteractionMode_Input, im, code, player, value);
  // The held codes repeat every tick while a button is down; the log carries the rest.
  if (code < 82u || (code > 85u && code < 90u) || code > 93u) {
    REXLOG_INFO("[fable3-interaction] action {} for player {} in mode {}: the mode's input answered {}", code, player,
                g_mode.load(), took);
  }
  return true;
}

}  // namespace refii::fable3

/// (IM, action*, player), reached only from the dispatcher's thunk: the guest's pad. Answers 0 while
/// the mode up is Godot's to drive; runs as the guest has it for any other.
REX_EXTERN(rex_InteractionMode_Input) {
  if (g_owned.load()) {
    if (g_pad_withheld.fetch_add(1u) == 0u) {
      REXLOG_INFO("[fable3-interaction] the guest's pad reached mode {}'s input: left unanswered (Godot's lever runs it)",
                  g_mode.load());
    }
    ctx.r3.u64 = 0u;
    return;
  }
  __imp__rex_InteractionMode_Input(ctx, base);
}
