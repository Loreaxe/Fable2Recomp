#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <cstdio>
#include <unordered_map>

#include <rex/logging.h>
#include <rex/ppc/context.h>
#include <rex/ppc/func.h>

#include "core/mod_values.h"
#include "core/refii_mod.h"
#include "core/refii_mod_events.h"

extern "C" REX_FUNC(__imp__rex_CECMorph_GetWeight);
/// Player two brought a hero (a character from a save) rather than standing as the plain henchman.
extern "C" int RefiiCoopGetOwnHero(void);
extern "C" REX_FUNC(rex_CECStats_FlushAppearance);

// MURGO'S MIRROR FOR BOTH PLAYERS. Each player's look is the player store's `character_morphs.*`
// set (refii_mod_appearance), owned by the machine that plays that hero and restated to the other,
// so both co-op machines hold both looks. Godot turns each into the weights of the weight function
// `sub_822DA028` and the inputs villagers judge, and publishes them per player as
// `refii.character_morphs.p<N>.*` (GameCharacterMorphs); this file answers both from them, for the
// hero of the player they belong to. The Mirror settings a player edits are this machine's settings
// slot 1 (`character_morphs`, the player whose pad is pad 0) or slot 2 (`character_morphs2`, local
// co-op's player two): the slot's player is published below from the store's own owners.

namespace {

// What villagers judge a hero's looks by. Neither reader goes through the weight function: each
// loads the CECStats field or CECAge's years itself, so the weight hook below never reaches them.
//   sub_82791658, the appearance opinion (r28 the hero, from the registry cell +0x6C at
//     0x82791694): `lfs f13,0xC(r11)` CECAge years 0x82791998, `lfs f31,0x38(r31)` fat 0x82791AD4,
//     `lfs f31,0x34(r31)` tan 0x82791B00, `lfs f31,0x40(r31)` strength 0x82791B28, each scaled by
//     the body's exposure (sub_824C2EE8) into the NPC's preference curves; a moved attractive/ugly or
//     scary/funny axis raises AppearanceChange* (sub_8278AA40). The shipped pass is player one's;
//     player two's runs in player two's opinion scope (mod_events.cpp rex_CECOpinionReaction_Update),
//     which puts hero two's holder in +0x6C, so r28 is hero two there. It re-runs when the hero's
//     appearance stamp (comp15 +0x68) passes the opinion's (+660), both the world frame
//     dword_83319650 (`sub_824E0B40` writes comp15 +0x64/+0x68; `sub_82791658` gates
//     `*(a1+660) < v7[26]` and ends `*(a1+660) = dword_83319650`). +660 is one word for both passes,
//     so two heroes flushed in one frame would leave the second pass behind the first's stamp:
//     Refresh below flushes one hero per tick.
//   sub_8278F848, a reaction line's HeroAppearanceChecks (r30 the entity reacted to, `mr r30,r4`
//     0x8278F858; r31 its CECStats, found by `cmpwi r11,0x39`): `lfs f31,0x38(r31)` fat 0x8278FACC,
//     `lfs f31,0x40(r31)` strength 0x8278FB00, `lfs f0,0x3C(r31)` height 0x8278FB40, `lfs
//     f0,0x34(r31)` tan 0x8278FB5C, `lfs f31,0x48(r31)` will 0x8278FB6C, `fmr f31,f1` after `bl
//     sub_824D6F90` normalised purity 0x8278FBAC, `lfs f13,0xC(r11)` CECAge years 0x8278FCE4.
//     Reaction lines reach player two through RefiiCoopReactToPlayerTwo (0x8278D998).
// Each hook runs after its instruction and replaces the loaded value with the stated input for the
// hero it applies to, an adult, in manual. Nothing is written to the guest: combat, fat decay, the
// general opinion (sub_8278B948: morality, purity, skill and will as renown) and Lua's Stats.Get*
// keep the real fields.
enum Judged : uint32_t { kTan, kFat, kHeight, kStrength, kWill, kPurity, kAge, kJudgedCount };
constexpr const char* kJudgedNames[kJudgedCount] = {"tan", "fat", "height", "strength", "will", "purity", "age"};

/// One player's published look and the hero it applies to.
struct Look {
  std::atomic<uint32_t> hero{0};
  std::atomic<bool> manual{false};
  std::atomic<bool> adult{false};
  std::array<std::atomic<float>, 19> weights{};
  std::array<std::atomic<float>, kJudgedCount> judged{};
  float generation = -1;
};

Look g_looks[2];

/// The guest player bound to each settings slot, as last published.
float g_slots[2] = {-1, -1};

/// Heroes whose appearance waits to be flushed, one a tick.
std::array<uint32_t, 4> g_refresh{};

/// Each player's hero as of the last tick, manual or not: the mod morphs answer for both.
std::array<std::atomic<uint32_t>, 2> g_heroes{};

/// Each player's mod morph weights as the hook last answered them, by id.
std::unordered_map<uint32_t, float> g_mod_weights[2];

std::atomic<bool> g_said_morph{false};
std::atomic<bool> g_said_texture{false};

void Once(std::atomic<bool>& said, const char* what) {
  if (!said.exchange(true, std::memory_order_relaxed)) REXLOG_INFO("Refii Mirror: {}", what);
}

void Key(char* out, size_t size, int32_t player, const char* name) {
  std::snprintf(out, size, "refii.character_morphs.p%d.%s", player, name);
}

// The weight function's own adult gate: type 1 weighs 1 for an adult and 0 below stage 2.
bool Adult(PPCContext* ctx, uint8_t* base, uint32_t hero) {
  if (refii_mod_entity_alive(base, hero) == 0) return false;
  const uint32_t morph = refii_mod_component(base, hero, 116u);
  if (morph == 0u) return false;
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u64 = morph;
  work.r4.u64 = 1;
  __imp__rex_CECMorph_GetWeight(work, base);
  return work.f1.f64 > 0;
}

/// The look applied to `entity`, or null: either player's hero while that player is manual.
Look* LookOf(uint32_t entity) {
  if (entity == 0u) return nullptr;
  for (Look& look : g_looks) {
    if (look.hero.load(std::memory_order_acquire) == entity && look.manual.load(std::memory_order_acquire)) return &look;
  }
  return nullptr;
}

void Judge(const PPCRegister& entity, PPCRegister& value, Judged input) {
  Look* look = LookOf(entity.u32);
  if (look == nullptr || !look->adult.load(std::memory_order_acquire)) return;
  const float stated = look->judged[input].load(std::memory_order_relaxed);
  if (std::isfinite(stated)) value.f64 = stated;
}

void QueueRefresh(uint32_t hero) {
  if (hero == 0u) return;
  for (uint32_t queued : g_refresh) {
    if (queued == hero) return;
  }
  for (uint32_t& slot : g_refresh) {
    if (slot == 0u) {
      slot = hero;
      return;
    }
  }
}

// The game's own appearance update for one hero: CECStats_FlushAppearance stamps it (the stamp the
// appearance opinion re-runs on) and GraphicAppearanceMorph.UpdateTextureMorphs rebuilds its layers.
// The layers keep each morph type's weight cached on the appearance component (component 17, the
// map at +164): ApplyTextureLayers asks the weight function only for a type the map lacks
// (`sub_8220DE88` 0x8220EE04 find, 0x8220EE50 the cached weight, 0x8220EE6C the weight function,
// 0x8220EE80 stored), and UpdateTextureMorphs (`sub_825B60F8`) is what empties it. So a hero whose
// layers were built before the look took over keeps the old weights until this runs on it: false
// while the hero has no appearance component yet (a load still building it), so it stays queued.
bool Refresh(PPCContext* ctx, uint8_t* base, uint32_t hero) {
  if (hero == 0 || refii_mod_entity_alive(base, hero) == 0) return true;
  if (refii_mod_component(base, hero, 17u) == 0u) return false;
  const uint32_t stats = refii_mod_component(base, hero, 57u);
  if (stats != 0u) {
    PPCContext work = refii::mods::Frame(*ctx);
    work.r3.u64 = stats;
    rex_CECStats_FlushAppearance(work, base);
  }
  char source[256];
  std::snprintf(source, sizeof(source),
                "local h=Refii.entity(%u); if h and h:IsAlive() and GraphicAppearanceMorph.IsAvailable(h) then "
                "GraphicAppearanceMorph.UpdateTextureMorphs(h) end",
                refii_mod_handle(hero));
  refii_mod_run_game_lua(ctx, base, source);
  return true;
}

// Settings slot 1 is the player this machine's pad 0 plays: player one alone, in local co-op and
// hosting, player two on an online joiner. Slot 2 is local co-op's player two (both players here).
// Read from the store's owners (refii_mod_player_here), the same answer its writes are checked by.
void PublishSlots() {
  const bool one = refii_mod_player_here(1) != 0;
  const bool two = refii_mod_player_here(2) != 0;
  const float slots[2] = {one ? 1.0f : (two ? 2.0f : 0.0f), one && two ? 2.0f : 0.0f};
  for (int i = 0; i < 2; ++i) {
    if (slots[i] == g_slots[i]) continue;
    g_slots[i] = slots[i];
    refii::mods::Publish(i == 0 ? "refii.character_morphs.slot1" : "refii.character_morphs.slot2", slots[i]);
  }
}

/// Reads player `player`'s published look when its generation moved. True when the look in force
/// changed: manual came on or off, or a manual value moved.
bool Load(Look& look, int32_t player) {
  char key[64];
  Key(key, sizeof(key), player, "generation");
  const float generation = refii::mods::Setting(key, 0);
  if (generation == look.generation) return false;
  look.generation = generation;
  const bool was_manual = look.manual.load(std::memory_order_relaxed);
  bool moved = false;
  for (uint32_t type = 2; type <= 18; ++type) {
    char name[8];
    std::snprintf(name, sizeof(name), "%u", type);
    Key(key, sizeof(key), player, name);
    float weight = refii::mods::Setting(key, 0);
    if (!std::isfinite(weight)) weight = 0;
    const float before = look.weights[type].exchange(weight, std::memory_order_relaxed);
    moved = moved || before != weight;
  }
  for (uint32_t input = 0; input < kJudgedCount; ++input) {
    Key(key, sizeof(key), player, kJudgedNames[input]);
    const float stated = refii::mods::Setting(key, NAN);
    const float before = look.judged[input].exchange(stated, std::memory_order_relaxed);
    moved = moved || !(before == stated || (std::isnan(before) && std::isnan(stated)));
  }
  Key(key, sizeof(key), player, "automatic");
  const bool manual = refii::mods::Setting(key, 1) == 0;
  look.manual.store(manual, std::memory_order_release);
  // What the hook now answers with, published for the debug channel's `mod-value`.
  Key(key, sizeof(key), player, "held.generation");
  refii::mods::Publish(key, generation);
  Key(key, sizeof(key), player, "held.manual");
  refii::mods::Publish(key, manual ? 1.0f : 0.0f);
  for (uint32_t type = 2; type <= 18; ++type) {
    char name[16];
    std::snprintf(name, sizeof(name), "held.%u", type);
    Key(key, sizeof(key), player, name);
    refii::mods::Publish(key, look.weights[type].load(std::memory_order_relaxed));
  }
  return manual != was_manual || (manual && moved);
}

// MODS' OWN MORPHS (refii_mod_morph_*): a weight that moved for a player — the store's `morph.<id>`,
// or the registry — redraws that player's hero and raises MorphChanged. An id seen for the first
// time is taken silently unless it already weighs something.
void WatchModMorphs(PPCContext* ctx, uint8_t* base) {
  std::array<uint32_t, 256> ids{};
  const int32_t count = std::min<int32_t>(refii_mod_morph_list(ids.data(), static_cast<int32_t>(ids.size())), 256);
  for (int32_t player = 1; player <= 2; ++player) {
    std::unordered_map<uint32_t, float>& seen = g_mod_weights[player - 1];
    std::unordered_map<uint32_t, float> now;
    const uint32_t hero = g_heroes[player - 1].load(std::memory_order_relaxed);
    for (int32_t i = 0; i < count; ++i) {
      float weight = 0;
      if (refii_mod_morph_weight(player, ids[i], &weight) == 0) continue;
      now[ids[i]] = weight;
      const auto before = seen.find(ids[i]);
      if (before == seen.end() ? weight == 0 : before->second == weight) continue;
      QueueRefresh(hero);
      refii::mods::MorphChangedEvent e{};
      PPCContext e_ctx = *ctx;
      e.ctx = &e_ctx;
      e.base = base;
      e.player = player;
      e.morph = ids[i];
      e.weight = weight;
      e.hero = hero;
      refii::mods::Dispatch(e);
    }
    if (now.size() != seen.size()) QueueRefresh(hero);
    seen.swap(now);
  }
}

void Tick(PPCContext* ctx, uint8_t* base, float) {
  if (base == nullptr) return;
  PublishSlots();
  for (int32_t player = 1; player <= 2; ++player) {
    const uint32_t any = refii_mod_player_hero(base, player);
    if (g_heroes[player - 1].exchange(any, std::memory_order_acq_rel) != any) g_mod_weights[player - 1].clear();
  }
  WatchModMorphs(ctx, base);
  for (int32_t player = 1; player <= 2; ++player) {
    Look& look = g_looks[player - 1];
    const bool changed = Load(look, player);
    const bool manual = look.manual.load(std::memory_order_acquire);
    const uint32_t hero = manual ? refii_mod_player_hero(base, player) : 0;
    look.adult.store(hero != 0 && Adult(ctx, base, hero), std::memory_order_release);
    const uint32_t previous = look.hero.exchange(hero, std::memory_order_acq_rel);
    if (previous != hero) QueueRefresh(previous);
    if (manual && (changed || previous != hero)) QueueRefresh(hero);
    if (changed) {
      refii::mods::AppearanceChangedEvent e{};
      PPCContext e_ctx = *ctx;
      e.ctx = &e_ctx;
      e.base = base;
      e.player = player;
      e.automatic = !manual;
      e.hero = refii_mod_player_hero(base, player);
      refii::mods::Dispatch(e);
    }
  }
  for (uint32_t& hero : g_refresh) {
    if (hero == 0u || !Refresh(ctx, base, hero)) continue;
    hero = 0u;
    break;
  }
}

REFII_MOD_ON_TICK(Tick);

}

namespace {

/// The player whose hero owns morph component `component` (its owner at +4), 0 for none.
int32_t PlayerOfComponent(uint8_t* base, uint32_t component) {
  if (component < 0x10000u || component >= 0xC0000000u) return 0;
  const uint32_t entity = refii::mods::LoadU32(base, component + 4);
  if (entity == 0u) return 0;
  for (int32_t player = 1; player <= 2; ++player) {
    if (g_heroes[player - 1].load(std::memory_order_relaxed) == entity) return player;
  }
  return 0;
}

}  // namespace

// THE WEIGHT FUNCTION `sub_822DA028(r3 CECMorph, r4 type)` (TU1; disc `sub_822DA728`), f1 the weight.
// A registered mod morph on either player's hero weighs the player's stored weight (refii_mod_morph_weight)
// in both modes, behind the function's own adult gate — type 1 weighs 0 below stage 2, as every type
// does there. The manual look answers types 2..18 for its hero; everything else is the game's own.
extern "C" REX_FUNC(rex_CECMorph_GetWeight) {
  REX_FUNC_PROLOGUE();
  const uint32_t type = ctx.r4.u32;
  const uint32_t component = ctx.r3.u32;
  if (type >= 0x100u && type < 0x80000000u) {
    float weight = 0;
    const int32_t player = PlayerOfComponent(base, component);
    if (player != 0 && refii_mod_morph_weight(player, type, &weight) != 0) {
      PPCContext adult = refii::mods::Frame(ctx);
      adult.r4.u64 = 1;
      __imp__rex_CECMorph_GetWeight(adult, base);
      ctx.f1.f64 = adult.f1.f64 > 0 ? weight : 0;
      return;
    }
  }
  Look* look = type >= 2 && type <= 18 && component >= 0x10000u && component < 0xC0000000u
                   ? LookOf(refii::mods::LoadU32(base, component + 4))
                   : nullptr;
  if (look != nullptr) {
    PPCContext adult = refii::mods::Frame(ctx);
    adult.r4.u64 = 1;
    __imp__rex_CECMorph_GetWeight(adult, base);
    ctx.f1.f64 = adult.f1.f64 > 0 ? look->weights[type].load(std::memory_order_relaxed) : 0;
    return;
  }
  __imp__rex_CECMorph_GetWeight(ctx, base);
}

void RefiiMirrorOpinionAge(PPCRegister& r28, PPCRegister& f13) { Judge(r28, f13, kAge); }
void RefiiMirrorOpinionFat(PPCRegister& r28, PPCRegister& f31) { Judge(r28, f31, kFat); }
void RefiiMirrorOpinionTan(PPCRegister& r28, PPCRegister& f31) { Judge(r28, f31, kTan); }
void RefiiMirrorOpinionStrength(PPCRegister& r28, PPCRegister& f31) { Judge(r28, f31, kStrength); }

void RefiiMirrorCheckFat(PPCRegister& r30, PPCRegister& f31) { Judge(r30, f31, kFat); }
void RefiiMirrorCheckStrength(PPCRegister& r30, PPCRegister& f31) { Judge(r30, f31, kStrength); }
void RefiiMirrorCheckHeight(PPCRegister& r30, PPCRegister& f0) { Judge(r30, f0, kHeight); }
void RefiiMirrorCheckTan(PPCRegister& r30, PPCRegister& f0) { Judge(r30, f0, kTan); }
void RefiiMirrorCheckWill(PPCRegister& r30, PPCRegister& f31) { Judge(r30, f31, kWill); }
void RefiiMirrorCheckPurity(PPCRegister& r30, PPCRegister& f31) { Judge(r30, f31, kPurity); }
void RefiiMirrorCheckAge(PPCRegister& r30, PPCRegister& f13) { Judge(r30, f13, kAge); }

// PLAYER TWO'S BODY MORPHS. ChangeEntityType (`sub_82365258`, disc `sub_823608F8`) gives an adult
// hero its morph component and takes it from a child, and leaves it off player two's:
//   `bl sub_821C2A18; cmpwi cr6,r3,2; bge` (0x82365514..1C) — below stage 2: `li r4,0x74; bl
//   sub_8238CB78` removes component 116;
//   `mr r3,r28; bl sub_828E5A78; cmplw cr6,r27,r3; beq cr6,loc_82365558` (0x82365534..40) — r28 the
//   player record, sub_828E5A78 its +112 hero two: player two skips
//   `sub_8238CD68(r27, "CECMorph", 1)` (0x82365544..54).
// Without component 116 the weight function is never asked for player two: the part submit takes
// the unmorphed single-model list, the skeletal list keeps its base and the texture layers keep
// type 1. The hook stands before the compare and jumps to the add (TU1 0x8236553C -> 0x82365544,
// disc 0x82360BDC -> 0x82360BE4), so player two gets the morph component every adult hero gets,
// from its own CECStats (the co-op apply writes the hero's into it), its own CECAge and its scars.
// ONLY A HERO PLAYER TWO BROUGHT: false runs the shipped compare (`cmplw cr6, r27, r3`), so the plain
// henchman's body stays unmorphed, as shipped; any other entity takes the add either way.
bool RefiiMirrorMorphForPlayerTwo() {
  if (RefiiCoopGetOwnHero() == 0) {
    return false;
  }
  Once(g_said_morph, "player two's hero is given its morph component");
  return true;
}

// The same player-two skip in ApplyTextureLayers (`sub_8220DE88`, disc `sub_8220DFC0`): the
// definition's TextureMorphs loop (Layer / MorphTexture / OriginalTexture) is passed over for hero
// two — `lwz r31,-8(r30)` the entity, `bl sub_828E5A78`, `cmplw cr6,r31,r3`, `beq cr6,loc_8220EF70`
// (0x8220EB78..BC; r16/r17 are set before the branch). The hook stands on the branch and goes on to
// the loop (TU1 0x8220EBBC -> 0x8220EBC0, disc 0x8220ECF4 -> 0x8220ECF8). For the plain henchman false
// runs the shipped `beq` (their body keeps its own textures).
bool RefiiMirrorTextureMorphsForPlayerTwo() {
  if (RefiiCoopGetOwnHero() == 0) {
    return false;
  }
  Once(g_said_texture, "player two's texture morphs are applied");
  return true;
}
