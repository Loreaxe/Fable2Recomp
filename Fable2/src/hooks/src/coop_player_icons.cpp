// refii — Fable 2: each split-screen half's offscreen hero / henchman marker is tested against that half's
// camera and switched by that half's camera; an online machine's markers follow its own camera only.
//
// Addresses are TU1 / disc; the bodies are the same instruction for instruction.
//
// THE RECORD UPDATE. The element manager's tick (sub_822939B0 / sub_82293568) runs, per 16-byte record of
// the vector at *(manager+0x64) (+4 begin, +8 end), `mr r3,r30; lbz r4,0x10(r29); bl sub_822B2F38`
// 0x82293C38..40 / `bl sub_822B2D70` 0x822937F8: r3 the record (+0 shown, +8 type: 52 the hero marker, 53
// the henchman's, +0xC the icon), r4 the GUI.SetShouldDrawPlayerIcons switch. With r4 0 it skips every
// test (`clrlwi r11,r4,24; cmplwi; beq` 0x822B2F78..88 / 0x822B2DB0..C0) and the record is hidden. Else
// the record is shown only when its target is not seen by THE SCENE CAMERA: root vf 0x14 (lhInGameState's
// slot 5, vtable 0x82001CCC, sub_821C9648: `*(game+0x6C)`), then `lwz r9,4(r3); addic. r30,r9,0x20`
// 0x822B32D8..DC / 0x822B3110..14 — the scene's view node +0x20 — handed to the projection test
// (`mr r3,r30; bl sub_82A3F7D0` 0x822B3300..08 / `bl sub_82A39D88` 0x822B3138..40: off the viewport at
// camera+0x44..+0x50 → `li r25,1` shown), and on screen the ray from camera+0x10 (`lvx128 v13,r30,r10`
// 0x822B3470 / 0x822B32A8) to the target shows it when blocked. r30 is not read after the ray.
//
// THE SCENE CAMERA IS PLAYER ONE'S. lhGameLoop_Tick's sub_82278DD0 / sub_82278C90 builds the block of
// "the" camera (`lwz r30,0x9C(r31); bl sub_821F87C8; bl lhCamera_ScreenBlock` 0x82278E04..28 /
// 0x82278CC4..E8) and sets the node from it (`lwz r30,0x6C(r31)` … `lwz r3,4(r30); bl sub_82293820`
// 0x82278E3C..4C / `bl sub_822933D8` 0x82278D0C). So on split screen the hero marker GameUi pins in player
// two's half (hero one, shown to player two) existed only while hero one was hidden from PLAYER ONE's
// camera — almost never.
//
// THE SETTER, sub_82293820 / sub_822933D8 (r3 the node, r4 a 64-byte block: +0 position, +0x30 the field
// of view, +0x34 the cut byte). It does nothing while the node has no render camera (`lwz r11,8(r31);
// cmpwi cr6,r11,-1; beq` 0x8229383C..44); else it computes the node's camera — position +0x30 (`li
// r10,0x30; stvx128 v0,r31,r10` 0x822938FC / 0x82293934), orientation +0x40, the fields of view +0x58 /
// +0x5C and aspect +0x60 under g_WidescreenMode, viewport +0x64..+0x70 from the screen size — and posts
// it to that render camera (`lwz r3,8(r31); addi r4,r31,0x20; li r5,1; bl RenderObject_PostMessage_p`
// 0x82293918..9C). The post (0x82261AC0) copies the message into command stream 1 (its vf 3,
// sub_822B7DE8: Reserve 128 and copy, nothing else), and the stream's command 5 hands it to
// sub_821F7C40 (Render_ProcessCommandStream 0x822E04E0), which drops an id below 0 or past the
// render-object table (`v3 < 0 || v3 >= count || !table[v3]` → no delivery) and then destroys the copy.
//
// PLAYER TWO'S SCENE CAMERA. While split, a second view node built by the guest's own constructor
// (sub_823893C0 / sub_8238DE58: ids +0..+0xC = -1, two camera descriptors +0x20 / +0xA0 with near 0.1, far
// 5000) is set after each set of the world's node by the guest's own setter from player two's block —
// lhCamera_ScreenBlock run for player two's camera holder (RefiiCoopSplitPartnerCameraHolder), the call the
// second listener and the particles' second view make. Its render camera id is -2: no render object has it,
// so the setter computes and its post is dropped by the guest's own deliverer. Nothing reads the node but
// this file. In the record update for the hero marker (type 52), r30 is that node's camera (the mid-asm
// hook below, after the `beq` on r30's cr0 0x822B32E0 / 0x822B3118); the henchman marker (53, player one's
// half) keeps the world's. This swaps no guest state: the guest's test runs once per record as shipped,
// asked of the camera of the half the record is drawn in.
//
// THE SWITCH. Player two's camera states its own GUI.SetShouldDrawPlayerIcons as stated value 25 (the core
// camera chunk, CoopHeroLua.cs; 1 off, 2 on, 0 never stated); while split the hero marker's r4 is that
// value (not off), the henchman marker's the game's own (player one's camera's, and every quest's).
//
// ONLINE. The native (Lua_GUI_SetShouldDrawPlayerIcons 0x82306230 / 0x82907E58) stores the switch
// (`stb r3,0x10(r8)` 0x82306288 / 0x82907EB0) and, on the session host (net+0x34 && net+0x35), sends it
// (`bl sub_8255C0D8` 0x823062DC / `bl sub_82526B78` 0x82907F04, message off_820A3D1C / off_820A4754); the
// joiner stores what it receives (lhNetManager_ProcessInboundGamePacket case 252, `stb r10,0x10(r8)`
// 0x82331B90 / 0x82314568). So the host's camera turned the joiner's markers on and off. A CAMERA's
// statement (inside SACCamera:Activate / :Deactivate, marked by the core chunk through
// Refii._camera_player_icons) is this machine's alone: its send is skipped (0x823062C0 → 0x823062E0 /
// 0x82907EE8 → 0x82907F08). A quest's (QO880_Vision's Init / OnExit) is still sent. The joiner's own camera
// states its own switch through the same native, which stores it before the host test.
//
// SINGLE PLAYER AND THE SHARED SCREEN are the shipped game: no split, so no second node, no substitution,
// no switch change; the send skip acts only on a host.

#include <atomic>
#include <cstdint>
#include <cstring>

#include <rex/logging.h>
#include <rex/ppc/context.h>
#include <rex/ppc/func.h>

#include "core/guest_build.h"
#include "core/guest_state.h"
#include "core/payload_bridge.h"

extern "C" REX_FUNC(__imp__rex_lhSceneView_SetCamera);
extern "C" REX_FUNC(__imp__rex_lhPlayerIcons_UpdateRecord);
extern "C" REX_FUNC(__imp__rex_lhCamera_ScreenBlock);
extern "C" REX_FUNC(rex_lhSceneView_Construct);

extern "C" uint8_t* RefiiGuestBase(void);
extern "C" uint32_t RefiiCoopSplitPartnerCameraHolder(uint8_t* base);

namespace {

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

/// The view node (sub_823893C0's 0x120 bytes), its camera +0x20, its render camera id +8.
constexpr uint32_t kNodeBytes = 0x120u;
constexpr uint32_t kNodeCamera = 0x20u;
constexpr uint32_t kNodeRenderCamera = 8u;
/// Player two's block lives after the node; 64 bytes (lhCamera_ScreenBlock's).
constexpr uint32_t kBlockAt = kNodeBytes;
constexpr uint32_t kBlockBytes = 0x40u;
/// No render object has a negative id: the setter computes, its post is dropped (sub_821F7C40 `v3 < 0`).
constexpr uint32_t kNoRenderCamera = 0xFFFFFFFEu;
/// The record's type (+8): 52 the hero marker, the one pinned in player two's half on split.
constexpr uint32_t kHeroMarker = 52u;
/// Player two's camera's own GUI.SetShouldDrawPlayerIcons (payload_bridge.h): 1 off, 2 on, 0 never stated.
constexpr uint32_t kStatedPartnerIcons = 25u;

/// Player two's view node (guest, 16-aligned), 0 until built; live while it holds this tick's camera.
std::atomic<uint32_t> g_node{0u};
std::atomic<bool> g_live{false};
/// Inside a camera's GUI.SetShouldDrawPlayerIcons (the core chunk's Refii._camera_player_icons).
std::atomic<int> g_from_camera{0};

std::atomic<bool> g_said_camera{false};
std::atomic<bool> g_said_switch{false};
std::atomic<bool> g_said_here{false};

void Once(std::atomic<bool>& said, const char* what) {
  if (!said.exchange(true, std::memory_order_relaxed)) {
    REXLOG_INFO("Refii co-op markers: {}", what);
  }
}

/// A copy of `ctx` with the stack pointer moved below the caller's frame.
PPCContext Frame(const PPCContext& ctx) {
  PPCContext work = ctx;
  work.r1.u32 = (ctx.r1.u32 - 0x100u) & ~0xFu;
  return work;
}

/// The world's view node: *(*(game+0x6C)+4), game = *(root+0xC).
uint32_t WorldNode(uint8_t* base) {
  const uint32_t root = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = Plausible(root) ? refii::vfs::LoadGuestU32(base, root + 0xCu) : 0u;
  const uint32_t scene = Plausible(game) ? refii::vfs::LoadGuestU32(base, game + 0x6Cu) : 0u;
  return Plausible(scene) ? refii::vfs::LoadGuestU32(base, scene + 4u) : 0u;
}

/// Player two's node, built once by the guest's constructor on the guest heap (lh_malloc, as the second
/// listener's block), with no render camera.
uint32_t PartnerNode(const PPCContext& ctx, uint8_t* base) {
  uint32_t node = g_node.load(std::memory_order_acquire);
  if (node != 0u) {
    return node;
  }
  static PPCFunc* const alloc = refii::vfs::FindGuestFunction(refii::guest::Address(0x8221F258u));
  if (alloc == nullptr) {
    return 0u;
  }
  PPCContext call = Frame(ctx);
  call.r3.u64 = kBlockAt + kBlockBytes + 16u;
  alloc(call, base);
  if (!Plausible(call.r3.u32)) {
    return 0u;
  }
  node = (call.r3.u32 + 15u) & ~15u;
  std::memset(base + node, 0, kBlockAt + kBlockBytes);
  PPCContext construct = Frame(ctx);
  construct.r3.u32 = node;
  rex_lhSceneView_Construct(construct, base);
  refii::vfs::StoreGuestU32(base, node + kNodeRenderCamera, kNoRenderCamera);
  g_node.store(node, std::memory_order_release);
  return node;
}

}  // namespace

/// Refii._camera_player_icons(on): the core camera chunk brackets a camera's GUI.SetShouldDrawPlayerIcons.
extern "C" void RefiiPlayerIconsFromCamera(int on) { g_from_camera.fetch_add(on != 0 ? 1 : -1, std::memory_order_relaxed); }

// The view node's camera (r3 the node, r4 the block). After the world's: player two's node, from player
// two's block, while split.
REX_EXTERN(rex_lhSceneView_SetCamera) {
  const PPCContext entry = ctx;
  __imp__rex_lhSceneView_SetCamera(ctx, base);
  if (!Plausible(entry.r3.u32) || entry.r3.u32 != WorldNode(base)) {
    return;
  }
  const uint32_t holder = RefiiCoopSplitPartnerCameraHolder(base);
  const uint32_t node = holder != 0u ? PartnerNode(entry, base) : 0u;
  if (node == 0u) {
    g_live.store(false, std::memory_order_release);
    return;
  }
  PPCContext block = Frame(entry);
  block.r3.u32 = node + kBlockAt;
  block.r4.u32 = holder;
  __imp__rex_lhCamera_ScreenBlock(block, base);
  PPCContext set = Frame(entry);
  set.r3.u32 = node;
  set.r4.u32 = node + kBlockAt;
  __imp__rex_lhSceneView_SetCamera(set, base);
  g_live.store(true, std::memory_order_release);
  Once(g_said_camera, "player two's half's hero marker is tested against player two's camera while split");
}

// One record's update (r3 the record, r4 the switch). While split the hero marker takes player two's
// camera's switch.
REX_EXTERN(rex_lhPlayerIcons_UpdateRecord) {
  const uint32_t record = ctx.r3.u32;
  if (Plausible(record) && refii::vfs::LoadGuestU32(base, record + 8u) == kHeroMarker &&
      g_live.load(std::memory_order_acquire) && RefiiCoopSplitPartnerCameraHolder(base) != 0u) {
    ctx.r4.u64 = refii_bridge_stated_value_now(kStatedPartnerIcons) != 1u ? 1u : 0u;
    Once(g_said_switch, "player two's half's hero marker follows player two's camera's player icons");
  }
  __imp__rex_lhPlayerIcons_UpdateRecord(ctx, base);
}

// Before `lwz r4,0x7C(r26)` (0x822B32E4 / 0x822B311C; r24 the record, r30 the scene camera): the hero
// marker's camera is player two's node's while split.
void RefiiCoopPlayerIconCamera(PPCRegister& r24, PPCRegister& r30) {
  if (!g_live.load(std::memory_order_acquire)) {
    return;
  }
  uint8_t* base = RefiiGuestBase();
  const uint32_t node = g_node.load(std::memory_order_acquire);
  if (base == nullptr || node == 0u || !Plausible(r24.u32) ||
      refii::vfs::LoadGuestU32(base, r24.u32 + 8u) != kHeroMarker) {
    return;
  }
  r30.u64 = node + kNodeCamera;
}

// The native's send to the session's partner (0x823062C0 / 0x82907EE8): skipped (to 0x823062E0 /
// 0x82907F08) for a camera's statement.
bool RefiiPlayerIconsStayHere() {
  if (g_from_camera.load(std::memory_order_relaxed) <= 0) {
    return false;
  }
  Once(g_said_here, "a camera's player icons stay on its own machine (not sent to the partner)");
  return true;
}
