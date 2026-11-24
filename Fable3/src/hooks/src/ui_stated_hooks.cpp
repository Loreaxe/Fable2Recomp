// refii - Fable 3's UI writers STATED AT THEIR ENTRY: the value each caller passes, before the
// writer looks for the element, its text box, its opacity or its renderable — the things a
// headless presentation does not have. gui_element_hooks.cpp keeps its after-the-fact reads
// (`@Element.Text` from box+0x88, the toggle's opacity) as the cross-check Godot compares these
// against; what Godot draws is what is said here.
//
// Every address is the Fable 3 disc IDB (port 13339); each offset names its base.
//
// THE PRESENTATION'S DOCUMENT. Every .bgf is loaded through ONE function, sub_825F27D8(slot,
// CCharString* name): when `*slot` is empty (0x825F27E8) it builds the path (sub_821DAFB0 at
// 0x825F2808) and stores the loader's answer `K->vt[7](K, path)` into `*slot` (0x825F2878). Its
// callers name the file: the HUD `"HUD.bgf"` (sub_824CD3F8 at 0x824CD42C), the interaction mode
// `"InteractionMode.bgf"` (sub_825EF150 at 0x825EF18C) and the screen loader sub_825F26E8 (a
// screen id's name from its map at +160, 0x825F2784). `@Presentation.Load(pres, file)` is that
// store: Godot keys every statement by the document, never by the address. Every statement after
// the load carries the document itself (`=hud`, PresentationKey) in place of the address, so a
// load statement the queue dropped (kMaxPending, oldest first) costs nothing but the reload's
// clean slate.
//
// TEXT. sub_82876D78(CCharString* path, pres, LHString* text) resolves the path from the
// presentation's root (sub_82877D40 at 0x82876DA4) and commits ONLY through elem+4 (0x82876DB8..
// 0x82876DD4) — the text is r5 at entry (`mr r30,r5` 0x82876D98). Its callers include the
// interaction title/description writer sub_8296C650, which resolves `<NPCInfo>.Name` /
// `.PersonalityTraits` (dword_835995A0 / 908, built by sub_831E65F0 / sub_831E6648) and clears
// `.Reward` / `.GuildSealsValue` with the empty string word_82000B4C (0x8296C6B4, 0x8296C6DC).
//
// THE INFO PANES' TEXT sub_82BDC5D8(pane, CCharString* field, LHString* text, r6, pres): the
// presentation is r7 or the interaction mode's IM+0 (0x82BDC634); the field resolves from it
// (0x82BDC64C) and its box is committed only when it is a text node (kind 5, 0x82BDC66C). 48 calls:
// the NPC panel (CInteractionModeMenuNPC_vf17 0x829AD17C / 0x829AD264 ...), real estate
// (sub_82B57178), the world map (sub_8271AE38).
//
// THE INTERACTION TEXT sub_82C203B8(wrapper, LHString* text) commits to the wrapper's +44 path
// with "1" appended (`sub_822E6630(&s, 1)` = "%d", 0x82C20514; joined 0x82C20524) when that element
// resolves, else to the +44 path itself (0x82C20550..0x82C20560: CrimeInfo's `Contents.Crimes` has
// no `Crimes1`), and defers the whole call while the mask is unsized (the text kept at +32, re-run
// by vf3 0x82C20370). It is stated where it commits, on the box guard 0x82C206E4 with the resolved
// element r30 and the text r26 (headless_commit_hooks.cpp), not at entry.
//
// THE INTERACTION BUTTONS sub_8296FF98(mode, button, LHString* label, CCharString* glyph,
// enabled): opacity 100 enabled / 50 not (0x8296FFE0 / 0x8296FFE8, SetOpacity 0x8296FFF4), the
// label committed to the button's `Title` (0x82970008, 0x8297003C), the glyph set on
// `Icon.Container.A_Text` (dword_8359CEB4 "Icon", dword_8359CEBC "Container.A_Text"; SetTexture
// 0x829700BC) and the button shown (sub_828777E8(button, pres, 1) at 0x829700EC) — all behind
// `**(mode+20)` (IM+0, 0x8296FFB8). `@Interaction.Button(pres, button path, label, glyph, enabled)`.
//
// THE LIST sub_82972A70(mode, scroll 0 refresh / 1 down / 2 up): its rows are the mode's own
// (vt+48 label, vt+52 icon) drawn through the button writer above; the cursor is mode+116 and the
// count mode+120 (as sub_8296A740 sets them, disc 0x8295B1D0 / disc 0x8295B1EC). The whole body is behind
// IM+0 (0x82972A88 / 0x82972A90), so the cursor is stated at its entry: `@Interaction.List`.
//
// SLIDES BY PATH. sub_82878130(CCharString* path, pres, slide) and its restarting twin
// sub_82878208(path, pres, slide) (GoToSlideAndRestart sub_82878198: SetState 0x828781B4, then the
// timeline to 0 at 0x828781E8) resolve the path from the presentation's root (sub_82877D40 at
// 0x82878164 / 0x8287823C) behind `if (pres)` (0x82878150 / 0x82878228) and hand the slide to
// SetState (0x82878178) / GoToSlideAndRestart (0x82878250): `@Ui.State` at entry. The interaction
// list is written this way and only this way — its refresh 2 (sub_82972870 at 0x829728B8), its
// hide 1 (sub_82972920 at 0x82972970, the mode exit sub_8296F9B0 at 0x8296FA18), its scroll down
// 3 / up 4 (sub_829729A8 at 0x82972A2C, sub_82972ED8 at 0x829731B0).
//
// THE TEXTURE SETTER sub_82876BF8(CCharString* path, elem): `@Ui.Texture`, the path as its caller
// passes it (gui_element_hooks.cpp reads it at entry): the interaction buttons' glyph on
// `Icon.Container.A_Text` (sub_8296FF98 at 0x829700BC) and the orb sub_829700F8 pairs with it on
// `A_Orb` (0x8297029C).
//
// THE DYE BAR sub_8296C150(mode, f1 r, f2 g, f3 b, f4 value, f5 min, f6 max, r10 player) and
// THE WEAPON PANEL sub_8296BD10(mode, r4 kind, r5 player): both ask sub_82966E10(player) first
// and send a remote player's to the partner (0x8295C9xx / 0x8296BD2C); stated at entry as
// `@Interaction.DyeBar` / `@Interaction.SidePanel`, their meters and toggles being the registry
// writers hud_hooks.cpp states.

#include <atomic>
#include <cstdint>
#include <cstring>
#include <functional>
#include <mutex>
#include <string>
#include <unordered_map>

#include <rex/ppc.h>
#include <rex/system/kernel_state.h>
#include <rex/system/xmemory.h>

#include "core/headless_presentation.h"
#include "gui_statements.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_GuiPresentation_Load);
REX_EXTERN(__imp__rex_GuiElement_SetTextAt);
REX_EXTERN(__imp__rex_InfoPane_SetText);
REX_EXTERN(__imp__rex_InteractionButton_Show);
REX_EXTERN(__imp__rex_InteractionList_Write);
REX_EXTERN(__imp__rex_InteractionMode_TitleDescription);
REX_EXTERN(__imp__rex_InteractionMode_DyeBar);
REX_EXTERN(__imp__rex_InteractionMode_SidePanel);
REX_EXTERN(__imp__rex_GuiElement_SetStateAt);
REX_EXTERN(__imp__rex_GuiElement_RestartStateAt);
REX_EXTERN(__imp__rex_GuiElement_SetTimeAt);
REX_EXTERN(__imp__rex_GuiElement_SetColour);

namespace {

using namespace refii::fable3;

const refii::guest::Addr kGuiManager{0x83596F84u};
constexpr uint32_t kGuiInteraction = 0x48u;  // GUI+72 = IM
constexpr uint32_t kImPresentation = 0u;     // IM+0, InteractionMode.bgf
constexpr uint32_t kModeIm = 20u;            // a mode object's IM (sub_8296FF98 at 0x8296FFB8)
// The list's cursor and count on the mode object, as the list's builder stores them: TU3 +0x2C/+0x30
// (sub_8296A740 `stw r3,0x2C(r31)` 0x8296AA28, `stw r11,0x30(r31)` 0x8296AA44), disc +0x74/+0x78
// (disc sub_8295AEE8 0x8295B1D0 / 0x8295B1EC).
uint32_t ModeCursor() { return refii::guest::Pick(0x2Cu, 0x74u); }
uint32_t ModeCount() { return refii::guest::Pick(0x30u, 0x78u); }

// The interaction writers' paths, CCharString handles their static initialisers build.
const refii::guest::Addr kNpcName{0x835995A0u};            // "<NPCInfo>.Name"           sub_831E65F0
const refii::guest::Addr kNpcTraits{0x835995A4u};          // "<NPCInfo>.PersonalityTraits" sub_831E6648
const refii::guest::Addr kNpcReward{0x835995A8u};          // "<NPCInfo>.Reward"         sub_831E66A0
const refii::guest::Addr kNpcGuildSeals{0x835995ACu};      // "<NPCInfo>.GuildSealsValue" sub_831E66F8

constexpr size_t kMaxValues = 16384u;

std::mutex g_mutex;
/// (channel, presentation, element, path) -> the hash of the last value posted.
std::unordered_map<size_t, size_t> g_last;

size_t KeyOf(uint32_t channel, uint32_t presentation, uint32_t element, const std::string& path) {
  size_t key = std::hash<std::string>{}(path);
  key ^= (static_cast<size_t>(channel) << 1) + 0x9E3779B97F4A7C15ull + (key << 6) + (key >> 2);
  key ^= (static_cast<size_t>(element) << 3) + 0x9E3779B97F4A7C15ull + (key << 6) + (key >> 2);
  if (element == 0u) {
    key ^= (static_cast<size_t>(presentation) << 5) + 0x9E3779B97F4A7C15ull + (key << 6) + (key >> 2);
  }
  return key;
}

/// Whether the value differs from the last one stated on this key (and records it).
bool Changed(uint32_t channel, uint32_t presentation, uint32_t element, const std::string& path, size_t value) {
  std::lock_guard<std::mutex> lock(g_mutex);
  if (g_last.size() > kMaxValues) {
    g_last.clear();
  }
  auto [it, inserted] = g_last.emplace(KeyOf(channel, presentation, element, path), value);
  if (!inserted && it->second == value) {
    return false;
  }
  it->second = value;
  return true;
}

void Post(const char* name, uint32_t presentation, uint32_t element, const std::string& path,
          refii::vfs::GuiNativeArgument value, const char* via) {
  Statement(name)
      .Add(Text(PresentationKey(presentation)))
      .Add(Text(Hex(element)))
      .Add(Text(path))
      .Add(std::move(value))
      .Add(Text(via))
      .Post();
}

uint32_t InteractionPresentation(const uint8_t* base) {
  const uint32_t gui = LoadGuestU32(base, kGuiManager);
  if (!Plausible(gui)) {
    return 0u;
  }
  const uint32_t im = LoadGuestU32(base, gui + kGuiInteraction);
  return Plausible(im) ? LoadGuestU32(base, im + kImPresentation) : 0u;
}

/// The presentation a mode object draws in: `**(mode+20)`.
uint32_t ModePresentation(const uint8_t* base, uint32_t mode) {
  if (!Plausible(mode)) {
    return 0u;
  }
  const uint32_t im = LoadGuestU32(base, mode + kModeIm);
  return Plausible(im) ? LoadGuestU32(base, im + kImPresentation) : 0u;
}

std::atomic<int64_t> g_list_last{-1};

/// Every presentation's document as its load named it (the file's stem, lower-cased).
std::mutex g_documents_mutex;
std::unordered_map<uint32_t, std::string> g_documents;

std::string DocumentName(const std::string& file) {
  size_t start = file.find_last_of("\\/");
  start = start == std::string::npos ? 0u : start + 1u;
  size_t end = file.find_last_of('.');
  if (end == std::string::npos || end <= start) {
    end = file.size();
  }
  std::string name = file.substr(start, end - start);
  for (char& c : name) {
    if (c >= 'A' && c <= 'Z') {
      c = static_cast<char>(c - 'A' + 'a');
    }
  }
  return name;
}

}  // namespace

namespace refii::fable3 {

uint32_t PresentationOfDocument(const std::string& document) {
  std::lock_guard<std::mutex> lock(g_documents_mutex);
  for (const auto& [presentation, name] : g_documents) {
    if (name == document && refii::headless::IsHeadless(presentation)) {
      return presentation;
    }
  }
  return 0u;
}

std::string PresentationKey(uint32_t presentation) {
  if (presentation != 0u) {
    std::lock_guard<std::mutex> lock(g_documents_mutex);
    auto found = g_documents.find(presentation);
    if (found != g_documents.end()) {
      return "=" + found->second;
    }
  }
  return Hex(presentation);
}

void StateText(uint32_t presentation, uint32_t element, const std::string& path, const std::string& text,
               const char* via) {
  if (!Changed(1u, presentation, element, path, std::hash<std::string>{}(text) ^ 0x1u)) {
    return;
  }
  Post("@Ui.Text", presentation, element, path, Text(text), via);
}

void StateTextTag(uint32_t presentation, uint32_t element, const std::string& path, uint32_t tag, const char* via) {
  // One text channel: a tag after a literal (or the reverse) is a change.
  if (!Changed(1u, presentation, element, path, (static_cast<size_t>(tag) << 1) ^ 0x2u)) {
    return;
  }
  Post("@Ui.TextTag", presentation, element, path, Text(Hex(tag)), via);
}

void StateTextName(uint32_t presentation, uint32_t element, const std::string& path, const std::string& name,
                   const char* via) {
  if (!Changed(1u, presentation, element, path, std::hash<std::string>{}(name) ^ 0x3u)) {
    return;
  }
  Post("@Ui.TextName", presentation, element, path, Text(name), via);
}

void StateVisible(uint32_t presentation, uint32_t element, const std::string& path, bool shown, const char* via) {
  if (!Changed(2u, presentation, element, path, shown ? 1u : 2u)) {
    return;
  }
  Post("@Ui.Visible", presentation, element, path, Boolean(shown), via);
}

// Every slide write is posted, a repeat of the same slide included: the funnel sub_82267158 enters
// the slide each time — it rewrites the slide byte and zeroes the component's time (+0x2B at
// 0x8226725C, +0x30 / +0x38 at 0x82267278 / 0x82267270) with no comparison — and the restarting
// setter sub_82878198 also puts its timeline back to 0 (`vt[9]()->vt[15](tl, elem, 0)` at
// 0x828781E8). Godot's playback re-enters on each one (Fable3HudDocument.Play).
void StateState(uint32_t presentation, uint32_t element, const std::string& path, int32_t slide, const char* via) {
  NoteStatedEntry(presentation, path);
  Post("@Ui.State", presentation, element, path, Number(static_cast<float>(slide)), via);
}

void StateOpacity(uint32_t presentation, uint32_t element, const std::string& path, float opacity, const char* via) {
  uint32_t bits = 0u;
  std::memcpy(&bits, &opacity, sizeof(bits));
  if (!Changed(9u, presentation, element, path, static_cast<size_t>(bits))) {
    return;
  }
  Post("@Ui.Opacity", presentation, element, path, Number(opacity), via);
}

void StateActive(uint32_t presentation, uint32_t element, const std::string& path, bool on, const char* via) {
  if (!Changed(6u, presentation, element, path, on ? 1u : 2u)) {
    return;
  }
  Post("@Ui.Active", presentation, element, path, Boolean(on), via);
}

void StateTexture(uint32_t presentation, uint32_t element, const std::string& path, const std::string& texture,
                  const char* via) {
  if (!Changed(5u, presentation, element, path, std::hash<std::string>{}(texture))) {
    return;
  }
  Post("@Ui.Texture", presentation, element, path, Text(texture), via);
}

}  // namespace refii::fable3

/// `@Presentation.Load(pres, file)` when the load filled an empty slot; every stated value is
/// forgotten, so the new document hears its texts again.
REX_EXTERN(rex_GuiPresentation_Load) {
  const uint32_t slot = ctx.r3.u32;
  const bool empty = base != nullptr && Plausible(slot) && LoadGuestU32(base, slot) == 0u;
  const std::string file = empty ? ReadCharString(base, ctx.r4.u32, 260u) : std::string();
  __imp__rex_GuiPresentation_Load(ctx, base);
  if (!empty) {
    return;
  }
  const uint32_t presentation = LoadGuestU32(base, slot);
  if (presentation == 0u) {
    return;
  }
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    g_last.clear();
  }
  {
    std::lock_guard<std::mutex> lock(g_documents_mutex);
    g_documents[presentation] = DocumentName(file);
  }
  ForgetPresentationReaders(presentation);
  g_list_last.store(-1);
  Statement("@Presentation.Load").Add(Text(Hex(presentation))).Add(Text(file)).Post();
}

/// sub_82876D78(path, pres, text): the text at entry.
REX_EXTERN(rex_GuiElement_SetTextAt) {
  if (base != nullptr) {
    const std::string path = ReadCharString(base, ctx.r3.u32);
    if (!path.empty()) {
      StateText(ctx.r4.u32, 0u, path, ReadLhString(base, ctx.r5.u32), "82819070");
    }
  }
  __imp__rex_GuiElement_SetTextAt(ctx, base);
}

/// sub_82BDC5D8(pane, field, text, r6, pres).
REX_EXTERN(rex_InfoPane_SetText) {
  if (base != nullptr) {
    const std::string field = ReadCharString(base, ctx.r4.u32);
    if (!field.empty()) {
      const uint32_t presentation = ctx.r7.u32 != 0u ? ctx.r7.u32 : InteractionPresentation(base);
      StateText(presentation, 0u, field, ReadLhString(base, ctx.r5.u32), "82BEA6D8");
    }
  }
  __imp__rex_InfoPane_SetText(ctx, base);
}

/// sub_8296FF98(mode, button, label, glyph, enabled).
REX_EXTERN(rex_InteractionButton_Show) {
  if (base != nullptr && Plausible(ctx.r4.u32)) {
    uint32_t named_in = 0u;
    std::string path;
    if (ElementName(ctx.r4.u32, named_in, path) && !path.empty()) {
      const uint32_t presentation = ModePresentation(base, ctx.r3.u32);
      const std::string label = ReadLhString(base, ctx.r5.u32);
      const std::string glyph = ReadCharString(base, ctx.r6.u32, 260u);
      const bool enabled = (ctx.r7.u32 & 0xFFu) != 0u;
      const size_t value = std::hash<std::string>{}(label + '\x1f' + glyph) ^ (enabled ? 0x55u : 0xAAu);
      if (Changed(3u, presentation, ctx.r4.u32, path, value)) {
        Statement("@Interaction.Button")
            .Add(Text(PresentationKey(presentation != 0u ? presentation : named_in)))
            .Add(Text(path))
            .Add(Text(label))
            .Add(Text(glyph))
            .Add(Boolean(enabled))
            .Post();
      }
    }
  }
  __imp__rex_InteractionButton_Show(ctx, base);
}

/// sub_82972A70(mode, scroll): the cursor and count it lays out, at entry.
REX_EXTERN(rex_InteractionList_Write) {
  if (base != nullptr && Plausible(ctx.r3.u32)) {
    const int32_t cursor = static_cast<int32_t>(LoadGuestU32(base, ctx.r3.u32 + ModeCursor()));
    const int32_t count = static_cast<int32_t>(LoadGuestU32(base, ctx.r3.u32 + ModeCount()));
    const int32_t scroll = static_cast<int32_t>(ctx.r4.u32);
    const int64_t key = (static_cast<int64_t>(cursor & 0xFFFF) << 24) | ((count & 0xFFFF) << 8) | (scroll & 0xFF);
    if (g_list_last.exchange(key) != key) {
      Statement("@Interaction.List")
          .Add(Number(static_cast<float>(cursor)))
          .Add(Number(static_cast<float>(count)))
          .Add(Number(static_cast<float>(scroll)))
          .Post();
    }
  }
  __imp__rex_InteractionList_Write(ctx, base);
}

/// THE LIST'S EMPTIED ROWS, stated at sub_82972A70's own commits. A row past the end (cursor+1 /
/// cursor+2 >= mode+120, 0x82972B98 / 0x82972C8C) and the leaving row DissapearingButton (always,
/// after the scroll filled it at 0x82972DDC / 0x82972E40) are not written through the button writer:
/// the body resolves the row's `Title` (dword_8359CECC, built "Title" by sub_8320DD60) and commits
/// the empty string word_82000B4C (sub_82299940 into r1+0x50) to its box, then hides the row
/// (sub_828777E8(row, pres, 0) at disc 0x82963FEC / disc 0x829640E0 / 0x82972ECC). Each [[midasm_hook]] sits on
/// the box guard `lwz r11,4(rTitle)` (0x82972C34 r27 / Button2 r28, 0x82972D28 r30 / Button1 r26,
/// 0x82972E84 r30 / DissapearingButton r24; the presentation r29 = `**(mode+20)`, 0x82972A88).
/// `@Ui.Text(pres, title, path, text, "82963DE0")`. The row's channel-3 value (what
/// rex_InteractionButton_Show last stated on it) becomes "emptied", so the writer's next label on the
/// row is stated even when it is the label the row held before it was emptied.
void RefiiF3InteractionRowEmptied(PPCRegister& title, PPCRegister& stack, PPCRegister& row,
                                  PPCRegister& presentation) {
  rex::system::KernelState* state = rex::system::kernel_state();
  rex::memory::Memory* memory = state != nullptr ? state->memory() : nullptr;
  const uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
  if (base == nullptr || !Plausible(title.u32) || !Plausible(row.u32)) {
    return;
  }
  uint32_t row_in = 0u, title_in = 0u;
  std::string row_path, title_path;
  if (!ElementName(row.u32, row_in, row_path) || row_path.empty() ||
      !ElementName(title.u32, title_in, title_path) || title_path.empty()) {
    return;
  }
  const std::string text = ReadLhString(base, stack.u32 + 0x50u);
  if (!Changed(3u, presentation.u32, row.u32, row_path, std::hash<std::string>{}("\x1e" + text) ^ 0x33u)) {
    return;
  }
  Post("@Ui.Text", presentation.u32 != 0u ? presentation.u32 : title_in, title.u32, title_path, Text(text),
       "82963DE0");
}

namespace {

/// A stated call site's own SetState / SetOpacity arguments (a [[midasm_hook]] on the `bl`):
/// the element named by the resolver hook, its presentation as the call passes it.
void StateCallSlide(uint32_t element, uint32_t presentation, int32_t slide, const char* via) {
  uint32_t named_in = 0u;
  std::string path;
  if (presentation != 0u && Plausible(element) && ElementName(element, named_in, path) && !path.empty()) {
    StateState(presentation, element, path, slide, via);
  }
}

void StateCallOpacity(uint32_t element, uint32_t presentation, float opacity, const char* via) {
  uint32_t named_in = 0u;
  std::string path;
  if (presentation != 0u && Plausible(element) && ElementName(element, named_in, path) && !path.empty()) {
    StateOpacity(presentation, element, path, opacity, via);
  }
}

}  // namespace

/// THE MULTIPLIER ROOTS AT THE HUD BUILD. sub_82973CB8(J, root path, text path, times path) runs
/// twice from the HUD load sub_824CD3F8: J = `*(H+0x44)` with "Multiplier" (dword_8359CF14,
/// 0x824CD4E8) and J = `*(H+0x44)+0x14` with "JobMultiplier" (dword_8359CF20, 0x824CD508). Once
/// all three paths resolved (0x82973D58) it rests the root `*J` on slide 4 — `li r5,4` 0x82973D64,
/// `lwz r3,0(r31)` 0x82973D68, `lwz r4,0(**(GUI+0x20))` 0x82973D70, `bl GuiElement_SetState`
/// 0x82973D74 — and hides it through sub_82973D90(J, 0) (0x82973D80). [[midasm_hook]] on that `bl`
/// [r3, r4, r5]: `@Ui.State(pres, root, path, 4, "82950A18")`.
void RefiiF3MultiplierSlide(PPCRegister& element, PPCRegister& presentation, PPCRegister& slide) {
  StateCallSlide(element.u32, presentation.u32, static_cast<int32_t>(slide.u32), "82950A18");
}

/// THE MULTIPLIER ROOT SHOWN OR HIDDEN, sub_82973D90(J, shown): f1 = 100 when shown (flt_8206D228,
/// 0x82973DC0) else 0 (flt_8206C918, 0x82973DC8) onto `*J` (`lwz r3,0(r31)` 0x82973DD0) in the HUD
/// (`lwz r4,0(**(GUI+0x20))` 0x82973DDC), `bl GuiElement_SetOpacity` 0x82973DE0; J+12 keeps the bool
/// (0x82973DE4). Its callers: the build above (hidden) and SetJobMultiplierLevel's sub_82973E00
/// from level 0 (0x82973ED4, shown). [[midasm_hook]] on the `bl` [r3, r4, f1]: `@Ui.Opacity`.
void RefiiF3MultiplierOpacity(PPCRegister& element, PPCRegister& presentation, PPCRegister& opacity) {
  StateCallOpacity(element.u32, presentation.u32, static_cast<float>(opacity.f64), "82950AF0");
}

/// THE HUD LAYER FADE sub_82973398(rec, GUI): while rec+4 is set (0x82973400) it fades one of
/// Layer's children — `*(*(H+0x24)+4)[rec+8]` (0x8297340C..disc 0x82B14AF0; NPCHighlights, ABXY,
/// SafeFrame, WorldIcons, FillWorldIcon, the two off-screen markers, filled by sub_8269F388) —
/// to the opacity sub_82973330 derives from rec+0, in the HUD (`lwz r4,0(r9)` 0x82973464), `bl
/// GuiElement_SetOpacity` 0x82973470. Hud_Update (0x82259288) drives it from the map at
/// `*(H+0x28)+4`, which no code found statically inserts into. [[midasm_hook]] on the `bl`
/// [r3, r4, f1]: `@Ui.Opacity(pres, child, path, f1, "82B14A50")`.
void RefiiF3LayerFade(PPCRegister& element, PPCRegister& presentation, PPCRegister& opacity) {
  StateCallOpacity(element.u32, presentation.u32, static_cast<float>(opacity.f64), "82B14A50");
}

/// THE OVERLAY BARS' TOPBOX SLIDE, sub_825F1CF0(IM, on): for the bumper bar IM+32 (vtable
/// 0x820D4940, stored 0x825EEF3C) and the trigger bar IM+36 (vtable 0x820D49B8, stored 0x825EEF90),
/// each while shown (+26, `lbz r10,0x1A(r11)` 0x825F1D18 / 0x825F1D5C): `SetState(bar+8, bar+4,
/// on ? 2 TopBoxOn : 3 TopBoxOff)` — `li r5,2` / `li r5,3` 0x825F1D3C..0x825F1D48, `bl` 0x825F1D4C;
/// 0x825F1D80..0x825F1D8C, `bl` 0x825F1D90. Its one caller is the TopBox publisher sub_8217D7A0:
/// off when the channels empty (0x8217D8AC), on when a message goes up (0x8217D8F8), both behind
/// IM+24 (`lwz r9,0x18(r3)` 0x8217D89C). [[midasm_hook]] on each `bl` [r3, r4, r5]: `@Ui.State`.
void RefiiF3OverlayBarTopBox(PPCRegister& element, PPCRegister& presentation, PPCRegister& slide) {
  StateCallSlide(element.u32, presentation.u32, static_cast<int32_t>(slide.u32), "825C6D78");
}

/// THE MINI-GAME COUNTER, sub_82716470(bar, player 1..4, LHString* text) (GUI.SetMiniGameCounter
/// 0x82552E0C, the bar's own 0x825F0AE4): `Bar.Elements.Text.Counter<n>` (dword_8359ACD4 + "%d",
/// 0x827164A4..0x827164B8) resolved in bar+4 (0x827164D8) goes to slide 2 — `li r5,2` 0x827164E4,
/// `lwz r4,4(r31)` 0x827164E8, `bl GuiElement_SetState` 0x827164EC; its `.Counter.Text` text is the
/// commit midasm at 0x82716524 (headless_commit_hooks.cpp). [[midasm_hook]] on the `bl` [r3, r4, r5].
void RefiiF3MiniGameCounterSlide(PPCRegister& element, PPCRegister& presentation, PPCRegister& slide) {
  StateCallSlide(element.u32, presentation.u32, static_cast<int32_t>(slide.u32), "826ECB18");
}

/// THE DECORATION MODE'S BUTTON, sub_829702B8(mode, button, tag, glyph) (ten calls from
/// CInteractionModeMenuDecoration_vf8 0x82B62C7C..0x82B62EF4): opacity 100 (flt_8206D228,
/// `lfs f1` 0x829702F8) onto the button (`mr r3,r28` 0x829702F4) in IM+0 (`mr r4,r31` 0x829702F0),
/// `bl GuiElement_SetOpacity` 0x829702FC; its `Title` tag (SetTextTag 0x8297032C), glyph (SetTexture
/// 0x829703AC) and show (sub_828777E8 0x829703DC) are stated by those writers' own hooks.
/// [[midasm_hook]] on the `bl` [r3, r4, f1]: `@Ui.Opacity`.
void RefiiF3DecorationButtonOpacity(PPCRegister& element, PPCRegister& presentation, PPCRegister& opacity) {
  StateCallOpacity(element.u32, presentation.u32, static_cast<float>(opacity.f64), "82961660");
}

/// THE WORLD MAP'S BUTTON, sub_8271A2D8(mode, CCharString* path, tag name, glyph) (the map menu
/// sub_8271CC88, 0x8271D098 ...): after offering it enabled through sub_8296FF98 (0x8271A348, stated
/// as `@Interaction.Button`) it sets the path's element to the caller's f1 (`fmr f1,f31` 0x8271A374)
/// in IM+0 (`mr r4,r31` 0x8271A370), `bl GuiElement_SetOpacity` 0x8271A378. [[midasm_hook]] [r3, r4, f1].
void RefiiF3WorldMapButtonOpacity(PPCRegister& element, PPCRegister& presentation, PPCRegister& opacity) {
  StateCallOpacity(element.u32, presentation.u32, static_cast<float>(opacity.f64), "82910D60");
}

/// THE WORLD MAP'S REGION, sub_8271B530(map, CCharString* name, slide): the region's element, cached
/// by the name's FNV-1 at map+348 (0x8271B55C..0x8271B658), goes to the caller's slide (`mr r5,r28`
/// 0x8271B65C) in IM+0 (`mr r4,r30` 0x8271B660), `bl GuiElement_SetState` 0x8271B668 (five calls
/// from sub_8271C338). [[midasm_hook]] on the `bl` [r3, r4, r5].
void RefiiF3WorldMapRegionSlide(PPCRegister& element, PPCRegister& presentation, PPCRegister& slide) {
  StateCallSlide(element.u32, presentation.u32, static_cast<int32_t>(slide.u32), "82911F08");
}

/// THE INTERACTION WORLD PANEL'S FLOOR, CInteractionModeGUIWorld_vf8 sub_82966850: when the
/// element's opacity reads under f31 (GetOpacity sub_82877268 0x82966DB8, `fcmpu` / `bge`
/// 0x82966DBC..0x82966DC0) it is set to f31 (`fmr f1,f31` 0x82966DC8) on r30 in r24, `bl
/// GuiElement_SetOpacity` 0x82966DD0 — the call is the statement, made only when the guest's own
/// read decides it. [[midasm_hook]] on the `bl` [r3, r4, f1].
void RefiiF3GuiWorldOpacity(PPCRegister& element, PPCRegister& presentation, PPCRegister& opacity) {
  StateCallOpacity(element.u32, presentation.u32, static_cast<float>(opacity.f64), "82956EF0");
}

/// sub_8296C650(mode, title, description): the four texts its body writes, at entry.
REX_EXTERN(rex_InteractionMode_TitleDescription) {
  if (base != nullptr && Plausible(ctx.r3.u32)) {
    const uint32_t presentation = ModePresentation(base, ctx.r3.u32);
    const char* via = "8295CE40";
    StateText(presentation, 0u, ReadCharString(base, kNpcName), ReadLhString(base, ctx.r4.u32), via);
    StateText(presentation, 0u, ReadCharString(base, kNpcTraits), ReadLhString(base, ctx.r5.u32), via);
    StateText(presentation, 0u, ReadCharString(base, kNpcReward), std::string(), via);
    StateText(presentation, 0u, ReadCharString(base, kNpcGuildSeals), std::string(), via);
  }
  __imp__rex_InteractionMode_TitleDescription(ctx, base);
}

/// sub_8296C150(mode, f1..f6, r10 player).
REX_EXTERN(rex_InteractionMode_DyeBar) {
  Statement("@Interaction.DyeBar")
      .Add(Number(static_cast<float>(ctx.f1.f64)))
      .Add(Number(static_cast<float>(ctx.f2.f64)))
      .Add(Number(static_cast<float>(ctx.f3.f64)))
      .Add(Number(static_cast<float>(ctx.f4.f64)))
      .Add(Number(static_cast<float>(ctx.f5.f64)))
      .Add(Number(static_cast<float>(ctx.f6.f64)))
      .Add(Text(Hex(ctx.r10.u32)))
      .Post();
  __imp__rex_InteractionMode_DyeBar(ctx, base);
}

/// sub_8296BD10(mode, kind, player).
REX_EXTERN(rex_InteractionMode_SidePanel) {
  Statement("@Interaction.SidePanel")
      .Add(Number(static_cast<float>(static_cast<int32_t>(ctx.r4.u32))))
      .Add(Text(Hex(ctx.r5.u32)))
      .Post();
  __imp__rex_InteractionMode_SidePanel(ctx, base);
}

/// sub_82878130(path, pres, slide): the slide at entry, behind the setter's own `if (pres)`.
REX_EXTERN(rex_GuiElement_SetStateAt) {
  if (base != nullptr && ctx.r4.u32 != 0u) {
    const std::string path = ReadCharString(base, ctx.r3.u32);
    if (!path.empty()) {
      StateState(ctx.r4.u32, 0u, path, static_cast<int32_t>(ctx.r5.u32), "8281A428");
      StatedPathScope scope(ctx.r4.u32, path);
      __imp__rex_GuiElement_SetStateAt(ctx, base);
      return;
    }
  }
  __imp__rex_GuiElement_SetStateAt(ctx, base);
}

/// sub_82878208(path, pres, slide): the same, for the restarting GoToSlide.
REX_EXTERN(rex_GuiElement_RestartStateAt) {
  if (base != nullptr && ctx.r4.u32 != 0u) {
    const std::string path = ReadCharString(base, ctx.r3.u32);
    if (!path.empty()) {
      StateState(ctx.r4.u32, 0u, path, static_cast<int32_t>(ctx.r5.u32), "8281A500");
      StatedPathScope scope(ctx.r4.u32, path);
      __imp__rex_GuiElement_RestartStateAt(ctx, base);
      return;
    }
  }
  __imp__rex_GuiElement_RestartStateAt(ctx, base);
}

/// sub_82878A50(path, pres, f1 fraction): a meter by path — behind `if (pres)` (0x82878A70) it
/// resolves the path and sets the timeline to `(int)(f1 * 1000)` (0x82878AAC, SetTime 0x82878AC0):
/// `@Ui.Time` at entry, in the timeline's units (hero status's Morality / Attractiveness / Weight,
/// sub_829BE760 at 0x829BE8E8 / 0x829BEA64 / 0x829BEA78).
REX_EXTERN(rex_GuiElement_SetTimeAt) {
  if (base != nullptr && ctx.r4.u32 != 0u) {
    const std::string path = ReadCharString(base, ctx.r3.u32);
    if (!path.empty()) {
      const int32_t time = static_cast<int32_t>(static_cast<float>(ctx.f1.f64) * 1000.0f);
      if (Changed(7u, ctx.r4.u32, 0u, path, static_cast<size_t>(static_cast<uint32_t>(time)))) {
        Post("@Ui.Time", ctx.r4.u32, 0u, path, Number(static_cast<float>(time)), "8281AD48");
      }
    }
  }
  __imp__rex_GuiElement_SetTimeAt(ctx, base);
}

/// sub_82876DF0(elem, pres, colour*): an element's colour at entry — behind `elem && pres`
/// (0x82876DF4 / 0x82876DFC) it writes the renderable's diffuse `*(*(elem+4))+0x40` as
/// (byte2, byte1, byte0, byte3) / 255 (0x82876E18..0x82876EA4, flt_832E93B8 = 1/255) and marks it
/// dirty (+0x84, 0x82876EA8): the four bytes are B, G, R, A. The money alert's text
/// (`MoneyAlert…Group.Text1`, A+56 resolved by sub_8287BB78 at 0x8287BBC8) takes white on show
/// and hide (dword_832E5ADC; 0x8287C038, 0x8287C7FC), green for a gain and red for a loss
/// (byte_832E5AF8 / byte_832E5AF4; 0x8287C344, the queued change 0x8287C114); the world map's
/// region text white / red (sub_8271F6D0 at 0x8271F7FC), the pooled world icons
/// (sub_82B874E0). `@Ui.Colour(pres, elem, path, r, g, b, a, via)`, 0..255.
REX_EXTERN(rex_GuiElement_SetColour) {
  const uint32_t element = ctx.r3.u32;
  const uint32_t presentation = ctx.r4.u32;
  if (base != nullptr && Plausible(element) && presentation != 0u && Plausible(ctx.r5.u32)) {
    const uint32_t bgra = LoadGuestU32(base, ctx.r5.u32);
    uint32_t named_in = 0u;
    std::string path;
    if (ElementName(element, named_in, path) && !path.empty() &&
        Changed(8u, presentation, element, path, static_cast<size_t>(bgra))) {
      Statement("@Ui.Colour")
          .Add(Text(PresentationKey(named_in != 0u ? named_in : presentation)))
          .Add(Text(Hex(element)))
          .Add(Text(path))
          .Add(Number(static_cast<float>((bgra >> 8) & 0xFFu)))
          .Add(Number(static_cast<float>((bgra >> 16) & 0xFFu)))
          .Add(Number(static_cast<float>(bgra >> 24)))
          .Add(Number(static_cast<float>(bgra & 0xFFu)))
          .Add(Text("828190E8"))
          .Post();
    }
  }
  __imp__rex_GuiElement_SetColour(ctx, base);
}
