// refii - the world icon's hold button from Godot (`worldicon:<p>:hold:<down|up>`), handed to the icon
// registry's own input on the guest's thread; the guest's own pad no longer reaches that input.
//
// THE GUEST'S INPUT (TU3 / disc; the two bodies are the same instruction for instruction). R =
// *(H+0x18) (hud_levers.h). The dispatcher's channel 14, the last, reaches sub_82614968(R, action*,
// player) / sub_825ED600 through the thunk 0x826170B0 / 0x825EFD80 (`b sub_82614968` 0x826170C8 / `b
// sub_825ED600` 0x825EFD98) and from nowhere else. It:
//   - leaves with 0 unless R+0x37 (world icons may draw) while the cutscene bit is clear (`extrwi
//     r6,r7,1,4` 0x826149A0, `lbz r10,0x37(r3)` 0x826149AC; disc 0x825ED638, 0x825ED644), unless
//     byte_832E2801 (disc byte_832A1D61, 0x826149D0 / 0x825ED668), and for any code but 74, 82 and 79
//     (`cmpwi r10,0x4A / 0x52 / 0x4F` 0x826149E0..0x826149F0, disc 0x825ED678..0x825ED688);
//   - takes player one's controller (`li r4,1; bl sub_821FF0A8` 0x826149F8..0x82614A04, disc `bl
//     sub_821FD790` 0x825ED69C) and whether the action is that player's (`lwz r11,0x24(r3); subf
//     r10,r11,r5; cntlzw; extrwi r31` 0x82614A14..0x82614A20, disc 0x825ED6AC..0x825ED6B8);
//   - leaves with 0 while that player's hero moves faster than 1.0 squared (`fcmpu cr6,f13,f0; bgt`
//     0x82614A84..0x82614A88, disc 0x825ED71C..0x825ED720);
//   - for any other player answers 1 and does nothing (`cmplwi cr6,r31,0; beq` → `li r3,1`
//     0x82614A8C..0x82614AE0, disc 0x825ED724..0x825ED778);
//   - 74 or 82: R+0x35 = 1 (`stb r11,0x35(r28)` 0x82614ABC, disc 0x825ED754) and the fill prompt's
//     toggle on (sub_829759E8 0x82614ADC, disc sub_82921200 0x825ED774); 79: with R+0x35 set the
//     release cue (sub_823333B0 / sub_82E02BE8 0x82614B0C / 0x82614B14, disc sub_8232C4A0 /
//     sub_82DCC978 0x825ED7A4 / 0x825ED7AC), R+0x35 = 0 (`stb r10,0x35(r28)` 0x82614B24, disc
//     0x825ED7BC) and the toggle off; answers 1.
// R+0x35 is the one thing the registry's tick sub_82613890 / sub_825EC530 reads of it: while it is set
// the tick fills the icon's meter, posts message 116 at full and runs the target's
// CECOnActionUse_Use; with no target the tick clears it, and the pad's held code sets it again.
//
// THE CUT. rex_WorldIcons_Input, the channel's handler, answers 0 without running for the guest's
// pad (channel 14 is the dispatcher's last: nothing reads its answer). The lever runs the handler's
// body with 74 on the button going down and 79 on its going up, and 82 once a guest tick while it is
// down, for the player who pressed: every gate above, the fill, the use and the release cue stay the
// guest's. A press a showing box takes first (hud_levers.h) is not handed.
//
// CUES STILL GUEST-PLAYED: the release cue above (TU3 0x82614B0C, disc 0x825ED7A4).

#include <atomic>
#include <cstdint>
#include <cstring>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "hud_levers.h"

REX_EXTERN(__imp__rex_WorldIcons_Input);

namespace {

using namespace refii::fable3;

/// GAME_ACTION_GUI_A, GAME_ACTION_GUI_A_HELD, GAME_ACTION_GUI_A_RELEASED.
constexpr uint32_t kPressed = 74u;
constexpr uint32_t kHeld = 82u;
constexpr uint32_t kReleased = 79u;

/// The hold Godot has down, and for whom. The guest's thread alone touches it.
bool g_down = false;
uint32_t g_player = 0u;

std::atomic<uint64_t> g_pad_actions{0u};

/// A is pressed all through play: the lever's first runs are logged, the rest counted.
constexpr uint32_t kLoggedRuns = 16u;
uint32_t g_runs = 0u;

uint32_t Run(PPCContext& ctx, uint8_t* base, uint32_t action, uint32_t player) {
  const uint32_t registry = HudObject(base, kHudIconRegistry);
  if (registry == 0u || BoxTakesAction(base, action, player)) {
    return 0u;
  }
  return CallWithAction(ctx, base, __imp__rex_WorldIcons_Input, registry, action, player);
}

}  // namespace

namespace refii::fable3 {

bool AnswerWorldIconHold(PPCContext& ctx, uint8_t* base, const char* action) {
  if (base == nullptr || action == nullptr) {
    return false;
  }
  uint32_t player = 0u;
  const char* at = LeverPlayer(action + sizeof(kWorldIconAction) - 1u, player);
  const bool down = at != nullptr && std::strcmp(at, "hold:down") == 0;
  if (at == nullptr || (!down && std::strcmp(at, "hold:up") != 0)) {
    REXLOG_WARN("[fable3-worldicon] '{}' is not worldicon:<p>:hold:<down|up>; dropped", action);
    return false;
  }
  if (down) {
    g_down = true;
    g_player = player;
    const uint32_t took = Run(ctx, base, kPressed, player);
    if (++g_runs <= kLoggedRuns) {
      REXLOG_INFO("[fable3-worldicon] hold down for player {}: the icons' input answered {} (run {})", player, took,
                  g_runs);
    }
    return true;
  }
  if (!g_down) {
    return true;
  }
  g_down = false;
  const uint32_t took = Run(ctx, base, kReleased, g_player);
  if (g_runs <= kLoggedRuns) {
    REXLOG_INFO("[fable3-worldicon] hold up for player {}: the icons' input answered {}", g_player, took);
  }
  return true;
}

void PumpWorldIconHold(PPCContext& ctx, uint8_t* base) {
  if (base != nullptr && g_down) {
    Run(ctx, base, kHeld, g_player);
  }
}

}  // namespace refii::fable3

/// (R, action*, player), reached only from the dispatcher's thunk: the guest's pad. Answers 0.
REX_EXTERN(rex_WorldIcons_Input) {
  if (g_pad_actions.fetch_add(1u) == 0u) {
    refii::fable3::SayCutHolds("fable3-worldicon", "icons' input", "worldicon:", refii::fable3::HandedCode(base, ctx.r4.u32));
  }
  ctx.r3.u64 = 0u;
}
