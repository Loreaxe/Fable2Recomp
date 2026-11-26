// refii - Fable 3's display boxes, the pause screen's message box and the menu box: stated at
// the calls that make, show, drop and close them, for Godot's Fable3GuiNatives / Fable3Boxes,
// which draw them and own their pad. Godot's answer comes back as a guest tick action carrying the
// player who pressed, and is handed to the box's OWN pad handler on the guest's thread with that
// player in r5 (AnswerBoxAction), so each class's and each build's own gates decide; the guest's
// pad never reaches a handler of a box Godot owns.
//
// THE PLAYER IN r5 is the pressing pad's local player, 1 or 2: the GUI input object's slot 4
// (`lwz r11,8(r3)` / `lwz r3,0x24(r11)`, TU3 0x82295BA0 / disc 0x822978E0) answers it for the
// router's caller (TU3 0x82601B9C..0x82601BB4), the numeric adjuster picks player record 1 for it
// and record 2 for anything else (`cmpwi r5,1`, TU3 0x8287A1F0 / disc 0x8281C488), and the menu
// box's mapper selects only for 1 (`cmpwi r4,1`, TU3 0x82B512D4 / disc the same body at
// sub_82B13220).
//
// THE BOX MANAGER BM = *(*(GUI+0x1C)+0x30) (GUI = *0x83596F84) holds two queues of 28 bytes at
// BM+4 (Q0: messages, questions, adjusters, quest boxes) and BM+32 (Q1: intermission and info
// cards, button boxes). Queue q: +4/+8 the pending vector's begin/end (8-byte {Box*, count*}
// entries), +20 the SHOWING box's shared_ptr. Its writers:
//   - the push sub_82623BF0(q, shared_ptr<Box>) — every creator's one door (93 callers: the
//     `GUI.Display*` natives, the C++ queries, the cards) — appends at q+4 through sub_82458000
//     (`v3[1] += 8` at 0x82458114), except a card of type 6/7/8 whose name is already queued,
//     which it drops (0x82623C6C..0x82623C88). The box is complete there: its creator's ctor
//     wrote every field from its own arguments.
//   - the queue's tick sub_82285AE8(q) (0x821B671C / disc 0x821B660C): a showing box whose vt[5]
//     Update answers 0 is released (disc 0x82287910, `*(q+20) = 0` at disc 0x82287918) — the one exit for
//     lifetime, Hide and RemoveDisplayBox; with none showing and a pending box, unless the menu
//     box is open (`*(SM+0x34)` +12, or its close timer +4 > 0), the queue's own vt[0]
//     (sub_82624028 for Q0) takes the front into q+20 (0x82624058) and calls the box's vt[1]
//     Show (0x82624094) — its only caller is this tick (disc 0x822879D4) — then erases the front.
//   - the pending erase sub_826241C0(out, vector, iterator) (the tick's at disc 0x822879E4,
//     RemoveDisplayBox's sub_82623CC0 at 0x82623E5C, RemoveAllDisplayBoxesWithName's sub_82623E78
//     at 0x82623FF4): `*(vector+4) -= 8` at 0x82624218.
//
// THE BOX (base ctor sub_82958000(box, name, text)): +0 vtable, +4 name CCharString (0x8295802C),
// +8 resolved text LHString (0x82958038), +12 shown. The class is its vtable, and slot 4 is its
// pad handler HandleInput(box, ev*, player), reached only through sub_825F38F8(BM, ev) — Q0's
// showing box, else Q1's (0x825F397C; the handler's answer is not read). The classes Godot owns:
//   - message disc off_820B45C0 (CMessageBox_HandleInput sub_82878F88: 73 GUI_CANCEL plays the cancel
//     cue and closes it, disc 0x8281B3D0..disc 0x8281B404; 76 with the download form +33 opens the
//     marketplace; both only when `+28` is -1 or the player, disc 0x8281B3BC);
//   - yes/no TU3 off_820B9A78 / disc off_820B45E0 (+28 = -1, +32/+36 the yes/no tags:
//     Lua_GUI_DisplayYesNoQuestion 0x82548A38..disc 0x82518E30): 41 answers 1, 42 answers 0
//     (TU3 `li r4,1` 0x82879AB4 / `li r4,0` 0x82879A9C; disc 0x8281BD54 / 0x8281BD38) through the
//     class's own answer — TU3 sub_82879AD8(box, answer), which refuses a 0 on a box whose +41 is
//     set (`lbz r11,0x29(r3)` 0x82879AF4); disc sub_8281BD88(box, answer). It posts message event
//     68 with the answer and player one's hero (`li r4,0x44` / `li r8,0x65` / `mr r9,r29`, TU3
//     0x82879B54..0x82879B6C) and closes the box through its vt[2] (0x82879B70..0x82879B80);
//   - the natal query TU3 off_820DB278 / disc off_820D8868: the same two actions through TU3
//     sub_82B93DB0(box, answer) (event 68 at 0x82B93E30), on the disc through sub_8281BD88 — one
//     function for both classes there;
//   - the numeric adjuster off_820B9A9C (ctor sub_82879C28(box, text, min, max, increment,
//     value, …): +100 min, +104 max, +108 increment, +112 value, 0x82879C94..0x82879CA0): its
//     handler sub_8287A1B0 (disc sub_8281C448, the same body) takes the pad only when +200 is -1 or
//     the player and +12 is set (0x8287A1C4..0x8287A1E8) and the player's hero exists
//     (0x8287A20C..0x8287A220), then 41/42 finish it through sub_8287A3A0(box, accept)
//     (0x8287A36C / 0x8287A358), which posts event 69 (accept) or 70 with `box[28]` = +112
//     (0x8287A3F0..0x8287A434) and closes it;
//   - the quest box off_820B9AC0 (CQuestInfoBox_HandleInput sub_8287A8E8: 73/61 post event 67 and
//     close, 0x8287A958..0x8287A96C);
//   - the tutorial notice disc off_82091964 (CTutorialBox_HandleInput sub_824C13F0: 41 closes);
//   - the info card CAcceptHoldBox off_820D9BB0 / disc off_820D71CC (its HandleInput sub_82B8BD28 /
//     disc sub_82B57178, the same body: no shown or player test). With A shown (+40) and not held
//     (+42 = 0), 74 posts PostButtonAnswer(1) (`li r3,1` / `bl` 0x82B8BDEC..0x82B8BDF0); with A held,
//     74 and 82 only set the hold H+8 (`stb r11,8(r10)` 0x82B8BDA0 / 0x82B8BDD4) and 79 clears it; with
//     Y shown (+41), 77 posts PostButtonAnswer(4) (0x82B8BE0C..0x82B8BE10). Its hold is Godot's clock
//     (display_box_stubs.cpp drops the guest's): `accept` on a held card is PostButtonAnswer(1) itself,
//     sub_824D1BC0 / disc sub_8291F060 — `PostEvent(MS, 64, zero16, 0, 0, 101, code)` (`li r4,0x40`
//     0x824D1BF4, `bl sub_821CFA18` 0x824D1C08) — the call the guest's update makes when its fill
//     crosses 1.0 (0x82B8BD00..0x82B8BD04). The accept does not close the card: the script removes it;
//   - the button box CButtonBox off_820DB200 / disc off_820D87F4 (HandleInput sub_82B93850 / disc
//     sub_82B5ECE0, the same body, no shown or player test): the RELEASE codes, 79 A → 1, 80 B → 2,
//     78 X → 3, 81 Y → 4 (`li r3,1` 0x82B93884, 2 0x82B938A0, 3 0x82B938BC, 4 0x82B938D8, each then
//     `bl sub_824D1BC0`); its Hide posts 0 every time (0x82B93830).
//
// THE PAUSE SCREEN'S MESSAGE BOX is not a queued box. sub_825F9F38(G, text LHString, download)
// — reached only from sub_823CDB48 (0x823CDC08) when the top mode is the GUI's — runs when
// G+36 is 1 (0x825F9F58): +36 = 2 (0x825F9F6C), +40 = screen 42 `paper.MessageBox` (0x825F9F74),
// +88 the download flag (0x825F9F60), and the text through sub_828791C8 (0x825FA04C). The
// screen input sub_825F9408(SM, ev, player) (SM = *(GUI+0x1C), 0x825F9428) answers it while
// `*(SM+40) == 42`: 72 GUI_SELECT runs sub_825FA0C8(SM, 2) (0x825F9444), whose reset
// sub_825FA150 (+36 = 1, +40 = 0 at 0x825FA164 / 0x825FA16C) drops it; 76 with +88 opens the
// marketplace (0x825F9460). sub_823CDB48 also resets an older one first (disc 0x823AB7CC).
//
// THE MENU BOX MB = *(SM+0x34): +0 the highlighted row, +12 open, +13 cancelled, +14 cancelable,
// +16/+20 the rows (8-byte shared_ptrs to {+0 TextTag CCharString, +4 Enabled}), +32 the title,
// the RESOLVED text LHString (sub_823CBB30 resolves the tag, 0x82548244 / 0x82562F6C). Its
// writers: the row add sub_82621CF0(MB, &tag) while closed and under six rows (0x82621CFC,
// 0x82621D30; enabled 1 at 0x82621D58); the row enable sub_82621E48(MB, index, enabled)
// (0x82621E9C); the title and cancelable by its two fillers (DisplayMenuBox's sub_825481C8:
// title 0x82548250, cancelable `+14 = a2` 0x82548274; Lua_SleepMenu_ShowSleepMenu: title
// 0x82562F78, rows SLEEP_FOR_*); the open sub_826220A8(MB) returns 1 when it opened (`+12 = 1`
// at 0x8262227C). Its pad handler sub_82621EB8(MB, ev, pad) (one caller, sub_82622848) maps the
// event through sub_82B51118 (0x82621EF8) — 109 up, 114 down, 1 cancel, 28 select — and moves
// the row with sub_82621DC0(MB, row) (0x82621FDC / 0x82622060). Select is sub_826226D8(MB): an
// enabled row clears +13 and closes (0x8262276C..0x82622770); cancel, when +14, sets +13 and
// closes (0x82621F78..0x82621F7C). The close sub_826222A8(MB) posts event 65 with
// `cancelled ? 0 : row + 1` (0x82622348).

#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "box_answers.h"
#include "gui_statements.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_BoxQueue_Push);
REX_EXTERN(__imp__rex_BoxQueue_Update);
REX_EXTERN(__imp__rex_BoxVector_Erase);
REX_EXTERN(__imp__rex_MessageBox_HandleInput);
REX_EXTERN(__imp__rex_QueryBox_HandleInput);
REX_EXTERN(__imp__rex_NatalQueryBox_HandleInput);
REX_EXTERN(__imp__rex_NumericAdjuster_HandleInput);
REX_EXTERN(__imp__rex_QuestBox_HandleInput);
REX_EXTERN(__imp__rex_TutorialBox_HandleInput);
REX_EXTERN(__imp__rex_AcceptHoldBox_HandleInput);
REX_EXTERN(__imp__rex_ButtonBox_HandleInput);
REX_EXTERN(__imp__rex_GuiManager_ScreenInput);
REX_EXTERN(__imp__rex_GuiManager_ModalReset);
REX_EXTERN(__imp__rex_MenuBox_Open);
REX_EXTERN(__imp__rex_MenuBox_Close);
REX_EXTERN(__imp__rex_MenuBox_Input);
REX_EXTERN(__imp__rex_MenuBox_AddRow);
REX_EXTERN(__imp__rex_MenuBox_SetEnabled);
REX_EXTERN(__imp__rex_ScreenManager_Destroy);
REX_EXTERN(rex_MessageBox_HandleInput);
REX_EXTERN(rex_QueryBox_HandleInput);
REX_EXTERN(rex_NatalQueryBox_HandleInput);
REX_EXTERN(rex_NumericAdjuster_HandleInput);
REX_EXTERN(rex_QuestBox_HandleInput);
REX_EXTERN(rex_TutorialBox_HandleInput);
REX_EXTERN(rex_AcceptHoldBox_HandleInput);
REX_EXTERN(rex_ButtonBox_HandleInput);
REX_EXTERN(rex_Box_PostButtonAnswer);
REX_EXTERN(rex_GuiManager_ScreenInput);
REX_EXTERN(rex_MenuBox_Close);
REX_EXTERN(rex_MenuBox_Highlight);
REX_EXTERN(rex_MenuBox_Accept);
REX_EXTERN(rex_GuiInput_MenuAction);

namespace {

using namespace refii::fable3;

const refii::guest::Addr kGuiManager{0x83596F84u};
const refii::guest::Addr kGame{0x83597034u};
constexpr uint32_t kQueuePendingEnd = 8u;
constexpr uint32_t kQueueShowing = 20u;
// A queue's size: Q0 at BM+4, Q1 after it (TU3 `addi r3,r31,0x28` 0x821B6748, disc 0x20 at
// disc 0x821B6608: TU3 added a word; the pending vector and the showing box stay at +4/+8 and +20).
uint32_t QueueStride() { return refii::guest::Pick(36u, 28u); }

constexpr uint32_t kBoxName = 4u;
constexpr uint32_t kBoxText = 8u;

/// The box classes, by vtable (Fable 3 TU3 IDB).
constexpr uint32_t kVtMessage = 0x820B9A30u;  // classified by ToTu3
constexpr uint32_t kVtYesNo = 0x820B9A78u;  // classified by ToTu3
constexpr uint32_t kVtNatalQuery = 0x820DB278u;  // classified by ToTu3
constexpr uint32_t kVtAdjuster = 0x820B9A9Cu;  // classified by ToTu3
constexpr uint32_t kVtQuest = 0x820B9AC0u;  // classified by ToTu3
constexpr uint32_t kVtMarriage = 0x820B9AE4u;  // classified by ToTu3
constexpr uint32_t kVtIntermission = 0x820DB1DCu;  // classified by ToTu3
/// CAcceptHoldBox (type 8; the PC RTTI class map: `Paper.HoldButton`, `Paper.Accept`).
constexpr uint32_t kVtInfoCard = 0x820D9BB0u;  // classified by ToTu3
constexpr uint32_t kVtButtonBox = 0x820DB200u;  // classified by ToTu3
constexpr uint32_t kVtWeddingPicker = 0x820DAFA0u;  // classified by ToTu3
/// Q0's CTutorialBox (type 26, ctor sub_824C0388: its Show hides the B button and both natal
/// choices, disc 0x824B27C0..disc 0x824B27E8; its input takes only 41, 0x824C140C) and Q1's CAcceptBox
/// (type 6, built by the card factory disc sub_825FB988 case 6; its input slot is `li r3, 0`) — class
/// names from the PC build's RTTI (scratch f3/gaps/rtti/classmap.tsv).
constexpr uint32_t kVtNotice = 0x82092D7Cu;  // classified by ToTu3
constexpr uint32_t kVtDisplay = 0x820D9BD4u;  // classified by ToTu3
/// TU3 only: GUI.DisplayNonInteractiveMessageBox's box (native sub_82545F48, vtable stored at
/// 0x82545FF8): a CMessageBox (ctor sub_82878AE0, name "Unnamed", the text its one argument
/// resolves) whose input slot is `li r3,0` (0x82F5D180), type 28 (slot 7 0x82372790), shown as the
/// message box with `paper.Buttons` hidden (0x82879570..0x82879578). It goes when RemoveDisplayBox
/// closes it. The disc has no such class (and no row: the address is data there).
constexpr uint32_t kVtNonInteractive = 0x820B9A54u;
bool Tu3() { return refii::guest::Current() == refii::guest::Build::kTu3; }

/// The box-manager SCREENS, by vtable: each class's slot 7 returns its box type (`li r3, N` at
/// the function the vtable's +28 names) and its constructor stores the vtable — Options
/// 0x820A0B10 (disc sub_825C95F0 at disc 0x825C9680, type 15), hero status 0x820C69DC (0x825516EC, 13),
/// promises 0x820B3160 (0x827DA788, 12), stats 0x820C6398 (0x829B5AB0, 20), quest summary
/// 0x820C6790 (0x829BC060, 27), king's schedule 0x820C6658 (0x829BB810, 25).
constexpr uint32_t kVtOptions = 0x820A0B10u;  // classified by ToTu3
constexpr uint32_t kVtHeroStatus = 0x820C69DCu;  // classified by ToTu3
constexpr uint32_t kVtPromises = 0x820B3160u;  // classified by ToTu3
constexpr uint32_t kVtStats = 0x820C6398u;  // classified by ToTu3
constexpr uint32_t kVtQuestSummary = 0x820C6790u;  // classified by ToTu3
constexpr uint32_t kVtKingsSchedule = 0x820C6658u;  // classified by ToTu3

/// The rest of the box-manager screens, each writing its own .bgf's elements (the presentation
/// slot is sub_829588C8's switch on the type; the slot's file is sub_82B73E68's table):
/// join / visibility options 0x820C6B58 (ctor sub_82551788, ONE class whose slot 7 returns its
/// +48 — 18 JoinOptions.bgf, 19 Visability.bgf, `lwz r3, 0x30(r3)` at 0x82A4B7D0), friends
/// channel 0x820C6BE0 (sub_82551950, 21), chat channel 0x820C64CC (sub_82550730, 22), the
/// treasury statement 0x820DAE9C (sub_82B8E898, 17), the bed menu 0x820D9EF8 (sub_82623738, 14),
/// the food query 0x820D9CE0 (sub_826232B0 / sub_82623388, 9), the wedding picker 0x820DAFA0
/// (sub_82B90948, 23), co-op partners 0x820DB154 (sub_82B91228, 24), the marriage proposal
/// 0x820B9AE4 (sub_82623610, 10) and the divorce message 0x820C4660 (sub_8297EDB8, 11).
constexpr uint32_t kVtJoinOptions = 0x820C6B58u;  // classified by ToTu3
constexpr uint32_t kVtFriendsChannel = 0x820C6BE0u;  // classified by ToTu3
constexpr uint32_t kVtChatChannel = 0x820C64CCu;  // classified by ToTu3
constexpr uint32_t kVtTreasury = 0x820DAE9Cu;  // classified by ToTu3
constexpr uint32_t kVtBedMenu = 0x820D9EF8u;  // classified by ToTu3
constexpr uint32_t kVtFoodQuery = 0x820D9CE0u;  // classified by ToTu3
constexpr uint32_t kVtPartners = 0x820DB154u;  // classified by ToTu3
constexpr uint32_t kVtDivorce = 0x820C4660u;  // classified by ToTu3

/// The join/visibility class's box type (its slot 7 reads +48).
constexpr uint32_t kJoinOptionsType = 48u;
constexpr uint32_t kTypeVisibility = 19u;

/// The message box's download form (DisplayDownloadMessageBox, sub_82546080): +33 set (0x8254612C)
/// shows `paper.MessageBox.Action` and names its X button by the tag at +36 (disc sub_8281AFA8 at
/// disc 0x8281B048 / disc 0x8281B07C); X (76) is taken only while +33 is set (sub_82878F88, disc 0x8281B410).
constexpr uint32_t kMessageDownload = 33u;
constexpr uint32_t kMessageActionTag = 36u;

/// The adjuster's answer reads its value at +112 (`box[28]`, 0x8287A3F0).
constexpr uint32_t kAdjusterValue = 112u;
/// Every box's shown byte (base ctor sub_82958000: +12 = 0; each Show sets it, each Hide clears it).
constexpr uint32_t kBoxShown = 12u;

/// The info card's flags from its params (CAcceptHoldBox_ctor sub_82B8B7A8, 0x82B8B7C8..0x82B8B86C):
/// +40 A shown, +41 Y shown, +42 A must be held — the bytes its handler tests (`lbz r11,0x28(r3)`
/// 0x82B8BD3C, `lbz r11,0x2A(r3)` 0x82B8BD50, `lbz r11,0x29(r31)` 0x82B8BDF4).
constexpr uint32_t kCardShowA = 40u;
constexpr uint32_t kCardShowY = 41u;
constexpr uint32_t kCardHoldA = 42u;
/// PostButtonAnswer's code for the accept.
constexpr uint32_t kButtonAccept = 1u;

/// The game actions the owned handlers take (sub_82878F88, sub_82B93CD8, sub_8287A8E8,
/// sub_824C13F0, sub_825F9408).
constexpr uint32_t kActionYes = 41u;
constexpr uint32_t kActionNo = 42u;
constexpr uint32_t kActionSelect = 72u;
constexpr uint32_t kActionCancel = 73u;
constexpr uint32_t kActionX = 76u;
/// The face buttons' pressed codes (A 74, Y 77) the card takes, and the released codes the button
/// box answers on, by its answer 1..4 (A 79, B 80, X 78, Y 81: `addi r11,r11,-0x4E` and its jump,
/// 0x82B93860..0x82B93880).
constexpr uint32_t kActionAPressed = 74u;
constexpr uint32_t kActionYPressed = 77u;
constexpr uint32_t kButtonBoxReleased[4] = {79u, 80u, 78u, 81u};

/// The pause screen's message box: SM+40 is 42 while it is up (0x825F9F74, read at 0x825F9434).
constexpr uint32_t kScreenManager = 0x1Cu;
constexpr uint32_t kModalScreenWord = 40u;
constexpr uint32_t kModalScreen = 42u;
/// The queue index the modal box is stated under: it is no queue's.
constexpr int32_t kModalQueue = -1;

constexpr uint32_t kMenuOpen = 12u;
constexpr uint32_t kMenuCancelled = 13u;
constexpr uint32_t kMenuCancelable = 14u;
constexpr uint32_t kMenuTitle = 32u;
constexpr uint32_t kMenuMaxRows = 6u;
/// sub_82B51118's actions the menu box's handler takes (0x82621F00..0x82621F14).
constexpr int32_t kMenuActionCancel = 1;
constexpr int32_t kMenuActionSelect = 28;
constexpr int32_t kMenuActionUp = 109;
constexpr int32_t kMenuActionDown = 114;

const char* KindOf(const uint8_t* base, uint32_t box, uint32_t vtable) {
  switch (refii::guest::ToTu3(vtable)) {
    case kVtMessage: return "message";
    case kVtNonInteractive: return Tu3() ? "noninteractive" : "unknown";
    case kVtYesNo: return "question";
    case kVtNatalQuery: return "question";
    case kVtNotice: return "notice";
    case kVtDisplay: return "display";
    case kVtAdjuster: return "adjuster";
    case kVtQuest: return "quest";
    case kVtIntermission: return "intermission";
    case kVtInfoCard: return "card";
    case kVtButtonBox: return "buttons";
    case kVtJoinOptions:
      return LoadGuestU32(base, box + kJoinOptionsType) == kTypeVisibility ? "screen:visibility" : "screen:join";
    case kVtFriendsChannel: return "screen:friends";
    case kVtChatChannel: return "screen:chat";
    case kVtTreasury: return "screen:treasury";
    case kVtBedMenu: return "screen:bed";
    case kVtFoodQuery: return "screen:food";
    case kVtWeddingPicker: return "screen:wedding";
    case kVtPartners: return "screen:partners";
    case kVtMarriage: return "screen:proposal";
    case kVtDivorce: return "screen:divorce";
    case kVtOptions: return "screen:options";
    case kVtHeroStatus: return "screen:herostatus";
    case kVtPromises: return "screen:promises";
    case kVtStats: return "screen:stats";
    case kVtQuestSummary: return "screen:quests";
    case kVtKingsSchedule: return "screen:schedule";
    default: return "unknown";
  }
}

/// Which of the manager's two queues `queue` is, or -1.
int32_t QueueIndex(const uint8_t* base, uint32_t queue) {
  const uint32_t gui = LoadGuestU32(base, kGuiManager);
  if (!Plausible(gui)) {
    return -1;
  }
  const uint32_t screens = LoadGuestU32(base, gui + kScreenManager);
  if (!Plausible(screens)) {
    return -1;
  }
  const uint32_t manager = LoadGuestU32(base, screens + 0x30u);
  if (!Plausible(manager) || queue < manager + 4u) {
    return -1;
  }
  const uint32_t offset = queue - (manager + 4u);
  return offset % QueueStride() == 0u && offset / QueueStride() < 2u ? static_cast<int32_t>(offset / QueueStride()) : -1;
}

/// `@Box.Queued(queue, box, kind, name, text, …)` — the creator's fields as the push receives
/// the box, by kind: message download, actionTag; question yesTag, noTag; adjuster min, max,
/// increment, value; card title, acceptTag, helpTag, showA, showY.
void PostQueued(const uint8_t* base, int32_t queue, uint32_t box) {
  const uint32_t vtable = LoadGuestU32(base, box);
  Statement queued("@Box.Queued");
  queued.Add(Number(static_cast<float>(queue)))
      .Add(Text(Hex(box)))
      .Add(Text(KindOf(base, box, vtable)))
      .Add(Text(ReadCharString(base, box + kBoxName)))
      .Add(Text(ReadLhString(base, box + kBoxText)));
  switch (refii::guest::ToTu3(vtable)) {
    case kVtMessage:
    case kVtNonInteractive:
      queued.Add(Boolean(base[box + kMessageDownload] != 0u)).Add(Text(ReadCharString(base, box + kMessageActionTag)));
      break;
    case kVtYesNo:
    case kVtNatalQuery:
      queued.Add(Text(ReadCharString(base, box + 32u))).Add(Text(ReadCharString(base, box + 36u)));
      break;
    case kVtAdjuster:
      queued.Add(Number(static_cast<float>(static_cast<int32_t>(LoadGuestU32(base, box + 100u)))))
          .Add(Number(static_cast<float>(static_cast<int32_t>(LoadGuestU32(base, box + 104u)))))
          .Add(Number(static_cast<float>(static_cast<int32_t>(LoadGuestU32(base, box + 108u)))))
          .Add(Number(static_cast<float>(static_cast<int32_t>(LoadGuestU32(base, box + kAdjusterValue)))));
      break;
    case kVtInfoCard:
      queued.Add(Text(ReadCharString(base, box + 36u)))
          .Add(Text(ReadCharString(base, box + 44u)))
          .Add(Text(ReadCharString(base, box + 48u)))
          .Add(Boolean(base[box + kCardShowA] != 0u))
          .Add(Boolean(base[box + kCardShowY] != 0u))
          .Add(Boolean(base[box + kCardHoldA] != 0u));
      break;
    default:
      break;
  }
  queued.Post();
}

/// A box on screen, as this file stated it: its class at the push and the last player the
/// guest's own router handed its handler (r5 at the handler's entry), kept for the answer's log line.
struct Showing {
  int32_t queue = -1;
  uint32_t vtable = 0u;
  uint32_t player = 0u;
};

std::mutex g_mutex;
std::unordered_map<uint32_t, uint32_t>* g_queued = nullptr;   // box -> vtable, pushed, not shown
std::unordered_map<uint32_t, Showing>* g_showing = nullptr;  // box -> what it is

std::unordered_map<uint32_t, uint32_t>& Queued() {
  if (g_queued == nullptr) {
    g_queued = new std::unordered_map<uint32_t, uint32_t>();
  }
  return *g_queued;
}

std::unordered_map<uint32_t, Showing>& ShowingBoxes() {
  if (g_showing == nullptr) {
    g_showing = new std::unordered_map<uint32_t, Showing>();
  }
  return *g_showing;
}

/// Set while this file runs a handler with Godot's answer, so the gate lets it through.
thread_local bool t_host_answer = false;

struct HostAnswer {
  HostAnswer() { t_host_answer = true; }
  ~HostAnswer() { t_host_answer = false; }
};

/// The menu box's rows as their adds and enables stated them since its last open or close.
struct MenuRow {
  std::string tag;
  bool enabled = true;
};
uint32_t g_menu = 0u;  // the open menu box, 0 when none
uint32_t g_modal = 0u;  // the pause screen's message box's manager while it is up, else 0
std::vector<MenuRow>* g_menu_rows = nullptr;

std::vector<MenuRow>& MenuRows() {
  if (g_menu_rows == nullptr) {
    g_menu_rows = new std::vector<MenuRow>();
  }
  return *g_menu_rows;
}

/// A copy of `ctx` with the stack pointer moved below the caller's frame; the bytes between the
/// new and the old pointer are this file's scratch (an event block at +0x100).
PPCContext Frame(const PPCContext& ctx, uint32_t bytes = 0x200u) {
  PPCContext work = ctx;
  work.r1.u32 = (ctx.r1.u32 - bytes) & ~0xFu;
  return work;
}

void StoreGuestU32(uint8_t* base, uint32_t address, uint32_t value) {
  const uint32_t swapped = __builtin_bswap32(value);
  std::memcpy(base + address, &swapped, sizeof(swapped));
}

/// Runs a pad handler `handler(box, {action, 0, 0}, player)` with Godot's answer; the handler's own
/// answer (1 when it took the action).
template <typename Handler>
uint32_t RunHandler(PPCContext& ctx, uint8_t* base, Handler handler, uint32_t box, uint32_t action, uint32_t player) {
  PPCContext work = Frame(ctx);
  const uint32_t event = work.r1.u32 + 0x100u;
  StoreGuestU32(base, event, action);
  StoreGuestU32(base, event + 4u, 0u);
  StoreGuestU32(base, event + 8u, 0u);
  StoreGuestU32(base, event + 12u, 0u);
  work.r3.u64 = box;
  work.r4.u64 = event;
  work.r5.u64 = player;
  HostAnswer answering;
  handler(work, base);
  return work.r3.u32;
}

/// The gate every owned handler runs through: Godot's answer passes, the guest's pad is
/// consumed (the router does not read the answer, 0x825F397C) after its player is noted.
template <typename Original>
void Gate(PPCContext& ctx, uint8_t* base, Original original) {
  if (t_host_answer) {
    original(ctx, base);
    return;
  }
  const uint32_t box = ctx.r3.u32;
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    auto it = ShowingBoxes().find(box);
    if (it == ShowingBoxes().end()) {
      // A box this file never stated as shown is not Godot's to answer.
      original(ctx, base);
      return;
    }
    it->second.player = ctx.r5.u32;
  }
  ctx.r3.u64 = 1u;
}

/// The message system's event serial, MS+0x9B0 (disc +0x980), MS = *(*(*(*(GAME+12)+28)+4)+28) —
/// the chain the save menu's accept reads it through (0x829B5618..0x829B562C); 0 when unreadable.
uint32_t MessageSerial(const uint8_t* base) {
  uint32_t at = LoadGuestU32(base, kGame);
  for (uint32_t offset : {0xCu, 0x1Cu, 4u, 0x1Cu}) {
    if (!Plausible(at)) {
      return 0u;
    }
    at = LoadGuestU32(base, at + offset);
  }
  return Plausible(at) ? LoadGuestU32(base, at + refii::guest::Pick(0x9B0u, 0x980u)) : 0u;
}

/// The pause screen's message box is up: SM+40 = 42.
bool ModalUp(const uint8_t* base) {
  const uint32_t gui = LoadGuestU32(base, kGuiManager);
  const uint32_t screens = Plausible(gui) ? LoadGuestU32(base, gui + kScreenManager) : 0u;
  return Plausible(screens) && LoadGuestU32(base, screens + kModalScreenWord) == kModalScreen;
}

}  // namespace

namespace refii::fable3 {

/// screen_stated_hooks.cpp: `@Screen.Fields(box, kind, true, …)` for a box-manager screen's Show.
void StateScreenShown(const uint8_t* base, uint32_t box, const char* kind);

}  // namespace refii::fable3

namespace refii::fable3 {

void StateModalMessageBox(uint32_t manager, const std::string& text, bool download) {
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_modal != 0u) {
      ShowingBoxes().erase(g_modal);
    }
    g_modal = manager;
    ShowingBoxes()[manager] = Showing{kModalQueue, 0u, 0u};
  }
  Statement("@Box.Queued")
      .Add(Number(static_cast<float>(kModalQueue)))
      .Add(Text(Hex(manager)))
      .Add(Text("modal"))
      .Add(Text(""))
      .Add(Text(text))
      .Add(Boolean(download))
      .Add(Text(download ? "GUI_BUTTONS_DOWNLOAD" : ""))
      .Post();
  Statement("@Box.Show").Add(Number(static_cast<float>(kModalQueue))).Add(Text(Hex(manager))).Post();
}

bool BoxIsShowing(uint32_t box) {
  std::lock_guard<std::mutex> lock(g_mutex);
  return ShowingBoxes().count(box) != 0u;
}

bool PadTakenAheadOfTopBox(const uint8_t* base, uint32_t action, uint32_t player) {
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    for (const auto& [box, showing] : ShowingBoxes()) {
      // The pause screen's message box: the screen route ahead of the dispatcher takes the press.
      // A queued box: the router (channel 1) answers "consumed" for 72..85 unless the one showing is
      // an intermission (0x825F39EC..0x825F3A40).
      if (showing.queue == kModalQueue || refii::guest::ToTu3(showing.vtable) != kVtIntermission) {
        return true;
      }
    }
  }
  // The menu box (channel 0): its mapper turns 74 into its select for player one alone (`cmpwi
  // r11,0x4A` 0x82B5133C, `cmpwi r4,1` 0x82B512D4) and its input answers 1 for it.
  const uint32_t menu = g_menu;
  return action == kActionAPressed && player == 1u && menu != 0u && Plausible(menu) && base[menu + kMenuOpen] != 0u;
}

/// The player field of a lever, `<p>:` with p 1 or 2, at `at`; the text after it, or null.
static const char* TakePlayer(const char* at, uint32_t& player) {
  if ((at[0] != '1' && at[0] != '2') || at[1] != ':') {
    return nullptr;
  }
  player = static_cast<uint32_t>(at[0] - '0');
  return at + 2;
}

bool AnswerBoxAction(PPCContext& ctx, uint8_t* base, const char* action) {
  if (base == nullptr || action == nullptr) {
    return false;
  }

  // `menubox:<p>:<row>` (0-based) / `menubox-cancel:<p>`.
  const bool menu_cancel = std::strncmp(action, kMenuBoxCancelAction, sizeof(kMenuBoxCancelAction) - 1u) == 0;
  if (menu_cancel || std::strncmp(action, kMenuBoxAnswerAction, sizeof(kMenuBoxAnswerAction) - 1u) == 0) {
    uint32_t player = 0u;
    const char* rest = nullptr;
    if (!menu_cancel) {
      rest = TakePlayer(action + sizeof(kMenuBoxAnswerAction) - 1u, player);
    } else if (action[sizeof(kMenuBoxCancelAction) - 1u] == ':' &&
               (action[sizeof(kMenuBoxCancelAction)] == '1' || action[sizeof(kMenuBoxCancelAction)] == '2')) {
      player = static_cast<uint32_t>(action[sizeof(kMenuBoxCancelAction)] - '0');
      rest = action + sizeof(kMenuBoxCancelAction) + 1u;
    }
    if (rest == nullptr) {
      REXLOG_WARN("[fable3-boxes] menu box answer '{}' names no player; dropped", action);
      return false;
    }
    const uint32_t menu = g_menu;
    if (menu == 0u || !Plausible(menu) || base[menu + kMenuOpen] == 0u) {
      REXLOG_WARN("[fable3-boxes] menu box answer '{}' with no menu box open; dropped", action);
      return false;
    }
    PPCContext work = Frame(ctx);
    work.r3.u64 = menu;
    if (menu_cancel) {
      if (base[menu + kMenuCancelable] == 0u) {
        return false;
      }
      // The guest's own cancel, any player's: `+13 = 1` then the close (0x82621F78..0x82621F7C).
      base[menu + kMenuCancelled] = 1u;
      rex_MenuBox_Close(work, base);
      return true;
    }
    // The guest selects for player one alone: its mapper turns 72 / 74 into the select only when
    // the pad is 1 (`cmpwi r4,1` / `bne`, TU3 0x82B512D4..0x82B512D8; disc sub_82B13220, the same body).
    if (player != 1u) {
      REXLOG_INFO("[fable3-boxes] menu box answer '{}': the guest selects for player one only; refused", action);
      return false;
    }
    const long row = std::strtol(rest, nullptr, 10);
    if (row < 0 || row >= static_cast<long>(kMenuMaxRows)) {
      return false;
    }
    work.r4.u64 = static_cast<uint32_t>(row);
    rex_MenuBox_Highlight(work, base);
    work = Frame(ctx);
    work.r3.u64 = menu;
    rex_MenuBox_Accept(work, base);
    return true;
  }

  // `box:<p>:<HEX>:<verb>[:<value>]`.
  if (std::strncmp(action, kBoxAnswerAction, sizeof(kBoxAnswerAction) - 1u) != 0) {
    return false;
  }
  uint32_t player = 0u;
  const char* at = TakePlayer(action + sizeof(kBoxAnswerAction) - 1u, player);
  if (at == nullptr) {
    REXLOG_WARN("[fable3-boxes] answer '{}' names no player; dropped", action);
    return false;
  }
  char* end = nullptr;
  const uint32_t box = static_cast<uint32_t>(std::strtoul(at, &end, 16));
  if (end == nullptr || *end != ':') {
    return false;
  }
  const std::string verb_and_value = end + 1;
  const size_t colon = verb_and_value.find(':');
  const std::string verb = verb_and_value.substr(0, colon);
  const long value = colon == std::string::npos ? 0 : std::strtol(verb_and_value.c_str() + colon + 1, nullptr, 10);

  Showing showing;
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    auto it = ShowingBoxes().find(box);
    if (it == ShowingBoxes().end()) {
      REXLOG_WARN("[fable3-boxes] answer '{}' for a box not on screen; dropped", action);
      return false;
    }
    showing = it->second;
  }
  REXLOG_INFO("[fable3-boxes] answer '{}' as player {} (the guest's router last handed this box player {})", action,
              player, showing.player);

  if (showing.queue == kModalQueue) {
    if (!ModalUp(base)) {
      return false;
    }
    // sub_826019F0 passes SM = *(GUI+0x1C), the action itself (not an event block) and the
    // player (0x82601A98, 0x82601B74). The player (`mr r27,r5` 0x825F9420) goes only to a screen
    // object's slot 6 past the modal's branch (`mr r5,r27` 0x825F95EC; disc 0x825CE564): the
    // modal's 72 and 76 do not read it.
    PPCContext work = Frame(ctx);
    work.r3.u64 = LoadGuestU32(base, LoadGuestU32(base, kGuiManager) + kScreenManager);
    work.r4.u64 = verb == "action" ? kActionX : kActionSelect;
    work.r5.u64 = player;
    HostAnswer answering;
    rex_GuiManager_ScreenInput(work, base);
    return true;
  }

  switch (refii::guest::ToTu3(showing.vtable)) {
    case kVtYesNo:
      // Through the box's own input, so each build's rule decides: the disc's answers 41/42 at once
      // (disc sub_8281BCF8); TU3's holds the other-player-paused box for its first ticks
      // (0x82879A04..0x82879A64) and its answer refuses a "no" while +41 is set (0x82879AF4).
      // The handler answers 1 only where it called the answer (TU3 0x82879AA8 / 0x82879AC0, disc
      // 0x8281BD40 / 0x8281BD5C), and the answer posted its event 68 when the message serial moved
      // (PostEvent `stw r10,0x9B0(r30)` 0x821CFB58, disc 0x980 at 0x821CFA88): a save menu
      // confirmation's row runs on that (save_menu_confirm.cpp).
      {
        const uint32_t before = MessageSerial(base);
        if (RunHandler(ctx, base, rex_QueryBox_HandleInput, box, verb == "yes" ? kActionYes : kActionNo, player) == 1u) {
          NoteQuestionAnswered(box, verb == "yes", MessageSerial(base) != before);
        }
      }
      return true;
    case kVtNatalQuery:
      RunHandler(ctx, base, rex_NatalQueryBox_HandleInput, box, verb == "yes" ? kActionYes : kActionNo, player);
      return true;
    case kVtAdjuster:
      // The value is Godot's until the answer: stored where the guest's answer reads it, on a box
      // that is still shown; the handler then applies its own gates (player, shown, the player's hero).
      if (verb == "accept" && base[box + kBoxShown] != 0u) {
        StoreGuestU32(base, box + kAdjusterValue, static_cast<uint32_t>(static_cast<int32_t>(value)));
      }
      RunHandler(ctx, base, rex_NumericAdjuster_HandleInput, box, verb == "accept" ? kActionYes : kActionNo, player);
      return true;
    case kVtMessage:
      RunHandler(ctx, base, rex_MessageBox_HandleInput, box, verb == "action" ? kActionX : kActionCancel, player);
      return true;
    case kVtQuest:
      RunHandler(ctx, base, rex_QuestBox_HandleInput, box, kActionCancel, player);
      return true;
    case kVtNotice:
      RunHandler(ctx, base, rex_TutorialBox_HandleInput, box, kActionYes, player);
      return true;
    case kVtInfoCard:
      if (verb == "help") {
        RunHandler(ctx, base, rex_AcceptHoldBox_HandleInput, box, kActionYPressed, player);
        return true;
      }
      if (verb != "accept") {
        return false;
      }
      if (base[box + kCardHoldA] == 0u) {
        // A press card: its own handler with A.
        RunHandler(ctx, base, rex_AcceptHoldBox_HandleInput, box, kActionAPressed, player);
        return true;
      }
      // A held card whose hold Godot timed to the full: the answer the guest's fill would post.
      if (base[box + kCardShowA] == 0u || base[box + kBoxShown] == 0u) {
        return false;
      }
      {
        PPCContext work = Frame(ctx);
        work.r3.u64 = kButtonAccept;
        rex_Box_PostButtonAnswer(work, base);
      }
      return true;
    case kVtButtonBox:
      if (verb != "button" || value < 1 || value > 4) {
        return false;
      }
      RunHandler(ctx, base, rex_ButtonBox_HandleInput, box, kButtonBoxReleased[value - 1], player);
      return true;
    default:
      REXLOG_WARN("[fable3-boxes] answer '{}' for a box Godot does not own (vtable {:08X}); dropped", action,
                  showing.vtable);
      return false;
  }
}

}  // namespace refii::fable3

/// (queue, shared_ptr<Box>): `@Box.Queued` once the push appended it (the pending end moved).
REX_EXTERN(rex_BoxQueue_Push) {
  const uint32_t queue = ctx.r3.u32;
  const uint32_t holder = ctx.r4.u32;
  const bool readable = base != nullptr && Plausible(queue) && Plausible(holder);
  const uint32_t box = readable ? LoadGuestU32(base, holder) : 0u;
  const uint32_t end_before = readable ? LoadGuestU32(base, queue + kQueuePendingEnd) : 0u;

  __imp__rex_BoxQueue_Push(ctx, base);

  if (!readable || !Plausible(box) || LoadGuestU32(base, queue + kQueuePendingEnd) == end_before) {
    return;
  }
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    Queued()[box] = LoadGuestU32(base, box);
  }
  if (refii::fable3::SaveMenuAccepting()) {
    refii::fable3::NoteSaveConfirmQueued(box);
  }
  PostQueued(base, QueueIndex(base, queue), box);
}

/// (out, vector, iterator): `@Box.Dropped` when a pending box leaves one of the manager's queues
/// without showing — RemoveDisplayBox, RemoveAllDisplayBoxesWithName.
REX_EXTERN(rex_BoxVector_Erase) {
  const uint32_t vector = ctx.r4.u32;
  const uint32_t iterator = ctx.r5.u32;
  const bool ours = base != nullptr && Plausible(vector) && Plausible(iterator) && QueueIndex(base, vector - 4u) >= 0;
  const uint32_t box = ours ? LoadGuestU32(base, iterator) : 0u;

  __imp__rex_BoxVector_Erase(ctx, base);

  // The tick's own erase follows its take (disc 0x822879D4 then disc 0x822879E4): that box is showing now,
  // and the tick states it.
  if (box == 0u || LoadGuestU32(base, vector - 4u + kQueueShowing) == box) {
    return;
  }
  bool was_queued = false;
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    was_queued = Queued().erase(box) != 0u;
  }
  if (was_queued) {
    refii::fable3::NoteBoxGone(box);
    Statement("@Box.Dropped").Add(Text(Hex(box))).Post();
  }
}

/// The queue's tick: the showing slot before and after is the show and the release.
REX_EXTERN(rex_BoxQueue_Update) {
  const uint32_t queue = ctx.r3.u32;
  const uint32_t before = base != nullptr && Plausible(queue) ? LoadGuestU32(base, queue + kQueueShowing) : 0u;

  __imp__rex_BoxQueue_Update(ctx, base);

  if (base == nullptr || !Plausible(queue)) {
    return;
  }
  const uint32_t after = LoadGuestU32(base, queue + kQueueShowing);
  if (after == before) {
    return;
  }
  const int32_t index = QueueIndex(base, queue);

  if (before != 0u) {
    {
      std::lock_guard<std::mutex> lock(g_mutex);
      ShowingBoxes().erase(before);
    }
    refii::fable3::NoteBoxGone(before);
    Statement("@Box.Close").Add(Number(static_cast<float>(index))).Add(Text(Hex(before))).Post();
  }
  if (after != 0u && Plausible(after)) {
    uint32_t vtable = 0u;
    {
      std::lock_guard<std::mutex> lock(g_mutex);
      auto queued = Queued().find(after);
      vtable = queued != Queued().end() ? queued->second : LoadGuestU32(base, after);
      if (queued != Queued().end()) {
        Queued().erase(queued);
      }
      ShowingBoxes()[after] = Showing{index, vtable, 0u};
    }
    Statement("@Box.Show").Add(Number(static_cast<float>(index))).Add(Text(Hex(after))).Post();
    // The Show has run (0x82624094 inside the tick): a screen's starting cursor and choices.
    StateScreenShown(base, after, KindOf(base, after, vtable));
  }
}

// The owned classes' pad handlers (box, ev*, player).
REX_EXTERN(rex_MessageBox_HandleInput) { Gate(ctx, base, __imp__rex_MessageBox_HandleInput); }
REX_EXTERN(rex_QueryBox_HandleInput) { Gate(ctx, base, __imp__rex_QueryBox_HandleInput); }
REX_EXTERN(rex_NatalQueryBox_HandleInput) { Gate(ctx, base, __imp__rex_NatalQueryBox_HandleInput); }
REX_EXTERN(rex_NumericAdjuster_HandleInput) { Gate(ctx, base, __imp__rex_NumericAdjuster_HandleInput); }
REX_EXTERN(rex_QuestBox_HandleInput) { Gate(ctx, base, __imp__rex_QuestBox_HandleInput); }
REX_EXTERN(rex_TutorialBox_HandleInput) { Gate(ctx, base, __imp__rex_TutorialBox_HandleInput); }
REX_EXTERN(rex_AcceptHoldBox_HandleInput) { Gate(ctx, base, __imp__rex_AcceptHoldBox_HandleInput); }
REX_EXTERN(rex_ButtonBox_HandleInput) { Gate(ctx, base, __imp__rex_ButtonBox_HandleInput); }

/// (SM, action, player): while the pause screen's message box is up, its two actions (72, and 76
/// on the download form) are Godot's; every other action runs as the guest has it.
REX_EXTERN(rex_GuiManager_ScreenInput) {
  const uint32_t action = ctx.r4.u32;
  if (!t_host_answer && base != nullptr && (action == kActionSelect || action == kActionX) && ModalUp(base)) {
    ctx.r3.u64 = 1u;
    return;
  }
  __imp__rex_GuiManager_ScreenInput(ctx, base);
}

/// (G): the pause screen's message box drops (its one box: the show's manager is the key).
REX_EXTERN(rex_GuiManager_ModalReset) {
  __imp__rex_GuiManager_ModalReset(ctx, base);
  uint32_t manager = 0u;
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    manager = g_modal;
    if (manager != 0u) {
      ShowingBoxes().erase(manager);
      g_modal = 0u;
    }
  }
  if (manager != 0u) {
    Statement("@Box.Close").Add(Number(static_cast<float>(kModalQueue))).Add(Text(Hex(manager))).Post();
  }
}

/// (MB, &tag): a row, as its add stated it.
REX_EXTERN(rex_MenuBox_AddRow) {
  const uint32_t tag = ctx.r4.u32;
  const std::string text = base != nullptr && Plausible(tag) ? ReadCharString(base, tag) : std::string();
  __imp__rex_MenuBox_AddRow(ctx, base);
  if (ctx.r3.u32 != 0u) {
    std::lock_guard<std::mutex> lock(g_mutex);
    MenuRows().push_back(MenuRow{text, true});
  }
}

/// (MB, index, enabled).
REX_EXTERN(rex_MenuBox_SetEnabled) {
  const uint32_t index = ctx.r4.u32;
  const bool enabled = (ctx.r5.u32 & 0xFFu) != 0u;
  __imp__rex_MenuBox_SetEnabled(ctx, base);
  std::lock_guard<std::mutex> lock(g_mutex);
  if (index < MenuRows().size()) {
    MenuRows()[index].enabled = enabled;
  }
}

/// `@MenuBox.Open(title, cancelable, row…)` — the title the filler resolved, whether B cancels,
/// and each row its TextTag prefixed `+` (enabled) or `-` (ghosted), as the adds stated them.
REX_EXTERN(rex_MenuBox_Open) {
  const uint32_t menu = ctx.r3.u32;
  __imp__rex_MenuBox_Open(ctx, base);
  std::vector<MenuRow> rows;
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    rows.swap(MenuRows());
  }
  if (base == nullptr || ctx.r3.u32 != 1u || !Plausible(menu)) {
    return;
  }
  g_menu = menu;

  Statement open("@MenuBox.Open");
  open.Add(Text(ReadLhString(base, menu + kMenuTitle))).Add(Boolean(base[menu + kMenuCancelable] != 0u));
  for (const MenuRow& row : rows) {
    open.Add(Text((row.enabled ? "+" : "-") + row.tag));
  }
  open.Post();
}

/// `@MenuBox.Close(result)` — 0 cancelled, else the 1-based row, as event 65 carries it.
REX_EXTERN(rex_MenuBox_Close) {
  const uint32_t menu = ctx.r3.u32;
  const bool was_open = base != nullptr && Plausible(menu) && base[menu + kMenuOpen] != 0u &&
                        LoadGuestU32(base, menu + 8u) != 0u;
  int32_t result = 0;
  if (was_open && base[menu + kMenuCancelled] == 0u) {
    result = static_cast<int32_t>(LoadGuestU32(base, menu)) + 1;
  }
  __imp__rex_MenuBox_Close(ctx, base);
  if (was_open) {
    if (g_menu == menu) {
      g_menu = 0u;
    }
    Statement("@MenuBox.Close").Add(Number(static_cast<float>(result))).Post();
  }
}

/// (MB, ev, pad): Godot owns the open menu box's pad. The guest's own mapping still runs (its
/// repeat timers are the guest's), and the handler's answer is kept: 1 for the four actions it
/// takes, 0 for any other, so nothing else changes for the caller.
REX_EXTERN(rex_MenuBox_Input) {
  const uint32_t menu = ctx.r3.u32;
  if (base == nullptr || !Plausible(menu) || base[menu + kMenuOpen] == 0u) {
    __imp__rex_MenuBox_Input(ctx, base);
    return;
  }
  PPCContext work = Frame(ctx);
  work.r3.u64 = ctx.r4.u32;
  work.r4.u64 = ctx.r5.u32;
  rex_GuiInput_MenuAction(work, base);
  const int32_t mapped = static_cast<int32_t>(work.r3.u32);
  ctx.r3.u64 = mapped == kMenuActionUp || mapped == kMenuActionDown || mapped == kMenuActionCancel ||
                       mapped == kMenuActionSelect
                   ? 1u
                   : 0u;
}

/// SM's destructor, SM = *(GUI+0x1C) (TU3 sub_82334A48, disc sub_8232D740), from the GUI shutdown
/// sub_823332D0 (0x82333360), the members' destructor sub_82331E60 (0x82332218) and sub_825ECD70
/// (0x825ECF28). It frees the save menu +0x38, the menu box +0x34 and the box manager +0x30 with both
/// queues (stores 0 at 0x82334ADC / 0x82334B18 / 0x82334B94; disc 0x8232D7D4 / 0x8232D810 / +0x30)
/// without closing a box, the menu box or the save menu: Save and Quit and the quit to the front end
/// end every one of them here. `@Screens.Destroyed(sm)`.
REX_EXTERN(rex_ScreenManager_Destroy) {
  const uint32_t manager = ctx.r3.u32;
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    Queued().clear();
    ShowingBoxes().clear();
    MenuRows().clear();
    g_menu = 0u;
    g_modal = 0u;
  }
  refii::fable3::ForgetSaveMenu();
  refii::fable3::ForgetSaveConfirms();
  Statement("@Screens.Destroyed").Add(Text(Hex(manager))).Post();
  __imp__rex_ScreenManager_Destroy(ctx, base);
}
