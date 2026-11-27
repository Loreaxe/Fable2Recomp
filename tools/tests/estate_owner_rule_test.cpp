// Who may act on a property in co-op (Fable2/src/vfs/include/core/estate_owner_rule.h): the owner, or
// the other hero while the two are married or business partners; a building no hero owns is open.
// Build: c++ -std=c++20 -I Fable2/src/vfs/include tools/tests/estate_owner_rule_test.cpp && ./a.out

#include <cassert>
#include <cstdio>

#include "core/estate_owner_rule.h"

using refii::coop::EstateAccess;
using refii::coop::EstateAccessFor;
using refii::coop::EstateActionAllowed;
using refii::coop::EstateActsFor;

int main() {
  // No hero owns it: open to either player, who may buy it and nothing else.
  for (int viewer = 1; viewer <= 2; ++viewer) {
    assert(EstateAccessFor(viewer, 0, 0) == EstateAccess::kOpen);
    assert(EstateActionAllowed(true, viewer, 0, 0));
    assert(!EstateActionAllowed(false, viewer, 0, 0));
    assert(EstateActsFor(viewer, 0, 2) == viewer);
  }
  // Their own: every action but buying it again.
  for (int viewer = 1; viewer <= 2; ++viewer) {
    for (int partnership = 0; partnership <= 2; ++partnership) {
      assert(EstateAccessFor(viewer, viewer, partnership) == EstateAccess::kOwner);
      assert(!EstateActionAllowed(true, viewer, viewer, partnership));
      assert(EstateActionAllowed(false, viewer, viewer, partnership));
      assert(EstateActsFor(viewer, viewer, partnership) == viewer);
    }
  }
  // The other hero's, both directions: refused without a partnership.
  assert(EstateAccessFor(1, 2, 0) == EstateAccess::kRefused);
  assert(EstateAccessFor(2, 1, 0) == EstateAccess::kRefused);
  assert(!EstateActionAllowed(false, 1, 2, 0));
  assert(!EstateActionAllowed(false, 2, 1, 0));
  assert(!EstateActionAllowed(true, 2, 1, 0));
  assert(EstateActsFor(2, 1, 0) == 2);
  // Married (2) or business partners (1): everything, acting as the owner; never a purchase of it.
  for (int partnership = 1; partnership <= 2; ++partnership) {
    assert(EstateAccessFor(1, 2, partnership) == EstateAccess::kPartner);
    assert(EstateAccessFor(2, 1, partnership) == EstateAccess::kPartner);
    assert(EstateActionAllowed(false, 1, 2, partnership));
    assert(EstateActionAllowed(false, 2, 1, partnership));
    assert(!EstateActionAllowed(true, 2, 1, partnership));
    assert(EstateActsFor(2, 1, partnership) == 1);
    assert(EstateActsFor(1, 2, partnership) == 2);
  }
  std::puts("estate owner rule: ok");
  return 0;
}
