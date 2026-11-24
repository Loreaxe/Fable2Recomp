#include "vfs_hooks_private.h"

#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include <rex/logging.h>

#include "core/guest_build.h"
#include "core/guest_state.h"
#include "core/host_asset_reader.h"
#include "core/mod_values.h"
#include "core/refii_mod.h"
#include "core/rex_payload_bridge.h"
#include "hooks/frontend_stated.h"
#include "hooks/ppc_externs.h"

#include "../../../lua/src/tools/luadec/luac32_luaconf.h"

#pragma push_macro("_MSC_EXTENSIONS")
#undef _MSC_EXTENSIONS
namespace refii::frontend_lua {
#include "../../../lua/thirdparty/lua512/src/lua.h"
#include "../../../lua/thirdparty/lua512/src/lauxlib.h"
#include "../../../lua/thirdparty/lua512/src/lualib.h"
}  // namespace refii::frontend_lua
#pragma pop_macro("_MSC_EXTENSIONS")

// MODS' LUA ON THE TITLE AND THE FRONT END.
//
// There is no game VM there: the game's Lua is built per session by Game_InitDatabases
// (`stw r3, 0xAC(r27)` at 0x82310e30) and closed when it ends. The mods' front-end Lua is the
// host's own: Lua 5.1.2 configured as the guest's Gameface Lua (32-bit float numbers, "%.14g";
// mod_frontend_lua_vm.cpp), with the base, string, table and math libraries — no io, os,
// package, debug, dofile or loadfile, so a mod's chunk reaches nothing outside its natives.
//
// ONE STATE PER FRONT-END SCREEN: the title's or the main menu's, whichever the front end made
// active (frontend_stated.cpp FrontEndActiveScreen, from the screens' own activates); each
// activation starts fresh, as each had its own Gameface presentation state. (The GUI's
// presentation set, GUI+56, holds no current presentation on the front end: its index there is
// 31, past the 30 slots, and `lhGuiPresentationSet_GetCurrent` 0x823DCBE0 answers `a1[39]` = 0 or
// `a1[3]` = 0.) Chunks run on the guest thread from the pump sites (mod_commands.cpp, mod_events.cpp), so a native that needs the guest (save_count,
// screen) calls it with the pump's context. Before each chunk the state is asked whether it holds
// the mods' front-end runtime; one that does not runs the host's composed chunk first
// (refii/mod_frontend.lua, Engine_ModLua: the front-end preamble and every mod's
// mod/frontend.lua). A chunk runs protected: an error is logged and never leaves it.

PPC_EXTERN_FUNC(rex_lhGuiPresentationSet_GetCurrent);
PPC_EXTERN_FUNC(rex_lhSaveManager_CountSavedGamesAllDevices);

namespace {

namespace flua = refii::frontend_lua;

using refii::mods::LoadU32;

constexpr uint32_t kGuiObject = 0x83496B08u;
constexpr uint32_t kGameRoot = 0x83496AB8u;

constexpr const char* kChunkKey = "refii/mod_frontend.lua";
constexpr const char* kProbe = "__refii_need_init(Refii == nil or Refii._frontend ~= true)";

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

/// The pump's context while a chunk runs: what a native's guest call is made with.
struct Running {
  PPCContext* ctx = nullptr;
  uint8_t* base = nullptr;
};
Running g_running;

bool g_need_init = false;

/// A native's argument `i` as the Gameface natives read one: a string's characters, a number as
/// "%.14g", anything else empty.
void ArgString(flua::lua_State* L, int i, char* out, size_t capacity) {
  out[0] = 0;
  const int type = flua::lua_type(L, i);
  if (type != LUA_TSTRING && type != LUA_TNUMBER) {
    return;
  }
  size_t n = 0;
  const char* text = flua::lua_tolstring(L, i, &n);
  if (text == nullptr) {
    return;
  }
  if (n >= capacity) {
    n = capacity - 1;
  }
  std::memcpy(out, text, n);
  out[n] = 0;
}

/// A number, a boolean as 1 / 0, anything else 0.
double ArgNumber(flua::lua_State* L, int i) {
  switch (flua::lua_type(L, i)) {
    case LUA_TNUMBER:
      return flua::lua_tonumber(L, i);
    case LUA_TBOOLEAN:
      return flua::lua_toboolean(L, i) != 0 ? 1.0 : 0.0;
    default:
      return 0.0;
  }
}

int32_t ArgInt(flua::lua_State* L, int i) {
  const double v = ArgNumber(L, i);
  return std::isfinite(v) ? static_cast<int32_t>(v) : 0;
}

void PushIdOrNil(flua::lua_State* L, uint32_t id) {
  if (id != 0u) {
    flua::lua_pushnumber(L, static_cast<flua::lua_Number>(id));
  } else {
    flua::lua_pushnil(L);
  }
}

// __refii_need_init(b): the probe's answer.
int NativeNeedInit(flua::lua_State* L) {
  g_need_init = flua::lua_toboolean(L, 1) != 0;
  return 0;
}

// Refii.publish(key, n): the value every reader sees as "mod:<key>" (refii::mods::Publish).
int NativePublish(flua::lua_State* L) {
  char key[128];
  ArgString(L, 1, key, sizeof(key));
  if (key[0] != 0) {
    refii::mods::Publish(key, static_cast<float>(ArgNumber(L, 2)));
  }
  return 0;
}

// Refii.value(key): a published value, or nil.
int NativeValue(flua::lua_State* L) {
  char key[128];
  ArgString(L, 1, key, sizeof(key));
  float value = 0.0f;
  if (key[0] != 0 && refii::mods::Published(key, value)) {
    flua::lua_pushnumber(L, value);
  } else {
    flua::lua_pushnil(L);
  }
  return 1;
}

int NativeLog(flua::lua_State* L) {
  char text[256];
  ArgString(L, 1, text, sizeof(text));
  REXLOG_INFO("[mod lua, front end] {}", text);
  return 0;
}

int NativeError(flua::lua_State* L) {
  char text[512];
  ArgString(L, 1, text, sizeof(text));
  if (text[0] != 0) {
    refii_bridge_mod_error_post(text, std::strlen(text));
    REXLOG_WARN("[mod lua, front end] {}", text);
  }
  return 0;
}

// Refii.open_menu(name [, p]): refii_mod_open_menu, which the front end's host list honours.
int NativeOpenMenu(flua::lua_State* L) {
  char name[96];
  ArgString(L, 1, name, sizeof(name));
  flua::lua_pushboolean(L, name[0] != 0 && refii_mod_open_menu(ArgInt(L, 2), name) != 0);
  return 1;
}

// Refii._message_box(title, text, accept, cancel, question, p): refii_mod_message_box, on the
// front end's own dialog; the id mod_message_box carries, or nil.
int NativeMessageBox(flua::lua_State* L) {
  char title[128];
  char text[480];
  char accept[64];
  char cancel[64];
  ArgString(L, 1, title, sizeof(title));
  ArgString(L, 2, text, sizeof(text));
  ArgString(L, 3, accept, sizeof(accept));
  ArgString(L, 4, cancel, sizeof(cancel));
  PushIdOrNil(L, refii_mod_message_box(ArgInt(L, 6), title, text, accept, ArgInt(L, 5) != 0 ? cancel : nullptr));
  return 1;
}

// Refii.notice(title, text [, seconds [, p]]): refii_mod_notice — on the front end its own dialog
// until dismissed; the id mod_notice carries, or nil.
int NativeNotice(flua::lua_State* L) {
  char title[128];
  char text[480];
  ArgString(L, 1, title, sizeof(title));
  ArgString(L, 2, text, sizeof(text));
  PushIdOrNil(L, refii_mod_notice(ArgInt(L, 4), title, text, static_cast<float>(ArgNumber(L, 3))));
  return 1;
}

// Refii._text(text): refii_mod_text, under the shared preamble's Refii.text — the host's text lookup
// for a bare key or each {KEY} in the text, or nil with no lookup installed.
int NativeText(flua::lua_State* L) {
  char text[480];
  ArgString(L, 1, text, sizeof(text));
  char out[1024];
  if (text[0] != 0 && refii_mod_text(text, out, sizeof(out)) >= 0) {
    flua::lua_pushstring(L, out);
  } else {
    flua::lua_pushnil(L);
  }
  return 1;
}

// Refii._menu_box(title, text, p, cancelable, n, o1 .. o12): refii_mod_menu_box, under the shared
// preamble's Refii.menu_box(title, text, options [, p [, cancelable]]); the id mod_menu_box carries.
int NativeMenuBox(flua::lua_State* L) {
  constexpr int32_t kMax = 12;
  char title[128];
  char text[480];
  char options[kMax][96];
  const char* list[kMax];
  ArgString(L, 1, title, sizeof(title));
  ArgString(L, 2, text, sizeof(text));
  int32_t count = ArgInt(L, 5);
  count = count < 0 ? 0 : (count > kMax ? kMax : count);
  for (int32_t i = 0; i < count; ++i) {
    ArgString(L, 6 + i, options[i], sizeof(options[i]));
    list[i] = options[i];
  }
  PushIdOrNil(L, refii_mod_menu_box(ArgInt(L, 3), title, text, list, count, ArgInt(L, 4) != 0 ? 1 : 0));
  return 1;
}

// Refii.menu_player(): whose menus are open here (refii_mod_menu_player).
int NativeMenuPlayer(flua::lua_State* L) {
  flua::lua_pushnumber(L, static_cast<flua::lua_Number>(refii_mod_menu_player(g_running.base)));
  return 1;
}

// Refii.hash(name): the game's FNV-1 of a name as a "0x…" string, as the game VM's native.
int NativeHash(flua::lua_State* L) {
  char text[256];
  ArgString(L, 1, text, sizeof(text));
  char hex[16];
  std::snprintf(hex, sizeof(hex), "0x%08x", refii_mod_fnv1(text));
  flua::lua_pushstring(L, hex);
  return 1;
}

// Refii.signed_in() -> the signed-in pad's user index, or nil (refii_mod_signed_in_user).
int NativeSignedIn(flua::lua_State* L) {
  const int32_t user = refii_mod_signed_in_user(g_running.base);
  if (user >= 0) {
    flua::lua_pushnumber(L, static_cast<flua::lua_Number>(user));
  } else {
    flua::lua_pushnil(L);
  }
  return 1;
}

// Refii.save_count() -> the signed-in profile's saved games on every device, or nil with no
// front end (refii_mod_save_count: a content enumeration — ask once a screen).
int NativeSaveCount(flua::lua_State* L) {
  const int32_t count = refii_mod_save_count(g_running.ctx, g_running.base);
  if (count >= 0) {
    flua::lua_pushnumber(L, static_cast<flua::lua_Number>(count));
  } else {
    flua::lua_pushnil(L);
  }
  return 1;
}

// Refii.screen() -> the current presentation's name ("FrontendMainMenu", ...), or nil.
int NativeScreen(flua::lua_State* L) {
  char name[96];
  if (refii_mod_frontend_screen(g_running.ctx, g_running.base, name, sizeof(name)) != 0 && name[0] != 0) {
    flua::lua_pushstring(L, name);
  } else {
    flua::lua_pushnil(L);
  }
  return 1;
}

struct Native {
  const char* name;
  flua::lua_CFunction function;
};

constexpr Native kNatives[] = {
    {"__refii_need_init", &NativeNeedInit},
    {"__refii_publish", &NativePublish},
    {"__refii_value", &NativeValue},
    {"__refii_log", &NativeLog},
    {"__refii_error", &NativeError},
    {"__refii_open_menu", &NativeOpenMenu},
    {"__refii_message_box", &NativeMessageBox},
    {"__refii_menu_player", &NativeMenuPlayer},
    {"__refii_notice", &NativeNotice},
    {"__refii_text", &NativeText},
    {"__refii_menu_box", &NativeMenuBox},
    {"__refii_hash", &NativeHash},
    {"__refii_signed_in", &NativeSignedIn},
    {"__refii_save_count", &NativeSaveCount},
    {"__refii_screen", &NativeScreen},
};

/// The state of one front-end screen's activation.
struct State {
  flua::lua_State* L = nullptr;
  uint32_t screen = 0;
  uint32_t activation = 0;
};
State g_state;
bool g_in_chunk = false;

/// A fresh state: the four libraries, the host-file loaders removed, the natives as globals.
flua::lua_State* OpenState() {
  flua::lua_State* L = flua::luaL_newstate();
  if (L == nullptr) {
    return nullptr;
  }
  constexpr flua::lua_CFunction kLibraries[] = {&flua::luaopen_base, &flua::luaopen_table, &flua::luaopen_string,
                                               &flua::luaopen_math};
  for (const flua::lua_CFunction open : kLibraries) {
    flua::lua_pushcfunction(L, open);
    flua::lua_call(L, 0, 0);
  }
  for (const char* removed : {"dofile", "loadfile"}) {
    flua::lua_pushnil(L);
    flua::lua_setfield(L, LUA_GLOBALSINDEX, removed);
  }
  for (const Native& native : kNatives) {
    flua::lua_pushcfunction(L, native.function);
    flua::lua_setfield(L, LUA_GLOBALSINDEX, native.name);
  }
  return L;
}

/// The state for the active screen: kept while that activation lasts, fresh for the next one.
flua::lua_State* StateFor(uint32_t screen, uint32_t activation) {
  if (g_state.L != nullptr && g_state.screen == screen && g_state.activation == activation) {
    return g_state.L;
  }
  if (g_state.L != nullptr) {
    flua::lua_close(g_state.L);
  }
  g_state = State{OpenState(), screen, activation};
  if (g_state.L != nullptr) {
    REXLOG_INFO("Refii front-end Lua: a fresh state for screen 0x{:08X} (activation {})", screen, activation);
  }
  return g_state.L;
}

/// `text` run protected in `L`; an error is logged.
bool RunChunk(flua::lua_State* L, const char* text, size_t length, const char* name) {
  const int top = flua::lua_gettop(L);
  int status = flua::luaL_loadbuffer(L, text, length, name);
  if (status == 0) {
    status = flua::lua_pcall(L, 0, 0, 0);
  }
  if (status != 0) {
    const char* error = flua::lua_tostring(L, -1);
    REXLOG_WARN("Refii front-end Lua: {} failed: {}", name, error != nullptr ? error : "(no message)");
  }
  flua::lua_settop(L, top);
  return status == 0;
}

}  // namespace

namespace refii::vfs {

/// Whether the game's own Lua VM is up (`*(*(*(0x83496AB8)+0xC)+0xAC)`, RunGameLua's state).
bool GameLuaUp(uint8_t* base) {
  const uint32_t root = LoadU32(base, refii::guest::Address(kGameRoot));
  const uint32_t game = Plausible(root) ? LoadU32(base, root + 0xCu) : 0u;
  return Plausible(game) && Plausible(LoadU32(base, game + 0xACu));
}

/// Runs `chunk` in the front end's mod Lua: the active screen's host state, the composed front-end
/// chunk run first where it has not been. False when no front-end screen is active, or no chunk is
/// composed.
bool RunFrontEndLua(PPCContext& ctx, uint8_t* base, const std::string& chunk) {
  flua::lua_State* L = nullptr;
  if (g_in_chunk) {
    // A native's own dispatch re-entering: the running state, whatever is current now.
    L = g_state.L;
  } else {
    uint32_t activation = 0;
    const uint32_t screen = FrontEndActiveScreen(&activation);
    if (screen == 0u) {
      return false;
    }
    L = StateFor(screen, activation);
  }
  if (L == nullptr) {
    return false;
  }

  const Running outer = g_running;
  const bool was_in_chunk = g_in_chunk;
  g_running = Running{&ctx, base};
  g_in_chunk = true;
  g_need_init = false;
  RunChunk(L, kProbe, std::strlen(kProbe), "=refii probe");
  bool ran = true;
  if (g_need_init) {
    std::vector<uint8_t> text;
    if (!ReadHostAsset(kChunkKey, text) || text.empty()) {
      REXLOG_INFO("Refii front-end Lua: no {} is composed (no mod enabled) -- '{}' not run", kChunkKey, chunk);
      ran = false;
    } else {
      RunChunk(L, reinterpret_cast<const char*>(text.data()), text.size(), "=refii/mod_frontend.lua");
      REXLOG_INFO("Refii front-end Lua: {} ({} bytes) run in the state of screen 0x{:08X}", kChunkKey,
                  text.size(), g_state.screen);
    }
  }
  if (ran) {
    RunChunk(L, chunk.data(), chunk.size(), "=frontend_lua");
  }
  g_in_chunk = was_in_chunk;
  g_running = outer;
  return ran;
}

}  // namespace refii::vfs

extern "C" int32_t refii_mod_run_frontend_lua(PPCContext* ctx, uint8_t* base, const char* chunk) {
  if (ctx == nullptr || chunk == nullptr) {
    return 0;
  }
  return refii::vfs::RunFrontEndLua(*ctx, base, chunk) ? 1 : 0;
}

// ---------------------------------------------------------------------------------------------
// THE FRONT END'S OWN STATE (core/refii_mod.h): the signed-in pad, the saves, the presentation.
// ---------------------------------------------------------------------------------------------

namespace {
constexpr uint32_t kFrontEndRoot = 0x83496AC0u;
constexpr uint32_t kSignedInUserIndex = 0x83496AF8u;
constexpr uint32_t kSignedInUserMask = 0x83496A70u;
}  // namespace

extern "C" int32_t refii_mod_signed_in_user(uint8_t* base) {
  if (base == nullptr) {
    return -1;
  }
  const uint32_t user = LoadU32(base, refii::guest::Address(kSignedInUserIndex));
  const uint32_t mask = LoadU32(base, refii::guest::Address(kSignedInUserMask));
  return user < 4u && ((mask >> user) & 1u) != 0u ? static_cast<int32_t>(user) : -1;
}

extern "C" int32_t refii_mod_save_count(PPCContext* ctx, uint8_t* base) {
  if (ctx == nullptr || base == nullptr) {
    return -1;
  }
  // The save manager the root screen's populate passes: *(*(0x83496AC0+12)+40) (0x826e2240..6c).
  const uint32_t app = LoadU32(base, refii::guest::Address(kFrontEndRoot));
  const uint32_t state = Plausible(app) ? LoadU32(base, app + 12u) : 0u;
  const uint32_t manager = Plausible(state) ? LoadU32(base, state + 40u) : 0u;
  if (!Plausible(manager)) {
    return -1;
  }
  if (refii_mod_signed_in_user(base) < 0) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx, 0x400u);
  work.r3.u32 = manager;
  rex_lhSaveManager_CountSavedGamesAllDevices(work, base);
  return static_cast<int32_t>(work.r3.u32);
}

extern "C" int32_t refii_mod_frontend_screen(PPCContext* ctx, uint8_t* base, char* out, uint32_t size) {
  if (out == nullptr || size == 0) {
    return 0;
  }
  out[0] = 0;
  if (ctx == nullptr || base == nullptr) {
    return 0;
  }
  const uint32_t gui = LoadU32(base, refii::guest::Address(kGuiObject));
  const uint32_t stack = Plausible(gui) ? LoadU32(base, gui + 56u) : 0u;
  if (!Plausible(stack)) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx, 0x200u);
  work.r3.u32 = stack;
  rex_lhGuiPresentationSet_GetCurrent(work, base);
  const uint32_t presentation = work.r3.u32;
  // CPresentation+4 is its name, an AKString: `char*` at +0 (memory: the vtable is 0x82006D48;
  // SetScriptVM is `stw r4,0x1C(r3)`, so +4/+12 are the name and the path).
  const uint32_t text = Plausible(presentation) ? LoadU32(base, presentation + 4u) : 0u;
  if (!Plausible(text)) {
    return 0;
  }
  uint32_t n = 0;
  while (n + 1u < size && base[text + n] != 0 && n < 255u) {
    out[n] = static_cast<char>(base[text + n]);
    ++n;
  }
  out[n] = 0;
  return n != 0 ? 1 : 0;
}
