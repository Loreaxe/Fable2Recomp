// TIME CONTROL'S DIAL, STATED. The guest keeps the dial as a record `D` and passes it in r3 to both:
//
//   draw   `sub_8260BA60(D)` (disc `sub_82613290`): called by the dial's tick (`sub_822C5A28`
//          0x822c5ab4, disc `sub_822C5A20` 0x822c5aac) only while `D+4` (active) stands. For
//          segment i = 0, 1 at `S = D + 32 + 40*i` it skips a handle `S+0` of -1 (0x8260bab0), else
//          posts the sprite `RenderObject_PostMessage_p(S+0, msg, 1)` (0x8260bb6c): rect `S+4..S+16`
//          (top, left, bottom, right) into msg +0xC..+0x18, texture `S+20` into +0x20, the angle
//          `S+24` plus {90, 0} degrees into +0x1C, colour (1, 1, 1, `D+12`).
//   close  `sub_8260B6A0(D)` (disc `sub_82612ED0`): acts only when `D+6` is set (0x8260b6ac) — by
//          the tick once the fade is out (0x822c5ae8) or by the spell component's reset
//          (`sub_826099B0` 0x826099e4) — and destroys both sprites.
//
// The draw states `@SlowTime.Draw <D> <alpha bits>` then per segment `<handle> <top> <left>
// <bottom> <right> <texture> <angle bits> <velocity bits>` as the function was given them — the
// angle before the degrees term, and `S+28`, the turn rate the dial's own tick (`sub_8260BCB8`)
// advances it by. The close states `@SlowTime.End <D>` when it takes the dial down.
#include <cstdint>
#include <cstdio>
#include <string>

#include "vfs_hooks_private.h"
#include "core/gui_native_calls.h"
#include "hooks/ppc_externs.h"

namespace {

void Add(refii::vfs::GuiNativeCall& call, std::string value) {
    refii::vfs::GuiNativeArgument argument;
    argument.kind = refii::vfs::GuiNativeArgument::Kind::kString;
    argument.text = std::move(value);
    call.arguments.push_back(std::move(argument));
}

std::string Hex(uint32_t value) {
    char hex[9];
    std::snprintf(hex, sizeof hex, "%08X", value);
    return hex;
}

}  // namespace

REX_EXTERN(rex_lhSlowTimeDial_Draw) {
    REX_FUNC_PROLOGUE();
    const uint32_t dial = ctx.r3.u32;
    const auto word = [&](uint32_t at) { return refii::vfs::LoadGuestU32(base, dial + at); };
    refii::vfs::GuiNativeCall call;
    call.name = "@SlowTime.Draw";
    Add(call, Hex(dial));
    Add(call, std::to_string(word(12u)));
    for (uint32_t segment = 32u; segment < 112u; segment += 40u) {
        for (uint32_t at = 0u; at < 32u; at += 4u) {
            Add(call, std::to_string(word(segment + at)));
        }
    }
    refii::vfs::PostGuiNativeCall(std::move(call));
    __imp__rex_lhSlowTimeDial_Draw(ctx, base);
}

REX_EXTERN(rex_lhSlowTimeDial_Close) {
    REX_FUNC_PROLOGUE();
    const uint32_t dial = ctx.r3.u32;
    if (refii::vfs::LoadGuestU8(base, dial + 6u) != 0u) {
        refii::vfs::GuiNativeCall call;
        call.name = "@SlowTime.End";
        Add(call, Hex(dial));
        refii::vfs::PostGuiNativeCall(std::move(call));
    }
    __imp__rex_lhSlowTimeDial_Close(ctx, base);
}
