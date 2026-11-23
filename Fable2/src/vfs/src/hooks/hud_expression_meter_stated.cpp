// THE EXPRESSION AND JOB TIMING METER, STATED. The guest runs the meter object `M` and judges it;
// each of these is one of M's own methods, called with M in r3, and the fields the HUD draws are
// stated as that method left them (`@Hud.ExpressionMeter`), on change:
//
//   tick   `sub_822ACA60(M, dt)` (disc `sub_822AC848`): the HUD update's one call. Moves the pip
//          (`sub_822ACBF8(M + 12, dt)`), sets the colour band `+40`, runs a pending off.
//   arm    `sub_823D2120(M, isA, position, speed)` (disc `sub_824F1E60`): arms pip A or B.
//   judge  `sub_823D1D40(M, isA, hit)` (disc `sub_824F1A80`): disarms it with the result.
//   start  `sub_823D1A48(M, expression, …)` (disc `sub_824F1788`): an expression begins.
//   off    `sub_823D1458(M)` (disc `sub_824F1198`).
//
// Fields: +0 on, +4 mode (1 and 2 a pip, 3 a job's bar fill), +12 / +13 pip A / B armed,
// +14 latched, +16 position 0..100, +20 speed per second, +36 / +37 hit A / B, +38 live,
// +40 band.
#include <array>
#include <cstdint>
#include <mutex>
#include <string>

#include "vfs_hooks_private.h"
#include "core/gui_native_calls.h"
#include "hooks/ppc_externs.h"

namespace {

std::mutex g_lock;
std::array<uint32_t, 11> g_said{};
bool g_said_any = false;

void StateMeter(uint8_t* base, uint32_t meter) {
    if (meter < 0x10000u) return;
    const auto word = [&](uint32_t at) { return refii::vfs::LoadGuestU32(base, meter + at); };
    const std::array<uint32_t, 11> now{base[meter],      word(4u),         base[meter + 12u], base[meter + 13u],
                                       base[meter + 14u], word(16u),        word(20u),         base[meter + 36u],
                                       base[meter + 37u], base[meter + 38u], word(40u)};
    {
        std::lock_guard<std::mutex> lock(g_lock);
        if (g_said_any && now == g_said) return;
        g_said = now;
        g_said_any = true;
    }

    refii::vfs::GuiNativeCall call;
    call.name = "@Hud.ExpressionMeter";
    for (size_t i = 0; i < now.size(); ++i) {
        refii::vfs::GuiNativeArgument argument;
        argument.kind = refii::vfs::GuiNativeArgument::Kind::kString;
        // Position and speed (5, 6) travel as their float bits.
        argument.text = std::to_string(now[i]);
        call.arguments.push_back(std::move(argument));
    }
    refii::vfs::PostGuiNativeCall(std::move(call));
}

}  // namespace

REX_EXTERN(rex_lhHudExpressionMeter_Tick) {
    REX_FUNC_PROLOGUE();
    const uint32_t meter = ctx.r3.u32;
    __imp__rex_lhHudExpressionMeter_Tick(ctx, base);
    StateMeter(base, meter);
}

REX_EXTERN(rex_lhHudExpressionMeter_ArmPip) {
    REX_FUNC_PROLOGUE();
    const uint32_t meter = ctx.r3.u32;
    __imp__rex_lhHudExpressionMeter_ArmPip(ctx, base);
    StateMeter(base, meter);
}

REX_EXTERN(rex_lhHudExpressionMeter_Judge) {
    REX_FUNC_PROLOGUE();
    const uint32_t meter = ctx.r3.u32;
    __imp__rex_lhHudExpressionMeter_Judge(ctx, base);
    StateMeter(base, meter);
}

REX_EXTERN(rex_lhHudExpressionMeter_Start) {
    REX_FUNC_PROLOGUE();
    const uint32_t meter = ctx.r3.u32;
    __imp__rex_lhHudExpressionMeter_Start(ctx, base);
    StateMeter(base, meter);
}

REX_EXTERN(rex_lhHudExpressionMeter_TurnOff) {
    REX_FUNC_PROLOGUE();
    const uint32_t meter = ctx.r3.u32;
    __imp__rex_lhHudExpressionMeter_TurnOff(ctx, base);
    StateMeter(base, meter);
}
