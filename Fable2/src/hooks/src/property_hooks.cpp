// refii — PROPERTY CONDITION AND REPAIR, Fable 3's rules on Fable 2's property system.
//
// Fable 2 has no condition (no Repair/Dilapidated/Condemned string or field in the binary). Fable 3
// keeps one per building, 0..1 (CECBuilding+128, "BuildingCondition"), and its tuning is:
//   - a rented-out HOME loses BuildingConditionDeteriorationPerRentCollection, 0.01, per rent drop
//     (`sub_824D54D0`, `if (*(rec+7))`: homes only; shops never decay);
//   - at 0 the tenants pay nothing (`sub_824D4DB8` / `sub_82895900`: `if (c <= 0) return 0`);
//   - the value scales by BuildingValueMultiplierAtMinCondition, 0.5: value × (0.5 + 0.5c)
//     (`sub_82894AB0`);
//   - a repair costs (full − current) × RepairBillMultiplier 0.5 = full × 0.25 × (1 − c)
//     (`sub_82896868`) and sets 1 (`sub_82B215D8`).
// Fable 2's rent drop is the same real-time five minutes (CECPlayerProperties, comp69: its update
// `sub_828CF400` pays `sub_828CF680(props, drops, out_of_game)`), so the rules apply as they are:
//   - the condition is a value of the WORLD, kept in player one's saved store (the host owns it; it
//     goes with the host's save and reaches a joiner through the player-state channel), keyed by the
//     building's UID; a building never seen yet stands at 1;
//   - decay at each payout (`sub_828CF680`), before the guest pays it, for each of the owner's
//     records (comp69's list: *(props+28) the sentinel, the record at node+8) that is a home
//     (+15), rented out (+13) and has tenants (+18);
//   - the rent a record pays (`sub_828CDD88(record)`, its income per drop, loaded or not) is 0 at 0;
//   - the buy and sale prices (`sub_8282BA48`, `sub_8282BCA0`: r3 the CECBuilding) scale by
//     condition, as Fable 3's value does;
//   - the repair (RefiiPropertyRepair, the pump's Property:repair action) charges the owner the bill
//     on the record's value (+44, `sub_8282AEB0` as last stated) through the guest's own money change
//     (`sub_8242E578(money, -cost, 1, 1, 0)`; a negative change is neither split nor redirected).
// Rent and sale money paid to player two stay player two's: the payout's and the sale's money
// changes (0x828CFC90, 0x828D0DE4) pass r5 = 0, which the co-op rule in `sub_8242E578` reads as "a
// henchman's gain goes to hero one".
//
// SHARED PROPERTY, Fable 3's (IsCoOwned / CoOwnedHeroUID; core/hero_partners.cpp): a building bought
// (`sub_828D0080(props, building)`) by a hero partnered with the other hero in the game is co-owned
// with them; its rent at each payout and its sale price (`sub_828D0B78(props, building)`) are split
// half and half, the partner's half paid to them when their hero is here and owed otherwise (paid
// when they are back, RefiiPropertyPayOwed). The world's bookkeeping is the host's: it runs where
// player one is played.

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <mutex>
#include <string>
#include <utility>
#include <vector>

#include <rex/ppc/context.h>
#include <rex/ppc/func.h>

#include "core/guest_build.h"
#include "core/guest_state.h"
#include "core/payload_bridge_fable2.h"
#include "core/property_condition.h"
#include "core/refii_mod.h"

extern "C" uint8_t* RefiiGuestBase(void);
/// Player two brought a hero (a character from a save) rather than standing as the plain henchman.
extern "C" int RefiiCoopGetOwnHero(void);
// The mod API's property events (mod_events.cpp).
extern "C" int32_t refii_mod_raise_property_trade(PPCContext* ctx, uint8_t* base, int32_t sold, uint32_t hero,
                                                  uint32_t building, int32_t* price_out);
extern "C" void refii_mod_raise_rent_paid(PPCContext* ctx, uint8_t* base, uint32_t hero, int32_t gold, int32_t drops,
                                          int32_t away);
extern "C" void refii_mod_raise_property_condition(PPCContext* ctx, uint8_t* base, uint64_t uid, int32_t player,
                                                   float condition, float previous, int32_t repaired);

/// On an online joiner, the host's value of a building getter (coop_estate_statements.cpp; 6 the buy
/// price, 7 the sale price): 1 and `out` set when the host stated it.
extern "C" int32_t refii_estate_stated_value(uint8_t* base, uint32_t building, int32_t value, int32_t* out);

extern "C" REX_FUNC(__imp__rex_CECPlayerProperties_PayIncome);
extern "C" REX_FUNC(__imp__rex_CECPlayerProperties_RecordIncome);
extern "C" REX_FUNC(__imp__rex_CECBuilding_BuyPrice);
extern "C" REX_FUNC(__imp__rex_CECBuilding_SalePrice);
extern "C" REX_FUNC(__imp__rex_CECPlayerProperties_Buy);
extern "C" REX_FUNC(__imp__rex_CECPlayerProperties_BuyQuietly);
extern "C" REX_FUNC(__imp__rex_CECPlayerProperties_Sell);

namespace {

using refii::property::kDecayPerDrop;

bool Heap(uint32_t at) { return at >= 0x10000u && at < 0xE0000000u; }

uint32_t Load32(uint8_t* base, uint32_t at) { return refii::vfs::LoadGuestU32(base, at); }

uint64_t Uid(uint8_t* base, uint32_t at) {
  return (static_cast<uint64_t>(Load32(base, at)) << 32) | Load32(base, at + 4u);
}

/// A mod's price for the purchase or sale in progress on this thread (PropertyBought / PropertySold),
/// -1 for the game's own: the buy and sale price hooks answer it.
thread_local int32_t t_price_override = -1;

struct PriceOverride {
  explicit PriceOverride(int32_t price) { t_price_override = price; }
  ~PriceOverride() { t_price_override = -1; }
  PriceOverride(const PriceOverride&) = delete;
  PriceOverride& operator=(const PriceOverride&) = delete;
};

}  // namespace

extern "C" float RefiiPropertyCondition(uint64_t uid) {
  return std::clamp(refii_mod_player_value(1, refii::property::ConditionKey(uid).c_str(), 1.0f), 0.0f, 1.0f);
}

extern "C" void RefiiPropertySetCondition(uint64_t uid, float condition) {
  refii_mod_player_save(1, refii::property::ConditionKey(uid).c_str(), std::clamp(condition, 0.0f, 1.0f));
}

namespace {

/// The building's UID for a CECBuilding (its entity at +4, the UID at entity+20/+24).
uint64_t BuildingUid(uint8_t* base, uint32_t building) {
  const uint32_t entity = Heap(building) ? Load32(base, building + 4u) : 0u;
  return Heap(entity) ? Uid(base, entity + 20u) : 0u;
}

int32_t Scaled(int32_t price, float condition) {
  return static_cast<int32_t>(std::lround(static_cast<double>(price) * refii::property::ValueScale(condition)));
}

/// The record in `props`' list (comp69: *(props+28) the sentinel, the record at node+8) for `uid`.
uint32_t RecordOf(uint8_t* base, uint32_t props, uint64_t uid) {
  const uint32_t head = Heap(props) ? Load32(base, props + 28u) : 0u;
  uint32_t node = Heap(head) ? Load32(base, head) : 0u;
  for (uint32_t n = 0; Heap(node) && node != head && n < 256u; ++n, node = Load32(base, node)) {
    const uint32_t record = Load32(base, node + 8u);
    if (Heap(record) && Uid(base, record) == uid) {
      return record;
    }
  }
  return 0u;
}

/// A gold change on `hero`'s CECMoney (comp81) through the guest's own (`sub_8242E578(money, delta,
/// 1, 1, 0)`: r5 = 1, so neither split nor redirected to hero one).
bool Gold(const PPCContext& ctx, uint8_t* base, uint32_t hero, int32_t delta) {
  const uint32_t money = hero != 0u ? refii_mod_component(base, hero, 81u) : 0u;
  if (money == 0u || delta == 0) {
    return false;
  }
  PPCContext work = refii::mods::Frame(ctx);
  work.r3.u64 = money;
  work.r4.u64 = static_cast<uint32_t>(delta);
  work.r5.u64 = 1u;
  work.r6.u64 = 1u;
  work.r7.u64 = 0u;
  return refii::mods::CallGuest(work, base, refii::guest::Address(0x8242E578u));
}

/// The hero in the game whose identity is `id`, or 0.
uint32_t HeroWithId(uint8_t* base, uint64_t id) {
  for (int32_t player = 1; player <= 2; ++player) {
    if (id != 0u && refii_bridge_hero_id(player) == id) {
      return refii_mod_player_hero(base, player);
    }
  }
  return 0u;
}

/// The co-owner of `uid` when `player`'s partnership with them stands, else 0.
uint64_t SharedWith(int32_t player, uint64_t uid) {
  const uint64_t co = refii_bridge_coowner(uid);
  return co != 0u && refii_bridge_partner_kind(player, co) > 0 ? co : 0u;
}

/// The partner's half: paid to them when they are here, owed otherwise.
void PayPartner(const PPCContext& ctx, uint8_t* base, uint64_t partner, int32_t gold) {
  const uint32_t hero = HeroWithId(base, partner);
  if (hero == 0u || !Gold(ctx, base, hero, gold)) {
    refii_bridge_owed_add(partner, gold);
  }
}

/// Whether this machine keeps the world's books (player one is played here).
bool KeepsBooks() { return refii_mod_player_here(1) != 0; }

}  // namespace

// The rent payout (`sub_828CF680`: r3 the owner's CECPlayerProperties, r4 the drops, r5 1 when it
// is the out-of-game catch-up computed on load). Each rented, tenanted home of the owner's loses
// 0.01 a drop before the payout reads its rent; one reaching 0 is told to the player. TU1 0x828CF680.
REX_EXTERN(rex_CECPlayerProperties_PayIncome) {
  const uint32_t props = ctx.r3.u32;
  const int32_t drops = static_cast<int32_t>(ctx.r4.u32);
  const bool out_of_game = ctx.r5.u8 != 0;
  const PPCContext entry = ctx;
  if (!KeepsBooks() && refii_mod_player_here(2) != 0 && Heap(props)) {
    // On an online joiner both heroes' properties mirror the host's (household_persist.cpp); the
    // host's copies pay (the money reaches the joiner as the guest's own message 47).
    ctx.r3.u64 = 0u;
    return;
  }
  if (Heap(props) && drops > 0 && drops < 100000 && KeepsBooks()) {
    const uint32_t head = Load32(base, props + 28u);
    uint32_t node = Heap(head) ? Load32(base, head) : 0u;
    for (uint32_t n = 0; Heap(node) && node != head && n < 256u; ++n, node = Load32(base, node)) {
      const uint32_t record = Load32(base, node + 8u);
      if (!Heap(record) || base[record + 15u] == 0u || base[record + 13u] == 0u || base[record + 18u] == 0u) {
        continue;
      }
      const uint64_t uid = Uid(base, record);
      const float before = RefiiPropertyCondition(uid);
      const float after = std::max(0.0f, before - kDecayPerDrop * static_cast<float>(drops));
      RefiiPropertySetCondition(uid, after);
      if (after != before) {
        PPCContext raise = entry;
        refii_mod_raise_property_condition(&raise, base, uid, refii_mod_player_of(base, Load32(base, props + 4u)), after,
                                           before, 0);
      }
      if (before > 0.0f && after <= 0.0f) {
        refii_bridge_property_notice_post(uid);
      }
    }
  }
  __imp__rex_CECPlayerProperties_PayIncome(ctx, base);
  const int32_t paid = static_cast<int32_t>(ctx.r3.u32);
  const uint32_t owner = Heap(props) ? Load32(base, props + 4u) : 0u;
  const int32_t player = owner != 0u ? refii_mod_player_of(base, owner) : 0;
  if (paid > 0 && owner != 0u && KeepsBooks()) {
    PPCContext raise = entry;
    refii_mod_raise_rent_paid(&raise, base, owner, paid, drops, out_of_game ? 1 : 0);
  }
  if (player == 0 || paid <= 0 || drops <= 0 || drops >= 100000 || !KeepsBooks()) {
    return;
  }
  // Each shared record's rent this payout (`sub_828CDD88` × drops, the payout's own sum; a record
  // pays when `sub_828CD8B8` or `sub_828CD6E8` holds or it is a dungeon, +14): half is the partner's.
  const uint32_t head = Load32(base, props + 28u);
  uint32_t node = Heap(head) ? Load32(base, head) : 0u;
  int32_t moved = 0;
  for (uint32_t n = 0; Heap(node) && node != head && n < 256u; ++n, node = Load32(base, node)) {
    const uint32_t record = Load32(base, node + 8u);
    const uint64_t partner = Heap(record) ? SharedWith(player, Uid(base, record)) : 0u;
    if (partner == 0u) {
      continue;
    }
    const auto ask = [&](uint32_t address) {
      PPCContext work = refii::mods::Frame(entry);
      work.r3.u64 = record;
      return refii::mods::CallGuest(work, base, refii::guest::Address(address)) ? work.r3.u32 : 0u;
    };
    if ((ask(0x828CD8B8u) & 0xFFu) == 0u && (ask(0x828CD6E8u) & 0xFFu) == 0u && base[record + 14u] == 0u) {
      continue;
    }
    const int32_t half = static_cast<int32_t>(ask(0x828CDD88u)) * drops / 2;
    if (half <= 0 || moved + half > paid) {
      continue;
    }
    moved += half;
    if (out_of_game) {
      // Not paid yet: the caller keeps it at props+0x80 for the away screen; the partner's half
      // waits for them.
      refii_bridge_owed_add(partner, half);
    } else if (Gold(entry, base, owner, -half)) {
      PayPartner(entry, base, partner, half);
    }
  }
  if (out_of_game && moved > 0) {
    ctx.r3.u64 = static_cast<uint32_t>(paid - moved);
  }
}

// A record's rent per drop (`sub_828CDD88`: r3 the record; the building's income when loaded,
// its cached +28 when not, a dungeon's rent). Tenants of a home in disrepair pay nothing.
// TU1 0x828CDD88.
REX_EXTERN(rex_CECPlayerProperties_RecordIncome) {
  const uint32_t record = ctx.r3.u32;
  __imp__rex_CECPlayerProperties_RecordIncome(ctx, base);
  if (Heap(record) && base[record + 15u] != 0u && RefiiPropertyCondition(Uid(base, record)) <= 0.0f) {
    ctx.r3.u64 = 0u;
  }
}

// The buy price and the sale price (`sub_8282BA48` / `sub_8282BCA0`: r3 the CECBuilding, the price
// in r3) scale with the building's condition, as Fable 3's value does. TU1 0x8282BA48 / 0x8282BCA0.
// On an online joiner they are the host's, as the host states them (its copy of the building lacks
// the host's economy, decoration and occupants).
constexpr int32_t kStatedBuy = 6;
constexpr int32_t kStatedSale = 7;

REX_EXTERN(rex_CECBuilding_BuyPrice) {
  const uint32_t building = ctx.r3.u32;
  int32_t stated = 0;
  if (refii_estate_stated_value(base, building, kStatedBuy, &stated) != 0) {
    ctx.r3.u64 = static_cast<uint32_t>(stated);
    return;
  }
  __imp__rex_CECBuilding_BuyPrice(ctx, base);
  const uint64_t uid = BuildingUid(base, building);
  if (uid != 0u) {
    ctx.r3.u64 = static_cast<uint32_t>(Scaled(static_cast<int32_t>(ctx.r3.u32), RefiiPropertyCondition(uid)));
  }
  if (t_price_override >= 0) {
    ctx.r3.u64 = static_cast<uint32_t>(t_price_override);
  }
}

REX_EXTERN(rex_CECBuilding_SalePrice) {
  const uint32_t building = ctx.r3.u32;
  int32_t stated = 0;
  if (refii_estate_stated_value(base, building, kStatedSale, &stated) != 0) {
    ctx.r3.u64 = static_cast<uint32_t>(stated);
    return;
  }
  __imp__rex_CECBuilding_SalePrice(ctx, base);
  const uint64_t uid = BuildingUid(base, building);
  if (uid != 0u) {
    ctx.r3.u64 = static_cast<uint32_t>(Scaled(static_cast<int32_t>(ctx.r3.u32), RefiiPropertyCondition(uid)));
  }
  if (t_price_override >= 0) {
    ctx.r3.u64 = static_cast<uint32_t>(t_price_override);
  }
}

// The rent payout's and the sale's money changes (0x828CFC90, 0x828D0DE4: `bl sub_8242E578` with r3
// the owner's CECMoney and r5 = 0): for hero two's r5 = 1, so the co-op rule does not hand their
// rent or sale price to hero one. The plain henchman owns nothing of their own: their money takes the
// shipped rule.
void RefiiPropertyPayee(PPCRegister& r3, PPCRegister& r5) {
  uint8_t* base = RefiiGuestBase();
  if (base == nullptr || !Heap(r3.u32) || RefiiCoopGetOwnHero() == 0) {
    return;
  }
  const uint32_t owner = Load32(base, r3.u32 + 4u);
  const uint32_t two = refii_mod_player_hero(base, 2);
  if (two != 0u && owner == two) {
    r5.u64 = 1u;
  }
}

namespace {

/// A purchase by either of the guest's two adds: the mod API's `property_trade` (kind 0) before it — a mod
/// may keep it unbought or set its price — and, once the record is on the buyer's list, the partnership's
/// share. `buy` is the guest's body.
template <typename Body>
void Purchase(PPCContext& ctx, uint8_t* base, Body buy) {
  const uint32_t props = ctx.r3.u32;
  const uint32_t building = ctx.r4.u32;
  const uint64_t uid = Heap(building) ? Uid(base, building + 20u) : 0u;
  const bool owned = uid != 0u && RecordOf(base, props, uid) != 0u;
  int32_t price = -1;
  if (!owned && uid != 0u && refii_mod_raise_property_trade(&ctx, base, 0, Load32(base, props + 4u), building, &price) != 0) {
    return;  // a mod kept it unbought
  }
  PriceOverride override_price(price);
  buy(ctx, base);
  if (uid == 0u || owned || RecordOf(base, props, uid) == 0u || !KeepsBooks()) {
    return;
  }
  const int32_t player = refii_mod_player_of(base, Load32(base, props + 4u));
  const uint64_t partner = player != 0 && refii_bridge_partnership() > 0 ? refii_bridge_hero_id(3 - player) : 0u;
  if (partner != 0u) {
    refii_bridge_coowner_set(uid, partner);
    refii_bridge_property_notice_post_kind(6, uid);
  } else if (refii_bridge_coowner(uid) != 0u) {
    refii_bridge_coowner_set(uid, 0u);
  }
}

}  // namespace

// A purchase (`sub_828D0080`: r3 the buyer's CECPlayerProperties, r4 the building's entity, its UID
// at +20; nothing when already owned, `sub_828D14E8`). Bought while partnered with the other hero in
// the game, it is shared with them. TU1 0x828D0080.
REX_EXTERN(rex_CECPlayerProperties_Buy) { Purchase(ctx, base, __imp__rex_CECPlayerProperties_Buy); }

// THE SCRIPT'S GRANT, `Lua_PlayerProperties_PurchasePropertyQuietly` (TU1 0x828D0568 / disc 0x828AFBB8, 202
// instructions, identical; the script method "PurchasePropertyQuietly" registered by `sub_828D4BF8`, data ref
// 0x828D4DE4, no code caller): r3 the buyer's CECPlayerProperties (`mr r28,r3` 0x828D0574), r4 the building
// (`mr r29,r4` 0x828D0578); the same ownership test (`bl sub_828D14E8` 0x828D057C), the record from the building
// (`sub_828CD2F0` 0x828D05A4) on the list (0x828D05F0) and its BoughtFor from `CECBuilding_BuyPrice`
// (0x828D0748), with no gold taken and no value walk. The same mod event and partner share as a purchase.
REX_EXTERN(rex_CECPlayerProperties_BuyQuietly) { Purchase(ctx, base, __imp__rex_CECPlayerProperties_BuyQuietly); }

// A sale (`sub_828D0B78`: r3 the seller's CECPlayerProperties, r4 the building's entity): the guest
// pays the seller its sale price (`sub_8282BCA0`); a shared building's half goes to the partner.
// TU1 0x828D0B78.
REX_EXTERN(rex_CECPlayerProperties_Sell) {
  const uint32_t props = ctx.r3.u32;
  const uint32_t building = ctx.r4.u32;
  const PPCContext entry = ctx;
  const uint64_t uid = Heap(building) ? Uid(base, building + 20u) : 0u;
  const uint32_t owner = Heap(props) ? Load32(base, props + 4u) : 0u;
  const int32_t player = owner != 0u ? refii_mod_player_of(base, owner) : 0;
  const uint64_t partner = player != 0 && uid != 0u && KeepsBooks() ? SharedWith(player, uid) : 0u;
  const uint32_t money = owner != 0u ? refii_mod_component(base, owner, 81u) : 0u;
  const int32_t before = money != 0u ? static_cast<int32_t>(Load32(base, money + 12u)) : 0;
  int32_t price = -1;
  if (uid != 0u && RecordOf(base, props, uid) != 0u &&
      refii_mod_raise_property_trade(&ctx, base, 1, owner, building, &price) != 0) {
    return;  // a mod kept it owned
  }
  PriceOverride override_price(price);
  __imp__rex_CECPlayerProperties_Sell(ctx, base);
  if (uid == 0u || RecordOf(base, props, uid) != 0u || !KeepsBooks()) {
    return;
  }
  if (partner != 0u && money != 0u) {
    const int32_t half = (static_cast<int32_t>(Load32(base, money + 12u)) - before) / 2;
    if (half > 0 && Gold(entry, base, owner, -half)) {
      PayPartner(entry, base, partner, half);
      refii_bridge_property_notice_post_kind(5, static_cast<uint64_t>(half));
    }
  }
  if (refii_bridge_coowner(uid) != 0u) {
    refii_bridge_coowner_set(uid, 0u);
  }
}

// What player `player`'s hero was owed while away (their half of shared rent and sales), paid now.
// Guest tick thread. Returns the gold paid.
extern "C" int32_t RefiiPropertyPayOwed(PPCContext& ctx, uint8_t* base, int32_t player) {
  const uint64_t id = refii_bridge_hero_id(player);
  const int32_t owed = id != 0u && KeepsBooks() ? refii_bridge_owed(id) : 0;
  const uint32_t hero = refii_mod_player_hero(base, player);
  if (owed <= 0 || hero == 0u || !Gold(ctx, base, hero, owed)) {
    return 0;
  }
  refii_bridge_owed_add(id, -owed);
  refii_bridge_property_notice_post_kind(4, static_cast<uint64_t>(owed));
  return owed;
}

// The repair (the pump's Property:repair:<uid> for `hero`): the record of hero's CECPlayerProperties
// (comp69) with that building; the bill from its value (+44); charged through the guest's money
// change on hero's CECMoney (comp81, gold at +12) when the hero can pay; the condition then 1.
// Returns 1 repaired, 0 not owned / nothing to repair, -1 not enough gold.
extern "C" int32_t RefiiPropertyRepair(PPCContext& ctx, uint8_t* base, uint32_t hero, uint64_t uid) {
  const uint32_t props = hero != 0u ? refii_mod_component(base, hero, 69u) : 0u;
  const uint32_t money = hero != 0u ? refii_mod_component(base, hero, 81u) : 0u;
  if (props == 0u || money == 0u) {
    return 0;
  }
  const uint32_t head = Load32(base, props + 28u);
  uint32_t node = Heap(head) ? Load32(base, head) : 0u;
  for (uint32_t n = 0; Heap(node) && node != head && n < 256u; ++n, node = Load32(base, node)) {
    const uint32_t record = Load32(base, node + 8u);
    if (!Heap(record) || Uid(base, record) != uid) {
      continue;
    }
    const float condition = RefiiPropertyCondition(uid);
    const int32_t cost = refii::property::RepairCost(static_cast<int32_t>(Load32(base, record + 44u)), condition);
    if (condition >= 1.0f || cost <= 0) {
      RefiiPropertySetCondition(uid, 1.0f);
      return 0;
    }
    if (static_cast<int32_t>(Load32(base, money + 12u)) < cost) {
      return -1;
    }
    PPCContext work = refii::mods::Frame(ctx);
    work.r3.u64 = money;
    work.r4.u64 = static_cast<uint32_t>(-cost);
    work.r5.u64 = 1u;
    work.r6.u64 = 1u;
    work.r7.u64 = 0u;
    refii::mods::CallGuest(work, base, refii::guest::Address(0x8242E578u));
    RefiiPropertySetCondition(uid, 1.0f);
    refii_mod_raise_property_condition(&ctx, base, uid, refii_mod_player_of(base, hero), 1.0f, condition, 1);
    return 1;
  }
  return 0;
}

// REPAIR ALL (the pump's Property:repairall:<player>): every record of `hero`'s below full condition,
// cheapest first, each through RefiiPropertyRepair while the gold lasts. `repaired`, `spent` and
// `left` (still in disrepair: not enough gold) are the outcome.
extern "C" void RefiiPropertyRepairAll(PPCContext& ctx, uint8_t* base, uint32_t hero, int32_t* repaired, int32_t* spent,
                                       int32_t* left) {
  *repaired = 0;
  *spent = 0;
  *left = 0;
  const uint32_t props = hero != 0u ? refii_mod_component(base, hero, 69u) : 0u;
  if (props == 0u) {
    return;
  }
  std::vector<std::pair<int32_t, uint64_t>> worn;
  const uint32_t head = Load32(base, props + 28u);
  uint32_t node = Heap(head) ? Load32(base, head) : 0u;
  for (uint32_t n = 0; Heap(node) && node != head && n < 256u; ++n, node = Load32(base, node)) {
    const uint32_t record = Load32(base, node + 8u);
    if (!Heap(record)) {
      continue;
    }
    const uint64_t uid = Uid(base, record);
    const float condition = RefiiPropertyCondition(uid);
    const int32_t cost = refii::property::RepairCost(static_cast<int32_t>(Load32(base, record + 44u)), condition);
    if (condition < 1.0f && cost > 0) {
      worn.emplace_back(cost, uid);
    }
  }
  std::sort(worn.begin(), worn.end());
  for (const auto& [cost, uid] : worn) {
    if (RefiiPropertyRepair(ctx, base, hero, uid) > 0) {
      ++*repaired;
      *spent += cost;
    } else {
      ++*left;
    }
  }
}
