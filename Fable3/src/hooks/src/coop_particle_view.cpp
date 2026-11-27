// refii — Fable 3: the particle system's level of detail answers either couch player's camera while split.
//
// THE PARTICLE VIEW. The scene camera post (TU3 sub_821CCDB0 / disc sub_821CCCA8, main thread) puts "the"
// camera — the selector's holder's block, sub_821CB2E0 — into a CollectionMessage (type 21, `bl
// sub_831C1438` 0x821CCE20) for the scene's view[0] (`lwz r4,0(r9)`, r9 = *(scene+4) 0x821CCE04..18), then the
// frame message (type 31, `bl sub_831BC9C0` 0x821CCF5C) naming view[0] and view[1] (`lwz r30,0(r7)` 0x821CCF00,
// `lwz r6,4(r5)` 0x821CCF14). On the particle thread (sub_831BD1A8's switch) type 21 sets the view's camera
// (sub_831BC970: the view sub_831BF8C0(msg+52), then sub_831BE848(view, v1 msg+16, v2 msg+32, f1 msg+48):
// position +0x30, direction +0x40, angle +0x50, and through sub_831BE770 the view cone +0x00..+0x24), and
// type 31 runs the frame (sub_831BD0F8 / disc sub_8318B238, r5 / r6 the two view ids) whose
// ParticleSystem_UpdateAndWrite (`bl` 0x831BD184) updates the scenes of those views. view[1] is the scene's
// second view, not a player's: in single player and on the shipped couch screen nothing posts it a camera.
//
// THE LEVEL OF DETAIL. For each system, UpdateAndWrite asks sub_831BFCC0(view, &system) (0x831C09F0), which
// calls sub_831BFAA8(&holder, view) (`bl` 0x831BFC88; disc sub_8318DBE8 from 0x8318DE80; its only caller):
// state system+8 = 0 (`stw r23,8(r11)` 0x831BFADC), the squared distance from the view's position (`lvx128
// v0,r4,r10` r10 = 0x30, 0x831BFAE4) stored at +0x14; at or beyond the range +0x10 the state is 2, else the
// bounds are tested against the view cone (sub_831BE938 0x831BFC2C, sub_831BE9C8 0x831BFC88) and a system
// outside it is 1 (`stw r11,8(r30)` 0x831BFC9C); then the holder is released (`bl sub_825FFF80` 0x831BFCA4).
// A system not at 0 is put out by sub_831BFCC0 (slot 28 of each instance, or sub_831B6A68). So on a split couch
// screen every effect beyond player one's range or out of player one's view was put out in player two's half.
//
// THE ANSWER. While each couch player has their own camera, the frame gives the particle thread a second view
// for its update: a copy of view[0] on the frame's guest stack, its camera set by the guest's own setter
// (sub_831BE848 / disc sub_8318C988) from the block player two's camera manager's getter last built (the
// stated block, refii_bridge_camera_stated_block). A system the shipped test puts out is asked again through
// that view; it keeps the better state (0 over 1 over 2), as the AI's 50 m filters keep either view. Single
// player, online and a shared couch screen run the shipped frame untouched.

#include <atomic>
#include <cstdint>
#include <cstring>

#include <rex/logging.h>
#include <rex/ppc.h>
#include <rex/ppc/context.h>

#include "coop_camera.h"
#include "core/payload_bridge.h"
#include "mod_seam.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_ParticleThread_Frame);
REX_EXTERN(__imp__rex_ParticleSystem_LevelOfDetail);

namespace {

using refii::fable3::seam::CallAt;
using refii::fable3::seam::LoadU32;
using refii::fable3::seam::Plausible;
using refii::fable3::seam::StoreU32;

const refii::guest::Addr kViewLookup{0x831BF8C0u};     // the view of an id, or 0
const refii::guest::Addr kViewSetCamera{0x831BE848u};  // view, v1 position, v2 direction, f1 angle

/// The bytes of a particle view this answer copies: the cone +0x00..+0x2F, the camera +0x30..+0x5F.
constexpr uint32_t kViewBytes = 0x60u;
/// The guest stack the frame takes below its caller's: the view at +0x40, the callee's frames below.
constexpr uint32_t kFrameBytes = 0x100u;

/// Player two's view for the frame running on this thread (the particle thread), else 0.
thread_local uint32_t t_second_view = 0u;

std::atomic<bool> g_said{false};

void SetLanes(PPCVRegister& v, const float* xyz, float w) {
  v.f32[3] = xyz[0];
  v.f32[2] = xyz[1];
  v.f32[1] = xyz[2];
  v.f32[0] = w;
}

float LoadF32(const uint8_t* base, uint32_t at) {
  const uint32_t bits = LoadU32(base, at);
  float value = 0.0f;
  std::memcpy(&value, &bits, sizeof(value));
  return value;
}

/// One more reference on a guest reference count (its word at `count`), as the guest's own lwarx loop.
void AddReference(uint8_t* base, uint32_t count) {
  auto* word = reinterpret_cast<uint32_t*>(base + count);
  uint32_t seen = __atomic_load_n(word, __ATOMIC_ACQUIRE);
  while (!__atomic_compare_exchange_n(word, &seen, __builtin_bswap32(__builtin_bswap32(seen) + 1u), true,
                                      __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE)) {
  }
}

}  // namespace

// The particle frame (r3 the message, r4 its flag, r5 / r6 the two view ids, f1 the step): player two's view
// built for it while split.
REX_EXTERN(rex_ParticleThread_Frame) {
  float block[11];
  if (!refii::fable3::coopcam::SplitInEffect(base) || refii_bridge_camera_stated_block(2, block) == 0) {
    __imp__rex_ParticleThread_Frame(ctx, base);
    return;
  }

  PPCContext lookup = ctx;
  lookup.r1.u32 = (ctx.r1.u32 - kFrameBytes) & ~0xFu;
  lookup.r3.u32 = ctx.r5.u32;
  const uint32_t first = CallAt(lookup, base, kViewLookup) ? lookup.r3.u32 : 0u;
  if (!Plausible(first)) {
    __imp__rex_ParticleThread_Frame(ctx, base);
    return;
  }

  PPCContext work = ctx;
  work.r1.u32 = (ctx.r1.u32 - kFrameBytes) & ~0xFu;
  StoreU32(base, work.r1.u32, ctx.r1.u32);  // the back chain
  const uint32_t view = work.r1.u32 + 0x40u;
  std::memcpy(base + view, base + first, kViewBytes);

  PPCContext set = work;
  set.r1.u32 = (work.r1.u32 - kFrameBytes) & ~0xFu;
  set.r3.u32 = view;
  SetLanes(set.v1, block, LoadF32(base, first + 0x3Cu));
  SetLanes(set.v2, block + 3, LoadF32(base, first + 0x4Cu));
  set.f1.f64 = block[9];
  if (!CallAt(set, base, kViewSetCamera)) {
    __imp__rex_ParticleThread_Frame(ctx, base);
    return;
  }
  if (!g_said.exchange(true, std::memory_order_relaxed)) {
    REXLOG_INFO("Refii couch camera: the particles' level of detail answers either player's camera while split");
  }

  t_second_view = view;
  __imp__rex_ParticleThread_Frame(work, base);
  t_second_view = 0u;
  const PPCRegister caller = ctx.r1;
  ctx = work;
  ctx.r1 = caller;
}

// The level of detail of one system (r3 the holder {system, reference count}, r4 the view): asked again
// through player two's view when the shipped answer puts the system out; the better state stands.
REX_EXTERN(rex_ParticleSystem_LevelOfDetail) {
  const uint32_t second = t_second_view;
  const uint32_t holder = ctx.r3.u32;
  if (second == 0u || ctx.r4.u32 == second || !Plausible(holder)) {
    __imp__rex_ParticleSystem_LevelOfDetail(ctx, base);
    return;
  }
  const uint32_t system = LoadU32(base, holder);
  const uint32_t count = LoadU32(base, holder + 4u);
  PPCContext first = ctx;
  __imp__rex_ParticleSystem_LevelOfDetail(first, base);
  if (!Plausible(system)) {
    return;
  }
  const uint32_t state = LoadU32(base, system + 8u);
  if (state == 0u) {
    return;
  }
  const uint32_t distance = LoadU32(base, system + 0x14u);

  // The release at the end (sub_825FFF80) emptied the holder and dropped its reference: both put back.
  StoreU32(base, holder, system);
  StoreU32(base, holder + 4u, count);
  if (Plausible(count)) {
    AddReference(base, count);
  }
  PPCContext again = ctx;
  again.r4.u32 = second;
  __imp__rex_ParticleSystem_LevelOfDetail(again, base);
  if (LoadU32(base, system + 8u) > state) {
    StoreU32(base, system + 8u, state);
    StoreU32(base, system + 0x14u, distance);
  }
}
