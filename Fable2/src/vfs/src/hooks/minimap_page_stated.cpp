// THE WORLD MAP'S MINIMAP PAGE, STATED BY ITS OWN FILL — the page's vf +12 `sub_829ABFC0` (TU1; disc
// `sub_828FE480`, 2150 instructions, identical mnemonic streams), run by the world map's Update
// (gui_screen_stub_world_map.cpp) under the page-fill scope "row.minimap" for the highlighted region row.
// What the fill writes by element name is stated by page_fills_stated.cpp (the legend's texts and shown
// flags, `lhGuiElement_SetText` / `sub_82426260`; the map image's rotation `sub_824254C8`; the four
// `sub_82425C10` legend textures). This file states the rest, the writes that go round those calls:
//
// THE INLINE TEXTURE STORES. The fill sets a quad's texture (`+0x200`, dirty byte `+0x224`) itself on
// eleven elements, behind `lwz r11,4(rE) ; cmplwi ; beq` (the render node; a headless element has none,
// so the store never ran). At each store's head the element it looked up by name and the hash are
// stated (both builds' sites at the same offsets, the bodies identical):
//   the map image `…Page2.Contents.Bowerstone`  0x829AC770 / 0x828FEC30: r24 the element, r29 the hash
//     VALUE (`lwz r29,0(r10)` 0x829AC674 / 0x828FEB34, the record's `MinimapBackgroundImage`);
//   the legend's `KeyImage<n>` for a region the hero stands in, r30 the element (found by
//     `sub_82426798` two calls earlier) and the hash's address in a register the store reads
//     (`lwz r10,0(rH)` after `stb r21,0x224(r10)`): 0x829AD3C0 / 0x828FF880 r26 (`minimap16`, hero),
//     0x829AD4B8 / 0x828FF978 r25 (`minimap11`, dog), 0x829AD5BC / 0x828FFA7C r23 (`minimap1`, current
//     quest) or 0x829AD624 / 0x828FFAE4 r24 (`minimap17`, its exit), 0x829AD71C / 0x828FFBDC r22
//     (`minimap3`, quest), 0x829AD814 / 0x828FFCD4 r16 (`minimap10`, merchant) — r26..r16 set at
//     0x829ACAD4..0x829ACAE8 (`addi rH, rX, dword_8349D720..734@l`; disc 0x828FEF94..0x828FEFA8 with
//     0x8349D6E8..6FC);
//   the last four rows' hashes are globals formed after the headless test (`lis r9,dword_8349D738@ha`
//     0x829AD924), so each site has its own hook naming its global: 0x829AD90C / 0x828FFDCC
//     `dword_8349D738` / `8349D700` (`minimap18`, region exit), 0x829ADA08 / 0x828FFEC8 `…740` / `…708`
//     (`minimap22`, weapon shops), 0x829ADB04 / 0x828FFFC4 `…744` / `…70C` (`minimap24`, miscellaneous),
//     0x829ADC00 / 0x829000C0 `…748` / `…710` (`minimap23`, appearance).
// The globals are the guest's static texture hashes (each `lhResourceName_ComputeHash` of the lower-cased
// `art\gui\wheelicons\minimap\minimap<n>.tex`), read when the store would have been made.
//
// THE ICONS. Every map icon is handed to one builder, `sub_829AE1C8(page r3, &hash r4, &world r5, &facing r6,
// f1 depth)` (TU1; disc `sub_82900688`, 235 instructions, identical) — the hero at 0x829ACB68 / 0x828FF028
// with its facing `sub_821E7508(&out, hero)` (0x829ACB50), and the dog, the trail target (current quest or its
// exit), each other quest and each travelling merchant through `sub_829AE158` (0x829ACD08 / 0x829ACE98 /
// 0x829AD074 / 0x829AD2A4; disc 0x828FF1C8 / 0x828FF358 / 0x828FF534 / 0x828FF764), which only builds the
// zero facing and calls it (0x829AE1B4). The builder's body projects the point through the page's fields and
// posts a GUI render command (`lhRenderCommandStream_PostCreateCommand` 0x829AE510) — drawing, which Godot
// owns. Its entry is stated and its body not run (midasm `return_on_true`; after each call the fill only
// moves its depth bias, `fmr f31,f30` 0x829ACB6C). Stated per icon, in call order: `icon <hash %08X>
// "x y z fx fy fz"` (the world point, the facing; all-zero facing = no turn, `sub_829AE1C8` 0x829AE30C..
// 0x829AE31C). Before the first icon of a fill, the projection the builder would have used, from the page
// (r3, single dereference): `map Page2 "north iconRot offX offY offZ scale proper extent"` = page+0x1C (the
// record's `MinimapDegreesRotationToNorth`, `stfs f12,0x1C(r16)` 0x829AC69C), +0x34 (`IconTransformRotation`
// 0x829AC6A4), the vec4 at +0x40 (`IconTransformX / Y / Z` into lanes 0 / 1 / 2 by the masks at 0x82099170 /
// 0x82099180 / 0x82099190, `stvx128 v0,r0,r31` with r31 = page+0x40 0x829AC6BC), +0x50 (`IconTransformScale`
// 0x829AC758), +0x54 (`HasProperMinimap`, `stb r3,0x54(r16)` 0x829AC76C), +0x30 (the current level's larger
// span, `stfs f6,0x30(r16)` 0x829AC8FC, reached only when the hero stands in the region — which every icon
// requires: 0x829AC888 / 0x829ACAEC).

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>

#include <rex/ppc/context.h>

#include "core/guest_build.h"
#include "core/guest_state.h"
#include "hooks/page_fills_stated.h"

extern "C" uint8_t* RefiiGuestBase(void);

namespace {

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

float GuestFloat(uint8_t* base, uint32_t at) {
  const uint32_t bits = refii::vfs::LoadGuestU32(base, at);
  float value = 0.0f;
  std::memcpy(&value, &bits, sizeof value);
  return value;
}

/// The hash at `at` stated on `element`, when a fill is open.
void StateHashAt(uint32_t element, uint32_t at) {
  uint8_t* base = RefiiGuestBase();
  if (base == nullptr || !refii::vfs::PageFillOpen() || !Plausible(element) || !Plausible(at)) {
    return;
  }
  refii::vfs::NotePageFillTexture(element, refii::vfs::LoadGuestU32(base, at));
}

/// The page whose projection was last stated, on this thread: one `map` per fill.
thread_local uint32_t t_map_stated_for = 0u;
thread_local uint32_t t_map_fill_serial = 0u;

}  // namespace

// The map image's store head, TU1 0x829AC770 / disc 0x828FEC30: r24 the element, r29 the hash value.
void RefiiMinimapImageTexture(PPCRegister& element, PPCRegister& hash) {
  if (!refii::vfs::PageFillOpen() || !Plausible(element.u32) || hash.u32 == 0u) {
    return;
  }
  refii::vfs::NotePageFillTexture(element.u32, hash.u32);
}

// A legend row's store head whose hash's address is in a register (six sites, above): r30 the element.
void RefiiMinimapKeyTexture(PPCRegister& element, PPCRegister& hash_at) { StateHashAt(element.u32, hash_at.u32); }

// The four legend rows whose hash is a global formed after the head (above): r30 the element.
void RefiiMinimapKeyRegionExit(PPCRegister& element) {
  StateHashAt(element.u32, refii::guest::Address(0x8349D738u));
}

void RefiiMinimapKeyWeaponShops(PPCRegister& element) {
  StateHashAt(element.u32, refii::guest::Address(0x8349D740u));
}

void RefiiMinimapKeyMiscellaneous(PPCRegister& element) {
  StateHashAt(element.u32, refii::guest::Address(0x8349D744u));
}

void RefiiMinimapKeyAppearance(PPCRegister& element) {
  StateHashAt(element.u32, refii::guest::Address(0x8349D748u));
}

// The icon builder's entry, TU1 0x829AE1C8 / disc 0x82900688: r3 the page, r4 &hash, r5 &world point,
// r6 &facing. Stated inside a fill; the drawing body never runs (its only callers are the fill and its
// zero-facing wrapper, so outside a fill it has nothing to draw for either).
bool RefiiMinimapIcon(PPCRegister& r3, PPCRegister& r4, PPCRegister& r5, PPCRegister& r6) {
  uint8_t* base = RefiiGuestBase();
  if (base == nullptr || !refii::vfs::PageFillOpen() || !Plausible(r3.u32) || !Plausible(r4.u32) ||
      !Plausible(r5.u32) || !Plausible(r6.u32)) {
    return true;
  }
  const uint32_t page = r3.u32;
  const uint32_t serial = refii::vfs::PageFillSerial();
  if (t_map_stated_for != page || t_map_fill_serial != serial) {
    t_map_stated_for = page;
    t_map_fill_serial = serial;
    char map[192];
    std::snprintf(map, sizeof map, "%g %g %g %g %g %g %u %g", GuestFloat(base, page + 0x1Cu),
                  GuestFloat(base, page + 0x34u), GuestFloat(base, page + 0x40u), GuestFloat(base, page + 0x44u),
                  GuestFloat(base, page + 0x48u), GuestFloat(base, page + 0x50u),
                  refii::vfs::LoadGuestU8(base, page + 0x54u) != 0u ? 1u : 0u, GuestFloat(base, page + 0x30u));
    refii::vfs::NotePageFillMark("map", "Page2", map);
  }
  char hash[9];
  std::snprintf(hash, sizeof hash, "%08X", refii::vfs::LoadGuestU32(base, r4.u32));
  char at[192];
  std::snprintf(at, sizeof at, "%g %g %g %g %g %g", GuestFloat(base, r5.u32), GuestFloat(base, r5.u32 + 4u),
                GuestFloat(base, r5.u32 + 8u), GuestFloat(base, r6.u32), GuestFloat(base, r6.u32 + 4u),
                GuestFloat(base, r6.u32 + 8u));
  refii::vfs::NotePageFillMark("icon", hash, at);
  return true;
}
