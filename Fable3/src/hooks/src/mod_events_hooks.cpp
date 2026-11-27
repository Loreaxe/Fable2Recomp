// refii - Fable 3's mod events: the gameplay seams, each read where the game decides, raised to the
// mods' Lua and Godot scripts with the catalog's kinds and field names (Godot/src/Engine/mods/
// Engine_ModApi.cs). Most report from the next tick (mod_lua_hooks.cpp QueueEvent). Gold, renown, an
// ability level, fast travel and a quest's start are DECIDED inside the seam before the game applies them
// (modlua::Decide), as the catalog states them: cancelled, or with their writable field rewritten.
//
// Every address is the TU3 IDB's (the disc's is bound by name, Fable3/versions/Fable3); each was
// matched to the disc's by instruction sequence, the four whose bodies differ by an identical
// prologue. Every offset names its base. A component's +4 is its owner entity (OnHeroDeath
// `lwz r10,4(r3)` then the owner's +0x94 live test, 0x8262ab70). A smart pointer's +4 is the block
// whose first word is the object (lhSmartPtr_Deref).

#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "core/payload_bridge.h"
#include "core/mod_values.h"
#include "mod_lua.h"
#include "mod_seam.h"
#include "../../vfs/include/fable3_guest_build.h"

// The per-character achievement seam, in rexruntime (achievement/achievement_runtime.h).
extern "C" uint64_t RefiiAchievementBindUser(uint32_t user_index, const char* save);

REX_EXTERN(__imp__rex_CECCreature_Die);
REX_EXTERN(__imp__rex_CECHealth_Modify);
REX_EXTERN(rex_CECHealth_GetMax);
REX_EXTERN(__imp__rex_CECMoney_Modify);
REX_EXTERN(__imp__rex_CECStats_ApplyRenown);
REX_EXTERN(__imp__rex_CECStats_GrantGuildSeals);
REX_EXTERN(__imp__rex_CECStats_SpendGuildSeals);
REX_EXTERN(__imp__rex_CECStats_RecomputeGuildSeals);
REX_EXTERN(__imp__rex_CECStats_SetAbilityLevel);
REX_EXTERN(__imp__rex_CECTreasury_Modify);
REX_EXTERN(__imp__rex_CECQuestTracker_SetAsActive);
REX_EXTERN(__imp__rex_CECQuestTracker_SetObjectiveTagSilently);
REX_EXTERN(__imp__rex_CECQuestTracker_OnQuestEnded);
REX_EXTERN(__imp__rex_WorldMap_TravelToStop);
REX_EXTERN(__imp__rex_TownDemand_Accept);
REX_EXTERN(__imp__rex_TownDemand_Reject);

namespace {

namespace modlua = refii::fable3::modlua;

// The catalog's kinds (Fable2/src/vfs/include/core/refii_mod_events.h).
constexpr int32_t kAbilityLevel = 31;
constexpr int32_t kEntityDied = 21;
constexpr int32_t kHeroDowned = 22;
constexpr int32_t kGold = 32;
constexpr int32_t kLevelLoaded = 24;
constexpr int32_t kLevelUnloading = 25;
constexpr int32_t kQuestStarted = 35;
constexpr int32_t kQuestEnded = 36;
constexpr int32_t kQuestObjective = 37;
constexpr int32_t kRenown = 42;
constexpr int32_t kFastTravel = 56;
constexpr int32_t kHealthChanged = 101;
constexpr int32_t kGuildSeals = 116;
constexpr int32_t kTreasury = 117;
constexpr int32_t kKingdomDecision = 118;

using namespace refii::fable3::seam;

/// A guest C string, bounded, as a Lua literal's body.
std::string Text(const uint8_t* base, uint32_t chars) {
  if (!Plausible(chars)) {
    return {};
  }
  size_t n = 0;
  while (n < 256u && base[chars + n] != 0) {
    ++n;
  }
  return modlua::Quoted(std::string_view(reinterpret_cast<const char*>(base + chars), n));
}

/// A CCharString's text: the string is one word, its rep's +0 the characters (sub_82225850's copy).
std::string CharString(const uint8_t* base, uint32_t string) {
  const uint32_t rep = Word(base, string);
  return Plausible(rep) ? Text(base, Word(base, rep)) : std::string();
}

/// The quests are the game's; their hero is the local player's (player 1's).
std::string QuestHero(const uint8_t* base) {
  return Fields("hero=%u,player=1", Handle(base, modlua::Hero(base, 1)));
}

/// The guild seals a stats component holds unspent: Stats.GetNumberOfGuildSeals (0x823DA230:
/// `lwz r11,0x448(r3); lwz r10,0x444(r3); add`) — earned from renown, and granted.
int32_t SealsHeld(const uint8_t* base, uint32_t stats) {
  return static_cast<int32_t>(Word(base, stats + 0x444u) + Word(base, stats + 0x448u));
}

/// The grant and spend run the recompute themselves; seals earned from renown are the recompute
/// alone, outside both.
int g_seal_depth = 0;

void RaiseSeals(const uint8_t* base, uint32_t stats, int32_t before, const char* source) {
  const int32_t after = SealsHeld(base, stats);
  if (after == before || !modlua::Wants(kGuildSeals)) {
    return;
  }
  const uint32_t entity = Word(base, stats + 4u);
  modlua::QueueEvent(kGuildSeals, Fields("entity=%u,player=%d,delta=%d,seals=%d,source='%s'", Handle(base, entity),
                                         modlua::PlayerOf(base, entity), after - before, after, source));
}

}  // namespace

namespace {
/// The zero-health dispatch's call of Die, returned to (`bl CECCreature_Die` 0x8262A8B4).
const refii::guest::Addr kDieFromHealth{0x8262A8B8u};
}  // namespace

// entity_died. CECCreature_Die (c, killer, category, hit): runs once, gated on the alive byte
// c+0x18 and c+0x28 != 8 (`lwz r9,0x28(r3); cmpwi r9,8; beq; lbz r11,0x18(r3); beq; stb r22,0x18(r3)`,
// 0x826771cc..0x826771fc); the creature is c+4. A death by damage — Die called from the zero-health
// dispatch sub_8262A560, the return address 0x8262A8B8 — is decided: the dispatch's health component is
// its r29 (`mr r29,r3` 0x8262A574, never written again in the function) and a cancel gives it the
// dispatch's own cannot-die answer, health 1.0 (`lbz r11,0x4F(r29)` then `stfs f0,0x14(r29)` of
// flt_82065440 = 1.0, 0x8262A878..8C), and skips Die; the dispatch goes on to its health packet
// (`mr r3,r29; bl CNetHealthPacket_p_ctor_p` 0x8262A8B8), as for any hit that left health above zero.
// The next hit asks again. Every other death (a script's Creature.Kill, the knockdown and response
// actions, summons) is reported.
REX_EXTERN(rex_CECCreature_Die) {
  const uint32_t creature = ctx.r3.u32;
  const uint32_t killer = ctx.r4.u32;
  const bool dies = Plausible(creature) && base[creature + 0x18u] != 0u && Word(base, creature + 0x28u) != 8u;
  const uint32_t entity = dies ? Word(base, creature + 4u) : 0u;
  if (dies && modlua::Wants(kEntityDied)) {
    const std::string fields = Fields("entity=%u,player=%d,killer=%u,killer_player=%d", Handle(base, entity),
                                      modlua::PlayerOf(base, entity), Handle(base, killer), modlua::PlayerOf(base, killer));
    const uint32_t health = ctx.r29.u32;
    if (static_cast<uint32_t>(ctx.lr) == kDieFromHealth && Plausible(health) && Word(base, health + 4u) == entity) {
      const modlua::Verdict verdict = modlua::Decide(ctx, base, kEntityDied, fields);
      if (verdict.cancelled) {
        StoreFloat(base, health + 0x14u, 1.0f);
        return;
      }
    } else {
      modlua::QueueEvent(kEntityDied, fields);
    }
  }
  __imp__rex_CECCreature_Die(ctx, base);
}

REX_EXTERN(__imp__rex_CECHealth_CheckDeath);

// hero_downed. The zero-health dispatch sub_8262A560 (r3 the health component h, owner at +4; r4 the
// killer, r5 the hit), the one caller of OnHeroDeath (0x8262A6A0) and of the damage Die (0x8262A8B4). Dead
// is health at or below 1e-4 (`lfs f13,0x14(r3)` 0x8262A570, flt_82061F20 = 1e-4, `bgt` to the alive
// path 0x8262A588). The hero branch: the owner has the creature component 24 (the lookup `cmpwi r6,0x18`
// 0x8262A604) and is a player — sub_82209238 (0x8262A670) is the world's hero holders +0x7C / +0x84
// (`addi r4,r30,0x7C` 0x82209280, `addi r4,r30,0x84` 0x82209298), the holders PlayerOf reads — then
// health = 1.0 (0x8262A690) and, unless the downed latch h+0x51 is set (`lbz r10,0x51(r29)` 0x8262A684,
// `bne` 0x8262A694), OnHeroDeath (the knockdown, the HEALTH_LOW sound, the resurrection potion, the
// revive flow), the hero's knockout statistics (component 15, sub_823DC008) and the latch.
// Decided before the dispatch, so the potion and the statistics follow the verdict. A cancel sets health
// 1.0 first: the dispatch then takes its alive path — the latch cleared (`stb r11,0x51(r29)` with 0,
// 0x8262AB4C) and the health packet — as after any hit that left the hero standing. Nothing of the
// knockdown runs; the next hit at zero asks again.
REX_EXTERN(rex_CECHealth_CheckDeath) {
  const uint32_t health = ctx.r3.u32;
  if (Plausible(health) && !(Float(base, health + 0x14u) > 1e-4f) && base[health + 0x51u] == 0u &&
      modlua::Wants(kHeroDowned)) {
    const uint32_t hero = Word(base, health + 4u);
    const int32_t player = modlua::PlayerOf(base, hero);
    if (player != 0 && Component(base, hero, 24u) != 0u) {
      const uint32_t killer = ctx.r4.u32;
      const modlua::Verdict verdict =
          modlua::Decide(ctx, base, kHeroDowned,
                         Fields("hero=%u,player=%d,killer=%u,killer_player=%d", Handle(base, hero), player,
                                Handle(base, killer), modlua::PlayerOf(base, killer)));
      if (verdict.cancelled) {
        StoreFloat(base, health + 0x14u, 1.0f);
      }
    }
  }
  __imp__rex_CECHealth_CheckDeath(ctx, base);
}

// health_changed. CECHealth_Modify (h, -, f1 delta, r5 attacker, r6 hit): health h+0x14
// (`lfs f29,0x14(r3)` at 0x82627a08), clamped to 0..max, the death dispatch inside the call, so
// the value after already carries the hero's 1.0 floor and a potion's revive. Raised when it moved.
REX_EXTERN(rex_CECHealth_Modify) {
  const uint32_t health = ctx.r3.u32;
  const uint32_t attacker = ctx.r5.u32;
  const float before = Plausible(health) ? Float(base, health + 0x14u) : 0.0f;
  const PPCContext entry = ctx;
  __imp__rex_CECHealth_Modify(ctx, base);
  if (!Plausible(health) || !modlua::Wants(kHealthChanged)) {
    return;
  }
  const float after = Float(base, health + 0x14u);
  if (after == before) {
    return;
  }
  // Health.GetMax (h) -> f1: a leaf (`lfs f0,0x20(r3); lfs f13,0x1C(r3); fadds`, clamped by +0x24).
  PPCContext max = entry;
  max.r1.u32 = (entry.r1.u32 - 0x100u) & ~0xFu;
  max.r3.u32 = health;
  rex_CECHealth_GetMax(max, base);
  const uint32_t entity = Word(base, health + 4u);
  modlua::QueueEvent(kHealthChanged,
                     Fields("entity=%u,player=%d,delta=%.9g,health=%.9g,max=%.9g,source=%u", Handle(base, entity),
                            modlua::PlayerOf(base, entity), static_cast<double>(after - before),
                            static_cast<double>(after), max.f1.f64, Handle(base, attacker)));
}

/// A Lua or Godot number as an int32: NaN 0, the range clamped (a raw cast is undefined outside it).
static int32_t ClampInt(double value) {
  if (!(value == value)) {
    return 0;
  }
  return value >= 2147483647.0 ? 2147483647 : value <= -2147483648.0 ? (-2147483647 - 1) : static_cast<int32_t>(value);
}

// gold. The purse's one writer (purse, delta, type, alert, notify, notify, replay): purse+0xC +=
// delta clamped to 0..999999999 (`lwz r26,0xC(r3)` ... `stw r23,0xC(r3)`, 0x823d26c4..0x823d2700),
// and a zero delta changes nothing (`cmpwi r4,0; beq`, 0x823d26c0). Decided before it: a cancel hands
// the writer zero, a written delta replaces r4. r9 set is a replay of the other machine's change
// (the handler TU3 sub_823D3438), decided there.
REX_EXTERN(rex_CECMoney_Modify) {
  const uint32_t purse = ctx.r3.u32;
  const bool replay = (ctx.r9.u32 & 0xFFu) != 0u;
  const int32_t delta = static_cast<int32_t>(ctx.r4.u32);
  if (Plausible(purse) && !replay && delta != 0 && modlua::Wants(kGold)) {
    const uint32_t entity = Word(base, purse + 4u);
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kGold,
        Fields("entity=%u,player=%d,gold=%d,delta=%d", Handle(base, entity), modlua::PlayerOf(base, entity),
               static_cast<int32_t>(Word(base, purse + 0xCu)), delta));
    double written = 0.0;
    if (verdict.cancelled) {
      ctx.r4.u64 = 0u;
    } else if (verdict.Read("delta", written)) {
      ctx.r4.s64 = ClampInt(written);
    }
  }
  __imp__rex_CECMoney_Modify(ctx, base);
}

// renown. The renown applier (stats, f1 delta, r5 source, r6 follower group): what every per-hero
// change funnels into — ModifyRenown's per-group recursion and the partner's share both call it —
// gated on the world byte `*(*(*(owner+8)+4)+263)` (0x823d9ca4). Renown is followers in Fable 3.
// Decided before the applier: a cancel hands it a zero delta, a written delta replaces f1.
REX_EXTERN(rex_CECStats_ApplyRenown) {
  const uint32_t stats = ctx.r3.u32;
  const double delta = ctx.f1.f64;
  const uint32_t entity = Plausible(stats) ? Word(base, stats + 4u) : 0u;
  const uint32_t world = Word(base, Word(base, entity + 8u) + 4u);
  if (delta != 0.0 && Plausible(entity) && Plausible(world) && base[world + 263u] != 0u && modlua::Wants(kRenown)) {
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kRenown,
        Fields("entity=%u,player=%d,delta=%.9g", Handle(base, entity), modlua::PlayerOf(base, entity), delta));
    double written = 0.0;
    if (verdict.cancelled) {
      ctx.f1.f64 = 0.0;
    } else if (verdict.Read("delta", written) && written == written) {
      ctx.f1.f64 = static_cast<double>(static_cast<float>(written));
    }
  }
  __imp__rex_CECStats_ApplyRenown(ctx, base);
}

// guild_seals. Granted (stats, n): stats+0x448 += n floored at 0, then the recompute
// (Lua_Stats_ModifyGuildSeals, 0x823da2c8..0x823da32c). Spent (stats, n): from +0x444 then +0x448
// (Lua_Stats_SpendGuildSeals). The recompute (stats, ...) alone adds +0x444 for each renown
// threshold passed: seals earned.
REX_EXTERN(rex_CECStats_GrantGuildSeals) {
  const uint32_t stats = ctx.r3.u32;
  const int32_t before = Plausible(stats) ? SealsHeld(base, stats) : 0;
  ++g_seal_depth;
  __imp__rex_CECStats_GrantGuildSeals(ctx, base);
  --g_seal_depth;
  if (Plausible(stats)) {
    RaiseSeals(base, stats, before, "granted");
  }
}

REX_EXTERN(rex_CECStats_SpendGuildSeals) {
  const uint32_t stats = ctx.r3.u32;
  const int32_t before = Plausible(stats) ? SealsHeld(base, stats) : 0;
  ++g_seal_depth;
  __imp__rex_CECStats_SpendGuildSeals(ctx, base);
  --g_seal_depth;
  if (Plausible(stats)) {
    RaiseSeals(base, stats, before, "spent");
  }
}

REX_EXTERN(rex_CECStats_RecomputeGuildSeals) {
  const uint32_t stats = ctx.r3.u32;
  const int32_t before = Plausible(stats) ? SealsHeld(base, stats) : 0;
  __imp__rex_CECStats_RecomputeGuildSeals(ctx, base);
  if (g_seal_depth == 0 && Plausible(stats)) {
    RaiseSeals(base, stats, before, "earned");
  }
}

// ability_level. Stats.SetHeroAbilityLevel's method (stats, type, level, send): the level at
// stats + 0x90 + 4*type (`addi r10,r21,0x24; slwi r29,r10,2` ... `stwx`, 0x823e6974), clamped to
// the ability's maximum.
// Decided before it, for a level that changes: a cancel hands it the level it has, a written level
// replaces r5.
REX_EXTERN(rex_CECStats_SetAbilityLevel) {
  const uint32_t stats = ctx.r3.u32;
  const uint32_t type = ctx.r4.u32;
  const int32_t level = static_cast<int32_t>(ctx.r5.u32);
  const bool known = Plausible(stats) && type < 64u;
  const int32_t before = known ? static_cast<int32_t>(Word(base, stats + 0x90u + 4u * type)) : 0;
  if (known && level != before && modlua::Wants(kAbilityLevel)) {
    const uint32_t entity = Word(base, stats + 4u);
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kAbilityLevel,
        Fields("entity=%u,player=%d,type=%u,level=%d,old_level=%d", Handle(base, entity),
               modlua::PlayerOf(base, entity), type, level, before));
    double written = 0.0;
    if (verdict.cancelled) {
      ctx.r5.s64 = before;
    } else if (verdict.Read("level", written)) {
      ctx.r5.s64 = ClampInt(written);
    }
  }
  __imp__rex_CECStats_SetAbilityLevel(ctx, base);
}

// treasury. The kingdom treasury's writer (t, delta, type, popup): t+0xC += delta clamped to
// ±999999999 (0x823d5938 / 0x823d5968). Treasury.Set writes +0xC itself and is not seen.
REX_EXTERN(rex_CECTreasury_Modify) {
  const uint32_t treasury = ctx.r3.u32;
  const int32_t type = static_cast<int32_t>(ctx.r5.u32);
  const int32_t before = Plausible(treasury) ? static_cast<int32_t>(Word(base, treasury + 0xCu)) : 0;
  __imp__rex_CECTreasury_Modify(ctx, base);
  if (!Plausible(treasury) || !modlua::Wants(kTreasury)) {
    return;
  }
  const int32_t after = static_cast<int32_t>(Word(base, treasury + 0xCu));
  if (after != before) {
    modlua::QueueEvent(kTreasury, Fields("treasury=%d,delta=%d,type=%d", after, after - before, type));
  }
}

// quest_started. QuestTracker.SetAsActive's method (tracker, name, -, make_primary): the record
// found by name gets +13 active and +14 activated (0x8250cd98 / 0x8250cd9c) on every call. Decided
// before it: a cancel leaves the quest as it was (the call not made).
REX_EXTERN(rex_CECQuestTracker_SetAsActive) {
  const std::string quest = Text(base, ctx.r4.u32);
  if (!quest.empty() && modlua::Wants(kQuestStarted)) {
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kQuestStarted,
        Fields("quest='%s',%s,make_primary=%s", quest.c_str(), QuestHero(base).c_str(),
               (ctx.r6.u32 & 0xFFu) != 0u ? "true" : "false"));
    if (verdict.cancelled) {
      return;
    }
  }
  __imp__rex_CECQuestTracker_SetAsActive(ctx, base);
}

REX_EXTERN(__imp__rex_CECQuestTracker_SetObjectiveTag);
REX_EXTERN(__imp__rex_CECQuestTracker_SetObjectiveTagFinal);

namespace {
/// Inside one of the three objective calls: the outermost decided it.
int g_objective_depth = 0;

/// quest_objective for (r3 tracker, r4 quest name, r5 tag), decided: true when it was cancelled. The
/// record is found by the name's FNV-1 (sub_821B0F48: `lbz r11,0(r4)` 0x821B0F48 ... 0x811C9DC5).
bool ObjectiveCancelled(PPCContext& ctx, uint8_t* base) {
  if (g_objective_depth != 0 || !modlua::Wants(kQuestObjective)) {
    return false;
  }
  const std::string quest = Text(base, ctx.r4.u32);
  if (quest.empty()) {
    return false;
  }
  const std::string tag = Text(base, ctx.r5.u32);
  return modlua::Decide(ctx, base, kQuestObjective, Fields("quest='%s',tag='%s',%s", quest.c_str(), tag.c_str(), QuestHero(base).c_str()))
      .cancelled;
}
}  // namespace

// quest_objective. Three calls add an objective, each the outer of the next: SetObjectiveTagFinal
// (0x8250F158: `bl Lua_QuestTracker_SetObjectiveTag` 0x8250F170, then the final tag onto the record's
// +0xA8) -> SetObjectiveTag (0x8250F0E8: `bl Lua_QuestTracker_SetObjectiveTagSilently` 0x8250F114, then
// the objective notice, sub_824D1570 and sub_8250F568 0x8250F14C) -> SetObjectiveTagSilently (0x8250F1D0:
// the tag onto the record's +0x80 list, the tracker's dirty byte +0x2F). The quest's default objective
// is Silently alone (SetAsActive, `bl` 0x8250CE08). The outermost one is decided, so a cancel skips the
// tag, its notice and its final mark together. Every caller drops r3: the Lua glue returns 0 after the
// call, Final and SetObjectiveTag go on to their own work, SetAsActive to a destructor (`addi r3,r1,..`
// 0x8250CE0C).
REX_EXTERN(rex_CECQuestTracker_SetObjectiveTagFinal) {
  if (ObjectiveCancelled(ctx, base)) {
    return;
  }
  ++g_objective_depth;
  __imp__rex_CECQuestTracker_SetObjectiveTagFinal(ctx, base);
  --g_objective_depth;
}

REX_EXTERN(rex_CECQuestTracker_SetObjectiveTag) {
  if (ObjectiveCancelled(ctx, base)) {
    return;
  }
  ++g_objective_depth;
  __imp__rex_CECQuestTracker_SetObjectiveTag(ctx, base);
  --g_objective_depth;
}

REX_EXTERN(rex_CECQuestTracker_SetObjectiveTagSilently) {
  if (ObjectiveCancelled(ctx, base)) {
    return;
  }
  ++g_objective_depth;
  __imp__rex_CECQuestTracker_SetObjectiveTagSilently(ctx, base);
  --g_objective_depth;
}

// quest_ended. (tracker, record): reached only from SetAsCompleted (`stb r29,0x15(r3)` before the
// call, 0x8250eaec) and SetAsFailed (`stb r30,0x16(r3)`, 0x8250ed8c); the record's +0 is its name.
REX_EXTERN(rex_CECQuestTracker_OnQuestEnded) {
  const uint32_t record = ctx.r4.u32;
  const std::string quest = Plausible(record) ? CharString(base, record) : std::string();
  const bool completed = Plausible(record) && base[record + 0x15u] != 0u;
  const bool failed = Plausible(record) && base[record + 0x16u] != 0u;
  __imp__rex_CECQuestTracker_OnQuestEnded(ctx, base);
  if (!quest.empty() && modlua::Wants(kQuestEnded)) {
    modlua::QueueEvent(kQuestEnded, Fields("quest='%s',%s,completed=%s,failed=%s", quest.c_str(),
                                           QuestHero(base).c_str(), completed ? "true" : "false",
                                           failed ? "true" : "false"));
  }
}

// fast_travel. (map, CCharString* level, CCharString* stop, paid): both strings handed on to the
// level load (its constructor 0x82755D10 keeps them at +24 / +28); 1 when the travel was issued.
// Decided before it: a cancel refuses it (0), as the game's own refusal does.
REX_EXTERN(rex_WorldMap_TravelToStop) {
  if (modlua::Wants(kFastTravel)) {
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kFastTravel,
        Fields("level='%s',stop='%s',paid=%s,player=1", CharString(base, ctx.r4.u32).c_str(),
               CharString(base, ctx.r5.u32).c_str(), (ctx.r6.u32 & 0xFFu) != 0u ? "true" : "false"));
    if (verdict.cancelled) {
      ctx.r3.u64 = 0u;
      return;
    }
  }
  __imp__rex_WorldMap_TravelToStop(ctx, base);
}

// kingdom_decision. A town demand accepted or rejected (demand): its save writer lays out +0 id,
// +12 province, +16 level; 1 when the decision was taken (accept refuses an unaffordable demand).
static void RaiseDecision(const uint8_t* base, uint32_t demand, bool accepted, uint32_t taken) {
  if ((taken & 0xFFu) == 0u || !Plausible(demand) || !modlua::Wants(kKingdomDecision)) {
    return;
  }
  modlua::QueueEvent(kKingdomDecision, Fields("demand=%u,province=%u,level=%u,accepted=%s", Word(base, demand),
                                              Word(base, demand + 12u), Word(base, demand + 16u),
                                              accepted ? "true" : "false"));
}

REX_EXTERN(rex_TownDemand_Accept) {
  const uint32_t demand = ctx.r3.u32;
  __imp__rex_TownDemand_Accept(ctx, base);
  RaiseDecision(base, demand, true, ctx.r3.u32);
}

REX_EXTERN(rex_TownDemand_Reject) {
  const uint32_t demand = ctx.r3.u32;
  __imp__rex_TownDemand_Reject(ctx, base);
  RaiseDecision(base, demand, false, ctx.r3.u32);
}

namespace refii::fable3::modlua {

// level_loaded / level_unloading, from world_hooks.cpp's level manager hooks: the level is
// `*(*(mgr + 0x28) + 4)` (the load stores its holder at mgr+0x28, 0x82350590), its name the
// CCharString at level+0xAC (its constructor's `addi r29,r3,0xAC`, 0x8245c454).
void NoteLevel(const uint8_t* base, uint32_t manager, bool loaded) {
  const int32_t kind = loaded ? kLevelLoaded : kLevelUnloading;
  if (!Plausible(manager) || !Wants(kind)) {
    return;
  }
  const uint32_t level = Word(base, Word(base, manager + 0x28u) + 4u);
  if (!Plausible(level)) {
    return;
  }
  QueueEvent(kind, Fields("level='%s'", CharString(base, level + 0xACu).c_str()));
}

}  // namespace refii::fable3::modlua

// ---------------------------------------------------------------------------------------------
// The world batch: experience, items, expressions, morality and purity, the clock, travel, crime,
// the pause and the saves (each pair matched by the TU3 IDB's "disc 0x..." comment and both
// prologues read: the registers named are the bodies' own `mr` of them).

REX_EXTERN(__imp__rex_CECExperience_Modify);
REX_EXTERN(__imp__rex_CECExperience_SpendAmounts);
REX_EXTERN(__imp__rex_lhSaveManager_WriteSave);
REX_EXTERN(__imp__rex_lhSaveManager_WritePackage);
REX_EXTERN(__imp__rex_lhSaveManager_WriteHeroPackage);
REX_EXTERN(__imp__rex_lhSaveManager_WritePlayerTwo);
REX_EXTERN(__imp__rex_CECInventory_AddOneOfRecord);
REX_EXTERN(__imp__rex_lhEntity_UseInventoryItem);
REX_EXTERN(__imp__rex_CECExpressionPerformer_Start);
REX_EXTERN(__imp__rex_CECStats_ModifyMorality);
REX_EXTERN(__imp__rex_CECStats_ModifyPrimal);
REX_EXTERN(__imp__rex_lhGameTimeManager_Update);
REX_EXTERN(__imp__rex_CGameModeLoadLevel_Start);
REX_EXTERN(__imp__rex_CECCrime_Add);
REX_EXTERN(__imp__rex_CGameModePause_Apply);
REX_EXTERN(__imp__rex_CGameModePause_Remove);

namespace {

constexpr int32_t kExperience = 20;
constexpr int32_t kGameSaving = 26;
constexpr int32_t kItemAdded = 27;
constexpr int32_t kItemUsed = 28;
constexpr int32_t kExpressionStarted = 41;
constexpr int32_t kMorality = 43;
constexpr int32_t kPurity = 44;
constexpr int32_t kTimeOfDay = 52;
constexpr int32_t kTravel = 55;
constexpr int32_t kCrime = 64;
constexpr int32_t kPaused = 98;
constexpr int32_t kSaveWritten = 103;

/// Inside SpendExperienceAmounts: its Modify calls are spends of the cost it was handed.
int g_experience_spend = 0;
/// Inside player two's package write (it looks player two up, `li r4,2`, then writes the hero package).
int g_saving_player_two = 0;

/// The GUI manager (`dword_83596F84`): its +0x38 holds the screen word at +4 (SetScreen `stw r28,4(r10)`
/// 0x825F9988), 32 when no screen is up (ExitScreen `stw r26,4(r11)` 0x825F9DBC, r26 = 32).
const refii::guest::Addr kGuiManager{0x83596F84u};
constexpr int kNoScreen = 32;

int32_t ScreenNow(const uint8_t* base) {
  const uint32_t holder = Word(base, Word(base, kGuiManager) + 0x38u);
  return Plausible(holder) ? static_cast<int32_t>(Word(base, holder + 4u)) : kNoScreen;
}

/// A decided float delta: cancelled, or the written `field` in place of f1.
bool DecideDelta(PPCContext& ctx, uint8_t* base, int32_t kind, uint32_t entity, const char* field) {
  const modlua::Verdict verdict =
      modlua::Decide(ctx, base, kind,
                     Fields("entity=%u,player=%d,delta=%.9g", Handle(base, entity), modlua::PlayerOf(base, entity), ctx.f1.f64));
  if (verdict.cancelled) {
    return false;
  }
  double written = 0.0;
  if (verdict.Read(field, written) && written == written) {
    ctx.f1.f64 = static_cast<double>(static_cast<float>(written));
  }
  return true;
}

}  // namespace

// experience. Experience.Modify (r3 component 189, owner at +4; r4 pool; f1 amount: the pool float at
// `*(c+0x10)+4*pool` += amount, floored at 0). Decided before it: a cancel leaves the pools as they were
// (the co-op net message is not sent either), a written amount replaces f1 — for a spend, the cost.
REX_EXTERN(rex_CECExperience_Modify) {
  const uint32_t component = ctx.r3.u32;
  const uint32_t entity = Plausible(component) ? Word(base, component + 4u) : 0u;
  if (Live(base, entity) && modlua::Wants(kExperience)) {
    const bool spend = g_experience_spend > 0;
    const double amount = spend ? -ctx.f1.f64 : ctx.f1.f64;
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kExperience,
        Fields("entity=%u,player=%d,pool=%d,amount=%.9g,spend=%s,nested=%s", Handle(base, entity), modlua::PlayerOf(base, entity),
               static_cast<int32_t>(ctx.r4.u32), amount, spend ? "true" : "false", "false"));
    if (verdict.cancelled) {
      return;
    }
    double written = 0.0;
    if (verdict.Read("amount", written) && written == written) {
      ctx.f1.f64 = static_cast<double>(static_cast<float>(spend ? -written : written));
    }
  }
  __imp__rex_CECExperience_Modify(ctx, base);
}

// SpendExperienceAmounts (r3 component, r4 a vector of six costs): each positive cost paid as
// Modify(c, pool, -cost) (0x823fac68).
REX_EXTERN(rex_CECExperience_SpendAmounts) {
  ++g_experience_spend;
  __imp__rex_CECExperience_SpendAmounts(ctx, base);
  --g_experience_spend;
}

// item_added. AddOneOfRecord (r3 the out smart pointer, its +0 the item; r4 the inventory, owner at +4;
// r5 the record; r6 notify): a zero record adds nothing (`cmplwi r5,0; beq` 0x8241aaa8). After it,
// when an item came back.
REX_EXTERN(rex_CECInventory_AddOneOfRecord) {
  const uint32_t out = ctx.r3.u32;
  const uint32_t inventory = ctx.r4.u32;
  const uint32_t record = ctx.r5.u32;
  const bool notify = (ctx.r6.u32 & 0xFFu) != 0u;
  __imp__rex_CECInventory_AddOneOfRecord(ctx, base);
  if (record == 0u || !Plausible(out) || Word(base, out) == 0u || !modlua::Wants(kItemAdded)) {
    return;
  }
  const uint32_t entity = Word(base, inventory + 4u);
  if (Live(base, entity)) {
    modlua::QueueEvent(kItemAdded, Fields("entity=%u,player=%d,definition='0x%08x',notify=%s", Handle(base, entity),
                                          modlua::PlayerOf(base, entity), record, notify ? "true" : "false"));
  }
}

// item_used. lhEntity_UseInventoryItem (r3 the user; r4 the item smart pointer, the item at +0, its
// record at item+4 and count at +0xC; r5 a script override). Decided before it: a cancel answers 0,
// the function's own refusal (`beq loc_8242F264` when *r4 is 0). The resurrection potion is used
// through it inside the hero's death, so a cancel there refuses the revive.
REX_EXTERN(rex_lhEntity_UseInventoryItem) {
  const uint32_t entity = ctx.r3.u32;
  const uint32_t item = Plausible(ctx.r4.u32) ? Word(base, ctx.r4.u32) : 0u;
  if (Live(base, entity) && Plausible(item) && modlua::Wants(kItemUsed)) {
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kItemUsed,
        Fields("entity=%u,player=%d,item=%u,definition='0x%08x',count=%d", Handle(base, entity), modlua::PlayerOf(base, entity),
               ObjectHandle(item), Word(base, item + 4u), static_cast<int32_t>(Word(base, item + 0xCu))));
    if (verdict.cancelled) {
      ctx.r3.u64 = 0u;
      return;
    }
  }
  __imp__rex_lhEntity_UseInventoryItem(ctx, base);
}

// expression_started. The expression performer's start (r3 component 177, owner at +4; r4 {+0 the
// expression, +4 its record}; returns 1 started, 0 refused — `lwz r11,0(r4); cmplwi; beq -> li r3,0`
// 0x8264a91c). Decided before it: a cancel answers 0, the game's own refusal, before anything is written.
REX_EXTERN(rex_CECExpressionPerformer_Start) {
  const uint32_t performer = ctx.r3.u32;
  const uint32_t entity = Plausible(performer) ? Word(base, performer + 4u) : 0u;
  const uint32_t expression = Plausible(ctx.r4.u32) ? Word(base, ctx.r4.u32) : 0u;
  if (expression != 0u && Live(base, entity) && modlua::Wants(kExpressionStarted)) {
    const modlua::Verdict verdict =
        modlua::Decide(ctx, base, kExpressionStarted,
                       Fields("entity=%u,player=%d,expression='0x%08x'", Handle(base, entity), modlua::PlayerOf(base, entity), expression));
    if (verdict.cancelled) {
      ctx.r3.u64 = 0u;
      return;
    }
  }
  __imp__rex_CECExpressionPerformer_Start(ctx, base);
}

// morality / purity. The stats component's ModifyMorality and ModifyPrimal (r3 stats 15, owner at +4;
// f1 delta: `lfs f31,0x368(r26); fadds; stfs` 0x823d8d30 / `lfs f13,0x374(r30)` 0x823da880, each clamped).
// Fable 3's Primal is Fable 2's purity (its callers read PurityBalancing / HeroChildPurityMultiplier).
// Decided before: a cancel skips the call (the stat, its tracked stats and unlocks untouched), a
// written delta replaces f1.
REX_EXTERN(rex_CECStats_ModifyMorality) {
  const uint32_t entity = Plausible(ctx.r3.u32) ? Word(base, ctx.r3.u32 + 4u) : 0u;
  if (ctx.f1.f64 != 0.0 && Live(base, entity) && modlua::Wants(kMorality) && !DecideDelta(ctx, base, kMorality, entity, "delta")) {
    return;
  }
  __imp__rex_CECStats_ModifyMorality(ctx, base);
}

REX_EXTERN(rex_CECStats_ModifyPrimal) {
  const uint32_t entity = Plausible(ctx.r3.u32) ? Word(base, ctx.r3.u32 + 4u) : 0u;
  if (ctx.f1.f64 != 0.0 && Live(base, entity) && modlua::Wants(kPurity) && !DecideDelta(ctx, base, kPurity, entity, "delta")) {
    return;
  }
  __imp__rex_CECStats_ModifyPrimal(ctx, base);
}

namespace {
int32_t g_last_hour = -1;
int32_t g_last_day = -1;
}  // namespace

// time_of_day. The game time manager's Update (r3 manager, f1 seconds): the day fraction at +4, the day
// count at +8 (`lwz r11,8(r31); addi r9,r11,1; stw r9,8(r31)` 0x821ab9e8); the hour as the function
// itself states SV_TIME_OF_DAY, +4 × 24 (`fmuls f1,f13,f0` 0x821aba2c). After it, when the hour or the
// day changed; the first call primes.
REX_EXTERN(rex_lhGameTimeManager_Update) {
  const uint32_t manager = ctx.r3.u32;
  __imp__rex_lhGameTimeManager_Update(ctx, base);
  if (!Plausible(manager)) {
    return;
  }
  const float time = Float(base, manager + 4u);
  const int32_t day = static_cast<int32_t>(Word(base, manager + 8u));
  int32_t hour = static_cast<int32_t>(time * 24.0f);
  hour = hour < 0 ? 0 : hour > 23 ? 23 : hour;
  if (hour == g_last_hour && day == g_last_day) {
    return;
  }
  const bool primed = g_last_hour >= 0;
  const bool new_day = primed && day != g_last_day;
  g_last_hour = hour;
  g_last_day = day;
  if (primed && modlua::Wants(kTimeOfDay)) {
    modlua::QueueEvent(kTimeOfDay, Fields("hour=%d,day=%d,time=%.9g,new_day=%s", hour, day, Fin(time), new_day ? "true" : "false"));
  }
}

// travel. The load-level game mode's Start (r3 the mode: world, level and entrance CCharStrings at
// +0x14/+0x18/+0x1C as its constructor copies them, the type at +0x24, and +0x3A the full-load byte
// Start writes, `stb r3,0x3A(r31)` 0x82756090). After it; the traveller is player 1.
REX_EXTERN(rex_CGameModeLoadLevel_Start) {
  const uint32_t mode = ctx.r3.u32;
  __imp__rex_CGameModeLoadLevel_Start(ctx, base);
  if (!Plausible(mode) || !modlua::Wants(kTravel)) {
    return;
  }
  modlua::QueueEvent(kTravel, Fields("world='%s',level='%s',entrance='%s',type=%d,full_load=%s,player=%d",
                                     CharString(base, mode + 0x14u).c_str(), CharString(base, mode + 0x18u).c_str(),
                                     CharString(base, mode + 0x1Cu).c_str(), static_cast<int32_t>(Word(base, mode + 0x24u)),
                                     base[mode + 0x3Au] != 0u ? "true" : "false", 1));
}

// crime. CECCrime_Add (r3 the crime component; r4 the criminal, r5 the victim, r6 the type; returns 1
// recorded, 0 refused). Decided before it: a cancel answers 0, the body's own refusal. Fable 3's types:
// 0 attempted murder, 1 murder, 2 drunk and disorderly, 3 extortion, 4 harassment, 5 parole violation,
// 6 resisting arrest, 7 theft, 8 trespass, 9 vagrancy, 10 vandalism.
REX_EXTERN(rex_CECCrime_Add) {
  const uint32_t criminal = ctx.r4.u32;
  const uint32_t victim = ctx.r5.u32;
  if (Live(base, criminal) && modlua::Wants(kCrime)) {
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kCrime,
        Fields("criminal=%u,player=%d,victim=%u,victim_player=%d,type=%d", Handle(base, criminal), modlua::PlayerOf(base, criminal),
               Handle(base, victim), modlua::PlayerOf(base, victim), static_cast<int32_t>(ctx.r6.u32)));
    if (verdict.cancelled) {
      ctx.r3.u64 = 0u;
      return;
    }
  }
  __imp__rex_CECCrime_Add(ctx, base);
}

namespace {

/// paused, after the pause mode's apply or remove: the mode's id is its vtable slot 1 (1 the GUI pause,
/// 2 the load pause, 12 the world and town maps).
void RaisePaused(PPCContext& ctx, uint8_t* base, uint32_t mode, bool paused) {
  if (!Plausible(mode) || !modlua::Wants(kPaused)) {
    return;
  }
  PPCContext work = Frame(ctx);
  const int32_t id = CallSlot(work, base, mode, 1u) ? static_cast<int32_t>(work.r3.u32) : -1;
  const int32_t screen = ScreenNow(base);
  modlua::QueueEvent(kPaused, Fields("paused=%s,mode=%d,player=%d,screen=%d", paused ? "true" : "false", id,
                                     id == 1 && screen != kNoScreen ? 1 : 0, screen));
}

}  // namespace

// paused. CGameModePause's slot 5 (apply) and slot 8 (remove), which the load pause and the map modes
// share (r3 the mode).
REX_EXTERN(rex_CGameModePause_Apply) {
  const uint32_t mode = ctx.r3.u32;
  __imp__rex_CGameModePause_Apply(ctx, base);
  RaisePaused(ctx, base, mode, true);
}

REX_EXTERN(rex_CGameModePause_Remove) {
  const uint32_t mode = ctx.r3.u32;
  __imp__rex_CGameModePause_Remove(ctx, base);
  RaisePaused(ctx, base, mode, false);
}

// game_saving. The save manager's WriteSave (r3 manager, r4 CCharString* the file), before it.
REX_EXTERN(rex_lhSaveManager_WriteSave) {
  if (modlua::Wants(kGameSaving)) {
    modlua::QueueEvent(kGameSaving, Fields("file='%s'", CharString(base, ctx.r4.u32).c_str()));
  }
  __imp__rex_lhSaveManager_WriteSave(ctx, base);
}

namespace {

/// REFII_MOD_HOST_SAVE_KIND (Fable 2's core/refii_mod_events.h): a written package, `player=N,name='X'`;
/// the host stamps its sidecar (refii_save.json) with the mod set it was written under.
constexpr int kHostSaveKind = 1004;

/// After a package write, as Fable 2's player-state hooks do: the player's saved mod values go beside
/// it (refii_player.txt in the package's folder, the running title's savegame root), the host stamps
/// it, then save_written is raised.
void RaiseSaveWritten(uint8_t* base, uint32_t name, bool ok) {
  const int32_t player = g_saving_player_two > 0 ? 2 : 1;
  const std::string package = CharString(base, name);
  if (ok && !package.empty()) {
    refii_bridge_player_write_saved(player, package.c_str());
    std::string fields = "player=" + std::to_string(player) + ",name='";
    for (const char c : package) {
      if (c != '\'' && c != '\\' && c != '\n' && c != ',') {
        fields += c;
      }
    }
    refii::mods::PostEvent(kHostSaveKind, fields + "'");
  }
  if (modlua::Wants(kSaveWritten)) {
    modlua::QueueEvent(kSaveWritten, Fields("player=%d,save='%s',ok=%s", player, package.c_str(), ok ? "true" : "false"));
  }
}

}  // namespace

// save_written. The full package writer and the hero-only one (r3 manager, r4 CCharString* the
// package; r3 1 written, 0 failed), after each; inside player two's bracket (sub_822F9108, `li r4,2`)
// the hero package is player two's.
REX_EXTERN(rex_lhSaveManager_WritePackage) {
  const uint32_t name = ctx.r4.u32;
  __imp__rex_lhSaveManager_WritePackage(ctx, base);
  RaiseSaveWritten(base, name, (ctx.r3.u32 & 0xFFu) != 0u);
}

// Inside player two's bracket the hero package goes to the manager's +4 (sub_822F9108: `addi r29,r3,4`
// 0x822F9128, `mr r4,r29` + `bl sub_822F9608` 0x822F91A8..0x822F91B4); one save set holds both players'
// heroes, so player two's hero is never written into the save player one plays (the manager's +0,
// sub_822F8940 `sub_82228298((int *)a1, v26)` 0x822F8BCC): r3 = 0, the writer's "not written".
REX_EXTERN(rex_lhSaveManager_WriteHeroPackage) {
  const uint32_t name = ctx.r4.u32;
  if (g_saving_player_two > 0) {
    const std::string target = CharString(base, name);
    if (!target.empty() && target == CharString(base, ctx.r3.u32)) {
      REXLOG_WARN("Refii saves: player two's hero NOT written into '{}', the save player one plays", target);
      ctx.r3.u64 = 0u;
      RaiseSaveWritten(base, name, false);
      return;
    }
  }
  __imp__rex_lhSaveManager_WriteHeroPackage(ctx, base);
  RaiseSaveWritten(base, name, (ctx.r3.u32 & 0xFFu) != 0u);
}

REX_EXTERN(rex_lhSaveManager_WritePlayerTwo) {
  ++g_saving_player_two;
  __imp__rex_lhSaveManager_WritePlayerTwo(ctx, base);
  --g_saving_player_two;
}

// ---------------------------------------------------------------------------------------------
// Opinions, the weather, the bed, collectables, gifts and following.

REX_EXTERN(__imp__rex_CECOpinionReaction_OnDeed);
REX_EXTERN(__imp__rex_CECOpinionReaction_ModifyAxisValue);
REX_EXTERN(__imp__rex_EnvironmentThemeManager_BlendToTheme);
REX_EXTERN(__imp__rex_CECBed_DisplayInteractionMenu);
REX_EXTERN(__imp__rex_CECOneTimeCollectable_SetStatus);
REX_EXTERN(__imp__rex_CECPlayer_GiveGiftFromInventoryItem);
REX_EXTERN(__imp__rex_CECFollow_Start);
REX_EXTERN(__imp__rex_CECFollow_Stop);

namespace {

constexpr int32_t kOpinionDeed = 50;
constexpr int32_t kOpinionModified = 51;
constexpr int32_t kWeatherTheme = 53;
constexpr int32_t kSleepMenu = 54;
constexpr int32_t kCollectableTaken = 66;
constexpr int32_t kGiftGiven = 76;
constexpr int32_t kFollow = 79;

/// Player 2's hero (the world's +0x84 holder); a hero that is not is player 1's.
int32_t HeroPlayer(const uint8_t* base, uint32_t hero) { return hero != 0u && hero == modlua::Hero(base, 2) ? 2 : 1; }

}  // namespace

// opinion_deed. The opinion component's OnDeed (r3 component 55, the villager at +4; r4 the deed
// message: the doer's smart pointer at +0x48, the deed at +0x20 — `lwz r11,0x20(r27)` 0x828fd5c4 —
// its kind at +0x34). Decided before it: a cancel skips it, so the villager ignores the deed.
REX_EXTERN(rex_CECOpinionReaction_OnDeed) {
  const uint32_t villager = Plausible(ctx.r3.u32) ? Word(base, ctx.r3.u32 + 4u) : 0u;
  const uint32_t message = ctx.r4.u32;
  if (Live(base, villager) && Plausible(message) && modlua::Wants(kOpinionDeed)) {
    const uint32_t doer = Deref(base, message + 0x48u);
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kOpinionDeed,
        Fields("villager=%u,doer=%u,doer_player=%d,deed='0x%08x',kind=%d", Handle(base, villager), Handle(base, doer),
               modlua::PlayerOf(base, doer), Word(base, message + 0x20u), static_cast<int32_t>(Word(base, message + 0x34u))));
    if (verdict.cancelled) {
      return;
    }
  }
  __imp__rex_CECOpinionReaction_OnDeed(ctx, base);
}

// opinion_modified. OpinionReaction.ModifyAxisValue (r3 the opinion component, villager at +4; r4 the
// hero whose opinion record it is — Fable 3 keeps one per hero; r5 the axis 0 love .. 4 respect; f1 the
// delta, added and clamped to [-1, 1]). Decided before: a cancel skips it, a written delta replaces f1.
REX_EXTERN(rex_CECOpinionReaction_ModifyAxisValue) {
  const uint32_t villager = Plausible(ctx.r3.u32) ? Word(base, ctx.r3.u32 + 4u) : 0u;
  const uint32_t hero = ctx.r4.u32;
  if (Live(base, villager) && Live(base, hero) && ctx.f1.f64 != 0.0 && modlua::Wants(kOpinionModified)) {
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kOpinionModified,
        Fields("villager=%u,axis=%d,delta=%.9g,player=%d", Handle(base, villager), static_cast<int32_t>(ctx.r5.u32), ctx.f1.f64,
               modlua::PlayerOf(base, hero)));
    if (verdict.cancelled) {
      return;
    }
    double written = 0.0;
    if (verdict.Read("delta", written) && written == written) {
      ctx.f1.f64 = static_cast<double>(static_cast<float>(written));
    }
  }
  __imp__rex_CECOpinionReaction_ModifyAxisValue(ctx, base);
}

// weather_theme. The theme manager's BlendToTheme (r3 manager, r4 CCharString* the theme, r5 the layer,
// f1 weight, f2 seconds). Decided before it: a cancel skips the blend (nothing is taken before the
// call), a written weight or seconds replaces f1 / f2. Refusing a network arrival (layer 3) leaves the
// other machine's sky apart.
REX_EXTERN(rex_EnvironmentThemeManager_BlendToTheme) {
  if (Plausible(ctx.r4.u32) && modlua::Wants(kWeatherTheme)) {
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kWeatherTheme,
        Fields("theme='%s',weight=%.9g,seconds=%.9g", CharString(base, ctx.r4.u32).c_str(), ctx.f1.f64, ctx.f2.f64));
    if (verdict.cancelled) {
      return;
    }
    double written = 0.0;
    if (verdict.Read("weight", written) && written == written) {
      ctx.f1.f64 = static_cast<double>(static_cast<float>(written));
    }
    if (verdict.Read("seconds", written) && written == written && written >= 0.0) {
      ctx.f2.f64 = static_cast<double>(static_cast<float>(written));
    }
  }
  __imp__rex_EnvironmentThemeManager_BlendToTheme(ctx, base);
}

// sleep_menu. The bed's interaction menu (r3 the bed component 207, r4 the user). Decided before it: a
// cancel skips it, so the bed's waiting byte (+0xD) is never set and the bed script reads SLEEP_NONE and
// lets the player go.
REX_EXTERN(rex_CECBed_DisplayInteractionMenu) {
  const uint32_t user = ctx.r4.u32;
  if (Live(base, user) && modlua::Wants(kSleepMenu)) {
    const modlua::Verdict verdict =
        modlua::Decide(ctx, base, kSleepMenu, Fields("seen=true,player=%d", modlua::PlayerOf(base, user)));
    if (verdict.cancelled) {
      return;
    }
  }
  __imp__rex_CECBed_DisplayInteractionMenu(ctx, base);
}

namespace {

/// collectable_taken from a path that writes the hero's status word itself (the gnome capture, the
/// pickup): `component` the collectable's component 84, `slot` the hero's word, `before` its value then.
void RaiseCollectable(const uint8_t* base, uint32_t component, int32_t player, uint32_t before) {
  const uint32_t slot = component + (player == 2 ? 0x10u : 0xCu);
  const uint32_t after = Word(base, slot);
  if (before != 0u || (after != 1u && after != 2u) || !modlua::Wants(kCollectableTaken)) {
    return;
  }
  const uint32_t entity = Word(base, component + 4u);
  const uint32_t record = Plausible(entity) && Word(base, entity + 0x1Cu) == 0u ? Word(base, entity + 0x20u) : 0u;
  const uint32_t other = component + (player == 2 ? 0xCu : 0x10u);
  modlua::QueueEvent(kCollectableTaken, Fields("player=%d,entity=%u,record='0x%08x',stays=%s", player, Handle(base, entity), record,
                                               Word(base, other) == 0u ? "true" : "false"));
}

}  // namespace

// collectable_taken. A one-time collectable's SetCurrentStatus (r3 component 84, the collectable at +4;
// r4 the new status; r5 the hero): each hero's status is its own word, player one's +0xC, player two's
// +0x10. Taken when that word goes from 0 to 1 or 2, the game's own collection test (sub_829A12D8).
REX_EXTERN(rex_CECOneTimeCollectable_SetStatus) {
  const uint32_t component = ctx.r3.u32;
  const int32_t player = HeroPlayer(base, ctx.r5.u32);
  const uint32_t slot = component + (player == 2 ? 0x10u : 0xCu);
  const uint32_t before = Plausible(component) ? Word(base, slot) : 1u;
  const uint32_t status = ctx.r4.u32;
  __imp__rex_CECOneTimeCollectable_SetStatus(ctx, base);
  if (before != 0u || (status != 1u && status != 2u) || !modlua::Wants(kCollectableTaken)) {
    return;
  }
  const uint32_t entity = Word(base, component + 4u);
  const uint32_t record = Plausible(entity) && Word(base, entity + 0x1Cu) == 0u ? Word(base, entity + 0x20u) : 0u;
  const uint32_t other = component + (player == 2 ? 0xCu : 0x10u);
  modlua::QueueEvent(kCollectableTaken, Fields("player=%d,entity=%u,record='0x%08x',stays=%s", player, Handle(base, entity), record,
                                               Word(base, other) == 0u ? "true" : "false"));
}

// gift_given. Player.GiveGiftFromInventoryItem (r3 the player component 37, the hero at +4; r4 the item's
// smart pointer, its record at item+4): the receiver is the hero's target (targeting 98, slot 21).
// Decided before it: a cancel skips it — nothing is given; its own refusals return before any write.
REX_EXTERN(rex_CECPlayer_GiveGiftFromInventoryItem) {
  const uint32_t hero = Plausible(ctx.r3.u32) ? Word(base, ctx.r3.u32 + 4u) : 0u;
  const uint32_t item = Plausible(ctx.r4.u32) ? Word(base, ctx.r4.u32) : 0u;
  if (Live(base, hero) && Plausible(item) && modlua::Wants(kGiftGiven)) {
    const uint32_t targeting = Component(base, hero, 98u);
    PPCContext work = Frame(ctx);
    const uint32_t target = targeting != 0u && CallSlot(work, base, targeting, 21u) ? work.r3.u32 : 0u;
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kGiftGiven,
        Fields("hero=%u,player=%d,target=%u,item='0x%08x'", Handle(base, hero), modlua::PlayerOf(base, hero), Handle(base, target),
               Word(base, item + 4u)));
    if (verdict.cancelled) {
      return;
    }
  }
  __imp__rex_CECPlayer_GiveGiftFromInventoryItem(ctx, base);
}

// follow. The follow component's start (r3 component 115, the follower at +4; r4 the leader) and stop
// (r3): the current leader is the smart pointer at +0x20. A start for a live leader that is not already
// followed is decided before it: a cancel hands it no leader (r4 = 0), the game's own refusal. The stop
// is raised after it when there was a leader.
REX_EXTERN(rex_CECFollow_Start) {
  const uint32_t component = ctx.r3.u32;
  const uint32_t leader = ctx.r4.u32;
  const uint32_t follower = Plausible(component) ? Word(base, component + 4u) : 0u;
  if (Live(base, follower) && Live(base, leader) && leader != Deref(base, component + 0x20u) && modlua::Wants(kFollow)) {
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kFollow,
        Fields("follower=%u,leader=%u,player=%d,following=%s", Handle(base, follower), Handle(base, leader),
               modlua::PlayerOf(base, leader), "true"));
    if (verdict.cancelled) {
      ctx.r4.u64 = 0u;
    }
  }
  __imp__rex_CECFollow_Start(ctx, base);
}

REX_EXTERN(rex_CECFollow_Stop) {
  const uint32_t component = ctx.r3.u32;
  const uint32_t follower = Plausible(component) ? Word(base, component + 4u) : 0u;
  const uint32_t leader = Plausible(component) ? Deref(base, component + 0x20u) : 0u;
  __imp__rex_CECFollow_Stop(ctx, base);
  if (Live(base, follower) && Live(base, leader) && modlua::Wants(kFollow)) {
    modlua::QueueEvent(kFollow, Fields("follower=%u,leader=%u,player=%d,following=%s", Handle(base, follower), Handle(base, leader),
                                       modlua::PlayerOf(base, leader), "false"));
  }
}

// ---------------------------------------------------------------------------------------------
// The menus: the GUI manager's screen word, around its SetScreen and ExitScreen (game_state_hooks.cpp).

namespace {
constexpr int32_t kMenuOpened = 57;
constexpr int32_t kMenuClosed = 58;
constexpr int32_t kMenuScreen = 59;
}  // namespace

namespace refii::fable3::modlua {

int32_t MenuScreen(const uint8_t* base) { return ScreenNow(base); }

void NoteMenuScreen(const uint8_t* base, int32_t before) {
  const int32_t now = ScreenNow(base);
  if (now == before) {
    return;
  }
  if (before == kNoScreen && Wants(kMenuOpened)) {
    QueueEvent(kMenuOpened, Fields("seen=true,player=%d", 1));
  }
  if (Wants(kMenuScreen)) {
    QueueEvent(kMenuScreen, Fields("screen=%d,player=%d", now, 1));
  }
  if (now == kNoScreen && Wants(kMenuClosed)) {
    QueueEvent(kMenuClosed, Fields("seen=true,player=%d", 1));
  }
}

}  // namespace refii::fable3::modlua

// ---------------------------------------------------------------------------------------------
// The family and the rented houses.

REX_EXTERN(__imp__rex_PlayerFamily_Marry);
REX_EXTERN(__imp__rex_PlayerFamily_Divorce);
REX_EXTERN(__imp__rex_PlayerFamily_RemoveMember);
REX_EXTERN(__imp__rex_PlayerFamily_AdoptWithSpouse);
REX_EXTERN(__imp__rex_OpinionReaction_SetEngagedToHero);
REX_EXTERN(__imp__rex_CECPlayerProperties_SetRentedOut);
REX_EXTERN(rex_PlayerFamily_IsMarriedTo);
REX_EXTERN(rex_PlayerFamily_FindMember);
REX_EXTERN(rex_World_EntityByUid);

namespace {

/// The game (`dword_83597034`).
const refii::guest::Addr kGame{0x83597034u};

constexpr int32_t kMarriage = 45;
constexpr int32_t kPropertyRented = 71;
constexpr int32_t kDivorce = 73;
constexpr int32_t kWidowed = 74;
constexpr int32_t kChildBorn = 75;
constexpr int32_t kEngaged = 78;

/// PlayerFamily.IsMarriedTo (family, spouse): live and a member record for the spouse's UID.
bool MarriedTo(const PPCContext& ctx, uint8_t* base, uint32_t family, uint32_t spouse) {
  PPCContext work = Frame(ctx);
  work.r3.u32 = family;
  work.r4.u32 = spouse;
  rex_PlayerFamily_IsMarriedTo(work, base);
  return (work.r3.u32 & 0xFFu) != 0u;
}

/// The building a 64-bit UID at `uid` names in the world (the lookup Sell and SetRentedOut make), or 0.
uint32_t BuildingByUid(const PPCContext& ctx, uint8_t* base, uint32_t uid) {
  const uint32_t world = Word(base, Word(base, Word(base, Word(base, Word(base, kGame) + 0xCu) + 0x1Cu) + 4u) + 4u);
  if (!Plausible(world) || !Plausible(uid)) {
    return 0u;
  }
  PPCContext work = Frame(ctx);
  work.r3.u32 = world;
  work.r4.u32 = uid;
  rex_World_EntityByUid(work, base);
  return Live(base, work.r3.u32) ? work.r3.u32 : 0u;
}

}  // namespace

// marriage. PlayerFamily's Marry (r3 the family component 82, the hero at +4; r4 the spouse): it moves
// the pending marital home (the hero's properties +0xAC). Decided before it: a cancel skips the
// wedding, the home kept pending (no caller reads its return).
REX_EXTERN(rex_PlayerFamily_Marry) {
  const uint32_t hero = Plausible(ctx.r3.u32) ? Word(base, ctx.r3.u32 + 4u) : 0u;
  const uint32_t spouse = ctx.r4.u32;
  if (Live(base, hero) && Live(base, spouse) && modlua::Wants(kMarriage)) {
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kMarriage, Fields("hero=%u,player=%d,spouse=%u", Handle(base, hero), modlua::PlayerOf(base, hero), Handle(base, spouse)));
    if (verdict.cancelled) {
      return;
    }
  }
  __imp__rex_PlayerFamily_Marry(ctx, base);
}

// divorce. PlayerFamily's Divorce (r3 the family, r4 the spouse), after it when the game's own
// IsMarriedTo held before.
REX_EXTERN(rex_PlayerFamily_Divorce) {
  const uint32_t family = ctx.r3.u32;
  const uint32_t hero = Plausible(family) ? Word(base, family + 4u) : 0u;
  const uint32_t spouse = ctx.r4.u32;
  const bool married = Live(base, hero) && Live(base, spouse) && modlua::Wants(kDivorce) && MarriedTo(ctx, base, family, spouse);
  __imp__rex_PlayerFamily_Divorce(ctx, base);
  if (married) {
    modlua::QueueEvent(kDivorce, Fields("hero=%u,player=%d,spouse=%u", Handle(base, hero), modlua::PlayerOf(base, hero), Handle(base, spouse)));
  }
}

// widowed. The family's member removal (r3 the family, r4 &the member's 64-bit UID, r5 died), after it
// for the spouse: the family's +0x35 clear and the member record the lookup finds (its +0 the UID) the
// one removed.
REX_EXTERN(rex_PlayerFamily_RemoveMember) {
  const uint32_t family = ctx.r3.u32;
  const uint32_t key = ctx.r4.u32;
  const uint32_t hero = Plausible(family) ? Word(base, family + 4u) : 0u;
  bool spouse = false;
  uint64_t uid = 0u;
  if (Live(base, hero) && Plausible(key) && base[family + 0x35u] == 0u && modlua::Wants(kWidowed)) {
    uid = (static_cast<uint64_t>(Word(base, key)) << 32) | Word(base, key + 4u);
    PPCContext work = Frame(ctx);
    work.r3.u32 = family;
    work.r4.u32 = key;
    rex_PlayerFamily_FindMember(work, base);
    const uint32_t record = work.r3.u32;
    spouse = Plausible(record) && ((static_cast<uint64_t>(Word(base, record)) << 32) | Word(base, record + 4u)) == uid;
  }
  __imp__rex_PlayerFamily_RemoveMember(ctx, base);
  if (spouse) {
    modlua::QueueEvent(kWidowed, Fields("hero=%u,player=%d,uid='0x%016llx'", Handle(base, hero), modlua::PlayerOf(base, hero),
                                        static_cast<unsigned long long>(uid)));
  }
}

// child_born. PlayerFamily's AdoptWithSpouse (r3 the family, r4 the spouse, r5 the child): the child is
// added only when both are live (`lbz r11,0x94(r4)` 0x827e9330). After it.
REX_EXTERN(rex_PlayerFamily_AdoptWithSpouse) {
  const uint32_t hero = Plausible(ctx.r3.u32) ? Word(base, ctx.r3.u32 + 4u) : 0u;
  const uint32_t spouse = ctx.r4.u32;
  const uint32_t child = ctx.r5.u32;
  const bool adopts = Live(base, hero) && Live(base, spouse) && Live(base, child);
  __imp__rex_PlayerFamily_AdoptWithSpouse(ctx, base);
  if (adopts && modlua::Wants(kChildBorn)) {
    modlua::QueueEvent(kChildBorn, Fields("hero=%u,player=%d,spouse=%u,child=%u", Handle(base, hero), modlua::PlayerOf(base, hero),
                                          Handle(base, spouse), Handle(base, child)));
  }
}

// engaged. OpinionReaction.SetEngagedToHero (r3 the opinion component, the villager at +4; r4 the hero;
// r5 engaged): the hero's opinion record +0x8C. Decided before it: a cancel leaves the flag and the
// engagement gossip as they were (the wedding clears it with 0: cancelling that keeps a spouse engaged).
REX_EXTERN(rex_OpinionReaction_SetEngagedToHero) {
  const uint32_t villager = Plausible(ctx.r3.u32) ? Word(base, ctx.r3.u32 + 4u) : 0u;
  const uint32_t hero = ctx.r4.u32;
  if (Live(base, villager) && Live(base, hero) && modlua::Wants(kEngaged)) {
    const modlua::Verdict verdict =
        modlua::Decide(ctx, base, kEngaged, Fields("villager=%u,player=%d,engaged=%s", Handle(base, villager), modlua::PlayerOf(base, hero),
                                                   (ctx.r5.u32 & 0xFFu) != 0u ? "true" : "false"));
    if (verdict.cancelled) {
      return;
    }
  }
  __imp__rex_OpinionReaction_SetEngagedToHero(ctx, base);
}

// property_rented. The player properties' SetRentedOut (r3 the properties component 81, the hero at +4;
// r4 &the building's UID; r5 rented; r6 quiet — a replayed network message). Decided before it when not
// quiet: a cancel leaves the record's +5 as it was and sends nothing.
REX_EXTERN(rex_CECPlayerProperties_SetRentedOut) {
  const uint32_t hero = Plausible(ctx.r3.u32) ? Word(base, ctx.r3.u32 + 4u) : 0u;
  if ((ctx.r6.u32 & 0xFFu) == 0u && Live(base, hero) && modlua::Wants(kPropertyRented)) {
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kPropertyRented,
        Fields("hero=%u,player=%d,building=%u,rented=%s", Handle(base, hero), modlua::PlayerOf(base, hero),
               Handle(base, BuildingByUid(ctx, base, ctx.r4.u32)), (ctx.r5.u32 & 0xFFu) != 0u ? "true" : "false"));
    if (verdict.cancelled) {
      return;
    }
  }
  __imp__rex_CECPlayerProperties_SetRentedOut(ctx, base);
}

// ---------------------------------------------------------------------------------------------
// Achievements and tracked stats.

REX_EXTERN(__imp__rex_CECStats_UnlockAchievement);
REX_EXTERN(__imp__rex_CECStats_ModifyTrackedStat);
REX_EXTERN(rex_CECStats_AchievementRecord);
REX_EXTERN(rex_GDB_RecordS32);

namespace {

constexpr int32_t kAchievement = 65;
constexpr int32_t kTrackedStat = 83;

/// The field hash of "XLASTAchievementID", set at startup (0x831d97d4); the unlock reads the record's
/// field by it (`sub_82349F08(record, dword_8359833C)`).
const refii::guest::Addr kXlastIdField{0x8359833Cu};

}  // namespace

// achievement. The stats' unlock (r3 the stats component 15, owner at +4; r4 the FNV-1a hash of the
// achievement's name): the id is the record's XLASTAchievementID, as the unlock itself reads it.
// Decided before it: a cancel skips the unlock (no write, no flag at stats+0x540, no partner mirror).
namespace {

/// achievement, decided before either unlock entry: false when a handler refused it.
bool DecideAchievement(PPCContext& ctx, uint8_t* base) {
  const uint32_t source = Plausible(ctx.r3.u32) ? Word(base, ctx.r3.u32 + 4u) : 0u;
  if (Live(base, source) && modlua::Wants(kAchievement)) {
    PPCContext work = Frame(ctx);
    work.r3.u32 = ctx.r4.u32;
    rex_CECStats_AchievementRecord(work, base);
    int32_t id = -1;
    if (Plausible(work.r3.u32)) {
      const uint32_t record = work.r3.u32;
      work = Frame(ctx);
      work.r3.u32 = record;
      work.r4.u32 = Word(base, kXlastIdField);
      rex_GDB_RecordS32(work, base);
      id = static_cast<int32_t>(work.r3.u32);
    }
    const modlua::Verdict verdict =
        modlua::Decide(ctx, base, kAchievement, Fields("id=%d,source=%u,player=%d", id, Handle(base, source), modlua::PlayerOf(base, source)));
    if (verdict.cancelled) {
      return false;
    }
  }
  return true;
}

}  // namespace

REX_EXTERN(rex_CECStats_UnlockAchievement) {
  if (DecideAchievement(ctx, base)) {
    __imp__rex_CECStats_UnlockAchievement(ctx, base);
  }
}

// The unlock's second entry (r3 the stats, r4 the hash), which the network dispatcher (sub_8276D530) calls
// for a replayed unlock and which skips the first: the same recipe, the same decision.
REX_EXTERN(__imp__rex_CECStats_UnlockAchievementReplayed);
REX_EXTERN(rex_CECStats_UnlockAchievementReplayed) {
  if (DecideAchievement(ctx, base)) {
    __imp__rex_CECStats_UnlockAchievementReplayed(ctx, base);
  }
}

// tracked_stat. The stats' ModifyTrackedStat (r3 the stats, owner at +4; r4 the stat — the FNV-1a hash of
// its name, sent as "0x…" text; r5 the amount). Decided before it: a cancel hands it 0 (its own
// `cmpwi r5,0; beq` exit), a written amount replaces r5.
REX_EXTERN(rex_CECStats_ModifyTrackedStat) {
  const uint32_t hero = Plausible(ctx.r3.u32) ? Word(base, ctx.r3.u32 + 4u) : 0u;
  const int32_t amount = static_cast<int32_t>(ctx.r5.u32);
  if (amount != 0 && Live(base, hero) && modlua::Wants(kTrackedStat)) {
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kTrackedStat,
        Fields("hero=%u,player=%d,stat='0x%08x',amount=%d", Handle(base, hero), modlua::PlayerOf(base, hero), ctx.r4.u32, amount));
    double written = 0.0;
    if (verdict.cancelled) {
      ctx.r5.u64 = 0u;
    } else if (verdict.Read("amount", written)) {
      ctx.r5.s64 = ClampInt(written);
    }
  }
  __imp__rex_CECStats_ModifyTrackedStat(ctx, base);
}

// ---------------------------------------------------------------------------------------------
// Property: purchases, sales, rent, condition, furniture, redecorating and the household budget.

REX_EXTERN(__imp__rex_CECPlayerProperties_BuyBuilding);
REX_EXTERN(__imp__rex_CECPlayerProperties_BuyByUid);
REX_EXTERN(__imp__rex_CECPlayerProperties_Sell);
REX_EXTERN(__imp__rex_CECPlayerProperties_PayIncome);
REX_EXTERN(__imp__rex_CECBuilding_ModifyCondition);
REX_EXTERN(__imp__rex_CECBuilding_SetCondition);
REX_EXTERN(__imp__rex_CECUpgradeableFurniture_SetLevel);
REX_EXTERN(__imp__rex_CInteractionModeMenuDecoration_Ctor);
REX_EXTERN(__imp__rex_CInteractionModeMenuDecoration_Dtor);
REX_EXTERN(__imp__rex_CInteractionModeRealEstate_SetBudget);
REX_EXTERN(rex_Building_GetBuildingOwner);
REX_EXTERN(rex_Building_GetBuyPrice);

namespace {

constexpr int32_t kPropertyBought = 68;
constexpr int32_t kPropertySold = 69;
constexpr int32_t kRentPaid = 70;
constexpr int32_t kPropertyCondition = 72;
constexpr int32_t kFurnitureUpgraded = 80;
constexpr int32_t kRedecorating = 81;
constexpr int32_t kHouseholdBudget = 82;

/// The building an owned piece belongs to: component 75's owner, the smart pointer at +12 (the getter
/// several natives share, sub_82A1D6E8: `v1 = a1 + 12` then its object).
uint32_t OwningBuilding(const uint8_t* base, uint32_t piece) {
  const uint32_t owned = Component(base, piece, 75u);
  return owned != 0u ? Deref(base, owned + 12u) : 0u;
}

/// The player owning a building (component 79): Building.GetBuildingOwner's entity's player.
int32_t BuildingPlayer(const PPCContext& ctx, uint8_t* base, uint32_t building_component) {
  if (!Plausible(building_component)) {
    return 0;
  }
  PPCContext work = Frame(ctx);
  work.r3.u32 = building_component;
  rex_Building_GetBuildingOwner(work, base);
  return modlua::PlayerOf(base, work.r3.u32);
}

/// A purchase decided before its routine: false when a handler refused it.
bool DecidePurchase(PPCContext& ctx, uint8_t* base, uint32_t hero, uint32_t building) {
  int32_t price = -1;
  if (const uint32_t component = Live(base, building) ? Component(base, building, 79u) : 0u; component != 0u) {
    PPCContext work = Frame(ctx);
    work.r3.u32 = component;
    rex_Building_GetBuyPrice(work, base);
    price = static_cast<int32_t>(work.r3.u32);
  }
  return !modlua::Decide(ctx, base, kPropertyBought,
                         Fields("hero=%u,player=%d,building=%u,price=%d", Handle(base, hero), modlua::PlayerOf(base, hero),
                                Handle(base, building), price))
              .cancelled;
}

uint32_t MoneyOf(const uint8_t* base, uint32_t hero) {
  const uint32_t money = Component(base, hero, 102u);
  return money != 0u ? Word(base, money + 0xCu) : 0u;
}

void RaiseCondition(const PPCContext& ctx, uint8_t* base, uint32_t building_component, float previous) {
  const float condition = Float(base, building_component + 0x80u);
  if (condition == previous || !modlua::Wants(kPropertyCondition)) {
    return;
  }
  const uint32_t building = Word(base, building_component + 4u);
  const unsigned long long uid =
      Plausible(building) ? (static_cast<unsigned long long>(Word(base, building + 24u)) << 32) | Word(base, building + 28u) : 0ull;
  modlua::QueueEvent(kPropertyCondition, Fields("uid='0x%016llx',player=%d,condition=%.9g,previous=%.9g,repaired=%s", uid,
                                                BuildingPlayer(ctx, base, building_component), Fin(condition), Fin(previous), "false"));
}

}  // namespace

// property_bought. The player properties' two purchase routines (r3 the properties component 81, the hero
// at +4; r4 the building, or &its 64-bit UID): they list the record and pay through Buy. Decided before
// either (never at Buy, whose record is listed already): a cancel skips the purchase, nothing paid.
REX_EXTERN(rex_CECPlayerProperties_BuyBuilding) {
  const uint32_t hero = Plausible(ctx.r3.u32) ? Word(base, ctx.r3.u32 + 4u) : 0u;
  if (Live(base, hero) && modlua::Wants(kPropertyBought) && !DecidePurchase(ctx, base, hero, ctx.r4.u32)) {
    return;
  }
  __imp__rex_CECPlayerProperties_BuyBuilding(ctx, base);
}

REX_EXTERN(rex_CECPlayerProperties_BuyByUid) {
  const uint32_t hero = Plausible(ctx.r3.u32) ? Word(base, ctx.r3.u32 + 4u) : 0u;
  if (Live(base, hero) && modlua::Wants(kPropertyBought) && !DecidePurchase(ctx, base, hero, BuildingByUid(ctx, base, ctx.r4.u32))) {
    return;
  }
  __imp__rex_CECPlayerProperties_BuyByUid(ctx, base);
}

// property_sold. The player properties' Sell (r3 the properties, the hero at +4; r4 &the building's UID;
// f1 the sale multiplier). Decided before it: a cancel keeps the record, pays and sends nothing.
REX_EXTERN(rex_CECPlayerProperties_Sell) {
  const uint32_t hero = Plausible(ctx.r3.u32) ? Word(base, ctx.r3.u32 + 4u) : 0u;
  if (Live(base, hero) && modlua::Wants(kPropertySold)) {
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kPropertySold,
        Fields("hero=%u,player=%d,building=%u,price=%d", Handle(base, hero), modlua::PlayerOf(base, hero),
               Handle(base, BuildingByUid(ctx, base, ctx.r4.u32)), -1));
    if (verdict.cancelled) {
      return;
    }
  }
  __imp__rex_CECPlayerProperties_Sell(ctx, base);
}

// rent_paid. The player properties' PayIncome (r3 the properties, the hero at +4): one drop of rent and
// shop profit per call, paid into the hero's purse (component 102's +0xC). After it, with what was paid.
REX_EXTERN(rex_CECPlayerProperties_PayIncome) {
  const uint32_t hero = Plausible(ctx.r3.u32) ? Word(base, ctx.r3.u32 + 4u) : 0u;
  const uint32_t before = Live(base, hero) ? MoneyOf(base, hero) : 0u;
  __imp__rex_CECPlayerProperties_PayIncome(ctx, base);
  if (!Live(base, hero) || !modlua::Wants(kRentPaid)) {
    return;
  }
  const int32_t gold = static_cast<int32_t>(MoneyOf(base, hero) - before);
  if (gold != 0) {
    modlua::QueueEvent(kRentPaid, Fields("hero=%u,player=%d,gold=%d,drops=%d,away=%s", Handle(base, hero), modlua::PlayerOf(base, hero),
                                         gold, 1, "false"));
  }
}

// property_condition. The building's ModifyCondition (r3 the building component 79; f1 the delta) and
// SetCondition (r3, f1 the value): the condition at +0x80, clamped 0..1. After each, when it moved.
REX_EXTERN(rex_CECBuilding_ModifyCondition) {
  const uint32_t component = ctx.r3.u32;
  const float previous = Plausible(component) ? Float(base, component + 0x80u) : 0.0f;
  const PPCContext entry = ctx;
  __imp__rex_CECBuilding_ModifyCondition(ctx, base);
  if (Plausible(component)) {
    RaiseCondition(entry, base, component, previous);
  }
}

REX_EXTERN(rex_CECBuilding_SetCondition) {
  const uint32_t component = ctx.r3.u32;
  const float previous = Plausible(component) ? Float(base, component + 0x80u) : 0.0f;
  const PPCContext entry = ctx;
  __imp__rex_CECBuilding_SetCondition(ctx, base);
  if (Plausible(component)) {
    RaiseCondition(entry, base, component, previous);
  }
}

// furniture_upgraded. The upgradeable furniture's level setter (r3 component 80, the piece at +4, its
// level at +0x10; r4 the level; r5 force). Decided before it, unforced and changing: a cancel answers 0
// (its own "unchanged": the callers skip the refresh and the revalue), a written level replaces r4.
REX_EXTERN(rex_CECUpgradeableFurniture_SetLevel) {
  const uint32_t component = ctx.r3.u32;
  const uint32_t piece = Plausible(component) ? Word(base, component + 4u) : 0u;
  const int32_t previous = Plausible(component) ? static_cast<int32_t>(Word(base, component + 0x10u)) : 0;
  const int32_t level = static_cast<int32_t>(ctx.r4.u32);
  if ((ctx.r5.u32 & 0xFFu) == 0u && level != previous && Live(base, piece) && modlua::Wants(kFurnitureUpgraded)) {
    const uint32_t building = OwningBuilding(base, piece);
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kFurnitureUpgraded,
        Fields("piece=%u,player=%d,level=%d,previous=%d", Handle(base, piece),
               BuildingPlayer(ctx, base, building != 0u ? Component(base, building, 79u) : 0u), level, previous));
    if (verdict.cancelled) {
      ctx.r3.u64 = 0u;
      return;
    }
    double written = 0.0;
    if (verdict.Read("level", written) && written == written) {
      ctx.r4.s64 = ClampInt(written);
    }
  }
  __imp__rex_CECUpgradeableFurniture_SetLevel(ctx, base);
}

namespace {

/// The building the decoration mode works on: the interaction manager (mode+20) names its target (the
/// smart pointer at +48), the target's owning building.
uint32_t DecoratedBuilding(const uint8_t* base, uint32_t mode) {
  const uint32_t manager = Word(base, mode + 20u);
  const uint32_t target = Plausible(manager) ? Deref(base, manager + 48u) : 0u;
  return target != 0u ? OwningBuilding(base, target) : 0u;
}

}  // namespace

// redecorating. The decoration mode's constructor (r3 the mode, r4 the interaction manager), after it,
// and its destructor (r3), before it. The decorator is the local hero (player 1).
REX_EXTERN(rex_CInteractionModeMenuDecoration_Ctor) {
  const uint32_t mode = ctx.r3.u32;
  __imp__rex_CInteractionModeMenuDecoration_Ctor(ctx, base);
  if (Plausible(mode) && modlua::Wants(kRedecorating)) {
    modlua::QueueEvent(kRedecorating, Fields("building=%u,player=%d,on=%s", Handle(base, DecoratedBuilding(base, mode)), 1, "true"));
  }
}

REX_EXTERN(rex_CInteractionModeMenuDecoration_Dtor) {
  const uint32_t mode = ctx.r3.u32;
  if (Plausible(mode) && modlua::Wants(kRedecorating)) {
    modlua::QueueEvent(kRedecorating, Fields("building=%u,player=%d,on=%s", Handle(base, DecoratedBuilding(base, mode)), 1, "false"));
  }
  __imp__rex_CInteractionModeMenuDecoration_Dtor(ctx, base);
}

// household_budget. The real-estate mode's budget (r3 the mode, r4 the gold 0..1000): the screen is the
// local hero's. Decided before it: a cancel skips it, a written gold replaces r4.
REX_EXTERN(rex_CInteractionModeRealEstate_SetBudget) {
  const uint32_t hero = modlua::Hero(base, 1);
  if (Live(base, hero) && modlua::Wants(kHouseholdBudget)) {
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kHouseholdBudget,
        Fields("hero=%u,player=%d,building=%u,gold=%d", Handle(base, hero), 1, 0u, static_cast<int32_t>(ctx.r4.u32)));
    if (verdict.cancelled) {
      return;
    }
    double written = 0.0;
    if (verdict.Read("gold", written) && written == written) {
      const int32_t gold = ClampInt(written);
      ctx.r4.s64 = gold < 0 ? 0 : gold > 1000 ? 1000 : gold;
    }
  }
  __imp__rex_CInteractionModeRealEstate_SetBudget(ctx, base);
}

// ---------------------------------------------------------------------------------------------
// Weapons equipped, relations, dialogue, weather regions and the game's start.

REX_EXTERN(__imp__rex_CPlayerInstructionReplaceWeapon_Execute);
REX_EXTERN(__imp__rex_CECFaction_RelationTo);
REX_EXTERN(__imp__rex_CECTalk_PlayLine);
REX_EXTERN(__imp__rex_lhPaintMap_DaySetAt);
REX_EXTERN(__imp__rex_Game_BuildStart);

namespace {

constexpr int32_t kWeaponEquipped = 30;
constexpr int32_t kRelation = 33;
constexpr int32_t kDialogue = 38;
constexpr int32_t kWeatherRegion = 60;
constexpr int32_t kStartGame = 99;

/// The start's save and level: CCharStrings InitialSaveGameName (SetInitialSaveGameName 0x822DE900) and the
/// new game's level.
const refii::guest::Addr kInitialSave{0x83597958u};
const refii::guest::Addr kInitialLevel{0x8359794Cu};
const refii::guest::Addr kProfiles{0x835979F8u};
const refii::guest::Addr kInitialClientSave{0x8359795Cu};
/// The active user (TU3 0x832DF7BC / disc 0x8329ED38), as the load mode's builder takes it.
const refii::guest::Addr kStartUser{0x832DF7BCu};
/// The commit a refused boot start is made again by: the player's START on the splash (Fable 3's front
/// end has no pump action for it; see the report's open question).
constexpr const char* kStartCommit = "START";
/// The save the boot's pick let through, so the start builder it leads to does not raise it again.
std::string g_stated_save;

uint32_t g_region_previous = 0u;

}  // namespace

// weapon_equipped. The hero's weapon-replace instruction's Execute (r3 the instruction, +8 the weapon's
// record; r4 the hero), before it.
REX_EXTERN(rex_CPlayerInstructionReplaceWeapon_Execute) {
  const uint32_t hero = ctx.r4.u32;
  if (Live(base, hero) && Plausible(ctx.r3.u32) && modlua::Wants(kWeaponEquipped)) {
    modlua::QueueEvent(kWeaponEquipped, Fields("entity=%u,player=%d,item=%u,definition='0x%08x'", Handle(base, hero),
                                               modlua::PlayerOf(base, hero), 0u, Word(base, ctx.r3.u32 + 8u)));
  }
  __imp__rex_CPlayerInstructionReplaceWeapon_Execute(ctx, base);
}

// relation. The faction's RelationTo (r3 the faction component 157, owner at +4; r4 the other; returns 1
// neutral, 2 ally, 4 enemy — `li r29,1/2/4 ... mr r3,r29`). Decided after it: a written relation (1, 2 or
// 4) is the answer; the callers only compare it. Asked many times a tick: raised only while wanted.
REX_EXTERN(rex_CECFaction_RelationTo) {
  const uint32_t entity = Plausible(ctx.r3.u32) ? Word(base, ctx.r3.u32 + 4u) : 0u;
  const uint32_t other = ctx.r4.u32;
  __imp__rex_CECFaction_RelationTo(ctx, base);
  if (!modlua::Wants(kRelation) || !Live(base, entity) || !Live(base, other)) {
    return;
  }
  const PPCContext answered = ctx;
  const modlua::Verdict verdict = modlua::Decide(
      ctx, base, kRelation,
      Fields("entity=%u,player=%d,other=%u,other_player=%d,relation=%d", Handle(base, entity), modlua::PlayerOf(base, entity),
             Handle(base, other), modlua::PlayerOf(base, other), static_cast<int32_t>(answered.r3.u32)));
  ctx.r3 = answered.r3;
  double written = 0.0;
  if (verdict.Read("relation", written) && (written == 1.0 || written == 2.0 || written == 4.0)) {
    ctx.r3.u64 = static_cast<uint64_t>(written);
  }
}

namespace {
/// The talk update's replay of a line it had put off (CECTalk_vf18 `bl CECTalk_PlayLine` 0x821E7728), returned to.
const refii::guest::Addr kLineReplayed{0x821E772Cu};
}  // namespace

// dialogue. The talk component's PlayLine (r3 the talk component, the speaker at +4; r4 the text key, r5
// subtitle, r6 the listener). Decided before it: a cancel answers 0, the call's own "no audio" answer
// (`li r3,0` 0x825B5528, after the line is stamped when byte_835970FB is set), and plays nothing — no
// audio, no subtitle, no speech action, and a line the speaker was still too busy for is not stored for
// later. The talk update's replay of a stored line (return 0x821E772C) is not asked again: that line was
// decided when it was stored.
REX_EXTERN(rex_CECTalk_PlayLine) {
  const uint32_t talk = ctx.r3.u32;
  if (Plausible(talk) && static_cast<uint32_t>(ctx.lr) != kLineReplayed && modlua::Wants(kDialogue)) {
    const uint32_t speaker = Word(base, talk + 4u);
    const uint32_t listener = ctx.r6.u32;
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kDialogue,
        Fields("speaker=%u,speaker_player=%d,text='0x%08x',listener=%u,listener_player=%d,subtitle=%s", Handle(base, speaker),
               modlua::PlayerOf(base, speaker), ctx.r4.u32, Handle(base, listener), modlua::PlayerOf(base, listener),
               (ctx.r5.u32 & 0xFFu) != 0u ? "true" : "false"));
    if (verdict.cancelled) {
      ctx.r3.u64 = 0u;
      return;
    }
  }
  __imp__rex_CECTalk_PlayLine(ctx, base);
}

// weather_region. The paint map's region at the hero's grid point (r3 the map, r4 int[2]; returns the
// region's theme id or -1): asked for player one's hero (the region update tests world+0x7C). After it,
// when the region changed.
REX_EXTERN(rex_lhPaintMap_DaySetAt) {
  __imp__rex_lhPaintMap_DaySetAt(ctx, base);
  const int32_t id = static_cast<int32_t>(ctx.r3.u32);
  const uint32_t region = id < 0 ? 0u : static_cast<uint32_t>(id);
  if (region == g_region_previous) {
    return;
  }
  const uint32_t previous = g_region_previous;
  g_region_previous = region;
  if (modlua::Wants(kWeatherRegion)) {
    modlua::QueueEvent(kWeatherRegion, Fields("day_set='0x%08x',previous='0x%08x',inside=%s,player=%d", region, previous,
                                              region != 0u ? "true" : "false", 1));
  }
}

REX_EXTERN(__imp__rex_FrontEnd_PickSave);

// start_game, the boot's. The Startup update (TU3 sub_82319330, disc sub_82312B80) calls this picker once
// START was accepted (splash F+0x288 == 3): it enumerates the signed-in user's saves newest first and
// copies the first valid one's name into InitialSaveGameName (0x83597958) — or the client's (0x8359795C)
// unless the profile's +0x1C and +0x1D are set — (`bl sub_82228298` 0x82319038, `li r3,1` 0x82319050);
// it answers 0 when it showed the player a message box instead (a corrupt or incompatible save,
// `li r3,0` 0x823190A8). The update takes 0 as the refusal: `clrlwi r11,r3,24; ... beq` 0x82319740..4C to
// the Startup's back-to-the-prompt sub_82534560 (splash F+0x288 = 2: "Press START" again) instead of its
// go-on sub_82534500. A cancel answers 0 after the call: the front end stays at the prompt, the save
// files untouched; the name stays in the global, so the next START answers 1 at once (the picker returns
// before enumerating when it is set, 0x82318DAC..C0) and asks again with the same save.
// The save is named as the guest names it: its XContent file name, "Hero" (dword_83597ADC, built from
// aHero by sub_831D3380) and the save's number 0..149 zero-padded to three digits (sub_822F8160,
// "%d" then "00"/"0"), copied to XCONTENT_DATA.szFileName (+0x108) when it was created (`li r4,0x2A;
// addi r3,r1,0x250+var_78; bl _strcpy_s_p` 0x822F868C..94) — "Hero000", "Hero001", ...
// Raised only for a save: an empty pick is the first boot (no save: the GUI component goes to the
// front-end level) and reaches the start builder's report as a new game.
REX_EXTERN(rex_FrontEnd_PickSave) {
  __imp__rex_FrontEnd_PickSave(ctx, base);
  if ((ctx.r3.u32 & 0xFFu) == 0u || !modlua::Wants(kStartGame)) {
    return;
  }
  const uint32_t flags = Word(base, Word(base, kProfiles) + 0x10u);
  const bool main = Plausible(flags) && base[flags + 0x1Cu] != 0u && base[flags + 0x1Du] != 0u;
  const std::string save = CharString(base, main ? kInitialSave : kInitialClientSave);
  if (save.empty()) {
    return;
  }
  const modlua::Verdict verdict = modlua::DecideAnywhere(
      ctx, base, kStartGame,
      Fields("save='%s',level='',new_game=false,player=%d,commit='%s'", save.c_str(), 1, kStartCommit));
  if (verdict.cancelled) {
    ctx.r3.u64 = 0u;
    return;
  }
  g_stated_save = save;
}

// start_game, every other start: the main game's start builder (r3 the game component). It acts only
// while the CURRENT component (the global `dword_83597034`, not r3) is not the GUI's: `!(*(*game)+24)(game)`,
// its slot 6 — the same test is made here on the same object. It pushes the save's load or a new game's
// level load: an in-game reload (QuitToLoadSaveGame / LeavePVPGame hand sub_8234E708 the save manager's
// +0, the current save's name; LoadFailQuestSave the quest's fail save), a new game, a co-op join. Read-only
// here — the front end is already gone and skipping the builder would leave the game with no load mode;
// the one start that can be refused, the boot's, was decided at the pick and is not raised again.
// It is also player one's adoption point (character_binding_hooks.cpp): the builder loads
// InitialSaveGameName as the active user (`lwz r5,dword_832DF7BC@l(r11)` 0x8234CDAC / 0x823459B8's twin),
// an empty one a new game, so that user's hero comes from that save before its Player is set up.
REX_EXTERN(rex_Game_BuildStart) {
  const uint32_t current = Word(base, kGame);
  bool builds = false;
  if (Plausible(current)) {
    PPCContext work = Frame(ctx);
    builds = CallSlot(work, base, current, 6u) && (work.r3.u32 & 0xFFu) == 0u;
  }
  if (builds) {
    const uint32_t user = Word(base, kStartUser);
    if (user < 4u) {
      RefiiAchievementBindUser(user, CharString(base, kInitialSave).c_str());
    }
  }
  if (builds && modlua::Wants(kStartGame)) {
    {
      const std::string save = CharString(base, kInitialSave);
      const bool stated = !save.empty() && save == g_stated_save;
      g_stated_save.clear();
      if (!stated) {
        const bool fresh = save.empty();
        modlua::QueueEvent(kStartGame, Fields("save='%s',level='%s',new_game=%s,player=%d,commit='%s'", save.c_str(),
                                              fresh ? CharString(base, kInitialLevel).c_str() : "", fresh ? "true" : "false", 1, ""));
      }
    }
  }
  __imp__rex_Game_BuildStart(ctx, base);
}

// collectable_taken, the paths that set the status word themselves: the gnome capture (r3 a component of
// the gnome, the gnome at +4; r4 the hero's smart pointer) and the pickup (r3 likewise; r4 the hero). Each
// finds the collectable's component 84 on that entity and writes the hero's word (`bl sub_829A1190; li
// r11,1; stw r11,0(r3)` 0x82b12b4c). Compared before and after.
REX_EXTERN(__imp__rex_GnomeCapture);
REX_EXTERN(__imp__rex_CollectablePickup);

REX_EXTERN(rex_GnomeCapture) {
  const uint32_t component = Plausible(ctx.r3.u32) ? Component(base, Word(base, ctx.r3.u32 + 4u), 84u) : 0u;
  const int32_t player = HeroPlayer(base, Deref(base, ctx.r4.u32));
  const uint32_t before = component != 0u ? Word(base, component + (player == 2 ? 0x10u : 0xCu)) : 1u;
  __imp__rex_GnomeCapture(ctx, base);
  if (component != 0u) {
    RaiseCollectable(base, component, player, before);
  }
}

REX_EXTERN(rex_CollectablePickup) {
  const uint32_t component = Plausible(ctx.r3.u32) ? Component(base, Word(base, ctx.r3.u32 + 4u), 84u) : 0u;
  const int32_t player = HeroPlayer(base, ctx.r4.u32);
  const uint32_t before = component != 0u ? Word(base, component + (player == 2 ? 0x10u : 0xCu)) : 1u;
  __imp__rex_CollectablePickup(ctx, base);
  if (component != 0u) {
    RaiseCollectable(base, component, player, before);
  }
}

// dog_trick. Dog.PerformCommand (r3 the dog component, the dog at +4; r4 {the command's hash, its record};
// r5 force): the trick runs when the hash is on the unlocked list (+0x2A4..+0x2A8) or forced. Decided
// before it: a cancel skips it, the dog does not do it. The master is the smart pointer at the dog
// component's +0xDC (Dog.GetMaster 0x8229C3D8: `v1 = a1 + 220`).
REX_EXTERN(__imp__rex_CECDog_PerformCommand);

namespace {
constexpr int32_t kDogTrick = 46;
}  // namespace

REX_EXTERN(rex_CECDog_PerformCommand) {
  const uint32_t component = ctx.r3.u32;
  const uint32_t dog = Plausible(component) ? Word(base, component + 4u) : 0u;
  if (Live(base, dog) && Plausible(ctx.r4.u32) && modlua::Wants(kDogTrick)) {
    const uint32_t master = Deref(base, component + 0xDCu);
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kDogTrick,
        Fields("dog=%u,trick='0x%08x',player=%d", Handle(base, dog), Word(base, ctx.r4.u32), modlua::PlayerOf(base, master)));
    if (verdict.cancelled) {
      return;
    }
  }
  __imp__rex_CECDog_PerformCommand(ctx, base);
}

// THE SHARED EVENTS (Fable 2 raises them too: Fable2/src/vfs/src/hooks/mod_events.cpp).
REX_EXTERN(__imp__rex_CECChest_Open);
REX_EXTERN(__imp__rex_CAction_OpenChest_Use);
REX_EXTERN(rex_CECChest_KeysNeeded);
REX_EXTERN(rex_CECChest_SealsNeeded);
REX_EXTERN(__imp__rex_CECShopkeeper_Buy);
REX_EXTERN(__imp__rex_CECShopkeeper_Sell);
REX_EXTERN(__imp__rex_CShopBasket_Steal);
REX_EXTERN(rex_CECShopkeeper_BuyPrice);
REX_EXTERN(rex_CECShopkeeper_SellPrice);
REX_EXTERN(__imp__rex_CECShopkeeper_BuyPrice);
REX_EXTERN(__imp__rex_CECShopkeeper_SellPrice);
REX_EXTERN(__imp__rex_CECDiggingSpot_OnDug);
REX_EXTERN(__imp__rex_CECDemonDoor_SpawnFace);
REX_EXTERN(__imp__rex_CECDemonDoor_DespawnFace);
REX_EXTERN(__imp__rex_CECDemonDoor_Open);
REX_EXTERN(__imp__rex_CECDemonDoor_OpenEffects);
REX_EXTERN(__imp__rex_CECPlayerDrunkenness_Drink);

namespace {

constexpr int32_t kChestOpened = 119;
constexpr int32_t kItemBought = 120;
constexpr int32_t kItemSold = 121;
constexpr int32_t kShopPrice = 122;
constexpr int32_t kDigSpotDug = 123;
constexpr int32_t kDemonDoor = 124;
constexpr int32_t kDrink = 125;

/// The open action running the chest's Open: its owner, `**(action+8)` (CActionBase: +4/+8 the owner's
/// weak reference). 0 while a script opens the chest itself.
uint32_t g_chest_opener = 0u;
int g_demon_door_opening = 0;

/// A shop item's record: the item is the smart pointer's +0, its record at item+4 (lhEntity_UseInventoryItem).
uint32_t ShopItemRecord(const uint8_t* base, uint32_t item_pointer) {
  const uint32_t item = Plausible(item_pointer) ? Word(base, item_pointer) : 0u;
  return Plausible(item) ? Word(base, item + 4u) : 0u;
}

/// The shop's price for `customer`: BuyPrice or SellPrice (r3 the shopkeeper, r4 the customer, r5 the
/// item), through the hooked entry so a mod's written price is the one reported.
int32_t ShopPriceOf(const PPCContext& ctx, uint8_t* base, bool buy, uint32_t shopkeeper, uint32_t customer, uint32_t item) {
  PPCContext work = Frame(ctx);
  work.r3.u64 = shopkeeper;
  work.r4.u64 = customer;
  work.r5.u64 = item;
  if (buy) {
    rex_CECShopkeeper_BuyPrice(work, base);
  } else {
    rex_CECShopkeeper_SellPrice(work, base);
  }
  return static_cast<int32_t>(work.r3.u32);
}

bool DecideBought(PPCContext& ctx, uint8_t* base, uint32_t shopkeeper, uint32_t buyer, uint32_t item, int32_t price, bool stolen) {
  return !modlua::Decide(ctx, base, kItemBought,
                         Fields("shopkeeper=%u,buyer=%u,player=%d,definition='0x%08x',price=%d,stolen=%s", Handle(base, shopkeeper),
                                Handle(base, buyer), modlua::PlayerOf(base, buyer), ShopItemRecord(base, item), price, Bool(stolen)))
              .cancelled;
}

/// shop_price after BuyPrice or SellPrice: the written price, at least 0, replaces r3.
void DecidePrice(PPCContext& ctx, uint8_t* base, const char* kind, uint32_t component, uint32_t customer, uint32_t item) {
  const modlua::Verdict verdict = modlua::Decide(
      ctx, base, kShopPrice,
      Fields("shopkeeper=%u,customer=%u,player=%d,definition='0x%08x',kind='%s',price=%d", Handle(base, Word(base, component + 4u)),
             Handle(base, customer), modlua::PlayerOf(base, customer), ShopItemRecord(base, item), kind,
             static_cast<int32_t>(ctx.r3.u32)));
  double written = 0.0;
  if (verdict.Read("price", written) && written == written) {
    ctx.r3.u64 = static_cast<uint32_t>(written < 0.0 ? 0 : written > 2147483647.0 ? 2147483647 : static_cast<int32_t>(written));
  }
}

void RaiseDemonDoor(uint8_t* base, uint32_t component, const char* state) {
  modlua::QueueEvent(kDemonDoor, Fields("door=%u,state='%s',player=0", Handle(base, Word(base, component + 4u)), state));
}

}  // namespace

// chest_opened. CAction_OpenChest's slot 24 (r3 the action; its target's component 47 through
// Lua_Chest_Open, `bl` 0x82BBC188) brackets the chest's Open for its opener. Lua_Chest_Open (r3 the
// chest component 47, the chest at +4) plays "Open" and runs its first-open tail (sub_82A20D78: only
// while +0xF is 0, `stb r11,0xF(r29)` sets it). Raised after it. The keys and the guild seals are the
// chest's own getters (r3 the component; the seals' r4 the opener: own seals plus each linked chest r4
// has not collected), asked before.
REX_EXTERN(rex_CAction_OpenChest_Use) {
  const uint32_t previous = g_chest_opener;
  const uint32_t owner = Plausible(ctx.r3.u32) ? Word(base, Word(base, ctx.r3.u32 + 8u)) : 0u;
  g_chest_opener = Live(base, owner) ? owner : 0u;
  __imp__rex_CAction_OpenChest_Use(ctx, base);
  g_chest_opener = previous;
}

REX_EXTERN(rex_CECChest_Open) {
  const uint32_t component = ctx.r3.u32;
  const uint32_t chest = Plausible(component) ? Word(base, component + 4u) : 0u;
  if (!Live(base, chest) || !modlua::Wants(kChestOpened)) {
    __imp__rex_CECChest_Open(ctx, base);
    return;
  }
  const uint32_t opener = g_chest_opener;
  const bool first = Byte(base, component + 0xFu) == 0u;
  PPCContext work = Frame(ctx);
  work.r3.u64 = component;
  rex_CECChest_KeysNeeded(work, base);
  const int32_t keys = static_cast<int32_t>(work.r3.u32);
  int32_t seals = 0;
  if (opener != 0u) {
    work = Frame(ctx);
    work.r3.u64 = component;
    work.r4.u64 = opener;
    rex_CECChest_SealsNeeded(work, base);
    seals = static_cast<int32_t>(work.r3.u32);
  }
  __imp__rex_CECChest_Open(ctx, base);
  modlua::QueueEvent(kChestOpened, Fields("chest=%u,opener=%u,player=%d,keys=%d,seals=%d,first=%s", Handle(base, chest),
                                          Handle(base, opener), modlua::PlayerOf(base, opener), keys, seals, Bool(first)));
}

// item_bought. CECShopkeeper::Buy (r3 the shopkeeper component, the shopkeeper at +4; r4 the item's
// smart pointer; r5 the out pointer the bought item lands in; r6 the buyer): it prices through BuyPrice
// (r3, r6, r4), takes the gold from the buyer's purse (component 102) and moves the stock. Decided
// before it: a cancel skips it, the caller (sub_82B046E8) sees the out pointer empty and the item still
// in the shop — nothing paid, nothing moved. The closed shop's steal (r3 the basket, the shop at +12's
// smart pointer; r4 the thief; r5 the item) moves the stock and books the crime; a cancel skips both.
REX_EXTERN(rex_CECShopkeeper_Buy) {
  const uint32_t component = ctx.r3.u32;
  const uint32_t buyer = ctx.r6.u32;
  if (Plausible(component) && Live(base, buyer) && modlua::Wants(kItemBought) &&
      !DecideBought(ctx, base, Word(base, component + 4u), buyer, ctx.r4.u32,
                    ShopPriceOf(ctx, base, true, component, buyer, ctx.r4.u32), false)) {
    return;
  }
  __imp__rex_CECShopkeeper_Buy(ctx, base);
}

REX_EXTERN(rex_CShopBasket_Steal) {
  const uint32_t thief = ctx.r4.u32;
  if (Live(base, thief) && Plausible(ctx.r5.u32) && modlua::Wants(kItemBought) &&
      !DecideBought(ctx, base, 0u, thief, ctx.r5.u32, 0, true)) {
    return;
  }
  __imp__rex_CShopBasket_Steal(ctx, base);
}

// item_sold. CECShopkeeper::Sell (r3 the shopkeeper component; r4 the item's smart pointer; r5 the
// seller; r6 the sell-all byte; r7 how many the seller holds): it pays SellPrice (r3, r5, r4), times r7
// when r6 is set, into the seller's purse. Decided before it: a cancel skips it, the item stays.
REX_EXTERN(rex_CECShopkeeper_Sell) {
  const uint32_t component = ctx.r3.u32;
  const uint32_t seller = ctx.r5.u32;
  if (Plausible(component) && Live(base, seller) && modlua::Wants(kItemSold)) {
    const int32_t count = (ctx.r6.u32 & 0xFFu) != 0u ? static_cast<int32_t>(ctx.r7.u32) : 1;
    const int32_t price = ShopPriceOf(ctx, base, false, component, seller, ctx.r4.u32);
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kItemSold,
        Fields("shopkeeper=%u,seller=%u,player=%d,definition='0x%08x',price=%d,count=%d", Handle(base, Word(base, component + 4u)),
               Handle(base, seller), modlua::PlayerOf(base, seller), ShopItemRecord(base, ctx.r4.u32), price, count));
    if (verdict.cancelled) {
      return;
    }
  }
  __imp__rex_CECShopkeeper_Sell(ctx, base);
}

// shop_price. The shopkeeper's BuyPrice and SellPrice (r3 the shopkeeper component; r4 the customer; r5
// the item's smart pointer; r3 the gold on return): the screen's price, the can-afford test and Buy/Sell
// all ask these, so the written price is the shop's price everywhere.
REX_EXTERN(rex_CECShopkeeper_BuyPrice) {
  const uint32_t component = ctx.r3.u32;
  const uint32_t customer = ctx.r4.u32;
  const uint32_t item = ctx.r5.u32;
  __imp__rex_CECShopkeeper_BuyPrice(ctx, base);
  if (Plausible(component) && Live(base, customer) && modlua::Wants(kShopPrice)) {
    DecidePrice(ctx, base, "buy", component, customer, item);
  }
}

REX_EXTERN(rex_CECShopkeeper_SellPrice) {
  const uint32_t component = ctx.r3.u32;
  const uint32_t customer = ctx.r4.u32;
  const uint32_t item = ctx.r5.u32;
  __imp__rex_CECShopkeeper_SellPrice(ctx, base);
  if (Plausible(component) && Live(base, customer) && modlua::Wants(kShopPrice)) {
    DecidePrice(ctx, base, "sell", component, customer, item);
  }
}

// dig_spot_dug. Lua_DiggingSpot_OnDug (r3 the digging spot component 116, the spot at +4; r4 the digger,
// may be 0): it counts the dig and, for a criminal spot, books the crime. Read before it (OnDug clears
// the spot): by the dog (HasBeenDugByDog `lbz r3,0x1C(r3)`, disc +0x34) and criminal (`lbz r11,0x1F(r30)`,
// disc +0x37). Raised after it.
REX_EXTERN(rex_CECDiggingSpot_OnDug) {
  const uint32_t component = ctx.r3.u32;
  const uint32_t digger = ctx.r4.u32;
  const uint32_t spot = Plausible(component) ? Word(base, component + 4u) : 0u;
  const bool wanted = Live(base, spot) && modlua::Wants(kDigSpotDug);
  const bool by_dog = wanted && Byte(base, component + refii::guest::Pick(0x1Cu, 0x34u)) != 0u;
  const bool criminal = wanted && Byte(base, component + refii::guest::Pick(0x1Fu, 0x37u)) != 0u;
  __imp__rex_CECDiggingSpot_OnDug(ctx, base);
  if (wanted) {
    const uint32_t who = Live(base, digger) ? digger : 0u;
    modlua::QueueEvent(kDigSpotDug, Fields("spot=%u,digger=%u,player=%d,by_dog=%s,criminal=%s", Handle(base, spot), Handle(base, who),
                                           modlua::PlayerOf(base, who), Bool(by_dog), Bool(criminal)));
  }
}

// demon_door. The demon door's SpawnFace and DespawnFace (r3 the door component 73, the door at +4; the
// face-out byte at +0xC: `lbz r11,0xC(r3)` then `stb r11,0xC(r29)`), raised when it changed. Open (r3)
// returns early when the door (component 97) is already open or moving, else opens it and tail-calls
// its effects (sub_82ADC958, r3): 'opened' is raised from the effects while inside Open (the load
// restore, CECDemonDoor slot 12, reaches the effects without Open).
REX_EXTERN(rex_CECDemonDoor_SpawnFace) {
  const uint32_t component = ctx.r3.u32;
  const uint8_t before = Byte(base, component + 0xCu);
  __imp__rex_CECDemonDoor_SpawnFace(ctx, base);
  if (before == 0u && Byte(base, component + 0xCu) != 0u && modlua::Wants(kDemonDoor)) {
    RaiseDemonDoor(base, component, "face_out");
  }
}

REX_EXTERN(rex_CECDemonDoor_DespawnFace) {
  const uint32_t component = ctx.r3.u32;
  const uint8_t before = Byte(base, component + 0xCu);
  __imp__rex_CECDemonDoor_DespawnFace(ctx, base);
  if (before != 0u && Plausible(component) && Byte(base, component + 0xCu) == 0u && modlua::Wants(kDemonDoor)) {
    RaiseDemonDoor(base, component, "face_in");
  }
}

REX_EXTERN(rex_CECDemonDoor_Open) {
  ++g_demon_door_opening;
  __imp__rex_CECDemonDoor_Open(ctx, base);
  --g_demon_door_opening;
}

REX_EXTERN(rex_CECDemonDoor_OpenEffects) {
  const uint32_t component = ctx.r3.u32;
  __imp__rex_CECDemonDoor_OpenEffects(ctx, base);
  if (g_demon_door_opening > 0 && Plausible(component) && modlua::Wants(kDemonDoor)) {
    RaiseDemonDoor(base, component, "opened");
  }
}

// drink. Lua_PlayerDrunkenness_Drink (r3 the drunkenness component, the hero at +4; f1 the amount):
// `fadds` f1 into the beer reservoir +0x10 and counts +0x1C. Decided before it: a cancel returns
// (nothing written); a written amount replaces f1.
REX_EXTERN(rex_CECPlayerDrunkenness_Drink) {
  const uint32_t component = ctx.r3.u32;
  const uint32_t hero = Plausible(component) ? Word(base, component + 4u) : 0u;
  if (Live(base, hero) && modlua::Wants(kDrink)) {
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kDrink,
        Fields("hero=%u,player=%d,amount=%.9g,drinks=%d", Handle(base, hero), modlua::PlayerOf(base, hero),
               Fin(static_cast<float>(ctx.f1.f64)), static_cast<int32_t>(Word(base, component + 0x1Cu))));
    if (verdict.cancelled) {
      return;
    }
    double written = 0.0;
    if (verdict.Read("amount", written) && written == written) {
      ctx.f1.f64 = static_cast<float>(written);
    }
  }
  __imp__rex_CECPlayerDrunkenness_Drink(ctx, base);
}

// ---------------------------------------------------------------------------------------------
// THE SANCTUARY (Fable3/src/hooks/src/mod_events_hooks.cpp, after THE SHARED EVENTS). Fable 3 only:
// Fable 2's pause menu has no world of its own (its counterpart is 'paused', 98).
//
// The Sanctuary is the GUI game component: the game runs two components, the world (CMainGameComponent,
// vtable TU3 0x82001A84 / disc 0x82001AE8) and the GUI (CGUIGameComponent, vtable TU3 0x82096D7C /
// disc 0x82095030). The current one is the global TU3 0x83597034 / disc 0x83556540 (guest_addresses.inc
// already pairs them). Its slot 6 (+0x18) answers "is the GUI": the GUI's is lhStub_ReturnOne (`li r3,1`
// TU3 0x82A99990 / disc 0x82C13A28), the world's answers 0 (`li r3,0` TU3 0x82F5D180 / disc 0x82CE17A8);
// GameComponentSwitchManager.IsGUIActive is exactly that call (`lwz r9,0x18(r10); bctr` TU3 0x82252FE4 /
// disc 0x8267846C).
//
// The switch manager (r3 of both seams) holds the request: +0 the EGUIEntryReason
// (GameComponentSwitchManager.GetGUIEntryReason `lwz r3,0(r3)` TU3 0x823B5220 / disc 0x822DEC50) and +4 the
// entity's smart pointer (GetGUIEntryEntity `addi r3,r3,4; lwz r11,4(r3); lwz r10,0(r11)` TU3 0x827A2DBC.. /
// disc 0x826867FC..). Reasons (scripts/miscellaneous/guilevelenums.lua): 1 save select, 2 main menu (START),
// 3 dog, 4 furniture upgrade, 5 clothing, 6 weapons, 7 trading, 8 gift giving, 9 multiplayer room, 10 world
// map, 11 exit initialise (a load), 12 exit, 13 exit to the front end, 14 exit to save select, 15 quit,
// 16..19 quit to load / fail-quest load / join / PvP; 21 (TU3 only) the world map's close with a pending
// stop (`*(map+0xC0) == 0 ? 12 : 21`, sub_824ED850).

REX_EXTERN(__imp__rex_GameComponentSwitchManager_SwitchToNextComponent);
REX_EXTERN(__imp__rex_GameComponentSwitchManager_OnArrived);

namespace {

constexpr int32_t kSanctuary = 126;
constexpr int32_t kSanctuaryRequest = 127;

/// The last request's hero and that hero's player, as PlayerOf answered in the world the request left
/// (the arrival runs in the other component's world, where the leaving world's hero is no player).
uint32_t g_sanctuary_hero = 0u;
int32_t g_sanctuary_player = 0;
/// The last arrival's answer: 0 the world, 1 the Sanctuary. The game starts in the world (the first
/// arrival from the front end does not run the arrival: sub_8234BFC0 runs it only when game+0xBA is set).
int g_in_sanctuary = 0;

/// Slot 6 of the current component, as IsGUIActive asks it: 1 in the Sanctuary, 0 in the world, -1 when
/// there is no component.
int CurrentIsSanctuary(const PPCContext& ctx, uint8_t* base) {
  const uint32_t component = Word(base, kGame);
  if (!Plausible(component)) {
    return -1;
  }
  PPCContext work = Frame(ctx);
  if (!CallSlot(work, base, component, 6u)) {
    return -1;
  }
  return (work.r3.u32 & 0xFFu) != 0u ? 1 : 0;
}

}  // namespace

// sanctuary_request. GameComponentSwitchManager.SwitchToNextComponent (TU3 0x8239D5A8 / disc 0x82446CE8, the
// Lua native and every C++ switch: START sub_823A05F8 reason 2, the game actions sub_828BD968 5/6/7/8/10,
// GUI.ToggleWorldMap 10, the options box 1, gift giving sub_821A3AC0 8, the world map's close 12/21, the
// loads and quits 11..19). r3 the manager (`mr r31,r3` TU3 0x8239D5B4 / `mr r29,r3` disc 0x82446CF4), r4 the
// reason (`mr r30,r4`, stored `stw r30,0(r31)` 0x8239D5E0 / `stw r30,0(r29)` 0x82446D9C), r5 the entity
// (`mr r29,r5` / `mr r27,r5`, into the smart pointer at +4: `addi r3,r31,4` 0x8239D600 / `addi r31,r29,4`
// 0x82446DB8). The switch goes to the other component: TU3 passes `cntlzw` of slot 6's answer to
// sub_8239EA40 (0x8239D6AC..C8), disc posts it (0x82446EB0..CC), so to_sanctuary = slot 6 answers 0 now.
//
// Its first act is the gate (TU3 sub_8239EC38, disc sub_82447F78, paired exact): when the game's
// +0xBB is already set (`lwz r11,0xC(r3); lbz r10,0xBB(r11); bne` TU3 0x8239ED6C / disc 0x824480AC) a switch
// is under way and the game itself refuses this one, so nothing is asked. Otherwise the gate claims +0xBB
// (`stb r10,0xBB(r11)` 0x8239ED84 / 0x824480C4) and the switch proceeds.
//
// Decided before it. A cancel skips the whole function: nothing is written (the gate, its +0xBB claim and
// its component-37 call are inside it). It is honoured only for an ENTRY (to_sanctuary, reasons 1..10):
// the callers ignore r3 and go on (START plays its button sound sub_82333558 and returns; the options box
// clears its own +0x23 and returns; the game actions break out of their switch; gift giving clears its two
// smart pointers), so the hero stays in the world. A leave, a load, a quit or a restart (11 and up) is
// never refused: those callers have already torn down what the switch completes.
REX_EXTERN(rex_GameComponentSwitchManager_SwitchToNextComponent) {
  const uint32_t manager = ctx.r3.u32;
  const int32_t reason = static_cast<int32_t>(ctx.r4.u32);
  const uint32_t entity = ctx.r5.u32;
  const uint32_t game = Word(base, Word(base, kGame) + 0xCu);
  const bool under_way = !Plausible(game) || Byte(base, game + 0xBBu) != 0u;
  if (Plausible(manager) && !under_way) {
    const uint32_t hero = Live(base, entity) ? entity : 0u;
    g_sanctuary_hero = hero;
    g_sanctuary_player = modlua::PlayerOf(base, hero);
    if (modlua::Wants(kSanctuaryRequest)) {
      const bool to_sanctuary = CurrentIsSanctuary(ctx, base) == 0;
      const modlua::Verdict verdict =
          modlua::Decide(ctx, base, kSanctuaryRequest,
                         Fields("hero=%u,player=%d,reason=%d,to_sanctuary=%s", Handle(base, hero), g_sanctuary_player, reason,
                                Bool(to_sanctuary)));
      if (verdict.cancelled && to_sanctuary && reason >= 1 && reason <= 10) {
        return;
      }
    }
  }
  __imp__rex_GameComponentSwitchManager_SwitchToNextComponent(ctx, base);
}

// sanctuary. The switch manager's arrival (TU3 0x8239E198 / disc 0x82447758; the IDB pairs them, changed),
// run once per completed switch by the component's make-current sub_8234BFC0 / sub_82344C48 (`lwz r7,0x24(r26);
// lwz r3,4(r7); bl` TU3 0x8234C034..3C / disc 0x82344CBC..C4) under the game's initialised byte +0xBA. Both
// components' slot 1 store themselves as current before that (`stw r31,dword_83597034` GUI 0x82534A84, world
// 0x8234F614 / disc 0x8250F144, 0x82347CC4), so slot 6 of the current component is the arrival's side.
// r3 the manager (`mr r26,r3` 0x8239E1B0 / `mr r25,r3` 0x82447770): the reason is read before it as the
// arrival reads it (`lwz r11,0(r26)` 0x8239E1E8 / `lwz r11,0(r3)` 0x8244776C), the entity from +4 (the
// arrival's own `addi r3,r26,4` 0x8239E794 / `addi r3,r25,4` 0x82447C74). Raised after it, when the side
// changed (a restart arrives back in the world it left). The hero's player is the request's when the entity
// is the request's, else PlayerOf in the arrival's world.
REX_EXTERN(rex_GameComponentSwitchManager_OnArrived) {
  const uint32_t manager = ctx.r3.u32;
  const int32_t reason = static_cast<int32_t>(Word(base, manager));
  const uint32_t entity = Plausible(manager) ? Deref(base, manager + 4u) : 0u;
  __imp__rex_GameComponentSwitchManager_OnArrived(ctx, base);
  const int side = CurrentIsSanctuary(ctx, base);
  if (side < 0 || side == g_in_sanctuary) {
    return;
  }
  g_in_sanctuary = side;
  if (modlua::Wants(kSanctuary)) {
    const uint32_t hero = Live(base, entity) ? entity : 0u;
    const int32_t player = hero != 0u && hero == g_sanctuary_hero ? g_sanctuary_player : modlua::PlayerOf(base, hero);
    modlua::QueueEvent(kSanctuary, Fields("hero=%u,player=%d,entered=%s,reason=%d", Handle(base, hero), player, Bool(side == 1), reason));
  }
}

// ---------------------------------------------------------------------------------------------
// THE WEAPON EVENTS (Fable 3 only: Fable 2 weapons neither level nor earn augments).
// Paste into Fable3/src/hooks/src/mod_events_hooks.cpp after THE SHARED EVENTS.
//
// The weapon is the CustomisableWeapon component, ECType 289 (Lua_Inventory_InstantiateItem looks it
// up, `cmpwi cr6,r6,0x121` TU3 0x8241CF70 / disc 0x82400610, and hands it to the level setter as r3,
// `bl sub_826CF1D0` TU3 0x8241CFD8 / disc 0x82400678). Its +4 is the weapon entity. TU3 and disc pair
// by the TU3 IDB's disc comments (SetLevel "changed", AddAugment "exact", the condition check
// "changed", the level-setter wrapper sub_826CF1D0 "exact") and by the native table
// (TriggerSpecialLevelUp TU3 0x826CDC58 / disc 0x826A26B0); the changed bodies are the same
// instruction sequence with the component's fields moved by +0x18 on the disc.
// The owner: the weapon's Carried component (ECType 105, `lbz r9,0x69(r11)` TU3 0x826CEB68 / disc
// 0x826A35C0) through Carried.GetCarrierEntity (TU3 sub_82A1D6E8 / disc sub_8243C9E8: `addi r3,r3,0xC;
// lwz r11,4(r3); lwz r10,0(r11)`), i.e. Deref(carried + 0xC).

REX_EXTERN(__imp__rex_CECCustomisableWeapon_SetLevel);
REX_EXTERN(__imp__rex_CECCustomisableWeapon_TriggerSpecialLevelUp);
REX_EXTERN(__imp__rex_CECCustomisableWeapon_AddAugment);
REX_EXTERN(__imp__rex_CECCustomisableWeapon_CheckConditionalAugments);

namespace {

constexpr int32_t kWeaponLevelUp = 128;
constexpr int32_t kWeaponAugmentUnlocked = 129;
constexpr int32_t kWeaponUpgradeReady = 130;

constexpr uint32_t kCarriedComponent = 105u;

/// Inside CustomisableWeapon.TriggerSpecialLevelUp: a SetLevel then is the weapon's morph.
int g_weapon_level_up_trigger = 0;

/// The weapon's level: +0x20 TU3 / +0x38 disc (CustomisableWeapon.GetLevel `lwz r3,0x20(r3)` TU3
/// 0x82EAA858 / `lwz r3,0x38(r3)` disc 0x82A986C8; SetLevel's own `lwz r11,0x20(r3)` 0x826CEE4C / `lwz
/// r11,0x38(r3)` 0x826A3A64).
int32_t WeaponLevel(const uint8_t* base, uint32_t weapon) {
  return static_cast<int32_t>(Word(base, weapon + refii::guest::Pick(0x20u, 0x38u)));
}

/// Who carries the weapon component's entity, 0 for none.
uint32_t WeaponOwner(const uint8_t* base, uint32_t weapon) {
  const uint32_t carried = Component(base, Word(base, weapon + 4u), kCarriedComponent);
  const uint32_t owner = carried != 0u ? Deref(base, carried + 0xCu) : 0u;
  return Live(base, owner) ? owner : 0u;
}

/// The highest level the weapon's level table names: the table is {begin, end} at +0x6C/+0x70 TU3,
/// +0x84/+0x88 disc, 20-byte rows sorted by their +0 level (the level step's lower_bound: `lwz
/// r11,0x6C(r31); lwz r6,0x70(r31); li r5,0x14; divw.` TU3 0x826CCE78..88 / disc 0x826A18D0..E0, the key
/// `lwz r9,0(r11)` TU3 0x826CCEDC / disc 0x826A1934). 0 for an empty table.
int32_t WeaponTopLevel(const uint8_t* base, uint32_t weapon) {
  const uint32_t first = Word(base, weapon + refii::guest::Pick(0x6Cu, 0x84u));
  const uint32_t last = Word(base, weapon + refii::guest::Pick(0x70u, 0x88u));
  if (!Plausible(first) || last <= first || (last - first) % 20u != 0u || last - first > 20u * 64u) {
    return 0;
  }
  return static_cast<int32_t>(Word(base, last - 20u));
}

/// The met-condition augments waiting for the next level-up: the vector at +0x144 TU3 / +0x15C disc,
/// {begin +4, end +8} = +0x148/+0x14C TU3, +0x160/+0x164 disc, 8-byte rows (the condition check's
/// `lwz r11,0x14C(r26); addi r29,r26,0x144; lwz r10,0x148(r26)` TU3 0x826CE970..78 / `lwz
/// r11,0x164(r26); addi r29,r26,0x15C; lwz r10,0x160(r26)` disc 0x826A33C8..D0).
int32_t PendingAugments(const uint8_t* base, uint32_t weapon) {
  const uint32_t first = Word(base, weapon + refii::guest::Pick(0x148u, 0x160u));
  const uint32_t last = Word(base, weapon + refii::guest::Pick(0x14Cu, 0x164u));
  return last > first && last - first < 8u * 4096u ? static_cast<int32_t>((last - first) >> 3) : 0;
}

}  // namespace

// weapon_level_up. CustomisableWeapon's SetLevel (TU3 0x826CEE40 / disc 0x826A3A58; r3 the weapon
// component, r4 the target level, r5 the hero whose ability level set it: `mr r30,r4; mr r29,r5`
// 0x826CEE54/58 / 0x826A3A6C/70). Below the current level (`cmpw cr6,r4,r11` 0x826CEE5C / 0x826A3A74)
// it resets (sub_826CEF88 / sub_826A3BA0: level 1, every augmentation and weapon effect cleared), then
// steps up one level at a time (sub_826CCE08 / sub_826A1860, each step adding that level's augments)
// until it reaches r4. Its only caller, sub_826CF1D0 (`addi r4,r3,1; mr r5,r31; bl` TU3 0x826CF368..74
// / disc 0x826A3F80..8C), asks for the hero's ability level + 1; it is reached from
// TriggerSpecialLevelUp (through sub_826CF0B8, the morph), Inventory.InstantiateItem (a new weapon
// brought to the hero's level) and one more caller (TU3 sub_824C7DB8 / disc sub_825DEDF0). All three
// ignore its result and go on. Decided before it, when r4 differs from the level: a cancel skips it
// (the level stays; TriggerSpecialLevelUp has already cleared its wait flag and still applies the
// pending condition augments); a written level, at least 1 and at most the higher of r4 and the level
// table's top, replaces r4.
REX_EXTERN(rex_CECCustomisableWeapon_TriggerSpecialLevelUp) {
  ++g_weapon_level_up_trigger;
  __imp__rex_CECCustomisableWeapon_TriggerSpecialLevelUp(ctx, base);
  --g_weapon_level_up_trigger;
}

REX_EXTERN(rex_CECCustomisableWeapon_SetLevel) {
  const uint32_t weapon = ctx.r3.u32;
  const uint32_t entity = Plausible(weapon) ? Word(base, weapon + 4u) : 0u;
  const int32_t target = static_cast<int32_t>(ctx.r4.u32);
  if (Live(base, entity) && modlua::Wants(kWeaponLevelUp)) {
    const int32_t from = WeaponLevel(base, weapon);
    if (target != from) {
      const uint32_t owner = Live(base, ctx.r5.u32) ? ctx.r5.u32 : WeaponOwner(base, weapon);
      const modlua::Verdict verdict = modlua::Decide(
          ctx, base, kWeaponLevelUp,
          Fields("weapon=%u,owner=%u,player=%d,level_from=%d,level=%d,cause='%s'", Handle(base, entity), Handle(base, owner),
                 modlua::PlayerOf(base, owner), from, target, g_weapon_level_up_trigger > 0 ? "level_up" : "sync"));
      if (verdict.cancelled) {
        return;
      }
      double written = 0.0;
      if (verdict.Read("level", written) && written == written) {
        const int32_t table_top = WeaponTopLevel(base, weapon);
        const int32_t top = table_top > target ? table_top : target;
        const int32_t level = written < 1.0 ? 1 : written > static_cast<double>(top) ? top : static_cast<int32_t>(written);
        ctx.r4.u64 = static_cast<uint32_t>(level);
      }
    }
  }
  __imp__rex_CECCustomisableWeapon_SetLevel(ctx, base);
}

// weapon_augment_unlocked. CustomisableWeapon's AddAugment (TU3 0x826D1998 / disc 0x826A6650; r3 the
// weapon component; r4 the augment's reference, its +0 the augment record: `lwz r11,0(r4)` 0x826D19A4
// / 0x826A665C, and a 0 record is refused, `beq cr6,loc_826D1BCC` 0x826D19B4 / `beq cr6,loc_826A6884`
// 0x826A666C; r5 the source byte, `mr r29,r5` 0x826D19AC / 0x826A6664). It hands the augment to the
// weapon's Augmentable (ECType 104, AddAugmentationPacket) and syncs it. Two callers: the level step
// passes r5 = 1 (`li r5,1` TU3 0x826CCFC0 / disc 0x826A1A18, a level's augment), TriggerSpecialLevelUp
// r5 = 0 (`li r5,0` TU3 0x826CDD8C / disc 0x826A27E4, a met condition applied at the level-up); both
// loop on and ignore its result, and TriggerSpecialLevelUp empties the condition list afterwards.
// Decided before it: a cancel skips it, exactly the function's own refusal of a 0 record (nothing
// written); a refused condition augment is gone with the list.
REX_EXTERN(rex_CECCustomisableWeapon_AddAugment) {
  const uint32_t weapon = ctx.r3.u32;
  const uint32_t entity = Plausible(weapon) ? Word(base, weapon + 4u) : 0u;
  const uint32_t augment = Plausible(ctx.r4.u32) ? Word(base, ctx.r4.u32) : 0u;
  if (augment != 0u && Live(base, entity) && modlua::Wants(kWeaponAugmentUnlocked)) {
    const uint32_t owner = WeaponOwner(base, weapon);
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kWeaponAugmentUnlocked,
        Fields("weapon=%u,owner=%u,player=%d,augment='0x%08x',source='%s',level=%d", Handle(base, entity), Handle(base, owner),
               modlua::PlayerOf(base, owner), augment, (ctx.r5.u32 & 0xFFu) != 0u ? "level" : "condition",
               WeaponLevel(base, weapon)));
    if (verdict.cancelled) {
      return;
    }
  }
  __imp__rex_CECCustomisableWeapon_AddAugment(ctx, base);
}

// weapon_upgrade_ready. CustomisableWeapon's condition check (TU3 0x826CE920 / disc 0x826A3378; r3 the
// weapon component). On the authority only (`lwz r3,4(r3); bl sub_822092F0` TU3 0x826CE930/34 / `bl
// sub_82209D10` disc 0x826A338C; elsewhere it relays a packet and returns): each met condition of the
// weapon's augment definition is moved to the pending vector (PendingAugments), the new ones counted
// (`subf. r4,r25,r8` 0x826CE9E4 / 0x826A343C, then sub_826CEB08 books the hero's stat), and while any is
// pending the weapon waits to level up (`stb r9,0x13C(r26)` TU3 0x826CEA1C / `stb r9,0x154(r26)` disc
// 0x826A3474; TriggerSpecialLevelUp tests and clears the same byte, `lbz r11,0x13C(r3)` 0x826CDC64 /
// `lbz r11,0x154(r3)` 0x826A26BC). Reported after it, when the pending count grew.
REX_EXTERN(rex_CECCustomisableWeapon_CheckConditionalAugments) {
  const uint32_t weapon = ctx.r3.u32;
  const uint32_t entity = Plausible(weapon) ? Word(base, weapon + 4u) : 0u;
  const bool wanted = Live(base, entity) && modlua::Wants(kWeaponUpgradeReady);
  const int32_t before = wanted ? PendingAugments(base, weapon) : 0;
  __imp__rex_CECCustomisableWeapon_CheckConditionalAugments(ctx, base);
  if (wanted) {
    const int32_t pending = PendingAugments(base, weapon);
    if (pending > before) {
      const uint32_t owner = WeaponOwner(base, weapon);
      modlua::QueueEvent(kWeaponUpgradeReady,
                         Fields("weapon=%u,owner=%u,player=%d,added=%d,pending=%d,level=%d", Handle(base, entity), Handle(base, owner),
                                modlua::PlayerOf(base, owner), pending - before, pending, WeaponLevel(base, weapon)));
    }
  }
}

// ---- paste into Fable3/src/hooks/src/mod_events_hooks.cpp, after THE SHARED EVENTS block ----
// Fable 3 only: hand holding. Every offset below is the same on TU3 and disc (no Pick needed): both
// builds' bodies were compared instruction by instruction (manifest.tsv carries each line).

REX_EXTERN(__imp__rex_CECHandHolding_Start);
REX_EXTERN(__imp__rex_CECHandHolding_SetHandHolding);

namespace {

constexpr int32_t kHandHolding = 131;
constexpr int32_t kHandHoldingRequest = 132;

/// The leader the hand-holding component holds: the smart pointer at +0x30 (SetHandHolding's on path
/// stores GetLeadingEntity into it, `addi r29,r27,0x30` + `bl sub_82258B38` TU3 0x826C34A4/0x826C34B0,
/// disc `addi r29,r26,0x30` + `bl sub_82308B28` 0x8269816C/0x82698178; the off path copies it out and
/// clears it, `addi r31,r27,0x30` + `bl sub_824BAD00` TU3 0x826C380C/0x826C3818, disc 0x826984C0/0x826984CC).
uint32_t HandLeader(const uint8_t* base, uint32_t component) { return Deref(base, component + 0x30u); }

/// HandHolding.IsBeingDragged's own test (TU3 0x826C7F00 = disc 0x8269C968, exact): the leader at +0x30
/// is live (`addi r3,r3,0x30; lwz r11,0x34(r31)` then `lbz r11,0x94(r3)`), and the villager's
/// component 72 (`cmpwi cr6,r6,0x48`) has +0x30 == 1 (`lwz r11,0x30(r11); cmpwi cr6,r11,1`).
bool HandDragged(const uint8_t* base, uint32_t component, uint32_t villager) {
  if (!Live(base, HandLeader(base, component))) {
    return false;
  }
  const uint32_t ik = Component(base, villager, 72u);
  return ik != 0u && Word(base, ik + 0x30u) == 1u;
}

}  // namespace

// hand_holding_request. CECHandHolding's start (TU3 0x826C27C0 / disc 0x82697500; r3 the villager's
// hand-holding component 71, the villager at +4: `lwz r4,4(r22)` TU3 0x826C295C / disc 0x8269769C;
// r4 the leader, `mr r25,r4`; r5 forced, `mr r26,r5` then `stb r26,0xF(r22)` TU3 0x826C2B80 / disc
// 0x826978C0; r6 stored +0x13; r7 the face flag). Callers: the hero's interact on a villager
// (sub_821A3AC0 / disc sub_821A40D8, after Villager.IsValidForHandHolding), HandHolding.InitiateHandHolding,
// HandHolding.ForceDragging, and CECHandHolding slot 18 (the saved hold's restore from +0xB0, and the
// host's queue of relayed partner requests). On an online client the body only relays the request
// (`bl sub_82398538` TU3 0x826C286C / disc `bl sub_8238DE30` 0x826975AC) and the host replays it, so the
// event is decided on both machines.
// A cancel is the game's own refusal: its first test (the leader's interaction component 302 asked
// sub_8261AAA8 / disc sub_825F3240 with the villager) returns straight to the epilogue with r3's low byte
// 0 (`beq cr6,loc_826C3130` TU3 0x826C296C / disc `beq cr6,loc_82697E70` 0x826976AC) before any store.
// No caller reads r3: the interact path, InitiateHandHolding and ForceDragging ignore it; slot 18 tests
// the component's +0xC afterwards (`lbz r9,0xC(r26)` TU3 0x8222F3C8 / disc 0x822304D0), which stays 0.
// ForceDragging has already dropped the hand (SetHandHolding(c,0,1) before its Start), so a cancel there
// leaves the villager released; a refused saved-hold restore is not retried (slot 18 clears +0xB0).
REX_EXTERN(rex_CECHandHolding_Start) {
  const uint32_t component = ctx.r3.u32;
  const uint32_t leader = ctx.r4.u32;
  const uint32_t villager = Plausible(component) ? Word(base, component + 4u) : 0u;
  if (Live(base, villager) && Live(base, leader) && modlua::Wants(kHandHoldingRequest)) {
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kHandHoldingRequest,
        Fields("villager=%u,leader=%u,player=%d,forced=%s", Handle(base, villager), Handle(base, leader),
               modlua::PlayerOf(base, leader), Bool((ctx.r5.u32 & 0xFFu) != 0u)));
    if (verdict.cancelled) {
      ctx.r3.u64 = 0u;
      return;
    }
  }
  __imp__rex_CECHandHolding_Start(ctx, base);
}

// hand_holding. CECHandHolding's state setter (TU3 0x826C3140 / disc 0x82697E80; r3 the component, r4 on,
// r5 the throw-response flag). It compares +0xC with r4's byte (`lbz r11,0xC(r3); clrlwi r6,r4,24;
// cmplw cr6,r11,r6` TU3 0x826C314C..68 / disc `lbz r11,0xC(r3); clrlwi r28,r4,24; cmplw cr6,r11,r28`
// 0x82697E8C..A8) and stores it unconditionally (`stb r28,0xC(r27)` TU3 0x826C3400 / `stb r27,0xC(r26)`
// disc 0x8269813C), then runs its on or off tail even when unchanged: raised only when +0xC changed. The
// on call comes from the start's IK link (sub_82889D08 `li r4,1; bl sub_826C3140` TU3 0x8288A09C/0x8288A0A4),
// which sets the leader at +0x30 inside this call, so on is read after; off clears +0x30, so it is read
// before. forced is the start's +0xF. On a TU3 online client the host's state arrives as message 462
// (sub_826C9A70: `lbz r11,4(r3); stb r11,0xC(r4)`) without this function, and is not raised there.
REX_EXTERN(rex_CECHandHolding_SetHandHolding) {
  const uint32_t component = ctx.r3.u32;
  const uint32_t villager = Plausible(component) ? Word(base, component + 4u) : 0u;
  const bool on = (ctx.r4.u32 & 0xFFu) != 0u;
  const bool wanted = Live(base, villager) && modlua::Wants(kHandHolding);
  const bool was = wanted && Byte(base, component + 0xCu) != 0u;
  const uint32_t leader_before = wanted ? HandLeader(base, component) : 0u;
  const bool dragged_before = wanted && HandDragged(base, component, villager);
  const bool forced = wanted && Byte(base, component + 0xFu) != 0u;
  __imp__rex_CECHandHolding_SetHandHolding(ctx, base);
  if (!wanted || was == on || (Byte(base, component + 0xCu) != 0u) != on) {
    return;
  }
  const uint32_t leader = on ? HandLeader(base, component) : leader_before;
  const bool dragged = on ? HandDragged(base, component, villager) : dragged_before;
  modlua::QueueEvent(kHandHolding, Fields("villager=%u,leader=%u,player=%d,holding=%s,dragged=%s,forced=%s", Handle(base, villager),
                                          Handle(base, leader), modlua::PlayerOf(base, leader), Bool(on), Bool(dragged), Bool(forced)));
}

// =====================================================================================================
// PART A — append to Fable3/src/hooks/src/mod_events_hooks.cpp (after THE SHARED EVENTS block).
// interaction_started (133) and interaction_result (134). Addresses are TU3; the disc's are bound by
// name (config_rows.txt). Every offset below is the same on both builds unless Pick() says otherwise.
// =====================================================================================================

// THE VILLAGER INTERACTIONS (Fable 3 only: Fable 2 has no CECCharacterInteraction and no relationship
// mini-game — its strings hold neither "ECCharacterInteraction" nor "RelationshipHero").
REX_EXTERN(__imp__rex_CECCharacterInteraction_Perform);
REX_EXTERN(rex_Entity_IsLocallyManaged);
REX_EXTERN(__imp__rex_RelationshipGame_Finish);

namespace {

constexpr int32_t kInteractionStarted = 133;
constexpr int32_t kInteractionResult = 134;

/// The GUI singleton is the file's kGuiManager (*0x83596F84, disc *0x83556490: guest_addresses.inc
/// already pairs them); its interaction mode IM at +0x48 (`lwz r3,0x48(r11)` 0x82715460 / disc 0x826EBB08).
constexpr uint32_t kGuiInteractionMode = 0x48u;
/// IM+4 the open flag (the clear's `li r27,1; stb r27,4(r29)` 0x825EEE90 / disc 0x825C4000, the close's
/// `stb r26,4(r30)` 0x825F134C / disc 0x825C62E4), IM+0 the presentation.
constexpr uint32_t kImOpen = 4u;
/// IM+0x2C the hero's and IM+0x34 the target's smart pointers: the guest's own getters
/// `addi r3,r3,0x2C; lwz r11,4(r3); lwz r10,0(r11)` (0x823B528C / disc 0x82397774) and
/// `addi r3,r3,0x34; …` (0x82289B14 / disc 0x824ABC04) — Deref's layout.
constexpr uint32_t kImHero = 0x2Cu;
constexpr uint32_t kImTarget = 0x34u;
/// The hero's RelationshipHero component, ECType 0x11A (the finish's own lookup: `lbz r10,0x11A(r11)`
/// 0x827154E4, `cmpwi cr6,r6,0x11A` 0x82715524; disc 0x826EBB8C / 0x826EBBCC).
constexpr uint32_t kRelationshipHero = 0x11Au;
/// The mini-game's kind at +0xB0 of its controller: stored by its start sub_82713640 from r5
/// (`mr r26,r5` 0x82713664; `stw r26,0xB0(r29)` 0x82713DD4; disc 0x826E9D04 / 0x826EA474), read by the
/// finish (`lwz r11,0xB0(r27)` 0x82715594 / disc 0x826EBC3C). RelationshipHero.StartNPCInteraction passes
/// 1 (`li r5,1` 0x828CEEC0 / disc 0x8291A898), Start and StartPodiumInteraction pass 0 (0x828CECE4,
/// 0x828CF188 / disc 0x8291A6C4, 0x8291AB78).
constexpr uint32_t kGameKind = 0xB0u;

}  // namespace

// interaction_started. CECCharacterInteraction::Perform (r3 the performer's CharacterInteraction
// component, ECType 302, the hero at +4: `lwz r3,4(r3)` 0x8261BCA8 / disc 0x825F4470; r4 the target:
// `mr r27,r4`; r5 the interaction's record: `mr r28,r5`, stored `stw r28,0x70(r31)` / disc +0x80; r6, r7
// bytes). Its callers: the villager ring (sub_829ACBA0: r3 the hero's component 302, r7 = 0 `li r7,0`
// 0x829ACD08 / disc 0x82967EF0), CharacterInteraction.PerformInteraction (r6 = 0, r7 = 1: `li r7,1; li
// r6,0` 0x8261BFF0 / disc 0x825F4720) and the host's receive of a relayed one (r6, r7 the packet's
// +0x18, +0x19: `lbz r7,0x19(r31)` 0x8261C8A8 / disc 0x825F5010), so r7 is 1 for a script's.
// Its first act is the guest's local-management test on the hero (`bl sub_822092F0; clrlwi r11,r3,24;
// beq` 0x8261BCC4..D0 / disc sub_82209D10 0x825F448C..98): off the owning machine the body only relays
// the request (`bl sub_82398538` 0x8261BE18 / disc `bl sub_8238DE30` 0x825F45D8) and the owner runs it,
// so the seam asks the same test first and decides only where the interaction is performed.
// Decided before it: a cancel skips the body before any store (the first is +0x86 at 0x8261BCD8, after
// the test). Callers: the Lua glue returns 0 whatever Perform does; the ring calls its menu step
// sub_829B00C8 after it either way; the receive returns Perform's r3, which on the performing path is
// a smart-pointer destructor's (sub_825FFF80), so no caller tests it.
REX_EXTERN(rex_CECCharacterInteraction_Perform) {
  const uint32_t component = ctx.r3.u32;
  const uint32_t hero = Plausible(component) ? Word(base, component + 4u) : 0u;
  const uint32_t target = ctx.r4.u32;
  if (Live(base, hero) && modlua::Wants(kInteractionStarted)) {
    PPCContext work = Frame(ctx);
    work.r3.u64 = hero;
    rex_Entity_IsLocallyManaged(work, base);
    if ((work.r3.u32 & 0xFFu) != 0u) {
      const modlua::Verdict verdict = modlua::Decide(
          ctx, base, kInteractionStarted,
          Fields("hero=%u,player=%d,target=%u,interaction='0x%08x',scripted=%s", Handle(base, hero), modlua::PlayerOf(base, hero),
                 Handle(base, target), ctx.r5.u32, Bool((ctx.r7.u32 & 0xFFu) != 0u)));
      if (verdict.cancelled) {
        return;
      }
    }
  }
  __imp__rex_CECCharacterInteraction_Perform(ctx, base);
}

// interaction_result. The relationship mini-game's finish (r3 its controller, `mr r27,r3`; r4 success,
// a word: `cntlzw r11,r4; extrwi r31,r11,1,26` 0x8271544C/54 makes r31 = failed, `mr r25,r4` keeps it
// for message 139; disc 0x826EBAF4..0x826EBB00). It returns at once unless the GUI's IM is open (IM+4
// and IM+0: `lbz r10,4(r3)` 0x82715464, `lwz r11,0(r3)` 0x82715470) and its hero (Deref IM+0x2C, `bl`
// 0x82715490) is live with a RelationshipHero (0x11A) — the same test here. Then it stores the hero's
// RelationshipHero+0xC = success (`clrlwi r29,r31,24; cntlzw; extrwi; stb r10,0xC(r3)` 0x82715584..90 /
// disc 0x826EBC2C..38), steps the hero's count +484 up or down, runs the game table's OnSuccess or
// OnFail and sends message 142 (kind 0) or 139 (kinds 1, 2) — every one of them from r4. Decided
// before it: a written success replaces r4 (0 or 1). All four callers tail-call it (`return
// sub_82715438(...)`: sub_82714548 x2, sub_82714D40, sub_82715DF0), so nothing after reads r4.
REX_EXTERN(rex_RelationshipGame_Finish) {
  const uint32_t game = ctx.r3.u32;
  const uint32_t gui = Word(base, kGuiManager);
  const uint32_t im = Plausible(gui) ? Word(base, gui + kGuiInteractionMode) : 0u;
  const bool open = Plausible(im) && Byte(base, im + kImOpen) != 0u && Word(base, im) != 0u;
  const uint32_t hero = open ? Deref(base, im + kImHero) : 0u;
  if (Plausible(game) && Live(base, hero) && Component(base, hero, kRelationshipHero) != 0u && modlua::Wants(kInteractionResult)) {
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kInteractionResult,
        Fields("hero=%u,player=%d,target=%u,kind=%d,success=%s", Handle(base, hero), modlua::PlayerOf(base, hero),
               Handle(base, Deref(base, im + kImTarget)), static_cast<int32_t>(Word(base, game + kGameKind)), Bool(ctx.r4.u32 != 0u)));
    double written = 0.0;
    if (verdict.Read("success", written)) {
      ctx.r4.u64 = written != 0.0 ? 1u : 0u;
    }
  }
  __imp__rex_RelationshipGame_Finish(ctx, base);
}

// relationship_stage (kind 136) — Fable 3 only. Paste beside THE SHARED EVENTS in
// Fable3/src/hooks/src/mod_events_hooks.cpp (uses mod_seam.h, mod_lua.h).
//
// THE RECORD. A villager's opinion component (the 0x360 record map; GetCurrentRelationshipStage
// `addi r3,r3,0x360` TU3 0x8224DE10 / disc 0x8224EA78) holds one record per hero: the local hero's is
// the map object itself (sub_821FC460 answers `*out = a1` when r4 is the world's local hero), any other
// hero's is a map node's +0x38. The stage is the record's +0x90 (GetCurrentRelationshipStage
// `lwz r3,0x90(r11)` TU3 0x8224DE28 / disc 0x8224EA90; 2 with no record, `li r3,2` 0x8224DE3C /
// 0x8224EAA4). Values are the shipped ERelationshipStages (scripts/miscellaneous/opinionaxesenum.lua):
// 0 fear, 1 hate, 2 neutral, 3 friend, 4 love.
//
// THE WRITER. SetStage (TU3 0x82B7E4D8 / disc 0x82B49BF0, IDB "exact"; r3 the record, r4 the stage,
// r5 the local-hero byte that gates its tracked-stat bump) is the only writer of +0x90 reached by a
// call: `lwz r11,0x90(r3); cmpw cr6,r11,r4; beq; stw r4,0x90(r3)` (TU3 0x82B7E4E4..F8 / disc
// 0x82B49BFC..C10). It has five call sites in four functions, the same on both builds, and every one
// states the opinion component the record belongs to:
//   progress    0x82B7EFE0 / 0x82B4A6F0 (r3 record, r4 component: `mr r29,r4` 0x82B7EFF4 / 0x82B4A704;
//               bl SetStage 0x82B7F078 and 0x82B7F158 / 0x82B4A788 and 0x82B4A868). Reached from the
//               component's update sub_82233DB8 / 0x82234FF8 (`mr r30,r3`, `addi r31,r30,0x360`, `mr r4,r30`
//               into sub_821B5930 / 0x821B57F0, whose `mr r25,r4` is passed on `mr r4,r25` at 0x821B5A2C /
//               0x821B58EC) and from sub_828FF878 / 0x828A7EB8 through sub_82B7EE68 / 0x82B4A578 (`mr r4,r31`
//               with `addi r3,r31,0x360`; `mr r26,r4` ... `mr r4,r26`).
//   quest end   0x82B801F8 / 0x82B4B900 (r3 record `mr r29,r3`, r4 success, r5 component `mr r26,r5`;
//               bl SetStage 0x82B80230 / 0x82B4B938). Callers: Reject/EndRelationshipQuest (`mr r5,r31` /
//               `mr r5,r29` with the component they took `addi r3,rX,0x360` from) and progress (`mr r5,r29`
//               0x82B7F0AC / 0x82B4A7BC).
//   reset       Lua_OpinionReaction_ResetCurrentRelationshipStage 0x8290E668 / 0x828B6CC0 (r3 component
//               `mr r28,r3`, r4 hero `mr r31,r4`, r5 stage `mr r29,r5`; bl SetStage 0x8290E700 / 0x828B6D58).
//               Also reached from C++: Villager.AddParent and sub_827E58B8 (divorce, Lua_PlayerFamily_Divorce).
//   sync        sub_829140B8 / 0x828BC008, the net client's apply (r3 component `mr r31,r3`, r4 the packet
//               `mr r29,r4`; stage `lwz r4,8(r29)`, bl SetStage 0x829141A8 / 0x828BC0F8, local byte 0).
// So SetStage is hooked for the record and the change, and each caller brackets it with the component
// it states (and, for reset, the hero it states). The villager is the component's +4 (reset passes
// `lwz r30,4(r28)` as message 184's entity, `li r4,0xB8; mr r6,r30; mr r7,r31` 0x8290E79C..C8 /
// 0x828B6DF4..E20).
//
// THE HERO. For every caller but reset, the guest's own record->hero answer, sub_82B80640 / 0x82B4BD38
// (IDB "exact"), the one progress and quest end use (`mr r3,r26; bl` 0x82B7F17C / 0x82B4A88C, `mr r3,r29;
// bl` 0x82B80234 / 0x82B4B93C): r3 the record (`mr r5,r3`), matches each player slot of the game's +0x40
// table (`ld r11,8(r5); ld r10,0x40(r3)` then the +0x18 / +0x50 doubles), answers that slot's hero
// (sub_82A1D6E8: `addi r3,r3,0xC; lwz r11,4(r3); lwz r10,0(r11)`), else the world's local hero. Read
// only (its smart-pointer derefs release a dead block, as every guest deref does).
//
// After-only: the event reports the change once SetStage stored it. No cancel: each caller goes on as
// if the stage moved. Quest end reads +0x90 straight after (`v11 = *(rec+0x90)`, the friend/love rewards
// for 3 and 4), progress tests it for 2 and books the opinion swing for the step, reset rescales the
// opinion to the stated stage (sub_82B7E268, stage*0.4-1) and broadcasts message 184 with it. A skipped
// store would leave each of them disagreeing with the record.

REX_EXTERN(__imp__rex_OpinionRecord_SetStage);
REX_EXTERN(__imp__rex_OpinionRecord_Progress);
REX_EXTERN(__imp__rex_OpinionRecord_FinishQuest);
REX_EXTERN(__imp__rex_OpinionReaction_ResetCurrentRelationshipStage);
REX_EXTERN(__imp__rex_CECOpinionReaction_ApplyStageSync);
REX_EXTERN(rex_OpinionRecord_Hero);

namespace {

constexpr int32_t kRelationshipStage = 136;

/// The opinion component the running SetStage call belongs to, the hero when the caller states it,
/// and why: set by the four callers around their original, nested calls restore the outer one.
struct StageScope {
  uint32_t component;
  uint32_t hero;
  const char* cause;
};
StageScope g_stage_scope{0u, 0u, nullptr};

class StageScopeGuard {
 public:
  StageScopeGuard(uint32_t component, uint32_t hero, const char* cause) : outer_(g_stage_scope) {
    g_stage_scope = StageScope{Plausible(component) ? component : 0u, hero, cause};
  }
  ~StageScopeGuard() { g_stage_scope = outer_; }

 private:
  StageScope outer_;
};

}  // namespace

REX_EXTERN(rex_OpinionRecord_Progress) {
  const StageScopeGuard scope(ctx.r4.u32, 0u, "opinion");
  __imp__rex_OpinionRecord_Progress(ctx, base);
}

REX_EXTERN(rex_OpinionRecord_FinishQuest) {
  const StageScopeGuard scope(ctx.r5.u32, 0u, "quest");
  __imp__rex_OpinionRecord_FinishQuest(ctx, base);
}

REX_EXTERN(rex_OpinionReaction_ResetCurrentRelationshipStage) {
  const StageScopeGuard scope(ctx.r3.u32, ctx.r4.u32, "reset");
  __imp__rex_OpinionReaction_ResetCurrentRelationshipStage(ctx, base);
}

REX_EXTERN(rex_CECOpinionReaction_ApplyStageSync) {
  const StageScopeGuard scope(ctx.r3.u32, 0u, "sync");
  __imp__rex_CECOpinionReaction_ApplyStageSync(ctx, base);
}

// relationship_stage. SetStage (r3 the record, r4 the stage, r5 the local byte): raised after it when
// +0x90 changed and the call came through one of the four callers above.
REX_EXTERN(rex_OpinionRecord_SetStage) {
  const uint32_t record = ctx.r3.u32;
  const int32_t stage = static_cast<int32_t>(ctx.r4.u32);
  const StageScope scope = g_stage_scope;
  const bool wanted = Plausible(record) && scope.component != 0u && modlua::Wants(kRelationshipStage);
  const int32_t previous = wanted ? static_cast<int32_t>(Word(base, record + 0x90u)) : stage;
  __imp__rex_OpinionRecord_SetStage(ctx, base);
  if (!wanted || previous == stage || static_cast<int32_t>(Word(base, record + 0x90u)) != stage) {
    return;
  }
  const uint32_t villager = Word(base, scope.component + 4u);
  if (!Live(base, villager)) {
    return;
  }
  uint32_t hero = scope.hero;
  if (!Live(base, hero)) {
    PPCContext work = Frame(ctx);
    work.r3.u64 = record;
    rex_OpinionRecord_Hero(work, base);
    hero = Live(base, work.r3.u32) ? work.r3.u32 : 0u;
  }
  modlua::QueueEvent(kRelationshipStage,
                     Fields("villager=%u,hero=%u,player=%d,stage=%d,previous=%d,cause='%s'", Handle(base, villager), Handle(base, hero),
                            modlua::PlayerOf(base, hero), stage, previous, scope.cause != nullptr ? scope.cause : ""));
}

// =============================================================================================
// WAVE 3 / a2 — paste at the end of Fable3/src/hooks/src/mod_events_hooks.cpp (after "THE SHARED
// EVENTS"; it uses that file's anonymous-namespace helpers CharString and the seam helpers).
// Kinds 140..152. Every TU3 address below has its disc twin in Fable3/versions/Fable3/refiii_config.toml
// under the same rex_ name (config_rows.txt); the disc instruction is quoted after the TU3 one.
// =============================================================================================

// THE DOG, THE DIVE SPOTS, VOMITING, THE JOBS AND THE GUILD VOICE (Fable 2 raises the shared ones too).
REX_EXTERN(__imp__rex_CECDog_SetEntityBeingDug);
REX_EXTERN(__imp__rex_CECDog_SetLeadingHero);
REX_EXTERN(__imp__rex_CECDog_UnlockTrick);
REX_EXTERN(__imp__rex_CECDogStats_TrainingLevelChanged);
REX_EXTERN(__imp__rex_CECDiveSpot_Use);
REX_EXTERN(__imp__rex_CECPlayerDrunkenness_Vomit);
REX_EXTERN(__imp__rex_CECJobSign_Start);
REX_EXTERN(rex_Entity_IsLocallyManaged);
REX_EXTERN(__imp__rex_CECStats_SetJobLevel);
REX_EXTERN(rex_CECStats_GetJobLevel);
REX_EXTERN(__imp__rex_CECStats_SetJobEarned);
REX_EXTERN(rex_CECStats_GetJobEarned);
REX_EXTERN(__imp__rex_CECStats_SetJobWorked);
REX_EXTERN(__imp__rex_GUI_SetJobMultiplierLevel);
REX_EXTERN(__imp__rex_GUI_TurnJobMultiplierOff);
REX_EXTERN(__imp__rex_GuildMessageManager_PlayNext);

namespace {

constexpr int32_t kDogDig = 137;
constexpr int32_t kDogLeading = 138;
constexpr int32_t kDogTrickLearned = 139;
constexpr int32_t kDogLearned = 140;
constexpr int32_t kDiveSpotUsed = 141;
constexpr int32_t kVomit = 142;
constexpr int32_t kJobStarted = 143;
constexpr int32_t kJobLevel = 144;
constexpr int32_t kJobEarnings = 145;
constexpr int32_t kJobWorked = 146;
constexpr int32_t kJobMultiplier = 147;
constexpr int32_t kGuildMessage = 149;

/// DiggingSpot (Lua_DiggingSpot_GetECType's `li r3,0x74`; the dig setter's own lookup `cmpwi r6,0x74`
/// TU3 0x82809114 / disc 0x8280276C) and Dog (Dog.GetECType `li r3,0x76` TU3 0x82372C58 / disc 0x827F3068).
constexpr uint32_t kDiggingSpotComponent = 116u;
constexpr uint32_t kDogComponent = 118u;

/// Whose dog: its master, the Dog component's smart pointer at +0xDC (Dog.GetMaster: `addi r3,r3,0xDC`
/// TU3 0x8229C3E4 / disc 0x8229E08C). 0 for a dog of neither hero.
int32_t DogPlayer(const uint8_t* base, uint32_t dog_component) {
  return Plausible(dog_component) ? modlua::PlayerOf(base, Deref(base, dog_component + 0xDCu)) : 0;
}

/// The job record's getters (r3 the stats component, r4 the CCharString* job name; r3 the value): the
/// record is the stats' +0x4C map keyed by the name's FNV-1 (`addi r3,r3,0x4C`), level +0, money earned
/// +4, times worked +8 (`lwz r3,0(r3)` / `lwz r3,4(r3)`). Find-or-insert, as the setters are.
int32_t JobValue(const PPCContext& ctx, uint8_t* base, bool earned, uint32_t stats, uint32_t job) {
  PPCContext work = Frame(ctx);
  work.r3.u64 = stats;
  work.r4.u64 = job;
  if (earned) {
    rex_CECStats_GetJobEarned(work, base);
  } else {
    rex_CECStats_GetJobLevel(work, base);
  }
  return static_cast<int32_t>(work.r3.u32);
}

}  // namespace

// dog_dig. Dog.SetEntityBeingDug (r3 the Dog component 118, the dog at +4; r4 the entity it digs, handed
// on untouched: `mr r31,r3` then `bl` TU3 0x82808FAC / disc 0x82802604, the body's smart-pointer
// constructor taking r4 `bl sub_8221D310` 0x8280900C / `bl sub_8221E2E8` 0x82802664): it keeps the target
// (+0x134) and, when the target is live and has a DiggingSpot (116), marks it dug by a dog (`stb
// r11,0x1C(r31)` TU3 0x82809178, `stb r11,0x34(r31)` disc 0x828027D0). Raised after it. The C++ callers
// (the network resync, TU3 sub_82809D40 / sub_8280A978, disc sub_82803BF0) call the body, not this native:
// a replica's dig is not raised. `first`: the spot's mark was clear before.
REX_EXTERN(rex_CECDog_SetEntityBeingDug) {
  const uint32_t component = ctx.r3.u32;
  const uint32_t target = ctx.r4.u32;
  const uint32_t dog = Plausible(component) ? Word(base, component + 4u) : 0u;
  if (!Live(base, dog) || !Live(base, target) || !modlua::Wants(kDogDig)) {
    __imp__rex_CECDog_SetEntityBeingDug(ctx, base);
    return;
  }
  const uint32_t spot = Component(base, target, kDiggingSpotComponent);
  const bool first = spot != 0u && Byte(base, spot + refii::guest::Pick(0x1Cu, 0x34u)) == 0u;
  __imp__rex_CECDog_SetEntityBeingDug(ctx, base);
  modlua::QueueEvent(kDogDig, Fields("dog=%u,spot=%u,player=%d,first=%s", Handle(base, dog), Handle(base, target),
                                     DogPlayer(base, component), Bool(first)));
}

// dog_leading. Dog.SetIsInLeadingHeroBehaviour (r3 the Dog component; r4 quest, a byte: TU3 `mr r30,r4`
// 0x828069B0 then `clrlwi r10,r30,24` 0x828069C8, disc `clrlwi r11,r4,24` 0x82800010): it sets the
// leading byte +0x16 (`stb r29,0x16(r3)` TU3 0x828069B4, `stb r30,0x16(r3)` disc 0x82800014) and tells
// the hero's HUD what the dog found. Not a quest: the find is the smart pointer at +0x104 while +0x88 is
// negative (`lwz r11,0x88(r31)` TU3 0x828069DC / `lwz r11,0x88(r3)` disc 0x82800028; `addi r3,r31,0x104`
// TU3 0x828069F0 / `addi r3,r29,0x104` disc 0x8280003C, then lhSmartPtr_Deref). Raised after it, every
// call (the dog brain's script makes it once per lead).
REX_EXTERN(rex_CECDog_SetLeadingHero) {
  const uint32_t component = ctx.r3.u32;
  const bool quest = (ctx.r4.u32 & 0xFFu) != 0u;
  const uint32_t dog = Plausible(component) ? Word(base, component + 4u) : 0u;
  __imp__rex_CECDog_SetLeadingHero(ctx, base);
  if (!Live(base, dog) || !modlua::Wants(kDogLeading)) {
    return;
  }
  const uint32_t target =
      !quest && static_cast<int32_t>(Word(base, component + 0x88u)) < 0 ? Deref(base, component + 0x104u) : 0u;
  modlua::QueueEvent(kDogLeading, Fields("dog=%u,player=%d,target=%u,quest=%s", Handle(base, dog), DogPlayer(base, component),
                                         Handle(base, target), Bool(quest)));
}

// dog_trick_learned. Dog.UnlockTrickIfNecessary (r3 the Dog component, `mr r29,r3` TU3 0x82803EA4 / `mr
// r28,r3` disc 0x827FD4F4; r4 the {hash, record} pair, `mr r26,r4` 0x82803EAC / 0x827FD4FC; r5 show, `mr
// r31,r5` 0x82803EC8 / 0x827FD518): for an expression-category-7 record whose hash (`lwz r9,0(r26)`
// 0x82803F14 / 0x827FD564) is not on the unlocked list +0x2A4..+0x2A8 (`lwz r11,0x2A4(r29); lwz
// r10,0x2A8(r29)` 0x82803F04/08, disc r28 0x827FD554/58), it appends the pair (`bl` 0x82803F4C /
// 0x827FD59C) and, when r5 is set (`clrlwi r11,r31,24` 0x82803F50 / 0x827FD5A0), plays the tutorial and
// DogLearningFX. A training level-up unlocks its tricks with r5 clear (sub_82653D58) and announces through
// dog_learned. Raised after it, when the list grew.
REX_EXTERN(rex_CECDog_UnlockTrick) {
  const uint32_t component = ctx.r3.u32;
  const uint32_t pair = ctx.r4.u32;
  const bool shown = (ctx.r5.u32 & 0xFFu) != 0u;
  const uint32_t dog = Plausible(component) ? Word(base, component + 4u) : 0u;
  if (!Live(base, dog) || !Plausible(pair) || !modlua::Wants(kDogTrickLearned)) {
    __imp__rex_CECDog_UnlockTrick(ctx, base);
    return;
  }
  const uint32_t trick = Word(base, pair);
  const uint32_t before = (Word(base, component + 0x2A8u) - Word(base, component + 0x2A4u)) / 8u;
  __imp__rex_CECDog_UnlockTrick(ctx, base);
  const uint32_t after = (Word(base, component + 0x2A8u) - Word(base, component + 0x2A4u)) / 8u;
  if (after > before) {
    modlua::QueueEvent(kDogTrickLearned, Fields("dog=%u,player=%d,trick='0x%08x',shown=%s,tricks=%u", Handle(base, dog),
                                                DogPlayer(base, component), trick, Bool(shown), after));
  }
}

// dog_learned. The training level's notice (r3 the DogStats component 242, the dog at +4: `lwz r4,4(r31)`
// TU3 0x82653BC4 / disc 0x8262B3EC; r4 the track 0 explore, 1 combat, 2 charisma, `cmpwi r4,3; bge`
// 0x82653B60 / 0x8262B388; r5 up, `clrlwi r11,r28,24` 0x82653B8C / 0x8262B3B4). Its callers write the
// level first and call it only on a change that is not silent (TU3 sub_82653688 / sub_82653790, disc
// sub_8262AEE8 / Lua_DogStats_SetTrainingLevel and the disc's two network receivers sub_82802DD8 /
// sub_82802F20). The level is +0x34 + 4*track (`addi r11,r4,0xD; slwi r29,r11,2; lwzx r10,r29,r3`
// 0x82653B68..70 / 0x8262B390..98), shown only when 0 or more (`cmpwi r10,0; blt` 0x82653B74 /
// 0x8262B39C); the XP +0x28 + 4*track (GetTrainingXP `addi r11,r4,0xA` 0x82653778 / 0x8262AFD0).
// Raised after it.
REX_EXTERN(rex_CECDogStats_TrainingLevelChanged) {
  const uint32_t stats = ctx.r3.u32;
  const uint32_t track = ctx.r4.u32;
  const bool up = (ctx.r5.u32 & 0xFFu) != 0u;
  __imp__rex_CECDogStats_TrainingLevelChanged(ctx, base);
  if (!Plausible(stats) || track > 2u || !modlua::Wants(kDogLearned)) {
    return;
  }
  const uint32_t dog = Word(base, stats + 4u);
  const int32_t level = static_cast<int32_t>(Word(base, stats + 0x34u + 4u * track));
  if (!Live(base, dog) || level < 0) {
    return;
  }
  modlua::QueueEvent(kDogLearned, Fields("dog=%u,player=%d,track=%u,level=%d,up=%s,xp=%d", Handle(base, dog),
                                         DogPlayer(base, Component(base, dog, kDogComponent)), track, level, Bool(up),
                                         static_cast<int32_t>(Word(base, stats + 0x28u + 4u * track))));
}

// dive_spot_used. The dive spot's use (r3 the DiveSpot component 117, `mr r30,r3` TU3 0x82A143A0 / disc
// 0x829D23F0; r4 the diver, `mr r31,r4` 0x82A143A4 / 0x829D23F4): it counts the find on the diver's stats
// (`bl` 0x82A14474 / 0x829D24C4) and runs the spot's script (`bl` 0x82A14480 / 0x829D24D0; online TU3
// takes a lock first and runs it later). Its one caller, CAction_SwimDiveLevelExit's slot 23, hands it
// the target's component 117 (`cmpwi r10,0x75` TU3 0x829591B0 / disc 0x82BD6BF8) and the action's owner
// (`mr r4,r3; mr r3,r31` 0x829591E4/E8 / 0x82BD6C2C/30). Raised after it.
REX_EXTERN(rex_CECDiveSpot_Use) {
  const uint32_t component = ctx.r3.u32;
  const uint32_t diver = ctx.r4.u32;
  const uint32_t spot = Plausible(component) ? Word(base, component + 4u) : 0u;
  const bool wanted = Live(base, spot) && Live(base, diver) && modlua::Wants(kDiveSpotUsed);
  __imp__rex_CECDiveSpot_Use(ctx, base);
  if (wanted) {
    modlua::QueueEvent(kDiveSpotUsed,
                       Fields("spot=%u,diver=%u,player=%d", Handle(base, spot), Handle(base, diver), modlua::PlayerOf(base, diver)));
  }
}

// vomit. The drunkenness component's vomit (r3 the component, `mr r30,r3` TU3 0x827E1C24 / disc
// 0x829848F4; the hero at +4, `lwz r10,4(r30)` 0x827E1C64 / 0x82984934): when the hero's action manager
// takes the "Vomit" action it plays DrunkVomit, empties the beer reservoir and counts NumberOfVomits +0x20
// (`lwz r11,0x20(r30)` 0x827E1E78 / 0x82984B48, `stw r5,0x20(r30)` 0x827E1EA4 / 0x82984B74). Raised
// after it, when the count moved (a refused action changes nothing and the update asks again).
REX_EXTERN(rex_CECPlayerDrunkenness_Vomit) {
  const uint32_t component = ctx.r3.u32;
  const uint32_t before = Word(base, component + 0x20u);
  __imp__rex_CECPlayerDrunkenness_Vomit(ctx, base);
  const uint32_t after = Word(base, component + 0x20u);
  if (after == before || !Plausible(component) || !modlua::Wants(kVomit)) {
    return;
  }
  const uint32_t hero = Word(base, component + 4u);
  if (Live(base, hero)) {
    modlua::QueueEvent(kVomit, Fields("hero=%u,player=%d,vomits=%d", Handle(base, hero), modlua::PlayerOf(base, hero),
                                      static_cast<int32_t>(after)));
  }
}

// job_started. The job sign's start (the JobSign component 311, the sign at +4): it fills the session's
// Job Data table (JobSign, JobPlayer, JobName = the CCharString at +0x28: `addi r4,r30,0x28` TU3 0x82B1A8BC
// / `addi r4,r29,0x28` disc 0x82ADBA40) and runs the sign's start function, the LuaRef at +0x50 (`lwz
// r11,0x50(r30)` TU3 0x82B1A690 / `lwz r11,0x50(r29)` disc 0x82ADB88C). THE BUILDS DIFFER:
//   TU3 (r3 sign, `mr r30,r3` 0x82B1A530; r4 the table; r5 a byte, `clrlwi r11,r29,24` 0x82B1A570, set
//       for player 2: the world's +0x84 holder sub_823BB4C0 `addi r3,r3,0x84`, else +0x7C sub_821FB318
//       `addi r3,r3,0x7C`, the holders modlua::Hero reads): r3 = 1 only once the start ran (`li r3,1`
//       0x82B1A94C; 0 at 0x82B1A684 when it is locked, deferred online or has no start function). The
//       deferred start re-enters from the lock retry (sub_8228ADF8) and is raised then.
//   disc (r3 sign `mr r29,r3` 0x82ADB83C; r4 the hero `mr r27,r4` 0x82ADB840; r5 the table): it starts
//       when the hero is live (`lbz r11,0x94(r4)` 0x82ADB854), this machine's (`bl sub_82209D10`
//       0x82ADB87C, rex_Entity_IsLocallyManaged) and +0x50 is set; a remote hero's start is sent to its machine,
//       which raises it there. Tested before the call.
// Raised after it.
REX_EXTERN(rex_CECJobSign_Start) {
  const uint32_t component = ctx.r3.u32;
  const uint32_t sign = Plausible(component) ? Word(base, component + 4u) : 0u;
  if (!Live(base, sign) || !modlua::Wants(kJobStarted)) {
    __imp__rex_CECJobSign_Start(ctx, base);
    return;
  }
  const std::string job = CharString(base, component + 0x28u);
  uint32_t hero = 0u;
  bool started = false;
  if (refii::guest::Current() == refii::guest::Build::kDisc) {
    hero = ctx.r4.u32;
    if (Live(base, hero) && Word(base, component + 0x50u) != 0u) {
      PPCContext work = Frame(ctx);
      work.r3.u64 = hero;
      rex_Entity_IsLocallyManaged(work, base);
      started = (work.r3.u32 & 0xFFu) != 0u;
    }
    __imp__rex_CECJobSign_Start(ctx, base);
  } else {
    hero = modlua::Hero(base, (ctx.r5.u32 & 0xFFu) != 0u ? 2 : 1);
    __imp__rex_CECJobSign_Start(ctx, base);
    started = ctx.r3.u32 == 1u;
  }
  if (started) {
    modlua::QueueEvent(kJobStarted, Fields("sign=%u,hero=%u,player=%d,job='%s'", Handle(base, sign), Handle(base, hero),
                                           modlua::PlayerOf(base, hero), job.c_str()));
  }
}

// job_level. Stats.SetJobLevel (r3 the stats component 15, the hero at +4; r4 the CCharString* job name,
// `lwz r11,0(r4)` TU3 0x823EBB50 / disc 0x823CA488; r5 the level, `mr r31,r5` 0x823EBB54 / 0x823CA48C):
// it writes the record's level and zeroes its times worked (`stw r31,0(r3); stw r11,8(r3)` 0x823EBBB8/BC /
// 0x823CA4F0/F4). Only the Lua native calls it. Decided before it: a cancel returns (the glue does nothing
// after the call), a written level replaces r5. `previous` from the record's getter.
REX_EXTERN(rex_CECStats_SetJobLevel) {
  const uint32_t stats = ctx.r3.u32;
  const uint32_t job = ctx.r4.u32;
  const uint32_t hero = Plausible(stats) ? Word(base, stats + 4u) : 0u;
  if (Live(base, hero) && Plausible(job) && modlua::Wants(kJobLevel)) {
    const int32_t previous = JobValue(ctx, base, false, stats, job);
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kJobLevel,
        Fields("hero=%u,player=%d,job='%s',level=%d,previous=%d", Handle(base, hero), modlua::PlayerOf(base, hero),
               CharString(base, job).c_str(), static_cast<int32_t>(ctx.r5.u32), previous));
    if (verdict.cancelled) {
      return;
    }
    double written = 0.0;
    if (verdict.Read("level", written) && written == written) {
      ctx.r5.u64 = static_cast<uint32_t>(written < 0.0 ? 0 : written > 2147483647.0 ? 2147483647 : static_cast<int32_t>(written));
    }
  }
  __imp__rex_CECStats_SetJobLevel(ctx, base);
}

// job_earnings. Stats.SetTotalMoneyEarnedForJob (r3 stats; r4 the job; r5 the total, `mr r31,r5` TU3
// 0x823EBD64 / disc 0x823CA69C): the record's +4 (`stw r31,4(r3)` 0x823EBDC4 / 0x823CA6FC). Only the Lua
// native calls it. Raised after it, with the total it replaced (the getter, before).
REX_EXTERN(rex_CECStats_SetJobEarned) {
  const uint32_t stats = ctx.r3.u32;
  const uint32_t job = ctx.r4.u32;
  const int32_t total = static_cast<int32_t>(ctx.r5.u32);
  const uint32_t hero = Plausible(stats) ? Word(base, stats + 4u) : 0u;
  if (!Live(base, hero) || !Plausible(job) || !modlua::Wants(kJobEarnings)) {
    __imp__rex_CECStats_SetJobEarned(ctx, base);
    return;
  }
  const int32_t previous = JobValue(ctx, base, true, stats, job);
  const std::string name = CharString(base, job);
  __imp__rex_CECStats_SetJobEarned(ctx, base);
  modlua::QueueEvent(kJobEarnings, Fields("hero=%u,player=%d,job='%s',total=%d,earned=%d", Handle(base, hero),
                                          modlua::PlayerOf(base, hero), name.c_str(), total, total - previous));
}

// job_worked. Stats.SetTimesWorkedJobThisLevel (r3 stats; r4 the job; r5 the count, `mr r31,r5` TU3
// 0x823EBE74 / disc 0x823CA7AC): the record's +8 (`stw r31,8(r3)` 0x823EBED4 / 0x823CA80C). Only the Lua
// native calls it. Raised after it, with the record's level.
REX_EXTERN(rex_CECStats_SetJobWorked) {
  const uint32_t stats = ctx.r3.u32;
  const uint32_t job = ctx.r4.u32;
  const int32_t times = static_cast<int32_t>(ctx.r5.u32);
  const uint32_t hero = Plausible(stats) ? Word(base, stats + 4u) : 0u;
  __imp__rex_CECStats_SetJobWorked(ctx, base);
  if (Live(base, hero) && Plausible(job) && modlua::Wants(kJobWorked)) {
    modlua::QueueEvent(kJobWorked, Fields("hero=%u,player=%d,job='%s',times=%d,level=%d", Handle(base, hero),
                                          modlua::PlayerOf(base, hero), CharString(base, job).c_str(), times,
                                          JobValue(ctx, base, false, stats, job)));
  }
}

// job_multiplier. GUI.SetJobMultiplierLevel (r3 the level: `mr r30,r3` TU3 0x8254CB0C / disc 0x8251CDAC,
// handed to the HUD element `mr r4,r30` 0x8254CB2C / 0x8251CDCC) and GUI.TurnJobMultiplierOff (r3 a byte,
// `clrlwi r10,r3,24` 0x8254CB64 / 0x8251CE04: slide 4 when set, 5 when clear, only while shown). The job
// script states them; no hero is stated. Raised after each.
REX_EXTERN(rex_GUI_SetJobMultiplierLevel) {
  const int32_t level = static_cast<int32_t>(ctx.r3.u32);
  __imp__rex_GUI_SetJobMultiplierLevel(ctx, base);
  if (modlua::Wants(kJobMultiplier)) {
    modlua::QueueEvent(kJobMultiplier, Fields("level=%d,on=true,player=0", level));
  }
}

REX_EXTERN(rex_GUI_TurnJobMultiplierOff) {
  __imp__rex_GUI_TurnJobMultiplierOff(ctx, base);
  if (modlua::Wants(kJobMultiplier)) {
    modlua::QueueEvent(kJobMultiplier, Fields("level=0,on=false,player=0"));
  }
}

// guild_message. The guild message manager's play-next (r3 the manager, `mr r27,r3`): with a message
// queued (`lwz r11,0x1C(r3)` count, TU3 0x82297D8C / disc 0x82299974), the first node (`lwz r11,0x18(r3);
// lwz r29,0(r11)` 0x82297D9C/A4, disc `lwz r30,0(r11)` 0x82299984/8C) is played: its +8 the message's
// text key (`lwz r4,8(r29)` 0x82297EBC / `lwz r4,8(r30)` 0x82299AA4), +0xC the narrator's (`lwz
// r8,0xC(r31)` 0x82297F0C / 0x82299AE8), voiced on the hero and subtitled, made the manager's current
// (`lwz r11,8(r29); stw r11,8(r27)` 0x82297FFC/0x82298000, disc 0x82299BC4/C8) and popped. Callers: the
// main update when none is current, and StopCurrentGuildMessage. Read before it (it frees the node);
// raised after it.
REX_EXTERN(rex_GuildMessageManager_PlayNext) {
  const uint32_t manager = ctx.r3.u32;
  uint32_t message = 0u;
  uint32_t narrator = 0u;
  bool queued = false;
  if (Plausible(manager) && Word(base, manager + 0x1Cu) != 0u && modlua::Wants(kGuildMessage)) {
    const uint32_t node = Word(base, Word(base, manager + 0x18u));
    if (Plausible(node)) {
      queued = true;
      message = Word(base, node + 8u);
      narrator = Word(base, node + 0xCu);
    }
  }
  __imp__rex_GuildMessageManager_PlayNext(ctx, base);
  if (queued) {
    modlua::QueueEvent(kGuildMessage, Fields("message='0x%08x',narrator='0x%08x',player=0", message, narrator));
  }
}

// =============================================================================================
// FABLE 3 — wave 3, agent a3: kinds 155..164 and 162 (pub games 160/161 and gargoyles 163 are
// Fable 2's alone). Paste at the end of Fable3/src/hooks/src/mod_events_hooks.cpp, after "THE SHARED
// EVENTS"; Text, CharString, Fields, Handle, Live, Word, Float, Byte, Frame and Bool are that file's.
// Every function pair below is instruction-identical between TU3 (13341) and disc (13340) after
// normalizing labels (SetAge and AdjustWealthIndex differ only in a constant table's label), so no
// offset needs Pick. manifest.tsv carries every instruction quoted here.
// =============================================================================================

REX_EXTERN(__imp__rex_CECStats_SetTitleRecord);
REX_EXTERN(__imp__rex_CECStats_AddUnlockedTitle);
REX_EXTERN(__imp__rex_CECStats_SetChapterProgress);
REX_EXTERN(__imp__rex_CECMorph_AddScar);
REX_EXTERN(__imp__rex_CECMorph_RemoveRandomScar);
REX_EXTERN(__imp__rex_CECAge_SetAge);
REX_EXTERN(__imp__rex_CPlayer_SetHeroStage);
REX_EXTERN(__imp__rex_CECAppearanceModifierManager_AddItem);
REX_EXTERN(__imp__rex_CECAppearanceModifierManager_RemoveWornItem);
REX_EXTERN(__imp__rex_CECAppearanceModifierManager_RemoveAllItemsOfCategory);
REX_EXTERN(__imp__rex_CECAppearanceModifierManager_SetDyeColour);
REX_EXTERN(__imp__rex_CECVillage_AdjustWealthIndex);

namespace {

constexpr int32_t kTitleChanged = 150;
constexpr int32_t kTitleUnlocked = 151;
constexpr int32_t kChapterProgress = 152;
constexpr int32_t kScarAdded = 153;
constexpr int32_t kScarRemoved = 154;
constexpr int32_t kAgeChanged = 155;
constexpr int32_t kHeroStage = 156;
constexpr int32_t kClothingWorn = 157;
constexpr int32_t kClothingRemoved = 158;
constexpr int32_t kClothingDyed = 159;
constexpr int32_t kVillageWealth = 162;

/// A morph component's scars (+0xBC: begin +0xC0, end +0xC4; 12-byte entries: AddScar `lwz r11,0xC4(r3);
/// li r9,0xC; lwz r8,0xC0(r3); subf r6,r8,r11; divw. r9,r6,r9`).
int32_t ScarCount(const uint8_t* base, uint32_t morph) {
  const uint32_t first = Word(base, morph + 0xC0u);
  const uint32_t last = Word(base, morph + 0xC4u);
  return last >= first ? static_cast<int32_t>((last - first) / 12u) : 0;
}

/// The appearance manager's worn list (+0xC: begin +0x10, end +0x14; 0x60-byte entries whose +0 is the
/// item: the inner remove `lwz r31,0x10(r3); lwz r10,0x14(r3) ... addi r31,r31,0x60`).
int32_t WornCount(const uint8_t* base, uint32_t manager) {
  const uint32_t first = Word(base, manager + 0x10u);
  const uint32_t last = Word(base, manager + 0x14u);
  return last >= first ? static_cast<int32_t>((last - first) / 0x60u) : 0;
}

/// An item's smart pointer: the item at +0, its record at item+4, its category at item+0x18 (the inner add
/// `lwz r10,0(r31); lwz r5,0x18(r10)`; RemoveAllItemsOfCategory `lwz r11,0x18(r3)` against r4).
uint32_t ClothingRecord(const uint8_t* base, uint32_t pointer) {
  const uint32_t item = Word(base, pointer);
  return Plausible(item) ? Word(base, item + 4u) : 0u;
}

int32_t ClothingCategory(const uint8_t* base, uint32_t pointer) {
  const uint32_t item = Word(base, pointer);
  return Plausible(item) ? static_cast<int32_t>(Word(base, item + 0x18u)) : -1;
}

/// A component's owner when it is a live entity, else 0.
uint32_t LiveOwner(const uint8_t* base, uint32_t component) {
  const uint32_t owner = Plausible(component) ? Word(base, component + 4u) : 0u;
  return Live(base, owner) ? owner : 0u;
}

void QueueClothingRemoved(const uint8_t* base, uint32_t owner, uint32_t record, int32_t category, int32_t count) {
  modlua::QueueEvent(kClothingRemoved, Fields("entity=%u,player=%d,definition='0x%08x',category=%d,count=%d", Handle(base, owner),
                                              modlua::PlayerOf(base, owner), record, category, count));
}

std::string WealthFields(const uint8_t* base, uint32_t component, float delta, bool scaled) {
  return Fields("village=%u,delta=%.9g,scaled=%s,wealth=%.9g,min=%.9g,max=%.9g", Handle(base, Word(base, component + 4u)), Fin(delta),
                Bool(scaled), Fin(Float(base, component + 0x58u)), Fin(Float(base, component + 0x5Cu)),
                Fin(Float(base, component + 0x60u)));
}

}  // namespace

// title_changed. Lua_Stats_SetTitleRecord (r3 the stats component, the hero at +4; r4 the title name's
// CCharString): it walks the unlocked titles (`lwz r11,0x46C(r30); lwz r10,0x468(r30)`) and, for the one that
// matches, writes the title record (`std r28,0x3F0(r30)`) and counts it. Raised after it when +0x3F0..+0x3F7
// moved. TU3 0x823DB7A0 / disc 0x823BA608.
REX_EXTERN(rex_CECStats_SetTitleRecord) {
  const uint32_t stats = ctx.r3.u32;
  const uint32_t name = ctx.r4.u32;
  const uint32_t hero = LiveOwner(base, stats);
  const bool wanted = hero != 0u && modlua::Wants(kTitleChanged);
  const uint32_t record = wanted ? Word(base, stats + 0x3F0u) : 0u;
  const uint32_t entry = wanted ? Word(base, stats + 0x3F4u) : 0u;
  __imp__rex_CECStats_SetTitleRecord(ctx, base);
  if (wanted && (Word(base, stats + 0x3F0u) != record || Word(base, stats + 0x3F4u) != entry)) {
    modlua::QueueEvent(kTitleChanged, Fields("hero=%u,player=%d,title='%s'", Handle(base, hero), modlua::PlayerOf(base, hero),
                                             CharString(base, name).c_str()));
  }
}

// title_unlocked. Lua_Stats_AddUnlockedTitle (r3 the stats component; r4 the title name's CCharString): when
// the name is a record it pushes it onto the unlocked titles (`addi r3,r30,0x464; bl sub_822FF938`, whose
// push is `stw r11,8(r30)`: the end at +0x46C, 4-byte entries). Raised after it when the end advanced.
// TU3 0x823DB8B0 / disc 0x823BA718.
REX_EXTERN(rex_CECStats_AddUnlockedTitle) {
  const uint32_t stats = ctx.r3.u32;
  const uint32_t name = ctx.r4.u32;
  const uint32_t hero = LiveOwner(base, stats);
  const bool wanted = hero != 0u && modlua::Wants(kTitleUnlocked);
  const uint32_t end = wanted ? Word(base, stats + 0x46Cu) : 0u;
  __imp__rex_CECStats_AddUnlockedTitle(ctx, base);
  if (wanted && Word(base, stats + 0x46Cu) != end) {
    const uint32_t first = Word(base, stats + 0x468u);
    const uint32_t last = Word(base, stats + 0x46Cu);
    modlua::QueueEvent(kTitleUnlocked, Fields("hero=%u,player=%d,title='%s',titles=%d", Handle(base, hero),
                                              modlua::PlayerOf(base, hero), CharString(base, name).c_str(),
                                              last >= first ? static_cast<int32_t>((last - first) / 4u) : 0));
  }
}

// chapter_progress. Lua_Stats_SetChapterProgress (r3 the stats component; f1 the progress): `lfs f0,0x39C(r3);
// fcmpu cr6,f1,f0; blt` past `stfs f1,0x39C(r3)` — it only rises — then, for a player (`lwz r3,4(r3)`), sends
// it. Raised after it when +0x39C changed. TU3 0x823E8B18 / disc 0x823C7530.
REX_EXTERN(rex_CECStats_SetChapterProgress) {
  const uint32_t stats = ctx.r3.u32;
  const uint32_t hero = LiveOwner(base, stats);
  const bool wanted = hero != 0u && modlua::Wants(kChapterProgress);
  const float previous = wanted ? Float(base, stats + 0x39Cu) : 0.0f;
  __imp__rex_CECStats_SetChapterProgress(ctx, base);
  if (wanted && Float(base, stats + 0x39Cu) != previous) {
    modlua::QueueEvent(kChapterProgress, Fields("hero=%u,player=%d,progress=%.9g,previous=%.9g", Handle(base, hero),
                                                modlua::PlayerOf(base, hero), Fin(Float(base, stats + 0x39Cu)), Fin(previous)));
  }
}

// scar_added. Lua_Morph_AddScar (r3 the morph component, the entity at +4 `lwz r10,4(r31)`; r4 the area mask):
// it refuses when two scars already cover the area (`and r5,r6,r4 ... cmpwi cr6,r7,2; bge` to the return),
// or without the entity's component 20 or a scar record for the area, else appends a 12-byte scar to +0xBC and
// redraws. Decided before it: a cancel returns as those refusals do. Callers discard r3: Morph.AddRandomScar's
// `bl` is its last call (`addi r1,r1,0x70` after it), and the Lua glue 0x82812A20 (entity, int) calls the
// native and returns 0, pushing nothing. TU3 0x82667D18 / disc 0x8263E9A8.
REX_EXTERN(rex_CECMorph_AddScar) {
  const uint32_t morph = ctx.r3.u32;
  const uint32_t entity = LiveOwner(base, morph);
  if (entity != 0u && modlua::Wants(kScarAdded)) {
    const modlua::Verdict verdict =
        modlua::Decide(ctx, base, kScarAdded,
                       Fields("entity=%u,player=%d,area='0x%08x',scars=%d", Handle(base, entity), modlua::PlayerOf(base, entity),
                              ctx.r4.u32, ScarCount(base, morph)));
    if (verdict.cancelled) {
      return;
    }
  }
  __imp__rex_CECMorph_AddScar(ctx, base);
}

// scar_removed. Lua_Morph_RemoveRandomScar (r3 the morph component): with any scar (`lwz r11,0xC0(r3); lwz
// r9,0xC4(r3)`, nothing when the count is 0) it erases a random one and shrinks +0xC4 by 12. Raised after it
// when the count dropped. TU3 0x82668388 / disc 0x8263F018.
REX_EXTERN(rex_CECMorph_RemoveRandomScar) {
  const uint32_t morph = ctx.r3.u32;
  const uint32_t entity = LiveOwner(base, morph);
  const bool wanted = entity != 0u && modlua::Wants(kScarRemoved);
  const int32_t before = wanted ? ScarCount(base, morph) : 0;
  __imp__rex_CECMorph_RemoveRandomScar(ctx, base);
  if (wanted && ScarCount(base, morph) < before) {
    modlua::QueueEvent(kScarRemoved, Fields("entity=%u,player=%d,scars=%d", Handle(base, entity), modlua::PlayerOf(base, entity),
                                            ScarCount(base, morph)));
  }
}

// age_changed. Lua_Age_SetAge (r3 the age component, the entity at +4; f1 the age): `lfs f12,0x10(r3);
// stfs f1,0xC(r3)`, clamps to [0, +0x10] (`fsel f10,f11,f1,f0 ... fsel f7,f8,f12,f10; stfs f7,0xC(r3)`) and
// tail-calls the age packet's send. Decided before it when f1 differs from +0xC: a cancel skips it (no
// write, no packet), a written age replaces f1 and is clamped by the game. TU3 0x826566F8 / disc 0x8262DA98.
REX_EXTERN(rex_CECAge_SetAge) {
  const uint32_t component = ctx.r3.u32;
  const uint32_t entity = LiveOwner(base, component);
  if (entity != 0u && static_cast<float>(ctx.f1.f64) != Float(base, component + 0xCu) && modlua::Wants(kAgeChanged)) {
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kAgeChanged,
        Fields("entity=%u,player=%d,age=%.9g,previous=%.9g,limit=%.9g", Handle(base, entity), modlua::PlayerOf(base, entity),
               Fin(static_cast<float>(ctx.f1.f64)), Fin(Float(base, component + 0xCu)), Fin(Float(base, component + 0x10u))));
    if (verdict.cancelled) {
      return;
    }
    double written = 0.0;
    if (verdict.Read("age", written) && written == written) {
      ctx.f1.f64 = static_cast<float>(written);
    }
  }
  __imp__rex_CECAge_SetAge(ctx, base);
}

// hero_stage. Lua_Player_SetHeroStage (r3 the player component, the hero at +4 `lwz r10,4(r3)`; r4 the stage):
// `lwz r10,0x74(r3); cmpw cr6,r4,r10; beqlr` then `stw r4,0x74(r11)` and the packet. Raised after it when +0x74
// changed. TU3 0x8242F810 / disc 0x82411B98.
REX_EXTERN(rex_CPlayer_SetHeroStage) {
  const uint32_t component = ctx.r3.u32;
  const uint32_t hero = LiveOwner(base, component);
  const bool wanted = hero != 0u && modlua::Wants(kHeroStage);
  const int32_t previous = wanted ? static_cast<int32_t>(Word(base, component + 0x74u)) : 0;
  __imp__rex_CPlayer_SetHeroStage(ctx, base);
  if (wanted && static_cast<int32_t>(Word(base, component + 0x74u)) != previous) {
    modlua::QueueEvent(kHeroStage, Fields("hero=%u,player=%d,stage=%d,previous=%d", Handle(base, hero), modlua::PlayerOf(base, hero),
                                          static_cast<int32_t>(Word(base, component + 0x74u)), previous));
  }
}

// clothing_worn. The appearance manager's add (r3 the manager component, the entity at +4 `lwz r31,4(r28)`;
// r4 the item's smart pointer: `lwz r11,0(r4); mr r28,r3; mr r31,r4`): it answers r3 = 0 for no item, a
// blocked slot (`lwz r11,0x60(r28)` against the item's mask) or an item that cannot go on (sub_8265BEB8 with
// the category `lwz r5,0x18(r10)`), else wears it and answers 1 (`li r3,0` / `li r3,1`). Every wear reaches it:
// AppearanceModifierManager.AddAndReplaceItem (0x82659180), CustomSuitStorage.WearScriptOutfit, the initial
// items and seven more. Decided before it: a cancel answers 0, the guest's own refusal.
// TU3 0x826591B0 / disc 0x826304F0.
REX_EXTERN(rex_CECAppearanceModifierManager_AddItem) {
  const uint32_t manager = ctx.r3.u32;
  const uint32_t pointer = ctx.r4.u32;
  const uint32_t entity = LiveOwner(base, manager);
  if (entity != 0u && Plausible(Word(base, pointer)) && modlua::Wants(kClothingWorn)) {
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kClothingWorn,
        Fields("entity=%u,player=%d,definition='0x%08x',category=%d", Handle(base, entity), modlua::PlayerOf(base, entity),
               ClothingRecord(base, pointer), ClothingCategory(base, pointer)));
    if (verdict.cancelled) {
      ctx.r3.u64 = 0u;
      return;
    }
  }
  __imp__rex_CECAppearanceModifierManager_AddItem(ctx, base);
}

// clothing_removed. The appearance manager's remove (r3 the manager; r4 the item's smart pointer, matched word
// for word against each worn entry: `lwz r11,4(r31); lwz r8,4(r4)`) erases the match from the worn list
// (+0x10..+0x14, 0x60 each) and answers 1, else 0 — AppearanceModifierManager.RemoveItem (0x8265964C) and
// four more reach it. RemoveAllItemsOfCategory (r3 the manager; r4 the category, matched as `lwz r11,0x18(r3)`)
// erases every worn item of it (`addi r11,r11,-0x60; stw r11,0x14(r30)`). Raised after each when the worn
// list shrank. TU3 0x82659680 / 0x82659898, disc 0x82630978 / 0x82630B90.
REX_EXTERN(rex_CECAppearanceModifierManager_RemoveWornItem) {
  const uint32_t manager = ctx.r3.u32;
  const uint32_t pointer = ctx.r4.u32;
  const uint32_t entity = LiveOwner(base, manager);
  const bool wanted = entity != 0u && modlua::Wants(kClothingRemoved);
  const uint32_t record = wanted ? ClothingRecord(base, pointer) : 0u;
  const int32_t category = wanted ? ClothingCategory(base, pointer) : -1;
  const int32_t before = wanted ? WornCount(base, manager) : 0;
  __imp__rex_CECAppearanceModifierManager_RemoveWornItem(ctx, base);
  if (wanted && WornCount(base, manager) < before) {
    QueueClothingRemoved(base, entity, record, category, before - WornCount(base, manager));
  }
}

REX_EXTERN(rex_CECAppearanceModifierManager_RemoveAllItemsOfCategory) {
  const uint32_t manager = ctx.r3.u32;
  const int32_t category = static_cast<int32_t>(ctx.r4.u32);
  const uint32_t entity = LiveOwner(base, manager);
  const bool wanted = entity != 0u && modlua::Wants(kClothingRemoved);
  const int32_t before = wanted ? WornCount(base, manager) : 0;
  __imp__rex_CECAppearanceModifierManager_RemoveAllItemsOfCategory(ctx, base);
  if (wanted && WornCount(base, manager) < before) {
    QueueClothingRemoved(base, entity, 0u, category, before - WornCount(base, manager));
  }
}

// clothing_dyed. Lua_AppearanceModifierManager_SetClothingItemDyeColour (r3 the manager `mr r28,r3`; r4 the
// item's smart pointer `mr r31,r4`; r5..r8 and f1 handed on untouched to the setter sub_8265B128, which reads
// them as r5 the slot byte `mr r27,r5` (nonzero writes +8/+0xC of the item's dye block, zero +0x10/+0x14),
// r6 red `mr r31,r6` -> byte +2, r7 green `mr r30,r7` -> +1, r8 blue `mr r29,r8` -> +0 (`stb r31,..+2;
// stb r30,..+1; stb r29,..`), f1 brightness `fmr f31,f1`), then sends the block to the co-op partner. The
// game's own dye screens, the outfits and scripts all reach it; the partner's packet (sub_826607A0) calls the
// setter directly and is not raised. Decided before it: a cancel skips it (no write, no packet), written
// colours and brightness replace r6..r8 and f1. TU3 0x8265B010 / disc 0x82632308.
REX_EXTERN(rex_CECAppearanceModifierManager_SetDyeColour) {
  const uint32_t manager = ctx.r3.u32;
  const uint32_t pointer = ctx.r4.u32;
  const uint32_t entity = LiveOwner(base, manager);
  if (entity != 0u && Plausible(Word(base, pointer)) && modlua::Wants(kClothingDyed)) {
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kClothingDyed,
        Fields("entity=%u,player=%d,definition='0x%08x',dye='0x00000000',red=%u,green=%u,blue=%u,brightness=%.9g,secondary=%s",
               Handle(base, entity), modlua::PlayerOf(base, entity), ClothingRecord(base, pointer), ctx.r6.u32 & 0xFFu,
               ctx.r7.u32 & 0xFFu, ctx.r8.u32 & 0xFFu, Fin(static_cast<float>(ctx.f1.f64)), Bool((ctx.r5.u32 & 0xFFu) == 0u)));
    if (verdict.cancelled) {
      return;
    }
    double written = 0.0;
    const auto colour = [](double value) { return static_cast<uint64_t>(value < 0.0 ? 0 : value > 255.0 ? 255 : static_cast<int>(value)); };
    if (verdict.Read("red", written) && written == written) {
      ctx.r6.u64 = colour(written);
    }
    if (verdict.Read("green", written) && written == written) {
      ctx.r7.u64 = colour(written);
    }
    if (verdict.Read("blue", written) && written == written) {
      ctx.r8.u64 = colour(written);
    }
    if (verdict.Read("brightness", written) && written == written) {
      ctx.f1.f64 = static_cast<float>(written);
    }
  }
  __imp__rex_CECAppearanceModifierManager_SetDyeColour(ctx, base);
}

// village_wealth. Lua_Village_AdjustWealthIndex (r3 the village component, the village at +4 (sub_827818B8
// `lwz r11,4(r3)`); f1 the change; r5 the keep-local byte `mr r30,r5`: zero sends the change to the co-op
// partner (`clrlwi r7,r30,24; bne` past `bl sub_827818B8`); r6 the unscaled byte `clrlwi r11,r6,24; bne` past
// `lfs f0,0x64(r3); fmuls f31,f0,f31`): wealth +0x58 += the change, clamped to [+0x5C, +0x60] (`lfs f0,0x58(r31);
// fadds f13,f0,f31; lfs f12,0x60(r31); lfs f9,0x5C(r31)` ... `stfs f4,0x58(r31)`). Decided before it when it
// is sent on (r5 = 0): a cancel skips it, change and packet both; a written delta replaces f1. A change kept
// local (the partner's packet, sub_827863A0 `li r5,1`, and the VillageWealthChanges scripts, 0x82517D14) is
// reported, so a cancel can never split the two machines. TU3 0x8277B540 / disc 0x827467C8.
REX_EXTERN(rex_CECVillage_AdjustWealthIndex) {
  const uint32_t component = ctx.r3.u32;
  const uint32_t village = Plausible(component) ? Word(base, component + 4u) : 0u;
  if (Live(base, village) && modlua::Wants(kVillageWealth)) {
    const bool scaled = (ctx.r6.u32 & 0xFFu) == 0u;
    const std::string fields = WealthFields(base, component, static_cast<float>(ctx.f1.f64), scaled);
    if ((ctx.r5.u32 & 0xFFu) != 0u) {
      modlua::QueueEvent(kVillageWealth, fields);
    } else {
      const modlua::Verdict verdict = modlua::Decide(ctx, base, kVillageWealth, fields);
      if (verdict.cancelled) {
        return;
      }
      double written = 0.0;
      if (verdict.Read("delta", written) && written == written) {
        ctx.f1.f64 = static_cast<float>(written);
      }
    }
  }
  __imp__rex_CECVillage_AdjustWealthIndex(ctx, base);
}

// sleep_chosen (61).
REX_EXTERN(__imp__rex_CECBed_ApplyAnswer);

namespace {
constexpr int32_t kSleepChosen = 61;
/// The bed box's A press, returned to after it applied the answer (`bl sub_825614D8` 0x82B8E5F8).
const refii::guest::Addr kBedBoxAnswered{0x82B8E5FCu};
}  // namespace

// sleep_chosen. The bed's menu is CBedInteractionMessageBox (DisplayBedInteractionMenu -> sub_82623738:
// +0x1C the user's smart pointer, +0x24 the bed entity's). Its HandleInput (sub_82B8DFA0, r3 the box:
// `mr r26,r3` 0x82B8DFB0, the only write of r26) switches on the input id (`lwz r3,0(r27)` 0x82B8E130,
// `addi r11,r3,-0x1D` 0x82B8E158); on the A press (41) it writes the highlighted answer onto the bed
// component 207 — the sleep row (+0x2C == 0: `lwz r11,0x2C(r26)` 0x82B8E5D4) its sleep choice +0x30 to
// the bed's +0x14 (`lwz r11,0x30(r26); stw r11,0x14(r30)` 0x82B8E5E4..E8), the sex row its +0x34 to +0x18
// — then calls this apply (r3 the bed component). +0x14 is the bed's sleep type (Bed.GetSleepType is
// `lwz r3,0x14(r3)`, 0x82A8C990): ESleepType 0 SLEEP_MORNING, 1 SLEEP_MIDDAY, 2 SLEEP_MIDNIGHT; Bed.Clear
// stores -1, SLEEP_NONE (`li r10,-1; stw r10,0x14(r31)` 0x82562684 / 0x82562698). Alone the apply only
// clears the bed's waiting byte (`stb r11,0xD(r31)` with 0, 0x825615C0); in co-op it hands the answer to
// the partner's consent (sub_82561638) or the session. The shipped OnActionUseBed waits on
// Bed.IsWaitingForResponse, then reads GetSleepType and calls SleepFunction.Sleep unless it is SLEEP_NONE.
// Decided before the apply, from the A press only (return 0x82B8E5FC, the sleep row): `choice` written is
// the bed's +0x14 (0..2); a cancel stores SLEEP_NONE, Bed.Clear's own value — the bed then ends exactly as
// the box's B (input 42: the bed's +0xD cleared, nothing written) leaves it, and the script lets the
// player go. The box's B and the sex row are not raised (they choose no sleep).
REX_EXTERN(rex_CECBed_ApplyAnswer) {
  const uint32_t bed = ctx.r3.u32;
  const uint32_t box = ctx.r26.u32;
  if (static_cast<uint32_t>(ctx.lr) == kBedBoxAnswered && Plausible(bed) && Plausible(box) && Word(base, box + 0x2Cu) == 0u &&
      modlua::Wants(kSleepChosen)) {
    const int32_t choice = static_cast<int32_t>(Word(base, bed + 0x14u));
    if (choice >= 0 && choice <= 2) {
      const uint32_t sleeper = Deref(base, box + 0x1Cu);
      const modlua::Verdict verdict =
          modlua::Decide(ctx, base, kSleepChosen,
                         Fields("bed=%u,choice=%d,cancelled=false,player=%d", Handle(base, Word(base, bed + 4u)), choice,
                                modlua::PlayerOf(base, sleeper)));
      double written = 0.0;
      if (verdict.cancelled) {
        StoreU32(base, bed + 0x14u, 0xFFFFFFFFu);
      } else if (verdict.Read("choice", written) && written >= 0.0 && written <= 2.0) {
        StoreU32(base, bed + 0x14u, static_cast<uint32_t>(written));
      }
    }
  }
  __imp__rex_CECBed_ApplyAnswer(ctx, base);
}

// THE PARTNERSHIP (84): the two heroes married or in business. Each hero's CECMultiplayerMarriage
// (component 83, c) keeps std::lists of {HeroUID} records — spouses at c+12 (size +16; Lua IsMarried
// is `*(c+16) != 0`), business partners at c+36 (+40) — and the game changes them in four places,
// each run by the authority (alone, couch, or the online host) on BOTH heroes' components; a client
// only mirrors them from the host's messages (codes 5/10, packet 358) through the same calls, or
// rebuilds them whole (the load, vf16's copy, TU3's packet 530), which are not changes and not hooked:
//   marry          TU3 0x824D9788 / disc 0x824BD130 (r3 c; the partner's HeroUID at c+120) — ends a
//                  business partnership with the same hero first (bl sub_824D8F18 at 824d982c / 824bd224)
//   form business  TU3 0x824D9CC8 / disc 0x824BD6B8 (r3 c; the partner at c+120)
//   divorce        TU3 0x824D7F98 / disc 0x824BBC48 (r3 c, r4 the HeroUID: `ld r9,8(r4)` at 824d7fbc)
//   end business   TU3 0x824D8F18 / disc 0x824BCA90 (r3 c, r4 the HeroUID)
// The kind is read before and after through the game's own finders, spouses then business (TU3
// sub_824DCB58 / disc 0x824BFF48 on c+12, sub_824DCF08 / 0x824C02F8 on c+36: r3 c, r4 the HeroUID,
// matching its id word +12 and its +24), and raised once, from player one's component, never on an
// online client: the event fires on the host.
REX_EXTERN(__imp__rex_CECMultiplayerMarriage_Marry);
REX_EXTERN(__imp__rex_CECMultiplayerMarriage_FormBusiness);
REX_EXTERN(__imp__rex_CECMultiplayerMarriage_Divorce);
REX_EXTERN(__imp__rex_CECMultiplayerMarriage_EndBusiness);

namespace {

constexpr int32_t kPartnership = 84;
/// Inside the marriage's own end of a business partnership: raised once, as the marriage.
thread_local int g_marrying = 0;

int32_t PartnershipKind(const PPCContext& ctx, uint8_t* base, uint32_t component, uint32_t uid) {
  for (const auto& [finder, kind] : {std::pair{refii::guest::Pick(0x824DCB58u, 0x824BFF48u), 2},
                                     std::pair{refii::guest::Pick(0x824DCF08u, 0x824C02F8u), 1}}) {
    PPCContext work = Frame(ctx, 0x100u);
    work.r3.u32 = component;
    work.r4.u32 = uid;
    if (CallAt(work, base, finder) && work.r3.u32 != 0u) {
      return kind;
    }
  }
  return 0;
}

/// Whether this change is the one raised: the host's (or the only machine's) player-one component.
bool PartnershipRaised(uint8_t* base, uint32_t component) {
  return Plausible(component) && !modlua::OnlineClient() && modlua::PlayerOf(base, Word(base, component + 4u)) == 1 &&
         modlua::Wants(kPartnership);
}

template <typename Original>
void ChangePartnership(PPCContext& ctx, uint8_t* base, uint32_t uid, Original original) {
  const uint32_t component = ctx.r3.u32;
  const bool raised = g_marrying == 0 && PartnershipRaised(base, component) && Plausible(uid);
  const int32_t previous = raised ? PartnershipKind(ctx, base, component, uid) : 0;
  const PPCContext entry = ctx;
  original(ctx, base);
  if (!raised) {
    return;
  }
  const int32_t kind = PartnershipKind(entry, base, component, uid);
  if (kind != previous) {
    modlua::QueueEvent(kPartnership, Fields("kind=%d,previous=%d", kind, previous));
  }
}

}  // namespace

REX_EXTERN(rex_CECMultiplayerMarriage_Marry) {
  const uint32_t uid = ctx.r3.u32 + 120u;
  ChangePartnership(ctx, base, uid, [](PPCContext& c, uint8_t* b) {
    ++g_marrying;
    __imp__rex_CECMultiplayerMarriage_Marry(c, b);
    --g_marrying;
  });
}

REX_EXTERN(rex_CECMultiplayerMarriage_FormBusiness) {
  ChangePartnership(ctx, base, ctx.r3.u32 + 120u, __imp__rex_CECMultiplayerMarriage_FormBusiness);
}

REX_EXTERN(rex_CECMultiplayerMarriage_Divorce) {
  ChangePartnership(ctx, base, ctx.r4.u32, __imp__rex_CECMultiplayerMarriage_Divorce);
}

REX_EXTERN(rex_CECMultiplayerMarriage_EndBusiness) {
  ChangePartnership(ctx, base, ctx.r4.u32, __imp__rex_CECMultiplayerMarriage_EndBusiness);
}
