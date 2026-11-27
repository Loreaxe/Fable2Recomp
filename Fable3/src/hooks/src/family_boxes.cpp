// refii - Fable 3's family boxes, Godot's: the bed menu, the divorce message, the treasury statement,
// the marriage proposal and the food query. Godot draws them and owns their pad
// (Fable3SessionBoxAnswers); each answer goes to the box's OWN pad handler as a `box:` lever with the
// player who pressed (display_box_hooks.cpp's gate), after storing the fields that handler's answer
// reads. TU3 addresses first, disc second; every body named here is the one word of its vtable (word
// scan of 0x82000000..0x82160000 on both builds: one vtable word and its .pdata row).
//
// THE BED MENU (CBedInteractionMessageBox: vtable 0x820D9EF8 / 0x820D7508; +4 Show sub_82B8DB48 /
// sub_82B58F98, +16 HandleInput sub_82B8DFA0 / sub_82B59348, +20 Update sub_82B8DEF0 / sub_82B59298; +8
// Hide sub_82B8DE48 / sub_824B2918 is CTutorialBox's too and is only called through the vtable). The
// opener sub_82623738 / sub_825FB858 (rex_CECBed_DisplayInteractionMenu's queue push) stores the hero
// +28/+32, the bed entity +36/+40, the three Lua flags +56 offered / +57 group / +58 cycle
// (RefiiF3BedOpened states them), the row +44 = 0, sleep +48 = 0, sex +52 = 0, the clock element +68 = 0.
//   - Show, REPLACED. Dropped with the presentation sub_829588C8 (0x82B8DB58): the root slide, the Sleep
//     title on `dword_8359F048` by `*dword_8359F064` (0x82B8DB94), the Sex title on `dword_8359F058` by
//     `*dword_8359F06C` when `+57` else `*dword_8359F068` (`lbz r9,0x39(r30)` 0x82B8DB98..0x82B8DBC0), the
//     clock element `+68` (0x82B8DBDC); without `+56` the two half-opacity writes sub_82B8E810
//     (0x82B8DBF8 / 0x82B8DC04); the row slides sub_82B8DD98 (0x82B8DC0C) and the option texts
//     sub_82B8DC78 (0x82B8DC14): the sleep option `*dword_8359F070[+48]` and its description
//     `*dword_8359F07C[+48]`, the sex option `*dword_8359F088[+52]` and `*dword_8359F090[+52]`
//     (GuiElement_SetTextTag 0x82B8DCD4..0x82B8DD7C). Kept: the open cue `dword_8359BE6C` (sub_823333B0 and
//     the handle's release, 0x82B8DC2C..0x82B8DC48), `+12 = 1` (0x82B8DC5C), BoxShown (0x82B8DC64), the
//     answer 1. Disc: dword_8355E454 / E458 / E45C titles, E460 / E46C / E478 / E480 tables, cue
//     dword_8355BA6C, the same instructions. Stated: `@Screen.Bed(box, sleep title, sex title, the three
//     sleep options and their three descriptions, the two sex options and their two descriptions)`.
//   - Update, REPLACED: the clock (`+68`, behind the presentation 0x82B8DF10, GuiTextBox_Commit
//     0x82B8DF78) dropped; the base update CDisplayBoxBase_p_vf8 (0x82B8DF88) kept, its answer the method's.
//   - HandleInput, GATED: shown (0x82B8DFAC), the hero's component 37 pad against r5 when the hero lives
//     (`cmpw cr6,r11,r28` 0x82B8E128 / 0x82B594D0); the switch on action - 29 (jump table 0x82B8E17C /
//     0x82B59524): 29 / 30 the row (Sex only with `+56`, 0x82B8E458), 31 / 32 the row's option (sleep
//     0..2 wrapping, 0x82B8E244..0x82B8E264 / 0x82B8E35C..0x82B8E380; sex 0..1 only with `+58`,
//     0x82B8E280 / 0x82B8E39C), 41 (case 12): the cue `dword_8359BF48`, the bed's component 207 +0x14 =
//     sleep (row 0) or +0x18 = sex (row 1) (0x82B8E5D4..0x82B8E5F4), the apply sub_825614D8
//     (rex_CECBed_ApplyAnswer, 0x82B8E5F8) and the hide (0x82B8E60C); 42 (case 13): the cue
//     `dword_8359BF4C`, in a live game the packet sub_82382F60 (0x82B8E7D0), `bed+13 = 0` (0x82B8E7E0), the
//     hide. The mod events `sleep_menu` / `sleep_chosen` ride these branches (mod_events_hooks.cpp).
//     Up / down play `unk_8359BF54` on every press past the gate (0x82B8E3E8..0x82B8E404 /
//     0x82B8E42C..0x82B8E448), left `dword_8359BF3C` and right `dword_8359BF40` likewise before their
//     4-tick repeat test (0x82B8E2CC..0x82B8E2E8 / 0x82B8E1B4..0x82B8E1D0); disc unk_8355BB54,
//     dword_8355BB3C, dword_8355BB40. Their redraws (sub_82B8DD98 / sub_82B8DC78) are the presentation's.
//     Levers: `sleep:<0..2>` (row 0, `+48`, then 41), `sex:<0..1>` (only with `+56`, and 1 only with
//     `+58`: row 1, `+52`, then 41), `cancel` (42) — a refused answer puts the fields back; Godot's own
//     moves, after the handler's test (shown, the hero's pad when it lives and has component 37):
//     `row:<0|1>` (the step cue; `+44` stored when the guest's stick would reach it) and
//     `option:<left|right>:<value>` (the left / right cue; the row's option stored when the guest's
//     own step could make it).
//
// THE DIVORCE MESSAGE (CDivorceMessageBox: vtable 0x820C4660 / 0x820C0088, 208 / 200 bytes; +16
// HandleInput sub_8297F5A8 / sub_82928958). Ctor sub_8297EDB8(box, name, pad r5, recA, recB, mode r8,
// reason r9, fee r10) / sub_829281C8 (`mr r27,r5` .. `mr r22,r10` 0x8297EDD8..0x8297EDEC / 0x829281E8..
// 0x829281FC; `+120 = pad` 0x8297EEBC, `+124 = fee` 0x8297EEC4, `+136 = reason` 0x8297EED4, `+172 = mode`
// 0x8297EF0C), its five creators partners' X, the spouse's interaction menu and component 83's three.
// `@Screen.Divorce(box, pad, mode, reason, fee)` at its entry. Its presentation is still made (the
// Show's three page writers carry the names, the reasons and the shares: sub_8297F8D8 / sub_8297FBC0 /
// sub_8297FFC0, sources not read).
//   - HandleInput, GATED: `r5 == +120` (`lwz r10,0x78(r3)` / `cmpw cr6,r5,r10` 0x8297F5BC..0x8297F5C8 /
//     disc the same); 74: the cue `dword_8359BF48` and the accept sub_829802A8 / sub_82929628 (0x8297F624
//     / 0x829289D4: divorce_confirm.cpp); modes 0 and 3 only (`lwz r11,0xAC(r31)` 0x8297F630): 75 the
//     cue `dword_8359BF4C` and `+177 = +176 = 1` (0x8297F674 / 0x8297F678); 32 / 31 the cue and, 0.3 s
//     apart (flt_8206CBF4), the reason `+136` up / down wrapping at the list `+144..+148` (0x8297F6F4..
//     0x8297F7D0), then its writer sub_82980460 / sub_829297D8.
//     Levers: `accept` (74), `cancel` (75), `reason:next` (32) / `reason:prev` (31).
//
// THE TREASURY STATEMENT (CMiniStatementBox: vtable 0x820DAE9C / 0x820D84A4; +16 HandleInput
// sub_82B8F068 / sub_82B5A410). Its presentation is still made (the balances and the transactions are
// written by sub_82B8FB00 / sub_82B5AEA8 behind it; their source calls were not read).
//   - HandleInput, GATED: shown, the opener's (+148) component 37 pad against r5 (`cmpw cr6,r11,r27`
//     0x82B8F1EC / 0x82B5A594); 74 the cue `dword_8359BF48` and the accept sub_82B8F320 / sub_82B5A6C8
//     (pages 2 -> 4 -> 5 -> Lua_Treasury_Transfer at 0x82B8F8B4 / 0x82B5AC5C -> 2); 75 the cue
//     `dword_8359BF4C` and page 4 / 5 -> 2 or the hide (0x82B8F25C..0x82B8F29C); 76 on page 2 flips the
//     direction `+32` (0x82B8F214..0x82B8F22C); anything else to the amount widget sub_82B8B3E0(+40, code,
//     player) (0x82B8F2F0), which steps `+56` by its step and clamps it for 31 / 32 (its switch on code
//     - 26, 0x82B8B41C..0x82B8B444).
//     Levers: `accept` (74), `back` (75), `flip` (76), `less` (31), `more` (32).
//
// THE MARRIAGE PROPOSAL (CMarriageProposalQueryBox: vtable 0x820B9AE4 / 0x820B4640; +16 HandleInput
// sub_8287AFA0 / sub_8281D238). Its presentation is still made (the six figures and the proposer's
// name are the creator sub_82623610's arguments, the gamer picture an element texture).
//   - HandleInput, GATED: shown (0x8287AFAC), the hero's (+28) component 37 pad against r5 (`cmpw
//     cr6,r11,r30` 0x8287B124); 41 sub_824D70B0(comp83, 1, 1, 0) (0x8287B400) and the hide; 42
//     sub_824D70B0(comp83, 0, 1, 0) (0x8287B294) and the hide; 76 the hide then sub_824D70B0(comp83, 0,
//     IsInLiveGame, 1) (0x8287B5E0: decline and queue "leave the game?"); 77 the gamer card sub_8236BCD0
//     (0x8287B82C).
//     Levers: `accept` (41), `decline` (42), `leave` (76), `card` (77).
//
// THE FOOD QUERY (CFoodItemQueryBox: vtable 0x820D9CE0 / 0x820D72F4; +16 HandleInput sub_82B8CC78 /
// sub_82B58048, 3508 / 3640 bytes). Its presentation is still made (the item's stat rows' sources were
// not read).
//   - HandleInput, GATED: shown (0x82B8CC84), the asker's (+28) component 37 pad against r5 (`cmpw
//     cr6,r11,r28` 0x82B8CDFC); 41 / 42 run the mode `+112`'s answer (0 the script's event 68 with 1 / 0,
//     1 the confirmed use, 2 the shop's vt[7], 3 the NPC gift, 4 the donation, 5 the received item:
//     the `bdz` chains 0x82B8CE18..0x82B8CE44 and 0x82B8D220..0x82B8D244), each build's own body.
//     Levers: `yes` (41), `no` (42).
//
// UI CUES are still the guest's: every one named above plays from the handler Godot's lever runs, and
// the bed's open cue from its Show here.

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <string>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "box_answers.h"
#include "gui_statements.h"
#include "mod_seam.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_BedBox_HandleInput);
REX_EXTERN(__imp__rex_DivorceBox_HandleInput);
REX_EXTERN(__imp__rex_MiniStatementBox_HandleInput);
REX_EXTERN(__imp__rex_MarriageBox_HandleInput);
REX_EXTERN(__imp__rex_FoodBox_HandleInput);
REX_EXTERN(__imp__rex_DivorceBox_Ctor);
REX_EXTERN(rex_BedBox_HandleInput);
REX_EXTERN(rex_DivorceBox_HandleInput);
REX_EXTERN(rex_MiniStatementBox_HandleInput);
REX_EXTERN(rex_MarriageBox_HandleInput);
REX_EXTERN(rex_FoodBox_HandleInput);
REX_EXTERN(rex_DisplayBox_Shown);
REX_EXTERN(rex_DisplayBox_Update);

namespace {

using namespace refii::fable3;

/// The family boxes' vtables (TU3; classified by ToTu3).
constexpr uint32_t kVtBed = 0x820D9EF8u;
constexpr uint32_t kVtDivorce = 0x820C4660u;
constexpr uint32_t kVtTreasury = 0x820DAE9Cu;
constexpr uint32_t kVtProposal = 0x820B9AE4u;
constexpr uint32_t kVtFood = 0x820D9CE0u;

constexpr uint32_t kBoxShown = 12u;

/// CBedInteractionMessageBox.
constexpr uint32_t kBedRow = 0x2Cu;
constexpr uint32_t kBedSleep = 0x30u;
constexpr uint32_t kBedSex = 0x34u;
constexpr uint32_t kBedOffered = 0x38u;
constexpr uint32_t kBedGroup = 0x39u;
constexpr uint32_t kBedCycle = 0x3Au;
constexpr uint32_t kBedSleepOptions = 3u;
constexpr uint32_t kBedSexOptions = 2u;
const refii::guest::Addr kBedSleepTitle{0x8359F064u};
const refii::guest::Addr kBedSexTitle{0x8359F068u};
const refii::guest::Addr kBedSexTitleGroup{0x8359F06Cu};
const refii::guest::Addr kBedSleepTags{0x8359F070u};
const refii::guest::Addr kBedSleepDescriptions{0x8359F07Cu};
const refii::guest::Addr kBedSexTags{0x8359F088u};
const refii::guest::Addr kBedSexDescriptions{0x8359F090u};
const refii::guest::Addr kBedOpenCue{0x8359BE6Cu};
const refii::guest::Addr kBedStepCue{0x8359BF54u};
const refii::guest::Addr kBedLeftCue{0x8359BF3Cu};
const refii::guest::Addr kBedRightCue{0x8359BF40u};
constexpr uint32_t kBedHeroRef = 0x1Cu;

constexpr uint32_t kActionYes = 41u;
constexpr uint32_t kActionNo = 42u;
constexpr uint32_t kActionLeft = 31u;
constexpr uint32_t kActionRight = 32u;
constexpr uint32_t kActionAPressed = 74u;
constexpr uint32_t kActionBPressed = 75u;
constexpr uint32_t kActionXPressed = 76u;
constexpr uint32_t kActionYPressed = 77u;

void Said(std::atomic<bool>& said, const char* what) {
  if (!said.exchange(true)) {
    REXLOG_INFO("[fable3-boxes] {}", what);
  }
}

uint32_t ToInt(const std::string& text, bool& ok) {
  char* end = nullptr;
  const long value = std::strtol(text.c_str(), &end, 10);
  ok = !text.empty() && end != nullptr && *end == '\0' && value >= 0;
  return ok ? static_cast<uint32_t>(value) : 0u;
}

/// A tag word of a static table: entry `index` of the table at `table`.
uint32_t TagAt(const uint8_t* base, uint32_t table, uint32_t index) { return LoadGuestU32(base, table + 4u * index); }

/// The bed's row and that row's option stored, then 41 through its handler; both back on a refusal.
bool BedAnswer(PPCContext& ctx, uint8_t* base, uint32_t box, uint32_t row, uint32_t field, uint32_t option,
               uint32_t player, const char* action) {
  const uint32_t row_before = LoadGuestU32(base, box + kBedRow);
  const uint32_t option_before = LoadGuestU32(base, box + field);
  seam::StoreU32(base, box + kBedRow, row);
  seam::StoreU32(base, box + field, option);
  if (RunOwnedHandler(ctx, base, rex_BedBox_HandleInput, box, kActionYes, player) == 0u) {
    seam::StoreU32(base, box + kBedRow, row_before);
    seam::StoreU32(base, box + field, option_before);
    REXLOG_INFO("[fable3-boxes] answer '{}': the bed menu's handler refused it", action);
  }
  return true;
}

/// One action through a family box's handler, for the verbs `codes` names.
struct Verb {
  const char* name;
  uint32_t code;
};

template <size_t N>
bool RunVerb(PPCContext& ctx, uint8_t* base, GuestFunction handler, uint32_t box, const std::string& verb,
             const Verb (&verbs)[N], uint32_t player) {
  for (const Verb& entry : verbs) {
    if (verb == entry.name) {
      RunOwnedHandler(ctx, base, handler, box, entry.code, player);
      return true;
    }
  }
  return false;
}

}  // namespace

/// CBedInteractionMessageBox::Show(box) without its presentation: the titles and options stated.
REX_EXTERN(rex_BedBox_Show) {
  static std::atomic<bool> said{false};
  Said(said, "bed menu: Show and Update replaced (stated, no presentation made)");
  const uint32_t box = ctx.r3.u32;
  Statement bed("@Screen.Bed");
  bed.Add(Text(Hex(box)))
      .Add(Text(Hex(LoadGuestU32(base, kBedSleepTitle))))
      .Add(Text(Hex(LoadGuestU32(base, base[box + kBedGroup] != 0u ? kBedSexTitleGroup : kBedSexTitle))));
  for (uint32_t i = 0; i < kBedSleepOptions; ++i) {
    bed.Add(Text(Hex(TagAt(base, kBedSleepTags, i))));
  }
  for (uint32_t i = 0; i < kBedSleepOptions; ++i) {
    bed.Add(Text(Hex(TagAt(base, kBedSleepDescriptions, i))));
  }
  for (uint32_t i = 0; i < kBedSexOptions; ++i) {
    bed.Add(Text(Hex(TagAt(base, kBedSexTags, i))));
  }
  for (uint32_t i = 0; i < kBedSexOptions; ++i) {
    bed.Add(Text(Hex(TagAt(base, kBedSexDescriptions, i))));
  }
  bed.Post();
  PlayGuestCue(ctx, base, kBedOpenCue);
  base[box + kBoxShown] = 1u;
  PPCContext work = seam::Frame(ctx);
  work.r3.u64 = box;
  rex_DisplayBox_Shown(work, base);
  ctx.r3.u64 = 1u;
}

/// CBedInteractionMessageBox::Update(box) without its clock: the base update and its answer.
REX_EXTERN(rex_BedBox_Update) {
  static std::atomic<bool> said{false};
  Said(said, "bed menu: its Update ran replaced (no clock)");
  PPCContext work = seam::Frame(ctx);
  work.r3.u64 = ctx.r3.u32;
  rex_DisplayBox_Update(work, base);
  ctx.r3.u64 = work.r3.u32;
}

/// The divorce box's ctor(box, name, pad, recA, recB, mode, reason, fee), stated at its entry.
REX_EXTERN(rex_DivorceBox_Ctor) {
  static std::atomic<bool> said{false};
  Said(said, "divorce message: its ctor is stated (rex_DivorceBox_Ctor)");
  Statement("@Screen.Divorce")
      .Add(Text(Hex(ctx.r3.u32)))
      .Add(Number(static_cast<float>(static_cast<int32_t>(ctx.r5.u32))))
      .Add(Number(static_cast<float>(static_cast<int32_t>(ctx.r8.u32))))
      .Add(Number(static_cast<float>(ctx.r9.u32 & 0xFFu)))
      .Add(Number(static_cast<float>(static_cast<int32_t>(ctx.r10.u32))))
      .Post();
  __imp__rex_DivorceBox_Ctor(ctx, base);
}

#define REFII_F3_FAMILY_GATE(hook, what)                                                    \
  REX_EXTERN(rex_##hook) {                                                                  \
    static std::atomic<bool> said{false};                                                   \
    Said(said, what ": its pad is Godot's (rex_" #hook " gated)");                          \
    GateOwnedInput(ctx, base, __imp__rex_##hook);                                           \
  }

REFII_F3_FAMILY_GATE(BedBox_HandleInput, "bed menu")
REFII_F3_FAMILY_GATE(DivorceBox_HandleInput, "divorce message")
REFII_F3_FAMILY_GATE(MiniStatementBox_HandleInput, "treasury statement")
REFII_F3_FAMILY_GATE(MarriageBox_HandleInput, "marriage proposal")
REFII_F3_FAMILY_GATE(FoodBox_HandleInput, "food query")

#undef REFII_F3_FAMILY_GATE

namespace refii::fable3 {

bool AnswerFamilyBox(PPCContext& ctx, uint8_t* base, uint32_t box, uint32_t vtable, const std::string& verb,
                     const std::string& value, uint32_t player, const char* action) {
  bool ok = false;
  switch (refii::guest::ToTu3(vtable)) {
    case kVtBed: {
      if (verb == "cancel") {
        RunOwnedHandler(ctx, base, rex_BedBox_HandleInput, box, kActionNo, player);
        return true;
      }
      if (verb == "row" || verb == "option") {
        const int64_t pad = BoxOwnerPad(base, box + kBedHeroRef);
        if (base[box + kBoxShown] == 0u || (pad != kNoOwnerTest && pad != static_cast<int64_t>(player))) {
          REXLOG_INFO("[fable3-boxes] answer '{}': not shown or not this box's player; refused", action);
          return false;
        }
        if (verb == "row") {
          const uint32_t row = ToInt(value, ok);
          if (!ok || row > 1u) {
            return false;
          }
          PlayGuestCue(ctx, base, kBedStepCue);
          if (row == 0u || base[box + kBedOffered] != 0u) {
            seam::StoreU32(base, box + kBedRow, row);
          }
          return true;
        }
        const size_t colon = value.find(':');
        const std::string side = value.substr(0, colon);
        const uint32_t to = colon == std::string::npos ? 0u : ToInt(value.substr(colon + 1), ok);
        if ((side != "left" && side != "right") || !ok) {
          return false;
        }
        PlayGuestCue(ctx, base, side == "left" ? kBedLeftCue : kBedRightCue);
        const uint32_t row = LoadGuestU32(base, box + kBedRow);
        if (row == 0u && to < kBedSleepOptions) {
          seam::StoreU32(base, box + kBedSleep, to);
        } else if (row == 1u && base[box + kBedCycle] != 0u && to < kBedSexOptions) {
          seam::StoreU32(base, box + kBedSex, to);
        }
        return true;
      }
      const uint32_t option = ToInt(value, ok);
      if (verb == "sleep") {
        if (!ok || option >= kBedSleepOptions) {
          return false;
        }
        return BedAnswer(ctx, base, box, 0u, kBedSleep, option, player, action);
      }
      if (verb == "sex") {
        // The stick reaches the row only when offered (0x82B8E458); left / right move its option only
        // when it cycles (0x82B8E280 / 0x82B8E39C), so without that it stays the opener's 0.
        if (!ok || option >= kBedSexOptions || base[box + kBedOffered] == 0u ||
            (option != 0u && base[box + kBedCycle] == 0u)) {
          REXLOG_INFO("[fable3-boxes] answer '{}': the bed menu does not offer it; refused", action);
          return false;
        }
        return BedAnswer(ctx, base, box, 1u, kBedSex, option, player, action);
      }
      return false;
    }
    case kVtDivorce: {
      static constexpr Verb kVerbs[] = {{"accept", kActionAPressed}, {"cancel", kActionBPressed}};
      if (RunVerb(ctx, base, rex_DivorceBox_HandleInput, box, verb, kVerbs, player)) {
        return true;
      }
      if (verb == "reason" && (value == "next" || value == "prev")) {
        RunOwnedHandler(ctx, base, rex_DivorceBox_HandleInput, box, value == "next" ? kActionRight : kActionLeft,
                        player);
        return true;
      }
      return false;
    }
    case kVtTreasury: {
      static constexpr Verb kVerbs[] = {{"accept", kActionAPressed}, {"back", kActionBPressed},
                                        {"flip", kActionXPressed},   {"less", kActionLeft},
                                        {"more", kActionRight}};
      return RunVerb(ctx, base, rex_MiniStatementBox_HandleInput, box, verb, kVerbs, player);
    }
    case kVtProposal: {
      static constexpr Verb kVerbs[] = {{"accept", kActionYes},
                                        {"decline", kActionNo},
                                        {"leave", kActionXPressed},
                                        {"card", kActionYPressed}};
      return RunVerb(ctx, base, rex_MarriageBox_HandleInput, box, verb, kVerbs, player);
    }
    case kVtFood: {
      static constexpr Verb kVerbs[] = {{"yes", kActionYes}, {"no", kActionNo}};
      return RunVerb(ctx, base, rex_FoodBox_HandleInput, box, verb, kVerbs, player);
    }
    default:
      return false;
  }
}

}  // namespace refii::fable3
