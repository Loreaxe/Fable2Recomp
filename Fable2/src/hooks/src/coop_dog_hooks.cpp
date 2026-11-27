// Co-op: a dog for each player, bound to them, as Fable 3 gives the visiting hero their own.
//
// THE GUEST'S ONE DOG. The dog component (97) holds its master as a weak reference at comp+300
// (Dog.GetMaster, `sub_82174410`). Its only writer, SetMaster `sub_82814DD8(dogComp, hero)`,
// also stores the dog into the one global "the dog" slot players+192 (Lua GetDog,
// `sub_8222C148`, and the logbook / menu readers). Both places that pick a master pick hero one
// (`sub_8222C1B8`, players+104): the init `sub_8280AB90` (vtable slot 11, run synchronously
// inside the create, 0x8238B984) and the per-frame update `sub_822AE210`, which rebinds any dog
// whose master is gone.
//
// PLAYER TWO'S DOG is the one named kPartnerDogName. The host creates it with that instance
// name, and the name travels in the create message (`sub_82337A98` copies it beside the record
// id; the client rebuilds it at 0x8232EB18 into its replica), so both machines tell it apart
// the same way (RefiiCoopPartnerDogRole, guest_tick_pump.cpp). For that dog:
//   - both master picks answer player two (0 while player two is absent: no bind at all);
//   - SetMaster leaves the global slot alone, so GetDog() stays hero one's dog.
//
// THE HOST MAKES IT, as Fable 3 does (`sub_8243A190` → `sub_823A0AF8(mgr, 1)` when the remote
// hero is created), through the game's own CreateEntityFromIDAtPosition in game Lua: the breed,
// both levels and the tricks come from player two's HeroState (the dog they were playing with),
// the plain CreatureDogHero when it states none; its collar is worn and its name restated. It exists
// while player two stands as a HERO they brought (RefiiCoopGetOwnHero) and hero one has a dog. The
// plain henchman has none, as shipped; the host destroys it when player two leaves or becomes the
// henchman (`entity:Destroy()`, `sub_8238D828`, which sends DestroyEntity to the joiner). A joiner
// never creates or destroys one. The rule is core/coop_partner_dog_rule.h.

#include <array>
#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>

#include <rex/logging.h>
#include <rex/ppc/context.h>

#include "core/coop_partner_dog_rule.h"
#include "core/guest_build.h"
#include "core/guest_state.h"
#include "core/refii_mod.h"

extern "C" uint8_t* RefiiGuestBase(void);
/// Player two brought a hero (a character from a save) rather than standing as the plain henchman.
extern "C" int RefiiCoopGetOwnHero(void);
extern "C" uint32_t RefiiCoopPlayerDog(uint8_t* base, int player);
extern "C" void RefiiCoopSetPartnerDog(uint32_t dog);
extern "C" int RefiiCoopPartnerDogRole(uint32_t dog);
extern "C" int RefiiCoopDressPartnerDog(PPCContext* ctx, uint8_t* base, uint32_t dog);
extern "C" int RefiiCoopPartnerDogName(char* out, uint32_t capacity);
extern "C" void RefiiCoopPartnerScreen(int on);
extern "C" REX_FUNC(__imp__rex_CECDog_Update);
extern "C" REX_FUNC(__imp__rex_CECDog_TeleportNearMasterOffScreen);
extern "C" REX_FUNC(__imp__rex_CECDog_IsMasterInsideBuilding);
extern "C" uint32_t RefiiGdbRecordId(PPCContext* ctx, uint8_t* base, const char* name);
extern "C" uint32_t refii_mod_component(uint8_t* base, uint32_t entity, uint32_t id);
extern "C" uint32_t RefiiCoopPartnerDogKit(uint32_t* definition, int32_t* searching, int32_t* combat,
                                           char* tricks, uint32_t capacity);

namespace {

constexpr const char* kPartnerDogName = "RefiiPartnerDog";

/// Ticks between looks at the dog (the guest steps 30 a second).
constexpr uint32_t kCadence = 30u;

/// How long a new player two waits for their HeroState to state a dog before the plain breed.
constexpr uint32_t kKitWaitTicks = 10u * 30u;

/// How long after an attempt that made no dog before the next.
constexpr uint32_t kRetryTicks = 5u * 30u;

/// How long player two must be gone before their dog is: a body the guest rebuilds (a retype, a
/// level change) is briefly out of the world.
constexpr uint32_t kGoneTicks = 3u * 30u;

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

/// Second dogs of player two's a bind refused, as mod handles, for the host's tick to destroy.
/// Guest tick thread only.
std::array<uint32_t, 4> g_doomed{};

std::atomic<bool> g_said_bind{false};
std::atomic<bool> g_said_global{false};

/// Whose master a dog gets at a hero-one pick: player two for player two's dog, else `hero_one`.
///
/// NEVER NO MASTER. The update goes on to `sub_822AEAC0` (0x822ae2c8), which dereferences the
/// master; shipped, hero one always exists, so a pick never came back empty. Player two's dog
/// whose player has gone (it outlives them by up to kGoneTicks before the tick destroys it),
/// and a second dog awaiting destruction, follow hero one meanwhile. Answering 0 crashed player
/// one the moment player two left (2026-10-20).
uint32_t MasterFor(uint32_t dog_comp, uint32_t hero_one) {
  uint8_t* base = RefiiGuestBase();
  if (base == nullptr || !Plausible(dog_comp)) {
    return hero_one;
  }
  const uint32_t dog = refii::vfs::LoadGuestU32(base, dog_comp + 4u);
  switch (RefiiCoopPartnerDogRole(dog)) {
    case 1: {
      // The henchman has no dog: one left from player two's time as a hero follows hero one until
      // the tick destroys it.
      const uint32_t hero_two = refii_mod_player_hero(base, 2);
      if (hero_two == 0u || refii_mod_entity_alive(base, hero_two) == 0 || RefiiCoopGetOwnHero() == 0) {
        return hero_one;
      }
      if (!g_said_bind.exchange(true)) {
        REXLOG_INFO("coop dogs: player two's dog binds to player two (0x{:08X})", hero_two);
      }
      return hero_two;
    }
    case 2:
      for (uint32_t& slot : g_doomed) {
        if (slot == refii_mod_handle(dog)) {
          break;
        }
        if (slot == 0u) {
          slot = refii_mod_handle(dog);
          break;
        }
      }
      return hero_one;
    default:
      return hero_one;
  }
}

/// This machine in an online session as the joiner (net manager +52 and +54).
bool IsJoiner(uint8_t* base) {
  const uint32_t root = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = Plausible(root) ? refii::vfs::LoadGuestU32(base, root + 0xCu) : 0u;
  const uint32_t session = Plausible(game) ? refii::vfs::LoadGuestU32(base, game + 0x8Cu) : 0u;
  return Plausible(session) && refii::vfs::LoadGuestU8(base, session + 0x34u) != 0u &&
         refii::vfs::LoadGuestU8(base, session + 0x36u) != 0u;
}

void Destroy(PPCContext* ctx, uint8_t* base, uint32_t dog) {
  char chunk[160];
  std::snprintf(chunk, sizeof(chunk),
                "local d = Refii.entity(%u) if d ~= nil and d:IsAlive() then d:Destroy() end",
                refii_mod_handle(dog));
  refii_mod_run_game_lua(ctx, base, chunk);
}

/// Creates player two's dog near them from their HeroState's dog. The breed must be one of the
/// game's own hero dogs; anything else (or none) makes the plain one.
void Spawn(PPCContext* ctx, uint8_t* base) {
  uint32_t definition = 0u;
  int32_t searching = 0;
  int32_t combat = 0;
  char tricks[1024] = {};
  RefiiCoopPartnerDogKit(&definition, &searching, &combat, tricks, sizeof(tricks));

  // The trick names are GDB names (the pump keeps only [A-Za-z0-9_]); quoted for a Lua table.
  std::string list;
  for (const char* at = tricks; *at != '\0';) {
    const char* end = std::strchr(at, ',');
    const std::size_t length = end != nullptr ? static_cast<std::size_t>(end - at) : std::strlen(at);
    if (length != 0u) {
      list += (list.empty() ? "\"" : ",\"") + std::string(at, length) + "\"";
    }
    at += length + (end != nullptr ? 1u : 0u);
  }

  // The name its player gave it, as a Lua string literal: quotes, backslashes and control bytes
  // are dropped rather than escaped (a name is letters).
  char given[64] = {};
  std::string name;
  if (RefiiCoopPartnerDogName(given, sizeof(given)) != 0) {
    for (const char* at = given; *at != '\0'; ++at) {
      const unsigned char c = static_cast<unsigned char>(*at);
      if (c >= 0x20u && c != '"' && c != '\\') name += *at;
    }
  }

  // THE BREED IS CHOSEN HERE, by record id. The stated breed is player two's dog's record id
  // (entity+0x1C); game Lua cannot compare one — `record:GetID()` is a userdata, so an equality
  // with a number is never true and every breed came out the plain dog (seen live 2026-10-20).
  // The match is made against the game's own record table and Lua is handed the breed's name,
  // whose `GetID()` goes to CreateEntityFromIDAtPosition as the game's breed change passes it.
  static constexpr const char* kBreeds[] = {
      "CreatureDogHero", "CreatureDogHeroBloodhound", "CreatureDogHeroDalmation", "CreatureDogHeroHusky",
  };
  const char* breed = kBreeds[0];
  for (const char* candidate : kBreeds) {
    if (definition != 0u && RefiiGdbRecordId(ctx, base, candidate) == definition) {
      breed = candidate;
      break;
    }
  }
  const std::string chunk =
      "local hero = GetPlayerHenchman and GetPlayerHenchman()\n"
      "if hero == nil or not hero:IsAlive() then return end\n"
      "local record = GDB.GetRecord(\"" + std::string(breed) + "\")\n"
      "if record == nil or record:IsNull() then return end\n"
      "local dog = CreateEntityFromIDAtPosition(record:GetID(), \"" + std::string(kPartnerDogName) + "\", hero:GetPosition())\n"
      "if dog == nil or not dog:IsAlive() then return end\n"
      "Refii._set_partner_dog(Refii.handle(dog))\n" +
      (name.empty() ? std::string()
                    : "Creature.SetPlayerDefinedName(dog, \"" + name + "\")\n") +
      "if " + std::to_string(searching) + " > 1 then Dog.RaiseSniffingLevelTo(dog, " +
      std::to_string(searching) + ") end\n"
      "if " + std::to_string(combat) + " > 1 then Dog.RaiseCombatLevelTo(dog, " +
      std::to_string(combat) + ") end\n"
      "for _, name in ipairs({ " + list + " }) do\n"
      "  local record = GDB.GetRecord(name)\n"
      "  if record ~= nil and not record:IsNull() then Dog.UnlockTrickIfNecessary(dog, record) end\n"
      "end\n";
  const bool ran = refii_mod_run_game_lua(ctx, base, chunk.c_str()) != 0;
  const uint32_t dog = RefiiCoopPlayerDog(base, 2);
  REXLOG_INFO("coop dogs: player two's dog {} ({}, stated breed 0x{:08X}, levels {}/{}, tricks [{}]){}",
              dog != 0u ? "created" : "NOT created", breed, definition, searching, combat, tricks,
              ran ? "" : " — the chunk failed");
}

void Tick(PPCContext* ctx, uint8_t* base, float) {
  static uint32_t tick = 0u;
  static uint32_t hero_two_ticks = 0u;
  static uint32_t gone_ticks = 0u;
  static uint32_t next_attempt = 0u;
  ++tick;
  if ((tick % kCadence) != 0u || base == nullptr) {
    return;
  }

  // The host owns every dog; a joiner's copies come and go with the host's.
  if (IsJoiner(base)) {
    g_doomed.fill(0u);
    return;
  }

  for (uint32_t& slot : g_doomed) {
    const uint32_t dog = slot != 0u ? refii_mod_handle_address(slot) : 0u;
    slot = 0u;
    if (dog != 0u && refii_mod_entity_alive(base, dog) != 0) {
      REXLOG_INFO("coop dogs: a second dog of player two's is destroyed");
      Destroy(ctx, base, dog);
    }
  }

  const uint32_t hero_two = refii_mod_player_hero(base, 2);
  refii::coop::PartnerDogFacts facts;
  facts.partner_here = hero_two != 0u && refii_mod_entity_alive(base, hero_two) != 0;
  facts.partner_is_hero = RefiiCoopGetOwnHero() != 0;
  const uint32_t dog = RefiiCoopPlayerDog(base, 2);
  facts.has_dog = dog != 0u;

  if (!facts.partner_here) {
    hero_two_ticks = 0u;
    gone_ticks += kCadence;
  } else {
    gone_ticks = 0u;
    hero_two_ticks = facts.partner_is_hero ? hero_two_ticks + kCadence : 0u;
  }
  facts.gone_ticks = gone_ticks;
  facts.hero_ticks = hero_two_ticks;
  facts.attempt_due = tick >= next_attempt;
  if (facts.partner_here && facts.partner_is_hero && !facts.has_dog) {
    facts.hero_one_has_dog = RefiiCoopPlayerDog(base, 1) != 0u;
    uint32_t definition = 0u;
    facts.kit_stated = RefiiCoopPartnerDogKit(&definition, nullptr, nullptr, nullptr, 0u) != 0u;
  }

  switch (refii::coop::NextPartnerDogStep(facts, kGoneTicks, kKitWaitTicks)) {
    case refii::coop::PartnerDogStep::kDestroy:
      if (facts.partner_here) {
        REXLOG_INFO("coop dogs: player two is the henchman now; the dog of their hero goes");
      } else {
        REXLOG_INFO("coop dogs: player two left; their dog goes with them");
      }
      Destroy(ctx, base, dog);
      RefiiCoopSetPartnerDog(0u);
      return;
    case refii::coop::PartnerDogStep::kDress:
      RefiiCoopDressPartnerDog(ctx, base, dog);
      return;
    case refii::coop::PartnerDogStep::kSpawn:
      next_attempt = tick + kRetryTicks;
      Spawn(ctx, base);
      return;
    case refii::coop::PartnerDogStep::kNone:
      return;
  }
}

REFII_MOD_ON_TICK(Tick);

}  // namespace

// The init's master pick (`mr r31, r3` right after `bl sub_8222C1B8`; r30 the dog component).
void RefiiCoopDogInitMaster(PPCRegister& r3, PPCRegister& r30) {
  r3.u64 = MasterFor(r30.u32, r3.u32);
}

// The update's rebind (`mr r4, r3` right after `bl sub_8222C1B8`; r26 the dog component).
void RefiiCoopDogUpdateMaster(PPCRegister& r3, PPCRegister& r26) {
  r3.u64 = MasterFor(r26.u32, r3.u32);
}

// SetMaster's global store (players+192 = this dog) is skipped for player two's dog: GetDog()
// and every reader of that slot stay on hero one's. r30 is the dog component.
bool RefiiCoopDogKeepsGlobalSlot(PPCRegister& r30) {
  uint8_t* base = RefiiGuestBase();
  if (base == nullptr || !Plausible(r30.u32)) {
    return false;
  }
  const uint32_t dog = refii::vfs::LoadGuestU32(base, r30.u32 + 4u);
  if (RefiiCoopPartnerDogRole(dog) == 0) {
    return false;
  }
  if (!g_said_global.exchange(true)) {
    REXLOG_INFO("coop dogs: player two's dog leaves GetDog() to hero one's");
  }
  return true;
}

// PLAYER TWO'S DOG SEES PLAYER TWO'S SCREEN. Its update and its off-screen teleport ask "is it on
// screen" of player one's camera (coop_hero_hooks.cpp, rex_lhCamera_ScreenBlock), so out of
// player one's view player two's dog kept teleporting to player two. Both run with player two's
// view for player two's dog (r3 = the dog component, its entity at +4).
namespace {
void AsPartnerDog(PPCContext& ctx, uint8_t* base, PPCFunc* original) {
  const uint32_t dog = Plausible(ctx.r3.u32) ? refii::vfs::LoadGuestU32(base, ctx.r3.u32 + 4u) : 0u;
  if (RefiiCoopPartnerDogRole(dog) == 0) {
    original(ctx, base);
    return;
  }
  RefiiCoopPartnerScreen(1);
  original(ctx, base);
  RefiiCoopPartnerScreen(0);
}
}  // namespace

REX_EXTERN(rex_CECDog_Update) { AsPartnerDog(ctx, base, __imp__rex_CECDog_Update); }

// PLAYER TWO'S DOG TRAVELS WITH THEM. A level change that stays in the level (a door, travel to a
// marker here) places the travellers in `sub_823D6E18` (CmdLevelChange vtable +32, disc
// `sub_8245E3F8`), when command+50 is clear and game+260 (disc +256) is not: hero one, then the
// global dog (players+192) through `sub_8280B4A8(dogComp, 0)` (0x823D70FC) — its own master's
// position, a nav fit, SetPosition — then hero two. Player two's dog is not the global dog and was
// left behind. The host runs the same teleport for it after the original; a joiner's copy follows
// the host's.
extern "C" REX_FUNC(__imp__rex_CmdLevelChange_PlaceTravellers);

REX_EXTERN(rex_CmdLevelChange_PlaceTravellers) {
  using refii::vfs::LoadGuestU8;
  using refii::vfs::LoadGuestU32;
  const uint32_t command = ctx.r3.u32;
  const uint32_t root = LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = Plausible(root) ? LoadGuestU32(base, root + 0xCu) : 0u;
  const bool same_level = Plausible(command) && Plausible(game) &&
                          LoadGuestU8(base, command + 50u) == 0u &&
                          LoadGuestU8(base, game + refii::guest::Pick(260u, 256u)) == 0u;
  const PPCContext saved = ctx;
  __imp__rex_CmdLevelChange_PlaceTravellers(ctx, base);
  if (!same_level || IsJoiner(base)) {
    return;
  }
  const uint32_t dog = RefiiCoopPlayerDog(base, 2);
  const uint32_t comp = dog != 0u && refii_mod_entity_alive(base, dog) != 0 ? refii_mod_component(base, dog, 97u) : 0u;
  static PPCFunc* const teleport = refii::vfs::FindGuestFunction(refii::guest::Address(0x8280B4A8u));
  if (comp == 0u || teleport == nullptr) {
    return;
  }
  PPCContext call = saved;
  call.r3.u64 = comp;
  call.r4.u64 = 0u;
  teleport(call, base);
  REXLOG_INFO("coop dogs: player two's dog travels with them");
}

REX_EXTERN(rex_CECDog_TeleportNearMasterOffScreen) {
  AsPartnerDog(ctx, base, __imp__rex_CECDog_TeleportNearMasterOffScreen);
}

// A DOG WITH NO MASTER IS NOT INSIDE ANYTHING. `sub_82812E88` (r3 the dog component) locks the
// master's weak reference at comp+300 and reads the master's flags at +52 with no null test, so
// an empty or expired reference faults at guest 0x34. Shipped, hero one never went away under
// their dog. Player two's body does: when a joiner takes their own hero, the host rebuilds player
// two's entity and the joiner's copy of the partner dog holds an expired master until the next
// CECDog_Update rebinds it. Asked in that window, the joiner's GameThread died about a second
// after "Travelling to Oakfield", and the host lost its peer. The
// Lua callers are guarded in CoopHeroLua (C.DogMasterInsideBuilding); this covers the rest.
REX_EXTERN(rex_CECDog_IsMasterInsideBuilding) {
  using refii::vfs::LoadGuestU32;
  const uint32_t comp = ctx.r3.u32;
  const uint32_t block = Plausible(comp) ? LoadGuestU32(base, comp + 304u) : 0u;
  const uint32_t master = Plausible(block) ? LoadGuestU32(base, block) : 0u;
  if (!Plausible(master)) {
    static std::atomic<bool> said{false};
    if (!said.exchange(true, std::memory_order_relaxed)) {
      REXLOG_INFO("coop dogs: a dog with no master was asked if its master is inside; answered no");
    }
    ctx.r3.u64 = 0u;
    return;
  }
  __imp__rex_CECDog_IsMasterInsideBuilding(ctx, base);
}
