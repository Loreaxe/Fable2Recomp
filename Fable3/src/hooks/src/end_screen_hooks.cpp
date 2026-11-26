// refii - Fable 3's epilogue (quest completion) and credits screens, stated as the screen manager
// opens and closes them, for Godot's Fable3EndScreens. Neither is a box-manager box: they are
// screen-manager screens (SM = *(GUI+0x1C), GUI = *0x83596F84), keyed in SM+64 by the .bgf id
// (6 questcompletion, 8 creditsscreen; built in sub_825F8F60 at disc 0x825CE2A0 / disc 0x825CE330), so no
// @Box.Open names them. Their texts and positions are element writes the element hooks already
// state; what only these calls state is that the screen is up, which presentation is it, and the
// epilogue's image.
//
// EPILOGUE E = SM[6] (28 bytes; vtable TU3 off_820A0BE0, stored by the SM ctor sub_825F8F60 at
// 0x825F9318, disc off_8209D2FC, stored by sub_825CDEE0 at 0x825CE294 — 0x820A0BE0 is string data
// in the disc image). Its pad is slot 5, Input(E, action, player) (TU3 sub_827A2480 / disc
// 0x8276B410), called for the open screen object by sub_826019F0 (`lwz r10,0x14(r11)` / `bctrl`,
// TU3 0x82601C58..0x82601C60 / disc 0x825D6C70..0x825D6C78), not by the modal input sub_825F9408.
// Show sub_827A24D0(E, title, description,
// renown, image, flag) — QuestTracker.ShowEpilogueScreen through sub_82511020 (0x825111C4) and the
// co-op net handler sub_82377E80 (disc 0x82373E54) — returns at once unless E+0x14 is clear
// (`lbz r11,0x14(r3)` 0x827A24DC), sets it (0x827A2508), loads the screen (sub_825F26E8(*(GUI+0x38),
// 6) at 0x827A2518) and attaches its presentation *(*(GUI+0x38)+0x20) (disc 0x8276B4B4) to the HUD
// holder (sub_8269F088 at 0x827A253C). The image is r7, a CCharString handle (`lwz r11,0(r26)` /
// `lwz r4,0(r11)` at 0x827A27B4..0x827A27C0); it reaches the `ScreenShot` texture by a raw store
// (`stw r9,0x200(r11)` 0x827A2868), never SetTexture, so this is the only place it is stated.
// Hide sub_827A2A90(E) acts only when E+0x14 is set (0x827A2A9C..0x827A2AA8) and clears it
// (0x827A2AB4).
//
// CREDITS. Start sub_825FA1B0(SM) — ShowCreditsScreen (0x8254CF88) and the net handler
// (disc 0x82374190) — runs the roll's init sub_82B74BA8 only while SM+0x23 is clear (disc 0x825CF1AC) and
// sets it (disc 0x825CF264); the roll writes `Text.Text0..23` under the presentation
// *(*(GUI+0x38)+0x9C) (`lwz r6,0x38(r11)` / `lwz r28,0x9C(r6)` 0x82B74D34..0x82B74D40). End
// sub_825FA2F8(SM) — the roll running out (0x82B75170), GUI_SELECT / GUI_CANCEL (0x82B751F8,
// 0x82B75244) and the net handler (disc 0x823741A4) — closes the screen and, with SM+0x23 set
// (0x825FA36C), pops the credits component.

#include <cstdint>

#include <rex/ppc.h>
#include <rex/system/kernel_state.h>
#include <rex/system/xmemory.h>

#include "gui_statements.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_Epilogue_Show);
REX_EXTERN(__imp__rex_Epilogue_Hide);
REX_EXTERN(__imp__rex_ScreenManager_StartCredits);
REX_EXTERN(__imp__rex_ScreenManager_EndCredits);

namespace {

using namespace refii::fable3;

const refii::guest::Addr kGuiManager{0x83596F84u};
/// GUI+0x38: the screen loader. sub_825F26E8(loader, id) keeps a screen in slot `4 * (id + 2)`
/// (0x825F277C), or — for the ids sub_825F2CD8 picks — in the one exclusive slot +0x9C with the id
/// at +0x98 (0x825F271C..0x825F2724). The credits (id 8) load just before the start
/// (ShowCreditsScreen 0x8254CF68), so at the start +0x9C is theirs.
constexpr uint32_t kGuiScreenLoader = 0x38u;
/// The loader's epilogue slot (id 6, disc 0x8276B4B4) and its exclusive slot (0x82B74D40).
constexpr uint32_t kEpiloguePresentation = 0x20u;
constexpr uint32_t kCreditsPresentation = 0x9Cu;
constexpr uint32_t kEpilogueOpen = 0x14u;
constexpr uint32_t kCreditsRunning = 0x23u;
/// The epilogue's title and description text names, FNV-1 words (0x827A2570 / 0x827A2594).
const refii::guest::Addr kEpilogueTitleName{0x8359B9A4u};
const refii::guest::Addr kEpilogueDescriptionName{0x8359B9A8u};
/// The screenshot's ImageName registry name, an FNV-1 word (0x827A2808).
const refii::guest::Addr kEpilogueImageName{0x8359B9C4u};

uint32_t LoaderWord(const uint8_t* base, uint32_t offset) {
  const uint32_t gui = LoadGuestU32(base, kGuiManager);
  if (!Plausible(gui)) {
    return 0u;
  }
  const uint32_t loader = LoadGuestU32(base, gui + kGuiScreenLoader);
  return Plausible(loader) ? LoadGuestU32(base, loader + offset) : 0u;
}

}  // namespace

/// `@Epilogue.Open(presentation, image, title, description, renown, titleName, descriptionName)`
/// when the show took (E+0x14 clear before, set after). The show keeps its arguments (`mr r28,r4;
/// mr r27,r5; mr r29,r6; mr r26,r7` 0x827A24E0..disc 0x8276B47C) and writes them only behind the
/// screen's presentation (`lwz r30,0x20(r10)` disc 0x8276B4B4 / beq disc 0x8276B4BC): the title into the
/// text registry under `*disc dword_8355AC24` (r5 = r28, 0x827A2568..0x827A2580), the description
/// under `*disc dword_8355AC28` (r5 = r27, 0x827A258C..0x827A25A4); the renown r6 is formatted into
/// AC30 / AC34 (0x827A2608..0x827A265C). The names' hashes are read from those words (their
/// static initialisers store the FNV-1, disc sub_831C7360 / disc sub_831C73A0).
REX_EXTERN(rex_Epilogue_Show) {
  const uint32_t epilogue = ctx.r3.u32;
  const bool closed = base != nullptr && Plausible(epilogue) && base[epilogue + kEpilogueOpen] == 0u;
  const std::string image = closed ? ReadCharString(base, ctx.r7.u32) : std::string();
  const std::string title = closed ? ReadLhString(base, ctx.r4.u32) : std::string();
  const std::string description = closed ? ReadLhString(base, ctx.r5.u32) : std::string();
  const int32_t renown = static_cast<int32_t>(ctx.r6.u32);
  __imp__rex_Epilogue_Show(ctx, base);
  if (closed && base[epilogue + kEpilogueOpen] != 0u) {
    Statement("@Epilogue.Open")
        .Add(Text(Hex(LoaderWord(base, kEpiloguePresentation))))
        .Add(Text(image))
        .Add(Text(title))
        .Add(Text(description))
        .Add(Number(static_cast<float>(renown)))
        .Add(Text(Hex(LoadGuestU32(base, kEpilogueTitleName))))
        .Add(Text(Hex(LoadGuestU32(base, kEpilogueDescriptionName))))
        .Post();
  }
}

/// THE SCREENSHOT. After `bl sub_827236F0` at 0x827A27F8 (r3 = r1+0x58 out, r4 = r1+0x50 the
/// image's path from sub_8253FB80 at 0x827A27C8, r5 = `*dword_8359B9D8` "ScreenShot"), the word at
/// r1+0x58 is the texture hash the show then stores raw into +512 of every element the ImageName
/// registry (R+0x30, `lwz r3,0x30(r7)` 0x827A2814) files under `*disc dword_8355AC44` (`lwz r9,
/// 0xA0+var_48(r1)` 0x827A2834, `stw r9,0x200(r11)` 0x827A2868) — only through the element's node.
/// A [[midasm_hook]] at 0x827A27FC: `@Epilogue.Image(name hash, texture hash, path)`.
void RefiiF3EpilogueScreenshot(PPCRegister& stack) {
  rex::system::KernelState* state = rex::system::kernel_state();
  rex::memory::Memory* memory = state != nullptr ? state->memory() : nullptr;
  const uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
  if (base == nullptr) {
    return;
  }
  Statement("@Epilogue.Image")
      .Add(Text(Hex(LoadGuestU32(base, kEpilogueImageName))))
      .Add(Text(Hex(LoadGuestU32(base, stack.u32 + 0x58u))))
      .Add(Text(ReadCharString(base, stack.u32 + 0x50u, 260u)))
      .Post();
}

/// `@Epilogue.Close` when the hide had an open screen to drop.
REX_EXTERN(rex_Epilogue_Hide) {
  const uint32_t epilogue = ctx.r3.u32;
  const bool open = base != nullptr && Plausible(epilogue) && base[epilogue + kEpilogueOpen] != 0u;
  __imp__rex_Epilogue_Hide(ctx, base);
  if (open) {
    Statement("@Epilogue.Close").Post();
  }
}

/// `@Credits.Open(presentation)` when the start ran the roll's init (SM+0x23 clear before).
REX_EXTERN(rex_ScreenManager_StartCredits) {
  const uint32_t manager = ctx.r3.u32;
  const bool idle = base != nullptr && Plausible(manager) && base[manager + kCreditsRunning] == 0u;
  __imp__rex_ScreenManager_StartCredits(ctx, base);
  if (idle) {
    Statement("@Credits.Open").Add(Text(Hex(LoaderWord(base, kCreditsPresentation)))).Post();
  }
}

/// `@Credits.Close` when the end popped a running roll (SM+0x23 set before).
REX_EXTERN(rex_ScreenManager_EndCredits) {
  const uint32_t manager = ctx.r3.u32;
  const bool running = base != nullptr && Plausible(manager) && base[manager + kCreditsRunning] != 0u;
  __imp__rex_ScreenManager_EndCredits(ctx, base);
  // Its stores to the GUI screen id (0x825FA3D4, 0x825FA4E4), stated as it returns.
  refii::fable3::StateGuiScreen(base);
  if (running) {
    Statement("@Credits.Close").Post();
  }
}
