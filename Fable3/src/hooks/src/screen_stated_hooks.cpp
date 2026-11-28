// refii - Fable 3's screens and HUD widgets stated at the calls that carry their values, for
// Godot's Fable3UiStatements: the HUD root's shown/hidden, the save menu, the Options screen's own
// query, the money alert, the target names, the crime timer, the job
// multiplier and the box-manager screens' rows and records. Each of these writers puts its value
// on screen only behind a presentation (`*H` for the HUD, the box's sub_829588C8 for a box, the
// save menu's cached +20) — the statement is the value as the caller passed it, at the call.
//
// Every address is the Fable 3 disc IDB (port 13339); each offset names its base. GUI =
// *0x83596F84, H = *(GUI+0x20) (the HUD), *H its presentation.
//
// THE HUD ROOT. hud.bgf's root (`Scene`) is written by three calls, none of them the interaction
// mode's tick sub_825EE908 (that one writes InteractionMode.bgf's root, IM+0): HIDE sub_824CD7E8(H)
// sets 2 unless already 2 (GetState 0x824CD81C, SetState 0x824CD83C) — every box's Show calls it;
// SHOW sub_824CD860(force) sets 1 unless already 1 and not forced (0x824CD88C..0x824CD8C0) — the
// boxes' hides, the HUD tick's enable (0x8225905C, forced), the cutscene rules (disc 0x8249AA84) and
// GUI.HideSideInfoPanel (disc 0x8251EF4C); the CUTSCENE RULES sub_823A5558(rules, a2, a3) end by
// setting 3 whenever their word `*rules` keeps bit 27 (0x08000000: `extrwi r10,r11,1,4`
// disc 0x8249B6A8, `li r5,3` / SetState disc 0x8249B6D8 / disc 0x8249B6DC). `@Hud.Root(state)` on each return.
//
// THE SAVE MENU (*(SM+56), SM = *(GUI+0x1C)): open sub_829B4E18(menu, allowed, entity) stores
// +0 = 1 (0x829B4EF0), +1 = allowed (0x829B4F20) and the cursor +12 = allowed ? 1 : 3
// (0x829B4F3C / 0x829B4F48) before its presentation block, and may refuse (0x829B4ECC); every
// cursor change funnels into sub_829B5350(menu) with the row at +12 (0x829B535C: 1 Save, 2 Save
// and Quit, 3 Quit, 4 Character Management); close sub_829B50A0(menu) clears +0 (0x829B50BC).
// `@SaveMenu.Open(allowed, row)`, `@SaveMenu.Row(row)`, `@SaveMenu.Close`.
//
// THE OPTIONS QUERY sub_825F6D30(box, tag) is options_hooks.cpp's (re-expressed there).
//
// THE MONEY ALERT A = *(H+120) player one, *(H+124) player two (sub_824CDC88): change
// sub_8287C2C8(A, old, delta, kingdom) — queued while A+0 is busy (0x8287C2D4 -> A+60);
// show sub_8287BFD8(A, amount, kingdom); hide sub_8287C768(A). The running count is the tick's.
//
// THE TARGET NAMES TN = *(H+112) / *(H+116): show sub_82604EF8(TN, target, viewer, keep) and clear
// sub_82605308(TN). THE CRIME TIMER CT = *(H+104): fine sub_8260C4D0(CT, fine) and seconds
// sub_8260C568(CT, secs), both formatted and committed only behind CT's elements and *H. THE JOB
// MULTIPLIER J: sub_82973E00(J, level, LHString* prefix) — J+16 is the level shown (0x82973E20).
//
// THE BOX SCREENS. Quest summary rows sub_829BCA18(box, row 0..6, quest*, selected, isLast)
// (0x829BC904; refuses rows outside 0..6, 0x829BCA3C..0x829BCA44); the stats page sub_829B73B0(box,
// page) / disc sub_8296EC88, which returns at once on the page it is on (`lwz r11,0x34(r3)` / `cmpw
// cr6,r11,r4` 0x829B73C4..0x829B73D0) — stated as asked, r4, every time; the promise ctor
// sub_827DA738(box, record, tag, accepted) / disc sub_827A3290, stated from r5 and r6 at its entry
// (`mr r29,r5` / `mr r28,r6` 0x827DA758..0x827DA75C / 0x827A32B0..0x827A32B4, stored at +72 / +77).
//
// THE SCREENS' CURSORS AND CHOICES are Godot's: no screen's pad handler is stated at its return
// (FB7: chat, wedding, promise, stats, hero status, schedule, quest summary, display_box_hooks.cpp;
// FB8: join / visibility, friends, partners in session_boxes.cpp; divorce, bed, treasury, proposal,
// food in family_boxes.cpp).

#include <atomic>
#include <cstdint>
#include <mutex>
#include <string>

#include <rex/ppc.h>

#include "box_answers.h"
#include "gui_statements.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_Hud_Hide);
REX_EXTERN(__imp__rex_Hud_Show);
REX_EXTERN(__imp__rex_CutsceneRules_Apply);
REX_EXTERN(__imp__rex_SaveMenu_Open);
REX_EXTERN(__imp__rex_SaveMenu_Row);
REX_EXTERN(__imp__rex_SaveMenu_Close);
REX_EXTERN(__imp__rex_MoneyAlert_Change);
REX_EXTERN(__imp__rex_MoneyAlert_Show);
REX_EXTERN(__imp__rex_MoneyAlert_Hide);
REX_EXTERN(__imp__rex_TargetNames_Show);
REX_EXTERN(__imp__rex_TargetNames_Clear);
REX_EXTERN(__imp__rex_CrimeTimer_Fine);
REX_EXTERN(__imp__rex_CrimeTimer_Seconds);
REX_EXTERN(__imp__rex_JobMultiplier_Set);
REX_EXTERN(__imp__rex_QuestSummary_Row);
REX_EXTERN(__imp__rex_StatsBox_Page);
REX_EXTERN(__imp__rex_PromiseBox_Ctor);
REX_EXTERN(__imp__rex_PartnersBox_Row);

namespace {

using namespace refii::fable3;

const refii::guest::Addr kGuiManager{0x83596F84u};
constexpr uint32_t kGuiHud = 0x20u;
constexpr uint32_t kHudMoneyOne = 120u;
constexpr uint32_t kHudMoneyTwo = 124u;
constexpr uint32_t kHudTargetsOne = 112u;
constexpr uint32_t kHudTargetsTwo = 116u;
constexpr uint32_t kCutsceneHudBit = 0x08000000u;

constexpr uint32_t kSaveMenuOpen = 0u;
constexpr uint32_t kSaveMenuAllowed = 1u;
constexpr uint32_t kSaveMenuRow = 12u;

constexpr uint32_t kMoneyBusy = 0u;

std::atomic<int32_t> g_hud_root{0};
std::atomic<int32_t> g_save_row{0};
/// The save menu its open last stated (the row lever's target, save_menu_row.cpp); 0 once closed or freed.
std::atomic<uint32_t> g_save_menu{0u};
std::atomic<int64_t> g_crime_fine{-1};
std::atomic<int64_t> g_crime_seconds{-1};

uint32_t HudWord(const uint8_t* base, uint32_t offset) {
  const uint32_t gui = LoadGuestU32(base, kGuiManager);
  const uint32_t hud = Plausible(gui) ? LoadGuestU32(base, gui + kGuiHud) : 0u;
  return Plausible(hud) ? LoadGuestU32(base, hud + offset) : 0u;
}

/// 0 or 1 for the widget of player one or two, -1 for neither.
int32_t PlayerOf(const uint8_t* base, uint32_t widget, uint32_t one, uint32_t two) {
  if (base == nullptr || widget == 0u) {
    return -1;
  }
  if (widget == HudWord(base, one)) {
    return 0;
  }
  return widget == HudWord(base, two) ? 1 : -1;
}

void StateRoot(int32_t state, const char* via) {
  if (g_hud_root.exchange(state) == state) {
    return;
  }
  Statement("@Hud.Root").Add(Number(static_cast<float>(state))).Add(Text(via)).Post();
}

Statement& AddInt(Statement& statement, uint32_t value) {
  return statement.Add(Number(static_cast<float>(static_cast<int32_t>(value))));
}

}  // namespace

REX_EXTERN(rex_Hud_Hide) {
  __imp__rex_Hud_Hide(ctx, base);
  StateRoot(2, "825C1CB0");
}

REX_EXTERN(rex_Hud_Show) {
  __imp__rex_Hud_Show(ctx, base);
  StateRoot(1, "825C1D28");
}

/// sub_823A5558(rules, a2, a3): state 3 when the rules' word keeps bit 27 on return.
REX_EXTERN(rex_CutsceneRules_Apply) {
  const uint32_t rules = ctx.r3.u32;
  __imp__rex_CutsceneRules_Apply(ctx, base);
  if (base != nullptr && Plausible(rules) && (LoadGuestU32(base, rules) & kCutsceneHudBit) != 0u) {
    StateRoot(3, "8249A030");
  }
}

REX_EXTERN(rex_SaveMenu_Open) {
  const uint32_t menu = ctx.r3.u32;
  __imp__rex_SaveMenu_Open(ctx, base);
  if (base == nullptr || !Plausible(menu) || base[menu + kSaveMenuOpen] == 0u) {
    return;
  }
  const int32_t row = static_cast<int32_t>(LoadGuestU32(base, menu + kSaveMenuRow));
  g_save_row.store(row);
  g_save_menu.store(menu);
  Statement("@SaveMenu.Open")
      .Add(Boolean(base[menu + kSaveMenuAllowed] != 0u))
      .Add(Number(static_cast<float>(row)))
      .Post();
}

REX_EXTERN(rex_SaveMenu_Row) {
  if (base != nullptr && Plausible(ctx.r3.u32)) {
    const int32_t row = static_cast<int32_t>(LoadGuestU32(base, ctx.r3.u32 + kSaveMenuRow));
    if (g_save_row.exchange(row) != row) {
      Statement("@SaveMenu.Row").Add(Number(static_cast<float>(row))).Post();
    }
  }
  __imp__rex_SaveMenu_Row(ctx, base);
}

REX_EXTERN(rex_SaveMenu_Close) {
  const bool open = base != nullptr && Plausible(ctx.r3.u32) && base[ctx.r3.u32 + kSaveMenuOpen] != 0u;
  __imp__rex_SaveMenu_Close(ctx, base);
  if (open) {
    g_save_row.store(0);
    g_save_menu.store(0u);
    Statement("@SaveMenu.Close").Post();
  }
}

/// sub_8287C2C8(A, old, delta, kingdom).
REX_EXTERN(rex_MoneyAlert_Change) {
  const int32_t player = PlayerOf(base, ctx.r3.u32, kHudMoneyOne, kHudMoneyTwo);
  const bool busy = base != nullptr && Plausible(ctx.r3.u32) && base[ctx.r3.u32 + kMoneyBusy] != 0u;
  Statement statement("@Hud.Money.Change");
  statement.Add(Number(static_cast<float>(player)));
  AddInt(statement, ctx.r4.u32);
  AddInt(statement, ctx.r5.u32);
  statement.Add(Boolean((ctx.r6.u32 & 0xFFu) != 0u)).Add(Boolean(busy)).Post();
  __imp__rex_MoneyAlert_Change(ctx, base);
}

/// sub_8287BFD8(A, amount, kingdom).
REX_EXTERN(rex_MoneyAlert_Show) {
  const int32_t player = PlayerOf(base, ctx.r3.u32, kHudMoneyOne, kHudMoneyTwo);
  Statement statement("@Hud.Money.Show");
  statement.Add(Number(static_cast<float>(player)));
  AddInt(statement, ctx.r4.u32);
  statement.Add(Boolean((ctx.r5.u32 & 0xFFu) != 0u)).Post();
  __imp__rex_MoneyAlert_Show(ctx, base);
}

REX_EXTERN(rex_MoneyAlert_Hide) {
  const int32_t player = PlayerOf(base, ctx.r3.u32, kHudMoneyOne, kHudMoneyTwo);
  Statement("@Hud.Money.Hide").Add(Number(static_cast<float>(player))).Post();
  __imp__rex_MoneyAlert_Hide(ctx, base);
}

/// sub_82604EF8(TN, target, viewer, keep).
REX_EXTERN(rex_TargetNames_Show) {
  const int32_t player = PlayerOf(base, ctx.r3.u32, kHudTargetsOne, kHudTargetsTwo);
  Statement("@Hud.Target.Show")
      .Add(Number(static_cast<float>(player)))
      .Add(Text(Hex(ctx.r4.u32)))
      .Add(Text(Hex(ctx.r5.u32)))
      .Add(Boolean((ctx.r6.u32 & 0xFFu) != 0u))
      .Post();
  __imp__rex_TargetNames_Show(ctx, base);
}

REX_EXTERN(rex_TargetNames_Clear) {
  const int32_t player = PlayerOf(base, ctx.r3.u32, kHudTargetsOne, kHudTargetsTwo);
  Statement("@Hud.Target.Clear").Add(Number(static_cast<float>(player))).Post();
  __imp__rex_TargetNames_Clear(ctx, base);
}

REX_EXTERN(rex_CrimeTimer_Fine) {
  const int64_t fine = static_cast<int32_t>(ctx.r4.u32);
  if (g_crime_fine.exchange(fine) != fine) {
    Statement("@Hud.Crime.Fine").Add(Number(static_cast<float>(fine))).Post();
  }
  __imp__rex_CrimeTimer_Fine(ctx, base);
}

REX_EXTERN(rex_CrimeTimer_Seconds) {
  const int64_t seconds = static_cast<int32_t>(ctx.r4.u32);
  if (g_crime_seconds.exchange(seconds) != seconds) {
    Statement("@Hud.Crime.Seconds").Add(Number(static_cast<float>(seconds))).Post();
  }
  __imp__rex_CrimeTimer_Seconds(ctx, base);
}

/// THE CRIME TIMER SHOWN OR HIDDEN. CT = `*(H+0x68)`; CT+4 the root `CrimeTimer` (dword_83599C6C,
/// resolved in the HUD at 0x8260C420). Every writer stores CT+0 = 2 shown / 1 hidden and then sets
/// the root to the same slide (hud.bgf CrimeTimer slide 2 "show", 1 "hide") behind the root's null
/// test — so the store is the decision. A [[midasm_hook]] on each store, (CT, value):
///   - the crime tick sub_82897980: 0x82897B28 / 0x82897B5C / 0x82897DC8 / 0x82897DEC [r11, r9];
///   - the HUD build sub_8260C3B0: 1 at 0x8260C4B0 [r31, r9] (`li r9,1` 0x8260C4A4);
///   - the partner's packet 369 sub_8260C668(msg, CT): 2 at 0x8260C6C0, 1 at 0x8260C6E8 [r31, r11]
///     (`li r11,2|1` 0x8260C6B8 / 0x8260C6E0; r31 = CT, `mr r31,r4` 0x8260C684);
///   - CECVillageCrimeManager_vf13: 1 at 0x82898658, 2 at 0x8289867C [r11, r9];
///   - the criminal teardown sub_82895F38: 1 at 0x82895FCC [r11, r9];
///   - RemoveCriminal sub_8289B528, the list emptied: 1 at 0x8289B6A8 [r11, r9];
///   - GUI.ShowMiscGUIElements: 2 at 0x8254FCB0 [r11, r9];
///   - the region-change save sub_823326B8: 1 at 0x82332A28 [r11, r26] (`li r26,1` 0x82332A08).
/// Each tick store is made only on a change (`cmpwi`/`beq` before it), the build's always.
/// `@Hud.Crime.Shown(shown)`.
void RefiiF3CrimeTimerShown(PPCRegister& timer, PPCRegister& value) {
  (void)timer;
  Statement("@Hud.Crime.Shown").Add(Boolean(value.u32 == 2u)).Post();
}

/// sub_82973E00(J, level, prefix).
REX_EXTERN(rex_JobMultiplier_Set) {
  if (base != nullptr) {
    Statement statement("@Hud.Job");
    AddInt(statement, ctx.r4.u32);
    statement.Add(Text(ReadLhString(base, ctx.r5.u32))).Post();
  }
  __imp__rex_JobMultiplier_Set(ctx, base);
}

/// The quest summary's owner: its ctor stores the player index of the GUI hero's (else the box
/// entity's) component 37 `*(+0x14)+0x24` (-1 with no +0x14) at `stw r11,0x38(r24)` TU3 0x829BC23C /
/// disc 0x829737D4, the value its input handler compares with r5 (`lwz r11,0x38(r3); cmpw r5,r11`
/// 0x829BD028..0x829BD030). `@Screen.Owner(box, player)`.
void RefiiF3QuestSummaryOwner(PPCRegister& box, PPCRegister& player) {
  Statement("@Screen.Owner").Add(Text(Hex(box.u32))).Add(Number(static_cast<float>(static_cast<int32_t>(player.u32)))).Post();
}

/// sub_829BCA18(box, row, quest, selected, isLast).
REX_EXTERN(rex_QuestSummary_Row) {
  const int32_t row = static_cast<int32_t>(ctx.r4.u32);
  if (row >= 0 && row <= 6) {
    Statement statement("@Screen.QuestRow");
    statement.Add(Text(Hex(ctx.r3.u32)));
    AddInt(statement, ctx.r4.u32);
    statement.Add(Text(Hex(ctx.r5.u32)))
        .Add(Boolean((ctx.r6.u32 & 0xFFu) != 0u))
        .Add(Boolean((ctx.r7.u32 & 0xFFu) != 0u))
        .Post();
  }
  __imp__rex_QuestSummary_Row(ctx, base);
}

/// sub_829B73B0(box, page) / disc sub_8296EC88: `@Screen.StatsPage(box, page)`, the page asked for
/// (the callee ignores the page it is on; Godot does too).
REX_EXTERN(rex_StatsBox_Page) {
  Statement statement("@Screen.StatsPage");
  statement.Add(Text(Hex(ctx.r3.u32)));
  AddInt(statement, ctx.r4.u32).Post();
  __imp__rex_StatsBox_Page(ctx, base);
}

/// sub_827DA738(box, record, tag, accepted) / disc sub_827A3290: `@Screen.Promise(box, tag, accepted)`
/// from r5 and r6 at the ctor's entry.
REX_EXTERN(rex_PromiseBox_Ctor) {
  Statement("@Screen.Promise")
      .Add(Text(Hex(ctx.r3.u32)))
      .Add(Text(Hex(ctx.r5.u32)))
      .Add(Boolean((ctx.r6.u32 & 0xFFu) != 0u))
      .Post();
  __imp__rex_PromiseBox_Ctor(ctx, base);
}

namespace refii::fable3 {

/// hud.bgf loaded into an empty slot (the GUI rebuilt by sub_822D9C80 after the main-game shutdown
/// sub_8234D188): the new root's first write is stated, whatever the old root held.
void ForgetHudRoot() {
  g_hud_root.store(0);
}

/// The save menu freed with its screen manager (display_box_hooks.cpp rex_ScreenManager_Destroy):
/// the next menu's first row write is stated.
void ForgetSaveMenu() {
  g_save_row.store(0);
  g_save_menu.store(0u);
}

uint32_t OpenSaveMenu() {
  return g_save_menu.load();
}

}  // namespace refii::fable3

/// THE BED'S OPENER sub_82623738(queue, a2, a3, offered, group, cycle) keeps r6/r7/r8 in r27/r26/r25
/// (0x82623754..0x8262375C) and stores them at box+56/+57/+58 (`stb r27,0x38(r31)` 0x82623820,
/// 0x82623834, 0x82623838). Offered: the Show halves the Sex row's opacity without it (sub_82B8E810
/// on `BedMenu.Sex` / `.Sex.Title`, 0x82B8DBE0..0x82B8DC04) and the stick never moves onto it
/// (`+56` tested at 0x82B8E458); group picks the Sex title's tag (0x82B8DB98); cycle lets left /
/// right change the Sex option (0x82B8E280 / 0x82B8E39C). A [[midasm_hook]] at 0x82623820.
void RefiiF3BedOpened(PPCRegister& box, PPCRegister& offered, PPCRegister& group, PPCRegister& cycle) {
  Statement("@Screen.Opened")
      .Add(Text(Hex(box.u32)))
      .Add(Text("bed"))
      .Add(Number(static_cast<float>(offered.u32 & 0xFFu)))
      .Add(Number(static_cast<float>(group.u32 & 0xFFu)))
      .Add(Number(static_cast<float>(cycle.u32 & 0xFFu)))
      .Post();
}

/// THE WEDDING PICKER'S CTOR sub_82B90948 stores its three affordable bytes (stack args 34/36/38)
/// at box+88/+89/+90 from r5 / r3 / r10 (`stb` 0x82B90A1C / 0x82B90A20 / 0x82B90A24). A tier
/// books only when its byte is set (sub_82B90EB8, 0x82B91060..0x82B91094) and the Show marks an
/// unaffordable price (dword_8359E824 appended, 0x82B90B84 / 0x82B90C00 / 0x82B90C74). A
/// [[midasm_hook]] at 0x82B90A28, the stores done.
void RefiiF3WeddingOpened(PPCRegister& box, PPCRegister& first, PPCRegister& second, PPCRegister& third) {
  Statement("@Screen.Opened")
      .Add(Text(Hex(box.u32)))
      .Add(Text("wedding"))
      .Add(Number(static_cast<float>(first.u32 & 0xFFu)))
      .Add(Number(static_cast<float>(second.u32 & 0xFFu)))
      .Add(Number(static_cast<float>(third.u32 & 0xFFu)))
      .Post();
}

/// THE PARTNERS' ROW WRITER sub_82B92638(box, CCharString* row path, record): the window row's
/// record as sub_82B92300 passes it (0x82B9253C) after storing the online / joinable results of
/// sub_8238F7E0 / sub_8238F850 at record+43 / +42 (0x82B92388 / 0x82B923A4; a local player's
/// record +42 = 0, +43 = 1, 0x82B92364). +40 is the marriage: the writer states the children
/// only for one (0x82B92888) and the row's slide is +2 without it (0x82B92554..0x82B92578).
/// `@Screen.PartnerRow(box, path, married, online, joinable)`.
REX_EXTERN(rex_PartnersBox_Row) {
  if (base != nullptr && Plausible(ctx.r3.u32) && Plausible(ctx.r5.u32)) {
    const uint32_t record = ctx.r5.u32;
    Statement("@Screen.PartnerRow")
        .Add(Text(Hex(ctx.r3.u32)))
        .Add(Text(ReadCharString(base, ctx.r4.u32)))
        .Add(Boolean(base[record + 40u] != 0u))
        .Add(Boolean(base[record + 43u] != 0u))
        .Add(Boolean(base[record + 42u] != 0u))
        .Post();
  }
  __imp__rex_PartnersBox_Row(ctx, base);
}

/// THE STATS SCREEN'S WINDOWS: each page's writer decides its first row, and the cursor `+0x44` is
/// highlighted where `start + row == cursor`. The three writers are replaced and state their window
/// with their rows (stats_screen_stubs.cpp): `@Screen.StatsWindow(box, page, start, count, rows)`.
namespace {

/// The stats pages' list lengths as their writers stated them, for the box that shows.
std::mutex g_stats_mutex;
uint32_t g_stats_box = 0u;
uint32_t g_stats_counts[3] = {0u, 0u, 0u};

}  // namespace

namespace refii::fable3 {

void StateStatsWindow(uint32_t box, int32_t page, uint32_t start, uint32_t count, int32_t rows) {
  {
    std::lock_guard<std::mutex> lock(g_stats_mutex);
    if (g_stats_box != box) {
      g_stats_box = box;
      g_stats_counts[0] = g_stats_counts[1] = g_stats_counts[2] = 0u;
    }
    g_stats_counts[page - 2] = count;
  }
  Statement statement("@Screen.StatsWindow");
  statement.Add(Text(Hex(box))).Add(Number(static_cast<float>(page)));
  AddInt(statement, start);
  AddInt(statement, count);
  statement.Add(Number(static_cast<float>(rows))).Post();
}

}  // namespace refii::fable3

namespace refii::fable3 {

uint32_t StatsRowCount(uint32_t box, int32_t page) {
  std::lock_guard<std::mutex> lock(g_stats_mutex);
  return box == g_stats_box && page >= 2 && page <= 4 ? g_stats_counts[page - 2] : 0u;
}

}  // namespace refii::fable3
