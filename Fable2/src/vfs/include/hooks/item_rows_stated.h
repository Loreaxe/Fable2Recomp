// THE ITEM ROWS, STATED (item_rows_stated.cpp). `sub_826CE230` (TU1; disc `sub_82407D20`) builds every
// item row of the item books from the item handle it is handed: while it runs, the row its spawn makes
// carries that item's record, instance, count, category and viewed byte, and the mark its
// `SetItemEmphasisByIndex` states (guest_tick_pump.cpp OnMenuSpawn, resource_manager_open.cpp).
#ifndef REFII_VFS_HOOKS_ITEM_ROWS_STATED_H
#define REFII_VFS_HOOKS_ITEM_ROWS_STATED_H

#include <cstdint>

namespace refii::vfs {

/// What one call of `sub_826CE230` was handed (TU1 screen ids).
struct ItemRowFacts {
    uint32_t screen = 0u;
    uint32_t ticket = 0u;
    uint32_t type = 0u;
    uint32_t record = 0u;
    uint32_t item = 0u;
    uint32_t count = 0u;
    uint32_t category = 0u;
    bool viewed = false;
    bool spawned = false;
};

/// The open call's facts for the row being spawned now, once per call; false outside one.
bool TakeItemRowForSpawn(ItemRowFacts* out);

/// True while `sub_826CE230` runs on this thread.
bool ItemRowOpen();

/// `g_Menu:SetItemEmphasisByIndex()`'s `Float1` while the call is open: the mark of the row it spawned.
void NoteItemRowEmphasis(int32_t level);

/// The hero-one slot (players record + 108) holding the hero the inventory lists list for one scope: the
/// holder of the player record the populates pick (key 2 for local player two's book or a machine that is
/// not the session host), when that is another entity than the slot's. Nothing otherwise, and nothing for a
/// null `base`. `screen` names the list for the log.
class ListedHeroScope {
public:
    ListedHeroScope(uint8_t* base, uint32_t screen);
    ~ListedHeroScope();
    ListedHeroScope(const ListedHeroScope&) = delete;
    ListedHeroScope& operator=(const ListedHeroScope&) = delete;

private:
    uint8_t* base = nullptr;
    uint32_t players = 0u;
    uint32_t holder = 0u;
    uint32_t outer = 0u;
};

}  // namespace refii::vfs

#endif  // REFII_VFS_HOOKS_ITEM_ROWS_STATED_H
