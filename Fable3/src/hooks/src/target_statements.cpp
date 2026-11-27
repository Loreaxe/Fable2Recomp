// refii - who targets each thing, stated at the guest's targeted setter: `@Target.By(thing, players,
// interact)`, for each split half to keep its own player's highlight and "Interact" icon
// (Fable3Targeting, Engine_EntityHighlight.DrawnInView).
//
// Addresses are TU3 / disc; offsets are from the CECTargeted component (r3).
//
// THE SETTER, sub_826F6298(targeted, byte, targeter) / disc sub_826C8FA8. Player two is the targeter
// whose uid matches the hero in the entity manager's +0x88 slot (`lwz r11,0x88(r11)` 0x826F62C4 /
// 0x826C8FD4; `lwz r8,0x18(r11)` / `cmplw r8,r10` 0x826F6320..0x826F6324 and `+0x1C` 0x826F632C..
// 0x826F6334; disc 0x826C9030..0x826C9044). It writes player two's byte `stb r28,0x22(r31)` with
// `stb r11,0x23(r31)` (0x826F63D4 / 0x826F63D8; disc 0x826C90E4 / 0x826C90E8) or player one's
// `stb r28,0x21(r31)` (0x826F63E8 / disc 0x826C90F8) — their only writer — and then, from the two
// bytes (`lbz r10,0x21(r31)` / `lbz r11,0x22(r31)` 0x826F6440 / 0x826F644C; disc 0x826C9150 /
// 0x826C915C):
//   - the highlight on (`bl sub_8219AFA8` 0x826F6488 / disc `bl sub_8219AFD0` 0x826C9198), which
//     ramps the target's renderable from the targeting hero's own state (+0x21 player one through
//     sub_821FB318, else +0x22 player two through sub_823BB4C0; TU3 0x8219AFE4..0x8219B0AC), or off
//     (`bl sub_826F76F0` 0x826F647C / disc `bl sub_826CA430` 0x826C918C);
//   - the "Interact" icon (`bl sub_82178AC8` 0x826F64A4 / disc `bl sub_82178B88` 0x826C91B4): only
//     when +0x28 is set (`lbz r11,0x28(r3)` / `beq` 0x82178AD4..0x82178AE4; disc 0x82178B94..
//     0x82178BA4), one pooled type-34 icon named "Interact" on the thing `*(r3+4)` (`lwz r5,4(r28)`,
//     `li r7,0x22`, `bl sub_82614D30` 0x8217906C..0x82179098; disc 0x82179174..`bl sub_825ED9C8`
//     0x821791A0), shown flag +0x29.
// Both are ONE per target, raised for whichever player targets it. The body runs unchanged; as it
// returns, the two bytes it left and +0x28 are stated with the thing.

#include <cstdint>

#include <rex/ppc.h>

#include "gui_statements.h"

REX_EXTERN(__imp__rex_Targeted_SetTargetedByPlayer);

// (targeted, byte, targeter).
REX_EXTERN(rex_Targeted_SetTargetedByPlayer) {
  using namespace refii::fable3;
  const uint32_t targeted = ctx.r3.u32;
  __imp__rex_Targeted_SetTargetedByPlayer(ctx, base);
  if (base == nullptr || !Plausible(targeted)) {
    return;
  }
  const uint32_t thing = LoadGuestU32(base, targeted + 4u);
  if (!Plausible(thing)) {
    return;
  }
  const uint32_t players = (base[targeted + 0x21u] != 0u ? 1u : 0u) | (base[targeted + 0x22u] != 0u ? 2u : 0u);
  Statement("@Target.By")
      .Add(Text(Hex(thing)))
      .Add(Number(static_cast<float>(players)))
      .Add(Boolean(base[targeted + 0x28u] != 0u))
      .Post();
}
