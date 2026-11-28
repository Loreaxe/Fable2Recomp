// refii — WHEN PLAYER TWO HAS A DOG OF THEIR OWN (hooks/src/coop_dog_hooks.cpp decides with this).
//
// Shipped, the game keeps one dog, hero one's: both master picks answer hero one (`lhEntity_GetModel`
// before the dog init's `mr r31, r3`, TU1 0x8280AF08 / disc 0x82780ED0) and SetMaster files it in the
// one global slot. The henchman never had a dog. A player two who BROUGHT A HERO (a character from a
// save: RefiiCoopGetOwnHero, the one statement of it) is a hero of their own and gets one while hero
// one has one; the plain henchman never does, and a dog made while player two was a hero goes when
// they become the henchman again (the co-op screen's X, or the session reset).
//
// THE DOG IS MADE ONLY ONCE PLAYER TWO'S PLAYER HOLDS THEIR BODY. Making it teaches it player two's
// tricks (Dog.UnlockTrickIfNecessary, TU1 0x828114D0 / disc 0x82787498), and the trick that completes
// ExpressionsTaughtForDogLover calls Stats.SetTitleAsFreeToBuy on the master's stats (TU1 0x8281195C /
// disc 0x82787924). For a grown hero (stats+0x58 >= 2.0) that body reads the id-2 Player's body with no
// test (TU1 0x824D8770 `bl sub_82264230` .. 0x824D877C `bl sub_8229EE78` .. 0x824D8784
// `lwz r9,0x14(r3)`; disc 0x8249F8E8 / 0x8249F8F4 / 0x8249F8FC). At a local join that Player holds no
// body from the new hero's creation (lhNetManager_SetupHenchman) until the deferred
// lhSceneNode_SetupCamera (TU1 0x8233D8E0 / disc 0x823202B8, whose first act is the Player+0xC weak
// reference, 0x82389D9C / 0x8238F56C), and the read faulted at guest 0x14 (2026-11-28, refii_315/316).
// `partner_bound` is that Player holding player two's hero, or no id-2 Player at all (the online host
// keeps none for the joiner, and the guest's own lookup then skips the read, 0x824D8778).

#ifndef REFII_CORE_COOP_PARTNER_DOG_RULE_H
#define REFII_CORE_COOP_PARTNER_DOG_RULE_H

#include <cstdint>

namespace refii::coop {

/// What the host does about player two's dog at one look.
enum class PartnerDogStep : uint8_t {
  kNone,     // nothing this look
  kDestroy,  // destroy the dog player two has
  kDress,    // the dog stands: dress it from player two's HeroState
  kSpawn,    // create it
};

/// What one look sees.
struct PartnerDogFacts {
  bool partner_here = false;       // player two's hero is in the world, alive
  bool partner_is_hero = false;    // player two brought a hero (RefiiCoopGetOwnHero)
  bool has_dog = false;            // player two's dog exists
  bool hero_one_has_dog = false;   // hero one's dog exists
  bool kit_stated = false;         // player two's HeroState has arrived (any version)
  bool attempt_due = false;        // the retry wait after an attempt has passed
  bool partner_bound = false;      // the id-2 Player holds player two's hero (or there is none)
  uint32_t gone_ticks = 0u;        // ticks player two has been gone, this look included
  uint32_t hero_ticks = 0u;        // ticks player two has stood as a hero, this look included
};

/// The step for one look. `gone_limit`: how long player two must be gone before their dog is (a
/// body the guest rebuilds is briefly out of the world). `kit_wait`: how long a new hero two waits
/// for their HeroState to state a breed before the plain dog.
constexpr PartnerDogStep NextPartnerDogStep(const PartnerDogFacts& f, uint32_t gone_limit, uint32_t kit_wait) {
  if (!f.partner_here) {
    return f.has_dog && f.gone_ticks >= gone_limit ? PartnerDogStep::kDestroy : PartnerDogStep::kNone;
  }
  if (!f.partner_is_hero) {
    return f.has_dog ? PartnerDogStep::kDestroy : PartnerDogStep::kNone;
  }
  if (f.has_dog) {
    return PartnerDogStep::kDress;
  }
  if (!f.hero_one_has_dog || !f.attempt_due || !f.partner_bound) {
    return PartnerDogStep::kNone;
  }
  if (!f.kit_stated && f.hero_ticks < kit_wait) {
    return PartnerDogStep::kNone;
  }
  return PartnerDogStep::kSpawn;
}

}  // namespace refii::coop

#endif  // REFII_CORE_COOP_PARTNER_DOG_RULE_H
