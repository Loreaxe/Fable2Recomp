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
//
// THE PARTNERSHIP. Who may act on the other hero's property (core/estate_owner_rule.h) turns on the
// partnership, both heroes' saved words (core/hero_partners.cpp). The host decides every estate action an
// online joiner forwards by its own reading of them (mod_events.cpp ReplayEstateIntent), so it states that
// reading — a session value of player one's store, kPartnersKey, 0 none, 1 business partners, 2 married —
// and the joiner's screens decide by it (EstatePartnership) once it has arrived: the joiner's property
// screen at the host's building opens as the owner's exactly when the host will run what it forwards.

#include <algorithm>
#include <array>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <unordered_map>
#include <utility>
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
PPC_EXTERN_FUNC(rex_lhNetManager_FindEntityByNetId);
PPC_EXTERN_FUNC(rex_lhWideString_Dtor);
/// `body` run as `hero`'s household (mod_events.cpp HouseholdScope).
extern "C" void refii_household_scoped(PPCContext* ctx, uint8_t* base, uint32_t hero,
                                       void (*body)(PPCContext* ctx, uint8_t* base, void* arg), void* arg);

namespace {

using refii::vfs::LoadGuestU32;

constexpr char kBlobKey[] = "refii.estate.buildings";
constexpr char kRulesKey[] = "refii.estate.no_property_two";
constexpr char kPartnersKey[] = "refii.estate.partners";
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
  std::unordered_map<std::string, std::string> owner_texts;  // what each owner / tenant text was stated as
  double restate_in = 0.0;
};

// THE OWNER'S AND THE TENANT'S NAMES, AS TEXT. Page 1 names a building's non-hero owner (0x829996EC) and its
// tenant (0x82999770) by `sub_823C8510`'s wide display name; a joiner with no replica of that creature (a villager
// not near it) found none and showed GUI_SCREEN_REAL_ESTATE_OWNER_OTHER / _TENANT_OTHER. So the host states the
// text its own page would show, per building, as session texts of player one's store (kOwnerTextPrefix /
// kTenantTextPrefix + the building's UID), LOSSLESS: the wide string's UTF-16 units (the rep's buffer and length,
// `sub_82B3F5C8`: rep+0 the buffer, rep+4 the unit count, a 0 unit after them; disc `sub_82B395D8`, the same) as
// UTF-8. The joiner makes them back into page 1's caption (coop_estate_owner.cpp EstateCaptionFromText). A hero
// owner is named by its title (coop_estate_owner.cpp).
constexpr char kOwnerTextPrefix[] = "refii.estate.owner.";
constexpr char kTenantTextPrefix[] = "refii.estate.tenant.";

std::string TextKey(const char* prefix, uint64_t uid) {
  char key[48];
  std::snprintf(key, sizeof(key), "%s%016llx", prefix, static_cast<unsigned long long>(uid));
  return key;
}

void PutUtf8(std::string& out, uint32_t cp) {
  if (cp < 0x80u) {
    out.push_back(static_cast<char>(cp));
  } else if (cp < 0x800u) {
    out.push_back(static_cast<char>(0xC0u | (cp >> 6)));
    out.push_back(static_cast<char>(0x80u | (cp & 0x3Fu)));
  } else if (cp < 0x10000u) {
    out.push_back(static_cast<char>(0xE0u | (cp >> 12)));
    out.push_back(static_cast<char>(0x80u | ((cp >> 6) & 0x3Fu)));
    out.push_back(static_cast<char>(0x80u | (cp & 0x3Fu)));
  } else {
    out.push_back(static_cast<char>(0xF0u | (cp >> 18)));
    out.push_back(static_cast<char>(0x80u | ((cp >> 12) & 0x3Fu)));
    out.push_back(static_cast<char>(0x80u | ((cp >> 6) & 0x3Fu)));
    out.push_back(static_cast<char>(0x80u | (cp & 0x3Fu)));
  }
}

/// A wide string object's text (its rep's UTF-16 units, big-endian in guest memory) as UTF-8.
std::string WideUtf8(uint8_t* base, uint32_t handle) {
  const uint32_t rep = Heap(handle) ? LoadGuestU32(base, handle) : 0u;
  const uint32_t buffer = Heap(rep) ? LoadGuestU32(base, rep) : 0u;
  const uint32_t count = Heap(rep) ? LoadGuestU32(base, rep + 4u) : 0u;
  std::string out;
  for (uint32_t i = 0; Heap(buffer) && i < count && i < 512u; ++i) {
    uint32_t unit = refii::vfs::LoadGuestU16(base, buffer + 2u * i);
    if (unit >= 0xD800u && unit < 0xDC00u && i + 1 < count) {
      const uint32_t low = refii::vfs::LoadGuestU16(base, buffer + 2u * (i + 1));
      if (low >= 0xDC00u && low < 0xE000u) {
        unit = 0x10000u + ((unit - 0xD800u) << 10) + (low - 0xDC00u);
        ++i;
      }
    }
    PutUtf8(out, unit);
  }
  return out;
}

/// The text the host's page 1 shows for `creature` when it is no hero: its display name, or "" for none.
std::string CreatureText(PPCContext& ctx, uint8_t* base, uint32_t creature) {
  if (creature == 0u || refii_mod_entity_alive(base, creature) == 0 || creature == refii_mod_player_hero(base, 1) ||
      creature == refii_mod_player_hero(base, 2)) {
    return {};
  }
  PPCContext work = refii::mods::Frame(ctx, 0x200u);
  const uint32_t out = (work.r1.u32 + 0x100u) & ~0xFu;
  refii::mods::StoreU32(base, out, 0u);
  work.r3.u64 = out;
  work.r4.u64 = creature;
  if (!refii::mods::CallGuest(work, base, refii::guest::Address(0x823C8510u))) {
    return {};
  }
  std::string text = WideUtf8(base, out);
  PPCContext drop = refii::mods::Frame(ctx, 0x200u);
  drop.r3.u64 = out;
  rex_lhWideString_Dtor(drop, base);
  return text;
}

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
  std::vector<std::pair<std::string, std::string>> owner_texts;  // (key, text)
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
    const uint32_t occupant = call(0x8282D5D8u);
    record.occupant = Uid(base, occupant);
    asked.owner_texts.emplace_back(TextKey(kOwnerTextPrefix, uid), CreatureText(*ctx, base, call(0x8282D410u)));
    asked.owner_texts.emplace_back(TextKey(kTenantTextPrefix, uid), CreatureText(*ctx, base, occupant));
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
  // The partnership the host decides the joiner's estate actions by (head note).
  refii_bridge_player_write(1, kPartnersKey, sizeof(kPartnersKey) - 1u, static_cast<float>(refii_bridge_partnership()),
                            0, 0);
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
  for (const Asked* part : {&twos, &ones}) {
    for (const auto& [key, text] : part->owner_texts) {
      auto& held = g_host.owner_texts[key];
      if (held != text) {
        held = text;
        refii_bridge_player_write_text(1, key.c_str(), key.size(), text.c_str(), text.size(), 0);
      }
    }
  }
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
  if (*out == 0u && record->occupant != 0u) {
    // A creature the host made at run time has no UID here: its replica is the net manager's for the
    // host's UID, which is its net id (`lhNetManager_FindEntityByNetId` TU1 0x82334A60 / disc 0x82317438:
    // on the host, +52 and +53 set, the id IS the UID, `sub_82264488`; on a client, the replica map at
    // +128). The manager is the game's +0x8C.
    const uint32_t root = LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
    const uint32_t game = Heap(root) ? LoadGuestU32(base, root + 0xCu) : 0u;
    const uint32_t manager = Heap(game) ? LoadGuestU32(base, game + 0x8Cu) : 0u;
    if (Heap(manager)) {
      PPCContext find = refii::mods::Frame(ctx);
      find.r3.u64 = manager;
      find.r4.u64 = record->occupant;
      rex_lhNetManager_FindEntityByNetId(find, base);
      *out = refii_mod_entity_alive(base, find.r3.u32) != 0 ? find.r3.u32 : 0u;
    }
  }
  return true;
}

bool EstateStatedCaptionText(uint8_t* base, uint32_t building, bool tenant, std::string* out) {
  if (out == nullptr || StatedFor(base, building) == nullptr) {
    return false;
  }
  const std::string key =
      TextKey(tenant ? kTenantTextPrefix : kOwnerTextPrefix, Uid(base, LoadGuestU32(base, building + 4u)));
  char text[REFII_MOD_PLAYER_TEXT_MAX + 1];
  const int32_t length = refii_bridge_player_get_text(1, key.c_str(), key.size(), text, sizeof(text));
  if (length <= 0) {
    return false;
  }
  *out = text;
  return true;
}

int32_t EstatePartnership() {
  float stated = 0.0f;
  const bool joiner = OnlineJoiner();
  const bool known = joiner && refii_bridge_player_get(1, kPartnersKey, sizeof(kPartnersKey) - 1u, &stated) != 0;
  return refii::coop::EstatePartnershipHere(joiner, known, static_cast<int32_t>(stated), refii_bridge_partnership());
}

std::string EstatePartnershipWords() {
  const uint64_t one = refii_bridge_hero_id(1);
  const uint64_t two = refii_bridge_hero_id(2);
  char text[160];
  std::snprintf(text, sizeof(text), "hero one %012llX's word %d, hero two %012llX's word %d, this machine reads %d",
                static_cast<unsigned long long>(one), refii_bridge_partner_kind(1, two),
                static_cast<unsigned long long>(two), refii_bridge_partner_kind(2, one), refii_bridge_partnership());
  std::string out(text);
  if (OnlineJoiner()) {
    float stated = 0.0f;
    out += refii_bridge_player_get(1, kPartnersKey, sizeof(kPartnersKey) - 1u, &stated) != 0
               ? ", the host states " + std::to_string(static_cast<int32_t>(stated))
               : std::string(", the host has stated none yet");
  }
  return out;
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
// Asked by page 1's renter caption (return address TU1 0x8299973C / disc 0x828EE04C) on an online joiner, the
// host's stated tenant text is made into the caption (coop_estate_owner.cpp EstateCaptionFromText; the midasm
// RefiiEstateTenantStatedText hands it over), whether or not the tenant resolves here.
REX_EXTERN(rex_CECBuilding_Occupant) {
  const PPCContext entry = ctx;
  uint32_t occupant = 0u;
  if (refii::vfs::EstateStatedOccupant(ctx, base, ctx.r3.u32, &occupant)) {
    ctx.r3.u64 = occupant;
    std::string text;
    if (static_cast<uint32_t>(entry.lr) == refii::guest::Address(0x8299973Cu) &&
        refii::vfs::EstateStatedCaptionText(base, entry.r3.u32, true, &text) && !text.empty()) {
      refii::vfs::EstateCaptionFromText(entry, base, text);
    }
    return;
  }
  __imp__rex_CECBuilding_Occupant(ctx, base);
}
