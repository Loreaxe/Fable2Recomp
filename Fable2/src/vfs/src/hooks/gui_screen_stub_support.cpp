// The ported screens' shared helpers (hooks/gui_screen_stub_support.h).
#include <algorithm>
#include <cctype>
#include <cstdint>
#include <cstring>
#include <initializer_list>
#include <string>

#include <rex/logging.h>

#include "core/guest_build.h"
#include "core/refii_mod.h"
#include "hooks/gui_screen_stub_support.h"
#include "hooks/gui_screen_stubs.h"
#include "hooks/ppc_externs.h"
#include "vfs_hooks_private.h"

PPC_EXTERN_FUNC(rex_lhGuiManager_FindScreenHandler);
PPC_EXTERN_FUNC(rex_lhGuiMenu_GetHighlightedItemData);

namespace refii::vfs::stub {

using refii::guest::Address;
using refii::mods::LoadU32;
using refii::mods::StoreU32;

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

uint8_t Load8(uint8_t* base, uint32_t at) { return base[at]; }

/// `G = *(0x83496B08)` (disc 0x834968A4).
uint32_t Gui(uint8_t* base) { return LoadU32(base, Address(0x83496B08u)); }

/// The game object, `*(*(0x83496AB8)+12)`.
uint32_t Game(uint8_t* base) {
  const uint32_t root = LoadU32(base, Address(0x83496AB8u));
  return Plausible(root) ? LoadU32(base, root + 12u) : 0u;
}

/// The tutorial manager `*(GAME+168)` and its state `+4`.
uint32_t Tutorials(uint8_t* base) {
  const uint32_t game = Game(base);
  return Plausible(game) ? LoadU32(base, game + 168u) : 0u;
}

/// The book screens' presentation, `*(*(G+56)+156)` (disc +152): the shared slot the screen
/// manager's `lhGuiPresentationSet_GetCurrent` (TU1 0x823DCBE0 / disc 0x823FE8F8) answers for every
/// screen id outside `sub_823DCDA0`'s own-presentation list {0, 3..13, 19}, and what the book
/// handlers read DIRECTLY, never through GetCurrent: logbook 0x826D8E50 / 0x826D90D8 / 0x826D914C /
/// 0x826D9958, options 0x826D9DB4 / 0x826D9E4C / 0x826D9E94, abilities 0x826C225C / 0x826C2290 /
/// 0x826C29A0 (disc 0x82581AE0 / 0x82581D68 / 0x82581DDC, 0x825829DC / 0x82582A74 / 0x82582ABC,
/// 0x8259E20C / 0x8259E240 / 0x8259E950). The pause root (screen 0, its own slot) takes GetCurrent
/// (GuestPresentation below).
uint32_t BookPresentation(uint8_t* base) {
  const uint32_t g = Gui(base);
  const uint32_t set = Plausible(g) ? LoadU32(base, g + 56u) : 0u;
  return Plausible(set) ? LoadU32(base, set + refii::guest::ScreenManagerField(156u)) : 0u;
}

/// One guest call on a scratch frame below `ctx`; r3 back.
uint32_t Call(PPCContext& ctx, uint8_t* base, uint32_t frame, uint32_t tu1, std::initializer_list<uint32_t> args) {
  PPCContext work = ctx;
  work.r1.u32 = frame;
  uint32_t* regs[] = {&work.r3.u32, &work.r4.u32, &work.r5.u32, &work.r6.u32,
                      &work.r7.u32, &work.r8.u32, &work.r9.u32, &work.r10.u32};
  size_t i = 0;
  for (uint32_t value : args) {
    *regs[i++] = value;
  }
  if (!refii::mods::CallGuest(work, base, Address(tu1))) {
    REXLOG_WARN("Refii screen stub: 0x{:08X} (TU1 0x{:08X}) is not a known guest function — not called",
                Address(tu1), tu1);
    return 0u;
  }
  return work.r3.u32;
}

/// A virtual call, slot `slot` of `object`, on a scratch frame.
uint32_t CallSlot(PPCContext& ctx, uint8_t* base, uint32_t frame, uint32_t object, uint32_t slot,
                  std::initializer_list<uint32_t> args) {
  PPCContext work = ctx;
  work.r1.u32 = frame;
  uint32_t* regs[] = {&work.r4.u32, &work.r5.u32, &work.r6.u32, &work.r7.u32};
  size_t i = 0;
  for (uint32_t value : args) {
    *regs[i++] = value;
  }
  refii::mods::CallVirtual(work, base, object, slot);
  return work.r3.u32;
}

/// A guest CCharString at `handle` built from `text`, copied into the frame at `chars` first.
void MakeString(PPCContext& ctx, uint8_t* base, uint32_t frame, uint32_t handle, uint32_t chars, const char* text) {
  const size_t n = std::strlen(text);
  std::memcpy(base + chars, text, n + 1u);
  StoreU32(base, handle, 0u);
  Call(ctx, base, frame, 0x8222CED0u, {handle, chars, 0xFFFFFFFFu});  // lhCharString_CtorFromCStr
}

void DropString(PPCContext& ctx, uint8_t* base, uint32_t frame, uint32_t handle) {
  Call(ctx, base, frame, 0x82214DD8u, {handle});  // lhCharString_Dtor
}

/// Runs `chunk` on `pres`, as `lhGui_RunLuaString` (TU1 0x82426470).
void RunMenuLua(PPCContext& ctx, uint8_t* base, uint32_t frame, uint32_t scratch, const char* chunk,
                uint32_t pres) {
  MakeString(ctx, base, frame, scratch, scratch + 0x10u, chunk);
  Call(ctx, base, frame, 0x82426470u, {scratch, pres});
  DropString(ctx, base, frame, scratch);
}

Scratch Claim(const PPCContext& ctx) {
  const uint32_t top = (ctx.r1.u32 - 0x600u) & ~0xFu;
  return {top, top + 0x200u};
}

void Said(const char* screen, const char* slot, uint32_t handler, uint32_t a, uint32_t b) {
  REXLOG_INFO("Refii screen stub: {} {} ran on 0x{:08X} ({} {})", screen, slot, handler, a, b);
}

/// The element a static name handle names in `pres`: `sub_821F0240` copies the handle into `slot`
/// and `sub_82426798` resolves it and releases the copy.
uint32_t Element(PPCContext& ctx, uint8_t* base, uint32_t frame, uint32_t slot, uint32_t name, uint32_t pres) {
  Call(ctx, base, frame, 0x821F0240u, {slot, Address(name)});
  return Call(ctx, base, frame, 0x82426798u, {slot, pres});
}

/// `sub_82426260(element, pres, on)` — the show / hide statement (opacity ×/÷ 1024).
void Toggle(PPCContext& ctx, uint8_t* base, uint32_t frame, uint32_t element, uint32_t pres, uint32_t on) {
  if (element != 0u) {
    Call(ctx, base, frame, 0x82426260u, {element, pres, on});
  }
}

/// The presentation the guest's own code takes, `lhGuiPresentationSet_GetCurrent(*(G+56))` (TU1
/// 0x823DCBE0 / disc 0x823FE8F8): set+156 while `sub_823DCEF0` says a modal stands, else the current
/// screen's slot `set[set[1] + 3]`.
uint32_t GuestPresentation(PPCContext& ctx, uint8_t* base, uint32_t frame) {
  const uint32_t g = Gui(base);
  const uint32_t set = Plausible(g) ? LoadU32(base, g + 56u) : 0u;
  return Plausible(set) ? Call(ctx, base, frame, 0x823DCBE0u, {set}) : 0u;
}

/// The current presentation's root element, as the root's Update and OnExit take it: pres vf+32, then
/// vf+68 (0x826A2620 / 0x826A2630); 0 without a presentation.
uint32_t PresentationRoot(PPCContext& ctx, uint8_t* base, uint32_t frame, uint32_t pres) {
  if (!Plausible(pres)) {
    return 0u;
  }
  const uint32_t scene = CallSlot(ctx, base, frame, pres, 8u, {});
  return Plausible(scene) ? CallSlot(ctx, base, frame, scene, 17u, {}) : 0u;
}

uint32_t ScreenHandler(PPCContext& ctx, uint8_t* base, uint32_t frame, uint32_t screen, uint32_t vtable) {
  const uint32_t g = Gui(base);
  const uint32_t qm = Plausible(g) ? LoadU32(base, g + 28u) : 0u;
  if (!Plausible(qm)) {
    return 0u;
  }
  PPCContext find = ctx;
  find.r1.u32 = frame;
  find.r3.u32 = qm;
  find.r4.u32 = refii::guest::ScreenId(screen);
  rex_lhGuiManager_FindScreenHandler(find, base);
  const uint32_t h = find.r3.u32;
  return Plausible(h) && LoadU32(base, h) == Address(vtable) ? h : 0u;
}

void HighlightedRow(PPCContext& ctx, uint8_t* base, uint32_t frame, uint32_t row, uint32_t chars, uint32_t* type,
                    uint32_t* id) {
  StoreU32(base, row, 0u);
  StoreU32(base, row + 4u, 0u);
  MakeString(ctx, base, frame, row + 8u, chars, "");
  MakeString(ctx, base, frame, row + 12u, chars, "");
  PPCContext ask = ctx;
  ask.r1.u32 = frame;
  ask.r3.u32 = LoadU32(base, LoadU32(base, Gui(base) + 28u) + 60u);
  ask.r4.u32 = row;
  rex_lhGuiMenu_GetHighlightedItemData(ask, base);
  *type = LoadU32(base, row);
  *id = LoadU32(base, row + 4u);
  DropString(ctx, base, frame, row + 12u);
  DropString(ctx, base, frame, row + 8u);
}

}  // namespace refii::vfs::stub
