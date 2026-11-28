// refii - Godot's answers to the boxes and the Options screen it owns, handed to the guest's own
// answer functions on the guest tick thread (display_box_hooks.cpp, options_hooks.cpp), and the
// statements the pause screen's message box shares with game_state_hooks.cpp.

#pragma once

#include <cstdint>
#include <string>

#include <rex/ppc.h>

namespace refii::fable3 {

/// `box:<p>:<HEX>:<verb>[:<value>]` — a box Godot owns, answered by local player p (1 or 2, the
/// value the box's handler takes in r5): `yes` / `no` (a question), `dismiss` / `action` (a
/// message, the download form's X; a quest box; a notice; the pause screen's message box),
/// `accept:<value>` / `cancel` (the numeric adjuster), `accept` / `help` (an info card: a press
/// card's A or Y through its handler, a held card's A once Godot's hold is full), `button:<n>`
/// (the button box's answer n, 1 A, 2 B, 3 X, 4 Y). The session and family boxes' verbs are in
/// session_boxes.cpp and family_boxes.cpp. The box screens: `close` (hero status; a signed
/// promise; quest summary; chat), `accept` (an unsigned promise; the king's schedule), `row:<n>` /
/// `select:<n>` (quest summary: Godot's cursor on list row n, and the pick), `accept:<tier>` /
/// `decline:<tier>` / `tier:<n>` (wedding picker, tier 0..2: the answers, and Godot's tier as it
/// moves), `chat:<0|1>` / `mute:<0|1>` (chat channel: save audible / muted, and Godot's working value
/// as it changes),
/// `stats-row:<n>` / `stats-open:<n>` / `stats-back` / `stats-card:<n>` (stats: Godot's cursor on the
/// page's list row n, A on it, B, and Y on the friends' board).
inline constexpr char kBoxAnswerAction[] = "box:";
/// `topbox:<p>:<a|y>:<down|up>` — the TopBox's A or Y pressed or released by local player p, handed
/// to its own input (topbox_answer.cpp); while one is down its held code goes each tick.
/// `topbox:<p>:<a|y>:fill:<0..1000>` — Godot's hold fill of that button, in thousandths, which the
/// replaced hold step takes as the progress.
inline constexpr char kTopBoxAction[] = "topbox:";
bool AnswerTopBox(PPCContext& ctx, uint8_t* base, const char* action);
/// One guest tick of the TopBox's held buttons.
void PumpTopBox(PPCContext& ctx, uint8_t* base);
/// Whether a press of `action` by `player` is taken by a surface the guest's dispatcher asks before
/// the TopBox: a showing box other than an intermission, the pause screen's message box, the menu
/// box's select (display_box_hooks.cpp).
bool PadTakenAheadOfTopBox(const uint8_t* base, uint32_t action, uint32_t player);
/// `menubox:<p>:<row>` (0-based) picks a row of the open menu box (player one's alone, as the
/// guest's mapper has it); `menubox-cancel:<p>` cancels it.
inline constexpr char kMenuBoxAnswerAction[] = "menubox:";
inline constexpr char kMenuBoxCancelAction[] = "menubox-cancel";
/// `options:<p>:<74|75|77|14>` — A, B, Y or START into the showing Options screen's own handler for
/// local player p (the handler takes its own hero's pad alone); on a settings page A and B wait
/// until the box holds Godot's working values (options_hooks.cpp).
inline constexpr char kOptionsAction[] = "options:";
/// `options-row:<p>:<row>[:accept]` — Godot's cursor on the page's 1-based row: on the list pages
/// the screen's own row setter, then with `:accept` A through its handler; on the settings pages
/// the row the screen's own cursor is walked to; for local player p.
inline constexpr char kOptionsRowAction[] = "options-row:";
/// `options-value:<p>:<page>:<row>:<value>` — Godot's working value of settings page `page`'s 1-based
/// row, which the screen's own left / right setters bring the box to (dropped on any other page).
inline constexpr char kOptionsValueAction[] = "options-value:";

/// `epilogue:<p>:close` — the epilogue's own input with INTERACT (13) for local player p (player one
/// always closes it; another player only outside the live session). `credits:<p>:skip` — the credits
/// screen's own input with GUI_SELECT (72): it ends the roll only with a local partner or in the live
/// session; alone the guest refuses it (end_screen_hooks.cpp).
inline constexpr char kEpilogueAction[] = "epilogue:";
inline constexpr char kCreditsAction[] = "credits:";
bool AnswerEndScreen(PPCContext& ctx, uint8_t* base, const char* action);

/// `map:<p>:<id>` — a world map button action (120 accept, 121 cancel, 123 / 124 the context actions,
/// 14 pause) handed to the map's own action forwarder for local player p, refused unless p is the
/// player the map's mode registered. `map-held:<p>:<9|10>:<down|up>` — LB / RB held by that player,
/// sent to the map's raw forwarder once a guest tick while down (map_input.cpp).
inline constexpr char kWorldMapAction[] = "map:";
inline constexpr char kWorldMapHeldAction[] = "map-held:";
bool AnswerWorldMap(PPCContext& ctx, uint8_t* base, const char* action);
/// One guest tick of the map's held buttons.
void PumpWorldMap(PPCContext& ctx, uint8_t* base);

/// Answers a box from a `box:` / `menubox` action. False when it names nothing on screen.
bool AnswerBoxAction(PPCContext& ctx, uint8_t* base, const char* action);

/// Runs an `options:` action through the Options screen's handler. False when none shows.
bool AnswerOptionsAction(PPCContext& ctx, uint8_t* base, const char* action);

/// `savemenu-row:<p>:<row>[:accept]` — the open save menu's cursor stepped to row 1..4 by its own
/// steps; with `:accept`, then 72 through its input for local player p.
inline constexpr char kSaveMenuRowAction[] = "savemenu-row:";
bool AnswerSaveMenuRow(PPCContext& ctx, uint8_t* base, const char* action);
/// One guest tick of that stepping.
void PumpSaveMenuRow(PPCContext& ctx, uint8_t* base);

/// `world-map:close:<p>` — the open world map asked to close for local player p: the guest's own
/// WorldMap.Exit on the map's game mode while it is the top mode (world_map_close.cpp). False when
/// the map is not the top mode. GUI.ToggleWorldMap is never the close.
inline constexpr char kWorldMapCloseAction[] = "world-map:close:";
bool AnswerWorldMapClose(PPCContext& ctx, uint8_t* base, const char* action);

/// `pc-extra:quicksave:<p>` / `pc-extra:safety:<p>` — the PC build's quicksave and safety toggle
/// for local player p, run on the guest's own functions (pc_extras.cpp).
inline constexpr char kPcExtraAction[] = "pc-extra:";
bool AnswerPcExtra(PPCContext& ctx, uint8_t* base, const char* action);

/// Puts Godot's cursor on an `options-row:` action's row. False when none shows or the page has no
/// such row.
bool AnswerOptionsRow(PPCContext& ctx, uint8_t* base, const char* action);
/// Takes an `options-value:` action's working value. False when none shows or the page has no
/// such row.
bool AnswerOptionsValue(PPCContext& ctx, uint8_t* base, const char* action);

/// `interaction-list:<p>:up` / `:down` — one step of the interaction list's cursor through the
/// list's own input for local player p (interaction_list_step.cpp). False with no list up.
inline constexpr char kInteractionListAction[] = "interaction-list:";
bool AnswerInteractionListStep(PPCContext& ctx, uint8_t* base, const char* action);
/// The list the guest handed its row writer, and its end (the hide; the mode's set, clear, close).
void NoteInteractionList(uint32_t list);
void ForgetInteractionList();

/// `emotion:<p>:<code>` — an emotion choice's button for local player p, as the game's own action
/// code (GAME_ACTION_EMOTION_UI_CHOICE_1..5_PRESSED 103..107 on the button going down, _RELEASED
/// 108..112 on its going up), through the choice UI's own input (emotion_choice_answer.cpp).
inline constexpr char kEmotionChoiceAction[] = "emotion:";
bool AnswerEmotionChoice(PPCContext& ctx, uint8_t* base, const char* action);

/// `worldicon:<p>:hold:<down|up>` — the world icon's hold button (A) pressed or released by local
/// player p, through the icon registry's own input (world_icon_hold.cpp); while it is down its held
/// code goes each tick.
inline constexpr char kWorldIconAction[] = "worldicon:";
bool AnswerWorldIconHold(PPCContext& ctx, uint8_t* base, const char* action);
/// One guest tick of the world icon's held button.
void PumpWorldIconHold(PPCContext& ctx, uint8_t* base);

/// `dpad:<p>:<code>` — a HUD d-pad direction for local player p, as the game's own action code
/// (GAME_ACTION_GUI_UP / _DOWN / _LEFT / _RIGHT 29..32 going down, their _RELEASED 33..36 going up),
/// through the d-pad widget's own input (hud_dpad_use.cpp).
inline constexpr char kHudDpadAction[] = "dpad:";
bool AnswerHudDpad(PPCContext& ctx, uint8_t* base, const char* action);

/// `interaction:<p>:<code>[:<value>]` — a game action (EGameAction code; `value` the stick's for 26 /
/// 27) for local player p through the interaction manager's own input (interaction_mode_input.cpp).
inline constexpr char kInteractionAction[] = "interaction:";
bool AnswerInteractionInput(PPCContext& ctx, uint8_t* base, const char* action);
/// The interaction mode as its openers and close state it: the mode set's second argument once its
/// clear ran, a dedicated opener's clear, the close's entry. `im` is their first argument.
void NoteInteractionModeSet(uint32_t im, int32_t mode);
void NoteInteractionModeOpenedByOpener(uint32_t im);
void NoteInteractionModeClosed(uint32_t im);

/// The pause screen's message box came up on `manager` with `text` (sub_825F9F38's arguments).
void StateModalMessageBox(uint32_t manager, const std::string& text, bool download);

/// The list length the stats screen's writer of `page` (2 categories, 3 statistics, 4 friends) last
/// stated for `box` (`@Screen.StatsWindow`'s count, screen_stated_hooks.cpp); 0 before it.
uint32_t StatsRowCount(uint32_t box, int32_t page);

/// `@Screen.StatsWindow(box, page, start, count, rows)` as a stats page's writer decided it
/// (stats_screen_stubs.cpp); the count is what StatsRowCount answers.
void StateStatsWindow(uint32_t box, int32_t page, uint32_t start, uint32_t count, int32_t rows);

/// Whether `box` is a queue's showing box, as the queue tick stated it.
bool BoxIsShowing(uint32_t box);

// THE OWNED BOXES' GATE (display_box_hooks.cpp), for the box screens answered in other files.
/// A guest function as codegen names it.
using GuestFunction = void (*)(PPCContext&, uint8_t*);
/// An owned box's pad handler: Godot's answer runs `original`; the guest's pad is consumed (the
/// router does not read the answer, TU3 0x825F397C / disc 0x825C8BEC) after its player is noted.
void GateOwnedInput(PPCContext& ctx, uint8_t* base, GuestFunction original);
/// Runs `handler(box, {action, 0, 0, 0}, player)` as Godot's answer; the handler's own answer.
uint32_t RunOwnedHandler(PPCContext& ctx, uint8_t* base, GuestFunction handler, uint32_t box, uint32_t action,
                         uint32_t player);
/// The message system's event serial (the save menu's chain), 0 when unreadable.
uint32_t MessageSerialNow(const uint8_t* base);
/// The guest's own cue call: `sub_823333B0(&handle, GUI, cue)` / disc sub_8232C4A0, then the handle's
/// release sub_82E02BE8 / disc sub_82DCC978 — for a cue a mover Godot replaced would have played.
void PlayGuestCue(PPCContext& ctx, uint8_t* base, uint32_t cue);
/// The pad a box's entity holds — its weak reference at `entity_ref`, the entity's component 37 `+0x14
/// -> +0x24`, -1 when `+0x14` is null — or kNoOwnerTest when the entity does not live or has no
/// component 37, where the join options' and the bed's handlers skip the test (session_boxes.cpp).
inline constexpr int64_t kNoOwnerTest = -2;
int64_t BoxOwnerPad(const uint8_t* base, uint32_t entity_ref);

/// A `box:` answer for a session box (session_boxes.cpp: join / visibility options, the friends
/// channel, co-op partners) or a family box (family_boxes.cpp: divorce, bed, treasury statement,
/// marriage proposal, food query), by the box's TU3 vtable. False when `vtable` is none of theirs.
bool AnswerSessionBox(PPCContext& ctx, uint8_t* base, uint32_t box, uint32_t vtable, const std::string& verb,
                      const std::string& value, uint32_t player, const char* action);
bool AnswerFamilyBox(PPCContext& ctx, uint8_t* base, uint32_t box, uint32_t vtable, const std::string& verb,
                     const std::string& value, uint32_t player, const char* action);

// THE DIVORCE CONFIRMATION (divorce_confirm.cpp).
/// The divorce box's accept is running on this thread: a box pushed now is its confirmation.
bool DivorceAccepting();
void NoteDivorceConfirmQueued(uint32_t box);
/// A yes/no box's own handler took Godot's answer; `posted` whether its event 68 went out, `serial`
/// the message serial before it, which is the posted record's own serial.
void NoteDivorceConfirmAnswered(uint32_t box, bool yes, bool posted, uint32_t serial);
void NoteDivorceConfirmGone(uint32_t box);
void ForgetDivorceConfirms();

// THE SAVE MENU'S CONFIRMATION (save_menu_confirm.cpp).
/// The save menu's accept is running on this thread: a box pushed now is its confirmation.
bool SaveMenuAccepting();
/// `box` was pushed by the save menu's accept.
void NoteSaveConfirmQueued(uint32_t box);
/// A yes/no box's own handler took Godot's answer `yes`; `posted` whether it posted its event 68.
void NoteQuestionAnswered(uint32_t box, bool yes, bool posted);
/// `box` left its queue (released after its hide, or dropped while pending).
void NoteBoxGone(uint32_t box);
/// The screen manager and its save menu were freed.
void ForgetSaveConfirms();

// THE REAL-ESTATE MODE'S CONFIRMATIONS (real_estate_confirm.cpp).
/// The mode's row handler (or its yes/no poll, which can ask again) is running on this thread: a
/// box pushed now is that mode's confirmation.
bool RealEstateAsking();
/// `box` was pushed by the running row handler.
void NoteRealEstateConfirmQueued(uint32_t box);
/// A box's own handler took Godot's answer — a question's yes or no, or (`adjuster`) an adjuster's
/// accept; the message serial before and after the handler says which events it posted.
void NoteRealEstateAnswered(uint32_t box, bool adjuster, uint32_t serial_before, uint32_t serial_after);
/// `box` left its queue.
void NoteRealEstateBoxGone(uint32_t box);
/// The screen manager and its queues were freed.
void ForgetRealEstateConfirms();
/// `realestate:<p>:row:<id>` — a row of the real-estate mode up, by the id its populate stated,
/// asked for local player p through the interaction manager's own input (real_estate_mode.cpp).
inline constexpr char kRealEstateAction[] = "realestate:";
bool AnswerRealEstateRow(PPCContext& ctx, uint8_t* base, const char* action);

}  // namespace refii::fable3
