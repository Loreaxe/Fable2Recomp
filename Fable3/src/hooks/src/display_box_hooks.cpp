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
// THE BOX SCREENS GODOT OWNS (FB7; TU3 / disc, each body the one word of its vtable's +16 unless
// said). Their cursors, tiers and working values are Godot's: a lever that names one stores it where
// the guest's own answer reads it, after the test the guest's handler makes, then runs that handler.
//   - hero status 0x820C69DC / 0x820C345C, sub_829BE438 / sub_82975A58: 75 sets the close byte +28
//     (`cmpwi cr6,r10,0x4B` 0x829BE440 / 0x82975A60, `stb r10,0x1C(r11)` 0x829BE450 / 0x82975A70); no
//     player and no shown test. `close`.
//   - promise 0x820B3160 / 0x820AFD94, sub_827DAB30 / sub_827A3600: the pad of player record 1
//     (`li r4,1` / `bl sub_821FF0A8` 0x827DAB48..0x827DAB5C / disc sub_821FD790 0x827A362C, `lwz r11,
//     0x24(r30)` / `cmpw cr6,r28,r11` 0x827DAB8C..0x827DAB90 / 0x827A365C..0x827A3660) unless GAME's
//     slot 6 answers true (`bctrl` 0x827DAB74 / 0x827A3644); 75 closes once signed (`lbz r10,0x4D(r31)`
//     0x827DABA4, `stb r11,0x4C(r31)` 0x827DABB8 / 0x827A3688); 74 signs while unsigned (`stb r8,
//     0x4D(r31)` 0x827DAC1C / 0x827A36EC) and posts the named message "PromiseAccepted" (`bl
//     sub_823B6C78` 0x827DAC84 / disc sub_82450E08 0x827A3754), answering 0. `accept`, `close`.
//   - king's schedule 0x820C6658 / 0x820C30E0, sub_829BBE90 / sub_82973428: 74 from player one alone
//     (`cmpwi cr6,r11,0x4A` 0x829BBEA8 / 0x82973440, `cmpwi cr6,r5,1` 0x829BBEB0 / 0x82973448) plays
//     SE_AGENDA_SCROLL_ACCEPT, clears +12 and calls its Hide (`bctrl` 0x829BBEF0 / 0x82973488). `accept`.
//   - quest summary 0x820C6790 / 0x820C3214, sub_829BD018 / sub_829745B0: the pad +56 and shown
//     (`lwz r11,0x38(r3)` / `cmpw cr6,r5,r11` 0x829BD028..0x829BD030 / 0x829745C0..0x829745C8, `lbz r11,
//     0xC(r3)` 0x829BD038 / 0x829745D0); 72 posts event 156 with the FNV-1 of the cursor row's quest
//     name (cursor `lwz r11,0x2C(r31)` 0x829BD0B4 / 0x8297464C against the list +28..+32, `li r4,0x9C`
//     0x829BD158 / 0x829746F0, `bl sub_821CFA18` 0x829BD170 / disc sub_821CF948 0x82974708) and hides
//     (`bctrl` 0x829BD184 / 0x8297471C); 73 hides (`bctrl` 0x829BD1D8 / 0x82974770). The stick's mover
//     sub_829BD210 / sub_829747A8 ends `stw r6,0x2C(r31)` / `bl sub_829BC888` (0x829BD2E8..0x829BD2F8 /
//     0x82974880..0x82974890, the seven-row window writer, whose other caller is the Show).
//     `row:<n>` (that tail), `select:<n>`, `close`.
//   - wedding picker 0x820DAFA0 / 0x820D85A4, sub_82B90EB8 / sub_82B5C260: shown and the pad +72
//     (`lbz r11,0xC(r3)` 0x82B90EC8 / 0x82B5C270, `lwz r11,0x48(r3)` / `cmpw cr6,r11,r5` 0x82B90ED8..
//     0x82B90EDC / 0x82B5C280..0x82B5C284); the switch on action - 29 (jump table read through IDA's
//     switch info on both builds): case 12 (41) books the tier +68 when its affordable byte +88..+90 is
//     set — event 69 with the tier (`lwz r9,0x44(r31)` 0x82B91060 / 0x82B5C408, `li r4,0x45` 0x82B910D8 /
//     0x82B5C480) and its Hide; case 13 (42) event 70 with the tier (`li r4,0x46` 0x82B91164 /
//     0x82B5C50C) and its Hide. `accept:<tier>`, `decline:<tier>`.
//   - chat channel 0x820C64CC / 0x820C2F58, sub_829BB230 / sub_829727C8: the pad of the box entity's
//     component 37 (`cmpwi cr6,r6,0x25` 0x829BB330 / 0x829728C8, `lwz r29,0x24(r11)` / `cmpw cr6,r29,
//     r27` 0x829BB39C..0x829BB3A8 / 0x82972934..0x82972940); 72 stores the working byte +36 as the
//     user's mute (`lbz r10,0x24(r30)` 0x829BB440 / 0x829729D8, `stb r10,0x20(r11)` 0x829BB478 /
//     0x82972A10, `bl sub_82370548` / disc sub_82369200) and hides; 73 hides. `chat:<0|1>`, `close`.
//   - stats 0x820C6398 / 0x820C2E28, sub_829B6158 / sub_8296DA10: shown, the entity's component-37 pad
//     (`lwz r30,0x24(r11)` / `cmpw cr6,r30,r28` 0x829B62D0..0x829B62DC / 0x8296DB88..0x8296DB94), then
//     the page +52's handler (2 sub_829B6348 / sub_8296DC00, 3 sub_829B64D8 / sub_8296DD90, 4
//     sub_829B6830 / sub_8296E100). The Show caches that pad at +64 (`stw r11,0x40(r29)` 0x829B5EC0 /
//     0x8296D740). A page's mover ends with the cursor +68 stored, on page 2 the category +72 = +68
//     (`lwz r7,0x44(r31)` / `stw r7,0x48(r31)` 0x829B63A4..0x829B63B0 / 0x8296DC5C..0x8296DC68), and the
//     page's writer (sub_829B7458 / sub_8296ED30, sub_829B76A0 / sub_8296EF78, sub_829B8FC0 /
//     sub_829708B0; each has five callers, all this class's). 72 opens (page 2: `li r4,3` / `bl
//     sub_829B73B0` 0x829B6448..0x829B6450 / 0x8296DD00..0x8296DD08; page 3 the leaderboard, `stw r9,
//     0x4C(r28)` 0x829B67C8 / 0x8296E098), 73 goes back or hides, 77 on page 4 is the gamer card.
//     `stats-row:<n>` (the mover's tail), `stats-open:<n>`, `stats-back`, `stats-card:<n>`.
// A row lever states what it stored, `@Screen.Cursor(box, row)`. The session boxes (join /
// visibility, friends channel, co-op partners) and the family boxes (divorce, bed, treasury, marriage
// proposal, food query) are answered by session_boxes.cpp and family_boxes.cpp through this file's
// gate (GateOwnedInput / RunOwnedHandler); the divorce confirmation's answer by divorce_confirm.cpp.
// THE MOVERS' CUES are still the guest's call (Godot has no Fable 3 cue player yet), made by the
// lever that replaces the move, on the mover's own condition: the quest summary's and the stats
// pages' step DOWN alone (SE_GUI_MENU_BOX_DOWN: `dword_8359BE14` 0x829BD2B8 / disc dword_8355BA14
// 0x82974850; `dword_8359BF2C` 0x829B6D8C / disc dword_8355BB2C 0x8296E664 — neither mover plays one
// going up); the wedding picker's tier up / down (SE_GUI_2D_MENU_SCROLL_UP `dword_8359BF58`
// 0x82B90FB0, _SCROLL_DOWN `dword_8359BF5C` 0x82B9101C; disc dword_8355BB58 0x82B5C358, dword_8355BB5C
// 0x82B5C3C4), stated by `tier:<n>`, which stores the tier where the stick stored it (`stw r11,
// 0x44(r31)` 0x82B90F94 / 0x82B91000) but not the redraw byte +0x5B; the chat channel's mute
// (sub_829BB608 / disc sub_82972BA0: left, 31, only from audible, SE_GUI_2D_MENU_SLIDER_LEFT
// `dword_8359BF3C` 0x829BB644 and `+0x24 = 1`; right, 32, only from muted, _SLIDER_RIGHT
// `dword_8359BF40` 0x829BB680 and `+0x24 = 0`; disc dword_8355BB3C 0x82972BDC, dword_8355BB40
// 0x82972C18), stated by `mute:<0|1>`.
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

#include <atomic>
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
REX_EXTERN(__imp__rex_HeroStatusBox_HandleInput);
REX_EXTERN(__imp__rex_PromiseBox_HandleInput);
REX_EXTERN(__imp__rex_KingsSchedule_HandleInput);
REX_EXTERN(__imp__rex_QuestSummary_HandleInput);
REX_EXTERN(__imp__rex_WeddingPickerBox_HandleInput);
REX_EXTERN(__imp__rex_OrbChatBox_HandleInput);
REX_EXTERN(__imp__rex_StatsBox_HandleInput);
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
REX_EXTERN(rex_HeroStatusBox_HandleInput);
REX_EXTERN(rex_PromiseBox_HandleInput);
REX_EXTERN(rex_KingsSchedule_HandleInput);
REX_EXTERN(rex_QuestSummary_HandleInput);
REX_EXTERN(rex_WeddingPickerBox_HandleInput);
REX_EXTERN(rex_OrbChatBox_HandleInput);
REX_EXTERN(rex_StatsBox_HandleInput);
REX_EXTERN(rex_QuestSummary_Rows);
REX_EXTERN(rex_StatsBox_WriteCategories);
REX_EXTERN(rex_StatsBox_WriteStatistics);
REX_EXTERN(rex_StatsBox_WriteFriends);
REX_EXTERN(rex_GuiSound_Play);
REX_EXTERN(rex_SoundHandle_Release);
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
/// B pressed (75), which the hero status and the promise close on.
constexpr uint32_t kActionBPressed = 75u;

/// The box screens' fields the levers store (base = the box; the header names each one's reader).
constexpr uint32_t kQuestListBegin = 0x1Cu;
constexpr uint32_t kQuestListEnd = 0x20u;
constexpr uint32_t kQuestCursor = 0x2Cu;
constexpr uint32_t kQuestOwnerPad = 0x38u;
constexpr uint32_t kWeddingTier = 0x44u;
constexpr uint32_t kWeddingOwnerPad = 0x48u;
constexpr uint32_t kChatWorking = 0x24u;
constexpr uint32_t kStatsPage = 0x34u;
constexpr uint32_t kStatsOwnerPad = 0x40u;
constexpr uint32_t kStatsCursor = 0x44u;
constexpr uint32_t kStatsCategory = 0x48u;

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

/// The cues the movers Godot replaced play (the header names each one's site).
const refii::guest::Addr kQuestStepDownCue{0x8359BE14u};
const refii::guest::Addr kStatsStepDownCue{0x8359BF2Cu};
const refii::guest::Addr kWeddingTierUpCue{0x8359BF58u};
const refii::guest::Addr kWeddingTierDownCue{0x8359BF5Cu};
const refii::guest::Addr kChatMuteCue{0x8359BF3Cu};
const refii::guest::Addr kChatUnmuteCue{0x8359BF40u};

/// The guest's own cue call, `sub_823333B0(out, GUI, cue*)` then the handle's release.
void PlayCue(PPCContext& ctx, uint8_t* base, uint32_t cue) {
  PPCContext work = Frame(ctx);
  const uint32_t handle = work.r1.u32 + 0x100u;
  work.r3.u64 = handle;
  work.r4.u64 = LoadGuestU32(base, kGuiManager);
  work.r5.u64 = cue;
  rex_GuiSound_Play(work, base);
  work.r3.u64 = handle;
  rex_SoundHandle_Release(work, base);
}

/// `@Screen.Cursor(box, row)`: the row a row lever stored.
void StateCursor(uint32_t box, long row) {
  Statement("@Screen.Cursor").Add(Text(Hex(box))).Add(Number(static_cast<float>(row))).Post();
}

/// Runs `writer(box)`, one of a box screen's own row writers.
template <typename Writer>
void RunWriter(PPCContext& ctx, uint8_t* base, Writer writer, uint32_t box) {
  PPCContext work = Frame(ctx);
  work.r3.u64 = box;
  writer(work, base);
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

void GateOwnedInput(PPCContext& ctx, uint8_t* base, GuestFunction original) { Gate(ctx, base, original); }

uint32_t RunOwnedHandler(PPCContext& ctx, uint8_t* base, GuestFunction handler, uint32_t box, uint32_t action,
                         uint32_t player) {
  return RunHandler(ctx, base, handler, box, action, player);
}

uint32_t MessageSerialNow(const uint8_t* base) { return MessageSerial(base); }

void PlayGuestCue(PPCContext& ctx, uint8_t* base, uint32_t cue) { PlayCue(ctx, base, cue); }

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
  const std::string value_text = colon == std::string::npos ? std::string() : verb_and_value.substr(colon + 1);

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
          const uint32_t after = MessageSerial(base);
          NoteQuestionAnswered(box, verb == "yes", after != before);
          NoteRealEstateAnswered(box, false, before, after);
          NoteDivorceConfirmAnswered(box, verb == "yes", after != before, before);
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
      {
        // The accept posts event 69 with the value (0x8287A3F0..0x8287A434); a real-estate adjuster's
        // poll runs on that record alone (real_estate_confirm.cpp checks the record's serial against
        // the ones posted here). A cancel posts event 70, which no poll reads.
        const uint32_t before = MessageSerial(base);
        RunHandler(ctx, base, rex_NumericAdjuster_HandleInput, box, verb == "accept" ? kActionYes : kActionNo, player);
        if (verb == "accept") {
          NoteRealEstateAnswered(box, true, before, MessageSerial(base));
        }
      }
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
    case kVtHeroStatus:
      if (verb != "close") {
        return false;
      }
      RunHandler(ctx, base, rex_HeroStatusBox_HandleInput, box, kActionBPressed, player);
      return true;
    case kVtPromises:
      if (verb != "accept" && verb != "close") {
        return false;
      }
      RunHandler(ctx, base, rex_PromiseBox_HandleInput, box, verb == "accept" ? kActionAPressed : kActionBPressed,
                 player);
      return true;
    case kVtKingsSchedule:
      if (verb != "accept") {
        return false;
      }
      RunHandler(ctx, base, rex_KingsSchedule_HandleInput, box, kActionAPressed, player);
      return true;
    case kVtQuestSummary: {
      if (verb == "close") {
        RunHandler(ctx, base, rex_QuestSummary_HandleInput, box, kActionCancel, player);
        return true;
      }
      if (verb != "row" && verb != "select") {
        return false;
      }
      // The handler's own test, made before the store: its pad and shown.
      const uint32_t rows = (LoadGuestU32(base, box + kQuestListEnd) - LoadGuestU32(base, box + kQuestListBegin)) >> 2;
      if (base[box + kBoxShown] == 0u || LoadGuestU32(base, box + kQuestOwnerPad) != player || value < 0 ||
          static_cast<uint32_t>(value) >= rows) {
        REXLOG_INFO("[fable3-boxes] answer '{}': not this box's player, or no such row of {}; refused", action, rows);
        return false;
      }
      const int32_t quest_row_before = static_cast<int32_t>(LoadGuestU32(base, box + kQuestCursor));
      StoreGuestU32(base, box + kQuestCursor, static_cast<uint32_t>(value));
      if (verb == "row") {
        if (value > quest_row_before) {
          PlayCue(ctx, base, kQuestStepDownCue);
        }
        RunWriter(ctx, base, rex_QuestSummary_Rows, box);
        StateCursor(box, value);
      } else {
        RunHandler(ctx, base, rex_QuestSummary_HandleInput, box, kActionSelect, player);
      }
      return true;
    }
    case kVtWeddingPicker: {
      if ((verb != "accept" && verb != "decline" && verb != "tier") || value < 0 || value > 2) {
        return false;
      }
      if (base[box + kBoxShown] == 0u || LoadGuestU32(base, box + kWeddingOwnerPad) != player) {
        REXLOG_INFO("[fable3-boxes] answer '{}': not this box's player; refused", action);
        return false;
      }
      const int32_t tier_before = static_cast<int32_t>(LoadGuestU32(base, box + kWeddingTier));
      StoreGuestU32(base, box + kWeddingTier, static_cast<uint32_t>(value));
      if (verb == "tier") {
        if (value != tier_before) {
          PlayCue(ctx, base, value < tier_before ? kWeddingTierUpCue : kWeddingTierDownCue);
        }
        return true;
      }
      RunHandler(ctx, base, rex_WeddingPickerBox_HandleInput, box, verb == "accept" ? kActionYes : kActionNo, player);
      return true;
    }
    case kVtChatChannel: {
      if (verb == "close") {
        RunHandler(ctx, base, rex_OrbChatBox_HandleInput, box, kActionCancel, player);
        return true;
      }
      if ((verb != "chat" && verb != "mute") || (value != 0 && value != 1)) {
        return false;
      }
      if (verb == "mute") {
        // The guest's left / right: muting only from audible, unmuting only from muted, each with its cue.
        if (base[box + kBoxShown] != 0u && base[box + kChatWorking] != static_cast<uint8_t>(value)) {
          PlayCue(ctx, base, value == 1 ? kChatMuteCue : kChatUnmuteCue);
          base[box + kChatWorking] = static_cast<uint8_t>(value);
        }
        return true;
      }
      // The owner test is the handler's (the entity's component 37): a refusal puts the byte back.
      const uint8_t before = base[box + kChatWorking];
      base[box + kChatWorking] = static_cast<uint8_t>(value);
      if (RunHandler(ctx, base, rex_OrbChatBox_HandleInput, box, kActionSelect, player) != 1u) {
        base[box + kChatWorking] = before;
        REXLOG_INFO("[fable3-boxes] answer '{}': the chat box's handler refused it", action);
      }
      return true;
    }
    case kVtStats: {
      if (verb == "stats-back") {
        RunHandler(ctx, base, rex_StatsBox_HandleInput, box, kActionCancel, player);
        return true;
      }
      if (verb != "stats-row" && verb != "stats-open" && verb != "stats-card") {
        return false;
      }
      const uint32_t page = LoadGuestU32(base, box + kStatsPage);
      const uint32_t rows = StatsRowCount(box, static_cast<int32_t>(page));
      if (base[box + kBoxShown] == 0u || LoadGuestU32(base, box + kStatsOwnerPad) != player || page < 2u ||
          page > 4u || value < 0 || static_cast<uint32_t>(value) >= rows) {
        REXLOG_INFO("[fable3-boxes] answer '{}': not this box's player, or no such row of page {}'s {}; refused",
                    action, page, rows);
        return false;
      }
      const int32_t stats_row_before = static_cast<int32_t>(LoadGuestU32(base, box + kStatsCursor));
      StoreGuestU32(base, box + kStatsCursor, static_cast<uint32_t>(value));
      if (verb == "stats-row" && value > stats_row_before) {
        PlayCue(ctx, base, kStatsStepDownCue);
      }
      if (page == 2u) {
        StoreGuestU32(base, box + kStatsCategory, static_cast<uint32_t>(value));
      }
      if (verb == "stats-row") {
        if (page == 2u) {
          RunWriter(ctx, base, rex_StatsBox_WriteCategories, box);
        } else if (page == 3u) {
          RunWriter(ctx, base, rex_StatsBox_WriteStatistics, box);
        } else {
          RunWriter(ctx, base, rex_StatsBox_WriteFriends, box);
        }
        StateCursor(box, value);
      } else {
        RunHandler(ctx, base, rex_StatsBox_HandleInput, box, verb == "stats-open" ? kActionSelect : kActionYPressed,
                   player);
      }
      return true;
    }
    default:
      if (AnswerSessionBox(ctx, base, box, showing.vtable, verb, value_text, player, action) ||
          AnswerFamilyBox(ctx, base, box, showing.vtable, verb, value_text, player, action)) {
        return true;
      }
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
  if (refii::fable3::RealEstateAsking()) {
    refii::fable3::NoteRealEstateConfirmQueued(box);
  }
  if (refii::fable3::DivorceAccepting()) {
    refii::fable3::NoteDivorceConfirmQueued(box);
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
    refii::fable3::NoteRealEstateBoxGone(box);
    refii::fable3::NoteDivorceConfirmGone(box);
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
    refii::fable3::NoteRealEstateBoxGone(before);
    refii::fable3::NoteDivorceConfirmGone(before);
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
// The owned box screens' pad handlers (box, ev*, player), each named in the log the first time the
// guest's router reaches it.
#define REFII_F3_OWNED_SCREEN(hook, what)                                                   \
  REX_EXTERN(rex_##hook) {                                                                  \
    static std::atomic<bool> said{false};                                                   \
    if (!said.exchange(true)) {                                                             \
      REXLOG_INFO("[fable3-boxes] " what ": its pad is Godot's (rex_" #hook " gated)");     \
    }                                                                                       \
    Gate(ctx, base, __imp__rex_##hook);                                                     \
  }

REFII_F3_OWNED_SCREEN(HeroStatusBox_HandleInput, "hero status")
REFII_F3_OWNED_SCREEN(PromiseBox_HandleInput, "promise")
REFII_F3_OWNED_SCREEN(KingsSchedule_HandleInput, "king's schedule")
REFII_F3_OWNED_SCREEN(QuestSummary_HandleInput, "quest summary")
REFII_F3_OWNED_SCREEN(WeddingPickerBox_HandleInput, "wedding picker")
REFII_F3_OWNED_SCREEN(OrbChatBox_HandleInput, "chat channel")
REFII_F3_OWNED_SCREEN(StatsBox_HandleInput, "stats")

#undef REFII_F3_OWNED_SCREEN

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
  refii::fable3::ForgetRealEstateConfirms();
  refii::fable3::ForgetDivorceConfirms();
  Statement("@Screens.Destroyed").Add(Text(Hex(manager))).Post();
  __imp__rex_ScreenManager_Destroy(ctx, base);
}
