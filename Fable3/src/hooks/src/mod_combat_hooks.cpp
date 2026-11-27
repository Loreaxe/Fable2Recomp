// refii - Fable 3's combat seams for mods: a hit's arrival and delivery, the health's take, the
// flinch, the block, the strike's swing and end, the roll, an ability request, an animation event,
// movement's interrupt test, a GameAction and the hero's target — raised with the catalog's kinds
// and field names (Godot/src/Engine/mods/Engine_ModApi.cs), DECIDED in the seam where the catalog
// lets a mod refuse or rewrite (modlua::Decide).
//
// Every address is the TU3 IDB's (the disc's is bound by name, Fable3/versions/Fable3); each was
// matched to the disc's by the TU3 IDB's "disc 0x..." function comment and both decompiled.
//
// THE HIT (NHittable::CHit, default ctor 0x824B34D8): +4 the attacker's smart pointer, +0x24
// knockdown, +0x25 unblockable, +0x26 the hit a block answers 5, +0x4C the cast's serial, +0x50 its
// level (-1 none), +0x54 the category (3 a spell), +0x58 the spell type (-1 none), +0x5C the kind,
// +0xF0 the damage (+0xF4 / +0xF8 its modifiers, 1.0 each). The receiver copies it (0x824B3E40)
// and every listener gets the copy, in priority order: the block (0), the health, the flinch.
//
// THE ACTION (CAction): +4 the owner's smart pointer (the owner `**(a+8)`), +0x44 started, +0x45
// ended, +0x47 stopped, +0x48 the interrupt veto, +0x49 interrupt allowed, +0x4A movement allowed;
// vtable slot 7 the clock (f1). The action manager is component 57; its +0x30 is the live action
// (slot 26 `lwz r11,0x30(r3)`, both builds).

#include <array>
#include <atomic>
#include <cstdint>
#include <string>
#include <string_view>

#include <rex/ppc.h>
#include <rex/system/kernel_state.h>
#include <rex/system/xmemory.h>

#include "mod_lua.h"
#include "mod_seam.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_CECHitReceiver_OnHit);
REX_EXTERN(__imp__rex_CECHealth_OnHit);
REX_EXTERN(__imp__rex_CECHitResponsePlayAction_OnHit);
REX_EXTERN(__imp__rex_CActionBlock_OnHit);
REX_EXTERN(__imp__rex_CAbilityActionMelee_CommitSwing);
REX_EXTERN(__imp__rex_CAction_End);
REX_EXTERN(__imp__rex_CAbilityActionStrafeRoll_CanStart);
REX_EXTERN(__imp__rex_CAbilityActionStrafeRoll_Start);
REX_EXTERN(__imp__rex_CECAbilities_PerformRequest);
REX_EXTERN(__imp__rex_CAction_OnAnimEvent);
REX_EXTERN(__imp__rex_CAction_MovementInterruptible);
REX_EXTERN(__imp__rex_CPlayerActionReceiver_ProcessAction);
REX_EXTERN(__imp__rex_CECTargetingPlayer_SetTarget);
REX_EXTERN(__imp__rex_CECTargetingPlayer_ClearTarget);
REX_EXTERN(rex_CECHealth_CalculateDamage);
REX_EXTERN(rex_CECHealth_IsLethal);
REX_EXTERN(rex_CECHealth_GetMax);
REX_EXTERN(rex_CAbilityProperties_GetBool);
REX_EXTERN(rex_CActionManager_ResolveHandle);

namespace {

namespace modlua = refii::fable3::modlua;
using namespace refii::fable3::seam;

// The catalog's kinds (Fable2/src/vfs/include/core/refii_mod_events.h).
constexpr int32_t kGameAction = 5;
constexpr int32_t kAbilityRequest = 6;
constexpr int32_t kMeleeSwing = 7;
constexpr int32_t kRoll = 8;
constexpr int32_t kRollStarted = 9;
constexpr int32_t kBlock = 10;
constexpr int32_t kAnim = 11;
constexpr int32_t kHit = 12;
constexpr int32_t kHitLanded = 13;
constexpr int32_t kHitResponse = 14;
constexpr int32_t kDamage = 23;
constexpr int32_t kTargetChanged = 49;
constexpr int32_t kMeleeSwingEnded = 110;
constexpr int32_t kActionMoveInterrupt = 111;

constexpr uint32_t kHitAttacker = 0x4u;
constexpr uint32_t kHitKnockdown = 0x24u;
constexpr uint32_t kHitUnblockable = 0x25u;
constexpr uint32_t kHitParried = 0x26u;
constexpr uint32_t kHitReversal = 0x2Bu;
constexpr uint32_t kHitSerial = 0x4Cu;
constexpr uint32_t kHitLevel = 0x50u;
constexpr uint32_t kHitCategory = 0x54u;
constexpr uint32_t kHitSpellType = 0x58u;
constexpr uint32_t kHitKind = 0x5Cu;
constexpr uint32_t kHitDamage = 0xF0u;
constexpr uint32_t kHitCategorySpell = 3u;

constexpr uint32_t kHitExclusionComponent = 24u;
constexpr uint32_t kHealthComponent = 40u;
constexpr uint32_t kActionManagerComponent = 57u;
constexpr uint32_t kManagerCurrentAction = 0x30u;

constexpr uint32_t kActionOwnerBlock = 0x8u;
constexpr uint32_t kActionStarted = 0x44u;
constexpr uint32_t kActionEnded = 0x45u;
constexpr uint32_t kActionStopped = 0x47u;
constexpr uint32_t kActionVeto = 0x48u;
constexpr uint32_t kActionInterruptAllowed = 0x49u;
constexpr uint32_t kActionElapsedSlot = 7u;

/// The game (`dword_83597034`).
const refii::guest::Addr kGame{0x83597034u};

/// The action's owner: `**(a+8)` (GetGUIEntryEntity, the block listener's `addi r3,r27,-0x5C`).
uint32_t OwnerOf(const uint8_t* base, uint32_t action) {
  const uint32_t owner = Word(base, Word(base, action + kActionOwnerBlock));
  return Plausible(owner) ? owner : 0u;
}

/// A CCharString's text (its one word the rep, the rep's +0 the characters), as a Lua literal's body.
std::string CharString(const uint8_t* base, uint32_t string) {
  const uint32_t chars = Word(base, Word(base, string));
  if (!Plausible(chars)) {
    return {};
  }
  size_t n = 0;
  while (n < 96u && base[chars + n] != 0) {
    ++n;
  }
  return modlua::Quoted(std::string_view(reinterpret_cast<const char*>(base + chars), n));
}

int32_t SpellType(const uint8_t* base, uint32_t hit) {
  return Word(base, hit + kHitCategory) == kHitCategorySpell ? static_cast<int32_t>(Word(base, hit + kHitSpellType)) : -1;
}

/// Health.GetMax (h) -> f1, the leaf the health dispatcher calls.
float MaxHealth(const PPCContext& ctx, uint8_t* base, uint32_t health) {
  PPCContext work = Frame(ctx);
  work.r3.u32 = health;
  rex_CECHealth_GetMax(work, base);
  return static_cast<float>(work.f1.f64);
}

/// The game's own damage for a hit on a health (CalculateDamage 0x82629D28: 0 unless the health's
/// +0x54 group admits the hit and the attacker may hurt it, else linear in the hit's +0xF0).
float GameDamage(const PPCContext& ctx, uint8_t* base, uint32_t health, uint32_t hit) {
  PPCContext work = Frame(ctx);
  work.r3.u32 = health;
  work.r4.u32 = hit;
  rex_CECHealth_CalculateDamage(work, base);
  return static_cast<float>(work.f1.f64);
}

/// The game's own lethal test (0x82626300: the invulnerability bytes +0x4D..+0x4F and the heroes'
/// invulnerable globals, then CalculateDamage >= health).
bool Lethal(const PPCContext& ctx, uint8_t* base, uint32_t health, uint32_t hit) {
  PPCContext work = Frame(ctx);
  work.r3.u32 = health;
  work.r4.u32 = hit;
  rex_CECHealth_IsLethal(work, base);
  return (work.r3.u32 & 0xFFu) != 0u;
}

// ---------------------------------------------------------------------------------------------
// The strikes in flight: a strike CommitSwing committed, until its action ends.

struct SwingRecord {
  uint32_t action = 0;
  uint32_t entity = 0;
  int32_t hits = 0;
  int32_t strike = -1;
  bool flourish = false;
};
/// More than both heroes' strikes and every creature mid-swing near them.
std::array<SwingRecord, 32> g_swings{};
size_t g_swing_next = 0;
int32_t g_swings_live = 0;

void RecordSwing(uint32_t action, uint32_t entity, int32_t strike, bool flourish) {
  if (action == 0u || entity == 0u) {
    return;
  }
  for (SwingRecord& r : g_swings) {
    if (r.action == action) {
      r = SwingRecord{action, entity, 0, strike, flourish};
      return;
    }
  }
  SwingRecord& slot = g_swings[g_swing_next];
  g_swing_next = (g_swing_next + 1u) % g_swings.size();
  if (slot.action == 0u) {
    ++g_swings_live;
  }
  slot = SwingRecord{action, entity, 0, strike, flourish};
}

SwingRecord* SwingOf(uint32_t action) {
  if (action == 0u || g_swings_live == 0) {
    return nullptr;
  }
  for (SwingRecord& r : g_swings) {
    if (r.action == action) {
      return &r;
    }
  }
  return nullptr;
}

uint32_t CurrentActionOf(const uint8_t* base, uint32_t entity) {
  const uint32_t manager = Component(base, entity, kActionManagerComponent);
  const uint32_t action = Word(base, manager + kManagerCurrentAction);
  return Plausible(action) && base[action + kActionStopped] == 0u ? action : 0u;
}

/// A delivered hit by `attacker`: counted for the strike that is its current action.
void NoteSwingHit(const uint8_t* base, uint32_t attacker) {
  if (attacker == 0u || g_swings_live == 0) {
    return;
  }
  if (SwingRecord* r = SwingOf(CurrentActionOf(base, attacker)); r != nullptr && r->entity == attacker) {
    ++r->hits;
  }
}

/// A refused melee press (16) refuses that creature's next release (17).
std::array<uint32_t, 8> g_refused_press{};

// ---------------------------------------------------------------------------------------------
// The deferred interrupt grants (modlua::AllowActionInterruptAt), granted once due.

struct PendingInterrupt {
  uint32_t action = 0;
  float at = 0.0f;
  float last = 0.0f;
  uint32_t ticks = 0;
};
std::array<PendingInterrupt, 32> g_pending_interrupts{};
/// A grant whose action never reaches its time is dropped after a minute of HF steps.
constexpr uint32_t kPendingInterruptTicks = 1800u;

}  // namespace

namespace refii::fable3::modlua {

uint32_t CurrentAction(const uint8_t* base, uint32_t entity) { return Live(base, entity) ? CurrentActionOf(base, entity) : 0u; }

bool ActionElapsed(const PPCContext& ctx, uint8_t* base, uint32_t action, float& seconds) {
  if (!Plausible(action) || !Plausible(Word(base, action))) {
    return false;
  }
  PPCContext work = Frame(ctx);
  if (!CallSlot(work, base, action, kActionElapsedSlot)) {
    return false;
  }
  seconds = static_cast<float>(work.f1.f64);
  return true;
}

bool ActionInterruptible(const uint8_t* base, uint32_t action) {
  return Plausible(action) && base[action + kActionVeto] == 0u && base[action + kActionInterruptAllowed] != 0u;
}

/// The anim handler's own grant: `if (!+0x48) +0x49 = 1` (ALLOW_ACTION_INTERRUPT, 0x828a6ad4).
bool AllowActionInterrupt(uint8_t* base, uint32_t action) {
  if (!Plausible(action) || base[action + kActionVeto] != 0u) {
    return false;
  }
  base[action + kActionInterruptAllowed] = 1u;
  return true;
}

bool AllowActionInterruptAt(const PPCContext& ctx, uint8_t* base, uint32_t action, float seconds) {
  float elapsed = 0.0f;
  if (!Plausible(action) || base[action + kActionStopped] != 0u || base[action + kActionVeto] != 0u ||
      !ActionElapsed(ctx, base, action, elapsed)) {
    return false;
  }
  if (!(seconds > elapsed)) {
    return AllowActionInterrupt(base, action);
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
    return false;
  }
  *slot = PendingInterrupt{action, seconds, elapsed, 0u};
  return true;
}

void PumpActionInterrupts(const PPCContext& ctx, uint8_t* base) {
  for (PendingInterrupt& pending : g_pending_interrupts) {
    if (pending.action == 0u) {
      continue;
    }
    float elapsed = 0.0f;
    // Dropped when the action stops, its clock runs backwards (the address is another action's
    // now) or it never gets there.
    if (base[pending.action + kActionStopped] != 0u || !ActionElapsed(ctx, base, pending.action, elapsed) ||
        elapsed + 1e-4f < pending.last || ++pending.ticks > kPendingInterruptTicks) {
      pending.action = 0u;
      continue;
    }
    pending.last = elapsed;
    if (elapsed >= pending.at) {
      AllowActionInterrupt(base, pending.action);
      pending.action = 0u;
    }
  }
}

}  // namespace refii::fable3::modlua

// hit / hit_landed. CECHitReceiver::OnHit (r3 the victim's component 36, owner at +4; r4 the hit;
// r5 a flag): its gates come first and drop the hit by a plain return (loc_824B768C) — the
// receiver's +0x1C clear, its +0x1D clear unless the hit's kind is 10 (`lwz r10,0x5C(r4)`,
// `lbz r11,0x1D(r3)`, `lbz r10,0x1C(r21)`, 0x824b4d04..0x824b4d38), the world's
// `*(*(*(*(*(game+0xC)+0x1C)+4)+0x20)+0x11)` (0x824b4d3c..0x824b4d5c), and an entity whose 64-bit
// +0x28 has bit 37 and whose component 24 has +0x1C (sub_828733E0). Then the copy (0x824b4da8)
// carries +0x24.. and +0xF0..+0xF8 to every listener, so a written damage or knockdown is stored in
// the hit before the call and put back after it (the caller's hit may be delivered again). A cancel
// returns before anything was touched, as the gates do; the callers discard the return.
REX_EXTERN(rex_CECHitReceiver_OnHit) {
  const uint32_t component = ctx.r3.u32;
  const uint32_t hit = ctx.r4.u32;
  const bool pre = modlua::Wants(kHit);
  const bool post = modlua::Wants(kHitLanded);
  const uint32_t victim = Word(base, component + 4u);
  bool taken = Plausible(component) && Plausible(hit) && Live(base, victim) && base[component + 0x1Cu] != 0u &&
               (base[component + 0x1Du] != 0u || Word(base, hit + kHitKind) == 10u);
  if (taken) {
    const uint32_t world = Word(base, Word(base, Word(base, Word(base, Word(base, kGame) + 0xCu) + 0x1Cu) + 4u) + 0x20u);
    taken = !(Plausible(world) && base[world + 0x11u] != 0u);
  }
  if (taken && (Word(base, victim + 0x28u) & 0x20u) != 0u) {
    const uint32_t exclusion = Component(base, victim, kHitExclusionComponent);
    taken = !(exclusion != 0u && base[exclusion + 0x1Cu] != 0u);
  }
  const uint32_t attacker = taken ? Deref(base, hit + kHitAttacker) : 0u;
  if (taken) {
    modlua::NoteImpactHit(Word(base, hit + kHitSerial));
  }
  if (!taken || (!pre && !post)) {
    __imp__rex_CECHitReceiver_OnHit(ctx, base);
    if (taken) {
      NoteSwingHit(base, attacker);
    }
    return;
  }

  const float damage = Float(base, hit + kHitDamage);
  const uint8_t knockdown = base[hit + kHitKnockdown];
  const int32_t spell_type = SpellType(base, hit);
  const int32_t spell_level = static_cast<int32_t>(Word(base, hit + kHitLevel));
  const uint32_t spell_serial = Word(base, hit + kHitSerial);
  float carried = damage;
  bool knocked = knockdown != 0u;
  if (pre) {
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kHit,
        Fields("victim=%u,victim_player=%d,attacker=%u,attacker_player=%d,damage=%.9g,spell_type=%d,spell_level=%d,"
               "spell_serial=%u,knockdown=%s,tag=%d",
               Handle(base, victim), modlua::PlayerOf(base, victim), Handle(base, attacker), modlua::PlayerOf(base, attacker),
               Fin(damage), spell_type, spell_level, spell_serial, Bool(knocked), 0));
    if (verdict.cancelled) {
      return;
    }
    double written = 0.0;
    if (verdict.Read("damage", written) && written == written) {
      carried = static_cast<float>(written < 0.0 ? 0.0 : written);
    }
    if (verdict.Read("knockdown", written)) {
      knocked = written != 0.0;
    }
  }
  if (carried != damage) {
    StoreFloat(base, hit + kHitDamage, carried);
  }
  if (knocked != (knockdown != 0u)) {
    base[hit + kHitKnockdown] = knocked ? 1u : 0u;
  }
  __imp__rex_CECHitReceiver_OnHit(ctx, base);
  if (carried != damage && Float(base, hit + kHitDamage) == carried) {
    StoreFloat(base, hit + kHitDamage, damage);
  }
  if (knocked != (knockdown != 0u)) {
    base[hit + kHitKnockdown] = knockdown;
  }
  NoteSwingHit(base, attacker);
  if (post) {
    modlua::QueueEvent(
        kHitLanded,
        Fields("victim=%u,victim_player=%d,attacker=%u,attacker_player=%d,damage=%.9g,spell_type=%d,spell_level=%d,"
               "spell_serial=%u,knockdown=%s,tag=%d",
               Handle(base, victim), modlua::PlayerOf(base, victim), Handle(base, attacker), modlua::PlayerOf(base, attacker),
               Fin(carried), spell_type, spell_level, spell_serial, Bool(knocked), 0));
  }
}

// damage. CECHealth::OnHit (r3 the listener = health+0xC, `addi r28,r22,-0xC` 0x82626430; r4 the
// receiver's copy; r5 the result word): gated on a result of 0 or 1 (`lwz r11,0(r5)` 0x826263f8),
// then CalculateDamage (0x8262643c) and the decrement through Modify. A cancel skips the call:
// nothing is written before CalculateDamage, OnHit never writes *r5, and its return is unused (it is
// reached only through the listener's slot 0).
REX_EXTERN(rex_CECHealth_OnHit) {
  const uint32_t listener = ctx.r3.u32;
  const uint32_t hit = ctx.r4.u32;
  const uint32_t result = Word(base, ctx.r5.u32);
  const uint32_t health = listener - 0xCu;
  if (!modlua::Wants(kDamage) || !Plausible(listener) || !Plausible(hit) || !Plausible(ctx.r5.u32) || result > 1u) {
    __imp__rex_CECHealth_OnHit(ctx, base);
    return;
  }
  const uint32_t victim = Word(base, health + 4u);
  const uint32_t attacker = Deref(base, hit + kHitAttacker);
  const modlua::Verdict verdict = modlua::Decide(
      ctx, base, kDamage,
      Fields("victim=%u,victim_player=%d,attacker=%u,attacker_player=%d,damage=%.9g,health=%.9g,max_health=%.9g,lethal=%s,"
             "spell_type=%d,spell_level=%d,spell_serial=%u",
             Handle(base, victim), modlua::PlayerOf(base, victim), Handle(base, attacker), modlua::PlayerOf(base, attacker),
             Fin(GameDamage(ctx, base, health, hit)), Fin(Float(base, health + 0x14u)), Fin(MaxHealth(ctx, base, health)),
             Bool(Lethal(ctx, base, health, hit)), SpellType(base, hit), static_cast<int32_t>(Word(base, hit + kHitLevel)),
             Word(base, hit + kHitSerial)));
  if (verdict.cancelled) {
    return;
  }
  __imp__rex_CECHealth_OnHit(ctx, base);
}

// hit_response. CECHitResponsePlayAction::OnHit (r3 the listener, the component at r3-0xC and its
// owner at r3-8, `lwz r10,-8(r3)` 0x82a88b74; r4 the hit; r5 the result word): the flinch or the
// knockdown is pushed on the action manager (CombatHitResponseBase, 0x82a896dc). Offered only where
// the game plays one: a result of 0 or 1, a kind other than 9 (`cmpwi r11,9` 0x82a890a8), not a
// reversal (`!lethal && hit+0x2B`) and not lethal — the game's own test on component 40
// (0x82a88c68), so a killing blow's fall always plays. A cancel skips the call: no action is pushed,
// and the hit streak, the morph reset and the stat it also keeps are skipped with it.
REX_EXTERN(rex_CECHitResponsePlayAction_OnHit) {
  const uint32_t listener = ctx.r3.u32;
  const uint32_t hit = ctx.r4.u32;
  if (!modlua::Wants(kHitResponse) || !Plausible(listener) || !Plausible(hit) || !Plausible(ctx.r5.u32)) {
    __imp__rex_CECHitResponsePlayAction_OnHit(ctx, base);
    return;
  }
  const uint32_t victim = Word(base, listener - 8u);
  const uint32_t health = Component(base, victim, kHealthComponent);
  const bool lethal = health != 0u && Lethal(ctx, base, health, hit);
  if (!Live(base, victim) || health == 0u || Word(base, ctx.r5.u32) > 1u || Word(base, hit + kHitKind) == 9u || lethal ||
      base[hit + kHitReversal] != 0u) {
    __imp__rex_CECHitResponsePlayAction_OnHit(ctx, base);
    return;
  }
  const modlua::Verdict verdict = modlua::Decide(
      ctx, base, kHitResponse,
      Fields("victim=%u,victim_player=%d,damage=%.9g,health=%.9g,max_health=%.9g", Handle(base, victim),
             modlua::PlayerOf(base, victim), Fin(GameDamage(ctx, base, health, hit)), Fin(Float(base, health + 0x14u)),
             Fin(MaxHealth(ctx, base, health))));
  if (verdict.cancelled) {
    return;
  }
  __imp__rex_CECHitResponsePlayAction_OnHit(ctx, base);
}

// block. CAction_CombatBlockHitResponse's hit listener (r3 the listener, at the action's +0x5C on
// TU3 — ctor 0x82BC86D0 `*(a1+92)` — and +0x54 on the disc; r4 the hit; r5 the result word): a hit
// with +0x26 is answered 5 (`li r11,5; stw r11,0(r5)` 0x82bc878c) and offered to no one; one with
// +0x25 is unblockable and the listener writes nothing; else it answers 2 or 3 and pushes the
// attacker's recoil. The only real block listener on Fable 3 (the BlockPose one is a stub). A cancel
// skips the call: *r5 stays the receiver's 0, so the health and the flinch take the hit in full.
REX_EXTERN(rex_CActionBlock_OnHit) {
  const uint32_t listener = ctx.r3.u32;
  const uint32_t hit = ctx.r4.u32;
  if (!modlua::Wants(kBlock) || !Plausible(listener) || !Plausible(hit) || base[hit + kHitParried] != 0u) {
    __imp__rex_CActionBlock_OnHit(ctx, base);
    return;
  }
  const uint32_t action = listener - refii::guest::Pick(0x5Cu, 0x54u);
  const uint32_t blocker = OwnerOf(base, action);
  const bool unblockable = base[hit + kHitUnblockable] != 0u;
  const modlua::Verdict verdict = modlua::Decide(
      ctx, base, kBlock,
      Fields("blocker=%u,player=%d,damage=%.9g,unblockable=%s", Handle(base, blocker), modlua::PlayerOf(base, blocker),
             Fin(Float(base, hit + kHitDamage)), Bool(unblockable)));
  if (verdict.cancelled && !unblockable) {
    return;
  }
  __imp__rex_CActionBlock_OnHit(ctx, base);
}

namespace {

/// The ability's EAbilityActionType, which CommitSwing copies to the live action (TU3 `lwz
/// r10,0x10(r31)` / `stw r10,0x98(r11)` 0x829C468C..90, disc 0x8297BC44..48).
constexpr uint32_t kAbilityActionType = 0x10u;
/// The ability's GDB property table, which the release strike's CanStart hands the bool lookup
/// (TU3 `lwz r3,4(r27)` 0x829C63C4, disc 0x8297D954).
constexpr uint32_t kAbilityProperties = 0x04u;
/// Fnv1HashWithBasis("Flourish", 0x811C9DC5), stored by its static initialiser (TU3 0x83213C30,
/// disc 0x831DF3D8) and read by the release strike's CanStart. TU3 dword_8359D598 / disc
/// dword_8355C78C.
constexpr uint32_t kFlourishHash = 0x8359D598u;

/// The ability's own "Flourish" property, as the release strike's CanStart reads it (TU3
/// 0x829C63C4..D0 / disc 0x8297D954..60): rex_CAbilityProperties_GetBool (r3 the table, r4 the
/// hash, r5 the bool out; 1 when the property is a bool).
bool AbilityIsFlourish(const PPCContext& ctx, uint8_t* base, uint32_t ability) {
  const uint32_t properties = Word(base, ability + kAbilityProperties);
  const uint32_t hash = Word(base, refii::guest::Address(kFlourishHash));
  if (properties == 0u || hash == 0u) {
    return false;
  }
  PPCContext work = Frame(ctx);
  const uint32_t out = (work.r1.u32 + 0x80u) & ~0xFu;
  base[out] = 0u;
  work.r3.u64 = properties;
  work.r4.u64 = hash;
  work.r5.u64 = out;
  rex_CAbilityProperties_GetBool(work, base);
  return work.r3.u32 != 0u && base[out] != 0u;
}

}  // namespace

// melee_swing. CAbilityActionMelee::CommitSwing (r3 the ability, r4 the entity; returns 1): called
// by every strike's Perform after it pushed the strike action, so the action manager's live action
// is that strike after it — recorded for its end (melee_swing_ended). `strike` is the ability's
// EAbilityActionType (1 strike, 2 flourish class, 24 release strike, 36..38 confined, 39 advanced,
// 40 wall smash, 41 wall leap, 46 / 47 lunge close / far, 48 sprint attack); `flourish` its own GDB
// Flourish property — the charged flourish, a release strike whose type the Perform overwrites with
// 2 only after this returns.
REX_EXTERN(rex_CAbilityActionMelee_CommitSwing) {
  const uint32_t ability = ctx.r3.u32;
  const uint32_t entity = ctx.r4.u32;
  __imp__rex_CAbilityActionMelee_CommitSwing(ctx, base);
  const bool swing = modlua::Wants(kMeleeSwing);
  const bool ended = modlua::Wants(kMeleeSwingEnded);
  if ((!swing && !ended) || !Live(base, entity)) {
    return;
  }
  const uint32_t action = CurrentActionOf(base, entity);
  const int32_t strike = ability != 0u ? static_cast<int32_t>(Word(base, ability + kAbilityActionType)) : -1;
  const bool flourish = ability != 0u && AbilityIsFlourish(ctx, base, ability);
  if (ended) {
    RecordSwing(action, entity, strike, flourish);
  }
  if (swing) {
    modlua::QueueEvent(kMeleeSwing, Fields("entity=%u,player=%d,action=%u,action_type=%d,action_name='%s',strike=%d,flourish=%s",
                                           Handle(base, entity), modlua::PlayerOf(base, entity), ObjectHandle(action), -1, "",
                                           strike, Bool(flourish)));
  }
}

// melee_swing_ended. CAction::End (r3 the action): its OnEnd runs only for a started action
// (`lbz r11,0x44(r3)` 0x8225093c) and the end marks it ended (+0x45), so started-and-not-ended
// before the call is the one real end. Every managed action's end passes through it.
REX_EXTERN(rex_CAction_End) {
  const uint32_t action = ctx.r3.u32;
  if (Plausible(action) && modlua::NoteChargeEnd(base, action)) {
    __imp__rex_CAction_End(ctx, base);
    modlua::RaiseChargeEnd(base);
    return;
  }
  SwingRecord* swing = Plausible(action) ? SwingOf(action) : nullptr;
  if (swing == nullptr) {
    __imp__rex_CAction_End(ctx, base);
    return;
  }
  const bool live = base[action + kActionStarted] != 0u && base[action + kActionEnded] == 0u;
  const SwingRecord ended = *swing;
  *swing = SwingRecord{};
  --g_swings_live;
  __imp__rex_CAction_End(ctx, base);
  if (live && modlua::Wants(kMeleeSwingEnded)) {
    modlua::QueueEvent(kMeleeSwingEnded, Fields("entity=%u,player=%d,action=%u,action_type=%d,hits=%d,strike=%d,flourish=%s",
                                                Handle(base, ended.entity), modlua::PlayerOf(base, ended.entity), ObjectHandle(action),
                                                -1, ended.hits, ended.strike, Bool(ended.flourish)));
  }
}

// roll_can_start / roll_started. CAbilityActionStrafeRoll's slot 2 CanStart (r3 the ability, r4 the
// entity; pure tests, 0 refuses) and slot 3 Start (1 when the roll was pushed and resolved).
REX_EXTERN(rex_CAbilityActionStrafeRoll_CanStart) {
  const uint32_t entity = ctx.r4.u32;
  if (modlua::Wants(kRoll) && Live(base, entity)) {
    const modlua::Verdict verdict =
        modlua::Decide(ctx, base, kRoll, Fields("entity=%u,player=%d", Handle(base, entity), modlua::PlayerOf(base, entity)));
    if (verdict.cancelled) {
      ctx.r3.u64 = 0u;
      return;
    }
  }
  __imp__rex_CAbilityActionStrafeRoll_CanStart(ctx, base);
}

REX_EXTERN(rex_CAbilityActionStrafeRoll_Start) {
  const uint32_t entity = ctx.r4.u32;
  __imp__rex_CAbilityActionStrafeRoll_Start(ctx, base);
  if ((ctx.r3.u32 & 0xFFu) != 0u && modlua::Wants(kRollStarted) && Live(base, entity)) {
    modlua::QueueEvent(kRollStarted, Fields("entity=%u,player=%d", Handle(base, entity), modlua::PlayerOf(base, entity)));
  }
}

// ability_request. CECAbilities::PerformRequest (r3 the entity, r4 the request; returns 1 only when
// an ability matched, `li r23,1` 0x826a8cc0): the type is the request's slot 1. A cancel returns 0,
// the answer its own gate gives (sub_826A9088); a refused melee press (16) refuses that creature's
// next release (17), which is reported and refused whatever a handler says.
REX_EXTERN(rex_CECAbilities_PerformRequest) {
  const uint32_t entity = ctx.r3.u32;
  const uint32_t request = ctx.r4.u32;
  if (!Live(base, entity) || !Plausible(request)) {
    __imp__rex_CECAbilities_PerformRequest(ctx, base);
    return;
  }
  constexpr uint32_t kMeleePressed = 16u;
  constexpr uint32_t kMeleeReleased = 17u;
  PPCContext work = Frame(ctx);
  const uint32_t type = CallSlot(work, base, request, 1u) ? work.r3.u32 : 0xFFFFFFFFu;
  if (type == kMeleeReleased) {
    for (uint32_t& refused : g_refused_press) {
      if (refused == entity) {
        refused = 0u;
        if (modlua::Wants(kAbilityRequest)) {
          modlua::QueueEvent(kAbilityRequest, Fields("entity=%u,player=%d,type=%u", Handle(base, entity),
                                                     modlua::PlayerOf(base, entity), type));
        }
        ctx.r3.u64 = 0u;
        return;
      }
    }
  }
  if (type != 0xFFFFFFFFu && modlua::Wants(kAbilityRequest)) {
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kAbilityRequest, Fields("entity=%u,player=%d,type=%u", Handle(base, entity), modlua::PlayerOf(base, entity), type));
    if (verdict.cancelled) {
      if (type == kMeleePressed) {
        uint32_t* slot = &g_refused_press[0];
        for (uint32_t& refused : g_refused_press) {
          if (refused == entity || refused == 0u) {
            slot = &refused;
            break;
          }
        }
        *slot = entity;
      }
      ctx.r3.u64 = 0u;
      return;
    }
  }
  __imp__rex_CECAbilities_PerformRequest(ctx, base);
}

// anim_event. CAction::OnAnimEvent (r3 the action, r4 the animation id, r6 the event's CCharString:
// `sub_821CD1B0(**r6, "PERFORM")` 0x828a67e0): PERFORM performs, ALLOW_ACTION_INTERRUPT sets +0x49
// unless +0x48 (0x828a6ad4), ALLOW_MOVEMENT_INTERRUPT +0x4A. Eleven overriders call this base. A
// cancel: the base never sees the event.
REX_EXTERN(rex_CAction_OnAnimEvent) {
  const uint32_t action = ctx.r3.u32;
  if (!modlua::Wants(kAnim) || !Plausible(action)) {
    __imp__rex_CAction_OnAnimEvent(ctx, base);
    return;
  }
  const uint32_t owner = OwnerOf(base, action);
  if (!Live(base, owner)) {
    __imp__rex_CAction_OnAnimEvent(ctx, base);
    return;
  }
  const modlua::Verdict verdict = modlua::Decide(
      ctx, base, kAnim,
      Fields("entity=%u,player=%d,name='%s',action=%u,action_type=%d,action_name='%s'", Handle(base, owner),
             modlua::PlayerOf(base, owner), CharString(base, ctx.r6.u32).c_str(), ObjectHandle(action), -1, ""));
  if (verdict.cancelled) {
    return;
  }
  __imp__rex_CAction_OnAnimEvent(ctx, base);
}

// action_move_interrupt. Movement's interrupt test (r3 the action handle {+4 the owner's block, +8
// the index}; answers `*(a+0x48) ? 0 : *(a+0x4A)` of the action the manager resolves, 0x827E1378).
// A pure query: a cancel answers no after it.
REX_EXTERN(rex_CAction_MovementInterruptible) {
  const uint32_t handle = ctx.r3.u32;
  __imp__rex_CAction_MovementInterruptible(ctx, base);
  if ((ctx.r3.u32 & 0xFFu) == 0u || !Plausible(handle) || !modlua::Wants(kActionMoveInterrupt)) {
    return;
  }
  const uint32_t entity = Word(base, Word(base, handle + 4u));
  if (!Live(base, entity)) {
    return;
  }
  PPCContext work = Frame(ctx);
  work.r3.u32 = handle;
  rex_CActionManager_ResolveHandle(work, base);
  const uint32_t action = Plausible(work.r3.u32) ? work.r3.u32 : 0u;
  const PPCContext answered = ctx;
  const modlua::Verdict verdict =
      modlua::Decide(ctx, base, kActionMoveInterrupt,
                     Fields("entity=%u,player=%d,action=%u,action_type=%d", Handle(base, entity), modlua::PlayerOf(base, entity),
                            ObjectHandle(action), -1));
  ctx.r3 = answered.r3;
  if (verdict.cancelled) {
    ctx.r3.u64 = 0u;
  }
}

// game_action. The player's GameAction receiver (r3 the receiver, its hero the smart pointer at +8;
// r4 the 32-byte GameAction, +0 the id): the hero's buttons as the control scheme maps them. A
// cancel skips the call: the dispatcher (sub_821CE3B0) discards its return, so the action is hidden
// from this receiver only.
REX_EXTERN(rex_CPlayerActionReceiver_ProcessAction) {
  const uint32_t receiver = ctx.r3.u32;
  const uint32_t message = ctx.r4.u32;
  if (!modlua::Wants(kGameAction) || !Plausible(receiver) || !Plausible(message)) {
    __imp__rex_CPlayerActionReceiver_ProcessAction(ctx, base);
    return;
  }
  const uint32_t hero = Deref(base, receiver + 8u);
  const int32_t player = modlua::PlayerOf(base, hero);
  if (player == 0) {
    __imp__rex_CPlayerActionReceiver_ProcessAction(ctx, base);
    return;
  }
  const modlua::Verdict verdict = modlua::Decide(
      ctx, base, kGameAction, Fields("hero=%u,player=%d,action=%u", Handle(base, hero), player, Word(base, message)));
  if (verdict.cancelled) {
    return;
  }
  __imp__rex_CPlayerActionReceiver_ProcessAction(ctx, base);
}

namespace {

/// The hero targeting's target: its slot 21 (SetTarget's first call, `(*a1 + 84)(a1)`).
uint32_t TargetOf(const PPCContext& ctx, uint8_t* base, uint32_t targeting) {
  PPCContext work = Frame(ctx);
  return CallSlot(work, base, targeting, 21u) && Plausible(work.r3.u32) ? work.r3.u32 : 0u;
}

/// target_changed after a set or clear that changed the hero's target.
void RaiseTarget(const PPCContext& ctx, uint8_t* base, uint32_t targeting, uint32_t before) {
  const uint32_t after = TargetOf(ctx, base, targeting);
  if (after == before) {
    return;
  }
  const uint32_t hero = Word(base, targeting + 4u);
  const int32_t player = modlua::PlayerOf(base, hero);
  if (player != 0) {
    modlua::QueueEvent(kTargetChanged, Fields("hero=%u,player=%d,target=%u", Handle(base, hero), player, Handle(base, after)));
  }
}

}  // namespace

// target_changed. The player targeting component's SetTarget (slot 23: r3 the component, owner at
// +4; r4 the target) and ClearTarget (slot 22: r3), compared through its own GetTarget (slot 21).
REX_EXTERN(rex_CECTargetingPlayer_SetTarget) {
  const uint32_t targeting = ctx.r3.u32;
  const bool wants = modlua::Wants(kTargetChanged) && Plausible(targeting);
  const uint32_t before = wants ? TargetOf(ctx, base, targeting) : 0u;
  const PPCContext entry = ctx;
  __imp__rex_CECTargetingPlayer_SetTarget(ctx, base);
  if (wants) {
    RaiseTarget(entry, base, targeting, before);
  }
}

REX_EXTERN(rex_CECTargetingPlayer_ClearTarget) {
  const uint32_t targeting = ctx.r3.u32;
  const bool wants = modlua::Wants(kTargetChanged) && Plausible(targeting);
  const uint32_t before = wants ? TargetOf(ctx, base, targeting) : 0u;
  const PPCContext entry = ctx;
  __imp__rex_CECTargetingPlayer_ClearTarget(ctx, base);
  if (wants) {
    RaiseTarget(entry, base, targeting, before);
  }
}

// ---------------------------------------------------------------------------------------------
// Spells, the look-at flag, the lock and the cutscene.

REX_EXTERN(__imp__rex_CAbilityActionMagicCharge_CanStart);
REX_EXTERN(__imp__rex_CECSpellManager_ChargeLevelFromTime);
REX_EXTERN(__imp__rex_CECSpellManager_LaunchSlot);
REX_EXTERN(__imp__rex_CECSpellManager_OnSpellLaunched);
REX_EXTERN(__imp__rex_CPlayer_SetLookAtButtonDown);
REX_EXTERN(rex_Player_HasPlayerMode);

namespace {

constexpr int32_t kSpellCharge = 15;
constexpr int32_t kSpellChargeLevel = 16;
constexpr int32_t kSpellLaunch = 17;
constexpr int32_t kSpellLaunched = 18;
constexpr int32_t kLookAtButton = 94;
constexpr int32_t kCutscene = 100;
constexpr int32_t kLockLost = 104;

constexpr uint32_t kPlayerComponent = 37u;
constexpr uint32_t kTargetingComponent = 98u;
constexpr uint32_t kTierComponent = 24u;

/// The spell manager's slots: {first spell, second spell} bytes from +0x163, 0xFF ends them
/// (ChargeLevelFromTime's `*(u8*)(mgr+355+2*i) == 255`); its state +0x80 is 5 while the release
/// cascades; its charge count and first slot +0x180 / +0x17C.
constexpr uint32_t kSpellSlots = 0x163u;
constexpr uint32_t kSpellState = 0x80u;
constexpr uint32_t kSpellChargeCount = 0x180u;
constexpr uint32_t kSpellChargeFirst = 0x17Cu;
/// The single-slot cvar ChargeLevelFromTime reads (`if (byte_832DE90E) { *first = *count - 1; *count = 1; }`).
const refii::guest::Addr kSingleSlotCharge{0x832DE90Eu};
/// The forced-slot cvar LaunchSlot reads (`if (dword_832DE958 >= 0) slot = dword_832DE958`).
const refii::guest::Addr kForcedSlot{0x832DE958u};

/// The player component's foreground-cutscene byte (Player.SetForegroundCutsceneInEffect
/// 0x824361E8: `*(u8*)(c+54) = on`) and the held-LT scope's camera override (SetOverrideCameraMode
/// 0x8242F910: scope 2 writes `c[49]`, and GetOverrideCameraMode answers it while LT is held).
constexpr uint32_t kPlayerForeground = 0x36u;
constexpr uint32_t kPlayerLookAtCamera = 0xC4u;
/// The normal scope's camera override, beside it (SetOverrideCameraMode: scope 1 `stw r30, 0xC0(r31)`
/// TU3 0x8242F94C / disc 0x82411CCC; scope 2 `stw r30, 0xC4(r31)` 0x8242F958 / 0x82411CD8). The
/// component's save writes the two as OverrideCameraModeNormal / OverrideCameraModeLookAt.
constexpr uint32_t kPlayerNormalCamera = 0xC0u;
/// The targeting component's force-lock byte (SetForceTargetLock 0x82A8FC60: `*(u8*)(c+20) = on`).
constexpr uint32_t kTargetingForce = 0x14u;
/// PLAYER_MODE_TARGET_LOCK: the targeting pass adds and clears it from the force byte.
constexpr uint32_t kTargetLockMode = 4u;

/// REFII_CUTSCENE_*: the cutscene event's kinds and in_cutscene's bits.
enum CutsceneBit : int32_t { kCutsceneFull = 1, kCutsceneInteractive = 2, kCutsceneLookAt = 4 };

int32_t FilledSlots(const uint8_t* base, uint32_t manager) {
  int32_t n = 0;
  while (n < 5 && base[manager + kSpellSlots + 2u * static_cast<uint32_t>(n)] != 0xFFu) {
    ++n;
  }
  return n;
}

bool HasPlayerMode(const PPCContext& ctx, uint8_t* base, uint32_t player_component, uint32_t mode) {
  if (!Plausible(player_component) || !Plausible(Word(base, player_component + 0x14u))) {
    return false;
  }
  PPCContext work = Frame(ctx);
  work.r3.u32 = player_component;
  work.r4.u32 = mode;
  rex_Player_HasPlayerMode(work, base);
  return (work.r3.u32 & 0xFFu) != 0u;
}

/// The top game-flow state is a full cutscene: GUI.IsFullCutscenePlaying (0x8254BC40) —
/// `*(*(**(*(*(game+0xC)+0x30)+4)+4)+8)`, its slot 1 answering 7 — read with every link checked.
bool FullCutscene(const PPCContext& ctx, uint8_t* base) {
  const uint32_t flow = Word(base, Word(base, Word(base, Word(base, kGame) + 0xCu) + 0x30u) + 4u);
  const uint32_t top = Word(base, Word(base, Word(base, flow) + 4u) + 8u);
  if (!Plausible(flow) || !Plausible(top) || !Plausible(Word(base, top))) {
    return false;
  }
  PPCContext work = Frame(ctx);
  return CallSlot(work, base, top, 1u) && work.r3.u32 == 7u;
}

/// The halves of each player's camera override record a mod's camera_override hold stands on (1 Normal,
/// 2 LookAt), stated by the hold itself (Refii._camera_held): a held LookAt half is not a look-at
/// cutscene, as on Fable 2.
std::array<std::atomic<int32_t>, 3> g_camera_held{};

int32_t CutsceneKinds(const PPCContext& ctx, uint8_t* base, int32_t player, bool full) {
  const uint32_t hero = modlua::Hero(base, player);
  const uint32_t component = Live(base, hero) ? Component(base, hero, kPlayerComponent) : 0u;
  int32_t kinds = full ? kCutsceneFull : 0;
  if (component != 0u && base[component + kPlayerForeground] != 0u) {
    kinds |= kCutsceneInteractive;
  }
  if (component != 0u && Word(base, component + kPlayerLookAtCamera) != 0u &&
      (player < 1 || player > 2 || (g_camera_held[static_cast<size_t>(player)].load(std::memory_order_relaxed) & 2) == 0)) {
    kinds |= kCutsceneLookAt;
  }
  (void)ctx;
  return kinds;
}

uint32_t TargetingOf(const uint8_t* base, int32_t player) {
  const uint32_t hero = modlua::Hero(base, player);
  return Live(base, hero) ? Component(base, hero, kTargetingComponent) : 0u;
}

bool LockHeld(const PPCContext& ctx, uint8_t* base, int32_t player) {
  const uint32_t hero = modlua::Hero(base, player);
  return Live(base, hero) && HasPlayerMode(ctx, base, Component(base, hero, kPlayerComponent), kTargetLockMode);
}

bool g_full = false;
std::array<int32_t, 3> g_kinds{};
std::array<bool, 3> g_locked{};

}  // namespace

namespace refii::fable3::modlua {

void RestoreCameraModes(uint8_t* base, uint32_t hero, uint32_t normal, uint32_t look_at) {
  const uint32_t component = Live(base, hero) ? Component(base, hero, kPlayerComponent) : 0u;
  if (component != 0u) {
    StoreU32(base, component + kPlayerNormalCamera, normal);
    StoreU32(base, component + kPlayerLookAtCamera, look_at);
  }
}

void NoteCameraHeld(int32_t player, int32_t bits) {
  if (player == 1 || player == 2) {
    g_camera_held[static_cast<size_t>(player)].store(bits & 3, std::memory_order_relaxed);
  }
}

int32_t InCutscene(const PPCContext& ctx, uint8_t* base, int32_t player) {
  const bool full = FullCutscene(ctx, base);
  if (player == 1 || player == 2) {
    return CutsceneKinds(ctx, base, player, full);
  }
  return CutsceneKinds(ctx, base, 1, full) | CutsceneKinds(ctx, base, 2, full);
}

uint32_t LockedTarget(const PPCContext& ctx, uint8_t* base, int32_t player) {
  const uint32_t targeting = TargetingOf(base, player);
  if (targeting == 0u || (base[targeting + kTargetingForce] == 0u && !LockHeld(ctx, base, player))) {
    return 0u;
  }
  const uint32_t target = TargetOf(ctx, base, targeting);
  return Live(base, target) ? target : 0u;
}

bool LockTarget(const PPCContext& ctx, uint8_t* base, int32_t player, uint32_t target) {
  const uint32_t targeting = TargetingOf(base, player);
  if (targeting == 0u) {
    return false;
  }
  if (target != 0u) {
    if (!Live(base, target)) {
      return false;
    }
    PPCContext work = Frame(ctx);
    work.r4.u32 = target;
    if (!CallSlot(work, base, targeting, 23u)) {
      return false;
    }
  }
  base[targeting + kTargetingForce] = 1u;
  return true;
}

bool ReleaseLock(uint8_t* base, int32_t player) {
  const uint32_t targeting = TargetingOf(base, player);
  if (targeting == 0u) {
    return false;
  }
  base[targeting + kTargetingForce] = 0u;
  return true;
}

int32_t CreatureTier(const uint8_t* base, uint32_t entity) {
  const uint32_t tier = Live(base, entity) ? Component(base, entity, kTierComponent) : 0u;
  if (tier == 0u) {
    return 0;
  }
  return (base[tier + 0x1Eu] != 0u ? 1 : 0) | (base[tier + 0x1Du] != 0u ? 2 : 0);
}

bool Living(const uint8_t* base, uint32_t entity) {
  if (!Live(base, entity)) {
    return false;
  }
  const uint32_t health = Component(base, entity, kHealthComponent);
  return health == 0u || Float(base, health + 0x14u) > 0.0f;
}

// cutscene and lock_lost: the game's own statements read each tick, raised on their edges.
void PollCombat(const PPCContext& ctx, uint8_t* base) {
  PumpActionInterrupts(ctx, base);
  if (Wants(kCutscene)) {
    const bool full = FullCutscene(ctx, base);
    if (full != g_full) {
      g_full = full;
      QueueEvent(kCutscene, Fields("player=%d,active=%s,kind=%d,kinds=%d,hero=%u", 0, Bool(full), kCutsceneFull,
                                   full ? kCutsceneFull : 0, 0u));
    }
    for (int32_t player = 1; player <= 2; ++player) {
      const int32_t kinds = CutsceneKinds(ctx, base, player, full) & ~kCutsceneFull;
      const int32_t before = g_kinds[static_cast<size_t>(player)];
      for (const int32_t kind : {kCutsceneInteractive, kCutsceneLookAt}) {
        if ((kinds & kind) != (before & kind)) {
          QueueEvent(kCutscene, Fields("player=%d,active=%s,kind=%d,kinds=%d,hero=%u", player, Bool((kinds & kind) != 0), kind,
                                       kinds | (full ? kCutsceneFull : 0), Handle(base, Hero(base, player))));
        }
      }
      g_kinds[static_cast<size_t>(player)] = kinds;
    }
  }
  if (Wants(kLockLost)) {
    for (int32_t player = 1; player <= 2; ++player) {
      const bool held = LockHeld(ctx, base, player);
      if (g_locked[static_cast<size_t>(player)] && !held) {
        const uint32_t targeting = TargetingOf(base, player);
        const uint32_t target = targeting != 0u ? TargetOf(ctx, base, targeting) : 0u;
        QueueEvent(kLockLost, Fields("player=%d,hero=%u,target=%u", player, Handle(base, Hero(base, player)), Handle(base, target)));
      }
      g_locked[static_cast<size_t>(player)] = held;
    }
  }
}

}  // namespace refii::fable3::modlua

// spell_charge. The quick charge's CanStart (slot 2: r3 the ability, r4 the entity, r5 the request):
// the charge is asked for by a type-18 request with +0x30 clear (`(*(*a3+4))(a3) == 18 ... !a3[12]`,
// 0x82c0d208). A cancel answers 0 without the call: the charge does not start.
REX_EXTERN(rex_CAbilityActionMagicCharge_CanStart) {
  const uint32_t entity = ctx.r4.u32;
  const uint32_t request = ctx.r5.u32;
  if (modlua::Wants(kSpellCharge) && Live(base, entity) && Plausible(request) && Word(base, request + 0x30u) == 0u) {
    PPCContext work = Frame(ctx);
    if (CallSlot(work, base, request, 1u) && work.r3.u32 == 18u) {
      const modlua::Verdict verdict = modlua::Decide(
          ctx, base, kSpellCharge, Fields("entity=%u,player=%d", Handle(base, entity), modlua::PlayerOf(base, entity)));
      if (verdict.cancelled) {
        ctx.r3.u64 = 0u;
        return;
      }
    }
  }
  __imp__rex_CAbilityActionMagicCharge_CanStart(ctx, base);
  if ((ctx.r3.u32 & 0xFFu) != 0u) {
    modlua::NoteChargeStarted(base, entity);
  }
}

// spell_charge_level. ChargeLevelFromTime (r3 the manager, f1 the held time, r5 int* count, r6 int*
// first): count = the slots the time reaches (at least 1), first 0 — or, under the single-slot cvar,
// first = count - 1 and count 1. The top slot is first + count - 1. Raised for the manager's own
// charge (r5 = mgr+0x180, r6 = mgr+0x17C); a written level is clamped to the filled slots and stored
// the way the cvar reads it.
REX_EXTERN(rex_CECSpellManager_ChargeLevelFromTime) {
  const uint32_t manager = ctx.r3.u32;
  const uint32_t count = ctx.r5.u32;
  const uint32_t first = ctx.r6.u32;
  __imp__rex_CECSpellManager_ChargeLevelFromTime(ctx, base);
  if (!Plausible(manager) || count != manager + kSpellChargeCount || first != manager + kSpellChargeFirst ||
      !modlua::Wants(kSpellChargeLevel)) {
    return;
  }
  const uint32_t caster = Word(base, manager + 4u);
  if (!Live(base, caster)) {
    return;
  }
  const int32_t level = static_cast<int32_t>(Word(base, first)) + static_cast<int32_t>(Word(base, count)) - 1;
  const modlua::Verdict verdict = modlua::Decide(
      ctx, base, kSpellChargeLevel, Fields("caster=%u,player=%d,level=%d", Handle(base, caster), modlua::PlayerOf(base, caster), level));
  double written = 0.0;
  if (!verdict.Read("level", written) || !(written == written)) {
    return;
  }
  const int32_t top = FilledSlots(base, manager) - 1;
  int32_t want = written <= 0.0 ? 0 : written >= 4.0 ? 4 : static_cast<int32_t>(written);
  want = want > top ? (top < 0 ? 0 : top) : want;
  if (base[static_cast<uint32_t>(kSingleSlotCharge)] != 0u) {
    StoreU32(base, first, static_cast<uint32_t>(want));
    StoreU32(base, count, 1u);
  } else {
    StoreU32(base, first, 0u);
    StoreU32(base, count, static_cast<uint32_t>(want + 1));
  }
}

// spell_launch. LaunchSlot (r3 the manager, r4 targeted, r5 the slot — replaced by the forced-slot cvar
// when it is >= 0): the slot's first spell is the byte at +0x163 + 2*slot. A written type is that
// byte for this launch, put back after; a cancel skips the launch.
REX_EXTERN(rex_CECSpellManager_LaunchSlot) {
  const uint32_t manager = ctx.r3.u32;
  const uint32_t caster = Plausible(manager) ? Word(base, manager + 4u) : 0u;
  const int32_t forced = static_cast<int32_t>(Word(base, kForcedSlot));
  const int32_t slot = forced >= 0 ? forced : static_cast<int32_t>(ctx.r5.u32);
  if (!modlua::Wants(kSpellLaunch) || !Live(base, caster) || slot < 0 || slot > 4) {
    __imp__rex_CECSpellManager_LaunchSlot(ctx, base);
    return;
  }
  const uint32_t at = manager + kSpellSlots + 2u * static_cast<uint32_t>(slot);
  const uint8_t held = base[at];
  const int32_t type = held == 0xFFu ? -1 : static_cast<int32_t>(static_cast<int8_t>(held));
  const modlua::Verdict verdict = modlua::Decide(
      ctx, base, kSpellLaunch,
      Fields("caster=%u,player=%d,targeted=%s,slot=%d,type=%d,cascading=%s,tag=%d", Handle(base, caster),
             modlua::PlayerOf(base, caster), Bool((ctx.r4.u32 & 0xFFu) != 0u), slot, type,
             Bool(Word(base, manager + kSpellState) == 5u), 0));
  if (verdict.cancelled) {
    return;
  }
  double written = 0.0;
  const bool swap = verdict.Read("type", written) && written == written && written >= 0.0 && written <= 127.0 &&
                    static_cast<int32_t>(written) != type;
  if (swap) {
    base[at] = static_cast<uint8_t>(static_cast<int32_t>(written));
  }
  __imp__rex_CECSpellManager_LaunchSlot(ctx, base);
  if (swap) {
    base[at] = held;
  }
}

// spell_launched. OnSpellLaunched (r3 the manager, r4 the slot, r5 targeted): called once the launch
// made its spell.
REX_EXTERN(rex_CECSpellManager_OnSpellLaunched) {
  const uint32_t manager = ctx.r3.u32;
  const int32_t slot = static_cast<int32_t>(ctx.r4.u32);
  __imp__rex_CECSpellManager_OnSpellLaunched(ctx, base);
  const uint32_t caster = Plausible(manager) ? Word(base, manager + 4u) : 0u;
  if (modlua::Wants(kSpellLaunched) && Live(base, caster)) {
    modlua::QueueEvent(kSpellLaunched,
                       Fields("caster=%u,player=%d,slot=%d", Handle(base, caster), modlua::PlayerOf(base, caster), slot));
  }
}

// look_at_button. The hero's look-at record's writer (r3 the record, its hero the smart pointer at
// +0x30; r4 down): stores +0xC when it changes and re-picks the camera (0x823A9038). Decided
// before it: a cancel skips the writer, a written down replaces r4.
REX_EXTERN(rex_CPlayer_SetLookAtButtonDown) {
  const uint32_t record = ctx.r3.u32;
  if (!modlua::Wants(kLookAtButton) || !Plausible(record)) {
    __imp__rex_CPlayer_SetLookAtButtonDown(ctx, base);
    return;
  }
  const uint32_t hero = Deref(base, record + 0x30u);
  const int32_t player = modlua::PlayerOf(base, hero);
  if (player == 0) {
    __imp__rex_CPlayer_SetLookAtButtonDown(ctx, base);
    return;
  }
  const bool down = (ctx.r4.u32 & 0xFFu) != 0u;
  const modlua::Verdict verdict = modlua::Decide(
      ctx, base, kLookAtButton, Fields("hero=%u,player=%d,down=%s", Handle(base, hero), player, Bool(down)));
  if (verdict.cancelled) {
    return;
  }
  double written = 0.0;
  if (verdict.Read("down", written)) {
    ctx.r4.u64 = written != 0.0 ? 1u : 0u;
  }
  __imp__rex_CPlayer_SetLookAtButtonDown(ctx, base);
}

// ---------------------------------------------------------------------------------------------
// Spells made, shots fired, statuses put on and taken off, slow time.

REX_EXTERN(__imp__rex_CSpellRegistry_Create);
REX_EXTERN(__imp__rex_CECFirearm_Fire);
REX_EXTERN(__imp__rex_lhEntity_AddComponent);
REX_EXTERN(__imp__rex_lhEntity_RemoveComponent);
REX_EXTERN(__imp__rex_SpellLightning_Electrify);
REX_EXTERN(__imp__rex_SpellChaos_ApplyToVictim);
REX_EXTERN(__imp__rex_SpellFireball_Ignite);
REX_EXTERN(__imp__rex_SpellSlowTime_SetWorldScale);

namespace {

constexpr int32_t kSpellCreated = 19;
constexpr int32_t kRangedShot = 47;
constexpr int32_t kStatusEffect = 48;
constexpr int32_t kSlowTime = 62;
constexpr int32_t kStatusApplying = 102;

/// The cast whose status is being installed (status_applying's serial and source), for the
/// status_effect its AddComponent raises inside the installer.
uint32_t g_status_serial = 0xFFFFFFFFu;
uint32_t g_status_source = 0u;

/// A status component's id (its class's slot 4, `li r3,0xE5 / 0xE6 / 0xE8 / 0xE9 / 0x111; blr`) as the
/// catalog's effect: 1 electrified, 2 chaos, 3 on fire, 4 in a vortex, 6 mauled by a dog; 0 for none.
int32_t StatusEffectOf(uint32_t id) {
  switch (id) {
    case 229u: return 1;
    case 230u: return 2;
    case 232u: return 3;
    case 233u: return 4;
    case 273u: return 6;
    default: return 0;
  }
}

void RaiseStatusEffect(uint8_t* base, uint32_t entity, int32_t effect, bool started) {
  modlua::QueueEvent(kStatusEffect, Fields("entity=%u,player=%d,effect=%d,started=%s,serial=%u,source=%u", Handle(base, entity),
                                           modlua::PlayerOf(base, entity), effect, Bool(started), g_status_serial,
                                           Handle(base, g_status_source)));
}

/// status_applying, decided before an installer: a cancel skips it (no status, no damage, no effect —
/// no caller reads its r3), a written `seconds` (> 0) is handed back.
bool DecideStatus(PPCContext& ctx, uint8_t* base, uint32_t victim, int32_t effect, uint32_t serial, uint32_t source, int32_t level,
                  double seconds, double& written) {
  written = seconds;
  if (!Live(base, victim) || !modlua::Wants(kStatusApplying)) {
    return true;
  }
  const modlua::Verdict verdict = modlua::Decide(
      ctx, base, kStatusApplying,
      Fields("entity=%u,player=%d,effect=%d,serial=%u,source=%u,level=%d,seconds=%.9g,tag=%d", Handle(base, victim),
             modlua::PlayerOf(base, victim), effect, serial, Handle(base, source), level, seconds, 0));
  if (verdict.cancelled) {
    return false;
  }
  double value = 0.0;
  if (verdict.Read("seconds", value) && value == value && value > 0.0) {
    written = value;
  }
  return true;
}

/// Runs an installer with the cast stated for the status_effect inside it.
template <typename Call>
void WithStatusCast(uint32_t serial, uint32_t source, Call call) {
  const uint32_t outer_serial = g_status_serial;
  const uint32_t outer_source = g_status_source;
  g_status_serial = serial;
  g_status_source = source;
  call();
  g_status_serial = outer_serial;
  g_status_source = outer_source;
}

}  // namespace

// ---------------------------------------------------------------------------------------------
// What a cast put in the world, by serial (Refii.spell_parts / spell_serial / vortex_held): the
// factory's record, the live vortices, the fireball's impacts and the sword set's swords, kept by the
// spell seams below whether or not an event is wanted.

namespace {

/// The spell object (CSpellRegistry_Create's answer): +0x10 its type (the class data read's first
/// argument, sub_8268BCD8 `lwz r18,0x10(r3)` TU3 0x8268bcfc / disc 0x826628f4), +0xB8 the serial
/// (Create `stw r21,0xB8(r30)` TU3 0x8268bc54 / disc 0x8266284c).
constexpr uint32_t kCastSpellType = 0x10u;
constexpr uint32_t kCastSpellSerial = 0xB8u;
/// The vortex component (CECVortex): +4 the vortex entity; +0x14 the cast's serial (Init `mr r26,r6`
/// then `stw r26,0x14(r31)`, its spell's call `lwz r6,0xB8(r28)`); +0x170 its point (Init `li r11,0x170`
/// / `stvx128 v9,r31,r11`, the same vector it stores at +0x120); +0x218 / +0x21C the held vector's first
/// and end, 8-byte smart pointers whose +4 is the block holding the entity — Shutdown walks it
/// (`lwz r11,0x21C(r28)`, `lwz r10,0x218(r28)`, the entry's `lwz r11,4(r3)`, the block's `lwz r10,0(r11)`
/// handed to Release) and the update appends each capture to it (`addi r23,r24,0x218` .. `mr r3,r23;
/// bl sub_823C6750`, the vector's push_back, right before Capture).
constexpr uint32_t kCastVortexSerial = 0x14u;
constexpr uint32_t kCastVortexPoint = 0x170u;
constexpr uint32_t kCastVortexHeldFirst = 0x218u;
constexpr uint32_t kCastVortexHeldEnd = 0x21Cu;
constexpr uint32_t kCastVortexHeldStride = 8u;

struct CastRecord {
  uint32_t serial = 0;
  uint32_t spell = 0;
  uint32_t caster = 0;
  int32_t type = -1;
  int32_t level = 0;
  bool targeted = false;
  bool impacted = false;
  bool exploded = false;
  float impact[3] = {0.0f, 0.0f, 0.0f};
  uint32_t swords[8] = {};
  int32_t sword_count = 0;
};
std::array<CastRecord, 32> g_casts{};
size_t g_cast_next = 0;

struct LiveVortex {
  uint32_t component = 0;
  uint32_t serial = 0;
};
std::array<LiveVortex, 8> g_vortices{};

CastRecord* CastOf(uint32_t serial) {
  if (serial == 0u) {
    return nullptr;
  }
  for (CastRecord& c : g_casts) {
    if (c.serial == serial) {
      return &c;
    }
  }
  return nullptr;
}

/// The record for `serial`, a new one (the oldest replaced) when there is none.
CastRecord& CastSlot(uint32_t serial) {
  if (CastRecord* found = CastOf(serial)) {
    return *found;
  }
  CastRecord& slot = g_casts[g_cast_next];
  g_cast_next = (g_cast_next + 1u) % g_casts.size();
  slot = CastRecord{};
  slot.serial = serial;
  return slot;
}

/// The record's spell object while it still carries the cast (its serial and type).
uint32_t LiveSpell(const uint8_t* base, const CastRecord& cast) {
  return Plausible(cast.spell) && Word(base, cast.spell + kCastSpellSerial) == cast.serial &&
                 static_cast<int32_t>(Word(base, cast.spell + kCastSpellType)) == cast.type
             ? cast.spell
             : 0u;
}

void RecordCast(uint32_t spell, uint32_t serial, int32_t type, bool targeted, int32_t level, uint32_t caster) {
  if (serial == 0u) {
    return;
  }
  CastRecord& record = CastSlot(serial);
  record.spell = spell;
  record.type = type;
  record.targeted = targeted;
  record.level = level;
  record.caster = caster;
}

/// The spell registry stops `spell` (RefiiF3SpellEnding): its object is about to go.
void ForgetSpell(uint32_t spell) {
  for (CastRecord& c : g_casts) {
    if (spell != 0u && c.spell == spell) {
      c.spell = 0u;
    }
  }
}

void RecordVortex(const uint8_t* base, uint32_t component, bool live) {
  for (LiveVortex& v : g_vortices) {
    if (v.component == component) {
      v = LiveVortex{};
    }
  }
  if (!live || !Plausible(component)) {
    return;
  }
  for (LiveVortex& v : g_vortices) {
    if (v.component == 0u) {
      v.component = component;
      v.serial = Word(base, component + kCastVortexSerial);
      return;
    }
  }
}

const LiveVortex* VortexOf(const uint8_t* base, uint32_t serial) {
  for (const LiveVortex& v : g_vortices) {
    if (serial != 0u && v.serial == serial && v.component != 0u && Word(base, v.component + kCastVortexSerial) == serial) {
      return &v;
    }
  }
  return nullptr;
}

void RecordImpact(uint32_t serial, float x, float y, float z, bool detonated) {
  CastRecord* cast = CastOf(serial);
  if (cast == nullptr) {
    return;
  }
  cast->impacted = true;
  cast->exploded = cast->exploded || detonated;
  cast->impact[0] = x;
  cast->impact[1] = y;
  cast->impact[2] = z;
}

void RecordSword(uint32_t serial, uint32_t sword) {
  CastRecord* cast = CastOf(serial);
  if (cast == nullptr || !Plausible(sword)) {
    return;
  }
  for (int32_t i = 0; i < cast->sword_count; ++i) {
    if (cast->swords[i] == sword) {
      return;
    }
  }
  if (cast->sword_count < 8) {
    cast->swords[cast->sword_count++] = sword;
  }
}

}  // namespace

namespace refii::fable3::modlua {

bool SpellParts(const uint8_t* base, uint32_t serial, CastParts& out) {
  out = CastParts{};
  const CastRecord* cast = CastOf(serial);
  if (cast == nullptr || cast->type < 0) {
    return false;
  }
  out.spell = LiveSpell(base, *cast);
  out.type = cast->type;
  out.targeted = cast->targeted;
  out.level = cast->level;
  out.caster = Live(base, cast->caster) ? cast->caster : 0u;
  if (const LiveVortex* vortex = VortexOf(base, serial)) {
    out.vortex = vortex->component;
    const uint32_t entity = Word(base, vortex->component + 4u);
    out.anchor = Live(base, entity) ? entity : 0u;
    for (uint32_t i = 0; i < 3u; ++i) {
      out.origin[i] = Float(base, vortex->component + kCastVortexPoint + 4u * i);
    }
    out.has_origin = true;
  } else if (cast->impacted) {
    std::memcpy(out.origin, cast->impact, sizeof(out.origin));
    out.has_origin = true;
  }
  out.exploded = cast->exploded;
  for (int32_t i = 0; i < cast->sword_count; ++i) {
    if (Live(base, cast->swords[i])) {
      out.swords[out.sword_count++] = cast->swords[i];
    }
  }
  return true;
}

uint32_t SpellSerial(const uint8_t* base, uint32_t spell) {
  for (const CastRecord& c : g_casts) {
    if (spell != 0u && c.spell == spell && LiveSpell(base, c) != 0u) {
      return c.serial;
    }
  }
  return 0u;
}

int32_t VortexHeld(const uint8_t* base, uint32_t serial, uint32_t* out, int32_t max) {
  const LiveVortex* vortex = VortexOf(base, serial);
  if (vortex == nullptr) {
    return -1;
  }
  const uint32_t first = Word(base, vortex->component + kCastVortexHeldFirst);
  const uint32_t end = Word(base, vortex->component + kCastVortexHeldEnd);
  if (!Plausible(first) || end < first || (end - first) % kCastVortexHeldStride != 0u ||
      (end - first) / kCastVortexHeldStride > 256u) {
    return 0;
  }
  int32_t count = 0;
  for (uint32_t at = first; at < end; at += kCastVortexHeldStride) {
    const uint32_t victim = Deref(base, at);
    if (!Live(base, victim)) {
      continue;
    }
    if (out != nullptr && count < max) {
      out[count] = victim;
    }
    ++count;
  }
  return count;
}

}  // namespace refii::fable3::modlua

// spell_created. The spell registry's Create (r3 registry; r4 the parameters: +0 type, +8 the weave,
// +0x10 targeted (1), +0x18 level, +0x50 the target's smart pointer, +0x58 the caster's; returns the
// spell, its serial stored `stw r21,0xB8(r30)` 0x8268bc54). After it, for a spell made.
REX_EXTERN(rex_CSpellRegistry_Create) {
  const uint32_t params = ctx.r4.u32;
  const bool read = Plausible(params);
  const int32_t type = read ? static_cast<int32_t>(Word(base, params)) : -1;
  const bool targeted = read && Word(base, params + 0x10u) == 1u;
  const int32_t level = read ? static_cast<int32_t>(Word(base, params + 0x18u)) : 0;
  const uint32_t target = read ? Deref(base, params + 0x50u) : 0u;
  const uint32_t caster = read ? Deref(base, params + 0x58u) : 0u;
  __imp__rex_CSpellRegistry_Create(ctx, base);
  const uint32_t spell = ctx.r3.u32;
  if (!read || !Plausible(spell)) {
    return;
  }
  const uint32_t serial = Word(base, spell + 0xB8u);
  RecordCast(spell, serial, type, targeted, level, caster);
  if (!modlua::Wants(kSpellCreated)) {
    return;
  }
  modlua::QueueEvent(kSpellCreated, Fields("spell=%u,type=%d,targeted=%s,target=%u,level=%d,caster=%u,caster_player=%d,serial=%u,slot=%d,"
                                           "tag=%d,host_serial=%u",
                                           ObjectHandle(spell), type, Bool(targeted), Handle(base, target), level, Handle(base, caster),
                                           modlua::PlayerOf(base, caster), serial, -1, 0, serial));
}

// ranged_shot. The firearm's Fire (r3 the firearm component 163, its weapon at +4; r4 the target or 0;
// r5 aimed): the shooter is the weapon's carrier (component 105's smart pointer at +0xC). Decided before
// it: a cancel skips the shot — no ammunition is spent and no projectile made; no caller reads r3.
REX_EXTERN(rex_CECFirearm_Fire) {
  const uint32_t weapon = Plausible(ctx.r3.u32) ? Word(base, ctx.r3.u32 + 4u) : 0u;
  if (Plausible(weapon) && modlua::Wants(kRangedShot)) {
    const uint32_t carried = Component(base, weapon, 105u);
    const uint32_t shooter = carried != 0u ? Deref(base, carried + 0xCu) : 0u;
    if (Live(base, shooter)) {
      const modlua::Verdict verdict = modlua::Decide(
          ctx, base, kRangedShot,
          Fields("shooter=%u,player=%d,target=%u,aimed=%s,weapon=%u", Handle(base, shooter), modlua::PlayerOf(base, shooter),
                 Handle(base, ctx.r4.u32), Bool((ctx.r5.u32 & 0xFFu) != 0u), Handle(base, weapon)));
      if (verdict.cancelled) {
        return;
      }
    }
  }
  __imp__rex_CECFirearm_Fire(ctx, base);
}

// status_effect. An entity's AddComponent (r3 the entity, r4 the registry key, r5 replace; returns the
// component), after it, and its RemoveComponent (r3 the entity, r4 the id), when a status component is
// added or one present (and not already being removed: its +8) is taken off.
REX_EXTERN(rex_lhEntity_AddComponent) {
  const uint32_t entity = ctx.r3.u32;
  __imp__rex_lhEntity_AddComponent(ctx, base);
  const uint32_t component = ctx.r3.u32;
  if (!Plausible(component) || !Live(base, entity) || !modlua::Wants(kStatusEffect)) {
    return;
  }
  const PPCContext added = ctx;
  PPCContext work = Frame(ctx);
  const int32_t effect = CallSlot(work, base, component, 4u) ? StatusEffectOf(work.r3.u32) : 0;
  ctx = added;
  if (effect != 0) {
    RaiseStatusEffect(base, entity, effect, true);
  }
}

REX_EXTERN(rex_lhEntity_RemoveComponent) {
  const uint32_t entity = ctx.r3.u32;
  const int32_t effect = StatusEffectOf(ctx.r4.u32);
  const uint32_t component = effect != 0 && Live(base, entity) ? Component(base, entity, ctx.r4.u32) : 0u;
  const bool present = component != 0u && base[component + 8u] == 0u;
  __imp__rex_lhEntity_RemoveComponent(ctx, base);
  if (present && modlua::Wants(kStatusEffect)) {
    RaiseStatusEffect(base, entity, effect, false);
  }
}

// status_applying. The three installers, decided before each. Electrify (r3 the source descriptor,
// r4 the victim, r5 the serial, r6 level, f1 seconds); Chaos (r3 descriptor, r4 victim, r5 serial,
// f1 seconds, r7 installs, r8 a received replica — decided only for r7 && !r8); Ignite (r3 victim,
// r4 descriptor, r5 serial, r6 level, f3 × f4 the burn seconds). The caster is the descriptor's object.
REX_EXTERN(rex_SpellLightning_Electrify) {
  const uint32_t caster = Plausible(ctx.r3.u32) ? Word(base, Word(base, ctx.r3.u32 + 4u)) : 0u;
  double seconds = 0.0;
  if (!DecideStatus(ctx, base, ctx.r4.u32, 1, ctx.r5.u32, caster, static_cast<int32_t>(ctx.r6.u32), ctx.f1.f64, seconds)) {
    return;
  }
  ctx.f1.f64 = seconds;
  WithStatusCast(ctx.r5.u32, caster, [&] { __imp__rex_SpellLightning_Electrify(ctx, base); });
}

REX_EXTERN(rex_SpellChaos_ApplyToVictim) {
  const uint32_t caster = Plausible(ctx.r3.u32) ? Word(base, Word(base, ctx.r3.u32 + 4u)) : 0u;
  if ((ctx.r7.u32 & 0xFFu) != 0u && (ctx.r8.u32 & 0xFFu) == 0u) {
    double seconds = 0.0;
    if (!DecideStatus(ctx, base, ctx.r4.u32, 2, ctx.r5.u32, caster, -1, ctx.f1.f64, seconds)) {
      return;
    }
    ctx.f1.f64 = seconds;
  }
  WithStatusCast(ctx.r5.u32, caster, [&] { __imp__rex_SpellChaos_ApplyToVictim(ctx, base); });
}

REX_EXTERN(rex_SpellFireball_Ignite) {
  const uint32_t caster = Plausible(ctx.r4.u32) ? Word(base, Word(base, ctx.r4.u32 + 4u)) : 0u;
  const double burn = ctx.f3.f64 * ctx.f4.f64;
  double seconds = 0.0;
  if (!DecideStatus(ctx, base, ctx.r3.u32, 3, ctx.r5.u32, caster, static_cast<int32_t>(ctx.r6.u32), burn, seconds)) {
    return;
  }
  if (seconds != burn && ctx.f4.f64 > 0.0) {
    ctx.f3.f64 = seconds / ctx.f4.f64;
  }
  WithStatusCast(ctx.r5.u32, caster, [&] { __imp__rex_SpellFireball_Ignite(ctx, base); });
}

// slow_time. Slow time's SetWorldScale (r3 the caster, f1 the scale, f2 the blend): the world's scale
// is at world+160 (`if (*(float*)(world+160) != a2)`). Raised when the scale changes; a written scale
// (> 0: the body divides by it) replaces f1.
REX_EXTERN(rex_SpellSlowTime_SetWorldScale) {
  const uint32_t caster = ctx.r3.u32;
  const uint32_t world = Word(base, Word(base, Word(base, Word(base, kGame) + 0xCu) + 0x1Cu) + 4u);
  if (Live(base, caster) && Plausible(world) && static_cast<float>(ctx.f1.f64) != Float(base, world + 160u) &&
      modlua::Wants(kSlowTime)) {
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kSlowTime,
        Fields("caster=%u,player=%d,scale=%.9g,active=%s", Handle(base, caster), modlua::PlayerOf(base, caster), ctx.f1.f64,
               Bool(ctx.f1.f64 < 1.0)));
    double written = 0.0;
    if (verdict.Read("scale", written) && written == written && written > 0.0 && written - written == 0.0) {
      ctx.f1.f64 = static_cast<double>(static_cast<float>(written));
    }
  }
  __imp__rex_SpellSlowTime_SetWorldScale(ctx, base);
}

// ---------------------------------------------------------------------------------------------
// Held items, removed items, accepted gifts, summons, the vortex and the fireball. A vector argument's
// lanes are f32[3] x, [2] y, [1] z (world_hooks.cpp's reading of the same registers).

REX_EXTERN(__imp__rex_CECCarrying_AttachItemToSlot);
REX_EXTERN(__imp__rex_CECCarrying_ReleaseSlot);
REX_EXTERN(__imp__rex_CECInventory_RefreshRecordRefs);
REX_EXTERN(__imp__rex_CECPlayer_RemoveGiftPermanently);
REX_EXTERN(__imp__rex_SpellRaiseDead_BindSummoner);
REX_EXTERN(__imp__rex_SpellDamoclesSword_Create);
REX_EXTERN(__imp__rex_VortexComponent_Init);
REX_EXTERN(__imp__rex_VortexComponent_Shutdown);
REX_EXTERN(__imp__rex_VortexComponent_Capture);
REX_EXTERN(__imp__rex_VortexComponent_Release);
REX_EXTERN(__imp__rex_VortexComponent_Update);
REX_EXTERN(__imp__rex_VortexComponent_Classify);
REX_EXTERN(__imp__rex_SpellFireball_Impact);

namespace {

constexpr int32_t kHeldItem = 29;
constexpr int32_t kSummoned = 63;
constexpr int32_t kGiftAccepted = 77;
constexpr int32_t kVortexStarted = 95;
constexpr int32_t kVortexEnded = 96;
constexpr int32_t kFireballImpact = 97;
constexpr int32_t kItemRemoved = 106;
constexpr int32_t kVortexCaught = 113;

/// The vortex component: +0x14 the cast's serial, +0x190 the caster's smart pointer, +0x200 the level,
/// +0x170 the point its update hands its ground effects.
constexpr uint32_t kVortexSerial = 0x14u;
constexpr uint32_t kVortexCaster = 0x190u;
constexpr uint32_t kVortexLevel = 0x200u;
constexpr uint32_t kVortexPoint = 0x170u;

void RaiseHeld(uint8_t* base, uint32_t carrying, uint32_t slot, uint32_t item, bool attached) {
  const uint32_t entity = Word(base, carrying + 4u);
  if (!Live(base, entity) || !modlua::Wants(kHeldItem)) {
    return;
  }
  modlua::QueueEvent(kHeldItem, Fields("entity=%u,player=%d,slot='%s',item=%u,attached=%s", Handle(base, entity),
                                       modlua::PlayerOf(base, entity), CharString(base, slot).c_str(), Handle(base, item), Bool(attached)));
}

void RaiseCaught(uint8_t* base, uint32_t vortex, uint32_t victim, bool caught) {
  if (!Plausible(vortex) || !Live(base, victim) || !modlua::Wants(kVortexCaught)) {
    return;
  }
  const uint32_t caster = Deref(base, vortex + kVortexCaster);
  modlua::QueueEvent(kVortexCaught, Fields("serial=%u,tag=%d,entity=%u,victim=%u,player=%d,caster=%u,caster_player=%d,caught=%s",
                                           Word(base, vortex + kVortexSerial), 0, Handle(base, Word(base, vortex + 4u)), Handle(base, victim),
                                           modlua::PlayerOf(base, victim), Handle(base, caster), modlua::PlayerOf(base, caster), Bool(caught)));
}

/// The fireball impact in progress (its serial) and the hits delivered with that serial inside it.
uint32_t g_impact_serial = 0u;
int32_t g_impact_hits = 0;

}  // namespace

namespace refii::fable3::modlua {

/// A hit delivered (mod_combat_hooks.cpp's receiver seam): counted for the fireball impact in progress.
void NoteImpactHit(uint32_t serial) {
  if (g_impact_serial != 0u && serial == g_impact_serial) {
    ++g_impact_hits;
  }
}

}  // namespace refii::fable3::modlua

// held_item. The carrying component's slot attach (r3 component 28, owner at +4; r4 CCharString* the
// slot; r5 the item), after it for a live item, and its release (r3, r4 the slot; returns the released
// entity or 0), after it when something was released.
REX_EXTERN(rex_CECCarrying_AttachItemToSlot) {
  const uint32_t carrying = ctx.r3.u32;
  const uint32_t slot = ctx.r4.u32;
  const uint32_t item = ctx.r5.u32;
  __imp__rex_CECCarrying_AttachItemToSlot(ctx, base);
  if (Plausible(carrying) && Live(base, item)) {
    RaiseHeld(base, carrying, slot, item, true);
  }
}

REX_EXTERN(rex_CECCarrying_ReleaseSlot) {
  const uint32_t carrying = ctx.r3.u32;
  const uint32_t slot = ctx.r4.u32;
  __imp__rex_CECCarrying_ReleaseSlot(ctx, base);
  if (Plausible(carrying) && Plausible(ctx.r3.u32)) {
    RaiseHeld(base, carrying, slot, ctx.r3.u32, false);
  }
}

// item_removed. The inventory's record refresh after a removal (r3 the inventory 16, owner at +4; r4
// the record): what is left of it is the sum of the counts (+0xC) of the items (+4 the record) on the
// inventory's list at +0xC (the walk AddOneOfRecord makes, `lwz r10,0xC(r4)`).
REX_EXTERN(rex_CECInventory_RefreshRecordRefs) {
  const uint32_t inventory = ctx.r3.u32;
  const uint32_t record = ctx.r4.u32;
  __imp__rex_CECInventory_RefreshRecordRefs(ctx, base);
  const uint32_t entity = Plausible(inventory) ? Word(base, inventory + 4u) : 0u;
  if (record == 0u || !Live(base, entity) || !modlua::Wants(kItemRemoved)) {
    return;
  }
  int32_t remaining = 0;
  const uint32_t head = Word(base, inventory + 0xCu);
  uint32_t node = Word(base, head);
  for (int guard = 0; Plausible(node) && node != head && guard < 4096; ++guard) {
    const uint32_t item = Word(base, node + 8u);
    if (Plausible(item) && Word(base, item + 4u) == record) {
      remaining += static_cast<int32_t>(Word(base, item + 0xCu));
    }
    node = Word(base, node);
  }
  modlua::QueueEvent(kItemRemoved, Fields("entity=%u,player=%d,inventory=%u,definition='0x%08x',count=%d,remaining=%d", Handle(base, entity),
                                          modlua::PlayerOf(base, entity), ObjectHandle(inventory), record, 1, remaining));
}

// gift_accepted. Player.RemoveGiftPermanently (r3 the player component 37, the hero at +4; r4 the Lua
// state: its one argument the gift's light userdata record, `cmpwi r9,2`), after it.
REX_EXTERN(rex_CECPlayer_RemoveGiftPermanently) {
  const uint32_t hero = Plausible(ctx.r3.u32) ? Word(base, ctx.r3.u32 + 4u) : 0u;
  const uint32_t state = ctx.r4.u32;
  const uint32_t first = Plausible(state) ? Word(base, state + 0x20u) : 0u;
  const bool one = Plausible(first) && Word(base, state + 0x1Cu) - first == 8u && Word(base, first + 4u) == 2u;
  const uint32_t item = one ? Word(base, first) : 0u;
  __imp__rex_CECPlayer_RemoveGiftPermanently(ctx, base);
  if (item != 0u && Live(base, hero) && modlua::Wants(kGiftAccepted)) {
    modlua::QueueEvent(kGiftAccepted,
                       Fields("hero=%u,player=%d,item='0x%08x'", Handle(base, hero), modlua::PlayerOf(base, hero), item));
  }
}

// summoned. Raise dead's summoner bind (r3 the summoner, r4 the summon — a dead riser, component 231 —
// r6 the cast's serial) and the Damocles sword's Create (r3 the sword, its object's smart pointer at +0;
// r4 the sword set, its serial at +0x10; r5 the caster), after each.
REX_EXTERN(rex_SpellRaiseDead_BindSummoner) {
  const uint32_t summoner = ctx.r3.u32;
  const uint32_t summon = ctx.r4.u32;
  const uint32_t serial = ctx.r6.u32;
  __imp__rex_SpellRaiseDead_BindSummoner(ctx, base);
  if (Live(base, summoner) && Live(base, summon) && Component(base, summon, 231u) != 0u && modlua::Wants(kSummoned)) {
    modlua::QueueEvent(kSummoned, Fields("summoner=%u,player=%d,summon=%u,kind=%d,serial=%u,tag=%d", Handle(base, summoner),
                                         modlua::PlayerOf(base, summoner), Handle(base, summon), 1, serial, 0));
  }
}

REX_EXTERN(rex_SpellDamoclesSword_Create) {
  const uint32_t sword = ctx.r3.u32;
  const uint32_t set = ctx.r4.u32;
  const uint32_t summoner = ctx.r5.u32;
  __imp__rex_SpellDamoclesSword_Create(ctx, base);
  if (Plausible(sword) && Plausible(set)) {
    RecordSword(Word(base, set + 0x10u), Deref(base, sword));
  }
  if (Live(base, summoner) && Plausible(sword) && modlua::Wants(kSummoned)) {
    modlua::QueueEvent(kSummoned, Fields("summoner=%u,player=%d,summon=%u,kind=%d,serial=%u,tag=%d", Handle(base, summoner),
                                         modlua::PlayerOf(base, summoner), Handle(base, Deref(base, sword)), 2,
                                         Plausible(set) ? Word(base, set + 0x10u) : 0u, 0));
  }
}

// vortex_started / vortex_ended / vortex_caught. The vortex component's Init (r3 the component C, the
// vortex entity at +4; r5 the target; v1 its point), after it; its Shutdown (r3), before it; its Capture
// and Release (r3, r4 the victim), after each.
namespace {
/// What a live vortex's vortex_started decided: whether it lifts creatures. Eight at once is far past
/// the game's own (one vortex per cast, a cast lasts seconds).
struct VortexLift {
  uint32_t component = 0u;
  bool lifts = true;
};
VortexLift g_vortex_lifts[8];
/// The vortex whose update (CECVortex_vf18) is running, 0 outside one.
uint32_t g_vortex_updating = 0u;

VortexLift* LiftSlot(uint32_t component) {
  for (VortexLift& slot : g_vortex_lifts) {
    if (slot.component == component) {
      return &slot;
    }
  }
  return nullptr;
}
}  // namespace

REX_EXTERN(rex_VortexComponent_Init) {
  const uint32_t vortex = ctx.r3.u32;
  const uint32_t target = ctx.r5.u32;
  const float x = ctx.v1.f32[3];
  const float y = ctx.v1.f32[2];
  const float z = ctx.v1.f32[1];
  __imp__rex_VortexComponent_Init(ctx, base);
  if (!Plausible(vortex)) {
    return;
  }
  RecordVortex(base, vortex, true);
  VortexLift* slot = LiftSlot(vortex);
  if (slot == nullptr) {
    slot = LiftSlot(0u);
  }
  if (slot == nullptr) {
    slot = &g_vortex_lifts[0];
  }
  *slot = VortexLift{vortex, true};
  if (!modlua::Wants(kVortexStarted)) {
    return;
  }
  const uint32_t caster = Deref(base, vortex + kVortexCaster);
  const modlua::Verdict verdict = modlua::Decide(
      ctx, base, kVortexStarted,
      Fields("entity=%u,caster=%u,caster_player=%d,target=%u,serial=%u,level=%d,x=%.9g,y=%.9g,z=%.9g,tag=%d,damage=%.9g,lifts=%s",
             Handle(base, Word(base, vortex + 4u)), Handle(base, caster), modlua::PlayerOf(base, caster), Handle(base, target),
             Word(base, vortex + kVortexSerial), static_cast<int32_t>(Word(base, vortex + kVortexLevel)), Fin(x), Fin(y), Fin(z), 0,
             0.0, "true"));
  double lifts = 1.0;
  if (verdict.Read("lifts", lifts)) {
    slot->lifts = lifts != 0.0;
  }
}

REX_EXTERN(rex_VortexComponent_Shutdown) {
  const uint32_t vortex = ctx.r3.u32;
  if (Plausible(vortex) && modlua::Wants(kVortexEnded)) {
    const uint32_t caster = Deref(base, vortex + kVortexCaster);
    modlua::QueueEvent(kVortexEnded, Fields("entity=%u,caster=%u,caster_player=%d,serial=%u,x=%.9g,y=%.9g,z=%.9g",
                                            Handle(base, Word(base, vortex + 4u)), Handle(base, caster), modlua::PlayerOf(base, caster),
                                            Word(base, vortex + kVortexSerial), Fin(Float(base, vortex + kVortexPoint)),
                                            Fin(Float(base, vortex + kVortexPoint + 4u)), Fin(Float(base, vortex + kVortexPoint + 8u))));
  }
  RecordVortex(base, vortex, false);  // gone before its releases run (vortex_held answers nil from here)
  __imp__rex_VortexComponent_Shutdown(ctx, base);
  if (VortexLift* slot = LiftSlot(vortex); slot != nullptr && vortex != 0u) {
    *slot = VortexLift{};
  }
}

REX_EXTERN(rex_VortexComponent_Capture) {
  const uint32_t vortex = ctx.r3.u32;
  const uint32_t victim = ctx.r4.u32;
  __imp__rex_VortexComponent_Capture(ctx, base);
  RaiseCaught(base, vortex, victim, true);
}

REX_EXTERN(rex_VortexComponent_Release) {
  const uint32_t vortex = ctx.r3.u32;
  const uint32_t victim = ctx.r4.u32;
  __imp__rex_VortexComponent_Release(ctx, base);
  RaiseCaught(base, vortex, victim, false);
}

// fireball_impact. The fireball's Impact (r3 the spell S: +0xB8 serial, +0x28 level, +0x68 the caster's
// smart pointer, +0xBC done; r4 the struck entity or 0; v1 the point, v2 the normal). A spell already
// done returns at once (`lbz r11,0xBC(r3)` 0x82b366b4); detonated is +0xBC set inside the call. After it,
// with the hits that carried its serial inside it.
REX_EXTERN(rex_SpellFireball_Impact) {
  const uint32_t spell = ctx.r3.u32;
  const uint32_t struck = ctx.r4.u32;
  const bool fresh = Plausible(spell) && base[spell + 0xBCu] == 0u;
  const bool wants = fresh && modlua::Wants(kFireballImpact);
  const float px = ctx.v1.f32[3], py = ctx.v1.f32[2], pz = ctx.v1.f32[1];
  const float nx = ctx.v2.f32[3], ny = ctx.v2.f32[2], nz = ctx.v2.f32[1];
  const uint32_t outer_serial = g_impact_serial;
  const int32_t outer_hits = g_impact_hits;
  g_impact_serial = wants ? Word(base, spell + 0xB8u) : 0u;
  g_impact_hits = 0;
  __imp__rex_SpellFireball_Impact(ctx, base);
  const int32_t hits = g_impact_hits;
  const uint32_t serial = g_impact_serial;
  g_impact_serial = outer_serial;
  g_impact_hits = outer_hits;
  if (fresh) {
    RecordImpact(Word(base, spell + 0xB8u), px, py, pz, base[spell + 0xBCu] != 0u);
  }
  if (!wants) {
    return;
  }
  const uint32_t caster = Deref(base, spell + 0x68u);
  modlua::QueueEvent(kFireballImpact,
                     Fields("serial=%u,level=%d,caster=%u,caster_player=%d,struck=%u,struck_player=%d,x=%.9g,y=%.9g,z=%.9g,nx=%.9g,ny=%.9g,"
                            "nz=%.9g,detonated=%s,hits=%d,tag=%d",
                            serial, static_cast<int32_t>(Word(base, spell + 0x28u)), Handle(base, caster), modlua::PlayerOf(base, caster),
                            Handle(base, struck), modlua::PlayerOf(base, struck), Fin(px), Fin(py), Fin(pz), Fin(nx), Fin(ny), Fin(nz),
                            Bool(base[spell + 0xBCu] != 0u), hits, 0));
}

// ---------------------------------------------------------------------------------------------
// The charge's end and the spells' ends.

REX_EXTERN(__imp__rex_CAbilityActionMagicCastQuick_Start);
REX_EXTERN(__imp__rex_CAbilityActionMagicCastFull_Start);

namespace {

constexpr int32_t kSpellEnded = 108;
constexpr int32_t kSpellChargeEnded = 109;

/// The charge actions: the quick charge's, and the charge loop's (the generic CActionLoop, so a loop
/// ends a charge only after that hero's charge started). TU3 vtables; the disc's in guest_addresses.inc.
constexpr uint32_t kQuickChargeAction = 0x820D3D40u;
constexpr uint32_t kChargeLoopAction = 0x820D3E78u;

std::array<bool, 3> g_charging{};
/// The entity whose quick or full cast is starting: its charge's end inside is a release.
uint32_t g_casting = 0u;
/// The charge end being decided: its caster, player and whether it was released.
uint32_t g_charge_caster = 0u;
int32_t g_charge_player = 0;
bool g_charge_released = false;

/// The spell the registry update is updating now (RefiiF3SpellUpdating): a stop of the same spell is its
/// natural end (reason 0), any other stop an end from elsewhere (1).
uint32_t g_spell_updating = 0u;

uint8_t* KernelBase() {
  rex::system::KernelState* state = rex::system::kernel_state();
  rex::memory::Memory* memory = state != nullptr ? state->memory() : nullptr;
  return memory != nullptr ? memory->virtual_membase() : nullptr;
}

}  // namespace

namespace refii::fable3::modlua {

void NoteChargeStarted(const uint8_t* base, uint32_t entity) {
  const int32_t player = PlayerOf(base, entity);
  if (player == 1 || player == 2) {
    g_charging[static_cast<size_t>(player)] = true;
  }
}

/// CAction_End for a live charge action of a hero: stated for RaiseChargeEnd after the call.
bool NoteChargeEnd(const uint8_t* base, uint32_t action) {
  if (base[action + kActionStarted] == 0u || base[action + kActionEnded] != 0u) {
    return false;
  }
  const uint32_t vtable = refii::guest::ToTu3(Word(base, action));
  if (vtable != kQuickChargeAction && vtable != kChargeLoopAction) {
    return false;
  }
  const uint32_t caster = OwnerOf(base, action);
  const int32_t player = PlayerOf(base, caster);
  if ((player != 1 && player != 2) || (vtable == kChargeLoopAction && !g_charging[static_cast<size_t>(player)])) {
    return false;
  }
  g_charging[static_cast<size_t>(player)] = false;
  g_charge_caster = caster;
  g_charge_player = player;
  g_charge_released = caster != 0u && caster == g_casting;
  NoteLendChargeEnd(player, g_charge_released);
  return true;
}

void RaiseChargeEnd(uint8_t* base) {
  if (Wants(kSpellChargeEnded)) {
    QueueEvent(kSpellChargeEnded, Fields("player=%d,caster=%u,released=%s", g_charge_player, Handle(base, g_charge_caster),
                                         Bool(g_charge_released)));
  }
  g_charge_caster = 0u;
}

}  // namespace refii::fable3::modlua

// The quick and full casts' Start (slot 3: r3 the ability, r4 the entity): the charge action they end
// inside (through CAction_End) is a release.
REX_EXTERN(rex_CAbilityActionMagicCastQuick_Start) {
  const uint32_t outer = g_casting;
  g_casting = ctx.r4.u32;
  __imp__rex_CAbilityActionMagicCastQuick_Start(ctx, base);
  g_casting = outer;
}

REX_EXTERN(rex_CAbilityActionMagicCastFull_Start) {
  const uint32_t outer = g_casting;
  g_casting = ctx.r4.u32;
  __imp__rex_CAbilityActionMagicCastFull_Start(ctx, base);
  g_casting = outer;
}

// spell_ended. The spell registry's update (CSpellRegistry_Update) walks its spells with the spell in r29:
// a spell not yet done is updated (slot 1, `lwz r10,4(r11)` 0x8225d940) and one done after it, or done
// before, is stopped (slot 2, 0x8225d970). The spell: +0xB8 serial, +0x10 type, +0x28 level, +0x68 the
// caster's smart pointer.
void RefiiF3SpellUpdating(PPCRegister& spell) { g_spell_updating = spell.u32; }

void RefiiF3SpellEnding(PPCRegister& spell) {
  const uint32_t at = spell.u32;
  const bool natural = at == g_spell_updating;
  g_spell_updating = 0u;
  ForgetSpell(at);
  uint8_t* base = KernelBase();
  if (base == nullptr || !Plausible(at) || !modlua::Wants(kSpellEnded)) {
    return;
  }
  const uint32_t caster = Deref(base, at + 0x68u);
  modlua::QueueEvent(kSpellEnded, Fields("serial=%u,type=%d,caster=%u,caster_player=%d,tag=%d,reason=%d,level=%d",
                                         Word(base, at + 0xB8u), static_cast<int32_t>(Word(base, at + 0x10u)), Handle(base, caster),
                                         modlua::PlayerOf(base, caster), 0, natural ? 0 : 1,
                                         static_cast<int32_t>(Word(base, at + 0x28u))));
}

// spell_pick. The spell manager's SetSpellSlot (r3 the manager 14, the caster at +4; r4 the slot 0..4; r5
// the first spell, r6 the second — Fable 3 weaves two; r7 send): it stores the two bytes at +0x163 + 2*slot.
// Decided before it: a cancel skips the store (the slot stays as it was), a written spell replaces r5.
REX_EXTERN(__imp__rex_CECSpellManager_SetSpellSlot);

namespace {
constexpr int32_t kSpellPick = 92;
}  // namespace

REX_EXTERN(rex_CECSpellManager_SetSpellSlot) {
  const uint32_t manager = ctx.r3.u32;
  const int32_t slot = static_cast<int32_t>(ctx.r4.u32);
  const uint32_t caster = Plausible(manager) ? Word(base, manager + 4u) : 0u;
  if (slot >= 0 && slot <= 4 && Live(base, caster) && modlua::Wants(kSpellPick)) {
    const uint8_t held = base[manager + kSpellSlots + 2u * static_cast<uint32_t>(slot)];
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kSpellPick,
        Fields("pick='%s',mod='%s',slot=%d,spell=%d,previous=%d,player=%d", "spell", "", slot,
               static_cast<int32_t>(static_cast<int8_t>(ctx.r5.u32 & 0xFFu)), held == 0xFFu ? -1 : static_cast<int32_t>(held),
               modlua::PlayerOf(base, caster)));
    if (verdict.cancelled) {
      return;
    }
    double written = 0.0;
    if (verdict.Read("spell", written) && written == written && written >= -1.0 && written <= 127.0) {
      ctx.r5.s64 = static_cast<int64_t>(written);
    }
  }
  __imp__rex_CECSpellManager_SetSpellSlot(ctx, base);
  if (slot >= 0 && slot <= 4 && Plausible(manager)) {
    modlua::NoteLendPick(base, manager, slot);
  }
}

// The vortex's update, CECVortex_vf18 (r3 the component: `mr r31,r3` 0x82A70B3C): its two passes
// sub_82A72BC8 (0x82A70C04) and sub_82A73C48 (0x82A70C0C) make every Classify call there is — directly
// (0x82A72FEC, 0x82A73F90), through the capture (sub_82A75428 from 0x82A73950 and from Capture 0x82A75FB4,
// Capture called from 0x82A73704), sub_82A76D78 (0x82A73A90), sub_82A771C0 (0x82A73EB4, and from
// sub_82A79140 0x82A792BC, called from 0x82A72F2C) and sub_82A77460 (from sub_82A79360 0x82A794DC, called
// from 0x82A735AC). No Classify call is outside the update.
REX_EXTERN(rex_VortexComponent_Update) {
  const uint32_t previous = g_vortex_updating;
  g_vortex_updating = ctx.r3.u32;
  __imp__rex_VortexComponent_Update(ctx, base);
  g_vortex_updating = previous;
}

// How the vortex takes an entity: sub_82A77F50 (r3 the entity `mr r30,r3`, r4 the level, r5 the kind out
// `mr r22,r5` 0x82A77F74: 1 lift, 2 one it cannot lift; r6..r10 factors out); returns whether the vortex
// affects it at all (`mr r23,r3` 0x82A77FAC, `mr r3,r23` at each return). For a kind of 1 it looks up the
// creature component 24 itself and, when the creature's flags +0x3C lack 0x40 (`lwz r11,0x3C(r11)`
// 0x82A78158), stores 2 (`li r11,2; stw r11,0(r22)` 0x82A7816C..70): the creature it cannot lift, which the
// update shudders once per cast instead of capturing. Inside the update of a vortex whose `lifts` is false
// a creature answered 1 is answered 2 the same way; props (no component 24) are lifted as before.
REX_EXTERN(rex_VortexComponent_Classify) {
  const uint32_t entity = ctx.r3.u32;
  const uint32_t kind = ctx.r5.u32;
  __imp__rex_VortexComponent_Classify(ctx, base);
  if (g_vortex_updating == 0u || (ctx.r3.u32 & 0xFFu) == 0u || !Plausible(kind) || Word(base, kind) != 1u) {
    return;
  }
  const VortexLift* live = LiftSlot(g_vortex_updating);
  if (live == nullptr || live->lifts || Component(base, entity, 24u) == 0u) {
    return;
  }
  StoreU32(base, kind, 2u);
}
