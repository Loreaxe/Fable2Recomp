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
/// (the button box's answer n, 1 A, 2 B, 3 X, 4 Y).
inline constexpr char kBoxAnswerAction[] = "box:";
/// `topbox:<p>:<a|y>:<down|up>` — the TopBox's A or Y pressed or released by local player p, handed
/// to its own input (topbox_answer.cpp); while one is down its held code goes each tick.
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
/// `options:<p>:<action>` — one game action into the showing Options screen's own handler, for
/// local player p (the handler takes its own hero's pad alone).
inline constexpr char kOptionsAction[] = "options:";
/// `options-row:<p>:<row>[:accept]` — the showing Options screen's cursor on its page's 1-based row,
/// by its own row setter or, where it has none, its own steps; with `:accept`, then A through
/// its handler; for local player p.
inline constexpr char kOptionsRowAction[] = "options-row:";

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

/// `pc-extra:quicksave:<p>` / `pc-extra:safety:<p>` — the PC build's quicksave and safety toggle
/// for local player p, run on the guest's own functions (pc_extras.cpp).
inline constexpr char kPcExtraAction[] = "pc-extra:";
bool AnswerPcExtra(PPCContext& ctx, uint8_t* base, const char* action);

/// Puts the Options screen's cursor on an `options-row:` action's row. False when none shows or
/// the page has no such row.
bool AnswerOptionsRow(PPCContext& ctx, uint8_t* base, const char* action);

/// `interaction-list:<p>:up` / `:down` — one step of the interaction list's cursor through the
/// list's own input for local player p (interaction_list_step.cpp). False with no list up.
inline constexpr char kInteractionListAction[] = "interaction-list:";
bool AnswerInteractionListStep(PPCContext& ctx, uint8_t* base, const char* action);
/// The list the guest handed its row writer, and its end (the hide; the mode's set, clear, close).
void NoteInteractionList(uint32_t list);
void ForgetInteractionList();

/// The pause screen's message box came up on `manager` with `text` (sub_825F9F38's arguments).
void StateModalMessageBox(uint32_t manager, const std::string& text, bool download);

/// Whether `box` is a queue's showing box, as the queue tick stated it.
bool BoxIsShowing(uint32_t box);

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

}  // namespace refii::fable3
