// THE SHOPKEEPER, STATED. The shop screen has one opener, `sub_823C4060(guiOwner r3, keeper r4)`
// (TU1; disc `sub_824F9290`): it sets screen 22 (disc 21), files r4 into the screen handler's
// keeper handle at +400 (`sub_824D1EC8(v5 + 400, ...)` 0x823c40dc, disc `sub_82978470` 0x824f930c)
// and enters GUI mode. Its callers are `Lua_GUI_DisplayShopScreen`, `CECVillager_OnUse` and
// `sub_826A5590`. The keeper is logged; the shop's pages are stated by their own fills
// (page_fills_stated.cpp).
#include <cstdint>

#include "vfs_hooks_private.h"
#include "hooks/ppc_externs.h"

REX_EXTERN(rex_lhGuiShopScreen_Open) {
    REX_FUNC_PROLOGUE();
    // The host's replay of an online joiner's use of a shopkeeper (coop_shop_for_joiner.cpp): the shop runs here
    // for hero two and shows on the joiner; no screen opens on this machine.
    if (refii::vfs::JoinerShopOpensOnHost(ctx.r4.u32)) {
        REXLOG_INFO("Refii shop: keeper {:08X} serves the online joiner's hero; its shop runs here unseen", ctx.r4.u32);
        return;
    }
    REXLOG_INFO("Refii shop: opened on keeper {:08X}", ctx.r4.u32);
    __imp__rex_lhGuiShopScreen_Open(ctx, base);
}
