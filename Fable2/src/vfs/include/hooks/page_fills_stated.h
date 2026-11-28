// A PAGE FILL'S ELEMENT WRITES, STATED (page_fills_stated.cpp). A book page's fill names each element
// it writes (`sub_82426798(&name, pres)` — directly or through the name wrappers `sub_8246B888`,
// `sub_82426D18`, `sub_82426328`, `sub_82426140`) and hands it its text, slide, shown flag, opacity or
// text colour. While a fill is open on this thread those writes are collected by the name the fill
// gave, and its close states them as one `@PageFill <screen> <scope> <key> (<kind> <path> <value>)…`
// call. Only a name the fill looked up in the open scope is stated.
#ifndef REFII_VFS_HOOKS_PAGE_FILLS_STATED_H
#define REFII_VFS_HOOKS_PAGE_FILLS_STATED_H

#include <cstdint>
#include <string>
#include <utility>
#include <vector>

#include <rex/ppc/context.h>

namespace refii::vfs {

/// Opens a fill on this thread for `screen` under `scope` ("front", "row"). Fills do not nest: an open
/// inside an open joins the outer one, and only the outer close states it.
void OpenPageFill(uint32_t screen, const char* scope);

/// States what the open fill wrote under `key` (the row the fill was run for, 0 for none) and closes
/// it. A fill that wrote nothing states nothing. A `restated` fill is one the guest runs every tick (a
/// book's page tick): it is stated only when it wrote something other than its last statement, and any
/// other close on the same screen (a new open) forgets that last statement.
void ClosePageFill(uint32_t key, bool restated = false);

/// What the last outermost ClosePageFill on this thread did, for a caller that reports it (the world map's
/// minimap page states once per region change, gui_screen_stub_world_map.cpp).
struct PageFillClosed {
  /// A `@PageFill` statement went out.
  bool posted = false;
  /// A restated fill wrote exactly its last statement again: nothing went out.
  bool unchanged = false;
  /// Writes to elements the fill did not find by name (not stated).
  uint32_t unnamed = 0u;
  /// What went out, by kind in first-seen order: "texture 11, rotation 1, text 9, …".
  std::string counts;
  /// The first `map` mark's value (the minimap fill's projection), empty when none.
  std::string map;
};
const PageFillClosed& LastPageFillClosed();

/// Forgets every restated statement made on `screen`, so the next restated close posts what it wrote
/// even when that equals the last statement: a screen whose fills are all restated (the world map's
/// rows) calls it when it opens, the opening Godot drops nothing it was told.
void ForgetRestatedPageFills(uint32_t screen);

/// True while a fill is open on this thread.
bool PageFillOpen();

/// The open fill's serial on this thread (each outermost open takes the next), for a hook that states
/// something once per fill.
uint32_t PageFillSerial();

/// A text written to `element` while a fill is open (SetText / SetTextFromTag, noted where the
/// headless element is told: gui_headless_presentation.cpp).
void NotePageFillText(uint32_t element, const std::vector<uint16_t>& units);

/// A GDB record the open fill names under `what` ("preview": the record its preview was set to,
/// preview_item_stated.cpp; "card": the furniture card it took), stated as kind `record`.
void NotePageFillRecord(const char* what, uint32_t record);

/// A texture hash the open fill stored on `element` (`sub_82425C10`, or one of the inline quad stores
/// the minimap fill makes, minimap_page_stated.cpp), stated as kind `texture` with the hash in hex.
void NotePageFillTexture(uint32_t element, uint32_t hash);

/// A mark the open fill made that is not an element write — the minimap fill's projection and each of
/// its icons (minimap_page_stated.cpp) — stated in call order as `<kind> <label> <value>`.
void NotePageFillMark(const char* kind, std::string label, std::string value);

/// A `@PageFill <screen> <scope> <key>` of text writes only, each (path, text), stated as a fill closes —
/// for a page whose fill ran on the other co-op machine (hooks/coop_shop_for_joiner.cpp).
/// A FILL RUN FOR ANOTHER MACHINE, with no presentation here (the online joiner's shop item pane, run on the host:
/// coop_shop_for_joiner.cpp). While it is open the element finder `sub_82426798` answers each name with a stand-in
/// element (zeroed: its renderable `+4` is 0, so SetText / SetTextFromTag / the tint return at their own headless
/// tests), the fill's writes are noted as in any fill, and the writers that would reach the presentation itself
/// (slide, shown, opacity, text colour, position get / set) are noted and not run. The stand-ins live in one guest
/// block made once (`lhMalloc`); `ctx` is the caller's.
void OpenPageFillRelay(PPCContext& ctx, uint8_t* base);
void ClosePageFillRelay();
bool PageFillRelaying();
/// `element` is one of the relay's stand-ins.
bool PageFillStandIn(uint32_t element);
/// A write a by-name wrapper made in the open fill (the relay takes the wrappers before they find their element).
void NotePageFillNamed(const char* kind, std::string path, std::string value);

void StatePageFillTexts(uint32_t screen, const char* scope, uint32_t key,
                        const std::vector<std::pair<std::string, std::string>>& texts);

}  // namespace refii::vfs

#endif  // REFII_VFS_HOOKS_PAGE_FILLS_STATED_H
