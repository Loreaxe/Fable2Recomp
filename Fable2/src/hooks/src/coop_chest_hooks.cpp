// SILVER-KEY CHESTS PER PLAYER — the dog's half (the opening itself is the core Lua's wrap of
// OnActionUseTakeItems.Update, CoopHeroLua: a chest one player has opened is closed again and
// refilled for the partner, and who has opened which chest is the saved player value
// `refii.chest.<UniqueID>`).
//
// THE DOG'S TREASURE TEST is `sub_822D1538(dog component, thing)` (disc `sub_822D1CB8`), asked by
// the dog's treasure search `sub_822AF5F8` (0x822AFA90, 0x822AFC2C; disc `sub_822AF430`) inside
// the dog's update. For a chest (the thing's component 40) it answers no when the chest is open or
// opening (0x822D1658 `lbz r10, 0xC(r11)`, 0x822D1664 `lbz r10, 0xD(r11)`), and when the dog's
// MASTER (`sub_82174410(dog)`, the weak reference at dog+300, 0x822D16A4) holds fewer silver keys
// than the chest asks for (0x822D17D4 Inventory.GetNumberOfItemsOfType "ObjectInventorySilverKey",
// 0x822D17E4 `cmpw r31, r30` / `blt`). The guest knows one "open" per chest: a chest closed again
// for the partner reads as fresh treasure to the dog of the player who has already opened it, and
// that dog would lead its master to a chest the master cannot open twice. The test states whose
// question it is — the dog's master — so a chest that master's player has opened is no treasure.

#include <cstdint>
#include <cstdio>

#include <rex/logging.h>
#include <rex/ppc/context.h>

#include "core/guest_build.h"
#include "core/guest_state.h"
#include "core/refii_mod.h"

extern "C" REX_FUNC(__imp__rex_CECDog_IsTreasureForMaster);

namespace {

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

}  // namespace

// The dog's treasure test: r3 the dog component (master weak reference at +300, its holder +304),
// r4 the thing; r3 back nonzero when the dog may lead its master to it. TU1 0x822D1538.
REX_EXTERN(rex_CECDog_IsTreasureForMaster) {
  const uint32_t dog = ctx.r3.u32;
  const uint32_t thing = ctx.r4.u32;
  __imp__rex_CECDog_IsTreasureForMaster(ctx, base);
  if ((ctx.r3.u32 & 0xFFu) == 0u || !Plausible(dog) || !Plausible(thing) ||
      refii_mod_component(base, thing, 40u) == 0u) {
    return;
  }
  const uint32_t holder = refii::vfs::LoadGuestU32(base, dog + 304u);
  const uint32_t master = Plausible(holder) ? refii::vfs::LoadGuestU32(base, holder) : 0u;
  const int32_t player = Plausible(master) ? refii_mod_player_of(base, master) : 0;
  if (player != 1 && player != 2) {
    return;
  }
  // The key the core Lua writes: "refii.chest." .. Refii.uid(chest) (mod_lua_natives.cpp NativeUid).
  char key[48];
  std::snprintf(key, sizeof(key), "refii.chest.0x%08x%08x", refii::vfs::LoadGuestU32(base, thing + 0x14u),
                refii::vfs::LoadGuestU32(base, thing + 0x18u));
  if (refii_mod_player_value(player, key, 0.0f) != 0.0f) {
    ctx.r3.u64 = 0u;
    static uint32_t said = 0u;  // guest tick thread only
    if (said != thing) {
      said = thing;
      REXLOG_INFO("coop chests: player {}'s dog passes the chest {} (its master has opened it)", player, key + 12);
    }
  }
}
