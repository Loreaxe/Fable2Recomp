// refii - Fable 3's guest tick pump: the host's posted work, run on the guest's own tick thread,
// and the HF step it rides, timed.
//
// THE TICK. The main loop sub_821CB7B0 (the FE component's Update sub_82534AA0 and the
// MainGameComponent's both run it) is a fixed-step accumulator on the guest's mftb clock
// (sub_822099A0): HF rate dbl_832DF4D8 = 30.0, game-tick rate dbl_832DF4D0 = 15.0, so every second
// HF step is a GameTick (steps per tick = 2, disc 0x821CB848..5C; the GameTick/HFOnlyTick split at
// disc 0x821CBB54). Owed <= 0 -> NtYieldExecution (sub_83130CC0 at 0x821CC1B8) and re-read; owed > 1
// -> one step and re-base to now, backlog discarded (disc 0x821CBB1C..30). Under the host's driven
// time base that is one step per granted step, then yield: the contract Fable 2's loop meets at
// 0x822754CC, met here by the loop itself. Nothing in the loop reads the pad: its only exits are
// app slot 4 (app+0x2C, disc 0x8217B7A8) and the component's quit bytes +180/+181/+187.
//
// THE SITE. Once per HF step, at disc 0x821CBD74, the loop calls slot 5 (+0x14) of the app object
// *dword_835A76A8 (vtable 0x82002548; a second copy at 0x820ECB80): sub_82188640, an eight-
// instruction thunk to slot 2 of the input system at app+0x24 (lwz r3,0x24(r3); beqlr; lwz
// r11,0(r3); lwz r10,8(r11); mtctr; bctr). Those two vtable words are the pointer's only homes
// in the image, so this runs exactly once per HF step, in every state the loop runs, and nowhere
// else: Fable 2's disc sub_822C1BD0 seam (Fable2/src/vfs/src/hooks/guest_tick_pump.cpp).
//
// THE STEP'S COST. The guest's own step timings (component+136 game tick, +144 the rest, written
// at 0x821CBF6C and disc 0x821CC0A4) are differences of the mftb clock, which the host freezes between
// grants, so they cannot measure a stall. The wall time between two visits here is one whole HF
// step as the host sees it; a step that the lead cap withheld costs nothing here, a step blocked
// in the guest (the scene ring full in sub_821F4450, a stream wait, a lock) shows as the gap.
//
// THE STALL'S SITE. A blocked thread cannot sample itself, so each step is stated to the shared
// step sampler (core/payload_bridge.h, src/runtime/src/core/guest_step_sampler.cpp) on this
// thread's own PPCContext; a host thread walks the guest back chain once the step has run 250 ms,
// and the step's stall line below carries those frames, innermost first.

#include <chrono>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "box_answers.h"
#include "gui_statements.h"
#include "core/payload_bridge.h"
#include "keyboard_ui.h"
#include "mod_lua.h"
#include "../../vfs/include/fable3_guest_build.h"
#include "../../vfs/include/fable3_vfs.h"

REX_EXTERN(__imp__rex_App_TickInput);

namespace {

using Clock = std::chrono::steady_clock;

/// The game loop's call site; the thunk is reached from no other loop today, but the step clock
/// is only meaningful when the caller is the step.
const refii::guest::Addr kGameLoopReturn{0x821CBE84u};

/// A step the host grants every 33 ms; a gap of this much is a guest that stopped stepping.
constexpr auto kStallThreshold = std::chrono::milliseconds(250);
constexpr auto kStallLogSpacing = std::chrono::seconds(2);
constexpr uint64_t kSummaryEverySteps = 1800u;  // a minute at 30 Hz

double Milliseconds(Clock::duration d) {
  return std::chrono::duration<double, std::milli>(d).count();
}

constexpr uint32_t kMaxFrames = 12u;

/// "0x82... < 0x82... < ..." or "" when the step was never sampled.
void FormatFrames(const uint32_t* frames, uint32_t count, char* out, size_t capacity) {
  size_t used = 0;
  for (uint32_t i = 0; i < count && used + 14 < capacity; ++i) {
    used += static_cast<size_t>(
        std::snprintf(out + used, capacity - used, "%s0x%08X", i == 0 ? "" : " < ", frames[i]));
  }
  out[used < capacity ? used : capacity - 1] = '\0';
}

void NoteDebugCall(uint32_t addr, uint64_t r3, const char* note) {
  refiii_vfs_note_debug_result(addr, r3, note, std::strlen(note));
}

/// `debug-call:ADDR[:r3[:r4[:r5[:r6]]]]` (all hex, live addresses): one recompiled guest function
/// called on the tick's own context with the registers stated, r3 recorded for the host's
/// RefiiHostGetDebugCallResult; the context is restored afterwards. Fable 2's twin is in
/// Fable2/src/vfs/src/hooks/guest_tick_pump.cpp; only the loopback debug channel posts these.
void DebugCall(PPCContext& ctx, uint8_t* base, const char* args) {
  const std::string text(args);
  std::vector<uint64_t> values;
  std::size_t from = 0u;
  bool parsed = true;
  while (from <= text.size()) {
    const std::size_t colon = text.find(':', from);
    const std::string part = text.substr(from, colon == std::string::npos ? std::string::npos : colon - from);
    char* end = nullptr;
    values.push_back(std::strtoull(part.c_str(), &end, 16));
    if (end == part.c_str() || *end != '\0') {
      parsed = false;
      break;
    }
    if (colon == std::string::npos) {
      break;
    }
    from = colon + 1u;
  }
  if (!parsed || values.empty() || values.size() > 5u) {
    NoteDebugCall(0u, 0u, "debug-call needs ADDR[:r3[:r4[:r5[:r6]]]], all hex");
    return;
  }
  const uint32_t target = static_cast<uint32_t>(values[0]);
  PPCFunc* function = nullptr;
  for (PPCFuncMapping* mapping = PPCFuncMappings; target != 0u && mapping != nullptr && mapping->host != nullptr;
       ++mapping) {
    if (static_cast<uint32_t>(mapping->guest) == target) {
      function = mapping->host;
      break;
    }
  }
  if (function == nullptr) {
    NoteDebugCall(target, 0u, "no recompiled function at that address");
    return;
  }
  const PPCContext saved = ctx;
  if (values.size() > 1u) ctx.r3.u64 = values[1];
  if (values.size() > 2u) ctx.r4.u64 = values[2];
  if (values.size() > 3u) ctx.r5.u64 = values[3];
  if (values.size() > 4u) ctx.r6.u64 = values[4];
  function(ctx, base);
  const uint64_t result = ctx.r3.u64;
  ctx = saved;
  NoteDebugCall(target, result, "ok");
}

/// Actions the host posted (core/guest_tick_queue.h): the keyboard's answer (keyboard_ui.cpp),
/// a box's and the menu box's answers (display_box_hooks.cpp), the TopBox's presses
/// (topbox_answer.cpp), the Options screen's actions
/// and row (options_hooks.cpp), the save menu's row (save_menu_row.cpp), the PC build's extras
/// (pc_extras.cpp) and the debug channel's `debug-gamelua:` / `debug-call:`. Any other is
/// named in the log and dropped, never deferred, so the queue cannot hold a name forever. `ctx`
/// is the tick's, the frame the answers call the guest below.
void DrainPostedActions(PPCContext& ctx, uint8_t* base) {
  const uint32_t taken = refii_bridge_guest_tick_pump_begin_drain();
  for (uint32_t i = 0; i < taken; ++i) {
    char name[128];
    const size_t needed = refii_bridge_guest_tick_pump_taken_action(i, name, sizeof(name));
    name[sizeof(name) - 1] = '\0';
    const size_t answer = sizeof(refii::fable3::kKeyboardAnswerAction) - 1u;
    if (std::strncmp(name, refii::fable3::kKeyboardAnswerAction, answer) == 0) {
      refii::fable3::AnswerKeyboardUi(base, name + answer);
      continue;
    }
    if (std::strcmp(name, refii::fable3::kKeyboardCancelAction) == 0) {
      refii::fable3::AnswerKeyboardUi(base, nullptr);
      continue;
    }
    if (std::strncmp(name, refii::fable3::kBoxAnswerAction, sizeof(refii::fable3::kBoxAnswerAction) - 1u) == 0 ||
        std::strncmp(name, "menubox", 7u) == 0) {
      refii::fable3::AnswerBoxAction(ctx, base, name);
      continue;
    }
    if (std::strncmp(name, refii::fable3::kUiReaderAction, sizeof(refii::fable3::kUiReaderAction) - 1u) == 0) {
      std::vector<char> whole(needed + 1u, '\0');
      refii_bridge_guest_tick_pump_taken_action(i, whole.data(), whole.size());
      refii::fable3::AnswerUiReaderAction(whole.data());
      continue;
    }
    if (std::strncmp(name, refii::fable3::kTopBoxAction, sizeof(refii::fable3::kTopBoxAction) - 1u) == 0) {
      refii::fable3::AnswerTopBox(ctx, base, name);
      continue;
    }
    if (std::strncmp(name, refii::fable3::kPcExtraAction, sizeof(refii::fable3::kPcExtraAction) - 1u) == 0) {
      refii::fable3::AnswerPcExtra(ctx, base, name);
      continue;
    }
    if (std::strncmp(name, refii::fable3::kInteractionListAction, sizeof(refii::fable3::kInteractionListAction) - 1u) == 0) {
      refii::fable3::AnswerInteractionListStep(ctx, base, name);
      continue;
    }
    if (std::strncmp(name, refii::fable3::kSaveMenuRowAction, sizeof(refii::fable3::kSaveMenuRowAction) - 1u) == 0) {
      refii::fable3::AnswerSaveMenuRow(ctx, base, name);
      continue;
    }
    if (std::strncmp(name, refii::fable3::kOptionsRowAction, sizeof(refii::fable3::kOptionsRowAction) - 1u) == 0) {
      refii::fable3::AnswerOptionsRow(ctx, base, name);
      continue;
    }
    if (std::strncmp(name, refii::fable3::kOptionsAction, sizeof(refii::fable3::kOptionsAction) - 1u) == 0) {
      refii::fable3::AnswerOptionsAction(ctx, base, name);
      continue;
    }
    // The debug channel's probe of the game VM (GameDebugServer "lua" with "@game "): the chunk runs
    // here, and the answer is whether it ran, or the Lua error.
    constexpr const char kGameLua[] = "debug-gamelua:";
    if (std::strncmp(name, kGameLua, sizeof(kGameLua) - 1u) == 0) {
      std::vector<char> whole(needed + 1u, '\0');
      refii_bridge_guest_tick_pump_taken_action(i, whole.data(), whole.size());
      std::string error;
      const bool ran = refii::fable3::modlua::Run(ctx, base, whole.data() + sizeof(kGameLua) - 1u,
                                                  "=refii/debug", &error);
      const std::string note = ran ? std::string("ok") : error;
      refiii_vfs_note_debug_result(0u, ran ? 1u : 0u, note.c_str(), note.size());
      continue;
    }
    constexpr const char kDebugCall[] = "debug-call:";
    if (std::strncmp(name, kDebugCall, sizeof(kDebugCall) - 1u) == 0) {
      std::vector<char> whole(needed + 1u, '\0');
      refii_bridge_guest_tick_pump_taken_action(i, whole.data(), whole.size());
      DebugCall(ctx, base, whole.data() + sizeof(kDebugCall) - 1u);
      continue;
    }
    REXLOG_WARN("Refii guest-tick pump: action '{}'{} has no Fable 3 implementation; dropped",
                name, needed >= sizeof(name) ? "..." : "");
  }
  refii_bridge_guest_tick_pump_end_drain();
}

struct StepClock {
  Clock::time_point last{};
  Clock::time_point last_stall_log{};
  uint64_t steps = 0;
  uint64_t suppressed = 0;
  Clock::duration window_total{};
  Clock::duration window_max{};
  Clock::duration window_input_max{};
  uint64_t window_over_100ms = 0;
  uint64_t window_steps = 0;

  /// `frames`/`frame_count`: the sampler's walk of the step that just ended, when it stalled.
  void Observe(Clock::time_point now, Clock::duration input, const uint32_t* frames,
               uint32_t frame_count) {
    if (last != Clock::time_point{}) {
      const Clock::duration step = now - last;
      ++window_steps;
      window_total += step;
      if (step > window_max) {
        window_max = step;
      }
      if (step > std::chrono::milliseconds(100)) {
        ++window_over_100ms;
      }
      if (step >= kStallThreshold) {
        if (now - last_stall_log >= kStallLogSpacing) {
          char site[kMaxFrames * 14 + 1];
          FormatFrames(frames, frame_count, site, sizeof(site));
          REXLOG_WARN("Refii pace: HF step {} blocked {:.0f} ms at {} (input update {:.1f} ms; "
                      "{} more stalls since the last line)",
                      steps, Milliseconds(step), frame_count != 0u ? site : "(not sampled)",
                      Milliseconds(input), suppressed);
          last_stall_log = now;
          suppressed = 0;
        } else {
          ++suppressed;
        }
      }
    }
    if (input > window_input_max) {
      window_input_max = input;
    }
    last = now;
    ++steps;

    if (steps % kSummaryEverySteps == 0u && window_steps != 0u) {
      REXLOG_INFO("Refii pace: {} HF steps, mean {:.1f} ms, max {:.0f} ms, {} over 100 ms; "
                  "input update max {:.1f} ms",
                  window_steps, Milliseconds(window_total) / static_cast<double>(window_steps),
                  Milliseconds(window_max), window_over_100ms, Milliseconds(window_input_max));
      window_total = {};
      window_max = {};
      window_input_max = {};
      window_over_100ms = 0;
      window_steps = 0;
    }
  }
};

StepClock g_step_clock;

}  // namespace

REX_EXTERN(rex_App_TickInput) {
  const bool from_game_loop = ctx.lr == kGameLoopReturn;

  const uint64_t tick = refii_bridge_guest_tick_pump_observe_tick();
  if (tick == 0u || tick == 600u || tick == 6000u || tick == 60000u) {
    REXLOG_INFO("Refii guest-tick pump: tick {} on the guest tick thread (drained {})", tick + 1,
                refii_bridge_guest_tick_pump_drained());
  }

  // A fault on this thread needs a stack to run its handler on; guest threads get none.
  refii_bridge_ensure_alternate_signal_stack();

  DrainPostedActions(ctx, base);
  refii::fable3::PumpSaveMenuRow(ctx, base);
  refii::fable3::PumpTopBox(ctx, base);
  refii::fable3::modlua::Tick(ctx, base);

  const Clock::time_point before = Clock::now();
  __imp__rex_App_TickInput(ctx, base);

  if (from_game_loop) {
    const Clock::time_point after = Clock::now();
    // The step that ends here is the one the sampler may have walked; the one beginning is
    // stated on this thread's own context, which the sampler reads while it is blocked.
    uint32_t frames[kMaxFrames];
    const uint32_t frame_count =
        refii_bridge_guest_step_end(g_step_clock.steps, frames, kMaxFrames);
    g_step_clock.Observe(after, after - before, frames, frame_count);
    refii_bridge_guest_step_begin(&ctx, base, g_step_clock.steps);
  }
}
