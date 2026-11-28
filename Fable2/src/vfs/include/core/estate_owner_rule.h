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

/// The partnership a machine decides by (`own` its own reading, refii_bridge_partnership: both heroes'
/// saved words agree). The host decides every estate action an online joiner forwards, and states the
/// partnership it reads to the joiner each second (coop_estate_statements.cpp); the joiner's screens take
/// that statement once it has arrived, so both machines decide by one reading. A value outside 0..2 is
/// none.
constexpr int32_t EstatePartnershipHere(bool online_joiner, bool stated_known, int32_t stated, int32_t own) {
  const int32_t kind = online_joiner && stated_known ? stated : own;
  return kind >= 0 && kind <= 2 ? kind : 0;
}

/// Whether a screen may be bound to the owner's hero as this machine has it: what the property screen
/// reads of its bound hero is the properties (69, the owned test and the record) and the targeting (77,
/// the use target the binding and both pages read). The purse (81) is read only by the purchase helper,
/// which a hero-owned building never runs (EstateActionAllowed refuses its purchase), and by the gold
/// readout, which tests for it first.
constexpr bool EstateOwnerCopyServes(bool has_properties, bool has_targeting) {
  return has_properties && has_targeting;
}

/// How a partner's screen is bound on this machine (RefiiEstateBuyerOpens):
///   kOwner — to the owner's hero (it carries properties and targeting): the owner's rows on the owner's
///            hero, the owner's use target answered as the viewer's sign (E2);
///   kViewerOwnersProperties — the owner's hero here has its properties but no targeting (an online
///            joiner's copy of the host's hero has no component 77, as a run logged it): the
///            screen is the viewer's own hero (its targeting is the sign it used, its input, its purse),
///            with the owner's properties lent in place of the viewer's while the screen runs, and every
///            action forwarded to the host, which runs it for the owner;
///   kNone  — neither: the page only.
enum class EstatePartnerBinding : uint8_t { kOwner, kViewerOwnersProperties, kNone };

constexpr EstatePartnerBinding EstatePartnerBindingFor(bool owner_properties, bool owner_targeting,
                                                       bool viewer_properties, bool viewer_targeting) {
  if (EstateOwnerCopyServes(owner_properties, owner_targeting)) {
    return EstatePartnerBinding::kOwner;
  }
  return owner_properties && viewer_properties && viewer_targeting ? EstatePartnerBinding::kViewerOwnersProperties
                                                                   : EstatePartnerBinding::kNone;
}

}  // namespace refii::coop

#endif
