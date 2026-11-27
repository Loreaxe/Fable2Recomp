#include "vfs_hooks_private.h"

#include <cstdint>

#include "hooks/ppc_externs.h"

// Target highlight colour, taken at the guest's setter rather than polled.
//
// `sub_825738F0` stores four bytes at `r31+0x60..0x63` (0x82573900..0x82573988), r31 the
// CECGraphicAppearance component. Polling that field cannot work: {0,0,0,255} is the clear
// value, written by `sub_82429BB0` via `sub_825738F0(comp3, &byte_83319B68)`, and the colour is
// written only while the ramp runs — `sub_822A3BF8` stops calling the setter once
// `dword_83319650 - *(comp+76)` passes `flt_83319490[kind] * 15.0`.
//
// Hooked calls:
//
//   sub_825738F0(CECGraphicAppearance* r3, const uint8_t rgba[4] r4)
//       States the colour and the renderable. The component-159 redirect (a troll weak spot
//       sends its highlight to another entity) is already resolved, so r3 is the component
//       that should light up.
//
//   sub_82427FD0(CECTargeted* r3, r4 the byte, r5 the player) — lhECTargeted_SetTargetedByPlayer
//       States that an entity became or stopped being a player's target. It writes `+0x21` for
//       player one or `+0x22` for player two (TU1 `stb r28,0x22(r29)` 0x8242810C / `stb r28,0x21(r29)`
//       0x82428114; disc 0x82397EE4 / 0x82397EEC), the bytes' only writer, then raises the highlight
//       while either is set (`sub_822A3BF8` 0x82428160 / disc `sub_822A39B8` 0x82397F38, else the
//       clear `sub_82429BB0` / `sub_82399988`) and the target's "Interact" icon the same way
//       (`sub_82175310(r3, either)` 0x8242817C / disc `sub_821750D8` 0x82397F54: shown only when
//       `+0x27` is set, 0x82175320 / 0x821750E8; one pooled type-34 icon keyed "Interact" on the
//       thing `*(r3+4)`, `lwz r5,4(r29)` 0x8217588C / 0x82175654). Both are ONE per target, raised
//       for whichever player targets it, so the players who target the thing are stated here,
//       as the setter leaves its two bytes: each split view keeps its own player's.
//
// Keyed by the component pointer, which the host already resolves per entity.

// THE TABLE IS NOT HERE. It lives in src/core/target_highlight_state.cpp, which links into
// librefii_vfs: this file compiles into the payload and the host bridge reads what it records,
// so a table beside these hooks is one the host cannot reach on Windows. See that file.

REX_EXTERN(rex_CECGraphicAppearance_SetHighlightColour) {
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();

  // The four bytes in store order (r4[0] lands at +0x60, r4[1] at +0x61, ...), packed
  // big-endian so R is the top byte.
  const uint32_t at = ctx.r4.u32;
  const uint32_t rgba = (static_cast<uint32_t>(refii::vfs::LoadGuestU8(base, at)) << 24) |
                        (static_cast<uint32_t>(refii::vfs::LoadGuestU8(base, at + 1u)) << 16) |
                        (static_cast<uint32_t>(refii::vfs::LoadGuestU8(base, at + 2u)) << 8) |
                        static_cast<uint32_t>(refii::vfs::LoadGuestU8(base, at + 3u));
  refii::vfs::NoteHighlightColour(ctx.r3.u32, rgba);

  REFII_VFS_NATIVE_PASSTHROUGH(rex_CECGraphicAppearance_SetHighlightColour);
}

REX_EXTERN(rex_lhECTargeted_SetTargetedByPlayer) {
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();
  refii::vfs::NoteHighlightTarget();
  const uint32_t targeted = ctx.r3.u32;
  __imp__rex_lhECTargeted_SetTargetedByPlayer(ctx, base);
  if (targeted < 0x10000u || targeted >= 0xC0000000u) {
    return;
  }
  const uint32_t mask = (refii::vfs::LoadGuestU8(base, targeted + 0x21u) != 0u ? 1u : 0u) |
                        (refii::vfs::LoadGuestU8(base, targeted + 0x22u) != 0u ? 2u : 0u);
  refii::vfs::NoteTargetedBy(refii::vfs::LoadGuestU32(base, targeted + 4u), mask,
                             refii::vfs::LoadGuestU8(base, targeted + 0x27u) != 0u);
}
