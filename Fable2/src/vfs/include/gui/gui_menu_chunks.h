// The menu contract at the C++ boundary: attributes are staged onto the "Parameters" element
// (`g_Param.element`), then a one-line `g_Menu:` / `g_MenuFormatter:` Lua chunk reads them.
// See docs/ui_native_cut.md.
//   sub_826B9510  string stager  r4 = attr name, r5 = value, r6 = presentation (0 = current)
//   sub_826B9440  float  stager  r4 = attr name, f1 = value, r6 = presentation
//   sub_82426470  the runner     r3 = chunk source, r4 = presentation
// `r3` is dead on both stagers; do not read it. Chunk text is carried verbatim because some
// chunks bake their argument into the source. Every record carries its presentation: the
// non-expandable menu's `menu/menuemphasisandghosting` redefines
// `SetItemEmphasis`/`SetItemGhosting` with `Float1` as a boolean.

#ifndef REFII_VFS_GUI_GUI_MENU_CHUNKS_H
#define REFII_VFS_GUI_GUI_MENU_CHUNKS_H

#include <cstdint>
#include <string>
#include <vector>

#include "core/payload_bridge_fable2.h"

namespace refii::vfs {

/// The first chunk the guest runs on a presentation it initialises (`sub_823DCC70`, disc
/// `sub_823FE988`): the presentation's Lua state, and so its menu, starts here. Recorded with the
/// `g_Menu:` statements so a reused address does not carry the last instance's menu.
inline constexpr const char kPresentationInitChunk[] = "GUI:SetAsNotRunningInAnarkStudio()";

/// The staged attribute the runner adds to a `...Selected...` statement and to `PushMenuState`:
/// the name of the row Godot states as highlighted, which is the row the shipped script's
/// `CurrentHighlightIdx` named.
inline constexpr const char kHighlightedAttribute[] = "@Highlighted";

/// Beside it: that row's id as Godot states it, in decimal text (a float would round a record
/// hash). A name is not an identity — the shop spawns the same item name under a Buy group and a
/// Sell group, and once per unstacked copy (`sub_826CE230`, 0x826ce2f4).
inline constexpr const char kHighlightedIdAttribute[] = "@HighlightedId";

/// One staged attribute, as the C++ handed it to the stager.
struct MenuChunkAttribute {
    std::string name;   // "String1", "Float3", …
    std::string text;   // the string value, empty for a float
    float number = 0.0f;
    bool is_number = false;
};

/// One `g_Menu:` / `g_MenuFormatter:` mutation: the chunk and what was staged for it.
struct MenuChunkCall {
    std::string chunk;                       // the Lua source, verbatim
    uint32_t presentation = 0;               // which menu it was dispatched to
    std::vector<MenuChunkAttribute> staged;
    uint64_t sequence = 0;
};

/// Records a staged attribute. Called from the two stagers.
inline void StageMenuAttribute(std::string name, std::string text, float number,
                               bool is_number) {
  refii_bridge_stage_menu_attribute(name.data(), name.size(), text.data(), text.size(),
                                    number, is_number ? 1 : 0);
}

/// Records a chunk and consumes the staged bag; a leftover would attach to the next chunk.
inline void RunMenuChunk(std::string chunk, uint32_t presentation) {
  refii_bridge_run_menu_chunk(chunk.data(), chunk.size(), presentation);
}

/// Rows on `presentation`'s menu root — see `refii_bridge_menu_root_rows`.
inline uint32_t MenuRootRows(uint32_t presentation) {
  return refii_bridge_menu_root_rows(presentation);
}

/// The 1-based place of `presentation`'s last spawned row — see
/// `refii_bridge_menu_last_spawn_ordinal`.
inline uint32_t MenuLastSpawnOrdinal(uint32_t presentation) {
  return refii_bridge_menu_last_spawn_ordinal(presentation);
}

/// `GetMenuItemDataByName`'s `String2` for `presentation` — see
/// `refii_bridge_menu_by_name_text_tag`.
inline bool MenuByNameTextTag(uint32_t presentation, std::string* out) {
  char text[256] = {};
  if (!refii_bridge_menu_by_name_text_tag(presentation, text, sizeof(text))) {
    return false;
  }
  *out = text;
  return true;
}

/// The attribute `name` staged for the chunk about to run, read before it consumes the bag: its text and
/// number. False when none is staged.
inline bool StagedMenuAttributeNow(const char* name, std::string* text, float* number) {
    char buffer[512];
    if (refii_bridge_staged_menu_attribute(name, text != nullptr ? buffer : nullptr, sizeof(buffer), number) == 0) {
        return false;
    }
    if (text != nullptr) {
        *text = buffer;
    }
    return true;
}

/// Takes every recorded call. Empties the queue.
std::vector<MenuChunkCall> TakeMenuChunkCalls();

/// Puts `taken[from..]` back at the front of the queue, in order (a drain whose buffers were full).
void PutBackMenuChunkCalls(std::vector<MenuChunkCall>& taken, std::size_t from);

/// Recorded, pending, and how many were dropped to the ceiling.
void MenuChunkCounters(uint64_t* out_recorded, uint32_t* out_pending, uint64_t* out_dropped);

}  // namespace refii::vfs

#endif  // REFII_VFS_GUI_GUI_MENU_CHUNKS_H
