// refii - Fable 3: the mouse turns the local player's camera, whole, in the update it is handed.
//
// Fable 2's hook (Fable2/src/vfs/src/hooks/mouse_look.cpp) is the reference; this file is the same
// contract against Fable 3's binary and camera scripts. Every address is the Fable 3 TU3 IDB
// ("disc" marks one TU3 lacks).
//
// WHERE THE TURN GOES.
//   - The camera manager's update `sub_822551E8(manager)` (rex_CameraManager_Update; its one
//     caller sub_82268F38 runs it per tick under the game lock when the game is not paused) runs
//     the active mode's Lua FrameUpdate once: the override at manager+60, else the primary at
//     manager+44 (`lwz r11,0x3C(r31); cmpwi; lwz r11,0x2C(r31)` at 0x8225551C..0x82255534), then
//     `sub_82258978(mode+44, mode+24)` at 0x82255540 (the FrameUpdate holder, the camera table).
//     An override whose +105 byte is set is dropped after its update (0x82255544..0x82255560).
//   - A mode is the C++ wrapper `sub_827A00A8` builds around a Lua camera object (vtable
//     0x82001F88): +24 the camera table's ref holder, +32/+44/+56/+68/+80/+92 the Activate /
//     FrameUpdate / Deactivate / CleanUp / ProcessGameAction / OnLevelLoad function holders
//     (0x827A0378..0x827A04E0). A holder's `*(+0)` is a ref object: +4 the lua_State
//     (0x827A222C), +8 the registry ref (0x827A2254; the camera table's at 0x82241C30).
//   - The wrapper is an input listener: its vtable slot 1, `sub_827A0DA8(mode, msg)`
//     (rex_CameraMode_ProcessGameAction), is what the dispatcher `sub_821CE3B0` calls with each
//     32-byte GameAction (`l->vt[1](l, action)` at 0x821CE6D8): msg+0 the action id, +4 x, +8 y.
//     It refuses while the player's input bit is set (`(*flags >> 3) & 1` at 0x827A0E00), while
//     the debug camera holds (`byte_835970C2`, 0x827A0F14) and for action 52; negates y under
//     CameraLookInverted (component 37's +40, 0x827A0EEC); and calls
//     `sub_827A2200(mode+80, mode+24, &action, &x, &y)`: pushes registry[ProcessGameAction],
//     registry[camera table], the three numbers (tag 3, float32) and pcalls with 4 arguments
//     (0x827A230C). Where the ids come from: the control scheme in globals.gdb binds
//     GameActionRotateCameraPad (GameAction 2) and GameActionZoomCameraPad (GameAction 4) to
//     XboxRightJoystick (records #2070b9bb / #f20eddba), x and y the stick's.
//
// THE CAMERAS' LOOK CONTRACT (gamescripts.bnk; the compiled chunks' constant pools).
//   SACCamera, `game:\data\scripts\Camera\StringAndCageCamera.lua` (chunk 842a028f): the same
//     fields as Fable 2's — RotateSpeed, RotateDamp, RotateRight, SmoothRotateRight,
//     ApplyRotateVector, ManualHeightOffset, RollingFocalPos, GetCanRotateCamera, IsTargetLocking,
//     LastRotateFrame, ClearToRotate, CheckForBetterTargetInDirection, and the natives
//     Player.GetCameraSensitivity and Timing.GetTickRate — so ProcessGameAction(ROTATION) ASSIGNS
//     `RotateRight = -x * (RotateSpeed / GetTickRate()) * (0.5 + sensitivity)` and
//     ApplyRotateVector eases it through SmoothRotateRight (GetRollingAverage, RotateDamp), as
//     the Fable 2 header lays out. Timing.GetTickRate is `(float)dbl_832DF4D0` (sub_82240908).
//   FirstPersonCamera (chunk 04059234): RotateSmoothAmount, RotateX/RotateY, SmoothedRotateX/Y.
//   RangedCombatCamera (chunk 6e8f631c): TendnessNormal, AngleLimit; an empty ProcessGameAction.
//
// SO, before each update the hook takes everything the mouse turned since the last one and hands
// it to the active mode through the mode's own receiver — its gates, its invert, its sensitivity
// all apply as to the stick — and makes the turn the camera accepted land WHOLE in this update,
// outside the stick's easing, exactly as the Fable 2 hook does for SAC (RotateRight = M, then
// SmoothRotateRight += M, taken back out after) and for first person (the accepted dx, dy moved
// into SmoothedRotateX/Y scaled by (n+1)/n). What the camera turned is stated with how the host
// draws a turn not yet taken (core/mouse_look_state.cpp): ORBIT about the focus at the stated
// horizontal distance to RollingFocalPos, IN PLACE for first person, NONE otherwise.
//
// THE LUA VM IS HAVOKSCRIPT ON 5.1 SHAPES (Fable3/src/hooks/src/gui_natives.cpp): L->top at +28,
// L->base at +32, stack_last at +36 (the room check `top + 16 > L[9]` at 0x822A6278); a TValue is
// 8 bytes, value then tag: 0 nil, 1 boolean, 3 number (a float32), 5 table, 7 userdata (payload
// at +16). The stack is read and written directly where the guest itself does (sub_827A2200
// writes its three numbers inline); fields go through the guest's own lua_getfield
// `sub_822A6118(L, idx, name)` and lua_setfield `sub_822A6250(L, idx, name)` (the ones the
// native registrars use, 0x822A6250 at 0x822E62AC), and the camera table is pushed by the
// guest's own `sub_82241C08(holder, L)` (rex_LuaRef_Push).
//
// THE MODE'S HERO, as the receiver finds it: `sub_827A0D48(mode)` looks the key at mode+4 up in
// the game state's player table `*(state+0x40)` (`lwz r4,4(r3)` / `lwz r5,0x40(r10)` at
// 0x827A0D5C / 0x827A0D6C, state = `*(*0x83597034+0xC)`) through `sub_821FF0A8` — 12-byte
// {key, player} entries from +8 to +0xC (`li r10,0xC` / `lwz r3,4(r11)` at 0x821FF0AC /
// 0x821FF120) — and takes key 1 when that key has none (0x827A0D80..0x827A0D90); the entity is
// `*(*(player+0x10))` (`sub_82A1D6E8`: `addi r3,r3,0xC; lwz r11,4(r3); lwz r10,0(r11)`). Its
// component 37 (the receiver's search, `cmpwi r10,0x25` at 0x827A0E8C) carries CameraLookInverted
// at +0x28 (`lbz r11,0x28(r31)` at 0x827A0EEC).
//
// A TARGET LOCK TAKES THE MOUSE AS FLICKS, as in Fable 2. The lock is the player mode
// CPlayerModeTargetLock (vtable disc 0x820C59A0, slot 8 `li r3,4` at 0x82E0CDE8), asked by the native
// Player.HasPlayerMode `sub_821B9D80(component37, mode)` (`lwz r3,0x14(r3)` then `sub_821F8028`,
// whose `sub_821E2AD0` matches each held mode's slot 8 against `mode`) — the targeting component
// asks it the same way (`lwz r3,0x14(r11); li r4,4; bl sub_821F8028` at 0x821A1BD4..disc 0x821A1F58).
// SACCamera's lock fields (IsTargetLocking, ClearToRotate, LastRotateFrame,
// CheckForBetterTargetInDirection, in the chunk's constant pool) are Fable 2's: a look x past 0.8
// switches target. So under a lock the mouse's turn is collected and only a deliberate flick
// (kFlickDegrees inside kFlickWindowSeconds, one direction) is handed, as ONE x of ±1 through the
// receiver; the push that made it hands nothing more until the mouse rests or reverses. The wheel
// has no lock that switches by it here (no mod statement in Fable 3) and is dropped.
//
// THE RANGED AIM'S CURSOR TAKES THE MOUSE, as the PC build's does. Zoom aim
// (CPlayerModeZoomAimRanged, type 27) runs the aim at mode+0x20 through `sub_828AC9D8(aim, stick)`
// (its only caller, `addi r29,r27,0x20; ld r4,0x160(r27)` at disc 0x8284DFCC / disc 0x8284DFD8), whose
// cursor move `sub_828AE960` (rex_lhRangedAim_MoveCursor) is called only while the cursor is free
// (`lbz r11,0xCD(r31)` at 0x828ACE74) with the smoothed stick (`ld r4,0x24(r31)` at 0x828ACE84):
// the 64-bit r4 is stored and read back as x then y (`std r4,arg_18(r1)` at 0x828AE978,
// `lfs f30,arg_18(r1)` / `lfs f29,arg_18+4(r1)` at disc 0x828522FC / 0x828AEA54), so x is the high
// word, y the low. Free of a target it moves the cursor, screen fractions at aim+0x2C / +0x30:
//     828526a8  fmadds f9, f13, f27, f12 / stfs f9, 0x2C(r31)   ; x += sx * speed * 2.0/tick
//     828526b0  fmadds f8, f11, f26, f10 / stfs f8, 0x30(r31)   ; y += sy * speed * 1.6/tick
// (speed `lfs f0,0x38(r28)` at 0x828AEDC8, r28 = `sub_821B1390(owner)` at 0x828AEA34; f27 / f26
// = flt_832DF684 / flt_832DF688 over dbl_832DF4D0, 0x828AEA6C..0x828AEA90), then clamps it to
// ±0.8 and searches under it (`sub_828B0440`); snapped to a target the stick picks between targets
// instead (0x828AEB68..0x828AED08). The cursor's y is screen-down (`sub_828B1A08`: `py / height *
// 2 - 1`), so the aim's stick y arrives negated unless the camera is inverted (`sub_828A9DD8`:
// `lfs f0,flt_82070520` = -1.0 times +0x164 unless +0x17E, which is component 37's +0x28,
// disc 0x8284D694..disc 0x8284D6A0 / disc 0x8284DCE8..disc 0x8284DCEC). The PC build moves the same cursor by the
// mouse through the same gain (`sub_F36C70`: `*(this+68) += a2[0] * (flt / tick) * stats[56]`).
// So the mouse's turn joins the stick at the cursor's own gain — a posted degree moves the cursor
// a degree's radians — its invert as the stick's, and the clamps, the search and the snap apply
// to it as to the stick. RangedCombatCamera takes nothing through its receiver and chases the aim
// point, so while an aim moves its cursor the camera update leaves the turn for it.
//
// NOT CARRIED FROM FABLE 2 (stated as remaining, not approximated): the ranged aim turned rigidly
// with the view, and the heading held while the hero moves. Both are Fable 2's wrappers of the
// camera scripts' own functions (RangedCombatCamera.UpdateCurrentDirection, SACCamera.CalculateXY,
// Godot/src/Titles/Fable2/host/CoopHeroLua.cs) compiled by refii_lua for Fable 2's Lua 5.1; Fable 3's
// scripts are HavokScript bytecode with no compiler or decompiler in the tree, so neither the
// wrapper can be loaded nor CalculateXY's Fable 3 body read to place one.

#include <cmath>
#include <cstdint>
#include <cstring>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "coop_camera.h"
#include "core/mouse_look_common.h"
#include "core/payload_bridge.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_CameraManager_Update);
REX_EXTERN(__imp__rex_lhRangedAim_MoveCursor);
REX_EXTERN(rex_CameraMode_ProcessGameAction);
REX_EXTERN(rex_LuaRef_Push);
REX_EXTERN(rex_lua_getfield);
REX_EXTERN(rex_lua_setfield);
REX_EXTERN(rex_Player_HasPlayerMode);
REX_EXTERN(rex_Entity_RangedAimStats);

namespace {

using refii::mouselook::AlignPointAlongRay;
using refii::mouselook::TargetLockFlickTracker;
using refii::mouselook::WheelSwitchQueue;
using refii::mouselook::kFlagHasHead;
using refii::mouselook::kFlagHostAimApplied;
using refii::mouselook::kFlagLookInverted;
using refii::mouselook::kHeightDeadZone;
using refii::mouselook::kModeAim;
using refii::mouselook::kModeInPlace;
using refii::mouselook::kModeNone;
using refii::mouselook::kModeOrbit;

/// GameActionRotateCameraPad / GameActionZoomCameraPad (globals.gdb, bound to XboxRightJoystick).
constexpr uint32_t kRotateCamera = 2u;
constexpr uint32_t kZoomCamera = 4u;

constexpr int32_t kGlobalsIndex = -10002;
constexpr const char* kDrivenCamera = "RefiiMouseLookCamera";

/// The GUI manager global (for inspecting Fable 3's reticule record 1 mode).
const refii::guest::Addr kGuiManager{0x83596F84u};

/// Ranged aim offsets (reticle_hooks.cpp / mouse_look.cpp header).
constexpr uint32_t kAimCursorX = 0x2Cu;
constexpr uint32_t kAimCursorY = 0x30u;
constexpr uint32_t kAimPoint = 0x80u;
constexpr uint32_t kAimFreeOffset = 0xCDu;

/// The game mode, whose `*(+0xC)` is the game state (`lwz r10,0xC(r11)` at 0x827A0D68).
const refii::guest::Addr kGame{0x83597034u};

/// The game state's player table (0x827A0D6C), keyed by a camera mode's +4.
constexpr uint32_t kStatePlayers = 0x40u;
constexpr uint32_t kModePlayerKey = 4u;

/// The player's component the receiver reads (0x827A0E8C), its CameraLookInverted byte (0x827A0EEC).
constexpr uint32_t kPlayerComponent = 37u;
constexpr uint32_t kCameraLookInverted = 0x28u;

/// CPlayerModeTargetLock's type (vtable disc 0x820C59A0 slot 8, `li r3,4` at 0x82E0CDE8).
constexpr uint32_t kTargetLockMode = 4u;

/// The aim's cursor move: the stats' cursor speed (0x828AEDC8), the gains over the tick rate
/// (0x828AEA70 / 0x828AEA74).
constexpr uint32_t kAimStatsSpeed = 0x38u;
const refii::guest::Addr kAimGainAcross{0x832DF684u};
const refii::guest::Addr kAimGainUp{0x832DF688u};

/// The Lua camera mode wrapper's vtable (sub_827A00A8 at 0x827A00C8), and its fields.
const refii::guest::Addr kModeVtable{0x82001F88u};
constexpr uint32_t kModeSelf = 24u;
constexpr uint32_t kModeProcessGameAction = 80u;

/// The camera manager's mode slots (0x8225551C..0x82255534).
constexpr uint32_t kManagerPrimary = 44u;
constexpr uint32_t kManagerOverride = 60u;

/// The debug camera holds the pad (0x827A0F14): nothing reaches the camera scripts.
const refii::guest::Addr kDebugCameraFlag{0x835970C2u};

/// Timing.GetTickRate's double (sub_82240908, `lfd f0, dbl_832DF4D0`).
const refii::guest::Addr kTickRate{0x832DF4D0u};

/// lua_State layout (gui_natives.cpp; 0x822A6278).
constexpr uint32_t kLuaTop = 28u;
constexpr uint32_t kLuaBase = 32u;
constexpr uint32_t kLuaStackLast = 36u;

/// TValue tags.
constexpr uint32_t kTagNil = 0u;
constexpr uint32_t kTagBoolean = 1u;
constexpr uint32_t kTagNumber = 3u;
constexpr uint32_t kTagTable = 5u;
constexpr uint32_t kTagUserdata = 7u;
constexpr uint32_t kUdataPayload = 16u;

enum class Kind { kOther, kStringAndCage, kFirstPerson, kRangedAim };

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

uint32_t LoadU32(const uint8_t* base, uint32_t address) {
  uint32_t be;
  std::memcpy(&be, base + address, sizeof(be));
  return __builtin_bswap32(be);
}

float LoadF32(const uint8_t* base, uint32_t address) {
  const uint32_t bits = LoadU32(base, address);
  float value;
  std::memcpy(&value, &bits, sizeof(value));
  return value;
}

double LoadF64(const uint8_t* base, uint32_t address) {
  const uint64_t bits =
      (static_cast<uint64_t>(LoadU32(base, address)) << 32) | LoadU32(base, address + 4u);
  double value;
  std::memcpy(&value, &bits, sizeof(value));
  return value;
}

void StoreU32(uint8_t* base, uint32_t address, uint32_t value) {
  const uint32_t be = __builtin_bswap32(value);
  std::memcpy(base + address, &be, sizeof(be));
}

void StoreF32(uint8_t* base, uint32_t address, float value) {
  uint32_t bits;
  std::memcpy(&bits, &value, sizeof(bits));
  StoreU32(base, address, bits);
}

/// A copy of `ctx` with the stack pointer moved below the caller's frame.
PPCContext Frame(const PPCContext& ctx, uint32_t bytes = 0x200u) {
  PPCContext work = ctx;
  work.r1.u32 = (ctx.r1.u32 - bytes) & ~0xFu;
  return work;
}

/// The mode the update runs: the override, else the primary.
uint32_t ActiveCamera(const uint8_t* base, uint32_t manager) {
  if (!Plausible(manager)) {
    return 0u;
  }
  const uint32_t overriding = LoadU32(base, manager + kManagerOverride);
  const uint32_t mode = Plausible(overriding) ? overriding : LoadU32(base, manager + kManagerPrimary);
  return Plausible(mode) && LoadU32(base, mode) == kModeVtable ? mode : 0u;
}

/// The receiver's own standing gate the hook can see: the debug camera holds the pad.
bool TakesInput(const uint8_t* base) { return base[kDebugCameraFlag] == 0u; }

uint32_t Word(const uint8_t* base, uint32_t at) { return Plausible(at) ? LoadU32(base, at) : 0u; }

/// What a weak reference at `holder` resolves to: `*(*(holder+4))` (lhSmartPtr_Deref
/// 0x82691588: `lwz r11,4(r3); lwz r10,0(r11)`), 0 when dead.
uint32_t Resolve(const uint8_t* base, uint32_t holder) {
  const uint32_t at = Word(base, Word(base, holder + 4u));
  return Plausible(at) ? at : 0u;
}

/// The entity's component `id`: its {id, component} pairs from +0x54 to +0x58 (the receiver's
/// search, `lwz r11,0x54(r3); lwz r7,0x58(r3)` at 0x827A0E6C), or 0.
uint32_t Component(const uint8_t* base, uint32_t entity, uint32_t id) {
  const uint32_t first = Word(base, entity + 0x54u);
  const uint32_t last = Word(base, entity + 0x58u);
  if (!Plausible(first) || last < first || last - first > 8u * 256u) {
    return 0u;
  }
  for (uint32_t at = first; at < last; at += 8u) {
    if (LoadU32(base, at) == id) {
      const uint32_t component = LoadU32(base, at + 4u);
      return Plausible(component) ? component : 0u;
    }
  }
  return 0u;
}

/// The mode's hero, as `sub_827A0D48` and `sub_82A1D6E8` find it (see the header), or 0.
uint32_t HeroOf(const uint8_t* base, uint32_t mode) {
  const uint32_t table = Word(base, Word(base, Word(base, kGame) + 0xCu) + kStatePlayers);
  if (!Plausible(mode) || !Plausible(table)) {
    return 0u;
  }
  const uint32_t first = Word(base, table + 8u);
  const uint32_t last = Word(base, table + 0xCu);
  if (!Plausible(first) || last < first || last - first > 12u * 64u) {
    return 0u;
  }
  const auto find = [&](uint32_t key) -> uint32_t {
    for (uint32_t at = first; at < last; at += 12u) {
      if (LoadU32(base, at) == key) {
        return LoadU32(base, at + 4u);
      }
    }
    return 0u;
  };
  uint32_t player = find(LoadU32(base, mode + kModePlayerKey));
  if (!Plausible(player)) {
    player = find(1u);
  }
  return Plausible(player) ? Resolve(base, player + 0xCu) : 0u;
}

/// Player.HasPlayerMode(component37, TARGET_LOCK): the guest's own native.
bool TargetLocked(PPCContext& ctx, uint8_t* base, uint32_t hero) {
  const uint32_t player = Component(base, hero, kPlayerComponent);
  if (!Plausible(player)) {
    return false;
  }
  PPCContext work = Frame(ctx);
  work.r3.u64 = player;
  work.r4.u64 = kTargetLockMode;
  rex_Player_HasPlayerMode(work, base);
  return (work.r3.u32 & 0xFFu) != 0u;
}

/// The hero's CameraLookInverted, which the aim's stick and the camera's receiver both honour.
bool LookInverted(const uint8_t* base, uint32_t hero) {
  const uint32_t player = Component(base, hero, kPlayerComponent);
  return Plausible(player) && base[player + kCameraLookInverted] != 0u;
}

/// Whether `aim` has a free cursor (`aim+0xCD != 0`) and Player 1's reticle record 1 is not
/// target-locked (active == 0 or mode == 0).
bool AimFreeAndUnlocked(const uint8_t* base, uint32_t aim) {
  if (!Plausible(aim) || base[aim + kAimFreeOffset] == 0u) {
    return false;
  }
  const uint32_t gui = Word(base, kGuiManager);
  const uint32_t hud = Plausible(gui) ? Word(base, gui + 0x20u) : 0u;
  const uint32_t manager = Plausible(hud) ? Word(base, hud + 0x3Cu) : 0u;
  const uint32_t array = Plausible(manager) ? Word(base, manager + 4u) : 0u;
  if (!Plausible(array)) {
    return true;
  }
  const uint32_t record = array + 0x30u;
  return base[record] == 0u || LoadU32(base, record + 0x20u) == 0u;
}

/// The camera's Lua table, pushed the way `sub_82241C08` pushes it, with a few field accessors.
/// Everything runs below the caller's stack pointer; the Lua stack is restored on destruction.
class CameraScript {
 public:
  CameraScript(PPCContext& ctx, uint8_t* base, uint32_t mode)
      : work_(Frame(ctx, 0x400u)), base_(base) {
    names_ = work_.r1.u32 + 0x200u;
    const uint32_t function = Plausible(mode) ? LoadU32(base, mode + kModeProcessGameAction) : 0u;
    const uint32_t self = Plausible(mode) ? LoadU32(base, mode + kModeSelf) : 0u;
    const uint32_t state = Plausible(function) ? LoadU32(base, function + 4u) : 0u;
    if (!Plausible(self) || !Plausible(state) || !Plausible(LoadU32(base, mode + kModeSelf))) {
      return;
    }
    L_ = state;
    self_holder_ = mode + kModeSelf;
    top_ = Top();
    if (!PushSelf() || Tag(-1) != kTagTable) {
      SetTop(top_);
      L_ = 0u;
    }
  }

  ~CameraScript() {
    if (L_ != 0u) {
      SetTop(top_);
    }
  }

  CameraScript(const CameraScript&) = delete;
  CameraScript& operator=(const CameraScript&) = delete;

  bool open() const { return L_ != 0u; }

  uint32_t state() const { return L_; }

  void Publish(const char* name) {
    if (PushSelf()) {
      SetField(kGlobalsIndex, name);
    }
  }

  /// The mode's GameAction receiver, with this script's frame (the table stays pushed; the
  /// receiver's own pushes are relative to the top).
  void Send(uint32_t mode, uint32_t action, float x, float y) {
    PPCContext w = work_;
    const uint32_t message = (w.r1.u32 + 0x100u) & ~0xFu;
    std::memset(base_ + message, 0, 32u);
    StoreU32(base_, message, action);
    StoreF32(base_, message + 4u, x);
    StoreF32(base_, message + 8u, y);
    w.r3.u32 = mode;
    w.r4.u32 = message;
    rex_CameraMode_ProcessGameAction(w, base_);
  }

  /// `self[name]` (through the class's __index) when it is a number.
  bool Number(const char* name, double* out) {
    GetField(-1, name);
    const bool number = Tag(-1) == kTagNumber;
    if (number) {
      *out = static_cast<double>(LoadF32(base_, Slot(-1)));
    }
    SetTop(-2);
    return number;
  }

  /// `self[name]` is neither nil nor false.
  bool Truth(const char* name) {
    GetField(-1, name);
    const uint32_t tag = Tag(-1);
    const bool truth = tag != kTagNil && !(tag == kTagBoolean && LoadU32(base_, Slot(-1)) == 0u);
    SetTop(-2);
    return truth;
  }

  /// `self[name] or fallback`.
  double NumberOr(const char* name, double fallback) {
    double value = fallback;
    return Number(name, &value) ? value : fallback;
  }

  /// `self[name] = value`.
  void SetNumber(const char* name, double value) {
    if (!Room(2u)) {
      return;
    }
    const uint32_t top = Top32();
    StoreF32(base_, top, static_cast<float>(value));
    StoreU32(base_, top + 4u, kTagNumber);
    SetTop32(top + 8u);
    SetField(-2, name);
  }

  /// `self[name] = nil`.
  void SetNil(const char* name) {
    if (!Room(2u)) {
      return;
    }
    const uint32_t top = Top32();
    StoreU32(base_, top, 0u);
    StoreU32(base_, top + 4u, kTagNil);
    SetTop32(top + 8u);
    SetField(-2, name);
  }

  /// `self[name]` when it is a CVector3: a full userdata whose payload (+16) is a pointer to the
  /// 16-byte vector, x y z at +0 +4 +8 (the constructor `sub_822405B8`: `*lua_newuserdata(L, 4) =
  /// block` at 0x822406DC, a CVector3 argument read as `(*udata)[4]` then `lvx128` at
  /// 0x82240628). Finite, and within `within` metres of `around`, or false.
  bool Vector(const char* name, const float* around, float within, float* xyz) {
    GetField(-1, name);
    bool ok = false;
    if (Tag(-1) == kTagUserdata) {
      const uint32_t udata = LoadU32(base_, Slot(-1));
      const uint32_t boxed = Plausible(udata) ? LoadU32(base_, udata + kUdataPayload) : 0u;
      ok = Plausible(boxed) && ReadVector(boxed, around, within, xyz);
    }
    SetTop(-2);
    return ok;
  }

  /// `self[name]` when it is a finite CVector3, without proximity filtering.
  bool VectorAny(const char* name, float* xyz) {
    GetField(-1, name);
    bool ok = false;
    if (Tag(-1) == kTagUserdata) {
      const uint32_t udata = LoadU32(base_, Slot(-1));
      const uint32_t boxed = Plausible(udata) ? LoadU32(base_, udata + kUdataPayload) : 0u;
      if (Plausible(boxed)) {
        float v[3];
        bool finite = true;
        for (uint32_t i = 0; i < 3u; ++i) {
          v[i] = LoadF32(base_, boxed + i * 4u);
          finite = finite && std::isfinite(v[i]);
        }
        if (finite) {
          for (uint32_t i = 0; i < 3u; ++i) {
            xyz[i] = v[i];
          }
          ok = true;
        }
      }
    }
    SetTop(-2);
    return ok;
  }

  /// Whether `self.OverriddenData[name]` is set (CameraBase:GetOverriddenData's table).
  bool Overridden(const char* name) {
    GetField(-1, "OverriddenData");
    bool set = false;
    if (Tag(-1) == kTagTable) {
      GetField(-1, name);
      set = Tag(-1) != kTagNil;
      SetTop(-2);
    }
    SetTop(-2);
    return set;
  }

 private:
  bool ReadVector(uint32_t at, const float* around, float within, float* xyz) const {
    if (!Plausible(at)) {
      return false;
    }
    float v[3];
    for (uint32_t i = 0; i < 3u; ++i) {
      v[i] = LoadF32(base_, at + i * 4u);
      if (!std::isfinite(v[i])) {
        return false;
      }
    }
    const float dx = v[0] - around[0];
    const float dy = v[1] - around[1];
    const float dz = v[2] - around[2];
    if (dx * dx + dy * dy + dz * dz > within * within) {
      return false;
    }
    for (uint32_t i = 0; i < 3u; ++i) {
      xyz[i] = v[i];
    }
    return true;
  }

  PPCContext Work() const {
    PPCContext w = work_;
    w.r3.u32 = L_;
    return w;
  }

  /// registry[*(*(holder)+4)] pushed, as `sub_82241C08(holder, L)` does for the camera table.
  bool PushSelf() {
    if (!Room(1u)) {
      return false;
    }
    PPCContext w = work_;
    w.r3.u32 = self_holder_;
    w.r4.u32 = L_;
    rex_LuaRef_Push(w, base_);
    return true;
  }

  uint32_t Stage(const char* text) {
    const size_t n = std::strlen(text);
    std::memcpy(base_ + names_, text, n);
    base_[names_ + n] = 0;
    return names_;
  }

  uint32_t Top32() const { return LoadU32(base_, L_ + kLuaTop); }
  void SetTop32(uint32_t top) { StoreU32(base_, L_ + kLuaTop, top); }

  /// Whether `slots` more values fit before stack_last (the guest's own check, 0x822A6278).
  bool Room(uint32_t slots) const {
    return Top32() + slots * 8u <= LoadU32(base_, L_ + kLuaStackLast);
  }

  /// The slot at a negative index from the top (-1 the top).
  uint32_t Slot(int32_t index) const {
    return Top32() + static_cast<uint32_t>(static_cast<int32_t>(index) * 8);
  }

  int32_t Top() const {
    return static_cast<int32_t>((Top32() - LoadU32(base_, L_ + kLuaBase)) / 8u);
  }

  /// lua_settop: a negative index pops down to it, a non-negative one sets the count from base.
  void SetTop(int32_t index) {
    if (index < 0) {
      SetTop32(Top32() + static_cast<uint32_t>((index + 1) * 8));
    } else {
      SetTop32(LoadU32(base_, L_ + kLuaBase) + static_cast<uint32_t>(index) * 8u);
    }
  }

  uint32_t Tag(int32_t index) const { return LoadU32(base_, Slot(index) + 4u); }

  void GetField(int32_t index, const char* name) {
    PPCContext w = Work();
    w.r4.s64 = index;
    w.r5.u32 = Stage(name);
    rex_lua_getfield(w, base_);
  }

  void SetField(int32_t index, const char* name) {
    PPCContext w = Work();
    w.r4.s64 = index;
    w.r5.u32 = Stage(name);
    rex_lua_setfield(w, base_);
  }

  PPCContext work_;
  uint8_t* base_;
  uint32_t names_ = 0u;
  uint32_t L_ = 0u;
  uint32_t self_holder_ = 0u;
  int32_t top_ = 0;
};

/// `_G[name] = nil` on `L`, below the caller's stack pointer.
void ClearGlobal(PPCContext& ctx, uint8_t* base, uint32_t L, const char* name) {
  if (!Plausible(L)) {
    return;
  }
  const uint32_t top = LoadU32(base, L + kLuaTop);
  const uint32_t last = LoadU32(base, L + kLuaStackLast);
  if (top + 16u > last) {
    return;
  }
  PPCContext w = Frame(ctx, 0x200u);
  const uint32_t text = w.r1.u32 + 0x100u;
  const size_t n = std::strlen(name);
  std::memcpy(base + text, name, n);
  base[text + n] = 0;
  StoreU32(base, top, 0u);
  StoreU32(base, top + 4u, kTagNil);
  StoreU32(base, L + kLuaTop, top + 8u);
  w.r3.u32 = L;
  w.r4.s64 = kGlobalsIndex;
  w.r5.u32 = text;
  rex_lua_setfield(w, base);
}

/// The camera's look integrator, by the class constants only that integrator reads.
Kind KindOf(CameraScript& script) {
  double value = 0.0;
  if (script.Number("RotateDamp", &value)) {
    return Kind::kStringAndCage;
  }
  if (script.Number("RotateSmoothAmount", &value)) {
    return Kind::kFirstPerson;
  }
  if (script.Number("TendnessNormal", &value)) {
    return Kind::kRangedAim;
  }
  return Kind::kOther;
}

/// What the update in flight was handed and what must be taken back out after it: guest tick
/// thread only, between Before and After.
struct Update {
  bool open = false;
  uint32_t mode = 0u;
  Kind kind = Kind::kOther;
  float taken = 0.0f;
  float taken_pitch = 0.0f;
  float held = 0.0f;
  float absorbed = 0.0f;
  float factor = 0.0f;
  int32_t draw = kModeNone;
  uint32_t aim = 0u;
  uint32_t hero = 0u;
  bool inverted = false;
  bool host_aim = false;
  float host_fwd[3] = {0.0f, 0.0f, 0.0f};
  uint32_t published = 0u;
  double smooth_added = 0.0;
  double smoothed_dx = 0.0;
  double smoothed_dy = 0.0;
  bool smoothed = false;
};

Update g_update;

/// The mode whose last handed turn was refused: drawn with no turn ahead until one is accepted
/// (its Lua gates — GetCanRotateCamera, a target lock, the input bit — are seen only on a hand-over).
uint32_t g_refused = 0u;

/// Camera updates seen: the account's tick (the host reads it as a sequence, HostBridge.GetMouseLook).
uint32_t g_updates = 0u;

/// The first accepted and the first refused hand-over, logged once each: the run's evidence.
bool g_logged_accepted = false;
bool g_logged_refused = false;

/// The hero whose camera the mouse drives (the manager the guest updates itself), as of its last
/// update: only that hero's aim takes the mouse.
uint32_t g_mouse_hero = 0u;
uint32_t g_rigid_hero = 0u;

/// The last aim that moved its cursor for the mouse's hero, and the camera update count it did so
/// at. An aim seen at this count or the one before is running — the two updates' order within a
/// tick is not relied on.
uint32_t g_aim_hero = 0u;
uint32_t g_aim_obj = 0u;
uint32_t g_aim_update = 0u;
bool g_logged_aim = false;

bool AimRunning(uint32_t hero) {
  return hero != 0u && hero == g_aim_hero && g_updates - g_aim_update <= 1u;
}

TargetLockFlickTracker g_flick;

/// Under a target lock: the mouse's turn becomes at most one stick flick (see the header).
void FlickTheLock(CameraScript& script, uint32_t mode, float yaw) {
  const int direction = g_flick.Collect(mode, yaw);
  if (direction != 0) {
    script.Send(mode, kRotateCamera, static_cast<float>(direction), 0.0f);
    refii_bridge_mouse_look_flicked();
  }
}

// SACCamera:ProcessGameAction (stringandcagecamera.lua main.117) is Fable 2's lock gate, read from the
// Kore disassembly:
//   pc  80..95   LastRotateFrame nil, or `LastRotateFrame + 1 < Timing.GetProgramFrame()`:
//                FirstRotateFrame = now, ClearToRotate = true
//   pc  96..102  else |x| < 0.8: ClearToRotate = true
//   pc 103..106  LastRotateFrame = Timing.GetProgramFrame()
//   pc 107..133  IsTargetLocking: direction = (x < 0); ClearToRotate and |x| >= 0.8 ->
//                Targeting.CheckForBetterTargetInDirection(self.Player, direction, 20), GrabTarget,
//                ClearToRotate = false
// So a notch is ONE ROTATE of x = +-1 through the receiver (the flick's own Send), sent only when it
// would switch now (ClearToSwitch below); at most one notch waits for the next update. Down +1 the
// next target right, up -1 left: refii_bridge_mouse_look_take_wheel's sign, as Fable 2's.
// Timing.GetProgramFrame (Lua_Timing_GetProgramFrame TU3 0x8225A750 / disc 0x8225B078 exact):
//   TU3  8225a758 lwz r10,0xC(r11) / 8225a75c lwz r3,0xB0(r10)      (r11 = dword_83597034, kGame)
//   disc 8225b080 lwz r10,0xC(r11) / 8225b084 lwz r3,0xB0(r10)      (r11 = dword_83556540)
// The Lua compares 32-bit floats: the frame is compared as one.

/// The game state's frame counter Timing.GetProgramFrame answers.
constexpr uint32_t kStateProgramFrame = 0xB0u;

uint32_t ProgramFrame(const uint8_t* base) {
  const uint32_t state = Word(base, Word(base, kGame) + 0xCu);  // as mouse_look reads the state (line 283)
  return Plausible(state) ? LoadU32(base, state + kStateProgramFrame) : 0u;
}

/// The heroes whose lock a mod states switches by the wheel: the frame each was last stated on, and
/// whether the statement is sticky.
struct WheelStated {
  uint32_t hero = 0u;
  uint32_t frame = 0u;
  bool sticky = false;
};

std::array<WheelStated, 4> g_wheel_stated{};

/// A statement holds for the frame it was made on and the two after it, so a lock stated each tick
/// never lapses between the mod's tick and the camera's update.
constexpr uint32_t kWheelStatementFrames = 2u;

bool WheelSwitches(const uint8_t* base, uint32_t hero) {
  if (!Plausible(hero)) {
    return false;
  }
  const uint32_t frame = ProgramFrame(base);
  for (const WheelStated& stated : g_wheel_stated) {
    if (stated.hero == hero &&
        (stated.sticky || (frame >= stated.frame && frame - stated.frame <= kWheelStatementFrames))) {
      return true;
    }
  }
  return false;
}

/// Sticky statements end with their hero's lock (or the hero): checked each camera update.
void ExpireWheelStatements(PPCContext& ctx, uint8_t* base) {
  for (WheelStated& stated : g_wheel_stated) {
    // Live: +0x94 & 0x40, the test Camera.GetFOV's trampoline makes (TU3 826a65f0 `lbz r11,0x94(r4)`,
    // 826a65f4 `rlwinm r10,r11,0,25,25`).
    const bool live = Plausible(stated.hero) && (base[stated.hero + 0x94u] & 0x40u) != 0u;
    if (stated.hero != 0u && stated.sticky && (!live || !TargetLocked(ctx, base, stated.hero))) {
      stated = WheelStated{};
    }
  }
}

WheelSwitchQueue g_wheel_switch;

/// Whether a ROTATE of |x| 1 sent now would switch target (the gate above).
bool ClearToSwitch(CameraScript& script, const uint8_t* base) {
  double last = 0.0;
  if (!script.Number("LastRotateFrame", &last)) {
    return true;
  }
  if (static_cast<float>(last) + 1.0f < static_cast<float>(ProgramFrame(base))) {
    return true;
  }
  return script.Truth("ClearToRotate");
}

/// Under a lock a mod states switches by the wheel: this update's notches become at most one switch.
void SwitchByWheel(CameraScript& script, const uint8_t* base, uint32_t mode, int32_t notches) {
  int send_dir = 0;
  if (g_wheel_switch.Step(mode, notches, ClearToSwitch(script, base), &send_dir)) {
    script.Send(mode, kRotateCamera, static_cast<float>(send_dir), 0.0f);
    refii_bridge_mouse_look_flicked();
  }
}

/// SAC: the rotate through the mode's receiver, made whole — or, under a target lock, a flick.
/// Sets the update's account.
void HandToStringAndCage(PPCContext& ctx, CameraScript& script, uint8_t* base, uint32_t mode,
                         uint32_t hero, float yaw, float pitch, int32_t wheel, const WheelSwitchQueue& waiting,
                         Update& update) {
  update.factor = 1.0f;
  const bool locked = TargetLocked(ctx, base, hero);
  double speed = 0.0;
  const double rate = static_cast<float>(LoadF64(base, kTickRate));
  if (!script.Number("RotateSpeed", &speed) || !(rate > 0.0) || !(speed > 0.0)) {
    update.draw = kModeNone;
    return;
  }

  // x in the receiver's units: a posted degree is a degree of turn at sensitivity 0.5.
  const double per_unit = speed / rate;
  const float x = static_cast<float>(yaw / per_unit);
  float y = static_cast<float>(pitch / per_unit);
  const bool height = std::fabs(y) > kHeightDeadZone;
  if (!height) {
    update.held = pitch;
    y = 0.0f;
  }

  if (locked) {
    if (WheelSwitches(base, hero)) {
      // The lock holds still under the mouse's movement; a mod stated the wheel switches it.
      g_flick.Reset();
      g_wheel_switch = waiting;
      SwitchByWheel(script, base, mode, wheel);
    } else {
      FlickTheLock(script, mode, yaw);
    }
    g_refused = mode;
  } else if (x != 0.0f) {
    g_flick.ClearSpent();
    double before = 0.0;
    double after = 0.0;
    const bool had = script.Number("RotateRight", &before);
    script.Send(mode, kRotateCamera, x, 0.0f);
    if (script.Number("RotateRight", &after) && (!had || after != before)) {
      script.SetNumber("SmoothRotateRight", script.NumberOr("SmoothRotateRight", 0.0) + after);
      update.smooth_added = after;
      update.absorbed = static_cast<float>(-after);
      if (std::fabs(yaw) > 1e-3f) {
        update.factor = static_cast<float>(-after / yaw);
      }
      g_refused = 0u;
    } else {
      g_refused = mode;
    }
  }

  if (height) {
    script.Send(mode, kZoomCamera, 0.0f, y);
  }
  update.draw = g_refused == mode ? kModeNone : kModeOrbit;
}

/// First person: the rotate through the receiver, then moved past its clamp and easing. A camera
/// bound to a target keeps its own path whole, and is drawn as it states it.
void HandToFirstPerson(CameraScript& script, uint32_t mode, float yaw, float pitch, Update& update) {
  double speed = 0.0;
  double smooth = 0.0;
  if (!script.Number("RotateSpeed", &speed) || !script.Number("RotateSmoothAmount", &smooth) ||
      !(speed > 0.0)) {
    update.draw = kModeNone;
    return;
  }

  const bool own = smooth <= 0.0 || script.Overridden("BoundTarget") ||
                   script.Overridden("BoundEntity");
  update.factor = 1.0f;
  if (yaw != 0.0f || pitch != 0.0f) {
    const double rx0 = script.NumberOr("RotateX", 0.0);
    const double ry0 = script.NumberOr("RotateY", 0.0);
    script.Send(mode, kRotateCamera, static_cast<float>(yaw / speed), static_cast<float>(pitch / speed));
    const double dx = script.NumberOr("RotateX", 0.0) - rx0;
    const double dy = script.NumberOr("RotateY", 0.0) - ry0;
    if (dx == 0.0 && dy == 0.0) {
      g_refused = mode;
    } else {
      g_refused = 0u;
      if (!own) {
        const double scale = (smooth + 1.0) / smooth;
        script.SetNumber("RotateX", rx0);
        script.SetNumber("RotateY", ry0);
        script.SetNumber("SmoothedRotateX", script.NumberOr("SmoothedRotateX", 0.0) + dx * scale);
        script.SetNumber("SmoothedRotateY", script.NumberOr("SmoothedRotateY", 0.0) + dy * scale);
        update.smoothed = true;
        update.smoothed_dx = dx;
        update.smoothed_dy = dy;
        update.absorbed = static_cast<float>(dx * speed);
      }
    }
  }
  update.draw = own || g_refused == mode ? kModeNone : kModeInPlace;
}

/// Before the manager's update: hands the mouse's pending turn to the mode it is about to run.
void BeforeCameraUpdate(PPCContext& ctx, uint8_t* base, uint32_t manager) {
  g_update = Update{};
  // The wheel, taken once per update: it switches a lock a mod states switches by it
  // (Refii.wheel_switches_target), else it is dropped, as on Fable 2. A waiting notch is kept only by
  // an update of a wheel-switched lock.
  const int32_t wheel = refii_bridge_mouse_look_take_wheel();
  const WheelSwitchQueue waiting = g_wheel_switch;
  g_wheel_switch.Reset();
  ExpireWheelStatements(ctx, base);

  const uint32_t mode = ActiveCamera(base, manager);
  const uint32_t hero = HeroOf(base, mode);
  g_mouse_hero = hero;
  if (mode != 0u && TakesInput(base) && AimRunning(hero) && g_aim_obj != 0u) {
    CameraScript script(ctx, base, mode);
    if (script.open() && KindOf(script) == Kind::kRangedAim) {
      if (refii_bridge_mouse_look_device() != 0 && !TargetLocked(ctx, base, hero) &&
          AimFreeAndUnlocked(base, g_aim_obj)) {
        float yaw = 0.0f;
        float pitch = 0.0f;
        refii_bridge_mouse_look_take(&yaw, &pitch);
        g_update.open = true;
        g_update.mode = mode;
        g_update.kind = Kind::kRangedAim;
        g_update.aim = g_aim_obj;
        g_update.hero = hero;
        g_update.taken = yaw;
        g_update.taken_pitch = pitch;
        g_update.factor = 1.0f;
        g_update.inverted = LookInverted(base, hero);
        g_update.draw = kModeAim;
        script.Publish(kDrivenCamera);
        g_update.published = script.state();

        float hfx = 0.0f, hfy = 0.0f, hfz = 0.0f;
        if (refii_bridge_mouse_look_host_aim(&hfx, &hfy, &hfz) != 0) {
          g_update.host_aim = true;
          g_update.host_fwd[0] = hfx;
          g_update.host_fwd[1] = hfy;
          g_update.host_fwd[2] = hfz;
          script.SetNumber("RefiiHostAimX", hfx);
          script.SetNumber("RefiiHostAimY", hfy);
          script.SetNumber("RefiiHostAimZ", hfz);
          script.SetNil("RefiiAimYaw");
          script.SetNil("RefiiAimTilt");
        } else {
          script.SetNil("RefiiHostAimX");
          script.SetNil("RefiiHostAimY");
          script.SetNil("RefiiHostAimZ");
          script.SetNumber("RefiiAimYaw", yaw);
          script.SetNumber("RefiiAimTilt", g_update.inverted ? -pitch : pitch);
        }
        return;
      }
      g_rigid_hero = 0u;
      // Snapped/locked or controller: leave the turn for rex_lhRangedAim_MoveCursor.
      return;
    }
  }
  g_rigid_hero = 0u;

  float yaw = 0.0f;
  float pitch = 0.0f;
  refii_bridge_mouse_look_take(&yaw, &pitch);
  g_update.open = true;
  g_update.taken = yaw;
  g_update.taken_pitch = pitch;
  g_update.inverted = Plausible(hero) && LookInverted(base, hero);

  if (mode == 0u || !TakesInput(base)) {
    // No camera to turn, or the camera takes no input now: the turn is spent, not stored up.
    return;
  }

  CameraScript script(ctx, base, mode);
  if (!script.open()) {
    return;
  }
  g_update.mode = mode;
  g_update.kind = KindOf(script);
  if (g_refused != 0u && g_refused != mode) {
    g_refused = 0u;
  }

  switch (g_update.kind) {
    case Kind::kStringAndCage:
      if (refii_bridge_mouse_look_device() != 0) {
        script.Publish(kDrivenCamera);
        g_update.published = script.state();
      }
      HandToStringAndCage(ctx, script, base, mode, hero, yaw, pitch, wheel, waiting, g_update);
      break;
    case Kind::kFirstPerson:
      HandToFirstPerson(script, mode, yaw, pitch, g_update);
      break;
    case Kind::kRangedAim:
    case Kind::kOther:
      break;
  }
}

/// After the update: takes the mouse's turn back out of the camera's easing and states the
/// account.
void AfterCameraUpdate(PPCContext& ctx, uint8_t* base, uint32_t manager) {
  ++g_updates;
  if (!g_update.open) {
    return;
  }
  Update update = g_update;
  g_update = Update{};
  if (update.published != 0u) {
    ClearGlobal(ctx, base, update.published, kDrivenCamera);
  }

  // An override the update dropped (manager+60 cleared at 0x82255560) is gone with its state.
  const bool current = update.mode != 0u && update.mode == ActiveCamera(base, manager);
  if (update.kind == Kind::kRangedAim) {
    static const char* const kAimFields[] = {
        "RefiiHostAimX", "RefiiHostAimY", "RefiiHostAimZ", "RefiiHostAimApplied",
        "RefiiAimYaw",   "RefiiAimTilt",  "RefiiAimPivot"};
    float head[3] = {0.0f, 0.0f, 0.0f};
    float pivot[3] = {0.0f, 0.0f, 0.0f};
    float limit = 0.0f;
    bool has_head = false;
    bool host_applied = false;
    {
      CameraScript script(ctx, base, current ? update.mode : 0u);
      if (script.open()) {
        has_head = script.VectorAny("RefiiAimPivot", head);
        host_applied = has_head && script.Truth("RefiiHostAimApplied");
        limit = static_cast<float>(script.NumberOr("AngleLimit", 0.0) * 180.0 / 3.14159265358979323846);
        for (const char* field : kAimFields) {
          script.SetNil(field);
        }
      }
    }
    if (current && has_head) {
      g_rigid_hero = update.hero;
      float stated[11];
      const bool has_cam = refii_bridge_camera_read(stated) != 0;
      if (host_applied && has_cam && Plausible(update.aim)) {
        const float cam_pos[3] = {stated[0], stated[1], stated[2]};
        float pt[3] = {
            LoadF32(base, update.aim + kAimPoint),
            LoadF32(base, update.aim + kAimPoint + 4u),
            LoadF32(base, update.aim + kAimPoint + 8u),
        };
        if (AlignPointAlongRay(cam_pos, update.host_fwd, pt)) {
          StoreF32(base, update.aim + kAimPoint, pt[0]);
          StoreF32(base, update.aim + kAimPoint + 4u, pt[1]);
          StoreF32(base, update.aim + kAimPoint + 8u, pt[2]);
        }
      }
      if (has_cam) {
        const float fx = stated[6];
        const float fy = stated[7];
        const float level = std::sqrt(fx * fx + fy * fy);
        const float ox = head[0] - stated[0];
        const float oy = head[1] - stated[1];
        const float oz = head[2] - stated[2];
        if (level > 1e-4f && std::isfinite(ox) && std::isfinite(oy) && std::isfinite(oz) &&
            ox * ox + oy * oy + oz * oz < 25.0f * 25.0f) {
          pivot[0] = (ox * fx + oy * fy) / level;
          pivot[1] = (ox * fy - oy * fx) / level;
          pivot[2] = oz;
        }
      }
      const uint32_t flags = (update.inverted ? kFlagLookInverted : 0u) |
                             (host_applied ? kFlagHostAimApplied : 0u) | kFlagHasHead;
      refii_bridge_mouse_look_state_ex(g_updates, update.taken, update.taken_pitch, 0.0f, 0.0f, 0.0f,
                                       1.0f, pivot[0], pivot[1], pivot[2], limit, kModeAim,
                                       update.aim, head[0], head[1], head[2], flags);
    } else {
      g_rigid_hero = 0u;
      const uint32_t flags = update.inverted ? kFlagLookInverted : 0u;
      refii_bridge_mouse_look_state_ex(g_updates, update.taken, update.taken_pitch, 0.0f, 0.0f, 0.0f,
                                       1.0f, 0.0f, 0.0f, 0.0f, 0.0f, kModeNone, 0u, 0.0f, 0.0f, 0.0f,
                                       flags);
    }
    return;
  }

  float pivot = 0.0f;
  {
    CameraScript script(ctx, base, current ? update.mode : 0u);
    if (script.open() && update.smooth_added != 0.0) {
      script.SetNumber("SmoothRotateRight",
                       script.NumberOr("SmoothRotateRight", 0.0) - update.smooth_added);
      // ApplyRotateVector zeroes RotateRight when it turned; a camera without its PreBound
      // positions yet did not, and the turn handed to it is taken back out unspent.
      const double left = script.NumberOr("RotateRight", 0.0);
      if (left != 0.0) {
        script.SetNumber("RotateRight", left - update.smooth_added);
        update.absorbed = 0.0f;
      }
    }
    if (script.open() && update.smoothed) {
      script.SetNumber("SmoothedRotateX", script.NumberOr("SmoothedRotateX", 0.0) - update.smoothed_dx);
      script.SetNumber("SmoothedRotateY", script.NumberOr("SmoothedRotateY", 0.0) - update.smoothed_dy);
    }

    if (update.draw == kModeOrbit) {
      // The camera looks at RollingFocalPos from its placed position: the turn's vertical axis
      // lies on the level line of sight, this far out. The position is the camera the shell last
      // drew (its rebuild of the manager's block, stated back by RefiiHostStateCamera); the
      // focus moves metres per second, so last frame's camera places the pivot to within the
      // frame's motion.
      float stated[11];
      float focus[3] = {0.0f, 0.0f, 0.0f};
      if (script.open() && refii_bridge_camera_read(stated) != 0 &&
          script.Vector("RollingFocalPos", stated, 200.0f, focus)) {
        const float dx = focus[0] - stated[0];
        const float dy = focus[1] - stated[1];
        pivot = std::sqrt(dx * dx + dy * dy);
      }
      if (!(pivot > 0.05f && pivot < 200.0f)) {
        update.draw = kModeNone;
        pivot = 0.0f;
      }
    }
  }
  if (!current) {
    update.draw = kModeNone;
  }

  if (update.taken != 0.0f || update.taken_pitch != 0.0f) {
    const bool accepted = update.absorbed != 0.0f;
    if (accepted && !g_logged_accepted) {
      g_logged_accepted = true;
      REXLOG_INFO("[mouse-look] first turn taken: mode 0x{:08X} kind {} yaw {:.2f} pitch {:.2f} "
                  "absorbed {:.2f} factor {:.3f} draw {} pivot {:.2f} m",
                  update.mode, static_cast<int>(update.kind), update.taken, update.taken_pitch,
                  update.absorbed, update.factor, update.draw, pivot);
    } else if (!accepted && !g_logged_refused) {
      g_logged_refused = true;
      REXLOG_INFO("[mouse-look] first turn refused: mode 0x{:08X} kind {} yaw {:.2f} pitch {:.2f} "
                  "(no camera, the debug camera, a gate in the script, or a non-look camera)",
                  update.mode, static_cast<int>(update.kind), update.taken, update.taken_pitch);
    }
  }

  const uint32_t flags = update.inverted ? kFlagLookInverted : 0u;
  refii_bridge_mouse_look_state_ex(g_updates, update.taken, update.taken_pitch, update.held,
                                   update.absorbed, 0.0f, update.factor, pivot, 0.0f, 0.0f, 0.0f,
                                   update.draw, 0u, 0.0f, 0.0f, 0.0f, flags);
}

}  // namespace

namespace refii::fable3::mouselook {

void StateWheel(const uint8_t* base, uint32_t hero, int32_t mode) {
  if (!Plausible(hero)) {
    return;
  }
  WheelStated* slot = nullptr;
  for (WheelStated& stated : g_wheel_stated) {
    if (stated.hero == hero) {
      slot = &stated;
      break;
    }
  }
  if (mode == 0) {
    if (slot != nullptr) {
      *slot = WheelStated{};
    }
    return;
  }
  const uint32_t frame = ProgramFrame(base);
  if (slot == nullptr) {
    for (WheelStated& stated : g_wheel_stated) {
      // A free slot, else the oldest lapsed tick statement (a sticky one is never taken).
      if (stated.hero == 0u || (!stated.sticky && frame - stated.frame > kWheelStatementFrames &&
                                (slot == nullptr || stated.frame < slot->frame))) {
        slot = &stated;
        if (stated.hero == 0u) {
          break;
        }
      }
    }
  }
  if (slot == nullptr) {
    return;  // four heroes already stated: more than any session has players
  }
  const bool sticky = mode == 2 || (slot->hero == hero && slot->sticky);
  *slot = WheelStated{hero, frame, sticky};
}

}  // namespace refii::fable3::mouselook

// The camera manager's update (r3 the manager): the mouse's turn handed before, its account
// stated after. Player two's manager in a couch game (coop_camera_hooks.cpp) takes no mouse: the mouse
// is player one's.
REX_EXTERN(rex_CameraManager_Update) {
  const uint32_t manager = ctx.r3.u32;
  if (manager != 0u && manager == refii::fable3::coopcam::PartnerManagerTicking()) {
    __imp__rex_CameraManager_Update(ctx, base);
    return;
  }
  BeforeCameraUpdate(ctx, base, manager);
  __imp__rex_CameraManager_Update(ctx, base);
  AfterCameraUpdate(ctx, base, manager);
}

// The ranged aim's cursor move (r3 the aim, r4 its stick: x in the high word, y in the low). For
// the mouse's hero's aim, everything the mouse turned since the last move joins the stick at the
// cursor's own gain (see the header), or stays centered when host aim drives the camera.
REX_EXTERN(rex_lhRangedAim_MoveCursor) {
  const uint32_t aim = ctx.r3.u32;
  // The aim's owner, as the move itself resolves it (`lwz r11,4(r31)` / lhSmartPtr_Deref at
  // 0x828AE9D8..0x828AEA04).
  const uint32_t hero = Resolve(base, aim);
  if (!Plausible(hero) || hero != g_mouse_hero || !TakesInput(base)) {
    __imp__rex_lhRangedAim_MoveCursor(ctx, base);
    return;
  }
  g_aim_hero = hero;
  g_aim_obj = aim;
  g_aim_update = g_updates;

  if (hero == g_rigid_hero && refii_bridge_mouse_look_device() != 0 &&
      AimFreeAndUnlocked(base, aim)) {
    const uint32_t xb = static_cast<uint32_t>(ctx.r4.u64 >> 32);
    const uint32_t yb = static_cast<uint32_t>(ctx.r4.u64);
    float sx = 0.0f;
    float sy = 0.0f;
    std::memcpy(&sx, &xb, sizeof(sx));
    std::memcpy(&sy, &yb, sizeof(sy));
    if (sx == 0.0f && sy == 0.0f) {
      StoreF32(base, aim + kAimCursorX, 0.0f);
      StoreF32(base, aim + kAimCursorY, 0.0f);
    }
    __imp__rex_lhRangedAim_MoveCursor(ctx, base);
    return;
  }

  float yaw = 0.0f;
  float pitch = 0.0f;
  refii_bridge_mouse_look_take(&yaw, &pitch);
  const bool inverted = LookInverted(base, hero);
  if (yaw != 0.0f || pitch != 0.0f) {
    // The stats the move reads its speed from: `sub_821B1390(owner)` (disc 0x828522E8..disc 0x828522F0).
    uint32_t stats = 0u;
    {
      PPCContext work = Frame(ctx);
      work.r3.u64 = hero;
      rex_Entity_RangedAimStats(work, base);
      stats = work.r3.u32;
    }
    const double rate = LoadF64(base, kTickRate);
    const double speed = Plausible(stats) ? LoadF32(base, stats + kAimStatsSpeed) : 0.0;
    const double gain_x = rate > 0.0 ? speed * LoadF32(base, kAimGainAcross) / rate : 0.0;
    const double gain_y = rate > 0.0 ? speed * LoadF32(base, kAimGainUp) / rate : 0.0;
    if (std::isfinite(gain_x) && std::isfinite(gain_y) && std::fabs(gain_x) > 1e-6 &&
        std::fabs(gain_y) > 1e-6) {
      constexpr double kRadians = 3.14159265358979323846 / 180.0;
      // Upward is screen-up, the cursor's -y, unless the camera is inverted (the stick's rule).
      const double up = inverted ? 1.0 : -1.0;
      float sx = 0.0f;
      float sy = 0.0f;
      const uint32_t xb = static_cast<uint32_t>(ctx.r4.u64 >> 32);
      const uint32_t yb = static_cast<uint32_t>(ctx.r4.u64);
      std::memcpy(&sx, &xb, sizeof(sx));
      std::memcpy(&sy, &yb, sizeof(sy));
      sx += static_cast<float>(yaw * kRadians / gain_x);
      sy += static_cast<float>(up * pitch * kRadians / gain_y);
      uint32_t xo = 0u;
      uint32_t yo = 0u;
      std::memcpy(&xo, &sx, sizeof(xo));
      std::memcpy(&yo, &sy, sizeof(yo));
      ctx.r4.u64 = (static_cast<uint64_t>(xo) << 32) | yo;
      if (!g_logged_aim) {
        g_logged_aim = true;
        REXLOG_INFO("[mouse-look] first aim move: aim 0x{:08X} yaw {:.2f} pitch {:.2f} gain {:.4f} "
                    "{:.4f} stick {:.3f} {:.3f}",
                    aim, yaw, pitch, gain_x, gain_y, sx, sy);
      }
    }
  }

  const uint32_t flags = inverted ? kFlagLookInverted : 0u;
  refii_bridge_mouse_look_state_ex(g_updates, yaw, pitch, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f,
                                   0.0f, kModeNone, 0u, 0.0f, 0.0f, 0.0f, flags);
  __imp__rex_lhRangedAim_MoveCursor(ctx, base);
}
