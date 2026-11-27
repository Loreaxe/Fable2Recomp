// refii - the Sanctuary's interaction mode, mode 3 (CInteractionModeGUIWorld): the guest's pad no longer
// reaches its consequences; Godot's `guiworld:<p>:<verb>` levers run them through the guest's own code.
//
// Addresses are TU3 / disc; every offset names its base: `mode` the object (*(IM+24)), `IM` the
// interaction manager (*(mode+20), `lwz r10,0x14(r3)` 0x82972EE8), GUI *0x83596F84 / *0x83556490, `cur`
// the current game component *0x83597034 / *0x83556540.
//
// THE CLASS. Vtable 0x820C3C10 / 0x820C2088 (each slot below is that vtable's only word for its body, by a
// word scan of .rdata/.data, except vf5's 11 and vf6's 16), built only by the mode set's case 3
// (sub_82964890 / sub_82954FA0 at 0x825EF4E0 / 0x825C4650), freed only through vf0 0x82964EC0 / 0x82955568
// (the body 0x82964FD8 has the one caller 0x82964EDC). Offsets: the IM +20 and the active byte +24 on
// both builds; cursor +44 / +116, rows +48 / +120 (`lwz r11,0x2C(r31)` 0x82969F4C, `lwz r10,0x30(r31)`
// 0x82969F54 / `0x74` 0x8295A6F4, `0x78` 0x8295A6FC); list active byte +76 / +148 (`lbz r11,0x4C(r30)`
// 0x829730E4 / `0x94` 0x82964454); A / X / Y label handles +80 / +144 / +176, disc +152 / +216 / +248
// (`lwz r11,0x50(r31)` 0x82969DC4, `0x90` 0x8296A0CC, `0xB0` 0x8296A000 / `0x98`, `0xD8`, `0xF8`); the
// dye bar shown byte +242 / +314, value +416 / +496, min +424 / +504, max +428 / +508 (`lbz r11,0xF2(r3)`
// 0x8296D17C, `lfs f0,0x1A0` 0x8296D218, `lfs f13,0x1A8` 0x8296D244, `lfs f10,0x1AC` 0x8296D258 / disc
// 0x13A, 0x1F0, 0x1F8, 0x1FC).
//
// THE ROUTE OF A PRESS. The dispatcher's channel 8, sub_825F0860(IM, action*, player) / sub_825C58A0,
// gates on IM+4, IM+0, IM+7, sub_821A38C8(SM) and **(SM+56) (0x825F086C..0x825F08E0), then
// `lwz r3,0x18(r30); lbz r11,0x18(r3)` and the object's vf5 (`lwz r10,0x14(r11); bctrl` 0x825F0908..
// 0x825F0910), a non-zero answer ending it with 1; then the script mini-game bar (IM+0x1C, sub_82716558
// 0x825F094C), then the bumper bar IM+32 and the trigger bar IM+36, each its vf4 (0x825F09C8 / 0x825F09F8).
// vf5 is the shared list input sub_82972ED8 / sub_82964248: for action 26 (left stick) the owner test
// (below), the stamps `stw r3,0x34|0x38|0x3C|0x40(r30)` from sub_82209028 0x829730A0..0x829730E0 (disc
// 0x7C 0x80 0x84 0x88) and, with the list active, the list's step and `li r3,1` (0x829731DC); for 27
// (right stick) the stamps `stw r3,0x48|0x44(r30)` 0x82973218 / 0x8297322C (disc 0x90 / 0x8C); then the
// base input sub_82970900 / sub_82961CA8 (0x8297323C): the active byte +24 and the owner gate
// sub_829704A0 / sub_82961848, the hold buttons sub_82970A98 when +8, and vf7.
//
// vf7 HandleInput 0x829699E0 / 0x8295A178 (mode, action*, player) first resolves the presser's hero
// (sub_821FF0A8(*(*(cur+12)+0x40), player) 0x82969A14, sub_82A1D6E8 0x82969A20; disc sub_821FD790,
// sub_8243C9E8) and answers 0 unless it is alive (`lbz r11,0x94(r3); rlwinm r10,r11,0,25,25` 0x82969A30).
// Then (jump table 0x82969AB4, bias 14 / disc 0x8295A24C): 14 START with the GUI current (`(*cur)->vf6`,
// `lwz r10,0x18(r11); bctrl` 0x82969B08) event 138 (`li r4,0x8A` 0x82969B64), else 0; 74 A, 76 X, 77 Y
// answer 0 unless the slot's label is non-empty (`lwz r11,0x50(r31); beq; lwz r11,4(r11); beq`
// 0x82969DC4..0x82969DD8), 1 when it is disabled (`lbz r11,0x65(r31); beq loc_8296A2AC` 0x82969DDC), else
// events 133 / 135 / 136 or the slot's named message and the row's; 75 B: 1, event 134 or its message,
// no label test (`lbz r11,0x85(r31)` 0x8296A198); 26 the weapon information slide (sub_8296C088, `li
// r3,0` 0x82969D60); 27 the info box / side pane page, then sub_82B74918(27, player, 0.6, 0.6) 31 / 32
// -> the gesture poster with 4 / 5 (0x82969CAC) and 1; 31 / 32 the side pane's page.
//
// THE PAD CUT. vf7 answers the guest's pad for 14 and 74..77 as above and posts nothing; for every other
// action it runs as the guest has it with its one consequence withheld: the gesture poster called from
// inside it (StickHeldLeft / Right). The slide and page writes 26 / 27 / 31 / 32 make stay the guest's
// (presentation still driven by the pad: NOTES.md). vf5 for this class runs the base input with neither
// the stamps nor the list step: for 26 with the list active and the owner test passing it answers 1 as
// vf5 does (0x829731DC). The bars' vf4 (bumper 0x82B54FE8 / 0x82B18D30, trigger 0x82B55248 / 0x82B18F90;
// jump tables 0x82B55028 / 0x82B18D70, 0x82B55288 / 0x82B18FD0) answer the guest's pad while mode 3 is up
// with their table: bumper 88 89 92 93 96 97 -> 1, trigger 86 87 90 91 94 95 -> 1, else 0, and post
// nothing (96 / 97 post 129 / 130, 94 / 95 131 / 132, `li r4,0x81` 0x82B550D0 … `li r4,0x84` 0x82B553C8).
// The hold buttons (FB5's, vf6 and sub_82970A98) keep the guest's pad.
//
// THE LEVERS run the guest's code: a button is handed to the channel's handler body with the lever mark,
// as the interaction lever hands it (interaction_mode_input.cpp, whose cut of the guest's pad at that
// handler is for modes 0 and 13 only, so the pad of mode 3 still reaches the stubs here), which the
// three stubs answer by running their originals; vf5 then runs the base input without its
// hold step (Godot sends no lever for a slot whose P_Fill holds it). The cursor is the list's cursor
// store and the selection-changed slot vf14 0x829652F8 / 0x829559A0 (event 137 with the row, the live
// packet, A's label) with the list's step cue sub_823333B0(&h, GUI, &unk_8359BE58) / disc 0x8355BA58
// (0x82972A00, 0x82973188). A held stick stores the world frame sub_82209028 / sub_82209868 into its
// stamp each step, as vf5 does on every stick event; the tick vf4 0x8296A2B8 / 0x8295AA60 then posts
// StickHeld the next frame and the flick two frames after the last store, and moves the dye value from
// the right stick's (0x8296A384 / 0x8296A38C, sub_8296D168). A right stick held left / right is the
// gesture poster sub_82966EA0 / sub_82957540 with 4 / 5 each step, as vf7 posts it. The owner test is
// vf5's (0x82972F38..0x8297306C): the gate sub_829704A0(mode, action*, player); with the GUI current
// also a live presser whose entity +0x18 / +0x1C equal the IM hero's (GetEntitySentBy(IM) 0x823B5280 /
// 0x82397768).
//
// THE STATEMENTS: what the mode keeps, as it keeps it. Its two fillers — the table parser sub_8296A740 /
// sub_8295AEE8 (mode, table; called by the ctor 0x82964E54 / 0x829554FC and GUI.RefreshInteractionMode
// 0x8254E048 / 0x8251E1E8) and the partner's copy sub_8296B890 / sub_8295C038 (mode, packet; the
// CNetInteractionModeMenuGUIWorldSetup receiver, `DisplayItemsForOtherMachine`) — each end in one
// statement, `@Interaction.World.Parsed(player, cursor, holds, rows)` (player the IM hero's,
// GetEntitySentBy(IM)), gathered while the filler runs, each on the guest's own instruction:
//   a kept row: the push sub_8296D798(rows, row*) — the parser's row reader sub_8296AE40 pushes only a row
//   whose TextTag and Icon are set (0x8296AFB4..0x8296AFDC), `mr r3,r21; bl sub_8296D798` 0x8296B098 /
//   0x8296B09C (disc 0x8295B844), the copy `mr r3,r24; bl` 0x8296B97C (disc 0x8295C124); the row is five
//   CCharStrings TextTag, Icon, Message, SelectedTag, SelectedHeldTag (vf12 / vf13 read +0 / +4): the
//   rows table, `<n>.TextTag` … in push order;
//   the cursor: the parser's clamped store `stw r11,0x2C(r31)` 0x8296AA58 (disc `0x74` 0x8295B200), taken
//   from r11 at the next instruction; the copy's `stw r11,0x2C(r28)` 0x8296BA78 (disc 0x8295C220):
//   the cursor;
//   a hold slot: the slot reader sub_8296AB90's `stb r3,0x14(r29)` 0x8296ADB8 (P_Fill, default 0; disc
//   0x8295B560), the copy sub_8296B800's `stb r11,0x14(r31)` 0x8296B858 (disc 0x8295C000), the slot
//   `(r29 - mode - 80) / 32` A B X Y (disc base +152): bit slot of the holds.
// One record: the rows travel with their cursor and holds, never half a list.
// The midasm sites sit in shared helpers (the reader and the copy also fill the setup packet); they state
// only while one of the two fillers runs for a GUI world. `@Interaction.World.Closed` at vf0.

#include "interaction_guiworld.h"

#include <atomic>
#include <cstdlib>
#include <cstring>
#include <string>
#include <utility>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "box_answers.h"
#include "gui_statements.h"
#include "hud_levers.h"
#include "mod_lua.h"
#include "mod_seam.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_InteractionMode_Input);
REX_EXTERN(__imp__rex_InteractionGuiWorld_Parse);
REX_EXTERN(__imp__rex_InteractionGuiWorld_Receive);
REX_EXTERN(__imp__rex_InteractionGuiWorld_Destroy);
REX_EXTERN(__imp__rex_InteractionGuiWorld_Input);
REX_EXTERN(__imp__rex_InteractionGuiWorld_Gesture);
REX_EXTERN(__imp__rex_OverlayBumper_Input);
REX_EXTERN(__imp__rex_OverlayTrigger_Input);
REX_EXTERN(rex_GuiSound_Play);
REX_EXTERN(rex_SoundHandle_Release);

namespace {

using namespace refii::fable3;
using refii::guest::Pick;

const refii::guest::Addr kVtable{0x820C3C10u};
const refii::guest::Addr kBaseInput{0x82970900u};
const refii::guest::Addr kOwnerGate{0x829704A0u};
const refii::guest::Addr kPlayerRecord{0x821FF0A8u};
const refii::guest::Addr kRecordHero{0x82A1D6E8u};
const refii::guest::Addr kEntitySentBy{0x823B5280u};
const refii::guest::Addr kWorldFrame{0x82209028u};
const refii::guest::Addr kCurrentComponent{0x83597034u};
const refii::guest::Addr kGuiManager{0x83596F84u};
const refii::guest::Addr kListCue{0x8359BE58u};

constexpr uint32_t kModeIm = 20u;
constexpr uint32_t kModeActive = 24u;
constexpr uint32_t kImPresentation = 0u;
constexpr uint32_t kImMode = 20u;
constexpr uint32_t kImObject = 24u;
constexpr uint32_t kModeGuiWorld = 3u;
constexpr uint32_t kSelectionChangedSlot = 14u;
constexpr uint32_t kGuiCurrentSlot = 6u;

uint32_t Cursor() { return Pick(0x2Cu, 0x74u); }
uint32_t Rows() { return Pick(0x30u, 0x78u); }
uint32_t ListActive() { return Pick(0x4Cu, 0x94u); }
uint32_t LabelA() { return Pick(0x50u, 0x98u); }
uint32_t LabelX() { return Pick(0x90u, 0xD8u); }
uint32_t LabelY() { return Pick(0xB0u, 0xF8u); }
uint32_t StampLeft() { return Pick(0x34u, 0x7Cu); }
uint32_t StampRight() { return Pick(0x38u, 0x80u); }
uint32_t StampDown() { return Pick(0x3Cu, 0x84u); }
uint32_t StampUp() { return Pick(0x40u, 0x88u); }
uint32_t StampRsDown() { return Pick(0x44u, 0x8Cu); }
uint32_t StampRsUp() { return Pick(0x48u, 0x90u); }
uint32_t DyeShown() { return Pick(0xF2u, 0x13Au); }
uint32_t DyeValue() { return Pick(0x1A0u, 0x1F0u); }
uint32_t DyeMin() { return Pick(0x1A8u, 0x1F8u); }
uint32_t DyeMax() { return Pick(0x1ACu, 0x1FCu); }
uint32_t RowVector() { return Pick(0xDCu, 0x124u); }
uint32_t FirstSlot() { return Pick(0x50u, 0x98u); }
constexpr uint32_t kSlotSize = 32u;

// EGameAction (miscellaneous/gameactionenum.lua).
constexpr uint32_t kActionPause = 14u;
constexpr uint32_t kActionMovement = 26u;
constexpr uint32_t kActionA = 74u;
constexpr uint32_t kActionB = 75u;
constexpr uint32_t kActionX = 76u;
constexpr uint32_t kActionY = 77u;
constexpr uint32_t kActionLeftTriggerReleased = 94u;
constexpr uint32_t kActionRightTriggerReleased = 95u;
constexpr uint32_t kActionLeftBumperReleased = 96u;
constexpr uint32_t kActionRightBumperReleased = 97u;

/// The gesture codes sub_82966F78 names: 0 JudgementFlickLeft, 1 JudgementFlickRight, 2 FlickDown,
/// 3 FlickUp, 4 StickHeldLeft, 5 StickHeldRight.
constexpr uint32_t kFlickLeft = 0u;
constexpr uint32_t kFlickRight = 1u;
constexpr uint32_t kFlickDown = 2u;
constexpr uint32_t kFlickUp = 3u;
constexpr uint32_t kStickHeldLeft = 4u;
constexpr uint32_t kStickHeldRight = 5u;

enum class Direction : uint8_t { kNone, kLeft, kRight, kUp, kDown };

/// The mode the parser last ran for, until its vf0.
std::atomic<uint32_t> g_mode{0u};
/// The held sticks and whose they are.
Direction g_left = Direction::kNone;
Direction g_right = Direction::kNone;
uint32_t g_held_player = 0u;

/// Set while a lever runs the guest's code: the stubs then run their originals.
thread_local bool t_lever = false;
/// The mode one of its two fillers is filling, and the guest's memory, while it runs.
thread_local uint32_t t_filling = 0u;
thread_local const uint8_t* t_base = nullptr;
/// Set while the guest's pad runs vf7: the gesture poster called from inside it is withheld.
thread_local bool t_pad_vf7 = false;

void LogOnce(std::atomic<bool>& said, const char* text) {
  if (!said.exchange(true)) {
    REXLOG_INFO("[fable3-guiworld] {}", text);
  }
}

uint32_t Answer(const PPCContext& work) { return work.r3.u32 & 0xFFu; }

/// `fn` at `address` with r3..r5 on a frame below the caller's; its r3.
uint32_t Call(PPCContext& ctx, uint8_t* base, uint32_t address, uint32_t r3, uint32_t r4 = 0u, uint32_t r5 = 0u) {
  PPCContext work = seam::Frame(ctx, 0x400u);
  work.r3.u64 = r3;
  work.r4.u64 = r4;
  work.r5.u64 = r5;
  if (!seam::CallAt(work, base, address)) {
    return 0u;
  }
  return work.r3.u32;
}

/// The GUI component is the current one: `(*cur)->vf6()` (0x82969B00..0x82969B14).
bool GuiCurrent(PPCContext& ctx, uint8_t* base) {
  const uint32_t cur = seam::Word(base, kCurrentComponent);
  if (!seam::Plausible(cur)) {
    return false;
  }
  PPCContext work = seam::Frame(ctx, 0x400u);
  return seam::CallSlot(work, base, cur, kGuiCurrentSlot) && Answer(work) != 0u;
}

/// The hero of local player `player` as vf7 resolves it (0x82969A08..0x82969A24), 0 for none.
uint32_t PresserHero(PPCContext& ctx, uint8_t* base, uint32_t player) {
  const uint32_t cur = seam::Word(base, kCurrentComponent);
  const uint32_t players = seam::Word(base, seam::Word(base, cur + 0xCu) + 0x40u);
  const uint32_t record = Call(ctx, base, kPlayerRecord, players, player);
  return seam::Plausible(record) ? Call(ctx, base, kRecordHero, record) : 0u;
}

/// A 32-byte action record on a frame below the tick's, holding `action`.
struct ActionRecord {
  PPCContext work;
  uint32_t at = 0u;
};

ActionRecord MakeRecord(const PPCContext& ctx, uint8_t* base, uint32_t action) {
  ActionRecord record{seam::Frame(ctx, 0x600u), 0u};
  record.at = record.work.r1.u32 + 0x200u;
  std::memset(base + record.at, 0, 32u);
  seam::StoreU32(base, record.at, action);
  return record;
}

/// The owner gate sub_829704A0(mode, action*, player).
bool Gate(PPCContext& ctx, uint8_t* base, uint32_t mode, uint32_t record, uint32_t player) {
  return (Call(ctx, base, kOwnerGate, mode, record, player) & 0xFFu) != 0u;
}

/// vf5's owner test for the left stick (0x82972F38..0x8297306C).
bool Owner(PPCContext& ctx, uint8_t* base, uint32_t mode, uint32_t record, uint32_t player) {
  const uint32_t presser = PresserHero(ctx, base, player);
  const bool gui = GuiCurrent(ctx, base);
  if (Gate(ctx, base, mode, record, player)) {
    return true;
  }
  if (!gui || !seam::Live(base, presser)) {
    return false;
  }
  const uint32_t hero = Call(ctx, base, kEntitySentBy, seam::Word(base, mode + kModeIm));
  return seam::Live(base, hero) && seam::Word(base, presser + 0x18u) == seam::Word(base, hero + 0x18u) &&
         seam::Word(base, presser + 0x1Cu) == seam::Word(base, hero + 0x1Cu);
}

/// A slot's label handle is set and its text non-empty (0x82969DC4..0x82969DD8).
bool Labelled(const uint8_t* base, uint32_t mode, uint32_t label) {
  const uint32_t handle = seam::Word(base, mode + label);
  return seam::Plausible(handle) && seam::Word(base, handle + 4u) != 0u;
}

/// The mode up, when it is the parsed GUI world: `*(IM+24)` is it and IM+20 is 3.
uint32_t ModeUp(const uint8_t* base) {
  const uint32_t mode = g_mode.load();
  if (base == nullptr || !seam::Plausible(mode) || seam::Word(base, mode) != kVtable) {
    return 0u;
  }
  const uint32_t im = seam::Word(base, mode + kModeIm);
  return seam::Plausible(im) && seam::Word(base, im + kImObject) == mode && seam::Word(base, im + kImMode) == kModeGuiWorld
             ? mode
             : 0u;
}

void LetGo() {
  g_left = Direction::kNone;
  g_right = Direction::kNone;
  g_held_player = 0u;
}

uint32_t StickStamp(Direction direction) {
  switch (direction) {
    case Direction::kLeft: return StampLeft();
    case Direction::kRight: return StampRight();
    case Direction::kUp: return StampUp();
    case Direction::kDown: return StampDown();
    default: return 0u;
  }
}

bool ParseDirection(const char* text, Direction& direction) {
  if (std::strcmp(text, "left") == 0) direction = Direction::kLeft;
  else if (std::strcmp(text, "right") == 0) direction = Direction::kRight;
  else if (std::strcmp(text, "up") == 0) direction = Direction::kUp;
  else if (std::strcmp(text, "down") == 0) direction = Direction::kDown;
  else if (std::strcmp(text, "none") == 0) direction = Direction::kNone;
  else return false;
  return true;
}

/// A button handed to the channel's handler body as the pad would (hud_levers.h); its answer.
uint32_t Press(PPCContext& ctx, uint8_t* base, uint32_t im, uint32_t action, uint32_t player) {
  t_lever = true;
  const uint32_t answer = CallWithAction(ctx, base, __imp__rex_InteractionMode_Input, im, action, player);
  t_lever = false;
  return answer;
}

void PostGesture(PPCContext& ctx, uint8_t* base, uint32_t mode, uint32_t code) {
  PPCContext work = seam::Frame(ctx, 0x400u);
  work.r3.u64 = mode;
  work.r4.u64 = code;
  __imp__rex_InteractionGuiWorld_Gesture(work, base);
}

void PlayListCue(PPCContext& ctx, uint8_t* base) {
  PPCContext work = seam::Frame(ctx, 0x400u);
  const uint32_t handle = work.r1.u32 + 0x100u;
  work.r3.u64 = handle;
  work.r4.u64 = seam::Word(base, kGuiManager);
  work.r5.u64 = kListCue;
  rex_GuiSound_Play(work, base);
  work.r3.u64 = handle;
  rex_SoundHandle_Release(work, base);
}

bool MoveCursor(PPCContext& ctx, uint8_t* base, uint32_t mode, uint32_t player, const char* field, const char* action) {
  char* end = nullptr;
  const unsigned long row = std::strtoul(field, &end, 10);
  if (end == field || *end != '\0') {
    REXLOG_WARN("[fable3-guiworld] '{}' names no row; dropped", action);
    return false;
  }
  const uint32_t rows = seam::Word(base, mode + Rows());
  if (base[mode + ListActive()] == 0u || row >= rows) {
    REXLOG_WARN("[fable3-guiworld] '{}': the list is {} with {} row(s); dropped", action,
                base[mode + ListActive()] != 0u ? "up" : "not active", rows);
    return false;
  }
  ActionRecord record = MakeRecord(ctx, base, kActionMovement);
  if (!Owner(record.work, base, mode, record.at, player)) {
    REXLOG_INFO("[fable3-guiworld] '{}': player {} does not own this Sanctuary (the list input's test); dropped",
                action, player);
    return false;
  }
  if (seam::Word(base, mode + Cursor()) == row) {
    return true;
  }
  seam::StoreU32(base, mode + Cursor(), static_cast<uint32_t>(row));
  PlayListCue(ctx, base);
  PPCContext work = seam::Frame(ctx, 0x400u);
  seam::CallSlot(work, base, mode, kSelectionChangedSlot);
  REXLOG_INFO("[fable3-guiworld] cursor on row {} of {} for player {}: the selection-changed slot ran", row, rows,
              player);
  return true;
}

bool SetDyeValue(uint8_t* base, uint32_t mode, const char* field, const char* action) {
  char* end = nullptr;
  const float value = std::strtof(field, &end);
  if (end == field || *end != '\0' || value != value) {
    REXLOG_WARN("[fable3-guiworld] '{}' names no value; dropped", action);
    return false;
  }
  if (base[mode + DyeShown()] == 0u) {
    REXLOG_WARN("[fable3-guiworld] '{}' with no dye bar up; dropped", action);
    return false;
  }
  const float low = seam::Float(base, mode + DyeMin());
  const float high = seam::Float(base, mode + DyeMax());
  const float clamped = value < low ? low : (value > high ? high : value);
  seam::StoreFloat(base, mode + DyeValue(), clamped);
  return true;
}

}  // namespace

namespace refii::fable3 {

bool IsGuiWorld(const uint8_t* base, uint32_t mode) {
  return base != nullptr && seam::Plausible(mode) && seam::Word(base, mode) == kVtable;
}

bool GuiWorldListInput(PPCContext& ctx, uint8_t* base) {
  const uint32_t mode = ctx.r3.u32;
  if (!IsGuiWorld(base, mode)) {
    return false;
  }
  const uint32_t record = ctx.r4.u32;
  const uint32_t player = ctx.r5.u32;
  if (t_lever) {
    // The base input sub_82970900 without its hold step: the active byte, the owner gate, vf7.
    ctx.r3.u64 = 0u;
    if (base[mode + kModeActive] != 0u && Gate(ctx, base, mode, record, player)) {
      PPCContext work = seam::Frame(ctx, 0x400u);
      work.r4.u64 = record;
      work.r5.u64 = player;
      if (seam::CallSlot(work, base, mode, 7u)) {
        ctx.r3.u64 = Answer(work) != 0u ? 1u : 0u;
      }
    }
    return true;
  }
  static std::atomic<bool> said{false};
  LogOnce(said, "the guest's pad reached the Sanctuary's list input: no stamps and no list step (Godot's levers)");
  const uint32_t im = seam::Word(base, mode + kModeIm);
  if (seam::Word(base, record) == kActionMovement && seam::Word(base, im + kImPresentation) != 0u &&
      base[mode + ListActive()] != 0u && Owner(ctx, base, mode, record, player)) {
    ctx.r3.u64 = 1u;
    return true;
  }
  ctx.r3.u64 = Call(ctx, base, kBaseInput, mode, record, player) & 0xFFu;
  return true;
}

bool AnswerGuiWorld(PPCContext& ctx, uint8_t* base, const char* action) {
  uint32_t player = 0u;
  const char* verb = LeverPlayer(action + sizeof(kGuiWorldAction) - 1u, player);
  if (verb == nullptr) {
    REXLOG_WARN("[fable3-guiworld] '{}' names no player; dropped", action);
    return false;
  }
  const uint32_t mode = ModeUp(base);
  if (mode == 0u) {
    REXLOG_WARN("[fable3-guiworld] '{}' with no Sanctuary mode up; dropped", action);
    LetGo();
    return false;
  }
  const uint32_t im = seam::Word(base, mode + kModeIm);

  struct Button {
    const char* verb;
    uint32_t action;
  };
  static constexpr Button kButtons[] = {
      {"a", kActionA},
      {"b", kActionB},
      {"x", kActionX},
      {"y", kActionY},
      {"start", kActionPause},
      {"lb", kActionLeftBumperReleased},
      {"rb", kActionRightBumperReleased},
      {"lt", kActionLeftTriggerReleased},
      {"rt", kActionRightTriggerReleased},
  };
  for (const Button& button : kButtons) {
    if (std::strcmp(verb, button.verb) != 0) {
      continue;
    }
    // The box router (channel 1) answers "consumed" for 72..85 while a box shows, ahead of channel 8.
    if (BoxTakesAction(base, button.action, player)) {
      REXLOG_INFO("[fable3-guiworld] '{}': a showing box has the pad ahead of the interaction mode; dropped", action);
      return true;
    }
    const uint32_t answer = Press(ctx, base, im, button.action, player);
    REXLOG_INFO("[fable3-guiworld] {} ({}) for player {}: the interaction manager's input answered {}", verb,
                button.action, player, answer);
    return true;
  }

  constexpr char kCursor[] = "cursor:";
  if (std::strncmp(verb, kCursor, sizeof(kCursor) - 1u) == 0) {
    return MoveCursor(ctx, base, mode, player, verb + sizeof(kCursor) - 1u, action);
  }

  constexpr char kStick[] = "stick:";
  constexpr char kRightStick[] = "rs:";
  const bool left = std::strncmp(verb, kStick, sizeof(kStick) - 1u) == 0;
  const bool right = std::strncmp(verb, kRightStick, sizeof(kRightStick) - 1u) == 0;
  if (left || right) {
    Direction direction = Direction::kNone;
    if (!ParseDirection(verb + (left ? sizeof(kStick) : sizeof(kRightStick)) - 1u, direction)) {
      REXLOG_WARN("[fable3-guiworld] '{}' names no direction; dropped", action);
      return false;
    }
    if (left && direction != Direction::kNone) {
      ActionRecord record = MakeRecord(ctx, base, kActionMovement);
      if (!Owner(record.work, base, mode, record.at, player)) {
        REXLOG_INFO("[fable3-guiworld] '{}': player {} does not own this Sanctuary; dropped", action, player);
        return false;
      }
    }
    (left ? g_left : g_right) = direction;
    g_held_player = player;
    return true;
  }

  constexpr char kFlick[] = "flick:";
  if (std::strncmp(verb, kFlick, sizeof(kFlick) - 1u) == 0) {
    Direction direction = Direction::kNone;
    if (!ParseDirection(verb + sizeof(kFlick) - 1u, direction) || direction == Direction::kNone) {
      REXLOG_WARN("[fable3-guiworld] '{}' names no direction; dropped", action);
      return false;
    }
    ActionRecord record = MakeRecord(ctx, base, kActionMovement);
    if (!Owner(record.work, base, mode, record.at, player)) {
      REXLOG_INFO("[fable3-guiworld] '{}': player {} does not own this Sanctuary; dropped", action, player);
      return false;
    }
    const uint32_t code = direction == Direction::kLeft    ? kFlickLeft
                          : direction == Direction::kRight ? kFlickRight
                          : direction == Direction::kDown  ? kFlickDown
                                                           : kFlickUp;
    PostGesture(ctx, base, mode, code);
    REXLOG_INFO("[fable3-guiworld] flick {} for player {}: the gesture poster ran", code, player);
    return true;
  }

  constexpr char kDyeValue[] = "dye-value:";
  if (std::strncmp(verb, kDyeValue, sizeof(kDyeValue) - 1u) == 0) {
    return SetDyeValue(base, mode, verb + sizeof(kDyeValue) - 1u, action);
  }

  REXLOG_WARN("[fable3-guiworld] '{}' names no verb this mode takes; dropped", action);
  return false;
}

void PumpGuiWorld(PPCContext& ctx, uint8_t* base) {
  if (g_left == Direction::kNone && g_right == Direction::kNone) {
    return;
  }
  const uint32_t mode = ModeUp(base);
  if (mode == 0u) {
    LetGo();
    return;
  }
  const uint32_t frame = Call(ctx, base, kWorldFrame, 0u);
  if (g_left != Direction::kNone) {
    seam::StoreU32(base, mode + StickStamp(g_left), frame);
  }
  if (g_right == Direction::kUp) {
    seam::StoreU32(base, mode + StampRsUp(), frame);
  } else if (g_right == Direction::kDown) {
    seam::StoreU32(base, mode + StampRsDown(), frame);
  } else if (g_right == Direction::kLeft || g_right == Direction::kRight) {
    ActionRecord record = MakeRecord(ctx, base, kActionMovement + 1u);
    if (base[mode + kModeActive] != 0u && Gate(record.work, base, mode, record.at, g_held_player)) {
      PostGesture(ctx, base, mode, g_right == Direction::kLeft ? kStickHeldLeft : kStickHeldRight);
    }
  }
}

}  // namespace refii::fable3

namespace {

/// What one fill stored, gathered from its midasm sites.
struct Filled {
  refii::vfs::GuiNativeArgument rows;
  uint32_t count = 0u;
  int32_t cursor = 0;
  uint32_t holds = 0u;
};
thread_local Filled* t_filled = nullptr;

/// Runs one of the mode's two fillers and states what it stored, in one statement.
void Fill(PPCContext& ctx, uint8_t* base, PPCFunc* original, const char* what) {
  const uint32_t mode = ctx.r3.u32;
  const bool stating = base != nullptr && ctx.r4.u32 != 0u && IsGuiWorld(base, mode);
  if (!stating) {
    original(ctx, base);
    return;
  }
  Filled filled;
  filled.rows.kind = refii::vfs::GuiNativeArgument::Kind::kTable;
  t_filling = mode;
  t_base = base;
  t_filled = &filled;
  original(ctx, base);
  t_filling = 0u;
  t_base = nullptr;
  t_filled = nullptr;
  if (g_mode.exchange(mode) != mode) {
    LetGo();
  }
  const uint32_t hero = Call(ctx, base, kEntitySentBy, seam::Word(base, mode + kModeIm));
  const int32_t player = modlua::PlayerOf(base, hero);
  Statement("@Interaction.World.Parsed")
      .Add(Number(static_cast<float>(player)))
      .Add(Number(static_cast<float>(filled.cursor)))
      .Add(Number(static_cast<float>(filled.holds)))
      .Add(std::move(filled.rows))
      .Post();
  REXLOG_INFO("[fable3-guiworld] the Sanctuary's {} filled the mode for player {}: {} row(s) kept, cursor {}, holds {:#x}",
              what, player, filled.count, filled.cursor, filled.holds);
}

}  // namespace

/// The table parser (mode, table).
REX_EXTERN(rex_InteractionGuiWorld_Parse) { Fill(ctx, base, __imp__rex_InteractionGuiWorld_Parse, "table"); }

/// The partner's copy (mode, packet).
REX_EXTERN(rex_InteractionGuiWorld_Receive) {
  Fill(ctx, base, __imp__rex_InteractionGuiWorld_Receive, "partner's table");
}

/// A row the filler keeps (r3 the rows vector, r4 the row): its five strings, as `<n>.TextTag` ….
void RefiiF3GuiWorldRow(PPCRegister& rows, PPCRegister& row) {
  static constexpr const char* kFields[] = {"TextTag", "Icon", "Message", "SelectedTag", "SelectedHeldTag"};
  const uint8_t* base = t_base;
  Filled* filled = t_filled;
  if (t_filling == 0u || base == nullptr || filled == nullptr || rows.u32 != t_filling + RowVector() ||
      !Plausible(row.u32)) {
    return;
  }
  const std::string prefix = std::to_string(++filled->count) + ".";
  for (uint32_t field = 0u; field < 5u; ++field) {
    refii::vfs::GuiNativeArgument::Field entry;
    entry.name = prefix + kFields[field];
    entry.kind = refii::vfs::GuiNativeArgument::Kind::kString;
    entry.text = ReadCharString(base, row.u32 + 4u * field);
    filled->rows.fields.push_back(std::move(entry));
  }
}

/// The cursor the filler stores (r11 the row, rMode the mode).
void RefiiF3GuiWorldCursor(PPCRegister& cursor, PPCRegister& mode) {
  if (t_filling != 0u && t_filled != nullptr && mode.u32 == t_filling) {
    t_filled->cursor = static_cast<int32_t>(cursor.u32);
  }
}

/// A slot's P_Fill as the filler stores it (the byte, the slot): bit `slot` of the holds (A B X Y).
void RefiiF3GuiWorldFill(PPCRegister& fill, PPCRegister& slot) {
  if (t_filling == 0u || t_filled == nullptr || slot.u32 < t_filling + FirstSlot()) {
    return;
  }
  const uint32_t at = slot.u32 - t_filling - FirstSlot();
  if (at % kSlotSize != 0u || at / kSlotSize >= 4u) {
    return;
  }
  const uint32_t bit = 1u << (at / kSlotSize);
  t_filled->holds = (fill.u32 & 0xFFu) != 0u ? (t_filled->holds | bit) : (t_filled->holds & ~bit);
}

/// vf0 (mode, flags): the mode freed.
REX_EXTERN(rex_InteractionGuiWorld_Destroy) {
  uint32_t freed = ctx.r3.u32;
  if (freed != 0u && g_mode.compare_exchange_strong(freed, 0u)) {
    LetGo();
    Statement("@Interaction.World.Closed").Post();
    REXLOG_INFO("[fable3-guiworld] the Sanctuary's mode freed");
  }
  __imp__rex_InteractionGuiWorld_Destroy(ctx, base);
}

/// vf7 (mode, action*, player).
REX_EXTERN(rex_InteractionGuiWorld_Input) {
  if (t_lever) {
    __imp__rex_InteractionGuiWorld_Input(ctx, base);
    return;
  }
  const uint32_t mode = ctx.r3.u32;
  const uint32_t action = base != nullptr && Plausible(ctx.r4.u32) ? LoadGuestU32(base, ctx.r4.u32) : 0u;
  const bool consequence = action == kActionPause || (action >= kActionA && action <= kActionY);
  if (!consequence) {
    t_pad_vf7 = true;
    __imp__rex_InteractionGuiWorld_Input(ctx, base);
    t_pad_vf7 = false;
    return;
  }
  static std::atomic<bool> said{false};
  LogOnce(said, "the guest's pad reached the Sanctuary's HandleInput: answered, nothing posted (Godot's levers post)");
  uint32_t answer = 0u;
  if (seam::Live(base, PresserHero(ctx, base, ctx.r5.u32))) {
    switch (action) {
      case kActionPause: answer = GuiCurrent(ctx, base) ? 1u : 0u; break;
      case kActionA: answer = Labelled(base, mode, LabelA()) ? 1u : 0u; break;
      case kActionB: answer = 1u; break;
      case kActionX: answer = Labelled(base, mode, LabelX()) ? 1u : 0u; break;
      case kActionY: answer = Labelled(base, mode, LabelY()) ? 1u : 0u; break;
      default: break;
    }
  }
  ctx.r3.u64 = answer;
}

/// The gesture poster (mode, code): withheld when the guest's pad calls it from inside vf7.
REX_EXTERN(rex_InteractionGuiWorld_Gesture) {
  if (t_pad_vf7) {
    static std::atomic<bool> said{false};
    LogOnce(said, "the guest's pad's right-stick gesture withheld (Godot's `rs:` lever posts it)");
    return;
  }
  __imp__rex_InteractionGuiWorld_Gesture(ctx, base);
}

namespace {

/// A bar's answer to the guest's pad while the Sanctuary's mode is up, by its table.
bool BarAnswersPad(PPCContext& ctx, const uint8_t* base, bool bumper) {
  if (t_lever || ModeUp(base) == 0u) {
    return false;
  }
  const uint32_t action = Plausible(ctx.r4.u32) ? LoadGuestU32(base, ctx.r4.u32) : 0u;
  const uint32_t first = bumper ? 88u : 86u;
  const uint32_t at = action - first;
  // Cases 0, 1, 4, 5 swallow the press, hold, …; 8 and 9 are the two release codes.
  const bool taken = action >= first && at <= 9u && (at == 0u || at == 1u || at == 4u || at == 5u || at == 8u || at == 9u);
  ctx.r3.u64 = taken ? 1u : 0u;
  static std::atomic<bool> said{false};
  LogOnce(said, "the guest's pad reached the Sanctuary's bumper / trigger bar: answered, nothing posted");
  return true;
}

}  // namespace

/// COverlayBumperButtons vf4 (bar, action*, player).
REX_EXTERN(rex_OverlayBumper_Input) {
  if (BarAnswersPad(ctx, base, true)) {
    return;
  }
  __imp__rex_OverlayBumper_Input(ctx, base);
}

/// COverlayTriggerButtons vf4 (bar, action*, player).
REX_EXTERN(rex_OverlayTrigger_Input) {
  if (BarAnswersPad(ctx, base, false)) {
    return;
  }
  __imp__rex_OverlayTrigger_Input(ctx, base);
}
