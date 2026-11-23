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

/// The screen whose warning is raised, as OpenDialog's r3 stated it, until the dialog's
/// UNFREEZE; 0 when none is. `title` is set when that screen is the title, else the main menu.
uint32_t FrontEndDialogOwner(bool* title);

/// Whether the front-end GUI exists (its constructor ran and its destructor has not).
bool FrontEndUp();

/// Around one presentation event reaching its callback, by the registration's names.
void FrontEndEventBegin();
void FrontEndEventEnd(const char* presentation, const char* parent, const char* event);

}  // namespace refii::vfs
