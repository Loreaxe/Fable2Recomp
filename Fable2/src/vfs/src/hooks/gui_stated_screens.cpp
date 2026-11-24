// THE IN-GAME SCREEN SET, STATED. The guest's screen manager (`*(GUI + 56)`) keeps the screen that
// is up and one presentation per screen id. Both are stated here from the calls that set them, into
// the shared registry (core/told_statements.h), so the host reads neither out of guest memory.
//
// WHICH SCREEN OWNS A PRESENTATION. Two functions file a presentation under a screen id, and each
// creates it through CGameface's maker (rex_CGameface_CreatePresentation) while it runs:
//   - `sub_823DC9D8(set, id)` (disc `sub_823FE6F0`): picks the id's own slot `&set[id + 3]`, or the
//     shared slot `set + 39` after `set[38] = id` and destroying what the shared slot held, then
//     `lhGuiPresentation_LoadIfUnloaded(slot, file)`.
//   - `sub_823DC888(set)` (disc `sub_823FE5A0`): the same for id 0, at GUI init.
// A presentation made inside either belongs to that id until its destroy.
//
// WHICH SCREEN IS UP is the screen setter's own argument (rex_lhGuiManager_SetScreen,
// mod_events.cpp). "None" is stated from the exits: both walk every id and unload it through
// `sub_823DD028(set, id)` (disc `sub_823FED30`), and write the none id straight after the last one
// (`*(set + 4) = 31` at 0x8236f01c, after the loop reaches 30). The last id's unload inside an exit
// is that statement.
#include <atomic>
#include <chrono>
#include <cstdint>

#include "vfs_hooks_private.h"
#include "core/guest_build.h"
#include "core/payload_bridge_fable2.h"
#include "hooks/ppc_externs.h"

namespace {

constexpr uint32_t kNoScreen = 0xFFFFFFFFu;
constexpr uint32_t kLastScreenTu1 = 29u;

thread_local int32_t t_loading = -1;
thread_local uint32_t t_loads_made = 0;
thread_local int32_t t_exiting = 0;
thread_local int32_t t_box_opening = 0;
thread_local int32_t t_asking_stop_travel = 0;
thread_local bool t_menu_box_opened = false;

// The pause root handler's player, as its setter last stated it: non-zero when player two's.
std::atomic<uint32_t> g_pause_player_two{0u};
// The abilities screen handler's, the same way: non-zero when player two's book.
std::atomic<uint32_t> g_abilities_player_two{0u};

constexpr uint32_t kAbilitiesAssignMode = 1u;
constexpr uint32_t kFlowTopVtable = 2u;
constexpr uint32_t kFlowDepth = 3u;

// The command list as its writer just left it: the count at `flow + 8`, the sentinel at
// `flow + 4`, the top the sentinel's `+4` link and its command at `+8` (`lwz r11,4(r31);
// lwz r10,4(r11); lwz r3,8(r10)` 0x82318740).
void StateFlowTop(uint8_t* base, uint32_t flow) {
    const uint32_t depth = refii::vfs::LoadGuestU32(base, flow + 8u);
    uint32_t vtable = 0u;
    if (depth != 0u) {
        const uint32_t sentinel = refii::vfs::LoadGuestU32(base, flow + 4u);
        const uint32_t command =
            refii::vfs::LoadGuestU32(base, refii::vfs::LoadGuestU32(base, sentinel + 4u) + 8u);
        vtable = refii::vfs::LoadGuestU32(base, command);
    }
    static std::atomic<uint64_t> last{~0ull};
    const uint64_t key = (static_cast<uint64_t>(flow) << 32) | vtable;
    if (last.exchange(key) != key) {
        REXLOG_INFO("Refii game flow: stack {:08X} top vtable {:08X}, depth {}", flow, vtable, depth);
    }
    refii_bridge_stated_value(kFlowTopVtable, vtable);
    refii_bridge_stated_value(kFlowDepth, depth);
}

}  // namespace

namespace refii::vfs {

int32_t StatedScreenLoading() { return t_loading; }

void StatedScreenPresentationMade() { ++t_loads_made; }

void StatedScreenExiting(int32_t delta) { t_exiting += delta; }

void StatedScreenBoxOpening(int32_t delta) { t_box_opening += delta; }

bool StatedScreenKeptByBox() { return t_box_opening > 0; }

void StatedMenuBoxOpened() {
    if (t_asking_stop_travel > 0) t_menu_box_opened = true;
}

bool StatedPausePlayerTwo() { return g_pause_player_two.load(std::memory_order_relaxed) != 0u; }

bool StatedAbilitiesPlayerTwo() { return g_abilities_player_two.load(std::memory_order_relaxed) != 0u; }

}  // namespace refii::vfs

REX_EXTERN(rex_lhGuiScreenSet_EnsureLoaded) {
    REX_FUNC_PROLOGUE();
    const int32_t outer = t_loading;
    t_loading = static_cast<int32_t>(refii::guest::ScreenIdToTu1(ctx.r4.u32));
    const uint32_t made_before = t_loads_made;
    const auto started = std::chrono::steady_clock::now();
    __imp__rex_lhGuiScreenSet_EnsureLoaded(ctx, base);
    const auto us = std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now() - started);
    if (t_loads_made != made_before) {
        REXLOG_INFO("Refii screen load: screen {} loaded in {} us ({} presentation(s) made)", t_loading,
                    us.count(), t_loads_made - made_before);
    }
    t_loading = outer;
}

// THE MODAL LATCH AND ID (`mgr + 40` / `+ 44`, mgr = `*(GUI + 28)`) have five writers besides the
// constructor's 1 / 0:
//   - the two openers (display_box_hooks.cpp), with their own arguments;
//   - the screen setter, back to 1 / 0 for every id but the boxes' own (0x8236e71c: `if ( !v9 )`,
//     v9 set for 19, 4, 5, 3 and 9) — stated from its argument in mod_events.cpp;
//   - `sub_8236FF58(mgr)` (disc `sub_82338150`), `*(a1 + 40) = 1; *(a1 + 44) = 0`, below;
//   - the world map's stop-travelling question `sub_826C7E90` (disc `sub_825A3DC0`), which opens a
//     menu box and then writes 4 / 7 (0x826c8084): stated when a menu box opened inside it.
REX_EXTERN(rex_lhGuiManager_ClearModalState) {
    REX_FUNC_PROLOGUE();
    __imp__rex_lhGuiManager_ClearModalState(ctx, base);
    refii_bridge_stated_modal(1u, 0u);
}

REX_EXTERN(rex_lhGuiWorldMapScreen_AskStopTravel) {
    REX_FUNC_PROLOGUE();
    const bool outer = t_menu_box_opened;
    t_menu_box_opened = false;
    ++t_asking_stop_travel;
    __imp__rex_lhGuiWorldMapScreen_AskStopTravel(ctx, base);
    --t_asking_stop_travel;
    if (t_menu_box_opened) {
        refii_bridge_stated_modal(4u, 7u);
    }
    t_menu_box_opened = outer;
}

// THE TAB STRIP's show `sub_826B8348(strip, tab)` (disc `sub_8240B218`): the only writer of the
// current tab, `a1[1] = a2; a1[2] = a2` (0x826b83a4), from each screen's enter with its own tab.
REX_EXTERN(rex_lhGuiTabStrip_Show) {
    REX_FUNC_PROLOGUE();
    const uint32_t tab = ctx.r4.u32;
    __imp__rex_lhGuiTabStrip_Show(ctx, base);
    refii_bridge_stated_tab(tab);
}

// WHOSE PAUSE MENU IT IS. The pause root handler (vtable 0x820CDE18, disc 0x820B06E8; screen id 0
// in the owner's map) keeps the player at `+4`: 0 player one, 1 player two. Its constructor
// `sub_826A1818` writes 0 (0x826a1848) and the one writer after it is the handler's vtable slot 8,
// `stw r4, 4(r3); blr` — a body the linker folded across classes (`sub_82EC34C8`, disc
// `sub_82348CA8`), so the statement is taken only when `r3` is a pause root handler. Its callers
// are the opener `sub_826A2C88(handler, isPlayerOne)` (`(*(*a1 + 32))(a1, a2 == 0)` 0x826a2cb8)
// and the screens that hand the menu back through `lhGuiManager_FindScreenHandler(owner, 0)`
// (0x826a6fb0, 0x826b18f0, 0x826cccf4, 0x827d0050, 0x827e146c, 0x82805578). The guest reads it
// back as `*(FindScreenHandler(owner, 0) + 4) == 1` (`sub_8236E400` 0x8236e430).
//
// WHOSE ABILITIES BOOK IT IS. The abilities handler (vtable 0x820CF4BC, disc 0x820C1D30; screen 14)
// keeps it at `+4` too, read as `screen+4 == 0` for player one (0x826C2D58, disc 0x8259ED08). Its
// writers: the constructor's 0 (`sub_826C1E80` 0x826c1ea4); the same folded slot 8 (vtable
// +0x20 is `sub_82EC34C8`), called by the pause root before it switches to screen 14 (0x826a6264);
// and its enter `sub_826C2118` (disc `sub_8259E0C8`, vtable +8), which copies the pause root's:
// `FindScreenHandler(owner, 0)` (0x826c2150), `lwz r11, 4(r3)`, `stw r11, 4(r31)` (0x826c216c,
// disc 0x8259e11c), 0 when there is no root.
REX_EXTERN(rex_lhFolded_StoreWordAt4) {
    const uint32_t self = ctx.r3.u32;
    const uint32_t value = ctx.r4.u32;
    __imp__rex_lhFolded_StoreWordAt4(ctx, base);
    const uint32_t vtable = refii::vfs::LoadGuestU32(base, self);
    if (vtable == refii::guest::Address(0x820CDE18u)) {
        g_pause_player_two.store(value == 1u ? 1u : 0u, std::memory_order_relaxed);
    } else if (vtable == refii::guest::Address(0x820CF4BCu)) {
        g_abilities_player_two.store(value != 0u ? 1u : 0u, std::memory_order_relaxed);
    }
}

REX_EXTERN(rex_lhGuiAbilitiesScreen_Enter) {
    REX_FUNC_PROLOGUE();
    __imp__rex_lhGuiAbilitiesScreen_Enter(ctx, base);
    g_abilities_player_two.store(g_pause_player_two.load(std::memory_order_relaxed), std::memory_order_relaxed);
}

REX_EXTERN(rex_lhGuiScreenSet_ShowRoot) {
    REX_FUNC_PROLOGUE();
    refii_bridge_stated_screens_up(1u);  // the GUI's init makes the set and files its root
    g_pause_player_two.store(0u, std::memory_order_relaxed);  // and constructs its handlers
    g_abilities_player_two.store(0u, std::memory_order_relaxed);
    const int32_t outer = t_loading;
    t_loading = 0;
    __imp__rex_lhGuiScreenSet_ShowRoot(ctx, base);
    t_loading = outer;
}

REX_EXTERN(rex_lhGuiScreenSet_Unload) {
    REX_FUNC_PROLOGUE();
    const uint32_t id = refii::guest::ScreenIdToTu1(ctx.r4.u32);
    __imp__rex_lhGuiScreenSet_Unload(ctx, base);
    if (t_exiting > 0 && id == kLastScreenTu1) {
        refii_bridge_stated_screen(kNoScreen);
    }
}

// SPELL ASSIGNMENT ON THE ABILITIES SCREEN. `sub_826C2480(screen, player)` (disc `sub_8259E430`)
// enters it and `sub_826C25C0(screen)` (disc `sub_8259E570`) leaves it: the only writers of the
// mode byte besides the constructor (`*(a1 + 620) = 1` 0x826c24c0, `= 0` 0x826c2600).
REX_EXTERN(rex_lhGuiAbilitiesScreen_EnterAssign) {
    REX_FUNC_PROLOGUE();
    refii_bridge_stated_value(kAbilitiesAssignMode, 1u);
    __imp__rex_lhGuiAbilitiesScreen_EnterAssign(ctx, base);
}

REX_EXTERN(rex_lhGuiAbilitiesScreen_ExitAssign) {
    REX_FUNC_PROLOGUE();
    __imp__rex_lhGuiAbilitiesScreen_ExitAssign(ctx, base);
    refii_bridge_stated_value(kAbilitiesAssignMode, 0u);
}

// THE GAME-FLOW COMMAND STACK. `GameFlow_QueueCommand(flow, command)` (0x82318718, disc
// 0x8233A198) pushes a command on top; `sub_82318800(flow, type)` (disc 0x8233A280) removes the
// topmost command of that type. The top after either is stated.
REX_EXTERN(rex_GameFlow_QueueCommand) {
    REX_FUNC_PROLOGUE();
    const uint32_t flow = ctx.r3.u32;
    __imp__rex_GameFlow_QueueCommand(ctx, base);
    StateFlowTop(base, flow);
}

REX_EXTERN(rex_GameFlow_PopCommand) {
    REX_FUNC_PROLOGUE();
    const uint32_t flow = ctx.r3.u32;
    __imp__rex_GameFlow_PopCommand(ctx, base);
    StateFlowTop(base, flow);
}
