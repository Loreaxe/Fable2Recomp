// refii - Fable 3's half of the native (hard) mod C API: what core/refii_mod.h and core/refii_mod_events.h
// declare and Fable 2 defines in Fable2/src/vfs/src/hooks/mod_*.cpp, so one native mod source links and
// runs on either title. Each call states Fable 2's contract (the header's doc comment) and answers it on
// Fable 3 through, in order of preference: an exported modlua:: / seam:: function, the game VM's own
// Refii.* helper (Fable3CoreLua, the shared preamble, the natives of mod_lua_hooks.cpp) through
// modlua::CallRefii, or a game function called in place. Entities cross to Lua as HANDLES and come back
// as addresses; records (definition IDs) cross as light userdata (LuaValue::Rec).
//
// THREAD AND CONTEXT. CallRefii runs only on the game VM's thread; a call given no PPCContext borrows
// modlua::CurrentContext() (set around the native ticks and the native event handlers). Off that thread,
// or with neither, a call fails as Fable 2's fails (0, false, -1 where Fable 2 answers -1; outs as Fable 2
// leaves them).
//
// This file includes Fable 2's refii_mod.h (the contract) and so its core/guest_build.h, whose
// refii::guest namespace collides with Fable 3's fable3_guest_build.h: Fable 3's build is asked through
// the C entries that header declares (refiii_guest_build, refiii_guest_address), below.

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <initializer_list>
#include <mutex>
#include <string>
#include <vector>

#include <rex/ppc.h>
#include <rex/system/kernel_state.h>
#include <rex/system/xmemory.h>

#include "core/refii_mod.h"
#include "core/refii_mod_bus.h"
#include "core/refii_mod_events.h"
#include "core/refii_mod_requests.h"
#include "mod_lua.h"
#include "mod_seam.h"

// Fable3/src/vfs/include/fable3_guest_build.h's C entries (refiii_vfs).
extern "C" int32_t refiii_guest_build(void);
extern "C" uint32_t refiii_guest_address(uint32_t tu3_address);

namespace {

namespace modlua = refii::fable3::modlua;
namespace seam = refii::fable3::seam;
using modlua::LuaValue;
using Results = std::vector<LuaValue>;

/// A value that differs between the builds: TU3's, or the disc's (refiii_guest_build 1).
template <typename T>
T Pick(T tu3, T disc) {
  return refiii_guest_build() == 1 ? disc : tu3;
}

/// The game's own root, dword_83597034 (disc dword_83556540): the world is *(*(*(*(*root+0xC)+0x1C)+4)+4),
/// the walk Lua_Global_GetLocalHero makes before it reads the world's +0x7C hero holder:
///   TU3  821fb37c lis r11,dword_83597034@ha / 821fb380 lwz r11,dword_83597034@l(r11) /
///        821fb384 lwz r10,0xC(r11) / 821fb388 lwz r9,0x1C(r10) / 821fb38c lwz r8,4(r9) /
///        821fb390 lwz r11,4(r8) / 821fb394 addi r3,r11,0x7C
///   disc 8236d41c lis r11,dword_83556540@ha / 8236d420 lwz r11,dword_83556540@l(r11) /
///        8236d424 lwz r10,0xC(r11) / 8236d428 lwz r9,0x1C(r10) / 8236d42c lwz r8,4(r9) /
///        8236d430 lwz r11,4(r8) / 8236d434 addi r3,r11,0x7C
uint32_t World(const uint8_t* base) {
  const uint32_t root = seam::Word(base, refiii_guest_address(0x83597034u));
  const uint32_t state = seam::Word(base, root + 0xCu);
  const uint32_t holder = seam::Word(base, seam::Word(base, state + 0x1Cu) + 4u);
  const uint32_t world = seam::Word(base, holder + 4u);
  return seam::Plausible(world) ? world : 0u;
}

/// Stats.GetHeroAbilityCostForLevel's body (Lua_Stats_GetHeroAbilityCostForLevel, TU3 0x823E7958 / disc
/// 0x823C6370, the same 20 instructions): r4 the ability type, r5 the level (1-based) -> r3 the cost.
/// r3 is not read (`addi r3,r1,0x60+var_10` before any use) and the record is the type's alone:
///   TU3  823e7964 addi r3,r1,0x60+var_10 / 823e7968 bl sub_823E7888 (r4 r5 kept: the type's
///        "Ability<X>Base" record by sub_823E7688(type), then AbilityGroupRecords[level - 1]) /
///        823e796c lwz r11,0x60+var_10(r1) / 823e7970 cmplwi cr6,r11,0 / 823e7974 beq loc_823E7998 (li r3,0) /
///        823e797c lwz r3,0x60+var_C(r1) / 823e7980 lwz r4,dword_8359838C@l(r11) / 823e7984 bl sub_82349F08
///        (the record's int property by that hash: sub_82D33CA0(rec, hash, 1), 0 when absent)
///   disc 823c637c addi r3,r1,0x60+var_10 / 823c6380 bl sub_823C62A0 / 823c6384 lwz r11,0x60+var_10(r1) /
///        823c6388 cmplwi cr6,r11,0 / 823c638c beq loc_823C63B0 / 823c6394 lwz r3,0x60+var_C(r1) /
///        823c6398 lwz r4,dword_83557844@l(r11) / 823c639c bl sub_82342B48
/// Its game caller, Lua_Stats_IsHeroAbilityAffordable, passes r4 the type and r5 the next level:
///   TU3  823e80b4 addi r5,r31,1 / 823e80b8 mr r4,r28 / 823e80bc mr r3,r29 / 823e80c0 bl (this)
///   disc 823c6acc addi r5,r31,1 / 823c6ad0 mr r4,r28 / 823c6ad4 mr r3,r29 / 823c6ad8 bl (this)
/// Types 1..19 (sub_823E7688's switch: 1 AbilityHeavyWeaponsBase .. 16 AbilityHealthBase, 17..19
/// WeaponLevelAbility).
uint32_t AbilityCostFunction() { return Pick(0x823E7958u, 0x823C6370u); }

uint8_t* KernelBase() {
  rex::system::KernelState* state = rex::system::kernel_state();
  rex::memory::Memory* memory = state != nullptr ? state->memory() : nullptr;
  return memory != nullptr ? memory->virtual_membase() : nullptr;
}

/// An entity (or other guest object) as the handle Lua takes; nil for 0.
LuaValue Handle(uint32_t address) {
  const uint32_t handle = address != 0u ? refii_mod_handle(address) : 0u;
  return handle != 0u ? LuaValue::Num(static_cast<double>(handle)) : LuaValue{};
}

LuaValue Num(double value) { return LuaValue::Num(value); }
LuaValue Bool(bool value) { return LuaValue::Bool(value); }
LuaValue Str(const char* text) { return LuaValue::Str(text != nullptr ? text : ""); }
LuaValue Rec(uint32_t id) { return LuaValue::Rec(id); }

bool Finite(float value) { return std::isfinite(value); }
bool Finite3(const float* xyz) {
  return xyz != nullptr && std::isfinite(xyz[0]) && std::isfinite(xyz[1]) && std::isfinite(xyz[2]);
}

/// Refii.<function>(args...) with `ctx`, or the native callback's context when there is none.
bool Call(PPCContext* ctx, uint8_t* base, const char* function, const std::vector<LuaValue>& args, Results& out) {
  out.clear();
  PPCContext* context = ctx != nullptr ? ctx : modlua::CurrentContext();
  if (context == nullptr || base == nullptr) {
    return false;
  }
  return modlua::CallRefii(*context, base, function, args, out);
}

bool Truthy(const Results& r, size_t i) {
  if (i >= r.size()) {
    return false;
  }
  const LuaValue& v = r[i];
  return v.kind == LuaValue::Kind::Boolean ? v.boolean : v.kind != LuaValue::Kind::Nil;
}

bool Number(const Results& r, size_t i, double& out) {
  if (i >= r.size() || r[i].kind != LuaValue::Kind::Number || !std::isfinite(r[i].number)) {
    return false;
  }
  out = r[i].number;
  return true;
}

int32_t Int(double value) {
  return std::isnan(value) ? 0 : value >= 2147483647.0 ? 2147483647 : value <= -2147483648.0 ? (-2147483647 - 1) : static_cast<int32_t>(value);
}

/// A returned handle as its address, 0 for nil or a stale handle.
uint32_t AddressOf(const Results& r, size_t i) {
  double h = 0.0;
  return Number(r, i, h) && h >= 1.0 && h < 4294967296.0 ? refii_mod_handle_address(static_cast<uint32_t>(h)) : 0u;
}

/// A returned record ID (light userdata), 0 for anything else.
uint32_t RecordOf(const Results& r, size_t i) {
  return i < r.size() && r[i].kind == LuaValue::Kind::Record ? r[i].raw : 0u;
}

void Copy(const std::string& text, char* out, uint32_t size) {
  if (out == nullptr || size == 0u) {
    return;
  }
  const size_t n = std::min<size_t>(text.size(), size - 1u);
  std::memcpy(out, text.data(), n);
  out[n] = 0;
}

/// 1 when Refii.<function> ran and answered true.
int32_t CallTrue(PPCContext* ctx, uint8_t* base, const char* function, const std::vector<LuaValue>& args) {
  Results r;
  return Call(ctx, base, function, args, r) && Truthy(r, 0) ? 1 : 0;
}

/// Three numbers from Refii.<function> into xyz.
int32_t CallXyz(PPCContext* ctx, uint8_t* base, const char* function, uint32_t entity, float* xyz) {
  Results r;
  double x = 0.0, y = 0.0, z = 0.0;
  if (xyz == nullptr || entity == 0u || !Call(ctx, base, function, {Handle(entity)}, r) || !Number(r, 0, x) ||
      !Number(r, 1, y) || !Number(r, 2, z)) {
    return 0;
  }
  xyz[0] = static_cast<float>(x);
  xyz[1] = static_cast<float>(y);
  xyz[2] = static_cast<float>(z);
  return 1;
}

/// The C API's own Lua: helpers that unpack what the Refii.* helpers answer as tables (CallRefii carries
/// numbers, booleans, strings and record IDs only). Defined on Refii once per game VM (its generation).
/// Every loop takes two variables (Fable 3's kpairs, Fable3CoreLua's note).
///   _capi_inventory_stack(e, i)   Refii.inventory(e)[i + 1]: record, count.
///   _capi_inventory_usable(e, i)  that stack's item record's InventoryItemComponent: Category not 0,
///     UsableFromInventory and VisibleInGUI — the game's own test (TU3 sub_82B83E40 / disc 0x82B4F210:
///     `sub_8233AC58(rec, "InventoryItemComponent")`, `sub_8251E6D8(comp, fnv("Category"))`, then
///     `sub_823CE768(comp, "UsableFromInventory") && sub_823CE768(comp, "VisibleInGUI")`). The Lua record
///     calls reach the same reads: RecordPtr:GetRecord (TU3 0x821AE5A8 / disc 0x821AE470) is
///     sub_82D33CA0(rec, fnv(name), 6) resolved by sub_821CF438 as sub_8233AC58's sub_82D33B10 is;
///     RecordPtr:GetBoolean (TU3 0x821B1638 / disc 0x821B17C8) is sub_821B15D0, sub_823CE768's body;
///     RecordPtr:GetS32 (TU3 0x8253FD68 / disc 0x82510870) is sub_82D33CA0(rec, fnv(name), 1), the read
///     sub_8251E6D8 makes. A missing field answers 0 / false in both.
///   _capi_appearance(p)           Refii.appearance(p)'s fields in refii_mod_appearance_t's order.
///   _capi_set_appearance(p, ..)   Refii.set_appearance(p, {..}) (nil keeps an input).
///   _capi_spells_known(p)         Refii.spells_known(p) as a 1 << type mask.
///   _capi_near(x, y, z, r, c, n)  Refii.near(..): the count written, then up to n handles.
constexpr const char* kGlue = R"lua(
function Refii._capi_inventory_stack(e, i)
	local list = Refii.inventory(e)
	local it = list ~= nil and list[i + 1] or nil
	if it == nil then return nil end
	return it.record, it.count
end
function Refii._capi_inventory_usable(e, i)
	local list = Refii.inventory(e)
	local it = list ~= nil and list[i + 1] or nil
	if it == nil or it.record == nil then return false end
	local r = GDB.GetRecord(it.record)
	if r == nil or r:IsNull() then return false end
	local c = r:GetRecord("InventoryItemComponent")
	if c == nil or c:IsNull() then return false end
	return c:GetS32("Category") ~= 0 and c:GetBoolean("UsableFromInventory") and c:GetBoolean("VisibleInGUI") and true or false
end
function Refii._capi_appearance(p)
	local a = Refii.appearance(p)
	if a == nil then return nil end
	return a.automatic and true or false, a.tan or 0, a.fat or 0, a.height or 0, a.strength or 0, a.skill or 0,
		a.will or 0, a.morality or 0, a.purity or 0, a.age or 0
end
function Refii._capi_set_appearance(p, tan, fat, height, strength, skill, will, morality, purity, age)
	if Refii.set_appearance == nil then return false end
	Refii.set_appearance(p, { tan = tan, fat = fat, height = height, strength = strength, skill = skill, will = will,
		morality = morality, purity = purity, age = age })
	return true
end
function Refii._capi_spells_known(p)
	local list = Refii.spells_known(p)
	if list == nil then return nil end
	local mask = 0
	for _, t in ipairs(list) do mask = mask + 2 ^ t end
	return mask
end
function Refii._capi_near(x, y, z, r, c, n)
	local list = Refii.near(x, y, z, r, c)
	local count = math.min(#list, n)
	local unpack = table.unpack or unpack
	return count, unpack(list, 1, count)
end
)lua";

/// Refii._capi_<function>: the glue defined first for this game VM, and again once when the call fails
/// (a VM rebuilt between the generation read and the call).
bool CallGlue(PPCContext* ctx, uint8_t* base, const char* function, const std::vector<LuaValue>& args, Results& out) {
  static uint32_t defined = 0xFFFFFFFFu;
  PPCContext* context = ctx != nullptr ? ctx : modlua::CurrentContext();
  if (context == nullptr || base == nullptr) {
    return false;
  }
  const auto define = [&] {
    const uint32_t generation = refii_mod_lua_generation();
    if (refii_mod_run_game_lua(context, base, kGlue) != 0) {
      defined = generation;
      return true;
    }
    return false;
  };
  if (defined != refii_mod_lua_generation() && !define()) {
    return false;
  }
  if (Call(context, base, function, args, out)) {
    return true;
  }
  return define() && Call(context, base, function, args, out);
}

}  // namespace

// ---------------------------------------------------------------------------------------------
// Entities and players
// ---------------------------------------------------------------------------------------------

// Route: modlua::Hero — player two's hero on an online joiner (modlua::OnlineClient), else player one's.
extern "C" uint32_t refii_mod_local_hero(uint8_t* base) {
  return base != nullptr ? modlua::Hero(base, modlua::OnlineClient() ? 2 : 1) : 0u;
}

// Route: modlua::Hero (the world's hero holders, +0x7C / +0x84).
extern "C" uint32_t refii_mod_player_hero(uint8_t* base, int32_t player) {
  return base != nullptr ? modlua::Hero(base, player) : 0u;
}

// Route: seam::Component (Fable 3's {id, component} pairs at +0x54..+0x58). Ids are Fable 3's, not
// Fable 2's (Fable 3's health is not 36).
extern "C" uint32_t refii_mod_component(uint8_t* base, uint32_t entity, uint32_t id) {
  return base != nullptr ? seam::Component(base, entity, id) : 0u;
}

// Route: Refii.position(e) (e:GetPosition()).
extern "C" int32_t refii_mod_entity_position(PPCContext* ctx, uint8_t* base, uint32_t entity, float* out_xyz) {
  return ctx != nullptr ? CallXyz(ctx, base, "position", entity, out_xyz) : 0;
}

// Route: refii_mod_run_game_lua. Fable 3 has no front-end Lua: its game VM stands from boot.
extern "C" int32_t refii_mod_run_frontend_lua(PPCContext* ctx, uint8_t* base, const char* chunk) {
  if (ctx == nullptr || chunk == nullptr) {
    return 0;
  }
  return refii_mod_run_game_lua(ctx, base, chunk);
}

// Route: Refii.signed_in() (the active pad's user record); -1 with none or no VM.
extern "C" int32_t refii_mod_signed_in_user(uint8_t* base) {
  Results r;
  double user = -1.0;
  return Call(nullptr, base, "signed_in", {}, r) && Number(r, 0, user) ? Int(user) : -1;
}

// Route: Refii.save_count() (the game's own enumeration's last count); -1 before one or with no VM.
extern "C" int32_t refii_mod_save_count(PPCContext* ctx, uint8_t* base) {
  Results r;
  double count = -1.0;
  return ctx != nullptr && Call(ctx, base, "save_count", {}, r) && Number(r, 0, count) ? Int(count) : -1;
}

// Route: splash::Up() — "splash" while the "Press START" splash stands (Refii.screen's answer); Fable 3
// has no Gameface front-end presentations to name.
extern "C" int32_t refii_mod_frontend_screen(PPCContext* ctx, uint8_t* base, char* out, uint32_t size) {
  (void)ctx;
  (void)base;
  if (out == nullptr || size == 0u) {
    return 0;
  }
  const std::string name = refii::fable3::splash::Up() ? "splash" : "";
  Copy(name, out, size);
  return name.empty() ? 0 : 1;
}

// Route: modlua::PlayerOf.
extern "C" int32_t refii_mod_player_of(uint8_t* base, uint32_t entity) {
  return base != nullptr && entity != 0u ? modlua::PlayerOf(base, entity) : 0;
}

// Route: Refii.player_for(e) (a hero itself, a dog's master, else the nearest hero); with no answer the
// one hero that exists (one hero alone is everyone's), 0 while neither does.
extern "C" int32_t refii_mod_player_for(PPCContext* ctx, uint8_t* base, uint32_t entity) {
  if (base == nullptr) {
    return 0;
  }
  Results r;
  double player = 0.0;
  if (entity != 0u && Call(ctx, base, "player_for", {Handle(entity)}, r) && Number(r, 0, player) &&
      (player == 1.0 || player == 2.0)) {
    return static_cast<int32_t>(player);
  }
  return modlua::Hero(base, 1) != 0u ? 1 : (modlua::Hero(base, 2) != 0u ? 2 : 0);
}

// Route: modlua::OnlineClient (the game's Network.IsClient, Refii.world_here's question).
extern "C" int32_t refii_mod_world_here(uint8_t* base) {
  (void)base;
  return modlua::OnlineClient() ? 0 : 1;
}

// Route: Refii.menu_player() (1: Fable 3's menus act for the player this machine plays first).
extern "C" int32_t refii_mod_menu_player(uint8_t* base) {
  Results r;
  double player = 1.0;
  return Call(nullptr, base, "menu_player", {}, r) && Number(r, 0, player) && player == 2.0 ? 2 : 1;
}

// Route: Refii.opinion(villager, p) -> love, fear, attractiveness, morality, respect
// (OpinionReaction.GetCurrentOpinion per axis for player p's hero).
extern "C" int32_t refii_mod_opinion(uint8_t* base, uint32_t villager, int32_t player, float* out) {
  if (out == nullptr || villager == 0u || (player != 1 && player != 2)) {
    return 0;
  }
  Results r;
  double axes[5];
  if (!Call(nullptr, base, "opinion", {Handle(villager), Num(player)}, r)) {
    return 0;
  }
  for (size_t i = 0; i < 5u; ++i) {
    if (!Number(r, i, axes[i])) {
      return 0;
    }
  }
  for (size_t i = 0; i < 5u; ++i) {
    out[i] = static_cast<float>(axes[i]);
  }
  return 1;
}

// Route: Refii.forgive_crimes(villager, p) (Villager.ForgiveHerosCrimes(v, hero)). Fable 3's native takes
// the hero and answers no count: 1 when it ran, for a hero that is a player's.
extern "C" int32_t refii_mod_forgive_crimes(PPCContext* ctx, uint8_t* base, uint32_t villager, uint32_t hero) {
  const int32_t player = base != nullptr ? modlua::PlayerOf(base, hero) : 0;
  if (ctx == nullptr || villager == 0u || player == 0) {
    return 0;
  }
  return CallTrue(ctx, base, "forgive_crimes", {Handle(villager), Num(player)});
}

// Route: Refii.discovered_crime(villager, p) (Villager.HasHeroCommittedCrimeIHaveDiscovered(v), which
// names no hero on Fable 3).
extern "C" int32_t refii_mod_discovered_crime(PPCContext* ctx, uint8_t* base, uint32_t villager, uint32_t hero) {
  const int32_t player = base != nullptr ? modlua::PlayerOf(base, hero) : 0;
  if (ctx == nullptr || villager == 0u || player == 0) {
    return 0;
  }
  return CallTrue(ctx, base, "discovered_crime", {Handle(villager), Num(player)});
}

// Route: Refii.perceived_renown(villager, p) (Villager.GetPerceivedRenownOfHero(v)).
extern "C" int32_t refii_mod_perceived_renown(PPCContext* ctx, uint8_t* base, uint32_t villager, uint32_t hero,
                                              int32_t* out) {
  const int32_t player = base != nullptr ? modlua::PlayerOf(base, hero) : 0;
  Results r;
  double renown = 0.0;
  if (ctx == nullptr || villager == 0u || player == 0 ||
      !Call(ctx, base, "perceived_renown", {Handle(villager), Num(player)}, r) || !Number(r, 0, renown)) {
    return 0;
  }
  if (out != nullptr) {
    *out = Int(renown);
  }
  return 1;
}

// Route: Refii.coop_mode() (Network.IsInCouchGame / IsInLiveGame / IsClient); 0 when it cannot be asked.
extern "C" int32_t refii_mod_coop_mode(uint8_t* base) {
  Results r;
  double mode = 0.0;
  return Call(nullptr, base, "coop_mode", {}, r) && Number(r, 0, mode) ? Int(mode) : 0;
}

// Route: none. Fable 3 keeps one opinion set per villager per hero itself; refii lends no player-two
// scope, so no code runs inside one.
extern "C" int32_t refii_mod_opinion_scope_two(void) { return 0; }

// Route: Refii.focus(p) (Targeting.GetTarget of player p's hero, living).
extern "C" uint32_t refii_mod_focus(uint8_t* base, int32_t player) {
  Results r;
  return Call(nullptr, base, "focus", {Num(player)}, r) ? AddressOf(r, 0) : 0u;
}

// Route: mouselook::StateWheel(hero, 1) (this frame).
extern "C" void refii_mod_wheel_switches_target(uint8_t* base, uint32_t hero) {
  if (base != nullptr && seam::Plausible(hero)) {
    refii::fable3::mouselook::StateWheel(base, hero, 1);
  }
}

// Route: mouselook::StateWheel(hero, 2 sticky / 0 off).
extern "C" void refii_mod_wheel_switches_target_sticky(PPCContext* ctx, uint8_t* base, uint32_t hero, int32_t on) {
  if (ctx != nullptr && base != nullptr && seam::Plausible(hero)) {
    refii::fable3::mouselook::StateWheel(base, hero, on != 0 ? 2 : 0);
  }
}

// Route: seam::Live (Fable 3's in-world bit, +0x94 & 0x40).
extern "C" int32_t refii_mod_entity_alive(uint8_t* base, uint32_t entity) {
  return base != nullptr && seam::Live(base, entity) ? 1 : 0;
}

// Route: the world Lua_Global_GetLocalHero reads (World, above).
extern "C" uint32_t refii_mod_world(uint8_t* base) { return base != nullptr ? World(base) : 0u; }

// ---------------------------------------------------------------------------------------------
// Health, experience, abilities
// ---------------------------------------------------------------------------------------------

// Route: Refii.health(e) -> Health.Get, Health.GetMax.
extern "C" int32_t refii_mod_health(uint8_t* base, uint32_t entity, float* out_current, float* out_max) {
  Results r;
  double current = 0.0, max = 0.0;
  if (entity == 0u || !Call(nullptr, base, "health", {Handle(entity)}, r) || !Number(r, 0, current) ||
      !Number(r, 1, max)) {
    return 0;
  }
  if (out_current != nullptr) {
    *out_current = static_cast<float>(current);
  }
  if (out_max != nullptr) {
    *out_max = static_cast<float>(max);
  }
  return 1;
}

// Route: Refii.set_health(e, v), clamped to [0, max] first. Fable 3's helper goes through Health.Modify,
// so unlike Fable 2's direct write the game's health events do run.
extern "C" int32_t refii_mod_set_health(uint8_t* base, uint32_t entity, float value) {
  float current = 0.0f, max = 0.0f;
  if (std::isnan(value) || refii_mod_health(base, entity, &current, &max) == 0) {
    return 0;
  }
  const float clamped = value < 0.0f ? 0.0f : (value > max ? max : value);
  return CallTrue(nullptr, base, "set_health", {Handle(entity), Num(clamped)});
}

// Route: Refii.ability_level(e, kind) (Stats.GetHeroAbilityLevel); -1 without stats.
extern "C" int32_t refii_mod_ability_level(uint8_t* base, uint32_t entity, int32_t type) {
  Results r;
  double level = -1.0;
  return entity != 0u && Call(nullptr, base, "ability_level", {Handle(entity), Num(type)}, r) && Number(r, 0, level)
             ? Int(level)
             : -1;
}

// Route: Refii.experience(e, pool) (Experience.Get).
extern "C" int32_t refii_mod_experience(uint8_t* base, uint32_t entity, int32_t pool, float* out) {
  Results r;
  double amount = 0.0;
  if (entity == 0u || pool < 0 || !Call(nullptr, base, "experience", {Handle(entity), Num(pool)}, r) ||
      !Number(r, 0, amount)) {
    return 0;
  }
  if (out != nullptr) {
    *out = static_cast<float>(amount);
  }
  return 1;
}

// Route: Lua_Stats_GetHeroAbilityCostForLevel called in place (AbilityCostFunction above): the cost of
// `level` of `type`, which Fable 3 keeps per ability record, not per hero. -1 without a live entity, for
// a type outside 1..19 or a level below 1; 0 when the record states no cost.
extern "C" int32_t refii_mod_ability_cost(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t type, int32_t level) {
  if (ctx == nullptr || base == nullptr || !seam::Live(base, entity) || type < 1 || type > 19 || level < 1) {
    return -1;
  }
  PPCContext work = seam::Frame(*ctx);
  work.r3.u32 = 0u;
  work.r4.s64 = type;
  work.r5.s64 = level;
  if (!seam::CallAt(work, base, AbilityCostFunction())) {
    return -1;
  }
  return static_cast<int32_t>(work.r3.u32);
}

// Route: Refii.give_experience(e, pool, amount) (Experience.Modify).
extern "C" int32_t refii_mod_give_experience(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t pool, float amount) {
  if (ctx == nullptr || entity == 0u || pool < 0 || !Finite(amount)) {
    return 0;
  }
  return CallTrue(ctx, base, "give_experience", {Handle(entity), Num(pool), Num(amount)});
}

// Route: Refii.spend_experience(e, pool, amount) (Experience.SpendExperienceAmounts, six pools).
extern "C" int32_t refii_mod_spend_experience(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t pool, float amount) {
  if (ctx == nullptr || entity == 0u || pool < 0 || pool > 5 || !Finite(amount)) {
    return 0;
  }
  return CallTrue(ctx, base, "spend_experience", {Handle(entity), Num(pool), Num(amount)});
}

// ---------------------------------------------------------------------------------------------
// Spells
// ---------------------------------------------------------------------------------------------

// Route: Refii._cast_spell (Fable3CoreLua: SpellManager.CreateScriptedSpellShot, what the shared
// preamble's Refii.cast_spell calls), `targeted` as a boolean (the helper tests it with `and`, where 0 is
// true). Returns 0: the native's glue (Lua_SpellManager_CreateScriptedSpellShot, TU3 0x82634E48 / disc
// 0x8260C830) drops its worker's spell (`sub_82635560(..)` then `result = 0`) and pushes nothing, and
// the spell factory seam's cast records (mod_combat_hooks.cpp) are not exported, so no route answers
// the spell object made. The cast itself is made.
extern "C" uint32_t refii_mod_cast_spell(PPCContext* ctx, uint8_t* base, uint32_t caster, int32_t type, int32_t targeted,
                                         uint32_t target, int32_t level, const float* pos, const float* dir) {
  return refii_mod_cast_spell_with(ctx, base, caster, type, targeted, target, level, pos, dir, nullptr);
}

// Route: refii_mod_cast_spell. Fable 3's helper takes no stamp: `tag` and `damage_scale` are not
// applied, and a cast `at` an entity or a point is refused (nothing cast) — no route places it.
extern "C" uint32_t refii_mod_cast_spell_with(PPCContext* ctx, uint8_t* base, uint32_t caster, int32_t type,
                                              int32_t targeted, uint32_t target, int32_t level, const float* pos,
                                              const float* dir, const refii_mod_cast_options* options) {
  if (ctx == nullptr || base == nullptr || type < 0 || type > 8 || !seam::Live(base, caster) ||
      (target != 0u && !seam::Live(base, target)) || (pos != nullptr && !Finite3(pos)) ||
      (dir != nullptr && !Finite3(dir)) || (options != nullptr && options->at_kind != REFII_MOD_CAST_AT_NONE)) {
    return 0u;
  }
  Results r;
  Call(ctx, base, "_cast_spell",
       {Handle(caster), Num(type), Bool(targeted != 0), Handle(target), Num(std::clamp(level, 0, 4)), Bool(pos != nullptr),
        Num(pos != nullptr ? pos[0] : 0.0f), Num(pos != nullptr ? pos[1] : 0.0f), Num(pos != nullptr ? pos[2] : 0.0f),
        Bool(dir != nullptr), Num(dir != nullptr ? dir[0] : 0.0f), Num(dir != nullptr ? dir[1] : 0.0f),
        Num(dir != nullptr ? dir[2] : 0.0f)},
       r);
  return 0u;
}

// Route: modlua::SpellSerial (the spell factory seam's records).
extern "C" uint32_t refii_mod_spell_serial(uint8_t* base, uint32_t spell) {
  return base != nullptr ? modlua::SpellSerial(base, spell) : 0u;
}

// Route: none — 0. The spell seam's cast records (mod_combat_hooks.cpp g_casts, LiveSpell) are not
// exported and no Refii.* helper or Lua native answers a caster's live spell.
extern "C" uint32_t refii_mod_live_spell(PPCContext* ctx, uint8_t* base, int32_t type, uint32_t caster) {
  (void)ctx;
  (void)base;
  (void)type;
  (void)caster;
  return 0u;
}

// Route: Refii.spell_in_slot(p, slot) -> main type (SpellManager.GetEntitySpellCastTableItem); -1 empty.
extern "C" int32_t refii_mod_spell_in_slot(uint8_t* base, int32_t player, int32_t slot) {
  Results r;
  double type = -1.0;
  if (slot < 0 || slot > 4 || !Call(nullptr, base, "spell_in_slot", {Num(player), Num(slot)}, r) || !Number(r, 0, type)) {
    return -1;
  }
  return type >= 0.0 && type <= 8.0 ? static_cast<int32_t>(type) : -1;
}

// Route: modlua::SpellParts (the spell seams' records), the origin falling back to the caster's position
// (Refii.position) as Fable 2's and Refii.spell_parts do.
extern "C" int32_t refii_mod_spell_parts(PPCContext* ctx, uint8_t* base, uint32_t serial, refii_mod_cast_parts* out) {
  if (out == nullptr) {
    return 0;
  }
  std::memset(out, 0, sizeof(*out));
  out->type = -1;
  modlua::CastParts parts;
  if (base == nullptr || serial == 0u || !modlua::SpellParts(base, serial, parts)) {
    return 0;
  }
  out->spell = parts.spell;
  out->type = parts.type;
  out->targeted = parts.targeted ? 1 : 0;
  out->level = parts.level;
  out->caster = parts.caster;
  out->caster_player = parts.caster != 0u ? modlua::PlayerOf(base, parts.caster) : 0;
  out->vortex = parts.vortex;
  out->anchor = parts.anchor;
  out->sword_count = std::clamp(parts.sword_count, 0, 8);
  for (int32_t i = 0; i < out->sword_count; ++i) {
    out->swords[i] = parts.swords[i];
  }
  out->exploded = parts.exploded ? 1 : 0;
  if (parts.has_origin) {
    std::memcpy(out->origin, parts.origin, sizeof(out->origin));
    out->has_origin = 1;
  } else if (ctx != nullptr && parts.caster != 0u && refii_mod_entity_position(ctx, base, parts.caster, out->origin) != 0) {
    out->has_origin = 1;
  }
  return 1;
}

// Route: modlua::VortexHeld (the vortex seams' live table and the component's held list).
extern "C" int32_t refii_mod_vortex_held(uint8_t* base, uint32_t serial, uint32_t* out, int32_t max) {
  return base != nullptr && serial != 0u ? modlua::VortexHeld(base, serial, out, out != nullptr ? max : 0) : -1;
}

// Route: Refii._spell_balance(caster, type, power, true) (the factory's balance read on a stand-in
// spell): the targeted class's fields, -1 where Fable 3's class carries none; 0 for a type with none.
extern "C" int32_t refii_mod_spell_balance(PPCContext* ctx, uint8_t* base, uint32_t caster, int32_t type, int32_t level,
                                           refii_mod_cast_balance* out) {
  if (out == nullptr) {
    return 0;
  }
  out->status_seconds = -1.0f;
  out->status_spread = -1.0f;
  out->duration = -1.0f;
  out->swords = -1;
  Results r;
  if (ctx == nullptr || caster == 0u ||
      !Call(ctx, base, "_spell_balance", {Handle(caster), Num(type), Num(std::clamp(level, 0, 4)), Bool(true)}, r) ||
      (r.size() == 1u && r[0].kind == LuaValue::Kind::Boolean)) {
    return 0;
  }
  double v = 0.0;
  bool any = false;
  if (Number(r, 0, v)) {
    out->status_seconds = static_cast<float>(v);
    any = true;
  }
  if (Number(r, 1, v)) {
    out->status_spread = static_cast<float>(v);
    any = true;
  }
  if (Number(r, 2, v)) {
    out->duration = static_cast<float>(v);
    any = true;
  }
  if (Number(r, 3, v)) {
    out->swords = Int(v);
    any = true;
  }
  return any ? 1 : 0;
}

// Route: Refii.host_serial — Fable 3 pairs no casts across machines: the serial is its own.
extern "C" uint32_t refii_mod_host_serial(uint8_t* base, uint32_t serial) {
  (void)base;
  return serial;
}

// Route: Refii.local_serial — as refii_mod_host_serial.
extern "C" uint32_t refii_mod_local_serial(uint8_t* base, uint32_t host_serial) {
  (void)base;
  return host_serial;
}

// Route: Refii.charge_spell(p, type, held) (the purchase test, then Refii._charge_lend).
extern "C" int32_t refii_mod_charge_spell(PPCContext* ctx, uint8_t* base, int32_t player, int32_t type, int32_t held) {
  if (ctx == nullptr || (player != 1 && player != 2)) {
    return 0;
  }
  return CallTrue(ctx, base, "charge_spell", {Num(player), Num(type), Bool(held != 0)});
}

// Route: Refii.assign_spell(p, spell) (SpellManager.SetEntitySpellCastTableItem on every slot); Fable 3's
// types 0..8.
extern "C" int32_t refii_mod_assign_spell(PPCContext* ctx, uint8_t* base, int32_t player, int32_t spell) {
  if (ctx == nullptr || spell < 0 || spell > 8) {
    return 0;
  }
  return CallTrue(ctx, base, "assign_spell", {Num(player), Num(spell)});
}

// Route: glue _capi_spells_known (Refii.spells_known: SpellManager.IsEntitySpellTypePowerLevelPurchased).
extern "C" int32_t refii_mod_spells_known(uint8_t* base, int32_t player, uint32_t* out_mask) {
  Results r;
  double mask = 0.0;
  if ((player != 1 && player != 2) || !CallGlue(nullptr, base, "_capi_spells_known", {Num(player)}, r) ||
      !Number(r, 0, mask)) {
    return 0;
  }
  if (out_mask != nullptr) {
    *out_mask = static_cast<uint32_t>(mask);
  }
  return 1;
}

// ---------------------------------------------------------------------------------------------
// The save
// ---------------------------------------------------------------------------------------------

// Route: Refii.can_save(p) (Player.IsAbleToSaveGame).
extern "C" int32_t refii_mod_can_save(PPCContext* ctx, uint8_t* base, int32_t player) {
  return ctx != nullptr ? CallTrue(ctx, base, "can_save", {Num(player)}) : 0;
}

// Route: Refii.save_game(p) (AutoSaveGame(true, true) where Player.IsAbleToSaveGame allows).
extern "C" int32_t refii_mod_save_game(PPCContext* ctx, uint8_t* base, int32_t player) {
  return ctx != nullptr ? CallTrue(ctx, base, "save_game", {Num(player)}) : 0;
}

// ---------------------------------------------------------------------------------------------
// Identity, placement, appearance
// ---------------------------------------------------------------------------------------------

// Route: Refii.definition(e) (e:GetRecord():GetID(), the record ID; Fable 3's IDs are not FNV-1 hashes).
extern "C" uint32_t refii_mod_entity_definition(uint8_t* base, uint32_t entity) {
  Results r;
  return entity != 0u && Call(nullptr, base, "definition", {Handle(entity)}, r) ? RecordOf(r, 0) : 0u;
}

// Route: Refii.name(e) (e:GetName()).
extern "C" int32_t refii_mod_entity_name(uint8_t* base, uint32_t entity, char* out, uint32_t size) {
  Copy("", out, size);
  Results r;
  if (entity == 0u || !Call(nullptr, base, "name", {Handle(entity)}, r) || r.empty() ||
      r[0].kind != LuaValue::Kind::String || r[0].text.empty()) {
    return 0;
  }
  Copy(r[0].text, out, size);
  return 1;
}

// Route: Refii.relation(a, b) (Faction.IsMyEnemy 4 / IsMyAlly 2 / IsNeutral 1, else 0).
extern "C" int32_t refii_mod_relation(PPCContext* ctx, uint8_t* base, uint32_t entity, uint32_t other) {
  Results r;
  double relation = 0.0;
  return ctx != nullptr && entity != 0u && other != 0u &&
                 Call(ctx, base, "relation", {Handle(entity), Handle(other)}, r) && Number(r, 0, relation)
             ? Int(relation)
             : 0;
}

// Route: Refii.set_position(e, x, y, z) (Physics.TeleportToPosition).
extern "C" int32_t refii_mod_set_position(PPCContext* ctx, uint8_t* base, uint32_t entity, const float* xyz) {
  if (ctx == nullptr || entity == 0u || !Finite3(xyz)) {
    return 0;
  }
  return CallTrue(ctx, base, "set_position", {Handle(entity), Num(xyz[0]), Num(xyz[1]), Num(xyz[2])});
}

// Route: Refii.facing(e) (Physics.GetFacingVector).
extern "C" int32_t refii_mod_facing(PPCContext* ctx, uint8_t* base, uint32_t entity, float* out_xyz) {
  return ctx != nullptr ? CallXyz(ctx, base, "facing", entity, out_xyz) : 0;
}

// Route: Refii.velocity(e) (Physics.GetVelocity).
extern "C" int32_t refii_mod_velocity(PPCContext* ctx, uint8_t* base, uint32_t entity, float* out_xyz) {
  return ctx != nullptr ? CallXyz(ctx, base, "velocity", entity, out_xyz) : 0;
}

// Route: Refii.set_facing(e, x, y, z) (Player.SetFacingVector for a hero, else Physics.SetFacingVector).
extern "C" int32_t refii_mod_set_facing(PPCContext* ctx, uint8_t* base, uint32_t entity, const float* xyz) {
  if (ctx == nullptr || entity == 0u || !Finite3(xyz)) {
    return 0;
  }
  return CallTrue(ctx, base, "set_facing", {Handle(entity), Num(xyz[0]), Num(xyz[1]), Num(xyz[2])});
}

// Route: Refii.entity_tint(e, r, g, b, a) (GraphicAppearance.SetTintColour, 0..255 per lane; alpha unused).
extern "C" int32_t refii_mod_entity_tint(PPCContext* ctx, uint8_t* base, uint32_t entity, float r, float g, float b,
                                         float a) {
  if (ctx == nullptr || entity == 0u || !Finite(r) || !Finite(g) || !Finite(b) || !Finite(a)) {
    return 0;
  }
  return CallTrue(ctx, base, "entity_tint",
                  {Handle(entity), Num(std::max(r, 0.0f)), Num(std::max(g, 0.0f)), Num(std::max(b, 0.0f)), Num(a)});
}

// Route: Refii.entity_untint(e) (SetTintColour 255, 255, 255).
extern "C" int32_t refii_mod_entity_untint(PPCContext* ctx, uint8_t* base, uint32_t entity) {
  return ctx != nullptr && entity != 0u ? CallTrue(ctx, base, "entity_untint", {Handle(entity)}) : 0;
}

// Route: Refii.entity_alpha(e, a) (GraphicAppearance.SetAlpha, clamped 0..1).
extern "C" int32_t refii_mod_entity_alpha(PPCContext* ctx, uint8_t* base, uint32_t entity, float alpha) {
  if (ctx == nullptr || entity == 0u || !Finite(alpha)) {
    return 0;
  }
  return CallTrue(ctx, base, "entity_alpha", {Handle(entity), Num(std::clamp(alpha, 0.0f, 1.0f))});
}

// Route: Refii.entity_scale(e, s) (GraphicAppearance.SetScale; refused without physics).
extern "C" int32_t refii_mod_entity_scale(PPCContext* ctx, uint8_t* base, uint32_t entity, float scale) {
  if (ctx == nullptr || entity == 0u || !Finite(scale) || !(scale > 0.0f)) {
    return 0;
  }
  return CallTrue(ctx, base, "entity_scale", {Handle(entity), Num(scale)});
}

// Route: Refii.entity_saturation_mask(e, on) (GraphicAppearance.SetSaturationMask).
extern "C" int32_t refii_mod_entity_saturation_mask(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t on) {
  return ctx != nullptr && entity != 0u ? CallTrue(ctx, base, "entity_saturation_mask", {Handle(entity), Bool(on != 0)}) : 0;
}

// Route: Refii.entity_drawable(e, on) (GraphicAppearance.SetAsDrawable).
extern "C" int32_t refii_mod_entity_drawable(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t on) {
  return ctx != nullptr && entity != 0u ? CallTrue(ctx, base, "entity_drawable", {Handle(entity), Bool(on != 0)}) : 0;
}

// ---------------------------------------------------------------------------------------------
// The target lock, cutscenes, movement speed
// ---------------------------------------------------------------------------------------------

// Route: modlua::LockTarget (the force byte, on `entity` or on what the targeting holds).
extern "C" int32_t refii_mod_lock_target(PPCContext* ctx, uint8_t* base, int32_t player, uint32_t entity) {
  if (ctx == nullptr || base == nullptr || (player != 1 && player != 2) || (entity != 0u && !seam::Live(base, entity))) {
    return 0;
  }
  return modlua::LockTarget(*ctx, base, player, entity) ? 1 : 0;
}

// Route: modlua::ReleaseLock (the force byte cleared).
extern "C" int32_t refii_mod_release_lock(PPCContext* ctx, uint8_t* base, int32_t player) {
  if (ctx == nullptr || base == nullptr || (player != 1 && player != 2)) {
    return 0;
  }
  return modlua::ReleaseLock(base, player) ? 1 : 0;
}

// Route: Refii.switch_target(p, left) (Targeting.CheckForBetterTargetInDirection); the target held after.
extern "C" uint32_t refii_mod_switch_target(PPCContext* ctx, uint8_t* base, int32_t player, int32_t left) {
  Results r;
  return ctx != nullptr && Call(ctx, base, "switch_target", {Num(player), Bool(left != 0)}, r) ? AddressOf(r, 0) : 0u;
}

// Route: modlua::LockedTarget.
extern "C" uint32_t refii_mod_locked_target(PPCContext* ctx, uint8_t* base, int32_t player) {
  if (ctx == nullptr || base == nullptr || (player != 1 && player != 2)) {
    return 0u;
  }
  return modlua::LockedTarget(*ctx, base, player);
}

// Route: modlua::InCutscene (player 0: either).
extern "C" int32_t refii_mod_in_cutscene(PPCContext* ctx, uint8_t* base, int32_t player) {
  if (ctx == nullptr || base == nullptr || player < 0 || player > 2) {
    return 0;
  }
  return modlua::InCutscene(*ctx, base, player);
}

// Route: Refii.set_speed_multiplier(p, f) (Player.SetOverriddenMovementSpeedMultiplier, then
// Player.SetPlayerMovementSpeedOverridden(true)).
extern "C" int32_t refii_mod_set_speed_multiplier(uint8_t* base, int32_t player, float factor) {
  if (!Finite(factor) || factor < 0.0f) {
    return 0;
  }
  return CallTrue(nullptr, base, "set_speed_multiplier", {Num(player), Num(factor)});
}

// Route: Refii.clear_speed_multiplier(p) (Player.SetPlayerMovementSpeedOverridden(false)).
extern "C" int32_t refii_mod_clear_speed_multiplier(uint8_t* base, int32_t player) {
  return CallTrue(nullptr, base, "clear_speed_multiplier", {Num(player)});
}

// Route: Refii.speed_multiplier(p) -> overridden (Refii._speed_overridden), factor
// (Player.GetOverriddenMovementSpeedMultiplier). 0 with `out` untouched without the player component.
extern "C" int32_t refii_mod_speed_multiplier(uint8_t* base, int32_t player, float* out) {
  Results r;
  double factor = 0.0;
  if (!Call(nullptr, base, "speed_multiplier", {Num(player)}, r) || r.size() < 2u || !Number(r, 1, factor)) {
    return 0;
  }
  if (out != nullptr) {
    *out = static_cast<float>(factor);
  }
  return Truthy(r, 0) ? 1 : 0;
}

// ---------------------------------------------------------------------------------------------
// Actions
// ---------------------------------------------------------------------------------------------

// Route: modlua::CurrentAction.
extern "C" uint32_t refii_mod_current_action(uint8_t* base, uint32_t entity) {
  return base != nullptr ? modlua::CurrentAction(base, entity) : 0u;
}

// Route: modlua::ActionElapsed (the action's slot 7).
extern "C" int32_t refii_mod_action_elapsed(PPCContext* ctx, uint8_t* base, uint32_t action, float* out) {
  float seconds = 0.0f;
  if (ctx == nullptr || base == nullptr || !modlua::ActionElapsed(*ctx, base, action, seconds)) {
    return 0;
  }
  if (out != nullptr) {
    *out = seconds;
  }
  return 1;
}

// Route: modlua::ActionInterruptible.
extern "C" int32_t refii_mod_action_interruptible(uint8_t* base, uint32_t action) {
  return base != nullptr && modlua::ActionInterruptible(base, action) ? 1 : 0;
}

// Route: modlua::AllowActionInterrupt.
extern "C" int32_t refii_mod_allow_action_interrupt(uint8_t* base, uint32_t action) {
  return base != nullptr && modlua::AllowActionInterrupt(base, action) ? 1 : 0;
}

// Route: modlua::AllowActionInterruptAt.
extern "C" int32_t refii_mod_allow_action_interrupt_at(PPCContext* ctx, uint8_t* base, uint32_t action, float seconds) {
  return ctx != nullptr && base != nullptr && Finite(seconds) && modlua::AllowActionInterruptAt(*ctx, base, action, seconds)
             ? 1
             : 0;
}

// Route: Refii.action_type's answer, -1: Fable 3's actions state no EScriptableAction (no factory seam).
extern "C" int32_t refii_mod_action_type(uint8_t* base, uint32_t action) {
  (void)base;
  (void)action;
  return -1;
}

// Route: Refii.action_name(a) (EScriptableAction's key for Refii.action_type), "" and 0 without one.
extern "C" int32_t refii_mod_action_name(uint8_t* base, uint32_t action, char* out, uint32_t size) {
  Copy("", out, size);
  Results r;
  if (action == 0u || !Call(nullptr, base, "action_name", {Handle(action)}, r) || r.empty() ||
      r[0].kind != LuaValue::Kind::String || r[0].text.empty()) {
    return 0;
  }
  Copy(r[0].text, out, size);
  return 1;
}

// Route: none — "". Fable 3's EScriptableAction is not Fable 2's (its LOOP is 49, Fable 2's 48), and no
// table of its names is kept natively; Refii.action_name reads the game's own enum in Lua.
extern "C" const char* refii_mod_scriptable_action_name(int32_t type) {
  (void)type;
  return "";
}

// Route: modlua::Living (live, and above zero health when it has a health).
extern "C" int32_t refii_mod_entity_living(uint8_t* base, uint32_t entity) {
  return base != nullptr && modlua::Living(base, entity) ? 1 : 0;
}

// ---------------------------------------------------------------------------------------------
// Statuses, hits
// ---------------------------------------------------------------------------------------------

// Route: Refii.apply_status(e, kind, seconds, level, source) (the spells' own installers; a vortex refused).
extern "C" int32_t refii_mod_apply_status(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t kind,
                                          float seconds, int32_t level, uint32_t source) {
  if (ctx == nullptr || entity == 0u || kind < REFII_STATUS_ELECTRIFIED || kind > REFII_STATUS_BURNING ||
      !Finite(seconds) || !(seconds > 0.0f)) {
    return 0;
  }
  return CallTrue(ctx, base, "apply_status",
                  {Handle(entity), Num(kind), Num(seconds), Num(std::clamp(level, 0, 4)), Handle(source)});
}

// Route: Refii.clear_status(e, kind) (lhEntity_RemoveComponent); a vortex refused, as Fable 2's.
extern "C" int32_t refii_mod_clear_status(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t kind) {
  if (ctx == nullptr || entity == 0u || kind < REFII_STATUS_ELECTRIFIED || kind > REFII_STATUS_BURNING) {
    return 0;
  }
  return CallTrue(ctx, base, "clear_status", {Handle(entity), Num(kind)});
}

// Route: Refii.has_status(e, kind) (the status's component). Game VM thread only on Fable 3.
extern "C" int32_t refii_mod_has_status(uint8_t* base, uint32_t entity, int32_t kind) {
  return entity != 0u ? CallTrue(nullptr, base, "has_status", {Handle(entity), Num(kind)}) : 0;
}

// Route: Refii.knockback(e, dx, dy, dz, force) (Hittable.Hit, Knockdown, unblockable, no damage).
extern "C" int32_t refii_mod_knockback(PPCContext* ctx, uint8_t* base, uint32_t entity, const float* dir, float force) {
  if (ctx == nullptr || entity == 0u || !Finite3(dir) || !Finite(force)) {
    return 0;
  }
  return CallTrue(ctx, base, "knockback", {Handle(entity), Num(dir[0]), Num(dir[1]), Num(dir[2]), Num(force)});
}

// Route: Refii.stagger(e) (Hittable.Hit, no knockdown, no damage).
extern "C" int32_t refii_mod_stagger(PPCContext* ctx, uint8_t* base, uint32_t entity) {
  return ctx != nullptr && entity != 0u ? CallTrue(ctx, base, "stagger", {Handle(entity)}) : 0;
}

// ---------------------------------------------------------------------------------------------
// Sound, music, animation
// ---------------------------------------------------------------------------------------------

// Route: Refii.play_sound(cue, e, tag) (Sound.PlayEvent).
extern "C" int32_t refii_mod_play_sound_on(PPCContext* ctx, uint8_t* base, uint32_t entity, const char* cue,
                                           const char* tag) {
  if (ctx == nullptr || entity == 0u || cue == nullptr || cue[0] == 0) {
    return 0;
  }
  return CallTrue(ctx, base, "play_sound", {Str(cue), Handle(entity), Str(tag)});
}

// Route: Refii.play_sound_at(cue, x, y, z, tag) (Refii._play_sound_at, eight point players kept).
extern "C" int32_t refii_mod_play_sound_at(PPCContext* ctx, uint8_t* base, const float* xyz, const char* cue,
                                           const char* tag) {
  if (ctx == nullptr || !Finite3(xyz) || cue == nullptr || cue[0] == 0) {
    return 0;
  }
  return CallTrue(ctx, base, "play_sound_at", {Str(cue), Num(xyz[0]), Num(xyz[1]), Num(xyz[2]), Str(tag)});
}

// Route: Refii.stop_sound(e or 0, tag, fade) (Sound.StopSoundCategoryPlaying; 0: the point players).
extern "C" int32_t refii_mod_stop_sound(PPCContext* ctx, uint8_t* base, uint32_t entity, const char* tag,
                                        int32_t fade_ms) {
  if (ctx == nullptr || tag == nullptr) {
    return 0;
  }
  return CallTrue(ctx, base, "stop_sound", {entity != 0u ? Handle(entity) : Num(0.0), Str(tag), Num(fade_ms)});
}

// Route: Refii.play_music(name) (SoundTools.PlayMusic, its handle kept by name).
extern "C" int32_t refii_mod_play_music(PPCContext* ctx, uint8_t* base, const char* name) {
  return ctx != nullptr && name != nullptr && name[0] != 0 ? CallTrue(ctx, base, "play_music", {Str(name)}) : 0;
}

// Route: Refii.stop_music(name) (SoundTools.StopMusic on the handle play_music kept).
extern "C" int32_t refii_mod_stop_music(PPCContext* ctx, uint8_t* base, const char* name) {
  return ctx != nullptr && name != nullptr && name[0] != 0 ? CallTrue(ctx, base, "stop_music", {Str(name)}) : 0;
}

// Route: Refii.play_animation(e, name, loops, blend, speed) (Action.SetCurrentAction PLAY_ANIMATION / LOOP).
extern "C" int32_t refii_mod_play_animation(PPCContext* ctx, uint8_t* base, uint32_t entity, const char* name,
                                            int32_t loops, float blend_seconds, float speed) {
  if (ctx == nullptr || entity == 0u || name == nullptr || name[0] == 0 || !Finite(blend_seconds) || !Finite(speed) ||
      !(speed > 0.0f)) {
    return 0;
  }
  return CallTrue(ctx, base, "play_animation",
                  {Handle(entity), Str(name), Num(loops), Num(std::max(blend_seconds, 0.0f)), Num(speed)});
}

// Route: Refii.stop_animation(e) (Action.FinishScriptedActions).
extern "C" int32_t refii_mod_stop_animation(PPCContext* ctx, uint8_t* base, uint32_t entity) {
  return ctx != nullptr && entity != 0u ? CallTrue(ctx, base, "stop_animation", {Handle(entity)}) : 0;
}

// ---------------------------------------------------------------------------------------------
// Opinion, deeds, NPC orders
// ---------------------------------------------------------------------------------------------

// Route: Refii.modify_opinion(villager, hero, axis, delta) (OpinionReaction.ModifyAxisValue, per hero).
extern "C" int32_t refii_mod_modify_opinion(PPCContext* ctx, uint8_t* base, uint32_t villager, uint32_t hero,
                                            int32_t axis, float delta) {
  if (ctx == nullptr || villager == 0u || hero == 0u || axis < 0 || axis > 4 || !Finite(delta)) {
    return 0;
  }
  return CallTrue(ctx, base, "modify_opinion", {Handle(villager), Handle(hero), Num(axis), Num(delta)});
}

// Route: Refii.add_deed(villager, hero, deed) (MessageEvents.PostMessage MESSAGE_EVENT_DEED_PERSON);
// `deed` is the Deed record's ID (Fable 3's IDs are not FNV-1 of the name).
extern "C" int32_t refii_mod_add_deed(PPCContext* ctx, uint8_t* base, uint32_t villager, uint32_t hero, uint32_t deed) {
  if (ctx == nullptr || villager == 0u || hero == 0u || deed == 0u) {
    return 0;
  }
  return CallTrue(ctx, base, "add_deed", {Handle(villager), Handle(hero), Rec(deed)});
}

// Route: Refii.npc_follow(e, t, radius) (Follow.FollowEntity; radius <= 0 the quests' 2).
extern "C" int32_t refii_mod_npc_follow(PPCContext* ctx, uint8_t* base, uint32_t entity, uint32_t target, float radius) {
  if (ctx == nullptr || entity == 0u || target == 0u || entity == target || !Finite(radius)) {
    return 0;
  }
  return CallTrue(ctx, base, "npc_follow", {Handle(entity), Handle(target), Num(radius > 0.0f ? radius : 2.0f)});
}

// Route: Refii.npc_go_to(e, x, y, z) (ScriptFunction.StartScriptControlledMovement, not waited for).
extern "C" int32_t refii_mod_npc_go_to(PPCContext* ctx, uint8_t* base, uint32_t entity, const float* xyz) {
  if (ctx == nullptr || entity == 0u || !Finite3(xyz)) {
    return 0;
  }
  return CallTrue(ctx, base, "npc_go_to", {Handle(entity), Num(xyz[0]), Num(xyz[1]), Num(xyz[2])});
}

// Route: Refii.npc_flee(e, from) (Navigation.FleeFromEntity, sprinting).
extern "C" int32_t refii_mod_npc_flee(PPCContext* ctx, uint8_t* base, uint32_t entity, uint32_t from) {
  if (ctx == nullptr || entity == 0u || from == 0u || entity == from) {
    return 0;
  }
  return CallTrue(ctx, base, "npc_flee", {Handle(entity), Handle(from)});
}

// Route: Refii.npc_attack(e, t) (CombatRegister:SetScriptTarget(e, t, true, false)).
extern "C" int32_t refii_mod_npc_attack(PPCContext* ctx, uint8_t* base, uint32_t entity, uint32_t target) {
  if (ctx == nullptr || entity == 0u || target == 0u || entity == target) {
    return 0;
  }
  return CallTrue(ctx, base, "npc_attack", {Handle(entity), Handle(target)});
}

// Route: Refii.npc_set_hostile(e, other, on) (Faction.Add/RemoveTemporaryEntityRelationship).
extern "C" int32_t refii_mod_npc_set_hostile(PPCContext* ctx, uint8_t* base, uint32_t entity, uint32_t other, int32_t on) {
  if (ctx == nullptr || entity == 0u || other == 0u || entity == other) {
    return 0;
  }
  return CallTrue(ctx, base, "npc_set_hostile", {Handle(entity), Handle(other), Bool(on != 0)});
}

// Route: Refii.npc_stop(e) (Follow.StopFollowing, StopScriptControlledMovement, ClearScriptTarget).
extern "C" int32_t refii_mod_npc_stop(PPCContext* ctx, uint8_t* base, uint32_t entity) {
  return ctx != nullptr && entity != 0u ? CallTrue(ctx, base, "npc_stop", {Handle(entity)}) : 0;
}

// ---------------------------------------------------------------------------------------------
// Time, weather, the level
// ---------------------------------------------------------------------------------------------

// Route: Refii.set_time_of_day(hours) (Timing.SetTimeOfDay), clamped to 0..24.
extern "C" int32_t refii_mod_set_time_of_day(PPCContext* ctx, uint8_t* base, float hours) {
  if (ctx == nullptr || !Finite(hours)) {
    return 0;
  }
  return CallTrue(ctx, base, "set_time_of_day", {Num(std::clamp(hours, 0.0f, 24.0f))});
}

// Route: Refii.time_of_day() (Timing.GetTimeOfDay); false and 0 without an answer.
extern "C" int32_t refii_mod_time_of_day(uint8_t* base, float* hours) {
  Results r;
  double value = 0.0;
  const bool read = Call(nullptr, base, "time_of_day", {}, r) && Number(r, 0, value);
  if (hours != nullptr) {
    *hours = read ? static_cast<float>(value) : 0.0f;
  }
  return read ? 1 : 0;
}

// Route: Refii.set_weather(theme, weight, seconds) (EnvironmentTheme.BlendToEnvironmentTheme).
extern "C" int32_t refii_mod_set_weather(PPCContext* ctx, uint8_t* base, const char* theme, float weight, float seconds) {
  if (ctx == nullptr || theme == nullptr || theme[0] == 0 || !Finite(weight) || !Finite(seconds) || seconds < 0.0f) {
    return 0;
  }
  return CallTrue(ctx, base, "set_weather", {Str(theme), Num(std::clamp(weight, 0.0f, 1.0f)), Num(seconds)});
}

// Route: Refii.modify_health(e, delta, attacker) (Health.Modify).
extern "C" int32_t refii_mod_modify_health(PPCContext* ctx, uint8_t* base, uint32_t entity, float delta, uint32_t attacker) {
  if (ctx == nullptr || entity == 0u || std::isnan(delta)) {
    return 0;
  }
  return CallTrue(ctx, base, "modify_health", {Handle(entity), Num(delta), Handle(attacker)});
}

// Route: Refii.level_name() (GetLevelLoaded's level).
extern "C" int32_t refii_mod_level_name(uint8_t* base, char* out, uint32_t size) {
  Copy("", out, size);
  Results r;
  if (!Call(nullptr, base, "level_name", {}, r) || r.empty() || r[0].kind != LuaValue::Kind::String || r[0].text.empty()) {
    return 0;
  }
  Copy(r[0].text, out, size);
  return 1;
}

// ---------------------------------------------------------------------------------------------
// Gold, items, hero stats
// ---------------------------------------------------------------------------------------------

// Route: Refii.gold(e) (Money.Get).
extern "C" int32_t refii_mod_gold(uint8_t* base, uint32_t entity, int32_t* out) {
  Results r;
  double gold = 0.0;
  if (entity == 0u || !Call(nullptr, base, "gold", {Handle(entity)}, r) || !Number(r, 0, gold)) {
    return 0;
  }
  if (out != nullptr) {
    *out = Int(gold);
  }
  return 1;
}

// Route: Refii.give_gold(e, delta) (Money.Add INCOME_TYPE_SCRIPT / Money.Remove OUTGOING_TYPE_OTHER).
extern "C" int32_t refii_mod_give_gold(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t delta) {
  return ctx != nullptr && entity != 0u ? CallTrue(ctx, base, "give_gold", {Handle(entity), Num(delta)}) : 0;
}

// Route: Refii.item_count(e, def) (Inventory.GetNumberOfItemsOfType); `definition` a record ID.
extern "C" int32_t refii_mod_item_count(uint8_t* base, uint32_t entity, uint32_t definition) {
  Results r;
  double count = 0.0;
  return entity != 0u && definition != 0u && Call(nullptr, base, "item_count", {Handle(entity), Rec(definition)}, r) &&
                 Number(r, 0, count)
             ? Int(count)
             : 0;
}

// Route: Refii.add_item(e, def, 1) (Inventory.AddItemOfType). Returns 0: Fable 3's add answers no item
// object to Lua and the item crosses only as an InventoryItemPtr userdata, which CallRefii does not
// carry; the item is added (refii_mod_item_count reads it).
extern "C" uint32_t refii_mod_add_item(PPCContext* ctx, uint8_t* base, uint32_t entity, uint32_t definition) {
  Results r;
  if (ctx != nullptr && entity != 0u && definition != 0u) {
    Call(ctx, base, "add_item", {Handle(entity), Rec(definition), Num(1.0)}, r);
  }
  return 0u;
}

// Route: Refii.add_item(e, def, n) (n adds of Inventory.AddItemOfType), n in 1..100; how many it made.
// `out_last` is 0: no item object crosses (refii_mod_add_item).
extern "C" int32_t refii_mod_add_items(PPCContext* ctx, uint8_t* base, uint32_t entity, uint32_t definition, int32_t count,
                                       uint32_t* out_last) {
  if (out_last != nullptr) {
    *out_last = 0u;
  }
  Results r;
  double added = 0.0;
  if (ctx == nullptr || entity == 0u || definition == 0u || count < 1 ||
      !Call(ctx, base, "add_item", {Handle(entity), Rec(definition), Num(std::min(count, 100))}, r) ||
      !Number(r, 0, added)) {
    return 0;
  }
  return Int(added);
}

// Route: Refii.renown(e) (Stats.GetRenownTotal).
extern "C" int32_t refii_mod_renown(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t* out) {
  Results r;
  double renown = 0.0;
  if (ctx == nullptr || entity == 0u || !Call(ctx, base, "renown", {Handle(entity)}, r) || !Number(r, 0, renown)) {
    return 0;
  }
  if (out != nullptr) {
    *out = Int(renown);
  }
  return 1;
}

// Route: Refii.morality(e) (Stats.GetMorality).
extern "C" int32_t refii_mod_morality(PPCContext* ctx, uint8_t* base, uint32_t entity, float* out) {
  Results r;
  double value = 0.0;
  if (ctx == nullptr || entity == 0u || !Call(ctx, base, "morality", {Handle(entity)}, r) || !Number(r, 0, value)) {
    return 0;
  }
  if (out != nullptr) {
    *out = static_cast<float>(value);
  }
  return 1;
}

// Route: Refii.purity(e) (Stats.GetPrimal, Fable 3's purity axis).
extern "C" int32_t refii_mod_purity(PPCContext* ctx, uint8_t* base, uint32_t entity, float* out) {
  Results r;
  double value = 0.0;
  if (ctx == nullptr || entity == 0u || !Call(ctx, base, "purity", {Handle(entity)}, r) || !Number(r, 0, value)) {
    return 0;
  }
  if (out != nullptr) {
    *out = static_cast<float>(value);
  }
  return 1;
}

// Route: Refii.modify_renown(e, delta) (Stats.ModifyRenown).
extern "C" int32_t refii_mod_modify_renown(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t delta) {
  return ctx != nullptr && entity != 0u ? CallTrue(ctx, base, "modify_renown", {Handle(entity), Num(delta)}) : 0;
}

// Route: Refii.modify_morality(e, delta) (Stats.ModifyMorality).
extern "C" int32_t refii_mod_modify_morality(PPCContext* ctx, uint8_t* base, uint32_t entity, float delta) {
  return ctx != nullptr && entity != 0u && Finite(delta)
             ? CallTrue(ctx, base, "modify_morality", {Handle(entity), Num(delta)})
             : 0;
}

// Route: Refii.modify_purity(e, delta) (Stats.ModifyPrimal).
extern "C" int32_t refii_mod_modify_purity(PPCContext* ctx, uint8_t* base, uint32_t entity, float delta) {
  return ctx != nullptr && entity != 0u && Finite(delta) ? CallTrue(ctx, base, "modify_purity", {Handle(entity), Num(delta)})
                                                         : 0;
}

// Route: Refii.physique(e) (Stats.GetFat; Fable 3 states no height: 0).
extern "C" int32_t refii_mod_physique(uint8_t* base, uint32_t entity, float* out_fat, float* out_height) {
  Results r;
  double fat = 0.0;
  if (entity == 0u || !Call(nullptr, base, "physique", {Handle(entity)}, r) || !Number(r, 0, fat)) {
    return 0;
  }
  double height = 0.0;
  if (out_fat != nullptr) {
    *out_fat = static_cast<float>(fat);
  }
  if (out_height != nullptr) {
    *out_height = Number(r, 1, height) ? static_cast<float>(height) : 0.0f;
  }
  return 1;
}

// Route: Refii.age(e) (Age.GetAge).
extern "C" int32_t refii_mod_age(uint8_t* base, uint32_t entity, float* out) {
  Results r;
  double age = 0.0;
  if (entity == 0u || !Call(nullptr, base, "age", {Handle(entity)}, r) || !Number(r, 0, age)) {
    return 0;
  }
  if (out != nullptr) {
    *out = static_cast<float>(age);
  }
  return 1;
}

// Route: glue _capi_appearance (Refii.appearance: the game's own stats, automatic; Fable 3 has no Mirror).
extern "C" int32_t refii_mod_appearance(PPCContext* ctx, uint8_t* base, int32_t player, refii_mod_appearance_t* out) {
  Results r;
  if (ctx == nullptr || (player != 1 && player != 2) || !CallGlue(ctx, base, "_capi_appearance", {Num(player)}, r) ||
      r.size() < 10u) {
    return 0;
  }
  refii_mod_appearance_t look{};
  look.automatic = Truthy(r, 0) ? 1 : 0;
  float* const fields[] = {&look.tan, &look.fat, &look.height, &look.strength, &look.skill,
                           &look.will, &look.morality, &look.purity, &look.age};
  for (size_t i = 0; i < 9u; ++i) {
    double v = 0.0;
    *fields[i] = Number(r, i + 1u, v) ? static_cast<float>(v) : 0.0f;
  }
  if (out != nullptr) {
    *out = look;
  }
  return 1;
}

// Route: glue _capi_set_appearance -> Refii.set_appearance(p, {..}) (the shared preamble's, through
// Refii._set_appearance: Fable 3 registers no such native, so the look is not changed until one exists).
// A NaN input crosses as nil (kept).
extern "C" void refii_mod_set_appearance(PPCContext* ctx, uint8_t* base, int32_t player, const refii_mod_appearance_t* inputs) {
  if (ctx == nullptr || inputs == nullptr || (player != 1 && player != 2)) {
    return;
  }
  const auto In = [](float v) { return std::isnan(v) ? LuaValue{} : Num(v); };
  Results r;
  CallGlue(ctx, base, "_capi_set_appearance",
           {Num(player), In(inputs->tan), In(inputs->fat), In(inputs->height), In(inputs->strength), In(inputs->skill),
            In(inputs->will), In(inputs->morality), In(inputs->purity), In(inputs->age)},
           r);
}

// Route: none. Fable 3's look is always the game's own (automatic); there are no manual inputs to switch to.
extern "C" void refii_mod_set_appearance_automatic(PPCContext* ctx, uint8_t* base, int32_t player, int32_t on) {
  (void)ctx;
  (void)base;
  (void)player;
  (void)on;
}

// Route: glue _capi_inventory_stack (Refii.inventory: Inventory.GetItemByIndex's stacks, in order).
extern "C" int32_t refii_mod_inventory_stack(uint8_t* base, uint32_t entity, int32_t index, uint32_t* out_definition,
                                             int32_t* out_count) {
  Results r;
  double count = 0.0;
  if (entity == 0u || index < 0 ||
      !CallGlue(nullptr, base, "_capi_inventory_stack", {Handle(entity), Num(index)}, r) || !Number(r, 1, count)) {
    return 0;
  }
  if (out_definition != nullptr) {
    *out_definition = RecordOf(r, 0);
  }
  if (out_count != nullptr) {
    *out_count = Int(count);
  }
  return 1;
}

// Route: glue _capi_inventory_usable (the stack's item record's InventoryItemComponent: Category,
// UsableFromInventory, VisibleInGUI — the game's own test, verified in kGlue's comment).
extern "C" int32_t refii_mod_inventory_usable(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t index) {
  Results r;
  return ctx != nullptr && entity != 0u && index >= 0 &&
                 CallGlue(ctx, base, "_capi_inventory_usable", {Handle(entity), Num(index)}, r) && Truthy(r, 0)
             ? 1
             : 0;
}

// Route: Refii.equipped(e) (Carrying.GetMeleeWeaponInAnySlot / GetRangedWeaponInAnySlot record IDs);
// 0 without Carrying (the helper's lone nil).
extern "C" int32_t refii_mod_equipped(uint8_t* base, uint32_t entity, uint32_t* out_melee, uint32_t* out_ranged) {
  Results r;
  if (entity == 0u || !Call(nullptr, base, "equipped", {Handle(entity)}, r) || r.size() < 2u) {
    return 0;
  }
  if (out_melee != nullptr) {
    *out_melee = RecordOf(r, 0);
  }
  if (out_ranged != nullptr) {
    *out_ranged = RecordOf(r, 1);
  }
  return 1;
}

// ---------------------------------------------------------------------------------------------
// The quick actions, expressions, sprint, locomotion, parry
// ---------------------------------------------------------------------------------------------

namespace {
/// One quick action through its Refii.* helper (kinds 1 use_item, 2 perform_expression, 3 equip_weapon,
/// 4 assign_spell). Fable 3's helpers keep no quick-menu gate (REFII_QUICK_UNGATED is moot) and its
/// expressions no hold (REFII_QUICK_HELD plays the tap).
int32_t Quick(PPCContext* ctx, uint8_t* base, int32_t player, int32_t kind, uint32_t record) {
  if (ctx == nullptr || (player != 1 && player != 2)) {
    return 0;
  }
  switch (kind) {
    case 1:
      return record != 0u ? CallTrue(ctx, base, "use_item", {Num(player), Rec(record)}) : 0;
    case 2:
      return record != 0u ? CallTrue(ctx, base, "perform_expression", {Num(player), Rec(record)}) : 0;
    case 3:
      return record != 0u ? CallTrue(ctx, base, "equip_weapon", {Num(player), Rec(record)}) : 0;
    case 4:
      return record <= 8u ? CallTrue(ctx, base, "assign_spell", {Num(player), Num(record)}) : 0;
    default:
      return 0;
  }
}
}  // namespace

// Route: Refii.use_item(p, def) (Inventory.GetItemOfType, Refii._use_item -> lhEntity_UseInventoryItem).
extern "C" int32_t refii_mod_use_item(PPCContext* ctx, uint8_t* base, int32_t player, uint32_t definition) {
  return Quick(ctx, base, player, 1, definition);
}

// Route: Refii.equip_weapon(p, def) (WeaponSetManager.EquipWeaponOfType, for a weapon held).
extern "C" int32_t refii_mod_equip_weapon(PPCContext* ctx, uint8_t* base, int32_t player, uint32_t definition) {
  return Quick(ctx, base, player, 3, definition);
}

// Route: Refii.perform_expression(p, expr) (ExpressionPerformer.PerformExpression; true while it plays).
extern "C" int32_t refii_mod_perform_expression(PPCContext* ctx, uint8_t* base, int32_t player, uint32_t expression) {
  return Quick(ctx, base, player, 2, expression);
}

// Route: Quick (the helpers above), `flags` as noted there.
extern "C" int32_t refii_mod_quick_action(PPCContext* ctx, uint8_t* base, int32_t player, int32_t kind, uint32_t record,
                                          uint32_t flags) {
  (void)flags;
  return Quick(ctx, base, player, kind, record);
}

// Route: Refii._expression(p, i) -> record ID, extendable (the performer's known list, component 177).
// Fable 3 keeps no use count: `out_uses` 0.
extern "C" int32_t refii_mod_expression(uint8_t* base, int32_t player, int32_t index, uint32_t* out_record,
                                        int32_t* out_uses) {
  Results r;
  if ((player != 1 && player != 2) || index < 0 || !Call(nullptr, base, "_expression", {Num(player), Num(index)}, r) ||
      RecordOf(r, 0) == 0u) {
    return 0;
  }
  if (out_record != nullptr) {
    *out_record = RecordOf(r, 0);
  }
  if (out_uses != nullptr) {
    *out_uses = 0;
  }
  return 1;
}

// Route: none — 0. Fable 3 has no expression wheel (its expressions are the interaction choices).
extern "C" uint32_t refii_mod_selected_expression(uint8_t* base, int32_t player) {
  (void)base;
  (void)player;
  return 0u;
}

// Route: Refii.release_expression(p) (the performer's own end, TU3 sub_8264BDC0 / disc sub_82623880).
extern "C" int32_t refii_mod_release_expression(PPCContext* ctx, uint8_t* base, int32_t player) {
  return ctx != nullptr && (player == 1 || player == 2) ? CallTrue(ctx, base, "release_expression", {Num(player)}) : 0;
}

// Route: Refii.sprinting(p) (Sprinting.IsSprinting).
extern "C" int32_t refii_mod_sprinting(uint8_t* base, int32_t player) {
  return player == 1 || player == 2 ? CallTrue(nullptr, base, "sprinting", {Num(player)}) : 0;
}

// Route: Refii.end_sprint(p) (CECSprint's own setter with 0; false when not sprinting).
extern "C" int32_t refii_mod_end_sprint(PPCContext* ctx, uint8_t* base, int32_t player) {
  return ctx != nullptr && (player == 1 || player == 2) ? CallTrue(ctx, base, "end_sprint", {Num(player)}) : 0;
}

// Route: Refii._set_locomotion(p, key, value) (held per hero; a non-finite value stops holding `key`).
extern "C" int32_t refii_mod_set_locomotion(PPCContext* ctx, uint8_t* base, int32_t player, const char* key, float value) {
  if (ctx == nullptr || key == nullptr || key[0] == 0 || (player != 1 && player != 2)) {
    return 0;
  }
  return CallTrue(ctx, base, "_set_locomotion", {Num(player), Str(key), Finite(value) ? Num(value) : LuaValue{}});
}

// Route: Refii.locomotion(p, key) (LocomotionData_GetValue as CECSprint calls it: held, loaded, else 1.0).
extern "C" int32_t refii_mod_locomotion(PPCContext* ctx, uint8_t* base, int32_t player, const char* key, float* out) {
  Results r;
  double value = 0.0;
  if (ctx == nullptr || out == nullptr || key == nullptr || key[0] == 0 || (player != 1 && player != 2) ||
      !Call(ctx, base, "locomotion", {Num(player), Str(key)}, r) || !Number(r, 0, value)) {
    return 0;
  }
  *out = static_cast<float>(value);
  return 1;
}

// Route: none — 0, as Fable 2 answers a hero without the entries: no Fable 3 code reads
// InputWindowSecondsForParry or MeleeParryWedgeAngle (Refii.set_parry answers false).
extern "C" int32_t refii_mod_set_parry(PPCContext* ctx, uint8_t* base, int32_t player, float window_seconds,
                                       float wedge_degrees) {
  (void)ctx;
  (void)base;
  (void)player;
  (void)window_seconds;
  (void)wedge_degrees;
  return 0;
}

// Route: none — 0 with the outs untouched (Refii.parry answers nil), as refii_mod_set_parry.
extern "C" int32_t refii_mod_parry(PPCContext* ctx, uint8_t* base, int32_t player, float* out_window, float* out_wedge) {
  (void)ctx;
  (void)base;
  (void)player;
  (void)out_window;
  (void)out_wedge;
  return 0;
}

// ---------------------------------------------------------------------------------------------
// The database
// ---------------------------------------------------------------------------------------------

// Route: Refii.record_string(def, field) (GDB.GetRecord(def):GetString(field) where it Exists).
extern "C" int32_t refii_mod_record_string(PPCContext* ctx, uint8_t* base, uint32_t definition, const char* field,
                                           char* out, uint32_t size) {
  Copy("", out, size);
  Results r;
  if (ctx == nullptr || definition == 0u || field == nullptr || field[0] == 0 || out == nullptr || size == 0u ||
      !Call(ctx, base, "record_string", {Rec(definition), Str(field)}, r) || r.empty() ||
      r[0].kind != LuaValue::Kind::String) {
    return 0;
  }
  Copy(r[0].text, out, size);
  return 1;
}

// Route: Refii.record_int(def, field) (RecordPtr:GetS32). A Lua number on Fable 3 is a float32.
extern "C" int32_t refii_mod_record_int(PPCContext* ctx, uint8_t* base, uint32_t definition, const char* field,
                                        int32_t* out) {
  if (out != nullptr) {
    *out = 0;
  }
  Results r;
  double value = 0.0;
  if (ctx == nullptr || definition == 0u || field == nullptr || field[0] == 0 || out == nullptr ||
      !Call(ctx, base, "record_int", {Rec(definition), Str(field)}, r) || !Number(r, 0, value)) {
    return 0;
  }
  *out = Int(value);
  return 1;
}

// Route: Refii.record_number(def, field) (RecordPtr:GetFloat).
extern "C" int32_t refii_mod_record_number(PPCContext* ctx, uint8_t* base, uint32_t definition, const char* field,
                                           float* out) {
  if (out != nullptr) {
    *out = 0.0f;
  }
  Results r;
  double value = 0.0;
  if (ctx == nullptr || definition == 0u || field == nullptr || field[0] == 0 || out == nullptr ||
      !Call(ctx, base, "record_number", {Rec(definition), Str(field)}, r) || !Number(r, 0, value)) {
    return 0;
  }
  *out = static_cast<float>(value);
  return 1;
}

// Route: Refii.record_bool(def, field) (RecordPtr:GetBoolean).
extern "C" int32_t refii_mod_record_bool(PPCContext* ctx, uint8_t* base, uint32_t definition, const char* field,
                                         int32_t* out) {
  if (out != nullptr) {
    *out = 0;
  }
  Results r;
  if (ctx == nullptr || definition == 0u || field == nullptr || field[0] == 0 || out == nullptr ||
      !Call(ctx, base, "record_bool", {Rec(definition), Str(field)}, r) || r.empty() ||
      r[0].kind != LuaValue::Kind::Boolean) {
    return 0;
  }
  *out = r[0].boolean ? 1 : 0;
  return 1;
}

// Route: Refii.record_exists(def) (GDB.RecordExists).
extern "C" int32_t refii_mod_record_exists(PPCContext* ctx, uint8_t* base, uint32_t definition) {
  return ctx != nullptr && definition != 0u ? CallTrue(ctx, base, "record_exists", {Rec(definition)}) : 0;
}

// Route: Refii.record_ref(def, field) (RecordPtr:GetRecord(field):GetID()).
extern "C" int32_t refii_mod_record_ref(PPCContext* ctx, uint8_t* base, uint32_t definition, const char* field,
                                        uint32_t* out) {
  if (out != nullptr) {
    *out = 0u;
  }
  Results r;
  if (ctx == nullptr || definition == 0u || field == nullptr || field[0] == 0 || out == nullptr ||
      !Call(ctx, base, "record_ref", {Rec(definition), Str(field)}, r) || RecordOf(r, 0) == 0u) {
    return 0;
  }
  *out = RecordOf(r, 0);
  return 1;
}

// ---------------------------------------------------------------------------------------------
// The world
// ---------------------------------------------------------------------------------------------

// Route: modlua::CreatureTier; -1 for an entity not in the world (Fable 3's tier reads 0 for an entity
// without the tier component, which it does not tell apart).
extern "C" int32_t refii_mod_creature_tier(uint8_t* base, uint32_t entity) {
  return base != nullptr && seam::Live(base, entity) ? modlua::CreatureTier(base, entity) : -1;
}

// Route: glue _capi_near (Refii.near: SearchTools within the radius, nearest first); at most 256 a call.
extern "C" int32_t refii_mod_entities_near(PPCContext* ctx, uint8_t* base, const float* xyz, float radius,
                                           int32_t creatures_only, uint32_t* out, int32_t max) {
  Results r;
  double count = 0.0;
  if (ctx == nullptr || !Finite3(xyz) || out == nullptr || max <= 0 || !Finite(radius) || !(radius > 0.0f) ||
      !CallGlue(ctx, base, "_capi_near",
                {Num(xyz[0]), Num(xyz[1]), Num(xyz[2]), Num(radius), Bool(creatures_only != 0), Num(std::min(max, 256))},
                r) ||
      !Number(r, 0, count)) {
    return 0;
  }
  int32_t written = 0;
  for (int32_t i = 0; i < Int(count) && written < max; ++i) {
    const uint32_t entity = AddressOf(r, static_cast<size_t>(i) + 1u);
    if (entity != 0u) {
      out[written++] = entity;
    }
  }
  return written;
}

// Route: Refii.line_of_sight(x1, y1, z1, x2, y2, z2) (IsLineOfSightBetweenPositions).
extern "C" int32_t refii_mod_line_of_sight(PPCContext* ctx, uint8_t* base, const float* from, const float* to) {
  if (ctx == nullptr || !Finite3(from) || !Finite3(to)) {
    return 0;
  }
  return CallTrue(ctx, base, "line_of_sight", {Num(from[0]), Num(from[1]), Num(from[2]), Num(to[0]), Num(to[1]), Num(to[2])});
}

// Route: Refii.spawn(def, x, y, z, "") (Debug.CreateEntityAtPosition, an empty instance name).
extern "C" uint32_t refii_mod_spawn(PPCContext* ctx, uint8_t* base, uint32_t definition, const float* xyz) {
  Results r;
  if (ctx == nullptr || definition == 0u || !Finite3(xyz) ||
      !Call(ctx, base, "spawn", {Rec(definition), Num(xyz[0]), Num(xyz[1]), Num(xyz[2]), Str("")}, r)) {
    return 0u;
  }
  return AddressOf(r, 0);
}

// ---------------------------------------------------------------------------------------------
// Input modes and the camera
// ---------------------------------------------------------------------------------------------

// Route: Refii.attack_mode(p) (the mode Godot last stated).
extern "C" int32_t refii_mod_attack_mode(int32_t player) {
  Results r;
  double mode = 0.0;
  return (player == 1 || player == 2) && Call(nullptr, KernelBase(), "attack_mode", {Num(player)}, r) && Number(r, 0, mode)
             ? Int(mode)
             : 0;
}

// Route: Refii.camera_mode(p) (the mode the core chunk's FrameUpdate wrapper states); -1 before one.
extern "C" int32_t refii_mod_camera_mode(int32_t player) {
  Results r;
  double mode = -1.0;
  return (player == 1 || player == 2) && Call(nullptr, KernelBase(), "camera_mode", {Num(player)}, r) && Number(r, 0, mode)
             ? Int(mode)
             : -1;
}

// Route: Refii._camera_override(p, mode, fov, pitch, blend, flags) (the hold Fable3CoreLua applies before
// CameraManager:FrameUpdate).
extern "C" int32_t refii_mod_camera_override(int32_t player, int32_t mode, float fov, float pitch_limit, float blend,
                                             int32_t flags) {
  if ((player != 1 && player != 2) || (mode != 0 && mode != REFII_CAMERA_GAME && mode != REFII_CAMERA_FIRST_PERSON) ||
      !Finite(fov) || !Finite(pitch_limit) || !Finite(blend)) {
    return 0;
  }
  return CallTrue(nullptr, KernelBase(), "_camera_override",
                  {Num(player), Num(mode), Num(fov), Num(pitch_limit), Num(blend), Num(flags & REFII_CAMERA_HOLD_AIM)});
}

// Route: Refii.fov(p) (view::StatedFovRadians in degrees, for a player played here); 0 with none.
extern "C" float refii_mod_camera_fov(uint8_t* base, int32_t player) {
  Results r;
  double degrees = 0.0;
  return (player == 1 || player == 2) && Call(nullptr, base, "fov", {Num(player)}, r) && Number(r, 0, degrees) &&
                 degrees > 0.0
             ? static_cast<float>(degrees)
             : 0.0f;
}

// ---------------------------------------------------------------------------------------------
// The typed events (core/refii_mod_events.h) over Fable 3's native text bus (core/refii_mod_bus.h)
// ---------------------------------------------------------------------------------------------
//
// Fable 3's seams raise every event as its catalog table text (`name=value,..`) through the bus, inside
// the seam (modlua::Decide / DecideAnywhere / the tick's Raise, each with a ContextScope). A typed
// subscription to a catalog kind registers one bus handler for that kind, by its Lua name; the handler
// builds Fable 2's struct from the text — entities from their handles, hashes and record IDs from their
// '0x…' text or number, strings, numbers, booleans — runs that kind's typed handlers in priority order on
// the one struct (each sees the writes before it, as on Fable 2), and answers the bus as a verdict does:
// `cancel=1` when cancelled, and `field=value` for each number or boolean a handler changed (the seam
// applies the writable ones). Fields the catalog text does not carry (a hit record's address, a message
// pointer) stay 0. A kind at or above REFII_EVENT_COUNT is a mod's own: refii_mod_dispatch runs it.

namespace {

struct TypedSubscription {
  int32_t kind;
  refii_mod_event_fn fn;
  void (*user)(void);
  int32_t priority;
  std::string owner;
  int32_t order;
};

struct TypedTable {
  std::mutex mutex;
  std::vector<TypedSubscription> subscriptions;
  std::array<bool, REFII_EVENT_COUNT> on_bus{};
};

/// Constructed on first use: subscriptions arrive from static initialisers (REFII_MOD_ON) in any order.
TypedTable& Typed() {
  static TypedTable table;
  return table;
}

std::vector<TypedSubscription> SubscriptionsOf(int32_t kind) {
  TypedTable& table = Typed();
  std::lock_guard<std::mutex> lock(table.mutex);
  std::vector<TypedSubscription> out;
  for (const TypedSubscription& s : table.subscriptions) {
    if (s.kind == kind) {
      out.push_back(s);
    }
  }
  return out;
}

/// The catalog's Lua names by kind (Godot/src/Engine/mods/Engine_ModApi.cs; Fable 2's kBusNames).
constexpr const char* kKindNames[REFII_EVENT_COUNT] = {
    "tick", "hero_ready", "hero_gone", "lua_ready", "save_loaded", "game_action", "ability_request",
    "melee_swing", "roll_can_start", "roll_started", "block", "anim_event", "hit", "hit_landed", "hit_response",
    "spell_charge", "spell_charge_level", "spell_launch", "spell_launched", "spell_created", "experience",
    "entity_died", "hero_downed", "damage", "level_loaded", "level_unloading", "game_saving", "item_added",
    "item_used", "held_item", "weapon_equipped", "ability_level", "gold", "relation", "input_action",
    "quest_started", "quest_ended", "quest_objective", "dialogue", "entity_spawned", "entity_removed",
    "expression_started", "renown", "morality", "purity", "marriage", "dog_trick", "ranged_shot",
    "status_effect", "target_changed", "opinion_deed", "opinion_modified", "time_of_day", "weather_theme",
    "sleep_menu", "travel", "fast_travel", "menu_opened", "menu_closed", "menu_screen", "weather_region",
    "sleep_chosen", "slow_time", "summoned", "crime", "achievement", "collectable_taken", "partner_consent",
    "property_bought", "property_sold", "rent_paid", "property_rented", "property_condition", "divorce",
    "widowed", "child_born", "gift_given", "gift_accepted", "engaged", "follow", "furniture_upgraded",
    "redecorating", "household_budget", "tracked_stat", "partnership", "household_restored", "dynamic_weather",
    "mod_menu_row", "mod_message_box", "mod_notice", "mod_menu_box", "mod_ability", "spell_pick",
    "partner_message", "look_at_button", "vortex_started", "vortex_ended", "fireball_impact", "paused",
    "start_game", "cutscene", "health_changed", "status_applying", "save_written", "lock_lost",
    "attack_mode_changed", "item_removed", "expression_wheel", "spell_ended", "spell_charge_ended",
    "melee_swing_ended", "action_move_interrupt", "camera_changed", "vortex_caught", "appearance_changed",
    "morph_changed", "guild_seals", "treasury", "kingdom_decision", "chest_opened", "item_bought", "item_sold",
    "shop_price", "dig_spot_dug", "demon_door", "drink", "sanctuary", "sanctuary_request", "weapon_level_up",
    "weapon_augment_unlocked", "weapon_upgrade_ready", "hand_holding", "hand_holding_request",
    "interaction_started", "interaction_result", "interaction_mode", "relationship_stage", "dog_dig",
    "dog_leading", "dog_trick_learned", "dog_learned", "dive_spot_used", "vomit", "job_started", "job_level",
    "job_earnings", "job_worked", "job_multiplier", "job_summary", "guild_message", "title_changed",
    "title_unlocked", "chapter_progress", "scar_added", "scar_removed", "age_changed", "hero_stage",
    "clothing_worn", "clothing_removed", "clothing_dyed", "pub_game_started", "pub_game_finished",
    "village_wealth", "gargoyle_destroyed", "camera_cut", "camera_blend", "camera_screen_changed",
    "camera_zoom_changed", "camera_tick"
};

enum class Enc : uint8_t { kHandle, kU32, kU64, kI32, kF32, kBool, kStr };

/// One catalog field: its name, how the text carries it, and the struct member it fills.
struct FieldSpec {
  const char* name;
  Enc enc;
  void* (*member)(void* event);
};

#define F(E, m, e) FieldSpec{#m, Enc::e, [](void* p) -> void* { return &static_cast<E*>(p)->m; }}

/// The text's value for `name`: a number, true/false, or a quoted string's body (the bus's text quotes
/// strings with ' and carries no ' inside one).
bool FieldText(const char* fields, size_t length, const char* name, std::string& value, bool& quoted) {
  const size_t want = std::strlen(name);
  size_t at = 0;
  while (at < length) {
    const size_t key = at;
    while (at < length && fields[at] != '=' && fields[at] != ',') {
      ++at;
    }
    if (at >= length || fields[at] != '=') {
      ++at;
      continue;
    }
    const bool match = at - key == want && std::memcmp(fields + key, name, want) == 0;
    ++at;
    size_t start = at;
    bool is_quoted = false;
    if (at < length && fields[at] == '\'') {
      is_quoted = true;
      start = ++at;
      while (at < length && fields[at] != '\'') {
        ++at;
      }
    } else {
      while (at < length && fields[at] != ',') {
        ++at;
      }
    }
    if (match) {
      value.assign(fields + start, at - start);
      quoted = is_quoted;
      return true;
    }
    if (is_quoted && at < length) {
      ++at;
    }
    while (at < length && fields[at] != ',') {
      ++at;
    }
    ++at;
  }
  return false;
}

uint64_t UnsignedText(const std::string& text) {
  const char* s = text.c_str();
  if (s[0] == '0' && (s[1] == 'x' || s[1] == 'X')) {
    return std::strtoull(s + 2, nullptr, 16);
  }
  const double v = std::strtod(s, nullptr);
  return v < 0.0 ? static_cast<uint64_t>(static_cast<int64_t>(v)) : static_cast<uint64_t>(v);
}

/// The numbers and booleans as a handler leaves them, for the answer.
double NumericOf(void* event, const FieldSpec& spec) {
  void* m = spec.member(event);
  switch (spec.enc) {
    case Enc::kU32: return static_cast<double>(*static_cast<uint32_t*>(m));
    case Enc::kI32: return static_cast<double>(*static_cast<int32_t*>(m));
    case Enc::kF32: return static_cast<double>(*static_cast<float*>(m));
    case Enc::kBool: return *static_cast<bool*>(m) ? 1.0 : 0.0;
    default: return 0.0;
  }
}

bool Numeric(Enc enc) { return enc == Enc::kU32 || enc == Enc::kI32 || enc == Enc::kF32 || enc == Enc::kBool; }

int32_t RunOn(refii::mods::Event& head, void* event, const std::vector<TypedSubscription>& subs, const char* fields,
              size_t length, char* out, size_t capacity, std::initializer_list<FieldSpec> specs) {
  std::vector<std::string> texts;
  texts.reserve(specs.size());
  std::vector<double> before;
  before.reserve(specs.size());
  for (const FieldSpec& spec : specs) {
    std::string text;
    bool quoted = false;
    const bool present = FieldText(fields, length, spec.name, text, quoted);
    void* m = spec.member(event);
    const double number = present && !quoted ? std::strtod(text.c_str(), nullptr) : 0.0;
    switch (spec.enc) {
      case Enc::kHandle: {
        const uint64_t handle = present ? UnsignedText(text) : 0u;
        *static_cast<uint32_t*>(m) =
            handle != 0u && handle < 0x100000000ull ? refii_mod_handle_address(static_cast<uint32_t>(handle)) : 0u;
        break;
      }
      case Enc::kU32:
        *static_cast<uint32_t*>(m) = present ? static_cast<uint32_t>(UnsignedText(text)) : 0u;
        break;
      case Enc::kU64:
        *static_cast<uint64_t*>(m) = present ? UnsignedText(text) : 0u;
        break;
      case Enc::kI32:
        *static_cast<int32_t*>(m) = Int(number);
        break;
      case Enc::kF32:
        *static_cast<float*>(m) = static_cast<float>(number);
        break;
      case Enc::kBool:
        *static_cast<bool*>(m) = present && (text == "true" || number != 0.0);
        break;
      case Enc::kStr:
        texts.push_back(present ? text : std::string());
        *static_cast<const char**>(m) = texts.back().c_str();
        break;
    }
    before.push_back(NumericOf(event, spec));
  }
  for (const TypedSubscription& s : subs) {
    s.fn(event, s.user);
  }
  std::string answer = head.cancel ? "cancel=1" : "";
  size_t i = 0;
  for (const FieldSpec& spec : specs) {
    const double now = NumericOf(event, spec);
    if (Numeric(spec.enc) && now != before[i] && !std::isnan(now)) {
      char pair[96];
      std::snprintf(pair, sizeof(pair), "%s%s=%.9g", answer.empty() ? "" : ",", spec.name, now);
      answer += pair;
    }
    ++i;
  }
  const size_t n = std::min(answer.size(), capacity > 0u ? capacity - 1u : 0u);
  if (capacity > 0u) {
    std::memcpy(out, answer.data(), n);
    out[n] = 0;
  }
  return static_cast<int32_t>(n);
}

template <typename E>
int32_t Run(const std::vector<TypedSubscription>& subs, const char* fields, size_t length, char* out, size_t capacity,
            std::initializer_list<FieldSpec> specs) {
  E event{};
  event.ctx = modlua::CurrentContext();
  event.base = KernelBase();
  event.cancel = false;
  if (event.ctx == nullptr || event.base == nullptr) {
    return 0;
  }
  return RunOn(event, &event, subs, fields, length, out, capacity, specs);
}

int32_t RunKind(int32_t kind, const std::vector<TypedSubscription>& subs, const char* fields, size_t length, char* out,
                size_t capacity) {
  using namespace refii::mods;
  switch (kind) {
    case REFII_EVENT_TICK:
      return Run<TickEvent>(subs, fields, length, out, capacity, {
          F(TickEvent, seconds, kF32)});
    case REFII_EVENT_HERO_READY:
      return Run<HeroEvent>(subs, fields, length, out, capacity, {
          F(HeroEvent, player, kI32), F(HeroEvent, hero, kHandle)});
    case REFII_EVENT_HERO_GONE:
      return Run<HeroGoneEvent>(subs, fields, length, out, capacity, {
          F(HeroGoneEvent, player, kI32), F(HeroGoneEvent, hero, kHandle)});
    case REFII_EVENT_LUA_READY:
      return Run<LuaEvent>(subs, fields, length, out, capacity, {
          F(LuaEvent, generation, kU32)});
    case REFII_EVENT_SAVE_LOADED:
      return Run<SaveLoadedEvent>(subs, fields, length, out, capacity, {
          F(SaveLoadedEvent, generation, kU32)});
    case REFII_EVENT_GAME_ACTION:
      return Run<GameActionEvent>(subs, fields, length, out, capacity, {
          F(GameActionEvent, hero, kHandle), F(GameActionEvent, player, kI32), F(GameActionEvent, action, kU32)});
    case REFII_EVENT_ABILITY_REQUEST:
      return Run<AbilityRequestEvent>(subs, fields, length, out, capacity, {
          F(AbilityRequestEvent, entity, kHandle), F(AbilityRequestEvent, player, kI32), F(AbilityRequestEvent, type,
          kU32)});
    case REFII_EVENT_MELEE_SWING:
      return Run<MeleeSwingEvent>(subs, fields, length, out, capacity, {
          F(MeleeSwingEvent, entity, kHandle), F(MeleeSwingEvent, player, kI32), F(MeleeSwingEvent, action, kHandle),
          F(MeleeSwingEvent, action_type, kI32), F(MeleeSwingEvent, action_name, kStr), F(MeleeSwingEvent, strike, kI32),
          F(MeleeSwingEvent, flourish, kBool)});
    case REFII_EVENT_ROLL_CAN_START:
      return Run<RollEvent>(subs, fields, length, out, capacity, {
          F(RollEvent, entity, kHandle), F(RollEvent, player, kI32)});
    case REFII_EVENT_ROLL_STARTED:
      return Run<RollStartedEvent>(subs, fields, length, out, capacity, {
          F(RollStartedEvent, entity, kHandle), F(RollStartedEvent, player, kI32)});
    case REFII_EVENT_BLOCK:
      return Run<BlockEvent>(subs, fields, length, out, capacity, {
          F(BlockEvent, blocker, kHandle), F(BlockEvent, player, kI32), F(BlockEvent, damage, kF32), F(BlockEvent,
          unblockable, kBool)});
    case REFII_EVENT_ANIM_EVENT:
      return Run<AnimEventEvent>(subs, fields, length, out, capacity, {
          F(AnimEventEvent, entity, kHandle), F(AnimEventEvent, player, kI32), F(AnimEventEvent, name, kStr),
          F(AnimEventEvent, action, kHandle), F(AnimEventEvent, action_type, kI32), F(AnimEventEvent,
          action_name, kStr)});
    case REFII_EVENT_HIT:
      return Run<HitEvent>(subs, fields, length, out, capacity, {
          F(HitEvent, victim, kHandle), F(HitEvent, victim_player, kI32), F(HitEvent, attacker, kHandle), F(HitEvent,
          attacker_player, kI32), F(HitEvent, damage, kF32), F(HitEvent, spell_type, kI32), F(HitEvent,
          spell_level, kI32), F(HitEvent, spell_serial, kU32), F(HitEvent, knockdown, kBool), F(HitEvent, tag,
          kI32)});
    case REFII_EVENT_HIT_LANDED:
      return Run<HitLandedEvent>(subs, fields, length, out, capacity, {
          F(HitLandedEvent, victim, kHandle), F(HitLandedEvent, victim_player, kI32), F(HitLandedEvent, attacker,
          kHandle), F(HitLandedEvent, attacker_player, kI32), F(HitLandedEvent, damage, kF32),
          F(HitLandedEvent, spell_type, kI32), F(HitLandedEvent, spell_level, kI32), F(HitLandedEvent,
          spell_serial, kU32), F(HitLandedEvent, knockdown, kBool), F(HitLandedEvent, tag, kI32)});
    case REFII_EVENT_HIT_RESPONSE:
      return Run<HitResponseEvent>(subs, fields, length, out, capacity, {
          F(HitResponseEvent, victim, kHandle), F(HitResponseEvent, victim_player, kI32), F(HitResponseEvent, damage,
          kF32), F(HitResponseEvent, health, kF32), F(HitResponseEvent, max_health, kF32)});
    case REFII_EVENT_SPELL_CHARGE:
      return Run<SpellChargeEvent>(subs, fields, length, out, capacity, {
          F(SpellChargeEvent, entity, kHandle), F(SpellChargeEvent, player, kI32)});
    case REFII_EVENT_SPELL_CHARGE_LEVEL:
      return Run<SpellChargeLevelEvent>(subs, fields, length, out, capacity, {
          F(SpellChargeLevelEvent, caster, kHandle), F(SpellChargeLevelEvent, player, kI32), F(SpellChargeLevelEvent,
          level, kI32)});
    case REFII_EVENT_SPELL_LAUNCH:
      return Run<SpellLaunchEvent>(subs, fields, length, out, capacity, {
          F(SpellLaunchEvent, caster, kHandle), F(SpellLaunchEvent, player, kI32), F(SpellLaunchEvent, targeted, kBool),
          F(SpellLaunchEvent, slot, kI32), F(SpellLaunchEvent, type, kI32), F(SpellLaunchEvent, cascading,
          kBool), F(SpellLaunchEvent, tag, kI32)});
    case REFII_EVENT_SPELL_LAUNCHED:
      return Run<SpellLaunchedEvent>(subs, fields, length, out, capacity, {
          F(SpellLaunchedEvent, caster, kHandle), F(SpellLaunchedEvent, player, kI32), F(SpellLaunchedEvent, slot,
          kI32)});
    case REFII_EVENT_SPELL_CREATED:
      return Run<SpellCreatedEvent>(subs, fields, length, out, capacity, {
          F(SpellCreatedEvent, spell, kHandle), F(SpellCreatedEvent, type, kI32), F(SpellCreatedEvent, targeted, kBool),
          F(SpellCreatedEvent, target, kHandle), F(SpellCreatedEvent, level, kI32), F(SpellCreatedEvent,
          caster, kHandle), F(SpellCreatedEvent, caster_player, kI32), F(SpellCreatedEvent, serial, kU32),
          F(SpellCreatedEvent, slot, kI32), F(SpellCreatedEvent, tag, kI32), F(SpellCreatedEvent, host_serial,
          kU32)});
    case REFII_EVENT_EXPERIENCE:
      return Run<ExperienceEvent>(subs, fields, length, out, capacity, {
          F(ExperienceEvent, entity, kHandle), F(ExperienceEvent, player, kI32), F(ExperienceEvent, pool, kI32),
          F(ExperienceEvent, amount, kF32), F(ExperienceEvent, spend, kBool), F(ExperienceEvent, nested,
          kBool)});
    case REFII_EVENT_ENTITY_DIED:
      return Run<EntityDiedEvent>(subs, fields, length, out, capacity, {
          F(EntityDiedEvent, entity, kHandle), F(EntityDiedEvent, player, kI32), F(EntityDiedEvent, killer, kHandle),
          F(EntityDiedEvent, killer_player, kI32)});
    case REFII_EVENT_HERO_DOWNED:
      return Run<HeroDownedEvent>(subs, fields, length, out, capacity, {
          F(HeroDownedEvent, hero, kHandle), F(HeroDownedEvent, player, kI32), F(HeroDownedEvent, killer, kHandle),
          F(HeroDownedEvent, killer_player, kI32)});
    case REFII_EVENT_DAMAGE:
      return Run<DamageEvent>(subs, fields, length, out, capacity, {
          F(DamageEvent, victim, kHandle), F(DamageEvent, victim_player, kI32), F(DamageEvent, attacker, kHandle),
          F(DamageEvent, attacker_player, kI32), F(DamageEvent, damage, kF32), F(DamageEvent, health, kF32),
          F(DamageEvent, max_health, kF32), F(DamageEvent, lethal, kBool), F(DamageEvent, spell_type, kI32),
          F(DamageEvent, spell_level, kI32), F(DamageEvent, spell_serial, kU32)});
    case REFII_EVENT_LEVEL_LOADED:
      return Run<LevelLoadedEvent>(subs, fields, length, out, capacity, {
          F(LevelLoadedEvent, level, kStr), F(LevelLoadedEvent, from_save, kBool)});
    case REFII_EVENT_LEVEL_UNLOADING:
      return Run<LevelUnloadingEvent>(subs, fields, length, out, capacity, {
          F(LevelUnloadingEvent, level, kStr)});
    case REFII_EVENT_GAME_SAVING:
      return Run<GameSavingEvent>(subs, fields, length, out, capacity, {
          F(GameSavingEvent, file, kStr)});
    case REFII_EVENT_ITEM_ADDED:
      return Run<ItemAddedEvent>(subs, fields, length, out, capacity, {
          F(ItemAddedEvent, entity, kHandle), F(ItemAddedEvent, player, kI32), F(ItemAddedEvent, definition, kU32),
          F(ItemAddedEvent, notify, kBool)});
    case REFII_EVENT_ITEM_USED:
      return Run<ItemUsedEvent>(subs, fields, length, out, capacity, {
          F(ItemUsedEvent, entity, kHandle), F(ItemUsedEvent, player, kI32), F(ItemUsedEvent, item, kHandle),
          F(ItemUsedEvent, definition, kU32), F(ItemUsedEvent, count, kI32)});
    case REFII_EVENT_HELD_ITEM:
      return Run<HeldItemEvent>(subs, fields, length, out, capacity, {
          F(HeldItemEvent, entity, kHandle), F(HeldItemEvent, player, kI32), F(HeldItemEvent, slot, kStr),
          F(HeldItemEvent, item, kHandle), F(HeldItemEvent, attached, kBool)});
    case REFII_EVENT_WEAPON_EQUIPPED:
      return Run<WeaponEquippedEvent>(subs, fields, length, out, capacity, {
          F(WeaponEquippedEvent, entity, kHandle), F(WeaponEquippedEvent, player, kI32), F(WeaponEquippedEvent, item,
          kHandle), F(WeaponEquippedEvent, definition, kU32)});
    case REFII_EVENT_ABILITY_LEVEL:
      return Run<AbilityLevelEvent>(subs, fields, length, out, capacity, {
          F(AbilityLevelEvent, entity, kHandle), F(AbilityLevelEvent, player, kI32), F(AbilityLevelEvent, type, kI32),
          F(AbilityLevelEvent, level, kI32), F(AbilityLevelEvent, old_level, kI32)});
    case REFII_EVENT_GOLD:
      return Run<GoldEvent>(subs, fields, length, out, capacity, {
          F(GoldEvent, entity, kHandle), F(GoldEvent, player, kI32), F(GoldEvent, gold, kI32), F(GoldEvent, delta,
          kI32), F(GoldEvent, passive, kBool), F(GoldEvent, share, kBool)});
    case REFII_EVENT_RELATION:
      return Run<RelationEvent>(subs, fields, length, out, capacity, {
          F(RelationEvent, entity, kHandle), F(RelationEvent, player, kI32), F(RelationEvent, other, kHandle),
          F(RelationEvent, other_player, kI32), F(RelationEvent, relation, kI32)});
    case REFII_EVENT_INPUT_ACTION:
      return Run<InputActionEvent>(subs, fields, length, out, capacity, {
          F(InputActionEvent, name, kStr), F(InputActionEvent, player, kI32), F(InputActionEvent, pressed, kBool)});
    case REFII_EVENT_QUEST_STARTED:
      return Run<QuestStartedEvent>(subs, fields, length, out, capacity, {
          F(QuestStartedEvent, quest, kStr), F(QuestStartedEvent, hero, kHandle), F(QuestStartedEvent, player, kI32),
          F(QuestStartedEvent, make_primary, kBool)});
    case REFII_EVENT_QUEST_ENDED:
      return Run<QuestEndedEvent>(subs, fields, length, out, capacity, {
          F(QuestEndedEvent, quest, kStr), F(QuestEndedEvent, hero, kHandle), F(QuestEndedEvent, player, kI32),
          F(QuestEndedEvent, completed, kBool), F(QuestEndedEvent, failed, kBool)});
    case REFII_EVENT_QUEST_OBJECTIVE:
      return Run<QuestObjectiveEvent>(subs, fields, length, out, capacity, {
          F(QuestObjectiveEvent, quest, kStr), F(QuestObjectiveEvent, tag, kStr), F(QuestObjectiveEvent, hero, kHandle),
          F(QuestObjectiveEvent, player, kI32)});
    case REFII_EVENT_DIALOGUE:
      return Run<DialogueEvent>(subs, fields, length, out, capacity, {
          F(DialogueEvent, speaker, kHandle), F(DialogueEvent, speaker_player, kI32), F(DialogueEvent, text, kU32),
          F(DialogueEvent, listener, kHandle), F(DialogueEvent, listener_player, kI32), F(DialogueEvent,
          subtitle, kBool)});
    case REFII_EVENT_ENTITY_SPAWNED:
      return Run<EntitySpawnedEvent>(subs, fields, length, out, capacity, {
          F(EntitySpawnedEvent, entity, kHandle), F(EntitySpawnedEvent, player, kI32), F(EntitySpawnedEvent, definition,
          kU32)});
    case REFII_EVENT_ENTITY_REMOVED:
      return Run<EntityRemovedEvent>(subs, fields, length, out, capacity, {
          F(EntityRemovedEvent, entity, kHandle), F(EntityRemovedEvent, player, kI32), F(EntityRemovedEvent, definition,
          kU32)});
    case REFII_EVENT_EXPRESSION_STARTED:
      return Run<ExpressionStartedEvent>(subs, fields, length, out, capacity, {
          F(ExpressionStartedEvent, entity, kHandle), F(ExpressionStartedEvent, player, kI32), F(ExpressionStartedEvent,
          expression, kU32)});
    case REFII_EVENT_RENOWN:
      return Run<RenownEvent>(subs, fields, length, out, capacity, {
          F(RenownEvent, entity, kHandle), F(RenownEvent, player, kI32), F(RenownEvent, delta, kI32), F(RenownEvent,
          share, kBool)});
    case REFII_EVENT_MORALITY:
      return Run<MoralityEvent>(subs, fields, length, out, capacity, {
          F(MoralityEvent, entity, kHandle), F(MoralityEvent, player, kI32), F(MoralityEvent, delta, kF32)});
    case REFII_EVENT_PURITY:
      return Run<PurityEvent>(subs, fields, length, out, capacity, {
          F(PurityEvent, entity, kHandle), F(PurityEvent, player, kI32), F(PurityEvent, delta, kF32)});
    case REFII_EVENT_MARRIAGE:
      return Run<MarriageEvent>(subs, fields, length, out, capacity, {
          F(MarriageEvent, hero, kHandle), F(MarriageEvent, player, kI32), F(MarriageEvent, spouse, kHandle)});
    case REFII_EVENT_DOG_TRICK:
      return Run<DogTrickEvent>(subs, fields, length, out, capacity, {
          F(DogTrickEvent, dog, kHandle), F(DogTrickEvent, trick, kU32), F(DogTrickEvent, player, kI32)});
    case REFII_EVENT_RANGED_SHOT:
      return Run<RangedShotEvent>(subs, fields, length, out, capacity, {
          F(RangedShotEvent, shooter, kHandle), F(RangedShotEvent, player, kI32), F(RangedShotEvent, target, kHandle),
          F(RangedShotEvent, aimed, kBool), F(RangedShotEvent, weapon, kHandle)});
    case REFII_EVENT_STATUS_EFFECT:
      return Run<StatusEffectEvent>(subs, fields, length, out, capacity, {
          F(StatusEffectEvent, entity, kHandle), F(StatusEffectEvent, player, kI32), F(StatusEffectEvent, effect, kI32),
          F(StatusEffectEvent, started, kBool), F(StatusEffectEvent, serial, kU32), F(StatusEffectEvent,
          source, kHandle)});
    case REFII_EVENT_TARGET_CHANGED:
      return Run<TargetChangedEvent>(subs, fields, length, out, capacity, {
          F(TargetChangedEvent, hero, kHandle), F(TargetChangedEvent, player, kI32), F(TargetChangedEvent, target,
          kHandle)});
    case REFII_EVENT_OPINION_DEED:
      return Run<OpinionDeedEvent>(subs, fields, length, out, capacity, {
          F(OpinionDeedEvent, villager, kHandle), F(OpinionDeedEvent, doer, kHandle), F(OpinionDeedEvent, doer_player,
          kI32), F(OpinionDeedEvent, deed, kU32), F(OpinionDeedEvent, kind, kI32)});
    case REFII_EVENT_OPINION_MODIFIED:
      return Run<OpinionModifiedEvent>(subs, fields, length, out, capacity, {
          F(OpinionModifiedEvent, villager, kHandle), F(OpinionModifiedEvent, axis, kI32), F(OpinionModifiedEvent,
          delta, kF32), F(OpinionModifiedEvent, player, kI32)});
    case REFII_EVENT_TIME_OF_DAY:
      return Run<TimeOfDayEvent>(subs, fields, length, out, capacity, {
          F(TimeOfDayEvent, hour, kI32), F(TimeOfDayEvent, day, kI32), F(TimeOfDayEvent, time, kF32), F(TimeOfDayEvent,
          new_day, kBool)});
    case REFII_EVENT_WEATHER_THEME:
      return Run<WeatherThemeEvent>(subs, fields, length, out, capacity, {
          F(WeatherThemeEvent, theme, kStr), F(WeatherThemeEvent, weight, kF32), F(WeatherThemeEvent, seconds, kF32)});
    case REFII_EVENT_SLEEP_MENU:
      return Run<SleepMenuEvent>(subs, fields, length, out, capacity, {
          F(SleepMenuEvent, seen, kBool), F(SleepMenuEvent, player, kI32)});
    case REFII_EVENT_TRAVEL:
      return Run<TravelEvent>(subs, fields, length, out, capacity, {
          F(TravelEvent, world, kStr), F(TravelEvent, level, kStr), F(TravelEvent, entrance, kStr), F(TravelEvent, type,
          kI32), F(TravelEvent, full_load, kBool), F(TravelEvent, player, kI32)});
    case REFII_EVENT_FAST_TRAVEL:
      return Run<FastTravelEvent>(subs, fields, length, out, capacity, {
          F(FastTravelEvent, level, kStr), F(FastTravelEvent, stop, kStr), F(FastTravelEvent, paid, kBool),
          F(FastTravelEvent, player, kI32)});
    case REFII_EVENT_MENU_OPENED:
      return Run<MenuOpenedEvent>(subs, fields, length, out, capacity, {
          F(MenuOpenedEvent, seen, kBool), F(MenuOpenedEvent, player, kI32)});
    case REFII_EVENT_MENU_CLOSED:
      return Run<MenuClosedEvent>(subs, fields, length, out, capacity, {
          F(MenuClosedEvent, seen, kBool), F(MenuClosedEvent, player, kI32)});
    case REFII_EVENT_MENU_SCREEN:
      return Run<MenuScreenEvent>(subs, fields, length, out, capacity, {
          F(MenuScreenEvent, screen, kI32), F(MenuScreenEvent, player, kI32)});
    case REFII_EVENT_WEATHER_REGION:
      return Run<WeatherRegionEvent>(subs, fields, length, out, capacity, {
          F(WeatherRegionEvent, day_set, kU32), F(WeatherRegionEvent, previous, kU32), F(WeatherRegionEvent, inside,
          kBool), F(WeatherRegionEvent, player, kI32)});
    case REFII_EVENT_SLEEP_CHOSEN:
      return Run<SleepChosenEvent>(subs, fields, length, out, capacity, {
          F(SleepChosenEvent, bed, kHandle), F(SleepChosenEvent, choice, kI32), F(SleepChosenEvent, cancelled, kBool),
          F(SleepChosenEvent, player, kI32)});
    case REFII_EVENT_SLOW_TIME:
      return Run<SlowTimeEvent>(subs, fields, length, out, capacity, {
          F(SlowTimeEvent, caster, kHandle), F(SlowTimeEvent, player, kI32), F(SlowTimeEvent, scale, kF32),
          F(SlowTimeEvent, active, kBool)});
    case REFII_EVENT_SUMMONED:
      return Run<SummonedEvent>(subs, fields, length, out, capacity, {
          F(SummonedEvent, summoner, kHandle), F(SummonedEvent, player, kI32), F(SummonedEvent, summon, kHandle),
          F(SummonedEvent, kind, kI32), F(SummonedEvent, serial, kU32), F(SummonedEvent, tag, kI32)});
    case REFII_EVENT_CRIME:
      return Run<CrimeEvent>(subs, fields, length, out, capacity, {
          F(CrimeEvent, criminal, kHandle), F(CrimeEvent, player, kI32), F(CrimeEvent, victim, kHandle), F(CrimeEvent,
          victim_player, kI32), F(CrimeEvent, type, kI32)});
    case REFII_EVENT_ACHIEVEMENT:
      return Run<AchievementEvent>(subs, fields, length, out, capacity, {
          F(AchievementEvent, id, kI32), F(AchievementEvent, source, kHandle), F(AchievementEvent, player, kI32)});
    case REFII_EVENT_COLLECTABLE_TAKEN:
      return Run<CollectableTakenEvent>(subs, fields, length, out, capacity, {
          F(CollectableTakenEvent, player, kI32), F(CollectableTakenEvent, entity, kHandle), F(CollectableTakenEvent,
          record, kU32), F(CollectableTakenEvent, stays, kBool)});
    case REFII_EVENT_PARTNER_CONSENT:
      return Run<PartnerConsentEvent>(subs, fields, length, out, capacity, {
          F(PartnerConsentEvent, ticket, kU32), F(PartnerConsentEvent, kind, kI32), F(PartnerConsentEvent, asker, kI32),
          F(PartnerConsentEvent, detail, kI32), F(PartnerConsentEvent, answer, kI32)});
    case REFII_EVENT_PROPERTY_BOUGHT:
      return Run<PropertyBoughtEvent>(subs, fields, length, out, capacity, {
          F(PropertyBoughtEvent, hero, kHandle), F(PropertyBoughtEvent, player, kI32), F(PropertyBoughtEvent, building,
          kHandle), F(PropertyBoughtEvent, price, kI32)});
    case REFII_EVENT_PROPERTY_SOLD:
      return Run<PropertySoldEvent>(subs, fields, length, out, capacity, {
          F(PropertySoldEvent, hero, kHandle), F(PropertySoldEvent, player, kI32), F(PropertySoldEvent, building,
          kHandle), F(PropertySoldEvent, price, kI32)});
    case REFII_EVENT_RENT_PAID:
      return Run<RentPaidEvent>(subs, fields, length, out, capacity, {
          F(RentPaidEvent, hero, kHandle), F(RentPaidEvent, player, kI32), F(RentPaidEvent, gold, kI32),
          F(RentPaidEvent, drops, kI32), F(RentPaidEvent, away, kBool)});
    case REFII_EVENT_PROPERTY_RENTED:
      return Run<PropertyRentedEvent>(subs, fields, length, out, capacity, {
          F(PropertyRentedEvent, hero, kHandle), F(PropertyRentedEvent, player, kI32), F(PropertyRentedEvent, building,
          kHandle), F(PropertyRentedEvent, rented, kBool)});
    case REFII_EVENT_PROPERTY_CONDITION:
      return Run<PropertyConditionEvent>(subs, fields, length, out, capacity, {
          F(PropertyConditionEvent, uid, kU64), F(PropertyConditionEvent, player, kI32), F(PropertyConditionEvent,
          condition, kF32), F(PropertyConditionEvent, previous, kF32), F(PropertyConditionEvent, repaired,
          kBool)});
    case REFII_EVENT_DIVORCE:
      return Run<DivorceEvent>(subs, fields, length, out, capacity, {
          F(DivorceEvent, hero, kHandle), F(DivorceEvent, player, kI32), F(DivorceEvent, spouse, kHandle)});
    case REFII_EVENT_WIDOWED:
      return Run<WidowedEvent>(subs, fields, length, out, capacity, {
          F(WidowedEvent, hero, kHandle), F(WidowedEvent, player, kI32), F(WidowedEvent, uid, kU64)});
    case REFII_EVENT_CHILD_BORN:
      return Run<ChildBornEvent>(subs, fields, length, out, capacity, {
          F(ChildBornEvent, hero, kHandle), F(ChildBornEvent, player, kI32), F(ChildBornEvent, spouse, kHandle),
          F(ChildBornEvent, child, kHandle)});
    case REFII_EVENT_GIFT_GIVEN:
      return Run<GiftGivenEvent>(subs, fields, length, out, capacity, {
          F(GiftGivenEvent, hero, kHandle), F(GiftGivenEvent, player, kI32), F(GiftGivenEvent, target, kHandle),
          F(GiftGivenEvent, item, kU32)});
    case REFII_EVENT_GIFT_ACCEPTED:
      return Run<GiftAcceptedEvent>(subs, fields, length, out, capacity, {
          F(GiftAcceptedEvent, hero, kHandle), F(GiftAcceptedEvent, player, kI32), F(GiftAcceptedEvent, item, kU32)});
    case REFII_EVENT_ENGAGED:
      return Run<EngagedEvent>(subs, fields, length, out, capacity, {
          F(EngagedEvent, villager, kHandle), F(EngagedEvent, player, kI32), F(EngagedEvent, engaged, kBool)});
    case REFII_EVENT_FOLLOW:
      return Run<FollowEvent>(subs, fields, length, out, capacity, {
          F(FollowEvent, follower, kHandle), F(FollowEvent, leader, kHandle), F(FollowEvent, player, kI32),
          F(FollowEvent, following, kBool)});
    case REFII_EVENT_FURNITURE_UPGRADED:
      return Run<FurnitureUpgradedEvent>(subs, fields, length, out, capacity, {
          F(FurnitureUpgradedEvent, piece, kHandle), F(FurnitureUpgradedEvent, player, kI32), F(FurnitureUpgradedEvent,
          level, kI32), F(FurnitureUpgradedEvent, previous, kI32)});
    case REFII_EVENT_REDECORATING:
      return Run<RedecoratingEvent>(subs, fields, length, out, capacity, {
          F(RedecoratingEvent, building, kHandle), F(RedecoratingEvent, player, kI32), F(RedecoratingEvent, on,
          kBool)});
    case REFII_EVENT_HOUSEHOLD_BUDGET:
      return Run<HouseholdBudgetEvent>(subs, fields, length, out, capacity, {
          F(HouseholdBudgetEvent, hero, kHandle), F(HouseholdBudgetEvent, player, kI32), F(HouseholdBudgetEvent,
          building, kHandle), F(HouseholdBudgetEvent, gold, kI32)});
    case REFII_EVENT_TRACKED_STAT:
      return Run<TrackedStatEvent>(subs, fields, length, out, capacity, {
          F(TrackedStatEvent, hero, kHandle), F(TrackedStatEvent, player, kI32), F(TrackedStatEvent, stat, kI32),
          F(TrackedStatEvent, amount, kI32)});
    case REFII_EVENT_PARTNERSHIP:
      return Run<PartnershipEvent>(subs, fields, length, out, capacity, {
          F(PartnershipEvent, kind, kI32), F(PartnershipEvent, previous, kI32)});
    case REFII_EVENT_HOUSEHOLD_RESTORED:
      return Run<HouseholdRestoredEvent>(subs, fields, length, out, capacity, {
          F(HouseholdRestoredEvent, hero, kHandle), F(HouseholdRestoredEvent, player, kI32), F(HouseholdRestoredEvent,
          spouses, kI32)});
    case REFII_EVENT_DYNAMIC_WEATHER:
      return Run<DynamicWeatherEvent>(subs, fields, length, out, capacity, {
          F(DynamicWeatherEvent, level, kStr), F(DynamicWeatherEvent, weather, kStr), F(DynamicWeatherEvent, previous,
          kStr), F(DynamicWeatherEvent, seconds, kF32), F(DynamicWeatherEvent, from_host, kBool)});
    case REFII_EVENT_MOD_MENU_ROW:
      return Run<ModMenuRowEvent>(subs, fields, length, out, capacity, {
          F(ModMenuRowEvent, menu, kStr), F(ModMenuRowEvent, row, kStr), F(ModMenuRowEvent, player, kI32),
          F(ModMenuRowEvent, mod, kStr), F(ModMenuRowEvent, command, kStr), F(ModMenuRowEvent, button, kStr),
          F(ModMenuRowEvent, subject_kind, kStr), F(ModMenuRowEvent, definition, kStr), F(ModMenuRowEvent,
          spell, kI32)});
    case REFII_EVENT_MOD_MESSAGE_BOX:
      return Run<ModMessageBoxEvent>(subs, fields, length, out, capacity, {
          F(ModMessageBoxEvent, id, kU32), F(ModMessageBoxEvent, button, kI32), F(ModMessageBoxEvent, player, kI32)});
    case REFII_EVENT_MOD_NOTICE:
      return Run<ModNoticeEvent>(subs, fields, length, out, capacity, {
          F(ModNoticeEvent, id, kU32), F(ModNoticeEvent, outcome, kI32), F(ModNoticeEvent, player, kI32)});
    case REFII_EVENT_MOD_MENU_BOX:
      return Run<ModMenuBoxEvent>(subs, fields, length, out, capacity, {
          F(ModMenuBoxEvent, id, kU32), F(ModMenuBoxEvent, option, kI32), F(ModMenuBoxEvent, player, kI32)});
    case REFII_EVENT_MOD_ABILITY:
      return Run<ModAbilityEvent>(subs, fields, length, out, capacity, {
          F(ModAbilityEvent, ability, kStr), F(ModAbilityEvent, mod, kStr), F(ModAbilityEvent, refund, kBool),
          F(ModAbilityEvent, previous, kI32), F(ModAbilityEvent, level, kI32), F(ModAbilityEvent, experience,
          kI32), F(ModAbilityEvent, player, kI32)});
    case REFII_EVENT_SPELL_PICK:
      return Run<SpellPickEvent>(subs, fields, length, out, capacity, {
          F(SpellPickEvent, pick, kStr), F(SpellPickEvent, mod, kStr), F(SpellPickEvent, slot, kI32), F(SpellPickEvent,
          spell, kI32), F(SpellPickEvent, previous, kI32), F(SpellPickEvent, player, kI32)});
    case REFII_EVENT_PARTNER_MESSAGE:
      return Run<PartnerMessageEvent>(subs, fields, length, out, capacity, {
          F(PartnerMessageEvent, mod, kStr), F(PartnerMessageEvent, name, kStr), F(PartnerMessageEvent, payload, kStr),
          F(PartnerMessageEvent, from, kI32)});
    case REFII_EVENT_LOOK_AT_BUTTON:
      return Run<LookAtButtonEvent>(subs, fields, length, out, capacity, {
          F(LookAtButtonEvent, hero, kHandle), F(LookAtButtonEvent, player, kI32), F(LookAtButtonEvent, down, kBool)});
    case REFII_EVENT_VORTEX_STARTED:
      return Run<VortexStartedEvent>(subs, fields, length, out, capacity, {
          F(VortexStartedEvent, entity, kHandle), F(VortexStartedEvent, caster, kHandle), F(VortexStartedEvent,
          caster_player, kI32), F(VortexStartedEvent, target, kHandle), F(VortexStartedEvent, serial, kU32),
          F(VortexStartedEvent, level, kI32), F(VortexStartedEvent, x, kF32), F(VortexStartedEvent, y, kF32),
          F(VortexStartedEvent, z, kF32), F(VortexStartedEvent, tag, kI32), F(VortexStartedEvent, damage,
          kF32), F(VortexStartedEvent, lifts, kBool)});
    case REFII_EVENT_VORTEX_ENDED:
      return Run<VortexEndedEvent>(subs, fields, length, out, capacity, {
          F(VortexEndedEvent, entity, kHandle), F(VortexEndedEvent, caster, kHandle), F(VortexEndedEvent, caster_player,
          kI32), F(VortexEndedEvent, serial, kU32), F(VortexEndedEvent, x, kF32), F(VortexEndedEvent, y,
          kF32), F(VortexEndedEvent, z, kF32)});
    case REFII_EVENT_FIREBALL_IMPACT:
      return Run<FireballImpactEvent>(subs, fields, length, out, capacity, {
          F(FireballImpactEvent, serial, kU32), F(FireballImpactEvent, level, kI32), F(FireballImpactEvent, caster,
          kHandle), F(FireballImpactEvent, caster_player, kI32), F(FireballImpactEvent, struck, kHandle),
          F(FireballImpactEvent, struck_player, kI32), F(FireballImpactEvent, x, kF32), F(FireballImpactEvent,
          y, kF32), F(FireballImpactEvent, z, kF32), F(FireballImpactEvent, nx, kF32), F(FireballImpactEvent,
          ny, kF32), F(FireballImpactEvent, nz, kF32), F(FireballImpactEvent, detonated, kBool),
          F(FireballImpactEvent, hits, kI32), F(FireballImpactEvent, tag, kI32)});
    case REFII_EVENT_PAUSED:
      return Run<PausedEvent>(subs, fields, length, out, capacity, {
          F(PausedEvent, paused, kBool), F(PausedEvent, mode, kI32), F(PausedEvent, player, kI32), F(PausedEvent,
          screen, kI32)});
    case REFII_EVENT_START_GAME:
      return Run<StartGameEvent>(subs, fields, length, out, capacity, {
          F(StartGameEvent, save, kStr), F(StartGameEvent, level, kStr), F(StartGameEvent, new_game, kBool),
          F(StartGameEvent, player, kI32), F(StartGameEvent, commit, kStr)});
    case REFII_EVENT_CUTSCENE:
      return Run<CutsceneEvent>(subs, fields, length, out, capacity, {
          F(CutsceneEvent, player, kI32), F(CutsceneEvent, active, kBool), F(CutsceneEvent, kind, kI32),
          F(CutsceneEvent, kinds, kI32), F(CutsceneEvent, hero, kHandle)});
    case REFII_EVENT_HEALTH_CHANGED:
      return Run<HealthChangedEvent>(subs, fields, length, out, capacity, {
          F(HealthChangedEvent, entity, kHandle), F(HealthChangedEvent, player, kI32), F(HealthChangedEvent, delta,
          kF32), F(HealthChangedEvent, health, kF32), F(HealthChangedEvent, max, kF32), F(HealthChangedEvent,
          source, kHandle)});
    case REFII_EVENT_STATUS_APPLYING:
      return Run<StatusApplyingEvent>(subs, fields, length, out, capacity, {
          F(StatusApplyingEvent, entity, kHandle), F(StatusApplyingEvent, player, kI32), F(StatusApplyingEvent, effect,
          kI32), F(StatusApplyingEvent, serial, kU32), F(StatusApplyingEvent, source, kHandle),
          F(StatusApplyingEvent, level, kI32), F(StatusApplyingEvent, seconds, kF32), F(StatusApplyingEvent,
          tag, kI32)});
    case REFII_EVENT_SAVE_WRITTEN:
      return Run<SaveWrittenEvent>(subs, fields, length, out, capacity, {
          F(SaveWrittenEvent, player, kI32), F(SaveWrittenEvent, save, kStr), F(SaveWrittenEvent, ok, kBool)});
    case REFII_EVENT_LOCK_LOST:
      return Run<LockLostEvent>(subs, fields, length, out, capacity, {
          F(LockLostEvent, player, kI32), F(LockLostEvent, hero, kHandle), F(LockLostEvent, target, kHandle)});
    case REFII_EVENT_ATTACK_MODE_CHANGED:
      return Run<AttackModeChangedEvent>(subs, fields, length, out, capacity, {
          F(AttackModeChangedEvent, player, kI32), F(AttackModeChangedEvent, mode, kI32)});
    case REFII_EVENT_ITEM_REMOVED:
      return Run<ItemRemovedEvent>(subs, fields, length, out, capacity, {
          F(ItemRemovedEvent, entity, kHandle), F(ItemRemovedEvent, player, kI32), F(ItemRemovedEvent, inventory,
          kHandle), F(ItemRemovedEvent, definition, kU32), F(ItemRemovedEvent, count, kI32),
          F(ItemRemovedEvent, remaining, kI32)});
    case REFII_EVENT_EXPRESSION_WHEEL:
      return Run<ExpressionWheelEvent>(subs, fields, length, out, capacity, {
          F(ExpressionWheelEvent, player, kI32), F(ExpressionWheelEvent, open, kBool), F(ExpressionWheelEvent,
          highlighted, kU32)});
    case REFII_EVENT_SPELL_ENDED:
      return Run<SpellEndedEvent>(subs, fields, length, out, capacity, {
          F(SpellEndedEvent, serial, kU32), F(SpellEndedEvent, type, kI32), F(SpellEndedEvent, caster, kHandle),
          F(SpellEndedEvent, caster_player, kI32), F(SpellEndedEvent, tag, kI32), F(SpellEndedEvent, reason,
          kI32), F(SpellEndedEvent, level, kI32)});
    case REFII_EVENT_SPELL_CHARGE_ENDED:
      return Run<SpellChargeEndedEvent>(subs, fields, length, out, capacity, {
          F(SpellChargeEndedEvent, player, kI32), F(SpellChargeEndedEvent, caster, kHandle), F(SpellChargeEndedEvent,
          released, kBool)});
    case REFII_EVENT_MELEE_SWING_ENDED:
      return Run<MeleeSwingEndedEvent>(subs, fields, length, out, capacity, {
          F(MeleeSwingEndedEvent, entity, kHandle), F(MeleeSwingEndedEvent, player, kI32), F(MeleeSwingEndedEvent,
          action, kHandle), F(MeleeSwingEndedEvent, action_type, kI32), F(MeleeSwingEndedEvent, hits, kI32),
          F(MeleeSwingEndedEvent, strike, kI32), F(MeleeSwingEndedEvent, flourish, kBool)});
    case REFII_EVENT_ACTION_MOVE_INTERRUPT:
      return Run<ActionMoveInterruptEvent>(subs, fields, length, out, capacity, {
          F(ActionMoveInterruptEvent, entity, kHandle), F(ActionMoveInterruptEvent, player, kI32),
          F(ActionMoveInterruptEvent, action, kHandle), F(ActionMoveInterruptEvent, action_type, kI32)});
    case REFII_EVENT_CAMERA_CHANGED:
      return Run<CameraChangedEvent>(subs, fields, length, out, capacity, {
          F(CameraChangedEvent, player, kI32), F(CameraChangedEvent, mode, kI32), F(CameraChangedEvent, previous, kI32),
          F(CameraChangedEvent, hero, kHandle)});
    case REFII_EVENT_VORTEX_CAUGHT:
      return Run<VortexCaughtEvent>(subs, fields, length, out, capacity, {
          F(VortexCaughtEvent, serial, kU32), F(VortexCaughtEvent, tag, kI32), F(VortexCaughtEvent, entity, kHandle),
          F(VortexCaughtEvent, victim, kHandle), F(VortexCaughtEvent, player, kI32), F(VortexCaughtEvent,
          caster, kHandle), F(VortexCaughtEvent, caster_player, kI32), F(VortexCaughtEvent, caught, kBool)});
    case REFII_EVENT_APPEARANCE_CHANGED:
      return Run<AppearanceChangedEvent>(subs, fields, length, out, capacity, {
          F(AppearanceChangedEvent, player, kI32), F(AppearanceChangedEvent, automatic, kBool),
          F(AppearanceChangedEvent, hero, kHandle)});
    case REFII_EVENT_MORPH_CHANGED:
      return Run<MorphChangedEvent>(subs, fields, length, out, capacity, {
          F(MorphChangedEvent, player, kI32), F(MorphChangedEvent, morph, kU32), F(MorphChangedEvent, weight, kF32),
          F(MorphChangedEvent, hero, kHandle)});
    case REFII_EVENT_GUILD_SEALS:
      return Run<GuildSealsEvent>(subs, fields, length, out, capacity, {
          F(GuildSealsEvent, entity, kHandle), F(GuildSealsEvent, player, kI32), F(GuildSealsEvent, delta, kI32),
          F(GuildSealsEvent, seals, kI32), F(GuildSealsEvent, source, kStr)});
    case REFII_EVENT_TREASURY:
      return Run<TreasuryEvent>(subs, fields, length, out, capacity, {
          F(TreasuryEvent, treasury, kI32), F(TreasuryEvent, delta, kI32), F(TreasuryEvent, type, kI32)});
    case REFII_EVENT_KINGDOM_DECISION:
      return Run<KingdomDecisionEvent>(subs, fields, length, out, capacity, {
          F(KingdomDecisionEvent, demand, kU32), F(KingdomDecisionEvent, province, kU32), F(KingdomDecisionEvent, level,
          kU32), F(KingdomDecisionEvent, accepted, kBool)});
    case REFII_EVENT_CHEST_OPENED:
      return Run<ChestOpenedEvent>(subs, fields, length, out, capacity, {
          F(ChestOpenedEvent, chest, kHandle), F(ChestOpenedEvent, opener, kHandle), F(ChestOpenedEvent, player, kI32),
          F(ChestOpenedEvent, keys, kI32), F(ChestOpenedEvent, seals, kI32), F(ChestOpenedEvent, first,
          kBool)});
    case REFII_EVENT_ITEM_BOUGHT:
      return Run<ItemBoughtEvent>(subs, fields, length, out, capacity, {
          F(ItemBoughtEvent, shopkeeper, kHandle), F(ItemBoughtEvent, buyer, kHandle), F(ItemBoughtEvent, player, kI32),
          F(ItemBoughtEvent, definition, kU32), F(ItemBoughtEvent, price, kI32), F(ItemBoughtEvent, stolen,
          kBool)});
    case REFII_EVENT_ITEM_SOLD:
      return Run<ItemSoldEvent>(subs, fields, length, out, capacity, {
          F(ItemSoldEvent, shopkeeper, kHandle), F(ItemSoldEvent, seller, kHandle), F(ItemSoldEvent, player, kI32),
          F(ItemSoldEvent, definition, kU32), F(ItemSoldEvent, price, kI32), F(ItemSoldEvent, count, kI32)});
    case REFII_EVENT_SHOP_PRICE:
      return Run<ShopPriceEvent>(subs, fields, length, out, capacity, {
          F(ShopPriceEvent, shopkeeper, kHandle), F(ShopPriceEvent, customer, kHandle), F(ShopPriceEvent, player, kI32),
          F(ShopPriceEvent, definition, kU32), F(ShopPriceEvent, kind, kStr), F(ShopPriceEvent, price,
          kI32)});
    case REFII_EVENT_DIG_SPOT_DUG:
      return Run<DigSpotDugEvent>(subs, fields, length, out, capacity, {
          F(DigSpotDugEvent, spot, kHandle), F(DigSpotDugEvent, digger, kHandle), F(DigSpotDugEvent, player, kI32),
          F(DigSpotDugEvent, by_dog, kBool), F(DigSpotDugEvent, criminal, kBool)});
    case REFII_EVENT_DEMON_DOOR:
      return Run<DemonDoorEvent>(subs, fields, length, out, capacity, {
          F(DemonDoorEvent, door, kHandle), F(DemonDoorEvent, state, kStr), F(DemonDoorEvent, player, kI32)});
    case REFII_EVENT_DRINK:
      return Run<DrinkEvent>(subs, fields, length, out, capacity, {
          F(DrinkEvent, hero, kHandle), F(DrinkEvent, player, kI32), F(DrinkEvent, amount, kF32), F(DrinkEvent, drinks,
          kI32)});
    case REFII_EVENT_SANCTUARY:
      return Run<SanctuaryEvent>(subs, fields, length, out, capacity, {
          F(SanctuaryEvent, hero, kHandle), F(SanctuaryEvent, player, kI32), F(SanctuaryEvent, entered, kBool),
          F(SanctuaryEvent, reason, kI32)});
    case REFII_EVENT_SANCTUARY_REQUEST:
      return Run<SanctuaryRequestEvent>(subs, fields, length, out, capacity, {
          F(SanctuaryRequestEvent, hero, kHandle), F(SanctuaryRequestEvent, player, kI32), F(SanctuaryRequestEvent,
          reason, kI32), F(SanctuaryRequestEvent, to_sanctuary, kBool)});
    case REFII_EVENT_WEAPON_LEVEL_UP:
      return Run<WeaponLevelUpEvent>(subs, fields, length, out, capacity, {
          F(WeaponLevelUpEvent, weapon, kHandle), F(WeaponLevelUpEvent, owner, kHandle), F(WeaponLevelUpEvent, player,
          kI32), F(WeaponLevelUpEvent, level_from, kI32), F(WeaponLevelUpEvent, level, kI32),
          F(WeaponLevelUpEvent, cause, kStr)});
    case REFII_EVENT_WEAPON_AUGMENT_UNLOCKED:
      return Run<WeaponAugmentUnlockedEvent>(subs, fields, length, out, capacity, {
          F(WeaponAugmentUnlockedEvent, weapon, kHandle), F(WeaponAugmentUnlockedEvent, owner, kHandle),
          F(WeaponAugmentUnlockedEvent, player, kI32), F(WeaponAugmentUnlockedEvent, augment, kU32),
          F(WeaponAugmentUnlockedEvent, source, kStr), F(WeaponAugmentUnlockedEvent, level, kI32)});
    case REFII_EVENT_WEAPON_UPGRADE_READY:
      return Run<WeaponUpgradeReadyEvent>(subs, fields, length, out, capacity, {
          F(WeaponUpgradeReadyEvent, weapon, kHandle), F(WeaponUpgradeReadyEvent, owner, kHandle),
          F(WeaponUpgradeReadyEvent, player, kI32), F(WeaponUpgradeReadyEvent, added, kI32),
          F(WeaponUpgradeReadyEvent, pending, kI32), F(WeaponUpgradeReadyEvent, level, kI32)});
    case REFII_EVENT_HAND_HOLDING:
      return Run<HandHoldingEvent>(subs, fields, length, out, capacity, {
          F(HandHoldingEvent, villager, kHandle), F(HandHoldingEvent, leader, kHandle), F(HandHoldingEvent, player,
          kI32), F(HandHoldingEvent, holding, kBool), F(HandHoldingEvent, dragged, kBool), F(HandHoldingEvent,
          forced, kBool)});
    case REFII_EVENT_HAND_HOLDING_REQUEST:
      return Run<HandHoldingRequestEvent>(subs, fields, length, out, capacity, {
          F(HandHoldingRequestEvent, villager, kHandle), F(HandHoldingRequestEvent, leader, kHandle),
          F(HandHoldingRequestEvent, player, kI32), F(HandHoldingRequestEvent, forced, kBool)});
    case REFII_EVENT_INTERACTION_STARTED:
      return Run<InteractionStartedEvent>(subs, fields, length, out, capacity, {
          F(InteractionStartedEvent, hero, kHandle), F(InteractionStartedEvent, player, kI32),
          F(InteractionStartedEvent, target, kHandle), F(InteractionStartedEvent, interaction, kU32),
          F(InteractionStartedEvent, scripted, kBool)});
    case REFII_EVENT_INTERACTION_RESULT:
      return Run<InteractionResultEvent>(subs, fields, length, out, capacity, {
          F(InteractionResultEvent, hero, kHandle), F(InteractionResultEvent, player, kI32), F(InteractionResultEvent,
          target, kHandle), F(InteractionResultEvent, kind, kI32), F(InteractionResultEvent, success,
          kBool)});
    case REFII_EVENT_INTERACTION_MODE:
      return Run<InteractionModeEvent>(subs, fields, length, out, capacity, {
          F(InteractionModeEvent, hero, kHandle), F(InteractionModeEvent, player, kI32), F(InteractionModeEvent, target,
          kHandle), F(InteractionModeEvent, mode, kI32), F(InteractionModeEvent, previous, kI32),
          F(InteractionModeEvent, open, kBool)});
    case REFII_EVENT_RELATIONSHIP_STAGE:
      return Run<RelationshipStageEvent>(subs, fields, length, out, capacity, {
          F(RelationshipStageEvent, villager, kHandle), F(RelationshipStageEvent, hero, kHandle),
          F(RelationshipStageEvent, player, kI32), F(RelationshipStageEvent, stage, kI32),
          F(RelationshipStageEvent, previous, kI32), F(RelationshipStageEvent, cause, kStr)});
    case REFII_EVENT_DOG_DIG:
      return Run<DogDigEvent>(subs, fields, length, out, capacity, {
          F(DogDigEvent, dog, kHandle), F(DogDigEvent, spot, kHandle), F(DogDigEvent, player, kI32), F(DogDigEvent,
          first, kBool)});
    case REFII_EVENT_DOG_LEADING:
      return Run<DogLeadingEvent>(subs, fields, length, out, capacity, {
          F(DogLeadingEvent, dog, kHandle), F(DogLeadingEvent, player, kI32), F(DogLeadingEvent, target, kHandle),
          F(DogLeadingEvent, quest, kBool)});
    case REFII_EVENT_DOG_TRICK_LEARNED:
      return Run<DogTrickLearnedEvent>(subs, fields, length, out, capacity, {
          F(DogTrickLearnedEvent, dog, kHandle), F(DogTrickLearnedEvent, player, kI32), F(DogTrickLearnedEvent, trick,
          kU32), F(DogTrickLearnedEvent, shown, kBool), F(DogTrickLearnedEvent, tricks, kI32)});
    case REFII_EVENT_DOG_LEARNED:
      return Run<DogLearnedEvent>(subs, fields, length, out, capacity, {
          F(DogLearnedEvent, dog, kHandle), F(DogLearnedEvent, player, kI32), F(DogLearnedEvent, track, kI32),
          F(DogLearnedEvent, level, kI32), F(DogLearnedEvent, up, kBool), F(DogLearnedEvent, xp, kI32)});
    case REFII_EVENT_DIVE_SPOT_USED:
      return Run<DiveSpotUsedEvent>(subs, fields, length, out, capacity, {
          F(DiveSpotUsedEvent, spot, kHandle), F(DiveSpotUsedEvent, diver, kHandle), F(DiveSpotUsedEvent, player,
          kI32)});
    case REFII_EVENT_VOMIT:
      return Run<VomitEvent>(subs, fields, length, out, capacity, {
          F(VomitEvent, hero, kHandle), F(VomitEvent, player, kI32), F(VomitEvent, vomits, kI32)});
    case REFII_EVENT_JOB_STARTED:
      return Run<JobStartedEvent>(subs, fields, length, out, capacity, {
          F(JobStartedEvent, sign, kHandle), F(JobStartedEvent, hero, kHandle), F(JobStartedEvent, player, kI32),
          F(JobStartedEvent, job, kStr)});
    case REFII_EVENT_JOB_LEVEL:
      return Run<JobLevelEvent>(subs, fields, length, out, capacity, {
          F(JobLevelEvent, hero, kHandle), F(JobLevelEvent, player, kI32), F(JobLevelEvent, job, kStr), F(JobLevelEvent,
          level, kI32), F(JobLevelEvent, previous, kI32)});
    case REFII_EVENT_JOB_EARNINGS:
      return Run<JobEarningsEvent>(subs, fields, length, out, capacity, {
          F(JobEarningsEvent, hero, kHandle), F(JobEarningsEvent, player, kI32), F(JobEarningsEvent, job, kStr),
          F(JobEarningsEvent, total, kI32), F(JobEarningsEvent, earned, kI32)});
    case REFII_EVENT_JOB_WORKED:
      return Run<JobWorkedEvent>(subs, fields, length, out, capacity, {
          F(JobWorkedEvent, hero, kHandle), F(JobWorkedEvent, player, kI32), F(JobWorkedEvent, job, kStr),
          F(JobWorkedEvent, times, kI32), F(JobWorkedEvent, level, kI32)});
    case REFII_EVENT_JOB_MULTIPLIER:
      return Run<JobMultiplierEvent>(subs, fields, length, out, capacity, {
          F(JobMultiplierEvent, level, kI32), F(JobMultiplierEvent, on, kBool), F(JobMultiplierEvent, player, kI32)});
    case REFII_EVENT_JOB_SUMMARY:
      return Run<JobSummaryEvent>(subs, fields, length, out, capacity, {
          F(JobSummaryEvent, part, kStr), F(JobSummaryEvent, text, kStr), F(JobSummaryEvent, subtitle, kStr),
          F(JobSummaryEvent, amount, kI32)});
    case REFII_EVENT_GUILD_MESSAGE:
      return Run<GuildMessageEvent>(subs, fields, length, out, capacity, {
          F(GuildMessageEvent, message, kStr), F(GuildMessageEvent, narrator, kStr), F(GuildMessageEvent, player,
          kI32)});
    case REFII_EVENT_TITLE_CHANGED:
      return Run<TitleChangedEvent>(subs, fields, length, out, capacity, {
          F(TitleChangedEvent, hero, kHandle), F(TitleChangedEvent, player, kI32), F(TitleChangedEvent, title, kStr)});
    case REFII_EVENT_TITLE_UNLOCKED:
      return Run<TitleUnlockedEvent>(subs, fields, length, out, capacity, {
          F(TitleUnlockedEvent, hero, kHandle), F(TitleUnlockedEvent, player, kI32), F(TitleUnlockedEvent, title, kStr),
          F(TitleUnlockedEvent, titles, kI32)});
    case REFII_EVENT_CHAPTER_PROGRESS:
      return Run<ChapterProgressEvent>(subs, fields, length, out, capacity, {
          F(ChapterProgressEvent, hero, kHandle), F(ChapterProgressEvent, player, kI32), F(ChapterProgressEvent,
          progress, kF32), F(ChapterProgressEvent, previous, kF32)});
    case REFII_EVENT_SCAR_ADDED:
      return Run<ScarAddedEvent>(subs, fields, length, out, capacity, {
          F(ScarAddedEvent, entity, kHandle), F(ScarAddedEvent, player, kI32), F(ScarAddedEvent, area, kU32),
          F(ScarAddedEvent, scars, kI32)});
    case REFII_EVENT_SCAR_REMOVED:
      return Run<ScarRemovedEvent>(subs, fields, length, out, capacity, {
          F(ScarRemovedEvent, entity, kHandle), F(ScarRemovedEvent, player, kI32), F(ScarRemovedEvent, scars, kI32)});
    case REFII_EVENT_AGE_CHANGED:
      return Run<AgeChangedEvent>(subs, fields, length, out, capacity, {
          F(AgeChangedEvent, entity, kHandle), F(AgeChangedEvent, player, kI32), F(AgeChangedEvent, age, kF32),
          F(AgeChangedEvent, previous, kF32), F(AgeChangedEvent, limit, kF32)});
    case REFII_EVENT_HERO_STAGE:
      return Run<HeroStageEvent>(subs, fields, length, out, capacity, {
          F(HeroStageEvent, hero, kHandle), F(HeroStageEvent, player, kI32), F(HeroStageEvent, stage, kI32),
          F(HeroStageEvent, previous, kI32)});
    case REFII_EVENT_CLOTHING_WORN:
      return Run<ClothingWornEvent>(subs, fields, length, out, capacity, {
          F(ClothingWornEvent, entity, kHandle), F(ClothingWornEvent, player, kI32), F(ClothingWornEvent, definition,
          kU32), F(ClothingWornEvent, category, kI32)});
    case REFII_EVENT_CLOTHING_REMOVED:
      return Run<ClothingRemovedEvent>(subs, fields, length, out, capacity, {
          F(ClothingRemovedEvent, entity, kHandle), F(ClothingRemovedEvent, player, kI32), F(ClothingRemovedEvent,
          definition, kU32), F(ClothingRemovedEvent, category, kI32), F(ClothingRemovedEvent, count, kI32)});
    case REFII_EVENT_CLOTHING_DYED:
      return Run<ClothingDyedEvent>(subs, fields, length, out, capacity, {
          F(ClothingDyedEvent, entity, kHandle), F(ClothingDyedEvent, player, kI32), F(ClothingDyedEvent, definition,
          kU32), F(ClothingDyedEvent, dye, kU32), F(ClothingDyedEvent, red, kI32), F(ClothingDyedEvent, green,
          kI32), F(ClothingDyedEvent, blue, kI32), F(ClothingDyedEvent, brightness, kF32),
          F(ClothingDyedEvent, secondary, kBool)});
    case REFII_EVENT_PUB_GAME_STARTED:
      return Run<PubGameStartedEvent>(subs, fields, length, out, capacity, {
          F(PubGameStartedEvent, game, kStr), F(PubGameStartedEvent, variation, kStr), F(PubGameStartedEvent, hero,
          kHandle), F(PubGameStartedEvent, player, kI32)});
    case REFII_EVENT_PUB_GAME_FINISHED:
      return Run<PubGameFinishedEvent>(subs, fields, length, out, capacity, {
          F(PubGameFinishedEvent, game, kStr), F(PubGameFinishedEvent, variation, kStr), F(PubGameFinishedEvent, hero,
          kHandle), F(PubGameFinishedEvent, player, kI32), F(PubGameFinishedEvent, gold, kI32),
          F(PubGameFinishedEvent, result, kI32)});
    case REFII_EVENT_VILLAGE_WEALTH:
      return Run<VillageWealthEvent>(subs, fields, length, out, capacity, {
          F(VillageWealthEvent, village, kHandle), F(VillageWealthEvent, delta, kF32), F(VillageWealthEvent, scaled,
          kBool), F(VillageWealthEvent, wealth, kF32), F(VillageWealthEvent, min, kF32), F(VillageWealthEvent,
          max, kF32)});
    case REFII_EVENT_GARGOYLE_DESTROYED:
      return Run<GargoyleDestroyedEvent>(subs, fields, length, out, capacity, {
          F(GargoyleDestroyedEvent, level, kStr), F(GargoyleDestroyedEvent, player, kI32)});
    case REFII_EVENT_CAMERA_CUT:
      return Run<CameraCutEvent>(subs, fields, length, out, capacity, {F(CameraCutEvent, player, kI32)});
    case REFII_EVENT_CAMERA_BLEND:
      return Run<CameraBlendEvent>(subs, fields, length, out, capacity, {
          F(CameraBlendEvent, player, kI32), F(CameraBlendEvent, ticks, kI32)});
    case REFII_EVENT_CAMERA_SCREEN_CHANGED:
      return Run<CameraScreenChangedEvent>(subs, fields, length, out, capacity, {
          F(CameraScreenChangedEvent, mode, kI32), F(CameraScreenChangedEvent, split, kBool),
          F(CameraScreenChangedEvent, previous, kI32), F(CameraScreenChangedEvent, reason, kI32)});
    case REFII_EVENT_CAMERA_ZOOM_CHANGED:
      return Run<CameraZoomChangedEvent>(subs, fields, length, out, capacity, {
          F(CameraZoomChangedEvent, player, kI32), F(CameraZoomChangedEvent, set, kStr),
          F(CameraZoomChangedEvent, previous_set, kStr), F(CameraZoomChangedEvent, level, kI32),
          F(CameraZoomChangedEvent, combat, kF32), F(CameraZoomChangedEvent, string_length, kF32),
          F(CameraZoomChangedEvent, cage_radius, kF32), F(CameraZoomChangedEvent, max_cage_radius, kF32),
          F(CameraZoomChangedEvent, height, kF32), F(CameraZoomChangedEvent, focal_offset, kF32),
          F(CameraZoomChangedEvent, fov, kF32), F(CameraZoomChangedEvent, max_coop_distance, kF32)});
    case REFII_EVENT_CAMERA_TICK:
      return Run<CameraTickEvent>(subs, fields, length, out, capacity, {
          F(CameraTickEvent, player, kI32), F(CameraTickEvent, x, kF32), F(CameraTickEvent, y, kF32),
          F(CameraTickEvent, z, kF32), F(CameraTickEvent, fx, kF32), F(CameraTickEvent, fy, kF32),
          F(CameraTickEvent, fz, kF32), F(CameraTickEvent, ux, kF32), F(CameraTickEvent, uy, kF32),
          F(CameraTickEvent, uz, kF32), F(CameraTickEvent, fov, kF32), F(CameraTickEvent, cut, kBool)});
    default:
      return 0;
  }
}

#undef F

/// The bus handler a typed subscription stands on: one per catalog kind.
int32_t TypedBus(int32_t kind, const char* fields, size_t length, char* out, size_t capacity, void* user) {
  (void)user;
  if (out == nullptr || capacity == 0u || fields == nullptr) {
    return 0;
  }
  out[0] = 0;
  const std::vector<TypedSubscription> subs = SubscriptionsOf(kind);
  return subs.empty() ? 0 : RunKind(kind, subs, fields, length, out, capacity);
}

}  // namespace

// Route: the typed table above; a catalog kind's first subscription puts TypedBus on the native bus by
// the kind's Lua name (refii_mod_bus_subscribe, priority 0). Returns the subscription's index, -1 for a
// negative kind or no function.
extern "C" int32_t refii_mod_subscribe(int32_t kind, refii_mod_event_fn fn, void (*user)(void), int32_t priority,
                                       const char* owner) {
  if (fn == nullptr || kind < 0) {
    return -1;
  }
  TypedTable& table = Typed();
  bool join_bus = false;
  int32_t order = 0;
  {
    std::lock_guard<std::mutex> lock(table.mutex);
    order = static_cast<int32_t>(table.subscriptions.size());
    table.subscriptions.push_back(TypedSubscription{kind, fn, user, priority, owner != nullptr ? owner : "?", order});
    // Higher priority first; ties keep registration order.
    for (size_t i = table.subscriptions.size() - 1; i > 0; --i) {
      if (table.subscriptions[i - 1].priority >= table.subscriptions[i].priority) {
        break;
      }
      std::swap(table.subscriptions[i - 1], table.subscriptions[i]);
    }
    if (kind < REFII_EVENT_COUNT && !table.on_bus[static_cast<size_t>(kind)]) {
      table.on_bus[static_cast<size_t>(kind)] = true;
      join_bus = true;
    }
  }
  if (join_bus) {
    refii_mod_bus_subscribe(kKindNames[kind], &TypedBus, nullptr, 0);
  }
  return order;
}

// Route: REFII_MOD_API_VERSION, the header this payload was built with.
extern "C" int32_t refii_mod_api_version(void) { return REFII_MOD_API_VERSION; }

// Route: the typed table — every typed handler of `kind`, in priority order, on the caller's struct
// (Fable 2's first step). Fable 3's own seams raise through the bus, never here, so this is a mod's own
// kind (at or above REFII_EVENT_COUNT); Lua and Godot do not hear it.
extern "C" void refii_mod_dispatch(int32_t kind, void* event) {
  if (event == nullptr) {
    return;
  }
  for (const TypedSubscription& s : SubscriptionsOf(kind)) {
    s.fn(event, s.user);
  }
}
