// refii - the TopBox's A and Y from Godot (`topbox:<p>:<a|y>:<down|up>`), handed to the TopBox's own
// input on the guest's thread, so its own gates, its press-or-hold rule and its answer stay the guest's;
// and a held button's fill on Godot's clock (`topbox:<p>:<a|y>:fill:<0..1000>`).
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
// byte and slides the prompt off; 0xE does nothing. A held button's answer is the TopBox update's.
//
// THE HOLD. The TopBox tick's hold tick sub_82188458(M, f1 dt) (disc sub_82188538) runs the hold step
// for A while the message shows A and holds it (`lbz r11,0x1C(r29)` 0x82188524, `lbz r11,0x1D(r29)`
// 0x82188534): TU3 sub_824D1910(f1 dt, r4 held = M+3, r5 &progress = M+8, r6 meter name, r7 answer 1)
// at 0x8218855C; and for Y (+0x1F, +0x20) with (M+4, M+0xC, name, 4) at 0x82188624. The disc's step
// sub_824B6E50 takes M first: (r3 M, f1 dt, r5 held, r6 &progress, r7 name, r8 answer), `li r8,1`
// 0x82188628 / `lbz r5,3(r30)` 0x8218862C / `mr r6,r31` 0x82188638 / `bl` 0x82188640, and `li r8,4`
// 0x821886F8 / `lbz r5,4(r30)` 0x821886FC / `addi r6,r30,0xC` 0x82188700 / `bl` 0x8218870C. The step:
//   `lfs f13,0(r5)` 0x824D192C (disc `lfs f13,0(r6)` 0x824B6E64): the old progress;
//   held: `lfs f0,flt_835970A4; fmadds f0,f0,f1,f13` 0x824D1944..0x824D1948 (disc flt_835565AC,
//   0x824B6E80..0x824B6E84), else `lfs f0,flt_835970A8; fnmsubs f0,f0,f1,f13` 0x824D1954..0x824D1958
//   (disc flt_835565B0, 0x824B6E90..0x824B6E94); two `fsel` clamps to 0..1 and `stfs f0,0(r31)`
//   0x824D19C0 (disc 0x824B6EFC);
//   unchanged (`fcmpu cr6,f0,f13; beq` 0x824D19C4..0x824D19C8, disc 0x824B6F00..0x824B6F04): done;
//   the button's meter gets its target, sub_82975CF8(meters, name, f1 = progress × 100.0 (`fmuls
//   f1,f0,f13` with flt_8206D228 = 100.0, 0x824D19D8), f2 = flt_820E99E0 = 10.0) with meters =
//   `*(*(*(*(GUI+0x20)+0x24)+0x18)+4)` (`lwz r8,0x20(r11); lwz r7,0x24(r8); lwz r6,0x18(r7); lwz
//   r3,4(r6)` 0x824D19E8..0x824D19F4), `bl` 0x824D19F8; disc the same loads 0x824B6F24..0x824B6F30,
//   flt_8206D268 = 100.0, flt_820E7004 = 10.0, `bl sub_8291F988` 0x824B6F34;
//   at 1.0 (`fcmpu cr6,f0,f31; blt` 0x824D1A00..0x824D1A04, disc 0x824B6F3C..0x824B6F40) the answer:
//   `mr r3,r30; bl sub_824D1BC0` 0x824D1A08..0x824D1A0C, disc `mr r4,r29; mr r3,r30; bl sub_824B70E0`
//   0x824B6F44..0x824B6F4C (which posts event 64 or, with channel 0 non-empty, leaves the session).
// The two rates are GUIValues.HeldButtonUpdateSpeed and .ReleasedButtonUpdateSpeed, read by the TopBox
// load sub_824D0838 (disc sub_824B5A48) through sub_821CE868 and stored `stfs f1,flt_835970A4` 0x824D08A4
// and `stfs f1,flt_835970A8` 0x824D08CC (disc 0x824B5AB4, 0x824B5ADC): stated on those stores. After
// the step the hold tick writes the dialogue volume from the progress it finds (0x82188560..0x821885CC).
// THE RATES ARE PER MILLISECOND. The step's dt is the GUI clock's, in whole milliseconds: the clock
// TU3 sub_82193B08 / disc sub_82193908 answers `(int)((seconds() - *(double*)(clock+0x90)) * 1000.0)`
// (`lfd f0,dbl_82073EA0` = 1000.0, `fmul f12,f13,f0`, `fctiwz` 0x82193B64..0x82193B6C; disc
// dbl_82073D80 = 1000.0 at 0x82193964..0x8219396C; seconds() = sub_822099C0 / disc sub_8220A380, the
// counter over its frequency); the GUI's tick takes it as an integer (`bl` 0x82254F90, `fcfid f13,f0;
// frsp f1,f13; bl sub_82258E60` 0x82254FB8..0x82254FC0; disc 0x82255D60, 0x82255D88..0x82255D90), the
// GUI update makes dt = now - last (`fsubs f31,f30,f0` 0x82258ED0 / disc 0x82259820) and hands it
// down (`fmr f1,f31; bl sub_82272EF0` 0x8225910C..0x82259110 / disc `bl sub_82274950` 0x82259A5C..
// 0x82259A60; `fmr f1,f31; bl sub_82188458` 0x82272FF4..0x82272FF8 / disc `bl sub_82188538`
// 0x82274A54..0x82274A58) to the hold tick (`fmr f29,f1` 0x82188474 / disc 0x82188554). So
// `progress += rate × dt` fills `rate` per millisecond; both rates are stated as the guest holds them.
//
// THE HOLD'S CUT. The step is replaced (rex_TopBox_HoldStep): the progress is the fill Godot states
// for that button, the answer is the guest's own poster on the step the fill is full while the guest
// holds the button. The meter's target is still set with the step's own call when the progress
// changed: the meter registry is the feed Godot's HUD document draws the prompt's fill from
// (hud_hooks.cpp rex_HudMeter_Apply), until the registries themselves are cut. The guest still says whether the button is held (its
// input sets and clears M+3 / M+4, and turns a hold into a release while the hero moves): the step's
// held argument is stated on its edges with the two rates, `@TopBox.Held(button, held, heldRate,
// releasedRate)`, and Godot fills only while it is set. A fill is taken only from the player whose
// press the input holds; while the guest does not hold the button the progress only falls.
//
// THE CUT. The guest's own pad still runs the input — its routing and its "taken" answer are the
// guest's, so nothing behind the TopBox fires while it shows — but the press answer does nothing
// for it (rex_TopBox_PressAnswer). Godot's press, hold and release run the same input for the
// player who pressed, with the guest's codes; while a button is down its held code goes once a tick,
// as the pad's does, so the input's moving-hero rule still turns a hold into a release. A press the
// dispatcher would have given to a surface ahead of channel 4 (PadTakenAheadOfTopBox) is not handed.

#include <cstdint>
#include <cstdlib>
#include <cstring>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "box_answers.h"
#include "gui_statements.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(rex_TopBox_HandleInput);
REX_EXTERN(__imp__rex_TopBox_PressAnswer);
REX_EXTERN(rex_Box_PostButtonAnswer);
REX_EXTERN(rex_TopBox_Answer);
REX_EXTERN(rex_HudMeter_SetTarget);

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

/// By button: Godot's fill (0..1), and the held argument the hold step last stated. The guest's
/// thread alone touches them.
float g_fill[2] = {0.0f, 0.0f};
/// By button: the player whose press was last handed to the input, 0 before any.
uint32_t g_fill_from[2] = {0u, 0u};
bool g_guest_holds[2] = {false, false};
/// GUIValues.HeldButtonUpdateSpeed / .ReleasedButtonUpdateSpeed as the TopBox load read them.
float g_held_rate = 0.0f;
float g_released_rate = 0.0f;
bool g_hold_step_logged = false;

/// The answers the hold step is called with (`li r7,1` 0x82188548, `li r7,4` 0x82188614).
constexpr uint32_t kAnswerY = 4u;
/// The meter registry the step hands sub_82975CF8: `*(*(*(H+0x24)+0x18)+4)`.
constexpr uint32_t kHudRegistries = 0x24u;
constexpr uint32_t kRegistriesVector = 0x18u;
constexpr uint32_t kMeterRegistry = 4u;
/// The step's own arguments to it: the progress as a percentage, over flt_820E99E0 = 10.0.
constexpr double kMeterScale = 100.0;
constexpr double kMeterTime = 10.0;

uint32_t MeterRegistry(const uint8_t* base) {
  const uint32_t gui = LoadGuestU32(base, kGuiManager);
  const uint32_t hud = Plausible(gui) ? LoadGuestU32(base, gui + kGuiHud) : 0u;
  const uint32_t holder = Plausible(hud) ? LoadGuestU32(base, hud + kHudRegistries) : 0u;
  const uint32_t vector = Plausible(holder) ? LoadGuestU32(base, holder + kRegistriesVector) : 0u;
  const uint32_t meters = Plausible(vector) ? LoadGuestU32(base, vector + kMeterRegistry) : 0u;
  return Plausible(meters) ? meters : 0u;
}

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
  if (std::strncmp(at + 4, "fill:", 5u) == 0) {
    char* end = nullptr;
    const long fill = std::strtol(at + 9, &end, 10);
    if (end == at + 9 || *end != '\0' || fill < 0 || fill > 1000) {
      REXLOG_WARN("[fable3-topbox] '{}' is not topbox:<p>:<a|y>:fill:<0..1000>; dropped", action);
      return false;
    }
    // The fill of the last press handed to the input, from the player who made it; after the
    // release it is that player's drain (the step lets the progress only fall then).
    if (g_fill_from[button] == player) {
      g_fill[button] = static_cast<float>(fill) / 1000.0f;
    }
    return true;
  }
  const bool down = std::strcmp(at + 4, "down") == 0;
  if (!down && std::strcmp(at + 4, "up") != 0) {
    REXLOG_WARN("[fable3-topbox] '{}' is not topbox:<p>:<a|y>:<down|up|fill:n>; dropped", action);
    return false;
  }
  if (down) {
    g_held[button] = Held{true, player};
    g_fill_from[button] = player;
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

/// The hold step (TU3 sub_824D1910 / disc sub_824B6E50), replaced: the progress is Godot's fill for the
/// button, never rising while the guest does not hold it; the meter's target is set with the step's
/// own call when the progress changed; the answer is the guest's poster on the step the fill is full.
REX_EXTERN(rex_TopBox_HoldStep) {
  const bool disc = refii::guest::Current() == refii::guest::Build::kDisc;
  const uint32_t manager = disc ? ctx.r3.u32 : 0u;
  const bool held = ((disc ? ctx.r5.u32 : ctx.r4.u32) & 0xFFu) != 0u;
  const uint32_t progress = disc ? ctx.r6.u32 : ctx.r5.u32;
  const uint32_t meter_name = disc ? ctx.r7.u32 : ctx.r6.u32;
  const uint32_t answer = disc ? ctx.r8.u32 : ctx.r7.u32;
  if (base == nullptr || !Plausible(progress)) {
    return;
  }
  if (!g_hold_step_logged) {
    g_hold_step_logged = true;
    REXLOG_INFO("[fable3-topbox] the hold step runs on Godot's fill (rates {} held, {} released, per ms)", g_held_rate,
                g_released_rate);
  }
  const int button = answer == kAnswerY ? 1 : 0;
  const float old = LoadGuestF32(base, progress);
  if (held != g_guest_holds[button]) {
    g_guest_holds[button] = held;
    if (held) {
      g_fill[button] = old;
    }
    Statement("@TopBox.Held")
        .Add(Text(button == 0 ? "a" : "y"))
        .Add(Boolean(held))
        .Add(Number(g_held_rate))
        .Add(Number(g_released_rate))
        .Post();
  }
  float fill = g_fill[button];
  fill = fill < 0.0f ? 0.0f : fill > 1.0f ? 1.0f : fill;
  if (!held && fill > old) {
    fill = old;
  }
  uint32_t bits = 0u;
  std::memcpy(&bits, &fill, sizeof(bits));
  StoreGuestU32(base, progress, bits);
  if (fill == old) {
    return;
  }
  PPCContext work = ctx;
  work.r1.u32 = (ctx.r1.u32 - 0x200u) & ~0xFu;
  if (const uint32_t meters = MeterRegistry(base); meters != 0u) {
    work.r3.u64 = meters;
    work.r4.u64 = meter_name;
    work.f1.f64 = static_cast<double>(fill) * kMeterScale;
    work.f2.f64 = kMeterTime;
    rex_HudMeter_SetTarget(work, base);
  }
  if (!held || fill < 1.0f || old >= 1.0f) {
    return;
  }
  REXLOG_INFO("[fable3-topbox] {} held full: answer {}", kButtonName[button], answer);
  if (disc) {
    work.r3.u64 = manager;
    work.r4.u64 = answer;
    rex_TopBox_Answer(work, base);
    return;
  }
  Statement("@TopBox.Answer").Add(Number(static_cast<float>(static_cast<int32_t>(answer)))).Post();
  work.r3.u64 = answer;
  rex_Box_PostButtonAnswer(work, base);
}

/// The TopBox load's stores of the two hold rates (`stfs f1,…`, TU3 0x824D08A4 / 0x824D08CC, disc
/// 0x824B5AB4 / 0x824B5ADC): f1 is the float getter's answer (sub_821CE868 / disc sub_821CE760) for
/// GUIValues' field.
void RefiiF3TopBoxHeldRate(PPCRegister& rate) {
  g_held_rate = static_cast<float>(rate.f64);
  REXLOG_INFO("[fable3-topbox] GUIValues.HeldButtonUpdateSpeed = {} per millisecond", g_held_rate);
}
void RefiiF3TopBoxReleasedRate(PPCRegister& rate) {
  g_released_rate = static_cast<float>(rate.f64);
  REXLOG_INFO("[fable3-topbox] GUIValues.ReleasedButtonUpdateSpeed = {} per millisecond", g_released_rate);
}
