// THE WEAPON SWITCHES THE MENU HAS PENDING, STATED. The pending object `*(GUI+52)` is written in one
// place and cleared in one:
//   pick   `sub_826CA098(pending r3, itemHandle r4)` (TU1; disc `sub_825A6020`), the only callee of
//          `lhGuiInventory_ActivateItemByCategory` case 1 (0x826c9bc0, disc 0x825a5b48), classifies
//          the item itself — "WeaponComponent" → "WeaponType" → `sub_82561420` (disc `sub_8250B778`)
//          — and at `cmpwi cr6, r3, 1` (0x826ca114, disc 0x825a609c) holds that class in r3 and the
//          item handle in r31: class 1 copies into the melee slot +0 and sets +32, class 2 into the
//          ranged slot +16 and sets +33. The item is the handle's tracked object +4 (0x826ca0bc).
//   clear  `sub_8236CF28(pending r3)` (disc `sub_823350E0`) empties both slots and both flags
//          (0x8236cf90/0x8236cf94, disc 0x82335148/0x8233514c). Its callers are
//          `lhGuiManager_ExitScreen` (after the switch is applied, 0x8236eee8) and both
//          `lhGuiManager_EnterGuiMode*`, each on GUI+52 and on its twin GUI+80, whose slots nothing
//          writes.
// Stated-value keys (payload_bridge.h): 15 the pending melee item, 16 the pending ranged; 0 none.
#include <cstdint>

#include "vfs_hooks_private.h"
#include "core/payload_bridge.h"
#include "hooks/ppc_externs.h"

extern "C" uint8_t* RefiiGuestBase(void);

namespace {
constexpr uint32_t kPendingMelee = 15u;
constexpr uint32_t kPendingRanged = 16u;
}  // namespace

void RefiiPendingWeaponPicked(PPCRegister& weapon_class, PPCRegister& handle) {
    uint8_t* base = RefiiGuestBase();
    const uint32_t kind = weapon_class.u32;
    if (base == nullptr || (kind != 1u && kind != 2u)) {
        return;
    }
    const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
    const uint32_t object = plausible(handle.u32) ? refii::vfs::LoadGuestU32(base, handle.u32) : 0u;
    const uint32_t item = plausible(object) ? refii::vfs::LoadGuestU32(base, object + 4u) : 0u;
    refii_bridge_stated_value(kind == 1u ? kPendingMelee : kPendingRanged, item);
}

REX_EXTERN(rex_lhGuiPendingWeapons_Clear) {
    REX_FUNC_PROLOGUE();
    __imp__rex_lhGuiPendingWeapons_Clear(ctx, base);
    refii_bridge_stated_value(kPendingMelee, 0u);
    refii_bridge_stated_value(kPendingRanged, 0u);
}
