// refii — Fable 3's camera, stated (src/runtime/include/core/camera_statements.h).
//
// THE CONTRACT (TU3 addresses; the disc's are in Fable3/versions/Fable3/refiii_config.toml under the
// same names). As on Fable 2, the camera every reader sees is one camera component's state
// (component 108 of the active wrapper's entity): the block builder sub_8224DE50 takes the entity's
// position (physics vt+0x50) and orientation (vt+0x54), the current horizontal FOV (+0x18), the cut
// byte (+0x64) and the last shake (+0xC4, block +0x38). That state is written only by
//   - the component's setters, the Lua Camera natives (r3 the component): MoveTo +0x90, MoveToCut,
//     the direction bodies sub_82179AF8 (v1 the direction, v2 world Z; SetDirection is a thunk to it
//     and SetTarget calls it) and sub_826A0578 (the Cut form), SetAngles / SetAnglesCut (degrees),
//     SetFOV +0x1C (Cut +0x18), SetRoll +0x6C (Cut +0x68), SetDOF (seven floats), the shake natives
//     (+0xC0), InterpolateFor (+0xD0/+0xD4, the factor +0xD8), SetCutThisFrame +0x64 (folded with
//     Villager.HideOccupation: a statement on a component that is never eased is not a camera's),
//     SetBlur +0xCC, SetRepulsion (point +0x100, radius +0x110);
//   - the easing step sub_821A2E70 (r3 the component, r4 the active flag): position lerp by +0xD8,
//     kept out of the repulsion sphere by sub_822895B8 (a smooth camera ray, so its answer is
//     stated), roll lerp, while the radius stands SetTarget(+0x100) and four times the factor, slerp
//     of +0xB0 to +0xA0, roll about forward, FOV lerp, the shake latched into +0xC4; then the factor's
//     next value, and the radius cleared when the interpolation ends;
//   - the manager's settle pass sub_8256C3D8 (factor 1, cut 1, counters 0, radius 0 before easing).
// Nothing here writes guest memory.
//
// THE REFERENCE. The getter sub_8224F510(manager) builds the block into manager+0xA0 (memoised per
// millisecond; +0x140 while manager+0x45 holds the spectator camera); it is stated when it changed.
// The push sub_82252778 ends each manager update. The presented game's manager is the one its own
// high-frequency pass asks the getter for (view_feed.cpp).

#include <array>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <mutex>

#include <rex/ppc.h>

#include "core/camera_statements.h"
#include "core/payload_bridge.h"

REX_EXTERN(__imp__rex_Camera_MoveTo);
REX_EXTERN(__imp__rex_Camera_MoveToCut);
REX_EXTERN(__imp__rex_Camera_SetDirection);
REX_EXTERN(__imp__rex_Camera_SetDirectionCut);
REX_EXTERN(__imp__rex_Camera_SetTarget);
REX_EXTERN(__imp__rex_Camera_SetTargetCut);
REX_EXTERN(__imp__rex_Camera_SetAngles);
REX_EXTERN(__imp__rex_Camera_SetAnglesCut);
REX_EXTERN(__imp__rex_Camera_SetFov);
REX_EXTERN(__imp__rex_Camera_SetFovCut);
REX_EXTERN(__imp__rex_Camera_SetRoll);
REX_EXTERN(__imp__rex_Camera_SetRollCut);
REX_EXTERN(__imp__rex_Camera_SetZoomLevel);
REX_EXTERN(__imp__rex_Camera_SetDof);
REX_EXTERN(__imp__rex_Camera_SetDofCut);
REX_EXTERN(__imp__rex_Camera_AddShake);
REX_EXTERN(__imp__rex_Camera_SetShake);
REX_EXTERN(__imp__rex_Camera_StopShake);
REX_EXTERN(__imp__rex_Camera_InterpolateFor);
REX_EXTERN(__imp__rex_Camera_SetCutThisFrame);
REX_EXTERN(__imp__rex_Camera_SetBlur);
REX_EXTERN(__imp__rex_Camera_SetBlurCut);
REX_EXTERN(__imp__rex_Camera_SetRepulsion);
REX_EXTERN(__imp__rex_Camera_Ease);
REX_EXTERN(__imp__rex_Camera_Repulsion);
REX_EXTERN(__imp__rex_CameraManager_Block);
REX_EXTERN(__imp__rex_CameraManager_Push);
REX_EXTERN(__imp__rex_CameraManager_Snap);
REX_EXTERN(__imp__rex_CameraManager_ActivateCamera);

namespace refii::fable3::view {
bool InPresentedPass();
}  // namespace refii::fable3::view

namespace {

namespace cam = refii::camera;

constexpr uint32_t kBlockBytes = 0x40u;
constexpr uint32_t kBlockAt = 0xA0u;

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

uint32_t LoadU32(const uint8_t* base, uint32_t at) {
  uint32_t value = 0;
  std::memcpy(&value, base + at, sizeof(value));
  return __builtin_bswap32(value);
}

float LoadF32(const uint8_t* base, uint32_t at) {
  const uint32_t bits = LoadU32(base, at);
  float value = 0.0f;
  std::memcpy(&value, &bits, sizeof(value));
  return value;
}

void ReadVec3(const uint8_t* base, uint32_t at, float* out) {
  for (uint32_t i = 0; i < 3u; ++i) {
    out[i] = LoadF32(base, at + i * 4u);
  }
}

struct Depth {
  uint32_t at = refii_bridge_camera_enter();
  Depth() = default;
  Depth(const Depth&) = delete;
  Depth& operator=(const Depth&) = delete;
  ~Depth() { refii_bridge_camera_leave(); }
};

void State(const cam::Statement& statement) { refii_bridge_camera_statement(statement.words); }

cam::Statement& Vector(cam::Statement& statement, const PPCContext& ctx) {
  return statement.Value(0, ctx.v1.f32[3]).Value(1, ctx.v1.f32[2]).Value(2, ctx.v1.f32[1]);
}

cam::Statement Setter(uint32_t call, const Depth& depth, const PPCContext& ctx) {
  cam::Statement statement(call, depth.at);
  statement.Component(ctx.r3.u32);
  return statement;
}

/// The manager the presented game's high-frequency pass last asked the getter for.
std::atomic<uint32_t> g_presented_manager{0};

std::mutex g_block_mutex;
struct LastBlock {
  uint32_t manager = 0;
  std::array<uint8_t, kBlockBytes> bytes{};
};
std::array<LastBlock, 4> g_last_blocks{};

bool BlockChanged(uint32_t manager, const uint8_t* bytes) {
  std::lock_guard<std::mutex> lock(g_block_mutex);
  LastBlock* slot = nullptr;
  for (LastBlock& last : g_last_blocks) {
    if (last.manager == manager) {
      slot = &last;
      break;
    }
  }
  if (slot == nullptr) {
    for (LastBlock& last : g_last_blocks) {
      if (last.manager == 0u) {
        slot = &last;
        break;
      }
    }
  }
  if (slot == nullptr) {
    slot = &g_last_blocks[manager % g_last_blocks.size()];
  }
  if (slot->manager == manager && std::memcmp(slot->bytes.data(), bytes, kBlockBytes) == 0) {
    return false;
  }
  slot->manager = manager;
  std::memcpy(slot->bytes.data(), bytes, kBlockBytes);
  return true;
}

/// The entity the block is built from: the override wrapper (+0x3C) or the primary (+0x2C), its
/// smart pointer's node (+0xC), the node's entity (sub_8224DE50's first loads).
uint32_t BlockSource(const uint8_t* base, uint32_t manager) {
  uint32_t wrapper = LoadU32(base, manager + 0x3Cu);
  if (!Plausible(wrapper)) {
    wrapper = LoadU32(base, manager + 0x2Cu);
  }
  const uint32_t node = Plausible(wrapper) ? LoadU32(base, wrapper + 0xCu) : 0u;
  return Plausible(node) ? LoadU32(base, node) : 0u;
}

}  // namespace

#define REFII_CAMERA_VECTOR(name, call)                                  \
  REX_EXTERN(name) {                                                     \
    const Depth depth;                                                   \
    cam::Statement statement = Setter(call, depth, ctx);                 \
    Vector(statement, ctx);                                              \
    __imp__##name(ctx, base);                                            \
    State(statement);                                                    \
  }

#define REFII_CAMERA_FLOATS(name, call, count)                           \
  REX_EXTERN(name) {                                                     \
    const Depth depth;                                                   \
    cam::Statement statement = Setter(call, depth, ctx);                 \
    const double floats[7] = {ctx.f1.f64, ctx.f2.f64, ctx.f3.f64,        \
                              ctx.f4.f64, ctx.f5.f64, ctx.f6.f64,        \
                              ctx.f7.f64};                               \
    for (uint32_t i = 0; i < (count); ++i) {                             \
      statement.Value(i, static_cast<float>(floats[i]));                 \
    }                                                                    \
    __imp__##name(ctx, base);                                            \
    State(statement);                                                    \
  }

#define REFII_CAMERA_WORD(name, call, mask)                              \
  REX_EXTERN(name) {                                                     \
    const Depth depth;                                                   \
    cam::Statement statement = Setter(call, depth, ctx);                 \
    statement.Word(ctx.r4.u32 & (mask));                                 \
    __imp__##name(ctx, base);                                            \
    State(statement);                                                    \
  }

REFII_CAMERA_VECTOR(rex_Camera_MoveTo, cam::kMoveTo)
REFII_CAMERA_VECTOR(rex_Camera_MoveToCut, cam::kMoveToCut)
REFII_CAMERA_VECTOR(rex_Camera_SetDirection, cam::kSetDirection)
REFII_CAMERA_VECTOR(rex_Camera_SetDirectionCut, cam::kSetDirectionCut)
REFII_CAMERA_VECTOR(rex_Camera_SetTarget, cam::kSetTarget)
REFII_CAMERA_VECTOR(rex_Camera_SetTargetCut, cam::kSetTargetCut)
REFII_CAMERA_FLOATS(rex_Camera_SetAngles, cam::kSetAngles, 3u)
REFII_CAMERA_FLOATS(rex_Camera_SetAnglesCut, cam::kSetAnglesCut, 3u)
REFII_CAMERA_FLOATS(rex_Camera_SetFov, cam::kSetFov, 1u)
REFII_CAMERA_FLOATS(rex_Camera_SetFovCut, cam::kSetFovCut, 1u)
REFII_CAMERA_FLOATS(rex_Camera_SetRoll, cam::kSetRoll, 1u)
REFII_CAMERA_FLOATS(rex_Camera_SetRollCut, cam::kSetRollCut, 1u)
REFII_CAMERA_WORD(rex_Camera_SetZoomLevel, cam::kSetZoomLevel, 0xFFFFFFFFu)
REFII_CAMERA_FLOATS(rex_Camera_SetDof, cam::kSetDof, 7u)
REFII_CAMERA_FLOATS(rex_Camera_SetDofCut, cam::kSetDofCut, 7u)
REFII_CAMERA_FLOATS(rex_Camera_AddShake, cam::kAddShake, 1u)
REFII_CAMERA_FLOATS(rex_Camera_SetShake, cam::kSetShake, 1u)
REFII_CAMERA_FLOATS(rex_Camera_StopShake, cam::kStopShake, 0u)
REFII_CAMERA_WORD(rex_Camera_InterpolateFor, cam::kInterpolateFor, 0xFFFFFFFFu)
REFII_CAMERA_WORD(rex_Camera_SetCutThisFrame, cam::kSetCutThisFrame, 0xFFu)
REFII_CAMERA_FLOATS(rex_Camera_SetBlur, cam::kSetBlur, 1u)
REFII_CAMERA_FLOATS(rex_Camera_SetBlurCut, cam::kSetBlurCut, 1u)

// SetRepulsion (comp, v1 point, f1 radius): `stfs f1,0x110(r3); stvx128 v1,r3,r11(0x100)`.
REX_EXTERN(rex_Camera_SetRepulsion) {
  const Depth depth;
  cam::Statement statement = Setter(cam::kSetRepulsion, depth, ctx);
  Vector(statement, ctx).Value(3, static_cast<float>(ctx.f1.f64));
  __imp__rex_Camera_SetRepulsion(ctx, base);
  State(statement);
}

// The easing step's inputs as it starts: factor +0xD8, shake +0xC0, repulsion radius +0x110, cut
// byte +0x64.
REX_EXTERN(rex_Camera_Ease) {
  const Depth depth;
  const uint32_t comp = ctx.r3.u32;
  cam::Statement begin(cam::kEaseBegin, depth.at);
  begin.Component(comp).Entity(LoadU32(base, comp + 4u)).Word(ctx.r4.u32 & 0xFFu);
  begin.Value(0, LoadF32(base, comp + 0xD8u))
      .Value(1, LoadF32(base, comp + 0xC0u))
      .Value(2, LoadF32(base, comp + 0x110u))
      .Value(3, static_cast<float>(base[comp + 0x64u]));
  State(begin);
  __imp__rex_Camera_Ease(ctx, base);
  cam::Statement end(cam::kEaseEnd, depth.at);
  end.Component(comp);
  State(end);
}

// sub_822895B8(out, comp, &eased): the eased position, or the smooth ray's hit (comp+0x130) when it
// fell inside the repulsion sphere; stated when the step kept a different point.
REX_EXTERN(rex_Camera_Repulsion) {
  const Depth depth;
  const uint32_t out = ctx.r3.u32;
  const uint32_t comp = ctx.r4.u32;
  float eased[3];
  ReadVec3(base, ctx.r5.u32, eased);
  __imp__rex_Camera_Repulsion(ctx, base);
  float kept[3];
  ReadVec3(base, out, kept);
  if (std::memcmp(eased, kept, sizeof(eased)) != 0) {
    cam::Statement statement(cam::kRepulsion, depth.at);
    statement.Component(comp);
    for (uint32_t i = 0; i < 3u; ++i) {
      statement.Value(i, eased[i]).Value(3u + i, kept[i]);
    }
    State(statement);
  }
}

REX_EXTERN(rex_CameraManager_Block) {
  const Depth depth;
  const uint32_t manager = ctx.r3.u32;
  if (refii::fable3::view::InPresentedPass()) {
    g_presented_manager.store(manager, std::memory_order_relaxed);
  }
  const uint32_t source = BlockSource(base, manager);
  __imp__rex_CameraManager_Block(ctx, base);
  const uint32_t block = manager + kBlockAt;
  if (ctx.r3.u32 != block) {
    return;
  }
  uint8_t bytes[kBlockBytes];
  std::memcpy(bytes, base + block, kBlockBytes);
  if (!BlockChanged(manager, bytes)) {
    return;
  }
  cam::Statement statement(cam::kGuestBlock, depth.at);
  statement.Manager(manager).Entity(source).Word(base[block + 0x34u]);
  for (uint32_t i = 0; i < 3u; ++i) {
    statement.Value(i, LoadF32(base, block + i * 4u))
        .Value(3u + i, LoadF32(base, block + 0x10u + i * 4u))
        .Value(6u + i, LoadF32(base, block + 0x20u + i * 4u));
  }
  statement.Value(9, LoadF32(base, block + 0x30u)).Value(10, LoadF32(base, block + 0x38u));
  State(statement);
}

REX_EXTERN(rex_CameraManager_Push) {
  const Depth depth;
  const uint32_t manager = ctx.r3.u32;
  __imp__rex_CameraManager_Push(ctx, base);
  const bool presented = manager == g_presented_manager.load(std::memory_order_relaxed);
  cam::Statement statement(cam::kPush, depth.at);
  statement.Manager(manager).Player(presented ? 1u : 0u, presented ? cam::kFlagDrawn : 0u);
  State(statement);
}

REX_EXTERN(rex_CameraManager_Snap) {
  const Depth depth;
  const uint32_t manager = ctx.r3.u32;
  cam::Statement begin(cam::kSnapBegin, depth.at);
  begin.Manager(manager);
  State(begin);
  __imp__rex_CameraManager_Snap(ctx, base);
  cam::Statement end(cam::kSnapEnd, depth.at);
  end.Manager(manager);
  State(end);
}

// ActivateCamera (manager, Lua camera): the wrappers it left at +0x2C (primary) and +0x3C (override).
REX_EXTERN(rex_CameraManager_ActivateCamera) {
  const Depth depth;
  const uint32_t manager = ctx.r3.u32;
  const uint32_t lua_camera = ctx.r4.u32;
  __imp__rex_CameraManager_ActivateCamera(ctx, base);
  cam::Statement statement(cam::kActivate, depth.at);
  statement.Manager(manager)
      .Word(lua_camera)
      .Bits(0, LoadU32(base, manager + 0x2Cu))
      .Bits(1, LoadU32(base, manager + 0x3Cu));
  State(statement);
}
