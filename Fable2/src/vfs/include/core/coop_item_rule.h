// WHO RECEIVES AN ITEM IN CO-OP, when player two plays their own hero (local, or an online hero
// joiner). Pure: the caller states the item's data and how it is being obtained; nothing here reads
// the guest. The core Lua's inventory add wrappers ask it (Refii._coop_item, mod_lua_natives.cpp) at the
// receiver argument of the add each shipped script makes (CoopHeroLua.cs). A plain henchman keeps the
// 360's rules: no hero two, no rule.
//
// STORY ITEMS go to PLAYER ONE whoever acts: the game's quest-item category (InventoryItemComponent
// "Category" 26, EIC_QUEST_ITEM, inventoryitemenums.lua) less the silver key (silver keys are per player
// already), and the records a shipped quest script asks the hero's inventory for that are the quest's own
// (a quest id in the name, a quest display key) or that no shop stocks — not the common goods a quest also
// asks for, which player one can always buy (the generated table core/coop_story_items.inc,
// tools/fable2_story_items.py). Quest threads ask hero one's bag (QuestManager.HeroEntity = GetPlayerHero()
// on the quest thread, hero one), so an item of these in hero two's bag soft-locks the quest.
//
// UNIQUE ITEMS ARE DUPLICATED: each hero gets their own copy, so nobody takes one from the other.
//   * a LEGENDARY weapon — Category 1 (EIC_WEAPONS) with Rating 5, the game's own top tier (13 records,
//     no shop sells one) — however it is obtained: a container, a spot, a pickup, a quest's grant;
//   * any other Rating-5 item a hero takes from a placed source (a chest, drawer or cupboard, a dive or a
//     dig spot, a pickup): the top tier the game places in the world once (the unique clothing, the
//     Potions of Life and Resurrection Phials, the Archon's Dream, the Gangreen Fairy, the Demon Door and
//     Gargoyle prizes of that tier).
//   Never a quest item (Category 26): a story item is not duplicated, it goes to player one. Never from
//   a silver-key chest: those already open once per player and refill for the partner.
// Where both apply (the Chopper: a legendary a quest waits for) the story rule places the item, so
// player one always holds it, and the partner gets the copy.
#pragma once

#include <cstdint>

namespace refii::coop {

/// How the item is being obtained.
enum ItemSource : int32_t {
  kItemPlaced = 1,     ///< out of a world container or spot, or a pickup (Inventory.AddItem / AddEntity)
  kItemGranted = 2,    ///< a script's grant by name (Inventory.AddItemOfType)
  kItemPerPlayer = 3,  ///< out of a silver-key chest: each player opens it once for themselves
};

/// What the rule asks of the add.
enum ItemFlag : uint32_t {
  kItemToPlayerOne = 1u,     ///< the item goes to player one's hero, whoever was to receive it
  kItemCopyForPartner = 2u,  ///< the hero who does not receive it gets their own copy
};

constexpr int32_t kCategoryWeapons = 1;     // EIC_WEAPONS
constexpr int32_t kCategoryQuestItem = 26;  // EIC_QUEST_ITEM
constexpr int32_t kTopRating = 5;

/// A story item: the quest-item category (not the silver key), or a record a quest script checks for.
constexpr bool StoryItem(int32_t category, bool silver_key, bool script_checked) {
  return (category == kCategoryQuestItem && !silver_key) || script_checked;
}

/// The rule's flags for one item.
constexpr uint32_t ItemRuleFlags(int32_t category, int32_t rating, bool silver_key, bool script_checked,
                                 int32_t source) {
  uint32_t flags = StoryItem(category, silver_key, script_checked) ? kItemToPlayerOne : 0u;
  const bool legendary = category == kCategoryWeapons && rating == kTopRating;
  const bool placed_top = source == kItemPlaced && rating == kTopRating;
  if (category != kCategoryQuestItem && source != kItemPerPlayer && (legendary || placed_top)) {
    flags |= kItemCopyForPartner;
  }
  return flags;
}

/// Who receives: `to` the player whose hero the add is made on, `copy` the player who gets their own
/// copy (0: none). `receiver` is the player (1 or 2) the shipped script named.
struct ItemReceivers {
  int32_t to;
  int32_t copy;
};

constexpr ItemReceivers Receivers(uint32_t flags, int32_t receiver) {
  const int32_t to = (flags & kItemToPlayerOne) != 0u ? 1 : receiver;
  return {to, (flags & kItemCopyForPartner) != 0u ? (to == 1 ? 2 : 1) : 0};
}

}  // namespace refii::coop
