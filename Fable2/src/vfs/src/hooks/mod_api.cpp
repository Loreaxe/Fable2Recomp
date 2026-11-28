// refii — what a hard mod's native code calls INTO: the per-tick callbacks and the hero lookup
// declared in core/refii_mod.h. Payload-internal: a mod's sources are compiled into this same
// module, so nothing here crosses the payload/host line.

#include <chrono>
#include <algorithm>
#include <array>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <unordered_map>
#include <vector>

#include <rex/logging.h>

#include "core/guest_state.h"
#include "core/payload_bridge_fable2.h"
#include "core/refii_mod.h"
#include "hooks/ppc_externs.h"
#include "vfs_hooks_private.h"

#include <atomic>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <string>

// The game's own bodies, by their config names — called, never overridden here.
PPC_EXTERN_FUNC(__imp__rex_CECStats_AbilityExperienceCost);
PPC_EXTERN_FUNC(__imp__rex_CSpellRegistry_FindLive);
PPC_EXTERN_FUNC(__imp__rex_CECFaction_RelationTo);
// Hooked in mod_events.cpp (the events), so a helper that must raise the event calls the name.
PPC_EXTERN_FUNC(rex_CECExperience_Modify);
PPC_EXTERN_FUNC(rex_CECExperience_Spend);
PPC_EXTERN_FUNC(rex_CSpellRegistry_Create);
PPC_EXTERN_FUNC(rex_CECHealth_Modify);
PPC_EXTERN_FUNC(rex_CECMoney_Modify);
PPC_EXTERN_FUNC(rex_CECInventory_AddOneOfRecord);
PPC_EXTERN_FUNC(rex_lhTrackedObject_RemoveHandleRef);
PPC_EXTERN_FUNC(rex_GDBManager_FindEntryByHash);
PPC_EXTERN_FUNC(rex_lhHeroStats_GetRenown);
PPC_EXTERN_FUNC(rex_lhHeroStats_GetMorality);
PPC_EXTERN_FUNC(rex_lhHeroStats_GetPurity);
// The stat modifiers (refii_mod_modify_renown ..): hooked in mod_events.cpp, so the events fire.
PPC_EXTERN_FUNC(rex_lhHeroStats_ModifyRenown);
// The slot table's one writer: hooked in mod_abilities.cpp (spell_pick), which passes a NOTIFY-clear
// write straight through (refii_mod_charge_spell).
PPC_EXTERN_FUNC(rex_CECSpellManager_SetSpellSlot);
PPC_EXTERN_FUNC(rex_lhHeroStats_ModifyMorality);
PPC_EXTERN_FUNC(rex_lhHeroStats_ModifyPurity);
PPC_EXTERN_FUNC(rex_Village_IsLineOfSight);
PPC_EXTERN_FUNC(rex_lhLuaScriptSystem_CreateEntityAtPosition);
// The lock, the statuses and the hit (refii_mod_lock_target .. refii_mod_stagger). OnHit is the
// Hit event's seam, so the name: a stated hit is a hit.
PPC_EXTERN_FUNC(rex_CPlayer_SetLookAtButtonDown);
PPC_EXTERN_FUNC(rex_SpellLightning_Electrify);
PPC_EXTERN_FUNC(rex_SpellChaos_ApplyToVictim);
PPC_EXTERN_FUNC(rex_SpellFireball_Ignite);
PPC_EXTERN_FUNC(rex_CECHitReceiver_OnHit);
// The opinion setter (refii_mod_modify_opinion): hooked in mod_events.cpp, so the event fires.
PPC_EXTERN_FUNC(rex_CECOpinionReaction_ModifyAxisValue);

namespace refii::vfs {
/// The script system the startup scripts ran on (mod_lua_hooks.cpp), or 0 before a session.
uint32_t ModScriptSystem();
uint32_t HeroEntity(uint8_t* base);
bool IsCoopJoiner(uint8_t* base);
/// guest_tick_pump.cpp: the quick menu's action (1 use an item, 2 perform an expression, 3 equip a
/// weapon, 4 assign a spell) for a player's hero, gated as the quick menu is; true when performed.
bool PerformQuickslot(PPCContext& ctx, uint8_t* base, uint32_t player, uint32_t kind, uint32_t record, uint32_t flags);
bool CoopSessionLive(uint8_t* base);
bool MenusActForPlayerTwo(uint8_t* base);
void RunModEventsTick(PPCContext& ctx, uint8_t* base, float seconds);
/// The deferred interrupt grants (refii_mod_allow_action_interrupt_at), granted once due.
void PumpActionInterrupts(PPCContext& ctx, uint8_t* base);

std::atomic<uint32_t> g_lua_generation{0};

void BumpLuaGeneration() {
  g_lua_generation.fetch_add(1u, std::memory_order_relaxed);
  refii_bridge_new_game_generation();
}
}  // namespace refii::vfs

namespace {

constexpr uint32_t kGameRoot = 0x83496AB8u;
constexpr uint32_t kHealthComponent = 36;
constexpr uint32_t kSpellManagerComponent = 56;
constexpr uint32_t kStatsComponent = 57;
constexpr uint32_t kExperienceComponent = 164;
constexpr uint32_t kHealthCurrent = 0x14;
constexpr uint32_t kHealthMax = 0x1C;
constexpr uint32_t kAbilityTable = 0x110;
constexpr uint32_t kExperiencePools = 0x10;
constexpr uint32_t kWorldRegistry = 0x44;
constexpr uint32_t kInventoryComponent = 13;
constexpr uint32_t kMoneyComponent = 81;
constexpr uint32_t kFactionComponent = 133;
constexpr uint32_t kEntityDefinition = 0x1C;
constexpr uint32_t kEntityName = 0x80;
constexpr uint32_t kPlacement = 0x7C;
constexpr uint32_t kSetPositionSlot = 25;
constexpr uint32_t kAppearanceComponent = 3;  // CECGraphicAppearance (ctor sub_82571DF8)
constexpr uint32_t kSetTintVector = 0x82573770u;
// The other component-3 setters (entity_appearance.cpp lists them): r3 the component, the value
// in f1 (alpha, scale) or r4 (a byte).
constexpr uint32_t kSetAlpha = 0x82573220u;           // disc 0x82547EC8
constexpr uint32_t kSetScale = 0x82573078u;           // disc 0x82547D20
constexpr uint32_t kSetSaturationMask = 0x822B8188u;  // disc 0x822B7EA8
constexpr uint32_t kSetAsDrawable = 0x825725A8u;      // disc 0x82547250
constexpr uint32_t kPlacementScaleSlot = 43;          // what SetScale calls on entity+0x7C (0x82573100)
// The target lock (refii_mod.h states each): the player component and its handle's HasMode.
constexpr uint32_t kPlayerComponent = 33;
constexpr uint32_t kTargetingComponent = 77;
constexpr uint32_t kPlayerHandle = 0x14;    // comp33 + 0x14, what HasPlayerMode dereferences (0x821aa5d8)
constexpr uint32_t kForceLockByte = 0x14;   // comp77 + 0x14, SetForceTargetLock's `stb r4,0x14(r3)`
constexpr uint32_t kTargetLockMode = 4;     // PLAYER_MODE_TARGET_LOCK
constexpr uint32_t kHandleHasMode = 0x821E8B90u;        // disc 0x821E8870
constexpr uint32_t kTargetingGetTargetSlot = 17;
constexpr uint32_t kTargetingSetTargetSlot = 19;
constexpr uint32_t kTargetingSwitchSlot = 49;
// The statuses: components, the installers' is-creature test and the removers.
constexpr uint32_t kStatusComponents[5] = {0u, 205u, 206u, 208u, 209u};
constexpr uint32_t kStatusCreatureComponent = 20;  // CECCreature: the installers' is-creature flag
constexpr uint32_t kRemoveElectrified = 0x827427B8u;  // disc 0x827178B8
constexpr uint32_t kRemoveChaos = 0x82749F38u;        // disc 0x8271F038
constexpr uint32_t kRemoveBurning = 0x827469A8u;      // disc 0x8271BAA8
// The hit record: its constructor, name setter and destructor, and the fields a Force Push fills.
constexpr uint32_t kHitReceiverComponent = 31;
constexpr uint32_t kHitCtor = 0x825C4958u;       // disc 0x824831E8
constexpr uint32_t kHitSetName = 0x82275438u;    // disc 0x82275368
constexpr uint32_t kHitDtor = 0x823919C0u;       // disc 0x8237D608
constexpr uint32_t kHitBytes = 0xE0u;            // 212 written by the constructor, rounded up
constexpr uint32_t kHitKnockdown = 0x18;
constexpr uint32_t kHitUnblockable = 0x19;
constexpr uint32_t kHitDamage = 0x38;
constexpr uint32_t kHitFlags = 0x40;
constexpr uint32_t kHitSerial = 0x44;
constexpr uint32_t kHitKindA = 0x50;
constexpr uint32_t kHitSpellType = 0x54;
constexpr uint32_t kHitKindB = 0x58;
constexpr uint32_t kHitOrigin = 0x70;
constexpr uint32_t kHitDirection = 0x90;
constexpr uint32_t kHitDirectionBack = 0xA0;
constexpr uint32_t kHitResponseName = 0xC0;
constexpr uint32_t kForcePushFlag = 0x20000u;
constexpr int32_t kForcePushType = 6;
constexpr uint32_t kManagerDefinitions = 400;
constexpr uint32_t kSpellSerial = 0x44;

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }
/// A guest C string into `out`, bounded; false for a null or implausible pointer.
bool CopyGuestString(uint8_t* base, uint32_t text, char* out, uint32_t size) {
  if (out == nullptr || size == 0) {
    return false;
  }
  out[0] = 0;
  if (!Plausible(text)) {
    return false;
  }
  uint32_t n = 0;
  for (; n + 1 < size && n < 512u; ++n) {
    const char c = static_cast<char>(base[text + n]);
    if (c == 0) {
      break;
    }
    out[n] = c;
  }
  out[n] = 0;
  return true;
}

/// An LHString handle (a pointer to the body whose first word is the char*), or 0.
uint32_t LhStringText(uint8_t* base, uint32_t handle_at) {
  const uint32_t body = refii::vfs::LoadGuestU32(base, handle_at);
  return Plausible(body) ? refii::vfs::LoadGuestU32(base, body) : 0u;
}

}  // namespace

namespace {


}  // namespace


extern "C" uint32_t refii_mod_local_hero(uint8_t* base) { return refii::vfs::HeroEntity(base); }

/// The players' heroes as the guest itself finds them (`sub_824CE278` 0x824ce3c0): the entity
/// registry `*(*(*(*(root)+0xC)+0x58)+4)` (the global at 0x83496B28), then `sub_8222C1B8` reads
/// player 1's handle at +0x6C and `sub_828E5A78` player 2's at +0x74, each a pointer to the
/// entity. Inlined rather than called: sub_828E5A78 is not emitted in the generated module.
extern "C" uint32_t refii_mod_player_hero(uint8_t* base, int32_t player) {
  if (player != 1 && player != 2) {
    return 0u;
  }

  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  uint32_t at = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  for (uint32_t offset : {0xCu, 0x58u, 0x4u, 0x0u}) {
    if (!plausible(at)) {
      return 0u;
    }
    at = refii::vfs::LoadGuestU32(base, at + offset);
  }

  if (!plausible(at)) {
    return 0u;
  }

  const uint32_t handle = player == 1 ? refii_bridge_hero_one_holder(base, at) : refii::vfs::LoadGuestU32(base, at + 0x74u);
  return plausible(handle) ? refii::vfs::LoadGuestU32(base, handle) : 0u;
}

extern "C" int32_t refii_mod_entity_position(PPCContext* ctx, uint8_t* base, uint32_t entity, float* out_xyz) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  if (ctx == nullptr || out_xyz == nullptr || !plausible(entity)) {
    return 0;
  }
  using refii::vfs::LoadGuestU32;
  const uint32_t placement = LoadGuestU32(base, entity + 0x7Cu);
  const uint32_t vtable = plausible(placement) ? LoadGuestU32(base, placement) : 0u;
  const uint32_t target = plausible(vtable) ? LoadGuestU32(base, vtable + 64u) : 0u;
  // The function table, not a scan of every mapping: this runs once per entity in a radius query.
  PPCFunc* getter = target != 0 && refii_bridge_rex_has_function(target) != 0
                        ? reinterpret_cast<PPCFunc*>(refii_bridge_rex_resolve_indirect_function(target))
                        : nullptr;
  if (getter == nullptr) {
    return 0;
  }
  // Scratch below the caller's frame; the getter stores the vector at r3.
  const uint32_t frame = (ctx->r1.u32 - 0x100u) & ~0xFu;
  const uint32_t out = frame + 0x60u;
  PPCContext call = *ctx;
  call.r1.u32 = frame;
  call.r3.u32 = out;
  call.r4.u32 = placement;
  getter(call, base);
  for (uint32_t i = 0; i < 3u; ++i) {
    const uint32_t bits = LoadGuestU32(base, out + 4u * i);
    std::memcpy(&out_xyz[i], &bits, sizeof(float));
  }
  return 1;
}

extern "C" uint32_t refii_mod_component(uint8_t* base, uint32_t entity, uint32_t id) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  if (id >= 256u || !plausible(entity)) {
    return 0u;
  }

  using refii::vfs::LoadGuestU32;
  const uint32_t present = LoadGuestU32(base, entity + 0x24u + (id / 32u) * 4u);
  if (((present >> (id % 32u)) & 1u) == 0u) {
    return 0u;
  }

  const uint32_t pairs = LoadGuestU32(base, entity + 0x48u);
  const uint32_t pairs_end = LoadGuestU32(base, entity + 0x4Cu);
  if (!plausible(pairs) || pairs_end < pairs || (pairs_end - pairs) > 8u * 512u) {
    return 0u;
  }

  const uint32_t index = LoadGuestU32(base, entity + 0x8Cu);
  if (plausible(index)) {
    const uint32_t at = pairs + 8u * base[index + id];
    if (at + 8u <= pairs_end && LoadGuestU32(base, at) == id) {
      const uint32_t component = LoadGuestU32(base, at + 4u);
      return plausible(component) ? component : 0u;
    }
  }

  for (uint32_t at = pairs; at + 8u <= pairs_end; at += 8u) {
    if (LoadGuestU32(base, at) == id) {
      const uint32_t component = LoadGuestU32(base, at + 4u);
      return plausible(component) ? component : 0u;
    }
  }
  return 0u;
}

extern "C" int32_t refii_mod_run_game_lua(PPCContext* ctx, uint8_t* base, const char* chunk) {
  if (ctx == nullptr || chunk == nullptr) {
    return 0;
  }
  return refii::vfs::RunGameLua(*ctx, base, chunk) ? 1 : 0;
}


extern "C" uint32_t refii_mod_lua_generation(void) {
  return refii::vfs::g_lua_generation.load(std::memory_order_relaxed);
}

namespace refii::vfs {

/// PLAYER STATE'S OWNERS AND HEROES (core/player_state.h), stated before the mods tick.
///
/// - Who plays which hero: both here outside an online session; inside one (a peer standing,
///   CoopSessionLive) the host plays player one and the joiner player two (IsCoopJoiner). A
///   joiner's player two came from the save it picked on the co-op screen (the co-op hero load
///   fills it, player_state_hooks.cpp).
/// - A new hero entity for a player drops that player's session values (a pool belongs to a
///   body); the saved ones stay until a save load replaces them.
/// - Player two leaving — the game's partner mode (game+0x11C: 1 online, 2 local co-op) dropping to
///   none — empties player two here: those values were the partner's and went home with them.
/// - A NEW GAME: player one's save name (SaveMgr+0, game+148, the LHString the save driver names
///   player one's package by, sub_82444C40 0x82444D14) changing to a name no load gave it. New Game
///   (sub_82379628) creates a fresh package and assigns its name there (sub_82445050, 0x82445358);
///   the only other writers are the load (0x82446b34, which player_state_hooks.cpp sees) and the
///   driver's first save of a game with no name (0x824455a4). Player one's values were the old
///   save's hero's; the new hero starts with none.
void DrivePlayerState(uint8_t* base) {
  static uint32_t heroes[2] = {0u, 0u};
  static uint32_t partner_mode = 0u;
  static std::string named;

  const bool live = CoopSessionLive(base);
  refii_bridge_player_set_owned(!live ? 3u : (IsCoopJoiner(base) ? 2u : 1u));

  const uint32_t root = LoadGuestU32(base, refii::guest::Address(kGameRoot));
  const uint32_t game = Plausible(root) ? LoadGuestU32(base, root + 12u) : 0u;
  if (!Plausible(game)) {
    return;
  }

  const uint32_t mode = LoadGuestU32(base, game + 0x11Cu);
  if (partner_mode != 0u && mode == 0u) {
    refii_bridge_player_clear(2, 0);
  }
  partner_mode = mode;

  constexpr uint32_t kSaveManager = 148u;
  const uint32_t rep = LoadGuestU32(base, game + kSaveManager);
  const uint32_t text = Plausible(rep) ? LoadGuestU32(base, rep) : 0u;
  const uint32_t length = Plausible(rep) ? LoadGuestU32(base, rep + 4u) : 0u;
  const std::string name = Plausible(text) && length != 0u && length <= 64u
                               ? std::string(reinterpret_cast<const char*>(base + text), length)
                               : std::string();
  if (name != named) {
    if (!named.empty() && !name.empty() && refii_bridge_player_loaded_from(1, name.c_str()) == 0) {
      refii_bridge_player_clear(1, 1);
    }
    named = name;
  }

  for (int32_t player = 1; player <= 2; ++player) {
    const uint32_t hero = refii_mod_player_hero(base, player);
    if (hero != 0u && hero != heroes[player - 1]) {
      refii_bridge_player_new_hero(player);
    }
    if (hero != 0u) {
      heroes[player - 1] = hero;
      // Its identity for Fable 3's partnerships (core/hero_partners.cpp), made where it is played.
      refii_bridge_hero_id_ensure(player);
    }
  }
}

/// Runs every mod's tick callback with the real seconds since the last guest tick. Called from
/// OnGuestTick, on the guest tick thread, where guest calls are legal.
void RunModTicks(PPCContext& ctx, uint8_t* base) {
  DrivePlayerState(base);

  using Clock = std::chrono::steady_clock;
  static Clock::time_point last = Clock::now();
  const Clock::time_point now = Clock::now();
  float seconds = std::chrono::duration<float>(now - last).count();
  last = now;
  // A hitch (a load screen, a breakpoint) is not time the player spent regenerating.
  if (seconds > 0.25f) {
    seconds = 0.25f;
  }

  DriveHouseholds(ctx, base, seconds);
  DriveWorldState(ctx, base, seconds);
  PumpActionInterrupts(ctx, base);
  refii_mod_run_ticks(&ctx, base, seconds);
  RunModEventsTick(ctx, base, seconds);
}

}  // namespace refii::vfs

// ---------------------------------------------------------------------------------------------
// Entities, stats, spells, the save
// ---------------------------------------------------------------------------------------------

// ---------------------------------------------------------------------------------------------
// A mod's requests for Godot: shared by every title (src/payload/src/core/mod_requests.cpp,
// core/refii_mod_requests.h). The Lua natives' handle forms, over the shared entries.
// ---------------------------------------------------------------------------------------------

namespace refii::vfs {

uint32_t PlayModFxOnHandle(const char* name, const float* xyz, uint32_t handle, const refii_mod_fx_options* options) {
  return refii_mod_play_fx_handle(name, xyz, handle, options);
}

uint32_t PlaceModLightOnHandle(uint32_t handle, const float* xyz, const float* rgb, float energy, float range,
                               const refii_mod_light_options* options) {
  return refii_mod_light_handle(handle, xyz, rgb, energy, range, options);
}

}  // namespace refii::vfs


extern "C" int32_t refii_mod_player_of(uint8_t* base, uint32_t entity) {
  if (entity == 0) {
    return 0;
  }
  if (entity == refii_mod_player_hero(base, 1)) {
    return 1;
  }
  if (entity == refii_mod_player_hero(base, 2)) {
    return 2;
  }
  return 0;
}

extern "C" int32_t refii_mod_menu_player(uint8_t* base) {
  return refii::vfs::IsCoopJoiner(base) || refii::vfs::MenusActForPlayerTwo(base) ? 2 : 1;
}

// How this game is shared: 0 alone, 1 local co-op (game+0x11C == 2, the guest's IsLocalCoopGame),
// 2 online with this machine hosting, 3 online with this machine joined. A partner counts once their
// hero exists.
extern "C" int32_t refii_mod_coop_mode(uint8_t* base) {
  if (base == nullptr || refii_mod_player_hero(base, 2) == 0u) {
    return 0;
  }
  if (refii::vfs::IsCoopJoiner(base)) {
    return 3;
  }
  const uint32_t root = refii::mods::LoadU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = root >= 0x10000u && root < 0xC0000000u ? refii::mods::LoadU32(base, root + 12u) : 0u;
  return game >= 0x10000u && game < 0xC0000000u && refii::mods::LoadU32(base, game + 0x11Cu) == 2u ? 1 : 2;
}

extern "C" int32_t refii_mod_world_here(uint8_t* base) { return refii_mod_coop_mode(base) == 3 ? 0 : 1; }

// guest_tick_pump.cpp: a player's dog (players+196's for player one, the co-op record for two).
extern "C" uint32_t RefiiCoopPlayerDog(uint8_t* base, int player);

// The player an entity deals with (core/refii_mod.h): its own if a hero, its master's if a dog,
// else the nearer living hero with 3 m of hysteresis — the rule the core co-op Lua's
// Refii.player_for states. The last answer per entity is kept for the hysteresis; the table is
// small (the entities a mod asks about) and starts over when it grows past its bound.
extern "C" int32_t refii_mod_player_for(PPCContext* ctx, uint8_t* base, uint32_t entity) {
  if (base == nullptr) {
    return 0;
  }
  const uint32_t one = refii_mod_player_hero(base, 1);
  const uint32_t two = refii_mod_player_hero(base, 2);
  const bool one_alive = one != 0u && refii_mod_entity_alive(base, one) != 0;
  const bool two_alive = two != 0u && refii_mod_entity_alive(base, two) != 0;
  if (one == 0u && two == 0u) {
    return 0;
  }
  if (const int32_t own = refii_mod_player_of(base, entity); own != 0) {
    return own;
  }
  if (entity != 0u) {
    if (entity == RefiiCoopPlayerDog(base, 1)) {
      return 1;
    }
    if (entity == RefiiCoopPlayerDog(base, 2)) {
      return 2;
    }
  }
  if (!one_alive || !two_alive) {
    // One hero standing is everyone's; with neither standing, the one that exists.
    return one_alive ? 1 : (two_alive ? 2 : (one != 0u ? 1 : 2));
  }
  float at[3];
  float p1[3];
  float p2[3];
  if (ctx == nullptr || entity == 0u || refii_mod_entity_position(ctx, base, entity, at) == 0 ||
      refii_mod_entity_position(ctx, base, one, p1) == 0 || refii_mod_entity_position(ctx, base, two, p2) == 0) {
    return 1;
  }
  const auto distance = [&at](const float* p) {
    const float dx = p[0] - at[0];
    const float dy = p[1] - at[1];
    const float dz = p[2] - at[2];
    return std::sqrt(dx * dx + dy * dy + dz * dz);
  };
  const float d1 = distance(p1);
  const float d2 = distance(p2);
  constexpr float kHysteresis = 3.0f;
  static std::unordered_map<uint32_t, int32_t> last;
  if (last.size() > 4096u) {
    last.clear();
  }
  auto found = last.find(entity);
  int32_t answer = d2 < d1 ? 2 : 1;
  if (found != last.end()) {
    // Keep the last answer until the other hero is clearly nearer.
    answer = found->second == 1 ? (d2 + kHysteresis < d1 ? 2 : 1) : (d1 + kHysteresis < d2 ? 1 : 2);
  }
  last[entity] = answer;
  return answer;
}

// A villager's opinion of either hero (core/coop_opinion.cpp): player one's is the opinion
// component's own current axes (+44, five floats); player two's the set kept for them, its current
// axes at block+20 in guest byte order, or the villager's base attitudes (+24) before player two
// has done anything there.
extern "C" int32_t refii_mod_opinion(uint8_t* base, uint32_t villager, int32_t player, float* out) {
  const uint32_t opinion = villager != 0u ? refii_mod_component(base, villager, 47u) : 0u;
  if (opinion == 0u || out == nullptr || (player != 1 && player != 2)) {
    return 0;
  }
  uint8_t two[REFII_OPINION_BLOCK_BYTES];
  const bool kept = player == 2 && refii_bridge_opinion_two_load(opinion, two) != 0;
  for (uint32_t axis = 0; axis < 5u; ++axis) {
    if (kept) {
      uint32_t raw = 0u;
      std::memcpy(&raw, two + 20u + (4u * axis), sizeof(raw));
      raw = __builtin_bswap32(raw);
      std::memcpy(&out[axis], &raw, sizeof(float));
    } else {
      out[axis] = refii::mods::LoadF32(base, opinion + (player == 1 ? 44u : 24u) + (4u * axis));
    }
  }
  return 1;
}

// The entity a hero is focused on: component 77's target (`GetFocusTarget` `sub_82843C40`,
// `*(*(comp + 0xC8))`), when it is alive (+144 & 0x40).
extern "C" uint32_t refii_mod_focus(uint8_t* base, int32_t player) {
  const uint32_t hero = refii_mod_player_hero(base, player);
  const uint32_t targeting = hero != 0u ? refii_mod_component(base, hero, 77u) : 0u;
  const uint32_t holder = targeting != 0u ? refii::mods::LoadU32(base, targeting + 0xC8u) : 0u;
  const uint32_t target = holder >= 0x10000u && holder < 0xC0000000u ? refii::mods::LoadU32(base, holder) : 0u;
  if (target < 0x10000u || target >= 0xC0000000u || (refii::mods::LoadU8(base, target + 144u) & 0x40u) == 0u) {
    return 0u;
  }
  return target;
}


// --- partner messages: the route's mod lane (core/event_relay.h, `pm:` lines) -------------------

namespace {



}  // namespace


namespace {



}  // namespace

// A signal and a published text for Godot on this machine, on the host-only UI kind: the payload
// crosses base64 (the bus's quoted field carries no quote), so it arrives exactly.



extern "C" int32_t refii_mod_entity_alive(uint8_t* base, uint32_t entity) {
  return Plausible(entity) && (base[entity + 0x90u] & 0x40u) != 0 ? 1 : 0;
}

extern "C" uint32_t refii_mod_world(uint8_t* base) {
  using refii::vfs::LoadGuestU32;
  const uint32_t root = LoadGuestU32(base, refii::guest::Address(kGameRoot));
  const uint32_t game = Plausible(root) ? LoadGuestU32(base, root + 0xCu) : 0u;
  const uint32_t holder = Plausible(game) ? LoadGuestU32(base, game + 0x58u) : 0u;
  const uint32_t world = Plausible(holder) ? LoadGuestU32(base, holder + 4u) : 0u;
  return Plausible(world) ? world : 0u;
}

extern "C" int32_t refii_mod_health(uint8_t* base, uint32_t entity, float* out_current, float* out_max) {
  const uint32_t health = refii_mod_component(base, entity, kHealthComponent);
  if (health == 0) {
    return 0;
  }
  if (out_current != nullptr) {
    *out_current = refii::mods::LoadF32(base, health + kHealthCurrent);
  }
  if (out_max != nullptr) {
    *out_max = refii::mods::LoadF32(base, health + kHealthMax);
  }
  return 1;
}

extern "C" int32_t refii_mod_set_health(uint8_t* base, uint32_t entity, float value) {
  const uint32_t health = refii_mod_component(base, entity, kHealthComponent);
  if (health == 0 || std::isnan(value)) {
    return 0;
  }
  const float max = refii::mods::LoadF32(base, health + kHealthMax);
  refii::mods::StoreF32(base, health + kHealthCurrent, value < 0.0f ? 0.0f : (value > max ? max : value));
  return 1;
}

extern "C" int32_t refii_mod_ability_level(uint8_t* base, uint32_t entity, int32_t type) {
  const uint32_t stats = refii_mod_component(base, entity, kStatsComponent);
  if (stats == 0 || type < 0 || type > 14) {
    return -1;
  }
  return static_cast<int32_t>(refii::mods::LoadU32(base, stats + kAbilityTable + 4u * static_cast<uint32_t>(type)));
}

extern "C" int32_t refii_mod_experience(uint8_t* base, uint32_t entity, int32_t pool, float* out) {
  const uint32_t experience = refii_mod_component(base, entity, kExperienceComponent);
  const uint32_t pools = experience != 0 ? refii::mods::LoadU32(base, experience + kExperiencePools) : 0u;
  if (!Plausible(pools) || pool < 0 || pool > 3) {
    return 0;
  }
  if (out != nullptr) {
    *out = refii::mods::LoadF32(base, pools + 4u * static_cast<uint32_t>(pool));
  }
  return 1;
}

extern "C" int32_t refii_mod_ability_cost(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t type, int32_t level) {
  const uint32_t stats = ctx != nullptr ? refii_mod_component(base, entity, kStatsComponent) : 0u;
  if (stats == 0 || type < 1 || type > 14 || level < 1) {
    return -1;
  }
  PPCContext work = *ctx;
  work.r3.u32 = stats;
  work.r4.u32 = static_cast<uint32_t>(type);
  work.r5.u32 = static_cast<uint32_t>(level);
  __imp__rex_CECStats_AbilityExperienceCost(work, base);
  return static_cast<int32_t>(work.r3.u32);
}

extern "C" int32_t refii_mod_give_experience(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t pool, float amount) {
  const uint32_t experience = ctx != nullptr ? refii_mod_component(base, entity, kExperienceComponent) : 0u;
  if (experience == 0 || pool < 0 || pool > 3) {
    return 0;
  }
  PPCContext work = *ctx;
  work.r3.u32 = experience;
  work.r4.u32 = static_cast<uint32_t>(pool);
  work.f1.f64 = static_cast<double>(amount);
  work.r6.u64 = 0;
  work.r7.u64 = 0;
  work.r8.u64 = 0;
  rex_CECExperience_Modify(work, base);
  return 1;
}

extern "C" int32_t refii_mod_spend_experience(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t pool, float amount) {
  const uint32_t experience = ctx != nullptr ? refii_mod_component(base, entity, kExperienceComponent) : 0u;
  if (experience == 0 || pool < 0 || pool > 3) {
    return 0;
  }
  PPCContext work = *ctx;
  work.r3.u32 = experience;
  work.r4.u32 = static_cast<uint32_t>(pool);
  work.f1.f64 = static_cast<double>(amount);
  rex_CECExperience_Spend(work, base);
  return 1;
}

namespace refii::vfs {
/// mod_events.cpp: the stamp the next spell factory call takes (refii_mod_cast_options).
void PushCastStamp(int32_t tag, float damage_scale);
void PopCastStamp();
}  // namespace refii::vfs

namespace {

/// The factory's spell types (sub_824B1828's switch on r4).
constexpr int32_t kSpellLightning = 0;
constexpr int32_t kSpellFireball = 1;
constexpr int32_t kSpellBlades = 3;
constexpr int32_t kSpellVortex = 4;
constexpr int32_t kSpellChaos = 5;
constexpr int32_t kSpellForcePush = 6;
constexpr const char* kSpellNames[8] = {"Lightning", "Fireball", "Slow Time", "Blades",
                                        "Vortex",    "Chaos",    "Force Push", "Raise Dead"};

/// sub_825FEFC8(entity r3, r4, f1) -> v1: the point a targeted Start aims at on its target. The
/// placement position (`lwz r4,0x7C(r31)`, slot 0x40, 0x825ff01c..30); with r4 clear, the body
/// point (component 23, `bl sub_824292A0` 0x825ff1ac) where the entity has one (+0x24 bit 8,
/// 0x825ff09c); lane 2 raised by f1 (`fadds f13,f0,f31` 0x825ff1cc), or by 0 for a dead entity
/// (+0x34 sign, 0x825ff03c) or posture 11 and -0.5 for posture 15 (sub_825FDB40, 0x825ff054..6c).
constexpr uint32_t kAimPoint = 0x825FEFC8u;
/// sub_8260D580(definitions hash, type, 1 "Targeted" / else "Area") -> entry: the definition the
/// factory hands the new spell's data read (sub_824B1828: `bl sub_8260D580`, then slot 0).
constexpr uint32_t kSpellDefinition = 0x8260D580u;
/// Targeted Lightning's data read, its class's slot 0 (sub_827405C0, r3 object, r4 definition):
/// copies five 32-byte per-power records from the defaults (0x83320BD0) to object +0x80, then
/// each power's GDB values over them; CreateImpactFX is the record's byte +0x1C (`stb r8,0x14(r30)`
/// 0x827407cc, r30 the record + 8).
constexpr uint32_t kLightningReadData = 0x827405C0u;
constexpr uint32_t kLightningRecords = 0x80u;
constexpr uint32_t kLightningRecordBytes = 32u;
constexpr uint32_t kLightningCreateImpactFx = 0x1Cu;
/// Targeted Lightning (sub_827407F0): with a target, the bolt runs from v1 to
/// sub_825FEFC8(target, CreateImpactFX of the power, 0.25) (`lbz r9,0x9C(r10)` 0x827408c0, `lfs
/// f1` flt_8209B730 0x827408d8, `bl` 0x827408dc), after a segment test from v1 to that point
/// (slot 0x44 type 0x16, 0x82740ce0..0x82740d14) that ends the bolt on the first creature or wall.
constexpr float kLightningLift = 0.25f;
/// Without a target the bolt's end is v1 + 15 v2 (flt_820994D8, 0x827408f4..0x8274091c), ±2 on
/// x and y (0x8274092c, 0x82740964); a clear segment then rolls > 0.5 (LHMath_GFFloatRandom
/// 0x82740a28, 0x82740a3c) to re-acquire a creature whose height is within 1.35 of v1's
/// (flt_820B139C 0x82740a38; `fabs(z - v1.z) <= 1.35` in sub_82743290, 0x82740af0).
constexpr float kLightningReach = 15.0f;
constexpr float kLightningReacquireHeight = 1.35f;
static_assert(kLightningReach > kLightningReacquireHeight,
              "a start one reach above the point is outside the re-acquire's height");
/// Targeted Fireball (sub_82743B10): the shot spawns at v1 (sub_828E5180 "SpellShotEntity"); a
/// shot within 1.0 of sub_825FEFC8(target, 0, 0) (0x82743d84..8c; `vmsum3fp` against flt_82099490
/// 0x82743e18..38) impacts there at once (`bl sub_827441F0` 0x82743e94), its normal
/// normalize(point - shot) (0x82743e54) — zero below the normaliser's epsilon.
constexpr float kFireballImpactReach = 1.0f;
/// Targeted Vortex (sub_828B20D0): its twister entity spawns at v1 + 1.25 v2 (flt_8209B760
/// 0x828b2148, `vmaddfp128` 0x828b2168, "VortexEntity" 0x828b2194).
constexpr float kVortexSpawnAhead = 1.25f;
/// Math_Vec3NormalizeInPlaceEps's epsilon (flt_82099A80): a shorter vector normalises to zero.
constexpr float kDirectionEpsilon = 1.0e-4f;

void CastAtError(const char* format, const char* what) {
  char error[256];
  std::snprintf(error, sizeof(error), format, what);
  refii_bridge_mod_error_post(error, std::strlen(error));
  REXLOG_WARN("[mods] {}", error);
}

/// sub_825FEFC8(entity, flag, lift): the game's own aim point on `entity`.
bool AimPoint(PPCContext* ctx, uint8_t* base, uint32_t entity, uint32_t flag, float lift, float* out) {
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = entity;
  work.r4.u32 = flag;
  work.f1.f64 = static_cast<double>(lift);
  if (!refii::mods::CallGuest(work, base, refii::guest::Address(kAimPoint))) {
    return false;
  }
  refii::mods::GetVector(work, 1, out);
  return std::isfinite(out[0]) && std::isfinite(out[1]) && std::isfinite(out[2]);
}

/// Targeted Lightning's CreateImpactFX at `power` for the caster's definitions, read by its own
/// class's data read into scratch below the caller's frame, as the factory feeds a new spell.
uint32_t LightningCreatesImpactFx(PPCContext* ctx, uint8_t* base, uint32_t definitions, int32_t power) {
  PPCContext lookup = refii::mods::Frame(*ctx, 0x400u);
  lookup.r3.u32 = definitions;
  lookup.r4.u32 = static_cast<uint32_t>(kSpellLightning);
  lookup.r5.u32 = 1u;
  if (!refii::mods::CallGuest(lookup, base, refii::guest::Address(kSpellDefinition))) {
    return 0u;
  }
  PPCContext read = refii::mods::Frame(*ctx, 0x400u);
  const uint32_t object = read.r1.u32 + 0x100u - kLightningRecords;  // records at r1+0x100..+0x1A0
  read.r3.u32 = object;
  read.r4.u32 = Plausible(lookup.r3.u32) ? lookup.r3.u32 : 0u;  // 0: the defaults alone, as the read does
  if (!refii::mods::CallGuest(read, base, refii::guest::Address(kLightningReadData))) {
    return 0u;
  }
  const uint32_t record = object + kLightningRecords + kLightningRecordBytes * static_cast<uint32_t>(power);
  return base[record + kLightningCreateImpactFx] != 0 ? 1u : 0u;
}

/// The unit direction from `from` to `to`, else `fallback` normalised, else up.
void Toward(const float* from, const float* to, const float* fallback, float* out) {
  float d[3] = {to[0] - from[0], to[1] - from[1], to[2] - from[2]};
  float length = std::sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
  if (!(length >= kDirectionEpsilon)) {
    d[0] = fallback[0];
    d[1] = fallback[1];
    d[2] = fallback[2];
    length = std::sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
  }
  if (!(length >= kDirectionEpsilon) || !std::isfinite(length)) {
    out[0] = 0.0f;
    out[1] = 0.0f;
    out[2] = 1.0f;
    return;
  }
  for (int i = 0; i < 3; ++i) {
    out[i] = d[i] / length;
  }
}

/// refii_mod_cast_options.at: the start (v1) and facing (v2) each class's Start makes of that
/// place, from `origin` (where the cast comes from) with `fallback` as the facing when the origin
/// stands on it. False, the mod error posted, for a type with no rule for the place.
bool PlaceCastAt(PPCContext* ctx, uint8_t* base, int32_t type, int32_t power, uint32_t definitions,
                 const refii_mod_cast_options& options, int32_t targeted, uint32_t target, const float* origin,
                 const float* fallback, float* v1, float* v2) {
  const char* name = kSpellNames[type];
  const bool entity = options.at_kind == REFII_MOD_CAST_AT_ENTITY;
  if (!entity && options.at_kind != REFII_MOD_CAST_AT_POINT) {
    CastAtError("cast_spell %s: at is an entity or a point", name);
    return false;
  }
  if (targeted == 0) {
    CastAtError("cast_spell %s: at casts the Targeted class; targeted must be set", name);
    return false;
  }
  float place[3] = {options.at_point[0], options.at_point[1], options.at_point[2]};
  if (entity) {
    if (!refii_mod_entity_alive(base, options.at_entity) || (target != 0 && target != options.at_entity)) {
      CastAtError("cast_spell %s: at names a live entity, and target is 0 or that entity", name);
      return false;
    }
    if (refii_mod_entity_position(ctx, base, options.at_entity, place) == 0) {
      return false;
    }
  } else if (target != 0 || !std::isfinite(place[0]) || !std::isfinite(place[1]) || !std::isfinite(place[2])) {
    CastAtError("cast_spell %s: at a point takes a finite point and no target", name);
    return false;
  }
  float u[3];
  switch (type) {
    case kSpellLightning:
      if (entity) {
        // On the bolt's own end: the segment test covers nothing, the bolt strikes the target.
        if (!AimPoint(ctx, base, options.at_entity, LightningCreatesImpactFx(ctx, base, definitions, power),
                      kLightningLift, v1)) {
          return false;
        }
        Toward(origin, v1, fallback, v2);
      } else {
        // One reach above the point, facing down: the end is the point, and no creature at the
        // point's height is within the re-acquire's 1.35 of the start.
        v1[0] = place[0];
        v1[1] = place[1];
        v1[2] = place[2] + kLightningReach;
        v2[0] = 0.0f;
        v2[1] = 0.0f;
        v2[2] = -1.0f;
      }
      return true;
    case kSpellFireball:
      if (!entity) {
        CastAtError("cast_spell %s: no rule for a point; its shot impacts at once only on a target (0x82743df0)", name);
        return false;
      }
      // Within the immediate-impact reach, set back toward the origin so the impact's normal is
      // the cast's direction rather than the zero a shot spawned on the point gets.
      if (!AimPoint(ctx, base, options.at_entity, 0u, 0.0f, place)) {
        return false;
      }
      Toward(origin, place, fallback, u);
      for (int i = 0; i < 3; ++i) {
        v1[i] = place[i] - 0.5f * kFireballImpactReach * u[i];
        v2[i] = u[i];
      }
      return true;
    case kSpellBlades:
      // Its swords are made at v1 (sub_825A2E68 stores v1 at +0xA0 0x825a2f54; each sword
      // sub_825A37E0 from it, 0x825a301c..28).
      Toward(origin, place, fallback, v2);
      v1[0] = place[0];
      v1[1] = place[1];
      v1[2] = place[2];
      return true;
    case kSpellVortex:
      Toward(origin, place, fallback, u);
      for (int i = 0; i < 3; ++i) {
        v1[i] = place[i] - kVortexSpawnAhead * u[i];
        v2[i] = u[i];
      }
      return true;
    case kSpellChaos:
      // sub_82748C28: the stream runs from v1 to sub_825FEFC8(target, 0, 0) (0x82748ca8..b0)
      // after a segment test that drops the target when anything else is struck first
      // (sub_828E5A10); the victim is turned only while the target stands (+0x74). No target, no
      // victim: nothing is done at a point.
      if (!entity) {
        CastAtError("cast_spell %s: no rule for a point; it acts only on its target (+0x74)", name);
        return false;
      }
      if (!AimPoint(ctx, base, options.at_entity, 0u, 0.0f, v1)) {
        return false;
      }
      Toward(origin, v1, fallback, v2);
      return true;
    case kSpellForcePush:
      // sub_828B2CC8: with a target it shoves along v1 (dropped to the ground, sub_825F6990
      // 0x828b2d9c) to sub_825FEFC8(target, 0, 0) (0x828b2db0); without one along v1 + 25 v2
      // (flt_8209BC54 0x828b2df4) to the first wall (slot 0x44, 0x828b2e64).
      if (entity && !AimPoint(ctx, base, options.at_entity, 0u, 0.0f, place)) {
        return false;
      }
      Toward(origin, place, fallback, v2);
      v1[0] = origin[0];
      v1[1] = origin[1];
      v1[2] = origin[2];
      return true;
    default:
      CastAtError("cast_spell %s: no rule for casting at a place", name);
      return false;
  }
}

}  // namespace

extern "C" uint32_t refii_mod_cast_spell(PPCContext* ctx, uint8_t* base, uint32_t caster, int32_t type, int32_t targeted,
                                         uint32_t target, int32_t level, const float* pos, const float* dir) {
  return refii_mod_cast_spell_with(ctx, base, caster, type, targeted, target, level, pos, dir, nullptr);
}

extern "C" uint32_t refii_mod_cast_spell_with(PPCContext* ctx, uint8_t* base, uint32_t caster, int32_t type,
                                              int32_t targeted, uint32_t target, int32_t level, const float* pos,
                                              const float* dir, const refii_mod_cast_options* options) {
  if (ctx == nullptr || type < 0 || type > 7 || !Plausible(caster)) {
    return 0;
  }
  const uint32_t manager = refii_mod_component(base, caster, kSpellManagerComponent);
  const uint32_t world = refii_mod_world(base);
  const uint32_t registry = world != 0 ? refii::mods::LoadU32(base, world + kWorldRegistry) : 0u;
  if (manager == 0 || !Plausible(registry) || (target != 0 && !refii_mod_entity_alive(base, target))) {
    return 0;
  }
  float at[3] = {0.0f, 0.0f, 0.0f};
  float facing[3] = {0.0f, 0.0f, 1.0f};
  if (pos != nullptr) {
    at[0] = pos[0];
    at[1] = pos[1];
    at[2] = pos[2];
  } else if (refii_mod_entity_position(ctx, base, target != 0 ? target : caster, at) == 0) {
    return 0;
  }
  if (dir != nullptr) {
    facing[0] = dir[0];
    facing[1] = dir[1];
    facing[2] = dir[2];
  } else if (target != 0) {
    float from[3];
    if (refii_mod_entity_position(ctx, base, caster, from) != 0) {
      const float d[3] = {at[0] - from[0], at[1] - from[1], at[2] - from[2]};
      const float length = std::sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
      if (length > 0.001f) {
        for (int i = 0; i < 3; ++i) {
          facing[i] = d[i] / length;
        }
      }
    }
  }
  const int32_t power = level < 0 ? 0 : (level > 4 ? 4 : level);
  const uint32_t definitions = refii::mods::LoadU32(base, manager + kManagerDefinitions);
  if (options != nullptr && options->at_kind != REFII_MOD_CAST_AT_NONE) {
    float origin[3] = {at[0], at[1], at[2]};
    if (pos == nullptr && refii_mod_entity_position(ctx, base, caster, origin) == 0) {
      return 0;
    }
    float fallback[3] = {0.0f, 0.0f, 0.0f};
    if (dir != nullptr) {
      fallback[0] = dir[0];
      fallback[1] = dir[1];
      fallback[2] = dir[2];
    } else {
      refii_mod_facing(ctx, base, caster, fallback);
    }
    if (!PlaceCastAt(ctx, base, type, power, definitions, *options, targeted, target, origin, fallback, at, facing)) {
      return 0;
    }
    targeted = 1;
    target = options->at_kind == REFII_MOD_CAST_AT_ENTITY ? options->at_entity : 0u;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = registry;
  work.r4.u32 = static_cast<uint32_t>(type);
  work.r5.u32 = targeted != 0 ? 1u : 0u;
  work.r6.u32 = target;
  work.r7.u32 = static_cast<uint32_t>(power);
  work.r8.u32 = caster;
  work.r9.u32 = definitions;
  refii::mods::SetVector(work, 1, at);
  refii::mods::SetVector(work, 2, facing);
  refii::vfs::PushCastStamp(options != nullptr ? options->tag : 0, options != nullptr ? options->damage_scale : 1.0f);
  rex_CSpellRegistry_Create(work, base);
  refii::vfs::PopCastStamp();
  return Plausible(work.r3.u32) ? work.r3.u32 : 0u;
}

extern "C" uint32_t refii_mod_spell_serial(uint8_t* base, uint32_t spell) {
  return Plausible(spell) ? refii::mods::LoadU32(base, spell + kSpellSerial) : 0u;
}

namespace {

/// A Targeted class's data read for refii_mod_spell_balance (r3 the object, r4 the definition, 0
/// for its defaults alone): where its per-power records start on the object and their stride, each
/// field's offset in a record (-1 the class has none), and the spread its Start draws on the status.
struct BalanceRead {
  int32_t type;
  uint32_t read;
  uint32_t records;
  uint32_t stride;
  int32_t status;
  float spread;
  int32_t duration;
  int32_t swords;
};

constexpr BalanceRead kBalanceReads[] = {
    // `*(v8 - 1)` with v8 = object + 0x88: ElectrifyDuration at record +4 (sub_827405C0).
    {kSpellLightning, kLightningReadData, kLightningRecords, kLightningRecordBytes, 4, 5.0f, -1, -1},
    // `v3 = result + 45`, nine words a power; `*(v11 - 1)` CreatureBurnTime at record +0 (sub_82743968).
    {kSpellFireball, 0x82743968u, 0xB4u, 36u, 0, 0.0f, -1, -1},
    // `v3 = result + 32`, five words a power; `*(v12 - 2)` NumSwords (type 1, int) at record +0 (sub_825A1D68).
    {kSpellBlades, 0x825A1D68u, 0x80u, 20u, -1, 0.0f, -1, 0},
    // `a1 + 128`, 0x5C a power; `*(v11 - 4)` Duration at record +0 (sub_828B1EA8).
    {kSpellVortex, 0x828B1EA8u, 0x80u, 92u, -1, 0.0f, 0, -1},
    // `v5 = result + 32`, one float a power: Duration (sub_82748B10).
    {kSpellChaos, 0x82748B10u, 0x80u, 4u, 0, 2.0f, -1, -1},
};

}  // namespace

extern "C" int32_t refii_mod_spell_balance(PPCContext* ctx, uint8_t* base, uint32_t caster, int32_t type, int32_t level,
                                           refii_mod_cast_balance* out) {
  if (out == nullptr) {
    return 0;
  }
  out->status_seconds = -1.0f;
  out->status_spread = -1.0f;
  out->duration = -1.0f;
  out->swords = -1;
  const BalanceRead* read = nullptr;
  for (const BalanceRead& r : kBalanceReads) {
    if (r.type == type) {
      read = &r;
    }
  }
  const uint32_t manager = ctx != nullptr && read != nullptr && Plausible(caster)
                               ? refii_mod_component(base, caster, kSpellManagerComponent)
                               : 0u;
  if (manager == 0) {
    return 0;
  }
  PPCContext lookup = refii::mods::Frame(*ctx, 0x400u);
  lookup.r3.u32 = refii::mods::LoadU32(base, manager + kManagerDefinitions);
  lookup.r4.u32 = static_cast<uint32_t>(type);
  lookup.r5.u32 = 1u;
  if (!refii::mods::CallGuest(lookup, base, refii::guest::Address(kSpellDefinition))) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx, 0x400u);
  const uint32_t object = work.r1.u32 + 0x100u - read->records;  // records from r1+0x100, above the read's own frame
  work.r3.u32 = object;
  work.r4.u32 = Plausible(lookup.r3.u32) ? lookup.r3.u32 : 0u;
  if (!refii::mods::CallGuest(work, base, refii::guest::Address(read->read))) {
    return 0;
  }
  const uint32_t power = static_cast<uint32_t>(level < 0 ? 0 : (level > 4 ? 4 : level));
  const uint32_t record = object + read->records + read->stride * power;
  if (read->status >= 0) {
    out->status_seconds = refii::mods::LoadF32(base, record + static_cast<uint32_t>(read->status));
    out->status_spread = read->spread;
  }
  if (read->duration >= 0) {
    out->duration = refii::mods::LoadF32(base, record + static_cast<uint32_t>(read->duration));
  }
  if (read->swords >= 0) {
    out->swords = static_cast<int32_t>(refii::mods::LoadU32(base, record + static_cast<uint32_t>(read->swords)));
  }
  return 1;
}

extern "C" uint32_t refii_mod_live_spell(PPCContext* ctx, uint8_t* base, int32_t type, uint32_t caster) {
  const uint32_t world = ctx != nullptr ? refii_mod_world(base) : 0u;
  const uint32_t registry = world != 0 ? refii::mods::LoadU32(base, world + kWorldRegistry) : 0u;
  if (!Plausible(registry) || type < 0 || type > 7) {
    return 0;
  }
  PPCContext work = *ctx;
  work.r3.u32 = registry;
  work.r4.u32 = static_cast<uint32_t>(type);
  work.r5.u32 = static_cast<uint32_t>(-2);  // targeted or area
  work.r6.u32 = caster;
  __imp__rex_CSpellRegistry_FindLive(work, base);
  return Plausible(work.r3.u32) ? work.r3.u32 : 0u;
}


namespace {

/// Player.IsAbleToSaveGame's body (TU1 0x823F3660 / disc 0x823D2070): r3 the player component.
constexpr uint32_t kIsAbleToSaveGame = 0x823F3660u;
/// AutoSaveGame's body (TU1 0x82445730 / disc 0x824E8D28): r3 the save manager, r4 defer, r5 force.
constexpr uint32_t kAutoSaveGame = 0x82445730u;
/// The game's save manager, *(game + 0x94): what AutoSaveGame is bound to (0x82459050).
constexpr uint32_t kGameSaveManager = 0x94;

}  // namespace

extern "C" int32_t refii_mod_can_save(PPCContext* ctx, uint8_t* base, int32_t player) {
  const uint32_t hero = ctx != nullptr ? refii_mod_player_hero(base, player) : 0u;
  const uint32_t component = hero != 0 ? refii_mod_component(base, hero, kPlayerComponent) : 0u;
  if (component == 0) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = component;
  return refii::mods::CallGuest(work, base, refii::guest::Address(kIsAbleToSaveGame)) && (work.r3.u32 & 0xFFu) != 0 ? 1 : 0;
}

extern "C" int32_t refii_mod_save_game(PPCContext* ctx, uint8_t* base, int32_t player) {
  if (refii_mod_can_save(ctx, base, player) == 0) {
    return 0;
  }
  const uint32_t root = refii::mods::LoadU32(base, refii::guest::Address(kGameRoot));
  const uint32_t game = Plausible(root) ? refii::mods::LoadU32(base, root + 12u) : 0u;
  const uint32_t manager = Plausible(game) ? refii::mods::LoadU32(base, game + kGameSaveManager) : 0u;
  if (!Plausible(manager)) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = manager;
  work.r4.u64 = 1;
  work.r5.u64 = 1;
  return refii::mods::CallGuest(work, base, refii::guest::Address(kAutoSaveGame)) ? 1 : 0;
}

// ---------------------------------------------------------------------------------------------
// Entity identity, factions, placement, gold, inventory, the level
// ---------------------------------------------------------------------------------------------

extern "C" uint32_t refii_mod_entity_definition(uint8_t* base, uint32_t entity) {
  return Plausible(entity) ? refii::mods::LoadU32(base, entity + kEntityDefinition) : 0u;
}

extern "C" int32_t refii_mod_entity_name(uint8_t* base, uint32_t entity, char* out, uint32_t size) {
  if (!Plausible(entity)) {
    if (out != nullptr && size != 0) {
      out[0] = 0;
    }
    return 0;
  }
  return CopyGuestString(base, LhStringText(base, entity + kEntityName), out, size) ? 1 : 0;
}

extern "C" int32_t refii_mod_relation(PPCContext* ctx, uint8_t* base, uint32_t entity, uint32_t other) {
  const uint32_t faction = ctx != nullptr ? refii_mod_component(base, entity, kFactionComponent) : 0u;
  if (faction == 0 || !Plausible(other)) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = faction;
  work.r4.u32 = other;
  __imp__rex_CECFaction_RelationTo(work, base);
  return static_cast<int32_t>(work.r3.u32);
}

extern "C" int32_t refii_mod_set_position(PPCContext* ctx, uint8_t* base, uint32_t entity, const float* xyz) {
  if (ctx == nullptr || xyz == nullptr || !Plausible(entity)) {
    return 0;
  }
  const uint32_t placement = refii::mods::LoadU32(base, entity + kPlacement);
  if (!Plausible(placement)) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  refii::mods::SetVector(work, 1, xyz);
  return refii::mods::CallVirtual(work, base, placement, kSetPositionSlot) ? 1 : 0;
}

namespace {

/// Physics.GetFacingVector (TU1 0x821F5BC0 / disc 0x821F5A90): r3 the out vector, r4 the placement.
constexpr uint32_t kPhysicsGetFacing = 0x821F5BC0u;
/// Physics.SetFacingVector (TU1 0x82266518 / disc 0x822664D8): r3 the placement, v1 the vector.
constexpr uint32_t kPhysicsSetFacing = 0x82266518u;
/// Player.SetFacingVector (TU1 0x823F3420 / disc 0x823D1E30): r3 the player component, v1 the vector.
constexpr uint32_t kPlayerSetFacing = 0x823F3420u;
/// Physics.GetVelocity is the placement's slot 19 (`lwz r11,0x4C(r12)` 0x8229dde4).
constexpr uint32_t kPlacementVelocitySlot = 19;
/// The component the Physics binder resolves (`sub_821E4368`: presence bit 2 at +0x24, slot 2).
constexpr uint32_t kPhysicsPlacementComponent = 2;
constexpr uint32_t kEntityInWorldByte = 0x90;
constexpr uint8_t kEntityInWorldBit = 0x40;

/// The placement the Physics binder hands its body: 0 for an entity not in the world.
uint32_t PhysicsPlacement(uint8_t* base, uint32_t entity) {
  if (!Plausible(entity) || (refii::mods::LoadU8(base, entity + kEntityInWorldByte) & kEntityInWorldBit) == 0) {
    return 0u;
  }
  return refii_mod_component(base, entity, kPhysicsPlacementComponent);
}

/// The vector a getter left at `at` (the r3 it returned).
bool ReadVector(uint8_t* base, uint32_t at, float* out_xyz) {
  if (!Plausible(at) || out_xyz == nullptr) {
    return false;
  }
  for (uint32_t i = 0; i < 3; ++i) {
    out_xyz[i] = refii::mods::LoadF32(base, at + 4u * i);
  }
  return true;
}

}  // namespace

extern "C" int32_t refii_mod_facing(PPCContext* ctx, uint8_t* base, uint32_t entity, float* out_xyz) {
  const uint32_t placement = ctx != nullptr ? PhysicsPlacement(base, entity) : 0u;
  if (placement == 0 || out_xyz == nullptr) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = work.r1.u32 + 0x40u;  // 16-byte aligned: the body stores with stvx128
  work.r4.u32 = placement;
  return refii::mods::CallGuest(work, base, refii::guest::Address(kPhysicsGetFacing)) && ReadVector(base, work.r3.u32, out_xyz)
             ? 1
             : 0;
}

extern "C" int32_t refii_mod_velocity(PPCContext* ctx, uint8_t* base, uint32_t entity, float* out_xyz) {
  const uint32_t placement = ctx != nullptr ? PhysicsPlacement(base, entity) : 0u;
  if (placement == 0 || out_xyz == nullptr) {
    return 0;
  }
  const uint32_t vtable = refii::mods::LoadU32(base, placement);
  if (vtable == 0) {
    return 0;
  }
  // Not CallVirtual, which puts the object in r3: this slot takes the out vector there and the object
  // in r4, and would write the velocity over the placement's own vtable.
  PPCContext work = refii::mods::Frame(*ctx);
  const uint32_t out = work.r1.u32 + 0x40u;
  work.r3.u32 = out;
  work.r4.u32 = placement;
  return refii::mods::CallGuest(work, base, refii::mods::LoadU32(base, vtable + kPlacementVelocitySlot * 4u)) &&
                 ReadVector(base, out, out_xyz)
             ? 1
             : 0;
}

extern "C" int32_t refii_mod_set_facing(PPCContext* ctx, uint8_t* base, uint32_t entity, const float* xyz) {
  if (xyz == nullptr || !std::isfinite(xyz[0]) || !std::isfinite(xyz[1]) || !std::isfinite(xyz[2])) {
    return 0;
  }
  const uint32_t placement = ctx != nullptr ? PhysicsPlacement(base, entity) : 0u;
  if (placement == 0) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  refii::mods::SetVector(work, 1, xyz);
  const uint32_t player = refii::mods::IsPlayerHero(base, entity) ? refii_mod_component(base, entity, kPlayerComponent) : 0u;
  if (player != 0) {
    work.r3.u32 = player;
    return refii::mods::CallGuest(work, base, refii::guest::Address(kPlayerSetFacing)) ? 1 : 0;
  }
  work.r3.u32 = placement;
  return refii::mods::CallGuest(work, base, refii::guest::Address(kPhysicsSetFacing)) ? 1 : 0;
}

// THE TINT SETTER, sub_82573770(CECGraphicAppearance* r3, const float r4[4]) (disc sub_82548418,
// the same 48 instructions): `lvx128 v0,r0,r4` (16-byte aligned {r, g, b, a}), skips when every
// lane is within 1e-4 of +80 (`vcmpgefp.` against flt_82099A80 = 1e-4, 0x825737bc), else stores
// +80..+95 (0x825737e0, 0x825737e8), marks the component dirty (+0x90 |= 0x200) and calls
// sub_82573318, which packs the record the entity feed and co-op state.
extern "C" int32_t refii_mod_entity_tint(PPCContext* ctx, uint8_t* base, uint32_t entity, float r, float g, float b,
                                         float a) {
  const float lanes[4] = {r, g, b, a};
  for (const float lane : lanes) {
    if (!std::isfinite(lane)) {
      return 0;
    }
  }
  const uint32_t look = ctx != nullptr ? refii_mod_component(base, entity, kAppearanceComponent) : 0u;
  if (look == 0) {
    return 0;
  }
  // Scratch below the caller's frame, above the setter's own.
  const uint32_t frame = (ctx->r1.u32 - 0x100u) & ~0xFu;
  const uint32_t rgba = frame + 0x60u;
  for (uint32_t i = 0; i < 4u; ++i) {
    refii::mods::StoreF32(base, rgba + 4u * i, lanes[i] > 0.0f ? lanes[i] : 0.0f);
  }
  PPCContext work = *ctx;
  work.r1.u32 = frame;
  work.r3.u32 = look;
  work.r4.u32 = rgba;
  return refii::mods::CallGuest(work, base, refii::guest::Address(kSetTintVector)) ? 1 : 0;
}

extern "C" int32_t refii_mod_entity_untint(PPCContext* ctx, uint8_t* base, uint32_t entity) {
  return refii_mod_entity_tint(ctx, base, entity, 1.0f, 1.0f, 1.0f, 1.0f);
}

// ---------------------------------------------------------------------------------------------
// The other appearance setters (refii_mod.h states each setter's instructions).
// ---------------------------------------------------------------------------------------------

namespace {

/// `setter(comp3, f1 = value)`: the alpha and scale setters take their float in f1.
int32_t SetAppearanceFloat(PPCContext* ctx, uint8_t* base, uint32_t entity, uint32_t setter, float value) {
  if (ctx == nullptr || !std::isfinite(value)) {
    return 0;
  }
  const uint32_t look = refii_mod_component(base, entity, kAppearanceComponent);
  if (look == 0) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = look;
  work.f1.f64 = static_cast<double>(value);
  return refii::mods::CallGuest(work, base, refii::guest::Address(setter)) ? 1 : 0;
}

/// `setter(comp3, r4 = byte)`: the saturation-mask and drawable setters take a byte in r4.
int32_t SetAppearanceByte(PPCContext* ctx, uint8_t* base, uint32_t entity, uint32_t setter, bool on) {
  const uint32_t look = ctx != nullptr ? refii_mod_component(base, entity, kAppearanceComponent) : 0u;
  if (look == 0) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = look;
  work.r4.u64 = on ? 1u : 0u;
  return refii::mods::CallGuest(work, base, refii::guest::Address(setter)) ? 1 : 0;
}

}  // namespace

extern "C" int32_t refii_mod_entity_alpha(PPCContext* ctx, uint8_t* base, uint32_t entity, float alpha) {
  if (!std::isfinite(alpha)) {
    return 0;
  }
  return SetAppearanceFloat(ctx, base, entity, kSetAlpha, std::clamp(alpha, 0.0f, 1.0f));
}

extern "C" int32_t refii_mod_entity_scale(PPCContext* ctx, uint8_t* base, uint32_t entity, float scale) {
  // The setter tells the placement through its vtable (`lwz r3,0x7C(r11)` 0x825730f8, no null
  // test), so an entity without one would crash inside it.
  if (!(scale > 0.0f) || !std::isfinite(scale) || !Plausible(entity) ||
      !Plausible(refii::mods::LoadU32(base, entity + kPlacement))) {
    return 0;
  }
  return SetAppearanceFloat(ctx, base, entity, kSetScale, scale);
}

extern "C" int32_t refii_mod_entity_saturation_mask(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t on) {
  return SetAppearanceByte(ctx, base, entity, kSetSaturationMask, on != 0);
}

extern "C" int32_t refii_mod_entity_drawable(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t on) {
  return SetAppearanceByte(ctx, base, entity, kSetAsDrawable, on != 0);
}

// ---------------------------------------------------------------------------------------------
// The target lock (refii_mod.h states the three switches and their instructions).
// ---------------------------------------------------------------------------------------------

namespace {

struct LockParts {
  uint32_t hero = 0;
  uint32_t player = 0;     // component 33
  uint32_t targeting = 0;  // component 77
};

LockParts PartsOf(uint8_t* base, int32_t player_index) {
  LockParts parts;
  parts.hero = refii_mod_player_hero(base, player_index);
  if (parts.hero != 0) {
    parts.player = refii_mod_component(base, parts.hero, kPlayerComponent);
    parts.targeting = refii_mod_component(base, parts.hero, kTargetingComponent);
  }
  return parts;
}

/// The targeting component's own GetTarget (slot 17), 0 for none.
uint32_t TargetOf(PPCContext& ctx, uint8_t* base, uint32_t targeting) {
  PPCContext query = refii::mods::Frame(ctx);
  return targeting != 0 && refii::mods::CallVirtual(query, base, targeting, kTargetingGetTargetSlot) ? query.r3.u32
                                                                                                        : 0u;
}

/// The player's look-at flag through its own writer; false when the game has no body for it.
bool SetLookAt(PPCContext& ctx, uint8_t* base, uint32_t player, bool on) {
  PPCContext work = refii::mods::Frame(ctx);
  work.r3.u32 = player;
  work.r4.u64 = on ? 1u : 0u;
  rex_CPlayer_SetLookAtButtonDown(work, base);
  return true;
}

/// Whether the player's handle holds mode `mode`: `sub_821E8B90(*(comp33+0x14), mode)`.
bool HasPlayerMode(PPCContext& ctx, uint8_t* base, uint32_t player, uint32_t mode) {
  const uint32_t handle = player != 0 ? refii::mods::LoadU32(base, player + kPlayerHandle) : 0u;
  if (!Plausible(handle)) {
    return false;
  }
  PPCContext query = refii::mods::Frame(ctx);
  query.r3.u32 = handle;
  query.r4.u64 = mode;
  return refii::mods::CallGuest(query, base, refii::guest::Address(kHandleHasMode)) && (query.r3.u32 & 0xFFu) != 0;
}

}  // namespace

extern "C" int32_t refii_mod_lock_target(PPCContext* ctx, uint8_t* base, int32_t player, uint32_t entity) {
  const LockParts parts = ctx != nullptr ? PartsOf(base, player) : LockParts{};
  if (parts.player == 0 || parts.targeting == 0) {
    return 0;
  }
  if (entity != 0) {
    if (!refii_mod_entity_alive(base, entity)) {
      return 0;
    }
    PPCContext work = refii::mods::Frame(*ctx);
    work.r4.u32 = entity;
    refii::mods::CallVirtual(work, base, parts.targeting, kTargetingSetTargetSlot);
  }
  refii::mods::StoreU8(base, parts.targeting + kForceLockByte, 1u);
  SetLookAt(*ctx, base, parts.player, true);
  return 1;
}

extern "C" int32_t refii_mod_release_lock(PPCContext* ctx, uint8_t* base, int32_t player) {
  const LockParts parts = ctx != nullptr ? PartsOf(base, player) : LockParts{};
  if (parts.player == 0 || parts.targeting == 0) {
    return 0;
  }
  refii::mods::StoreU8(base, parts.targeting + kForceLockByte, 0u);
  SetLookAt(*ctx, base, parts.player, false);
  return 1;
}

extern "C" uint32_t refii_mod_switch_target(PPCContext* ctx, uint8_t* base, int32_t player, int32_t left) {
  const LockParts parts = ctx != nullptr ? PartsOf(base, player) : LockParts{};
  if (parts.targeting == 0) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  work.r4.u64 = left != 0 ? 1u : 0u;
  if (!refii::mods::CallVirtual(work, base, parts.targeting, kTargetingSwitchSlot)) {
    return 0;
  }
  return TargetOf(*ctx, base, parts.targeting);
}

extern "C" uint32_t refii_mod_locked_target(PPCContext* ctx, uint8_t* base, int32_t player) {
  const LockParts parts = ctx != nullptr ? PartsOf(base, player) : LockParts{};
  if (parts.player == 0 || parts.targeting == 0) {
    return 0;
  }
  const bool locked = refii::mods::LoadU8(base, parts.targeting + kForceLockByte) != 0 ||
                      HasPlayerMode(*ctx, base, parts.player, kTargetLockMode);
  return locked ? TargetOf(*ctx, base, parts.targeting) : 0u;
}

// ---------------------------------------------------------------------------------------------
// Cutscenes (core/refii_mod_events.h CutsceneEvent quotes each instruction).
// ---------------------------------------------------------------------------------------------

namespace refii::vfs {
int32_t ModCameraHeldBits(int32_t player);  // mod_commands.cpp
}  // namespace refii::vfs

namespace {

/// comp33 +0x3B: `Player.SetForegroundCutsceneInEffect`'s `stb r4,0x3B(r3)` (0x823e72dc, disc 0x823c5d0c).
constexpr uint32_t kForegroundCutscene = 0x3B;
/// comp33 +0xC8: the LOOK_AT scope of `Player.SetOverrideCameraMode` (`stw r4,0xC8(r3)` 0x823e31cc /
/// 0x823e31e0, disc 0x823c1b94 / 0x823c1ba8); 0 is CAMERA_MODE_DEFAULT.
constexpr uint32_t kLookAtCameraMode = 0xC8;
/// `GUI.IsFullCutscenePlaying` (disc 0x82907D08): the top game-flow state's type == 7.
constexpr uint32_t kIsFullCutscenePlaying = 0x823060E0u;
/// The game-flow list it reads: *(*(*(*(game+12)+128)+4)+4); the node is *(list+4).
constexpr uint32_t kFlowChain[] = {12u, 128u, 4u, 4u};

/// The game's own `GUI.IsFullCutscenePlaying`, asked only past the empty-list test it traps on
/// (`cmplw r11,r10; bne; twui` 0x8230610c..14): false with no game, no flow list or no state.
bool FullCutscenePlaying(PPCContext& ctx, uint8_t* base) {
  uint32_t at = refii::mods::LoadU32(base, refii::guest::Address(kGameRoot));
  for (const uint32_t offset : kFlowChain) {
    if (!Plausible(at)) {
      return false;
    }
    at = refii::mods::LoadU32(base, at + offset);
  }
  if (!Plausible(at)) {
    return false;
  }
  const uint32_t node = refii::mods::LoadU32(base, at + 4u);
  if (node == at || !Plausible(node) || !Plausible(refii::mods::LoadU32(base, node + 8u))) {
    return false;
  }
  PPCContext query = refii::mods::Frame(ctx);
  return refii::mods::CallGuest(query, base, refii::guest::Address(kIsFullCutscenePlaying)) && (query.r3.u32 & 0xFFu) != 0;
}

/// The hero's own statements (INTERACTIVE, LOOK_AT) on its player component; 0 without one.
int32_t HeroCutsceneKinds(uint8_t* base, int32_t player) {
  const uint32_t hero = refii_mod_player_hero(base, player);
  const uint32_t comp = hero != 0 ? refii_mod_component(base, hero, kPlayerComponent) : 0u;
  if (comp == 0) {
    return 0;
  }
  int32_t kinds = 0;
  if (refii::mods::LoadU8(base, comp + kForegroundCutscene) != 0) {
    kinds |= REFII_CUTSCENE_INTERACTIVE;
  }
  // A camera_override hold standing on the LookAt half wrote this word itself (refii_mod_camera_override):
  // that is a mod's camera, not a scene's.
  if (refii::mods::LoadU32(base, comp + kLookAtCameraMode) != 0 && (refii::vfs::ModCameraHeldBits(player) & 2) == 0) {
    kinds |= REFII_CUTSCENE_LOOK_AT;
  }
  return kinds;
}

}  // namespace

extern "C" int32_t refii_mod_in_cutscene(PPCContext* ctx, uint8_t* base, int32_t player) {
  if (ctx == nullptr || base == nullptr) {
    return 0;
  }
  int32_t kinds = FullCutscenePlaying(*ctx, base) ? REFII_CUTSCENE_FULL : 0;
  if (player == 1 || player == 2) {
    kinds |= HeroCutsceneKinds(base, player);
  } else if (player == 0) {
    kinds |= HeroCutsceneKinds(base, 1) | HeroCutsceneKinds(base, 2);
  }
  return kinds;
}

namespace refii::vfs {

/// The tick's poll (mod_events.cpp): the full cutscene, and one player's own statements.
bool ModFullCutscenePlaying(PPCContext& ctx, uint8_t* base) { return FullCutscenePlaying(ctx, base); }
int32_t ModHeroCutsceneKinds(uint8_t* base, int32_t player) { return HeroCutsceneKinds(base, player); }

/// The lock_lost seam (mod_events.cpp rex_CPlayer_ClearPlayerMode): whether `player`'s hero holds
/// player mode 4, the game's own HasPlayerMode; the player whose mode handle (comp33+0x14) `handle`
/// is, 0 for neither; and the target its targeting component holds (GetTarget, slot 17).
bool ModHeroLockMode(PPCContext& ctx, uint8_t* base, int32_t player) {
  const LockParts parts = PartsOf(base, player);
  return parts.player != 0 && HasPlayerMode(ctx, base, parts.player, kTargetLockMode);
}
int32_t ModPlayerOfModeHandle(uint8_t* base, uint32_t handle) {
  for (int32_t player = 1; player <= 2 && Plausible(handle); ++player) {
    const LockParts parts = PartsOf(base, player);
    if (parts.player != 0 && refii::mods::LoadU32(base, parts.player + kPlayerHandle) == handle) {
      return player;
    }
  }
  return 0;
}
uint32_t ModHeroTarget(PPCContext& ctx, uint8_t* base, int32_t player) {
  return TargetOf(ctx, base, PartsOf(base, player).targeting);
}

}  // namespace refii::vfs

// ---------------------------------------------------------------------------------------------
// Movement speed, actions, the living test, the slot table (refii_mod.h quotes each instruction).
// ---------------------------------------------------------------------------------------------

namespace {

/// comp33 +0x1E: `Player.SetPlayerMovementSpeedOverridden` (`stb r4,0x1E(r3)` 0x823dff78).
constexpr uint32_t kSpeedOverridden = 0x1E;
/// comp33 +0xCC: `Player.SetOverriddenMovementSpeedMultiplier` (`stfs f1,0xCC(r3)` 0x8283ecc8).
constexpr uint32_t kSpeedMultiplier = 0xCC;
constexpr uint32_t kActionManager = 49;
/// manager +0x10: the current action (`stw r28,0(r31)` 0x822a496c, r31 = manager+0x10).
constexpr uint32_t kManagerCurrentAction = 0x10;
/// action +0x4B stopped (`stb r11,0x4B(r30)` 0x82709ff8), +0x4C holds the permission back, +0x4D
/// the permission (`sub_822CB150` 0x822cb1b4..c4; the handler's `stb r25,0x4D(r26)` 0x82709cb8).
constexpr uint32_t kActionStopped = 0x4B;
constexpr uint32_t kActionNoInterrupt = 0x4C;
constexpr uint32_t kActionInterruptAllowed = 0x4D;
/// The action's clock, vtable slot 7 (`sub_8226D418`).
constexpr uint32_t kActionElapsedSlot = 7;
constexpr uint32_t kCreature = 20;
/// comp20 +0x18: 1 from the constructor (0x823a318c), 0 once rex_CECCreature_Die ran (0x823a4ec8).
constexpr uint32_t kCreatureNotDied = 0x18;
/// comp56 +0x80: the int8 [2][5] slot table (`sub_82541F30` writes both rows, the launch reads
/// row = its flag, `lbz r9,0x80(r10)` 0x8253f0e8).
constexpr uint32_t kSlotTable = 0x80;
constexpr int32_t kSpellSlots = 5;

uint32_t PlayerComponentOf(uint8_t* base, int32_t player) {
  const uint32_t hero = refii_mod_player_hero(base, player);
  return hero != 0 ? refii_mod_component(base, hero, kPlayerComponent) : 0u;
}

}  // namespace

extern "C" int32_t refii_mod_set_speed_multiplier(uint8_t* base, int32_t player, float factor) {
  const uint32_t component = PlayerComponentOf(base, player);
  if (component == 0 || !std::isfinite(factor) || factor < 0.0f) {
    return 0;
  }
  // The scripts' order: the multiplier first, then the byte the locomotion tests.
  refii::mods::StoreF32(base, component + kSpeedMultiplier, factor);
  refii::mods::StoreU8(base, component + kSpeedOverridden, 1u);
  return 1;
}

extern "C" int32_t refii_mod_clear_speed_multiplier(uint8_t* base, int32_t player) {
  const uint32_t component = PlayerComponentOf(base, player);
  if (component == 0) {
    return 0;
  }
  refii::mods::StoreU8(base, component + kSpeedOverridden, 0u);
  return 1;
}

extern "C" int32_t refii_mod_speed_multiplier(uint8_t* base, int32_t player, float* out) {
  const uint32_t component = PlayerComponentOf(base, player);
  if (component == 0) {
    return 0;
  }
  if (out != nullptr) {
    *out = refii::mods::LoadF32(base, component + kSpeedMultiplier);
  }
  return refii::mods::LoadU8(base, component + kSpeedOverridden) != 0 ? 1 : 0;
}

extern "C" uint32_t refii_mod_current_action(uint8_t* base, uint32_t entity) {
  const uint32_t manager = refii_mod_component(base, entity, kActionManager);
  const uint32_t action = manager != 0 ? refii::mods::LoadU32(base, manager + kManagerCurrentAction) : 0u;
  if (!Plausible(action) || refii::mods::LoadU8(base, action + kActionStopped) != 0) {
    return 0;
  }
  return action;
}

extern "C" int32_t refii_mod_action_elapsed(PPCContext* ctx, uint8_t* base, uint32_t action, float* out) {
  if (ctx == nullptr || !Plausible(action) || !Plausible(refii::mods::LoadU32(base, action))) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  if (!refii::mods::CallVirtual(work, base, action, kActionElapsedSlot)) {
    return 0;
  }
  if (out != nullptr) {
    *out = static_cast<float>(work.f1.f64);
  }
  return 1;
}

extern "C" int32_t refii_mod_action_interruptible(uint8_t* base, uint32_t action) {
  return Plausible(action) && refii::mods::LoadU8(base, action + kActionNoInterrupt) == 0 &&
                 refii::mods::LoadU8(base, action + kActionInterruptAllowed) != 0
             ? 1
             : 0;
}

extern "C" int32_t refii_mod_allow_action_interrupt(uint8_t* base, uint32_t action) {
  if (!Plausible(action) || refii::mods::LoadU8(base, action + kActionNoInterrupt) != 0) {
    return 0;
  }
  refii::mods::StoreU8(base, action + kActionInterruptAllowed, 1u);
  return 1;
}

namespace {

/// A grant waiting for its action's clock (refii_mod_allow_action_interrupt_at).
struct PendingInterrupt {
  uint32_t action = 0;
  float at = 0.0f;
  float last = 0.0f;
  uint32_t ticks = 0;
};

/// More than both heroes' committed strikes and any creature a mod times besides.
std::array<PendingInterrupt, 32> g_pending_interrupts{};

/// A grant whose action never reaches its time is dropped after this many ticks (a minute at 30 Hz).
constexpr uint32_t kPendingInterruptTicks = 1800;

}  // namespace

extern "C" int32_t refii_mod_allow_action_interrupt_at(PPCContext* ctx, uint8_t* base, uint32_t action, float seconds) {
  float elapsed = 0.0f;
  if (ctx == nullptr || !Plausible(action) || refii::mods::LoadU8(base, action + kActionStopped) != 0 ||
      refii::mods::LoadU8(base, action + kActionNoInterrupt) != 0 || refii_mod_action_elapsed(ctx, base, action, &elapsed) == 0) {
    return 0;
  }
  if (!(seconds > elapsed)) {
    return refii_mod_allow_action_interrupt(base, action);
  }
  PendingInterrupt* slot = nullptr;
  for (PendingInterrupt& pending : g_pending_interrupts) {
    if (pending.action == action) {
      slot = &pending;
      break;
    }
    if (slot == nullptr && pending.action == 0u) {
      slot = &pending;
    }
  }
  if (slot == nullptr) {
    return 0;
  }
  *slot = PendingInterrupt{action, seconds, elapsed, 0u};
  return 1;
}

namespace refii::vfs {

/// Each waiting grant: given once its action's clock reaches the time; dropped when the action
/// stops, its clock runs backwards (the address is another action's now) or it never gets there.
void PumpActionInterrupts(PPCContext& ctx, uint8_t* base) {
  for (PendingInterrupt& pending : g_pending_interrupts) {
    if (pending.action == 0u) {
      continue;
    }
    float elapsed = 0.0f;
    PPCContext work = ctx;
    if (refii::mods::LoadU8(base, pending.action + kActionStopped) != 0 ||
        refii_mod_action_elapsed(&work, base, pending.action, &elapsed) == 0 || elapsed + 1e-4f < pending.last ||
        ++pending.ticks > kPendingInterruptTicks) {
      pending.action = 0u;
      continue;
    }
    pending.last = elapsed;
    if (elapsed >= pending.at) {
      refii_mod_allow_action_interrupt(base, pending.action);
      pending.action = 0u;
    }
  }
}

}  // namespace refii::vfs

extern "C" int32_t refii_mod_entity_living(uint8_t* base, uint32_t entity) {
  if (!refii_mod_entity_alive(base, entity)) {
    return 0;
  }
  const uint32_t creature = refii_mod_component(base, entity, kCreature);
  return creature == 0 || refii::mods::LoadU8(base, creature + kCreatureNotDied) != 0 ? 1 : 0;
}

extern "C" int32_t refii_mod_spell_in_slot(uint8_t* base, int32_t player, int32_t slot) {
  const uint32_t hero = refii_mod_player_hero(base, player);
  const uint32_t manager = hero != 0 ? refii_mod_component(base, hero, kSpellManagerComponent) : 0u;
  if (manager == 0 || slot < 0 || slot >= kSpellSlots) {
    return -1;
  }
  const int32_t type = static_cast<int8_t>(refii::mods::LoadU8(base, manager + kSlotTable + static_cast<uint32_t>(slot)));
  return type >= 0 && type <= 7 ? type : -1;
}

// ---------------------------------------------------------------------------------------------
// Status effects (refii_mod.h states each installer's registers and the bytes it reads).
// ---------------------------------------------------------------------------------------------

namespace {

/// The status component id for a kind, 0 for an unknown kind.
uint32_t StatusComponent(int32_t kind) {
  return kind >= 1 && kind <= 4 ? kStatusComponents[kind] : 0u;
}

constexpr int32_t kNoCastSerial = -1;

}  // namespace

extern "C" int32_t refii_mod_has_status(uint8_t* base, uint32_t entity, int32_t kind) {
  const uint32_t component = StatusComponent(kind);
  return component != 0 && refii_mod_component(base, entity, component) != 0 ? 1 : 0;
}

extern "C" int32_t refii_mod_apply_status(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t kind,
                                          float seconds, int32_t level, uint32_t source) {
  if (ctx == nullptr || !(seconds > 0.0f) || !std::isfinite(seconds) || !refii_mod_entity_alive(base, entity) ||
      StatusComponent(kind) == 0 || kind == REFII_STATUS_VORTEX) {
    return 0;
  }
  if (!Plausible(source) || !refii_mod_entity_alive(base, source)) {
    source = entity;
  }
  level = std::clamp(level, 0, 4);
  float xyz[3] = {0.0f, 0.0f, 0.0f};
  refii_mod_entity_position(ctx, base, entity, xyz);
  PPCContext work = refii::mods::Frame(*ctx, 0x200u);
  switch (kind) {
    case REFII_STATUS_ELECTRIFIED:
      work.r3.u32 = source;
      work.r4.u32 = entity;
      work.r5.s64 = kNoCastSerial;
      work.r6.s64 = level;
      work.f1.f64 = static_cast<double>(seconds);  // +40 duration
      work.f2.f64 = 0.0;                           // total damage: none
      work.f3.f64 = 0.5;                           // first tick delay (+60); moot at 0 damage
      work.f4.f64 = 0.5;                           // tick interval (+64)
      work.f5.f64 = 0.0;                           // +68
      rex_SpellLightning_Electrify(work, base);
      return 1;
    case REFII_STATUS_CHAOS:
      work.r3.u32 = source;
      work.r4.u32 = entity;
      work.r5.s64 = kNoCastSerial;
      work.r6.u64 = 0;
      work.r7.u64 = refii_mod_component(base, entity, kStatusCreatureComponent) != 0 ? 1u : 0u;
      work.r8.u64 = 0;  // not a replica: in co-op the game sends it itself
      work.f1.f64 = static_cast<double>(seconds);
      refii::mods::SetVector(work, 1, xyz);
      rex_SpellChaos_ApplyToVictim(work, base);
      return 1;
    case REFII_STATUS_BURNING: {
      // The three bytes the installer reads from ITS CALLER's frame (0x120+arg_6F/74/7F): the
      // frame `work` stands on is that caller's.
      refii::mods::StoreU8(base, work.r1.u32 + 0x6Fu, 1u);
      refii::mods::StoreU32(base, work.r1.u32 + 0x74u, 0u);
      refii::mods::StoreU8(base, work.r1.u32 + 0x7Fu, 1u);
      work.r3.u32 = entity;
      work.r4.u32 = source;
      work.r5.s64 = kNoCastSerial;
      work.r6.s64 = level;
      work.f1.f64 = 1.0;                           // strength, at distance 0
      work.f2.f64 = 0.0;                           // no falloff
      work.f3.f64 = static_cast<double>(seconds);  // burn time
      work.f4.f64 = 0.0;                           // burn damage: none
      work.f5.f64 = 1.0;                           // heat
      refii::mods::SetVector(work, 1, xyz);
      rex_SpellFireball_Ignite(work, base);
      return 1;
    }
    default:
      return 0;
  }
}

extern "C" int32_t refii_mod_clear_status(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t kind) {
  if (ctx == nullptr || refii_mod_has_status(base, entity, kind) == 0) {
    return 0;
  }
  uint32_t remover = 0;
  switch (kind) {
    case REFII_STATUS_ELECTRIFIED:
      remover = kRemoveElectrified;
      break;
    case REFII_STATUS_CHAOS:
      remover = kRemoveChaos;
      break;
    case REFII_STATUS_BURNING:
      remover = kRemoveBurning;
      break;
    default:
      return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = entity;
  return refii::mods::CallGuest(work, base, refii::guest::Address(remover)) ? 1 : 0;
}

// ---------------------------------------------------------------------------------------------
// Knockback and stagger: one hit record, the Force Push's shape (refii_mod.h quotes the builder).
// ---------------------------------------------------------------------------------------------

namespace {

/// The hit this thread is delivering through refii_mod_knockback / refii_mod_stagger, or 0.
thread_local uint32_t g_stated_hit = 0;

/// Builds the record on `frame` and delivers it; `vector` null for a stagger.
int32_t DeliverStatedHit(PPCContext* ctx, uint8_t* base, uint32_t entity, const float* vector) {
  if (ctx == nullptr || !refii_mod_entity_alive(base, entity)) {
    return 0;
  }
  const uint32_t receiver = refii_mod_component(base, entity, kHitReceiverComponent);
  // The receiver's own gates (0x825c5248..0x825c5278): a hit of our kind is dropped unless both
  // bytes stand, and a dropped hit would still have been built and destroyed — so refuse first.
  if (receiver == 0 || refii::mods::LoadU8(base, receiver + 0x18u) == 0 ||
      refii::mods::LoadU8(base, receiver + 0x19u) == 0) {
    return 0;
  }
  float xyz[3] = {0.0f, 0.0f, 0.0f};
  refii_mod_entity_position(ctx, base, entity, xyz);

  // Scratch below the caller's frame, above the callees' own and above the register home area a
  // callee may spill into (r1+0x54..0x94): the record, then the name.
  PPCContext work = refii::mods::Frame(*ctx, 0x300u);
  const uint32_t hit = work.r1.u32 + 0x100u;
  const uint32_t name = hit + kHitBytes;
  work.r3.u32 = hit;
  if (!refii::mods::CallGuest(work, base, refii::guest::Address(kHitCtor))) {
    return 0;
  }
  refii::mods::StoreF32(base, hit + kHitDamage, 0.0f);
  refii::mods::StoreU32(base, hit + kHitSerial, static_cast<uint32_t>(kNoCastSerial));
  if (vector != nullptr) {
    refii::mods::StoreU8(base, hit + kHitKnockdown, 1u);
    refii::mods::StoreU8(base, hit + kHitUnblockable, 1u);
    refii::mods::StoreU32(base, hit + kHitFlags, kForcePushFlag);
    refii::mods::StoreU32(base, hit + kHitKindA, 3u);
    refii::mods::StoreU32(base, hit + kHitSpellType, static_cast<uint32_t>(kForcePushType));
    refii::mods::StoreU32(base, hit + kHitKindB, 1u);
    for (uint32_t i = 0; i < 3u; ++i) {
      refii::mods::StoreF32(base, hit + kHitOrigin + 4u * i, xyz[i] - vector[i]);
      refii::mods::StoreF32(base, hit + kHitDirection + 4u * i, vector[i]);
      refii::mods::StoreF32(base, hit + kHitDirectionBack + 4u * i, -vector[i]);
    }
    refii::mods::StoreF32(base, hit + kHitOrigin + 12u, 0.0f);
    refii::mods::StoreF32(base, hit + kHitDirection + 12u, 0.0f);
    refii::mods::StoreF32(base, hit + kHitDirectionBack + 12u, 0.0f);
    static constexpr char kResponse[] = "ResponseToKnockdownUp";
    std::memcpy(base + name, kResponse, sizeof(kResponse));
    PPCContext naming = work;
    naming.r3.u32 = hit + kHitResponseName;
    naming.r4.u32 = name;
    refii::mods::CallGuest(naming, base, refii::guest::Address(kHitSetName));
  }

  PPCContext deliver = work;
  deliver.r3.u32 = receiver;
  deliver.r4.u32 = hit;
  deliver.r5.u64 = 1;
  g_stated_hit = hit;
  rex_CECHitReceiver_OnHit(deliver, base);
  g_stated_hit = 0;

  PPCContext destroy = work;
  destroy.r3.u32 = hit;
  refii::mods::CallGuest(destroy, base, refii::guest::Address(kHitDtor));
  return 1;
}

}  // namespace

bool refii::vfs::IsModStatedHit(uint32_t hit) { return hit != 0 && hit == g_stated_hit; }

extern "C" int32_t refii_mod_knockback(PPCContext* ctx, uint8_t* base, uint32_t entity, const float* dir, float force) {
  if (dir == nullptr || !std::isfinite(force)) {
    return 0;
  }
  float vector[3];
  for (int i = 0; i < 3; ++i) {
    if (!std::isfinite(dir[i])) {
      return 0;
    }
    vector[i] = dir[i] * force;
  }
  if (vector[0] == 0.0f && vector[1] == 0.0f && vector[2] == 0.0f) {
    return 0;
  }
  return DeliverStatedHit(ctx, base, entity, vector);
}

extern "C" int32_t refii_mod_stagger(PPCContext* ctx, uint8_t* base, uint32_t entity) {
  return DeliverStatedHit(ctx, base, entity, nullptr);
}

// ---------------------------------------------------------------------------------------------
// Sound: the game's own cue player and its music box (refii_mod.h quotes each body).
// ---------------------------------------------------------------------------------------------

namespace {

constexpr uint32_t kSoundManager = 0x83496D9Cu;       // disc 0x83496BCC: the manager the natives use
constexpr uint32_t kLhStringFromChars = 0x8222CED0u;  // (slot, chars)      disc 0x8222CF18
constexpr uint32_t kLhStringRelease = 0x82214DD8u;    // (slot)             disc 0x82214F08
constexpr uint32_t kCueCtor = 0x822671A8u;            // (cue, slot)        disc 0x82267168
constexpr uint32_t kCueDtor = 0x82289A48u;            // (cue)              disc 0x82289530
constexpr uint32_t kCueKnown = 0x82287DE0u;           // (manager+12, hash) disc 0x822878C8
constexpr uint32_t kPlayOnComponent = 0x821D9608u;    // (out, comp135, cue, tag slot) disc 0x821D9258
constexpr uint32_t kPlayOnEmitter = 0x821E0F48u;      // (out, emitter, cue, tag slot) disc 0x821E0B08
constexpr uint32_t kReleaseVoice = 0x82C05EF8u;       // (out)              disc 0x822ADE08
constexpr uint32_t kCreateEmitter = 0x829FD018u;      // (manager, v1 xyz)  disc 0x829F8250
constexpr uint32_t kFreeEmitter = 0x829FD168u;        // (manager, emitter) disc 0x829F83A0
constexpr uint32_t kStopTagged = 0x829FDEF0u;         // (emitter, tag slot, fade ms) disc 0x829F9128
constexpr uint32_t kPlayMusic = 0x8255BB28u;          // (name slot)        disc 0x825204B0
constexpr uint32_t kStopMusic = 0x8255BC20u;          // (name slot)        disc 0x825205A8
constexpr uint32_t kSoundComponent = 135;             // what PlayEventOnHero looks up (0x823ba99c)
constexpr uint32_t kComponentEmitter = 44;            // comp135 + 44, the emitter `sub_821D9608` makes
constexpr uint32_t kCueHash = 8;                      // cue + 8: `sub_82265220`'s a1[2]
constexpr uint32_t kPointEmitters = 8;

// The scratch a sound call lays out above the callees' spill area (Frame(ctx, 0x300)).
constexpr uint32_t kCueSlot = 0x100;
constexpr uint32_t kTagSlot = 0x110;
constexpr uint32_t kVoiceOut = 0x120;
constexpr uint32_t kCueRecord = 0x130;  // 72 bytes (`sub_82275380` writes to +65)
constexpr uint32_t kCueChars = 0x180;   // 128
constexpr uint32_t kTagChars = 0x200;   // 128
constexpr uint32_t kPointVector = 0x280;
constexpr uint32_t kNameBytes = 128;

/// A cue, tag or music name: printable ASCII without spaces, 1..127 bytes.
bool SoundName(const char* name, bool empty_ok) {
  if (name == nullptr) {
    return false;
  }
  const size_t n = std::strlen(name);
  if (n >= kNameBytes || (n == 0 && !empty_ok)) {
    return false;
  }
  for (size_t i = 0; i < n; ++i) {
    const unsigned char c = static_cast<unsigned char>(name[i]);
    if (c <= 0x20 || c >= 0x7F) {
      return false;
    }
  }
  return true;
}

uint32_t SoundManager(uint8_t* base) {
  const uint32_t manager = refii::mods::LoadU32(base, refii::guest::Address(kSoundManager));
  return Plausible(manager) ? manager : 0u;
}

/// An LHString slot on the frame holding `text`; released by ReleaseLhString.
bool MakeLhString(PPCContext& work, uint8_t* base, uint32_t slot, uint32_t chars, const char* text) {
  const size_t n = std::strlen(text);
  std::memcpy(base + chars, text, n + 1);
  refii::mods::StoreU32(base, slot, 0u);
  PPCContext call = work;
  call.r3.u32 = slot;
  call.r4.u32 = chars;
  call.r5.u64 = 0xFFFFFFFFu;  // NUL-terminated
  return refii::mods::CallGuest(call, base, refii::guest::Address(kLhStringFromChars));
}

void ReleaseLhString(PPCContext& work, uint8_t* base, uint32_t slot) {
  PPCContext call = work;
  call.r3.u32 = slot;
  refii::mods::CallGuest(call, base, refii::guest::Address(kLhStringRelease));
}

/// The point emitters refii_mod_play_sound_at made, oldest first; a slot is reused by its index.
uint32_t g_point_emitters[kPointEmitters] = {};
uint32_t g_point_next = 0;

/// Whether `emitter` is still in the manager's list (+4 the sentinel, nodes {next, prev, emitter}):
/// the game frees emitters of its own accord, and a freed one must not be walked.
bool EmitterListed(uint8_t* base, uint32_t manager, uint32_t emitter) {
  const uint32_t sentinel = refii::mods::LoadU32(base, manager + 4u);
  if (!Plausible(sentinel) || emitter == 0u) {
    return false;
  }
  uint32_t node = refii::mods::LoadU32(base, sentinel);
  for (uint32_t guard = 0; guard < 4096u && Plausible(node) && node != sentinel; ++guard) {
    if (refii::mods::LoadU32(base, node + 8u) == emitter) {
      return true;
    }
    node = refii::mods::LoadU32(base, node);
  }
  return false;
}

/// Builds the cue record and the tag on `work`'s frame and starts the voice on `target` through
/// `player` (the component player or the emitter player); the voice released as the native does.
bool PlayCue(PPCContext& work, uint8_t* base, uint32_t player, uint32_t target, const char* cue, const char* tag) {
  const uint32_t frame = work.r1.u32;
  if (!MakeLhString(work, base, frame + kCueSlot, frame + kCueChars, cue)) {
    return false;
  }
  if (!MakeLhString(work, base, frame + kTagSlot, frame + kTagChars, tag)) {
    ReleaseLhString(work, base, frame + kCueSlot);
    return false;
  }
  bool started = false;
  PPCContext call = work;
  call.r3.u32 = frame + kCueRecord;
  call.r4.u32 = frame + kCueSlot;
  if (refii::mods::CallGuest(call, base, refii::guest::Address(kCueCtor))) {
    bool known = true;
    if (player == kPlayOnEmitter) {
      // The component player checks the hash itself (0x821d9698); the emitter player does not.
      PPCContext ask = work;
      ask.r3.u32 = SoundManager(base) + 12u;
      ask.r4.u32 = refii::mods::LoadU32(base, frame + kCueRecord + kCueHash);
      known = refii::mods::CallGuest(ask, base, refii::guest::Address(kCueKnown)) && (ask.r3.u32 & 0xFFu) != 0;
    }
    if (known) {
      refii::mods::StoreU32(base, frame + kVoiceOut, 0u);
      refii::mods::StoreU32(base, frame + kVoiceOut + 4u, 0u);
      PPCContext play = work;
      play.r3.u32 = frame + kVoiceOut;
      play.r4.u32 = target;
      play.r5.u32 = frame + kCueRecord;
      play.r6.u32 = frame + kTagSlot;
      if (refii::mods::CallGuest(play, base, refii::guest::Address(player))) {
        started = refii::mods::LoadU32(base, frame + kVoiceOut) != 0u;
        PPCContext release = work;
        release.r3.u32 = frame + kVoiceOut;
        refii::mods::CallGuest(release, base, refii::guest::Address(kReleaseVoice));
      }
    }
    PPCContext destroy = work;
    destroy.r3.u32 = frame + kCueRecord;
    refii::mods::CallGuest(destroy, base, refii::guest::Address(kCueDtor));
  }
  ReleaseLhString(work, base, frame + kTagSlot);
  ReleaseLhString(work, base, frame + kCueSlot);
  return started;
}

int32_t StopTagged(PPCContext& work, uint8_t* base, uint32_t emitter, const char* tag, int32_t fade_ms) {
  const uint32_t frame = work.r1.u32;
  if (!MakeLhString(work, base, frame + kTagSlot, frame + kTagChars, tag)) {
    return 0;
  }
  PPCContext call = work;
  call.r3.u32 = emitter;
  call.r4.u32 = frame + kTagSlot;
  call.r5.s64 = fade_ms < 0 ? 0 : fade_ms;
  const bool ok = refii::mods::CallGuest(call, base, refii::guest::Address(kStopTagged));
  ReleaseLhString(work, base, frame + kTagSlot);
  return ok ? 1 : 0;
}

int32_t MusicCall(PPCContext* ctx, uint8_t* base, uint32_t function, const char* name) {
  if (ctx == nullptr || !SoundName(name, false) || SoundManager(base) == 0u) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx, 0x300u);
  const uint32_t frame = work.r1.u32;
  if (!MakeLhString(work, base, frame + kCueSlot, frame + kCueChars, name)) {
    return 0;
  }
  PPCContext call = work;
  call.r3.u32 = frame + kCueSlot;
  const bool ran = refii::mods::CallGuest(call, base, refii::guest::Address(function));
  ReleaseLhString(work, base, frame + kCueSlot);
  return ran && (function != kPlayMusic || call.r3.u32 != 0u) ? 1 : 0;
}

}  // namespace

extern "C" int32_t refii_mod_play_sound_at(PPCContext* ctx, uint8_t* base, const float* xyz, const char* cue,
                                           const char* tag) {
  if (ctx == nullptr || xyz == nullptr || !SoundName(cue, false) || !SoundName(tag == nullptr ? "" : tag, true)) {
    return 0;
  }
  for (int i = 0; i < 3; ++i) {
    if (!std::isfinite(xyz[i])) {
      return 0;
    }
  }
  const uint32_t manager = SoundManager(base);
  if (manager == 0u) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx, 0x300u);
  // The oldest of our emitters goes back to the manager before its slot is reused.
  uint32_t& slot = g_point_emitters[g_point_next % kPointEmitters];
  if (slot != 0u && EmitterListed(base, manager, slot)) {
    PPCContext give = work;
    give.r3.u32 = manager;
    give.r4.u32 = slot;
    refii::mods::CallGuest(give, base, refii::guest::Address(kFreeEmitter));
  }
  slot = 0u;
  PPCContext create = work;
  create.r3.u32 = manager;
  refii::mods::SetVector(create, 1, xyz);
  if (!refii::mods::CallGuest(create, base, refii::guest::Address(kCreateEmitter)) || !Plausible(create.r3.u32)) {
    return 0;
  }
  slot = create.r3.u32;
  g_point_next = (g_point_next + 1u) % kPointEmitters;
  return PlayCue(work, base, kPlayOnEmitter, slot, cue, tag == nullptr ? "" : tag) ? 1 : 0;
}

extern "C" int32_t refii_mod_play_sound_on(PPCContext* ctx, uint8_t* base, uint32_t entity, const char* cue,
                                           const char* tag) {
  if (ctx == nullptr || !refii_mod_entity_alive(base, entity) || !SoundName(cue, false) ||
      !SoundName(tag == nullptr ? "" : tag, true) || SoundManager(base) == 0u) {
    return 0;
  }
  const uint32_t sound = refii_mod_component(base, entity, kSoundComponent);
  if (sound == 0u) {
    float xyz[3] = {0.0f, 0.0f, 0.0f};
    return refii_mod_entity_position(ctx, base, entity, xyz) != 0 ? refii_mod_play_sound_at(ctx, base, xyz, cue, tag) : 0;
  }
  PPCContext work = refii::mods::Frame(*ctx, 0x300u);
  return PlayCue(work, base, kPlayOnComponent, sound, cue, tag == nullptr ? "" : tag) ? 1 : 0;
}

extern "C" int32_t refii_mod_stop_sound(PPCContext* ctx, uint8_t* base, uint32_t entity, const char* tag,
                                        int32_t fade_ms) {
  if (ctx == nullptr || !SoundName(tag == nullptr ? "" : tag, true)) {
    return 0;
  }
  const uint32_t manager = SoundManager(base);
  if (manager == 0u) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx, 0x300u);
  if (entity != 0u) {
    const uint32_t sound = refii_mod_entity_alive(base, entity) != 0 ? refii_mod_component(base, entity, kSoundComponent) : 0u;
    const uint32_t emitter = sound != 0u ? refii::mods::LoadU32(base, sound + kComponentEmitter) : 0u;
    return Plausible(emitter) ? StopTagged(work, base, emitter, tag == nullptr ? "" : tag, fade_ms) : 0;
  }
  int32_t walked = 0;
  for (uint32_t emitter : g_point_emitters) {
    if (emitter != 0u && EmitterListed(base, manager, emitter)) {
      walked |= StopTagged(work, base, emitter, tag == nullptr ? "" : tag, fade_ms);
    }
  }
  return walked;
}

extern "C" int32_t refii_mod_play_music(PPCContext* ctx, uint8_t* base, const char* name) {
  return MusicCall(ctx, base, kPlayMusic, name);
}

extern "C" int32_t refii_mod_stop_music(PPCContext* ctx, uint8_t* base, const char* name) {
  return MusicCall(ctx, base, kStopMusic, name);
}

// ---------------------------------------------------------------------------------------------
// Animation: the scripted action the shipped AI plays a named animation through.
// ---------------------------------------------------------------------------------------------

namespace {

/// An animation record name as the action table takes it: letters, digits and '_', 1..63 bytes.
bool AnimationName(const char* name) {
  if (name == nullptr || *name == 0) {
    return false;
  }
  size_t n = 0;
  for (const char* c = name; *c != 0; ++c, ++n) {
    const bool ok = (*c >= 'a' && *c <= 'z') || (*c >= 'A' && *c <= 'Z') || (*c >= '0' && *c <= '9') || *c == '_';
    if (!ok || n >= 63u) {
      return false;
    }
  }
  return true;
}

}  // namespace

extern "C" int32_t refii_mod_play_animation(PPCContext* ctx, uint8_t* base, uint32_t entity, const char* name,
                                            int32_t loops, float blend_seconds, float speed) {
  if (ctx == nullptr || !AnimationName(name) || !refii_mod_entity_alive(base, entity) || !std::isfinite(blend_seconds) ||
      !std::isfinite(speed) || !(speed > 0.0f)) {
    return 0;
  }
  if (blend_seconds < 0.0f) {
    blend_seconds = 0.0f;
  }
  char chunk[512];
  // BehaviourBase:PlayAnimation's table, and PlayLoopedAnimation's LOOP around it for loops.
  std::snprintf(chunk, sizeof(chunk),
                "local e = Refii.entity(%u) if e then "
                "local a = { Type = EScriptableAction.PLAY_ANIMATION, Anim = \"%s\", OverrideLooking = true, "
                "SpeedMultiplier = %.4f, BlendInTime = %.4f } "
                "if %d > 1 then a = { Type = EScriptableAction.LOOP, LoopAction = a, NumLoops = %d, KillFXOnFinish = true } end "
                "Action.SetCurrentAction(e, a) end",
                refii_mod_handle(entity), name, static_cast<double>(speed), static_cast<double>(blend_seconds), loops, loops);
  return refii_mod_run_game_lua(ctx, base, chunk);
}

extern "C" int32_t refii_mod_stop_animation(PPCContext* ctx, uint8_t* base, uint32_t entity) {
  if (ctx == nullptr || !refii_mod_entity_alive(base, entity)) {
    return 0;
  }
  char chunk[128];
  std::snprintf(chunk, sizeof(chunk), "local e = Refii.entity(%u) if e then Action.FinishScriptedActions(e) end",
                refii_mod_handle(entity));
  return refii_mod_run_game_lua(ctx, base, chunk);
}

// ---------------------------------------------------------------------------------------------
// Opinion and deeds: the game's own setters, per hero (refii_mod.h quotes each).
// ---------------------------------------------------------------------------------------------

extern "C" int32_t refii_mod_opinion_enter(PPCContext* ctx, uint8_t* base, uint32_t villager, uint32_t hero);
extern "C" void refii_mod_opinion_leave(void);

extern "C" int32_t refii_mod_modify_opinion(PPCContext* ctx, uint8_t* base, uint32_t villager, uint32_t hero,
                                            int32_t axis, float delta) {
  const uint32_t opinion = ctx != nullptr && refii_mod_entity_alive(base, villager) != 0 ? refii_mod_component(base, villager, 47u) : 0u;
  if (opinion == 0u || axis < 0 || axis > 4 || !std::isfinite(delta) || !refii_mod_entity_alive(base, hero)) {
    return 0;
  }
  const bool scoped = refii_mod_opinion_enter(ctx, base, villager, hero) != 0;
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = opinion;
  work.r4.s64 = axis;
  work.f1.f64 = static_cast<double>(delta);
  rex_CECOpinionReaction_ModifyAxisValue(work, base);
  if (scoped) {
    refii_mod_opinion_leave();
  }
  return 1;
}

extern "C" int32_t refii_mod_add_deed(PPCContext* ctx, uint8_t* base, uint32_t villager, uint32_t hero, uint32_t deed) {
  if (ctx == nullptr || deed == 0u || !refii_mod_entity_alive(base, villager) || !refii_mod_entity_alive(base, hero) ||
      refii_mod_component(base, villager, 47u) == 0u) {
    return 0;
  }
  // The deed record, by the hash a mod names it with; its id is the record's first word.
  const uint32_t gdb = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496DA0u));
  if (!Plausible(gdb)) {
    return 0;
  }
  PPCContext lookup = refii::mods::Frame(*ctx);
  lookup.r3.u32 = gdb;
  lookup.r4.u32 = deed;
  rex_GDBManager_FindEntryByHash(lookup, base);
  if (!Plausible(lookup.r3.u32)) {
    return 0;
  }
  const uint32_t id = refii::mods::LoadU32(base, lookup.r3.u32);
  // The message system, as the game reaches it (0x823ff8f4): *(*(*(root+12)+88)+4)+16.
  const uint32_t root = refii::mods::LoadU32(base, refii::guest::Address(kGameRoot));
  const uint32_t game = Plausible(root) ? refii::mods::LoadU32(base, root + 12u) : 0u;
  const uint32_t manager = Plausible(game) ? refii::mods::LoadU32(base, game + 88u) : 0u;
  const uint32_t cell = Plausible(manager) ? refii::mods::LoadU32(base, manager + 4u) : 0u;
  const uint32_t messages = Plausible(cell) ? refii::mods::LoadU32(base, cell + 16u) : 0u;
  float xyz[3] = {0.0f, 0.0f, 0.0f};
  if (!Plausible(messages) || refii_mod_entity_position(ctx, base, hero, xyz) == 0) {
    return 0;
  }
  PPCContext post = refii::mods::Frame(*ctx, 0x200u);
  const uint32_t pos = post.r1.u32 + 0x100u;
  for (uint32_t i = 0; i < 3u; ++i) {
    refii::mods::StoreF32(base, pos + 4u * i, xyz[i]);
  }
  refii::mods::StoreF32(base, pos + 12u, 0.0f);
  post.r3.u32 = messages;
  post.r4.u64 = 50u;  // MESSAGE_EVENT_DEED_PERSON (miscellaneous/messageeventenum.lua)
  post.r5.u32 = pos;
  post.r6.u32 = hero;
  post.r7.u32 = villager;
  post.r8.u64 = 101u;
  post.r9.u32 = id;
  return refii::mods::CallGuest(post, base, refii::guest::Address(0x82279B80u)) ? 1 : 0;
}

// ---------------------------------------------------------------------------------------------
// NPC behaviour: the quests' own orders, run in the game VM (refii_mod.h quotes each entry).
// ---------------------------------------------------------------------------------------------

namespace {

/// Runs `body` with `e` = entity's game userdata and `t` = other's (nil when other is 0);
/// refused before the VM for a dead entity or other.
int32_t NpcChunk(PPCContext* ctx, uint8_t* base, uint32_t entity, uint32_t other, const char* body) {
  if (ctx == nullptr || !refii_mod_entity_alive(base, entity) || (other != 0u && !refii_mod_entity_alive(base, other))) {
    return 0;
  }
  char chunk[768];
  std::snprintf(chunk, sizeof(chunk),
                "local e = Refii.entity(%u) local t = Refii.entity(%u) if e and (t or %u == 0) then %s end",
                refii_mod_handle(entity), other != 0u ? refii_mod_handle(other) : 0u, other != 0u ? 1u : 0u, body);
  return refii_mod_run_game_lua(ctx, base, chunk);
}

}  // namespace

extern "C" int32_t refii_mod_npc_follow(PPCContext* ctx, uint8_t* base, uint32_t entity, uint32_t target, float radius) {
  if (target == 0u || entity == target || !std::isfinite(radius)) {
    return 0;
  }
  char body[160];
  // qr_assassination.lua: Follow.FollowEntity(GuardA, TargetNPC, 2.0).
  std::snprintf(body, sizeof(body), "if Follow.IsAvailable(e) then Follow.FollowEntity(e, t, %.3f) end",
                static_cast<double>(radius > 0.0f ? radius : 2.0f));
  return NpcChunk(ctx, base, entity, target, body);
}

extern "C" int32_t refii_mod_npc_go_to(PPCContext* ctx, uint8_t* base, uint32_t entity, const float* xyz) {
  if (xyz == nullptr || !std::isfinite(xyz[0]) || !std::isfinite(xyz[1]) || !std::isfinite(xyz[2])) {
    return 0;
  }
  char body[200];
  // quests/miscfunctions.lua main.25, as qc080_monksquest.lua calls it (wait = false: no yield).
  std::snprintf(body, sizeof(body),
                "ScriptFunction.StartScriptControlledMovement(e, CVector3(%.3f, %.3f, %.3f), false)",
                static_cast<double>(xyz[0]), static_cast<double>(xyz[1]), static_cast<double>(xyz[2]));
  return NpcChunk(ctx, base, entity, 0u, body);
}

extern "C" int32_t refii_mod_npc_flee(PPCContext* ctx, uint8_t* base, uint32_t entity, uint32_t from) {
  if (from == 0u || entity == from) {
    return 0;
  }
  // ai/aibase.lua main.41, BehaviourBase:FleeFromEntityNoWait's call.
  return NpcChunk(ctx, base, entity, from,
                  "if Navigation.IsAvailable(e) then Navigation.FleeFromEntity(e, t, ENavigationSpeed.NAV_SPEED_SPRINT, true) end");
}

extern "C" int32_t refii_mod_npc_attack(PPCContext* ctx, uint8_t* base, uint32_t entity, uint32_t target) {
  if (target == 0u || entity == target) {
    return 0;
  }
  // ai/combatregister.lua main.19 (ignore the chase radius; drop the target once unattackable).
  return NpcChunk(ctx, base, entity, target, "CombatRegister:SetScriptTarget(e, t, true, false)");
}

extern "C" int32_t refii_mod_npc_set_hostile(PPCContext* ctx, uint8_t* base, uint32_t entity, uint32_t other, int32_t on) {
  if (other == 0u || entity == other) {
    return 0;
  }
  return NpcChunk(ctx, base, entity, other,
                  on != 0
                      ? "if Faction.IsAvailable(e) then Faction.AddTemporaryEntityRelationship(e, t, EFactionStatus.FACTION_STATUS_ENEMY) end"
                      : "if Faction.IsAvailable(e) then Faction.RemoveTemporaryEntityRelationship(e, t) end");
}

extern "C" int32_t refii_mod_npc_stop(PPCContext* ctx, uint8_t* base, uint32_t entity) {
  return NpcChunk(ctx, base, entity, 0u,
                  "if Follow.IsAvailable(e) then Follow.StopFollowing(e) end "
                  "ScriptFunction.StopScriptControlledMovement(e) CombatRegister:ClearScriptTarget(e)");
}

// ---------------------------------------------------------------------------------------------
// Time of day and weather (refii_mod.h quotes the setter and the blend).
// ---------------------------------------------------------------------------------------------

namespace {

constexpr uint32_t kSetTimeOfDayAbsolute = 0x82515AA0u;  // disc 0x8249A790

/// The time manager the Timing natives are bound to (0x82459000..14): the world cell's +0x14
/// (+0x10 is the message system refii_mod_add_deed posts through).
uint32_t TimeManager(uint8_t* base) {
  const uint32_t root = refii::mods::LoadU32(base, refii::guest::Address(kGameRoot));
  const uint32_t game = Plausible(root) ? refii::mods::LoadU32(base, root + 0xCu) : 0u;
  const uint32_t world = Plausible(game) ? refii::mods::LoadU32(base, game + 0x58u) : 0u;
  const uint32_t cell = Plausible(world) ? refii::mods::LoadU32(base, world + 4u) : 0u;
  const uint32_t manager = Plausible(cell) ? refii::mods::LoadU32(base, cell + 0x14u) : 0u;
  return Plausible(manager) ? manager : 0u;
}

/// An environment-theme record name: letters, digits and '_', 1..63 bytes.
bool ThemeName(const char* name) {
  if (name == nullptr || *name == 0) {
    return false;
  }
  size_t n = 0;
  for (const char* c = name; *c != 0; ++c, ++n) {
    const bool ok = (*c >= 'a' && *c <= 'z') || (*c >= 'A' && *c <= 'Z') || (*c >= '0' && *c <= '9') || *c == '_';
    if (!ok || n >= 63u) {
      return false;
    }
  }
  return true;
}

}  // namespace

extern "C" int32_t refii_mod_set_time_of_day(PPCContext* ctx, uint8_t* base, float hours) {
  const uint32_t manager = ctx != nullptr && std::isfinite(hours) ? TimeManager(base) : 0u;
  if (manager == 0u) {
    return 0;
  }
  float fraction = hours / 24.0f;
  fraction = fraction < 0.0f ? 0.0f : (fraction > 1.0f ? 1.0f : fraction);
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = manager;
  work.f1.f64 = static_cast<double>(fraction);
  return refii::mods::CallGuest(work, base, refii::guest::Address(kSetTimeOfDayAbsolute)) ? 1 : 0;
}

extern "C" int32_t refii_mod_time_of_day(uint8_t* base, float* hours) {
  const uint32_t manager = TimeManager(base);
  if (hours != nullptr) {
    *hours = manager != 0u ? refii::mods::LoadF32(base, manager + 4u) * 24.0f : 0.0f;
  }
  return manager != 0u ? 1 : 0;
}

extern "C" int32_t refii_mod_set_weather(PPCContext* ctx, uint8_t* base, const char* theme, float weight, float seconds) {
  if (ctx == nullptr || !ThemeName(theme) || !std::isfinite(weight) || !std::isfinite(seconds) || seconds < 0.0f) {
    return 0;
  }
  char chunk[192];
  // qc090_garth.lua: EnvironmentTheme.BlendToEnvironmentTheme("EnvThemeWestcliffNight", 1.0, 1.0).
  std::snprintf(chunk, sizeof(chunk), "EnvironmentTheme.BlendToEnvironmentTheme(\"%s\", %.4f, %.4f)", theme,
                static_cast<double>(weight < 0.0f ? 0.0f : (weight > 1.0f ? 1.0f : weight)), static_cast<double>(seconds));
  return refii_mod_run_game_lua(ctx, base, chunk);
}

extern "C" int32_t refii_mod_modify_health(PPCContext* ctx, uint8_t* base, uint32_t entity, float delta, uint32_t attacker) {
  const uint32_t health = ctx != nullptr ? refii_mod_component(base, entity, kHealthComponent) : 0u;
  if (health == 0 || std::isnan(delta)) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = health;
  work.f1.f64 = static_cast<double>(delta);
  work.r4.u64 = 0;
  work.r5.u32 = Plausible(attacker) ? attacker : 0u;
  rex_CECHealth_Modify(work, base);
  return 1;
}

extern "C" int32_t refii_mod_level_name(uint8_t* base, char* out, uint32_t size) {
  if (out != nullptr && size != 0) {
    out[0] = 0;
  }
  // The world manager is *(*(world+8)+4), as the game reaches it (0x824DA760, sub_82446928).
  const uint32_t world = refii_mod_world(base);
  const uint32_t holder8 = world != 0 ? refii::vfs::LoadGuestU32(base, world + 8u) : 0u;
  const uint32_t manager = Plausible(holder8) ? refii::vfs::LoadGuestU32(base, holder8 + 4u) : 0u;
  const uint32_t holder = Plausible(manager) ? refii::vfs::LoadGuestU32(base, manager + 0x24u) : 0u;
  const uint32_t level = Plausible(holder) ? refii::vfs::LoadGuestU32(base, holder + 4u) : 0u;
  if (!Plausible(level)) {
    return 0;
  }
  return CopyGuestString(base, LhStringText(base, level + 0xACu), out, size) ? 1 : 0;
}

extern "C" int32_t refii_mod_gold(uint8_t* base, uint32_t entity, int32_t* out) {
  const uint32_t purse = refii_mod_component(base, entity, kMoneyComponent);
  if (purse == 0) {
    return 0;
  }
  if (out != nullptr) {
    *out = static_cast<int32_t>(refii::mods::LoadU32(base, purse + 12u));
  }
  return 1;
}

extern "C" int32_t refii_mod_give_gold(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t delta) {
  const uint32_t purse = ctx != nullptr ? refii_mod_component(base, entity, kMoneyComponent) : 0u;
  if (purse == 0) {
    return 0;
  }
  // The game's own "give gold" thunk: Modify(purse, delta, 0, 1, 0).
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = purse;
  work.r4.s32 = delta;
  work.r5.u64 = 0;
  work.r6.u64 = 1;
  work.r7.u64 = 0;
  rex_CECMoney_Modify(work, base);
  return 1;
}

extern "C" int32_t refii_mod_item_count(uint8_t* base, uint32_t entity, uint32_t definition) {
  const uint32_t inventory = refii_mod_component(base, entity, kInventoryComponent);
  const uint32_t sentinel = inventory != 0 ? refii::mods::LoadU32(base, inventory + 16u) : 0u;
  if (!Plausible(sentinel)) {
    return 0;
  }
  int32_t count = 0;
  uint32_t node = refii::mods::LoadU32(base, sentinel);
  for (uint32_t walked = 0; Plausible(node) && node != sentinel && walked < 4096u; ++walked) {
    const uint32_t item = refii::mods::LoadU32(base, node + 8u);
    if (Plausible(item) && refii::mods::LoadU32(base, item + 4u) == definition) {
      count += static_cast<int32_t>(refii::mods::LoadU32(base, item + 12u));
    }
    node = refii::mods::LoadU32(base, node);
  }
  return count;
}

extern "C" uint32_t refii_mod_add_item(PPCContext* ctx, uint8_t* base, uint32_t entity, uint32_t definition) {
  const uint32_t inventory = ctx != nullptr ? refii_mod_component(base, entity, kInventoryComponent) : 0u;
  if (inventory == 0 || definition == 0) {
    return 0;
  }
  // The shared pointer the add fills: four words on our own frame below the caller's.
  PPCContext work = refii::mods::Frame(*ctx);
  const uint32_t out = work.r1.u32 + 0x40u;
  for (uint32_t i = 0; i < 4; ++i) {
    refii::mods::StoreU32(base, out + 4u * i, 0u);
  }
  work.r3.u32 = out;
  work.r4.u32 = inventory;
  work.r5.u32 = definition;
  work.r6.u64 = 1;
  rex_CECInventory_AddOneOfRecord(work, base);
  const uint32_t item = refii::mods::LoadU32(base, out);
  if (!Plausible(item)) {
    return 0u;
  }
  // The add registered the out slot as a reference holder on the item (+40); every game caller
  // releases it before its frame goes (0x823B815C), or the item keeps a pointer into a dead frame.
  PPCContext release = refii::mods::Frame(*ctx);
  release.r3.u32 = item;
  release.r4.u32 = out;
  rex_lhTrackedObject_RemoveHandleRef(release, base);
  return item;
}

namespace {
/// The most items one refii_mod_add_items adds: each is the game's whole add, events included.
constexpr int32_t kAddItemsMax = 100;
}  // namespace

extern "C" int32_t refii_mod_add_items(PPCContext* ctx, uint8_t* base, uint32_t entity, uint32_t definition, int32_t count,
                                       uint32_t* out_last) {
  if (out_last != nullptr) {
    *out_last = 0u;
  }
  const int32_t wanted = count < 1 ? 0 : (count > kAddItemsMax ? kAddItemsMax : count);
  int32_t added = 0;
  for (int32_t i = 0; i < wanted; ++i) {
    const uint32_t item = refii_mod_add_item(ctx, base, entity, definition);
    if (item == 0u) {
      break;
    }
    ++added;
    if (out_last != nullptr) {
      *out_last = item;
    }
  }
  return added;
}

// ---------------------------------------------------------------------------------------------
// Hero stats, physique, inventory, weapons, space, spawning (v2 helpers)
// ---------------------------------------------------------------------------------------------

namespace {

constexpr uint32_t kAgeComponent = 122;
constexpr uint32_t kWeaponSetComponent = 28;
constexpr uint32_t kCreatureComponent = 20;
constexpr uint32_t kStatsFat = 56;
constexpr uint32_t kStatsHeight = 60;
constexpr uint32_t kAgeYears = 12;
constexpr uint32_t kInventoryList = 16;
constexpr uint32_t kWeaponMelee = 84;
constexpr uint32_t kWeaponRanged = 88;
/// The world's entity manager (TU1 0x83496B28): +8 the intrusive list's head (each node is
/// entity+8, node +0 the next node), the link rex_lhEntityManager_Add makes.
constexpr uint32_t kEntityManager = 0x83496B28u;
constexpr uint32_t kManagerList = 8;
constexpr uint32_t kEntityNode = 8;
constexpr uint32_t kEntityFlags = 0x90;
constexpr uint8_t kInWorld = 0x40;
/// Presence of component 2 (the placement the sphere test reads: entity+0x24 bit 2), as
/// sub_822000E8 checks before it asks for a position.
constexpr uint32_t kPresenceWord = 0x24;
constexpr uint32_t kHasPlacementBit = 1u << 2;
constexpr int32_t kNearScanLimit = 20000;

uint32_t StatsOf(uint8_t* base, uint32_t entity) {
  return Plausible(entity) ? refii_mod_component(base, entity, kStatsComponent) : 0u;
}

}  // namespace

extern "C" int32_t refii_mod_renown(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t* out) {
  const uint32_t stats = ctx != nullptr ? StatsOf(base, entity) : 0u;
  if (stats == 0) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = stats;
  rex_lhHeroStats_GetRenown(work, base);
  if (out != nullptr) {
    *out = static_cast<int32_t>(work.r3.u32);
  }
  return 1;
}

extern "C" int32_t refii_mod_morality(PPCContext* ctx, uint8_t* base, uint32_t entity, float* out) {
  const uint32_t stats = ctx != nullptr ? StatsOf(base, entity) : 0u;
  if (stats == 0) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = stats;
  rex_lhHeroStats_GetMorality(work, base);
  if (out != nullptr) {
    *out = static_cast<float>(work.f1.f64);
  }
  return 1;
}

extern "C" int32_t refii_mod_purity(PPCContext* ctx, uint8_t* base, uint32_t entity, float* out) {
  const uint32_t stats = ctx != nullptr ? StatsOf(base, entity) : 0u;
  if (stats == 0) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = stats;
  rex_lhHeroStats_GetPurity(work, base);
  if (out != nullptr) {
    *out = static_cast<float>(work.f1.f64);
  }
  return 1;
}

extern "C" int32_t refii_mod_modify_renown(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t delta) {
  const uint32_t stats = ctx != nullptr ? StatsOf(base, entity) : 0u;
  if (stats == 0) {
    return 0;
  }
  // Stats.ModifyRenown: `li r5,0; b sub_824D62A0` (0x824d6bd8).
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = stats;
  work.r4.u64 = static_cast<uint32_t>(delta);
  work.r5.u64 = 0;
  rex_lhHeroStats_ModifyRenown(work, base);
  return 1;
}

extern "C" int32_t refii_mod_modify_morality(PPCContext* ctx, uint8_t* base, uint32_t entity, float delta) {
  const uint32_t stats = ctx != nullptr && std::isfinite(delta) ? StatsOf(base, entity) : 0u;
  if (stats == 0) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = stats;
  work.f1.f64 = static_cast<double>(delta);
  rex_lhHeroStats_ModifyMorality(work, base);
  return 1;
}

extern "C" int32_t refii_mod_modify_purity(PPCContext* ctx, uint8_t* base, uint32_t entity, float delta) {
  const uint32_t stats = ctx != nullptr && std::isfinite(delta) ? StatsOf(base, entity) : 0u;
  if (stats == 0) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = stats;
  work.f1.f64 = static_cast<double>(delta);
  rex_lhHeroStats_ModifyPurity(work, base);
  return 1;
}

extern "C" int32_t refii_mod_physique(uint8_t* base, uint32_t entity, float* out_fat, float* out_height) {
  const uint32_t stats = StatsOf(base, entity);
  if (stats == 0) {
    return 0;
  }
  if (out_fat != nullptr) {
    *out_fat = refii::mods::LoadF32(base, stats + kStatsFat);
  }
  if (out_height != nullptr) {
    *out_height = refii::mods::LoadF32(base, stats + kStatsHeight);
  }
  return 1;
}

extern "C" int32_t refii_mod_age(uint8_t* base, uint32_t entity, float* out) {
  const uint32_t age = Plausible(entity) ? refii_mod_component(base, entity, kAgeComponent) : 0u;
  if (age == 0) {
    return 0;
  }
  if (out != nullptr) {
    *out = refii::mods::LoadF32(base, age + kAgeYears);
  }
  return 1;
}

// --- Murgo's Mirror: a player's look (refii_mod_appearance) -------------------------------------

namespace {

using LookField = float refii_mod_appearance_t::*;
/// refii_mod_appearance_t's inputs, with their player-store keys (Engine_CharacterMorphs.StoreKey)
/// and the Mirror's ranges (Engine_CharacterMorphs.Sliders).
constexpr LookField kLookFields[] = {
    &refii_mod_appearance_t::tan,    &refii_mod_appearance_t::fat,      &refii_mod_appearance_t::height,
    &refii_mod_appearance_t::strength, &refii_mod_appearance_t::skill,  &refii_mod_appearance_t::will,
    &refii_mod_appearance_t::morality, &refii_mod_appearance_t::purity, &refii_mod_appearance_t::age,
};
constexpr const char* kLookKeys[] = {
    "character_morphs.tan",   "character_morphs.fat",      "character_morphs.height",
    "character_morphs.strength", "character_morphs.skill", "character_morphs.will",
    "character_morphs.morality", "character_morphs.purity", "character_morphs.age",
};
constexpr const char* kLookAutomatic = "character_morphs.automatic";
constexpr float kLookMin[] = {0, 0, -1, 0, 0, 0, 0, 0, 18};
constexpr float kLookMax[] = {1, 1, 1, 1, 1, 1, 1, 1, 60};
/// HeroMorphStats' neutral look, for an input neither stated nor readable.
constexpr refii_mod_appearance_t kNeutralLook = {1, 0.5f, 0, 0, 0, 0, 0, 0.5f, 0.5f, 30};
/// The game's normalised purity (TU1 sub_824D6F90 / disc sub_8249E100, r3 the stats): (purity -
/// +0x28) / (+0x2C - +0x28), 1 for a range at or under 1e-4 — what sub_8278F848 (0x8278FBA0) and
/// the weight function sub_822DA028 read.
constexpr uint32_t kNormalisedPurity = 0x824D6F90u;
constexpr uint32_t kStatsTan = 0x34;
constexpr uint32_t kStatsStrength = 0x40;
constexpr uint32_t kStatsSkill = 0x44;
constexpr uint32_t kStatsWill = 0x48;
constexpr uint32_t kStatsMoralityMin = 0x14;
constexpr uint32_t kStatsMoralityMax = 0x18;

/// The game's own look of `hero`: the stats fields the looks readers load, morality normalised as
/// sub_822DA028's cases 9..16 do inline (`(GetMorality - v8[5]) / (v8[6] - v8[5])`, v8 the stats),
/// purity by the game's normaliser, CECAge's years. False without stats.
bool GameLook(PPCContext* ctx, uint8_t* base, uint32_t hero, refii_mod_appearance_t& out) {
  const uint32_t stats = ctx != nullptr ? StatsOf(base, hero) : 0u;
  if (stats == 0u) {
    return false;
  }
  out = kNeutralLook;
  out.tan = refii::mods::LoadF32(base, stats + kStatsTan);
  out.fat = refii::mods::LoadF32(base, stats + kStatsFat);
  out.height = refii::mods::LoadF32(base, stats + kStatsHeight);
  out.strength = refii::mods::LoadF32(base, stats + kStatsStrength);
  out.skill = refii::mods::LoadF32(base, stats + kStatsSkill);
  out.will = refii::mods::LoadF32(base, stats + kStatsWill);
  float morality = 0.0f;
  const float low = refii::mods::LoadF32(base, stats + kStatsMoralityMin);
  const float high = refii::mods::LoadF32(base, stats + kStatsMoralityMax);
  if (refii_mod_morality(ctx, base, hero, &morality) != 0 && high > low) {
    out.morality = (morality - low) / (high - low);
  }
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u64 = stats;
  if (refii::mods::CallGuest(work, base, refii::guest::Address(kNormalisedPurity))) {
    out.purity = static_cast<float>(work.f1.f64);
  }
  refii_mod_age(base, hero, &out.age);
  return true;
}

/// The inputs the store holds for `player` into `out`, each one present; true when all are.
bool StoredInputs(int32_t player, refii_mod_appearance_t& out) {
  bool all = true;
  for (size_t i = 0; i < std::size(kLookFields); ++i) {
    float value = 0.0f;
    if (refii_mod_player_get(player, kLookKeys[i], &value) != 0 && std::isfinite(value)) {
      out.*kLookFields[i] = value;
    } else {
      all = false;
    }
  }
  return all;
}

/// Writes a whole look into the player's store: the inputs clamped to the Mirror's ranges, then the
/// mode. Saved values: the look is the hero's, written with its save and read back by its load, so a
/// save made without one loads in the game's own look. A write where the hero is not played reaches
/// the machine that plays it (player_state.cpp).
void WriteLook(int32_t player, bool automatic, const refii_mod_appearance_t& look) {
  for (size_t i = 0; i < std::size(kLookFields); ++i) {
    const float value = look.*kLookFields[i];
    refii_mod_player_save(player, kLookKeys[i],
                          std::clamp(std::isfinite(value) ? value : kNeutralLook.*kLookFields[i], kLookMin[i], kLookMax[i]));
  }
  refii_mod_player_save(player, kLookAutomatic, automatic ? 1.0f : 0.0f);
}

}  // namespace

extern "C" int32_t refii_mod_appearance(PPCContext* ctx, uint8_t* base, int32_t player, refii_mod_appearance_t* out) {
  if (player != 1 && player != 2) {
    return 0;
  }
  refii_mod_appearance_t look = kNeutralLook;
  float automatic = 1.0f;
  const bool manual = refii_mod_player_get(player, kLookAutomatic, &automatic) != 0 && automatic == 0.0f &&
                      StoredInputs(player, look);
  if (manual) {
    look.automatic = 0;
  } else if (!GameLook(ctx, base, refii_mod_player_hero(base, player), look)) {
    return 0;
  }
  if (out != nullptr) {
    *out = look;
  }
  return 1;
}

extern "C" void refii_mod_set_appearance(PPCContext* ctx, uint8_t* base, int32_t player, const refii_mod_appearance_t* inputs) {
  if ((player != 1 && player != 2) || inputs == nullptr) {
    return;
  }
  refii_mod_appearance_t look = kNeutralLook;
  refii_mod_appearance(ctx, base, player, &look);
  for (const LookField field : kLookFields) {
    if (!std::isnan(inputs->*field)) {
      look.*field = inputs->*field;
    }
  }
  WriteLook(player, false, look);
}

extern "C" void refii_mod_set_appearance_automatic(PPCContext* ctx, uint8_t* base, int32_t player, int32_t on) {
  if (player != 1 && player != 2) {
    return;
  }
  refii_mod_appearance_t look = kNeutralLook;
  refii_mod_appearance_t probe = kNeutralLook;
  if (!StoredInputs(player, probe)) {
    GameLook(ctx, base, refii_mod_player_hero(base, player), look);
  }
  StoredInputs(player, look);
  WriteLook(player, on != 0, look);
}

namespace refii::vfs {

/// GameCharacterMorphs' statement of a settings slot's look for `player` (the `appearance_state`
/// command): written only where that player's hero is played, so a slot whose player moved to the
/// other machine (a join) never writes into the store the other machine owns.
void AppearanceState(int32_t player, bool automatic, const refii_mod_appearance_t& look) {
  if (refii_mod_player_here(player) == 0) {
    return;
  }
  WriteLook(player, automatic, look);
}

}  // namespace refii::vfs


namespace {
/// The inventory item's own "usable from the inventory" test (TU1 0x827649C8 / disc 0x827417E0):
/// r3 the item; the definition's InventoryItemComponent (`sub_822A99E8`) and its
/// "UsableFromInventory" bool (`sub_82310290`). The Items page's use and its A button ask it
/// (0x826d19b8, 0x829b5084).
constexpr uint32_t kItemUsableFromInventory = 0x827649C8u;
}  // namespace

extern "C" int32_t refii_mod_inventory_usable(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t index) {
  const uint32_t inventory = ctx != nullptr && Plausible(entity) ? refii_mod_component(base, entity, kInventoryComponent) : 0u;
  const uint32_t head = inventory != 0 ? refii::vfs::LoadGuestU32(base, inventory + kInventoryList) : 0u;
  if (!Plausible(head) || index < 0) {
    return 0;
  }
  uint32_t node = refii::vfs::LoadGuestU32(base, head);
  for (int32_t i = 0; Plausible(node) && node != head && i < 4096; ++i, node = refii::vfs::LoadGuestU32(base, node)) {
    if (i != index) {
      continue;
    }
    const uint32_t item = refii::vfs::LoadGuestU32(base, node + 8u);
    if (!Plausible(item)) {
      return 0;
    }
    PPCContext work = refii::mods::Frame(*ctx);
    work.r3.u32 = item;
    return refii::mods::CallGuest(work, base, refii::guest::Address(kItemUsableFromInventory)) && (work.r3.u32 & 0xFFu) != 0 ? 1 : 0;
  }
  return 0;
}

extern "C" int32_t refii_mod_inventory_stack(uint8_t* base, uint32_t entity, int32_t index, uint32_t* out_definition,
                                             int32_t* out_count) {
  const uint32_t inventory = Plausible(entity) ? refii_mod_component(base, entity, kInventoryComponent) : 0u;
  if (inventory == 0 || index < 0) {
    return 0;
  }
  const uint32_t head = refii::vfs::LoadGuestU32(base, inventory + kInventoryList);
  if (!Plausible(head)) {
    return 0;
  }
  uint32_t node = refii::vfs::LoadGuestU32(base, head);
  for (int32_t i = 0; Plausible(node) && node != head && i < 4096; ++i, node = refii::vfs::LoadGuestU32(base, node)) {
    if (i != index) {
      continue;
    }
    const uint32_t item = refii::vfs::LoadGuestU32(base, node + 8u);
    if (!Plausible(item)) {
      return 0;
    }
    if (out_definition != nullptr) {
      *out_definition = refii::vfs::LoadGuestU32(base, item + 4u);
    }
    if (out_count != nullptr) {
      *out_count = static_cast<int32_t>(refii::vfs::LoadGuestU32(base, item + 12u));
    }
    return 1;
  }
  return 0;
}

extern "C" int32_t refii_mod_equipped(uint8_t* base, uint32_t entity, uint32_t* out_melee, uint32_t* out_ranged) {
  const uint32_t set = Plausible(entity) ? refii_mod_component(base, entity, kWeaponSetComponent) : 0u;
  if (set == 0) {
    return 0;
  }
  if (out_melee != nullptr) {
    *out_melee = refii::vfs::LoadGuestU32(base, set + kWeaponMelee);
  }
  if (out_ranged != nullptr) {
    *out_ranged = refii::vfs::LoadGuestU32(base, set + kWeaponRanged);
  }
  return 1;
}

// ---------------------------------------------------------------------------------------------
// The quick menu's actions, the performer's expressions, the spell tables, a record's strings
// ---------------------------------------------------------------------------------------------

namespace {

constexpr uint32_t kPerformerComponent = 154;
constexpr uint32_t kPerformerKnownBegin = 0x88;
constexpr uint32_t kPerformerKnownEnd = 0x8C;
constexpr uint32_t kPerformerEntryBytes = 20;
constexpr uint32_t kPerformerEntryUses = 12;
constexpr uint32_t kPerformerKnownMax = 512;
constexpr uint32_t kSpellTiers = 5;
constexpr uint32_t kSpellTypes = 8;
constexpr uint32_t kSpellKnown = 156;    // manager + 156 + 5 * type + tier: the hero knows it
constexpr uint32_t kSpellOffered = 196;  // manager + 196 + 5 * type + tier: the tier may take it
constexpr uint32_t kGuiObject = 0x83496B08u;           // disc 0x834968A4
constexpr uint32_t kGdbManager = 0x83496DA0u;          // disc 0x83496BD0
constexpr uint32_t kRecordStringGetter = 0x82323540u;  // disc 0x82345978: (entry, "Name") -> char*
// GDBEntry_FindPropWalkParents(entry, hash, int* type) -> value*: the property, inherited, and the
// type the database stores it as. GDBEntry_GetPropTyped (TU1 0x82A006C8) makes this walk and then
// its conversion (sub_8229CE90), which hands back the same value word whatever type is asked for
// (`*a4 = a3` before any test; the result is dropped), so the stored type is what decodes the word.
constexpr uint32_t kRecordPropertyWalk = 0x821E5A08u;  // disc 0x821E55C8
constexpr uint32_t kGdbTypeInt = 1;                     // GDB property types, as the database states them
constexpr uint32_t kGdbTypeEnum = 5;
constexpr uint32_t kGdbTypeBool = 0;                    // word != 0: lwz, cntlzw, extrwi 1,26, xori 1 (0x825A1F20)
constexpr uint32_t kGdbTypeFloat = 3;                   // lfs 0(r3) after every type-3 call (79 of 80 sites)
// The expression wheel (the HUD's own reader): the
// controller at *(*(GUI + 32) + 60); 15 words read as one.
constexpr uint32_t kWheelOpen = 0;
constexpr uint32_t kWheelCursor = 5;
constexpr uint32_t kWheelPlayer = 6;
constexpr uint32_t kWheelBegin = 13;
constexpr uint32_t kWheelEnd = 14;
constexpr uint32_t kWheelEntryBytes = 12;
constexpr uint32_t kWheelEntriesMax = 64;

int32_t Quickslot(PPCContext* ctx, uint8_t* base, int32_t player, uint32_t kind, uint32_t record, uint32_t flags = 0u) {
  if (ctx == nullptr || player < 1 || player > 2) {
    return 0;
  }
  PPCContext work = *ctx;
  return refii::vfs::PerformQuickslot(work, base, static_cast<uint32_t>(player), kind, record, flags) ? 1 : 0;
}

/// Player `player`'s performer (component 154), or 0.
uint32_t PlayerPerformer(uint8_t* base, int32_t player) {
  const uint32_t hero = player >= 1 && player <= 2 ? refii_mod_player_hero(base, player) : 0u;
  return Plausible(hero) ? refii_mod_component(base, hero, kPerformerComponent) : 0u;
}

/// The performer's known-expression vector, validated; false when it is not one.
bool KnownExpressions(uint8_t* base, uint32_t performer, uint32_t& begin, uint32_t& end) {
  begin = refii::mods::LoadU32(base, performer + kPerformerKnownBegin);
  end = refii::mods::LoadU32(base, performer + kPerformerKnownEnd);
  return Plausible(begin) && end >= begin && end - begin <= kPerformerEntryBytes * kPerformerKnownMax &&
         (end - begin) % kPerformerEntryBytes == 0;
}

}  // namespace

extern "C" int32_t refii_mod_use_item(PPCContext* ctx, uint8_t* base, int32_t player, uint32_t definition) {
  return Quickslot(ctx, base, player, 1u, definition);
}

extern "C" int32_t refii_mod_perform_expression(PPCContext* ctx, uint8_t* base, int32_t player, uint32_t expression) {
  return Quickslot(ctx, base, player, 2u, expression);
}

extern "C" int32_t refii_mod_equip_weapon(PPCContext* ctx, uint8_t* base, int32_t player, uint32_t definition) {
  return Quickslot(ctx, base, player, 3u, definition);
}

extern "C" int32_t refii_mod_assign_spell(PPCContext* ctx, uint8_t* base, int32_t player, int32_t spell) {
  if (spell < 0 || spell >= static_cast<int32_t>(kSpellTypes)) {
    return 0;
  }
  return Quickslot(ctx, base, player, 4u, static_cast<uint32_t>(spell));
}

extern "C" int32_t refii_mod_quick_action(PPCContext* ctx, uint8_t* base, int32_t player, int32_t kind, uint32_t record,
                                          uint32_t flags) {
  if (kind < 1 || kind > 4 || (kind == 4 && record >= kSpellTypes)) {
    return 0;
  }
  return Quickslot(ctx, base, player, static_cast<uint32_t>(kind), record, flags & (REFII_QUICK_UNGATED | REFII_QUICK_HELD));
}

namespace {

/// The performer's own end of a held expression (TU1 0x82414A50 / disc 0x823E6E88): r3 the
/// performer. GameAction 25's case calls it (`bl sub_82414A50` 0x826e63ec).
constexpr uint32_t kPerformerEndHeld = 0x82414A50u;
/// The performer's playing expression record (+104): StartExpression stores it (`stw r?,0x68`),
/// the end clears it.
constexpr uint32_t kPerformerPlaying = 104;
/// The sprint component and its flag (`cmpwi r7,0x92` 0x826e61a0; `lbz r11,0x24(r31)` 0x826e6248).
constexpr uint32_t kSprintComponent = 146;
constexpr uint32_t kSprintFlag = 0x24;
/// The sprint setter (TU1 0x8284A8C0 / disc 0x827DA750): r3 the component, r4 the new flag.
constexpr uint32_t kSetSprinting = 0x8284A8C0u;

}  // namespace

extern "C" int32_t refii_mod_release_expression(PPCContext* ctx, uint8_t* base, int32_t player) {
  const uint32_t performer = ctx != nullptr ? PlayerPerformer(base, player) : 0u;
  if (performer == 0 || refii::mods::LoadU32(base, performer + kPerformerPlaying) == 0) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = performer;
  return refii::mods::CallGuest(work, base, refii::guest::Address(kPerformerEndHeld)) ? 1 : 0;
}

namespace {
uint32_t PlayerSprint(uint8_t* base, int32_t player) {
  const uint32_t hero = player >= 1 && player <= 2 ? refii_mod_player_hero(base, player) : 0u;
  return Plausible(hero) ? refii_mod_component(base, hero, kSprintComponent) : 0u;
}
}  // namespace

extern "C" int32_t refii_mod_sprinting(uint8_t* base, int32_t player) {
  const uint32_t sprint = PlayerSprint(base, player);
  return sprint != 0 && refii::mods::LoadU8(base, sprint + kSprintFlag) != 0 ? 1 : 0;
}

extern "C" int32_t refii_mod_end_sprint(PPCContext* ctx, uint8_t* base, int32_t player) {
  const uint32_t sprint = ctx != nullptr ? PlayerSprint(base, player) : 0u;
  if (sprint == 0 || refii::mods::LoadU8(base, sprint + kSprintFlag) == 0) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = sprint;
  work.r4.u64 = 0;
  return refii::mods::CallGuest(work, base, refii::guest::Address(kSetSprinting)) ? 1 : 0;
}

// ---------------------------------------------------------------------------------------------
// Per-hero locomotion and parry (core/refii_mod.h states the copies and their readers)
// ---------------------------------------------------------------------------------------------

namespace {

constexpr uint32_t kLocomotionComponent = 110;  // `cmpwi r11,0x6E` 0x823ef054, the loader's caller
constexpr uint32_t kLocomotionTable = 0x64;     // the map the loader fills (`addi r30,r31,0x64` 0x821b231c)
/// The map's operator[] (TU1 0x823C5E00 / disc 0x824191A0): r3 the map, r4 -> the key; r3 -> the float.
constexpr uint32_t kFloatMapAt = 0x823C5E00u;
/// The player component's property table (+340: `sub_823ACBC0(a1 + 340, table)` in sub_823E9BF0).
constexpr uint32_t kCombatParamsTable = 340;
/// The table's find (TU1 0x8257EC08 / disc 0x82C343A0): r3 the out {map, node}, r4 the table, r5 ->
/// the key; node == *(table + 4) is the end. node +16 the value: +4 its type (1 a float), +8 the float.
constexpr uint32_t kParamsFind = 0x8257EC08u;

/// The game's property-name hash, `sub_823B0A38`: CRC-32 over the name's bytes through the table at
/// 0x82098C40 (the standard reflected table: 0, 0x77073096, 0xEE0E612C, ..), from 0, no final xor.
uint32_t GameNameHash(const char* name) {
  uint32_t crc = 0;
  for (const char* c = name; *c != 0; ++c) {
    crc ^= static_cast<uint8_t>(*c);
    for (int bit = 0; bit < 8; ++bit) {
      crc = (crc >> 1) ^ ((crc & 1u) != 0 ? 0xEDB88320u : 0u);
    }
  }
  return crc;
}

/// The ten fields the loader copies (`sub_8283E408`), in its order.
constexpr const char* kLocomotionKeys[] = {
    "MinControlMagForMovement", "MinVelMagForMovement", "MaxTurnSpeedStatic", "MaxTurnSpeedRunning",
    "MaxSpeed", "SprintSpeedMultiplier", "SprintTerminationSpeedFraction", "FrictionCoefficient",
    "MaxSpeedLateralStatic", "MaxSpeedLateralRunning"};

bool LocomotionKey(const char* key) {
  if (key == nullptr) {
    return false;
  }
  for (const char* known : kLocomotionKeys) {
    if (std::strcmp(known, key) == 0) {
      return true;
    }
  }
  return false;
}

/// Per player: the values held (hash -> value) and what the last load copied for each held hash.
std::unordered_map<uint32_t, float> g_held_locomotion[2];
std::unordered_map<uint32_t, float> g_loaded_locomotion[2];
/// Per player: the parry window and wedge held (NaN: not held).
float g_held_parry[2][2] = {{NAN, NAN}, {NAN, NAN}};

/// The float slot for `hash` in the component's table, through the game's own operator[]; 0 when
/// the call fails.
uint32_t LocomotionSlot(const PPCContext& ctx, uint8_t* base, uint32_t component, uint32_t hash) {
  const uint32_t frame = (ctx.r1.u32 - 0x100u) & ~0xFu;
  const uint32_t key = frame + 0x60u;
  refii::mods::StoreU32(base, key, hash);
  PPCContext work = ctx;
  work.r1.u32 = frame;
  work.r3.u32 = component + kLocomotionTable;
  work.r4.u32 = key;
  return refii::mods::CallGuest(work, base, refii::guest::Address(kFloatMapAt)) && Plausible(work.r3.u32) ? work.r3.u32 : 0u;
}

uint32_t PlayerLocomotion(uint8_t* base, int32_t player) {
  const uint32_t hero = player >= 1 && player <= 2 ? refii_mod_player_hero(base, player) : 0u;
  return Plausible(hero) ? refii_mod_component(base, hero, kLocomotionComponent) : 0u;
}

/// Writes `value` into the slot, first remembering what the last load copied there.
bool WriteHeldLocomotion(const PPCContext& ctx, uint8_t* base, int32_t player, uint32_t component, uint32_t hash, float value) {
  const uint32_t slot = LocomotionSlot(ctx, base, component, hash);
  if (slot == 0) {
    return false;
  }
  g_loaded_locomotion[player - 1].emplace(hash, refii::mods::LoadF32(base, slot));
  refii::mods::StoreF32(base, slot, value);
  return true;
}

/// The value object for `name` in the hero's combat parameter table, or 0.
uint32_t CombatParam(const PPCContext& ctx, uint8_t* base, int32_t player, const char* name) {
  const uint32_t hero = player >= 1 && player <= 2 ? refii_mod_player_hero(base, player) : 0u;
  const uint32_t component = Plausible(hero) ? refii_mod_component(base, hero, kPlayerComponent) : 0u;
  const uint32_t table = component != 0 ? refii::mods::LoadU32(base, component + kCombatParamsTable) : 0u;
  if (!Plausible(table)) {
    return 0u;
  }
  const uint32_t frame = (ctx.r1.u32 - 0x100u) & ~0xFu;
  const uint32_t out = frame + 0x60u;
  const uint32_t key = frame + 0x70u;
  refii::mods::StoreU32(base, out, 0u);
  refii::mods::StoreU32(base, out + 4u, 0u);
  refii::mods::StoreU32(base, key, GameNameHash(name));
  PPCContext work = ctx;
  work.r1.u32 = frame;
  work.r3.u32 = out;
  work.r4.u32 = table;
  work.r5.u32 = key;
  if (!refii::mods::CallGuest(work, base, refii::guest::Address(kParamsFind))) {
    return 0u;
  }
  const uint32_t node = refii::mods::LoadU32(base, out + 4u);
  if (!Plausible(node) || node == refii::mods::LoadU32(base, table + 4u)) {
    return 0u;
  }
  const uint32_t value = refii::mods::LoadU32(base, node + 16u);
  return Plausible(value) && refii::mods::LoadU32(base, value + 4u) == 1u ? value : 0u;
}

bool WriteParry(const PPCContext& ctx, uint8_t* base, int32_t player, float window, float wedge) {
  bool wrote = false;
  const char* names[2] = {"InputWindowSecondsForParry", "MeleeParryWedgeAngle"};
  const float values[2] = {window, wedge};
  for (int i = 0; i < 2; ++i) {
    if (!std::isfinite(values[i]) || values[i] < 0.0f) {
      continue;
    }
    const uint32_t value = CombatParam(ctx, base, player, names[i]);
    if (value != 0) {
      refii::mods::StoreF32(base, value + 8u, values[i]);
      wrote = true;
    }
  }
  return wrote;
}

}  // namespace

extern "C" int32_t refii_mod_set_locomotion(PPCContext* ctx, uint8_t* base, int32_t player, const char* key, float value) {
  const uint32_t component = ctx != nullptr && LocomotionKey(key) ? PlayerLocomotion(base, player) : 0u;
  if (component == 0) {
    return 0;
  }
  const uint32_t hash = GameNameHash(key);
  auto& held = g_held_locomotion[player - 1];
  auto& loaded = g_loaded_locomotion[player - 1];
  if (!std::isfinite(value)) {
    held.erase(hash);
    const auto shipped = loaded.find(hash);
    if (shipped == loaded.end()) {
      return 1;
    }
    const uint32_t slot = LocomotionSlot(*ctx, base, component, hash);
    if (slot != 0) {
      refii::mods::StoreF32(base, slot, shipped->second);
    }
    loaded.erase(shipped);
    return slot != 0 ? 1 : 0;
  }
  if (!WriteHeldLocomotion(*ctx, base, player, component, hash, value)) {
    return 0;
  }
  held[hash] = value;
  return 1;
}

extern "C" int32_t refii_mod_locomotion(PPCContext* ctx, uint8_t* base, int32_t player, const char* key, float* out) {
  const uint32_t component = ctx != nullptr && out != nullptr && LocomotionKey(key) ? PlayerLocomotion(base, player) : 0u;
  const uint32_t slot = component != 0 ? LocomotionSlot(*ctx, base, component, GameNameHash(key)) : 0u;
  if (slot == 0) {
    return 0;
  }
  *out = refii::mods::LoadF32(base, slot);
  return 1;
}

extern "C" int32_t refii_mod_set_parry(PPCContext* ctx, uint8_t* base, int32_t player, float window_seconds,
                                       float wedge_degrees) {
  if (ctx == nullptr || player < 1 || player > 2) {
    return 0;
  }
  if (std::isfinite(window_seconds) && window_seconds >= 0.0f) {
    g_held_parry[player - 1][0] = window_seconds;
  }
  if (std::isfinite(wedge_degrees) && wedge_degrees >= 0.0f) {
    g_held_parry[player - 1][1] = wedge_degrees;
  }
  return WriteParry(*ctx, base, player, window_seconds, wedge_degrees) ? 1 : 0;
}

extern "C" int32_t refii_mod_parry(PPCContext* ctx, uint8_t* base, int32_t player, float* out_window, float* out_wedge) {
  if (ctx == nullptr) {
    return 0;
  }
  const uint32_t window = CombatParam(*ctx, base, player, "InputWindowSecondsForParry");
  const uint32_t wedge = CombatParam(*ctx, base, player, "MeleeParryWedgeAngle");
  if (window == 0 || wedge == 0) {
    return 0;
  }
  if (out_window != nullptr) {
    *out_window = refii::mods::LoadF32(base, window + 8u);
  }
  if (out_wedge != nullptr) {
    *out_wedge = refii::mods::LoadF32(base, wedge + 8u);
  }
  return 1;
}

namespace refii::vfs {

/// After the loader copied a LocomotionData table into `component` (mod_events.cpp's hook on
/// rex_CECLocomotion_LoadData): what it copied is the new "loaded" value, and the held values go
/// back on top.
void ReapplyHeldLocomotion(PPCContext& ctx, uint8_t* base, uint32_t component) {
  const int32_t player = Plausible(component) ? refii_mod_player_of(base, refii::mods::LoadU32(base, component + 4u)) : 0;
  if (player < 1 || player > 2) {
    return;
  }
  g_loaded_locomotion[player - 1].clear();
  for (const auto& [hash, value] : g_held_locomotion[player - 1]) {
    WriteHeldLocomotion(ctx, base, player, component, hash, value);
  }
}

/// A player's new hero (mod_events.cpp's hero edge): its player component made a fresh copy of
/// HeroCombatBalanceParams, so the held parry goes back on it.
void ReapplyHeldParry(PPCContext& ctx, uint8_t* base, int32_t player) {
  if (player >= 1 && player <= 2) {
    WriteParry(ctx, base, player, g_held_parry[player - 1][0], g_held_parry[player - 1][1]);
  }
}

}  // namespace refii::vfs

extern "C" int32_t refii_mod_expression(uint8_t* base, int32_t player, int32_t index, uint32_t* out_record,
                                        int32_t* out_uses) {
  const uint32_t performer = PlayerPerformer(base, player);
  uint32_t begin = 0, end = 0;
  if (performer == 0 || index < 0 || !KnownExpressions(base, performer, begin, end)) {
    return 0;
  }
  const uint32_t at = begin + kPerformerEntryBytes * static_cast<uint32_t>(index);
  if (at + kPerformerEntryBytes > end) {
    return 0;
  }
  if (out_record != nullptr) {
    *out_record = refii::mods::LoadU32(base, at);
  }
  if (out_uses != nullptr) {
    *out_uses = static_cast<int32_t>(refii::mods::LoadU32(base, at + kPerformerEntryUses));
  }
  return 1;
}

extern "C" int32_t refii_mod_spells_known(uint8_t* base, int32_t player, uint32_t* out_mask) {
  const uint32_t hero = player >= 1 && player <= 2 ? refii_mod_player_hero(base, player) : 0u;
  const uint32_t manager = Plausible(hero) ? refii_mod_component(base, hero, kSpellManagerComponent) : 0u;
  if (manager == 0) {
    return 0;
  }
  uint32_t mask = 0;
  for (uint32_t type = 0; type < kSpellTypes; ++type) {
    for (uint32_t tier = 0; tier < kSpellTiers; ++tier) {
      const uint32_t index = kSpellTiers * type + tier;
      if (refii::mods::LoadU8(base, manager + kSpellKnown + index) != 0 &&
          refii::mods::LoadU8(base, manager + kSpellOffered + index) != 0) {
        mask |= 1u << type;
        break;
      }
    }
  }
  if (out_mask != nullptr) {
    *out_mask = mask;
  }
  return 1;
}

namespace {

/// THE LENT CHARGE (refii_mod_charge_spell): one per player — the spell lent, the player's own slots
/// it borrowed (the slot table's row 1, `+0x85 + slot`, what the slot writer's hook reads as the
/// pick before it), whether the button has been let go and how long ago, and the restore once due.
struct ChargeLend {
  bool active = false;
  int32_t type = -1;
  uint32_t hero = 0;
  int8_t saved[kSpellSlots] = {};
  bool released = false;
  float since_release = 0.0f;
  float age = 0.0f;
  bool restore = false;
  uint32_t generation = 0;
};
std::array<ChargeLend, 2> g_charge_lend{};

/// The slot table's row 1: what the game's own pick path reads back (+0x80 + 5 + slot).
constexpr uint32_t kSlotTableRow = 5;
/// A lend whose button was let go with nothing charged comes back after this long.
constexpr float kLendIdleSeconds = 1.0f;
/// Any lend comes back after this long, whatever happened.
constexpr float kLendMaxSeconds = 30.0f;

uint32_t SpellManagerOf(uint8_t* base, uint32_t hero) {
  return Plausible(hero) ? refii_mod_component(base, hero, kSpellManagerComponent) : 0u;
}

/// One slot through the game's own writer, NOTIFY clear (no pick, nothing sent).
void WriteSlot(PPCContext& ctx, uint8_t* base, uint32_t manager, int32_t slot, int32_t spell) {
  PPCContext work = refii::mods::Frame(ctx);
  work.r3.u32 = manager;
  work.r4.u32 = 0u;  // the guest passes 0 from every one of its own call sites
  work.r5.u32 = static_cast<uint32_t>(slot);
  work.r6.u32 = static_cast<uint32_t>(spell);
  work.r7.u32 = 0u;
  rex_CECSpellManager_SetSpellSlot(work, base);
}

void RestoreLend(PPCContext& ctx, uint8_t* base, int32_t player) {
  ChargeLend& lend = g_charge_lend[player - 1];
  if (!lend.active) {
    return;
  }
  const uint32_t manager = refii_mod_player_hero(base, player) == lend.hero ? SpellManagerOf(base, lend.hero) : 0u;
  if (manager != 0u) {
    for (int32_t slot = 0; slot < kSpellSlots; ++slot) {
      WriteSlot(ctx, base, manager, slot, lend.saved[slot]);
    }
  }
  lend = ChargeLend{};
}

}  // namespace

extern "C" int32_t refii_mod_charge_spell(PPCContext* ctx, uint8_t* base, int32_t player, int32_t type, int32_t held) {
  if (ctx == nullptr || (player != 1 && player != 2) || refii_mod_player_here(player) == 0) {
    return 0;
  }
  ChargeLend& lend = g_charge_lend[player - 1];
  if (held == 0) {
    refii_mod_press(player, "B", 0);
    if (lend.active && !lend.released) {
      lend.released = true;
      lend.since_release = 0.0f;
    }
    return lend.active ? 1 : 0;
  }
  const uint32_t hero = refii_mod_player_hero(base, player);
  const uint32_t manager = SpellManagerOf(base, hero);
  if (manager == 0u || type < 0 || type >= static_cast<int32_t>(kSpellTypes)) {
    return 0;
  }
  const auto offered = [&](int32_t tier) {
    const uint32_t index = kSpellTiers * static_cast<uint32_t>(type) + static_cast<uint32_t>(tier);
    return refii::mods::LoadU8(base, manager + kSpellKnown + index) != 0 && refii::mods::LoadU8(base, manager + kSpellOffered + index) != 0;
  };
  if (!offered(0)) {
    return 0;
  }
  if (lend.active && lend.hero != hero) {
    RestoreLend(*ctx, base, player);
  }
  if (!lend.active) {
    lend.active = true;
    lend.hero = hero;
    lend.generation = refii_mod_lua_generation();
    for (int32_t slot = 0; slot < kSpellSlots; ++slot) {
      lend.saved[slot] = static_cast<int8_t>(refii::mods::LoadU8(base, manager + kSlotTable + kSlotTableRow + static_cast<uint32_t>(slot)));
    }
  }
  lend.type = type;
  lend.released = false;
  lend.since_release = 0.0f;
  lend.age = 0.0f;
  lend.restore = false;
  bool above = false;
  for (int32_t tier = 0; tier < kSpellSlots; ++tier) {
    above = above || !offered(tier);
    WriteSlot(*ctx, base, manager, tier, above ? -1 : type);
  }
  refii_mod_press(player, "B", 1);
  return 1;
}

namespace refii::vfs {

/// mod_events.cpp: whether a lend is out for `player` (rex_CAction_End then watches its charge).
bool ModChargeLendActive(int32_t player) {
  return (player == 1 || player == 2) && g_charge_lend[player - 1].active;
}

/// mod_events.cpp rex_CAction_End: the lent player's charge ended; without a release nothing will
/// be cast, so the slots come back on the next tick.
void ModChargeLendEnded(int32_t player, bool released) {
  if (ModChargeLendActive(player) && !released) {
    g_charge_lend[player - 1].restore = true;
  }
}

/// mod_events.cpp rex_CECSpellManager_SetState: a spell manager entered `state`; 6 is the cast's
/// last slot out (the cascade's `if (*(+0x170) <= 0) SetState(6)`, the quick cast's after its one
/// LaunchSlot), 0 the reset — either way the lent slots have been read for the last time.
void ModChargeLendState(uint8_t* base, uint32_t manager, int32_t state) {
  if (state != 6 && state != 0) {
    return;
  }
  for (ChargeLend& lend : g_charge_lend) {
    if (lend.active && lend.released && SpellManagerOf(base, lend.hero) == manager) {
      lend.restore = true;
    }
  }
}

/// mod_abilities.cpp rex_CECSpellManager_SetSpellSlot: a real pick (NOTIFY set) of `spell` for a
/// lent player's slot is what comes back for that slot.
void ModChargeLendPicked(int32_t player, int32_t slot, int32_t spell) {
  if (ModChargeLendActive(player) && slot >= 0 && slot < kSpellSlots) {
    g_charge_lend[player - 1].saved[slot] = static_cast<int8_t>(spell);
  }
}

/// Every tick (mod_events.cpp RunModEventsTick): a restore that is due, a lend whose button went
/// with nothing charged, a hero gone, the mods' Lua reloaded, or the time limit.
void PumpChargeLend(PPCContext& ctx, uint8_t* base, float seconds) {
  for (int32_t player = 1; player <= 2; ++player) {
    ChargeLend& lend = g_charge_lend[player - 1];
    if (!lend.active) {
      continue;
    }
    lend.age += seconds;
    if (lend.released) {
      lend.since_release += seconds;
    }
    const uint32_t manager = SpellManagerOf(base, lend.hero);
    // The manager's state, as CECSpellManager::SetState stores it (`stw r4,0x70(r31)` 0x8253e864):
    // outside 2..5 (charging, quick cast, released, cascade) nothing is being charged or cast.
    const uint32_t state = manager != 0u ? refii::mods::LoadU32(base, manager + 0x70u) : 0u;
    const bool idle = lend.released && lend.since_release >= kLendIdleSeconds && (state < 2u || state > 5u);
    if (lend.restore || idle || lend.age >= kLendMaxSeconds || refii_mod_player_hero(base, player) != lend.hero ||
        refii_mod_lua_generation() != lend.generation) {
      if (refii_mod_lua_generation() != lend.generation) {
        refii_mod_press(player, "B", 0);
      }
      RestoreLend(ctx, base, player);
    }
  }
}

}  // namespace refii::vfs

extern "C" uint32_t refii_mod_selected_expression(uint8_t* base, int32_t player) {
  if (player < 1 || player > 2) {
    return 0;
  }
  const uint32_t gui = refii::vfs::LoadGuestU32(base, refii::guest::Address(kGuiObject));
  const uint32_t hud = Plausible(gui) ? refii::mods::LoadU32(base, gui + 32u) : 0u;
  const uint32_t wheel = Plausible(hud) ? refii::mods::LoadU32(base, hud + 60u) : 0u;
  if (!Plausible(wheel) || refii::mods::LoadU32(base, wheel + 4u * kWheelOpen) != 1u ||
      refii::mods::LoadU32(base, wheel + 4u * kWheelPlayer) != static_cast<uint32_t>(player)) {
    return 0;
  }
  const uint32_t begin = refii::mods::LoadU32(base, wheel + 4u * kWheelBegin);
  const uint32_t end = refii::mods::LoadU32(base, wheel + 4u * kWheelEnd);
  const uint32_t cursor = refii::mods::LoadU32(base, wheel + 4u * kWheelCursor);
  if (!Plausible(begin) || end < begin || end - begin > kWheelEntryBytes * kWheelEntriesMax ||
      (end - begin) % kWheelEntryBytes != 0 || cursor >= (end - begin) / kWheelEntryBytes) {
    return 0;
  }
  const uint32_t entry = begin + cursor * kWheelEntryBytes;
  const uint32_t record = refii::mods::LoadU32(base, entry);
  const uint32_t flags = refii::mods::LoadU32(base, entry + 4u);
  const uint32_t tail = refii::mods::LoadU32(base, entry + 8u);
  if (((flags >> 16) & 0xFFu) == 0u || (flags & 0xFFu) != 0u || (tail & 0xFFFF0000u) != 0u) {
    return 0;
  }
  return record;
}

extern "C" int32_t refii_mod_record_string(PPCContext* ctx, uint8_t* base, uint32_t definition, const char* field,
                                           char* out, uint32_t size) {
  if (out != nullptr && size != 0) {
    out[0] = 0;
  }
  if (ctx == nullptr || definition == 0 || field == nullptr || field[0] == 0 || out == nullptr || size == 0) {
    return 0;
  }
  const uint32_t gdb = refii::vfs::LoadGuestU32(base, refii::guest::Address(kGdbManager));
  if (!Plausible(gdb)) {
    return 0;
  }
  PPCContext lookup = refii::mods::Frame(*ctx);
  lookup.r3.u32 = gdb;
  lookup.r4.u32 = definition;
  rex_GDBManager_FindEntryByHash(lookup, base);
  const uint32_t entry = lookup.r3.u32;
  if (!Plausible(entry)) {
    return 0;
  }
  // The property name on our own frame: the getter hashes it (FNV-1) and asks the entry.
  PPCContext work = refii::mods::Frame(*ctx);
  const uint32_t name = work.r1.u32 + 0x80u;
  size_t n = std::strlen(field);
  if (n > 63) {
    n = 63;
  }
  std::memcpy(base + name, field, n);
  base[name + n] = 0;
  work.r3.u32 = entry;
  work.r4.u32 = name;
  if (!refii::mods::CallGuest(work, base, refii::guest::Address(kRecordStringGetter))) {
    return 0;
  }
  const uint32_t text = work.r3.u32;
  if (!Plausible(text)) {
    return 0;
  }
  uint32_t i = 0;
  for (; i + 1 < size && base[text + i] != 0; ++i) {
    out[i] = static_cast<char>(base[text + i]);
  }
  out[i] = 0;
  return 1;
}

namespace {

/// The database entry a record hash names (GDBManager_FindEntryByHash), or 0.
uint32_t RecordEntry(PPCContext* ctx, uint8_t* base, uint32_t definition) {
  const uint32_t gdb = refii::vfs::LoadGuestU32(base, refii::guest::Address(kGdbManager));
  if (ctx == nullptr || definition == 0 || !Plausible(gdb)) {
    return 0u;
  }
  PPCContext lookup = refii::mods::Frame(*ctx);
  lookup.r3.u32 = gdb;
  lookup.r4.u32 = definition;
  rex_GDBManager_FindEntryByHash(lookup, base);
  return Plausible(lookup.r3.u32) ? lookup.r3.u32 : 0u;
}

/// The value word of an entry's property, INHERITED, and the type the database stores it as
/// (GDBEntry_FindPropWalkParents), or 0.
uint32_t RecordProperty(PPCContext* ctx, uint8_t* base, uint32_t entry, const char* field, int32_t* type) {
  *type = -1;
  PPCContext work = refii::mods::Frame(*ctx);
  const uint32_t out = (work.r1.u32 + 0x100u) & ~0xFu;
  refii::mods::StoreU32(base, out, 0xFFFFFFFFu);
  work.r3.u32 = entry;
  work.r4.u32 = refii_mod_fnv1(field);
  work.r5.u32 = out;
  if (!refii::mods::CallGuest(work, base, refii::guest::Address(kRecordPropertyWalk)) || !Plausible(work.r3.u32)) {
    return 0u;
  }
  *type = static_cast<int32_t>(refii::mods::LoadU32(base, out));
  return work.r3.u32;
}

}  // namespace

extern "C" int32_t refii_mod_record_int(PPCContext* ctx, uint8_t* base, uint32_t definition, const char* field,
                                        int32_t* out) {
  if (out != nullptr) {
    *out = 0;
  }
  const uint32_t entry = field != nullptr && field[0] != 0 && out != nullptr ? RecordEntry(ctx, base, definition) : 0u;
  int32_t type = -1;
  const uint32_t value = entry != 0u ? RecordProperty(ctx, base, entry, field, &type) : 0u;
  if (value == 0u || (type != static_cast<int32_t>(kGdbTypeInt) && type != static_cast<int32_t>(kGdbTypeEnum))) {
    return 0;
  }
  *out = static_cast<int32_t>(refii::mods::LoadU32(base, value));
  return 1;
}

extern "C" int32_t refii_mod_record_exists(PPCContext* ctx, uint8_t* base, uint32_t definition) {
  return RecordEntry(ctx, base, definition) != 0u ? 1 : 0;
}

extern "C" int32_t refii_mod_record_number(PPCContext* ctx, uint8_t* base, uint32_t definition, const char* field,
                                           float* out) {
  if (out != nullptr) {
    *out = 0.0f;
  }
  const uint32_t entry = field != nullptr && field[0] != 0 && out != nullptr ? RecordEntry(ctx, base, definition) : 0u;
  if (entry == 0u) {
    return 0;
  }
  int32_t type = -1;
  const uint32_t value = RecordProperty(ctx, base, entry, field, &type);
  if (value == 0u) {
    return 0;
  }
  if (type == static_cast<int32_t>(kGdbTypeFloat)) {
    *out = refii::mods::LoadF32(base, value);
    return 1;
  }
  if (type == static_cast<int32_t>(kGdbTypeInt) || type == static_cast<int32_t>(kGdbTypeEnum)) {
    *out = static_cast<float>(static_cast<int32_t>(refii::mods::LoadU32(base, value)));
    return 1;
  }
  return 0;
}

extern "C" int32_t refii_mod_record_bool(PPCContext* ctx, uint8_t* base, uint32_t definition, const char* field,
                                         int32_t* out) {
  if (out != nullptr) {
    *out = 0;
  }
  const uint32_t entry = field != nullptr && field[0] != 0 && out != nullptr ? RecordEntry(ctx, base, definition) : 0u;
  int32_t type = -1;
  const uint32_t value = entry != 0u ? RecordProperty(ctx, base, entry, field, &type) : 0u;
  if (value == 0u || type != static_cast<int32_t>(kGdbTypeBool)) {
    return 0;
  }
  *out = refii::mods::LoadU32(base, value) != 0u ? 1 : 0;
  return 1;
}

extern "C" int32_t refii_mod_record_ref(PPCContext* ctx, uint8_t* base, uint32_t definition, const char* field,
                                        uint32_t* out) {
  /// The database's reference type: its value word is the named record's hash (`lwz r4,0(r11)`
  /// before GDBManager_FindEntryByHash, 0x821b0994..98).
  constexpr uint32_t kGdbTypeRef = 6;
  static_assert(kGdbTypeRef != kGdbTypeBool);
  if (out != nullptr) {
    *out = 0u;
  }
  const uint32_t entry = field != nullptr && field[0] != 0 && out != nullptr ? RecordEntry(ctx, base, definition) : 0u;
  int32_t type = -1;
  const uint32_t value = entry != 0u ? RecordProperty(ctx, base, entry, field, &type) : 0u;
  if (value == 0u || type != static_cast<int32_t>(kGdbTypeRef)) {
    return 0;
  }
  *out = refii::mods::LoadU32(base, value);
  return 1;
}

extern "C" int32_t refii_mod_creature_tier(uint8_t* base, uint32_t entity) {
  /// comp20 +0x1C IsLieutenant, +0x1D IsElite (the init's `addi r5,r31,0x1C` 0x823a3434 / `addi
  /// r5,r31,0x1D` 0x823a3448; Creature.IsLieutenant / IsElite read them back).
  constexpr uint32_t kCreatureIsLieutenant = 0x1C;
  constexpr uint32_t kCreatureIsElite = 0x1D;
  const uint32_t creature = entity != 0 ? refii_mod_component(base, entity, kCreatureComponent) : 0u;
  if (creature == 0u) {
    return -1;
  }
  int32_t tier = 0;
  if (refii::mods::LoadU8(base, creature + kCreatureIsElite) != 0) {
    tier |= REFII_CREATURE_ELITE;
  }
  if (refii::mods::LoadU8(base, creature + kCreatureIsLieutenant) != 0) {
    tier |= REFII_CREATURE_LIEUTENANT;
  }
  return tier;
}

extern "C" int32_t refii_mod_entities_near(PPCContext* ctx, uint8_t* base, const float* xyz, float radius,
                                           int32_t creatures_only, uint32_t* out, int32_t max) {
  if (ctx == nullptr || xyz == nullptr || out == nullptr || max <= 0 || !(radius > 0.0f)) {
    return 0;
  }
  const uint32_t manager = refii::vfs::LoadGuestU32(base, refii::guest::Address(kEntityManager));
  if (!Plausible(manager)) {
    return 0;
  }
  struct Found {
    uint32_t entity;
    float distance2;
  };
  std::vector<Found> found;
  const float limit = radius * radius;
  uint32_t node = refii::vfs::LoadGuestU32(base, manager + kManagerList);
  for (int32_t i = 0; Plausible(node) && i < kNearScanLimit; ++i, node = refii::vfs::LoadGuestU32(base, node)) {
    const uint32_t entity = node - kEntityNode;
    if ((base[entity + kEntityFlags] & kInWorld) == 0 ||
        (refii::vfs::LoadGuestU32(base, entity + kPresenceWord) & kHasPlacementBit) == 0) {
      continue;
    }
    if (creatures_only != 0 && refii_mod_component(base, entity, kCreatureComponent) == 0) {
      continue;
    }
    float at[3];
    if (refii_mod_entity_position(ctx, base, entity, at) == 0) {
      continue;
    }
    const float dx = at[0] - xyz[0], dy = at[1] - xyz[1], dz = at[2] - xyz[2];
    const float d2 = dx * dx + dy * dy + dz * dz;
    if (d2 < limit) {
      found.push_back(Found{entity, d2});
    }
  }
  std::sort(found.begin(), found.end(), [](const Found& a, const Found& b) { return a.distance2 < b.distance2; });
  const int32_t n = static_cast<int32_t>(found.size()) < max ? static_cast<int32_t>(found.size()) : max;
  for (int32_t i = 0; i < n; ++i) {
    out[i] = found[static_cast<size_t>(i)].entity;
  }
  return n;
}

extern "C" int32_t refii_mod_line_of_sight(PPCContext* ctx, uint8_t* base, const float* from, const float* to) {
  if (ctx == nullptr || from == nullptr || to == nullptr) {
    return 0;
  }
  // The physics object the raycast is made on (*(world+0x18)): none before a session.
  const uint32_t world = refii_mod_world(base);
  if (world == 0 || !Plausible(refii::vfs::LoadGuestU32(base, world + 0x18u))) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx, 0x800u);
  refii::mods::SetVector(work, 1, from);
  refii::mods::SetVector(work, 2, to);
  rex_Village_IsLineOfSight(work, base);
  return (work.r3.u32 & 0xFFu) != 0 ? 1 : 0;
}

extern "C" uint32_t refii_mod_spawn(PPCContext* ctx, uint8_t* base, uint32_t definition, const float* xyz) {
  const uint32_t system = refii::vfs::ModScriptSystem();
  if (ctx == nullptr || xyz == nullptr || definition == 0 || !Plausible(system)) {
    return 0;
  }
  // The game creates an entity even for a hash its database lacks; refuse that here. The manager
  // is g_GDBManager (TU1 0x83496DA0), its lookup GDBManager_FindEntryByHash (r3 manager, r4 hash).
  const uint32_t gdb = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496DA0u));
  if (!Plausible(gdb)) {
    return 0;
  }
  PPCContext lookup = refii::mods::Frame(*ctx);
  lookup.r3.u32 = gdb;
  lookup.r4.u32 = definition;
  rex_GDBManager_FindEntryByHash(lookup, base);
  if (!Plausible(lookup.r3.u32)) {
    return 0;
  }
  // The name argument is an LHString handle the entity's name is copied from: an empty handle,
  // on our own frame, names nothing.
  PPCContext work = refii::mods::Frame(*ctx, 0x400u);
  const uint32_t name = work.r1.u32 + 0x80u;
  refii::mods::StoreU32(base, name, 0u);
  work.r3.u32 = system;
  work.r4.u32 = definition;
  work.r5.u32 = name;
  refii::mods::SetVector(work, 1, xyz);
  rex_lhLuaScriptSystem_CreateEntityAtPosition(work, base);
  return Plausible(work.r3.u32) ? work.r3.u32 : 0u;
}
