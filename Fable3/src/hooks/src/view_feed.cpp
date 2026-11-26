// refii - Fable 3's world view, stated to the host: each new guest frame, and its preload gate,
// which waits while Godot builds the level. The camera itself is stated by its setters and easing
// step (camera_statements.cpp); the shell rebuilds it and states the drawn camera back
// (RefiiHostStateCamera), which StatedFovRadians and the mouse look read.
//
// ONE VIEW PER GAME, AND ONLY THE PRESENTED ONE IS FOLLOWED. The front end keeps two games standing
// (presented_world.h in the host bridge); each runs its own high-frequency pass sub_82255CF0(game,
// step), which asks the getter for its own camera manager's block (`lwz r30,0x40(r31)`,
// sub_82208EA0, `bl sub_821CB2E0` at 0x82255D50). The game last made current is *0x83597030; while
// its pass runs, InPresentedPass() holds, and camera_statements.cpp names the manager it asks for
// as the presented one.
//
// THE PRELOAD GATE. sub_822E13B8(engine) answers "the 3D Engine is still preloading": 1 while
// engine+0xC8 < 5 or engine+0x264 is set (0x822E13DC / 0x822E13E8). The level-change command
// waits on it in its state 9 (`bl sub_822E13B8` at 0x82756EE4, `bne` stays), the load-from-save
// command in its state 4, and the game tick's loading-screen fade holds on it. Answering 1 while
// the host builds keeps the guest in its own loading screen, the hold Fable 2's bridge makes.

#include <cstdint>
#include <cstring>

#include <rex/ppc.h>

#include "core/payload_bridge.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_GameLoop_HighFrequencyUpdate);
REX_EXTERN(__imp__rex_Engine_IsPreLoading);

namespace {

const refii::guest::Addr kPresentedGame{0x83597030u};

/// Inside the presented game's high-frequency pass, on this thread (camera_statements.cpp names the
/// manager that pass asks the getter for as the presented one).
thread_local bool t_presented_pass = false;

uint32_t LoadGuestU32(const uint8_t* base, uint32_t address) {
  uint32_t value = 0;
  std::memcpy(&value, base + address, sizeof(value));
  return __builtin_bswap32(value);
}

}  // namespace

namespace refii::fable3::view {
float StatedFovRadians() {
  float drawn[11];
  return refii_bridge_camera_read(drawn) != 0 ? drawn[9] : 0.0f;
}
bool InPresentedPass() { return t_presented_pass; }
}  // namespace refii::fable3::view

// THE GUEST FRAME. sub_82255CF0(game, f1 step time) is the main loop's high-frequency pass (its
// one caller, sub_821CB7B0 at disc 0x821CBEE8): the camera blend and the view's render-camera
// fill, the posed state of the step. The guest keeps no counter of it, so this is where the host learns a new frame
// of guest state is in memory — what Fable 2's rex_lhGameLoop_HighFrequencyUpdate states. f1 is
// the step's time (lhGameLoop_Tick's gameloop+0xC0, QPC seconds; `fmr f31,f1` at 0x82255D0C), the
// stamp it gives the render queue's batch (sub_82195F88(dword_835A0270, f31) at 0x82255D28): kept
// with the frame, it turns a render-side time back into a guest frame (cloth_hooks.cpp).
REX_EXTERN(rex_GameLoop_HighFrequencyUpdate) {
  const double step_time = ctx.f1.f64;
  const bool presented = ctx.r3.u32 != 0u && ctx.r3.u32 == LoadGuestU32(base, kPresentedGame);
  t_presented_pass = presented;
  __imp__rex_GameLoop_HighFrequencyUpdate(ctx, base);
  t_presented_pass = false;
  refii_bridge_note_guest_frame();
  refii_bridge_note_guest_step_time(step_time);
}

REX_EXTERN(rex_Engine_IsPreLoading) {
  __imp__rex_Engine_IsPreLoading(ctx, base);
  if (refii_bridge_world_build_pending() != 0) {
    ctx.r3.u64 = 1;
  }
}
