// The front end's stated model (hooks/frontend_stated.cpp): what the hooks in other files tell it.
#pragma once

#include <cstdint>

namespace refii::vfs {

/// Every stated GotoSlide (`sub_82BB5760`), counted against the front-end call in flight.
void FrontEndGotoSlide(uint32_t slide);

/// StampSaveCard ran inside the call in flight.
void FrontEndCardStamped();

/// The front end's IsHighlightedItemAtRoot answered inside the call in flight.
void FrontEndRootTestAnswered(bool at_root);

/// Around OpenDialog (`sub_826E40E0`, r3 `owner`): the owner's mode is 1 after it.
void FrontEndDialogOpening();
void FrontEndDialogOpened(uint32_t owner);

/// Around the main menu's activate (rex_lhGui_FrontendRoot_PopulateMainMenu, r3 `menu`): mode 0
/// after it.
void FrontEndMenuActivating();
void FrontEndMenuActivated(uint32_t menu);

/// The front end's two screens as their own activates stated them (the title's
/// `sub_826E30F0`, the main menu's `sub_826E1F70`, each with the screen in r3), 0 before the
/// activate ran or once the front-end GUI is gone. Objects to CALL, never to read.
uint32_t FrontEndTitleScreen();
uint32_t FrontEndMenuScreen();

/// The active one of the two (the title's while it is active, the main menu's while that is), 0
/// when the front end is not up or neither is active. `activation` counts the screens made active
/// across every front end (the construction's title, each switch): a new count is a new screen.
uint32_t FrontEndActiveScreen(uint32_t* activation);

/// The screen whose warning is raised, as OpenDialog's r3 stated it, until the dialog's
/// UNFREEZE; 0 when none is. `title` is set when that screen is the title, else the main menu.
uint32_t FrontEndDialogOwner(bool* title);

/// Whether the front-end GUI exists (its constructor ran and its destructor has not).
bool FrontEndUp();

/// The title's stated mode (9 legal, 0 idle, 8 attract, 10/11 start, 12 dialog, 1 dialog closing)
/// and its press-A latch; false while the title is not the active screen.
bool FrontEndTitleState(uint32_t* mode, bool* armed);

/// The pad the title's A was taken from: r5 of the title's input slot on the arm that writes
/// `dword_83496AF8` (TU1 0x826E30DC / disc 0x826C6370). 0 until a title has taken A.
uint32_t FrontEndPad();

/// Held around every call a pump lever makes into the front end's two input slots, so the
/// slots' hooks can tell the lever's calls from the pad route's (`sub_826E39C0` / disc
/// `sub_826C5B88`, the front end's only other caller of slot +12).
struct FrontEndLeverCall {
    FrontEndLeverCall();
    ~FrontEndLeverCall();
    FrontEndLeverCall(const FrontEndLeverCall&) = delete;
    FrontEndLeverCall& operator=(const FrontEndLeverCall&) = delete;
};

/// The main menu's stated mode (`screen+24`: 0 the list, 2-4 the gender card, 5 the load fan, 6 a
/// warning), as the model above keeps it; 0 when the front end is not up.
uint32_t FrontEndMenuMode();

/// Around one presentation event reaching its callback, by the registration's names.
void FrontEndEventBegin();
void FrontEndEventEnd(const char* presentation, const char* parent, const char* event);

}  // namespace refii::vfs
