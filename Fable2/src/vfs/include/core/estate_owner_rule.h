// refii — WHO MAY ACT ON A PROPERTY IN CO-OP (hooks/coop_estate_owner.cpp, mod_events.cpp decide with this).
//
// The user's rule (2026-11-27): both players see the real owner; a property's owner is the only one who
// may change its rent, redecorate, move in, sell, evict, rent it out or upgrade its furniture — each
// hero is their own — unless the two heroes are married or business partners (Fable 3's partnership,
// core/hero_partners.cpp), when each may do everything on the other's. Both directions, local and
// online. A building no hero owns is the guest's as shipped: whoever stands at its sign may buy it.
// The plain henchman owns nothing (EstateOwnerPlayer never answers them).

#ifndef REFII_CORE_ESTATE_OWNER_RULE_H
#define REFII_CORE_ESTATE_OWNER_RULE_H

#include <cstdint>

namespace refii::coop {

/// What the viewer of a property may do there.
enum class EstateAccess : uint8_t {
  kOpen,     // no hero owns it: the guest's own rows (buy, buy to rent out, buy as the marital home)
  kOwner,    // the viewer's own: the guest's own rows
  kPartner,  // the other hero's, and the two are partners: the screen acts for the owner
  kRefused,  // the other hero's, not partners: the page shows the owner, the only row is Back
};

/// `viewer` and `owner` are players (1, 2; owner 0 when no hero owns it); `partnership` 0 none, 1
/// business partners, 2 married (refii_bridge_partnership).
constexpr EstateAccess EstateAccessFor(int32_t viewer, int32_t owner, int32_t partnership) {
  if (owner == 0) {
    return EstateAccess::kOpen;
  }
  if (owner == viewer) {
    return EstateAccess::kOwner;
  }
  return partnership > 0 ? EstateAccess::kPartner : EstateAccess::kRefused;
}

/// The player whose hero the screen acts for: the owner for a partner, else the viewer.
constexpr int32_t EstateActsFor(int32_t viewer, int32_t owner, int32_t partnership) {
  return EstateAccessFor(viewer, owner, partnership) == EstateAccess::kPartner ? owner : viewer;
}

/// Whether one estate action by `viewer` may run: a purchase only of a building no hero owns, every
/// other action only on a hero's property the viewer owns or is a partner of its owner.
constexpr bool EstateActionAllowed(bool purchase, int32_t viewer, int32_t owner, int32_t partnership) {
  if (purchase) {
    return owner == 0;
  }
  const EstateAccess access = EstateAccessFor(viewer, owner, partnership);
  return access == EstateAccess::kOwner || access == EstateAccess::kPartner;
}

}  // namespace refii::coop

#endif
