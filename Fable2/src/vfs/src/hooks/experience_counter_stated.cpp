// THE HUD EXPERIENCE COUNTER'S VISIBILITY, PER HERO, STATED. The counter (`*(HUD+0x68)`) keeps one
// show flag per hero, +64 hero one and +65 hero two, decided once per HUD update by
// `sub_82539F38(counter)` (TU1; disc `sub_8241A420`): for a hero with authority it calls that hero's
// setter with the expression-type-17 test; for one without it stores 0 inline (0x82539fbc /
// 0x8253a01c, disc 0x8241a4a4 / 0x8241a504).
//   hero one  `sub_825391E8(counter r3, shown r4)` (disc `sub_824196D0`): `stb r4, 0x40(r3)`
//   hero two  `sub_82539280(counter r3, shown r4)` (disc `sub_82419768`): `stb r4, 0x41(r3)`
// The setters are also the network message's receivers (`lhNetManager_ProcessInboundGamePacket`
// case 258). Stated-value key 17 (payload_bridge.h): bit 0 hero one's counter shown, bit 1 hero
// two's. A hero whose setter the decision did not call was cleared inline.
#include <cstdint>

#include "vfs_hooks_private.h"
#include "core/payload_bridge.h"
#include "hooks/ppc_externs.h"

namespace {

constexpr uint32_t kExperienceShown = 17u;

// Guest thread only: the shown bits, and which setters ran inside the current decision.
uint32_t g_shown = 0u;
thread_local int t_deciding = 0;
thread_local uint32_t t_set_inside = 0u;

void State(uint32_t bit, bool shown) {
    g_shown = shown ? (g_shown | bit) : (g_shown & ~bit);
    refii_bridge_stated_value(kExperienceShown, g_shown);
}

}  // namespace

REX_EXTERN(rex_lhHudExperienceCounter_ShowHeroOne) {
    REX_FUNC_PROLOGUE();
    const bool shown = (ctx.r4.u32 & 0xFFu) != 0u;
    __imp__rex_lhHudExperienceCounter_ShowHeroOne(ctx, base);
    if (t_deciding > 0) {
        t_set_inside |= 1u;
    }
    State(1u, shown);
}

REX_EXTERN(rex_lhHudExperienceCounter_ShowHeroTwo) {
    REX_FUNC_PROLOGUE();
    const bool shown = (ctx.r4.u32 & 0xFFu) != 0u;
    __imp__rex_lhHudExperienceCounter_ShowHeroTwo(ctx, base);
    if (t_deciding > 0) {
        t_set_inside |= 2u;
    }
    State(2u, shown);
}

REX_EXTERN(rex_lhHudExperienceCounter_DecideShown) {
    REX_FUNC_PROLOGUE();
    const uint32_t outer = t_set_inside;
    t_set_inside = 0u;
    ++t_deciding;
    __imp__rex_lhHudExperienceCounter_DecideShown(ctx, base);
    --t_deciding;
    const uint32_t cleared = 3u & ~t_set_inside;
    t_set_inside = outer;
    if ((g_shown & cleared) != 0u) {
        State(cleared, false);
    }
}
