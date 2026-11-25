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
// THE OPTIONS QUERY sub_825F6D30(box, tag): root state 6, the question by tag into
// `.Group.QueryBox.Text` (dword_835995E8, sub_82877030 at 0x825F6D78) and box+30 = 1
// (0x825F6D80) — all behind the box's presentation (sub_829588C8 at 0x825F6D44, beq 0x825F6D50).
// The question is r4: `@Options.Query(box, tag)`.
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
// page), which stores +0x34 before its presentation lookup and returns at once on the same page
// (0x829B73C4..0x829B73D8); the king's schedule ctor sub_829BB7C0(box, table) -> day +0x1C
// (0x829BB858); the promise ctor sub_827DA738(box, entity, tag, accepted) -> +72 tag (0x827DA79C),
// +77 accepted (0x827DA7A8).
//
// THE SCREENS' CURSORS AND CHOICES live in the box and move only inside its pad handler — no call
// carries them, and every element write of them is behind the presentation — so each handler is
// stated at its RETURN as the fields it moved, `@Screen.Fields(box, kind, false, fields…)` when
// they changed, and at the Show's return (display_box_hooks.cpp, StateScreenShown) as the fields it
// starts on, `@Screen.Fields(box, kind, true, fields…)`. The handlers run on box+12 alone (set by
// Show before its presentation gate), so they run headless. Join / visibility (CSimpleOnlineOptionsBox_HandleInput sub_829BF5E0): the type
// +48 (18 join / 19 visibility, `stw r28,0x30` 0x82551870) and the cursor +52 (`stw r9,0x34(r29)`
// 0x829BF908). Chat (COrbChatBox_HandleInput sub_829BB230): the working mute byte +36
// (`stb r9,0x24(r31)` 0x829BB690). Friends (sub_829C0600): the cursor +36 (`stw r9,0x24(r29)`
// 0x829C081C). Treasury (CMiniStatementBox_HandleInput sub_82B8F068): the page +28 (`stw
// r11,0x1C(r3)` 0x82B8F348), the direction byte +32 (`stb r9,0x20(r29)` 0x82B8F22C), the amount
// widget+16 = +56 (the widget at +40, sub_82B8B3E0 at 0x82B8F2F0). Bed
// (CBedInteractionMessageBox_HandleInput sub_82B8DFA0): the row +44, sleep +48, sex +52 (0x82B8E414,
// 0x82B8E244, 0x82B8E298). Wedding (CWeddingPickerBox_HandleInput sub_82B90EB8): the tier +68
// (0x82B90F94). Partners (CMultiplayerPartnersBox_HandleInput sub_82B916C0): the cursor +56 and
// scroll +60 (0x82B91864 / 0x82B91880). Divorce (CDivorceMessageBox_HandleInput sub_8297F5A8):
// the reason byte +136 (0x8297F704) and the Show's page mode +172 (sub_8297F0D8 at 0x8297F19C:
// 0/3 the reason page sub_8297F8D8, 1/4 sub_8297FBC0, 2/5 sub_8297FFC0). Promise
// (sub_827DAB30): the signed byte +77 (A while unsigned, `+77 = 1` 0x827DAC1C). Stats
// (sub_829B6158): the page +52 (sub_829B73B0), the cursor +68 every page highlights (categories
// 0x829B7558, statistics 0x829B7C18), the category +72 (`a1[18] = a1[17]` 0x829B63B0) and the
// friends' row +76 (0x829B67C8).

#include <atomic>
#include <cstdint>
#include <initializer_list>
#include <mutex>
#include <string>
#include <unordered_map>

#include <rex/ppc.h>

#include "gui_statements.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_Hud_Hide);
REX_EXTERN(__imp__rex_Hud_Show);
REX_EXTERN(__imp__rex_CutsceneRules_Apply);
REX_EXTERN(__imp__rex_SaveMenu_Open);
REX_EXTERN(__imp__rex_SaveMenu_Row);
REX_EXTERN(__imp__rex_SaveMenu_Close);
REX_EXTERN(__imp__rex_OptionsBox_Query);
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
REX_EXTERN(__imp__rex_KingsSchedule_Ctor);
REX_EXTERN(__imp__rex_PromiseBox_Ctor);
REX_EXTERN(__imp__rex_JoinOptionsBox_HandleInput);
REX_EXTERN(__imp__rex_OrbChatBox_HandleInput);
REX_EXTERN(__imp__rex_FriendsChannelBox_HandleInput);
REX_EXTERN(__imp__rex_MiniStatementBox_HandleInput);
REX_EXTERN(__imp__rex_BedBox_HandleInput);
REX_EXTERN(__imp__rex_WeddingPickerBox_HandleInput);
REX_EXTERN(__imp__rex_PartnersBox_HandleInput);
REX_EXTERN(__imp__rex_DivorceBox_HandleInput);
REX_EXTERN(__imp__rex_PromiseBox_HandleInput);
REX_EXTERN(__imp__rex_StatsBox_HandleInput);
REX_EXTERN(__imp__rex_PartnersBox_Row);
REX_EXTERN(__imp__rex_KingsSchedule_Row);

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
constexpr uint32_t kScheduleDay = 0x1Cu;
constexpr uint32_t kPromiseTag = 72u;
constexpr uint32_t kPromiseAccepted = 77u;

std::atomic<int32_t> g_hud_root{0};
std::atomic<int32_t> g_save_row{0};
std::atomic<int64_t> g_crime_fine{-1};
std::atomic<int64_t> g_crime_seconds{-1};

/// The fields a screen's Show or handler left, by box: a handler's posted when any changed, a
/// Show's always (a box address is reused by the next box).
std::mutex g_fields_mutex;
std::unordered_map<uint32_t, uint64_t> g_fields;

/// `@Screen.Fields(box, kind, shown, values…)`: `shown` for the Show's return, else a handler's.
void StateFields(const uint8_t* base, uint32_t box, const char* kind, std::initializer_list<uint32_t> values,
                 bool shown = false) {
  if (base == nullptr || !Plausible(box)) {
    return;
  }
  uint64_t value = 0xcbf29ce484222325ull;
  for (uint32_t field : values) {
    value = (value ^ field) * 0x100000001b3ull;
  }
  {
    std::lock_guard<std::mutex> lock(g_fields_mutex);
    if (g_fields.size() > 256u) {
      g_fields.clear();
    }
    auto [it, inserted] = g_fields.emplace(box, value);
    if (!shown && !inserted && it->second == value) {
      return;
    }
    it->second = value;
  }
  Statement statement("@Screen.Fields");
  statement.Add(Text(Hex(box))).Add(Text(kind)).Add(Boolean(shown));
  for (uint32_t field : values) {
    statement.Add(Number(static_cast<float>(static_cast<int32_t>(field))));
  }
  statement.Post();
}

/// Each screen's cursor and choice fields (offsets from the box; the file header names each
/// field's writer), under the kind Godot's Fable3ScreenStatements keys them by.
void StateKindFields(const uint8_t* base, uint32_t box, const char* kind, bool shown) {
  const std::string name = kind != nullptr ? kind : "";
  auto word = [&](uint32_t offset) { return LoadGuestU32(base, box + offset); };
  auto byte = [&](uint32_t offset) { return static_cast<uint32_t>(base[box + offset]); };
  if (name == "screen:join" || name == "screen:visibility") {
    StateFields(base, box, "join", {word(0x30u), word(0x34u)}, shown);
  } else if (name == "screen:chat") {
    StateFields(base, box, "chat", {byte(0x24u)}, shown);
  } else if (name == "screen:friends") {
    StateFields(base, box, "friends", {word(0x24u)}, shown);
  } else if (name == "screen:treasury") {
    StateFields(base, box, "treasury", {word(0x1Cu), byte(0x20u), word(0x38u)}, shown);
  } else if (name == "screen:bed") {
    StateFields(base, box, "bed", {word(0x2Cu), word(0x30u), word(0x34u)}, shown);
  } else if (name == "screen:wedding") {
    StateFields(base, box, "wedding", {word(0x44u)}, shown);
  } else if (name == "screen:partners") {
    StateFields(base, box, "partners", {word(0x38u), word(0x3Cu)}, shown);
  } else if (name == "screen:divorce") {
    StateFields(base, box, "divorce", {byte(0x88u), word(0xACu)}, shown);
  } else if (name == "screen:promises") {
    StateFields(base, box, "promise", {byte(0x4Du)}, shown);
  } else if (name == "screen:stats") {
    StateFields(base, box, "stats", {word(0x34u), word(0x44u), word(0x48u), word(0x4Cu)}, shown);
  }
}

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
    Statement("@SaveMenu.Close").Post();
  }
}

REX_EXTERN(rex_OptionsBox_Query) {
  Statement("@Options.Query").Add(Text(Hex(ctx.r3.u32))).Add(Text(Hex(ctx.r4.u32))).Post();
  __imp__rex_OptionsBox_Query(ctx, base);
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

/// sub_829B73B0(box, page): stated when it moves the page.
REX_EXTERN(rex_StatsBox_Page) {
  const uint32_t box = ctx.r3.u32;
  const uint32_t page = ctx.r4.u32;
  const bool moves = base != nullptr && Plausible(box) && LoadGuestU32(base, box + 0x34u) != page;
  if (moves) {
    Statement statement("@Screen.StatsPage");
    statement.Add(Text(Hex(box)));
    AddInt(statement, page).Post();
  }
  __imp__rex_StatsBox_Page(ctx, base);
}

REX_EXTERN(rex_KingsSchedule_Ctor) {
  const uint32_t box = ctx.r3.u32;
  __imp__rex_KingsSchedule_Ctor(ctx, base);
  if (base != nullptr && Plausible(box)) {
    Statement statement("@Screen.Schedule");
    statement.Add(Text(Hex(box)));
    AddInt(statement, LoadGuestU32(base, box + kScheduleDay)).Post();
  }
}

REX_EXTERN(rex_PromiseBox_Ctor) {
  const uint32_t box = ctx.r3.u32;
  __imp__rex_PromiseBox_Ctor(ctx, base);
  if (base != nullptr && Plausible(box)) {
    Statement("@Screen.Promise")
        .Add(Text(Hex(box)))
        .Add(Text(Hex(LoadGuestU32(base, box + kPromiseTag))))
        .Add(Boolean(base[box + kPromiseAccepted] != 0u))
        .Post();
  }
}

// The screens' pad handlers (box, ev*, player): their moved fields at return.
#define REFII_F3_SCREEN_HANDLER(hook, kind)              \
  REX_EXTERN(rex_##hook) {                                \
    const uint32_t box = ctx.r3.u32;                      \
    __imp__rex_##hook(ctx, base);                         \
    if (base != nullptr && Plausible(box)) {              \
      StateKindFields(base, box, kind, false);            \
    }                                                     \
  }

REFII_F3_SCREEN_HANDLER(JoinOptionsBox_HandleInput, "screen:join")
REFII_F3_SCREEN_HANDLER(OrbChatBox_HandleInput, "screen:chat")
REFII_F3_SCREEN_HANDLER(FriendsChannelBox_HandleInput, "screen:friends")
REFII_F3_SCREEN_HANDLER(MiniStatementBox_HandleInput, "screen:treasury")
REFII_F3_SCREEN_HANDLER(BedBox_HandleInput, "screen:bed")
REFII_F3_SCREEN_HANDLER(WeddingPickerBox_HandleInput, "screen:wedding")
REFII_F3_SCREEN_HANDLER(PartnersBox_HandleInput, "screen:partners")
REFII_F3_SCREEN_HANDLER(DivorceBox_HandleInput, "screen:divorce")
REFII_F3_SCREEN_HANDLER(PromiseBox_HandleInput, "screen:promises")
REFII_F3_SCREEN_HANDLER(StatsBox_HandleInput, "screen:stats")

#undef REFII_F3_SCREEN_HANDLER

namespace refii::fable3 {

/// A box-manager screen's fields as its Show left them (display_box_hooks.cpp, after the queue
/// tick's Show): the cursor each Show starts on — join/visibility's saved setting (sub_829BFAC0),
/// chat's saved mute (0x829BAE34), friends' 1 (disc 0x82977A64), the ctor's treasury page and bed row.
void StateScreenShown(const uint8_t* base, uint32_t box, const char* kind) {
  if (base != nullptr && Plausible(box)) {
    StateKindFields(base, box, kind, true);
  }
}

/// hud.bgf loaded into an empty slot (the GUI rebuilt by sub_822D9C80 after the main-game shutdown
/// sub_8234D188): the new root's first write is stated, whatever the old root held.
void ForgetHudRoot() {
  g_hud_root.store(0);
}

/// The save menu freed with its screen manager (display_box_hooks.cpp rex_ScreenManager_Destroy):
/// the next menu's first row write is stated.
void ForgetSaveMenu() {
  g_save_row.store(0);
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

/// THE KING'S SCHEDULE ROW sub_829BBD90(box, row 1..4, tick slide, tag): the Show's call per row
/// (0x829BBCD8) — the table's tick for a listed task, 4 and tag 0 past the day's tasks
/// (0x829BBCCC / 0x829BBCD0); the row writer sets the tag on `TaskN` and the tick on `TickBoxN`
/// (0x829BBE48 / 0x829BBE58). `@Screen.ScheduleRow(box, row, tick, tag)`.
REX_EXTERN(rex_KingsSchedule_Row) {
  const int32_t row = static_cast<int32_t>(ctx.r4.u32);
  if (row >= 1 && row <= 4) {
    Statement statement("@Screen.ScheduleRow");
    statement.Add(Text(Hex(ctx.r3.u32)));
    AddInt(statement, ctx.r4.u32);
    AddInt(statement, ctx.r5.u32);
    statement.Add(Text(Hex(ctx.r6.u32))).Post();
  }
  __imp__rex_KingsSchedule_Row(ctx, base);
}

/// THE STATS SCREEN'S WINDOWS: each page's writer decides its first row, and the cursor `+68` is
/// highlighted where `start + row == cursor`. `@Screen.StatsWindow(box, page, start, count, rows)`
/// at the instruction after the decision:
///   - categories sub_829B7458: count = the categories / 20 (`divw r7` 0x829B7480); start r24 = 0, or
///     cursor - 8 when count > 8 and cursor >= 8 (0x829B7484..0x829B749C); rows 0..7 (`cmplwi r28,8`
///     0x829B75F8). Midasm at 0x829B74A4 (box r27).
///   - statistics sub_829B76A0: count r30 (0x829B78CC); rows r27 = 6 when the profile is online and
///     listed (disc 0x8296F1C8..disc 0x8296F1D4), else 9 (disc 0x8296F1E8); start r31 = 0, or cursor - rows + 1
///     when count > rows and cursor >= rows (0x829B790C..0x829B792C). Midasm at 0x829B7930 (box r28).
///   - friends sub_829B8FC0: count r31 (stored at box+80, 0x829B9268); start r10 = 0, or cursor - 8
///     when count > 9 and cursor >= 9 (0x829B9264..0x829B927C); rows 0..8. Midasm at 0x829B9280
///     (box r28).
namespace {

void StateStatsWindow(uint32_t box, int32_t page, uint32_t start, uint32_t count, int32_t rows) {
  Statement statement("@Screen.StatsWindow");
  statement.Add(Text(Hex(box))).Add(Number(static_cast<float>(page)));
  AddInt(statement, start);
  AddInt(statement, count);
  statement.Add(Number(static_cast<float>(rows))).Post();
}

}  // namespace

void RefiiF3StatsCategories(PPCRegister& box, PPCRegister& start, PPCRegister& count) {
  StateStatsWindow(box.u32, 2, start.u32, count.u32, 8);
}

void RefiiF3StatsStatistics(PPCRegister& box, PPCRegister& start, PPCRegister& count, PPCRegister& rows) {
  StateStatsWindow(box.u32, 3, start.u32, count.u32, static_cast<int32_t>(rows.u32));
}

void RefiiF3StatsFriends(PPCRegister& box, PPCRegister& start, PPCRegister& count) {
  StateStatsWindow(box.u32, 4, start.u32, count.u32, 9);
}
