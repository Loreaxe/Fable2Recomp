// refii — THE HOST STATES ITS BUILDINGS TO AN ONLINE JOINER (Fable 2 co-op).
//
// A building's book is kept where the world runs. The guest's netcode sends none of it to a client:
// the joiner's copy of a building (CECBuilding, comp67) keeps what the level gave it, so its property
// screen read decoration 2490 and town economy 0 where the host had 9960 and 6000, no tenant, a
// price of 13245 where the host charged 17582, and after a re-join the rent level it had before.
//
// The screen reads the building through the guest's own getters, each on r3 the CECBuilding — page 1
// (`sub_82998FB0`, disc `sub_828ED8C0`), its tick (`sub_8299A728`) and the rows (`sub_82998300`):
//   base value  `sub_8282AEB0` / disc `sub_8274D1E0`: BasicSalePrice × (1 + comp67+112 × the town's
//               economy, the comp66 link's comp65 +16 × 0.01) + comp67+44 × comp67+116;
//   economy     `sub_8282C1C0` / `sub_8274E4F0`: BasicSalePrice × that town term, less BasicSalePrice;
//   rent        `sub_8282C110` / `sub_8274E440`: the value × the GDB factor × (1 + level × 0.01), the
//               level read inline (`lwz r6,0x5C(r31)` 0x8282C154 / 0x8274E484);
//   buy, sale   `sub_8282BA48` / `sub_8274DD78`, `sub_8282BCA0` / `sub_8274DFD0` (property_hooks.cpp
//               answers them too);
//   adjustments `sub_8282C470` / `sub_8274E7A0` (NotForSaleAdjustment, while comp67+128 counts
//               occupants), `sub_8282C610` / `sub_8274E940` (the page's HERO_OPINION line) and
//               `sub_8282C870` / `sub_8274EBA0` (PlayerKilledOccupantAdjustment, comp67+81);
//   occupant    `sub_8282D5D8` / `sub_8274F908`: an adult of the building's occupant list (+124, its
//               count +128), the owner the page names when no hero owns it (coop_estate_owner.cpp) and
//               the tenant of a rented one (page 1 0x82999738 / disc 0x828EE048);
//   decoration  read inline: `lwz r10,0x2C(r25)` and `lfs f0,0x74(r25)` (0x829997D4 / 0x829997D8,
//               disc 0x828EE0E4 / 0x828EE0E8), multiplied at 0x829997FC / 0x828EE10C.
// So the host asks each getter for the building as the joiner's screen there would — under the household
// of the hero that screen acts for (core/estate_owner_rule.h EstateActsFor): player one's for a building of
// player one's while the two are partners (the joiner's screen is then the owner's, and the host replays
// its actions as player one's, mod_events.cpp ReplayEstateIntent), else player two's, the buyer and owner
// the host charges — and states the answers. The getters read the hero through the players record's hero
// slot the household scope lends (the opinion adjustment `sub_8282C610`:
// `lhEntity_GetModel(**(*(*(G+12)+88)+4))` 0x8282C6FC, disc `sub_8274E940`); on the joiner the same getter answers the host's value for that building, and the three
// inline fields (+44, +116, +92) are written as the host has them. The joiner's own screen, rows and
// tick then state what the host's would.
//
// THE STATEMENT is a world blob of player one's store, `refii.estate.buildings`, which reaches the
// joiner as the player store's blobs do: per building its UID, its occupant's UID (0 none), the eight
// getter values, the decoration, its scale and the rent level. The host restates it once a second
// when any of it changed, and at once after a replayed estate action, for every building on either
// hero's property list and every one the joiner looked at this session (`look:<sign uid>:0`, sent
// before the joiner's sale-sign script runs, CoopHeroLua.cs). Off line, and on the host, every getter
// is the guest's own.
//
// THE CUTSCENE RULE. The sale sign's script opens nothing while
// `Player.IsInteractiveCutsceneRuleSet(GetPlayerHero(), CUTSCENE_RULE_NO_PROPERTY_SCREEN = 65536)`
// (OnActionUseSaleSign:Update) — `Lua_Player_IsInteractiveCutsceneRuleSet` (TU1 0x822CD090 / disc
// 0x822CD810, the CECPlayer comp 33: `lwz r11,0x7C(r3)` / `and r10,r11,r4`) — and a foreground cutscene's
// rules are set where the world runs: on the host, on its copy of hero two (Set / Add / RemoveAll mirror a
// hero-one rule onto player two's hero, 0x823E2270 / 0x823E2898 / 0x823E2E58). The joiner's own copy never
// hears of them (its client skips every Lua update). So the host states its answer for hero two — a
// session value of player one's store, kRulesKey, 1 while the rule is set — and the joiner's sign script
// asks that instead of its own copy (CoopHeroLua.cs, Refii._estate_blocked).

#include <algorithm>
#include <array>
#include <cstdint>
#include <cstring>
#include <string>
#include <unordered_map>
#include <vector>

#include <rex/logging.h>

#include "core/estate_owner_rule.h"
#include "core/guest_build.h"
#include "core/guest_state.h"
#include "core/payload_bridge_fable2.h"
#include "core/refii_mod.h"
#include "hooks/ppc_externs.h"
#include "vfs_hooks_private.h"

extern "C" uint32_t refii_household_resolve_uid(PPCContext* ctx, uint8_t* base, uint64_t uid);
/// `body` run as `hero`'s household (mod_events.cpp HouseholdScope).
extern "C" void refii_household_scoped(PPCContext* ctx, uint8_t* base, uint32_t hero,
                                       void (*body)(PPCContext* ctx, uint8_t* base, void* arg), void* arg);

namespace {

using refii::vfs::LoadGuestU32;

constexpr char kBlobKey[] = "refii.estate.buildings";
constexpr char kRulesKey[] = "refii.estate.no_property_two";
/// CUTSCENE_RULE_NO_PROPERTY_SCREEN (interactivecutsceneruleenum.lua).
constexpr uint32_t kNoPropertyScreen = 65536u;
constexpr uint32_t kValues = static_cast<uint32_t>(refii::vfs::EstateValue::kCount);
constexpr size_t kRecordBytes = 8u + 8u + 4u * kValues + 4u * 3u;
constexpr size_t kMaxRecords = 128u;
constexpr double kRestateSeconds = 1.0;
// property_hooks.cpp asks the buy and sale prices by these numbers.
static_assert(static_cast<uint32_t>(refii::vfs::EstateValue::kBuy) == 6u &&
              static_cast<uint32_t>(refii::vfs::EstateValue::kSale) == 7u);

/// Each getter's TU1 address, in EstateValue's order.
constexpr std::array<uint32_t, kValues> kGetters = {0x8282AEB0u, 0x8282C1C0u, 0x8282C110u, 0x8282C470u,
                                                    0x8282C610u, 0x8282C870u, 0x8282BA48u, 0x8282BCA0u};

bool Heap(uint32_t at) { return at >= 0x10000u && at < 0xE0000000u; }

uint64_t Uid(uint8_t* base, uint32_t entity) {
  return Heap(entity) ? (static_cast<uint64_t>(LoadGuestU32(base, entity + 20u)) << 32) | LoadGuestU32(base, entity + 24u)
                      : 0u;
}

struct Record {
  uint64_t building = 0u;
  uint64_t occupant = 0u;
  std::array<int32_t, kValues> values{};
  uint32_t decoration = 0u;
  uint32_t scale = 0u;  // the float's bits
  uint32_t level = 0u;

  bool operator==(const Record&) const = default;
};

void Put32(std::vector<uint8_t>& out, uint32_t v) {
  for (int shift = 0; shift < 32; shift += 8) {
    out.push_back(static_cast<uint8_t>(v >> shift));
  }
}

void Put64(std::vector<uint8_t>& out, uint64_t v) {
  Put32(out, static_cast<uint32_t>(v));
  Put32(out, static_cast<uint32_t>(v >> 32));
}

uint32_t Get32(const uint8_t* at) {
  return static_cast<uint32_t>(at[0]) | (static_cast<uint32_t>(at[1]) << 8) | (static_cast<uint32_t>(at[2]) << 16) |
         (static_cast<uint32_t>(at[3]) << 24);
}

uint64_t Get64(const uint8_t* at) { return static_cast<uint64_t>(Get32(at)) | (static_cast<uint64_t>(Get32(at + 4)) << 32); }

bool OnlineJoiner() { return refii_mod_player_here(1) == 0 && refii_mod_player_here(2) != 0; }

// ---- THE HOST ----------------------------------------------------------------------------------

struct Host {
  std::vector<uint64_t> looked;
  std::vector<uint8_t> stated;
  double restate_in = 0.0;
};

Host g_host;

/// The building UIDs on `hero`'s property list (comp69: *(props+28) the sentinel, the record at
/// node+8, its UID the record's first eight bytes).
void ListedBy(uint8_t* base, uint32_t hero, std::vector<uint64_t>& out) {
  const uint32_t props = hero != 0u ? refii_mod_component(base, hero, 69u) : 0u;
  const uint32_t head = props != 0u ? LoadGuestU32(base, props + 28u) : 0u;
  uint32_t node = Heap(head) ? LoadGuestU32(base, head) : 0u;
  for (uint32_t n = 0; Heap(node) && node != head && n < 256u; ++n, node = LoadGuestU32(base, node)) {
    const uint32_t record = LoadGuestU32(base, node + 8u);
    if (Heap(record)) {
      out.push_back((static_cast<uint64_t>(LoadGuestU32(base, record)) << 32) | LoadGuestU32(base, record + 4u));
    }
  }
}

struct Asked {
  std::vector<uint64_t> uids;
  std::vector<uint8_t> bytes;
};

/// Every listed building's getters asked, as the screen there asks them (run under hero two's household).
void AskAll(PPCContext* ctx, uint8_t* base, void* arg) {
  Asked& asked = *static_cast<Asked*>(arg);
  for (const uint64_t uid : asked.uids) {
    const uint32_t entity = refii_household_resolve_uid(ctx, base, uid);
    const uint32_t building = entity != 0u ? refii_mod_component(base, entity, 67u) : 0u;
    if (building == 0u) {
      continue;
    }
    const auto call = [&](uint32_t tu1) {
      PPCContext work = refii::mods::Frame(*ctx);
      work.r3.u64 = building;
      return refii::mods::CallGuest(work, base, refii::guest::Address(tu1)) ? work.r3.u32 : 0u;
    };
    Record record;
    record.building = uid;
    record.occupant = Uid(base, call(0x8282D5D8u));
    for (uint32_t i = 0; i < kValues; ++i) {
      record.values[i] = static_cast<int32_t>(call(kGetters[i]));
    }
    record.decoration = LoadGuestU32(base, building + 0x2Cu);
    record.scale = LoadGuestU32(base, building + 0x74u);
    record.level = LoadGuestU32(base, building + 0x5Cu);
    Put64(asked.bytes, record.building);
    Put64(asked.bytes, record.occupant);
    for (const int32_t value : record.values) {
      Put32(asked.bytes, static_cast<uint32_t>(value));
    }
    Put32(asked.bytes, record.decoration);
    Put32(asked.bytes, record.scale);
    Put32(asked.bytes, record.level);
  }
}

void DriveHost(PPCContext& ctx, uint8_t* base, float seconds) {
  g_host.restate_in -= seconds;
  if (g_host.restate_in > 0.0) {
    return;
  }
  g_host.restate_in = kRestateSeconds;
  const uint32_t two = refii_mod_player_hero(base, 2);
  // Hero two's NO_PROPERTY_SCREEN as this machine's copy holds it (head note), for the joiner's sign.
  {
    const uint32_t player = two != 0u ? refii_mod_component(base, two, 33u) : 0u;
    PPCContext work = refii::mods::Frame(ctx);
    work.r3.u64 = player;
    work.r4.u64 = kNoPropertyScreen;
    const bool blocked = player != 0u && refii::mods::CallGuest(work, base, refii::guest::Address(0x822CD090u)) &&
                         (work.r3.u32 & 0xFFu) != 0u;
    refii_bridge_player_write(1, kRulesKey, sizeof(kRulesKey) - 1u, blocked ? 1.0f : 0.0f, 0, 0);
  }
  Asked asked;
  ListedBy(base, refii_mod_player_hero(base, 1), asked.uids);
  ListedBy(base, two, asked.uids);
  asked.uids.insert(asked.uids.end(), g_host.looked.begin(), g_host.looked.end());
  std::vector<uint64_t> unique;
  for (const uint64_t uid : asked.uids) {
    if (uid != 0u && unique.size() < kMaxRecords && std::find(unique.begin(), unique.end(), uid) == unique.end()) {
      unique.push_back(uid);
    }
  }
  // Each building under the household of the hero the joiner's screen acts for there.
  Asked ones;
  Asked twos;
  const int32_t partnership = refii_bridge_partnership();
  for (const uint64_t uid : unique) {
    const uint32_t entity = refii_household_resolve_uid(&ctx, base, uid);
    const int32_t owner = entity != 0u && refii_mod_component(base, entity, 67u) != 0u
                              ? refii::vfs::EstateOwnerPlayer(ctx, base, entity)
                              : 0;
    (refii::coop::EstateActsFor(2, owner, owner == 1 ? partnership : 0) == 1 ? ones : twos).uids.push_back(uid);
  }
  refii_household_scoped(&ctx, base, two, &AskAll, &twos);
  refii_household_scoped(&ctx, base, 0u, &AskAll, &ones);
  asked.bytes = std::move(twos.bytes);
  asked.bytes.insert(asked.bytes.end(), ones.bytes.begin(), ones.bytes.end());
  if (asked.bytes == g_host.stated) {
    return;
  }
  g_host.stated = asked.bytes;
  refii_bridge_player_blob_set(1, kBlobKey, asked.bytes.empty() ? nullptr : asked.bytes.data(),
                               static_cast<uint32_t>(asked.bytes.size()));
}

// ---- THE JOINER --------------------------------------------------------------------------------

struct Stated {
  Record record;
  uint32_t applied_to = 0u;  // the building entity its fields were last written into
};

struct Joiner {
  std::vector<uint8_t> loaded;
  std::unordered_map<uint64_t, Stated> buildings;
  double check_in = 0.0;
};

Joiner g_joiner;

/// The host's inline fields onto this machine's copy of the building.
void Apply(PPCContext& ctx, uint8_t* base, Stated& stated) {
  const uint32_t entity = refii_household_resolve_uid(&ctx, base, stated.record.building);
  const uint32_t building = entity != 0u ? refii_mod_component(base, entity, 67u) : 0u;
  if (building == 0u || entity == stated.applied_to) {
    return;
  }
  refii::mods::StoreU32(base, building + 0x2Cu, stated.record.decoration);
  refii::mods::StoreU32(base, building + 0x74u, stated.record.scale);
  refii::mods::StoreU32(base, building + 0x5Cu, stated.record.level);
  stated.applied_to = entity;
}

void DriveJoiner(PPCContext& ctx, uint8_t* base, float seconds) {
  const int32_t size = refii_bridge_player_blob_get(1, kBlobKey, nullptr, 0u);
  std::vector<uint8_t> bytes(size > 0 ? static_cast<size_t>(size) : 0u);
  if (size > 0) {
    refii_bridge_player_blob_get(1, kBlobKey, bytes.data(), static_cast<uint32_t>(bytes.size()));
  }
  if (bytes != g_joiner.loaded) {
    std::unordered_map<uint64_t, Stated> next;
    for (size_t at = 0; at + kRecordBytes <= bytes.size(); at += kRecordBytes) {
      const uint8_t* p = bytes.data() + at;
      Record record;
      record.building = Get64(p);
      record.occupant = Get64(p + 8);
      for (uint32_t i = 0; i < kValues; ++i) {
        record.values[i] = static_cast<int32_t>(Get32(p + 16 + 4 * i));
      }
      record.decoration = Get32(p + 16 + 4 * kValues);
      record.scale = Get32(p + 20 + 4 * kValues);
      record.level = Get32(p + 24 + 4 * kValues);
      Stated stated{record, 0u};
      const auto old = g_joiner.buildings.find(record.building);
      if (old != g_joiner.buildings.end() && old->second.record == record) {
        stated.applied_to = old->second.applied_to;  // unchanged: this machine's own writes stand
      }
      next[record.building] = stated;
    }
    g_joiner.buildings = std::move(next);
    g_joiner.loaded = std::move(bytes);
    g_joiner.check_in = 0.0;
  }
  g_joiner.check_in -= seconds;
  if (g_joiner.check_in > 0.0) {
    return;
  }
  g_joiner.check_in = kRestateSeconds;
  for (auto& [uid, stated] : g_joiner.buildings) {
    Apply(ctx, base, stated);
  }
}

/// The statement for a CECBuilding on an online joiner, or null.
const Record* StatedFor(uint8_t* base, uint32_t building) {
  if (g_joiner.buildings.empty() || !Heap(building) || !OnlineJoiner()) {
    return nullptr;
  }
  const auto found = g_joiner.buildings.find(Uid(base, LoadGuestU32(base, building + 4u)));
  return found != g_joiner.buildings.end() ? &found->second.record : nullptr;
}

}  // namespace

namespace refii::vfs {

void DriveEstateStatements(PPCContext& ctx, uint8_t* base, float seconds) {
  const int32_t mode = refii_mod_coop_mode(base);
  if (mode != 2) {
    g_host = Host{};
  }
  if (mode != 3) {
    g_joiner = Joiner{};
  }
  if (mode == 2) {
    DriveHost(ctx, base, seconds);
  } else if (mode == 3) {
    DriveJoiner(ctx, base, seconds);
  }
  // Each machine's own hero's title, for the other machine's pages (coop_estate_owner.cpp).
  static double title_in = 0.0;
  if (mode == 2 || mode == 3) {
    title_in -= seconds;
    if (title_in <= 0.0) {
      title_in = kRestateSeconds;
      StateOwnTitle(ctx, base);
    }
  }
}

void EstateStatementsChanged() { g_host.restate_in = 0.0; }

void EstateLook(PPCContext& ctx, uint8_t* base, uint64_t sign_uid) {
  const uint32_t sign = refii_household_resolve_uid(&ctx, base, sign_uid);
  const uint32_t link = sign != 0u ? refii_mod_component(base, sign, 142u) : 0u;
  PPCContext work = refii::mods::Frame(ctx);
  work.r3.u64 = link;
  const uint32_t building =
      link != 0u && refii::mods::CallGuest(work, base, refii::guest::Address(0x8229EE78u)) ? work.r3.u32 : 0u;
  const uint64_t uid = refii_mod_component(base, building, 67u) != 0u ? Uid(base, building) : 0u;
  if (uid == 0u) {
    REXLOG_WARN("Refii estate: the joiner looks at sale sign {:016X}, which names no building here", sign_uid);
    return;
  }
  if (std::find(g_host.looked.begin(), g_host.looked.end(), uid) == g_host.looked.end()) {
    if (g_host.looked.size() >= kMaxRecords) {
      g_host.looked.erase(g_host.looked.begin());
    }
    g_host.looked.push_back(uid);
    REXLOG_INFO("Refii estate: the joiner looks at building {:016X}; its book is stated to them from now on", uid);
  }
  g_host.restate_in = 0.0;
}

bool EstateStatedValue(uint8_t* base, uint32_t building, EstateValue value, int32_t* out) {
  const Record* record = StatedFor(base, building);
  if (record == nullptr || out == nullptr || value >= EstateValue::kCount) {
    return false;
  }
  *out = record->values[static_cast<uint32_t>(value)];
  return true;
}

bool EstateStatedOccupant(PPCContext& ctx, uint8_t* base, uint32_t building, uint32_t* out) {
  const Record* record = StatedFor(base, building);
  if (record == nullptr || out == nullptr) {
    return false;
  }
  *out = record->occupant != 0u ? refii_household_resolve_uid(&ctx, base, record->occupant) : 0u;
  return true;
}

bool EstateBlockedForTwo() {
  float blocked = 0.0f;
  return OnlineJoiner() && refii_bridge_player_get(1, kRulesKey, sizeof(kRulesKey) - 1u, &blocked) != 0 &&
         blocked != 0.0f;
}

bool EstateStatedSign(PPCContext& ctx, uint8_t* base, uint32_t sign) {
  const uint32_t link = sign != 0u ? refii_mod_component(base, sign, 142u) : 0u;
  PPCContext work = refii::mods::Frame(ctx);
  work.r3.u64 = link;
  const uint32_t building =
      link != 0u && refii::mods::CallGuest(work, base, refii::guest::Address(0x8229EE78u)) ? work.r3.u32 : 0u;
  const uint32_t record = building != 0u ? refii_mod_component(base, building, 67u) : 0u;
  return StatedFor(base, record) != nullptr;
}

}  // namespace refii::vfs

// For property_hooks.cpp (the buy and sale prices): the host's value on an online joiner.
extern "C" int32_t refii_estate_stated_value(uint8_t* base, uint32_t building, int32_t value, int32_t* out) {
  return value >= 0 && refii::vfs::EstateStatedValue(base, building, static_cast<refii::vfs::EstateValue>(value), out)
             ? 1
             : 0;
}

// ---- THE GETTERS (r3 the CECBuilding, the answer in r3) -------------------------------------------

#define REFII_ESTATE_GETTER(name, kind)                                                    \
  PPC_EXTERN_FUNC(__imp__##name);                                                          \
  REX_EXTERN(name) {                                                                       \
    int32_t stated = 0;                                                                    \
    if (refii::vfs::EstateStatedValue(base, ctx.r3.u32, refii::vfs::EstateValue::kind, &stated)) { \
      ctx.r3.u64 = static_cast<uint32_t>(stated);                                          \
      return;                                                                              \
    }                                                                                      \
    __imp__##name(ctx, base);                                                              \
  }
// TU1 0x8282AEB0 / disc 0x8274D1E0, 0x8282C1C0 / 0x8274E4F0, 0x8282C110 / 0x8274E440, 0x8282C470 /
// 0x8274E7A0, 0x8282C610 / 0x8274E940, 0x8282C870 / 0x8274EBA0.
REFII_ESTATE_GETTER(rex_CECBuilding_BaseValue, kBaseValue)
REFII_ESTATE_GETTER(rex_CECBuilding_EconomyBonus, kEconomy)
REFII_ESTATE_GETTER(rex_CECBuilding_Rent, kRent)
REFII_ESTATE_GETTER(rex_CECBuilding_NotForSaleAdjustment, kNotForSale)
REFII_ESTATE_GETTER(rex_CECBuilding_OpinionAdjustment, kOpinion)
REFII_ESTATE_GETTER(rex_CECBuilding_OwnerKilledAdjustment, kOwnerKilled)
#undef REFII_ESTATE_GETTER

// The occupant, TU1 0x8282D5D8 / disc 0x8274F908.
PPC_EXTERN_FUNC(__imp__rex_CECBuilding_Occupant);
REX_EXTERN(rex_CECBuilding_Occupant) {
  uint32_t occupant = 0u;
  if (refii::vfs::EstateStatedOccupant(ctx, base, ctx.r3.u32, &occupant)) {
    ctx.r3.u64 = occupant;
    return;
  }
  __imp__rex_CECBuilding_Occupant(ctx, base);
}
