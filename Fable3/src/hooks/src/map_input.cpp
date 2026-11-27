// refii - Fable 3's world map as a game mode: stated when it takes and gives up the input, and its
// buttons handed to it by Godot as the player who owns it, for Fable3MapTable / Fable3MapInput. The
// scene it draws is map_table_hooks.cpp's. TU3 / disc; every body named here was read on both.
//
// THE MODE (CGameModeWorldMap, PC RTTI name; vtable TU3 0x82094918 / disc 0x82092E14, 9 slots; the
// object is `*(GUI+0x54)`). Slot 5 ENTER sub_824EBF68 / disc sub_824CC8D0 and slot 8 LEAVE
// sub_824EC590 / disc sub_824CCB58 are each one word of that vtable. The enter registers ONE of the
// mode's two input receivers with the input manager — player one's (`lwz r4,0x6C(r27)` 0x824EC060 /
// disc `lwz r4,0x68(r29)` 0x824CC9C0) when the hero the map was opened for is player one's or the
// game world's (`cmplw` / `beq` 0x824EC048..0x824EC054 / 0x824CC9A8..0x824CC9B4), else player two's
// (`lwz r4,0x74(r27)` 0x824EC058 / disc 0x70 0x824CC9B8) — through `bl sub_82531268` (0x824EC070 /
// disc sub_8250E1D0 0x824CC9D0). A receiver is {vtable 0x8209493C / 0x82092E38, +4 player, +8 map};
// its slot 4 answers the player (`lwz r3,4(r3)`). `@WorldMap.Entered(player)` from the receiver in r4
// at the instruction after the choice (a [[midasm_hook]] at 0x824EC064 / 0x824CC9C4), posted as the
// enter returns; `@WorldMap.Left()` at the leave's entry. The leave is also all the first half of
// GUI.ToggleWorldMap does to an OPEN map (0x8254D100 / 0x8251D310: it pops the mode, and the close
// sub_824ED850 / sub_824CDBB8 — Lua CloseMap, the scene un-presented — never runs): a map left that
// way stays drawn with nothing reading its input, which Godot can now tell.
//
// THE RECEIVER'S TWO THUNKS (slots 1 and 6: `lwz r3,8(r3)` / `b`, TU3 0x824EB6A0 / 0x824EB6A8, disc
// 0x824CC0D0 / 0x824CC0D8) are the only callers of the two forwarders:
//   - GAME ACTIONS sub_824ED9E8 / disc sub_824CDD38 (map, action*): a 32-byte record, +0 the action
//     id, +4 / +8 / +12 / +16 floats (`lfs f11,4(r30)` .. `lfs f0,0x10(r30)` 0x824EDA74..0x824EDA8C).
//     While a queued box or the modal is up (`bl sub_821A38C8` 0x824EDA50 / disc sub_821A3D20
//     0x824CDDA0) it forwards only when the hero's answer record is newer than the map's last
//     (`lwz r10,0x5C(r3)` / `cmpw` 0x824EDA24..0x824EDA38) or an answer just arrived (`lwz r11,
//     0x68(r31)` 0x824EDA68); then `WorldMap:ProcessGameAction(id, f1..f4)` through sub_824F0DC0
//     (0x824EDAB0 / disc sub_824D1040). It takes no player.
//   - RAW sub_824EDAC0 / disc sub_824CDE10 (map, raw*): with nothing up, a record of type 14 (HELD,
//     `lwz r11,0x2C(r30)` / `cmpwi cr6,r11,0xE` 0x824EDAF0..0x824EDAF4 / 0x824CDE40..0x824CDE44) is
//     `WorldMap:ProcessButtonHeld(*(u8*)(raw+8))` (`lbz r11,8(r30)` 0x824EDAFC / 0x824CDE4C): the
//     shipped Lua tilts the camera on buttons 9 / 10 (LB / RB).
// GODOT OWNS THE MAP'S BUTTONS once the enter stated an owner: the pad's button actions and raw
// records are dropped at the two forwarders, and Godot's come as levers run through the same
// forwarders, refused unless they name the owner. `map:<p>:<id>` for 120 accept, 121 cancel, 123 /
// 124 the two context actions, 14 pause (`worldmap/input.lua`'s own compares); `map-held:<p>:<9|10>:
// <down|up>` for LB / RB, sent to the raw forwarder once a guest tick while down. STILL THE PAD'S:
// the analog actions 3, 118, 119, 122 (cursor, scroll, orbit) and 125 (the trigger's toggle) pass
// through as the guest built them — the scale and sign of their floats and the trigger's threshold
// are the control scheme's (sub_829A2D18, not read), so Godot does not make them yet.
//
// CENSUS, one line each: a pad action dropped or passed, a lever with the forwarder's own box test
// (the value `bl sub_821A38C8` returned, a [[midasm_hook]] at 0x824EDA54 / 0x824CDDA4 on r3),
// WorldMap.Exit sub_824ED768 / disc sub_824CDB60 (map[, arg]) — which only sets the request byte the
// mode's update acts on — and the close sub_824ED850 / disc sub_824CDBB8 (map).

#include <atomic>
#include <cstdint>
#include <cstdlib>
#include <cstring>

#include <rex/logging.h>
#include <rex/ppc.h>
#include <rex/system/kernel_state.h>
#include <rex/system/xmemory.h>

#include "box_answers.h"
#include "gui_statements.h"
#include "mod_seam.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_WorldMap_Enter);
REX_EXTERN(__imp__rex_WorldMap_Leave);
REX_EXTERN(__imp__rex_WorldMap_Exit);
REX_EXTERN(__imp__rex_WorldMap_Close);
REX_EXTERN(__imp__rex_WorldMap_ForwardAction);
REX_EXTERN(__imp__rex_WorldMap_ForwardRaw);
REX_EXTERN(rex_WorldMap_ForwardAction);
REX_EXTERN(rex_WorldMap_ForwardRaw);

namespace {

using namespace refii::fable3;

/// The mode whose enter last stated an owner, and that owner (1 or 2); 0 once it left.
std::atomic<uint32_t> g_map{0u};
std::atomic<uint32_t> g_owner{0u};
/// LB (bit 0) and RB (bit 1) held by the owner, as `map-held:` stated them.
std::atomic<uint32_t> g_held{0u};

/// The receiver the running enter registered (its player), on this thread.
thread_local uint32_t t_registered = 0u;
/// Set while a lever runs a forwarder, so the cut lets it through.
thread_local bool t_host_input = false;
/// The forwarder's own "a box is up" answer for the lever in hand.
thread_local int t_box_up = -1;

std::atomic<uint64_t> g_pad_dropped{0u};
std::atomic<uint64_t> g_pad_passed{0u};
std::atomic<uint64_t> g_raw_dropped{0u};

constexpr uint32_t kReceiverPlayer = 4u;
constexpr uint32_t kRawButton = 8u;
constexpr uint32_t kRawType = 0x2Cu;
constexpr uint32_t kRawHeld = 14u;
constexpr uint32_t kRawBytes = 72u;
constexpr uint32_t kActionBytes = 32u;
constexpr uint32_t kButtonLeftBumper = 9u;
constexpr uint32_t kButtonRightBumper = 10u;

/// The analog actions the pad still makes (EGameAction: CAMERA_MOVEMENT 3, WORLD_MAP_MOVE_CURSOR 118,
/// WORLD_MAP_SCROLL_MAP 119, WORLD_MAP_ORBIT_CAMERA 122, WORLD_MAP_MOVE_MAP_ITEM_TOGGLE 125).
bool StillThePads(uint32_t action) {
  return action == 3u || action == 118u || action == 119u || action == 122u || action == 125u;
}

/// The actions Godot hands the map (WORLD_MAP_ACCEPT 120, _CANCEL 121, the context actions 123 /
/// 124, PAUSE 14).
bool GodotMakes(long action) {
  return action == 120 || action == 121 || action == 123 || action == 124 || action == 14;
}

const uint8_t* GuestBase() {
  rex::system::KernelState* state = rex::system::kernel_state();
  rex::memory::Memory* memory = state != nullptr ? state->memory() : nullptr;
  return memory != nullptr ? memory->virtual_membase() : nullptr;
}

/// `<p>:` at `at`; the text after it, or null.
const char* TakePlayer(const char* at, uint32_t& player) {
  if ((at[0] != '1' && at[0] != '2') || at[1] != ':') {
    return nullptr;
  }
  player = static_cast<uint32_t>(at[0] - '0');
  return at + 2;
}

void SendHeld(PPCContext& ctx, uint8_t* base, uint32_t map, uint32_t button) {
  PPCContext work = refii::fable3::seam::Frame(ctx);
  const uint32_t raw = work.r1.u32 + 0x100u;
  std::memset(base + raw, 0, kRawBytes);
  base[raw + kRawButton] = static_cast<uint8_t>(button);
  refii::fable3::seam::StoreU32(base, raw + kRawType, kRawHeld);
  work.r3.u64 = map;
  work.r4.u64 = raw;
  t_host_input = true;
  rex_WorldMap_ForwardRaw(work, base);
  t_host_input = false;
}

}  // namespace

namespace refii::fable3 {

bool AnswerWorldMap(PPCContext& ctx, uint8_t* base, const char* action) {
  if (base == nullptr || action == nullptr) {
    return false;
  }
  const bool held = std::strncmp(action, kWorldMapHeldAction, sizeof(kWorldMapHeldAction) - 1u) == 0;
  if (!held && std::strncmp(action, kWorldMapAction, sizeof(kWorldMapAction) - 1u) != 0) {
    return false;
  }
  uint32_t player = 0u;
  const char* at = TakePlayer(action + (held ? sizeof(kWorldMapHeldAction) : sizeof(kWorldMapAction)) - 1u, player);
  if (at == nullptr) {
    REXLOG_WARN("[fable3-map] '{}' names no player; dropped", action);
    return false;
  }
  const uint32_t map = g_map.load();
  const uint32_t owner = g_owner.load();
  if (map == 0u || owner == 0u) {
    REXLOG_INFO("[fable3-map] '{}': no world map mode has the input; refused", action);
    return false;
  }
  if (player != owner) {
    REXLOG_INFO("[fable3-map] '{}' refused: the map is player {}'s", action, owner);
    return false;
  }
  char* end = nullptr;
  const long value = std::strtol(at, &end, 10);
  if (held) {
    const uint32_t bit = value == static_cast<long>(kButtonLeftBumper) ? 1u : value == static_cast<long>(kButtonRightBumper) ? 2u : 0u;
    if (bit == 0u || end == nullptr || *end != ':') {
      return false;
    }
    if (std::strcmp(end + 1, "down") == 0) {
      g_held.fetch_or(bit);
    } else {
      g_held.fetch_and(~bit);
    }
    REXLOG_INFO("[fable3-map] '{}': the owner's held buttons are now {:#x}", action, g_held.load());
    return true;
  }
  if (!GodotMakes(value)) {
    REXLOG_WARN("[fable3-map] '{}': not an action Godot makes for the map; dropped", action);
    return false;
  }
  PPCContext work = refii::fable3::seam::Frame(ctx);
  const uint32_t record = work.r1.u32 + 0x100u;
  std::memset(base + record, 0, kActionBytes);
  refii::fable3::seam::StoreU32(base, record, static_cast<uint32_t>(value));
  work.r3.u64 = map;
  work.r4.u64 = record;
  t_box_up = -1;
  t_host_input = true;
  rex_WorldMap_ForwardAction(work, base);
  t_host_input = false;
  REXLOG_INFO("[fable3-map] '{}' handed to the map's forwarder as its owner ({})", action,
              t_box_up == 1   ? "a box is up: the guest forwards it only on a fresh answer"
              : t_box_up == 0 ? "nothing over the map"
                              : "the forwarder returned before its box test");
  return true;
}

void PumpWorldMap(PPCContext& ctx, uint8_t* base) {
  const uint32_t held = g_held.load();
  const uint32_t map = g_map.load();
  if (held == 0u || map == 0u || base == nullptr) {
    return;
  }
  if ((held & 1u) != 0u) {
    SendHeld(ctx, base, map, kButtonLeftBumper);
  }
  if ((held & 2u) != 0u) {
    SendHeld(ctx, base, map, kButtonRightBumper);
  }
}

}  // namespace refii::fable3

/// The enter's chosen receiver, in r4 at 0x824EC064 / disc 0x824CC9C4.
void RefiiF3WorldMapReceiver(PPCRegister& receiver) {
  const uint8_t* base = GuestBase();
  if (base != nullptr && Plausible(receiver.u32)) {
    t_registered = LoadGuestU32(base, receiver.u32 + kReceiverPlayer);
  }
}

/// The forwarder's box test as it returned, r3 at 0x824EDA54 / disc 0x824CDDA4.
void RefiiF3WorldMapBoxGate(PPCRegister& answer) {
  t_box_up = (answer.u32 & 0xFFu) != 0u ? 1 : 0;
}

/// (mode): `@WorldMap.Entered(player)` once the enter registered a receiver.
REX_EXTERN(rex_WorldMap_Enter) {
  const uint32_t mode = ctx.r3.u32;
  t_registered = 0u;
  __imp__rex_WorldMap_Enter(ctx, base);
  const uint32_t player = t_registered;
  t_registered = 0u;
  if (player == 1u || player == 2u) {
    g_map.store(mode);
    g_owner.store(player);
    REXLOG_INFO("[fable3-map] world map mode {:08X} entered: player {}'s receiver registered", mode, player);
    Statement("@WorldMap.Entered").Add(Number(static_cast<float>(player))).Post();
  }
}

/// (mode): `@WorldMap.Left()`.
REX_EXTERN(rex_WorldMap_Leave) {
  const uint32_t mode = ctx.r3.u32;
  if (g_map.load() == mode) {
    g_map.store(0u);
    g_owner.store(0u);
    g_held.store(0u);
  }
  REXLOG_INFO("[fable3-map] world map mode {:08X} left (pad: {} button actions and {} raw records dropped, {} analog passed)",
              mode, g_pad_dropped.load(), g_raw_dropped.load(), g_pad_passed.load());
  Statement("@WorldMap.Left").Post();
  __imp__rex_WorldMap_Leave(ctx, base);
}

/// (map[, arg]): the exit request, logged.
REX_EXTERN(rex_WorldMap_Exit) {
  REXLOG_INFO("[fable3-map] WorldMap.Exit asked of map {:08X}: its next update closes it", ctx.r3.u32);
  __imp__rex_WorldMap_Exit(ctx, base);
}

/// (map): the close, logged.
REX_EXTERN(rex_WorldMap_Close) {
  REXLOG_INFO("[fable3-map] the world map's close runs for map {:08X}", ctx.r3.u32);
  __imp__rex_WorldMap_Close(ctx, base);
}

/// (map, action*): Godot's lever passes; with an owner stated, the pad's button actions are dropped
/// and its analog ones pass.
REX_EXTERN(rex_WorldMap_ForwardAction) {
  if (t_host_input || g_owner.load() == 0u || base == nullptr || !Plausible(ctx.r4.u32)) {
    __imp__rex_WorldMap_ForwardAction(ctx, base);
    return;
  }
  const uint32_t action = LoadGuestU32(base, ctx.r4.u32);
  if (StillThePads(action)) {
    g_pad_passed.fetch_add(1u);
    __imp__rex_WorldMap_ForwardAction(ctx, base);
    return;
  }
  const uint64_t dropped = g_pad_dropped.fetch_add(1u);
  if (dropped < 64u) {
    REXLOG_INFO("[fable3-map] map action {} from the pad DROPPED (cut: the map's buttons are Godot's) #{}", action, dropped + 1u);
  }
}

/// (map, raw*): Godot's held record passes; with an owner stated, the pad's raw records are dropped.
REX_EXTERN(rex_WorldMap_ForwardRaw) {
  if (t_host_input || g_owner.load() == 0u) {
    __imp__rex_WorldMap_ForwardRaw(ctx, base);
    return;
  }
  if (g_raw_dropped.fetch_add(1u) == 0u) {
    REXLOG_INFO("[fable3-map] the pad's raw records no longer reach the map (cut: held LB / RB are Godot's)");
  }
}
