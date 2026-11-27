// The text of an online joiner's carried use (Fable2/src/vfs/include/core/joiner_use_text.h): `carry|`
// names an item's record, a script class and the hero's target; anything that breaks a rule is dropped.
// Build: c++ -std=c++20 -I Fable2/src/vfs/include tools/tests/joiner_use_text_test.cpp && ./a.out

#include <cassert>
#include <cstdio>
#include <string>

#include "core/joiner_use_text.h"

using refii::coop::UseCarryParse;
using refii::coop::UseClassName;

int main() {
  uint32_t hash = 0;
  uint64_t target = 0;
  std::string name;
  // Record and class; no target.
  assert(UseCarryParse("1A2B3C4D|OnCarriedActionUseBonusEffects", &hash, &name, &target) && hash == 0x1A2B3C4Du &&
         name == "OnCarriedActionUseBonusEffects" && target == 0u);
  // With the target the hero had.
  assert(UseCarryParse("ff|OnCarriedActionUseRing|00C1120243000004", &hash, &name, &target) && hash == 0xFFu &&
         target == 0x00C1120243000004ull);
  // Dropped: a zero or too long record, a bad digit, a bad class, a bad or too long target, extra fields.
  assert(!UseCarryParse("0|OnCarriedActionUseBook", &hash, &name, &target));
  assert(!UseCarryParse("123456789|OnCarriedActionUseBook", &hash, &name, &target));
  assert(!UseCarryParse("12G4|OnCarriedActionUseBook", &hash, &name, &target));
  assert(!UseCarryParse("1234|On.Bad", &hash, &name, &target));
  assert(!UseCarryParse("1234|OnCarriedActionUseBook|xyz", &hash, &name, &target));
  assert(!UseCarryParse("1234|OnCarriedActionUseBook|11112222333344445", &hash, &name, &target));
  assert(!UseCarryParse("1234|OnCarriedActionUseBook|1|2", &hash, &name, &target));
  assert(!UseCarryParse("1234", &hash, &name, &target));
  assert(UseClassName("OnCarriedActionUseBonusEffects") && !UseClassName("9Lives") && !UseClassName("a.b") &&
         !UseClassName(""));
  std::puts("ok");
  return 0;
}
