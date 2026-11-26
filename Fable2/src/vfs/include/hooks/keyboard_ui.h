// The platform keyboard's answer (keyboard_ui.cpp), handed over by the guest tick pump.
#ifndef REFII_VFS_HOOKS_KEYBOARD_UI_H
#define REFII_VFS_HOOKS_KEYBOARD_UI_H

#include <cstdint>

namespace refii::vfs {

/// The guest tick action that answers the open keyboard with text: `keyboard:<UTF-8>`.
inline constexpr char kKeyboardAnswerAction[] = "keyboard:";

/// The guest tick action that cancels it.
inline constexpr char kKeyboardCancelAction[] = "keyboard-cancel";

/// Completes the pending `XamShowKeyboardUI` request on the guest's thread: `text` (UTF-8) written
/// into the guest's buffer and the XOVERLAPPED completed with success, or null to complete it as
/// cancelled. False when no keyboard is open.
bool AnswerKeyboardUi(uint8_t* base, const char* text);

}  // namespace refii::vfs

#endif
