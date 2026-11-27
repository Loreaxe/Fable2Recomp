// refii — Fable 3: each split couch half's offscreen hero / partner marker is tested against that half's
// camera and switched by that half's camera; an online machine's markers follow its own camera only.
//
// Addresses are TU3 / disc. The view setter and the native are the same instruction for instruction; the
// record update is relaid on disc (the registers differ, named below).
//
// THE RECORD UPDATE. The HUD update (rex_Hud_Update, sub_82258E60 / sub_822597B0) runs, per 12-byte record
// of *(manager+0x50), `mr r3,r30; lbz r4,0x10(r29); bl sub_82196548` 0x82259114..30 / `bl sub_82196410`
// 0x82259A64..80: r3 the record (+0 shown, +4 type: 52 the hero marker, art icon_player1, 53 the partner's,
// +8 the icon), r4 the GUI.SetShouldDrawPlayerIcons switch (`mr r29,r4` 0x8219655C / 0x82196424). Type 52's
// target is this machine's player one's hero (`bl sub_8239F8B0` 0x821965C4: sub_823BB518 on
// *(*(*(game+0x1C)+4)+4)), type 53's the other hero (`bl sub_821FB318; cmplw cr6,r30,r3; bne` 0x821965F0..F8,
// then `bl sub_823BB4C0`). With the switch off every test is skipped and the record hidden (`clrlwi
// r11,r29,24; cmplwi; beq` 0x82196684..90 / 0x8219654C). Else the record is shown only when its target is
// not seen by THE SCENE CAMERA: root vf 0x14 (CMainGameComponent's slot 5, sub_82535B70 / sub_825101C8:
// `*(game+0x10)`), `lwz r9,4(r3); addic. r31,r9,0x20` 0x82196A30..34 (disc `addic. r29,r9,0x20`
// 0x82196838..3C) — the scene's view node +0x20 — handed to the projection test (`mr r3,r31; bl
// sub_82C74ED0` 0x82196A4C..54 / `mr r3,r29; bl sub_82C3FB68` 0x82196854..5C: off screen → `li r27,1` /
// `li r26,1` shown), and on screen the ray from camera+0x10 (`lvx128 v13,r31,r10` 0x82196AA8 / `lvx128
// v13,r29,r10` 0x82196984) to the target shows it when blocked. The camera register is not read after it.
//
// THE SCENE CAMERA IS PLAYER ONE'S. GameLoop_HighFrequencyUpdate (sub_82255CF0 / sub_82256AC0) builds the
// block of "the" camera (`lwz r30,0x40(r31); bl sub_82208EA0; bl sub_821CB2E0` 0x82255D2C..50 /
// 0x82256AFC..B20), blends it (Camera_Blend) and sets the node from it (`lwz r30,0x10(r31)` … `lwz r3,4(r30);
// bl View_SetRenderCamera` 0x82255D64..74 / 0x82256B34..44). So on split screen a marker drawn in player
// two's half existed only while its hero was hidden from PLAYER ONE's camera.
//
// THE SETTER, View_SetRenderCamera (sub_822607C8 / sub_82261CB8; r3 the node, r4 a 64-byte block: +0
// position, +0x30 the field of view, +0x34 the cut byte). It does nothing while the node has no render
// camera (`lwz r11,0xC(r3); cmpwi cr6,r11,-1; beq` 0x822607DC..EC); else it computes the node's camera
// (position +0x30 `stvx128 v13,r31,r4` 0x822608B0, orientation +0x40, the fields of view +0x98 / +0x9C,
// viewport +0x5C..+0x68) and posts it to that render camera (`li r5,1; lwz r3,0xC(r31); mr r4,r28; bl
// RenderObject_PostMessage` 0x82260A40..4C). Its only other writes are the node's and byte_832DE95E, which
// it sets from root vf 0x18 alone (0x822609C0..0A0C), so a second call in the same tick writes the same
// value. The post (0x821C20E0 / 0x821C2120) copies the message into command stream 1, and the stream's
// command 5 hands it to sub_822071F0 (0x822A0F40), which drops an id below 0 or past the render-object
// table (`a1 < 0 || a1 >= count || !table[a1]` → no delivery).
//
// PLAYER TWO'S SCENE CAMERA. While split, a second view node built by the guest's own constructor
// (sub_824F19F8 / sub_824D1BB0: ids +0..+0x10 = -1, camera descriptors +0x20 / +0xB0, near 0.1, far 5000)
// is set after each set of the world's node by the guest's own setter from player two's block (sub_821CB2E0
// / sub_821CB288 on player two's holder: `lwz r3,4(r30); bl sub_8224F510` 0x821CB338..44 / 0x821CB2DC..E8,
// the block of the holder's own manager). Its render camera id is -2: no render object has it, so the setter
// computes and its post is dropped. Nothing reads the node but this file. In the record update for the hero
// marker (type 52, drawn in player two's half on split: Engine_HudSpriteView.PartnerMarkerHalf) the camera
// register is that node's camera (the mid-asm hook below, after the `beq` on its cr0); the partner marker
// (53, player one's half) keeps the world's. This swaps no guest state.
//
// THE SWITCH. Player two's camera states its own GUI.SetShouldDrawPlayerIcons as stated value 25
// (Fable3CoreLua.cs; 1 off, 2 on, 0 never stated); while split the hero marker's r4 is that value (not off).
//
// ONLINE. The native (Lua_GUI_SetShouldDrawPlayerIcons 0x8254BD70 / 0x8251C0F0) stores the switch (`stb
// r31,0x10(r8)` 0x8254BDD8 / 0x8251C154) and, on the session host (net+0x1C && net+0x1D), sends it (`bl sub_82382E20`
// 0x8254BE30 / `bl sub_8237A2C0` 0x8251C1AC); the joiner stores what it receives
// (lhNetManager_ProcessInboundGamePacket 0x8237ADF4..F8 / 0x82372BA4..A8). A CAMERA's statement (inside
// SACCamera:Activate / :Deactivate, stringandcagecamera.lua 763 / 784, marked by the core chunk through
// Refii._camera_player_icons) is this machine's alone: its send is skipped (0x8254BE10 → 0x8254BE34 /
// 0x8251C18C → 0x8251C1B0). A quest's (qc140_coronation.lua 737 / 768) is still sent.

#include <atomic>
#include <cstdint>
#include <cstring>

#include <rex/logging.h>
#include <rex/ppc.h>
#include <rex/ppc/context.h>
#include <rex/system/kernel_state.h>
#include <rex/system/xmemory.h>

#include "coop_camera.h"
#include "core/payload_bridge.h"
#include "gui_statements.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_View_SetRenderCamera);
REX_EXTERN(__imp__rex_Hud_PlayerIconRecord);
REX_EXTERN(rex_Camera_ScreenBlock);
REX_EXTERN(rex_SceneView_Construct);
REX_EXTERN(rex_lhNew);

namespace {

using refii::fable3::LoadGuestU32;
using refii::fable3::Plausible;

const refii::guest::Addr kGame{0x83597034u};

/// The view node (sub_824F19F8's 0x140 bytes), its camera +0x20, its render camera id +0xC.
constexpr uint32_t kNodeBytes = 0x140u;
constexpr uint32_t kNodeCamera = 0x20u;
constexpr uint32_t kNodeRenderCamera = 0xCu;
/// Player two's block lives after the node; 64 bytes (sub_821CB2E0's).
constexpr uint32_t kBlockAt = kNodeBytes;
constexpr uint32_t kBlockBytes = 0x40u;
/// No render object has a negative id: the setter computes, its post is dropped (sub_822071F0 `a1 < 0`).
constexpr uint32_t kNoRenderCamera = 0xFFFFFFFEu;
/// The record's type (+4): 52 the hero marker, the one drawn in player two's half on split.
constexpr uint32_t kHeroMarker = 52u;
/// Player two's camera's own GUI.SetShouldDrawPlayerIcons (payload_bridge.h): 1 off, 2 on, 0 never stated.
constexpr uint32_t kStatedPartnerIcons = 25u;

std::atomic<uint32_t> g_node{0u};
std::atomic<bool> g_live{false};
std::atomic<int32_t> g_from_camera{0};

std::atomic<bool> g_said_camera{false};
std::atomic<bool> g_said_switch{false};
std::atomic<bool> g_said_here{false};

void Once(std::atomic<bool>& said, const char* what) {
  if (!said.exchange(true, std::memory_order_relaxed)) {
    REXLOG_INFO("Refii couch markers: {}", what);
  }
}

uint8_t* GuestBase() {
  rex::system::KernelState* state = rex::system::kernel_state();
  rex::memory::Memory* memory = state != nullptr ? state->memory() : nullptr;
  return memory != nullptr ? memory->virtual_membase() : nullptr;
}

void StoreU32(uint8_t* base, uint32_t at, uint32_t value) {
  const uint32_t be = __builtin_bswap32(value);
  std::memcpy(base + at, &be, sizeof(be));
}

/// A copy of `ctx` with the stack pointer moved below the caller's frame.
PPCContext Frame(const PPCContext& ctx) {
  PPCContext work = ctx;
  work.r1.u32 = (ctx.r1.u32 - 0x100u) & ~0xFu;
  return work;
}

/// The world's view node: *(*(game+0x10)+4), game = *(root+0xC).
uint32_t WorldNode(const uint8_t* base) {
  const uint32_t root = LoadGuestU32(base, kGame);
  const uint32_t game = Plausible(root) ? LoadGuestU32(base, root + 0xCu) : 0u;
  const uint32_t scene = Plausible(game) ? LoadGuestU32(base, game + 0x10u) : 0u;
  return Plausible(scene) ? LoadGuestU32(base, scene + 4u) : 0u;
}

/// Player two's node, built once by the guest's constructor on the guest heap (the game's operator new),
/// with no render camera.
uint32_t PartnerNode(const PPCContext& ctx, uint8_t* base) {
  uint32_t node = g_node.load(std::memory_order_acquire);
  if (node != 0u) {
    return node;
  }
  PPCContext call = Frame(ctx);
  call.r3.u32 = kBlockAt + kBlockBytes + 16u;
  rex_lhNew(call, base);
  if (!Plausible(call.r3.u32)) {
    return 0u;
  }
  node = (call.r3.u32 + 15u) & ~15u;
  std::memset(base + node, 0, kBlockAt + kBlockBytes);
  PPCContext construct = Frame(ctx);
  construct.r3.u32 = node;
  rex_SceneView_Construct(construct, base);
  StoreU32(base, node + kNodeRenderCamera, kNoRenderCamera);
  g_node.store(node, std::memory_order_release);
  return node;
}

}  // namespace

/// Refii._camera_player_icons(on): the core chunk brackets a camera's GUI.SetShouldDrawPlayerIcons.
extern "C" void RefiiF3PlayerIconsFromCamera(int32_t on) {
  g_from_camera.fetch_add(on != 0 ? 1 : -1, std::memory_order_relaxed);
}

// The view node's camera (r3 the node, r4 the block). After the world's: player two's node, from player two's
// block, while split.
REX_EXTERN(rex_View_SetRenderCamera) {
  const PPCContext entry = ctx;
  __imp__rex_View_SetRenderCamera(ctx, base);
  if (!Plausible(entry.r3.u32) || entry.r3.u32 != WorldNode(base)) {
    return;
  }
  const uint32_t holder =
      refii::fable3::coopcam::SplitInEffect(base) ? refii::fable3::coopcam::PartnerHolder(base) : 0u;
  const uint32_t node = holder != 0u ? PartnerNode(entry, base) : 0u;
  if (node == 0u) {
    g_live.store(false, std::memory_order_release);
    return;
  }
  PPCContext block = Frame(entry);
  block.r3.u32 = node + kBlockAt;
  block.r4.u32 = holder;
  rex_Camera_ScreenBlock(block, base);
  PPCContext set = Frame(entry);
  set.r3.u32 = node;
  set.r4.u32 = node + kBlockAt;
  __imp__rex_View_SetRenderCamera(set, base);
  g_live.store(true, std::memory_order_release);
  Once(g_said_camera, "player two's half's hero marker is tested against player two's camera while split");
}

// One record's update (r3 the record, r4 the switch). While split the hero marker takes player two's camera's
// switch.
REX_EXTERN(rex_Hud_PlayerIconRecord) {
  const uint32_t record = ctx.r3.u32;
  if (Plausible(record) && LoadGuestU32(base, record + 4u) == kHeroMarker && g_live.load(std::memory_order_acquire) &&
      refii::fable3::coopcam::SplitInEffect(base)) {
    ctx.r4.u64 = refii_bridge_stated_value_now(kStatedPartnerIcons) != 1u ? 1u : 0u;
    Once(g_said_switch, "player two's half's hero marker follows player two's camera's player icons");
  }
  __imp__rex_Hud_PlayerIconRecord(ctx, base);
}

// After the scene camera's `beq` (TU3 0x82196A3C, r26 the record, r31 the camera / disc 0x82196844, r25,
// r29): the hero marker's camera is player two's node's while split.
void RefiiF3PlayerIconCamera(PPCRegister& record, PPCRegister& camera) {
  if (!g_live.load(std::memory_order_acquire)) {
    return;
  }
  const uint8_t* base = GuestBase();
  const uint32_t node = g_node.load(std::memory_order_acquire);
  if (base == nullptr || node == 0u || !Plausible(record.u32) || LoadGuestU32(base, record.u32 + 4u) != kHeroMarker) {
    return;
  }
  camera.u64 = node + kNodeCamera;
}

// The native's send to the session's partner (0x8254BE10 / 0x8251C18C): skipped (to 0x8254BE34 / 0x8251C1B0)
// for a camera's statement.
bool RefiiF3PlayerIconsStayHere() {
  if (g_from_camera.load(std::memory_order_relaxed) <= 0) {
    return false;
  }
  Once(g_said_here, "a camera's player icons stay on its own machine (not sent to the partner)");
  return true;
}
