// c++ -std=c++20 -I Fable2/src/vfs/include tools/tests/coop_item_rule_test.cpp && ./a.out
// The co-op item rule (core/coop_item_rule.h) and the generated story table's shape
// (core/coop_story_items.inc; that it matches the scripts is tools/fable2_story_items.py --check).
#include "core/coop_item_rule.h"

#include <cctype>
#include <cstdio>
#include <cstring>
#include <set>
#include <string>
#include <vector>

using namespace refii::coop;

// Story items: the quest category less the silver key, or a scripted check.
static_assert(StoryItem(26, false, false) && !StoryItem(26, true, false), "category 26, not the silver key");
static_assert(StoryItem(16, false, true) && StoryItem(1, false, true), "a record a quest script checks for");
static_assert(!StoryItem(1, false, false) && !StoryItem(0, false, false), "anything else is not a story item");

// Legendary weapons: duplicated from every source but a silver-key chest.
static_assert(ItemRuleFlags(1, 5, false, false, kItemPlaced) == kItemCopyForPartner, "the Daichi from its chest");
static_assert(ItemRuleFlags(1, 5, false, false, kItemGranted) == kItemCopyForPartner, "the Maelstrom, a quest's grant");
static_assert(ItemRuleFlags(1, 5, false, false, kItemPerPlayer) == 0u, "a silver-key chest refills for the partner");
static_assert(ItemRuleFlags(1, 4, false, false, kItemPlaced) == 0u, "a master weapon is ordinary loot");

// Rating 5 placed in the world: duplicated; granted by a script: not (only a legendary is).
static_assert(ItemRuleFlags(18, 5, false, false, kItemPlaced) == kItemCopyForPartner, "the Will User Robe");
static_assert(ItemRuleFlags(6, 5, false, false, kItemPlaced) == kItemCopyForPartner, "a Potion of Life from a chest");
static_assert(ItemRuleFlags(6, 5, false, false, kItemGranted) == 0u, "a five-star potion a script grants");
static_assert(ItemRuleFlags(6, 5, false, false, kItemPerPlayer) == 0u, "a Potion of Life from a silver-key chest");
static_assert(ItemRuleFlags(6, 3, false, false, kItemPlaced) == 0u, "ordinary loot stays as it is");
static_assert(ItemRuleFlags(0, 1, false, false, kItemPlaced) == 0u, "gold");

// Quest items are never duplicated, whatever their rating.
static_assert(ItemRuleFlags(26, 5, false, false, kItemPlaced) == kItemToPlayerOne, "a Knothole Island volume");
static_assert(ItemRuleFlags(26, 1, false, false, kItemGranted) == kItemToPlayerOne, "a quest key granted");
static_assert(ItemRuleFlags(26, 1, true, false, kItemPlaced) == 0u, "the silver key is per player already");

// Both: the story rule places it, the partner gets the copy.
static_assert(ItemRuleFlags(1, 5, false, true, kItemPlaced) == (kItemToPlayerOne | kItemCopyForPartner), "the Chopper");
static_assert(ItemRuleFlags(3, 5, false, true, kItemPlaced) == (kItemToPlayerOne | kItemCopyForPartner),
              "the Archon's Dream");
static_assert(ItemRuleFlags(1, 5, false, true, kItemPerPlayer) == kItemToPlayerOne, "per-player chests copy nothing");

// The receivers.
constexpr bool Is(ItemReceivers r, int32_t to, int32_t copy) { return r.to == to && r.copy == copy; }
static_assert(Is(Receivers(0u, 1), 1, 0) && Is(Receivers(0u, 2), 2, 0), "ordinary: whoever the script named");
static_assert(Is(Receivers(kItemToPlayerOne, 2), 1, 0) && Is(Receivers(kItemToPlayerOne, 1), 1, 0),
              "a story item: player one, whoever acts");
static_assert(Is(Receivers(kItemCopyForPartner, 1), 1, 2) && Is(Receivers(kItemCopyForPartner, 2), 2, 1),
              "a unique item: the finder, and the partner's own copy");
static_assert(Is(Receivers(kItemToPlayerOne | kItemCopyForPartner, 2), 1, 2) &&
                  Is(Receivers(kItemToPlayerOne | kItemCopyForPartner, 1), 1, 2),
              "both: player one holds it, player two gets the copy");

namespace {

std::vector<std::string> g_items;
std::vector<std::string> g_families;

#define REFII_STORY_ITEM(name) g_items.emplace_back(name);
#define REFII_STORY_FAMILY(prefix) g_families.emplace_back(prefix);
void Load() {
#include "core/coop_story_items.inc"
}
#undef REFII_STORY_ITEM
#undef REFII_STORY_FAMILY

bool Identifier(const std::string& name) {
  if (name.empty()) {
    return false;
  }
  for (char c : name) {
    if (!std::isalnum(static_cast<unsigned char>(c)) && c != '_') {
      return false;
    }
  }
  return true;
}

}  // namespace

int main() {
  Load();
  int failed = 0;
  const auto expect = [&](bool ok, const char* what) {
    if (!ok) {
      std::printf("FAILED: %s\n", what);
      ++failed;
    }
  };
  expect(!g_items.empty(), "the story table lists records");
  std::set<std::string> seen;
  for (std::size_t i = 0; i < g_items.size(); ++i) {
    expect(Identifier(g_items[i]), "every name is a record name");
    expect(i == 0 || g_items[i - 1] < g_items[i], "sorted and unique (the generator's order)");
    expect(g_items[i] != "ObjectInventorySilverKey", "the silver key is not a story item");
    seen.insert(g_items[i]);
  }
  for (const char* name : {"QC070_ThagGateKey", "GilesPortcullisKey", "QO815_Augment", "QO170_KeyToChest",
                           "QO630_MuttonOfEternalHope", "QO630_WineOfForgiveness", "QO630_SacredPieOfKindness",
                           "QO020_CoffinNote", "QO110_Book", "QD010_IceKey", "QD010_StormKey", "QD010_SunKey",
                           "QD070_Key", "QD060_ChestyInvite", "QO160_Book", "ObjectInventory_Legend_Axe"}) {
    expect(seen.count(name) == 1, name);
  }
  for (const char* name : {"ObjectInventoryFoodAndDrinkAlcoholBeer_1", "ObjectInventorySpade", "ObjectInventoryLute",
                           "ObjectInventoryGiftRing_1", "ObjectInventoryGiftDogTreat", "ObjectInventoryBookDogTrickBeg"}) {
    expect(seen.count(name) == 0, "a common good the shops stock is not a story item");
  }
  for (const std::string& prefix : g_families) {
    expect(Identifier(prefix) && prefix.back() == '_', "a family is a prefix the script appends a number to");
  }
  if (failed == 0) {
    std::printf("ok (%zu story records, %zu families)\n", g_items.size(), g_families.size());
  }
  return failed == 0 ? 0 : 1;
}
