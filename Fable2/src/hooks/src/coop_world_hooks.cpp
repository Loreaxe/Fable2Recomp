// PER-HERO STATE IN THE HOST'S WORLD — the payload half (Fable 3 keeps these per hero).
//
// ONE-TIME COLLECTABLES PER PLAYER (Fable 3's CECOneTimeCollectable, `sub_82948D70`): in co-op a
// silver key one player picks up stays in the world for the other, who takes their own; it goes
// once both have it. Which player has taken a key is a saved player value (core/coop_collectables.cpp),
// so it goes home with each hero's save, and the key is hidden in the view of whoever has it.
//
// Read in the binary (TU1, hand-checked):
//   The pickup (OnActionUsePickUp.lua) is Inventory.AddEntity(user, item, true, true) — the native
//   `sub_824F0DE8(out, inventory, entity, destroy, notify)`: it makes the item from the entity's own
//   record (`*(entity+28)` → `sub_824F0820`, AddOneOfRecord) and, when `destroy`, removes the world
//   entity (`sub_8238D828`). So keeping the entity is the call with `destroy` clear.
//   A silver key is the record `NGDB_CRecord_Get_int(g_GDBManager, "ObjectInventorySilverKey", 0)` —
//   how the game names it in its own key credit (`sub_823FE8B8` from `sub_824F31D8`).
//   An entity's UniqueID is +0x14 (high) / +0x18 (low) (`sub_8238B860` 0x8238b8cc/0x8238b8d4).

#include <cstdint>
#include <cstring>

#include <rex/logging.h>
#include <rex/ppc/context.h>

#include "core/guest_build.h"
#include "core/guest_state.h"
#include "core/payload_bridge_fable2.h"
#include "core/refii_mod.h"
#include "core/refii_mod_events.h"

extern "C" uint8_t* RefiiGuestBase(void);
/// Player two brought a hero (a character from a save) rather than standing as the plain henchman.
extern "C" int RefiiCoopGetOwnHero(void);
extern "C" REX_FUNC(__imp__rex_CECInventory_AddEntity);
extern "C" REX_FUNC(rex_lhEntity_Destroy);
extern "C" void refii_mod_raise_collectable_taken(PPCContext* ctx, uint8_t* base, int32_t player, uint32_t entity,
                                                  uint32_t record, int32_t stays);

// A GDB record's id by its name — `NGDB_CRecord_Get_int(g_GDBManager, name, 0)` (`sub_8217F390`),
// the id space of an entity's +28 and of CreateEntityFromIDAtPosition. 0 when there is none.
// Game Lua cannot compare ids (`record:GetID()` is a userdata), so a choice between records is
// made here and handed to Lua by name.
extern "C" uint32_t RefiiGdbRecordId(PPCContext* ctx, uint8_t* base, const char* name) {
  static PPCFunc* const lookup = refii::vfs::FindGuestFunction(refii::guest::Address(0x8217F390u));
  const uint32_t gdb = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496DA0u));
  const std::size_t length = name != nullptr ? std::strlen(name) : 0u;
  if (ctx == nullptr || lookup == nullptr || gdb < 0x10000u || gdb >= 0xC0000000u || length == 0u || length >= 0x100u) {
    return 0u;
  }
  PPCContext work = *ctx;
  work.r1.u32 = (ctx->r1.u32 - 0x200u) & ~0xFu;
  const uint32_t text = work.r1.u32 + 0x60u;
  std::memcpy(base + text, name, length + 1u);
  work.r3.u32 = gdb;
  work.r4.u32 = text;
  work.r5.u64 = 0u;
  lookup(work, base);
  return work.r3.u32;
}

namespace {

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

/// The silver key's record, asked of the game's own record table once.
uint32_t SilverKeyRecord(PPCContext& ctx, uint8_t* base) {
  static uint32_t record = 0u;
  if (record == 0u) {
    record = RefiiGdbRecordId(&ctx, base, "ObjectInventorySilverKey");
  }
  return record;
}

uint64_t UniqueId(uint8_t* base, uint32_t entity) {
  return (static_cast<uint64_t>(refii::vfs::LoadGuestU32(base, entity + 0x14u)) << 32) |
         refii::vfs::LoadGuestU32(base, entity + 0x18u);
}

/// A partner who collects for themselves: player two as a hero they brought. The plain henchman's
/// pickup is the shipped one (it runs as hero one, and the key goes).
bool PartnerPresent(uint8_t* base) {
  return RefiiCoopGetOwnHero() != 0 && refii_mod_player_hero(base, 1) != 0u && refii_mod_player_hero(base, 2) != 0u;
}

bool IsSilverKey(PPCContext& ctx, uint8_t* base, uint32_t entity) {
  const uint32_t record = Plausible(entity) ? refii::vfs::LoadGuestU32(base, entity + 28u) : 0u;
  return record != 0u && record == SilverKeyRecord(ctx, base);
}

void Destroy(PPCContext& ctx, uint8_t* base, uint32_t entity) {
  PPCContext work = ctx;
  work.r1.u32 = (ctx.r1.u32 - 0x200u) & ~0xFu;
  work.r3.u32 = entity;
  rex_lhEntity_Destroy(work, base);
}

// Keys in the world, so Godot can hide the taken ones per view.
void OnSpawned(refii::mods::EntitySpawnedEvent& e) {
  if (e.ctx != nullptr && IsSilverKey(*e.ctx, e.base, e.entity)) {
    refii_bridge_collectable_track(e.entity, UniqueId(e.base, e.entity));
  }
}
REFII_MOD_ON(EntitySpawned, OnSpawned);

// A villager leaving the world takes its opinion of player two with it (core/coop_opinion.cpp):
// the component's address is the next creature's.
void OnRemoved(refii::mods::EntityRemovedEvent& e) {
  refii_bridge_collectable_forget(e.entity);
  if (const uint32_t opinion = refii_mod_component(e.base, e.entity, 47u)) {
    refii_bridge_opinion_two_forget(opinion);
  }
}
REFII_MOD_ON(EntityRemoved, OnRemoved);

}  // namespace

// Inventory.AddEntity: r3 the out item reference, r4 the inventory (component 13, owner at +4),
// r5 the world entity, r6 destroy, r7 notify. TU1 0x824F0DE8.
REX_EXTERN(rex_CECInventory_AddEntity) {
  const uint32_t inventory = ctx.r4.u32;
  const uint32_t entity = ctx.r5.u32;
  const bool destroy = (ctx.r6.u32 & 0xFFu) != 0u;
  const uint32_t user = Plausible(inventory) ? refii::vfs::LoadGuestU32(base, inventory + 4u) : 0u;
  const int32_t player = Plausible(user) ? refii_mod_player_of(base, user) : 0;
  if (!destroy || player == 0 || !PartnerPresent(base) || !IsSilverKey(ctx, base, entity)) {
    __imp__rex_CECInventory_AddEntity(ctx, base);
    return;
  }

  const uint64_t uid = UniqueId(base, entity);
  const uint32_t partner_bit = 1u << (player == 1 ? 1 : 0);
  const bool partner_has = (refii_bridge_collectable_mask(uid) & partner_bit) != 0u;
  refii_mod_raise_collectable_taken(&ctx, base, player, entity, refii::vfs::LoadGuestU32(base, entity + 28u),
                                    partner_has ? 0 : 1);
  ctx.r6.u64 = 0u;  // the item is made either way; the world key stays for the partner
  __imp__rex_CECInventory_AddEntity(ctx, base);
  refii_bridge_collectable_take(player, uid);
  refii_bridge_collectable_track(entity, uid);
  if (partner_has) {
    Destroy(ctx, base, entity);
    REXLOG_INFO("coop collectables: player {} took the silver key {:016X}; both have it now", player, uid);
  } else {
    REXLOG_INFO("coop collectables: player {} took the silver key {:016X}; it stays for their partner", player, uid);
  }
}

// The Lua pickup's question about the entity it is picking up for player `player`: 1 when that
// player has taken it already, 0 otherwise (any entity no player has taken).
extern "C" int32_t RefiiCoopKeyTaken(uint32_t entity, int32_t player) {
  uint8_t* base = RefiiGuestBase();
  if (base == nullptr || !Plausible(entity) || (player != 1 && player != 2)) {
    return 0;
  }
  return (refii_bridge_collectable_mask(UniqueId(base, entity)) & (1u << (player - 1))) != 0u ? 1 : 0;
}
