// refii - Fable 2's system keyboard: the guest asks the platform for text, and the host is the
// platform. Its one caller is the villager card's rename (gui_screen_stub_query.cpp), which the dog's
// naming reaches too (`Lua_Dog_TriggerRenameScreen` opens the card and asks at once).
//
// THE CALL. `sub_826CDCD8(h)` (disc `sub_82584A00`) calls the thunk `j_XamShowKeyboardUI` (TU1
// 0x82CC2E90, disc 0x82CBC600: `b XamShowKeyboardUI`, one caller each) at 0x826CDF60 / 0x82584C88
// with r3 the user index of player-map key 1, r4 0x20000004, r5 the default text and r8 the buffer
// (both h+34: the current name, cut to 16 units), r6 the title, r7 the description, r9 17 units,
// r10 the XOVERLAPPED at h+76, which it zeroed. A result of 997 marks the request pending (h+32 = 1,
// 0x826CDF70).
//
// THE ANSWER THE GUEST WAITS FOR. The card's Update (0x826CC9A8 / 0x825836D0) runs while h+32 is
// set: it waits for the XOVERLAPPED's first word to leave 997 (0x826CC9C8..0x826CC9D4), then
// `XGetOverlappedResult(overlapped, 0, 1)` (0x826CC9E0) answers that word. Non-zero clears h+32 and
// renames nothing; zero reads the buffer as the new name.
//
// THE HOST'S SIDE. The hook leaves the request pending and states it as `@Keyboard.Open(user, title,
// description, default, max units)`; Godot's platform keyboard (Engine_UiPlatformKeyboard) asks the
// player and posts the answer as a guest tick action (`keyboard:<text>` or `keyboard-cancel`), which
// the tick pump hands to AnswerKeyboardUi on the guest's own thread.
#include "hooks/keyboard_ui.h"

#include <cstdint>
#include <mutex>
#include <string>

#include <rex/logging.h>

#include "core/guest_state.h"
#include "core/gui_native_calls.h"
#include "hooks/ppc_externs.h"
#include "vfs_hooks_private.h"

namespace {

using refii::vfs::LoadGuestU16;
using refii::vfs::LoadGuestU32;
using refii::vfs::StoreGuestU16;
using refii::vfs::StoreGuestU32;

constexpr uint32_t kErrorSuccess = 0u;
constexpr uint32_t kErrorInvalidParameter = 87u;
constexpr uint32_t kErrorIoPending = 997u;
constexpr uint32_t kErrorCancelled = 1223u;

/// XOVERLAPPED: +0 InternalLow (the result), +4 InternalHigh, +12 hEvent, +16 completion routine,
/// +24 dwExtendedError.
constexpr uint32_t kOverlappedResult = 0u;
constexpr uint32_t kOverlappedHigh = 4u;
constexpr uint32_t kOverlappedEvent = 12u;
constexpr uint32_t kOverlappedRoutine = 16u;
constexpr uint32_t kOverlappedExtended = 24u;

constexpr uint32_t kMaxUnits = 512u;

struct Pending {
  uint32_t buffer = 0u;
  uint32_t units = 0u;
  uint32_t overlapped = 0u;
};

std::mutex g_mutex;
Pending g_pending;

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

void AppendUtf8(std::string& text, uint32_t code) {
  if (code < 0x80u) {
    text.push_back(static_cast<char>(code));
  } else if (code < 0x800u) {
    text.push_back(static_cast<char>(0xC0u | (code >> 6)));
    text.push_back(static_cast<char>(0x80u | (code & 0x3Fu)));
  } else if (code < 0x10000u) {
    text.push_back(static_cast<char>(0xE0u | (code >> 12)));
    text.push_back(static_cast<char>(0x80u | ((code >> 6) & 0x3Fu)));
    text.push_back(static_cast<char>(0x80u | (code & 0x3Fu)));
  } else {
    text.push_back(static_cast<char>(0xF0u | (code >> 18)));
    text.push_back(static_cast<char>(0x80u | ((code >> 12) & 0x3Fu)));
    text.push_back(static_cast<char>(0x80u | ((code >> 6) & 0x3Fu)));
    text.push_back(static_cast<char>(0x80u | (code & 0x3Fu)));
  }
}

/// A NUL-terminated UTF-16BE string at `address`, as UTF-8.
std::string ReadWide(uint8_t* base, uint32_t address) {
  std::string text;
  if (!Plausible(address)) {
    return text;
  }
  for (uint32_t i = 0; i < kMaxUnits; ++i) {
    uint32_t unit = LoadGuestU16(base, address + (i * 2u));
    if (unit == 0u) {
      break;
    }
    if (unit >= 0xD800u && unit < 0xDC00u) {
      const uint32_t low = LoadGuestU16(base, address + ((i + 1u) * 2u));
      if (low >= 0xDC00u && low < 0xE000u) {
        unit = 0x10000u + ((unit - 0xD800u) << 10) + (low - 0xDC00u);
        ++i;
      }
    }
    AppendUtf8(text, unit);
  }
  return text;
}

/// The next code point of UTF-8 `text` from `at`, advancing it; U+FFFD for a malformed byte.
uint32_t NextCodePoint(const char* text, size_t& at) {
  const auto byte = [&](size_t i) { return static_cast<uint8_t>(text[i]); };
  const uint8_t lead = byte(at);
  uint32_t code = 0xFFFDu;
  size_t length = 1u;
  if (lead < 0x80u) {
    code = lead;
  } else if ((lead & 0xE0u) == 0xC0u) {
    length = 2u;
  } else if ((lead & 0xF0u) == 0xE0u) {
    length = 3u;
  } else if ((lead & 0xF8u) == 0xF0u) {
    length = 4u;
  }
  if (length > 1u) {
    code = lead & (0xFFu >> (length + 1u));
    for (size_t i = 1; i < length; ++i) {
      const uint8_t next = byte(at + i);
      if ((next & 0xC0u) != 0x80u) {
        at += i;
        return 0xFFFDu;
      }
      code = (code << 6) | (next & 0x3Fu);
    }
  }
  at += length;
  return code;
}

void Add(refii::vfs::GuiNativeCall& call, std::string text) {
  refii::vfs::GuiNativeArgument argument;
  argument.kind = refii::vfs::GuiNativeArgument::Kind::kString;
  argument.text = std::move(text);
  call.arguments.push_back(std::move(argument));
}

void Add(refii::vfs::GuiNativeCall& call, float number) {
  refii::vfs::GuiNativeArgument argument;
  argument.kind = refii::vfs::GuiNativeArgument::Kind::kNumber;
  argument.number = number;
  call.arguments.push_back(std::move(argument));
}

}  // namespace

/// `XamShowKeyboardUI(user, flags, default, title, description, buffer, units, overlapped)`, through
/// its one guest thunk: left pending for the host to answer.
REX_EXTERN(rex_Xam_ShowKeyboardUI) {
  const uint32_t user = ctx.r3.u32;
  const uint32_t flags = ctx.r4.u32;
  const uint32_t default_text = ctx.r5.u32;
  const uint32_t title = ctx.r6.u32;
  const uint32_t description = ctx.r7.u32;
  const uint32_t buffer = ctx.r8.u32;
  const uint32_t units = ctx.r9.u32;
  const uint32_t overlapped = ctx.r10.u32;

  if (base == nullptr || !Plausible(buffer) || !Plausible(overlapped) || units == 0u || units > kMaxUnits) {
    ctx.r3.u64 = kErrorInvalidParameter;
    return;
  }
  if (LoadGuestU32(base, overlapped + kOverlappedEvent) != 0u ||
      LoadGuestU32(base, overlapped + kOverlappedRoutine) != 0u) {
    REXLOG_WARN("Refii keyboard: the XOVERLAPPED at {:08X} carries an event or a routine; the answer "
                "completes it by its result word only",
                overlapped);
  }

  // The default before the buffer is touched: the card passes one address for both.
  const std::string stated_default = ReadWide(base, default_text);
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    g_pending = Pending{buffer, units, overlapped};
  }
  StoreGuestU32(base, overlapped + kOverlappedHigh, 0u);
  StoreGuestU32(base, overlapped + kOverlappedExtended, kErrorIoPending);
  StoreGuestU32(base, overlapped + kOverlappedResult, kErrorIoPending);

  refii::vfs::GuiNativeCall call;
  call.name = "@Keyboard.Open";
  Add(call, static_cast<float>(user));
  Add(call, ReadWide(base, title));
  Add(call, ReadWide(base, description));
  Add(call, stated_default);
  Add(call, static_cast<float>(units));
  refii::vfs::PostGuiNativeCall(std::move(call));
  REXLOG_INFO("Refii keyboard: open for user {} (flags {:08X}, {} unit(s), overlapped {:08X}, default '{}')", user,
              flags, units, overlapped, stated_default);
  ctx.r3.u64 = kErrorIoPending;
}

namespace refii::vfs {

bool KeyboardUiPending() {
  std::lock_guard<std::mutex> lock(g_mutex);
  return g_pending.overlapped != 0u;
}

bool AnswerKeyboardUi(uint8_t* base, const char* text) {
  Pending pending;
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    pending = g_pending;
    g_pending = Pending{};
  }
  if (base == nullptr || pending.overlapped == 0u) {
    REXLOG_WARN("Refii keyboard: an answer with no keyboard open; dropped");
    return false;
  }

  uint32_t result = kErrorCancelled;
  uint32_t written = 0u;
  if (text != nullptr) {
    // The buffer holds `units` code units with the terminator.
    size_t at = 0u;
    while (text[at] != '\0' && written + 1u < pending.units) {
      uint32_t code = NextCodePoint(text, at);
      if (code >= 0x10000u) {
        if (written + 2u >= pending.units) {
          break;
        }
        code -= 0x10000u;
        StoreGuestU16(base, pending.buffer + (written * 2u), static_cast<uint16_t>(0xD800u + (code >> 10)));
        ++written;
        code = 0xDC00u + (code & 0x3FFu);
      }
      StoreGuestU16(base, pending.buffer + (written * 2u), static_cast<uint16_t>(code));
      ++written;
    }
    StoreGuestU16(base, pending.buffer + (written * 2u), 0u);
    result = kErrorSuccess;
  }

  // The result word last: the card's poll reads only it (0x826CC9C8).
  StoreGuestU32(base, pending.overlapped + kOverlappedExtended, result);
  __atomic_thread_fence(__ATOMIC_RELEASE);
  StoreGuestU32(base, pending.overlapped + kOverlappedResult, result);
  REXLOG_INFO("Refii keyboard: answered {} ({} unit(s))", text != nullptr ? "with text" : "cancelled", written);
  return true;
}

}  // namespace refii::vfs
