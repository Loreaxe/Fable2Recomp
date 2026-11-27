// refii — Fable 2: the particle systems' level of detail answers either split-screen player's camera.
//
// Addresses are TU1 / disc; the bodies are the same instruction for instruction.
//
// THE PARTICLE VIEW. The world frame (sub_821DFDD0 / sub_821DF990, called once a frame from
// sub_821DC270 / sub_821DBDD0) looks up the world scene's particle view (`bl sub_832342B0` 0x821E00F0 /
// `bl sub_8322DA38` 0x821DFCB0: a 0x60-byte view in a map keyed by id), takes "the" camera
// (`lwz r3,0x9C(r10); bl sub_821F87C8` 0x821E0108..0C / `bl sub_821F8760` 0x821DFCCC), builds its block
// (`addi r3,r1,var_1110; bl lhCamera_ScreenBlock` 0x821E011C..20 / 0x821DFCDC..E0: +0 position, +0x20
// front, +0x30 the field of view) and sets the view's camera from it (`lvx128 v2` block+0x20, `lvx128 v1`
// block+0, `lfs f1` block+0x30, `mr r3,r30; bl sub_832318C8` 0x821E012C..3C / `bl sub_8322B050`
// 0x821DFCFC). The setter stores position +0x30, front +0x40 (`stvx128 v1,r0,r30` / `v2,r0,r29`,
// r30 = view+0x30, r29 = view+0x40, 0x832318DC..EC / 0x8322B064..74), the field of view +0x50 (`stfs
// f1,0x50(r31)` 0x8323192C / 0x8322B0B4, widened under g_WidescreenMode) and the cone (`bl sub_832317F8`
// 0x8323193C / `bl sub_8322AF80` 0x8322B0C4: apex +0x00, axis +0x10, cos / sin of the half angle +0x20 /
// +0x24). Then the frame runs the systems' update (`bl sub_83234C38` 0x821E0208 / `bl sub_8322E3C0`
// 0x821DFDC8). The getter answers player two only inside a player-two bracket
// (coop_camera_readers.cpp), which the world frame never enters: the view is player one's camera.
//
// THE LEVEL OF DETAIL. The update's pass (sub_83234A80 / sub_8322E208) takes each scene's view by its
// id (`lwz r3,0x40(r11); bl sub_832342B0` 0x83234ACC..D0 / 0x8322E254..58) and asks, per system,
// sub_83233EF0(&holder, view) / sub_8322D678 (`bl` 0x83234B7C / 0x8322E304, their only callers):
// state system+8 = 0 (`stw r24,8(r11)` 0x83233F24 / 0x8322D6AC), the squared distance from the view's
// position (`li r10,0x30; lvx128 v0,r23,r10` 0x83233F14 / 0x83233F28) to the first instance's +0x50,
// stored at system+0x18 (`stfs f0,0x18(r8)` 0x83233F54 / 0x8322D6DC); at or beyond the range
// system+0x14 (`fcmpu cr6,f13,f12` 0x83233F64 / 0x8322D6EC) the state is 2 (`li r10,2` 0x83233F7C),
// else the instances' bounds are tested against the view's cone (sub_832319D0 0x83234078, sub_83231A60
// 0x832340DC / sub_8322B158, sub_8322B1E8 0x8322D864) and a system outside it is 1 (`li r10,1; stw
// r10,8(r11)` 0x832340F0..F4 / 0x8322D878..7C); then the holder is released (`mr r3,r25; bl
// sub_821B7B18` 0x832340F8..FC / `bl sub_829FF648` 0x8322D884: one reference dropped, `lwarx; addi
// r10,r10,-1; stwcx.` 0x821B7B40..48 / 0x829FF670..78, and both words zeroed, `stw r11,0(r31); stw
// r11,4(r31)` 0x821B7B80..84 / 0x829FF6B0..B4).
// A system not at 0 is not updated (`lwz r10,8(r11); cmpwi cr6,r10,0; bne` sub_83233890 0x832338AC..B4 /
// sub_8322D018 0x8322D034..3C, and sub_832337C0 0x832337E4..EC / sub_8322CF48 0x8322CF6C..74: the
// instances' update slots +36 and +40 — the per-tick writers of each emitter's world transform), and one
// whose first instance has a lifetime (+0xC0 >= 0) or whose system+0x10 is set is ENDED (`lwz
// r10,0xC0(r3); cmpwi; bge` 0x83234B94..9C, `lbz r10,0x10(r31)` 0x83234BA0, `bl sub_83228E88` 0x83234BF0 /
// 0x8322E31C..24, 0x8322E328, `bl sub_83222610` 0x8322E378: `stb r31,0xC8(r11)` 0x83228EAC / 0x83222634,
// the flag sub_83234338 retires on). So on split screen every effect beyond player one's range or out of
// player one's view stood still, and every timed one ended, in player two's half too.
//
// THE ANSWER. While split screen is in effect, the world frame keeps a second view on its guest stack.
// When the frame sets the world view's camera, the same view is copied there and its camera set by the
// guest's own setter from player two's block — lhCamera_ScreenBlock run for player two's camera holder,
// the same call the second listener makes (RefiiCoopSplitPartnerCameraHolder, coop_hero_hooks.cpp). A
// system the shipped test puts out through the world view is asked again through player two's view and
// keeps the better state (0 over 1 over 2) with its distance. Single player, online and a shared screen
// run the shipped frame untouched.

#include <atomic>
#include <cstdint>
#include <cstring>

#include <rex/logging.h>
#include <rex/ppc/context.h>
#include <rex/ppc/func.h>

#include "core/guest_state.h"

extern "C" REX_FUNC(__imp__rex_lhWorld_Frame);
extern "C" REX_FUNC(__imp__rex_ParticleView_SetCamera);
extern "C" REX_FUNC(__imp__rex_ParticleSystem_LevelOfDetail);
extern "C" REX_FUNC(__imp__rex_lhCamera_ScreenBlock);

extern "C" int RefiiCoopSplitScreen(void);
extern "C" uint32_t RefiiCoopSplitPartnerCameraHolder(uint8_t* base);

namespace {

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

/// The guest stack the world frame takes below its caller's: player two's view at +0x40 (0x60 bytes),
/// player two's camera block at +0xA0 (0x40 bytes); the frame's own stack below.
constexpr uint32_t kFrameBytes = 0x100u;
constexpr uint32_t kViewAt = 0x40u;
constexpr uint32_t kBlockAt = 0xA0u;
constexpr uint32_t kViewBytes = 0x60u;

/// Inside the world frame on this thread while split: the stack slot for player two's view; else 0.
thread_local uint32_t t_slot = 0u;
/// The world view the frame set this frame, and player two's copy of it (both 0 until it is set).
thread_local uint32_t t_world_view = 0u;
thread_local uint32_t t_second_view = 0u;

std::atomic<bool> g_said{false};

/// v = the 16 guest bytes at `at`, as lvx128 loads them (the whole vector byte-reversed).
void LoadVector(PPCVRegister& v, const uint8_t* base, uint32_t at) {
  for (uint32_t i = 0; i < 16u; ++i) {
    v.u8[15u - i] = base[at + i];
  }
}

float LoadF32(uint8_t* base, uint32_t at) {
  const uint32_t bits = refii::vfs::LoadGuestU32(base, at);
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

// The world frame (r3 the world, r4 the frame, f1 the step): a stack slot for player two's view while
// split.
REX_EXTERN(rex_lhWorld_Frame) {
  if (t_slot != 0u || RefiiCoopSplitScreen() == 0) {
    __imp__rex_lhWorld_Frame(ctx, base);
    return;
  }
  PPCContext work = ctx;
  work.r1.u32 = (ctx.r1.u32 - kFrameBytes) & ~0xFu;
  refii::vfs::StoreGuestU32(base, work.r1.u32, ctx.r1.u32);  // the back chain
  t_slot = work.r1.u32;
  __imp__rex_lhWorld_Frame(work, base);
  t_slot = 0u;
  t_world_view = 0u;
  t_second_view = 0u;
  const PPCRegister caller = ctx.r1;
  ctx = work;
  ctx.r1 = caller;
}

// A particle view's camera (r3 the view, v1 position, v2 front, f1 the field of view). Inside the world
// frame while split: player two's copy of the view, its camera player two's.
REX_EXTERN(rex_ParticleView_SetCamera) {
  const PPCContext entry = ctx;
  __imp__rex_ParticleView_SetCamera(ctx, base);
  const uint32_t slot = t_slot;
  const uint32_t view = entry.r3.u32;
  if (slot == 0u || !Plausible(view)) {
    return;
  }
  const uint32_t holder = RefiiCoopSplitPartnerCameraHolder(base);
  if (holder == 0u) {
    t_world_view = 0u;
    t_second_view = 0u;
    return;
  }

  const uint32_t block = slot + kBlockAt;
  PPCContext call = entry;
  call.r3.u32 = block;
  call.r4.u32 = holder;
  __imp__rex_lhCamera_ScreenBlock(call, base);

  const uint32_t second = slot + kViewAt;
  std::memcpy(base + second, base + view, kViewBytes);
  PPCContext set = entry;
  set.r3.u32 = second;
  LoadVector(set.v1, base, block + 0x00u);
  LoadVector(set.v2, base, block + 0x20u);
  set.f1.f64 = LoadF32(base, block + 0x30u);
  __imp__rex_ParticleView_SetCamera(set, base);

  t_world_view = view;
  t_second_view = second;
  if (!g_said.exchange(true, std::memory_order_relaxed)) {
    REXLOG_INFO("Refii co-op cameras: the particles' level of detail answers either player's camera while split");
  }
}

// The level of detail of one system (r3 the holder {system, reference count}, r4 the view): asked again
// through player two's view when the shipped answer through the world view puts the system out; the
// better state stands.
REX_EXTERN(rex_ParticleSystem_LevelOfDetail) {
  const uint32_t second = t_second_view;
  const uint32_t holder = ctx.r3.u32;
  if (second == 0u || ctx.r4.u32 != t_world_view || !Plausible(holder)) {
    __imp__rex_ParticleSystem_LevelOfDetail(ctx, base);
    return;
  }
  const uint32_t system = refii::vfs::LoadGuestU32(base, holder);
  const uint32_t count = refii::vfs::LoadGuestU32(base, holder + 4u);
  PPCContext first = ctx;
  __imp__rex_ParticleSystem_LevelOfDetail(first, base);
  if (!Plausible(system)) {
    return;
  }
  const uint32_t state = refii::vfs::LoadGuestU32(base, system + 8u);
  if (state == 0u) {
    return;
  }
  const uint32_t distance = refii::vfs::LoadGuestU32(base, system + 0x18u);

  // The release at the end (sub_821B7B18) emptied the holder and dropped its reference: both put back.
  refii::vfs::StoreGuestU32(base, holder, system);
  refii::vfs::StoreGuestU32(base, holder + 4u, count);
  if (Plausible(count)) {
    AddReference(base, count);
  }
  PPCContext again = ctx;
  again.r4.u32 = second;
  __imp__rex_ParticleSystem_LevelOfDetail(again, base);
  if (refii::vfs::LoadGuestU32(base, system + 8u) > state) {
    refii::vfs::StoreGuestU32(base, system + 8u, state);
    refii::vfs::StoreGuestU32(base, system + 0x18u, distance);
  }
}
