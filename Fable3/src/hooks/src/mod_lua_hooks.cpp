// refii - Fable 3's mod Lua: the mods' composed chunks run in the GAME VM after the shipped startup
// scripts, the Refii.* natives registered there, and the host's mod lanes (commands, input, the
// tick, the Lua subscription masks) served on that VM's own thread.
//
// Every address is the TU3 IDB's; the disc's is bound by the same name (Fable3/versions/Fable3).
//
// THE VM. The game VM ("Fable 3") is built by sub_822D9808: the old script system and state are
// deleted (lua_close), a new state made (sub_822E54D0), a 576-byte script system constructed on it
// (sub_8231B568: ss+0 = L, ss+477 = 0) and its namespaces registered. The UI's state ("Fable 3
// Anark", sub_823319E0) shares the same Kore runtime code, so nothing here hooks the runtime: the
// seams are the script system's own functions, which only the game VM's script system calls.
//
// THE SEAM. Game_InitDatabases (sub_8234BFC0) calls the startup scripts (sub_8231CAA8) on every
// component init; the body runs the save-load, general, AI, quest and camera scripts only while
// byte ss+477 is clear, then sets it:
//     8231cabc  if ( !*((_BYTE *)a1 + 477) ) { ... sub_822E5610(*a1, ...) x5 ...; *(a1+477) = 1; }
// So the mods' chunks run on that 0 -> 1 transition, once per VM, after every shipped script and
// native: Fable 2's position (Fable2/src/vfs/src/hooks/mod_lua_hooks.cpp). A VM kept across a
// component switch keeps the mods' state, as it keeps the game's.
//
// RUNNING A CHUNK. Not through the game's lhLua_RunScriptBuffer (sub_822E56E0), which copies the
// load error into a string it destroys and leaves a runtime error unread: luaL_loadbuffer
// (sub_822CFD30: L, G+0x288 load settings, buffer, size, name; nonzero with the message on top)
// then lua_pcall (sub_82236DD8: L, 0, 0, 0; nonzero with the message on top), the top restored
// after. The loader compiles SOURCE: sub_822A94F0 undumps only a first byte of 27 and hands
// anything else to the Kore lexer and parser (0x822a95a8), so the host's text runs as composed.
//
// A NATIVE. A host PPCFunc given a guest address (refii_bridge_rex_allocate_thunk), pushed with
// lua_pushcclosure (sub_822384F8: L, fn, nup, flags), bound with lua_setfield. Called with r3 = L,
// answering r3 = the result count (luaD_call's `mr r3,r31; mtctr; bctrl`). Between the push and the
// set, lhLuaNamespace_RegisterFunctionHash(holder, L, name) (0x82361988) keys the closure on top as
// FNV-1a("<namespace>_<name>") in PlutoPermanentsSaveTable / LoadTable, as it does for each of the
// game's natives — the namespace is `*holder`, its name the CCharString at +8, its enable byte +12 —
// so a Refii native a mod keeps in its saved state is written by name, never as a C function.
//
// THE STACK. L+0x1C top, L+0x20 base, L+0x24 stack_last; a TValue is {value, tag} (tags 0 nil,
// 1 boolean, 3 number, 4 string, 5 table). lua_pushnumber / pushboolean do not exist on TU3, so
// those are written in place as the game's own glue does, after the room check lua_setfield makes
// (`top + 16 > stack_last` -> luaD_growstack(L+4, L, 2), 0x822a6278). Numbers are float32.

#include "mod_lua.h"
#include "mod_seam.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <mutex>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <iterator>
#include <string>
#include <thread>
#include <vector>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "core/host_asset_reader.h"
#include "core/camera_views.h"
#include "core/mod_values.h"
#include "core/payload_bridge.h"
#include "core/refii_mod_bus.h"
#include "core/achievement_statement.h"
#include "core/refii_mod_requests.h"
#include "core/rex_payload_bridge.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(rex_SpellLightning_Electrify);
REX_EXTERN(rex_SpellChaos_ApplyToVictim);
REX_EXTERN(rex_SpellFireball_Ignite);
REX_EXTERN(rex_SpellSource_Ctor);
REX_EXTERN(rex_SpellSource_Dtor);
REX_EXTERN(__imp__rex_CECSpellManager_SetSpellSlot);
REX_EXTERN(__imp__rex_lhLuaScriptSystem_LoadStartupScripts);
REX_EXTERN(__imp__rex_lhLuaScriptSystem_LoadScriptSave);
REX_EXTERN(__imp__rex_lhLuaScriptSystem_RunCameraScripts);
REX_EXTERN(__imp__rex_lhLuaScriptSystem_RunPostLoadScripts);
REX_EXTERN(__imp__rex_lua_close);
REX_EXTERN(rex_luaL_loadbuffer);
REX_EXTERN(rex_lua_pcall);
REX_EXTERN(rex_lua_settop);
REX_EXTERN(rex_lua_type);
REX_EXTERN(rex_lua_tonumber);
REX_EXTERN(rex_lua_toboolean);
REX_EXTERN(rex_lua_tolstring);
REX_EXTERN(rex_lua_pushlstring);
REX_EXTERN(rex_lua_pushcclosure);
REX_EXTERN(rex_lua_rawgeti);
REX_EXTERN(rex_luaD_growstack);
REX_EXTERN(rex_lua_getfield);
REX_EXTERN(rex_lua_setfield);
REX_EXTERN(rex_lhLuaNamespace_RegisterFunctionHash);
REX_EXTERN(rex_lhCharString_Ctor);
REX_EXTERN(rex_lhCharString_Dtor);
REX_EXTERN(rex_lhNew);
REX_EXTERN(rex_lhDelete);
REX_EXTERN(rex_lhLua_PushEntity);
REX_EXTERN(rex_lhScriptEntityRef_Resolve);
REX_EXTERN(rex_lua_getmetatable);
REX_EXTERN(rex_lua_touserdata);
REX_EXTERN(__imp__rex_lhEntity_Destroy);
REX_EXTERN(__imp__rex_lhEntityManager_Add);
REX_EXTERN(rex_TopBoxMsg_Ctor);
REX_EXTERN(rex_TopBoxMsg_Dtor);
REX_EXTERN(rex_TopBox_Post);
REX_EXTERN(rex_LHString_FromWide);
REX_EXTERN(rex_LHString_Copy);
REX_EXTERN(rex_LHString_Dtor);
REX_EXTERN(rex_CECSprint_SetSprinting);
REX_EXTERN(rex_lhEntity_RemoveComponent);
REX_EXTERN(rex_World_EntityByUid);
REX_EXTERN(rex_lhEntity_UseInventoryItem);

namespace {

constexpr uint32_t kStateTop = 0x1Cu;
constexpr uint32_t kStateBase = 0x20u;
constexpr uint32_t kStateStackLast = 0x24u;
/// G(L) + 0x288: the load settings every luaL_loadbuffer caller passes (`*a1 + 648`, 0x822e5780).
constexpr uint32_t kGlobalLoadSettings = 0x288u;
/// The script system's startup-scripts-ran byte (0x8231cabc).
constexpr uint32_t kScriptSystemRan = 477u;

constexpr int32_t kGlobalsIndex = -10002;
constexpr uint32_t kTagNil = 0u;
constexpr uint32_t kTagBoolean = 1u;
constexpr uint32_t kTagNumber = 3u;
constexpr int32_t kTypeNumber = 3;
constexpr int32_t kTypeString = 4;
constexpr int32_t kTypeTable = 5;
constexpr int32_t kTypeUserdata = 7;
constexpr int32_t kRegistryIndex = -10000;
/// The script system's registry ref to the Entity userdata's metatable (`lwz r4,0x230(r30)`,
/// sub_8293CBA0 reads `*(ss + 560)`; -2 until the first push makes it).
constexpr uint32_t kScriptSystemEntityMeta = 560u;

/// The host's composed chunks (Godot/src/Engine/mods/Engine_ModLua.cs), read through the mount.
constexpr const char* kInitKey = "refii/mod_init.lua";
/// Fable 3's helper calls (Godot/src/Titles/Fable3/Fable3CoreLua.cs), run before any mod's chunk.
constexpr const char* kCoreKey = "refii/core_init.lua";
constexpr const char* kCameraKey = "refii/mod_camera.lua";

/// The event kinds this runtime raises (Fable2/src/vfs/include/core/refii_mod_events.h).
constexpr int32_t kEventTick = 0;
constexpr int32_t kEventHero = 1;
constexpr int32_t kEventHeroGone = 2;
constexpr int32_t kEventLua = 3;
constexpr int32_t kEventSaveLoaded = 4;
constexpr int32_t kEventDynamicWeather = 86;
constexpr int32_t kEventInputAction = 34;
constexpr int32_t kEventEntitySpawned = 39;
constexpr int32_t kEventEntityRemoved = 40;
constexpr int32_t kEventModMenuRow = 87;
constexpr int32_t kEventModMessageBox = 88;
constexpr int32_t kEventModNotice = 89;
constexpr int32_t kEventModMenuBox = 90;
constexpr int32_t kEventAttackModeChanged = 105;
constexpr int32_t kEventCameraChanged = 112;
constexpr int32_t kEventCameraCut = 164;
constexpr int32_t kEventCameraBlend = 165;
constexpr int32_t kEventCameraScreenChanged = 166;
constexpr int32_t kEventCameraZoomChanged = 167;
constexpr int32_t kEventCameraTick = 168;
/// The kind the host's query replies arrive under (REFII_MOD_HOST_REPLY_KIND).
constexpr int32_t kHostReplyKind = 1000;

/// Refii._masks: groups of 20 bits (a float32 is exact to 24), kinds 0..199 (ModApi.LuaMaskBits /
/// LuaMaskGroups).
constexpr int32_t kLuaMaskBits = 20;
constexpr int32_t kLuaMaskGroups = 10;
constexpr float kLuaDirtyPollSeconds = 1.0f;

/// One HF step of the loop App_TickInput rides (game_tick_pump.cpp: HF rate 30).
constexpr float kStepSeconds = 1.0f / 30.0f;

/// The game (`dword_83597034`). Its world, as Lua_Global_IsPlayer hands it to IsPlayer
/// (0x8219ABC8): `*(*(*(*(game + 0xC) + 0x1C) + 4) + 4)`. The world's hero holders, keyed by the
/// player's PlayerID (the save loader binds ID 1 to +0x7C, ID 2 to +0x84 at 0x8242b0f8 / 0x8242b10c):
/// smart pointers whose +4 is the block holding the hero (getters sub_821FB318 / sub_823BB4C0).
const refii::guest::Addr kGame{0x83597034u};
constexpr uint32_t kWorldHeroOne = 0x7Cu;
constexpr uint32_t kWorldHeroTwo = 0x84u;

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

uint32_t LoadU32(const uint8_t* base, uint32_t at) {
  uint32_t value;
  std::memcpy(&value, base + at, sizeof(value));
  return __builtin_bswap32(value);
}

void StoreU32(uint8_t* base, uint32_t at, uint32_t value) {
  const uint32_t be = __builtin_bswap32(value);
  std::memcpy(base + at, &be, sizeof(be));
}

uint32_t Word(const uint8_t* base, uint32_t at) { return Plausible(at) ? LoadU32(base, at) : 0u; }

/// A copy of `ctx` with the stack pointer moved below the caller's frame.
PPCContext Frame(const PPCContext& ctx, uint32_t bytes) {
  PPCContext work = ctx;
  work.r1.u32 = (ctx.r1.u32 - bytes) & ~0xFu;
  return work;
}

/// The game's operator new / delete (sub_8221E3B0 / sub_8221E388).
uint32_t GuestNew(const PPCContext& ctx, uint8_t* base, uint32_t size) {
  PPCContext work = Frame(ctx, 0x100u);
  work.r3.u32 = size;
  rex_lhNew(work, base);
  return work.r3.u32;
}

void GuestDelete(const PPCContext& ctx, uint8_t* base, uint32_t block) {
  if (block == 0u) {
    return;
  }
  PPCContext work = Frame(ctx, 0x100u);
  work.r3.u32 = block;
  rex_lhDelete(work, base);
}

// ---------------------------------------------------------------------------------------------
// The game VM this runtime serves.

std::atomic<uint32_t> g_state{0u};
/// The game VM's script system (r3 of the startup scripts): the Entity metatable's ref is its +560.
std::atomic<uint32_t> g_system{0u};
std::atomic<std::thread::id> g_vm_thread{};
/// Bumped for each new VM and each save read into one: the masks are read again.
std::atomic<uint32_t> g_generation{0u};

bool OnVmThread() { return std::this_thread::get_id() == g_vm_thread.load(std::memory_order_relaxed); }

// ---------------------------------------------------------------------------------------------
// The Lua C API over a guest frame.

/// Strings are staged in the frame's scratch, from +0x40 for kScratchBytes; a longer one goes to
/// the guest heap for the call.
constexpr uint32_t kScratchBytes = 0x300u;
constexpr uint32_t kNativeFrame = 0x400u;

struct GuestLua {
  PPCContext& ctx;
  uint8_t* base;
  uint32_t L;
  uint32_t scratch;

  GuestLua(PPCContext& c, uint8_t* b, uint32_t state) : ctx(c), base(b), L(state), scratch(c.r1.u32 + 0x40u) {}

  PPCContext Call() const {
    PPCContext work = ctx;
    work.r3.u32 = L;
    return work;
  }

  int32_t Top() const {
    return static_cast<int32_t>((LoadU32(base, L + kStateTop) - LoadU32(base, L + kStateBase)) >> 3);
  }

  void SetTop(int32_t index) {
    PPCContext w = Call();
    w.r4.s64 = index;
    rex_lua_settop(w, base);
  }

  /// Room for `slots` more values, as lua_setfield makes it.
  void Room(uint32_t slots) {
    if (LoadU32(base, L + kStateTop) + 8u * slots > LoadU32(base, L + kStateStackLast)) {
      PPCContext w = ctx;
      w.r3.u32 = L + 4u;
      w.r4.u32 = L;
      w.r5.u32 = slots;
      rex_luaD_growstack(w, base);
    }
  }

  int32_t Type(int32_t index) {
    PPCContext w = Call();
    w.r4.s64 = index;
    rex_lua_type(w, base);
    return static_cast<int32_t>(w.r3.u32);
  }

  double Number(int32_t index) {
    PPCContext w = Call();
    w.r4.s64 = index;
    rex_lua_tonumber(w, base);
    return w.f1.f64;
  }

  bool Boolean(int32_t index) {
    PPCContext w = Call();
    w.r4.s64 = index;
    rex_lua_toboolean(w, base);
    return w.r3.u32 != 0u;
  }

  /// The string (or number, as Lua converts it) at `index`, up to `limit` bytes; empty otherwise.
  std::string String(int32_t index, size_t limit = 4096u) {
    const int32_t type = Type(index);
    if (type != kTypeString && type != kTypeNumber) {
      return {};
    }
    PPCContext w = Call();
    w.r4.s64 = index;
    w.r5.u32 = 0u;
    rex_lua_tolstring(w, base);
    const uint32_t text = w.r3.u32;
    if (!Plausible(text)) {
      return {};
    }
    size_t n = 0;
    while (n < limit && text + n < 0xC0000000u && base[text + n] != 0) {
      ++n;
    }
    return std::string(reinterpret_cast<const char*>(base + text), n);
  }

  void PushRaw(uint32_t value, uint32_t tag) {
    Room(1u);
    const uint32_t top = LoadU32(base, L + kStateTop);
    StoreU32(base, top, value);
    StoreU32(base, top + 4u, tag);
    StoreU32(base, L + kStateTop, top + 8u);
  }

  void PushNil() { PushRaw(0u, kTagNil); }
  void PushBoolean(bool value) { PushRaw(value ? 1u : 0u, kTagBoolean); }
  void PushNumber(double value) {
    const float f = std::isfinite(value) ? static_cast<float>(value) : 0.0f;
    uint32_t bits;
    std::memcpy(&bits, &f, sizeof(bits));
    PushRaw(bits, kTagNumber);
  }

  void PushString(std::string_view text) {
    Room(1u);
    uint32_t staged = scratch;
    uint32_t heap = 0u;
    if (text.size() >= kScratchBytes) {
      heap = GuestNew(ctx, base, static_cast<uint32_t>(text.size()) + 1u);
      if (!Plausible(heap)) {
        PushNil();
        return;
      }
      staged = heap;
    }
    std::memcpy(base + staged, text.data(), text.size());
    base[staged + text.size()] = 0;
    PPCContext w = Call();
    w.r4.u32 = staged;
    w.r5.u32 = static_cast<uint32_t>(text.size());
    rex_lua_pushlstring(w, base);
    GuestDelete(ctx, base, heap);
  }

  uint32_t Stage(const char* text) {
    size_t n = std::strlen(text);
    if (n >= kScratchBytes) {
      n = kScratchBytes - 1u;
    }
    std::memcpy(base + scratch, text, n);
    base[scratch + n] = 0;
    return scratch;
  }

  void GetField(int32_t index, const char* name) {
    Room(2u);
    PPCContext w = Call();
    w.r4.s64 = index;
    w.r5.u32 = Stage(name);
    rex_lua_getfield(w, base);
  }

  void SetField(int32_t index, const char* name) {
    PPCContext w = Call();
    w.r4.s64 = index;
    w.r5.u32 = Stage(name);
    rex_lua_setfield(w, base);
  }

  void RawGetI(int32_t index, int32_t n) {
    Room(1u);
    PPCContext w = Call();
    w.r4.s64 = index;
    w.r5.s64 = n;
    rex_lua_rawgeti(w, base);
  }

  void PushClosure(uint32_t function) {
    Room(1u);
    PPCContext w = Call();
    w.r4.u32 = function;
    w.r5.u32 = 0u;
    w.r6.u32 = 0u;
    rex_lua_pushcclosure(w, base);
  }

  /// luaL_loadbuffer + lua_pcall of `chunk`; the top restored. False with the message in `error`.
  bool Run(std::string_view chunk, const char* name, std::string* error) {
    const int32_t top = Top();
    const uint32_t size = static_cast<uint32_t>(chunk.size());
    const uint32_t buffer = GuestNew(ctx, base, size + 1u);
    if (!Plausible(buffer)) {
      if (error != nullptr) {
        *error = "no guest memory for the chunk";
      }
      return false;
    }
    std::memcpy(base + buffer, chunk.data(), size);
    base[buffer + size] = 0;

    PPCContext w = Call();
    w.r4.u32 = LoadU32(base, L) + kGlobalLoadSettings;
    w.r5.u32 = buffer;
    w.r6.u32 = size;
    w.r7.u32 = Stage(name);
    rex_luaL_loadbuffer(w, base);
    uint32_t status = w.r3.u32;
    if (status == 0u) {
      w = Call();
      w.r4.u32 = 0u;
      w.r5.u32 = 0u;
      w.r6.u32 = 0u;
      rex_lua_pcall(w, base);
      status = w.r3.u32;
    }
    if (status != 0u && error != nullptr) {
      *error = Top() > top ? String(-1, 1024u) : std::string("(no message)");
      if (error->empty()) {
        *error = "(a non-string error)";
      }
    }
    SetTop(top);
    GuestDelete(ctx, base, buffer);
    return status == 0u;
  }
};

// ---------------------------------------------------------------------------------------------
// The natives.

int32_t g_results = 0;
bool g_lua_cancelled = false;

#define REFII_F3_NATIVE(name)                         \
  void name##Body(GuestLua& lua);                     \
  void name(PPCContext& ctx, uint8_t* base) {         \
    const uint32_t state = ctx.r3.u32;                \
    PPCContext frame = Frame(ctx, kNativeFrame);      \
    GuestLua lua(frame, base, state);                 \
    g_results = 0;                                    \
    name##Body(lua);                                  \
    ctx.r3.u64 = static_cast<uint32_t>(g_results);    \
  }                                                   \
  void name##Body(GuestLua& lua)

uint32_t World(const uint8_t* base) {
  return Word(base, Word(base, Word(base, Word(base, Word(base, kGame) + 0xCu) + 0x1Cu) + 4u) + 4u);
}

/// Player 1's or 2's hero, read as the getters read it (without their release of a dead block). 0
/// when none.
/// The game's own session questions (Network.IsClient / Network.IsInLiveGame, as Refii.coop_mode asks
/// them in Lua), called in place: leaf functions on the network manager `*(0x835979F8) + 0x10`, which
/// they read without a test (`lwz r10,0x10(r11)`), so they are called only once it is there.
///   IsClient     TU3 0x82209428 / disc 0x82209E50: net+0x1C (in a session) and net+0x1E (client).
///   IsInLiveGame TU3 0x82209460 / disc 0x82209E88: the client test, else the live word (TU3 +0x5C,
///     disc +0x58: the reason it is called and not read).
const refii::guest::Addr kNetworkManager{0x835979F8u};
const refii::guest::Addr kNetworkIsClient{0x82209428u};
const refii::guest::Addr kNetworkIsInLiveGame{0x82209460u};

/// This machine joined another's game online, as of the last tick (StateCoop).
std::atomic<bool> g_online_client{false};

bool GuestYes(const PPCContext& ctx, uint8_t* base, uint32_t address) {
  PPCContext work = Frame(ctx, 0x100u);
  return refii::fable3::seam::CallAt(work, base, address) && (work.r3.u32 & 0xFFu) != 0u;
}

/// Who plays which player here, stated each tick from the game's own answers: 3 (both) alone or in
/// couch co-op, 1 on an online host, 2 on an online client — Fable 2's DrivePlayerState rule, so the
/// player store, the players' events and RefiiHost.player_here mean the same on both titles.
void StateCoop(const PPCContext& ctx, uint8_t* base) {
  const uint32_t manager = Word(base, kNetworkManager);
  const bool ready = Plausible(manager) && Plausible(Word(base, manager + 0x10u));
  const bool client = ready && GuestYes(ctx, base, kNetworkIsClient);
  const bool live = ready && GuestYes(ctx, base, kNetworkIsInLiveGame);
  g_online_client.store(client, std::memory_order_relaxed);
  refii_bridge_player_set_owned(!live ? 3u : (client ? 2u : 1u));
}

/// Player 1's or 2's hero, the SESSION's players as Fable 2 numbers them: 1 the host's hero, 2 the
/// joiner's (or couch player two). The world keeps this machine's hero at +0x7C and the other's at +0x84
/// (GetLocalHero TU3 0x821FB370 / disc 0x8236D410 `addi r3,r11,0x7C`; GetRemoteHero 0x821DFE98 /
/// 0x821E02D0 `addi r3,r11,0x84`), so on an online client player 1 is +0x84. 0 when none.
uint32_t PlayerHero(const uint8_t* base, int32_t player) {
  if (player != 1 && player != 2) {
    return 0u;
  }
  const uint32_t world = World(base);
  if (!Plausible(world)) {
    return 0u;
  }
  const bool local_slot = (player == 1) != g_online_client.load(std::memory_order_relaxed);
  const uint32_t block = Word(base, world + (local_slot ? kWorldHeroOne : kWorldHeroTwo) + 4u);
  return Plausible(block) ? Word(base, block) : 0u;
}

uint32_t Fnv1(std::string_view text) {
  uint32_t hash = 0x811C9DC5u;
  for (char c : text) {
    hash = (hash * 0x01000193u) ^ static_cast<uint8_t>(c);
  }
  return hash;
}

void Reply(const std::string& id, bool ok, const std::string& fields);

/// _query_reply(id, ok, fields): a Godot query's answer from Refii._query (the Lua dispatcher), as Fable 2's
/// query replies are: `id=N,ok=B,` then the fields text the dispatcher formatted.
REFII_F3_NATIVE(NativeHostReply) {
  const std::string id = lua.String(1, 32u);
  Reply(id.empty() ? std::string("0") : id, lua.Boolean(2), lua.String(3, 4096u));
}

/// _player_load_saved(p, package): player p's saved mod values read from the package the guest names
/// (GetCurrentMainSaveGameName / GetCurrentHenchmanSaveGameName), as Fable 2's load hooks read them.
REFII_F3_NATIVE(NativePlayerLoadSaved) {
  const std::string package = lua.String(2, 256u);
  const int32_t player = static_cast<int32_t>(lua.Number(1));
  if (!package.empty() && (player == 1 || player == 2)) {
    refii_bridge_player_load_saved(player, package.c_str());
  }
}

int32_t IntArgument(GuestLua& lua, int32_t index);

/// Refii.signal(name, payload): "<mod>.<name>" and any text to Godot on this machine (RefiiHost's
/// mod_signal). The preamble's lane runtime rides it: refii.hello, ask_host ('refii.ask'),
/// call_lua's answers ('refii.answer') and value_changed's watch — none of which reach Godot on
/// Fable 3 without it (Engine_ModLua.LaneRuntime guards every one on `Refii.signal ~= nil`).
REFII_F3_NATIVE(NativeSignal) {
  const std::string name = lua.String(1, REFII_MOD_SIGNAL_NAME_MAX + 1u);
  const std::string payload = lua.String(2, REFII_MOD_SIGNAL_PAYLOAD_MAX + 1u);
  lua.PushBoolean(refii_mod_signal(name.c_str(), payload.c_str()) != 0);
  g_results = 1;
}

/// Refii.publish_text(key, text) (the preamble wraps it as Refii._publish_text).
REFII_F3_NATIVE(NativePublishText) {
  const std::string key = lua.String(1, REFII_MOD_SIGNAL_NAME_MAX + 1u);
  const std::string text = lua.String(2, REFII_MOD_SIGNAL_PAYLOAD_MAX + 1u);
  lua.PushBoolean(refii_mod_publish_text(key.c_str(), text.c_str()) != 0);
  g_results = 1;
}

/// Refii.send_to_partner(mod, name, payload) -> true when posted (an online session only).
REFII_F3_NATIVE(NativeSendToPartner) {
  const std::string mod = lua.String(1, 63u);
  const std::string name = lua.String(2, 63u);
  const std::string payload = lua.Type(3) == kTypeString ? lua.String(3, REFII_MOD_PARTNER_PAYLOAD_MAX + 1u) : std::string();
  lua.PushBoolean(refii_mod_send_to_partner(mod.c_str(), name.c_str(), payload.c_str()) != 0);
  g_results = 1;
}

/// Refii._decide(kind, fields) -> true unless refused: the event asked NOW, through the same Decide a
/// seam uses (Lua, then a Godot verdict, then the host's event), for a runtime that lives in Lua (the
/// mod abilities, calls.lua). `fields` is Lua table text. Writes are not returned (none of its users
/// has writable fields). NOTE: no Fable 3 native runs Decide today; Decide nests a lua_pcall on the
/// same state from inside a native call, which is legal Lua and keeps its own frame (Frame below
/// kNativeFrame, scratch above r1), but has not run yet.
REFII_F3_NATIVE(NativeDecide) {
  const int32_t kind = IntArgument(lua, 1);
  const std::string fields = lua.String(2, 1024u);
  const refii::fable3::modlua::Verdict verdict = refii::fable3::modlua::Decide(lua.ctx, lua.base, kind, fields);
  lua.PushBoolean(!verdict.cancelled);
  g_results = 1;
}

// Fable 2 puts a consent question to the partner in Godot (GameUiCoopConsent: a host box in their
// half, or its own cq:/ca: lines on the co-op channel). Fable 3 has no host boxes (the game's own box
// GUI presents a mod's message box, Fable3Boxes), so the question is put the way a mod would put it:
//   - couch co-op: refii_mod_message_box for the partner (player 3 - asker), its answer taken at
//     box_answer (B5) before it is raised as mod_message_box;
//   - online: the route's mod lane under the reserved mod "refii" (ModLuaLane.Scope, which no mod
//     may use for a signal either): `refii ask <b64 "ticket\nasker\ntext">` to the peer, whose box
//     answers `refii answer <b64 "ticket\n0|1">`; both are taken in B3 and never raised;
//   - alone: yes at once ("nobody to ask: no partner objects", GameUiCoopConsent).
// Unanswered in 30 s is a no, as Fable 2's ConsentTimeoutSeconds. The ticket and the answer are the
// runtime's (core/coop_consent.cpp), so Refii.partner_answer reads them on either title.

constexpr int32_t kEventPartnerConsent = 67;
constexpr int32_t kEventPartnerMessage = 93;
constexpr float kConsentSeconds = 30.0f;

struct ConsentAsk {
  uint32_t ticket = 0u;     // ours (this machine's runtime), or 0 for a peer's question we show
  uint32_t peer_ticket = 0u;  // the peer's ticket, for a peer's question
  uint32_t box = 0u;        // the message box shown here, 0 when the question went to the peer
  float age = 0.0f;
};
std::vector<ConsentAsk> g_consent;

void GiveConsent(uint32_t ticket, bool yes) {
  refii_bridge_consent_give(ticket, yes ? 1 : 0);
  for (size_t i = 0; i < g_consent.size(); ++i) {
    if (g_consent[i].ticket == ticket) {
      g_consent.erase(g_consent.begin() + static_cast<std::ptrdiff_t>(i));
      return;
    }
  }
}

void SendConsentLine(const char* name, const std::string& text) {
  // The route takes the payload base64 itself (refii_mod_send_to_partner); "refii" is the reserved mod.
  refii_mod_send_to_partner("refii", name, text.c_str());
}

/// A new question from this machine's script: PartnerConsent may answer it for the partner (Fable 2's
/// refii_mod_raise_partner_consent: answer -1 asks, < 0.5 no, else yes); otherwise it is put.
void ConsentAsked(GuestLua& lua, uint32_t ticket, int32_t asker, const std::string& question) {
  const refii::fable3::modlua::Verdict verdict = refii::fable3::modlua::Decide(
      lua.ctx, lua.base, kEventPartnerConsent,
      refii::fable3::seam::Fields("ticket=%u,kind=0,asker=%d,detail=0,answer=-1", ticket, asker));
  double answer = -1.0;
  if (verdict.Read("answer", answer) && answer >= 0.0) {
    refii_bridge_consent_give(ticket, answer >= 0.5 ? 1 : 0);
    return;
  }
  const uint32_t owned = refii_bridge_player_owned();
  const int32_t partner = asker == 1 ? 2 : 1;
  ConsentAsk ask;
  ask.ticket = ticket;
  if (owned == 3u) {
    if (PlayerHero(lua.base, partner) == 0u) {
      refii_bridge_consent_give(ticket, 1);  // alone: nobody to ask
      return;
    }
    ask.box = refii_mod_message_box(partner, "Your partner asks", question.c_str(), "Yes", "No");
    if (ask.box == 0u) {
      refii_bridge_consent_give(ticket, 0);
      return;
    }
  } else {
    SendConsentLine("ask", std::to_string(ticket) + "\n" + std::to_string(asker) + "\n" + question);
  }
  g_consent.push_back(ask);
}

/// Refii.ask_partner(asker, question) -> ticket, nil for a bad asker or an empty question.
REFII_F3_NATIVE(NativeAskPartner) {
  const int32_t asker = IntArgument(lua, 1);
  const std::string question = lua.String(2, 239u);
  if (question.empty() || (asker != 1 && asker != 2)) {
    lua.PushNil();
  } else {
    const uint32_t ticket = refii_bridge_consent_ask_text(asker, question.c_str());
    ConsentAsked(lua, ticket, asker, question);
    lua.PushNumber(static_cast<double>(ticket));
  }
  g_results = 1;
}

/// Refii.partner_answer(ticket) -> nil while pending, then true or false.
REFII_F3_NATIVE(NativePartnerAnswer) {
  const int32_t answer = refii_bridge_consent_answer(static_cast<uint32_t>(IntArgument(lua, 1)));
  if (answer < 0) {
    lua.PushNil();
  } else {
    lua.PushBoolean(answer == 1);
  }
  g_results = 1;
}

/// box_answer for a consent box: true when `box` was one (taken, never raised as mod_message_box).
bool TakeConsentBox(uint32_t box, int32_t button) {
  for (size_t i = 0; i < g_consent.size(); ++i) {
    ConsentAsk& ask = g_consent[i];
    if (ask.box == 0u || ask.box != box) {
      continue;
    }
    const bool yes = button == 0;
    if (ask.peer_ticket != 0u) {
      SendConsentLine("answer", std::to_string(ask.peer_ticket) + "\n" + (yes ? "1" : "0"));
      g_consent.erase(g_consent.begin() + static_cast<std::ptrdiff_t>(i));
    } else {
      GiveConsent(ask.ticket, yes);
    }
    return true;
  }
  return false;
}

/// The 30 s lapse, once per tick.
void AgeConsent() {
  for (size_t i = g_consent.size(); i-- > 0;) {
    ConsentAsk& ask = g_consent[i];
    ask.age += kStepSeconds;
    if (ask.age < kConsentSeconds) {
      continue;
    }
    if (ask.peer_ticket == 0u) {
      refii_bridge_consent_give(ask.ticket, 0);
    }
    g_consent.erase(g_consent.begin() + static_cast<std::ptrdiff_t>(i));
  }
}

REFII_F3_NATIVE(NativeLog) {
  REXLOG_INFO("[mod lua] {}", lua.String(1, 1024u));
}

REFII_F3_NATIVE(NativeError) {
  const std::string text = lua.String(1, 1024u);
  if (!text.empty()) {
    refii_bridge_mod_error_post(text.data(), text.size());
    REXLOG_WARN("[mod lua] {}", text);
  }
}

REFII_F3_NATIVE(NativePublish) {
  const std::string key = lua.String(1, 128u);
  if (!key.empty()) {
    refii::mods::Publish(key, static_cast<float>(lua.Number(2)));
  }
}

REFII_F3_NATIVE(NativeValue) {
  const std::string key = lua.String(1, 128u);
  float value = 0.0f;
  if (!key.empty() && refii::mods::Published(key, value)) {
    lua.PushNumber(value);
  } else {
    lua.PushNil();
  }
  g_results = 1;
}

REFII_F3_NATIVE(NativeHash) {
  char text[16];
  std::snprintf(text, sizeof(text), "0x%08x", Fnv1(lua.String(1, 256u)));
  lua.PushString(text);
  g_results = 1;
}

REFII_F3_NATIVE(NativePlayerHero) {
  const double player = lua.Type(1) == kTypeNumber ? lua.Number(1) : 1.0;
  const uint32_t hero = PlayerHero(lua.base, std::isfinite(player) ? static_cast<int32_t>(player) : 1);
  if (hero != 0u) {
    lua.PushNumber(refii_bridge_mod_handle(hero));
  } else {
    lua.PushNil();
  }
  g_results = 1;
}

REFII_F3_NATIVE(NativeCancelled) {
  (void)lua;
  g_lua_cancelled = true;
}

/// The event a seam is deciding now (refii::fable3::modlua::Decide), -1 outside one: Refii._write
/// lands in its writes.
int32_t g_deciding_kind = -1;
refii::fable3::modlua::Verdict* g_deciding = nullptr;

/// Refii._write(kind, field, value): a handler's change to a writable number of the event being
/// decided, as the preamble's _fire states it after the handlers ran. False outside that moment.
REFII_F3_NATIVE(NativeWrite) {
  const bool now = g_deciding != nullptr && lua.Type(1) == kTypeNumber &&
                   static_cast<int32_t>(lua.Number(1)) == g_deciding_kind && lua.Type(3) == kTypeNumber;
  if (now) {
    g_deciding->Write(lua.String(2, 64u), lua.Number(3));
  }
  lua.PushBoolean(now);
  g_results = 1;
}

/// The entity a script value names: a handle number, or a game Entity userdata — checked to carry
/// the Entity metatable (the game's glue dereferences any userdata unchecked) and resolved through
/// its script ref. 0 for anything else or a dead entity.
uint32_t EntityArgument(GuestLua& lua, int32_t index) {
  const int32_t type = lua.Type(index);
  if (type == kTypeNumber) {
    const double handle = lua.Number(index);
    return handle >= 1.0 && handle < 4294967296.0 ? refii_bridge_mod_handle_address(static_cast<uint32_t>(handle)) : 0u;
  }
  if (type != kTypeUserdata) {
    return 0u;
  }
  const uint32_t system = g_system.load(std::memory_order_relaxed);
  const uint32_t meta_ref = Plausible(system) ? LoadU32(lua.base, system + kScriptSystemEntityMeta) : 0xFFFFFFFEu;
  if (static_cast<int32_t>(meta_ref) < 0) {
    return 0u;
  }
  const int32_t top = lua.Top();
  lua.Room(2u);
  PPCContext w = lua.Call();
  w.r4.s64 = index;
  rex_lua_getmetatable(w, lua.base);
  bool entity_meta = false;
  if (w.r3.u32 != 0u) {
    lua.RawGetI(kRegistryIndex, static_cast<int32_t>(meta_ref));
    const uint32_t at = LoadU32(lua.base, lua.L + kStateTop);
    entity_meta = LoadU32(lua.base, at - 16u) == LoadU32(lua.base, at - 8u) &&
                  LoadU32(lua.base, at - 12u) == LoadU32(lua.base, at - 4u);
  }
  lua.SetTop(top);
  if (!entity_meta) {
    return 0u;
  }
  w = lua.Call();
  w.r4.s64 = index;
  rex_lua_touserdata(w, lua.base);
  const uint32_t block = w.r3.u32;
  const uint32_t ref = Plausible(block) ? LoadU32(lua.base, block) : 0u;
  if (!Plausible(ref)) {
    return 0u;
  }
  w = lua.ctx;
  w.r3.u32 = ref;
  rex_lhScriptEntityRef_Resolve(w, lua.base);
  return Plausible(w.r3.u32) ? w.r3.u32 : 0u;
}

/// Refii.entity(h): the game's own Entity userdata for a handle (or an Entity passed through), what
/// the shipped scripts and the game's natives take; nil for a dead or unknown one.
REFII_F3_NATIVE(NativeEntity) {
  const uint32_t entity = EntityArgument(lua, 1);
  if (entity == 0u) {
    lua.PushNil();
  } else {
    lua.Room(2u);
    PPCContext w = lua.Call();
    w.r4.u32 = entity;
    rex_lhLua_PushEntity(w, lua.base);
  }
  g_results = 1;
}

/// Refii.handle(e): the handle for a game Entity (or a handle passed through); nil for anything else.
REFII_F3_NATIVE(NativeHandle) {
  const uint32_t entity = EntityArgument(lua, 1);
  if (entity == 0u) {
    lua.PushNil();
  } else {
    lua.PushNumber(refii_bridge_mod_handle(entity));
  }
  g_results = 1;
}

/// Refii.alive(e): whether a handle or Entity still names a live entity.
REFII_F3_NATIVE(NativeAlive) {
  lua.PushBoolean(EntityArgument(lua, 1) != 0u);
  g_results = 1;
}

// ---------------------------------------------------------------------------------------------
// A mod's requests for Godot (core/refii_mod_requests.h, shared with Fable 2): the preamble's
// play_fx, light, message_box, menu_box, mark, world_meter... unpack their options into these, in
// Fable 2's argument order, so the one preamble serves both titles.

uint32_t HandleArgument(GuestLua& lua, int32_t index) {
  const double value = lua.Number(index);
  return value >= 1.0 && value < 4294967296.0 ? static_cast<uint32_t>(value) : 0u;
}

int32_t IntArgument(GuestLua& lua, int32_t index) {
  const double value = lua.Number(index);
  return value != value ? 0 : value >= 2147483647.0 ? 2147483647 : value <= -2147483648.0 ? (-2147483647 - 1)
                                                                                         : static_cast<int32_t>(value);
}

float FloatArgument(GuestLua& lua, int32_t index) { return static_cast<float>(lua.Number(index)); }

void PushId(GuestLua& lua, uint32_t id) {
  if (id != 0u) {
    lua.PushNumber(id);
  } else {
    lua.PushNil();
  }
  g_results = 1;
}

REFII_F3_NATIVE(NativePlayFx) {
  const std::string name = lua.String(1, 127u);
  const float xyz[3] = {FloatArgument(lua, 2), FloatArgument(lua, 3), FloatArgument(lua, 4)};
  const uint32_t handle = HandleArgument(lua, 5);
  refii_mod_fx_options options = refii_mod_fx_default_options();
  options.duration = FloatArgument(lua, 6);
  options.scale = FloatArgument(lua, 7);
  options.tint[0] = FloatArgument(lua, 8);
  options.tint[1] = FloatArgument(lua, 9);
  options.tint[2] = FloatArgument(lua, 10);
  options.intensity = FloatArgument(lua, 11);
  options.loop = IntArgument(lua, 12) != 0 ? 1 : 0;
  options.offset[0] = FloatArgument(lua, 13);
  options.offset[1] = FloatArgument(lua, 14);
  options.offset[2] = FloatArgument(lua, 15);
  const std::string at = lua.String(16, 95u);
  options.at = at.empty() ? nullptr : at.c_str();
  PushId(lua, refii_mod_play_fx_handle(name.c_str(), handle != 0u ? nullptr : xyz, handle, &options));
}

REFII_F3_NATIVE(NativeStopFx) { refii_mod_stop_fx(HandleArgument(lua, 1)); }

REFII_F3_NATIVE(NativeLight) {
  const uint32_t handle = HandleArgument(lua, 1);
  const float xyz[3] = {FloatArgument(lua, 2), FloatArgument(lua, 3), FloatArgument(lua, 4)};
  const float rgb[3] = {FloatArgument(lua, 5), FloatArgument(lua, 6), FloatArgument(lua, 7)};
  refii_mod_light_options options{};
  options.offset[0] = FloatArgument(lua, 10);
  options.offset[1] = FloatArgument(lua, 11);
  options.offset[2] = FloatArgument(lua, 12);
  const std::string at = lua.String(13, 95u);
  options.at = at.empty() ? nullptr : at.c_str();
  options.flicker = FloatArgument(lua, 14);
  options.direction[0] = FloatArgument(lua, 15);
  options.direction[1] = FloatArgument(lua, 16);
  options.direction[2] = FloatArgument(lua, 17);
  options.cone = FloatArgument(lua, 18);
  options.attenuation = FloatArgument(lua, 19);
  PushId(lua, refii_mod_light_handle(handle, xyz, rgb, FloatArgument(lua, 8), FloatArgument(lua, 9), &options));
}

REFII_F3_NATIVE(NativeLightRemove) { refii_mod_light_remove(HandleArgument(lua, 1)); }

REFII_F3_NATIVE(NativeOpenMenu) {
  lua.PushBoolean(refii_mod_open_menu(IntArgument(lua, 2), lua.String(1, 95u).c_str()) != 0);
  g_results = 1;
}

REFII_F3_NATIVE(NativeMessageBox) {
  const std::string title = lua.String(1, 127u);
  const std::string text = lua.String(2, 479u);
  const std::string accept = lua.String(3, 63u);
  const std::string cancel = lua.String(4, 63u);
  PushId(lua, refii_mod_message_box(IntArgument(lua, 6), title.c_str(), text.c_str(), accept.c_str(),
                                    IntArgument(lua, 5) != 0 ? cancel.c_str() : nullptr));
}

REFII_F3_NATIVE(NativeNotice) {
  const std::string title = lua.String(1, 127u);
  const std::string text = lua.String(2, 479u);
  PushId(lua, refii_mod_notice(IntArgument(lua, 4), title.c_str(), text.c_str(), FloatArgument(lua, 3)));
}

REFII_F3_NATIVE(NativeMenuBox) {
  constexpr int32_t kMax = 12;
  const std::string title = lua.String(1, 127u);
  const std::string text = lua.String(2, 479u);
  const int32_t count = std::max<int32_t>(0, std::min<int32_t>(kMax, IntArgument(lua, 5)));
  std::string options[kMax];
  const char* list[kMax];
  for (int32_t i = 0; i < count; ++i) {
    options[i] = lua.String(6 + i, 95u);
    list[i] = options[i].c_str();
  }
  PushId(lua, refii_mod_menu_box(IntArgument(lua, 3), title.c_str(), text.c_str(), list, count,
                                 IntArgument(lua, 4) != 0 ? 1 : 0));
}

REFII_F3_NATIVE(NativeText) {
  const std::string text = lua.String(1, 479u);
  char out[1024];
  if (!text.empty() && refii_mod_text(text.c_str(), out, sizeof(out)) >= 0) {
    lua.PushString(out);
  } else {
    lua.PushNil();
  }
  g_results = 1;
}

REFII_F3_NATIVE(NativeMark) {
  const uint32_t handle = HandleArgument(lua, 1);
  const float xyz[3] = {FloatArgument(lua, 2), FloatArgument(lua, 3), FloatArgument(lua, 4)};
  const std::string icon = lua.String(5, 159u);
  const std::string text = lua.String(6, 159u);
  const uint32_t entity = handle != 0u ? refii_bridge_mod_handle_address(handle) : 0u;
  PushId(lua, refii_mod_mark(IntArgument(lua, 7), entity, handle != 0u ? nullptr : xyz, icon.c_str(), text.c_str()));
}

REFII_F3_NATIVE(NativeUnmark) { refii_mod_unmark(HandleArgument(lua, 1)); }

REFII_F3_NATIVE(NativeWorldMeter) {
  const uint32_t handle = HandleArgument(lua, 1);
  const float xyz[3] = {FloatArgument(lua, 2), FloatArgument(lua, 3), FloatArgument(lua, 4)};
  const std::string style = lua.String(7, 31u);
  const uint32_t entity = handle != 0u ? refii_bridge_mod_handle_address(handle) : 0u;
  PushId(lua, refii_mod_world_meter(IntArgument(lua, 8), entity, handle != 0u ? nullptr : xyz, FloatArgument(lua, 5),
                                    FloatArgument(lua, 6), style.c_str()));
}

REFII_F3_NATIVE(NativeMarkMeter) {
  lua.PushBoolean(refii_mod_mark_meter(HandleArgument(lua, 1), FloatArgument(lua, 2), FloatArgument(lua, 3),
                                       lua.String(4, 31u).c_str()) != 0);
  g_results = 1;
}

REFII_F3_NATIVE(NativeFade) {
  const std::string dir = lua.String(2, 31u);
  const bool to_black = dir == "to_black" || dir == "out" || dir == "black";
  lua.PushBoolean(refii_mod_fade(IntArgument(lua, 1), to_black ? 1 : 0, FloatArgument(lua, 3)) != 0);
  g_results = 1;
}

REFII_F3_NATIVE(NativeMeter) {
  lua.PushBoolean(refii_mod_meter(IntArgument(lua, 5), lua.String(1, 95u).c_str(), lua.String(2, 127u).c_str(),
                                  FloatArgument(lua, 3), FloatArgument(lua, 4), lua.String(6, 31u).c_str()) != 0);
  g_results = 1;
}

REFII_F3_NATIVE(NativeMeterRemove) {
  lua.PushBoolean(refii_mod_meter_remove(lua.String(1, 95u).c_str()) != 0);
  g_results = 1;
}

REFII_F3_NATIVE(NativeInputClaim) {
  lua.PushBoolean(refii_mod_input_claim(IntArgument(lua, 1), lua.String(2, 95u).c_str(), lua.Boolean(3) ? 1 : 0) != 0);
  g_results = 1;
}

REFII_F3_NATIVE(NativeHudIcon) {
  lua.PushBoolean(refii_mod_hud_icon(IntArgument(lua, 1), lua.String(2, 95u).c_str(), lua.String(3, 159u).c_str()) != 0);
  g_results = 1;
}

REFII_F3_NATIVE(NativeHudSlide) {
  lua.PushBoolean(refii_mod_hud_slide(IntArgument(lua, 1), lua.String(2, 95u).c_str(), IntArgument(lua, 3)) != 0);
  g_results = 1;
}

REFII_F3_NATIVE(NativeHudHold) {
  lua.PushBoolean(refii_mod_hud_hold(IntArgument(lua, 1), lua.String(2, 95u).c_str(), lua.Boolean(3) ? 1 : 0) != 0);
  g_results = 1;
}

REFII_F3_NATIVE(NativePress) {
  const int32_t held = lua.Type(3) == kTypeNumber ? IntArgument(lua, 3) : (lua.Boolean(3) ? 1 : 0);
  lua.PushBoolean(refii_mod_press(IntArgument(lua, 1), lua.String(2, 31u).c_str(), held) != 0);
  g_results = 1;
}

REFII_F3_NATIVE(NativeTap) {
  const int32_t ticks = lua.Type(3) == kTypeNumber ? IntArgument(lua, 3) : 1;
  lua.PushBoolean(refii_mod_tap(IntArgument(lua, 1), lua.String(2, 31u).c_str(), ticks) != 0);
  g_results = 1;
}

REFII_F3_NATIVE(NativeMenuRows) {
  const std::string rows = lua.String(2, REFII_MOD_MENU_ROWS_MAX + 1u);
  lua.PushBoolean(refii_mod_set_menu_rows(IntArgument(lua, 3), lua.String(1, 95u).c_str(), rows.c_str()) != 0);
  g_results = 1;
}

REFII_F3_NATIVE(NativeOpenScreen) {
  const double id = lua.Number(1);
  lua.PushBoolean(id >= 1.0 && id <= 4294967295.0 &&
                  refii_mod_open_screen(IntArgument(lua, 2), static_cast<uint32_t>(id)) != 0);
  g_results = 1;
}

struct Native {
  const char* name;
  void (*function)(PPCContext&, uint8_t*);
  uint32_t thunk;
};

// ---------------------------------------------------------------------------------------------
// Combat and control (mod_combat_hooks.cpp): the action a creature plays, its clock and its interrupt,
// the target lock, the cutscene statements, the creature's tier.

/// An action handle (what current_action answers) as its address, 0 for none.
uint32_t ActionArgument(GuestLua& lua, int32_t index) {
  if (lua.Type(index) != kTypeNumber) {
    return 0u;
  }
  const double handle = lua.Number(index);
  return handle >= 1.0 && handle < 4294967296.0 ? refii_bridge_mod_handle_address(static_cast<uint32_t>(handle)) : 0u;
}

/// A player argument: 1 or 2, `fallback` when absent.
int32_t PlayerArgument(GuestLua& lua, int32_t index, int32_t fallback) {
  if (lua.Type(index) != kTypeNumber) {
    return fallback;
  }
  const double player = lua.Number(index);
  return player == 1.0 || player == 2.0 ? static_cast<int32_t>(player) : 0;
}

/// Refii.current_action(e) -> action: the creature's live action (a handle), nil for none.
REFII_F3_NATIVE(NativeCurrentAction) {
  const uint32_t action = refii::fable3::modlua::CurrentAction(lua.base, EntityArgument(lua, 1));
  if (action != 0u) {
    lua.PushNumber(refii_bridge_mod_handle(action));
  } else {
    lua.PushNil();
  }
  g_results = 1;
}

/// Refii.action_elapsed(a) -> seconds on the action's own clock, nil for none.
REFII_F3_NATIVE(NativeActionElapsed) {
  float seconds = 0.0f;
  if (refii::fable3::modlua::ActionElapsed(lua.ctx, lua.base, ActionArgument(lua, 1), seconds)) {
    lua.PushNumber(seconds);
  } else {
    lua.PushNil();
  }
  g_results = 1;
}

/// Refii.action_interruptible(a): the next action may interrupt it now.
REFII_F3_NATIVE(NativeActionInterruptible) {
  lua.PushBoolean(refii::fable3::modlua::ActionInterruptible(lua.base, ActionArgument(lua, 1)));
  g_results = 1;
}

/// Refii.allow_action_interrupt(a): the ALLOW_ACTION_INTERRUPT grant now.
REFII_F3_NATIVE(NativeAllowActionInterrupt) {
  lua.PushBoolean(refii::fable3::modlua::AllowActionInterrupt(lua.base, ActionArgument(lua, 1)));
  g_results = 1;
}

/// Refii.allow_action_interrupt_at(a, seconds): the grant once a's clock reaches seconds.
REFII_F3_NATIVE(NativeAllowActionInterruptAt) {
  const double seconds = lua.Type(2) == kTypeNumber ? lua.Number(2) : 0.0;
  lua.PushBoolean(refii::fable3::modlua::AllowActionInterruptAt(lua.ctx, lua.base, ActionArgument(lua, 1),
                                                                 static_cast<float>(seconds)));
  g_results = 1;
}

/// Refii.action_type(a) -> -1: Fable 3's actions do not state an EScriptableAction yet.
REFII_F3_NATIVE(NativeActionType) {
  (void)ActionArgument(lua, 1);
  lua.PushNumber(-1.0);
  g_results = 1;
}

/// Refii.player_of(e) -> 1 or 2 for a player's hero, else 0.
REFII_F3_NATIVE(NativePlayerOf) {
  lua.PushNumber(refii::fable3::modlua::PlayerOf(lua.base, EntityArgument(lua, 1)));
  g_results = 1;
}

/// Refii.living(e): alive, and above zero health when it has a health.
REFII_F3_NATIVE(NativeLiving) {
  lua.PushBoolean(refii::fable3::modlua::Living(lua.base, EntityArgument(lua, 1)));
  g_results = 1;
}

/// Refii.lock_target(p [, e]): the player's lock, on e or on what the targeting holds.
REFII_F3_NATIVE(NativeLockTarget) {
  const int32_t player = PlayerArgument(lua, 1, 1);
  const bool named = lua.Type(2) > 0;  // none (-1) and nil (0) lock on what the targeting holds
  const uint32_t target = named ? EntityArgument(lua, 2) : 0u;
  lua.PushBoolean(player != 0 && (!named || target != 0u) &&
                  refii::fable3::modlua::LockTarget(lua.ctx, lua.base, player, target));
  g_results = 1;
}

/// Refii.locked_target(p) -> target (a handle), nil while the player holds no lock.
REFII_F3_NATIVE(NativeLockedTarget) {
  const int32_t player = PlayerArgument(lua, 1, 1);
  const uint32_t target = player != 0 ? refii::fable3::modlua::LockedTarget(lua.ctx, lua.base, player) : 0u;
  if (target != 0u) {
    lua.PushNumber(refii_bridge_mod_handle(target));
  } else {
    lua.PushNil();
  }
  g_results = 1;
}

/// Refii.release_lock(p): the force byte released; the game drops its lock mode.
REFII_F3_NATIVE(NativeReleaseLock) {
  const int32_t player = PlayerArgument(lua, 1, 1);
  lua.PushBoolean(player != 0 && refii::fable3::modlua::ReleaseLock(lua.base, player));
  g_results = 1;
}

/// Refii.in_cutscene([p [, kinds]]) -> inside, kinds: the statements holding now (1 full, 2
/// interactive, 4 look-at); inside when any of `kinds` (all three by default) holds.
REFII_F3_NATIVE(NativeInCutscene) {
  const int32_t player = PlayerArgument(lua, 1, 0);
  const int32_t mask = lua.Type(2) == kTypeNumber ? static_cast<int32_t>(lua.Number(2)) : 7;
  const int32_t kinds = refii::fable3::modlua::InCutscene(lua.ctx, lua.base, player);
  lua.PushBoolean((kinds & mask) != 0);
  lua.PushNumber(kinds);
  g_results = 2;
}

/// Refii.creature_tier(e) -> bits: Refii.CREATURE_ELITE, Refii.CREATURE_LIEUTENANT.
REFII_F3_NATIVE(NativeCreatureTier) {
  lua.PushNumber(refii::fable3::modlua::CreatureTier(lua.base, EntityArgument(lua, 1)));
  g_results = 1;
}

/// Refii.event_replica(): false — every Fable 3 event is this machine's own.
REFII_F3_NATIVE(NativeEventReplica) {
  lua.PushBoolean(false);
  g_results = 1;
}

// ---------------------------------------------------------------------------------------------
// The player store (Refii.player_*): the runtime's one store (core/player_state.h, the same Fable 2
// uses), a number or a text per player and key, what the preamble's pools keep and a HUD piece's
// `player:<key>` reads. Session values go with a new hero (PumpHeroes); saved values are kept for
// the run (Fable 3's saves do not carry them yet).

std::string StoreKey(GuestLua& lua) { return lua.String(2, 127u); }

/// Refii.player_here(p): this machine plays that player's hero.
REFII_F3_NATIVE(NativePlayerHere) {
  const int32_t player = PlayerArgument(lua, 1, 1);
  lua.PushBoolean(player != 0 && ((refii_bridge_player_owned() >> (player - 1)) & 1u) != 0u);
  g_results = 1;
}

/// Refii.player_value(p, key [, fallback]) -> the value, the fallback (nil without one) when none.
REFII_F3_NATIVE(NativePlayerValue) {
  const std::string key = StoreKey(lua);
  float value = 0.0f;
  if (!key.empty() && refii_bridge_player_get(PlayerArgument(lua, 1, 0), key.data(), key.size(), &value) != 0) {
    lua.PushNumber(value);
  } else if (lua.Type(3) == kTypeNumber) {
    lua.PushNumber(lua.Number(3));
  } else {
    lua.PushNil();
  }
  g_results = 1;
}

/// player_set / player_add (the 4th argument true: saved) / player_save.
void PlayerWrite(GuestLua& lua, int32_t add, int32_t saved) {
  const std::string key = StoreKey(lua);
  if (!key.empty() && lua.Type(3) == kTypeNumber) {
    refii_bridge_player_write(PlayerArgument(lua, 1, 0), key.data(), key.size(), static_cast<float>(lua.Number(3)), add,
                              saved);
  }
}

REFII_F3_NATIVE(NativePlayerSet) { PlayerWrite(lua, 0, 0); }
REFII_F3_NATIVE(NativePlayerAdd) { PlayerWrite(lua, 1, lua.Boolean(4) ? 1 : 0); }
REFII_F3_NATIVE(NativePlayerSave) { PlayerWrite(lua, 0, 1); }

/// Refii.player_value_text(p, key) -> the text, nil when none.
REFII_F3_NATIVE(NativePlayerValueText) {
  const std::string key = StoreKey(lua);
  char text[257];
  if (!key.empty() &&
      refii_bridge_player_get_text(PlayerArgument(lua, 1, 0), key.data(), key.size(), text, sizeof(text)) >= 0) {
    lua.PushString(text);
  } else {
    lua.PushNil();
  }
  g_results = 1;
}

/// ACHIEVEMENTS PER CHARACTER, as the machine that plays the hero states them (core/achievement_statement.h):
/// the statement in player p's store, the same text Godot's RefiiHost twins read, so both surfaces answer
/// alike — and on both co-op machines, the partner's hero included.
refii::achievements::Statement AchievementStatement(int32_t player) {
  char text[257];
  constexpr size_t kKey = sizeof(refii::achievements::kStatementKey) - 1u;
  if ((player != 1 && player != 2) ||
      refii_bridge_player_get_text(player, refii::achievements::kStatementKey, kKey, text, sizeof(text)) <= 0) {
    return {};
  }
  return refii::achievements::ParseStatement(text);
}

/// Refii.achievement_unlocked(p, id): whether the hero player p plays has unlocked achievement id.
REFII_F3_NATIVE(NativeAchievementUnlocked) {
  const refii::achievements::Statement statement = AchievementStatement(PlayerArgument(lua, 1, 0));
  const double id = lua.Type(2) == kTypeNumber ? lua.Number(2) : 0.0;
  lua.PushBoolean(statement.valid && id >= 1.0 && id <= 255.0 &&
                  refii::achievements::Holds(statement.hero, static_cast<uint32_t>(id)));
  g_results = 1;
}

/// Refii.achievements(p): the ids the hero player p plays has unlocked, ascending, "id,id,…" ("" for none).
REFII_F3_NATIVE(NativeAchievements) {
  const refii::achievements::Statement statement = AchievementStatement(PlayerArgument(lua, 1, 0));
  lua.PushString(statement.valid ? refii::achievements::IdList(statement.hero) : std::string());
  g_results = 1;
}

/// Refii.character_id(p): the character id of the hero player p plays, "" before the game bound one.
REFII_F3_NATIVE(NativeCharacterId) {
  const refii::achievements::Statement statement = AchievementStatement(PlayerArgument(lua, 1, 0));
  lua.PushString(statement.valid ? statement.character : std::string());
  g_results = 1;
}

void PlayerWriteText(GuestLua& lua, int32_t saved) {
  const std::string key = StoreKey(lua);
  const std::string text = lua.String(3, 256u);
  if (!key.empty()) {
    refii_bridge_player_write_text(PlayerArgument(lua, 1, 0), key.data(), key.size(), text.data(), text.size(), saved);
  }
}

REFII_F3_NATIVE(NativePlayerSetText) { PlayerWriteText(lua, 0); }
REFII_F3_NATIVE(NativePlayerSaveText) { PlayerWriteText(lua, 1); }

// ---------------------------------------------------------------------------------------------
// The parity calls no game native answers (the rest are Lua over the game's natives, Fable3CoreLua).

uint32_t HeroComponent(GuestLua& lua, int32_t player, uint32_t id) {
  const uint32_t hero = player != 0 ? PlayerHero(lua.base, player) : 0u;
  return hero != 0u ? refii::fable3::seam::Component(lua.base, hero, id) : 0u;
}

/// Refii._speed_overridden(p): the player component's override byte (Player.SetPlayerMovementSpeedOverridden
/// 0x8242C428 / disc 0x82AD0F00: `*(u8*)(c+29) = on`), which no native reads back.
REFII_F3_NATIVE(NativeSpeedOverridden) {
  const uint32_t player = HeroComponent(lua, PlayerArgument(lua, 1, 1), 37u);
  lua.PushBoolean(player != 0u && lua.base[player + 0x1Du] != 0u);
  g_results = 1;
}

/// Refii.end_sprint(p): the sprint component's own setter with 0 (sub_82A5D7C8, which CECSprint_vf18
/// calls when stamina runs out); false when the hero is not sprinting (+0x24).
REFII_F3_NATIVE(NativeEndSprint) {
  const uint32_t sprint = HeroComponent(lua, PlayerArgument(lua, 1, 1), 169u);
  const bool sprinting = sprint != 0u && lua.base[sprint + 0x24u] != 0u;
  if (sprinting) {
    PPCContext work = Frame(lua.ctx, 0x200u);
    work.r3.u32 = sprint;
    work.r4.u64 = 0u;
    rex_CECSprint_SetSprinting(work, lua.base);
  }
  lua.PushBoolean(sprinting);
  g_results = 1;
}

/// The status kinds' components (Refii.apply_status's 1 electrified, 2 chaos, 3 on fire, 4 in a vortex):
/// CECElectrified 229, CECInChaos 230, CECOnFire 232, CECInVortex 233 (each class's id slot).
uint32_t StatusComponentId(int32_t kind) {
  switch (kind) {
    case 1: return 229u;
    case 2: return 230u;
    case 3: return 232u;
    case 4: return 233u;
    default: return 0u;
  }
}

/// Refii.has_status(e, kind): the entity carries that status's component.
REFII_F3_NATIVE(NativeHasStatus) {
  const uint32_t entity = EntityArgument(lua, 1);
  const uint32_t id = StatusComponentId(IntArgument(lua, 2));
  lua.PushBoolean(entity != 0u && id != 0u && refii::fable3::seam::Component(lua.base, entity, id) != 0u);
  g_results = 1;
}

/// Refii.clear_status(e, kind): the status's component removed (lhEntity_RemoveComponent, entity, id).
REFII_F3_NATIVE(NativeClearStatus) {
  const uint32_t entity = EntityArgument(lua, 1);
  const uint32_t id = StatusComponentId(IntArgument(lua, 2));
  const bool has = entity != 0u && id != 0u && refii::fable3::seam::Component(lua.base, entity, id) != 0u;
  if (has) {
    PPCContext work = Frame(lua.ctx, 0x200u);
    work.r3.u32 = entity;
    work.r4.u32 = id;
    rex_lhEntity_RemoveComponent(work, lua.base);
  }
  lua.PushBoolean(has);
  g_results = 1;
}

/// The keyboard's attack mode Godot last stated per player (attack_mode P MODE; 0 none).
std::array<int32_t, 2> g_attack_mode{0, 0};

/// Refii.attack_mode(p) -> the keyboard's attack button mode for p (1 melee X, 2 ranged Y, 3 magic B), as
/// Godot states it with attack_mode P MODE; 0 for a player no keyboard plays here.
REFII_F3_NATIVE(NativeAttackMode) {
  const int32_t player = PlayerArgument(lua, 1, 1);
  lua.PushNumber(player == 1 || player == 2 ? g_attack_mode[static_cast<size_t>(player - 1)] : 0);
  g_results = 1;
}

/// Refii.apply_status(e, kind, seconds [, level [, source]]) -> bool: a status through the spell's own
/// per-victim installer, without the spell (kind 1 electrified, 2 chaos, 3 on fire; 4, a vortex, is
/// refused), for `seconds`, at spell level 0..4, from `source` (absent or dead: the entity itself), no
/// damage. The installers are the seamed ones, so status_applying is decided (its serial 4294967295)
/// and status_effect raised. True when the status's component is on the entity after the call (the
/// installer may still refuse: the world's spell-count gate, sub_824A92B0).
///
/// THE SOURCE. Every installer takes the cast's 48-byte source (the spell's +0x68: three smart pointers,
/// the caster first, then six words), built from an entity the way the game builds one for a spell it
/// casts without a spell object — Lua_SpellManager_PushCreaturesAwayForLevelUp (TU3 0x825D3418 / disc
/// 0x825A5AD0): `sub_82B34ED8(v14, entity, 0, 0)` then, after use, `sub_825CA268(v14)`. The constructor
/// (rex_SpellSource_Ctor, TU3 0x82B34ED8 / disc 0x82AF6E70: r3 out, r4 r5 r6 the three entities) writes
/// each pointer's vtable and binds it (sub_8221D310), the six words 0; the destructor (rex_SpellSource_Dtor,
/// TU3 0x825CA268 / disc 0x8259CAE8: r3) releases the three. The source sits at work.r1 + 0x200, above
/// every frame the calls below make and clear of the stack words the fire installer reads from its caller.
///
/// ELECTRIFIED (rex_SpellLightning_Electrify, TU3 0x82B44190 / disc 0x82B05E90): r3 the source, r4 the
/// victim, r5 the serial, r6 the level, f1..f5 (`mr r29,r3 / fmr f31,f1 / mr r31,r4 / fmr f30,f2 / mr r28,r5
/// / fmr f29,f3 / mr r27,r6 / fmr f28,f4 / fmr f27,f5`), handed to CECElectrified's setter (sub_82A5F090,
/// disc 0x82A1E868: `stfs f1,0x28(r3)` the seconds left, `stfs f2,0x38(r31)` the total damage, `stfs
/// f3,0x40(r31)` the first on-phase, `stfs f4,0x44(r31)` each on-phase, `stfs f5,0x48(r31)` each off-phase,
/// `stw r5,0x20(r31)` the serial, r6 -> +0x24 the level). Its update (CECElectrified_vf18 0x82A5F2E8) counts
/// +0x28 down and alternates on (+0x44) and off (+0x48) phases; an on-phase as long as the status keeps it
/// on throughout. Damage 0.
///
/// CHAOS (rex_SpellChaos_ApplyToVictim, TU3 0x82B3C340 / disc 0x82AFE370): r3 the source, r4 the victim,
/// r5 the serial, f1 the seconds, r7 install, r8 a received replica (`mr r27,r3 / fmr f30,f1 / mr r28,r4 /
/// mr r24,r5 / mr r23,r7 / mr r26,r8`; r6 is not read). Install 1, replica 0: the game sends it to the
/// session itself. The source's entity is where the chaos comes from.
///
/// ON FIRE (rex_SpellFireball_Ignite, TU3 0x82B39500 / disc 0x82AFB4E0): r3 the victim, r4 the source,
/// r5 the serial, r6 the level, v1 the origin, f1 the strength at distance 0, f2 its falloff per unit, f3 the
/// burn time, f4 its factor (burn = f3 * f4, `fmuls f31,f28,f29`; a factor above 0.1001 draws it ±15%),
/// f5 -> CECOnFire +0x48 (the damage it spreads over the burn), f6 -> +0x4C (the heat it keeps back), and
/// four words from ITS CALLER's frame (`lbz r9,0x120+arg_77(r1)`, `lwz r8,0x120+arg_7C(r1)`, `lbz
/// r10,0x120+arg_87(r1)`, `lbz r11,0x120+arg_8F(r1)`): +0x77 keep the creature's burning behaviour
/// (sub_82A67420's a5: 0 clears +0x62), +0x7C a re-ignite stamp (0: always), +0x87 burn visibly (the ctor's
/// a40 with a creature: +0x66, +0x5C, +0x54), +0x8F suppress the burn FX (+0x69; CECOnFire_vf18 spawns
/// them only while 0). The fireball's own impact writes 1, 0, 1 and the spell's +0x3E (`stb r28 / stw r24 /
/// stb r23 / stb r11`, TU3 0x82b369d4..e8, r24 0 and r23 1). Here: strength 1, no falloff (so the origin
/// is moot: zero), f3 = 10 × seconds with factor 0.1 (exact, no draw), no damage, no heat kept back,
/// and 1, 0, 1, 0.
REFII_F3_NATIVE(NativeApplyStatus) {
  const uint32_t entity = EntityArgument(lua, 1);
  const int32_t kind = IntArgument(lua, 2);
  const double seconds = lua.Number(3);
  const int32_t level = std::clamp(lua.Type(4) == kTypeNumber ? IntArgument(lua, 4) : 0, 0, 4);
  uint32_t source = lua.Type(5) == kTypeNumber || lua.Type(5) == kTypeUserdata ? EntityArgument(lua, 5) : 0u;
  const uint32_t id = StatusComponentId(kind);
  if (entity == 0u || id == 0u || kind == 4 || !(seconds > 0.0) || !std::isfinite(seconds)) {
    lua.PushBoolean(false);
    g_results = 1;
    return;
  }
  if (source == 0u) {
    source = entity;
  }
  PPCContext work = Frame(lua.ctx, 0x400u);
  const uint32_t descriptor = work.r1.u32 + 0x200u;
  PPCContext make = work;
  make.r3.u32 = descriptor;
  make.r4.u32 = source;
  make.r5.u64 = 0u;
  make.r6.u64 = 0u;
  rex_SpellSource_Ctor(make, lua.base);
  switch (kind) {
    case 1:
      work.r3.u32 = descriptor;
      work.r4.u32 = entity;
      work.r5.s64 = -1;
      work.r6.s64 = level;
      work.f1.f64 = seconds;
      work.f2.f64 = 0.0;
      work.f3.f64 = seconds;
      work.f4.f64 = seconds;
      work.f5.f64 = 0.0;
      rex_SpellLightning_Electrify(work, lua.base);
      break;
    case 2:
      work.r3.u32 = descriptor;
      work.r4.u32 = entity;
      work.r5.s64 = -1;
      work.r6.u64 = 0u;
      work.r7.u64 = 1u;
      work.r8.u64 = 0u;
      work.f1.f64 = seconds;
      rex_SpellChaos_ApplyToVictim(work, lua.base);
      break;
    default: {
      lua.base[work.r1.u32 + 0x77u] = 1u;
      StoreU32(lua.base, work.r1.u32 + 0x7Cu, 0u);
      lua.base[work.r1.u32 + 0x87u] = 1u;
      lua.base[work.r1.u32 + 0x8Fu] = 0u;
      work.r3.u32 = entity;
      work.r4.u32 = descriptor;
      work.r5.s64 = -1;
      work.r6.s64 = level;
      work.f1.f64 = 1.0;
      work.f2.f64 = 0.0;
      work.f3.f64 = seconds * 10.0;
      work.f4.f64 = 0.1;
      work.f5.f64 = 0.0;
      work.f6.f64 = 0.0;
      for (int lane = 0; lane < 4; ++lane) {
        work.v1.f32[lane] = 0.0f;
      }
      rex_SpellFireball_Ignite(work, lua.base);
      break;
    }
  }
  PPCContext release = Frame(lua.ctx, 0x400u);
  release.r3.u32 = descriptor;
  rex_SpellSource_Dtor(release, lua.base);
  lua.PushBoolean(refii::fable3::seam::Component(lua.base, entity, id) != 0u);
  g_results = 1;
}

/// A handle or serial as a number, nil for 0.
void PushNonZero(GuestLua& lua, uint32_t handle) {
  if (handle != 0u) {
    lua.PushNumber(handle);
  } else {
    lua.PushNil();
  }
}

/// Refii._spell_parts(serial) -> type, targeted, level, caster, caster_player, spell, vortex, anchor,
/// exploded, has_origin, x, y, z, s1 .. s8 (handles, nil when absent), or nil for a cast the factory never
/// saw. Refii.spell_parts (Fable3CoreLua) builds the table and fills the origin from the caster.
REFII_F3_NATIVE(NativeSpellParts) {
  const double number = lua.Number(1);
  refii::fable3::modlua::CastParts parts;
  if (!(number >= 1.0 && number < 4294967296.0) ||
      !refii::fable3::modlua::SpellParts(lua.base, static_cast<uint32_t>(number), parts)) {
    lua.PushNil();
    g_results = 1;
    return;
  }
  using refii::fable3::seam::Handle;
  using refii::fable3::seam::ObjectHandle;
  lua.PushNumber(parts.type);
  lua.PushBoolean(parts.targeted);
  lua.PushNumber(parts.level);
  PushNonZero(lua, Handle(lua.base, parts.caster));
  lua.PushNumber(refii::fable3::modlua::PlayerOf(lua.base, parts.caster));
  PushNonZero(lua, ObjectHandle(parts.spell));
  PushNonZero(lua, ObjectHandle(parts.vortex));
  PushNonZero(lua, Handle(lua.base, parts.anchor));
  lua.PushBoolean(parts.exploded);
  lua.PushBoolean(parts.has_origin);
  for (const float lane : parts.origin) {
    lua.PushNumber(lane);
  }
  for (int32_t i = 0; i < 8; ++i) {
    PushNonZero(lua, i < parts.sword_count ? Handle(lua.base, parts.swords[i]) : 0u);
  }
  g_results = 21;
}

/// Refii.spell_serial(spell) -> serial: the cast number a spell object's hits carry, nil for a spell
/// handle whose object is gone (or was never the factory's). A spell handle is spell_created's `spell`.
REFII_F3_NATIVE(NativeSpellSerial) {
  const uint32_t serial = refii::fable3::modlua::SpellSerial(lua.base, EntityArgument(lua, 1));
  PushNonZero(lua, serial);
  g_results = 1;
}

/// Refii._vortex_held(serial) -> count, e1 .. (up to 64 handles), or nil when no live vortex carries it.
REFII_F3_NATIVE(NativeVortexHeld) {
  const double number = lua.Number(1);
  uint32_t held[64];
  const int32_t count = number >= 1.0 && number < 4294967296.0
                            ? refii::fable3::modlua::VortexHeld(lua.base, static_cast<uint32_t>(number), held, 64)
                            : -1;
  if (count < 0) {
    lua.PushNil();
    g_results = 1;
    return;
  }
  const int32_t listed = std::min<int32_t>(count, 64);
  lua.PushNumber(listed);
  for (int32_t i = 0; i < listed; ++i) {
    lua.PushNumber(refii_bridge_mod_handle(held[i]));
  }
  g_results = 1 + listed;
}

// ---------------------------------------------------------------------------------------------
// THE LENT CHARGE (Refii.charge_spell). One per player: the spell lent to the hero's own slot table, the
// slots it borrowed, whether the button was let go, and how the charge ended.
//
// The slot table is the spell manager's (component 14 — Lua_SpellManager_PushCreaturesAwayForLevelUp
// looks it up by `cmpwi cr6,r10,0xE` before calling the manager's own sub_82634098): five charge tiers of
// {first, second} bytes from +0x163, 0xFF none. Its writer, SetSpellSlot (TU3 0x82636270 / disc
// 0x8260DC60: r3 manager, r4 tier, r5 first, r6 second, r7 send): `stb r5,0x163(r11)` / `stb r6,0x164(r11)`
// with r11 = manager + 2 * tier, the manager's refresh (sub_82689350), and only when r7 is set
// (`clrlwi r7,r27,24` / `beq`) the packet to the session. The lend writes it through __imp__ (no seam:
// no spell_pick) with r7 0 — the game's "not a choice" write. SpellManager.SetEntitySpellCastTableItem
// is NOT used: its native calls the writer with `li r7,1` (TU3 0x82635c64).
//
// The cast's end is the manager's state (+0x80, its SetState sub_82633010 `stw r4,0x80(r3)` TU3
// 0x82633030 / disc 0x8260a9f4): 6 once the quick cast's one slot went out (`li r4,6` TU3 0x82633220 /
// disc 0x8260abe0) or the full cast's cascade finished, 0 the reset. The slots come back: at once when the
// charge ended without its cast (CAction_End of the charge, not inside a cast's Start); once the state
// reads 6 or 0 after a released charge ended in its cast; a second after a release no charge end
// followed (nothing charged); after 30 s whatever happened; never onto another hero. A real pick made
// meanwhile (the spell_pick seam's write) is what comes back for its tier.

struct ChargeLend {
  bool active = false;
  int32_t type = -1;
  uint32_t hero = 0u;
  uint32_t manager = 0u;
  uint8_t saved[5][2] = {};
  bool released = false;
  bool ended = false;
  bool ended_released = false;
  float since_release = 0.0f;
  float age = 0.0f;
};
std::array<ChargeLend, 2> g_lend{};

constexpr uint32_t kSpellManagerComponent = 14u;
constexpr uint32_t kSlotTable = 0x163u;
constexpr uint32_t kManagerState = 0x80u;
constexpr float kLendIdleSeconds = 1.0f;
constexpr float kLendMaxSeconds = 30.0f;

void WriteLentSlot(const PPCContext& ctx, uint8_t* base, uint32_t manager, int32_t tier, uint8_t first, uint8_t second) {
  PPCContext work = Frame(ctx, 0x200u);
  work.r3.u32 = manager;
  work.r4.s64 = tier;
  work.r5.s64 = static_cast<int8_t>(first);
  work.r6.s64 = static_cast<int8_t>(second);
  work.r7.u64 = 0u;
  __imp__rex_CECSpellManager_SetSpellSlot(work, base);
}

void RestoreLend(const PPCContext& ctx, uint8_t* base, int32_t player) {
  ChargeLend& lend = g_lend[static_cast<size_t>(player - 1)];
  const uint32_t hero = PlayerHero(base, player);
  if (lend.active && hero == lend.hero && refii::fable3::seam::Component(base, hero, kSpellManagerComponent) == lend.manager) {
    for (int32_t tier = 0; tier < 5; ++tier) {
      WriteLentSlot(ctx, base, lend.manager, tier, lend.saved[tier][0], lend.saved[tier][1]);
    }
  }
  lend = ChargeLend{};
}

/// Once per tick (Tick, after PollCombat): the lends whose charge is over come back.
void PumpChargeLend(const PPCContext& ctx, uint8_t* base) {
  for (int32_t player = 1; player <= 2; ++player) {
    ChargeLend& lend = g_lend[static_cast<size_t>(player - 1)];
    if (!lend.active) {
      continue;
    }
    lend.age += kStepSeconds;
    if (lend.released) {
      lend.since_release += kStepSeconds;
    }
    const uint32_t state = Word(base, lend.manager + kManagerState);
    const bool due = lend.age >= kLendMaxSeconds || (lend.ended && !lend.ended_released) ||
                     (lend.ended && lend.ended_released && (state == 6u || state == 0u)) ||
                     (lend.released && !lend.ended && lend.since_release >= kLendIdleSeconds);
    if (due || PlayerHero(base, player) != lend.hero) {
      RestoreLend(ctx, base, player);
    }
  }
}

/// Refii._charge_lend(p, type, tiers, held) -> taken (Refii.charge_spell's): held 1 lends `type` to the
/// first `tiers` charge tiers of player p's hero (the tiers above emptied) and holds the magic button; 0
/// lets the button go. On the machine that plays p only.
REFII_F3_NATIVE(NativeChargeLend) {
  const int32_t player = PlayerArgument(lua, 1, 0);
  const bool held = IntArgument(lua, 4) != 0;
  bool taken = false;
  if ((player == 1 || player == 2) && ((refii_bridge_player_owned() >> (player - 1)) & 1u) != 0u) {
    ChargeLend& lend = g_lend[static_cast<size_t>(player - 1)];
    if (!held) {
      refii_mod_press(player, "B", 0);
      if (lend.active && !lend.released) {
        lend.released = true;
        lend.since_release = 0.0f;
      }
      taken = lend.active;
    } else {
      const int32_t type = IntArgument(lua, 2);
      const int32_t tiers = std::clamp(IntArgument(lua, 3), 0, 5);
      const uint32_t hero = PlayerHero(lua.base, player);
      const uint32_t manager =
          refii::fable3::seam::Live(lua.base, hero) ? refii::fable3::seam::Component(lua.base, hero, kSpellManagerComponent) : 0u;
      if (manager != 0u && type >= 0 && type <= 8 && tiers >= 1) {
        if (lend.active && lend.hero != hero) {
          RestoreLend(lua.ctx, lua.base, player);
        }
        if (!lend.active) {
          lend.active = true;
          lend.hero = hero;
          lend.manager = manager;
          for (uint32_t tier = 0; tier < 5u; ++tier) {
            lend.saved[tier][0] = lua.base[manager + kSlotTable + 2u * tier];
            lend.saved[tier][1] = lua.base[manager + kSlotTable + 2u * tier + 1u];
          }
        }
        lend.type = type;
        lend.released = false;
        lend.ended = false;
        lend.ended_released = false;
        lend.since_release = 0.0f;
        lend.age = 0.0f;
        for (int32_t tier = 0; tier < 5; ++tier) {
          WriteLentSlot(lua.ctx, lua.base, manager, tier, tier < tiers ? static_cast<uint8_t>(type) : 0xFFu, 0xFFu);
        }
        refii_mod_press(player, "B", 1);
        taken = true;
      }
    }
  }
  lua.PushBoolean(taken);
  g_results = 1;
}

/// Refii._by_uid(text): the entity whose UID (Refii.uid's decimal text) this is, as
/// lhScriptEntityRef_Resolve finds it: the world's map at +216 keyed by the 64-bit UID, while the
/// game's +186 holds a world (0x82211c98).
REFII_F3_NATIVE(NativeByUid) {
  const std::string text = lua.String(1, 32u);
  char* end = nullptr;
  const unsigned long long uid = text.empty() ? 0ull : std::strtoull(text.c_str(), &end, 10);
  const uint32_t state = Word(lua.base, Word(lua.base, kGame) + 0xCu);
  const uint32_t world = World(lua.base);
  uint32_t entity = 0u;
  if (uid != 0ull && end != nullptr && *end == 0 && Plausible(state) && lua.base[state + 186u] != 0u && Plausible(world)) {
    PPCContext work = Frame(lua.ctx, 0x200u);
    const uint32_t key = work.r1.u32 + 0x100u;
    StoreU32(lua.base, key, static_cast<uint32_t>(uid >> 32));
    StoreU32(lua.base, key + 4u, static_cast<uint32_t>(uid));
    work.r3.u32 = world;
    work.r4.u32 = key;
    rex_World_EntityByUid(work, lua.base);
    entity = work.r3.u32;
  }
  if (refii::fable3::seam::Live(lua.base, entity)) {
    lua.PushNumber(refii_bridge_mod_handle(entity));
  } else {
    lua.PushNil();
  }
  g_results = 1;
}

/// Refii._use_item(e, item): the entity uses an inventory item the game's Inventory natives handed
/// Lua (lhEntity_UseInventoryItem: entity, the item userdata's block, an empty override name).
REFII_F3_NATIVE(NativeUseItem) {
  const uint32_t entity = EntityArgument(lua, 1);
  uint32_t item = 0u;
  if (entity != 0u && lua.Type(2) == kTypeUserdata) {
    PPCContext w = lua.Call();
    w.r4.s64 = 2;
    rex_lua_touserdata(w, lua.base);
    item = Word(lua.base, w.r3.u32);
  }
  bool used = false;
  if (Plausible(item) && Word(lua.base, item) != 0u) {
    PPCContext work = Frame(lua.ctx, 0x200u);
    const uint32_t empty = work.r1.u32 + 0x100u;
    StoreU32(lua.base, empty, 0u);
    work.r3.u32 = entity;
    work.r4.u32 = item;
    work.r5.u32 = empty;
    rex_lhEntity_UseInventoryItem(work, lua.base);
    used = (work.r3.u32 & 0xFFu) != 0u;
  }
  lua.PushBoolean(used);
  g_results = 1;
}

/// Refii.hide_part(e, part, on [, p]): the host's part hiding on the entity's drawn rig (shared
/// request, Fable 2's).
REFII_F3_NATIVE(NativeHidePart) {
  lua.PushBoolean(refii_mod_hide_part_for(EntityArgument(lua, 1), lua.String(2, 127u).c_str(), lua.Boolean(3) ? 1 : 0,
                                          IntArgument(lua, 4)) != 0);
  g_results = 1;
}

// ---------------------------------------------------------------------------------------------
// The camera: each player's ECameraMode as the core chunk's FrameUpdate wrapper states it.

std::array<int32_t, 3> g_camera_mode{-1, -1, -1};

/// Refii._camera_stated(hero, mode): the core chunk's statement after each CameraManager:FrameUpdate;
/// camera_changed when the player's mode changed.
REFII_F3_NATIVE(NativeCameraStated) {
  const uint32_t hero = EntityArgument(lua, 1);
  const int32_t player = refii::fable3::modlua::PlayerOf(lua.base, hero);
  const int32_t mode = lua.Type(2) == kTypeNumber ? static_cast<int32_t>(lua.Number(2)) : -1;
  if ((player == 1 || player == 2) && g_camera_mode[static_cast<size_t>(player)] != mode) {
    const int32_t previous = g_camera_mode[static_cast<size_t>(player)];
    g_camera_mode[static_cast<size_t>(player)] = mode;
    char fields[96];
    std::snprintf(fields, sizeof(fields), "player=%d,mode=%d,previous=%d,hero=%u", player, mode, previous,
                  refii_bridge_mod_handle(hero));
    refii::fable3::modlua::QueueEvent(kEventCameraChanged, fields);
  }
}

/// Refii._camera_zoom_stated(hero, set, level, combat, string_length, cage_radius, max_cage_radius, height,
/// focal_offset, fov, max_coop_distance): the core chunk's statement after each
/// SACCamera:CalculateActiveZoomLevel; camera_zoom_changed when the set or the combat blend (weight
/// above 0) differs from what was last raised for the player.
std::array<std::string, 3> g_zoom_set{};
std::array<int32_t, 3> g_zoom_combat{-1, -1, -1};

REFII_F3_NATIVE(NativeCameraZoomStated) {
  const int32_t player = refii::fable3::modlua::PlayerOf(lua.base, EntityArgument(lua, 1));
  if (player != 1 && player != 2) {
    return;
  }
  const std::string set = lua.String(2, 63u);
  double values[8];
  for (int32_t i = 0; i < 8; ++i) {
    values[i] = lua.Type(4 + i) == kTypeNumber ? lua.Number(4 + i) : 0.0;
  }
  const int32_t combat = values[0] > 0.0 ? 1 : 0;
  const size_t at = static_cast<size_t>(player);
  if (g_zoom_combat[at] == combat && g_zoom_set[at] == set) {
    return;
  }
  const std::string previous = g_zoom_combat[at] < 0 ? std::string() : g_zoom_set[at];
  g_zoom_set[at] = set;
  g_zoom_combat[at] = combat;
  char numbers[320];
  std::snprintf(numbers, sizeof(numbers),
                ",level=%d,combat=%.9g,string_length=%.9g,cage_radius=%.9g,max_cage_radius=%.9g,height=%.9g,"
                "focal_offset=%.9g,fov=%.9g,max_coop_distance=%.9g",
                lua.Type(3) == kTypeNumber ? static_cast<int32_t>(lua.Number(3)) : 0, values[0], values[1], values[2],
                values[3], values[4], values[5], values[6], values[7]);
  refii::fable3::modlua::QueueEvent(kEventCameraZoomChanged,
                                    "player=" + std::to_string(player) + ",set='" +
                                        refii::fable3::modlua::Quoted(set) + "',previous_set='" +
                                        refii::fable3::modlua::Quoted(previous) + "'" + numbers);
}

/// Refii._camera_read(what, p [, x, y]): the camera's read calls (refii_mod_requests.h), flat for the
/// preamble's Refii.camera_* — 1 pose (12 numbers), 2 stated (11), 3 view (4), 4 screen (mode, split),
/// 5 ray (6); nil where there is none. Fable 2's NativeCameraRead.
REFII_F3_NATIVE(NativeCameraRead) {
  const int32_t what = lua.Type(1) == kTypeNumber ? static_cast<int32_t>(lua.Number(1)) : 0;
  const int32_t player = lua.Type(2) == kTypeNumber ? static_cast<int32_t>(lua.Number(2)) : 0;
  float out[12] = {};
  int32_t count = 0;
  if (what == 1 && refii_mod_camera_pose(player, out) != 0) {
    count = 12;
  } else if (what == 2 && refii_mod_camera_stated(player, out) != 0) {
    count = 11;
  } else if (what == 3 && refii_mod_camera_view(player, out) != 0) {
    count = 4;
  } else if (what == 4) {
    int32_t split = 0;
    out[0] = static_cast<float>(refii_mod_camera_screen_mode(&split));
    out[1] = static_cast<float>(split);
    count = 2;
  } else if (what == 5) {
    const float x = lua.Type(3) == kTypeNumber ? static_cast<float>(lua.Number(3)) : 0.5f;
    const float y = lua.Type(4) == kTypeNumber ? static_cast<float>(lua.Number(4)) : 0.5f;
    if (refii_mod_camera_ray(player, x, y, out) != 0) {
      count = 6;
    }
  }
  if (count == 0) {
    lua.PushNil();
    g_results = 1;
    return;
  }
  for (int32_t i = 0; i < count; ++i) {
    lua.PushNumber(out[i]);
  }
  g_results = count;
}

/// Refii._camera_offset_stated(p, on, right, up, back, yaw, pitch, roll, fov): the display offset the
/// camera store resolved for player p, for the shell's drawn frame only.
REFII_F3_NATIVE(NativeCameraOffsetStated) {
  float seven[7];
  for (int32_t i = 0; i < 7; ++i) {
    seven[i] = lua.Type(3 + i) == kTypeNumber ? static_cast<float>(lua.Number(3 + i)) : 0.0f;
  }
  refii_bridge_camera_offset_set(lua.Type(1) == kTypeNumber ? static_cast<int32_t>(lua.Number(1)) : 0, seven,
                                 lua.Boolean(2) ? 1 : 0);
}

extern "C" int32_t RefiiF3CoopSplitScreen(void);

/// Refii._coop_split() -> whether each couch player has their own camera now (coop_camera_hooks.cpp): the
/// core chunk's own-camera rules and Refii.split_screen.
REFII_F3_NATIVE(NativeCoopSplit) {
  lua.PushBoolean(RefiiF3CoopSplitScreen() != 0);
  g_results = 1;
}

extern "C" int32_t RefiiF3PartnerCameraUpdating(void);
extern "C" void RefiiF3PlayerIconsFromCamera(int32_t on);
extern "C" int32_t RefiiF3PartnerCameraGeneration(void);
extern "C" void RefiiF3PartnerScreen(int32_t on);

/// Refii._partner_camera_update() -> true while player two's camera manager updates in a couch game, its
/// selection and its mode's frame update both (coop_camera_hooks.cpp RefiiF3PartnerCameraUpdating).
REFII_F3_NATIVE(NativePartnerCameraUpdate) {
  lua.PushBoolean(RefiiF3PartnerCameraUpdating() != 0);
  g_results = 1;
}

/// Refii._partner_screen(on): "the" camera answers player two's view until turned off (nests), as on Fable 2
/// (coop_camera_hooks.cpp RefiiF3PartnerScreen): the camera mod API's camera_shake for player two.
REFII_F3_NATIVE(NativePartnerScreen) {
  RefiiF3PartnerScreen(lua.Boolean(1) ? 1 : 0);
  g_results = 0;
}

/// Refii._partner_player_icons(on): player two's camera's GUI.SetShouldDrawPlayerIcons, stated for player two's
/// half (stated value 25: 1 off, 2 on; Fable3CoreLua.cs's wrap) instead of the one HUD's.
REFII_F3_NATIVE(NativePartnerPlayerIcons) {
  refii_bridge_stated_value(25u, lua.Boolean(1) ? 2u : 1u);
  g_results = 0;
}

/// Refii._camera_player_icons(on): true around a camera's own GUI.SetShouldDrawPlayerIcons (SACCamera:Activate /
/// :Deactivate, Fable3CoreLua.cs's wrap), so the native's send to the session's partner is skipped: a camera's
/// switch is its own machine's (coop_player_icons.cpp RefiiF3PlayerIconsStayHere).
REFII_F3_NATIVE(NativeCameraPlayerIcons) {
  RefiiF3PlayerIconsFromCamera(lua.Boolean(1) ? 1 : 0);
  g_results = 0;
}

/// Refii._partner_camera_manager() -> which camera manager player two's is: a small number moved on at each
/// new one (coop_camera_hooks.cpp RefiiF3PartnerCameraGeneration), float32-exact.
REFII_F3_NATIVE(NativePartnerCameraManager) {
  lua.PushNumber(static_cast<double>(RefiiF3PartnerCameraGeneration()));
  g_results = 1;
}

/// Refii._coop_tether_stated(distance, separation): the core camera chunk's statement each camera tick of the
/// co-op leash — the distance its camera last stated to Debug.SetLimitCharacterDistance and the heroes'
/// head-to-head separation, as the leash sub_8279C050 (disc sub_82765928) measures it — for hybrid's split.
REFII_F3_NATIVE(NativeCoopTetherStated) {
  if (lua.Type(1) == kTypeNumber && lua.Type(2) == kTypeNumber) {
    refii_bridge_coop_tether_stated(static_cast<float>(lua.Number(1)), static_cast<float>(lua.Number(2)));
  }
}

/// Refii.camera_mode(p) -> the player's ECameraMode (Refii.CAMERA_*), -1 before the first frame.
REFII_F3_NATIVE(NativeCameraMode) {
  const int32_t player = PlayerArgument(lua, 1, 1);
  lua.PushNumber(player != 0 ? g_camera_mode[static_cast<size_t>(player)] : -1);
  g_results = 1;
}

/// The locomotion component's field reader, TU3 sub_82249458 / disc sub_8224A298 (changed): r3 the
/// component (id 134), r4 a CCharString* key, r5 1 (also read the held overrides), f1 the value.
/// TU3 walks a stack of {.., override map +8, record +0} entries from comp+0xF8 down to comp+0xF4
/// (the top live entry); the disc reads one override map at comp+0x6C and the record at comp+0x64.
/// Both: the override map first (r5 != 0), then the record (sub_821C4C10 TU3 / sub_821C4C48 disc,
/// hash of the key by sub_82412818 / sub_823F04B8), and 1.0 when neither holds the key.
/// The caller this native copies, CECSprint_vf18 (TU3 0x8228A420 / disc 0x8228C350 exact):
///   TU3  8228a5b8 mr r31,r11 (component 0x86, `cmpwi cr6,r10,0x86` 8228a5a8)
///        8228a5d8 li r5,-1 / 8228a5dc addi r4,r11,aMaxspeed@l / 8228a5e0 addi r3,r1,0xA0+var_40
///        8228a5e4 bl sub_8221E890 (lhCharString_Ctor) / 8228a5e8 li r5,1 / 8228a5ec addi r4,r1,0xA0+var_40
///        8228a5f0 mr r3,r31 / 8228a5f4 bl sub_82249458 / 8228a5f8 addi r3,r1,0xA0+var_40 / 8228a600 bl lhCharString_Dtor
///   disc 8228c508 li r5,-1 / 8228c514 bl sub_8221F788 / 8228c518 li r5,1 / 8228c520 mr r3,r31
///        8228c524 bl sub_8224A298 / 8228c528 addi r3,r1,0xA0+var_40 / 8228c530 bl lhCharString_Dtor
REX_EXTERN(rex_LocomotionData_GetValue);

/// A light userdata's tag (lua_type 2; the game's glue reads a record ID as tag 2, e.g.
/// Lua_MessageEvents_PostMessage's "extra_data").
constexpr uint32_t kTagLightUserdata = 2u;
/// lua_type's LUA_TBOOLEAN (the file has kTypeNumber 3, kTypeString 4, kTypeTable 5, kTypeUserdata 7).
constexpr int32_t kTypeBoolean = 1;

/// ExpressionPerformer (component 177: PerformExpression's trampoline sub_82650CB8 / disc 0x82628538
/// exact looks the id up, `lbz r?,177(r10)` cache byte and the sorted search on 177). Its known
/// expressions are the vector +0x8C..+0x90 of 20-byte entries, the record ID first and the
/// extendable byte at +8 — IsExpressionAvailable's test sub_8264C130 (disc 0x82623BE8 exact):
///   8264c278 lwz r11,0x8C(r31) / 8264c27c lwz r10,0x90(r31) / 8264c28c lwz r8,0(r11) /
///   8264c298 addi r11,r11,0x14      (disc 82623d30 / 82623d34 / 82623d44 / 82623d50; r31 = r3 the component)
/// and IsExpressionExtendable's sub_8264CAC8 (disc 0x82624588 exact) answering `lbz r3,8(r11)`
/// (8264ccb4 / disc 82624774) for the entry whose first word is the record ID; the entry is built by
/// sub_8264C378: {record ID, record, extendable, 0, 0, 1000}, extendable set for "InitiallyExtendable" /
/// "Extendable". The first word is the record pair's ID (sub_821C5ED0(name) -> {ID, record}), what
/// RecordPtr:GetID answers and GDB.GetRecord takes as tag 2.
constexpr uint32_t kExpressionPerformerComponent = 177u;
constexpr uint32_t kExpressionsBegin = 0x8Cu;
constexpr uint32_t kExpressionsEnd = 0x90u;
constexpr uint32_t kExpressionEntry = 0x14u;
constexpr uint32_t kExpressionExtendable = 8u;

/// Refii._expression(p, i) -> id, extendable: player p's hero's i-th known expression (0-based), its
/// record ID as a light userdata; nil past the end; false without a hero or a performer.
REFII_F3_NATIVE(NativeExpression) {
  const uint32_t performer = HeroComponent(lua, PlayerArgument(lua, 1, 1), kExpressionPerformerComponent);
  const uint32_t first = performer != 0u ? Word(lua.base, performer + kExpressionsBegin) : 0u;
  const uint32_t last = performer != 0u ? Word(lua.base, performer + kExpressionsEnd) : 0u;
  if (performer == 0u || (first != last && (!Plausible(first) || last < first || (last - first) % kExpressionEntry != 0u ||
                                            last - first > kExpressionEntry * 1024u))) {
    lua.PushBoolean(false);
    g_results = 1;
    return;
  }
  const double index = lua.Type(2) == kTypeNumber ? lua.Number(2) : -1.0;
  const uint32_t count = (last - first) / kExpressionEntry;
  if (!(index >= 0.0) || index >= static_cast<double>(count)) {
    lua.PushNil();
    g_results = 1;
    return;
  }
  const uint32_t entry = first + static_cast<uint32_t>(index) * kExpressionEntry;
  lua.PushRaw(LoadU32(lua.base, entry), kTagLightUserdata);
  lua.PushBoolean(lua.base[entry + kExpressionExtendable] != 0u);
  g_results = 2;
}

/// Refii.release_expression(p) -> bool: the performer's own end of player p's held expression, Fable 2's
/// release (sub_82414A50). Fable 3's is TU3 sub_8264BDC0 / disc sub_82623880 (r3 the ExpressionPerformer):
/// it does nothing unless the hero has CECRelationshipHero (282: `cmpwi 0x11A` search) and an expression
/// is playing (performer+0x6C, the record: TU3 `result[27]` / disc `result + 108`); it clears that, and
/// while extending (+0xC) marks it released (+0xD = 1) and drops the extend player mode (16) — released
/// before the countdown the basic form plays, released while extending the extension ends. The game's
/// own callers: the trophy tap, the interaction mini-game's result, the PerformExpressionLoop exit. It
/// also stops an NPC interaction the interaction GUI holds in its first two states, as theirs does.
REFII_F3_NATIVE(NativeReleaseExpression) {
  const uint32_t performer = HeroComponent(lua, PlayerArgument(lua, 1, 1), kExpressionPerformerComponent);
  if (performer == 0u || Word(lua.base, performer + 0x6Cu) == 0u) {
    lua.PushBoolean(false);
    g_results = 1;
    return;
  }
  PPCContext work = Frame(lua.ctx, 0x200u);
  work.r3.u32 = performer;
  const bool called = refii::fable3::seam::CallAt(work, lua.base, refii::guest::Pick(0x8264BDC0u, 0x82623880u));
  lua.PushBoolean(called && Word(lua.base, performer + 0x6Cu) == 0u);
  g_results = 1;
}

// SPELL_BALANCE. As on Fable 2, each spell the factory makes reads its tuning per power: CSpellRegistry_Create
// (TU3 0x8268B618 / disc 0x82662210) calls the balance read (TU3 sub_8268BCD8 / disc sub_826628D0, r3 the
// spell) on the new spell, which takes +0x10 the type, +0x18 the weave (-1 none), +0x1C the weave part,
// +0x20 the mode (1 targeted, 0 area), +0x28 the power (0..4), +0x2C the caster's balance record, and
// stores at +0xB0 a heap struct of that power's defaults with the record's `<Type>/<Type>Targeted|Area/PowerN`
// values over them (`v3[44] = v11`). The record is the caster's spell manager's (component 14) +0x1A0
// (`stw r3,0x1A0(r28)` TU3 0x82630E54 / disc 0x8260891C, from the field SpellBalanceRecordRef). The same
// read runs here on a stand-in spell, and the struct is freed with the game's delete.
// The fields, by the installers that read them: Lightning's status ElectrifyDuration (targeted +4, `lfs
// f13,4(r30)` 0x82B42200; area +8) and its spread 5; Fireball's CreatureBurnTime (targeted +0, area +4),
// which Fable 3 jitters by ±7.5% instead of adding a spread (0); Chaos's Duration +0 (`lfs f12,0(r15)`
// 0x82B3B3C4) and spread 2; Vortex's Duration +0 (`lfs f31,0(r30)` 0x82BC11D4); Blades' NumSwords +0, an
// integer (`lwz r28,0(r30)` 0x8268D080). Types are Fable 3's: 0 Lightning, 1 Fireball, 3 Blades, 4 Vortex, 5 Chaos.
/// Refii._spell_balance(caster, type, power, targeted) -> status_seconds, status_spread, duration, swords
/// (nil where the class carries none); false with no spell manager or no struct.
REFII_F3_NATIVE(NativeSpellBalance) {
  const uint32_t caster = EntityArgument(lua, 1);
  const int32_t type = static_cast<int32_t>(lua.Number(2));
  const int32_t power = static_cast<int32_t>(lua.Number(3));
  const bool targeted = lua.Type(4) != kTypeBoolean || lua.Boolean(4);
  const uint32_t manager = caster != 0u ? refii::fable3::seam::Component(lua.base, caster, kSpellManagerComponent) : 0u;
  if (manager == 0u || type < 0 || type > 8 || power < 0 || power > 4) {
    lua.PushBoolean(false);
    g_results = 1;
    return;
  }
  PPCContext work = Frame(lua.ctx, 0x400u);
  const uint32_t spell = work.r1.u32 + 0x100u;
  std::memset(lua.base + spell, 0, 0xC0u);
  StoreU32(lua.base, spell + 0x10u, static_cast<uint32_t>(type));
  StoreU32(lua.base, spell + 0x18u, 0xFFFFFFFFu);
  StoreU32(lua.base, spell + 0x20u, targeted ? 1u : 0u);
  StoreU32(lua.base, spell + 0x28u, static_cast<uint32_t>(power));
  StoreU32(lua.base, spell + 0x2Cu, Word(lua.base, manager + 0x1A0u));
  work.r3.u32 = spell;
  const uint32_t balance = refii::fable3::seam::CallAt(work, lua.base, refii::guest::Pick(0x8268BCD8u, 0x826628D0u))
                               ? Word(lua.base, spell + 0xB0u)
                               : 0u;
  if (!Plausible(balance)) {
    lua.PushBoolean(false);
    g_results = 1;
    return;
  }
  const auto f = [&](uint32_t offset) { return static_cast<double>(refii::fable3::seam::Float(lua.base, balance + offset)); };
  double seconds = NAN, spread = NAN, duration = NAN, swords = NAN;
  switch (type) {
    case 0: seconds = f(targeted ? 4u : 8u); spread = 5.0; break;
    case 1: seconds = f(targeted ? 0u : 4u); spread = 0.0; break;
    case 3: swords = static_cast<double>(static_cast<int32_t>(Word(lua.base, balance))); break;
    case 4: duration = f(0u); break;
    case 5: seconds = f(0u); spread = 2.0; break;
    default: break;
  }
  GuestDelete(lua.ctx, lua.base, balance);
  for (const double v : {seconds, spread, duration, swords}) {
    if (std::isnan(v)) {
      lua.PushNil();
    } else {
      lua.PushNumber(v);
    }
  }
  g_results = 4;
}

// THE FRONT END'S ANSWERS (Fable 2's signed_in / save_count / screen, mod_frontend_lua.cpp). Fable 3 needs
// no front-end VM for them: its game VM stands from boot and the mods' runtime from START, so they are
// natives of the game VM, and Godot's `query frontend` reads them.
//   signed_in: the active pad START was accepted on (TU3 dword_832DF7BC / disc dword_8329ED38, written at
//     the accept: TU3 0x82319C58) when its user record says signed in — Lua_Global_GetExistingSaveGameNames'
//     own test (TU3 0x822F5690 / disc 0x822F1E48): the record *(*(net+0x28)+8) + 32*pad, signed in when
//     byte 0 (Live) or 1 (local) is set and byte 2 (guest) is clear. -1 with none.
//   save_count: the saves the game's own enumeration last found — the enumerator TU3 sub_822F5388 / disc
//     sub_822F1B40 (r3 user, r4 device, r5 the name vector {+4 begin, +8 end} of 4-byte CCharStrings),
//     as its callers count it, (end - begin) >> 2; -1 before the game enumerated.
//   screen: "splash" while the "Press START" splash stands, else "" (Fable 3 has no Gameface front-end
//     presentations to name).
const refii::guest::Addr kActivePad{0x832DF7BCu};
std::atomic<int32_t> g_save_count{-1};

REX_EXTERN(__imp__rex_SaveGame_EnumerateNames);

int32_t SignedInUser(const uint8_t* base) {
  const uint32_t pad = Word(base, kActivePad);
  const uint32_t manager = Word(base, kNetworkManager);
  const uint32_t users = Plausible(manager) ? Word(base, manager + 0x28u) : 0u;
  const uint32_t records = Plausible(users) ? Word(base, users + 8u) : 0u;
  if (pad >= 4u || !Plausible(records)) {
    return -1;
  }
  const uint8_t* record = base + records + 32u * pad;
  return (record[0] != 0u || record[1] != 0u) && record[2] == 0u ? static_cast<int32_t>(pad) : -1;
}

/// Refii.signed_in() -> the signed-in pad's user index, -1 none.
REFII_F3_NATIVE(NativeSignedIn) {
  lua.PushNumber(SignedInUser(lua.base));
  g_results = 1;
}

/// Refii.save_count() -> the saves the game's own enumeration last found, -1 before one.
REFII_F3_NATIVE(NativeSaveCount) {
  lua.PushNumber(g_save_count.load(std::memory_order_relaxed));
  g_results = 1;
}

/// Refii.screen() -> "splash" while the "Press START" splash stands, else "".
REFII_F3_NATIVE(NativeScreen) {
  lua.PushString(refii::fable3::splash::Up() ? "splash" : "");
  g_results = 1;
}

/// The locomotion component (id 0x86: CECSprint_vf18's search `cmpwi cr6,r10,0x86` TU3 8228a5a8 /
/// disc 8228c4d8, its value then `mr r31,r11` the getter's r3).
constexpr uint32_t kLocomotionComponent = 134u;

/// Refii.locomotion(p, key) -> number: what player p's hero's locomotion reads for a LocomotionData
/// field ("MaxSpeed", "SprintSpeedMultiplier", ..), through the game's own reader as CECSprint calls
/// it: the held override, else the loaded record, else 1.0 (the reader's own answer for a key neither
/// holds — Fable 3 cannot tell that apart, Fable 2 answers nil). nil without a hero or the component.
REFII_F3_NATIVE(NativeLocomotion) {
  const uint32_t component = HeroComponent(lua, PlayerArgument(lua, 1, 1), kLocomotionComponent);
  const std::string key = lua.String(2, 63u);
  if (component == 0u || key.empty()) {
    lua.PushNil();
    g_results = 1;
    return;
  }
  // The key's text is staged at the frame's scratch (+0x40); the CCharString (one word) above it.
  const uint32_t text = lua.Stage(key.c_str());
  const uint32_t holder = (lua.scratch + kScratchBytes - 0x10u) & ~3u;
  StoreU32(lua.base, holder, 0u);
  PPCContext w = Frame(lua.ctx, 0x200u);
  w.r3.u32 = holder;
  w.r4.u32 = text;
  w.r5.s64 = -1;
  rex_lhCharString_Ctor(w, lua.base);
  w = Frame(lua.ctx, 0x200u);
  w.r3.u32 = component;
  w.r4.u32 = holder;
  w.r5.u64 = 1u;
  rex_LocomotionData_GetValue(w, lua.base);
  const double value = w.f1.f64;
  w = Frame(lua.ctx, 0x200u);
  w.r3.u32 = holder;
  rex_lhCharString_Dtor(w, lua.base);
  lua.PushNumber(value);
  g_results = 1;
}

// SET_LOCOMOTION. Fable 2 writes a held value into the hero's own copy of the LocomotionData table and
// re-applies it after each load. Fable 3 keeps that copy as value objects in a per-hero property table
// (TU3: one per pushed layer, a layer lapsing when its weak owner dies, with no call at that moment),
// so a hold is answered at the one reader instead: every read goes through LocomotionData_GetValue
// (TU3 0x82249458 / disc 0x8224A298, r3 the CECPlayerControl component 134, r4 the key's CCharString,
// r5 the state-first byte; f1 the value, 1.0 for a key nothing holds), CECSprint's MaxSpeed and
// SprintTerminationSpeedFraction included (TU3 8228a5f4 / 8228a8b4 / 8228a8e4). Fable 2's order holds:
// a caller that asks state-first (r5) still gets the active locomotion state's own MovementParams
// when that map has the key, else the held value, else the game's.
//   TU3: the live top layer is the last entry of the 12-byte vector comp+0xF4..+0xF8 whose layer P
//        (entry+4) still has its owner (`*(P+28)` non-null and `**(P+28)` non-zero: 82249484 lwz
//        r11,-8(r31) / 8224948c lwz r11,0x1C(r11)); its state map is P+8 (find sub_82D6E200 at
//        82249544, the end node `lwz r10,0xC(r31)` 82249548).
//   disc: the state map is comp+0x6C (find sub_823CE190 at 8224a2fc, end comp+0x70 at 8224a300).
// The key is hashed by the game's name hash (TU3 sub_82412818 / disc sub_823F04B8: text, length).
REX_EXTERN(__imp__rex_LocomotionData_GetValue);
REX_EXTERN(rex_LocomotionName_Hash);
REX_EXTERN(rex_LocomotionMap_Find);

/// The fields a hold may name: Fable 2's ten but MinVelMagForMovement (absent from Fable 3), and the
/// four Fable 3 reads that Fable 2 does not.
constexpr const char* kLocomotionKeys[] = {
    "MinControlMagForMovement", "MaxTurnSpeedStatic", "MaxTurnSpeedRunning", "MaxSpeed",
    "SprintSpeedMultiplier", "SprintTerminationSpeedFraction", "FrictionCoefficient",
    "MaxSpeedLateralStatic", "MaxSpeedLateralRunning", "FrictionCoefficientSecondOrder",
    "FrictionCoefficientLateral", "MinControlMagForTurn", "AllowedToMoveBackwards"};

/// Each player's held fields, by key text.
std::array<std::vector<std::pair<std::string, float>>, 2> g_locomotion_held;
std::atomic<int32_t> g_locomotion_holds{0};

bool LocomotionKey(const std::string& key) {
  for (const char* known : kLocomotionKeys) {
    if (key == known) {
      return true;
    }
  }
  return false;
}

/// Whether the active locomotion state's MovementParams hold `text` (the getter's state-first map).
bool LocomotionStateHolds(const PPCContext& ctx, uint8_t* base, uint32_t component, uint32_t text, uint32_t length) {
  uint32_t map = 0u;
  uint32_t end = 0u;
  if (refii::guest::Current() == refii::guest::Build::kDisc) {
    map = component + 0x6Cu;
    end = Word(base, component + 0x70u);
  } else {
    const uint32_t first = Word(base, component + 0xF4u);
    for (uint32_t entry = Word(base, component + 0xF8u); entry != first && entry > first; entry -= 0xCu) {
      const uint32_t layer = Word(base, entry - 8u);
      const uint32_t owner = layer != 0u ? Word(base, layer + 28u) : 0u;
      if (owner != 0u && Word(base, owner) != 0u) {
        map = layer + 8u;
        end = Word(base, layer + 12u);
        break;
      }
    }
  }
  if (map == 0u) {
    return false;
  }
  PPCContext work = Frame(ctx, 0x200u);
  work.r3.u32 = text;
  work.r4.u32 = length;
  rex_LocomotionName_Hash(work, base);
  const uint32_t scratch = work.r1.u32 + 0x100u;
  StoreU32(base, scratch, work.r3.u32);
  StoreU32(base, scratch + 4u, 0u);
  work = Frame(ctx, 0x200u);
  work.r3.u32 = scratch + 4u;
  work.r4.u32 = map;
  work.r5.u32 = scratch;
  rex_LocomotionMap_Find(work, base);
  return Word(base, scratch + 4u) != end;
}

/// Refii._set_locomotion(p, key, value) -> bool: holds one field for player p's hero; a non-number
/// stops holding it (the game's own value reads again). False for a field Fable 3 does not read or
/// no hero.
REFII_F3_NATIVE(NativeSetLocomotion) {
  const int32_t player = PlayerArgument(lua, 1, 1);
  const std::string key = lua.String(2, 63u);
  if ((player != 1 && player != 2) || !LocomotionKey(key) || HeroComponent(lua, player, kLocomotionComponent) == 0u) {
    lua.PushBoolean(false);
    g_results = 1;
    return;
  }
  auto& held = g_locomotion_held[player - 1];
  auto it = std::find_if(held.begin(), held.end(), [&](const auto& entry) { return entry.first == key; });
  if (lua.Type(3) == kTypeNumber && std::isfinite(lua.Number(3))) {
    const float value = static_cast<float>(lua.Number(3));
    if (it != held.end()) {
      it->second = value;
    } else {
      held.emplace_back(key, value);
    }
  } else if (it != held.end()) {
    held.erase(it);
  }
  g_locomotion_holds.store(static_cast<int32_t>(g_locomotion_held[0].size() + g_locomotion_held[1].size()),
                           std::memory_order_relaxed);
  lua.PushBoolean(true);
  g_results = 1;
}

REX_EXTERN(rex_LocomotionData_GetValue) {
  if (g_locomotion_holds.load(std::memory_order_relaxed) == 0) {
    __imp__rex_LocomotionData_GetValue(ctx, base);
    return;
  }
  const uint32_t component = ctx.r3.u32;
  int32_t player = 0;
  for (int32_t p = 1; p <= 2 && player == 0; ++p) {
    const uint32_t hero = PlayerHero(base, p);
    if (hero != 0u && refii::fable3::seam::Component(base, hero, kLocomotionComponent) == component) {
      player = p;
    }
  }
  const uint32_t rep = player != 0 ? Word(base, ctx.r4.u32) : 0u;
  const uint32_t text = rep != 0u ? Word(base, rep) : 0u;
  const uint32_t length = rep != 0u ? Word(base, rep + 4u) : 0u;
  if (text == 0u || length == 0u || length > 63u) {
    __imp__rex_LocomotionData_GetValue(ctx, base);
    return;
  }
  const std::string key(reinterpret_cast<const char*>(base + text), length);
  const auto& held = g_locomotion_held[player - 1];
  const auto it = std::find_if(held.begin(), held.end(), [&](const auto& entry) { return entry.first == key; });
  if (it == held.end() || ((ctx.r5.u32 & 0xFFu) != 0u && LocomotionStateHolds(ctx, base, component, text, length))) {
    __imp__rex_LocomotionData_GetValue(ctx, base);
    return;
  }
  ctx.f1.f64 = static_cast<double>(it->second);
}

// PLAY_SOUND_AT. A cue plays on a sound player (NSound::CSoundPlayer, PC RTTI) the sound system owns:
// g_snd (TU3 0x835974DC / disc 0x835569DC) keeps a list of them, {next, prev, +8 player}. An entity's
// CECSound makes its own at the entity's position (the play helper TU3 0x82699C40 / disc 0x821F2230:
// `+0x4D` enabled byte, `+0x2C` its player, created by sub_82D4F1A8 / disc sub_82D1CE90 with r3 g_snd,
// v1 the point). Lua_Sound_PlayEvent (TU3 0x82699EF0 / disc 0x826709F8) is: params from the cue
// (sub_821EC9B0 / disc sub_821ED3D8, r3 the 112-byte params, r4 the cue's CCharString), the helper
// (TU3 through sub_821F1900, which FNV-1-hashes the tag; disc the helper with the tag's CCharString),
// the voice handle out[1] released by its slot 1, the params' destructor (sub_821E3578 / disc
// sub_821E3968). The same sequence plays here on a stand-in for the CECSound whose player is one of
// ours, made at the point: eight kept, the oldest freed by FreePlayer (sub_82D4F2C0 / disc
// sub_82D1CFA8, which deletes only a player still in g_snd's list). The helper's own gates apply
// (enabled, params flag 2, the cue known, the group byte). A point sound is not sent to the partner.
// stop_sound with entity 0 stops a tag on our players still listed (StopCategory sub_82D501D0 /
// disc sub_82D1DD10: r4 the tag's hash on TU3, a pointer to its CCharString on disc; r5 the fade).
constexpr uint32_t kPointPlayers = 8u;
std::array<uint32_t, kPointPlayers> g_point_players{};
uint32_t g_point_next = 0u;

const refii::guest::Addr kSoundSystem{0x835974DCu};

uint32_t SoundSystem(const uint8_t* base) { return Word(base, kSoundSystem); }

bool PointPlayerListed(const uint8_t* base, uint32_t system, uint32_t player) {
  const uint32_t head = system != 0u ? Word(base, system) : 0u;
  if (head == 0u || player == 0u) {
    return false;
  }
  uint32_t node = Word(base, head);
  for (uint32_t guard = 0u; node != head && node != 0u && guard < 4096u; ++guard) {
    if (Word(base, node + 8u) == player) {
      return true;
    }
    node = Word(base, node);
  }
  return false;
}

/// Writes `text` (at most 127 bytes) at `at`, NUL-terminated.
void StageText(uint8_t* base, uint32_t at, const std::string& text) {
  const size_t n = std::min<size_t>(text.size(), 127u);
  std::memcpy(base + at, text.data(), n);
  base[at + n] = 0u;
}

/// Refii._play_sound_at(cue, x, y, z, tag) -> bool: the cue started at the point (guest axes).
REFII_F3_NATIVE(NativePlaySoundAt) {
  const std::string cue = lua.String(1, 127u);
  const float x = FloatArgument(lua, 2), y = FloatArgument(lua, 3), z = FloatArgument(lua, 4);
  const std::string tag = lua.Type(5) == kTypeString ? lua.String(5, 127u) : cue;
  const uint32_t system = SoundSystem(lua.base);
  if (cue.empty() || system == 0u || !std::isfinite(x) || !std::isfinite(y) || !std::isfinite(z)) {
    lua.PushBoolean(false);
    g_results = 1;
    return;
  }
  const PPCContext outer = Frame(lua.ctx, 0x600u);
  const uint32_t at = outer.r1.u32 + 0x100u;
  const uint32_t cue_holder = at, tag_holder = at + 4u, out = at + 8u, sound = at + 0x10u, params = at + 0x80u;
  const uint32_t cue_text = at + 0x120u, tag_text = at + 0x1A0u;
  std::memset(lua.base + at, 0, 0x220u);
  StageText(lua.base, cue_text, cue);
  StageText(lua.base, tag_text, tag);

  // The oldest of ours goes first: FreePlayer deletes it only if g_snd still lists it.
  uint32_t& slot = g_point_players[g_point_next];
  g_point_next = (g_point_next + 1u) % kPointPlayers;
  PPCContext work = outer;
  if (slot != 0u) {
    work.r3.u32 = system;
    work.r4.u32 = slot;
    refii::fable3::seam::CallAt(work, lua.base, refii::guest::Pick(0x82D4F2C0u, 0x82D1CFA8u));
    slot = 0u;
  }
  work = outer;
  work.r3.u32 = system;
  work.v1.f32[3] = x;
  work.v1.f32[2] = y;
  work.v1.f32[1] = z;
  work.v1.f32[0] = 0.0f;
  if (!refii::fable3::seam::CallAt(work, lua.base, refii::guest::Pick(0x82D4F1A8u, 0x82D1CE90u)) || work.r3.u32 == 0u) {
    lua.PushBoolean(false);
    g_results = 1;
    return;
  }
  slot = work.r3.u32;

  // The stand-in CECSound: its player (+0x2C) and the enabled byte (+0x4D); the entity (+4) is read
  // only to make a player, which it has.
  StoreU32(lua.base, sound + 0x2Cu, slot);
  lua.base[sound + 0x4Du] = 1u;
  for (const auto& [holder, text] : {std::pair{cue_holder, cue_text}, std::pair{tag_holder, tag_text}}) {
    work = outer;
    work.r3.u32 = holder;
    work.r4.u32 = text;
    work.r5.s64 = -1;
    rex_lhCharString_Ctor(work, lua.base);
  }
  work = outer;
  work.r3.u32 = params;
  work.r4.u32 = cue_holder;
  refii::fable3::seam::CallAt(work, lua.base, refii::guest::Pick(0x821EC9B0u, 0x821ED3D8u));
  work = outer;
  work.r3.u32 = out;
  work.r4.u32 = sound;
  work.r5.u32 = params;
  work.r6.u32 = tag_holder;
  refii::fable3::seam::CallAt(work, lua.base, refii::guest::Pick(0x821F1900u, 0x821F2230u));
  const bool started = Word(lua.base, out) != 0u;
  const uint32_t voice = Word(lua.base, out + 4u);
  if (voice != 0u) {
    work = outer;
    refii::fable3::seam::CallSlot(work, lua.base, voice, 1u);
  }
  work = outer;
  work.r3.u32 = params;
  refii::fable3::seam::CallAt(work, lua.base, refii::guest::Pick(0x821E3578u, 0x821E3968u));
  for (const uint32_t holder : {cue_holder, tag_holder}) {
    work = outer;
    work.r3.u32 = holder;
    rex_lhCharString_Dtor(work, lua.base);
  }
  lua.PushBoolean(started);
  g_results = 1;
}

/// Refii._stop_point_sounds(tag, fade) -> bool: the tag stopped on every point player still listed.
REFII_F3_NATIVE(NativeStopPointSounds) {
  const std::string tag = lua.String(1, 127u);
  const int32_t fade = lua.Type(2) == kTypeNumber ? static_cast<int32_t>(lua.Number(2)) : 0;
  const uint32_t system = SoundSystem(lua.base);
  if (tag.empty() || system == 0u) {
    lua.PushBoolean(false);
    g_results = 1;
    return;
  }
  const PPCContext outer = Frame(lua.ctx, 0x400u);
  const uint32_t holder = outer.r1.u32 + 0x100u, text = outer.r1.u32 + 0x120u;
  StageText(lua.base, text, tag);
  StoreU32(lua.base, holder, 0u);
  const bool disc = refii::guest::Current() == refii::guest::Build::kDisc;
  uint32_t key = 0x811C9DC5u;  // TU3: the FNV-1 the play wrapper and Sound.StopSoundCategoryPlaying take
  for (const char c : tag) {
    key = (key * 0x01000193u) ^ static_cast<uint8_t>(c);
  }
  PPCContext work = outer;
  if (disc) {
    work.r3.u32 = holder;
    work.r4.u32 = text;
    work.r5.s64 = -1;
    rex_lhCharString_Ctor(work, lua.base);
  }
  bool any = false;
  for (const uint32_t player : g_point_players) {
    if (!PointPlayerListed(lua.base, system, player)) {
      continue;
    }
    work = outer;
    work.r3.u32 = player;
    work.r4.u32 = disc ? holder : key;
    work.r5.s64 = fade;
    any = refii::fable3::seam::CallAt(work, lua.base, refii::guest::Pick(0x82D501D0u, 0x82D1DD10u)) || any;
  }
  if (disc) {
    work = outer;
    work.r3.u32 = holder;
    rex_lhCharString_Dtor(work, lua.base);
  }
  lua.PushBoolean(any);
  g_results = 1;
}

/// A store text read as a hash by Fable 2's refii_mod_player_hash rule: strtoull base 0, the whole
/// text, at most 32 bits (Godot's RefiiHost.PlayerHash reads it the same way).
bool StoreHash(const char* text, uint32_t* out) {
  char* end = nullptr;
  const unsigned long long value = std::strtoull(text, &end, 0);
  if (end == text || *end != 0 || value > 0xFFFFFFFFull) {
    return false;
  }
  *out = static_cast<uint32_t>(value);
  return true;
}

/// Refii.player_value_hash(p, key) -> "0x…", nil when the key holds no hash text (Fable 2's
/// NativePlayerValueHash over the same store).
REFII_F3_NATIVE(NativePlayerValueHash) {
  const std::string key = StoreKey(lua);
  char text[257];
  uint32_t hash = 0u;
  if (!key.empty() &&
      refii_bridge_player_get_text(PlayerArgument(lua, 1, 0), key.data(), key.size(), text, sizeof(text)) > 0 &&
      StoreHash(text, &hash)) {
    char hex[16];
    std::snprintf(hex, sizeof(hex), "0x%08x", hash);
    lua.PushString(hex);
  } else {
    lua.PushNil();
  }
  g_results = 1;
}

/// Refii.player_save_hash(p, key, hash): a saved text "0x%08x" (Fable 2's refii_mod_player_save_hash);
/// hash a "0x…" / decimal string (strtoul base 0) or a number (clamped to 32 bits), as Fable 2's Lua::Hash.
REFII_F3_NATIVE(NativePlayerSaveHash) {
  const std::string key = StoreKey(lua);
  if (key.empty()) {
    return;
  }
  uint32_t hash = 0u;
  if (lua.Type(3) == kTypeString) {
    hash = static_cast<uint32_t>(std::strtoul(lua.String(3, 63u).c_str(), nullptr, 0));
  } else {
    const double n = lua.Number(3);
    hash = !(n > 0.0) ? 0u : n >= 4294967295.0 ? 0xFFFFFFFFu : static_cast<uint32_t>(n);
  }
  char text[16];
  std::snprintf(text, sizeof(text), "0x%08x", hash);
  refii_bridge_player_write_text(PlayerArgument(lua, 1, 0), key.data(), key.size(), text, std::strlen(text), 1);
}

/// Refii.morph_id("<mod>.<name>") -> "0x…": Fable 2's refii_mod_morph_id (FNV-1 of the trimmed,
/// lower-cased name, top bit cleared, moved past 0x100); "0x…" passes through. Pure text: no game state.
/// A morph named by its id text ("0x…") or "<mod>.<name>" (Fable 2's MorphArgument).
uint32_t MorphArgument(GuestLua& lua, int32_t index) {
  const std::string name = lua.String(index, 159u);
  if (name.size() > 2 && name[0] == '0' && (name[1] == 'x' || name[1] == 'X')) {
    return static_cast<uint32_t>(std::strtoul(name.c_str() + 2, nullptr, 16));
  }
  return name.empty() ? 0u : refii_mod_morph_id(name.c_str());
}

REFII_F3_NATIVE(NativeMorphId) {
  char hex[16];
  std::snprintf(hex, sizeof(hex), "0x%08x", MorphArgument(lua, 1));
  lua.PushString(hex);
  g_results = 1;
}

/// Refii.morph_get(p, morph) -> weight, or nil for a morph no enabled mod declares (refii_mod_morph_weight).
REFII_F3_NATIVE(NativeMorphGet) {
  float weight = 0.0f;
  if (refii_mod_morph_weight(PlayerArgument(lua, 1, 1), MorphArgument(lua, 2), &weight) != 0) {
    lua.PushNumber(weight);
  } else {
    lua.PushNil();
  }
  g_results = 1;
}

/// Refii.morph_set(p, morph, w) -> ok: the weight saved with the hero (clamped to the declared range); the
/// hero is redrawn and morph_changed raised on the next tick (StateModMorphs).
REFII_F3_NATIVE(NativeMorphSet) {
  lua.PushBoolean(refii_mod_set_morph_weight(PlayerArgument(lua, 1, 1), MorphArgument(lua, 2), FloatArgument(lua, 3)) != 0);
  g_results = 1;
}

// MODS' OWN MORPHS ON FABLE 3. The weight function TU3 sub_8229F068 / disc sub_822A0A38 (r3 the hero's
// CECMorph, component 0x8C, its entity at +4: `lwz r7,4(r3)` 0x8229F088; r4 the MorphType; f1 the weight)
// is asked per row of the hero's MorphTargets list (the draw's two passes, TU3 0x8222B5C8 / 0x8222B638),
// by the skeletal and particle morphs too, and answers an unknown type 0.0 — its switch is bounded at 14
// (`cmplwi cr6,r11,0xD` 0x8229F288) — behind its own adult gate (`bl Lua_Age_GetAgeGroup; cmpwi r3,1;
// ble default` 0x8229F23C..44). A registered mod type is answered the player's stored weight
// (refii_mod_morph_weight) where the game's own adult probe, type 14 (1.0 for an adult), passes. The game
// weighs again only when the hero rebuilds: a changed weight sets the CECMorph dirty (TU3 sub_82668860 /
// disc sub_8263F4C8: `stb r9(1),0xC(r3)` 0x82668870 / 0x8263F4D8), as Morph.SetMorphOverride does; its
// update reapplies the skeletal morphs and invalidates the drawn parts.
constexpr uint32_t kMorphComponent = 0x8Cu;
constexpr int32_t kEventMorphChanged = 115;

/// Each player's hero and the mod weights it was last answered with, by id.
struct MorphSeen {
  uint32_t hero = 0u;
  std::vector<std::pair<uint32_t, float>> weights;
};
std::array<MorphSeen, 2> g_morph_seen;


/// Refii.wheel_switches_target(e [, on]): e's target lock switches by the mouse wheel — e alone for
/// this frame (state it each tick the lock holds), on true / false STICKY until off or the lock goes
/// (mouse_look.cpp below). Any hero; it acts only where the mouse drives that hero's camera.
REFII_F3_NATIVE(NativeWheelSwitchesTarget) {
  const uint32_t hero = EntityArgument(lua, 1);
  const int32_t kind = lua.Type(2);
  if (hero != 0u) {
    if (kind == kTypeBoolean || kind == kTypeNumber) {
      const bool on = kind == kTypeBoolean ? lua.Boolean(2) : lua.Number(2) != 0.0;
      refii::fable3::mouselook::StateWheel(lua.base, hero, on ? 2 : 0);
    } else {
      refii::fable3::mouselook::StateWheel(lua.base, hero, 1);
    }
  }
}

/// Refii._camera_held(hero, bits): the halves of hero's override record the camera_override hold
/// wrote and still stands on (1 Normal, 2 LookAt), stated by Fable3CoreLua's Refii._camera_apply.
REFII_F3_NATIVE(NativeCameraHeld) {
  const int32_t player = refii::fable3::modlua::PlayerOf(lua.base, EntityArgument(lua, 1));
  if (player == 1 || player == 2) {
    refii::fable3::modlua::NoteCameraHeld(player, lua.Type(2) == kTypeNumber ? static_cast<int32_t>(lua.Number(2)) & 3 : 0);
  }
}

/// Refii._camera_native_restore(hero, normal, look_at): the Player component's two override-camera
/// words stored back (Fable3CoreLua's Refii._camera_apply, after each SetCameraOverride of the hold).
REFII_F3_NATIVE(NativeCameraNativeRestore) {
  const uint32_t hero = EntityArgument(lua, 1);
  if (hero != 0u && lua.Type(2) == kTypeNumber && lua.Type(3) == kTypeNumber) {
    refii::fable3::modlua::RestoreCameraModes(lua.base, hero, static_cast<uint32_t>(static_cast<int32_t>(lua.Number(2))),
                                              static_cast<uint32_t>(static_cast<int32_t>(lua.Number(3))));
  }
}

/// Refii._view_fov() -> degrees: the horizontal 4:3 FOV of the camera this machine renders with, as the
/// shell states its rebuild back (the block's +0x30); 0 before the first.
REFII_F3_NATIVE(NativeViewFov) {
  lua.PushNumber(static_cast<double>(refii::fable3::view::StatedFovRadians()) * 57.29577951308232);
  g_results = 1;
}

/// Once a tick: a player's changed mod morph weight redraws that hero and raises morph_changed (on every
/// machine: the weights are the player store's, the same on both). A new hero only takes the baseline.
void StateModMorphs(const PPCContext& ctx, uint8_t* base) {
  std::array<uint32_t, 256> ids{};
  const int32_t count = std::min<int32_t>(refii_mod_morph_list(ids.data(), static_cast<int32_t>(ids.size())), 256);
  for (int32_t player = 1; player <= 2; ++player) {
    MorphSeen& seen = g_morph_seen[player - 1];
    const uint32_t hero = PlayerHero(base, player);
    std::vector<std::pair<uint32_t, float>> now;
    for (int32_t i = 0; i < count; ++i) {
      float weight = 0.0f;
      if (refii_mod_morph_weight(player, ids[i], &weight) != 0) {
        now.emplace_back(ids[i], weight);
      }
    }
    if (hero != seen.hero) {
      seen.hero = hero;
      seen.weights = std::move(now);
      continue;
    }
    if (hero == 0u || now == seen.weights) {
      continue;
    }
    for (const auto& [id, weight] : now) {
      const auto was = std::find_if(seen.weights.begin(), seen.weights.end(), [&](const auto& w) { return w.first == id; });
      if (was == seen.weights.end() || was->second != weight) {
        refii::fable3::modlua::QueueEvent(kEventMorphChanged, refii::fable3::seam::Fields("player=%d,morph='0x%08x',weight=%.9g,hero=%u", player, id,
                                                                  static_cast<double>(weight), refii_mod_handle(hero)));
      }
    }
    seen.weights = std::move(now);
    const uint32_t morph = refii::fable3::seam::Component(base, hero, kMorphComponent);
    if (morph != 0u) {
      PPCContext work = Frame(ctx, 0x200u);
      work.r3.u32 = morph;
      work.r4.u64 = 0u;
      refii::fable3::seam::CallAt(work, base, refii::guest::Pick(0x82668860u, 0x8263F4C8u));
    }
  }
}

Native g_natives[] = {
    {"log", &NativeLog, 0u},
    {"_view_fov", &NativeViewFov, 0u},
    {"_query_reply", &NativeHostReply, 0u},
    {"_player_load_saved", &NativePlayerLoadSaved, 0u},
    {"_expression", &NativeExpression, 0u},
    {"locomotion", &NativeLocomotion, 0u},
    {"_set_locomotion", &NativeSetLocomotion, 0u},
    {"_play_sound_at", &NativePlaySoundAt, 0u},
    {"release_expression", &NativeReleaseExpression, 0u},
    {"_spell_balance", &NativeSpellBalance, 0u},
    {"signed_in", &NativeSignedIn, 0u},
    {"save_count", &NativeSaveCount, 0u},
    {"screen", &NativeScreen, 0u},
    {"_stop_point_sounds", &NativeStopPointSounds, 0u},
    {"player_value_hash", &NativePlayerValueHash, 0u},
    {"player_save_hash", &NativePlayerSaveHash, 0u},
    {"morph_id", &NativeMorphId, 0u},
    {"morph_get", &NativeMorphGet, 0u},
    {"morph_set", &NativeMorphSet, 0u},
    {"wheel_switches_target", &NativeWheelSwitchesTarget, 0u},
    {"_camera_held", &NativeCameraHeld, 0u},
    {"_camera_native_restore", &NativeCameraNativeRestore, 0u},
    {"_error", &NativeError, 0u},
    {"publish", &NativePublish, 0u},
    {"value", &NativeValue, 0u},
    {"hash", &NativeHash, 0u},
    {"player_hero", &NativePlayerHero, 0u},
    {"entity", &NativeEntity, 0u},
    {"handle", &NativeHandle, 0u},
    {"alive", &NativeAlive, 0u},
    {"_play_fx", &NativePlayFx, 0u},
    {"stop_fx", &NativeStopFx, 0u},
    {"_light", &NativeLight, 0u},
    {"light_remove", &NativeLightRemove, 0u},
    {"open_menu", &NativeOpenMenu, 0u},
    {"_message_box", &NativeMessageBox, 0u},
    {"notice", &NativeNotice, 0u},
    {"_menu_box", &NativeMenuBox, 0u},
    {"_text", &NativeText, 0u},
    {"_mark", &NativeMark, 0u},
    {"unmark", &NativeUnmark, 0u},
    {"_world_meter", &NativeWorldMeter, 0u},
    {"mark_meter", &NativeMarkMeter, 0u},
    {"fade", &NativeFade, 0u},
    {"meter", &NativeMeter, 0u},
    {"meter_remove", &NativeMeterRemove, 0u},
    {"input_claim", &NativeInputClaim, 0u},
    {"hud_icon", &NativeHudIcon, 0u},
    {"hud_slide", &NativeHudSlide, 0u},
    {"hud_hold", &NativeHudHold, 0u},
    {"press", &NativePress, 0u},
    {"tap", &NativeTap, 0u},
    {"_menu_rows", &NativeMenuRows, 0u},
    {"open_screen", &NativeOpenScreen, 0u},
    {"_cancelled", &NativeCancelled, 0u},
    {"_write", &NativeWrite, 0u},
    {"current_action", &NativeCurrentAction, 0u},
    {"action_elapsed", &NativeActionElapsed, 0u},
    {"action_interruptible", &NativeActionInterruptible, 0u},
    {"allow_action_interrupt", &NativeAllowActionInterrupt, 0u},
    {"allow_action_interrupt_at", &NativeAllowActionInterruptAt, 0u},
    {"action_type", &NativeActionType, 0u},
    {"player_of", &NativePlayerOf, 0u},
    {"living", &NativeLiving, 0u},
    {"lock_target", &NativeLockTarget, 0u},
    {"locked_target", &NativeLockedTarget, 0u},
    {"release_lock", &NativeReleaseLock, 0u},
    {"in_cutscene", &NativeInCutscene, 0u},
    {"creature_tier", &NativeCreatureTier, 0u},
    {"event_replica", &NativeEventReplica, 0u},
    {"player_here", &NativePlayerHere, 0u},
    {"achievement_unlocked", &NativeAchievementUnlocked, 0u},
    {"achievements", &NativeAchievements, 0u},
    {"character_id", &NativeCharacterId, 0u},
    {"player_value", &NativePlayerValue, 0u},
    {"player_set", &NativePlayerSet, 0u},
    {"player_save", &NativePlayerSave, 0u},
    {"player_add", &NativePlayerAdd, 0u},
    {"player_value_text", &NativePlayerValueText, 0u},
    {"player_set_text", &NativePlayerSetText, 0u},
    {"player_save_text", &NativePlayerSaveText, 0u},
    {"_speed_overridden", &NativeSpeedOverridden, 0u},
    {"end_sprint", &NativeEndSprint, 0u},
    {"has_status", &NativeHasStatus, 0u},
    {"clear_status", &NativeClearStatus, 0u},
    {"apply_status", &NativeApplyStatus, 0u},
    {"attack_mode", &NativeAttackMode, 0u},
    {"_spell_parts", &NativeSpellParts, 0u},
    {"spell_serial", &NativeSpellSerial, 0u},
    {"_vortex_held", &NativeVortexHeld, 0u},
    {"_charge_lend", &NativeChargeLend, 0u},
    {"signal", &NativeSignal, 0u},
    {"publish_text", &NativePublishText, 0u},
    {"send_to_partner", &NativeSendToPartner, 0u},
    {"ask_partner", &NativeAskPartner, 0u},
    {"partner_answer", &NativePartnerAnswer, 0u},
    {"_decide", &NativeDecide, 0u},
    {"_by_uid", &NativeByUid, 0u},
    {"_use_item", &NativeUseItem, 0u},
    {"hide_part", &NativeHidePart, 0u},
    {"_camera_stated", &NativeCameraStated, 0u},
    {"camera_mode", &NativeCameraMode, 0u},
    {"_camera_zoom_stated", &NativeCameraZoomStated, 0u},
    {"_camera_read", &NativeCameraRead, 0u},
    {"_camera_offset_stated", &NativeCameraOffsetStated, 0u},
    {"_coop_tether_stated", &NativeCoopTetherStated, 0u},
    {"_coop_split", &NativeCoopSplit, 0u},
    {"_partner_camera_update", &NativePartnerCameraUpdate, 0u},
    {"_partner_camera_manager", &NativePartnerCameraManager, 0u},
    {"_partner_screen", &NativePartnerScreen, 0u},
    {"_partner_player_icons", &NativePartnerPlayerIcons, 0u},
    {"_camera_player_icons", &NativeCameraPlayerIcons, 0u},
};

/// A guest function inside the game module, for the thunk pool: lhLua_RunScriptBuffer.
const refii::guest::Addr kModuleAnchor{0x822E56E0u};

bool AllocateThunks() {
  static bool done = false;
  static bool ok = false;
  if (done) {
    return ok;
  }
  done = true;
  ok = true;
  for (Native& native : g_natives) {
    native.thunk = refii_bridge_rex_allocate_thunk(reinterpret_cast<void*>(native.function), kModuleAnchor);
    if (native.thunk == 0u) {
      REXLOG_WARN("Refii mod Lua: no guest address for Refii.{}; the natives are not registered", native.name);
      ok = false;
    }
  }
  return ok;
}

/// The namespace RegisterFunctionHash reads for the Refii natives, built once in the guest heap:
/// block+0 the namespace (+8 its CCharString "Refii", +12 its enable byte), block+0x18 the holder.
uint32_t RefiiNamespaceHolder(PPCContext& ctx, uint8_t* base) {
  static uint32_t holder = 0u;
  if (holder != 0u) {
    return holder;
  }
  const uint32_t block = GuestNew(ctx, base, 0x40u);
  if (!Plausible(block)) {
    return 0u;
  }
  std::memset(base + block, 0, 0x40u);
  const uint32_t chars = block + 0x20u;
  std::memcpy(base + chars, "Refii", 6u);
  PPCContext w = Frame(ctx, 0x100u);
  w.r3.u32 = block + 8u;
  w.r4.u32 = chars;
  w.r5.s64 = -1;
  rex_lhCharString_Ctor(w, base);
  base[block + 12u] = 1u;
  StoreU32(base, block + 0x18u, block);
  holder = block + 0x18u;
  return holder;
}

/// The Refii table and every native in it, in the VM `state`.
void RegisterNatives(PPCContext& ctx, uint8_t* base, uint32_t state) {
  if (!AllocateThunks()) {
    return;
  }
  PPCContext frame = Frame(ctx, kNativeFrame);
  GuestLua lua(frame, base, state);
  std::string error;
  if (!lua.Run("Refii = Refii or {}", "=refii/natives", &error)) {
    REXLOG_WARN("Refii mod Lua: the Refii table could not be made: {}", error);
    return;
  }
  const uint32_t holder = RefiiNamespaceHolder(frame, base);
  const int32_t top = lua.Top();
  lua.GetField(kGlobalsIndex, "Refii");
  if (lua.Type(-1) != kTypeTable) {
    lua.SetTop(top);
    return;
  }
  for (const Native& native : g_natives) {
    lua.PushClosure(native.thunk);
    if (holder != 0u) {
      PPCContext w = frame;
      w.r3.u32 = holder;
      w.r4.u32 = state;
      w.r5.u32 = lua.Stage(native.name);
      rex_lhLuaNamespace_RegisterFunctionHash(w, base);
    }
    lua.SetField(-2, native.name);
  }
  lua.SetTop(top);
  REXLOG_INFO("Refii mod Lua: {} Refii.* natives registered{}", sizeof(g_natives) / sizeof(g_natives[0]),
              holder != 0u ? " as permanents" : "");
}

/// The host's composed chunk at `key`, run in `state`. False when there is none or it failed.
bool RunComposed(PPCContext& ctx, uint8_t* base, uint32_t state, const char* key) {
  std::vector<uint8_t> text;
  if (!refii::vfs::ReadHostAsset(key, text) || text.empty()) {
    return false;
  }
  PPCContext frame = Frame(ctx, kNativeFrame);
  GuestLua lua(frame, base, state);
  std::string error;
  const std::string name = std::string("=") + key;
  const bool ran = lua.Run(std::string_view(reinterpret_cast<const char*>(text.data()), text.size()),
                           name.c_str(), &error);
  if (ran) {
    REXLOG_INFO("Refii mod Lua: {} ({} bytes) ran", key, text.size());
  } else {
    REXLOG_WARN("Refii mod Lua: {} ({} bytes) FAILED: {}", key, text.size(), error);
    const std::string report = std::string(key) + ": " + error;
    refii_bridge_mod_error_post(report.data(), report.size());
  }
  return ran;
}

// ---------------------------------------------------------------------------------------------
// Entities coming and going, raised from the next tick: the seams run inside the world's own update
// (and a destroy inside a script's native), where no Lua is run.

struct QueuedEvent {
  int32_t kind;
  std::string fields;
};

std::mutex g_queue_mutex;
std::vector<QueuedEvent> g_queue;
constexpr size_t kMaxQueued = 4096u;
constexpr int kRaisedPerTick = 64;

bool HostOrLuaWants(int32_t kind);

void QueueEdge(int32_t kind, uint32_t entity) {
  if (!HostOrLuaWants(kind)) {
    return;
  }
  char fields[48];
  std::snprintf(fields, sizeof(fields), "entity=%u", refii_bridge_mod_handle(entity));
  refii::fable3::modlua::QueueEvent(kind, fields);
}

// ---------------------------------------------------------------------------------------------
// A mod's notice in the game's own TopBox, as the game posts its own wide-text messages
// (sub_824D0FE8, the remote player's join notice): a message (sub_824CF630: +0 id, +4 title key 0,
// +8 main text, +16 lifetime ms) whose main text is a CHUDTopBoxMainWideText — 12 bytes, +0 its
// vtable (slot 0 `li r3,4`: kind 4), +8 an LHString — which the post takes without the book check
// (`if (vf0(main) != 4)` book.Contains, 0x824d0d00). Posted on the queue ShowTopBoxMessage posts to,
// `*(*(GUI+0x20)+0x4C)` channel 8 (0x82550f68), local only (TU3 target 3; disc broadcast 0, player
// 0), through the hooked post, which states it to Godot as the game's own. TU3 refuses a message
// whose main +4 equals a queued one's (0x824D0F08..0x824D0F38), so each notice carries its own.

const refii::guest::Addr kGui{0x83596F84u};
const refii::guest::Addr kWideTextVtable{0x820812E4u};
constexpr uint32_t kTopBoxChannel = 8u;
constexpr uint32_t kTopBoxLocalTarget = 3u;
std::atomic<uint32_t> g_notice_serial{0u};

/// UTF-8 to UTF-16 code units (invalid bytes become U+FFFD; astral code points as pairs).
std::u16string Utf16(std::string_view text) {
  std::u16string out;
  for (size_t i = 0; i < text.size();) {
    const auto c = static_cast<uint8_t>(text[i]);
    uint32_t cp = 0xFFFDu;
    size_t n = 1;
    if (c < 0x80u) {
      cp = c;
    } else if ((c >> 5) == 6u && i + 1 < text.size()) {
      cp = ((c & 0x1Fu) << 6) | (static_cast<uint8_t>(text[i + 1]) & 0x3Fu);
      n = 2;
    } else if ((c >> 4) == 14u && i + 2 < text.size()) {
      cp = ((c & 0x0Fu) << 12) | ((static_cast<uint8_t>(text[i + 1]) & 0x3Fu) << 6) |
           (static_cast<uint8_t>(text[i + 2]) & 0x3Fu);
      n = 3;
    } else if ((c >> 3) == 30u && i + 3 < text.size()) {
      cp = ((c & 0x07u) << 18) | ((static_cast<uint8_t>(text[i + 1]) & 0x3Fu) << 12) |
           ((static_cast<uint8_t>(text[i + 2]) & 0x3Fu) << 6) | (static_cast<uint8_t>(text[i + 3]) & 0x3Fu);
      n = 4;
    }
    if (cp >= 0x10000u) {
      cp -= 0x10000u;
      out.push_back(static_cast<char16_t>(0xD800u + (cp >> 10)));
      out.push_back(static_cast<char16_t>(0xDC00u + (cp & 0x3FFu)));
    } else {
      out.push_back(static_cast<char16_t>(cp));
    }
    i += n;
  }
  return out;
}

/// Posts `text` in the game's TopBox for `milliseconds`. False when the guest refused it.
bool PostTopBoxNotice(PPCContext& ctx, uint8_t* base, std::string_view text, float milliseconds) {
  const uint32_t gui = Word(base, kGui);
  const uint32_t queue = Word(base, Word(base, gui + 0x20u) + 0x4Cu);
  if (!Plausible(queue) || text.empty()) {
    return false;
  }
  PPCContext frame = Frame(ctx, kNativeFrame);
  // The frame: +0x40 the message (48), +0x70 the empty tag CCharString, +0x74 the LHString.
  const uint32_t message = frame.r1.u32 + 0x40u;
  const uint32_t tag = frame.r1.u32 + 0x70u;
  const uint32_t string = frame.r1.u32 + 0x74u;

  const std::u16string wide = Utf16(text);
  const uint32_t chars = GuestNew(frame, base, static_cast<uint32_t>(wide.size() + 1u) * 2u);
  const uint32_t empty = GuestNew(frame, base, 1u);
  const uint32_t main = GuestNew(frame, base, 12u);
  if (!Plausible(chars) || !Plausible(empty) || !Plausible(main)) {
    GuestDelete(frame, base, chars);
    GuestDelete(frame, base, empty);
    GuestDelete(frame, base, main);
    return false;
  }
  for (size_t i = 0; i < wide.size(); ++i) {
    const uint16_t be = __builtin_bswap16(static_cast<uint16_t>(wide[i]));
    std::memcpy(base + chars + 2u * i, &be, 2u);
  }
  base[chars + 2u * wide.size()] = 0;
  base[chars + 2u * wide.size() + 1u] = 0;
  base[empty] = 0;

  PPCContext w = frame;
  w.r3.u32 = tag;
  w.r4.u32 = empty;
  w.r5.s64 = -1;
  rex_lhCharString_Ctor(w, base);

  w = frame;
  w.r3.u32 = message;
  w.r4.u32 = 0u;
  w.r5.u32 = tag;
  w.f1.f64 = milliseconds;
  w.r6.u32 = 0u;
  w.r7.u32 = 0u;
  rex_TopBoxMsg_Ctor(w, base);

  w = frame;
  w.r3.u32 = string;
  w.r4.u32 = chars;
  rex_LHString_FromWide(w, base);

  StoreU32(base, main, kWideTextVtable);
  StoreU32(base, main + 4u, 0x52460000u | (g_notice_serial.fetch_add(1u, std::memory_order_relaxed) & 0xFFFFu));
  w = frame;
  w.r3.u32 = main + 8u;
  w.r4.u32 = string;
  rex_LHString_Copy(w, base);
  GuestDelete(frame, base, LoadU32(base, message + 8u));
  StoreU32(base, message + 8u, main);

  w = frame;
  w.r3.u32 = queue;
  w.r4.u32 = kTopBoxChannel;
  w.r5.u32 = message;
  if (refii::guest::Current() == refii::guest::Build::kDisc) {
    w.r6.u32 = 0u;  // broadcast
    w.r7.u32 = 0u;  // player
  } else {
    w.r6.u32 = kTopBoxLocalTarget;
  }
  rex_TopBox_Post(w, base);
  const bool posted = (w.r3.u32 & 0xFFu) != 0u;

  // The queue keeps its own copy; ours goes: the wide text's string, the message (frees +8, +12).
  w = frame;
  w.r3.u32 = main + 8u;
  rex_LHString_Dtor(w, base);
  w = frame;
  w.r3.u32 = string;
  rex_LHString_Dtor(w, base);
  w = frame;
  w.r3.u32 = message;
  rex_TopBoxMsg_Dtor(w, base);
  w = frame;
  w.r3.u32 = tag;
  rex_lhCharString_Dtor(w, base);
  GuestDelete(frame, base, chars);
  GuestDelete(frame, base, empty);
  return posted;
}

std::string FromBase64(std::string_view text) {
  static const std::string kAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
  std::string out;
  uint32_t bits = 0;
  int count = 0;
  for (char c : text) {
    const size_t v = kAlphabet.find(c);
    if (v == std::string::npos) {
      continue;
    }
    bits = (bits << 6) | static_cast<uint32_t>(v);
    count += 6;
    if (count >= 8) {
      count -= 8;
      out.push_back(static_cast<char>((bits >> count) & 0xFFu));
    }
  }
  return out;
}

// ---------------------------------------------------------------------------------------------
// The bus's Lua side and the host lanes.

uint32_t g_lua_masks[kLuaMaskGroups] = {};
uint32_t g_mask_generation = 0xFFFFFFFFu;
float g_dirty_poll = 0.0f;
uint32_t g_hero_seen[2] = {0u, 0u};
int32_t g_lua_depth = 0;

bool LuaWants(int32_t kind) {
  if (kind < 0 || kind >= kLuaMaskBits * kLuaMaskGroups) {
    return false;
  }
  return ((g_lua_masks[kind / kLuaMaskBits] >> (kind % kLuaMaskBits)) & 1u) != 0u;
}

/// Whether anyone hears `kind`: Lua's handlers, Godot's mask, a Godot verdict or a native handler.
bool HostOrLuaWants(int32_t kind) {
  return LuaWants(kind) || refii::mods::HostWantsEvent(kind) || refii_mod_bus_wants(kind) != 0 ||
         (kind >= 0 && kind < refii::mods::kHostMaskCapacity &&
          ((refii_bridge_mod_verdict_mask_word(kind / 64) >> (kind % 64)) & 1u) != 0u);
}

/// Refii._masks[1..groups] read straight off the table, and Refii._dirty cleared.
void ReadLuaMasks(GuestLua& lua) {
  const int32_t top = lua.Top();
  for (uint32_t& mask : g_lua_masks) {
    mask = 0u;
  }
  lua.GetField(kGlobalsIndex, "Refii");
  if (lua.Type(-1) == kTypeTable) {
    lua.GetField(-1, "_masks");
    if (lua.Type(-1) == kTypeTable) {
      for (int32_t g = 0; g < kLuaMaskGroups; ++g) {
        lua.RawGetI(-1, g + 1);
        if (lua.Type(-1) == kTypeNumber) {
          const double value = lua.Number(-1);
          if (value >= 0.0 && value < static_cast<double>(1u << kLuaMaskBits)) {
            g_lua_masks[g] = static_cast<uint32_t>(value);
          }
        }
        lua.SetTop(-2);
      }
    }
    lua.SetTop(-2);
    lua.PushBoolean(false);
    lua.SetField(-2, "_dirty");
  }
  lua.SetTop(top);
}

bool LuaDirty(GuestLua& lua) {
  const int32_t top = lua.Top();
  bool dirty = false;
  lua.GetField(kGlobalsIndex, "Refii");
  if (lua.Type(-1) == kTypeTable) {
    lua.GetField(-1, "_dirty");
    dirty = lua.Boolean(-1);
  }
  lua.SetTop(top);
  return dirty;
}

/// A text as a single-quoted Lua literal's body: quotes, backslashes and line breaks become
/// underscores, as Fable 2's LuaString.
std::string LuaQuoted(std::string_view text) {
  std::string out;
  out.reserve(text.size());
  for (char c : text) {
    out.push_back((c == '\'' || c == '\\' || c == '\n' || c == '\r') ? '_' : c);
  }
  return out;
}

/// The kinds Fable 3 raises, by the catalog's Lua names (ModApi.Fable3Raised), for the native bus.
constexpr std::pair<const char*, int32_t> kRaisedNames[] = {
    {"tick", 0}, {"hero_ready", 1}, {"hero_gone", 2}, {"lua_ready", 3}, {"save_loaded", 4}, {"game_action", 5},
    {"ability_request", 6}, {"melee_swing", 7}, {"roll_can_start", 8}, {"roll_started", 9}, {"block", 10},
    {"anim_event", 11}, {"hit", 12}, {"hit_landed", 13}, {"hit_response", 14}, {"spell_charge", 15},
    {"spell_charge_level", 16}, {"spell_launch", 17}, {"spell_launched", 18}, {"spell_created", 19},
    {"experience", 20}, {"entity_died", 21}, {"hero_downed", 22}, {"damage", 23}, {"level_loaded", 24},
    {"level_unloading", 25}, {"game_saving", 26}, {"item_added", 27}, {"item_used", 28}, {"held_item", 29},
    {"weapon_equipped", 30}, {"ability_level", 31}, {"gold", 32}, {"relation", 33}, {"input_action", 34},
    {"quest_started", 35}, {"quest_ended", 36}, {"quest_objective", 37}, {"dialogue", 38}, {"entity_spawned", 39},
    {"entity_removed", 40}, {"expression_started", 41}, {"renown", 42}, {"morality", 43}, {"purity", 44},
    {"marriage", 45}, {"dog_trick", 46}, {"ranged_shot", 47}, {"status_effect", 48}, {"target_changed", 49},
    {"opinion_deed", 50}, {"opinion_modified", 51}, {"time_of_day", 52}, {"weather_theme", 53}, {"sleep_menu", 54},
    {"travel", 55}, {"fast_travel", 56}, {"menu_closed", 58}, {"menu_screen", 59}, {"weather_region", 60},
    {"slow_time", 62}, {"summoned", 63}, {"crime", 64}, {"achievement", 65}, {"collectable_taken", 66},
    {"property_bought", 68}, {"property_sold", 69}, {"rent_paid", 70}, {"property_rented", 71},
    {"property_condition", 72}, {"divorce", 73}, {"widowed", 74}, {"child_born", 75}, {"gift_given", 76},
    {"gift_accepted", 77}, {"engaged", 78}, {"follow", 79}, {"furniture_upgraded", 80}, {"redecorating", 81},
    {"household_budget", 82}, {"tracked_stat", 83}, {"mod_menu_row", 87}, {"mod_message_box", 88},
    {"mod_notice", 89}, {"mod_menu_box", 90}, {"spell_pick", 92}, {"look_at_button", 94}, {"vortex_started", 95},
    {"vortex_ended", 96}, {"fireball_impact", 97}, {"paused", 98}, {"start_game", 99}, {"cutscene", 100},
    {"health_changed", 101}, {"status_applying", 102}, {"save_written", 103}, {"lock_lost", 104},
    {"attack_mode_changed", 105}, {"item_removed", 106}, {"spell_ended", 108}, {"spell_charge_ended", 109},
    {"melee_swing_ended", 110}, {"action_move_interrupt", 111}, {"camera_changed", 112}, {"vortex_caught", 113},
    {"guild_seals", 116}, {"treasury", 117}, {"kingdom_decision", 118},
    {"chest_opened", 119}, {"item_bought", 120}, {"item_sold", 121}, {"shop_price", 122}, {"dig_spot_dug", 123},
    {"demon_door", 124}, {"drink", 125},
    {"partner_consent", 67}, {"partner_message", 93}, {"mod_ability", 91},
    {"sleep_chosen", 61}, {"menu_opened", 57}, {"dynamic_weather", 86},
    {"sanctuary", 126}, {"sanctuary_request", 127}, {"weapon_level_up", 128}, {"weapon_augment_unlocked", 129}, {"weapon_upgrade_ready", 130},
    {"hand_holding", 131}, {"hand_holding_request", 132}, {"interaction_started", 133}, {"interaction_result", 134}, {"interaction_mode", 135},
    {"relationship_stage", 136}, {"dog_dig", 137}, {"dog_leading", 138}, {"dog_trick_learned", 139}, {"dog_learned", 140},
    {"dive_spot_used", 141}, {"vomit", 142}, {"job_started", 143}, {"job_level", 144}, {"job_earnings", 145},
    {"job_worked", 146}, {"job_multiplier", 147}, {"guild_message", 149}, {"title_changed", 150}, {"title_unlocked", 151},
    {"chapter_progress", 152}, {"scar_added", 153}, {"scar_removed", 154}, {"age_changed", 155}, {"hero_stage", 156},
    {"clothing_worn", 157}, {"clothing_removed", 158}, {"clothing_dyed", 159}, {"village_wealth", 162},
    {"partnership", 84}, {"morph_changed", 115},
    {"camera_cut", 164}, {"camera_blend", 165}, {"camera_screen_changed", 166}, {"camera_zoom_changed", 167},
    {"camera_tick", 168},
};

void NameRaisedKinds() {
  static const bool named = [] {
    for (const auto& name : kRaisedNames) {
      refii_mod_bus_name(name.first, name.second);
    }
    return true;
  }();
  (void)named;
}

/// A verdict's answer (`cancel=1,field=number,...`, a Godot verdict's or the native bus's) into `verdict`.
void ApplyAnswer(refii::fable3::modlua::Verdict& verdict, char* answer) {
  for (char* cursor = answer; cursor != nullptr && *cursor != 0;) {
    char* comma = std::strchr(cursor, ',');
    if (comma != nullptr) {
      *comma = 0;
    }
    if (char* equals = std::strchr(cursor, '='); equals != nullptr) {
      *equals = 0;
      const double value = std::strtod(equals + 1, nullptr);
      if (std::strcmp(cursor, "cancel") == 0) {
        verdict.cancelled = verdict.cancelled || value != 0.0;
      } else {
        verdict.Write(cursor, value);
      }
    }
    cursor = comma != nullptr ? comma + 1 : nullptr;
  }
}

/// Raises `kind` with the table text `fields` (Lua's and the host's, as Fable 2's HostFields):
/// to Lua's handlers when its mask wants it, then to Godot when its mask does. True when a Lua
/// handler cancelled.
bool Raise(GuestLua& lua, int32_t kind, const std::string& fields) {
  bool cancelled = false;
  if (refii_mod_bus_wants(kind) != 0) {
    char answer[512];
    refii::fable3::modlua::ContextScope scope(lua.ctx);
    refii_mod_bus_run(kind, fields.data(), fields.size(), answer, sizeof(answer));
  }
  if (LuaWants(kind) && g_lua_depth < 2) {
    char head[160];
    std::snprintf(head, sizeof(head), "if Refii and Refii._fire and not Refii._fire(%d,{", kind);
    const std::string chunk =
        std::string(head) + fields + "}) and Refii._cancelled then Refii._cancelled() end";
    const bool outer = g_lua_cancelled;
    g_lua_cancelled = false;
    ++g_lua_depth;
    std::string error;
    if (!lua.Run(chunk, "=refii/event", &error)) {
      REXLOG_WARN("Refii mod Lua: event {} did not run: {}", kind, error);
    }
    --g_lua_depth;
    cancelled = g_lua_cancelled;
    g_lua_cancelled = outer;
  }
  if (refii::mods::HostWantsEvent(kind)) {
    refii::mods::PostEvent(kind, fields);
  }
  return cancelled;
}

/// THE CAMERA EVENTS from the statements and the shell's views (refii_mod_events.h 164..168), raised at
/// the step, outside every camera update — Fable 2's DispatchCameraEvents.
int32_t g_camera_screen_raised = -1;
bool g_camera_split_raised = false;

void RaiseCameraEvents(GuestLua& lua) {
  namespace cam = refii::camera;
  std::array<uint32_t, 32u * cam::kEventWords> words{};
  const uint32_t taken = refii_bridge_camera_events_take(words.data(), 32u);
  for (uint32_t i = 0; i < taken; ++i) {
    const uint32_t* w = &words[i * cam::kEventWords];
    float b[11];
    std::memcpy(b, &w[3], sizeof(b));
    char fields[400];
    if (w[0] == cam::kEventCut) {
      std::snprintf(fields, sizeof(fields), "player=%u", w[1]);
      Raise(lua, kEventCameraCut, fields);
    } else if (w[0] == cam::kEventBlend) {
      std::snprintf(fields, sizeof(fields), "player=%u,ticks=%u", w[1], w[2]);
      Raise(lua, kEventCameraBlend, fields);
    } else if (w[0] == cam::kEventTick) {
      std::snprintf(fields, sizeof(fields),
                    "player=%u,x=%.9g,y=%.9g,z=%.9g,fx=%.9g,fy=%.9g,fz=%.9g,ux=%.9g,uy=%.9g,uz=%.9g,fov=%.9g,cut=%s", w[1],
                    b[0], b[1], b[2], b[3], b[4], b[5], b[6], b[7], b[8], b[9] * 57.29577951308232, b[10] != 0.0f ? "true" : "false");
      Raise(lua, kEventCameraTick, fields);
    }
  }

  int32_t split = 0;
  const int32_t mode = refii_bridge_camera_screen_read(&split);
  if (mode >= 0 && (mode != g_camera_screen_raised || (split != 0) != g_camera_split_raised)) {
    char fields[96];
    std::snprintf(fields, sizeof(fields), "mode=%d,split=%s,previous=%d,reason=%d", mode, split != 0 ? "true" : "false",
                  g_camera_screen_raised, mode == g_camera_screen_raised && mode == cam::kScreenHybrid ? 1 : 0);
    g_camera_screen_raised = mode;
    g_camera_split_raised = split != 0;
    Raise(lua, kEventCameraScreenChanged, fields);
  }
}

void Reply(const std::string& id, bool ok, const std::string& fields) {
  std::string text = "id=" + id + ",ok=" + (ok ? "true" : "false");
  if (!fields.empty()) {
    text += "," + fields;
  }
  refii::mods::PostEvent(kHostReplyKind, text);
}

/// The route's mod lane (core/event_relay.h `pm:` lines, the peer's refii_mod_send_to_partner), each
/// raised as partner_message (93) once, in the order sent — Fable 2's DrainPartnerMessages. `from` is
/// the player the sending machine plays (1 host, 2 joiner: THE FRAME). The reserved mod "refii"
/// carries B4's consent lines and is never raised. A line that does not read is dropped.
void DrainPartnerMessages(GuestLua& lua) {
  char text[768];
  for (int n = 0; n < 32 && refii_bridge_route_take_mod(text, sizeof(text)) != 0; ++n) {
    text[sizeof(text) - 1] = '\0';
    if (std::strncmp(text, "pm:", 3) != 0) {
      continue;
    }
    // pm:<from>:<mod>:<name>:<base64>; base64 carries no ':'.
    std::string_view rest(text + 3);
    std::string_view part[4];
    bool ok = true;
    for (int f = 0; f < 3 && ok; ++f) {
      const size_t colon = rest.find(':');
      ok = colon != std::string_view::npos;
      if (ok) {
        part[f] = rest.substr(0, colon);
        rest.remove_prefix(colon + 1u);
      }
    }
    part[3] = rest;
    const int32_t from = ok && part[0].size() == 1u ? part[0][0] - '0' : 0;
    if (!ok || (from != 1 && from != 2) || part[1].empty() || part[2].empty()) {
      continue;
    }
    const std::string payload = FromBase64(part[3]);
    if (payload.size() > REFII_MOD_PARTNER_PAYLOAD_MAX) {
      continue;
    }
    if (part[1] == "refii") {
      // B4: "ask" ticket\nasker\ntext  |  "answer" ticket\n0|1
      const size_t a = payload.find('\n');
      const uint32_t ticket = static_cast<uint32_t>(std::strtoul(payload.c_str(), nullptr, 10));
      if (part[2] == "ask" && a != std::string::npos) {
        const size_t b = payload.find('\n', a + 1u);
        const int32_t asker = static_cast<int32_t>(std::strtol(payload.c_str() + a + 1u, nullptr, 10));
        const std::string question = b != std::string::npos ? payload.substr(b + 1u) : std::string();
        ConsentAsk ask;
        ask.peer_ticket = ticket;
        ask.box = refii_mod_message_box(asker == 1 ? 2 : 1, "Your partner asks", question.c_str(), "Yes", "No");
        if (ask.box != 0u) {
          g_consent.push_back(ask);
        } else {
          SendConsentLine("answer", std::to_string(ticket) + "\n0");
        }
      } else if (part[2] == "answer" && a != std::string::npos) {
        GiveConsent(ticket, payload[a + 1u] == '1');
      }
      continue;
    }
    char fields[768];
    std::snprintf(fields, sizeof(fields), "mod='%s',name='%s',payload='%s',from=%d",
                  LuaQuoted(part[1]).c_str(), LuaQuoted(part[2]).c_str(), LuaQuoted(payload).c_str(), from);
    Raise(lua, kEventPartnerMessage, fields);
  }
}

/// One line of the host's mod lane (HostBridge.PostModCommand). The verbs Fable 3 serves; any
/// other is named once and dropped, and a query is answered unsupported so its caller never waits.
/// The host's answers and statements Godot sends as commands (Fable 2's mod_commands.cpp words): a
/// box's, a notice's, a menu box's and a menu row's answer, the keyboard's attack mode, and a Godot
/// script's player-store writes. True when `w[0]` was one of them.
bool RunHostAnswer(GuestLua& lua, const std::vector<std::string>& w) {
  const auto I = [&](size_t i) { return i < w.size() ? static_cast<int32_t>(std::strtol(w[i].c_str(), nullptr, 10)) : 0; };
  const auto U = [&](size_t i) { return i < w.size() ? static_cast<uint32_t>(std::strtoul(w[i].c_str(), nullptr, 10)) : 0u; };
  const auto F = [&](size_t i) { return i < w.size() ? std::strtof(w[i].c_str(), nullptr) : 0.0f; };
  const auto Q = [&](size_t i) { return i < w.size() && w[i] != "-" ? LuaQuoted(w[i]) : std::string(); };
  const std::string& op = w[0];
  char fields[768];
  if (op == "box_answer" && w.size() >= 4) {
    if (TakeConsentBox(U(1), I(2))) {
      return true;
    }
    std::snprintf(fields, sizeof(fields), "id=%u,button=%d,player=%d", U(1), I(2), I(3));
    Raise(lua, kEventModMessageBox, fields);
  } else if (op == "notice_done" && w.size() >= 4) {
    std::snprintf(fields, sizeof(fields), "id=%u,outcome=%d,player=%d", U(1), I(2), I(3));
    Raise(lua, kEventModNotice, fields);
  } else if (op == "menu_box_answer" && w.size() >= 4) {
    std::snprintf(fields, sizeof(fields), "id=%u,option=%d,player=%d", U(1), I(2), I(3));
    Raise(lua, kEventModMenuBox, fields);
  } else if (op == "menu_row" && w.size() >= 4) {
    // menu_row P MENU ROW MOD [BASE64 COMMAND|-] [BUTTON] [SUBJECT KIND] [DEFINITION] [SPELL]
    const std::string command = w.size() >= 6 && w[5] != "-" ? LuaQuoted(FromBase64(w[5])) : std::string();
    std::snprintf(fields, sizeof(fields),
                  "menu='%s',row='%s',player=%d,mod='%s',command='%s',button='%s',subject_kind='%s',definition='%s',spell=%d",
                  Q(2).c_str(), Q(3).c_str(), I(1), Q(4).c_str(), command.c_str(), Q(6).c_str(), Q(7).c_str(), Q(8).c_str(),
                  w.size() > 9 ? I(9) : -1);
    Raise(lua, kEventModMenuRow, fields);
  } else if (op == "attack_mode" && w.size() >= 3) {
    const int32_t player = I(1);
    const int32_t mode = I(2);
    if (player >= 1 && player <= 2 && mode >= 0 && mode <= 3 && g_attack_mode[player - 1] != mode) {
      g_attack_mode[player - 1] = mode;
      std::snprintf(fields, sizeof(fields), "player=%d,mode=%d", player, mode);
      Raise(lua, kEventAttackModeChanged, fields);
    }
  } else if ((op == "player_set" || op == "player_add" || op == "player_save") && w.size() >= 4) {
    refii_bridge_player_write(I(1), w[2].data(), w[2].size(), F(3), op == "player_add" ? 1 : 0,
                              op == "player_save" || (op == "player_add" && I(4) != 0) ? 1 : 0);
  } else if ((op == "player_set_text" || op == "player_save_text") && w.size() >= 4) {
    const std::string text = w[3] == "-" ? std::string() : FromBase64(w[3]);
    refii_bridge_player_write_text(I(1), w[2].data(), w[2].size(), text.data(), text.size(),
                                   op == "player_save_text" ? 1 : 0);
  } else if (op == "dynamic_weather" && w.size() >= 6) {
    // dynamic_weather FROM_HOST SECONDS LEVEL WEATHER PREVIOUS ('-' empty): the dynamic weather in effect
    // changed, as Godot decided it (Engine_DynamicWeatherChoice), the counterpart of Fable 2's
    // "ModWeather:" pump action (Fable2/src/vfs/src/hooks/mod_events.cpp RaiseDynamicWeather).
    std::snprintf(fields, sizeof(fields), "level='%s',weather='%s',previous='%s',seconds=%.9g,from_host=%s", Q(3).c_str(),
                  Q(4).c_str(), Q(5).c_str(), static_cast<double>(F(2)), I(1) != 0 ? "true" : "false");
    Raise(lua, kEventDynamicWeather, fields);
  } else if (op == "partner_send" && w.size() >= 3) {
    // partner_send MOD NAME [BASE64]: RefiiHost.send_to_partner (Fable 2's mod_commands.cpp verb).
    const std::string payload = w.size() >= 4 ? FromBase64(w[3]) : std::string();
    refii_mod_send_to_partner(w[1].c_str(), w[2].c_str(), payload.c_str());
  } else {
    return false;
  }
  return true;
}

/// Hands a Godot command or query line to the Lua dispatcher (Refii._command / Refii._query in
/// Fable3CoreLua): every word as a string, the dispatcher's table converts and calls the same Refii.*
/// a Lua mod calls. False when the dispatcher is not loaded yet.
bool RunDispatcher(GuestLua& lua, const char* function, const std::string& words) {
  std::string chunk = std::string("if not (Refii and Refii.") + function + ") then error('no dispatcher') end Refii." + function + "(";
  bool first = true;
  for (size_t at = 0; at < words.size();) {
    const size_t end = words.find(' ', at);
    const std::string word = words.substr(at, end == std::string::npos ? std::string::npos : end - at);
    at = end == std::string::npos ? words.size() : end + 1u;
    chunk += (first ? "'" : ",'") + LuaQuoted(word) + "'";
    first = false;
  }
  chunk += ")";
  std::string error;
  if (!lua.Run(chunk, "=refii/godot", &error)) {
    if (error.find("no dispatcher") != std::string::npos) {
      return false;
    }
    REXLOG_WARN("Refii mod Lua: Godot's '{}' failed: {}", words.substr(0, words.find(' ', words.find(' ') + 1u)), error);
    refii_bridge_mod_error_post(error.data(), error.size());
  }
  return true;
}

void RunCommand(GuestLua& lua, const std::string& line) {
  const size_t space = line.find(' ');
  const std::string op = line.substr(0, space);
  const std::string rest = space == std::string::npos ? std::string() : line.substr(space + 1u);
  {
    std::vector<std::string> words;
    for (size_t at = 0; at < line.size();) {
      const size_t end = line.find(' ', at);
      words.push_back(line.substr(at, end == std::string::npos ? std::string::npos : end - at));
      at = end == std::string::npos ? line.size() : end + 1u;
    }
    if (!words.empty() && RunHostAnswer(lua, words)) {
      return;
    }
  }
  if (op == "run_lua") {
    std::string error;
    if (!lua.Run(rest, "=refii/run_lua", &error)) {
      REXLOG_WARN("Refii mod Lua: run_lua failed: {}", error);
      refii_bridge_mod_error_post(error.data(), error.size());
    }
    return;
  }
  if (op == "topbox_notice") {
    // topbox_notice <milliseconds> <base64 UTF-8>: a mod's notice, from Godot's notice presenter.
    const size_t at = rest.find(' ');
    if (at != std::string::npos) {
      const float ms = std::strtof(rest.c_str(), nullptr);
      if (!PostTopBoxNotice(lua.ctx, lua.base, FromBase64(rest.substr(at + 1u)), ms > 0.0f ? ms : 5000.0f)) {
        REXLOG_WARN("Refii mod Lua: the TopBox refused a mod notice");
      }
    }
    return;
  }
  if (op == "publish") {
    const size_t at = rest.find(' ');
    if (at != std::string::npos) {
      refii::mods::Publish(rest.substr(0, at), std::strtof(rest.c_str() + at + 1u, nullptr));
    }
    return;
  }
  if (op == "mask_word") {
    char which[16] = {0};
    int32_t word = -1;
    unsigned long long bits = 0;
    if (std::sscanf(rest.c_str(), "%15s %d %llx", which, &word, &bits) == 3) {
      if (std::strcmp(which, "event") == 0) {
        refii_bridge_mod_set_host_event_mask_word(word, bits);
      } else if (std::strcmp(which, "verdict") == 0) {
        refii_bridge_mod_set_verdict_mask_word(word, bits);
      }
    }
    return;
  }
  if (op == "query") {
    const size_t at = rest.find(' ');
    const std::string id = rest.substr(0, at);
    const std::string verb = at == std::string::npos ? std::string() : rest.substr(at + 1u, rest.find(' ', at + 1u) - at - 1u);
    if (!RunDispatcher(lua, "_query", line.substr(line.find(' ') + 1u))) {
      Reply(id.empty() ? std::string("0") : id, false, "error='" + LuaQuoted(verb) + " is not available in Fable 3 yet'");
    }
    return;
  }
  if (!RunDispatcher(lua, "_command", line)) {
    static uint32_t named = 0u;
    if (named < 32u) {
      ++named;
      REXLOG_WARN("Refii mod Lua: command '{}' has no Fable 3 implementation; dropped", op);
    }
  }
}

/// The heroes, stated to Lua (Refii._hero) and raised as hero_ready / hero_gone on a change.
void PumpHeroes(GuestLua& lua) {
  for (int32_t p = 1; p <= 2; ++p) {
    const uint32_t hero = PlayerHero(lua.base, p);
    uint32_t& seen = g_hero_seen[p - 1];
    if (hero == seen) {
      continue;
    }
    char fields[64];
    if (seen != 0u) {
      std::snprintf(fields, sizeof(fields), "player=%d,hero=%u", p, refii_bridge_mod_handle(seen));
      Raise(lua, kEventHeroGone, fields);
    }
    seen = hero;
    if (hero != 0u) {
      // A new hero keeps only its saved values (the runtime store's rule, as Fable 2's tick).
      refii_bridge_player_new_hero(p);
    }
    char chunk[96];
    std::snprintf(chunk, sizeof(chunk), "if Refii and Refii._hero then Refii._hero[%d] = %u end", p,
                  hero != 0u ? refii_bridge_mod_handle(hero) : 0u);
    lua.Run(chunk, "=refii/hero", nullptr);
    if (hero != 0u) {
      std::snprintf(fields, sizeof(fields), "player=%d,hero=%u", p, refii_bridge_mod_handle(hero));
      Raise(lua, kEventHero, fields);
    }
  }
}

/// A new VM, or a save read into one: the natives, the chunks, the masks, the ready event.
void OnLuaReady(PPCContext& ctx, uint8_t* base, uint32_t state, bool save_loaded) {
  const uint32_t generation = g_generation.fetch_add(1u, std::memory_order_relaxed) + 1u;
  PPCContext frame = Frame(ctx, kNativeFrame);
  GuestLua lua(frame, base, state);
  g_hero_seen[0] = g_hero_seen[1] = 0u;
  ReadLuaMasks(lua);
  g_mask_generation = generation;
  char fields[32];
  std::snprintf(fields, sizeof(fields), "generation=%u", generation);
  if (save_loaded) {
    // The players' saved mod values, from the packages the guest states it loaded, before any mod hears
    // save_loaded (Fable 2 reads them in its LoadFromDevice / LoadCoopHero hooks).
    lua.Run("if Refii and Refii._player_load_saved then "
            "local m = GetCurrentMainSaveGameName and GetCurrentMainSaveGameName() "
            "if type(m) == 'string' and m ~= '' then Refii._player_load_saved(1, m) end "
            "local h = GetCurrentHenchmanSaveGameName and GetCurrentHenchmanSaveGameName() "
            "if type(h) == 'string' and h ~= '' then Refii._player_load_saved(2, h) end end",
            "=refii/load_saved", nullptr);
  }
  Raise(lua, save_loaded ? kEventSaveLoaded : kEventLua, fields);
}

}  // namespace

namespace refii::fable3::modlua {

bool Wants(int32_t kind) { return HostOrLuaWants(kind); }

namespace {
/// The context the native C API's calls without one borrow (ContextScope): the frame of the tick or
/// event the native mod's callback runs inside, on the VM's thread.
thread_local PPCContext* t_context = nullptr;
}  // namespace

PPCContext* CurrentContext() { return t_context; }

ContextScope::ContextScope(PPCContext& ctx) : previous_(t_context) { t_context = &ctx; }
ContextScope::~ContextScope() { t_context = previous_; }

/// Refii.<function>(args...) in the game VM, protected, every result kept: numbers as numbers, a
/// record ID (a light userdata) as its raw word. False without a VM, off its thread, when the
/// helper is missing, or when it raised (the error logged).
bool CallRefii(PPCContext& ctx, uint8_t* base, const char* function, const std::vector<LuaValue>& args,
               std::vector<LuaValue>& results) {
  results.clear();
  const uint32_t state = g_state.load(std::memory_order_relaxed);
  if (function == nullptr || state == 0u || !OnVmThread()) {
    return false;
  }
  PPCContext frame = Frame(ctx, kNativeFrame);
  GuestLua lua(frame, base, state);
  const std::string chunk = std::string("local f = Refii and Refii.") + function +
                            " if f == nil then error('Refii." + function + " is missing') end return f(...)";
  const int32_t top = lua.Top();
  const uint32_t size = static_cast<uint32_t>(chunk.size());
  const uint32_t buffer = GuestNew(frame, base, size + 1u);
  if (!Plausible(buffer)) {
    return false;
  }
  std::memcpy(base + buffer, chunk.data(), size);
  base[buffer + size] = 0;
  PPCContext w = lua.Call();
  w.r4.u32 = LoadU32(base, state) + kGlobalLoadSettings;
  w.r5.u32 = buffer;
  w.r6.u32 = size;
  w.r7.u32 = lua.Stage("=refii/native_api");
  rex_luaL_loadbuffer(w, base);
  uint32_t status = w.r3.u32;
  if (status == 0u) {
    lua.Room(static_cast<uint32_t>(args.size()) + 1u);
    for (const LuaValue& arg : args) {
      switch (arg.kind) {
        case LuaValue::Kind::Number: lua.PushNumber(arg.number); break;
        case LuaValue::Kind::Boolean: lua.PushBoolean(arg.boolean); break;
        case LuaValue::Kind::String: lua.PushString(arg.text); break;
        case LuaValue::Kind::Record: lua.PushRaw(arg.raw, kTagLightUserdata); break;
        default: lua.PushNil(); break;
      }
    }
    w = lua.Call();
    w.r4.u32 = static_cast<uint32_t>(args.size());
    w.r5.s64 = -1;  // LUA_MULTRET
    w.r6.u32 = 0u;
    rex_lua_pcall(w, base);
    status = w.r3.u32;
  }
  if (status != 0u) {
    const std::string error = lua.Top() > top ? lua.String(-1, 1024u) : std::string("(no message)");
    REXLOG_WARN("Refii mod Lua: native API call Refii.{} failed: {}", function, error);
    refii_bridge_mod_error_post(error.data(), error.size());
  } else {
    const uint32_t stack = LoadU32(base, state + kStateBase);
    for (int32_t index = top + 1; index <= lua.Top(); ++index) {
      LuaValue value;
      switch (lua.Type(index)) {
        case kTypeNumber: value = LuaValue::Num(lua.Number(index)); break;
        case kTypeString: value = LuaValue::Str(lua.String(index)); break;
        case kTypeBoolean: value = LuaValue::Bool(lua.Boolean(index)); break;
        case static_cast<int32_t>(kTagLightUserdata):
          value = LuaValue::Rec(LoadU32(base, stack + 8u * static_cast<uint32_t>(index - 1)));
          break;
        default: break;
      }
      results.push_back(std::move(value));
    }
  }
  lua.SetTop(top);
  GuestDelete(frame, base, buffer);
  return status == 0u;
}

bool OnlineClient() { return g_online_client.load(std::memory_order_relaxed); }

void Verdict::Write(const std::string& field, double value) {
  for (auto& write : writes) {
    if (write.first == field) {
      write.second = value;
      return;
    }
  }
  writes.emplace_back(field, value);
}

bool Verdict::Read(const char* field, double& value) const {
  for (const auto& write : writes) {
    if (write.first == field) {
      value = write.second;
      return true;
    }
  }
  return false;
}

Verdict Decide(PPCContext& ctx, uint8_t* base, int32_t kind, const std::string& fields) {
  Verdict verdict;
  const bool godot = kind >= 0 && kind < refii::mods::kHostMaskCapacity &&
                     ((refii_bridge_mod_verdict_mask_word(kind / 64) >> (kind % 64)) & 1u) != 0u &&
                     refii_bridge_mod_verdict_handler() != nullptr;
  const bool native = refii_mod_bus_wants(kind) != 0;
  if (!LuaWants(kind) && !godot && !native) {
    if (refii::mods::HostWantsEvent(kind)) {
      refii::mods::PostEvent(kind, fields);
    }
    return verdict;
  }
  const uint32_t state = g_state.load(std::memory_order_relaxed);
  if (state == 0u || !OnVmThread() || g_deciding != nullptr) {
    // Not on the VM's thread, or inside another decision: reported, never decided.
    QueueEvent(kind, fields);
    return verdict;
  }

  g_deciding = &verdict;
  g_deciding_kind = kind;
  // Native handlers first (core/refii_mod_bus.h), as Fable 2's typed subscribers run before Lua.
  if (native) {
    char answer[512];
    ContextScope scope(ctx);
    if (refii_mod_bus_run(kind, fields.data(), fields.size(), answer, sizeof(answer)) > 0) {
      ApplyAnswer(verdict, answer);
    }
  }
  if (LuaWants(kind) && !verdict.cancelled) {
    PPCContext frame = Frame(ctx, kNativeFrame);
    GuestLua lua(frame, base, state);
    char head[160];
    std::snprintf(head, sizeof(head), "if Refii and Refii._fire and not Refii._fire(%d,{", kind);
    const std::string chunk = std::string(head) + fields + "}) and Refii._cancelled then Refii._cancelled() end";
    const bool outer = g_lua_cancelled;
    g_lua_cancelled = false;
    std::string error;
    if (!lua.Run(chunk, "=refii/event", &error)) {
      REXLOG_WARN("Refii mod Lua: event {} did not run: {}", kind, error);
    }
    verdict.cancelled = g_lua_cancelled;
    g_lua_cancelled = outer;
  }

  // The fields as they stand after Lua's writes: what Godot's verdict and the host's event read.
  std::string stated = fields;
  for (const auto& write : verdict.writes) {
    const std::string key = write.first + "=";
    const size_t at = stated.find(key) == 0 ? 0 : stated.find("," + key);
    if (at == std::string::npos) {
      continue;
    }
    const size_t value_at = at + (at == 0 ? 0u : 1u) + key.size();
    const size_t end = stated.find(',', value_at);
    char number[32];
    std::snprintf(number, sizeof(number), "%.9g", write.second);
    stated.replace(value_at, (end == std::string::npos ? stated.size() : end) - value_at, number);
  }

  if (godot && !verdict.cancelled) {
    char answer[512];
    const int32_t length =
        refii_bridge_mod_verdict_handler()(kind, stated.c_str(), stated.size(), answer, sizeof(answer) - 1u);
    if (length > 0) {
      answer[length < static_cast<int32_t>(sizeof(answer)) ? length : static_cast<int32_t>(sizeof(answer)) - 1] = 0;
      ApplyAnswer(verdict, answer);
    }
  }
  g_deciding = nullptr;
  g_deciding_kind = -1;

  if (refii::mods::HostWantsEvent(kind)) {
    refii::mods::PostEvent(kind, verdict.cancelled ? stated + ",cancelled=true" : stated);
  }
  return verdict;
}

Verdict DecideAnywhere(PPCContext& ctx, uint8_t* base, int32_t kind, const std::string& fields) {
  const uint32_t state = g_state.load(std::memory_order_relaxed);
  if (state != 0u && OnVmThread()) {
    return Decide(ctx, base, kind, fields);
  }
  Verdict verdict;
  if (g_deciding != nullptr) {
    QueueEvent(kind, fields);
    return verdict;
  }
  const bool godot = kind >= 0 && kind < refii::mods::kHostMaskCapacity &&
                     ((refii_bridge_mod_verdict_mask_word(kind / 64) >> (kind % 64)) & 1u) != 0u &&
                     refii_bridge_mod_verdict_handler() != nullptr;
  if (refii_mod_bus_wants(kind) != 0) {
    char answer[512];
    ContextScope scope(ctx);
    if (refii_mod_bus_run(kind, fields.data(), fields.size(), answer, sizeof(answer)) > 0) {
      ApplyAnswer(verdict, answer);
    }
  }
  if (godot && !verdict.cancelled) {
    char answer[512];
    const int32_t length =
        refii_bridge_mod_verdict_handler()(kind, fields.c_str(), fields.size(), answer, sizeof(answer) - 1u);
    if (length > 0) {
      answer[length < static_cast<int32_t>(sizeof(answer)) ? length : static_cast<int32_t>(sizeof(answer)) - 1] = 0;
      ApplyAnswer(verdict, answer);
    }
  }
  if (refii::mods::HostWantsEvent(kind)) {
    refii::mods::PostEvent(kind, verdict.cancelled ? fields + ",cancelled=true" : fields);
  }
  return verdict;
}

void QueueEvent(int32_t kind, std::string fields) {
  std::lock_guard<std::mutex> lock(g_queue_mutex);
  if (g_queue.size() < kMaxQueued) {
    g_queue.push_back({kind, std::move(fields)});
  }
}

uint32_t Hero(const uint8_t* base, int32_t player) { return PlayerHero(base, player); }

int32_t PlayerOf(const uint8_t* base, uint32_t entity) {
  if (!Plausible(entity)) {
    return 0;
  }
  return entity == PlayerHero(base, 1) ? 1 : entity == PlayerHero(base, 2) ? 2 : 0;
}

std::string Quoted(std::string_view text) { return LuaQuoted(text); }

bool Run(PPCContext& ctx, uint8_t* base, std::string_view chunk, const char* name, std::string* error) {
  const uint32_t state = g_state.load(std::memory_order_relaxed);
  if (state == 0u) {
    if (error != nullptr) {
      *error = "no game VM";
    }
    return false;
  }
  if (!OnVmThread()) {
    if (error != nullptr) {
      *error = "not the game VM's thread";
    }
    return false;
  }
  PPCContext frame = Frame(ctx, kNativeFrame);
  GuestLua lua(frame, base, state);
  return lua.Run(chunk, name, error);
}

void NoteLendChargeEnd(int32_t player, bool released) {
  if (player != 1 && player != 2) {
    return;
  }
  ChargeLend& lend = g_lend[static_cast<size_t>(player - 1)];
  if (lend.active) {
    lend.ended = true;
    lend.ended_released = released;
  }
}

void NoteLendPick(const uint8_t* base, uint32_t manager, int32_t slot) {
  if (slot < 0 || slot > 4) {
    return;
  }
  for (ChargeLend& lend : g_lend) {
    if (lend.active && lend.manager == manager) {
      lend.saved[slot][0] = base[manager + kSlotTable + 2u * static_cast<uint32_t>(slot)];
      lend.saved[slot][1] = base[manager + kSlotTable + 2u * static_cast<uint32_t>(slot) + 1u];
    }
  }
}

void Tick(PPCContext& ctx, uint8_t* base) {
  // Native mods' tick callbacks (refii_mod_register_tick), every guest tick with or without a game VM,
  // with the real seconds since the last (a hitch capped at a quarter second), as on Fable 2.
  {
    using Clock = std::chrono::steady_clock;
    static Clock::time_point last = Clock::now();
    const Clock::time_point now = Clock::now();
    const float seconds = std::min(std::chrono::duration<float>(now - last).count(), 0.25f);
    last = now;
    ContextScope scope(ctx);
    refii_mod_run_ticks(&ctx, base, seconds);
  }
  const uint32_t state = g_state.load(std::memory_order_relaxed);
  if (state == 0u || !OnVmThread()) {
    // No VM to serve: input edges are stale by the time one exists.
    char name[160];
    while (refii_bridge_mod_input_poll(name, sizeof(name), nullptr, nullptr) != 0) {
    }
    return;
  }
  PPCContext frame = Frame(ctx, kNativeFrame);
  GuestLua lua(frame, base, state);

  const uint32_t generation = g_generation.load(std::memory_order_relaxed);
  if (generation != g_mask_generation) {
    g_mask_generation = generation;
    ReadLuaMasks(lua);
    g_dirty_poll = 0.0f;
  } else {
    g_dirty_poll += kStepSeconds;
    if (g_dirty_poll >= kLuaDirtyPollSeconds) {
      g_dirty_poll = 0.0f;
      if (LuaDirty(lua)) {
        ReadLuaMasks(lua);
      }
    }
  }

  StateCoop(frame, base);
  PumpHeroes(lua);
  StateModMorphs(frame, base);
  // The cutscene and lock edges and the waiting interrupt grants (mod_combat_hooks.cpp), queued
  // before the queue drains so a tick handler already sees them.
  PollCombat(frame, base);
  PumpChargeLend(frame, base);
  DrainPartnerMessages(lua);
  AgeConsent();
  // The mod abilities' stated prices (.cost / .refund / .afford beside each level), every 15th tick as
  // Fable 2's engine states them (Fable3CoreLua: Refii._ability_state).
  static int ability_poll = 0;
  if (++ability_poll >= 15) {
    ability_poll = 0;
    lua.Run("if Refii and Refii._ability_state and next(Refii._abilities or {}) then Refii._ability_state() end",
            "=refii/abilities", nullptr);
  }

  char name[160];
  int32_t player = 0;
  int32_t pressed = 0;
  while (refii_bridge_mod_input_poll(name, sizeof(name), &player, &pressed) != 0) {
    char fields[256];
    std::snprintf(fields, sizeof(fields), "name='%s',player=%d,pressed=%s", LuaQuoted(name).c_str(), player,
                  pressed != 0 ? "true" : "false");
    Raise(lua, kEventInputAction, fields);
  }

  std::vector<QueuedEvent> queued;
  {
    std::lock_guard<std::mutex> lock(g_queue_mutex);
    const size_t n = g_queue.size() < static_cast<size_t>(kRaisedPerTick) ? g_queue.size() : kRaisedPerTick;
    queued.assign(std::make_move_iterator(g_queue.begin()),
                  std::make_move_iterator(g_queue.begin() + static_cast<std::ptrdiff_t>(n)));
    g_queue.erase(g_queue.begin(), g_queue.begin() + static_cast<std::ptrdiff_t>(n));
  }
  for (const QueuedEvent& event : queued) {
    Raise(lua, event.kind, event.fields);
  }

  // Bounded per step, as Fable 2's, so a flooding script cannot stall the simulation.
  char line[4096];
  for (int n = 0; n < 64 && refii_bridge_mod_command_poll(line, sizeof(line)) != 0; ++n) {
    line[sizeof(line) - 1] = '\0';
    RunCommand(lua, line);
  }

  RaiseCameraEvents(lua);

  char fields[48];
  std::snprintf(fields, sizeof(fields), "seconds=%.9g", static_cast<double>(kStepSeconds));
  Raise(lua, kEventTick, fields);
}

}  // namespace refii::fable3::modlua

REX_EXTERN(rex_lhLuaScriptSystem_LoadStartupScripts) {
  const PPCContext entry = ctx;
  const uint32_t system = ctx.r3.u32;
  const bool ran_before = Plausible(system) && base[system + kScriptSystemRan] != 0u;
  __imp__rex_lhLuaScriptSystem_LoadStartupScripts(ctx, base);
  if (!Plausible(system) || ran_before || base[system + kScriptSystemRan] == 0u) {
    return;
  }

  const uint32_t state = LoadU32(base, system);
  if (!Plausible(state)) {
    return;
  }
  g_vm_thread.store(std::this_thread::get_id(), std::memory_order_relaxed);
  g_system.store(system, std::memory_order_relaxed);
  g_state.store(state, std::memory_order_relaxed);
  REXLOG_INFO("Refii mod Lua: the game VM 0x{:08X} ran its startup scripts", state);

  PPCContext work = entry;
  NameRaisedKinds();
  RegisterNatives(work, base, state);
  RunComposed(work, base, state, kCoreKey);
  RunComposed(work, base, state, kInitKey);
  RunComposed(work, base, state, kCameraKey);
  OnLuaReady(work, base, state, false);
}

REX_EXTERN(rex_lhLuaScriptSystem_LoadScriptSave) {
  const PPCContext entry = ctx;
  __imp__rex_lhLuaScriptSystem_LoadScriptSave(ctx, base);
  const uint32_t system = entry.r3.u32;
  const uint32_t state = Plausible(system) ? LoadU32(base, system) : 0u;
  if (state != 0u && state == g_state.load(std::memory_order_relaxed)) {
    PPCContext work = entry;
    OnLuaReady(work, base, state, true);
  }
}

REX_EXTERN(rex_lhLuaScriptSystem_RunCameraScripts) {
  const PPCContext entry = ctx;
  __imp__rex_lhLuaScriptSystem_RunCameraScripts(ctx, base);
  const uint32_t system = entry.r3.u32;
  const uint32_t state = Plausible(system) ? LoadU32(base, system) : 0u;
  if (state != 0u && state == g_state.load(std::memory_order_relaxed)) {
    PPCContext work = entry;
    // The camera scripts made a new CameraManager: the core chunk's wrapper goes on it again.
    PPCContext frame = Frame(work, kNativeFrame);
    GuestLua lua(frame, base, state);
    lua.Run("if Refii and Refii._wrap_camera then Refii._wrap_camera() end", "=refii/camera_wrap", nullptr);
    RunComposed(work, base, state, kCameraKey);
  }
}

/// The game's AI scripts (aisetupscript.lua's AddAIScriptFile list) and its post-scripts-loaded file run
/// HERE, at each main-game world init (new game, every save load), after the startup scripts and the
/// mods' init.lua: lhLuaScriptSystem_RunPostLoadScripts (TU3 0x8231CE70 / disc 0x82315FA0), r3 the
/// script system, r4 "a save follows":
///   TU3  8231ce84 mr r30,r3 / 8231ce88 mr r31,r4 / 8231ce94 bl sub_8231D120 (RunAIScripts: `lwz r3,0(r28)`
///        L = *ss) / 8231ce98 stb r31,0x1DE(r30)       disc 82315fb4 / 82315fb8 / 82315fc4 bl sub_82316250 /
///        82315fc8; its one caller, world init: TU3 8234c970 bl LoadStartupScripts ... 8234c9c8 bl here.
/// Every run rebuilds CombatInfo, CombatWheel, CombatStyles… from scratch, so mods hear lua_ready again
/// (same generation: the VM is the same) and patch the new tables.
REX_EXTERN(rex_lhLuaScriptSystem_RunPostLoadScripts) {
  const PPCContext entry = ctx;
  __imp__rex_lhLuaScriptSystem_RunPostLoadScripts(ctx, base);
  const uint32_t system = entry.r3.u32;
  const uint32_t state = Plausible(system) ? LoadU32(base, system) : 0u;
  if (state != 0u && state == g_state.load(std::memory_order_relaxed)) {
    PPCContext work = entry;
    PPCContext frame = Frame(work, kNativeFrame);
    GuestLua lua(frame, base, state);
    char fields[48];
    std::snprintf(fields, sizeof(fields), "generation=%u,scripts=true", g_generation.load(std::memory_order_relaxed));
    Raise(lua, kEventLua, fields);
  }
}

REX_EXTERN(rex_lua_close) {
  const uint32_t state = ctx.r3.u32;
  uint32_t expected = state;
  if (state != 0u && g_state.compare_exchange_strong(expected, 0u, std::memory_order_relaxed)) {
    REXLOG_INFO("Refii mod Lua: the game VM 0x{:08X} is closing", state);
  }
  __imp__rex_lua_close(ctx, base);
}

/// An entity linked into the manager: raised as entity_spawned from the next tick.
REX_EXTERN(rex_lhEntityManager_Add) {
  const uint32_t manager = ctx.r3.u32;
  const uint32_t entity = ctx.r4.u32;
  const uint32_t before = Plausible(manager) ? LoadU32(base, manager + 0xCu) : 0u;
  __imp__rex_lhEntityManager_Add(ctx, base);
  if (Plausible(manager) && Plausible(entity) && LoadU32(base, manager + 0xCu) != before) {
    QueueEdge(kEventEntitySpawned, entity);
  }
}

/// An entity's destroy: its handle is forgotten (a handle never outlives its entity) and
/// entity_removed raised from the next tick with the number scripts held.
REX_EXTERN(rex_lhEntity_Destroy) {
  const uint32_t entity = ctx.r3.u32;
  if (Plausible(entity)) {
    QueueEdge(kEventEntityRemoved, entity);
  }
  __imp__rex_lhEntity_Destroy(ctx, base);
  if (Plausible(entity)) {
    refii_bridge_mod_handle_forget(entity);
  }
}

// Native mods' game VM (core/refii_mod_requests.h): a chunk run protected in the game VM the mods' Lua
// runs in, on its thread, as Fable 2's refii_mod_run_game_lua; false without a VM or off its thread.
extern "C" int32_t refii_mod_run_game_lua(PPCContext* ctx, uint8_t* base, const char* chunk) {
  const uint32_t state = g_state.load(std::memory_order_relaxed);
  if (ctx == nullptr || chunk == nullptr || state == 0u || !OnVmThread()) {
    return 0;
  }
  PPCContext frame = Frame(*ctx, kNativeFrame);
  GuestLua lua(frame, base, state);
  return lua.Run(chunk, "=refii/native", nullptr) ? 1 : 0;
}

/// Changes every time the game VM is rebuilt (a new session, a load): the mods' runtime generation.
extern "C" uint32_t refii_mod_lua_generation(void) { return g_generation.load(std::memory_order_relaxed); }

namespace refii::fable3::binding {
void SavesListed(uint32_t user, uint32_t names);
}  // namespace refii::fable3::binding

// r3 the user, r5 the save-name vector it fills (begin +4, end +8). The drop-in join's list is also the
// joining player's choice (character_binding_hooks.cpp).
REX_EXTERN(rex_SaveGame_EnumerateNames) {
  const uint32_t user = ctx.r3.u32;
  const uint32_t names = ctx.r5.u32;
  __imp__rex_SaveGame_EnumerateNames(ctx, base);
  refii::fable3::binding::SavesListed(user, names);
  if (Plausible(names)) {
    const uint32_t begin = Word(base, names + 4u);
    const uint32_t end = Word(base, names + 8u);
    if (end >= begin && end - begin < 4u * 4096u) {
      g_save_count.store(static_cast<int32_t>((end - begin) >> 2), std::memory_order_relaxed);
    }
  }
}

REX_EXTERN(__imp__rex_CECMorph_GetWeight);

REX_EXTERN(rex_CECMorph_GetWeight) {
  const uint32_t type = ctx.r4.u32;
  float weight = 0.0f;
  if (type >= 0x100u && type < 0x80000000u) {
    const int32_t player = refii::fable3::modlua::PlayerOf(base, Word(base, ctx.r3.u32 + 4u));
    if ((player == 1 || player == 2) && refii_mod_morph_weight(player, type, &weight) != 0) {
      // The game's own adult gate, through its own probe: type 14 weighs 1.0 for an adult, 0 below.
      PPCContext probe = ctx;
      probe.r4.u64 = 14u;
      __imp__rex_CECMorph_GetWeight(probe, base);
      ctx.f1.f64 = probe.f1.f64 > 0.0 ? static_cast<double>(weight) : 0.0;
      return;
    }
  }
  __imp__rex_CECMorph_GetWeight(ctx, base);
}

/// Kore's load-time _R1 pass (TU3 sub_821F9E58 / disc sub_821FA6D0, run by lua_load over every proto)
/// rewrites a consumer to take its operand from r19, the value the previous instruction produced. It
/// leaves a CLOSURE's upvalue-binding words in the scan when the CLOSURE is a jump target (any
/// instruction after a JMP or a test), and each binding is a producer MOVE/GETUPVAL with A = 0, so the
/// next instruction on r0 reads a stale r19: `if x then C.f = function() ... C ... end` stores into the
/// wrong object (run-verified 2026-11-17, string.dump of the runtime compile: CLOSURE / MOVE 0 0 0 /
/// SETFIELD_R1 r0). Plain forms are always correct (each plain handler loads r19 itself and falls into
/// its _R1 entry: SETFIELD TU3 0x822A3C10 `ld r19,0(r30)` -> 0x822A3C14), so the loop is skipped:
///   TU3  821fa100 li r30,1 -> 821fa230 lwz r11,0(r21) (the targets' free; the loop's own n <= 1 exit,
///        821fa108 ble loc_821FA230)        disc 821fa978 -> 821faaa8 (821fa980 ble loc_821FAAA8).
/// Shipped bytecode already carries its _R1 forms and is byte-identical either way; only runtime
/// compiles (mods, loadstring) change, to plain forms.
void RefiiF3KoreSkipR1Pass() {}
