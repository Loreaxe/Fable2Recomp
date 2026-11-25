// What the ported screens' files share (gui_screen_stub_*.cpp): guest calls on a scratch frame, guest
// strings, the GUI's objects. One screen per file; the levers Godot posts are declared in
// hooks/gui_screen_stubs.h.
#ifndef REFII_VFS_HOOKS_GUI_SCREEN_STUB_SUPPORT_H
#define REFII_VFS_HOOKS_GUI_SCREEN_STUB_SUPPORT_H

#include <cstdint>
#include <initializer_list>

#include <rex/ppc/context.h>

namespace refii::vfs::stub {

/// Scratch below the caller: the callee frames grow down from `frame`, the scratch sits above it.
struct Scratch {
  uint32_t frame;
  uint32_t data;
};

bool Plausible(uint32_t at);

uint8_t Load8(uint8_t* base, uint32_t at);

/// `G = *(0x83496B08)` (disc 0x834968A4).
uint32_t Gui(uint8_t* base);

/// The game object, `*(*(0x83496AB8)+12)`.
uint32_t Game(uint8_t* base);

/// The tutorial manager `*(GAME+168)` and its state `+4`.
uint32_t Tutorials(uint8_t* base);

/// The book screens' presentation, `*(*(G+56)+156)` (disc +152): the shared slot the screen
/// manager's `lhGuiPresentationSet_GetCurrent` (TU1 0x823DCBE0 / disc 0x823FE8F8) answers for every
/// screen id outside `sub_823DCDA0`'s own-presentation list {0, 3..13, 19}, and what the book
/// handlers read DIRECTLY, never through GetCurrent: logbook 0x826D8E50 / 0x826D90D8 / 0x826D914C /
/// 0x826D9958, options 0x826D9DB4 / 0x826D9E4C / 0x826D9E94, abilities 0x826C225C / 0x826C2290 /
/// 0x826C29A0 (disc 0x82581AE0 / 0x82581D68 / 0x82581DDC, 0x825829DC / 0x82582A74 / 0x82582ABC,
/// 0x8259E20C / 0x8259E240 / 0x8259E950). The pause root (screen 0, its own slot) takes GetCurrent
/// (GuestPresentation below).
uint32_t BookPresentation(uint8_t* base);

/// One guest call on a scratch frame below `ctx`; r3 back.
uint32_t Call(PPCContext& ctx, uint8_t* base, uint32_t frame, uint32_t tu1, std::initializer_list<uint32_t> args);

/// A virtual call, slot `slot` of `object`, on a scratch frame.
uint32_t CallSlot(PPCContext& ctx, uint8_t* base, uint32_t frame, uint32_t object, uint32_t slot,
                  std::initializer_list<uint32_t> args);

/// A guest CCharString at `handle` built from `text`, copied into the frame at `chars` first.
void MakeString(PPCContext& ctx, uint8_t* base, uint32_t frame, uint32_t handle, uint32_t chars, const char* text);

void DropString(PPCContext& ctx, uint8_t* base, uint32_t frame, uint32_t handle);

/// Runs `chunk` on `pres`, as `lhGui_RunLuaString` (TU1 0x82426470).
void RunMenuLua(PPCContext& ctx, uint8_t* base, uint32_t frame, uint32_t scratch, const char* chunk,
                uint32_t pres);

Scratch Claim(const PPCContext& ctx);

void Said(const char* screen, const char* slot, uint32_t handler, uint32_t a = 0u, uint32_t b = 0u);

/// The element a static name handle names in `pres`: `sub_821F0240` copies the handle into `slot`
/// and `sub_82426798` resolves it and releases the copy.
uint32_t Element(PPCContext& ctx, uint8_t* base, uint32_t frame, uint32_t slot, uint32_t name, uint32_t pres);

/// `sub_82426260(element, pres, on)` — the show / hide statement (opacity ×/÷ 1024).
void Toggle(PPCContext& ctx, uint8_t* base, uint32_t frame, uint32_t element, uint32_t pres, uint32_t on);

/// The presentation the guest's own code takes, `lhGuiPresentationSet_GetCurrent(*(G+56))` (TU1
/// 0x823DCBE0 / disc 0x823FE8F8): set+156 while `sub_823DCEF0` says a modal stands, else the current
/// screen's slot `set[set[1] + 3]`.
uint32_t GuestPresentation(PPCContext& ctx, uint8_t* base, uint32_t frame);

/// The current presentation's root element, as the root's Update and OnExit take it: pres vf+32, then
/// vf+68 (0x826A2620 / 0x826A2630); 0 without a presentation.
uint32_t PresentationRoot(PPCContext& ctx, uint8_t* base, uint32_t frame, uint32_t pres);

/// The handler of the screen with TU1 id `screen`, out of the guest's map
/// (`lhGuiManager_FindScreenHandler(QM, id)`, TU1 0x8236FFC0 / disc 0x823381B8) and checked against its
/// vtable (TU1 address `vtable`); 0 when absent.
uint32_t ScreenHandler(PPCContext& ctx, uint8_t* base, uint32_t frame, uint32_t screen, uint32_t vtable);

/// The highlighted row as a list screen's Update asks for it: `lhGuiMenu_GetHighlightedItemData(*(QM+60),
/// &row)` (TU1 0x826C1B50 / disc 0x8259DB00), answered natively from the row Godot states. `row` is 16
/// bytes of the caller's scratch ({type, id, String1, String2}), its two strings built at `chars` and
/// released before the return, as the callers release them.
void HighlightedRow(PPCContext& ctx, uint8_t* base, uint32_t frame, uint32_t row, uint32_t chars, uint32_t* type,
                    uint32_t* id);

/// The logbook opened on another player (`sub_823C48D8`, the orb's View Stats): 0 not, 1 opened
/// elsewhere, 2 opened over the world map (its B returns there). gui_screen_stub_logbook.cpp.
int LogbookOrbMode();

}  // namespace refii::vfs::stub

#endif
