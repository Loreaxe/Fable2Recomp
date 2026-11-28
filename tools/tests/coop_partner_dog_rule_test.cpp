// Player two's dog (Fable2/src/vfs/include/core/coop_partner_dog_rule.h): only a player two who brought
// a hero has one, the plain henchman never does, and a hero's dog goes when they become the henchman.
// Build: c++ -std=c++20 -I Fable2/src/vfs/include tools/tests/coop_partner_dog_rule_test.cpp && ./a.out

#include <cassert>
#include <cstdio>

#include "core/coop_partner_dog_rule.h"

using refii::coop::NextPartnerDogStep;
using refii::coop::PartnerDogFacts;
using refii::coop::PartnerDogStep;

namespace {

constexpr unsigned kGone = 90u;
constexpr unsigned kWait = 300u;

PartnerDogFacts Hero() {
  PartnerDogFacts f;
  f.partner_here = true;
  f.partner_is_hero = true;
  f.hero_one_has_dog = true;
  f.kit_stated = true;
  f.attempt_due = true;
  f.partner_bound = true;
  f.hero_ticks = 30u;
  return f;
}

}  // namespace

int main() {
  // A hero who brought their save, hero one with a dog, the kit in: the dog is made.
  assert(NextPartnerDogStep(Hero(), kGone, kWait) == PartnerDogStep::kSpawn);

  // THE PLAIN HENCHMAN: never a dog, whatever else holds — even with hero one's dog, a stated kit and
  // the kit wait long past (the defect: the plain dog came after the wait).
  {
    PartnerDogFacts f = Hero();
    f.partner_is_hero = false;
    f.kit_stated = false;
    f.hero_ticks = 100000u;
    assert(NextPartnerDogStep(f, kGone, kWait) == PartnerDogStep::kNone);
    f.kit_stated = true;
    assert(NextPartnerDogStep(f, kGone, kWait) == PartnerDogStep::kNone);
  }

  // A hero who becomes the henchman (X on the co-op screen, the session reset): their dog goes at once.
  {
    PartnerDogFacts f = Hero();
    f.partner_is_hero = false;
    f.has_dog = true;
    assert(NextPartnerDogStep(f, kGone, kWait) == PartnerDogStep::kDestroy);
  }

  // A hero's standing dog is dressed, not made again.
  {
    PartnerDogFacts f = Hero();
    f.has_dog = true;
    assert(NextPartnerDogStep(f, kGone, kWait) == PartnerDogStep::kDress);
  }

  // No dog for hero one: none for player two either.
  {
    PartnerDogFacts f = Hero();
    f.hero_one_has_dog = false;
    assert(NextPartnerDogStep(f, kGone, kWait) == PartnerDogStep::kNone);
  }

  // The retry wait after an attempt holds.
  {
    PartnerDogFacts f = Hero();
    f.attempt_due = false;
    assert(NextPartnerDogStep(f, kGone, kWait) == PartnerDogStep::kNone);
  }

  // A new hero whose Player does not hold them yet (the join's deferred SetupCamera): no dog until it
  // does, whatever else holds — making it would teach its tricks to a master whose Player has no body.
  {
    PartnerDogFacts f = Hero();
    f.partner_bound = false;
    assert(NextPartnerDogStep(f, kGone, kWait) == PartnerDogStep::kNone);
    f.kit_stated = false;
    f.hero_ticks = 100000u;
    assert(NextPartnerDogStep(f, kGone, kWait) == PartnerDogStep::kNone);
    f.partner_bound = true;
    assert(NextPartnerDogStep(f, kGone, kWait) == PartnerDogStep::kSpawn);
  }

  // An unbound Player never holds back the dress or the destroy of a dog that stands.
  {
    PartnerDogFacts f = Hero();
    f.partner_bound = false;
    f.has_dog = true;
    assert(NextPartnerDogStep(f, kGone, kWait) == PartnerDogStep::kDress);
    f.partner_is_hero = false;
    assert(NextPartnerDogStep(f, kGone, kWait) == PartnerDogStep::kDestroy);
  }

  // A new hero whose kit has not come waits for it, then takes the plain breed.
  {
    PartnerDogFacts f = Hero();
    f.kit_stated = false;
    f.hero_ticks = kWait - 30u;
    assert(NextPartnerDogStep(f, kGone, kWait) == PartnerDogStep::kNone);
    f.hero_ticks = kWait;
    assert(NextPartnerDogStep(f, kGone, kWait) == PartnerDogStep::kSpawn);
  }

  // Player two gone: their dog outlives a rebuilt body, then goes. Gone with no dog: nothing.
  {
    PartnerDogFacts f;
    f.has_dog = true;
    f.gone_ticks = kGone - 30u;
    assert(NextPartnerDogStep(f, kGone, kWait) == PartnerDogStep::kNone);
    f.gone_ticks = kGone;
    assert(NextPartnerDogStep(f, kGone, kWait) == PartnerDogStep::kDestroy);
    f.has_dog = false;
    assert(NextPartnerDogStep(f, kGone, kWait) == PartnerDogStep::kNone);
  }

  std::puts("coop_partner_dog_rule_test: ok");
  return 0;
}
