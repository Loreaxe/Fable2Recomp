// refii - what Fable 3's UI hooks share: reading the guest's two string classes and posting a
// statement as a pseudo-call on the UI cut's queue (core/gui_native_calls.h), so Godot's
// Fable3GuiNatives routes `@Box.Open`, `@Element.Text` and the rest like any `GUI.*` call.
//
// CCharString: a handle word holding a rep pointer; rep+0 = `char*` (sub_82361988 reads a name
// as `*(char**)handle[0]`, 0x82361A1C). LHString (the game's WString): a handle word holding a rep
// pointer; rep+0 = UTF-16BE characters, rep+4 = length in code units (the compare sub_82299858
// walks `*v2` by `v2[1]`), the same shape the HUD registries' texts have (hud_hooks.cpp).

#pragma once

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>

#include <rex/ppc.h>

#include "core/gui_native_calls.h"

namespace refii::fable3 {

inline uint32_t LoadGuestU32(const uint8_t* base, uint32_t address) {
  uint32_t value = 0;
  std::memcpy(&value, base + address, sizeof(value));
  return __builtin_bswap32(value);
}

inline uint16_t LoadGuestU16(const uint8_t* base, uint32_t address) {
  uint16_t value = 0;
  std::memcpy(&value, base + address, sizeof(value));
  return __builtin_bswap16(value);
}

inline float LoadGuestF32(const uint8_t* base, uint32_t address) {
  const uint32_t bits = LoadGuestU32(base, address);
  float value = 0.0f;
  std::memcpy(&value, &bits, sizeof(value));
  return value;
}

/// A guest pointer, loosely: enough to refuse obvious rubbish while reading structures another
/// thread may be mutating.
inline bool Plausible(uint32_t value) { return value >= 0x10000u && value < 0xC0000000u; }

/// The CCharString whose handle word sits at `at`, bounded.
inline std::string ReadCharString(const uint8_t* base, uint32_t at, uint32_t max_chars = 160u) {
  std::string text;
  if (!Plausible(at)) {
    return text;
  }
  const uint32_t rep = LoadGuestU32(base, at);
  if (!Plausible(rep)) {
    return text;
  }
  const uint32_t chars = LoadGuestU32(base, rep);
  if (!Plausible(chars)) {
    return text;
  }
  for (uint32_t i = 0; i < max_chars; ++i) {
    const char ch = static_cast<char>(base[chars + i]);
    if (ch == '\0') {
      break;
    }
    text.push_back(ch);
  }
  return text;
}

inline void AppendUtf8(std::string& out, uint32_t code) {
  if (code < 0x80u) {
    out.push_back(static_cast<char>(code));
  } else if (code < 0x800u) {
    out.push_back(static_cast<char>(0xC0u | (code >> 6)));
    out.push_back(static_cast<char>(0x80u | (code & 0x3Fu)));
  } else if (code < 0x10000u) {
    out.push_back(static_cast<char>(0xE0u | (code >> 12)));
    out.push_back(static_cast<char>(0x80u | ((code >> 6) & 0x3Fu)));
    out.push_back(static_cast<char>(0x80u | (code & 0x3Fu)));
  } else {
    out.push_back(static_cast<char>(0xF0u | (code >> 18)));
    out.push_back(static_cast<char>(0x80u | ((code >> 12) & 0x3Fu)));
    out.push_back(static_cast<char>(0x80u | ((code >> 6) & 0x3Fu)));
    out.push_back(static_cast<char>(0x80u | (code & 0x3Fu)));
  }
}

/// The LHString whose handle word sits at `at`, as UTF-8.
inline std::string ReadLhString(const uint8_t* base, uint32_t at, uint32_t max_units = 1024u) {
  std::string text;
  if (!Plausible(at)) {
    return text;
  }
  const uint32_t rep = LoadGuestU32(base, at);
  if (!Plausible(rep)) {
    return text;
  }
  const uint32_t chars = LoadGuestU32(base, rep);
  const uint32_t length = LoadGuestU32(base, rep + 4u);
  if (!Plausible(chars) || length == 0u || length > max_units) {
    return text;
  }
  for (uint32_t i = 0; i < length; ++i) {
    uint32_t unit = LoadGuestU16(base, chars + (i * 2u));
    if (unit >= 0xD800u && unit < 0xDC00u && i + 1u < length) {
      const uint32_t low = LoadGuestU16(base, chars + ((i + 1u) * 2u));
      if (low >= 0xDC00u && low < 0xE000u) {
        unit = 0x10000u + ((unit - 0xD800u) << 10) + (low - 0xDC00u);
        ++i;
      }
    }
    AppendUtf8(text, unit);
  }
  return text;
}

inline std::string Hex(uint32_t value) {
  char buffer[16];
  std::snprintf(buffer, sizeof(buffer), "%08X", value);
  return buffer;
}

inline refii::vfs::GuiNativeArgument Text(const std::string& text) {
  refii::vfs::GuiNativeArgument argument;
  argument.kind = refii::vfs::GuiNativeArgument::Kind::kString;
  argument.text = text;
  return argument;
}

inline refii::vfs::GuiNativeArgument Number(float value) {
  refii::vfs::GuiNativeArgument argument;
  argument.kind = refii::vfs::GuiNativeArgument::Kind::kNumber;
  argument.number = value;
  return argument;
}

inline refii::vfs::GuiNativeArgument Boolean(bool value) {
  refii::vfs::GuiNativeArgument argument;
  argument.kind = refii::vfs::GuiNativeArgument::Kind::kBoolean;
  argument.boolean = value;
  return argument;
}

/// The Gameface element the registry filer sub_82BCF250(registry, element, pres) is filing on this
/// thread (gui_element_hooks.cpp), 0 outside a filing: the element a registry insert inside it
/// was made from.
uint32_t FilingElement();

/// The presentation and path the element hooks (gui_element_hooks.cpp) named `element` by — the
/// resolver's dotted path, a registry filing's `Attribute:value`, a Lua getElement's — false when
/// nothing named it.
bool ElementName(uint32_t element, uint32_t& presentation, std::string& path);

/// A presentation as a statement names it: `=<document>` once its load was seen (the .bgf's stem,
/// lower-cased: `=hud`, `=interactionmode`), else its address in hex (ui_stated_hooks.cpp).
std::string PresentationKey(uint32_t presentation);

/// THE STATED ELEMENT WRITES (ui_stated_hooks.cpp): each writer's VALUE as its caller passed it,
/// posted at the writer's entry — never read back from the text box, the opacity or the
/// renderable it ends in, which a headless element does not have. Keyed `(presentation, element,
/// path)`: the element when the writer takes one, else the path the writer resolves from the
/// presentation's root (element 0). Each posts only when the value changed; a presentation load
/// forgets every value, so a reloaded document hears its texts again.
/// `@Ui.Text(pres, elem, path, text, via)` — a literal string.
void StateText(uint32_t presentation, uint32_t element, const std::string& path, const std::string& text,
               const char* via);
/// `@Ui.TextTag(pres, elem, path, tag hex, via)` — a Babel tag by its FNV-1 (sub_82876F90's r4).
void StateTextTag(uint32_t presentation, uint32_t element, const std::string& path, uint32_t tag, const char* via);
/// `@Ui.TextName(pres, elem, path, name, via)` — a Babel tag by name (sub_82876EB0's r4).
void StateTextName(uint32_t presentation, uint32_t element, const std::string& path, const std::string& name,
                   const char* via);
/// `@Ui.Visible(pres, elem, path, shown, via)` — the visibility toggle's bool (sub_828777E8's r5).
void StateVisible(uint32_t presentation, uint32_t element, const std::string& path, bool shown, const char* via);
/// `@Ui.State(pres, elem, path, slide, via)` — a component's slide as its writer passes it: the
/// path setters' r5 (sub_82878130 / sub_82878208), the interaction mode tick's root slide and the
/// interaction bars' show (interaction_hooks.cpp).
void StateState(uint32_t presentation, uint32_t element, const std::string& path, int32_t slide, const char* via);
/// `@Ui.Opacity(pres, elem, path, opacity, via)` — an outright opacity a writer's own call passes
/// (sub_82877128's f1 at a stated call site).
void StateOpacity(uint32_t presentation, uint32_t element, const std::string& path, float opacity, const char* via);
/// `@Ui.Active(pres, elem, path, on, via)` — the active flag's request (sub_828773E8's r5): flag 0x2000,
/// `globalactive`, the whole subtree skipped when off.
void StateActive(uint32_t presentation, uint32_t element, const std::string& path, bool on, const char* via);
/// `@Ui.Texture(pres, elem, path, texture path, via)` — the texture setter's CCharString (sub_82876BF8's r3).
void StateTexture(uint32_t presentation, uint32_t element, const std::string& path, const std::string& texture,
                  const char* via);

/// The name hash of the text box whose lines sub_82976D48 is laying out on this thread
/// (topbox_hooks.cpp), 0 outside it: the line positions its layout writes (sub_82976790 ->
/// sub_82876288 at 0x8297695C) are the guest's own fit of its own font, which Godot replaces.
uint32_t LaidOutBox();

/// The HUD's TopBox text box name hash (`*dword_83598CD0`, the publisher sub_8217D7A0's r4 at
/// 0x8217DB40), 0 before the static initialiser ran.
uint32_t TopBoxName(const uint8_t* base);

/// States the GUI screen id `*(*(GUI+0x38)+4)` as one of its writers left it
/// (game_state_hooks.cpp): every writer that is not the screen set calls this as it returns.
void StateGuiScreen(const uint8_t* base);

/// The live headless presentation whose load named `document` (PresentationKey without its `=`), or 0.
uint32_t PresentationOfDocument(const std::string& document);

/// THE RUNTIME'S READERS ANSWERED FROM STATEMENTS (ui_reader_hooks.cpp).
/// A slide the guest set through GuiElement_SetState (gui_element_hooks.cpp, after the setter ran):
/// the path its path setter names the owner by, for the scroll gate.
void NoteStatedSlide(uint32_t element, uint32_t presentation, int32_t slide);
/// A `@Ui.State` posted for `path` (ui_stated_hooks.cpp StateState): the entry Godot counts too.
void NoteStatedEntry(uint32_t presentation, const std::string& path);
/// A presentation loaded into an empty slot: its stated slides and entries start afresh.
void ForgetPresentationReaders(uint32_t presentation);
/// hud.bgf loaded into an empty slot: `@Hud.Root` states the new root's first write (screen_stated_hooks.cpp).
void ForgetHudRoot();
/// The save menu freed with its screen manager: `@SaveMenu.Row` states the next menu's first write.
void ForgetSaveMenu();
/// The path setter (SetStateAt / RestartStateAt) running on this thread names the owner its
/// GuiElement_SetState reaches.
class StatedPathScope {
 public:
  StatedPathScope(uint32_t presentation, const std::string& path);
  ~StatedPathScope();
  StatedPathScope(const StatedPathScope&) = delete;
  StatedPathScope& operator=(const StatedPathScope&) = delete;

 private:
  uint32_t presentation_ = 0;
  std::string path_;
  uint32_t outer_presentation_ = 0;
  std::string outer_path_;
};
/// The guest-tick action prefix Godot states the readers' values under.
inline constexpr char kUiReaderAction[] = "ui-";
/// One posted action under kUiReaderAction: `ui-box:`, `ui-reached:` or `ui-live:`.
void AnswerUiReaderAction(const char* action);

/// A statement under a `@Family.Event` name with its arguments, in order.
class Statement {
 public:
  explicit Statement(const char* name) { call_.name = name; }
  Statement& Add(refii::vfs::GuiNativeArgument argument) {
    call_.arguments.push_back(std::move(argument));
    return *this;
  }
  void Post() { refii::vfs::PostGuiNativeCall(std::move(call_)); }

 private:
  refii::vfs::GuiNativeCall call_;
};

}  // namespace refii::fable3
