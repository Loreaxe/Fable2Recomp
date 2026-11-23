// THE SHOPKEEPER, STATED. The shop screen has one opener, `sub_823C4060(guiOwner r3, keeper r4)`
// (TU1; disc `sub_824F9290`): it sets screen 22 (disc 21), files r4 into the screen handler's
// keeper handle at +400 (`sub_824D1EC8(v5 + 400, ...)` 0x823c40dc, disc `sub_82978470` 0x824f930c)
// and enters GUI mode. Its callers are `Lua_GUI_DisplayShopScreen`, `CECVillager_OnUse` and
// `sub_826A5590`. The keeper it was handed is the entity the pump's shop samplers ask about.
#include <atomic>
#include <cstdint>

#include "vfs_hooks_private.h"
#include "hooks/ppc_externs.h"

namespace {
std::atomic<uint32_t> g_shopkeeper{0u};
}  // namespace

namespace refii::vfs {

uint32_t StatedShopkeeper() { return g_shopkeeper.load(std::memory_order_relaxed); }

}  // namespace refii::vfs

REX_EXTERN(rex_lhGuiShopScreen_Open) {
    REX_FUNC_PROLOGUE();
    const uint32_t keeper = ctx.r4.u32;
    g_shopkeeper.store(keeper, std::memory_order_relaxed);
    REXLOG_INFO("Refii shop: opened on keeper {:08X}", keeper);
    __imp__rex_lhGuiShopScreen_Open(ctx, base);
}
