// THE ITEM ROWS, STATED — see hooks/item_rows_stated.h.
//
// THE CALL. Every item row of the item books is built by one function, `sub_826CE230` (TU1; disc
// `sub_82407D20`, 367 instructions, the only difference the shop's screen id `li r3,0x16` / `0x15`):
//   r3 the row list, r4 &item handle (`lwz r4, 0(r27)` 0x826CE254 / 0x82407D44: `*r4` is the ITEM),
//   r5 the definition's InventoryItemComponent, r6 &parent name, r7 the population ticket, r8 the event
//   type (already rewritten by the wrapper `sub_826CEEF0`: 83 / 310 for an augment, 80 for a dye), r9 the
//   screen (15 / 16 / 25 / 22; disc 14 / 15 / 24 / 21), r10 the presentation.
// What it reads off the item it was handed, base = the item: `+4 / +8` the definition ref (record hash
// first, 0x826CE448 / 0x826CE44C), `+0xC` the count (`lwz r11, 0xC(r25)` 0x826CE304), `+0x10` the
// category (`lwz r11, 0x10(r31)` 0x826CE3BC), `+0x14` the viewed byte (`lbz r11, 0x14(r25)` 0x826CE378).
// It spawns the row through `lhGui_InGameMenu_SpawnButton` (0x826CE6A8) and then, for an item with the
// shop's pricing arm off (`arg_67`, 0 from every inventory caller), states the row's worn / held mark:
// `sub_826D0890(list, item, &item, index, screen)` (0x826CE794 / disc 0x82408284) stages `Float1` = the
// mark, `Float2` = the index `g_Menu:GetLastItemIndex()` answered, and runs
// `g_Menu:SetItemEmphasisByIndex()` (0x826D0BE4 / disc 0x8240A71C).
//
// So the row the spawn inside this call makes IS that item's row, and the emphasis staged inside it IS
// that row's mark: both are attached to the captured row (guest_tick_pump_state.cpp) with the facts the
// call was handed. Godot's item books read them (RefiiHostGetMenuRowItem); no list is walked.
//
// THE MARK IS ASKED OF THE LISTED HERO. The list is the player record the populates pick by the session
// bytes — `lbz 0x34 / 0x35` of the net manager, key 1 when both are set, else 2, then `bl sub_82264230`
// (TU1 0x829B19EC items, 0x829B3DA0 clothing, 0x829B22B4 dye targets, 0x829B0CF0 augment targets,
// 0x829B11A8 remover targets, 0x82430500 gift, 0x826CF708 counts, 0x826CF218 best ratings, 0x8246B1B4 the
// page info; disc `bl sub_822641F0` at 0x828CAB0C, 0x828CCEC0, 0x828CB3D4, 0x828C9E10, 0x828CA2C8,
// 0x823A4690, 0x824091F8, 0x82408D08, 0x82836DE4), whose key local player two's book states as 2
// (RefiiCoopInventoryListPlayer, guest_tick_pump.cpp). The mark reads the hero-ONE slot instead
// (`lwz r11, 0x6C(r11)` of the players record, 0x826D08CC / 0x8240A404). For the length of this call the
// slot holds the listed record's holder (Player record + 12 + 4) when that is another entity — local
// player two, and an online joiner, whose populates pick key 2 — so the list and its marks are one hero's.
#include <cstdint>

#include <rex/logging.h>

#include "core/guest_build.h"
#include "core/guest_state.h"
#include "core/payload_bridge_fable2.h"
#include "hooks/item_rows_stated.h"
#include "hooks/ppc_externs.h"
#include "vfs_hooks_private.h"

namespace refii::vfs {
uint32_t PlayerWithId(uint8_t* base, uint32_t id);
bool MenusActForPlayerTwo(uint8_t* base);
}  // namespace refii::vfs

namespace {

thread_local refii::vfs::ItemRowFacts t_row;
thread_local bool t_open = false;

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

/// The key the populates pick (see the file note): 2 for local player two's book or a machine that is
/// not the session host, else 1.
uint32_t ListedPlayerKey(uint8_t* base) {
    if (refii::vfs::MenusActForPlayerTwo(base)) {
        return 2u;
    }
    const uint32_t root = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
    const uint32_t game = Plausible(root) ? refii::vfs::LoadGuestU32(base, root + 0xCu) : 0u;
    const uint32_t net = Plausible(game) ? refii::vfs::LoadGuestU32(base, game + 0x8Cu) : 0u;
    if (!Plausible(net)) {
        return 1u;
    }
    const bool host = refii::vfs::LoadGuestU8(base, net + 0x34u) != 0u && refii::vfs::LoadGuestU8(base, net + 0x35u) != 0u;
    return host ? 1u : 2u;
}

}  // namespace

namespace refii::vfs {

ListedHeroScope::ListedHeroScope(uint8_t* guest, uint32_t screen) {
    using refii::vfs::LoadGuestU32;
    if (guest == nullptr) {
        return;
    }
    const uint32_t key = ListedPlayerKey(guest);
    if (key != 2u) {
        return;
    }
    const uint32_t record = refii::vfs::PlayerWithId(guest, key);
    const uint32_t listed = Plausible(record) ? LoadGuestU32(guest, record + 16u) : 0u;
    const uint32_t listed_hero = Plausible(listed) ? LoadGuestU32(guest, listed) : 0u;
    const uint32_t root = LoadGuestU32(guest, refii::guest::Address(0x83496AB8u));
    const uint32_t game = Plausible(root) ? LoadGuestU32(guest, root + 0xCu) : 0u;
    const uint32_t manager = Plausible(game) ? LoadGuestU32(guest, game + 88u) : 0u;
    const uint32_t cell = Plausible(manager) ? LoadGuestU32(guest, manager + 4u) : 0u;
    const uint32_t list = Plausible(cell) ? LoadGuestU32(guest, cell) : 0u;
    const uint32_t slot = Plausible(list) ? LoadGuestU32(guest, list + 108u) : 0u;
    const uint32_t slot_hero = Plausible(slot) ? LoadGuestU32(guest, slot) : 0u;
    static uint32_t s_said = 0u;
    if (s_said < 8u) {
        ++s_said;
        REXLOG_INFO("Refii item rows: screen {} lists player record {}'s hero 0x{:08X}; the hero-one slot "
                    "holds 0x{:08X} — {}",
                    screen, key, listed_hero, slot_hero,
                    !Plausible(listed_hero) || listed_hero == slot_hero ? "the marks are asked of it as it stands"
                                                                       : "the slot holds the listed hero for the row");
    }
    if (!Plausible(listed_hero) || listed_hero == slot_hero || !Plausible(list)) {
        return;
    }
    base = guest;
    players = list;
    holder = slot;
    // Inside another lend (HeroTwoLend, HouseholdScope) the real holder stays the one that lend named.
    outer = refii_bridge_hero_slot_lent_here();
    refii_bridge_hero_slot_lend(outer != 0u ? outer : holder);
    refii::vfs::StoreGuestU32(guest, list + 108u, listed);
}

ListedHeroScope::~ListedHeroScope() {
    if (base != nullptr) {
        refii::vfs::StoreGuestU32(base, players + 108u, holder);
        if (outer != 0u) {
            refii_bridge_hero_slot_lend(outer);
        } else {
            refii_bridge_hero_slot_return();
        }
    }
}

bool TakeItemRowForSpawn(ItemRowFacts* out) {
    if (!t_open || t_row.spawned) {
        return false;
    }
    t_row.spawned = true;
    *out = t_row;
    return true;
}

bool ItemRowOpen() { return t_open; }

void NoteItemRowEmphasis(int32_t level) {
    if (t_open && t_row.spawned) {
        refii_bridge_menu_row_emphasis_last(level);
    }
}

}  // namespace refii::vfs

REX_EXTERN(rex_lhGuiItemRow_Build) {
    REX_FUNC_PROLOGUE();
    using refii::vfs::LoadGuestU32;
    const uint32_t handle = ctx.r4.u32;
    const uint32_t item = Plausible(handle) ? LoadGuestU32(base, handle) : 0u;
    const uint32_t screen = refii::guest::ScreenIdToTu1(ctx.r9.u32);
    const bool outer = t_open;
    const refii::vfs::ItemRowFacts was = t_row;
    t_row = refii::vfs::ItemRowFacts{};
    t_row.screen = screen;
    t_row.ticket = ctx.r7.u32;
    t_row.type = ctx.r8.u32;
    if (Plausible(item)) {
        t_row.item = item;
        t_row.record = LoadGuestU32(base, item + 4u);
        t_row.count = LoadGuestU32(base, item + 0xCu);
        t_row.category = LoadGuestU32(base, item + 0x10u);
        t_row.viewed = refii::vfs::LoadGuestU8(base, item + 0x14u) != 0u;
    }
    t_open = true;
    {
        const bool lists = screen == 15u || screen == 16u || screen == 25u;
        refii::vfs::ListedHeroScope lend(lists ? base : nullptr, screen);
        __imp__rex_lhGuiItemRow_Build(ctx, base);
    }
    t_open = outer;
    t_row = was;
}
