// refii — the Refii.* natives in the GAME VM, for mods' Lua. Registered into the global `Refii`
// table before any mod's init.lua runs (mod_lua_hooks.cpp), once per Lua state.
//
// HOW A HOST FUNCTION BECOMES A LUA NATIVE. The runtime hands a host PPCFunc a guest address
// (refii_bridge_rex_allocate_thunk) that the guest's function table resolves back to it, so the
// VM's own lua_pushcclosure takes it like any of the game's 3,602 natives: called with r3 = L,
// answering r3 = the result count. The Lua C API it uses is the GAME runtime's (two Lua runtimes
// are linked; the UI's is never touched): versions/*/refii_config.toml names each function.
//
// NUMBERS. This Lua's number is a 32-bit float, so an entity is never passed as its address:
// every entity or object is a HANDLE (refii_mod_handle), the same one the events carry, and a
// definition or text hash is a "0x…" string (a number is accepted too). Results follow the same
// rule.
//
// SURFACE (all under Refii): publish(key, n) value(key) log(text) hash(name)
//   health(e) -> cur, max · set_health(e, v) · modify_health(e, delta, attacker)
//   position(e) -> x, y, z · set_position(e, x, y, z)
//   facing(e) -> x, y, z · velocity(e) -> x, y, z · set_facing(e, x, y [, z])
//   entity_tint(e, r, g, b [, a]) · entity_untint(e)
//   entity_alpha(e, a) · entity_scale(e, s) · entity_saturation_mask(e, on) · entity_drawable(e, on)
//   lock_target(p [, e]) · release_lock(p) · switch_target(p, left) -> target · locked_target(p) -> target
//   in_cutscene([p [, kinds]]) -> inside, kinds   (refii_mod_in_cutscene: 1 full, 2 interactive, 4 look-at)
//   apply_status(e, kind, seconds [, level [, source]]) · clear_status(e, kind) · has_status(e, kind)
//   set_speed_multiplier(p, factor) · clear_speed_multiplier(p) · speed_multiplier(p) -> overridden, factor
//   current_action(e) -> action · action_elapsed(a) -> seconds · action_interruptible(a) · allow_action_interrupt(a)
//   allow_action_interrupt_at(a, seconds)
//   action_type(a) -> EScriptableAction · action_name(a) -> its name
//   living(e) · spell_in_slot(p, slot) -> type · spell_parts(serial) -> table · vortex_held(serial) -> { e, .. }
//   spell_balance(caster, type, level) -> { status_seconds, status_spread, duration, swords }   (refii_mod_spell_balance)
//   host_serial(serial) -> serial · local_serial(host_serial) -> serial   (refii_mod_host_serial / _local_serial)
//   knockback(e, x, y, z [, force]) · stagger(e)
//   play_sound(cue, e [, tag]) · play_sound_at(cue, x, y, z [, tag]) · stop_sound(e, tag [, fade_ms])
//   play_music(name) · stop_music(name)
//   play_animation(e, name [, loops [, blend_seconds [, speed]]]) · stop_animation(e)
//   modify_opinion(villager, hero, axis, delta) · add_deed(villager, hero, deed)
//   npc_follow(e, target [, radius]) · npc_go_to(e, x, y, z) · npc_flee(e, from) · npc_attack(e, target)
//   npc_set_hostile(e, other, on) · npc_stop(e)
//   set_time_of_day(hours) · time_of_day() -> hours · set_weather(theme [, weight [, seconds]])
//   gold(e) · give_gold(e, delta) · item_count(e, def) · add_item(e, def)
//   experience(e, pool) · give_experience(e, pool, amount) · spend_experience(e, pool, amount)
//   ability_level(e, type) · spell_serial(spell) -> serial
//   _cast_spell(caster, type, targeted, target, level, has_pos, px, py, pz, has_dir, dx, dy, dz [, tag [, scale
//   [, at_kind, at_entity, ax, ay, az]]]) -> spell, serial
//   (cast_spell(caster, type, targeted, target, level [, pos [, dir [, options]]]) is the preamble's: it unpacks the tables)
//   relation(a, b) · alive(e) · definition(e) · name(e) · player_of(e) · level_name()
//   player_hero(p) · local_hero() · menu_player() · player_value(p, key [, fallback])
//   player_set(p, key, v) · player_add(p, key, d [, saved]) · player_save(p, key, v) · player_here(p)
//   player_value_text(p, key) · player_set_text(p, key, s) · player_save_text(p, key, s)
//   player_value_hash(p, key) -> "0x…" · player_save_hash(p, key, "0x…")
//   event_replica() · wheel_switches_target(e)
//   renown(e) · morality(e) · purity(e) · physique(e) -> fat, height
//   modify_renown(e, delta) · modify_morality(e, delta) · modify_purity(e, delta)
//   can_save(p) · save_game(p)
//   age(e) · inventory(e) · equipped(e) -> melee, ranged · near(x, y, z, r [, creatures])
//   appearance(p) -> {automatic, tan, ..} · _set_appearance(p, tan, fat, height, strength, skill, will,
//   morality, purity, age) (set_appearance is the preamble's) · set_appearance_automatic(p, on)
//   morph_get(p, morph) · morph_set(p, morph, w) -> ok · morph_id(name) -> "0x…" (morph "<mod>.<name>" or "0x…")
//   use_item(p, def [, opts]) · equip_weapon(p, def [, opts]) · perform_expression(p, expression [, held [, opts]])
//   assign_spell(p, spell [, opts]) (opts {ungated = true}) · release_expression(p)
//   sprinting(p) · end_sprint(p) · _set_locomotion(p, key, value|false) · locomotion(p, key)
//   set_parry(p, window, wedge) · parry(p) -> window, wedge
//   expressions(p) · spells_known(p) · selected_expression(p) · record_string(def, field) · record_int(def, field)
//   record_number(def, field) · record_bool(def, field) · record_exists(def) · add_item(e, def [, n]) -> item, added
//   record_ref(def, field) -> "0x…" · creature_tier(e) -> bits (Refii.CREATURE_ELITE, CREATURE_LIEUTENANT)
//   charge_spell(p, type, held)
//   wheel_switches_target(e [, on])
//   line_of_sight(x1, y1, z1, x2, y2, z2) · spawn(def, x, y, z) · entity(handle) · handle(entity)
//   forgive_crimes(villager, p) · discovered_crime(villager, p) · perceived_renown(villager, p)
//   stop_fx(id) · _play_fx(name, x, y, z, e, duration, scale, r, g, b, intensity, loop, ox, oy, oz)
//   (play_fx / play_fx_on are the preamble's, Engine_ModLua: they unpack the options table)
//   open_menu(name [, p]) · _message_box(title, text, accept, cancel, question, p)
//   (message_box is the preamble's: it fills the defaults)
//   notice(title, text [, seconds [, p]]) · _menu_box(title, text, p, cancelable, n, o1 .. o12)
//   _text(text) -> the mod text lookup's answer, or nil (text(key, a .. h) is the preamble's: it fills %1 .. %8)
//   _mark(e, x, y, z, icon, text, p) (mark is the preamble's) · unmark(id) · fade(p, dir [, seconds])
//   _world_meter(e, x, y, z, value, max, style, p) (world_meter is the preamble's) · mark_meter(id, value, max [, style])
//   meter(id, label, value, max [, p [, style]]) · meter_remove(id)
//   camera_mode(p) · _camera_override(p, mode, fov, pitch_limit, blend, flags) · fov(p) · hide_part(e, part, on [, p])
//   (camera_override is the preamble's: it unpacks the options table)
//   _camera_stated(hero, mode) · _camera_hold(hero) · _camera_held(hero, bits)   (the core camera chunk's)
//   input_claim(p, action, on) · press(p, button, held) · tap(p, button [, ticks]) · hud_icon(p, element, texture) · hud_slide(p, element, slide) · hud_hold(p, piece, on)
//   _light(e, x, y, z, r, g, b, energy, range, ox, oy, oz, at, flicker, dx, dy, dz, cone, attenuation) (light is the preamble's)
//   light_remove(id) · send_to_partner(mod, name, payload) · signal(name, payload) · publish_text(key, text)
//   (menu_box is the preamble's: it unpacks the options table)
//   _menu_rows(menu, rows, p) · open_screen(id [, p])
//   (set_menu_rows is the preamble's: it writes the rows table as the bus text)
//   _write(kind, field, value) · _error(text)

#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <utility>

#include <rex/logging.h>

#include "core/achievement_statement.h"
#include "core/guest_build.h"
#include "core/joiner_use_text.h"
#include "core/mod_values.h"
#include "core/refii_mod.h"
#include "core/payload_bridge.h"
#include "core/rex_payload_bridge.h"
#include "hooks/ppc_externs.h"

/// The local co-op screen mode (guest_tick_pump.cpp).
extern "C" int RefiiCoopSplitScreen(void);
extern "C" uint32_t RefiiCoopPlayerDog(uint8_t* base, int player);
extern "C" void RefiiCoopSetPartnerDog(uint32_t dog);
/// Player two brought a hero (a character from a save), not the plain henchman (guest_tick_pump.cpp).
extern "C" int RefiiCoopGetOwnHero(void);
extern "C" void RefiiCoopSetJobWorker(int player);
extern "C" void RefiiCoopPartnerScreen(int on);
extern "C" int RefiiCoopPartnerCameraUpdating(void);
extern "C" void RefiiPlayerIconsFromCamera(int on);
extern "C" int RefiiCoopPartnerCameraGeneration(void);
extern "C" void RefiiCoopSetEstateBuyer(int player);
extern "C" int32_t RefiiEstateBuyerOpens(PPCContext* ctx, uint8_t* base, int32_t stated);
extern "C" void RefiiEndRedecorating(PPCContext* ctx, uint8_t* base, uint32_t entity);
extern "C" void refii_bridge_estate_intent_post(const char* text);
extern "C" void refii_bridge_camera_offset_set(int32_t player, const float* seven, int32_t on);
extern "C" void refii_bridge_coop_tether_stated(float distance, float separation);
extern "C" uint32_t refii_household_resolve_uid(PPCContext* ctx, uint8_t* base, uint64_t uid);
extern "C" void refii_household_context(int32_t on);
extern "C" int32_t refii_household_received_item(PPCContext* ctx, uint8_t* base, uint32_t hash);
extern "C" int32_t refii_household_carry_target(char* out, uint32_t capacity);
extern "C" void refii_household_state_use_target(PPCContext* ctx, uint8_t* base);
extern "C" void refii_household_forward_speech(uint8_t* base, uint32_t speaker, float seconds);
extern "C" int32_t refii_household_follower_two(PPCContext* ctx, uint8_t* base, int32_t index, uint32_t* entity,
                                                uint32_t* flags);

PPC_EXTERN_FUNC(rex_lua_pushcclosure);
PPC_EXTERN_FUNC(rex_lua_getfield);
PPC_EXTERN_FUNC(rex_lua_setfield);
PPC_EXTERN_FUNC(rex_lua_createtable);
PPC_EXTERN_FUNC(rex_lua_gettop);
PPC_EXTERN_FUNC(rex_lua_settop);
PPC_EXTERN_FUNC(rex_lua_type);
PPC_EXTERN_FUNC(rex_lua_tonumber);
PPC_EXTERN_FUNC(rex_lua_toboolean);
PPC_EXTERN_FUNC(rex_lua_tolstring);
PPC_EXTERN_FUNC(rex_lua_pushnil);
PPC_EXTERN_FUNC(rex_lua_pushnumber);
PPC_EXTERN_FUNC(rex_lua_pushboolean);
PPC_EXTERN_FUNC(rex_lua_pushstring);
PPC_EXTERN_FUNC(rex_lua_touserdata);
PPC_EXTERN_FUNC(rex_lua_rawseti);
PPC_EXTERN_FUNC(rex_lhLua_PushEntity);
PPC_EXTERN_FUNC(rex_lhLua_IsFableType);
PPC_EXTERN_FUNC(rex_lhScriptEntityRef_Resolve);
PPC_EXTERN_FUNC(rex_lhEntityManager_CollectWithComponent);
PPC_EXTERN_FUNC(rex_CECGroupMind_AddGroupMinds);

namespace refii::vfs {
bool WriteModEventField(int32_t kind, const char* field, double value);
// coop_estate_statements.cpp: the host has stated this sale sign's building here.
bool EstateStatedSign(PPCContext& ctx, uint8_t* base, uint32_t sign);
// display_box_hooks.cpp: a wide LHString handle's text.
std::string ReadStatedWideLhString(uint8_t* base, uint32_t handle);
// guest_tick_pump.cpp: a component off an entity by id, the guest's own layout.
uint32_t EntityComponent(uint8_t* base, uint32_t entity, uint32_t id);
// mod_commands.cpp: the camera per player.
void ModCameraStated(PPCContext& ctx, uint8_t* base, uint32_t hero, int32_t mode);
void ModCameraZoomStated(PPCContext& ctx, uint8_t* base, uint32_t hero, const char* set, int32_t level,
                         const float* values);
bool ModCameraHold(PPCContext& ctx, uint8_t* base, uint32_t hero, int32_t* mode, float* fov, float* pitch, float* blend,
                   int32_t* flags, uint32_t* revision);
void ModCameraHeld(uint8_t* base, uint32_t hero, int32_t bits);
void NoteLuaCancelled();
void BoxSeatEnter(uint8_t* base, int32_t player);
void BoxSeatLeave(uint8_t* base);
uint32_t PlayModFxOnHandle(const char* name, const float* xyz, uint32_t handle, const refii_mod_fx_options* options);
uint32_t PlaceModLightOnHandle(uint32_t handle, const float* xyz, const float* rgb, float energy, float range,
                               const refii_mod_light_options* options);
}

namespace {

constexpr int32_t kGlobalsIndex = -10002;
constexpr int32_t kTypeString = 4;
constexpr int32_t kTypeTable = 5;
/// A guest function inside the game module, for the thunk pool (RunScriptBuffer, TU1).
constexpr uint32_t kModuleAnchor = 0x82451F18u;
constexpr uint32_t kScratchBytes = 0x100u;
/// The frame's scratch as a whole (the native's frame is 0x400 bytes, scratch from +0x40).
constexpr uint32_t kLongScratchBytes = 0x300u;
/// The LHString holding "FableType_CEntity", the Entity userdata's metatable name in the registry
/// (set by sub_8324BD18; rex_lhLua_PushEntity reads it, TU1 0x823A2FEC).
constexpr uint32_t kEntityTypeName = 0x834979B0u;
constexpr int32_t kTypeNumber = 3;

/// The Lua C API over a guest frame: every call below the native's own stack pointer, strings
/// staged in the frame's scratch.
struct Lua {
  PPCContext& ctx;
  uint8_t* base;
  uint32_t L;
  uint32_t scratch;

  Lua(PPCContext& c, uint8_t* b, uint32_t state) : ctx(c), base(b), L(state), scratch(c.r1.u32 + 0x40u) {}

  PPCContext Work() const {
    PPCContext work = ctx;
    work.r3.u32 = L;
    return work;
  }

  /// `text` copied into the frame's scratch; the guest address of the copy.
  uint32_t Stage(const char* text) {
    size_t n = std::strlen(text);
    if (n >= kScratchBytes - 1) {
      n = kScratchBytes - 1;
    }
    std::memcpy(base + scratch, text, n);
    base[scratch + n] = 0;
    return scratch;
  }

  int32_t Top() {
    PPCContext w = Work();
    rex_lua_gettop(w, base);
    return static_cast<int32_t>(w.r3.u32);
  }
  void SetTop(int32_t index) {
    PPCContext w = Work();
    w.r4.s64 = index;
    rex_lua_settop(w, base);
  }
  int32_t Type(int32_t index) {
    PPCContext w = Work();
    w.r4.s64 = index;
    rex_lua_type(w, base);
    return static_cast<int32_t>(w.r3.u32);
  }
  double Number(int32_t index) {
    PPCContext w = Work();
    w.r4.s64 = index;
    rex_lua_tonumber(w, base);
    return w.f1.f64;
  }
  bool Boolean(int32_t index) {
    PPCContext w = Work();
    w.r4.s64 = index;
    rex_lua_toboolean(w, base);
    return w.r3.u32 != 0;
  }
  /// The string at `index` copied into `out` (empty for a non-string).
  void String(int32_t index, char* out, size_t size) {
    out[0] = 0;
    if (Type(index) != kTypeString) {
      return;
    }
    PPCContext w = Work();
    w.r4.s64 = index;
    w.r5.u32 = 0;
    rex_lua_tolstring(w, base);
    const uint32_t text = w.r3.u32;
    if (text < 0x10000u || text >= 0xC0000000u) {
      return;
    }
    size_t n = 0;
    for (; n + 1 < size; ++n) {
      const char c = static_cast<char>(base[text + n]);
      if (c == 0) {
        break;
      }
      out[n] = c;
    }
    out[n] = 0;
  }
  /// The string at `index` whole, up to `limit` bytes, and its length in `length` (counted to its
  /// terminator, at most 1 MiB; a longer one is cut to `limit` and the caller decides what a cut
  /// means). Empty for a non-string.
  std::string LongString(int32_t index, size_t limit, size_t* length = nullptr) {
    if (length != nullptr) {
      *length = 0;
    }
    if (Type(index) != kTypeString) {
      return {};
    }
    PPCContext w = Work();
    w.r4.s64 = index;
    w.r5.u32 = 0;
    rex_lua_tolstring(w, base);
    const uint32_t text = w.r3.u32;
    if (text < 0x10000u || text >= 0xC0000000u) {
      return {};
    }
    size_t size = 0;
    while (size < (1u << 20) && text + size < 0xC0000000u && base[text + size] != 0) {
      ++size;
    }
    if (length != nullptr) {
      *length = size;
    }
    return std::string(reinterpret_cast<const char*>(base + text), size < limit ? size : limit);
  }
  void PushNil() {
    PPCContext w = Work();
    rex_lua_pushnil(w, base);
  }
  void PushNumber(double value) {
    PPCContext w = Work();
    w.f1.f64 = value;
    rex_lua_pushnumber(w, base);
  }
  void PushBoolean(bool value) {
    PPCContext w = Work();
    w.r4.u32 = value ? 1u : 0u;
    rex_lua_pushboolean(w, base);
  }
  void PushString(const char* text) {
    PPCContext w = Work();
    w.r4.u32 = Stage(text);
    rex_lua_pushstring(w, base);
  }
  /// A string of up to kLongScratchBytes - 1 bytes (the route's lines): staged across the frame's
  /// whole scratch, which nothing else holds while it is pushed.
  void PushLongString(const char* text) {
    size_t n = std::strlen(text);
    if (n >= kLongScratchBytes - 1) {
      n = kLongScratchBytes - 1;
    }
    std::memcpy(base + scratch, text, n);
    base[scratch + n] = 0;
    PPCContext w = Work();
    w.r4.u32 = scratch;
    rex_lua_pushstring(w, base);
  }
  void PushClosure(uint32_t function) {
    PPCContext w = Work();
    w.r4.u32 = function;
    w.r5.u32 = 0;
    rex_lua_pushcclosure(w, base);
  }
  void GetField(int32_t index, const char* name) {
    PPCContext w = Work();
    w.r4.s64 = index;
    w.r5.u32 = Stage(name);
    rex_lua_getfield(w, base);
  }
  void SetField(int32_t index, const char* name) {
    PPCContext w = Work();
    w.r4.s64 = index;
    w.r5.u32 = Stage(name);
    rex_lua_setfield(w, base);
  }
  /// t[n] = the value on top (popped), t at `index`.
  void RawSetI(int32_t index, int32_t n) {
    PPCContext w = Work();
    w.r4.s64 = index;
    w.r5.s64 = n;
    rex_lua_rawseti(w, base);
  }
  void NewTable() {
    PPCContext w = Work();
    w.r4.u32 = 0;
    w.r5.u32 = 32;
    rex_lua_createtable(w, base);
  }

  // Argument readers in the API's terms.
  /// A whole number read safely: NaN and anything outside [lo, hi] clamp (a raw float-to-int cast
  /// is undefined there).
  static double Clamp(double value, double lo, double hi) {
    if (std::isnan(value)) {
      return 0.0;
    }
    return value < lo ? lo : (value > hi ? hi : value);
  }
  uint32_t Entity(int32_t index) {
    return refii_mod_handle_address(static_cast<uint32_t>(Clamp(Number(index), 0.0, 4294967295.0)));
  }
  int32_t Int(int32_t index) { return static_cast<int32_t>(Clamp(Number(index), -2147483648.0, 2147483647.0)); }
  /// A hash: a "0x…" (or decimal) string, or a number.
  uint32_t Hash(int32_t index) {
    if (Type(index) == kTypeString) {
      char text[64];
      String(index, text, sizeof(text));
      return static_cast<uint32_t>(std::strtoul(text, nullptr, 0));
    }
    return static_cast<uint32_t>(Clamp(Number(index), 0.0, 4294967295.0));
  }
  void PushHash(uint32_t hash) {
    char text[16];
    std::snprintf(text, sizeof(text), "0x%08x", hash);
    PushString(text);
  }
  void PushHandle(uint32_t address) {
    if (address == 0) {
      PushNil();
    } else {
      PushNumber(refii_mod_handle(address));
    }
  }
};

}  // namespace

namespace {
/// How many values the running native pushed.
int32_t g_results = 0;
}  // namespace
#define RESULTS(n) (g_results = (n))

namespace {

/// Each native runs with the guest's own stack pointer: its frame is where the VM called it, so
/// the API calls are made from a frame below it. r3 = L in, r3 = the result count out.
#define REFII_LUA_NATIVE(name)                                   \
  void name##Body(Lua& lua);                                     \
  void name(PPCContext& ctx, uint8_t* base) {                    \
    const uint32_t state = ctx.r3.u32;                           \
    PPCContext frame = refii::mods::Frame(ctx, 0x400u);          \
    Lua lua(frame, base, state);                                 \
    g_results = 0;                                               \
    name##Body(lua);                                             \
    ctx.r3.u64 = static_cast<uint32_t>(g_results);               \
  }                                                              \
  void name##Body(Lua& lua)

REFII_LUA_NATIVE(NativePublish) {
  char key[128];
  lua.String(1, key, sizeof(key));
  if (key[0] != 0) {
    refii::mods::Publish(key, static_cast<float>(lua.Number(2)));
  }
  RESULTS(0);
}

REFII_LUA_NATIVE(NativeValue) {
  char key[128];
  lua.String(1, key, sizeof(key));
  float value = 0.0f;
  if (key[0] != 0 && refii::mods::Published(key, value)) {
    lua.PushNumber(value);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeLog) {
  char text[256];
  lua.String(1, text, sizeof(text));
  REXLOG_INFO("[mod lua] {}", text);
  RESULTS(0);
}

REFII_LUA_NATIVE(NativeHash) {
  char text[256];
  lua.String(1, text, sizeof(text));
  lua.PushHash(refii_mod_fnv1(text));
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeHealth) {
  float current = 0.0f, max = 0.0f;
  if (refii_mod_health(lua.base, lua.Entity(1), &current, &max) != 0) {
    lua.PushNumber(current);
    lua.PushNumber(max);
    RESULTS(2);
  } else {
    lua.PushNil();
    RESULTS(1);
  }
}

REFII_LUA_NATIVE(NativeSetHealth) {
  lua.PushBoolean(refii_mod_set_health(lua.base, lua.Entity(1), static_cast<float>(lua.Number(2))) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeModifyHealth) {
  lua.PushBoolean(refii_mod_modify_health(&lua.ctx, lua.base, lua.Entity(1), static_cast<float>(lua.Number(2)),
                                          lua.Entity(3)) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativePosition) {
  float xyz[3] = {0, 0, 0};
  if (refii_mod_entity_position(&lua.ctx, lua.base, lua.Entity(1), xyz) != 0) {
    lua.PushNumber(xyz[0]);
    lua.PushNumber(xyz[1]);
    lua.PushNumber(xyz[2]);
    RESULTS(3);
  } else {
    lua.PushNil();
    RESULTS(1);
  }
}

// Refii.facing(e) / Refii.velocity(e) -> x, y, z (nil for none): Physics.GetFacingVector /
// GetVelocity (refii_mod_facing / refii_mod_velocity), guest axes, z up.
REFII_LUA_NATIVE(NativeFacing) {
  float xyz[3] = {0, 0, 0};
  if (refii_mod_facing(&lua.ctx, lua.base, lua.Entity(1), xyz) != 0) {
    lua.PushNumber(xyz[0]);
    lua.PushNumber(xyz[1]);
    lua.PushNumber(xyz[2]);
    RESULTS(3);
  }
  lua.PushNil();
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeVelocity) {
  float xyz[3] = {0, 0, 0};
  if (refii_mod_velocity(&lua.ctx, lua.base, lua.Entity(1), xyz) != 0) {
    lua.PushNumber(xyz[0]);
    lua.PushNumber(xyz[1]);
    lua.PushNumber(xyz[2]);
    RESULTS(3);
  }
  lua.PushNil();
  RESULTS(1);
}

// Refii.set_facing(e, x, y [, z]) -> bool: Physics.SetFacingVector (a hero's through
// Player.SetFacingVector), refii_mod_set_facing; the game keeps the facing level (it drops z).
REFII_LUA_NATIVE(NativeSetFacing) {
  const float xyz[3] = {static_cast<float>(lua.Number(2)), static_cast<float>(lua.Number(3)),
                        static_cast<float>(lua.Number(4))};
  lua.PushBoolean(refii_mod_set_facing(&lua.ctx, lua.base, lua.Entity(1), xyz) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeSetPosition) {
  const float xyz[3] = {static_cast<float>(lua.Number(2)), static_cast<float>(lua.Number(3)),
                        static_cast<float>(lua.Number(4))};
  lua.PushBoolean(refii_mod_set_position(&lua.ctx, lua.base, lua.Entity(1), xyz) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeDecorateRotation) {
  const uint32_t entity = lua.Entity(1);
  const uint32_t placement = refii_mod_entity_alive(lua.base, entity) != 0
                                 ? refii::mods::LoadU32(lua.base, entity + 0x7Cu) : 0u;
  if (placement < 0x10000u || placement >= 0xC0000000u) {
    lua.PushNil();
    RESULTS(1);
  }
  PPCContext call = refii::mods::Frame(lua.ctx);
  if (lua.Type(2) == kTypeNumber) {
    float q[4];
    float length = 0;
    for (int i = 0; i < 4; ++i) {
      q[i] = static_cast<float>(lua.Number(i + 2));
      length += q[i] * q[i];
    }
    if (!std::isfinite(length) || length < 0.000001f) {
      lua.PushBoolean(false);
      RESULTS(1);
    }
    uint32_t bits[4];
    for (int i = 0; i < 4; ++i) {
      q[i] /= std::sqrt(length);
      std::memcpy(&bits[i], &q[i], sizeof(float));
    }
    call.r4.u64 = (static_cast<uint64_t>(bits[0]) << 32) | bits[1];
    call.r5.u64 = (static_cast<uint64_t>(bits[2]) << 32) | bits[3];
    lua.PushBoolean(refii::mods::CallVirtual(call, lua.base, placement, 29));
    RESULTS(1);
  }
  const uint32_t vtable = refii::mods::LoadU32(lua.base, placement);
  const uint32_t target = vtable >= 0x10000u && vtable < 0xC0000000u
                              ? refii::mods::LoadU32(lua.base, vtable + 0x44u) : 0u;
  const uint32_t out = call.r1.u32 + 0x60u;
  call.r3.u32 = out;
  call.r4.u32 = placement;
  if (!refii::mods::CallGuest(call, lua.base, target)) {
    lua.PushNil();
    RESULTS(1);
  }
  float q[4];
  for (int i = 0; i < 4; ++i) q[i] = refii::mods::LoadF32(lua.base, out + i * 4u);
  for (float value : q) lua.PushNumber(value);
  RESULTS(4);
}

// Refii.entity_tint(e, r, g, b [, a]): a defaults to 1.
REFII_LUA_NATIVE(NativeEntityTint) {
  const float a = lua.Type(5) == kTypeNumber ? static_cast<float>(lua.Number(5)) : 1.0f;
  lua.PushBoolean(refii_mod_entity_tint(&lua.ctx, lua.base, lua.Entity(1), static_cast<float>(lua.Number(2)),
                                        static_cast<float>(lua.Number(3)), static_cast<float>(lua.Number(4)), a) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeEntityUntint) {
  lua.PushBoolean(refii_mod_entity_untint(&lua.ctx, lua.base, lua.Entity(1)) != 0);
  RESULTS(1);
}

// The other component-3 setters: Refii.entity_alpha(e, a) · entity_scale(e, s) ·
// entity_saturation_mask(e, on) · entity_drawable(e, on). `on` is a Lua boolean or number.
REFII_LUA_NATIVE(NativeEntityAlpha) {
  lua.PushBoolean(refii_mod_entity_alpha(&lua.ctx, lua.base, lua.Entity(1), static_cast<float>(lua.Number(2))) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeEntityScale) {
  lua.PushBoolean(refii_mod_entity_scale(&lua.ctx, lua.base, lua.Entity(1), static_cast<float>(lua.Number(2))) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeEntitySaturationMask) {
  const bool on = lua.Type(2) == kTypeNumber ? lua.Number(2) != 0.0 : lua.Boolean(2);
  lua.PushBoolean(refii_mod_entity_saturation_mask(&lua.ctx, lua.base, lua.Entity(1), on ? 1 : 0) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeEntityDrawable) {
  const bool on = lua.Type(2) == kTypeNumber ? lua.Number(2) != 0.0 : lua.Boolean(2);
  lua.PushBoolean(refii_mod_entity_drawable(&lua.ctx, lua.base, lua.Entity(1), on ? 1 : 0) != 0);
  RESULTS(1);
}

// The target lock: Refii.lock_target(p [, e]) (e nil or 0: the game's own pick) · release_lock(p)
// · switch_target(p, left) -> the target held afterwards · locked_target(p) -> the target or nil.
REFII_LUA_NATIVE(NativeLockTarget) {
  const uint32_t entity = lua.Type(2) == kTypeNumber ? lua.Entity(2) : 0u;
  lua.PushBoolean(refii_mod_lock_target(&lua.ctx, lua.base, lua.Int(1), entity) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeReleaseLock) {
  lua.PushBoolean(refii_mod_release_lock(&lua.ctx, lua.base, lua.Int(1)) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeSwitchTarget) {
  const bool left = lua.Type(2) == kTypeNumber ? lua.Number(2) != 0.0 : lua.Boolean(2);
  lua.PushHandle(refii_mod_switch_target(&lua.ctx, lua.base, lua.Int(1), left ? 1 : 0));
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeLockedTarget) {
  lua.PushHandle(refii_mod_locked_target(&lua.ctx, lua.base, lua.Int(1)));
  RESULTS(1);
}

// Cutscenes: Refii.in_cutscene([p [, kinds]]) -> inside, kinds — whether any of `kinds` (the
// REFII_CUTSCENE_* bits, all three when nil) holds for player p (nil or 0: either player), and
// every bit holding (refii_mod_in_cutscene).
REFII_LUA_NATIVE(NativeInCutscene) {
  const int32_t player = lua.Type(1) == kTypeNumber ? lua.Int(1) : 0;
  const int32_t wanted = lua.Type(2) == kTypeNumber ? lua.Int(2)
                                                     : REFII_CUTSCENE_FULL | REFII_CUTSCENE_INTERACTIVE | REFII_CUTSCENE_LOOK_AT;
  const int32_t kinds = refii_mod_in_cutscene(&lua.ctx, lua.base, player);
  lua.PushBoolean((kinds & wanted) != 0);
  lua.PushNumber(kinds);
  RESULTS(2);
}

// Movement speed: Refii.set_speed_multiplier(p, factor) · clear_speed_multiplier(p) ·
// speed_multiplier(p) -> overridden, factor (nil without a player component).
REFII_LUA_NATIVE(NativeSetSpeedMultiplier) {
  lua.PushBoolean(refii_mod_set_speed_multiplier(lua.base, lua.Int(1), static_cast<float>(lua.Number(2))) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeClearSpeedMultiplier) {
  lua.PushBoolean(refii_mod_clear_speed_multiplier(lua.base, lua.Int(1)) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeSpeedMultiplier) {
  float factor = 1.0f;
  const uint32_t hero = refii_mod_player_hero(lua.base, lua.Int(1));
  if (hero == 0 || refii_mod_component(lua.base, hero, 33u) == 0) {
    lua.PushNil();
    RESULTS(1);
    return;
  }
  const bool overridden = refii_mod_speed_multiplier(lua.base, lua.Int(1), &factor) != 0;
  lua.PushBoolean(overridden);
  lua.PushNumber(factor);
  RESULTS(2);
}

// Actions: Refii.current_action(e) -> action or nil · action_elapsed(a) -> seconds or nil ·
// action_interruptible(a) · allow_action_interrupt(a). An action is a handle like any object.
REFII_LUA_NATIVE(NativeCurrentAction) {
  lua.PushHandle(refii_mod_current_action(lua.base, lua.Entity(1)));
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeActionElapsed) {
  float seconds = 0.0f;
  if (refii_mod_action_elapsed(&lua.ctx, lua.base, lua.Entity(1), &seconds) != 0) {
    lua.PushNumber(seconds);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeActionInterruptible) {
  lua.PushBoolean(refii_mod_action_interruptible(lua.base, lua.Entity(1)) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeAllowActionInterrupt) {
  lua.PushBoolean(refii_mod_allow_action_interrupt(lua.base, lua.Entity(1)) != 0);
  RESULTS(1);
}

// Refii.allow_action_interrupt_at(a, seconds): the grant once a's clock reaches seconds
// (refii_mod_allow_action_interrupt_at). True when granted or waiting.
REFII_LUA_NATIVE(NativeAllowActionInterruptAt) {
  lua.PushBoolean(refii_mod_allow_action_interrupt_at(&lua.ctx, lua.base, lua.Entity(1), static_cast<float>(lua.Number(2))) != 0);
  RESULTS(1);
}

// Refii.action_type(a) -> EScriptableAction (0..88) or nil · Refii.action_name(a) -> its name or
// nil: what the action was made from (refii_mod_action_type, the factory seam's record).
REFII_LUA_NATIVE(NativeActionType) {
  const int32_t type = refii_mod_action_type(lua.base, lua.Entity(1));
  if (type >= 0) {
    lua.PushNumber(type);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeActionName) {
  char name[64];
  if (refii_mod_action_name(lua.base, lua.Entity(1), name, sizeof(name)) != 0) {
    lua.PushString(name);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

// Refii.living(e): in the world and, for a creature, not yet died (refii_mod_entity_living).
REFII_LUA_NATIVE(NativeLiving) {
  lua.PushBoolean(refii_mod_entity_living(lua.base, lua.Entity(1)) != 0);
  RESULTS(1);
}

// Refii.spell_in_slot(p, slot) -> the spell type held, -1 for none.
REFII_LUA_NATIVE(NativeSpellInSlot) {
  lua.PushNumber(refii_mod_spell_in_slot(lua.base, lua.Int(1), lua.Int(2)));
  RESULTS(1);
}

// Refii.spell_parts(serial) -> { spell, type, targeted, level, caster, caster_player, vortex,
// anchor, swords = { e1, .. }, origin = { x, y, z } or nil, exploded } (handles; absent parts
// nil), or nil for a cast the factory never saw (refii_mod_spell_parts).
REFII_LUA_NATIVE(NativeSpellParts) {
  refii_mod_cast_parts parts{};
  if (refii_mod_spell_parts(&lua.ctx, lua.base, static_cast<uint32_t>(Lua::Clamp(lua.Number(1), 0.0, 4294967295.0)), &parts) == 0) {
    lua.PushNil();
    RESULTS(1);
    return;
  }
  lua.NewTable();
  const auto handle_field = [&lua](const char* name, uint32_t address) {
    if (address != 0) {
      lua.PushNumber(refii_mod_handle(address));
      lua.SetField(-2, name);
    }
  };
  handle_field("spell", parts.spell);
  lua.PushNumber(parts.type);
  lua.SetField(-2, "type");
  lua.PushBoolean(parts.targeted != 0);
  lua.SetField(-2, "targeted");
  lua.PushNumber(parts.level);
  lua.SetField(-2, "level");
  handle_field("caster", parts.caster);
  lua.PushNumber(parts.caster_player);
  lua.SetField(-2, "caster_player");
  handle_field("vortex", parts.vortex);
  handle_field("anchor", parts.anchor);
  lua.NewTable();
  for (int32_t i = 0; i < parts.sword_count; ++i) {
    lua.PushNumber(refii_mod_handle(parts.swords[i]));
    lua.RawSetI(-2, i + 1);
  }
  lua.SetField(-2, "swords");
  if (parts.has_origin != 0) {
    lua.NewTable();
    for (int32_t i = 0; i < 3; ++i) {
      lua.PushNumber(parts.origin[i]);
      lua.RawSetI(-2, i + 1);
    }
    lua.SetField(-2, "origin");
  }
  lua.PushBoolean(parts.exploded != 0);
  lua.SetField(-2, "exploded");
  RESULTS(1);
}

// Refii.vortex_held(serial) -> { e1, .. } (handles, the vortex's own list order), or nil when no
// live vortex carries `serial` (refii_mod_vortex_held).
REFII_LUA_NATIVE(NativeVortexHeld) {
  uint32_t held[64];
  const int32_t count =
      refii_mod_vortex_held(lua.base, static_cast<uint32_t>(Lua::Clamp(lua.Number(1), 0.0, 4294967295.0)), held, 64);
  if (count < 0) {
    lua.PushNil();
    RESULTS(1);
    return;
  }
  lua.NewTable();
  for (int32_t i = 0; i < count && i < 64; ++i) {
    lua.PushNumber(refii_mod_handle(held[i]));
    lua.RawSetI(-2, i + 1);
  }
  RESULTS(1);
}

// Refii.spell_balance(caster, type, level) -> { status_seconds, status_spread, duration, swords }: one
// power of a spell as its Targeted class's own data read loads it for the caster's definitions, a
// field the class does not carry absent; nil for another type or a caster with no spell manager
// (refii_mod_spell_balance).
REFII_LUA_NATIVE(NativeSpellBalance) {
  refii_mod_cast_balance balance{};
  if (refii_mod_spell_balance(&lua.ctx, lua.base, lua.Entity(1), static_cast<int32_t>(lua.Number(2)),
                              static_cast<int32_t>(lua.Number(3)), &balance) == 0) {
    lua.PushNil();
    RESULTS(1);
    return;
  }
  lua.NewTable();
  if (balance.status_seconds >= 0.0f) {
    lua.PushNumber(balance.status_seconds);
    lua.SetField(-2, "status_seconds");
    lua.PushNumber(balance.status_spread);
    lua.SetField(-2, "status_spread");
  }
  if (balance.duration >= 0.0f) {
    lua.PushNumber(balance.duration);
    lua.SetField(-2, "duration");
  }
  if (balance.swords >= 0) {
    lua.PushNumber(balance.swords);
    lua.SetField(-2, "swords");
  }
  RESULTS(1);
}

// Refii.host_serial(serial) -> the online host's serial of this machine's spell `serial`, and
// Refii.local_serial(host_serial) -> this machine's spell for the host's; nil while the cast is not
// paired (refii_mod_host_serial / refii_mod_local_serial: itself on the host, alone, local co-op).
REFII_LUA_NATIVE(NativeHostSerial) {
  const uint32_t serial = refii_mod_host_serial(lua.base, static_cast<uint32_t>(Lua::Clamp(lua.Number(1), 0.0, 4294967295.0)));
  if (serial == 0) {
    lua.PushNil();
  } else {
    lua.PushNumber(serial);
  }
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeLocalSerial) {
  const uint32_t serial = refii_mod_local_serial(lua.base, static_cast<uint32_t>(Lua::Clamp(lua.Number(1), 0.0, 4294967295.0)));
  if (serial == 0) {
    lua.PushNil();
  } else {
    lua.PushNumber(serial);
  }
  RESULTS(1);
}

// Statuses: Refii.apply_status(e, kind, seconds [, level [, source]]) · clear_status(e, kind) ·
// has_status(e, kind); kinds 1 electrified 2 chaos 3 burning 4 vortex (the status_effect event's).
REFII_LUA_NATIVE(NativeApplyStatus) {
  const int32_t level = lua.Type(4) == kTypeNumber ? lua.Int(4) : 0;
  const uint32_t source = lua.Type(5) == kTypeNumber ? lua.Entity(5) : 0u;
  lua.PushBoolean(refii_mod_apply_status(&lua.ctx, lua.base, lua.Entity(1), lua.Int(2), static_cast<float>(lua.Number(3)),
                                         level, source) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeClearStatus) {
  lua.PushBoolean(refii_mod_clear_status(&lua.ctx, lua.base, lua.Entity(1), lua.Int(2)) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeHasStatus) {
  lua.PushBoolean(refii_mod_has_status(lua.base, lua.Entity(1), lua.Int(2)) != 0);
  RESULTS(1);
}

// Knockback and stagger: Refii.knockback(e, x, y, z [, force]) (force defaults to 1) · stagger(e).
REFII_LUA_NATIVE(NativeKnockback) {
  const float dir[3] = {static_cast<float>(lua.Number(2)), static_cast<float>(lua.Number(3)),
                        static_cast<float>(lua.Number(4))};
  const float force = lua.Type(5) == kTypeNumber ? static_cast<float>(lua.Number(5)) : 1.0f;
  lua.PushBoolean(refii_mod_knockback(&lua.ctx, lua.base, lua.Entity(1), dir, force) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeStagger) {
  lua.PushBoolean(refii_mod_stagger(&lua.ctx, lua.base, lua.Entity(1)) != 0);
  RESULTS(1);
}

// Sound: Refii.play_sound(cue, e [, tag]) · play_sound_at(cue, x, y, z [, tag]) ·
// stop_sound(e, tag [, fade_ms]) (e 0: the point emitters) · play_music(name) · stop_music(name).
REFII_LUA_NATIVE(NativePlaySound) {
  char cue[128];
  char tag[128];
  lua.String(1, cue, sizeof(cue));
  lua.String(3, tag, sizeof(tag));
  lua.PushBoolean(refii_mod_play_sound_on(&lua.ctx, lua.base, lua.Entity(2), cue, tag) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativePlaySoundAt) {
  char cue[128];
  char tag[128];
  lua.String(1, cue, sizeof(cue));
  lua.String(5, tag, sizeof(tag));
  const float xyz[3] = {static_cast<float>(lua.Number(2)), static_cast<float>(lua.Number(3)),
                        static_cast<float>(lua.Number(4))};
  lua.PushBoolean(refii_mod_play_sound_at(&lua.ctx, lua.base, xyz, cue, tag) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeStopSound) {
  char tag[128];
  lua.String(2, tag, sizeof(tag));
  const int32_t fade = lua.Type(3) == kTypeNumber ? lua.Int(3) : 0;
  const uint32_t entity = lua.Type(1) == kTypeNumber && lua.Number(1) != 0.0 ? lua.Entity(1) : 0u;
  lua.PushBoolean(refii_mod_stop_sound(&lua.ctx, lua.base, entity, tag, fade) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativePlayMusic) {
  char name[128];
  lua.String(1, name, sizeof(name));
  lua.PushBoolean(refii_mod_play_music(&lua.ctx, lua.base, name) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeStopMusic) {
  char name[128];
  lua.String(1, name, sizeof(name));
  lua.PushBoolean(refii_mod_stop_music(&lua.ctx, lua.base, name) != 0);
  RESULTS(1);
}

// Animation: Refii.play_animation(e, name [, loops [, blend_seconds [, speed]]]) ·
// stop_animation(e). loops defaults to 1, blend to the action's 0.1 s, speed to 1.
REFII_LUA_NATIVE(NativePlayAnimation) {
  char name[64];
  lua.String(2, name, sizeof(name));
  const int32_t loops = lua.Type(3) == kTypeNumber ? lua.Int(3) : 1;
  const float blend = lua.Type(4) == kTypeNumber ? static_cast<float>(lua.Number(4)) : 0.1f;
  const float speed = lua.Type(5) == kTypeNumber ? static_cast<float>(lua.Number(5)) : 1.0f;
  lua.PushBoolean(refii_mod_play_animation(&lua.ctx, lua.base, lua.Entity(1), name, loops, blend, speed) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeStopAnimation) {
  lua.PushBoolean(refii_mod_stop_animation(&lua.ctx, lua.base, lua.Entity(1)) != 0);
  RESULTS(1);
}

// Opinion and deeds: Refii.modify_opinion(villager, hero, axis, delta) · add_deed(villager, hero,
// deed) — deed a "0x…" string (Refii.hash of the Deed record's name).
REFII_LUA_NATIVE(NativeModifyOpinion) {
  lua.PushBoolean(refii_mod_modify_opinion(&lua.ctx, lua.base, lua.Entity(1), lua.Entity(2), lua.Int(3),
                                           static_cast<float>(lua.Number(4))) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeAddDeed) {
  lua.PushBoolean(refii_mod_add_deed(&lua.ctx, lua.base, lua.Entity(1), lua.Entity(2), lua.Hash(3)) != 0);
  RESULTS(1);
}

// NPC behaviour: Refii.npc_follow(e, target [, radius]) · npc_go_to(e, x, y, z) · npc_flee(e, from)
// · npc_attack(e, target) · npc_set_hostile(e, other, on) · npc_stop(e) — the quests' own orders
// (refii_mod_npc_*), run in the game VM. radius defaults to the quests' 2.
REFII_LUA_NATIVE(NativeNpcFollow) {
  const float radius = lua.Type(3) == kTypeNumber ? static_cast<float>(lua.Number(3)) : 2.0f;
  lua.PushBoolean(refii_mod_npc_follow(&lua.ctx, lua.base, lua.Entity(1), lua.Entity(2), radius) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeNpcGoTo) {
  const float xyz[3] = {static_cast<float>(lua.Number(2)), static_cast<float>(lua.Number(3)), static_cast<float>(lua.Number(4))};
  lua.PushBoolean(refii_mod_npc_go_to(&lua.ctx, lua.base, lua.Entity(1), xyz) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeNpcFlee) {
  lua.PushBoolean(refii_mod_npc_flee(&lua.ctx, lua.base, lua.Entity(1), lua.Entity(2)) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeNpcAttack) {
  lua.PushBoolean(refii_mod_npc_attack(&lua.ctx, lua.base, lua.Entity(1), lua.Entity(2)) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeNpcSetHostile) {
  const bool on = lua.Type(3) == kTypeNumber ? lua.Number(3) != 0.0 : lua.Boolean(3);
  lua.PushBoolean(refii_mod_npc_set_hostile(&lua.ctx, lua.base, lua.Entity(1), lua.Entity(2), on ? 1 : 0) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeNpcStop) {
  lua.PushBoolean(refii_mod_npc_stop(&lua.ctx, lua.base, lua.Entity(1)) != 0);
  RESULTS(1);
}

// Time and weather: Refii.set_time_of_day(hours) · time_of_day() -> hours ·
// set_weather(theme [, weight [, seconds]]) — the game clock's own setter (refii_mod_set_time_of_day)
// and the quests' environment-theme blend (refii_mod_set_weather); weight defaults to 1, seconds to 1.
REFII_LUA_NATIVE(NativeSetTimeOfDay) {
  lua.PushBoolean(refii_mod_set_time_of_day(&lua.ctx, lua.base, static_cast<float>(lua.Number(1))) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeTimeOfDay) {
  float hours = 0.0f;
  if (refii_mod_time_of_day(lua.base, &hours) != 0) {
    lua.PushNumber(hours);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeSetWeather) {
  char theme[64];
  lua.String(1, theme, sizeof(theme));
  const float weight = lua.Type(2) == kTypeNumber ? static_cast<float>(lua.Number(2)) : 1.0f;
  const float seconds = lua.Type(3) == kTypeNumber ? static_cast<float>(lua.Number(3)) : 1.0f;
  lua.PushBoolean(refii_mod_set_weather(&lua.ctx, lua.base, theme, weight, seconds) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeGold) {
  int32_t gold = 0;
  if (refii_mod_gold(lua.base, lua.Entity(1), &gold) != 0) {
    lua.PushNumber(gold);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeGiveGold) {
  lua.PushBoolean(refii_mod_give_gold(&lua.ctx, lua.base, lua.Entity(1), lua.Int(2)) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeItemCount) {
  lua.PushNumber(refii_mod_item_count(lua.base, lua.Entity(1), lua.Hash(2)));
  RESULTS(1);
}

// Refii.add_item(e, def [, n]) -> item, added: n of def (1 when nil; at most 100) through the
// game's own add (refii_mod_add_items); the last item's handle (nil for none) and how many were added.
REFII_LUA_NATIVE(NativeAddItem) {
  const int32_t count = lua.Type(3) == kTypeNumber ? lua.Int(3) : 1;
  uint32_t last = 0u;
  const int32_t added = refii_mod_add_items(&lua.ctx, lua.base, lua.Entity(1), lua.Hash(2), count, &last);
  lua.PushHandle(last);
  lua.PushNumber(added);
  RESULTS(2);
}

REFII_LUA_NATIVE(NativeExperience) {
  float amount = 0.0f;
  if (refii_mod_experience(lua.base, lua.Entity(1), lua.Int(2), &amount) != 0) {
    lua.PushNumber(amount);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeGiveExperience) {
  lua.PushBoolean(refii_mod_give_experience(&lua.ctx, lua.base, lua.Entity(1), lua.Int(2),
                                            static_cast<float>(lua.Number(3))) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeSpendExperience) {
  lua.PushBoolean(refii_mod_spend_experience(&lua.ctx, lua.base, lua.Entity(1), lua.Int(2),
                                             static_cast<float>(lua.Number(3))) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeAbilityLevel) {
  const int32_t level = refii_mod_ability_level(lua.base, lua.Entity(1), lua.Int(2));
  if (level >= 0) {
    lua.PushNumber(level);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

// Refii._cast_spell(caster, type, targeted, target, level, has_pos, px, py, pz, has_dir, dx, dy,
// dz [, tag [, damage_scale [, at_kind, at_entity, ax, ay, az]]]) -> spell, serial:
// refii_mod_cast_spell_with with the start position and facing when stated, the cast's tag and
// hit scale, and where it goes (refii_mod_cast_options; at_kind REFII_MOD_CAST_AT_*; the preamble's
// Refii.cast_spell unpacks its pos / dir / options tables, as play_fx does its options). The
// serial is read from the object the factory returned, so a Lua mod knows its own cast by the
// number its hits carry — the object's spell_created is raised inside this call and reaches Lua
// deferred, after the chunk. Nil, nil for no cast.
REFII_LUA_NATIVE(NativeCastSpell) {
  const bool targeted = lua.Type(3) == 1 ? lua.Boolean(3) : lua.Int(3) != 0;
  const float pos[3] = {static_cast<float>(lua.Number(7)), static_cast<float>(lua.Number(8)),
                        static_cast<float>(lua.Number(9))};
  const float dir[3] = {static_cast<float>(lua.Number(11)), static_cast<float>(lua.Number(12)),
                        static_cast<float>(lua.Number(13))};
  refii_mod_cast_options options{};
  options.tag = lua.Int(14);
  options.damage_scale = lua.Type(15) == kTypeNumber ? static_cast<float>(lua.Number(15)) : 1.0f;
  options.at_kind = lua.Int(16);
  options.at_entity = lua.Entity(17);
  options.at_point[0] = static_cast<float>(lua.Number(18));
  options.at_point[1] = static_cast<float>(lua.Number(19));
  options.at_point[2] = static_cast<float>(lua.Number(20));
  const uint32_t spell = refii_mod_cast_spell_with(&lua.ctx, lua.base, lua.Entity(1), lua.Int(2), targeted ? 1 : 0,
                                                   lua.Entity(4), lua.Int(5), lua.Int(6) != 0 ? pos : nullptr,
                                                   lua.Int(10) != 0 ? dir : nullptr, &options);
  lua.PushHandle(spell);
  const uint32_t serial = refii_mod_spell_serial(lua.base, spell);
  if (serial != 0) {
    lua.PushNumber(serial);
  } else {
    lua.PushNil();
  }
  RESULTS(2);
}

// Refii.spell_serial(spell) -> serial: the cast number a spell object's hits carry
// (refii_mod_spell_serial), or nil for no object. A spell handle is the object's address for the
// object's life and is never forgotten: the serial, not the handle, is a cast's identity.
REFII_LUA_NATIVE(NativeSpellSerial) {
  const uint32_t serial = refii_mod_spell_serial(lua.base, lua.Entity(1));
  if (serial != 0) {
    lua.PushNumber(serial);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeRelation) {
  lua.PushNumber(refii_mod_relation(&lua.ctx, lua.base, lua.Entity(1), lua.Entity(2)));
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeAlive) {
  lua.PushBoolean(refii_mod_entity_alive(lua.base, lua.Entity(1)) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeDefinition) {
  const uint32_t definition = refii_mod_entity_definition(lua.base, lua.Entity(1));
  if (definition != 0) {
    lua.PushHash(definition);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeName) {
  char name[128];
  if (refii_mod_entity_name(lua.base, lua.Entity(1), name, sizeof(name)) != 0) {
    lua.PushString(name);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

REFII_LUA_NATIVE(NativePlayerOf) {
  lua.PushNumber(refii_mod_player_of(lua.base, lua.Entity(1)));
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeLevelName) {
  char name[128];
  if (refii_mod_level_name(lua.base, name, sizeof(name)) != 0) {
    lua.PushString(name);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

REFII_LUA_NATIVE(NativePlayerHero) {
  lua.PushHandle(refii_mod_player_hero(lua.base, lua.Int(1)));
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeSplitScreen) {
  lua.PushBoolean(RefiiCoopSplitScreen() != 0);
  RESULTS(1);
}

// Refii.player_dog(p): player p's dog — hero one's is the game's own GetDog, player two's the
// co-op dog bound to them.
REFII_LUA_NATIVE(NativePlayerDog) {
  lua.PushHandle(RefiiCoopPlayerDog(lua.base, lua.Int(1)));
  RESULTS(1);
}

// Refii._set_partner_dog([dog]): records player two's dog; with none, clears the record (the
// core Lua's breed change, so the replacement is recorded by its first bind).
REFII_LUA_NATIVE(NativeSetPartnerDog) {
  RefiiCoopSetPartnerDog(lua.Type(1) == kTypeNumber ? lua.Entity(1) : 0u);
  RESULTS(0);
}

// Refii._two_is_hero(): whether player two brought a hero (a character from a save) rather than
// standing as the plain henchman — the one statement every hero-only feature of the core Lua keys on.
REFII_LUA_NATIVE(NativeTwoIsHero) {
  lua.PushBoolean(RefiiCoopGetOwnHero() != 0);
  RESULTS(1);
}

// Refii._job_worker(p) / Refii._estate_buyer(p): the core Lua's statements of who works the
// running job and who the real-estate screen about to open buys for (0 none, 1, 2).
// Core only: Refii._restore_group_minds() -> count. Every group-mind component in the world
// registers its group minds again, as the game's own Lua_Debug_ReloadAI does for component 42
// (TU1 0x82459F4C..0x8245A08C): the entities holding one come from the entity manager's own
// search (rex_lhEntityManager_CollectWithComponent, filter = the component id word, kind 7, out =
// a vector {_, begin, end, capacity}; the manager is *(*(*(*(game root)+12)+88)+4)+12) and each
// component's rex_CECGroupMind_AddGroupMinds calls GroupMindManager:AddGroupMindAttachedToEntity
// for every group-mind name it holds. A component restored from a save skips that at activation
// (CECGroupMind_vf11 tests +0x1C, set by vf8 for a restore) because the save's AISave brings the
// group minds back: a save without one leaves every guard, monk and quest NPC without its group.
REFII_LUA_NATIVE(NativeRestoreGroupMinds) {
  constexpr uint32_t kGroupMindComponent = 42u;
  uint8_t* base = lua.base;
  const auto step = [&](uint32_t at, uint32_t offset) -> uint32_t {
    return at >= 0x10000u && at < 0xC0000000u ? refii::mods::LoadU32(base, at + offset) : 0u;
  };
  const uint32_t root = refii::mods::LoadU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t manager = step(step(step(step(root, 0xCu), 0x58u), 0x4u), 0xCu);
  if (manager < 0x10000u || manager >= 0xC0000000u) {
    lua.PushNumber(0);
    RESULTS(1);
  }

  PPCContext call = refii::mods::Frame(lua.ctx);
  const uint32_t filter = call.r1.u32 + 0x80u;
  const uint32_t found = call.r1.u32 + 0x90u;
  refii::mods::StoreU32(base, filter, kGroupMindComponent);
  for (uint32_t i = 0; i < 16u; i += 4u) {
    refii::mods::StoreU32(base, found + i, 0u);
  }
  call.r3.u32 = manager;
  call.r4.u32 = filter;
  call.r5.u32 = 7u;
  call.r6.u32 = found;
  rex_lhEntityManager_CollectWithComponent(call, base);

  const uint32_t begin = refii::mods::LoadU32(base, found + 4u);
  const uint32_t end = refii::mods::LoadU32(base, found + 8u);
  int32_t restored = 0;
  if (begin != 0u && end >= begin && end - begin <= 4u * 65536u) {
    for (uint32_t at = begin; at < end; at += 4u) {
      const uint32_t component = refii::vfs::EntityComponent(base, refii::mods::LoadU32(base, at), kGroupMindComponent);
      if (component == 0u) {
        continue;
      }
      PPCContext add = refii::mods::Frame(lua.ctx);
      add.r3.u32 = component;
      rex_CECGroupMind_AddGroupMinds(add, base);
      ++restored;
    }
  }
  if (begin != 0u) {
    PPCContext release = refii::mods::Frame(lua.ctx);
    release.r3.u32 = begin;
    rex_lh_free(release, base);
  }
  lua.PushNumber(restored);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeJobWorker) {
  RefiiCoopSetJobWorker(lua.Int(1));
  RESULTS(0);
}

// Refii._partner_screen(on): "on screen" answers player two's view until turned off (nests).
REFII_LUA_NATIVE(NativePartnerScreen) {
  RefiiCoopPartnerScreen(lua.Boolean(1) ? 1 : 0);
  RESULTS(0);
}

// Refii._partner_player_icons(on): player two's camera's GUI.SetShouldDrawPlayerIcons, stated for player two's
// half (stated value 25: 1 off, 2 on; the core camera chunk's wrap, CoopHeroLua.cs) instead of the one HUD's.
REFII_LUA_NATIVE(NativePartnerPlayerIcons) {
  refii_bridge_stated_value(25u, lua.Boolean(1) ? 2u : 1u);
  RESULTS(0);
}

// Refii._camera_player_icons(on): true around a camera's own GUI.SetShouldDrawPlayerIcons (SACCamera:Activate /
// :Deactivate, the core camera chunk's wrap), so the native's send to the session's partner is skipped: a
// camera's switch is its own machine's (coop_player_icons.cpp RefiiPlayerIconsStayHere).
REFII_LUA_NATIVE(NativeCameraPlayerIcons) {
  RefiiPlayerIconsFromCamera(lua.Boolean(1) ? 1 : 0);
  RESULTS(0);
}

// Refii._partner_camera_update(): true while player two's camera manager updates in local co-op, its
// selection and its mode's frame update both (coop_hero_hooks.cpp RefiiCoopPartnerCameraUpdating).
REFII_LUA_NATIVE(NativePartnerCameraUpdate) {
  lua.PushBoolean(RefiiCoopPartnerCameraUpdating() != 0);
  RESULTS(1);
}

// Refii._partner_camera_manager(): which camera manager player two's is, a number that moves on at every
// Player built with one (a join, a re-join); 0 before the first (coop_hero_hooks.cpp
// RefiiCoopPartnerCameraGeneration).
REFII_LUA_NATIVE(NativePartnerCameraManager) {
  lua.PushNumber(static_cast<double>(RefiiCoopPartnerCameraGeneration()));
  RESULTS(1);
}

// Refii._estate_buyer(player) -> opens: before the sale sign's GUI.DisplayRealEstateScreen, the player the
// opener runs as. States the buyer the screen's binding takes (RefiiCoopEstateBuyer) as
// RefiiEstateBuyerOpens answers it (hero two on an online joiner, mod_events.cpp); false: no building
// for that buyer, the screen is not opened.
REFII_LUA_NATIVE(NativeEstateBuyer) {
  const int32_t stated = lua.Int(1);
  const int32_t buyer = RefiiEstateBuyerOpens(&lua.ctx, lua.base, stated);
  RefiiCoopSetEstateBuyer(buyer);
  REXLOG_INFO("Refii estate: GUI.DisplayRealEstateScreen asked as player {}, buyer {}, frame {}", stated, buyer,
              refii::mods::LoadU32(lua.base, refii::guest::Address(0x83319650u)));
  lua.PushBoolean(buyer != 0);
  RESULTS(1);
}

uint32_t EntityArgument(Lua& lua, int32_t index);

// Core co-op only, an online joiner: Refii._estate_look(sign) tells the host this machine is about to
// open the sale sign `sign` (the `look` intent, mod_events.cpp ReplayEstateIntent), so the host
// states its building's book here (coop_estate_statements.cpp); Refii._estate_ready(sign, frames) —
// the host's statement for that building has arrived; `frames` is how many world frames the caller
// still waits, and on the last one an unanswered sign is logged. `sign` is the script's own
// ItemBeingUsed, a game Entity userdata (EntityArgument; a handle number is taken too).
REFII_LUA_NATIVE(NativeEstateLook) {
  const uint32_t sign = EntityArgument(lua, 1);
  const uint64_t uid = sign != 0u ? (static_cast<uint64_t>(refii::mods::LoadU32(lua.base, sign + 20u)) << 32) |
                                        refii::mods::LoadU32(lua.base, sign + 24u)
                                  : 0u;
  if (uid == 0u) {
    REXLOG_WARN("Refii estate: the joiner's sale sign is no live entity here; the host is not asked for its book");
    RESULTS(0);
    return;
  }
  char text[64];
  std::snprintf(text, sizeof(text), "look:%016llX:0", static_cast<unsigned long long>(uid));
  refii_bridge_estate_intent_post(text);
  REXLOG_INFO("Refii estate: the joiner asks the host for the book of sale sign {:016X}, frame {}", uid,
              refii::mods::LoadU32(lua.base, refii::guest::Address(0x83319650u)));
  RESULTS(0);
}

REFII_LUA_NATIVE(NativeEstateReady) {
  const uint32_t sign = EntityArgument(lua, 1);
  const int32_t frames = lua.Int(2);
  const bool ready = sign != 0u && refii::vfs::EstateStatedSign(lua.ctx, lua.base, sign);
  if (ready || frames == 1) {
    const uint64_t uid = sign != 0u ? (static_cast<uint64_t>(refii::mods::LoadU32(lua.base, sign + 20u)) << 32) |
                                          refii::mods::LoadU32(lua.base, sign + 24u)
                                    : 0u;
    if (ready) {
      REXLOG_INFO("Refii estate: the host's book of sale sign {:016X} is here; the page opens on it", uid);
    } else {
      REXLOG_WARN("Refii estate: no book from the host for sale sign {:016X} in time; the page is not opened", uid);
    }
  }
  lua.PushBoolean(ready);
  RESULTS(1);
}

namespace {
constexpr int32_t kTypeUserdata = 7;

/// The record hash of the inventory item a Lua InventoryItem userdata holds. Its body holds a POINTER
/// to the item's handle: the GUI.DisplayReceivedItem binding passes `*body` as the handle
/// (`bl lua_touserdata` / `lwz r3,0(r3)` / `bctrl`, TU1 0x8235C1E0..0x8235C1EC, disc 0x823B1E68..
/// 0x823B1E74), and its core `sub_82304EF8` takes the item from the handle's first word (TU1 0x82304F0C
/// `lwz r11,0(r4)`). The record hash is item+4 (`sub_821DA348` stores it there, with the GDB entry at
/// +8; `CECInventory_FindItemByRecordHash` compares it, 0x824F1CE4), the count item+12. 0 for anything
/// else, an Entity userdata included.
uint32_t ItemRecord(Lua& lua, int32_t index) {
  if (lua.Type(index) != kTypeUserdata) {
    return 0u;
  }
  PPCContext check = lua.Work();
  check.r4.s64 = index;
  check.r5.u32 = refii::guest::Address(kEntityTypeName);
  rex_lhLua_IsFableType(check, lua.base);
  if (check.r3.u32 != 0) {
    return 0u;
  }
  PPCContext data = lua.Work();
  data.r4.s64 = index;
  rex_lua_touserdata(data, lua.base);
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  const uint32_t body = data.r3.u32;
  const uint32_t handle = plausible(body) ? refii::mods::LoadU32(lua.base, body) : 0u;
  const uint32_t item = plausible(handle) ? refii::mods::LoadU32(lua.base, handle) : 0u;
  if (!plausible(item)) {
    return 0u;
  }
  const uint32_t count = refii::mods::LoadU32(lua.base, item + 12u);
  return count >= 1u && count <= 0xFFFFu ? refii::mods::LoadU32(lua.base, item + 4u) : 0u;
}
}  // namespace

// Core co-op only. Refii._item_record(item) — the record hash ("0x…") of a game InventoryItem
// userdata, or nil (ItemRecord above).
REFII_LUA_NATIVE(NativeItemRecord) {
  const uint32_t hash = ItemRecord(lua, 1);
  if (hash != 0u) {
    lua.PushHash(hash);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

// Core co-op only, an online joiner. Refii._received_item(hash) opens the received card on this
// player's own item of that record (a host run's GUI.DisplayReceivedItem); true when opened.
REFII_LUA_NATIVE(NativeReceivedItem) {
  lua.PushBoolean(refii_household_received_item(&lua.ctx, lua.base, lua.Hash(1)) != 0);
  RESULTS(1);
}

// Core co-op only, an online joiner. Refii._use_carried(item, class [, target uid]) — the host uses
// this item of hero two's as the script `class` for its hero two, the hero's target as given
// (`carry|<hash>|<class>|<uid>`, mod_events.cpp). True when sent.
REFII_LUA_NATIVE(NativeUseCarried) {
  const uint32_t hash = ItemRecord(lua, 1);
  char name[72];
  lua.String(2, name, sizeof(name));
  char aimed[32];
  lua.String(3, aimed, sizeof(aimed));
  const unsigned long long target = std::strtoull(aimed, nullptr, 16);
  const bool ok = refii_mod_coop_mode(lua.base) == 3 && hash != 0u && refii::coop::UseClassName(name);
  if (ok) {
    char text[120];
    std::snprintf(text, sizeof(text), "carry|%08X|%s|%016llX", hash, name, target);
    refii_bridge_estate_intent_post(text);
  }
  lua.PushBoolean(ok);
  RESULTS(1);
}

// Core co-op only, the host. Refii._use_stated_target() — the target ("0x…") the joiner's hero had
// when it used the carried item whose run is being made now, or nil.
REFII_LUA_NATIVE(NativeUseStatedTarget) {
  char text[40];
  if (refii_household_carry_target(text, sizeof(text)) != 0) {
    lua.PushString(text);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

// Core co-op only, an online joiner, once a world frame. Refii._use_target_state() states this hero's
// use target to the host when it changes (mod_events.cpp refii_household_state_use_target).
REFII_LUA_NATIVE(NativeUseTargetState) {
  refii_household_state_use_target(&lua.ctx, lua.base);
  RESULTS(0);
}

// Core co-op only. Refii._dog_name(dog) — the name its player gave the dog: its naming component's
// (comp20) LHString at +52 when that holds text (what the co-op kit samples, guest_tick_pump.cpp), or nil.
REFII_LUA_NATIVE(NativeDogName) {
  const uint32_t naming = refii_mod_component(lua.base, lua.Entity(1), 20u);
  const uint32_t custom = naming != 0u ? refii::mods::LoadU32(lua.base, naming + 52u) : 0u;
  std::string name;
  if (custom >= 0x10000u && custom < 0xC0000000u && refii::mods::LoadU32(lua.base, custom + 4u) != 0u) {
    name = refii::vfs::ReadStatedWideLhString(lua.base, naming + 52u);
  }
  if (name.empty() || name.size() > 60u) {
    lua.PushNil();
  } else {
    lua.PushString(name.c_str());
  }
  RESULTS(1);
}

namespace {
/// A creature's scars: its morph component's (comp116) vector of 12-byte entries at +24..+28, which
/// `Morph.RemoveRandomScar` (TU1 0x825F32B0 / disc 0x8248B3E8) picks from and erases.
bool Scars(uint8_t* base, uint32_t entity, uint32_t* morph, uint32_t* first, uint32_t* count) {
  *morph = refii_mod_component(base, entity, 116u);
  if (*morph == 0u) {
    return false;
  }
  *first = refii::mods::LoadU32(base, *morph + 24u);
  const uint32_t last = refii::mods::LoadU32(base, *morph + 28u);
  if (last < *first || (last - *first) % 12u != 0u || (last - *first) > 12u * 256u) {
    return false;
  }
  *count = (last - *first) / 12u;
  return true;
}
}  // namespace

// Core co-op only. Refii._scars(e) — the scars as text, one "a.b.c" (the entry's three words, hex)
// per scar joined by ",", or nil without a morph component.
REFII_LUA_NATIVE(NativeScars) {
  uint32_t morph = 0u, first = 0u, count = 0u;
  if (!Scars(lua.base, lua.Entity(1), &morph, &first, &count)) {
    lua.PushNil();
    RESULTS(1);
    return;
  }
  std::string text;
  for (uint32_t i = 0; i < count && i < 16u; ++i) {
    char entry[32];
    std::snprintf(entry, sizeof(entry), "%s%x.%x.%x", i == 0u ? "" : ",", refii::mods::LoadU32(lua.base, first + 12u * i),
                  refii::mods::LoadU32(lua.base, first + 12u * i + 4u), refii::mods::LoadU32(lua.base, first + 12u * i + 8u));
    text += entry;
  }
  lua.PushString(text.c_str());
  RESULTS(1);
}

// Core co-op only, an online joiner. Refii._scar_remove(e, index) — the host's scar removal, the
// scar it chose (0-based), done here as `Morph.RemoveRandomScar` does it after its pick: the entries
// after it moved down one, the end back 12 bytes, the dirty byte +16 set, then its refresh
// `sub_825F2518(comp116)` (its erase TU1 0x825F3320..0x825F3384; disc `sub_8248A650`). True when removed.
REFII_LUA_NATIVE(NativeScarRemove) {
  uint32_t morph = 0u, first = 0u, count = 0u;
  const int32_t index = lua.Int(2);
  if (refii_mod_coop_mode(lua.base) != 3 || !Scars(lua.base, lua.Entity(1), &morph, &first, &count) || index < 0 ||
      static_cast<uint32_t>(index) >= count) {
    lua.PushBoolean(false);
    RESULTS(1);
    return;
  }
  for (uint32_t at = first + 12u * static_cast<uint32_t>(index); at + 12u < first + 12u * count; at += 12u) {
    for (uint32_t w = 0; w < 12u; w += 4u) {
      refii::mods::StoreU32(lua.base, at + w, refii::mods::LoadU32(lua.base, at + 12u + w));
    }
  }
  refii::mods::StoreU32(lua.base, morph + 28u, first + 12u * (count - 1u));
  lua.base[morph + 16u] = 1u;
  PPCContext work = refii::mods::Frame(lua.ctx);
  work.r3.u64 = morph;
  lua.PushBoolean(refii::mods::CallGuest(work, lua.base, refii::guest::Address(0x825F2518u)));
  RESULTS(1);
}

// Core co-op only, an online joiner: Refii._estate_blocked() — the host's copy of this machine's hero holds
// CUTSCENE_RULE_NO_PROPERTY_SCREEN (coop_estate_statements.cpp), the rule the sale sign's script asks.
REFII_LUA_NATIVE(NativeEstateBlocked) {
  lua.PushBoolean(refii::vfs::EstateBlockedForTwo());
  RESULTS(1);
}

// Core co-op only: Refii._household(action, uid, value) sends player two's household action to the
// co-op peer (`<action>:<uid hex>:<value>`, the pump's Estate: action there; mod_events.cpp). `uid`
// is Refii.uid's text ("0x…").
REFII_LUA_NATIVE(NativeHousehold) {
  char action[24];
  char uid[32];
  lua.String(1, action, sizeof(action));
  lua.String(2, uid, sizeof(uid));
  const unsigned long long value = std::strtoull(uid, nullptr, 16);
  char text[96];
  std::snprintf(text, sizeof(text), "%s:%016llX:%d", action, value, lua.Int(3));
  if (action[0] != 0) {
    refii_bridge_estate_intent_post(text);
  }
  RESULTS(0);
}

// Core co-op only: Refii._by_uid(uid) — the handle of the living entity with that UniqueID
// (Refii.uid's "0x…" text), or nil.
REFII_LUA_NATIVE(NativeEndRedecorating) {
  RefiiEndRedecorating(&lua.ctx, lua.base, lua.Entity(1));
  RESULTS(0);
}

REFII_LUA_NATIVE(NativeByUid) {
  char uid[32];
  lua.String(1, uid, sizeof(uid));
  lua.PushHandle(refii_household_resolve_uid(&lua.ctx, lua.base, std::strtoull(uid, nullptr, 16)));
  RESULTS(1);
}

// Core co-op only: Refii._followers_two(i) — player two's i-th follower as the host states it
// (household_persist.cpp publishes them; online the joiner's own copy has none): its handle and
// flags (1 engaged to player two, 2 following for sex, 4 married to player two), or nil.
REFII_LUA_NATIVE(NativeFollowersTwo) {
  uint32_t entity = 0u;
  uint32_t flags = 0u;
  if (refii_household_follower_two(&lua.ctx, lua.base, lua.Int(1), &entity, &flags) == 0) {
    lua.PushNil();
    RESULTS(1);
    return;
  }
  lua.PushHandle(entity);
  lua.PushNumber(static_cast<double>(flags));
  RESULTS(2);
}

// Core co-op only: Refii._household_context(on) — the core Lua's silent bed thread for player two
// runs (on 1, off 0, nested): player two's stats it counts go to the joiner (mod_events.cpp).
REFII_LUA_NATIVE(NativeHouseholdContext) {
  refii_household_context(lua.Boolean(1) ? 1 : 0);
  RESULTS(0);
}

// Core co-op only: Refii._forward_speech(e, seconds) — the lines `e` speaks on the host in the next
// seconds go to the online joiner too (the bed scene run for its hero two; mod_events.cpp).
REFII_LUA_NATIVE(NativeForwardSpeech) {
  refii_household_forward_speech(lua.base, lua.Entity(1), static_cast<float>(lua.Number(2)));
  RESULTS(0);
}


REFII_LUA_NATIVE(NativeLocalHero) {
  lua.PushHandle(refii_mod_local_hero(lua.base));
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeMenuPlayer) {
  lua.PushNumber(refii_mod_menu_player(lua.base));
  RESULTS(1);
}

// PLAYER STATE (core/refii_mod.h refii_mod_player_*): per-player values, the same on both co-op
// machines, the saved ones kept with the hero's own save.
REFII_LUA_NATIVE(NativePlayerValue) {
  char key[128];
  lua.String(2, key, sizeof(key));
  float value = 0.0f;
  if (key[0] != 0 && refii_mod_player_get(lua.Int(1), key, &value) != 0) {
    lua.PushNumber(value);
  } else if (lua.Type(3) == kTypeNumber) {
    lua.PushNumber(lua.Number(3));
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

REFII_LUA_NATIVE(NativePlayerSet) {
  char key[128];
  lua.String(2, key, sizeof(key));
  if (key[0] != 0) {
    refii_mod_player_set(lua.Int(1), key, static_cast<float>(lua.Number(3)));
  }
  RESULTS(0);
}

REFII_LUA_NATIVE(NativePlayerAdd) {
  char key[128];
  lua.String(2, key, sizeof(key));
  if (key[0] != 0) {
    refii_mod_player_add(lua.Int(1), key, static_cast<float>(lua.Number(3)), lua.Boolean(4) ? 1 : 0);
  }
  RESULTS(0);
}

REFII_LUA_NATIVE(NativePlayerSave) {
  char key[128];
  lua.String(2, key, sizeof(key));
  if (key[0] != 0) {
    refii_mod_player_save(lua.Int(1), key, static_cast<float>(lua.Number(3)));
  }
  RESULTS(0);
}

// Typed values in the same store (refii_mod_player_text / _hash): a text kept exactly, a hash as
// its "0x%08x" text — a record hash survives where a float would not.
REFII_LUA_NATIVE(NativePlayerValueText) {
  char key[128];
  lua.String(2, key, sizeof(key));
  char text[REFII_MOD_PLAYER_TEXT_MAX + 1];
  if (key[0] != 0 && refii_mod_player_text(lua.Int(1), key, text, sizeof(text)) >= 0) {
    lua.PushLongString(text);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

REFII_LUA_NATIVE(NativePlayerSetText) {
  char key[128];
  lua.String(2, key, sizeof(key));
  const std::string text = lua.LongString(3, REFII_MOD_PLAYER_TEXT_MAX);
  if (key[0] != 0) {
    refii_mod_player_set_text(lua.Int(1), key, text.c_str());
  }
  RESULTS(0);
}

REFII_LUA_NATIVE(NativePlayerSaveText) {
  char key[128];
  lua.String(2, key, sizeof(key));
  const std::string text = lua.LongString(3, REFII_MOD_PLAYER_TEXT_MAX);
  if (key[0] != 0) {
    refii_mod_player_save_text(lua.Int(1), key, text.c_str());
  }
  RESULTS(0);
}

// ACHIEVEMENTS PER CHARACTER, as the machine that plays the hero states them (core/achievement_statement.h):
// the statement in player p's store, the same text Godot's RefiiHost twins read, so both surfaces answer
// alike — and on both co-op machines, the partner's hero included.
static refii::achievements::Statement AchievementStatement(int32_t player) {
  char text[REFII_MOD_PLAYER_TEXT_MAX + 1];
  if ((player != 1 && player != 2) ||
      refii_mod_player_text(player, refii::achievements::kStatementKey, text, sizeof(text)) <= 0) {
    return {};
  }
  return refii::achievements::ParseStatement(text);
}

// Refii.achievement_unlocked(p, id): whether the hero player p plays has unlocked achievement id.
REFII_LUA_NATIVE(NativeAchievementUnlocked) {
  const refii::achievements::Statement statement = AchievementStatement(lua.Int(1));
  const int32_t id = lua.Int(2);
  lua.PushBoolean(statement.valid && id > 0 && refii::achievements::Holds(statement.hero, static_cast<uint32_t>(id)));
  RESULTS(1);
}

// Refii.achievements(p): the ids the hero player p plays has unlocked, ascending, "id,id,…" ("" for none).
REFII_LUA_NATIVE(NativeAchievements) {
  const refii::achievements::Statement statement = AchievementStatement(lua.Int(1));
  lua.PushString(statement.valid ? refii::achievements::IdList(statement.hero).c_str() : "");
  RESULTS(1);
}

// Refii.character_id(p): the character id of the hero player p plays (the one its saves carry in
// refii_save.json), "" before the game bound one or when p plays no hero.
REFII_LUA_NATIVE(NativeCharacterId) {
  const refii::achievements::Statement statement = AchievementStatement(lua.Int(1));
  lua.PushString(statement.valid ? statement.character.c_str() : "");
  RESULTS(1);
}

REFII_LUA_NATIVE(NativePlayerValueHash) {
  char key[128];
  lua.String(2, key, sizeof(key));
  uint32_t hash = 0;
  if (key[0] != 0 && refii_mod_player_hash(lua.Int(1), key, &hash) != 0) {
    lua.PushHash(hash);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

REFII_LUA_NATIVE(NativePlayerSaveHash) {
  char key[128];
  lua.String(2, key, sizeof(key));
  if (key[0] != 0) {
    refii_mod_player_save_hash(lua.Int(1), key, lua.Hash(3));
  }
  RESULTS(0);
}

REFII_LUA_NATIVE(NativePlayerHere) {
  lua.PushBoolean(refii_mod_player_here(lua.Int(1)) != 0);
  RESULTS(1);
}

// Refii.attack_mode(p): the keyboard's attack button mode for p (1 melee, 2 ranged, 3 magic), 0 none.
REFII_LUA_NATIVE(NativeAttackMode) {
  lua.PushNumber(refii_mod_attack_mode(lua.Int(1)));
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeEventReplica) {
  lua.PushBoolean(refii_mod_event_replica() != 0);
  RESULTS(1);
}

// Core co-op only: a script asks the partner (kind, asker 1|2, detail) and polls the answer.
extern "C" int32_t RefiiCoopKeyTaken(uint32_t entity, int32_t player);

// Core co-op only: whether player p has taken the per-player collectable e already (1) or not (0).
REFII_LUA_NATIVE(NativeKeyTaken) {
  lua.PushNumber(RefiiCoopKeyTaken(lua.Entity(1), lua.Int(2)));
  RESULTS(1);
}

extern "C" int32_t refii_mod_raise_partner_consent(PPCContext* ctx, uint8_t* base, uint32_t ticket, int32_t kind,
                                                   int32_t asker, int32_t detail);

/// A new question: the PartnerConsent event may answer it for the partner, in which case it is
/// answered before anyone is asked.
void ConsentAsked(PPCContext& ctx, uint8_t* base, uint32_t ticket, int32_t kind, int32_t asker, int32_t detail) {
  const int32_t answer = refii_mod_raise_partner_consent(&ctx, base, ticket, kind, asker, detail);
  if (answer >= 0) {
    refii_bridge_consent_give(ticket, answer);
  }
}

REFII_LUA_NATIVE(NativeConsentAsk) {
  const int32_t kind = lua.Int(1);
  const int32_t asker = lua.Int(2);
  const int32_t detail = lua.Int(3);
  const uint32_t ticket = refii_bridge_consent_ask(kind, asker, detail);
  ConsentAsked(lua.ctx, lua.base, ticket, kind, asker, detail);
  lua.PushNumber(static_cast<double>(ticket));
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeConsentAnswer) {
  lua.PushNumber(refii_bridge_consent_answer(static_cast<uint32_t>(lua.Int(1))));
  RESULTS(1);
}

// Refii.ask_partner(asker, question): a mod's own question for the other player (a box on their
// screen, or the peer's online); a ticket for Refii.partner_answer. Unanswered in 30 s is a no.
REFII_LUA_NATIVE(NativeAskPartner) {
  char text[256];
  lua.String(2, text, sizeof(text));
  const int32_t asker = lua.Int(1);
  if (text[0] == 0 || (asker != 1 && asker != 2)) {
    lua.PushNil();
  } else {
    const uint32_t ticket = refii_bridge_consent_ask_text(asker, text);
    ConsentAsked(lua.ctx, lua.base, ticket, 0, asker, 0);
    lua.PushNumber(static_cast<double>(ticket));
  }
  RESULTS(1);
}

// Refii.partner_answer(ticket): nil while pending, then true or false.
REFII_LUA_NATIVE(NativePartnerAnswer) {
  const int32_t answer = refii_bridge_consent_answer(static_cast<uint32_t>(lua.Int(1)));
  if (answer < 0) {
    lua.PushNil();
  } else {
    lua.PushBoolean(answer == 1);
  }
  RESULTS(1);
}

// Refii.key_taken(e, p): whether player p has taken the per-player collectable e (a silver key).
REFII_LUA_NATIVE(NativeKeyTakenPublic) {
  lua.PushBoolean(RefiiCoopKeyTaken(lua.Entity(1), lua.Int(2)) != 0);
  RESULTS(1);
}

// Refii.opinion(villager, p) -> love, fear, attractiveness, morality, respect: how the villager
// feels about player p's hero; nil when it holds no opinions.
REFII_LUA_NATIVE(NativeOpinion) {
  float axes[5] = {};
  if (refii_mod_opinion(lua.base, lua.Entity(1), lua.Int(2), axes) == 0) {
    lua.PushNil();
    RESULTS(1);
  } else {
    for (const float axis : axes) {
      lua.PushNumber(axis);
    }
    RESULTS(5);
  }
}

extern "C" int32_t refii_mod_opinion_enter(PPCContext* ctx, uint8_t* base, uint32_t villager, uint32_t hero);
extern "C" void refii_mod_opinion_leave(void);

// Core co-op only: Refii._opinion_enter(villager [, hero]) runs what follows as the villager's
// opinion of hero two when it concerns them (the named hero, else the nearer); 1 when entered,
// each 1 matched by Refii._opinion_leave().
REFII_LUA_NATIVE(NativeOpinionEnter) {
  const uint32_t hero = lua.Type(2) == kTypeNumber ? lua.Entity(2) : 0u;
  lua.PushNumber(refii_mod_opinion_enter(&lua.ctx, lua.base, lua.Entity(1), hero));
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeOpinionLeave) {
  refii_mod_opinion_leave();
  RESULTS(0);
}

// Core co-op only: Refii._route_post(line) / Refii._route_take() -> line or nil — the route, the
// ordered and acknowledged co-op channel the core Lua's calls for the peer's player and their
// answers travel on (core/event_relay.h ps.r; the line is the Lua's own text).
REFII_LUA_NATIVE(NativeRoutePost) {
  char text[768];
  lua.String(1, text, sizeof(text));
  if (text[0] != 0) {
    refii_bridge_route_post(text);
  }
  RESULTS(0);
}

REFII_LUA_NATIVE(NativeRouteTake) {
  char text[768];
  if (refii_bridge_route_take(text, sizeof(text)) != 0) {
    lua.PushLongString(text);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

// Core co-op only: Refii._ui_seat(p) before a guest call that builds a box for player p (1 or 2),
// Refii._ui_seat(0) after it: every box that call queued is stated as p's (display_box_hooks.cpp),
// so on split screen it opens in p's half on p's pad. The call between must not yield.
REFII_LUA_NATIVE(NativeUiSeat) {
  const int32_t player = lua.Int(1);
  if (player == 1 || player == 2) {
    refii::vfs::BoxSeatEnter(lua.base, player);
  } else {
    refii::vfs::BoxSeatLeave(lua.base);
  }
  RESULTS(0);
}

// Refii.forgive_crimes(villager, p) -> n: the crimes of player p's hero the villager knows of are
// forgiven (the other hero's stand); how many. Refii.discovered_crime(villager, p): whether the
// villager has discovered a crime of player p's. Refii.perceived_renown(villager, p): the renown the
// villager credits player p's hero with, or nil (refii_mod_forgive_crimes / _discovered_crime /
// _perceived_renown: the shipped Villager natives name no hero).
REFII_LUA_NATIVE(NativeForgiveCrimes) {
  lua.PushNumber(refii_mod_forgive_crimes(&lua.ctx, lua.base, lua.Entity(1), refii_mod_player_hero(lua.base, lua.Int(2))));
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeDiscoveredCrime) {
  lua.PushBoolean(
      refii_mod_discovered_crime(&lua.ctx, lua.base, lua.Entity(1), refii_mod_player_hero(lua.base, lua.Int(2))) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativePerceivedRenown) {
  int32_t renown = 0;
  if (refii_mod_perceived_renown(&lua.ctx, lua.base, lua.Entity(1), refii_mod_player_hero(lua.base, lua.Int(2)),
                                 &renown) != 0) {
    lua.PushNumber(renown);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

// Refii.uid(e): the entity's own 64-bit UniqueID (+0x14 high, +0x18 low) as "0x…": the same on
// both co-op machines and across saves, unlike a handle; nil for no entity.
REFII_LUA_NATIVE(NativeUid) {
  const uint32_t entity = lua.Entity(1);
  if (entity == 0u) {
    lua.PushNil();
  } else {
    char text[24];
    std::snprintf(text, sizeof(text), "0x%08x%08x", refii::mods::LoadU32(lua.base, entity + 0x14u),
                  refii::mods::LoadU32(lua.base, entity + 0x18u));
    lua.PushString(text);
  }
  RESULTS(1);
}

// Core co-op only: Refii._chest_counted(chest) — after Chest.SnapToClosed re-closes a chest for the
// partner, its "has been opened" byte (component 40 +0x0F, which SnapToClosed clears) is put back,
// so the world map's chest count (`sub_825949E0`, gated on it) counts the chest once.
REFII_LUA_NATIVE(NativeChestCounted) {
  const uint32_t chest = refii_mod_component(lua.base, lua.Entity(1), 40u);
  if (chest != 0u) {
    refii::mods::StoreU8(lua.base, chest + 0x0Fu, 1u);
  }
  RESULTS(0);
}

// Core co-op only: Refii._chest_money(chest) — puts a chest's gold back as its definition states
// it: the money component (81) re-reads "Money" from its own definition record, the read it made
// when the chest was created (`sub_8242E2A8(comp, entry)` → `sub_823AD468(entry, "Money", comp+12)`;
// the entry is the component's record, `sub_821DA3B0(&h, comp)` then `sub_821B24F8(&h)`).
REFII_LUA_NATIVE(NativeChestMoney) {
  const uint32_t money = refii_mod_component(lua.base, lua.Entity(1), 81u);
  if (money != 0u) {
    PPCContext work = refii::mods::Frame(lua.ctx);
    const uint32_t handle = (work.r1.u32 + 0x80u) & ~0xFu;
    refii::mods::StoreU32(lua.base, handle, 0u);
    refii::mods::StoreU32(lua.base, handle + 4u, 0u);
    work.r3.u64 = handle;
    work.r4.u64 = money;
    if (refii::mods::CallGuest(work, lua.base, refii::guest::Address(0x821DA3B0u))) {
      work = refii::mods::Frame(lua.ctx);
      work.r3.u64 = handle;
      if (refii::mods::CallGuest(work, lua.base, refii::guest::Address(0x821B24F8u)) && work.r3.u32 >= 0x10000u) {
        const uint32_t entry = work.r3.u32;
        work = refii::mods::Frame(lua.ctx);
        work.r3.u64 = money;
        work.r4.u64 = entry;
        refii::mods::CallGuest(work, lua.base, refii::guest::Address(0x8242E2A8u));
      }
    }
  }
  RESULTS(0);
}

// Refii.coop_mode(): 0 alone, 1 local co-op, 2 online hosting, 3 online joined.
REFII_LUA_NATIVE(NativeCoopMode) {
  lua.PushNumber(refii_mod_coop_mode(lua.base));
  RESULTS(1);
}

// Refii.world_here(): whether this machine runs the shared world (alone, local co-op, online host).
REFII_LUA_NATIVE(NativeWorldHere) {
  lua.PushBoolean(refii_mod_world_here(lua.base) != 0);
  RESULTS(1);
}

// Refii.focus(p): the living entity player p's hero is focused on, or nil.
REFII_LUA_NATIVE(NativeFocus) {
  lua.PushHandle(refii_mod_focus(lua.base, lua.Int(1)));
  RESULTS(1);
}

// Refii.wheel_switches_target(e [, on]): e's target lock switches by the mouse wheel — with e
// alone for this tick (call it each tick the lock holds; refii_mod_wheel_switches_target), with on
// (true / false) STICKY until off or the lock goes (refii_mod_wheel_switches_target_sticky).
REFII_LUA_NATIVE(NativeWheelSwitchesTarget) {
  const int32_t kind = lua.Type(2);
  if (kind == 1 || kind == kTypeNumber) {
    const bool on = kind == 1 ? lua.Boolean(2) : lua.Int(2) != 0;
    refii_mod_wheel_switches_target_sticky(&lua.ctx, lua.base, lua.Entity(1), on ? 1 : 0);
  } else {
    refii_mod_wheel_switches_target(lua.base, lua.Entity(1));
  }
  RESULTS(0);
}

REFII_LUA_NATIVE(NativeError) {
  char text[512];
  lua.String(1, text, sizeof(text));
  if (text[0] != 0) {
    refii_bridge_mod_error_post(text, std::strlen(text));
    REXLOG_WARN("[mod lua] {}", text);
  }
  RESULTS(0);
}

// Refii.entity(handle): the game's own Entity userdata for a handle — what the shipped scripts
// and the game's natives take (Entity:GetName(), CreatureGenerator...) — pushed by the game's push
// (an interned, ref-counted script reference holding a weak pointer and the entity's id), or nil.
REFII_LUA_NATIVE(NativeEntity) {
  const uint32_t entity = lua.Entity(1);
  if (entity == 0 || refii_mod_entity_alive(lua.base, entity) == 0) {
    lua.PushNil();
  } else {
    PPCContext w = lua.Work();
    w.r4.u32 = entity;
    rex_lhLua_PushEntity(w, lua.base);
  }
  RESULTS(1);
}

/// The entity at argument `index`: a handle number, or a game Entity userdata (any the shipped
/// scripts or natives gave) resolved through its script reference; 0 for a dead entity or anything
/// else.
uint32_t EntityArgument(Lua& lua, int32_t index) {
  if (lua.Type(index) == kTypeNumber) {
    return lua.Entity(index);
  }
  PPCContext check = lua.Work();
  check.r4.s64 = index;
  check.r5.u32 = refii::guest::Address(kEntityTypeName);
  rex_lhLua_IsFableType(check, lua.base);
  if (check.r3.u32 == 0) {
    return 0u;
  }
  PPCContext data = lua.Work();
  data.r4.s64 = index;
  rex_lua_touserdata(data, lua.base);
  const uint32_t body = data.r3.u32;
  const uint32_t ref = body >= 0x10000u && body < 0xC0000000u ? refii::mods::LoadU32(lua.base, body) : 0u;
  if (ref < 0x10000u || ref >= 0xC0000000u) {
    return 0u;
  }
  PPCContext resolve = lua.ctx;
  resolve.r3.u32 = ref;
  rex_lhScriptEntityRef_Resolve(resolve, lua.base);
  return resolve.r3.u32;
}

// Refii.handle(x): the handle for a game Entity userdata (any the shipped scripts or natives gave),
// a number handed back as it is, or nil for a dead entity or anything else.
REFII_LUA_NATIVE(NativeHandle) {
  if (lua.Type(1) == kTypeNumber) {
    lua.PushNumber(lua.Number(1));
    RESULTS(1);
    return;
  }
  lua.PushHandle(EntityArgument(lua, 1));
  RESULTS(1);
}

// Refii.player_for(e): 1 or 2, the player entity `e` deals with (refii_mod_player_for: a hero
// itself, a player's dog its master, anything else the nearer hero); nil with no hero. `e` is a
// handle or a game Entity, so the core co-op Lua and mods ask the one rule.
REFII_LUA_NATIVE(NativePlayerFor) {
  const int32_t player = refii_mod_player_for(&lua.ctx, lua.base, EntityArgument(lua, 1));
  if (player == 1 || player == 2) {
    lua.PushNumber(player);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

// The camera per player (refii_mod.h refii_mod_camera_*). The public three, and the core camera
// chunk's three (Godot/src/Titles/Fable2/host/CoopHeroLua.cs) that state the mode and apply the hold.
// Refii.camera_mode(p) -> ECameraMode, -1 none.
REFII_LUA_NATIVE(NativeCameraMode) {
  lua.PushNumber(refii_mod_camera_mode(lua.Int(1)));
  RESULTS(1);
}

// Refii._camera_override(p, mode, fov, pitch_limit, blend, flags) -> stated (camera_override is the
// preamble's: it unpacks the options table).
REFII_LUA_NATIVE(NativeCameraOverride) {
  lua.PushBoolean(refii_mod_camera_override(lua.Int(1), lua.Int(2), static_cast<float>(lua.Number(3)),
                                            static_cast<float>(lua.Number(4)), static_cast<float>(lua.Number(5)),
                                            lua.Int(6)) != 0);
  RESULTS(1);
}

// Refii.fov(p) -> degrees (refii_mod_camera_fov), 0 with no camera.
REFII_LUA_NATIVE(NativeCameraFov) {
  lua.PushNumber(refii_mod_camera_fov(lua.base, lua.Int(1)));
  RESULTS(1);
}

// Refii._camera_stated(hero, mode): hero (a game Entity or a handle) has mode active.
REFII_LUA_NATIVE(NativeCameraStated) {
  const uint32_t hero = EntityArgument(lua, 1);
  if (hero != 0u) {
    refii::vfs::ModCameraStated(lua.ctx, lua.base, hero, lua.Int(2));
  }
  RESULTS(0);
}

// Refii._camera_zoom_stated(hero, set, level, combat, string_length, cage_radius, max_cage_radius, height,
// focal_offset, fov, max_coop_distance): the core chunk's statement of the zoom level hero's camera uses.
REFII_LUA_NATIVE(NativeCameraZoomStated) {
  const uint32_t hero = EntityArgument(lua, 1);
  char set[64];
  lua.String(2, set, sizeof(set));
  float values[8];
  for (int32_t i = 0; i < 8; ++i) {
    values[i] = static_cast<float>(lua.Number(4 + i));
  }
  if (hero != 0u) {
    refii::vfs::ModCameraZoomStated(lua.ctx, lua.base, hero, set, lua.Int(3), values);
  }
  RESULTS(0);
}

// Refii._camera_read(what, p [, x, y]): the camera's read calls (refii_mod_requests.h), flat for the
// preamble's Refii.camera_* — 1 pose (12 numbers), 2 stated (11), 3 view (4), 4 screen (mode, split),
// 5 ray (6); nil where there is none.
REFII_LUA_NATIVE(NativeCameraRead) {
  const int32_t what = lua.Int(1);
  const int32_t player = lua.Int(2);
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
    RESULTS(1);
    return;
  }
  for (int32_t i = 0; i < count; ++i) {
    lua.PushNumber(out[i]);
  }
  RESULTS(count);
}

// Refii._camera_offset_stated(p, on, right, up, back, yaw, pitch, roll, fov): the display offset the
// camera store resolved for player p (Engine_ModLuaCamera.cs), for the shell's drawn frame only.
REFII_LUA_NATIVE(NativeCameraOffsetStated) {
  float seven[7];
  for (int32_t i = 0; i < 7; ++i) {
    seven[i] = static_cast<float>(lua.Number(3 + i));
  }
  refii_bridge_camera_offset_set(lua.Int(1), seven, lua.Boolean(2) ? 1 : 0);
  RESULTS(0);
}

// Refii._coop_tether_stated(distance, separation): the core camera chunk's statement each camera tick of the
// co-op leash — the distance its camera last stated to Debug.SetLimitCharacterDistance and the heroes' head-to-head
// separation, as the leash sub_821A0298 measures it — for hybrid's split (Engine_CameraScreens).
REFII_LUA_NATIVE(NativeCoopTetherStated) {
  if (lua.Type(1) == kTypeNumber && lua.Type(2) == kTypeNumber) {
    refii_bridge_coop_tether_stated(static_cast<float>(lua.Number(1)), static_cast<float>(lua.Number(2)));
  }
  RESULTS(0);
}

// Refii._camera_hold(hero) -> mode, fov, pitch_limit, blend, flags, revision; nil with no hold in force.
REFII_LUA_NATIVE(NativeCameraHold) {
  int32_t mode = 0, flags = 0;
  float fov = 0.0f, pitch = 0.0f, blend = -1.0f;
  uint32_t revision = 0;
  const uint32_t hero = EntityArgument(lua, 1);
  if (hero == 0u || !refii::vfs::ModCameraHold(lua.ctx, lua.base, hero, &mode, &fov, &pitch, &blend, &flags, &revision)) {
    lua.PushNil();
    RESULTS(1);
    return;
  }
  lua.PushNumber(mode);
  lua.PushNumber(fov);
  lua.PushNumber(pitch);
  lua.PushNumber(blend);
  lua.PushNumber(flags);
  lua.PushNumber(revision);
  RESULTS(6);
}

// Refii._camera_held(hero, bits): the halves of hero's override record the hold wrote and still stand.
REFII_LUA_NATIVE(NativeCameraHeld) {
  const uint32_t hero = EntityArgument(lua, 1);
  if (hero != 0u) {
    refii::vfs::ModCameraHeld(lua.base, hero, lua.Int(2));
  }
  RESULTS(0);
}

// Refii.hide_part(e, part, on [, p]) -> posted (refii_mod_hide_part_for: drawing only, Godot's
// skeleton; p 1 or 2 that player's view alone, absent or 0 every view).
REFII_LUA_NATIVE(NativeHidePart) {
  char part[128];
  lua.String(2, part, sizeof(part));
  lua.PushBoolean(refii_mod_hide_part_for(EntityArgument(lua, 1), part, lua.Boolean(3) ? 1 : 0, lua.Int(4)) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeRenown) {
  int32_t renown = 0;
  if (refii_mod_renown(&lua.ctx, lua.base, lua.Entity(1), &renown) != 0) {
    lua.PushNumber(renown);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeMorality) {
  float value = 0.0f;
  if (refii_mod_morality(&lua.ctx, lua.base, lua.Entity(1), &value) != 0) {
    lua.PushNumber(value);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

REFII_LUA_NATIVE(NativePurity) {
  float value = 0.0f;
  if (refii_mod_purity(&lua.ctx, lua.base, lua.Entity(1), &value) != 0) {
    lua.PushNumber(value);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

// Refii.can_save(p) -> bool: Player.IsAbleToSaveGame for player p's hero (refii_mod_can_save).
REFII_LUA_NATIVE(NativeCanSave) {
  lua.PushBoolean(refii_mod_can_save(&lua.ctx, lua.base, lua.Int(1)) != 0);
  RESULTS(1);
}

// Refii.save_game(p) -> bool: AutoSaveGame(true, true) where p may save (refii_mod_save_game);
// save_written follows per package.
REFII_LUA_NATIVE(NativeSaveGame) {
  lua.PushBoolean(refii_mod_save_game(&lua.ctx, lua.base, lua.Int(1)) != 0);
  RESULTS(1);
}

// Refii.modify_renown(e, delta) / modify_morality(e, delta) / modify_purity(e, delta) -> bool:
// the game's own Stats.Modify* bodies (refii_mod_modify_renown ..), so the events fire.
REFII_LUA_NATIVE(NativeModifyRenown) {
  lua.PushBoolean(refii_mod_modify_renown(&lua.ctx, lua.base, lua.Entity(1), lua.Int(2)) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeModifyMorality) {
  lua.PushBoolean(refii_mod_modify_morality(&lua.ctx, lua.base, lua.Entity(1), static_cast<float>(lua.Number(2))) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeModifyPurity) {
  lua.PushBoolean(refii_mod_modify_purity(&lua.ctx, lua.base, lua.Entity(1), static_cast<float>(lua.Number(2))) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativePhysique) {
  float fat = 0.0f, height = 0.0f;
  if (refii_mod_physique(lua.base, lua.Entity(1), &fat, &height) != 0) {
    lua.PushNumber(fat);
    lua.PushNumber(height);
    RESULTS(2);
  } else {
    lua.PushNil();
    RESULTS(1);
  }
}

REFII_LUA_NATIVE(NativeAge) {
  float age = 0.0f;
  if (refii_mod_age(lua.base, lua.Entity(1), &age) != 0) {
    lua.PushNumber(age);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

// Refii.appearance(p) -> {automatic, tan, fat, height, strength, skill, will, morality, purity, age}
// or nil: the look in force for player p's hero (refii_mod_appearance).
REFII_LUA_NATIVE(NativeAppearance) {
  refii_mod_appearance_t look{};
  if (refii_mod_appearance(&lua.ctx, lua.base, lua.Int(1), &look) == 0) {
    lua.PushNil();
    RESULTS(1);
    return;
  }
  lua.NewTable();
  lua.PushBoolean(look.automatic != 0);
  lua.SetField(-2, "automatic");
  const std::pair<const char*, float> inputs[] = {
      {"tan", look.tan},     {"fat", look.fat},           {"height", look.height},
      {"strength", look.strength}, {"skill", look.skill}, {"will", look.will},
      {"morality", look.morality}, {"purity", look.purity}, {"age", look.age},
  };
  for (const auto& [name, value] : inputs) {
    lua.PushNumber(value);
    lua.SetField(-2, name);
  }
  RESULTS(1);
}

// Refii._set_appearance(p, tan, fat, height, strength, skill, will, morality, purity, age): the
// preamble's Refii.set_appearance(p, {..}); a nil input keeps how it looks (refii_mod_set_appearance).
REFII_LUA_NATIVE(NativeSetAppearance) {
  const auto input = [&lua](int32_t index) {
    return lua.Type(index) == kTypeNumber ? static_cast<float>(lua.Number(index)) : NAN;
  };
  const refii_mod_appearance_t look = {0, input(2), input(3), input(4), input(5), input(6),
                                       input(7), input(8), input(9), input(10)};
  refii_mod_set_appearance(&lua.ctx, lua.base, lua.Int(1), &look);
  RESULTS(0);
}

// Refii.set_appearance_automatic(p, on): the game's own look again (true), or the stated one (false).
REFII_LUA_NATIVE(NativeSetAppearanceAutomatic) {
  refii_mod_set_appearance_automatic(&lua.ctx, lua.base, lua.Int(1), lua.Boolean(2) ? 1 : 0);
  RESULTS(0);
}

// A mod morph as a script names it: "<mod>.<name>" (refii_mod_morph_id) or its id "0x…"; 0 for none.
// A Lua number is a 32-bit float, so an id is a string.
uint32_t MorphArgument(Lua& lua, int32_t index) {
  char text[160];
  lua.String(index, text, sizeof(text));
  if (text[0] == 0) {
    return 0;
  }
  if (text[0] == '0' && (text[1] == 'x' || text[1] == 'X')) {
    return static_cast<uint32_t>(std::strtoul(text + 2, nullptr, 16));
  }
  return refii_mod_morph_id(text);
}

// Refii.morph_get(p, morph) -> weight, or nil for a morph no enabled mod declares (refii_mod_morph_weight).
REFII_LUA_NATIVE(NativeMorphGet) {
  float weight = 0.0f;
  if (refii_mod_morph_weight(lua.Int(1), MorphArgument(lua, 2), &weight) != 0) {
    lua.PushNumber(weight);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

// Refii.morph_set(p, morph, w) -> true when the morph is declared (refii_mod_set_morph_weight).
REFII_LUA_NATIVE(NativeMorphSet) {
  lua.PushBoolean(refii_mod_set_morph_weight(lua.Int(1), MorphArgument(lua, 2), static_cast<float>(lua.Number(3))) != 0);
  RESULTS(1);
}

// Refii.morph_id("<mod>.<name>") -> "0x…", the MorphType its records carry.
REFII_LUA_NATIVE(NativeMorphId) {
  char hex[16];
  std::snprintf(hex, sizeof(hex), "0x%08x", MorphArgument(lua, 1));
  lua.PushString(hex);
  RESULTS(1);
}

// Refii.inventory(e): { {definition = "0x…", count = n, usable = bool}, ... } in the game's order,
// or nil; usable is the item's own UsableFromInventory test (refii_mod_inventory_usable).
REFII_LUA_NATIVE(NativeInventory) {
  const uint32_t entity = lua.Entity(1);
  if (refii_mod_component(lua.base, entity, 13) == 0) {
    lua.PushNil();
    RESULTS(1);
    return;
  }
  lua.NewTable();
  uint32_t definition = 0;
  int32_t count = 0;
  for (int32_t i = 0; i < 4096 && refii_mod_inventory_stack(lua.base, entity, i, &definition, &count) != 0; ++i) {
    lua.NewTable();
    lua.PushHash(definition);
    lua.SetField(-2, "definition");
    lua.PushNumber(count);
    lua.SetField(-2, "count");
    lua.PushBoolean(refii_mod_inventory_usable(&lua.ctx, lua.base, entity, i) != 0);
    lua.SetField(-2, "usable");
    lua.RawSetI(-2, i + 1);
  }
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeEquipped) {
  uint32_t melee = 0, ranged = 0;
  if (refii_mod_equipped(lua.base, lua.Entity(1), &melee, &ranged) == 0) {
    lua.PushNil();
    RESULTS(1);
    return;
  }
  if (melee != 0) {
    lua.PushHash(melee);
  } else {
    lua.PushNil();
  }
  if (ranged != 0) {
    lua.PushHash(ranged);
  } else {
    lua.PushNil();
  }
  RESULTS(2);
}

// Refii.near(x, y, z, radius [, creatures_only]): handles, nearest first (at most 2048).
/// The REFII_QUICK_* flags an options table at `index` states: `ungated` (and `held`) true.
uint32_t QuickFlags(Lua& lua, int32_t index) {
  if (lua.Type(index) != kTypeTable) {
    return 0u;
  }
  uint32_t flags = 0u;
  lua.GetField(index, "ungated");
  flags |= lua.Boolean(-1) ? REFII_QUICK_UNGATED : 0u;
  lua.SetTop(-2);
  lua.GetField(index, "held");
  flags |= lua.Boolean(-1) ? REFII_QUICK_HELD : 0u;
  lua.SetTop(-2);
  return flags;
}

// The quick menu's actions for player p (refii_mod_quick_action): Refii.use_item(p, def [, opts]) ·
// equip_weapon(p, def [, opts]) · perform_expression(p, expression [, held [, opts]]) ·
// assign_spell(p, spell [, opts]) -> performed. opts {ungated = true} skips the quick menu's gate
// (still refused for a dead hero and in a cutscene); held keeps the expression unreleased.
REFII_LUA_NATIVE(NativeUseItem) {
  lua.PushBoolean(refii_mod_quick_action(&lua.ctx, lua.base, lua.Int(1), 1, lua.Hash(2), QuickFlags(lua, 3) & REFII_QUICK_UNGATED) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeEquipWeapon) {
  lua.PushBoolean(refii_mod_quick_action(&lua.ctx, lua.base, lua.Int(1), 3, lua.Hash(2), QuickFlags(lua, 3) & REFII_QUICK_UNGATED) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativePerformExpression) {
  uint32_t flags = QuickFlags(lua, 3) | QuickFlags(lua, 4);
  if (lua.Type(3) == 1 && lua.Boolean(3)) {
    flags |= REFII_QUICK_HELD;
  }
  lua.PushBoolean(refii_mod_quick_action(&lua.ctx, lua.base, lua.Int(1), 2, lua.Hash(2), flags) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeAssignSpell) {
  const int32_t spell = lua.Int(2);
  lua.PushBoolean(spell >= 0 && refii_mod_quick_action(&lua.ctx, lua.base, lua.Int(1), 4, static_cast<uint32_t>(spell),
                                                       QuickFlags(lua, 3) & REFII_QUICK_UNGATED) != 0);
  RESULTS(1);
}

// Refii.release_expression(p) -> bool: the performer's own end of a held expression.
REFII_LUA_NATIVE(NativeReleaseExpression) {
  lua.PushBoolean(refii_mod_release_expression(&lua.ctx, lua.base, lua.Int(1)) != 0);
  RESULTS(1);
}

// Refii.sprinting(p) -> bool · Refii.end_sprint(p) -> bool: the sprint component's flag, and the
// release's own end (refii_mod_end_sprint).
REFII_LUA_NATIVE(NativeSprinting) {
  lua.PushBoolean(refii_mod_sprinting(lua.base, lua.Int(1)) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeEndSprint) {
  lua.PushBoolean(refii_mod_end_sprint(&lua.ctx, lua.base, lua.Int(1)) != 0);
  RESULTS(1);
}

// Refii._set_locomotion(p, key, value) -> bool: one LocomotionData field on player p's hero's own
// table, held (refii_mod_set_locomotion); false (or nil) as the value stops holding it. The
// preamble's Refii.set_locomotion(p, {key = value, ..}) calls it per field.
REFII_LUA_NATIVE(NativeSetLocomotion) {
  char key[64];
  lua.String(2, key, sizeof(key));
  const float value = lua.Type(3) == kTypeNumber ? static_cast<float>(lua.Number(3)) : NAN;
  lua.PushBoolean(refii_mod_set_locomotion(&lua.ctx, lua.base, lua.Int(1), key, value) != 0);
  RESULTS(1);
}

// Refii.locomotion(p, key) -> number or nil: what player p's hero's own table holds for key.
REFII_LUA_NATIVE(NativeLocomotion) {
  char key[64];
  lua.String(2, key, sizeof(key));
  float value = 0.0f;
  if (refii_mod_locomotion(&lua.ctx, lua.base, lua.Int(1), key, &value) != 0) {
    lua.PushNumber(value);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

// Refii.set_parry(p, window, wedge) -> bool (nil leaves one) · Refii.parry(p) -> window, wedge.
REFII_LUA_NATIVE(NativeSetParry) {
  const float window = lua.Type(2) == kTypeNumber ? static_cast<float>(lua.Number(2)) : NAN;
  const float wedge = lua.Type(3) == kTypeNumber ? static_cast<float>(lua.Number(3)) : NAN;
  lua.PushBoolean(refii_mod_set_parry(&lua.ctx, lua.base, lua.Int(1), window, wedge) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeParry) {
  float window = 0.0f, wedge = 0.0f;
  if (refii_mod_parry(&lua.ctx, lua.base, lua.Int(1), &window, &wedge) != 0) {
    lua.PushNumber(window);
    lua.PushNumber(wedge);
    RESULTS(2);
  } else {
    lua.PushNil();
    RESULTS(1);
  }
}

// Refii.expressions(p): { {record = "0x…", id = "0x…", uses = n}, … } the performer knows (id is
// the record: what perform_expression takes and selected_expression answers), or nil without one.
REFII_LUA_NATIVE(NativeExpressions) {
  const int32_t player = lua.Int(1);
  const uint32_t hero = player >= 1 && player <= 2 ? refii_mod_player_hero(lua.base, player) : 0u;
  if (hero == 0 || refii_mod_component(lua.base, hero, 154) == 0) {
    lua.PushNil();
    RESULTS(1);
    return;
  }
  lua.NewTable();
  uint32_t record = 0;
  int32_t uses = 0;
  for (int32_t i = 0; i < 512 && refii_mod_expression(lua.base, player, i, &record, &uses) != 0; ++i) {
    lua.NewTable();
    lua.PushHash(record);
    lua.SetField(-2, "record");
    lua.PushHash(record);
    lua.SetField(-2, "id");
    lua.PushNumber(uses);
    lua.SetField(-2, "uses");
    lua.RawSetI(-2, i + 1);
  }
  RESULTS(1);
}

// Refii.spells_known(p): { type, … } ascending — the spell types the hero can assign to some tier —
// or nil without a spell manager.
REFII_LUA_NATIVE(NativeSpellsKnown) {
  uint32_t mask = 0;
  if (refii_mod_spells_known(lua.base, lua.Int(1), &mask) == 0) {
    lua.PushNil();
    RESULTS(1);
    return;
  }
  lua.NewTable();
  int32_t n = 0;
  for (uint32_t type = 0; type < 8; ++type) {
    if ((mask & (1u << type)) != 0) {
      lua.PushNumber(type);
      lua.RawSetI(-2, ++n);
    }
  }
  RESULTS(1);
}

// Refii.selected_expression(p): the record "0x…" the expression wheel highlights while it is up for
// player p, or nil.
REFII_LUA_NATIVE(NativeSelectedExpression) {
  const uint32_t record = refii_mod_selected_expression(lua.base, lua.Int(1));
  if (record != 0) {
    lua.PushHash(record);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

// Refii.record_string(def, field): a GDB record's inherited string property ("NameTag",
// "IconGraphic") through the game's own getter, or nil.
REFII_LUA_NATIVE(NativeRecordString) {
  char field[64];
  lua.String(2, field, sizeof(field));
  char text[256];
  if (refii_mod_record_string(&lua.ctx, lua.base, lua.Hash(1), field, text, sizeof(text)) != 0) {
    lua.PushString(text);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

// Refii.record_int(def, field): a GDB record's inherited integer or enum property ("Category",
// "WeaponType") through the game's typed getter, or nil.
REFII_LUA_NATIVE(NativeRecordInt) {
  char field[64];
  lua.String(2, field, sizeof(field));
  int32_t value = 0;
  if (refii_mod_record_int(&lua.ctx, lua.base, lua.Hash(1), field, &value) != 0) {
    lua.PushNumber(static_cast<double>(value));
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

// Refii.record_number(def, field): a GDB record's inherited float (or int / enum) property, or nil.
REFII_LUA_NATIVE(NativeRecordNumber) {
  char field[64];
  lua.String(2, field, sizeof(field));
  float value = 0.0f;
  if (refii_mod_record_number(&lua.ctx, lua.base, lua.Hash(1), field, &value) != 0) {
    lua.PushNumber(static_cast<double>(value));
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

// Refii.record_bool(def, field): a GDB record's inherited bool property, true / false, or nil.
REFII_LUA_NATIVE(NativeRecordBool) {
  char field[64];
  lua.String(2, field, sizeof(field));
  int32_t value = 0;
  if (refii_mod_record_bool(&lua.ctx, lua.base, lua.Hash(1), field, &value) != 0) {
    lua.PushBoolean(value != 0);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

// Refii.record_exists(def): whether the database has the record.
REFII_LUA_NATIVE(NativeRecordExists) {
  lua.PushBoolean(refii_mod_record_exists(&lua.ctx, lua.base, lua.Hash(1)) != 0);
  RESULTS(1);
}

// Refii.record_ref(def, field): the record a GDB reference property (type 6) names, "0x…", or nil
// (refii_mod_record_ref).
REFII_LUA_NATIVE(NativeRecordRef) {
  char field[64];
  lua.String(2, field, sizeof(field));
  uint32_t value = 0u;
  if (refii_mod_record_ref(&lua.ctx, lua.base, lua.Hash(1), field, &value) != 0) {
    lua.PushHash(value);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

// Refii.creature_tier(e): the creature's rank bits as its definition states them (refii_mod_creature_tier),
// nil without a creature component.
REFII_LUA_NATIVE(NativeCreatureTier) {
  const int32_t tier = refii_mod_creature_tier(lua.base, lua.Entity(1));
  if (tier < 0) {
    lua.PushNil();
  } else {
    lua.PushNumber(static_cast<double>(tier));
  }
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeNear) {
  const float xyz[3] = {static_cast<float>(lua.Number(1)), static_cast<float>(lua.Number(2)),
                        static_cast<float>(lua.Number(3))};
  const bool creatures = lua.Type(5) == 1 ? lua.Boolean(5) : lua.Int(5) != 0;
  static uint32_t found[2048];
  const int32_t n = refii_mod_entities_near(&lua.ctx, lua.base, xyz, static_cast<float>(lua.Number(4)),
                                            creatures ? 1 : 0, found, 2048);
  lua.NewTable();
  for (int32_t i = 0; i < n; ++i) {
    lua.PushNumber(refii_mod_handle(found[i]));
    lua.RawSetI(-2, i + 1);
  }
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeLineOfSight) {
  const float from[3] = {static_cast<float>(lua.Number(1)), static_cast<float>(lua.Number(2)),
                         static_cast<float>(lua.Number(3))};
  const float to[3] = {static_cast<float>(lua.Number(4)), static_cast<float>(lua.Number(5)),
                       static_cast<float>(lua.Number(6))};
  lua.PushBoolean(refii_mod_line_of_sight(&lua.ctx, lua.base, from, to) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeSpawn) {
  const float xyz[3] = {static_cast<float>(lua.Number(2)), static_cast<float>(lua.Number(3)),
                        static_cast<float>(lua.Number(4))};
  lua.PushHandle(refii_mod_spawn(&lua.ctx, lua.base, lua.Hash(1), xyz));
  RESULTS(1);
}

// Refii._play_fx(name, x, y, z, e, duration, scale, r, g, b, intensity, loop, ox, oy, oz, at): the
// request Godot draws (refii_mod_play_fx); e is a handle, 0 for a world position; at a model
// helper name or "" (refii_mod_fx_options.at). The id, or nil.
REFII_LUA_NATIVE(NativePlayFx) {
  char name[128];
  lua.String(1, name, sizeof(name));
  const float xyz[3] = {static_cast<float>(lua.Number(2)), static_cast<float>(lua.Number(3)),
                        static_cast<float>(lua.Number(4))};
  const uint32_t handle = static_cast<uint32_t>(Lua::Clamp(lua.Number(5), 0.0, 4294967295.0));
  refii_mod_fx_options options = refii_mod_fx_default_options();
  options.duration = static_cast<float>(lua.Number(6));
  options.scale = static_cast<float>(lua.Number(7));
  options.tint[0] = static_cast<float>(lua.Number(8));
  options.tint[1] = static_cast<float>(lua.Number(9));
  options.tint[2] = static_cast<float>(lua.Number(10));
  options.intensity = static_cast<float>(lua.Number(11));
  options.loop = lua.Int(12) != 0 ? 1 : 0;
  options.offset[0] = static_cast<float>(lua.Number(13));
  options.offset[1] = static_cast<float>(lua.Number(14));
  options.offset[2] = static_cast<float>(lua.Number(15));
  char at[96];
  lua.String(16, at, sizeof(at));
  options.at = at[0] != 0 ? at : nullptr;
  const uint32_t id = refii::vfs::PlayModFxOnHandle(name, handle != 0 ? nullptr : xyz, handle, &options);
  if (id != 0) {
    lua.PushNumber(id);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

// Refii._mark(e, x, y, z, icon, text, p): refii_mod_mark — a HUD world marker over handle e (0: at
// x, y, z) for player p (0 or nil: every player played here); the preamble's Refii.mark(target,
// icon, text [, p]) takes a handle or {x, y, z}. The id Refii.unmark takes, or nil.
REFII_LUA_NATIVE(NativeMark) {
  const uint32_t handle = static_cast<uint32_t>(Lua::Clamp(lua.Number(1), 0.0, 4294967295.0));
  const float xyz[3] = {static_cast<float>(lua.Number(2)), static_cast<float>(lua.Number(3)),
                        static_cast<float>(lua.Number(4))};
  char icon[160];
  char text[160];
  lua.String(5, icon, sizeof(icon));
  lua.String(6, text, sizeof(text));
  const uint32_t entity = handle != 0u ? refii_mod_handle_address(handle) : 0u;
  const uint32_t id = refii_mod_mark(lua.Int(7), entity, handle != 0u ? nullptr : xyz, icon, text);
  if (id != 0) {
    lua.PushNumber(id);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

// Refii.unmark(id): removes a marker by any surface's id.
REFII_LUA_NATIVE(NativeUnmark) {
  refii_mod_unmark(static_cast<uint32_t>(Lua::Clamp(lua.Number(1), 0.0, 4294967295.0)));
  RESULTS(0);
}

// Refii._world_meter(e, x, y, z, value, max, style, p): refii_mod_world_meter — a bar on the marker
// channel over handle e (0: at x, y, z); the preamble's Refii.world_meter takes a handle or {x, y, z}.
// The id Refii.mark_meter and Refii.unmark take, or nil.
REFII_LUA_NATIVE(NativeWorldMeter) {
  const uint32_t handle = static_cast<uint32_t>(Lua::Clamp(lua.Number(1), 0.0, 4294967295.0));
  const float xyz[3] = {static_cast<float>(lua.Number(2)), static_cast<float>(lua.Number(3)),
                        static_cast<float>(lua.Number(4))};
  char style[32];
  lua.String(7, style, sizeof(style));
  const uint32_t entity = handle != 0u ? refii_mod_handle_address(handle) : 0u;
  const uint32_t id = refii_mod_world_meter(lua.Int(8), entity, handle != 0u ? nullptr : xyz,
                                            static_cast<float>(lua.Number(5)), static_cast<float>(lua.Number(6)), style);
  if (id != 0) {
    lua.PushNumber(id);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

// Refii.mark_meter(id, value, max [, style]): refii_mod_mark_meter — a marker's fill stated or
// restated in place; max 0 takes it off. True when posted.
REFII_LUA_NATIVE(NativeMarkMeter) {
  char style[32];
  lua.String(4, style, sizeof(style));
  lua.PushBoolean(refii_mod_mark_meter(static_cast<uint32_t>(Lua::Clamp(lua.Number(1), 0.0, 4294967295.0)),
                                       static_cast<float>(lua.Number(2)), static_cast<float>(lua.Number(3)), style) != 0);
  RESULTS(1);
}

// Refii.fade(p, dir [, seconds]): refii_mod_fade — player p's screen (0 or nil: every player
// played here) "to_black" / "out" or "from_black" / "in" over seconds (0 or nil: at once). True
// when posted.
REFII_LUA_NATIVE(NativeFade) {
  char dir[32];
  lua.String(2, dir, sizeof(dir));
  const bool to_black = std::strcmp(dir, "to_black") == 0 || std::strcmp(dir, "out") == 0 || std::strcmp(dir, "black") == 0;
  lua.PushBoolean(refii_mod_fade(lua.Int(1), to_black ? 1 : 0, static_cast<float>(lua.Number(3))) != 0);
  RESULTS(1);
}

// Refii.meter(id, label, value, max [, p [, style]]): refii_mod_meter — a HUD meter for player p
// (0 or nil: every player played here) in style "bar" (nil), "boss" or "timer" (the HUD's own
// primitives), or "generic", "crucible" or "price" (the game's own meter art, a lit copy). True when posted.
REFII_LUA_NATIVE(NativeMeter) {
  char id[96];
  char label[128];
  char style[32];
  lua.String(1, id, sizeof(id));
  lua.String(2, label, sizeof(label));
  lua.String(6, style, sizeof(style));
  lua.PushBoolean(refii_mod_meter(lua.Int(5), id, label, static_cast<float>(lua.Number(3)),
                                  static_cast<float>(lua.Number(4)), style) != 0);
  RESULTS(1);
}

// Refii.meter_remove(id): refii_mod_meter_remove. True when posted.
REFII_LUA_NATIVE(NativeMeterRemove) {
  char id[96];
  lua.String(1, id, sizeof(id));
  lua.PushBoolean(refii_mod_meter_remove(id) != 0);
  RESULTS(1);
}

// Refii.input_claim(p, action, on): refii_mod_input_claim — the action's buttons and keys leave
// player p's pad (0 or nil: both) while claimed; its input_action events still fire. True when posted.
REFII_LUA_NATIVE(NativeInputClaim) {
  char action[96];
  lua.String(2, action, sizeof(action));
  lua.PushBoolean(refii_mod_input_claim(lua.Int(1), action, lua.Boolean(3) ? 1 : 0) != 0);
  RESULTS(1);
}

// Refii.press(p, button, held): refii_mod_press — held true / a number holds the button on player p's
// pad (a trigger pulled by it: true or 1 full, 2..255 that much), false / nil / 0 releases. True when posted.
REFII_LUA_NATIVE(NativePress) {
  char button[32];
  lua.String(2, button, sizeof(button));
  const int32_t held = lua.Type(3) == kTypeNumber ? lua.Int(3) : (lua.Boolean(3) ? 1 : 0);
  lua.PushBoolean(refii_mod_press(lua.Int(1), button, held) != 0);
  RESULTS(1);
}

// Refii.charge_spell(p, type, held): refii_mod_charge_spell — held true charges spell `type` through
// player p's own magic charge (its tiers lent, no spell_pick), false lets the button go; the slots come
// back once the charge is over. True when the charge was taken (false: not known on tier one).
REFII_LUA_NATIVE(NativeChargeSpell) {
  const int32_t held = lua.Type(3) == kTypeNumber ? lua.Int(3) : (lua.Boolean(3) ? 1 : 0);
  lua.PushBoolean(refii_mod_charge_spell(&lua.ctx, lua.base, lua.Int(1), lua.Int(2), held) != 0);
  RESULTS(1);
}

// Refii.tap(p, button [, ticks]): refii_mod_tap — down for at least `ticks` guest ticks (1 when absent).
REFII_LUA_NATIVE(NativeTap) {
  char button[32];
  lua.String(2, button, sizeof(button));
  const int32_t ticks = lua.Type(3) == kTypeNumber ? lua.Int(3) : 1;
  lua.PushBoolean(refii_mod_tap(lua.Int(1), button, ticks) != 0);
  RESULTS(1);
}

// Refii.hud_icon(p, element, texture): refii_mod_hud_icon — a game texture path or a mod PNG on a HUD
// element for player p (0 or nil: both); "" or nil clears. True when posted.
REFII_LUA_NATIVE(NativeHudIcon) {
  char element[96];
  char texture[160];
  lua.String(2, element, sizeof(element));
  lua.String(3, texture, sizeof(texture));
  lua.PushBoolean(refii_mod_hud_icon(lua.Int(1), element, texture) != 0);
  RESULTS(1);
}

// Refii.hud_slide(p, element, slide): refii_mod_hud_slide — the slide as the game counts them; 0 clears.
REFII_LUA_NATIVE(NativeHudSlide) {
  char element[96];
  lua.String(2, element, sizeof(element));
  lua.PushBoolean(refii_mod_hud_slide(lua.Int(1), element, lua.Int(3)) != 0);
  RESULTS(1);
}

// Refii.hud_hold(p, piece, on): refii_mod_hud_hold — keeps a HUD piece visible for player p.
REFII_LUA_NATIVE(NativeHudHold) {
  char piece[96];
  lua.String(2, piece, sizeof(piece));
  lua.PushBoolean(refii_mod_hud_hold(lua.Int(1), piece, lua.Boolean(3) ? 1 : 0) != 0);
  RESULTS(1);
}

// Refii.stop_fx(id): any surface's id.
REFII_LUA_NATIVE(NativeStopFx) {
  refii_mod_stop_fx(static_cast<uint32_t>(Lua::Clamp(lua.Number(1), 0.0, 4294967295.0)));
  RESULTS(0);
}

// Refii._light(e, x, y, z, r, g, b, energy, range, ox, oy, oz, at, flicker, dx, dy, dz, cone,
// attenuation): the light Godot places (refii_mod_light); e is a handle, 0 for the world point
// x, y, z; at a model helper name or ""; a cone above 0 makes it a spot along dx, dy, dz
// (refii_mod_light_options). The preamble's Refii.light(target, color, energy, range [, o]) builds
// it. The id, or nil.
REFII_LUA_NATIVE(NativeLight) {
  const uint32_t handle = static_cast<uint32_t>(Lua::Clamp(lua.Number(1), 0.0, 4294967295.0));
  const float xyz[3] = {static_cast<float>(lua.Number(2)), static_cast<float>(lua.Number(3)),
                        static_cast<float>(lua.Number(4))};
  const float rgb[3] = {static_cast<float>(lua.Number(5)), static_cast<float>(lua.Number(6)),
                        static_cast<float>(lua.Number(7))};
  refii_mod_light_options options{};
  options.offset[0] = static_cast<float>(lua.Number(10));
  options.offset[1] = static_cast<float>(lua.Number(11));
  options.offset[2] = static_cast<float>(lua.Number(12));
  char at[96];
  lua.String(13, at, sizeof(at));
  options.at = at[0] != 0 ? at : nullptr;
  options.flicker = static_cast<float>(lua.Number(14));
  options.direction[0] = static_cast<float>(lua.Number(15));
  options.direction[1] = static_cast<float>(lua.Number(16));
  options.direction[2] = static_cast<float>(lua.Number(17));
  options.cone = static_cast<float>(lua.Number(18));
  options.attenuation = static_cast<float>(lua.Number(19));
  const uint32_t id = refii::vfs::PlaceModLightOnHandle(handle, xyz, rgb, static_cast<float>(lua.Number(8)),
                                                        static_cast<float>(lua.Number(9)), &options);
  if (id != 0) {
    lua.PushNumber(id);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

// Refii.light_remove(id): removes a mod light by any surface's id.
REFII_LUA_NATIVE(NativeLightRemove) {
  refii_mod_light_remove(static_cast<uint32_t>(Lua::Clamp(lua.Number(1), 0.0, 4294967295.0)));
  RESULTS(0);
}

// Refii.send_to_partner(mod, name, payload): refii_mod_send_to_partner — a mod's own message to
// the other machine of an online session, raised there as partner_message. True when posted.
REFII_LUA_NATIVE(NativeSendToPartner) {
  char mod[64];
  char name[64];
  char payload[REFII_MOD_PARTNER_PAYLOAD_MAX + 2];
  lua.String(1, mod, sizeof(mod));
  lua.String(2, name, sizeof(name));
  lua.String(3, payload, sizeof(payload));
  lua.PushBoolean(refii_mod_send_to_partner(mod, name, payload) != 0);
  RESULTS(1);
}

// Refii.signal(name, payload): refii_mod_signal — "<mod>.<name>" and any text (at most
// REFII_MOD_SIGNAL_PAYLOAD_MAX bytes, longer cut with a mod error) to Godot on this machine, raised
// there as RefiiHost.mod_signal(mod, name, payload). True when posted.
REFII_LUA_NATIVE(NativeSignal) {
  char name[REFII_MOD_SIGNAL_NAME_MAX + 2];
  lua.String(1, name, sizeof(name));
  const std::string payload = lua.LongString(2, REFII_MOD_SIGNAL_PAYLOAD_MAX + 1);
  lua.PushBoolean(refii_mod_signal(name, payload.c_str()) != 0);
  RESULTS(1);
}

// Refii.publish_text(key, text): refii_mod_publish_text — a text Godot reads with get_text(key).
REFII_LUA_NATIVE(NativePublishText) {
  char key[REFII_MOD_SIGNAL_NAME_MAX + 2];
  lua.String(1, key, sizeof(key));
  const std::string text = lua.LongString(2, REFII_MOD_SIGNAL_PAYLOAD_MAX + 1);
  lua.PushBoolean(refii_mod_publish_text(key, text.c_str()) != 0);
  RESULTS(1);
}

// Refii.open_menu(name [, p]): a mod's menu for player p (0 or nil: whoever owns the menus
// here), over the list that has the input or on its own (refii_mod_open_menu). True when posted.
REFII_LUA_NATIVE(NativeOpenMenu) {
  char name[96];
  lua.String(1, name, sizeof(name));
  lua.PushBoolean(refii_mod_open_menu(lua.Int(2), name) != 0);
  RESULTS(1);
}

// Refii._menu_rows(menu, rows, p): refii_mod_set_menu_rows with the preamble's text for the rows
// table (Refii.set_menu_rows), up to REFII_MOD_MENU_ROWS_MAX bytes. True when posted.
REFII_LUA_NATIVE(NativeMenuRows) {
  char menu[96];
  lua.String(1, menu, sizeof(menu));
  // The whole text: refii_mod_set_menu_rows carries up to REFII_MOD_MENU_ROWS_MAX in parts and names
  // a cut on the mod errors channel; the extra byte read lets it see one.
  const std::string rows = lua.LongString(2, REFII_MOD_MENU_ROWS_MAX + 1);
  lua.PushBoolean(refii_mod_set_menu_rows(lua.Int(3), menu, rows.c_str()) != 0);
  RESULTS(1);
}

// Refii.open_screen(id [, p]): a host-owned screen for player p (refii_mod_open_screen). True when posted.
REFII_LUA_NATIVE(NativeOpenScreen) {
  const double id = lua.Number(1);
  lua.PushBoolean(id >= 1.0 && id <= 4294967295.0 &&
                  refii_mod_open_screen(lua.Int(2), static_cast<uint32_t>(id)) != 0);
  RESULTS(1);
}

// Refii._message_box(title, text, accept, cancel, question, p): refii_mod_message_box; the
// preamble's Refii.message_box fills the defaults. The id the mod_message_box event carries, or nil.
REFII_LUA_NATIVE(NativeMessageBox) {
  char title[128];
  char text[480];
  char accept[64];
  char cancel[64];
  lua.String(1, title, sizeof(title));
  lua.String(2, text, sizeof(text));
  lua.String(3, accept, sizeof(accept));
  lua.String(4, cancel, sizeof(cancel));
  const uint32_t id = refii_mod_message_box(lua.Int(6), title, text, accept, lua.Int(5) != 0 ? cancel : nullptr);
  if (id != 0) {
    lua.PushNumber(id);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

// Refii.notice(title, text [, seconds [, p]]): refii_mod_notice — the notice channel, for player p
// (0 or nil: whoever owns the menus here). The id the mod_notice event carries, or nil.
REFII_LUA_NATIVE(NativeNotice) {
  char title[128];
  char text[480];
  lua.String(1, title, sizeof(title));
  lua.String(2, text, sizeof(text));
  const uint32_t id = refii_mod_notice(lua.Int(4), title, text, static_cast<float>(lua.Number(3)));
  if (id != 0) {
    lua.PushNumber(id);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

// Refii._text(text): refii_mod_text — a bare key, or each {KEY} in the text, through the host's lookup
// (the running book, the mods' tables in the culture in force, their en-uk rows), or nil with no
// lookup installed. The preamble's Refii.text fills %1 .. %8 from its arguments.
REFII_LUA_NATIVE(NativeText) {
  char text[480];
  lua.String(1, text, sizeof(text));
  char out[1024];
  if (text[0] != 0 && refii_mod_text(text, out, sizeof(out)) >= 0) {
    lua.PushString(out);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

// Refii._menu_box(title, text, p, cancelable, n, o1 .. o12): refii_mod_menu_box; the preamble's
// Refii.menu_box(title, text, options [, p [, cancelable]]) unpacks the options table (this Lua's
// C API here has no array reads). The id the mod_menu_box event carries, or nil.
REFII_LUA_NATIVE(NativeMenuBox) {
  constexpr int32_t kMax = 12;
  char title[128];
  char text[480];
  char options[kMax][96];
  const char* list[kMax];
  lua.String(1, title, sizeof(title));
  lua.String(2, text, sizeof(text));
  const int32_t count = static_cast<int32_t>(Lua::Clamp(lua.Number(5), 0.0, kMax));
  for (int32_t i = 0; i < count; ++i) {
    lua.String(6 + i, options[i], sizeof(options[i]));
    list[i] = options[i];
  }
  const uint32_t id = refii_mod_menu_box(lua.Int(3), title, text, list, count, lua.Int(4) != 0 ? 1 : 0);
  if (id != 0) {
    lua.PushNumber(id);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

// Refii._cancelled(): the preamble's _fire states that a handler returned true.
REFII_LUA_NATIVE(NativeCancelled) {
  refii::vfs::NoteLuaCancelled();
  RESULTS(0);
}

REFII_LUA_NATIVE(NativeWrite) {
  char field[32];
  lua.String(2, field, sizeof(field));
  lua.PushBoolean(refii::vfs::WriteModEventField(lua.Int(1), field, lua.Number(3)));
  RESULTS(1);
}

struct Native {
  const char* name;
  PPCFunc* function;
  uint32_t thunk;
};

Native g_natives[] = {
    {"publish", &NativePublish, 0},
    {"value", &NativeValue, 0},
    {"log", &NativeLog, 0},
    {"hash", &NativeHash, 0},
    {"health", &NativeHealth, 0},
    {"set_health", &NativeSetHealth, 0},
    {"modify_health", &NativeModifyHealth, 0},
    {"position", &NativePosition, 0},
    {"set_position", &NativeSetPosition, 0},
    {"facing", &NativeFacing, 0},
    {"velocity", &NativeVelocity, 0},
    {"set_facing", &NativeSetFacing, 0},
    {"_decorate_rotation", &NativeDecorateRotation, 0},
    {"entity_tint", &NativeEntityTint, 0},
    {"entity_untint", &NativeEntityUntint, 0},
    {"entity_alpha", &NativeEntityAlpha, 0},
    {"entity_scale", &NativeEntityScale, 0},
    {"entity_saturation_mask", &NativeEntitySaturationMask, 0},
    {"entity_drawable", &NativeEntityDrawable, 0},
    {"lock_target", &NativeLockTarget, 0},
    {"release_lock", &NativeReleaseLock, 0},
    {"switch_target", &NativeSwitchTarget, 0},
    {"locked_target", &NativeLockedTarget, 0},
    {"in_cutscene", &NativeInCutscene, 0},
    {"set_speed_multiplier", &NativeSetSpeedMultiplier, 0},
    {"clear_speed_multiplier", &NativeClearSpeedMultiplier, 0},
    {"speed_multiplier", &NativeSpeedMultiplier, 0},
    {"current_action", &NativeCurrentAction, 0},
    {"action_elapsed", &NativeActionElapsed, 0},
    {"action_interruptible", &NativeActionInterruptible, 0},
    {"allow_action_interrupt", &NativeAllowActionInterrupt, 0},
    {"allow_action_interrupt_at", &NativeAllowActionInterruptAt, 0},
    {"action_type", &NativeActionType, 0},
    {"action_name", &NativeActionName, 0},
    {"living", &NativeLiving, 0},
    {"spell_in_slot", &NativeSpellInSlot, 0},
    {"spell_parts", &NativeSpellParts, 0},
    {"vortex_held", &NativeVortexHeld, 0},
    {"spell_balance", &NativeSpellBalance, 0},
    {"host_serial", &NativeHostSerial, 0},
    {"local_serial", &NativeLocalSerial, 0},
    {"apply_status", &NativeApplyStatus, 0},
    {"clear_status", &NativeClearStatus, 0},
    {"has_status", &NativeHasStatus, 0},
    {"knockback", &NativeKnockback, 0},
    {"stagger", &NativeStagger, 0},
    {"play_sound", &NativePlaySound, 0},
    {"play_sound_at", &NativePlaySoundAt, 0},
    {"stop_sound", &NativeStopSound, 0},
    {"play_music", &NativePlayMusic, 0},
    {"stop_music", &NativeStopMusic, 0},
    {"play_animation", &NativePlayAnimation, 0},
    {"stop_animation", &NativeStopAnimation, 0},
    {"modify_opinion", &NativeModifyOpinion, 0},
    {"add_deed", &NativeAddDeed, 0},
    {"npc_follow", &NativeNpcFollow, 0},
    {"npc_go_to", &NativeNpcGoTo, 0},
    {"npc_flee", &NativeNpcFlee, 0},
    {"npc_attack", &NativeNpcAttack, 0},
    {"npc_set_hostile", &NativeNpcSetHostile, 0},
    {"npc_stop", &NativeNpcStop, 0},
    {"set_time_of_day", &NativeSetTimeOfDay, 0},
    {"time_of_day", &NativeTimeOfDay, 0},
    {"set_weather", &NativeSetWeather, 0},
    {"gold", &NativeGold, 0},
    {"give_gold", &NativeGiveGold, 0},
    {"item_count", &NativeItemCount, 0},
    {"add_item", &NativeAddItem, 0},
    {"experience", &NativeExperience, 0},
    {"give_experience", &NativeGiveExperience, 0},
    {"spend_experience", &NativeSpendExperience, 0},
    {"ability_level", &NativeAbilityLevel, 0},
    {"_cast_spell", &NativeCastSpell, 0},
    {"spell_serial", &NativeSpellSerial, 0},
    {"relation", &NativeRelation, 0},
    {"alive", &NativeAlive, 0},
    {"definition", &NativeDefinition, 0},
    {"name", &NativeName, 0},
    {"player_of", &NativePlayerOf, 0},
    {"level_name", &NativeLevelName, 0},
    {"player_hero", &NativePlayerHero, 0},
    {"local_hero", &NativeLocalHero, 0},
    {"menu_player", &NativeMenuPlayer, 0},
    {"player_value", &NativePlayerValue, 0},
    {"player_set", &NativePlayerSet, 0},
    {"player_add", &NativePlayerAdd, 0},
    {"player_save", &NativePlayerSave, 0},
    {"player_value_text", &NativePlayerValueText, 0},
    {"player_set_text", &NativePlayerSetText, 0},
    {"player_save_text", &NativePlayerSaveText, 0},
    {"player_value_hash", &NativePlayerValueHash, 0},
    {"player_save_hash", &NativePlayerSaveHash, 0},
    {"player_here", &NativePlayerHere, 0},
    {"achievement_unlocked", &NativeAchievementUnlocked, 0},
    {"achievements", &NativeAchievements, 0},
    {"character_id", &NativeCharacterId, 0},
    {"event_replica", &NativeEventReplica, 0},
    {"attack_mode", &NativeAttackMode, 0},
    {"camera_mode", &NativeCameraMode, 0},
    {"_camera_override", &NativeCameraOverride, 0},
    {"fov", &NativeCameraFov, 0},
    {"_camera_stated", &NativeCameraStated, 0},
    {"_camera_zoom_stated", &NativeCameraZoomStated, 0},
    {"_camera_read", &NativeCameraRead, 0},
    {"_camera_offset_stated", &NativeCameraOffsetStated, 0},
    {"_coop_tether_stated", &NativeCoopTetherStated, 0},
    {"_camera_hold", &NativeCameraHold, 0},
    {"_camera_held", &NativeCameraHeld, 0},
    {"hide_part", &NativeHidePart, 0},
    {"split_screen", &NativeSplitScreen, 0},
    {"player_dog", &NativePlayerDog, 0},
    {"player_for", &NativePlayerFor, 0},
    {"_set_partner_dog", &NativeSetPartnerDog, 0},
    {"_two_is_hero", &NativeTwoIsHero, 0},
    {"_restore_group_minds", &NativeRestoreGroupMinds, 0},
    {"_job_worker", &NativeJobWorker, 0},
    {"_estate_buyer", &NativeEstateBuyer, 0},
    {"_estate_look", &NativeEstateLook, 0},
    {"_estate_ready", &NativeEstateReady, 0},
    {"_estate_blocked", &NativeEstateBlocked, 0},
    {"_item_record", &NativeItemRecord, 0},
    {"_use_carried", &NativeUseCarried, 0},
    {"_use_stated_target", &NativeUseStatedTarget, 0},
    {"_use_target_state", &NativeUseTargetState, 0},
    {"_received_item", &NativeReceivedItem, 0},
    {"_dog_name", &NativeDogName, 0},
    {"_scars", &NativeScars, 0},
    {"_scar_remove", &NativeScarRemove, 0},
    {"_household", &NativeHousehold, 0},
    {"_by_uid", &NativeByUid, 0},
    {"_end_redecorating", &NativeEndRedecorating, 0},
    {"_followers_two", &NativeFollowersTwo, 0},
    {"_household_context", &NativeHouseholdContext, 0},
    {"_forward_speech", &NativeForwardSpeech, 0},
    {"_partner_screen", &NativePartnerScreen, 0},
    {"_partner_player_icons", &NativePartnerPlayerIcons, 0},
    {"_camera_player_icons", &NativeCameraPlayerIcons, 0},
    {"_partner_camera_update", &NativePartnerCameraUpdate, 0},
    {"_partner_camera_manager", &NativePartnerCameraManager, 0},
    {"_consent_ask", &NativeConsentAsk, 0},
    {"_key_taken", &NativeKeyTaken, 0},
    {"_consent_answer", &NativeConsentAnswer, 0},
    {"ask_partner", &NativeAskPartner, 0},
    {"partner_answer", &NativePartnerAnswer, 0},
    {"key_taken", &NativeKeyTakenPublic, 0},
    {"opinion", &NativeOpinion, 0},
    {"focus", &NativeFocus, 0},
    {"wheel_switches_target", &NativeWheelSwitchesTarget, 0},
    {"coop_mode", &NativeCoopMode, 0},
    {"world_here", &NativeWorldHere, 0},
    {"uid", &NativeUid, 0},
    {"_chest_counted", &NativeChestCounted, 0},
    {"_chest_money", &NativeChestMoney, 0},
    {"_opinion_enter", &NativeOpinionEnter, 0},
    {"_opinion_leave", &NativeOpinionLeave, 0},
    {"_route_post", &NativeRoutePost, 0},
    {"_route_take", &NativeRouteTake, 0},
    {"_ui_seat", &NativeUiSeat, 0},
    {"forgive_crimes", &NativeForgiveCrimes, 0},
    {"discovered_crime", &NativeDiscoveredCrime, 0},
    {"perceived_renown", &NativePerceivedRenown, 0},
    {"renown", &NativeRenown, 0},
    {"morality", &NativeMorality, 0},
    {"purity", &NativePurity, 0},
    {"modify_renown", &NativeModifyRenown, 0},
    {"modify_morality", &NativeModifyMorality, 0},
    {"modify_purity", &NativeModifyPurity, 0},
    {"can_save", &NativeCanSave, 0},
    {"save_game", &NativeSaveGame, 0},
    {"physique", &NativePhysique, 0},
    {"age", &NativeAge, 0},
    {"appearance", &NativeAppearance, 0},
    {"_set_appearance", &NativeSetAppearance, 0},
    {"set_appearance_automatic", &NativeSetAppearanceAutomatic, 0},
    {"morph_get", &NativeMorphGet, 0},
    {"morph_set", &NativeMorphSet, 0},
    {"morph_id", &NativeMorphId, 0},
    {"inventory", &NativeInventory, 0},
    {"equipped", &NativeEquipped, 0},
    {"use_item", &NativeUseItem, 0},
    {"equip_weapon", &NativeEquipWeapon, 0},
    {"perform_expression", &NativePerformExpression, 0},
    {"assign_spell", &NativeAssignSpell, 0},
    {"release_expression", &NativeReleaseExpression, 0},
    {"sprinting", &NativeSprinting, 0},
    {"end_sprint", &NativeEndSprint, 0},
    {"_set_locomotion", &NativeSetLocomotion, 0},
    {"locomotion", &NativeLocomotion, 0},
    {"set_parry", &NativeSetParry, 0},
    {"parry", &NativeParry, 0},
    {"expressions", &NativeExpressions, 0},
    {"spells_known", &NativeSpellsKnown, 0},
    {"selected_expression", &NativeSelectedExpression, 0},
    {"record_string", &NativeRecordString, 0},
    {"record_int", &NativeRecordInt, 0},
    {"record_number", &NativeRecordNumber, 0},
    {"record_bool", &NativeRecordBool, 0},
    {"record_exists", &NativeRecordExists, 0},
    {"record_ref", &NativeRecordRef, 0},
    {"creature_tier", &NativeCreatureTier, 0},
    {"near", &NativeNear, 0},
    {"line_of_sight", &NativeLineOfSight, 0},
    {"spawn", &NativeSpawn, 0},
    {"entity", &NativeEntity, 0},
    {"handle", &NativeHandle, 0},
    {"_play_fx", &NativePlayFx, 0},
    {"stop_fx", &NativeStopFx, 0},
    {"open_menu", &NativeOpenMenu, 0},
    {"_menu_rows", &NativeMenuRows, 0},
    {"open_screen", &NativeOpenScreen, 0},
    {"_message_box", &NativeMessageBox, 0},
    {"notice", &NativeNotice, 0},
    {"_text", &NativeText, 0},
    {"_mark", &NativeMark, 0},
    {"unmark", &NativeUnmark, 0},
    {"_world_meter", &NativeWorldMeter, 0},
    {"mark_meter", &NativeMarkMeter, 0},
    {"fade", &NativeFade, 0},
    {"meter", &NativeMeter, 0},
    {"meter_remove", &NativeMeterRemove, 0},
    {"input_claim", &NativeInputClaim, 0},
    {"press", &NativePress, 0},
    {"charge_spell", &NativeChargeSpell, 0},
    {"tap", &NativeTap, 0},
    {"hud_icon", &NativeHudIcon, 0},
    {"hud_slide", &NativeHudSlide, 0},
    {"hud_hold", &NativeHudHold, 0},
    {"_light", &NativeLight, 0},
    {"light_remove", &NativeLightRemove, 0},
    {"send_to_partner", &NativeSendToPartner, 0},
    {"signal", &NativeSignal, 0},
    {"publish_text", &NativePublishText, 0},
    {"_menu_box", &NativeMenuBox, 0},
    {"_write", &NativeWrite, 0},
    {"_error", &NativeError, 0},
    {"_cancelled", &NativeCancelled, 0},
};

bool AllocateThunks() {
  static bool done = false;
  static bool ok = false;
  if (done) {
    return ok;
  }
  done = true;
  ok = true;
  const uint32_t anchor = refii::guest::Address(kModuleAnchor);
  for (Native& native : g_natives) {
    native.thunk = refii_bridge_rex_allocate_thunk(reinterpret_cast<void*>(native.function), anchor);
    if (native.thunk == 0) {
      REXLOG_WARN("Refii mod Lua: no guest address for Refii.{} -- the natives are not registered", native.name);
      ok = false;
    }
  }
  return ok;
}

}  // namespace

namespace refii::vfs {

void RegisterModLuaNatives(PPCContext& ctx, uint8_t* base, uint32_t state) {
  if (state < 0x10000u || state >= 0xC0000000u || !AllocateThunks()) {
    return;
  }
  PPCContext frame = refii::mods::Frame(ctx, 0x400u);
  Lua lua(frame, base, state);
  const int32_t top = lua.Top();
  lua.GetField(kGlobalsIndex, "Refii");
  if (lua.Type(-1) != kTypeTable) {
    lua.SetTop(-2);
    lua.NewTable();
    lua.SetField(kGlobalsIndex, "Refii");
    lua.GetField(kGlobalsIndex, "Refii");
  }
  for (const Native& native : g_natives) {
    lua.PushClosure(native.thunk);
    lua.SetField(-2, native.name);
  }
  lua.SetTop(top);
  REXLOG_INFO("Refii mod Lua: {} Refii.* natives registered", sizeof(g_natives) / sizeof(g_natives[0]));
}

}  // namespace refii::vfs
