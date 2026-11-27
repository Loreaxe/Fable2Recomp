// refii — Fable 3: player two's own camera in couch co-op (camera C2f).
//
// WHO HAS A CAMERA MANAGER. One path builds one: the Player ctor (TU3 sub_8245ACD0 / disc sub_8243B418;
// r31 the Player, `stw r4,0x24(r31)` the player-map key) allocates the holder and its manager unless
// the key is not 1 and the session's mode is the shared screen: `cmpwi cr6,r4,1; beq build` then
// `lwz r9,0x80(NET)` (disc 0x7C) `cmpwi cr6,r9,1; beq skip` — the skip at TU3 0x8245AD9C / disc
// 0x8243B4E4, the build at TU3 0x8245ADA0 / disc 0x8243B4E8. lhSceneNode_SetupCamera (TU3 0x8245B148 /
// disc 0x8243B8D0; r28 the Player) asks the same before spawning the camera entities and dereferences
// Player+4 with no test: skip `beq` TU3 0x8245B384 / disc 0x8243BAF8, spawn TU3 0x8245B388 / disc
// 0x8243BAFC. A couch game's player two is built with the mode already 1 (the drop-in writes it first),
// so shipped, only player one has a camera. Both gates are answered "build" for key 2: player two
// always has a camera manager; the mode value stays 1 (214 readers on TU3, 209 on disc).
//
// THE CAMERA SCRIPTS RUN ONCE PER VM. lhCameraManager_Create (TU3 0x8256A9A8 / disc 0x8253A9E0; `stw
// r4,0(r3)` the Player, `mr r31,r3` the manager, not written again before) runs every camera script
// file through Debug.ReloadCameras (`bl Lua_Debug_ReloadCameras` TU3 0x8256ABB8 / disc 0x8253AC00):
// every mode table is rebuilt and player one's current camera no longer matches its table, so it is
// activated again. For player two's manager in a couch game with player one's standing the call is
// skipped (to TU3 0x8256ABBC / disc 0x8253AC04): the new manager binds the tables that stand.
//
// THE GUEST TICKS ONE MANAGER. Every caller of the holder update (TU3 sub_82268F38 / disc sub_8226A8C0:
// the camera critical section stru_83597F1C / stru_835573E0, then `lwz r3,4(r31); bl
// CameraManager_Update` unless paused) hands it the selector's holder. After it, player two's holder
// gets the same update: the three static bytes the update writes (byte_835970C2 / C3 / D8, disc
// byte_835565BF / E0 / E1) are player two's own copy for its call; its push sends no camera packet and
// posts no depth of field (camera_statements.cpp); and inside it the selector answers player two.
//
// "THE" CAMERA. Every reader asks the selector (TU3 sub_82208EA0 / disc sub_82209680, r3 the player
// manager): key 2's holder on an online client, else key 1's (`li r4,1; bl sub_821FF0A8`). Inside a
// player-two bracket — player two's own camera update, always; player two's character movement
// (CInputProcessCharacterMovement_vf1 TU3 0x829330D0 / disc 0x828DA310, its entity the smart pointer at
// this+8, `addi r31,r3,8; bl lhSmartPtr_Deref`) while each player has their own camera — it answers
// player two's holder. So player two's camera script activates its modes on its own manager
// (Lua_CameraManager_ActivateCamera: `bl sub_82208EA0; lwz r30,4(r3)` then `bl
// lhCameraManager_ActivateCamera` TU3 0x82321228..0x82321280 / disc 0x8231A330..0x8231A388), and player
// two walks by player two's camera.
//
// The player manager's players are a sorted vector of 12-byte entries {key, Player, ...} at +8..+0xC
// (FindPlayer TU3 sub_821FF0A8 / disc sub_821FD790); the manager is `*(*(*0x83597034+0xC)+0x40)` (disc
// 0x83556540). A Player's camera holder is +4 and the holder's manager +4; its hero the smart pointer at
// +0xC (`*(*(Player+0x10))`, lhSmartPtr_Deref TU3 0x82691588 / disc 0x829A2650).

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

REX_EXTERN(__imp__rex_CameraHolder_Update);
REX_EXTERN(__imp__rex_PlayerManager_CameraHolder);
REX_EXTERN(__imp__rex_CInputProcessCharacterMovement_ProcessInput);

namespace {

using refii::fable3::LoadGuestU32;
using refii::fable3::Plausible;

const refii::guest::Addr kGame{0x83597034u};
const refii::guest::Addr kNetwork{0x835979F8u};
// The update's three static bytes: the easing latch, the tint toggle and its previous value.
const refii::guest::Addr kLatches[3] = {
    refii::guest::Addr{0x835970C2u}, refii::guest::Addr{0x835970C3u}, refii::guest::Addr{0x835970D8u}};

/// The shell's stated value for "player two plays on this screen" (payload_bridge.h key 24).
constexpr uint32_t kStatedLocalPartner = 24u;

std::atomic<bool> g_split{false};
std::atomic<bool> g_said_built{false};
std::atomic<bool> g_said_scripts{false};
std::atomic<bool> g_said_tick{false};
std::atomic<bool> g_said_moved{false};

/// Player two's copy of the three bytes, carried from one of its updates to the next.
uint8_t g_partner_latches[3] = {0u, 0u, 0u};
uint32_t g_partner_latches_for = 0u;

/// Which manager is player two's: moved on each time player two's update meets a manager it has not
/// updated (a join, every re-join, a rebuilt Player), so the core chunk starts that manager's record empty.
std::atomic<uint32_t> g_partner_generation{0u};

/// The manager under player two's update (guest thread), and the selector's player-two brackets.
std::atomic<uint32_t> g_partner_ticking{0u};
thread_local uint32_t t_partner_depth = 0u;

uint8_t* GuestBase() {
  rex::system::KernelState* state = rex::system::kernel_state();
  rex::memory::Memory* memory = state != nullptr ? state->memory() : nullptr;
  return memory != nullptr ? memory->virtual_membase() : nullptr;
}

uint8_t LoadU8(const uint8_t* base, uint32_t at) { return base[at]; }
void StoreU8(uint8_t* base, uint32_t at, uint8_t value) { base[at] = value; }

/// The session's mode is the shared screen: `lwz r9,0x80(NET)` TU3 / `0x7C` disc, 1.
bool Couch(const uint8_t* base) {
  const uint32_t network = LoadGuestU32(base, kNetwork);
  const uint32_t session = Plausible(network) ? LoadGuestU32(base, network + 0x10u) : 0u;
  return Plausible(session) && LoadGuestU32(base, session + refii::guest::Pick(0x80u, 0x7Cu)) == 1u;
}

/// FindPlayer(players, key): the Player keyed `key`, else 0.
uint32_t PlayerIn(const uint8_t* base, uint32_t players, uint32_t key) {
  if (!Plausible(players)) {
    return 0u;
  }
  const uint32_t begin = LoadGuestU32(base, players + 8u);
  const uint32_t end = LoadGuestU32(base, players + 0xCu);
  if (!Plausible(begin) || end < begin || end - begin > 12u * 16u) {
    return 0u;
  }
  for (uint32_t entry = begin; entry + 12u <= end; entry += 12u) {
    if (LoadGuestU32(base, entry) == key) {
      const uint32_t player = LoadGuestU32(base, entry + 4u);
      return Plausible(player) ? player : 0u;
    }
  }
  return 0u;
}

uint32_t Players(const uint8_t* base) {
  const uint32_t game = LoadGuestU32(base, kGame);
  const uint32_t state = Plausible(game) ? LoadGuestU32(base, game + 0xCu) : 0u;
  return Plausible(state) ? LoadGuestU32(base, state + 0x40u) : 0u;
}

uint32_t HolderOf(const uint8_t* base, uint32_t player) {
  const uint32_t holder = Plausible(player) ? LoadGuestU32(base, player + 4u) : 0u;
  return Plausible(holder) ? holder : 0u;
}

uint32_t ManagerOf(const uint8_t* base, uint32_t holder) {
  const uint32_t manager = Plausible(holder) ? LoadGuestU32(base, holder + 4u) : 0u;
  return Plausible(manager) ? manager : 0u;
}

uint32_t HeroOf(const uint8_t* base, uint32_t player) {
  const uint32_t block = Plausible(player) ? LoadGuestU32(base, player + 0x10u) : 0u;
  const uint32_t hero = Plausible(block) ? LoadGuestU32(base, block) : 0u;
  return Plausible(hero) ? hero : 0u;
}

/// Player two's holder in a couch game, when it has a camera manager; else 0.
uint32_t PartnerHolder(const uint8_t* base, uint32_t players) {
  if (!Couch(base)) {
    return 0u;
  }
  const uint32_t holder = HolderOf(base, PlayerIn(base, players, 2u));
  return ManagerOf(base, holder) != 0u ? holder : 0u;
}

uint32_t KeyOf(const uint8_t* base, uint32_t player) {
  return Plausible(player) ? LoadGuestU32(base, player + 0x24u) : 0u;
}

void Once(std::atomic<bool>& said, const char* what) {
  if (!said.exchange(true, std::memory_order_relaxed)) {
    REXLOG_INFO("Refii couch camera: {}", what);
  }
}

}  // namespace

namespace refii::fable3::coopcam {

void AnswerCoopSplit(const char* name) {
  const bool on = name != nullptr && std::strcmp(name + sizeof(kCoopSplitAction) - 1u, "1") == 0;
  if (on != g_split.exchange(on, std::memory_order_relaxed)) {
    REXLOG_INFO("Refii couch camera: the screen is {}", on ? "split: each player their own camera" : "shared");
  }
}

uint32_t PartnerManagerTicking() { return g_partner_ticking.load(std::memory_order_relaxed); }

bool SplitInEffect(const uint8_t* base) {
  return base != nullptr && g_split.load(std::memory_order_relaxed) && ::PartnerHolder(base, Players(base)) != 0u;
}

uint32_t PartnerHolder(const uint8_t* base) { return base != nullptr ? ::PartnerHolder(base, Players(base)) : 0u; }

bool IsPartnerHero(const uint8_t* base, uint32_t entity) {
  return base != nullptr && Plausible(entity) && Couch(base) && entity == HeroOf(base, PlayerIn(base, Players(base), 2u));
}

void ManagerDestroyed(const uint8_t* base, uint32_t manager) {
  if (base == nullptr || !Plausible(manager)) {
    return;
  }
  const uint32_t player = LoadGuestU32(base, manager);
  if (KeyOf(base, player) == 2u) {
    REXLOG_INFO("Refii couch camera: player two's Player 0x{:08X} lost its camera manager 0x{:08X}", player,
                manager);
  }
  if (g_partner_latches_for == manager) {
    g_partner_latches_for = 0u;
  }
}

uint32_t PartnerDepth() { return t_partner_depth; }
void EnterPartner() { ++t_partner_depth; }
void LeavePartner() { --t_partner_depth; }

}  // namespace refii::fable3::coopcam

/// 1 for the whole of player two's camera manager update (the second holder update above). One manager
/// update (CameraManager_Update TU3 sub_822551E8 / disc sub_82255FB8, r26 the manager) makes two Lua calls:
/// the selection, CameraManager:FrameUpdate(hero) (`bl sub_8226D730` on manager+0x18 / +4 and the Player's
/// hero, TU3 0x82255518; disc `bl sub_8226F060` 0x822562E8), then the active mode's FrameUpdate (`bl
/// sub_82258978` on wrapper+0x2C / +0x18, the override wrapper +0x3C else the primary +0x2C, TU3 0x82255540;
/// disc `bl sub_82259328` 0x82256310). The mode's update is what writes the saved
/// CameraManager.CameraPosition (CameraBase:FrameUpdate / :PureFrameUpdate, camerabase.lua 247-248 / 299-300)
/// and states the leash and the canopy fading, so the core chunk asks this (Refii._partner_camera_update)
/// rather than marking the selection call alone.
extern "C" int32_t RefiiF3PartnerCameraUpdating(void) {
  return refii::fable3::coopcam::PartnerManagerTicking() != 0u ? 1 : 0;
}

/// Refii._partner_camera_manager() on Fable 3 (mod_lua_hooks.cpp): a small number naming player two's camera
/// manager, 0 before the first. Hero two may come back as the same entity, so the core chunk keys player two's
/// current camera to this as well as to the hero: a new manager's first selection activates the best camera
/// (CameraManager_Update calls the active mode with no test: `lwz r30,0x2C(r26)` .. `addi r3,r30,0x2C; bl
/// sub_82258978` .. `lbz r11,0x69(r30)` TU3 0x8225552C..0x82255544, disc 0x822562FC..0x82256314).
extern "C" int32_t RefiiF3PartnerCameraGeneration(void) {
  return static_cast<int32_t>(g_partner_generation.load(std::memory_order_relaxed) & 0xFFFFFFu);
}

namespace {
/// The script brackets on this thread (RefiiF3PartnerScreen): which of them entered a selector bracket.
thread_local uint64_t t_screen_entered = 0u;
thread_local uint32_t t_screen_depth = 0u;
}  // namespace

/// Refii._partner_screen(on) on Fable 3 (mod_lua_hooks.cpp), as on Fable 2: "the" camera is player two's view
/// until turned off (nests). While each couch player has their own camera the selector answers player two's
/// holder inside (a player-two bracket); otherwise "the" camera stays the shared screen's, player one's.
extern "C" void RefiiF3PartnerScreen(int32_t on) {
  if (on != 0) {
    const bool enter = refii::fable3::coopcam::SplitInEffect(GuestBase());
    if (t_screen_depth < 64u) {
      const uint64_t bit = 1ull << t_screen_depth;
      t_screen_entered = enter ? (t_screen_entered | bit) : (t_screen_entered & ~bit);
      ++t_screen_depth;
      if (enter) {
        ++t_partner_depth;
      }
    }
  } else if (t_screen_depth > 0u) {
    --t_screen_depth;
    if ((t_screen_entered & (1ull << t_screen_depth)) != 0u) {
      --t_partner_depth;
    }
  }
}

/// Refii.split_screen() on Fable 3 (mod_lua_hooks.cpp): each player has their own camera now.
extern "C" int32_t RefiiF3CoopSplitScreen(void) {
  return refii::fable3::coopcam::SplitInEffect(GuestBase()) ? 1 : 0;
}

// The Player ctor's skip (TU3 0x8245AD9C / disc 0x8243B4E4): build for key 2 (to the build at 0x8245ADA0
// / 0x8243B4E8). Any other key keeps the shipped test; where the mode is not the shared screen the
// shipped test builds too.
bool RefiiF3PartnerCamera(PPCRegister& r31) {
  uint8_t* base = GuestBase();
  if (base == nullptr || KeyOf(base, r31.u32) != 2u) {
    return false;
  }
  Once(g_said_built, "player two's Player gets a camera manager of their own");
  return true;
}

// SetupCamera's skip (TU3 0x8245B384 / disc 0x8243BAF8): spawn player two's camera entities when its
// Player has a holder and a manager, the two loads the spawn makes with no test (to 0x8245B388 /
// 0x8243BAFC).
bool RefiiF3PartnerCameraEntities(PPCRegister& r28) {
  uint8_t* base = GuestBase();
  return base != nullptr && KeyOf(base, r28.u32) == 2u && ManagerOf(base, HolderOf(base, r28.u32)) != 0u;
}

// The manager ctor's Debug.ReloadCameras (TU3 0x8256ABB8 / disc 0x8253AC00, r31 the manager): skipped (to
// 0x8256ABBC / 0x8253AC04) for player two's manager in a couch game while player one's stands.
bool RefiiF3PartnerCameraScripts(PPCRegister& r31) {
  uint8_t* base = GuestBase();
  if (base == nullptr || !Couch(base)) {
    return false;
  }
  const uint32_t player = Plausible(r31.u32) ? LoadGuestU32(base, r31.u32) : 0u;
  if (KeyOf(base, player) != 2u || ManagerOf(base, HolderOf(base, PlayerIn(base, Players(base), 1u))) == 0u) {
    return false;
  }
  Once(g_said_scripts, "player two's camera manager binds the camera scripts that stand (no reload)");
  return true;
}

// The holder update (r3 the holder): the guest's, then player two's in a couch game.
REX_EXTERN(rex_CameraHolder_Update) {
  const uint32_t holder = ctx.r3.u32;
  __imp__rex_CameraHolder_Update(ctx, base);
  if (g_partner_ticking.load(std::memory_order_relaxed) != 0u) {
    return;
  }
  const uint32_t players = Players(base);
  const uint32_t second = PartnerHolder(base, players);
  refii_bridge_stated_value(kStatedLocalPartner, Couch(base) && PlayerIn(base, players, 2u) != 0u ? 1u : 0u);
  if (second == 0u || second == holder) {
    return;
  }
  const uint32_t manager = ManagerOf(base, second);
  if (g_partner_latches_for != manager) {
    // A new manager starts from the bytes as player one's update left them.
    for (int i = 0; i < 3; ++i) {
      g_partner_latches[i] = LoadU8(base, kLatches[i]);
    }
    g_partner_latches_for = manager;
    g_partner_generation.fetch_add(1u, std::memory_order_relaxed);
  }
  uint8_t first[3];
  for (int i = 0; i < 3; ++i) {
    first[i] = LoadU8(base, kLatches[i]);
    StoreU8(base, kLatches[i], g_partner_latches[i]);
  }

  g_partner_ticking.store(manager, std::memory_order_relaxed);
  ++t_partner_depth;
  PPCContext work = ctx;
  work.r3.u64 = second;
  __imp__rex_CameraHolder_Update(work, base);
  --t_partner_depth;
  g_partner_ticking.store(0u, std::memory_order_relaxed);
  Once(g_said_tick, "player two's camera manager updates after player one's");

  for (int i = 0; i < 3; ++i) {
    g_partner_latches[i] = LoadU8(base, kLatches[i]);
    StoreU8(base, kLatches[i], first[i]);
  }
}

// The selector (r3 the player manager): player two's holder inside a player-two bracket.
REX_EXTERN(rex_PlayerManager_CameraHolder) {
  if (t_partner_depth > 0u) {
    const uint32_t second = PartnerHolder(base, ctx.r3.u32);
    if (second != 0u) {
      ctx.r3.u64 = second;
      return;
    }
  }
  __imp__rex_PlayerManager_CameraHolder(ctx, base);
}

// Character movement (r3 the process, its entity the smart pointer at +8): player two's walks by player
// two's camera while each player has their own.
REX_EXTERN(rex_CInputProcessCharacterMovement_ProcessInput) {
  const uint32_t sp = ctx.r3.u32 + 8u;
  const uint32_t block = Plausible(ctx.r3.u32) ? LoadGuestU32(base, sp + 4u) : 0u;
  const uint32_t entity = Plausible(block) ? LoadGuestU32(base, block) : 0u;
  const bool partner = Plausible(entity) && refii::fable3::coopcam::SplitInEffect(base) &&
                       entity == HeroOf(base, PlayerIn(base, Players(base), 2u));
  if (!partner) {
    __imp__rex_CInputProcessCharacterMovement_ProcessInput(ctx, base);
    return;
  }
  Once(g_said_moved, "player two walks by player two's camera");
  ++t_partner_depth;
  __imp__rex_CInputProcessCharacterMovement_ProcessInput(ctx, base);
  --t_partner_depth;
}
