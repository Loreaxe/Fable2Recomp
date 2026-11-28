// refii — Fable 2's camera, stated (src/runtime/include/core/camera_statements.h).
//
// THE CONTRACT (TU1 addresses; the disc's are in Fable2/versions/Fable2/refii_config.toml under the
// same names). The camera the guest's readers see is one camera component's state (component 87 of
// the active wrapper's camera entity): the block builder sub_821D32C0 takes the entity's position
// (physics vt+0x40), its orientation (vt+0x44, rows Y and Z of the quaternion as forward and up), the
// component's current horizontal FOV (+0x18) and its cut byte (+0x5C). That state is written only by
//   - the component's setters, the Lua Camera natives (r3 the component, the argument in v1 / f1..f6 /
//     r4): MoveTo +0x90, MoveToCut (+ entity position), SetDirection / SetTarget (+0xA0 from the
//     direction against world Z), their Cut forms (+0xB0 too), SetAngles (+0xA0 from degrees),
//     SetFOV +0x1C (Cut +0x18), SetRoll +0x64 (Cut +0x60), SetDOF, the shake natives (+0xC0),
//     InterpolateFor (+0xDC/+0xE0, the factor +0xE4), SetCutThisFrame +0x5C, SetBlur +0xD8;
//   - the easing step sub_822A4A00 (r3 the component, r4 the active flag), run by the manager's
//     update for the primary (+0x24) and override (+0x34) entities: position lerp by +0xE4 plus the
//     shake's random offset (sub_822CBAA8), roll lerp, slerp of +0xB0 to +0xA0, the shake's random
//     turn of its rows (sub_8217B468), roll about forward, FOV lerp; then the factor's next value;
//   - the manager's settle pass sub_82482EB0, which sets factor 1, cut 1 and the counters 0 itself
//     before easing (a load, a level change).
// Each hook states its call with the arguments it was handed, and the step states the inputs it
// runs on (factor, shake, cut byte) and what its random shake added. Nothing here writes guest
// memory.
//
// THE REFERENCE. The getter sub_82265F90(manager) builds the block (memoised per millisecond) into
// manager+0x90; it is stated when it changed, with the entity it was built from, so the shell can
// prove its rebuild against what every reader sees. The push sub_821B8FD8 ends each manager update.

#include <array>
#include <cstdint>
#include <cstring>
#include <mutex>

#include <rex/ppc/context.h>
#include <rex/ppc/func.h>

#include "core/camera_statements.h"
#include "core/guest_build.h"
#include "core/guest_state.h"
#include "core/payload_bridge.h"

extern "C" REX_FUNC(__imp__rex_Camera_MoveTo);
extern "C" REX_FUNC(__imp__rex_Camera_MoveToCut);
extern "C" REX_FUNC(__imp__rex_Camera_SetDirection);
extern "C" REX_FUNC(__imp__rex_Camera_SetDirectionCut);
extern "C" REX_FUNC(__imp__rex_Camera_SetTarget);
extern "C" REX_FUNC(__imp__rex_Camera_SetTargetCut);
extern "C" REX_FUNC(__imp__rex_Camera_SetAngles);
extern "C" REX_FUNC(__imp__rex_Camera_SetAnglesCut);
extern "C" REX_FUNC(__imp__rex_Camera_SetFov);
extern "C" REX_FUNC(__imp__rex_Camera_SetFovCut);
extern "C" REX_FUNC(__imp__rex_Camera_SetRoll);
extern "C" REX_FUNC(__imp__rex_Camera_SetRollCut);
extern "C" REX_FUNC(__imp__rex_Camera_SetZoomLevel);
extern "C" REX_FUNC(__imp__rex_Camera_SetDof);
extern "C" REX_FUNC(__imp__rex_Camera_SetDofCut);
extern "C" REX_FUNC(__imp__rex_Camera_AddShake);
extern "C" REX_FUNC(__imp__rex_Camera_SetShake);
extern "C" REX_FUNC(__imp__rex_Camera_StopShake);
extern "C" REX_FUNC(__imp__rex_Camera_InterpolateFor);
extern "C" REX_FUNC(__imp__rex_Camera_SetCutThisFrame);
extern "C" REX_FUNC(__imp__rex_Camera_SetBlur);
extern "C" REX_FUNC(__imp__rex_Camera_SetBlurCut);
extern "C" REX_FUNC(__imp__rex_Camera_Ease);
extern "C" REX_FUNC(__imp__rex_Camera_ShakePosition);
extern "C" REX_FUNC(__imp__rex_Camera_ShakeRotation);
extern "C" REX_FUNC(__imp__rex_lhCameraManager_Block);
extern "C" REX_FUNC(__imp__rex_lhCameraManager_Push);
extern "C" REX_FUNC(__imp__rex_lhCameraManager_Snap);

namespace {

namespace cam = refii::camera;
using refii::vfs::LoadGuestU32;
using refii::vfs::LoadGuestU8;

/// The block's bytes (sub_821D32C0): position, up, forward (16 each), FOV, cut byte.
constexpr uint32_t kBlockBytes = 0x38u;
constexpr uint32_t kBlockAt = 0x90u;

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

float LoadF32(uint8_t* base, uint32_t at) {
  const uint32_t bits = LoadGuestU32(base, at);
  float value = 0.0f;
  std::memcpy(&value, &bits, sizeof(value));
  return value;
}

/// The hook depth the calling thread is at, held for one hook's span.
struct Depth {
  uint32_t at = refii_bridge_camera_enter();
  Depth() = default;
  Depth(const Depth&) = delete;
  Depth& operator=(const Depth&) = delete;
  ~Depth() { refii_bridge_camera_leave(); }
};

void State(const cam::Statement& statement) { refii_bridge_camera_statement(statement.words); }

/// A VMX argument's x, y, z (the recompiler keeps the lanes reversed: x is f32[3]).
cam::Statement& Vector(cam::Statement& statement, const PPCContext& ctx) {
  return statement.Value(0, ctx.v1.f32[3]).Value(1, ctx.v1.f32[2]).Value(2, ctx.v1.f32[1]);
}

cam::Statement Setter(uint32_t call, const Depth& depth, const PPCContext& ctx) {
  cam::Statement statement(call, depth.at);
  statement.Component(ctx.r3.u32);
  return statement;
}

/// The player-map key whose Player owns `manager` (game+0x9C: {key, Player, _} from +8 to +0xC;
/// Player+4 the holder, holder+4 the manager), 0 for none.
uint32_t KeyOf(uint8_t* base, uint32_t manager) {
  const uint32_t root = LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = Plausible(root) ? LoadGuestU32(base, root + 0xCu) : 0u;
  const uint32_t map = Plausible(game) ? LoadGuestU32(base, game + 0x9Cu) : 0u;
  if (!Plausible(map)) {
    return 0u;
  }
  const uint32_t begin = LoadGuestU32(base, map + 0x8u);
  const uint32_t end = LoadGuestU32(base, map + 0xCu);
  if (!Plausible(begin) || end <= begin || end - begin > 0x1000u) {
    return 0u;
  }
  for (uint32_t entry = begin; entry + 12u <= end; entry += 12u) {
    const uint32_t player = LoadGuestU32(base, entry + 4u);
    const uint32_t holder = Plausible(player) ? LoadGuestU32(base, player + 4u) : 0u;
    if (Plausible(holder) && LoadGuestU32(base, holder + 4u) == manager) {
      return LoadGuestU32(base, entry);
    }
  }
  return 0u;
}

/// The key this machine draws: 2 on an online joiner (net+0x34 and +0x36 both set, the test the
/// getter itself makes at 0x82265FB0), else 1.
uint32_t DrawnKey(uint8_t* base) {
  const uint32_t root = LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = Plausible(root) ? LoadGuestU32(base, root + 0xCu) : 0u;
  const uint32_t net = Plausible(game) ? LoadGuestU32(base, game + 0x8Cu) : 0u;
  return Plausible(net) && LoadGuestU8(base, net + 0x34u) != 0u && LoadGuestU8(base, net + 0x36u) != 0u
             ? 2u
             : 1u;
}

/// The entity the block is built from: the override wrapper (+0x3C) or the primary (+0x2C), its
/// smart pointer's node (+0xC), the node's entity (sub_821D32C0's first loads).
uint32_t BlockSource(uint8_t* base, uint32_t manager) {
  uint32_t wrapper = LoadGuestU32(base, manager + 0x3Cu);
  if (!Plausible(wrapper)) {
    wrapper = LoadGuestU32(base, manager + 0x2Cu);
  }
  const uint32_t node = Plausible(wrapper) ? LoadGuestU32(base, wrapper + 0xCu) : 0u;
  return Plausible(node) ? LoadGuestU32(base, node) : 0u;
}

std::mutex g_block_mutex;
struct LastBlock {
  uint32_t manager = 0;
  std::array<uint8_t, kBlockBytes> bytes{};
};
std::array<LastBlock, 4> g_last_blocks{};

/// True when `bytes` differs from what `manager` last stated (and remembers it).
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

void ReadVec3(uint8_t* base, uint32_t at, float* out) {
  for (uint32_t i = 0; i < 3u; ++i) {
    out[i] = LoadF32(base, at + i * 4u);
  }
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
    const double floats[6] = {ctx.f1.f64, ctx.f2.f64, ctx.f3.f64,        \
                              ctx.f4.f64, ctx.f5.f64, ctx.f6.f64};       \
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
REFII_CAMERA_FLOATS(rex_Camera_SetDof, cam::kSetDof, 6u)
REFII_CAMERA_FLOATS(rex_Camera_SetDofCut, cam::kSetDofCut, 6u)
REFII_CAMERA_FLOATS(rex_Camera_AddShake, cam::kAddShake, 1u)
REFII_CAMERA_FLOATS(rex_Camera_SetShake, cam::kSetShake, 1u)
REFII_CAMERA_FLOATS(rex_Camera_StopShake, cam::kStopShake, 0u)
REFII_CAMERA_WORD(rex_Camera_InterpolateFor, cam::kInterpolateFor, 0xFFFFFFFFu)
REFII_CAMERA_WORD(rex_Camera_SetCutThisFrame, cam::kSetCutThisFrame, 0xFFu)
REFII_CAMERA_FLOATS(rex_Camera_SetBlur, cam::kSetBlur, 1u)
REFII_CAMERA_FLOATS(rex_Camera_SetBlurCut, cam::kSetBlurCut, 1u)

// The easing step's inputs as it starts (the factor +0xE4, the shake +0xC0, which the manager
// raised by its own +0x54 just before the call, the cut byte +0x5C), its nested statements, its end.
REX_EXTERN(rex_Camera_Ease) {
  const Depth depth;
  const uint32_t comp = ctx.r3.u32;
  cam::Statement begin(cam::kEaseBegin, depth.at);
  begin.Component(comp).Entity(LoadGuestU32(base, comp + 4u)).Word(ctx.r4.u32 & 0xFFu);
  begin.Value(0, LoadF32(base, comp + 0xE4u))
      .Value(1, LoadF32(base, comp + 0xC0u))
      .Value(2, 0.0f)
      .Value(3, static_cast<float>(LoadGuestU8(base, comp + 0x5Cu)));
  State(begin);
  __imp__rex_Camera_Ease(ctx, base);
  cam::Statement end(cam::kEaseEnd, depth.at);
  end.Component(comp);
  State(end);
}

// sub_822CBAA8(comp, &position): adds GFFloatRandom(±0.16·shake) per axis while the shake is up.
REX_EXTERN(rex_Camera_ShakePosition) {
  const Depth depth;
  const uint32_t comp = ctx.r3.u32;
  const uint32_t at = ctx.r4.u32;
  float before[3];
  ReadVec3(base, at, before);
  __imp__rex_Camera_ShakePosition(ctx, base);
  float after[3];
  ReadVec3(base, at, after);
  if (std::memcmp(before, after, sizeof(before)) != 0) {
    cam::Statement statement(cam::kShakePosition, depth.at);
    statement.Component(comp);
    for (uint32_t i = 0; i < 3u; ++i) {
      statement.Value(i, after[i] - before[i]);
    }
    State(statement);
  }
}

// sub_8217B468(comp, rows): turns the up and forward rows sub_822B42C0 took of +0xB0 by a random
// axis and angle while the shake is up.
REX_EXTERN(rex_Camera_ShakeRotation) {
  const Depth depth;
  const uint32_t comp = ctx.r3.u32;
  const uint32_t rows = ctx.r4.u32;
  float before[6];
  ReadVec3(base, rows, before);
  ReadVec3(base, rows + 0x10u, before + 3);
  __imp__rex_Camera_ShakeRotation(ctx, base);
  float after[6];
  ReadVec3(base, rows, after);
  ReadVec3(base, rows + 0x10u, after + 3);
  if (std::memcmp(before, after, sizeof(before)) != 0) {
    cam::Statement statement(cam::kShakeRotation, depth.at);
    statement.Component(comp);
    for (uint32_t i = 0; i < 6u; ++i) {
      statement.Value(i, before[i]).Value(6u + i, after[i]);
    }
    State(statement);
  }
}

REX_EXTERN(rex_lhCameraManager_Block) {
  const Depth depth;
  const uint32_t manager = ctx.r3.u32;
  const uint32_t source = BlockSource(base, manager);
  __imp__rex_lhCameraManager_Block(ctx, base);
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
  statement.Manager(manager).Entity(source).Word(LoadGuestU8(base, block + 0x34u));
  for (uint32_t i = 0; i < 3u; ++i) {
    statement.Value(i, LoadF32(base, block + i * 4u))
        .Value(3u + i, LoadF32(base, block + 0x10u + i * 4u))
        .Value(6u + i, LoadF32(base, block + 0x20u + i * 4u));
  }
  statement.Value(9, LoadF32(base, block + 0x30u)).Value(10, 0.0f);
  State(statement);
}

REX_EXTERN(rex_lhCameraManager_Push) {
  const Depth depth;
  const uint32_t manager = ctx.r3.u32;
  __imp__rex_lhCameraManager_Push(ctx, base);
  const uint32_t key = KeyOf(base, manager);
  cam::Statement statement(cam::kPush, depth.at);
  statement.Manager(manager).Player(key, key != 0u && key == DrawnKey(base) ? cam::kFlagDrawn : 0u);
  State(statement);
}

/// A joiner's snap made before its own level change has loaded (Fable2/src/hooks/src/coop_hero_hooks.cpp
/// owns the rule): not run, and so not stated.
extern "C" int RefiiCoopSnapWaitsForLevel(uint8_t* base, uint32_t manager);

REX_EXTERN(rex_lhCameraManager_Snap) {
  if (RefiiCoopSnapWaitsForLevel(base, ctx.r3.u32) != 0) {
    return;
  }
  const Depth depth;
  const uint32_t manager = ctx.r3.u32;
  cam::Statement begin(cam::kSnapBegin, depth.at);
  begin.Manager(manager);
  State(begin);
  __imp__rex_lhCameraManager_Snap(ctx, base);
  cam::Statement end(cam::kSnapEnd, depth.at);
  end.Manager(manager);
  State(end);
}

/// ActivateCamera (manager, Lua camera) has run (Fable2/src/hooks/src/coop_hero_hooks.cpp owns the
/// hook): the wrappers it left at +0x2C (primary) and +0x3C (override).
extern "C" void RefiiCameraActivated(uint8_t* base, uint32_t manager, uint32_t lua_camera) {
  if (base == nullptr || !Plausible(manager)) {
    return;
  }
  cam::Statement statement(cam::kActivate, 0u);
  statement.Manager(manager)
      .Word(lua_camera)
      .Bits(0, LoadGuestU32(base, manager + 0x2Cu))
      .Bits(1, LoadGuestU32(base, manager + 0x3Cu));
  State(statement);
}

/// The manager's destructor is about to run (coop_hero_hooks.cpp owns the hook): the shell drops the
/// manager, and its last block is forgotten, so a manager built later at the same address states its
/// first block whatever it holds.
extern "C" void RefiiCameraManagerDestroyed(uint32_t manager) {
  if (!Plausible(manager)) {
    return;
  }
  {
    std::lock_guard<std::mutex> lock(g_block_mutex);
    for (LastBlock& last : g_last_blocks) {
      if (last.manager == manager) {
        last = LastBlock{};
      }
    }
  }
  cam::Statement statement(cam::kDestroy, 0u);
  statement.Manager(manager);
  State(statement);
}
