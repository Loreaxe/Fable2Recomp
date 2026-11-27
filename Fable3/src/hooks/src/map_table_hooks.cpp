// refii - Fable 3's map tables, stated to the host at the guest's own entry points: the world map
// (GUI.ToggleWorldMap, the game component at GUI+0x54, ctor sub_824EB6B0) and the town map
// (GUI.ToggleTownMapPrototype, sub_829B1CB8) each build a 3D table in a render scene of their own
// and PRESENT that scene in place of the world. Godot draws it (Fable3MapTable.cs) from what these
// hooks see the guest add, move, remove and present.
//
// Every address is the Fable 3 TU3 IDB ("disc" marks one TU3 lacks); each offset names its base.
//
// THE HOLDER H (224 bytes, ctor sub_82984370; the world map's at component+40, made by
// sub_824ED338 at 0x824ED45C, the town map's by sub_829B20F0 at 0x829B2120):
//   H+0   the scene: sub_82C84668(1, 1, 0) (0x829843DC, `stw r3, 0(r31)` 0x829843E0)
//   H+4   std::list of every render object id added through H (sub_822DE670 at 0x829846B8)
//   H+16  the camera's render object id, -1 until the camera's first statement (0x82984398)
//   H+32  the camera MESSAGE (vtable off_82001430, ctor sub_82C70F68 at 0x829843A4)
//   H+208 presented byte (0x829843D8; compared and stored by sub_82984588, 0x82984598 / 0x82984668)
// Its methods:
//   sub_82984678(H, msg) ADD: `sub_82256458(msg, *H, 1)` (0x829846A0), the id appended to H+4,
//       returned. Every map object comes through here: the static entities' sub_82986358
//       (0x829865C8 animated, 0x8298673C static), the characters' sub_8298A088 (0x8298A114), the
//       lights (sub_824EDBF0), the town map's (sub_829B25F0), and sub_829846D8's add.
//   sub_829846D8(H, int* id, msg) ADD OR UPDATE: `*id == -1` adds (0x82984724), else
//       `sub_821C20E0(*id, msg, 1)` re-sends the whole message (0x82984704). The cameras call it
//       as (H, H+16, H+32) after copying their 0x88-byte block into H+32 (sub_82262EE8): the world
//       map's camera ctor sub_8298F918 (0x8298FB84) and its update sub_8298FD60 (0x8298FEAC), the
//       town map's sub_829B23C8 (0x829B25DC) and sub_829B3A20 (disc 0x82952E04).
//   sub_82984588(H, on) PRESENT: on, `sub_822E2120(engine, t, *H, *(H+16))` (r5 = `lwz r28,0(r3)`
//       0x829845C0, r6 = `lwz r29,0x10(r3)` 0x829845BC, call 0x82984664) makes the engine draw H's
//       scene through H's camera (engine view +144 scene, +140 camera id, sub_822E2120
//       0x822E215C/0x822E2164); off, it hands back the world view's own (`**view`, `*(view+12)`,
//       0x82984620..0x82984648). The world map presents at open (sub_824ED338, disc 0x824CD8C4) and
//       un-presents at close (sub_824ED850, disc 0x824CDCEC); the town map at disc 0x829514B8.
//   sub_82984450(H) DESTROY: un-presents (0x82984464), then writes opcode 4 for every id of H+4
//       straight into the ring (0x82984498..0x82984500, not through sub_82C708C8).
// sub_82984740(H, id) removes one through sub_82C708C8(id, 1) (0x829847C4) — world_hooks.cpp's
// rex_RenderScene_RemoveObject, which hands the id here (MapTableObjectRemoved).
//
// THE CAMERA MESSAGE, the layout sub_822607C8 fills for the world view at V+32 (V+48 position
// 0x822608B0, V+64 orientation 0x822608A8, V+152 / V+156 the horizontal / vertical fov
// 0x822608B4 / 0x822608C8, V+88 aspect 0x822608E4) and sub_82C70F68 constructs (near 0.1 at +48,
// far 5000 at +52, 0x82C70FE4 / 0x82C70FE8): +16 position, +32 orientation quaternion (x, y, z, w),
// +48 near, +52 far, +56 aspect, +120 horizontal fov, +124 vertical fov (2 atan(tan(h/2) /
// aspect), sub_8298F918 0x8298FB10..0x8298FB34). The quaternion is the D3D one of the matrix whose
// rows are (forward x up, forward, up): sub_822727B0 builds that matrix (rows at -0x80, -0x70,
// -0x60: `vsubfp v30` the cross, forward, up, 0x82272818..0x82272840) and takes x = (m12 - m21)s,
// y = (m20 - m02)s, z = (m01 - m10)s (0x82272884..0x822728F0, lanes placed by the masks at
// unk_82065310/60/70). So the shell's forward is that quaternion's row 1, its up row 2.
//
// THE MESH MESSAGES. Both mesh classes lay out the same words: off_820E63F8 (static; built on
// the stack at 0x8298667C..0x82986738, and the FX mesh descriptor's own, 0x82624534) and
// off_820E6474 (animated; ctors sub_82C80120 and sub_82C7FFF0):
//   +4 flags (0x2000 IgnoreViewportCulling, 0x800 displacement: 0x82986398 / 0x829863A8)
//   +16 orientation quaternion (entity+128, 0x829866A4 / 0x82C80160)
//   +32 position (entity+112, 0x829866AC / 0x82C80168)
//   +48 uniform scale (entity+48, 0x82986680 / 0x82C80158)
//   +112 the model's resource id (*arg: entity+260, MeshID's number or sub_83165FC0 of MeshPath,
//        sub_82984D88 0x82984E08 / 0x82984E3C; `stw r31` 0x82986714, sub_82C80120 0x82C80198)
//   +132 the static class's record id (entity+256, the low word of `std r29` 0x82986738)
//
// THE COMMANDS. A map entity's update (sub_829856E0) moves its object with sub_821F4630(id, cmd,
// 1), only for what changed since it last sent (it keeps the sent copy, 0x829858A4 / 0x829858E0
// / 0x82985970): each command class answers its id from slot 1 and serialises from slot 2.
//   off_82000D6C id 3 (`li r3, 3` 0x82A7ACE0): +4 +8 +12 position x y z, +16 orientation
//       quaternion (0x82985868..0x8298589C; serialiser sub_822401D8 copies +4..+12 and +16..+31)
//   off_820A393C id 6 (0x82372490): +4 uniform scale (0x829858C0; sub_82C7F170)
//   off_820A3904 id 15 (0x82C32EE8): +16 tint r g b a (entity+80, 0x8298590C..0x82985930; sub_82C7F0B0)
//   off_820A38F8 id 14 (0x82C710F8): +4 alpha, the tint's a (0x82985940; sub_82C7EF58)
// The render ids are one pool across rings (sub_82CCE0F8 refills a ring's cache from the shared
// free list and counter), so an id names one object while it lives.

#include "map_table_hooks.h"

#include <atomic>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <unordered_set>

#include <rex/ppc.h>

#include "core/payload_bridge.h"
#include "gui_statements.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_MapScene_Add);
REX_EXTERN(__imp__rex_MapScene_AddOrUpdate);
REX_EXTERN(__imp__rex_MapScene_Present);
REX_EXTERN(__imp__rex_MapScene_Destroy);
REX_EXTERN(__imp__rex_RenderObject_Command);

namespace {

const refii::guest::Addr kStaticMeshMessage{0x820E63F8u};
const refii::guest::Addr kAnimatedMeshMessage{0x820E6474u};
constexpr uint32_t kTransformCommand = 0x82000D6Cu;  // classified by ToTu3
constexpr uint32_t kScaleCommand = 0x820A393Cu;  // classified by ToTu3
constexpr uint32_t kTintCommand = 0x820A3904u;  // classified by ToTu3
constexpr uint32_t kAlphaCommand = 0x820A38F8u;  // classified by ToTu3

constexpr uint32_t kHolderCamera = 16u;
constexpr uint32_t kHolderCameraMessage = 32u;

/// Ids at or past this are not tracked (the twin table is a few thousand in a region).
constexpr uint32_t kIds = 65536u;
/// The holder that owns each render id, 0 for none.
std::atomic<uint32_t> g_owner[kIds];

// Row words (core/payload_bridge.h, REFII_SCENE_OBJECT_WORDS).
constexpr uint32_t kRowQuaternion = 4u;
constexpr uint32_t kRowPosition = 8u;
constexpr uint32_t kRowScale = 11u;
constexpr uint32_t kRowTint = 12u;
constexpr uint32_t kRowAlpha = 16u;
constexpr uint32_t kOne = 0x3F800000u;

uint32_t LoadGuestU32(const uint8_t* base, uint32_t address) {
  uint32_t value = 0;
  std::memcpy(&value, base + address, sizeof(value));
  return __builtin_bswap32(value);
}

bool Plausible(uint32_t address) { return address >= 0x10000u && address < 0xC0000000u; }

bool IsMeshMessage(const uint8_t* base, uint32_t message) {
  if (!Plausible(message)) {
    return false;
  }
  const uint32_t vtable = LoadGuestU32(base, message);
  return vtable == kStaticMeshMessage || vtable == kAnimatedMeshMessage;
}

/// A mesh message's row: model, flags, record, class, its transform, a white tint and full alpha
/// (the commands that follow move them).
void MeshRow(const uint8_t* base, uint32_t message, uint32_t* words) {
  const uint32_t vtable = LoadGuestU32(base, message);
  words[0] = LoadGuestU32(base, message + 112u);
  words[1] = LoadGuestU32(base, message + 4u);
  words[2] = vtable == kStaticMeshMessage ? LoadGuestU32(base, message + 132u) : 0u;
  words[3] = vtable;
  for (uint32_t i = 0u; i < 4u; ++i) {
    words[kRowQuaternion + i] = LoadGuestU32(base, message + 16u + i * 4u);
  }
  for (uint32_t i = 0u; i < 3u; ++i) {
    words[kRowPosition + i] = LoadGuestU32(base, message + 32u + i * 4u);
  }
  words[kRowScale] = LoadGuestU32(base, message + 48u);
  for (uint32_t i = 0u; i < 4u; ++i) {
    words[kRowTint + i] = kOne;
  }
  words[kRowAlpha] = kOne;
}

void StateMesh(const uint8_t* base, uint32_t holder, uint32_t id, uint32_t message) {
  if (id >= kIds) {
    return;
  }
  uint32_t words[REFII_SCENE_OBJECT_WORDS];
  MeshRow(base, message, words);
  g_owner[id].store(holder, std::memory_order_release);
  refii_bridge_scene_object_stated(holder, id, words);
}

/// The transform part of a re-sent mesh message, its tint and alpha kept.
void RestateMeshTransform(const uint8_t* base, uint32_t id, uint32_t message) {
  uint32_t words[REFII_SCENE_OBJECT_WORDS];
  MeshRow(base, message, words);
  refii_bridge_scene_object_patch(id, 0u, words, kRowTint);
}

/// THE MESSAGES THE TABLE DOES NOT DRAW: anything added through H that is not a mesh message —
/// the entity sprites (CWorldMapEntitySprite_vf2 0x8298B618 -> sub_829846D8, message from
/// sub_8298B000) and the lights / sun (Lua_WorldMap_Lights 0x824EDC84.., CWorldMapEntityLight_vf2
/// 0x8298A734 -> sub_82984678). Their layouts are not read yet; each class is named once
/// (`@MapTable.Unstated(holder, id, vtable)`) so a run lists what the table still lacks.
std::mutex g_unstated_mutex;
std::unordered_set<uint32_t> g_unstated;

void NameUnstated(const uint8_t* base, uint32_t holder, uint32_t id, uint32_t message) {
  if (!Plausible(message)) {
    return;
  }
  const uint32_t vtable = LoadGuestU32(base, message);
  {
    std::lock_guard<std::mutex> lock(g_unstated_mutex);
    if (g_unstated.size() > 64u || !g_unstated.insert(vtable).second) {
      return;
    }
  }
  refii::fable3::Statement("@MapTable.Unstated")
      .Add(refii::fable3::Text(refii::fable3::Hex(holder)))
      .Add(refii::fable3::Number(static_cast<float>(static_cast<int32_t>(id))))
      .Add(refii::fable3::Text(refii::fable3::Hex(vtable)))
      .Post();
}

}  // namespace

namespace refii::fable3 {

void MapTableObjectRemoved(uint32_t id) {
  if (id < kIds && g_owner[id].exchange(0u, std::memory_order_acq_rel) != 0u) {
    refii_bridge_scene_object_removed(id);
  }
}

}  // namespace refii::fable3

REX_EXTERN(rex_MapScene_Add) {
  const uint32_t holder = ctx.r3.u32;
  const uint32_t message = ctx.r4.u32;
  const bool mesh = IsMeshMessage(base, message);
  __imp__rex_MapScene_Add(ctx, base);
  const uint32_t id = ctx.r3.u32;
  if (mesh && id != 0xFFFFFFFFu) {
    StateMesh(base, holder, id, message);
  } else if (!mesh && base != nullptr) {
    NameUnstated(base, holder, id, message);
  }
}

REX_EXTERN(rex_MapScene_AddOrUpdate) {
  const uint32_t holder = ctx.r3.u32;
  const uint32_t id_slot = ctx.r4.u32;
  const uint32_t message = ctx.r5.u32;
  const uint32_t before = Plausible(id_slot) ? LoadGuestU32(base, id_slot) : 0xFFFFFFFFu;
  __imp__rex_MapScene_AddOrUpdate(ctx, base);
  if (!Plausible(holder) || !Plausible(message)) {
    return;
  }

  if (id_slot == holder + kHolderCamera && message == holder + kHolderCameraMessage) {
    uint32_t words[REFII_SCENE_CAMERA_WORDS];
    for (uint32_t i = 0u; i < 3u; ++i) {
      words[i] = LoadGuestU32(base, message + 16u + i * 4u);
    }
    for (uint32_t i = 0u; i < 4u; ++i) {
      words[3u + i] = LoadGuestU32(base, message + 32u + i * 4u);
    }
    words[7] = LoadGuestU32(base, message + 124u);
    words[8] = LoadGuestU32(base, message + 48u);
    words[9] = LoadGuestU32(base, message + 52u);
    refii_bridge_scene_camera_stated(holder, words);
    return;
  }

  if (!IsMeshMessage(base, message)) {
    NameUnstated(base, holder, before, message);
  }

  // An add went through sub_82984678 (rex_MapScene_Add states it); an update re-sent the message.
  if (before != 0xFFFFFFFFu && before < kIds && IsMeshMessage(base, message) &&
      g_owner[before].load(std::memory_order_acquire) == holder) {
    RestateMeshTransform(base, before, message);
  }
}

/// (holder, on): presented as the call states it, r4's low byte (`clrlwi r11,r4,24` TU3 0x8298459C;
/// the callee compares it with H+208 only to skip a repeat, 0x82984598..0x829845AC).
REX_EXTERN(rex_MapScene_Present) {
  const uint32_t holder = ctx.r3.u32;
  const int on = (ctx.r4.u32 & 0xFFu) != 0u ? 1 : 0;
  __imp__rex_MapScene_Present(ctx, base);
  if (Plausible(holder)) {
    refii_bridge_scene_presented(holder, on);
  }
}

REX_EXTERN(rex_MapScene_Destroy) {
  const uint32_t holder = ctx.r3.u32;
  __imp__rex_MapScene_Destroy(ctx, base);
  if (holder == 0u) {
    return;
  }
  for (uint32_t id = 0u; id < kIds; ++id) {
    uint32_t owner = holder;
    g_owner[id].compare_exchange_strong(owner, 0u, std::memory_order_acq_rel);
  }
  refii_bridge_scene_holder_dropped(holder);
}

REX_EXTERN(rex_RenderObject_Command) {
  const uint32_t id = ctx.r3.u32;
  const uint32_t command = ctx.r4.u32;
  const bool owned = id < kIds && g_owner[id].load(std::memory_order_acquire) != 0u && Plausible(command);
  __imp__rex_RenderObject_Command(ctx, base);
  if (!owned) {
    return;
  }

  uint32_t words[4];
  switch (refii::guest::ToTu3(LoadGuestU32(base, command))) {
    case kTransformCommand:
      for (uint32_t i = 0u; i < 4u; ++i) {
        words[i] = LoadGuestU32(base, command + 16u + i * 4u);
      }
      refii_bridge_scene_object_patch(id, kRowQuaternion, words, 4u);
      for (uint32_t i = 0u; i < 3u; ++i) {
        words[i] = LoadGuestU32(base, command + 4u + i * 4u);
      }
      refii_bridge_scene_object_patch(id, kRowPosition, words, 3u);
      break;
    case kScaleCommand:
      words[0] = LoadGuestU32(base, command + 4u);
      refii_bridge_scene_object_patch(id, kRowScale, words, 1u);
      break;
    case kTintCommand:
      for (uint32_t i = 0u; i < 4u; ++i) {
        words[i] = LoadGuestU32(base, command + 16u + i * 4u);
      }
      refii_bridge_scene_object_patch(id, kRowTint, words, 4u);
      break;
    case kAlphaCommand:
      words[0] = LoadGuestU32(base, command + 4u);
      refii_bridge_scene_object_patch(id, kRowAlpha, words, 1u);
      break;
    default:
      break;
  }
}
