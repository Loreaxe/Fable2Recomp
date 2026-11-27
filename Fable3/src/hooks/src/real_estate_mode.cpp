// refii - the real-estate mode (interaction modes 6 and 7) stated at its own calls, its button
// show cut, and Godot's row lever `realestate:<p>:row:<id>`; the crime mode's rows, pushed through
// the same push.
//
// THE MODE CInteractionModeRealEstate, vtable TU3 0x820D54C8 / disc 0x820D2858, 16 slots. Mode
// fields +0..+40 are the same on both builds; from +44 up the disc's are TU3 + 72. Each replaced or
// wrapped body sits in this vtable alone (an aligned word scan of .rdata / .data finds it once:
// TU3 0x820D54D0 vf2, 0x820D54E4 vf7, 0x820D54E8 vf8, 0x820D54F4 vf11; disc 0x820D2860, 0x820D2874,
// 0x820D2878, 0x820D2884).
//   vf1 0x82B55DC0 / 0x82B199C8 open: +24 = 1, vf8 (`lwz r10,0x20(r11)` slot 8), the countdown +0x70 /
//       +0xB8 = 3 (5 offline), the panel sub_82B57178 / sub_82B1ADF8, the money alert's show
//       (rex_MoneyAlert_Show) and the target's script rules sub_8296FA90(mode, 0) / sub_82960E38.
//       The panel's every text goes through the info pane's writer, stated at its entry
//       (rex_InfoPane_SetText), into the fields BuildingName, OwnerTitle, OwnerName, CoOwnedPath,
//       PriceTitle, Price, MiscTitle1, MiscInfo1, MiscTitle2, MiscInfo2 (the CCharStrings TU3
//       dword_8359E484..dword_8359E4A8 / disc dword_8355D764..dword_8355D788, both builds the same
//       ten). Kept whole.
//   vf2 0x82B55FB0 / 0x82B19BB8 close: the list hide, panel 1, the base close, the money alert's
//       hide, the script rules off (sub_8296FA90(mode, 1)). Kept whole; wrapped: `@RealEstate.Close`.
//   vf4 0x82B5C058 / 0x82B1FBE0 update: kept (its pane tick, vcall+12 on the CInfoPaneTextAssetsBox
//       at +0xB0 / +0xF8, commits the pane's deferred texts into the headless document: those texts
//       were stated at the writer's entry; the pane class is shared, its tick is not cut here).
//   vf7 0x82B5BE30 / 0x82B1F9C0 input: kept for the guest's pad; wrapped for the lever's row.
//   vf8 0x82B570D8 / 0x82B1AD58 shows the document's `Buttons.Button1..4` (dword_8359E474 + n,
//       sub_82877D40 / sub_828777E8(button, pres, 1)): drawing alone. REPLACED: nothing.
//   vf11 0x82B56120 / 0x82B19D28 populate: kept; wrapped: the rows it pushes are stated.
//
// THE ROWS. vf11 pushes each row through sub_82B5E868(vector, row*) / sub_82B2B4C0 (TU3 vector
// mode+0x50, ten call sites 0x82B56330..0x82B56F2C; disc mode+0x98, ten 0x82B19FB8..0x82B1ABA8),
// a push_back of the 12-byte {CCharString tag, CCharString icon, int id} (`sub_82225850` of +0 and
// +4, `stw` of +8, TU3 0x82B5E900..0x82B5E920). The same push is the crime mode's: its vf11
// CInteractionModeMenuCrime_vf11 TU3 0x82B68080 / disc 0x82B2ADC0 (vtable 0x820D5B38 / 0x820D2EF8,
// slot 11 at 0x820D5B64 / 0x820D2F24 only) empties its vector first (`addi r29,r3,0x54` / `bl
// sub_82B68768` 0x82B6808C..0x82B68098; disc `addi r29,r3,0x9C` 0x82B2ADCC) and pushes into it
// (`mr r3,r29` 0x82B68280 / 0x82B68304 / 0x82B6837C; disc 0x82B2AFC0 / 0x82B2B044 / 0x82B2B0BC): id 0
// pay the fine, 1 community service, 2 resist arrest. Each populate's pushes are stated as its own,
// by the populate running and its vector. vf11 never clears the vector; the relist sub_82B58BC0 / sub_82B1C7E0 does (the range
// destroy `bl sub_82B5E938` 0x82B58C30 / `bl sub_82B22110` 0x82B1C850, end = begin), the one other
// writer of its end besides the constructor and the destructor. Its callers are the yes/no poll
// (TU3 0x82B5C78C / 0x82B5C79C, disc 0x82B201BC / 0x82B201C4) and, TU3 only, the mode's net
// handler 392 sub_82B5EA78.
//   `@RealEstate.Row(mode, id, tag, icon)` at each push, `@RealEstate.Listed(mode)` after vf11,
//   `@RealEstate.Rows.Clear(mode)` at the relist's entry, `@RealEstate.Close(mode)` at vf2's;
//   `@Crime.Rows.Clear(mode)` at the crime populate's entry, `@Crime.Row(mode, id, tag, icon)`,
//   `@Crime.Listed(mode)` after it.
//
// THE ROW LEVER. vf7's A (`cmpwi r4,0x4A` TU3 0x82B5BE5C / disc 0x82B1F9F4) plays the select cue
// (sub_823333B0(&h, GUI, &dword_8359BF48) and the handle's release sub_82E02BE8, TU3
// 0x82B5BF90..0x82B5BFAC; disc sub_8232C4A0 / &dword_8355BB48 / sub_82DCC978, 0x82B1FB20..0x82B1FB38),
// takes the id of the row at the list's cursor (`lwz r4,8(r10)` of rows + 12 * cursor, refused
// outside 0..15, TU3 0x82B5BFEC..0x82B5C00C / disc 0x82B1FB70..0x82B1FB90) and calls
// lhGuiRealEstateScreen_OnMenuEvent(mode, id, player) (0x82B5C018 / 0x82B1FB9C). Behind it, while
// the mode's input-blocked byte +0x6B / +0xB3 is clear (TU3 0x82B5BE3C / disc 0x82B1F9D4).
// The lever runs the interaction manager's own input sub_825F0860(IM, ev*, player) / sub_825C58A0
// with action 74 for local player p — its tests (IM+4 open and IM+0, IM+7 clear, the screen
// manager's sub_821A38C8, the save menu's open byte, 0x825F086C..0x825F08E0) and the mode's slot 5
// with its own player gate sub_829704A0 / sub_82961848 (the list base's vf5 -> sub_82970900) are the
// guest's. When that reaches vf7 for this lever, the A arm runs with the lever's id in place of the
// cursor's: the cue, then the row handler (rex_RealEstate_OnMenuEvent, which names its
// confirmations, real_estate_confirm.cpp). Only an id the populate stated for this mode is handed.
// The guest's pad still drives vf7 as before (cursor, A, B, the pane's scroll).

#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <string>
#include <unordered_map>
#include <unordered_set>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "box_answers.h"
#include "gui_statements.h"
#include "mod_seam.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_RealEstate_Close);
REX_EXTERN(__imp__rex_RealEstate_Input);
REX_EXTERN(__imp__rex_RealEstate_Populate);
REX_EXTERN(__imp__rex_RealEstate_PushRow);
REX_EXTERN(__imp__rex_RealEstate_Relist);
REX_EXTERN(__imp__rex_CrimeMode_Populate);
REX_EXTERN(rex_RealEstate_OnMenuEvent);
REX_EXTERN(rex_GuiSound_Play);
REX_EXTERN(rex_SoundHandle_Release);

namespace {

using namespace refii::fable3;

const refii::guest::Addr kGuiManager{0x83596F84u};
/// The interaction manager's input sub_825F0860(IM, ev*, player) / disc sub_825C58A0.
const refii::guest::Addr kImInput{0x825F0860u};
/// SE_GUI_2D_MENU_SELECT, vf7's A cue (`addi r5,r10,dword_8359BF48@l` 0x82B5BF9C).
const refii::guest::Addr kCueSelect{0x8359BF48u};

/// IM = *(GUI+0x48) (`lwz r3,0x48(r11)` TU3 0x8254A524 / disc 0x8251A8A4); its mode object +24.
constexpr uint32_t kGuiIm = 0x48u;
/// The class, by its TU3 vtable (ToTu3 classifies the disc's 0x820D2858).
constexpr uint32_t kVtRealEstate = 0x820D54C8u;
constexpr uint32_t kImObject = 24u;
constexpr uint32_t kActionA = 74u;
constexpr int32_t kLastRowId = 15;

uint32_t RowsVector() { return refii::guest::Pick(0x50u, 0x98u); }
uint32_t CrimeRowsVector() { return refii::guest::Pick(0x54u, 0x9Cu); }
uint32_t InputBlocked() { return refii::guest::Pick(0x6Bu, 0xB3u); }

std::mutex g_mutex;
/// The ids each mode's populate pushed since its last relist.
std::unordered_map<uint32_t, std::unordered_set<int32_t>>* g_ids = nullptr;

std::unordered_map<uint32_t, std::unordered_set<int32_t>>& Ids() {
  if (g_ids == nullptr) {
    g_ids = new std::unordered_map<uint32_t, std::unordered_set<int32_t>>();
  }
  return *g_ids;
}

/// The real-estate populate running on this thread (its mode), and the lever's row id while the
/// lever's action runs the manager's input.
thread_local uint32_t t_populating = 0u;
thread_local uint32_t t_crime_populating = 0u;
thread_local int32_t t_lever_id = -1;
thread_local bool t_lever_taken = false;

void LogOnce(bool& logged, const char* what) {
  if (!logged) {
    logged = true;
    REXLOG_INFO("[fable3-realestate] {}", what);
  }
}

void StoreU32(uint8_t* base, uint32_t address, uint32_t value) {
  const uint32_t swapped = __builtin_bswap32(value);
  std::memcpy(base + address, &swapped, sizeof(swapped));
}

void PlaySelectCue(PPCContext& ctx, uint8_t* base) {
  PPCContext work = seam::Frame(ctx);
  const uint32_t handle = work.r1.u32 + 0x100u;
  work.r3.u64 = handle;
  work.r4.u64 = LoadGuestU32(base, kGuiManager);
  work.r5.u64 = kCueSelect;
  rex_GuiSound_Play(work, base);
  work = seam::Frame(ctx);
  work.r3.u64 = handle;
  rex_SoundHandle_Release(work, base);
}

}  // namespace

namespace refii::fable3 {

bool AnswerRealEstateRow(PPCContext& ctx, uint8_t* base, const char* action) {
  if (base == nullptr || action == nullptr) {
    return false;
  }
  const char* at = action + sizeof(kRealEstateAction) - 1u;
  if ((at[0] != '1' && at[0] != '2') || at[1] != ':' || std::strncmp(at + 2, "row:", 4u) != 0) {
    REXLOG_WARN("[fable3-realestate] '{}' is not realestate:<p>:row:<id>; dropped", action);
    return false;
  }
  const uint32_t player = static_cast<uint32_t>(at[0] - '0');
  char* end = nullptr;
  const long id = std::strtol(at + 6, &end, 10);
  if (end == at + 6 || *end != '\0' || id < 0 || id > kLastRowId) {
    REXLOG_WARN("[fable3-realestate] '{}' names no row id 0..15; dropped", action);
    return false;
  }
  const uint32_t gui = LoadGuestU32(base, kGuiManager);
  const uint32_t im = Plausible(gui) ? LoadGuestU32(base, gui + kGuiIm) : 0u;
  const uint32_t mode = Plausible(im) ? LoadGuestU32(base, im + kImObject) : 0u;
  if (!Plausible(mode) || refii::guest::ToTu3(LoadGuestU32(base, mode)) != kVtRealEstate) {
    REXLOG_WARN("[fable3-realestate] '{}' with no real-estate mode up; dropped", action);
    return false;
  }
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    auto ids = Ids().find(mode);
    if (ids == Ids().end() || ids->second.count(static_cast<int32_t>(id)) == 0u) {
      REXLOG_WARN("[fable3-realestate] '{}': mode {:08X}'s populate stated no row {}; dropped", action, mode, id);
      return false;
    }
  }
  PPCContext work = seam::Frame(ctx);
  const uint32_t event = work.r1.u32 + 0x100u;
  for (uint32_t offset = 0u; offset < 32u; offset += 4u) {
    StoreU32(base, event + offset, 0u);
  }
  StoreU32(base, event, kActionA);
  work.r3.u64 = im;
  work.r4.u64 = event;
  work.r5.u64 = player;
  t_lever_id = static_cast<int32_t>(id);
  t_lever_taken = false;
  const bool called = seam::CallAt(work, base, kImInput);
  const bool taken = t_lever_taken;
  t_lever_id = -1;
  t_lever_taken = false;
  if (!called) {
    REXLOG_WARN("[fable3-realestate] row {} for player {}: the manager's input is not a recompiled function", id,
                player);
  } else if (taken) {
    REXLOG_INFO("[fable3-realestate] row {} for player {}: the mode's A ran it", id, player);
  } else {
    REXLOG_INFO("[fable3-realestate] row {} for player {}: the manager's or the mode's gates took no A (answered {})",
                id, player, work.r3.u32);
  }
  return taken;
}

}  // namespace refii::fable3

/// (mode, ev*, player): the guest's input; for the row lever's A, the A arm with the lever's id.
REX_EXTERN(rex_RealEstate_Input) {
  static bool logged = false;
  LogOnce(logged, "the mode's input takes the row lever's id");
  const uint32_t mode = ctx.r3.u32;
  const uint32_t action = base != nullptr && Plausible(ctx.r4.u32) ? LoadGuestU32(base, ctx.r4.u32) : 0u;
  if (t_lever_id < 0 || action != kActionA || base == nullptr || !Plausible(mode) ||
      base[mode + InputBlocked()] != 0u) {
    __imp__rex_RealEstate_Input(ctx, base);
    return;
  }
  t_lever_taken = true;
  PlaySelectCue(ctx, base);
  PPCContext work = seam::Frame(ctx);
  work.r3.u64 = mode;
  work.r4.u64 = static_cast<uint32_t>(t_lever_id);
  work.r5.u64 = ctx.r5.u32;
  rex_RealEstate_OnMenuEvent(work, base);
  ctx.r3.u64 = 1u;
}

/// vf8: the document's four buttons shown — drawing alone; nothing runs.
REX_EXTERN(rex_RealEstate_ShowButtons) {
  static bool logged = false;
  LogOnce(logged, "the mode's button show is cut (drawing only)");
  (void)ctx;
  (void)base;
}

/// vf11 (mode): the rows it pushes are stated, then `@RealEstate.Listed(mode)`.
REX_EXTERN(rex_RealEstate_Populate) {
  static bool logged = false;
  LogOnce(logged, "the populate states its rows");
  const uint32_t mode = ctx.r3.u32;
  const uint32_t outer = t_populating;
  t_populating = mode;
  __imp__rex_RealEstate_Populate(ctx, base);
  t_populating = outer;
  Statement("@RealEstate.Listed").Add(Text(Hex(mode))).Post();
}

/// (vector, row*): a row the real-estate populate pushes, as the push copies it.
REX_EXTERN(rex_RealEstate_PushRow) {
  static bool logged = false;
  static bool crime_logged = false;
  const uint32_t vector = ctx.r3.u32;
  const uint32_t row = ctx.r4.u32;
  if (t_populating != 0u && base != nullptr && vector == t_populating + RowsVector() && Plausible(row)) {
    LogOnce(logged, "a populate row stated");
    const int32_t id = static_cast<int32_t>(LoadGuestU32(base, row + 8u));
    const std::string tag = ReadCharString(base, row);
    const std::string icon = ReadCharString(base, row + 4u, 260u);
    {
      std::lock_guard<std::mutex> lock(g_mutex);
      Ids()[t_populating].insert(id);
    }
    Statement("@RealEstate.Row")
        .Add(Text(Hex(t_populating)))
        .Add(Number(static_cast<float>(id)))
        .Add(Text(tag))
        .Add(Text(icon))
        .Post();
  } else if (t_crime_populating != 0u && base != nullptr && vector == t_crime_populating + CrimeRowsVector() &&
             Plausible(row)) {
    LogOnce(crime_logged, "a crime populate row stated");
    Statement("@Crime.Row")
        .Add(Text(Hex(t_crime_populating)))
        .Add(Number(static_cast<float>(static_cast<int32_t>(LoadGuestU32(base, row + 8u)))))
        .Add(Text(ReadCharString(base, row)))
        .Add(Text(ReadCharString(base, row + 4u, 260u)))
        .Post();
  }
  __imp__rex_RealEstate_PushRow(ctx, base);
}

/// (mode): the relist empties the row vector before its rebuild.
REX_EXTERN(rex_RealEstate_Relist) {
  static bool logged = false;
  LogOnce(logged, "the relist states its clear");
  const uint32_t mode = ctx.r3.u32;
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    Ids().erase(mode);
  }
  Statement("@RealEstate.Rows.Clear").Add(Text(Hex(mode))).Post();
  __imp__rex_RealEstate_Relist(ctx, base);
}

/// vf2 (mode, a2): `@RealEstate.Close(mode)`, then the guest's close.
REX_EXTERN(rex_RealEstate_Close) {
  static bool logged = false;
  LogOnce(logged, "the close is stated");
  const uint32_t mode = ctx.r3.u32;
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    Ids().erase(mode);
  }
  Statement("@RealEstate.Close").Add(Text(Hex(mode))).Post();
  __imp__rex_RealEstate_Close(ctx, base);
}

/// The crime mode's vf11 (mode): it empties its rows and pushes them; each is stated as crime's.
REX_EXTERN(rex_CrimeMode_Populate) {
  static bool logged = false;
  LogOnce(logged, "the crime populate states its rows");
  const uint32_t mode = ctx.r3.u32;
  Statement("@Crime.Rows.Clear").Add(Text(Hex(mode))).Post();
  const uint32_t outer = t_crime_populating;
  t_crime_populating = mode;
  __imp__rex_CrimeMode_Populate(ctx, base);
  t_crime_populating = outer;
  Statement("@Crime.Listed").Add(Text(Hex(mode))).Post();
}
