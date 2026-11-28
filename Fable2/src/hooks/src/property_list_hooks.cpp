// refii — EACH HERO'S OWNED PROPERTIES, STATED BY THE CALLS THAT CHANGE THEM (core/property_list_state.cpp).
//
// The list is CECPlayerProperties' (hero component 69): `props+24` the list object, `props+28` its sentinel,
// each node's record at +8 ("PlayerBuildingData", 0x44 bytes; its fields as the guest's own serializer
// `sub_828CE118` names them, core/payload_bridge_fable2.h). Every function below was read on both builds and
// pairs instruction for instruction (TU1 / disc, same size); none holds another hook row. No list is walked:
// each statement is the call's own arguments, or the record the call was handed, as the call left it.
//
// WHAT ADDS A RECORD — exactly three inserts (`sub_8261DAE8(props+24, …)`):
//   a purchase `CECPlayerProperties_Buy` 0x828D0080 / 0x828AF6D0 (insert 0x828D0108) and the script's grant
//     `PurchasePropertyQuietly` 0x828D0568 / 0x828AFBB8 (insert 0x828D05F0), both building the record with
//     `sub_828CD2F0(record r3, building r4)` 0x828CD2F0 / 0x828AC940 (callers 0x828D00BC, 0x828D05A4 only) and
//     setting its BoughtFor after it (`stw …,48(record)` 0x828D03BC / 0x828D074C). The purchase wrapper
//     (property_hooks.cpp Purchase) opens a scope; the record the constructor returns in it is stated, all its
//     fields, when the purchase returns. Already owned (`bl sub_828D14E8 ; … bne` 0x828D0094..0x828D00A0), no
//     record is built and nothing is stated.
//   the loader, `CECPlayerProperties_vf9(props r3, archive r4, kind r5, saving r6)` 0x828D1A00 / 0x828B1050
//     (the component's slot 9, vtable 0x820031A8 / 0x820031C8 +0x24 — no other vtable holds it): kind 1 only
//     (`cmpwi cr6,r5,1 ; bne` 0x828D1A1C), r6 == 0 the load (`clrlwi r10,r22,24 ; … beq loc_828D1E60`
//     0x828D1AFC..0x828D1B08), which empties the list unconditionally (`bl sub_827A0E18` 0x828D1E64 / 0x828B14B4)
//     and reads each record with `sub_828CE118(record r3, archive r4, r5 = 0)` (0x828D1FB0 / 0x828B1600) before
//     its insert (0x828D1FC8). So: the load clears the owner's statement, and each record the per-record call
//     read is stated. `sub_828CE118` has two call sites, both in vf9 (0x828D1B94 the save, 0x828D1FB0 the load);
//     outside a load it states nothing. Every list that reaches a hero's component goes through vf9: the world
//     save's load, the household's restore and the joiner's mirror (vfs/src/hooks/household_persist.cpp, which
//     run the same slot 9 on a reader).
// WHAT REMOVES ONE — `sub_828CFE08(props r3, building r4, price r5)` 0x828CFE08 / 0x828AF458, the record whose
//   BuildingID equals the building's UID (`lwz r9,0x14(r31)` / `lwz r9,0x18(r31)` 0x828CFE94 / 0x828CFEA4) is
//   unlinked; one caller, the sale (0x828D121C). The component's destructor, slot 0 `sub_828CE870` 0x828CE870 /
//   0x828ADEC0 (`sub_827A0E18(props+24)` 0x828CE898), drops the owner's statement.
// WHAT CHANGES ONE (each takes the building's ENTITY, its UID at +20 / +24, `ld r10,0x14(r4)`):
//   marital home `sub_828D2230(props, building, on r5)` 0x828D2230 / 0x828B1880 (`stb r5,0xC(record)`
//     0x828D22DC); cleared by `sub_828D2300(props, uid r4)` 0x828D2300 / 0x828B1950 — r4 the 64-bit UID itself
//     (`std r4,arg_18(r1)`, `lwz r3,arg_18+4` / `lwz r5,arg_18` compared with record+4 / +0, 0x828D2308..
//     0x828D2384; callers sub_827A7300, sub_827AD148, CECPlayerFamily_FinishDivorcing 0x827B32A8 `ld r4,0x24(r31)`);
//     set by the marriage's `sub_828D23D0(props, spouse r4, building r5)` 0x828D23D0 / 0x828B1A20, inline: a
//     building with `+0x90 & 0x40` (0x828D23F4..0x828D2414), the record of its UID (`ld r11,0x14(r5)`
//     0x828D2588, compared 0x828D25D4 / 0x828D25E0), `stb r9(=1),0xC(r10)` 0x828D262C;
//   rented out `CECPlayerProperties_SetRentedOut` (r5 on; mod_events.cpp states it after the guest's call);
//   tenants and workers `sub_828D3BD8(props, building, tenants r5, workers r6)` 0x828D3BD8 / 0x828B3228
//     (`stb r5,0x12` / `stb r6,0x13` 0x828D3CF8 / 0x828D3CFC);
//   the guest re-asking ONE RECORD it holds (r3 the record; matched to the record that property was stated
//     with): has tenants `sub_828CD6E8` 0x828CD6E8 / 0x828ACD38 (caches +18, returns `lbz r3,0x12`), has workers
//     `sub_828CD8B8` 0x828CD8B8 / 0x828ACF08 (`stb r11,0x13(r31)` 0x828CDA74, returns +19), its value
//     `sub_828CE5C8` 0x828CE5C8 / 0x828ADC18 (r3 back; `stw r3,0x2C(r31)` 0x828CE748 when loaded; called for
//     every record by the value walk `sub_828D4218` after a purchase, a sale, a furniture upgrade and a load),
//     its income per drop `CECPlayerProperties_RecordIncome` (property_hooks.cpp states the guest's answer).

#include <cstdint>
#include <cstring>

#include <rex/ppc/context.h>
#include <rex/ppc/func.h>

#include "core/guest_state.h"
#include "core/payload_bridge_fable2.h"

extern "C" REX_FUNC(__imp__rex_PlayerBuildingData_FromBuilding);
extern "C" REX_FUNC(__imp__rex_CECPlayerProperties_Serialize);
extern "C" REX_FUNC(__imp__rex_PlayerBuildingData_Serialize);
extern "C" REX_FUNC(__imp__rex_CECPlayerProperties_RemoveRecord);
extern "C" REX_FUNC(__imp__rex_CECPlayerProperties_Destroy);
extern "C" REX_FUNC(__imp__rex_CECPlayerProperties_SetMaritalHome);
extern "C" REX_FUNC(__imp__rex_CECPlayerProperties_ClearMaritalHome);
extern "C" REX_FUNC(__imp__rex_CECPlayerProperties_MarryHome);
extern "C" REX_FUNC(__imp__rex_CECPlayerProperties_SetOccupied);
extern "C" REX_FUNC(__imp__rex_PlayerBuildingData_HasTenants);
extern "C" REX_FUNC(__imp__rex_PlayerBuildingData_HasWorkers);
extern "C" REX_FUNC(__imp__rex_PlayerBuildingData_Value);

namespace {

bool Heap(uint32_t at) { return at >= 0x10000u && at < 0xE0000000u; }

uint32_t Load32(uint8_t* base, uint32_t at) { return refii::vfs::LoadGuestU32(base, at); }

uint8_t Load8(uint8_t* base, uint32_t at) { return refii::vfs::LoadGuestU8(base, at); }

uint64_t Uid(uint8_t* base, uint32_t at) {
  return (static_cast<uint64_t>(Load32(base, at)) << 32) | Load32(base, at + 4u);
}

/// A building entity's UID (+20 / +24), 0 for none.
uint64_t BuildingUid(uint8_t* base, uint32_t building) { return Heap(building) ? Uid(base, building + 20u) : 0u; }

/// The record the guest built or read, all its fields.
RefiiStatedProperty Read(uint8_t* base, uint32_t record) {
  RefiiStatedProperty said{};
  said.uid = Uid(base, record);
  const uint32_t rep = Load32(base, record + 8u);
  const uint32_t chars = Heap(rep) ? Load32(base, rep) : 0u;
  for (uint32_t i = 0; Heap(chars) && i + 1u < sizeof(said.name); ++i) {
    const char c = static_cast<char>(Load8(base, chars + i));
    if (c == '\0') {
      break;
    }
    said.name[i] = c == '\t' || c == '\n' ? ' ' : c;
  }
  said.flags |= Load8(base, record + 12u) != 0u ? REFII_PROPERTY_MARITAL : 0u;
  said.flags |= Load8(base, record + 13u) != 0u ? REFII_PROPERTY_RENTED : 0u;
  said.flags |= Load8(base, record + 14u) != 0u ? REFII_PROPERTY_DUNGEON : 0u;
  said.flags |= Load8(base, record + 15u) != 0u ? REFII_PROPERTY_HOME : 0u;
  said.flags |= Load8(base, record + 18u) != 0u ? REFII_PROPERTY_TENANTS : 0u;
  said.flags |= Load8(base, record + 19u) != 0u ? REFII_PROPERTY_WORKERS : 0u;
  said.income = static_cast<int32_t>(Load32(base, record + 28u));
  said.value = static_cast<int32_t>(Load32(base, record + 44u));
  said.bought = static_cast<int32_t>(Load32(base, record + 48u));
  return said;
}

void State(uint8_t* base, uint32_t props, uint32_t record) {
  if (Heap(props) && Heap(record)) {
    const RefiiStatedProperty said = Read(base, record);
    refii_bridge_property_list_put(props, record, &said);
  }
}

/// The purchase in progress on this thread and the record its constructor returned.
thread_local int t_purchasing = 0;
thread_local uint32_t t_built = 0u;

/// The owner whose list the loader is reading on this thread.
thread_local uint32_t t_loading = 0u;

}  // namespace

// A purchase's scope (property_hooks.cpp Purchase, around both Buy and PurchasePropertyQuietly).
extern "C" void RefiiPropertyListPurchaseBegin(void) {
  if (t_purchasing++ == 0) {
    t_built = 0u;
  }
}

// The purchase returned: the record it built, as it left it (BoughtFor included).
extern "C" void RefiiPropertyListPurchaseEnd(uint8_t* base, uint32_t props) {
  if (t_purchasing <= 0 || --t_purchasing != 0) {
    return;
  }
  if (base != nullptr && t_built != 0u) {
    State(base, props, t_built);
  }
  t_built = 0u;
}

// `sub_828CD2F0(record r3, building r4)` → r3 the record. TU1 0x828CD2F0.
REX_EXTERN(rex_PlayerBuildingData_FromBuilding) {
  __imp__rex_PlayerBuildingData_FromBuilding(ctx, base);
  if (t_purchasing > 0 && Heap(ctx.r3.u32)) {
    t_built = ctx.r3.u32;
  }
}

// `CECPlayerProperties_vf9(props r3, archive r4, kind r5, saving r6)`. TU1 0x828D1A00.
REX_EXTERN(rex_CECPlayerProperties_Serialize) {
  const uint32_t props = ctx.r3.u32;
  const bool load = ctx.r5.u32 == 1u && (ctx.r6.u32 & 0xFFu) == 0u && Heap(props);
  if (!load) {
    __imp__rex_CECPlayerProperties_Serialize(ctx, base);
    return;
  }
  refii_bridge_property_list_clear(props);
  const uint32_t outer = t_loading;
  t_loading = props;
  __imp__rex_CECPlayerProperties_Serialize(ctx, base);
  t_loading = outer;
}

// `sub_828CE118(record r3, archive r4, saving r5)`. TU1 0x828CE118.
REX_EXTERN(rex_PlayerBuildingData_Serialize) {
  const uint32_t record = ctx.r3.u32;
  const bool load = t_loading != 0u && (ctx.r5.u32 & 0xFFu) == 0u;
  __imp__rex_PlayerBuildingData_Serialize(ctx, base);
  if (load) {
    State(base, t_loading, record);
  }
}

// `sub_828CFE08(props r3, building r4, price r5)`. TU1 0x828CFE08.
REX_EXTERN(rex_CECPlayerProperties_RemoveRecord) {
  const uint32_t props = ctx.r3.u32;
  const uint64_t uid = BuildingUid(base, ctx.r4.u32);
  __imp__rex_CECPlayerProperties_RemoveRecord(ctx, base);
  if (uid != 0u) {
    refii_bridge_property_list_remove(props, uid);
  }
}

// Slot 0, `sub_828CE870(props r3, flags r4)`. TU1 0x828CE870.
REX_EXTERN(rex_CECPlayerProperties_Destroy) {
  refii_bridge_property_list_clear(ctx.r3.u32);
  __imp__rex_CECPlayerProperties_Destroy(ctx, base);
}

// `sub_828D2230(props r3, building r4, on r5)`. TU1 0x828D2230.
REX_EXTERN(rex_CECPlayerProperties_SetMaritalHome) {
  const uint32_t props = ctx.r3.u32;
  const uint64_t uid = BuildingUid(base, ctx.r4.u32);
  const int32_t on = (ctx.r5.u32 & 0xFFu) != 0u ? 1 : 0;
  __imp__rex_CECPlayerProperties_SetMaritalHome(ctx, base);
  refii_bridge_property_list_flag(props, uid, REFII_PROPERTY_MARITAL, on);
}

// `sub_828D2300(props r3, uid r4 — the 64-bit UID)`. TU1 0x828D2300.
REX_EXTERN(rex_CECPlayerProperties_ClearMaritalHome) {
  const uint32_t props = ctx.r3.u32;
  const uint64_t uid = ctx.r4.u64;
  __imp__rex_CECPlayerProperties_ClearMaritalHome(ctx, base);
  refii_bridge_property_list_flag(props, uid, REFII_PROPERTY_MARITAL, 0);
}

// `sub_828D23D0(props r3, spouse r4, building r5)`: marks the building's record the marital home. TU1 0x828D23D0.
REX_EXTERN(rex_CECPlayerProperties_MarryHome) {
  const uint32_t props = ctx.r3.u32;
  const uint32_t building = ctx.r5.u32;
  const uint64_t uid = Heap(building) && (Load8(base, building + 0x90u) & 0x40u) != 0u ? BuildingUid(base, building) : 0u;
  __imp__rex_CECPlayerProperties_MarryHome(ctx, base);
  if (uid != 0u) {
    refii_bridge_property_list_flag(props, uid, REFII_PROPERTY_MARITAL, 1);
  }
}

// `sub_828D3BD8(props r3, building r4, tenants r5, workers r6)`. TU1 0x828D3BD8.
REX_EXTERN(rex_CECPlayerProperties_SetOccupied) {
  const uint32_t props = ctx.r3.u32;
  const uint64_t uid = BuildingUid(base, ctx.r4.u32);
  const int32_t tenants = (ctx.r5.u32 & 0xFFu) != 0u ? 1 : 0;
  const int32_t workers = (ctx.r6.u32 & 0xFFu) != 0u ? 1 : 0;
  __imp__rex_CECPlayerProperties_SetOccupied(ctx, base);
  refii_bridge_property_list_flag(props, uid, REFII_PROPERTY_TENANTS, tenants);
  refii_bridge_property_list_flag(props, uid, REFII_PROPERTY_WORKERS, workers);
}

// `sub_828CD6E8(record r3)` → has tenants, cached at +18. TU1 0x828CD6E8.
REX_EXTERN(rex_PlayerBuildingData_HasTenants) {
  const uint32_t record = ctx.r3.u32;
  __imp__rex_PlayerBuildingData_HasTenants(ctx, base);
  if (Heap(record)) {
    refii_bridge_property_list_record_field(record, REFII_PROPERTY_FIELD_TENANTS, Load8(base, record + 18u));
  }
}

// `sub_828CD8B8(record r3)` → has workers, cached at +19. TU1 0x828CD8B8.
REX_EXTERN(rex_PlayerBuildingData_HasWorkers) {
  const uint32_t record = ctx.r3.u32;
  __imp__rex_PlayerBuildingData_HasWorkers(ctx, base);
  if (Heap(record)) {
    refii_bridge_property_list_record_field(record, REFII_PROPERTY_FIELD_WORKERS, Load8(base, record + 19u));
  }
}

// `sub_828CE5C8(record r3)` → its value. TU1 0x828CE5C8.
REX_EXTERN(rex_PlayerBuildingData_Value) {
  const uint32_t record = ctx.r3.u32;
  __imp__rex_PlayerBuildingData_Value(ctx, base);
  refii_bridge_property_list_record_field(record, REFII_PROPERTY_FIELD_VALUE, static_cast<int32_t>(ctx.r3.u32));
}
