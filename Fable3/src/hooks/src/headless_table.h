// Fable 3's Gameface for the shared headless core (core/headless_presentation.h): the addresses
// and offsets the core calls and reads, TU3 addresses (refii::guest::Address converts them for the
// disc, Fable3/versions/Fable3/guest_addresses.inc), each with the instruction that proves it. The
// hooks that hand it to the core, and the title's own services (find, the guest heap), are
// headless_presentation_hooks.cpp; the slots the guest calls on a handle are answered in
// stated_element_hooks.cpp.
//
// Fable 3's elements: every element +4 reader null-checks it (F3 audit gf §5 i), so no inert quad
// record.
#pragma once

#include <cstdint>

#include "core/headless_presentation.h"
#include "../../vfs/include/fable3_guest_build.h"

namespace refii::fable3 {

/// The table without the title's services (find / alloc / free), which the hooks file sets.
inline refii::headless::GamefaceTable HeadlessTableAddresses() {
    refii::headless::GamefaceTable t;
    t.title = "Fable 3";
    // CPresentation: the kernel alloc of the maker K vf +28 `sub_82C558F8` takes 0x36C (`li r4,0x36C`
    // 0x82C55958..0x82C55970), its ctor `sub_82C68D68` stores the vtable 0x82007628 (`stw r10,0(r31)`
    // 0x82C68D90, disc 0x82007700). Slots: +32 the element service `addi r3,r3,0x1EC` (vtable
    // 0x82006E58, disc 0x82006F38; root at service +8 — the folded root getter `lwz r3,8(r3)`
    // 0x82F09930), +36 the slide manager `addic. r11,r3,0x1EC; addi r3,r3,0x238` 0x82193B90 (vtable
    // 0x82002AC0, disc 0x82002B40), +68 nullsub_1 0x828173B8 (`blr`), +12 the scene `lwz r3,0x18(r3)`.
    t.presentation_size = 0x36Cu;
    t.presentation_vtable = refii::guest::Address(0x82007628u);
    t.service_vtable = refii::guest::Address(0x82006E58u);
    t.animation_vtable = refii::guest::Address(0x82002AC0u);
    t.pres_scene = 24u;
    t.pres_service = 492u;
    t.pres_root = 500u;
    t.pres_animation = 568u;
    t.pres_slot_nullsub = 68u;
    // K (CGameface, vtable 0x82002A80): the clock K +8 vf +16 (`addi r3,r31,8` / `lwz r10,0x10(r11)`
    // 0x82279B38..0x82279B44); the callback K +588 / ctx +592 (`lwz r11,0x24C(r29)` / `lwz r6,0x250(r29)`
    // 0x82C55A50..0x82C55A70: (2, pres, 0, ctx) made, 0x82C55BB4 (0, ...) loaded; 0x82C55C70 (1, ...)
    // destroyed).
    t.gameface_clock = 8u;
    t.clock_slot_ms = 16u;
    t.gameface_callback = 588u;
    t.gameface_callback_ctx = 592u;
    // CScene, vtable 0x82002A00: +16 `sub_82272A80` the refresh, called unchecked after pres vf +12
    // (`lwz r10,0x10(r11)` / `bctrl` 0x825F2DF0..0x825F2DF8); +28 `lwz r3,0x6F0(r3)`, whose list
    // `sub_825FE650` reads as empty (+0x10 == +0x14, 0x825FE684..0x825FE68C).
    t.scene_vtable = refii::guest::Address(0x82002A00u);
    t.scene_slots = 16u;
    t.scene_slot_refresh = 4u;
    t.scene_slot_list = 7u;
    t.scene_render_list = 0x6F0u;
    // Names: `sub_824CBC50` (`mullw` by 0x1003F over signed bytes, 100 cap), 27 bits (`insrwi
    // r11,r3,27,0` 0x8287611C). "Parameters": the contracts walk hashes the CCharString
    // `dword_8359E7EC` (`addi r28,r11,dword_8359E7EC@l` 0x828779EC, `bl sub_824CBC50` 0x82877A18).
    t.name_hash = refii::guest::Address(0x824CBC50u);
    t.attribute_id_mask = 0x07FFFFFFu;
    t.parameters_name = refii::guest::Address(0x8359E7ECu);
    // The deserialiser's element section `sub_82C644A8`: timeoffset / endtime `dword_8359FA58` /
    // `dword_8359FA5C` set flag 0x0002 (`v28 == dword_8359FA58` 0x82C64854, `|| v28 ==
    // dword_8359FA5C` 0x82C64888 -> svc vf +80(e, 2, 1) 0x82C648A8).
    t.attr_start_time = refii::guest::Address(0x8359FA58u);
    t.attr_end_time = refii::guest::Address(0x8359FA5Cu);
    t.quad_record_ctor = 0u;
    t.quad_record_size = 0u;
    return t;
}

/// Fable 3's table with its services, built once (headless_presentation_hooks.cpp).
const refii::headless::GamefaceTable& Gameface();

}  // namespace refii::fable3
