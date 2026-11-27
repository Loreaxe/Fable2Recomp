// refii — WHO A HERO IS, AND WHO THEY ARE PARTNERED WITH: Fable 3's player-to-player marriage and
// business partnership, kept the way Fable 3 keeps them (CECMultiplayerMarriage's MultiplayerSpouse
// and BusinessPartner records, by the other hero's HeroUID) on Fable 2's heroes.
//
// A HERO'S IDENTITY is a random 48-bit number made once for that hero and kept with its own save:
// two saved player values (`refii.hero.idh` / `.idl`, 24 bits each, exact in a float), so it is the
// same on both machines (the player store restates it) and comes back when the hero's save loads.
// Only the machine that plays the hero makes it, and only once that hero is in the game. THE PLAIN
// HENCHMAN HAS NONE: player two is a hero with an identity only while they brought one (a character
// from a save, refii_bridge_two_is_hero); the henchman is no one's partner, co-owns nothing and is owed
// nothing, and nothing is made or saved for them.
//
// A PARTNERSHIP is a saved value in EACH partner's store, `refii.partner.<other's id>`: 1 business,
// 2 married. Both are written together; it stands between the two heroes in the game only while
// both stores say so.
//
// SHARED PROPERTY (Fable 3's IsCoOwned / CoOwnedHeroUID): a building bought while partnered is
// co-owned with the partner, a saved value of the WORLD in player one's store (as condition is):
// `refii.coowned.<uid>.h/.l` the co-owner's id. It is shared while the holder's partnership with
// that id stands. Rent and a sale are split half and half; the half of a partner who is not in the
// game is owed (Fable 3's m_Money), `refii.owed.<id>` in the world, and paid when they are back.

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <random>
#include <string>

#include "core/payload_bridge_fable2.h"

#include <atomic>

namespace {

/// Player two brought a hero (payload_bridge_fable2.h refii_bridge_two_is_hero).
std::atomic<bool> g_two_is_hero{false};

constexpr char kIdHigh[] = "refii.hero.idh";
constexpr char kIdLow[] = "refii.hero.idl";
constexpr uint32_t kHalfMask = 0xFFFFFFu;

bool Read(int32_t player, const char* key, float& out) {
  return refii_bridge_player_get(player, key, std::strlen(key), &out) != 0;
}

void Save(int32_t player, const char* key, float value) {
  refii_bridge_player_write(player, key, std::strlen(key), value, 0, 1);
}

void Add(int32_t player, const char* key, float value) {
  refii_bridge_player_write(player, key, std::strlen(key), value, 1, 1);
}

std::string Keyed(const char* prefix, uint64_t id, const char* suffix = "") {
  char key[64];
  std::snprintf(key, sizeof(key), "%s%012llX%s", prefix, static_cast<unsigned long long>(id), suffix);
  return key;
}

std::string UidKey(uint64_t uid, const char* suffix) {
  char key[64];
  std::snprintf(key, sizeof(key), "refii.coowned.%016llX%s", static_cast<unsigned long long>(uid), suffix);
  return key;
}

uint32_t Half(float value) {
  return value >= 0.0f && value <= static_cast<float>(kHalfMask) ? static_cast<uint32_t>(value) : 0u;
}

uint64_t Join(uint32_t high, uint32_t low) { return (static_cast<uint64_t>(high & kHalfMask) << 24) | (low & kHalfMask); }

}  // namespace

extern "C" int32_t refii_bridge_two_is_hero(void) { return g_two_is_hero.load(std::memory_order_relaxed) ? 1 : 0; }

extern "C" void refii_bridge_two_is_hero_set(int32_t on) { g_two_is_hero.store(on != 0, std::memory_order_relaxed); }

extern "C" uint64_t refii_bridge_hero_id(int32_t player) {
  if (player == 2 && refii_bridge_two_is_hero() == 0) {
    return 0u;
  }
  float high = 0.0f;
  float low = 0.0f;
  if (!Read(player, kIdHigh, high) || !Read(player, kIdLow, low)) {
    return 0u;
  }
  return Join(Half(high), Half(low));
}

extern "C" void refii_bridge_hero_id_ensure(int32_t player) {
  if ((player != 1 && player != 2) || (player == 2 && refii_bridge_two_is_hero() == 0) ||
      ((refii_bridge_player_owned() >> (player - 1)) & 1u) == 0u || refii_bridge_hero_id(player) != 0u) {
    return;
  }
  static std::mt19937_64 random{std::random_device{}()};
  uint64_t id = 0u;
  while (id == 0u) {
    id = random() & ((static_cast<uint64_t>(kHalfMask) << 24) | kHalfMask);
  }
  Save(player, kIdHigh, static_cast<float>(static_cast<uint32_t>(id >> 24)));
  Save(player, kIdLow, static_cast<float>(static_cast<uint32_t>(id & kHalfMask)));
}

extern "C" int32_t refii_bridge_partner_kind(int32_t player, uint64_t other) {
  float kind = 0.0f;
  return other != 0u && Read(player, Keyed("refii.partner.", other).c_str(), kind) ? static_cast<int32_t>(kind) : 0;
}

extern "C" int32_t refii_bridge_partnership(void) {
  const uint64_t one = refii_bridge_hero_id(1);
  const uint64_t two = refii_bridge_hero_id(2);
  if (one == 0u || two == 0u) {
    return 0;
  }
  const int32_t first = refii_bridge_partner_kind(1, two);
  const int32_t second = refii_bridge_partner_kind(2, one);
  return first == second ? first : 0;
}

extern "C" int32_t refii_bridge_partnership_set(int32_t kind) {
  const uint64_t one = refii_bridge_hero_id(1);
  const uint64_t two = refii_bridge_hero_id(2);
  if (one == 0u || two == 0u || kind < 0 || kind > 2) {
    return 0;
  }
  Save(1, Keyed("refii.partner.", two).c_str(), static_cast<float>(kind));
  Save(2, Keyed("refii.partner.", one).c_str(), static_cast<float>(kind));
  return 1;
}

extern "C" uint64_t refii_bridge_coowner(uint64_t uid) {
  float high = 0.0f;
  float low = 0.0f;
  if (uid == 0u || !Read(1, UidKey(uid, ".h").c_str(), high) || !Read(1, UidKey(uid, ".l").c_str(), low)) {
    return 0u;
  }
  return Join(Half(high), Half(low));
}

extern "C" void refii_bridge_coowner_set(uint64_t uid, uint64_t id) {
  if (uid == 0u) {
    return;
  }
  Save(1, UidKey(uid, ".h").c_str(), static_cast<float>(static_cast<uint32_t>((id >> 24) & kHalfMask)));
  Save(1, UidKey(uid, ".l").c_str(), static_cast<float>(static_cast<uint32_t>(id & kHalfMask)));
}

extern "C" int32_t refii_bridge_owed(uint64_t id) {
  float owed = 0.0f;
  return id != 0u && Read(1, Keyed("refii.owed.", id).c_str(), owed) && owed > 0.0f ? static_cast<int32_t>(owed) : 0;
}

extern "C" void refii_bridge_owed_add(uint64_t id, int32_t gold) {
  if (id != 0u && gold != 0) {
    Add(1, Keyed("refii.owed.", id).c_str(), static_cast<float>(gold));
  }
}
