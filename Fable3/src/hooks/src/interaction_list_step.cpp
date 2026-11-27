// refii - the interaction list's cursor from outside: Godot's pointer asks for one step and the
// list's own input takes it (`interaction-list:<p>:up` / `:down`).
//
// THE GUEST'S MECHANISM (TU3 addresses; "disc" marks the disc build's). The list is the interaction
// mode's object (IM+24): its cursor is +0x2C and its row count +0x30 (disc +0x74 / +0x78), and it
// takes no row as an argument anywhere. Every move is one of two steps of its input
// sub_82972ED8(list, ev*, player) (disc sub_82964248), reached for a stick event:
//   `82972F2C lwz r11,0(r27); cmpwi r11,0x1A; bne` (disc 0x8296429C..0x829642A0): event id 26;
//   the player's ownership (sub_821FF0A8 of the player, sub_829704A0(list, ev, player) at 0x82972F9C /
//   0x8297305C; disc sub_821FD790, sub_82961848 at 0x8296430C / 0x829643CC), else `li r3,0` out;
//   `829730E4 lbz r11,0x4C(r30); beq` (disc `82964454 lbz r11,0x94(r30)`): only while the list is
//   active - set by the refresh sub_82972870 (`82972900 stb r11,0x4C(r31)`, disc sub_82963BE0
//   `82963C70 stb r11,0x94(r31)`), cleared by the hide sub_82972920 (`8297298C stb r11,0x4C(r30)`, disc
//   sub_82963C90 `82963CFC stb r11,0x94(r30)`);
//   `829730F0 lfs f0,8(r27); lfs f13,0(r8); fcmpu; bge` (disc 0x82964460..0x82964468): the event's
//   value at +8 under -0.5 (flt_82071B08, disc flt_82071C54) is DOWN: behind the list element's own
//   gate (`bl sub_82878358` 0x82973108, disc `bl sub_8281A650` 0x82964478) `8297311C bl sub_829729A8`
//   (disc `8296448C bl sub_82963D18`) - cursor + 1 while under count - 1 (`829729D0 lwz r11,0x30(r3);
//   addi r10,r11,-1; lwz r11,0x2C(r3); cmplw; bge` .. `829729EC stw r11,0x2C(r3)`; disc 0x82963D40..
//   0x82963D5C on 0x78 / 0x74), the list root's slide 3, the rows rewritten (sub_82972A70(list, 1)),
//   vtable +0x3C;
//   `8297312C lfs f13,-0x53C0(r8); fcmpu; ble` (disc `8296449C lfs f13,-0x54CC(r8)`): over 0.5
//   (flt_8206C748, disc flt_8206C788) is UP, inline, behind the same gate: `82973160 lwz r11,0x2C(r30);
//   cmplwi r11,0; beq` .. `8297316C addi r11,r11,-1; 82973174 stw r11,0x2C(r30)` (disc 0x829644D0..
//   0x829644E4 on 0x74), slide 4, the rows rewritten (sub_82972A70(list, 0)), vtable +0x38.
// There is no step-up function on either build (the down step's one caller is this input), so the
// lever runs the input itself for both directions, with event 26 and the value 1 or -1 at +8: the
// guest's ownership test, its active test, its gate and its cue all run as for a stick. Nothing of
// the list's is written here.
//
// The list is remembered where the guest hands it to its row writer (rex_InteractionList_Write,
// ui_stated_hooks.cpp) and forgotten at its hide and with the interaction mode's set, clear and
// close; a step is taken only while the remembered list is still its mode's object
// (`*(list+0x14)` is the IM - `829729BC lwz r11,0x14(r3)`, disc 0x82963D2C - and IM+24 the object).
// Both edges are stated: `@Interaction.ListUp(up)` - a list the guest is writing rows for, until
// its hide or its mode's end.
//
// The Sanctuary's class (CInteractionModeGUIWorld, vtable 0x820C3C10 / disc 0x820C2088) is not one of
// these lists: the input answers it through interaction_guiworld.cpp (its cursor is Godot's, set by
// `guiworld:<p>:cursor:<row>`), and the row writer remembers no list for it (ui_stated_hooks.cpp).

#include <atomic>
#include <cstdint>
#include <cstring>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "box_answers.h"
#include "gui_statements.h"
#include "interaction_guiworld.h"

REX_EXTERN(rex_InteractionList_Input);
REX_EXTERN(__imp__rex_InteractionList_Input);
REX_EXTERN(__imp__rex_InteractionList_Hide);

namespace {

using namespace refii::fable3;

constexpr uint32_t kListIm = 0x14u;
constexpr uint32_t kImObject = 24u;
/// The stick event's id (`cmpwi r11,0x1A`).
constexpr uint32_t kEventStick = 26u;
/// Past the input's thresholds of -0.5 / 0.5 on either side.
constexpr float kDown = -1.0f;
constexpr float kUp = 1.0f;

std::atomic<uint32_t> g_list{0u};

void StoreU32(uint8_t* base, uint32_t address, uint32_t value) {
  const uint32_t swapped = __builtin_bswap32(value);
  std::memcpy(base + address, &swapped, sizeof(swapped));
}

}  // namespace

namespace refii::fable3 {

void NoteInteractionList(uint32_t list) {
  if (g_list.exchange(list) != list) {
    Statement("@Interaction.ListUp").Add(Boolean(true)).Post();
  }
}

void ForgetInteractionList() {
  if (g_list.exchange(0u) != 0u) {
    Statement("@Interaction.ListUp").Add(Boolean(false)).Post();
  }
}

bool AnswerInteractionListStep(PPCContext& ctx, uint8_t* base, const char* action) {
  const char* field = action + sizeof(kInteractionListAction) - 1u;
  if ((field[0] != '1' && field[0] != '2') || field[1] != ':') {
    REXLOG_WARN("[fable3-interaction-list] '{}' names no player; dropped", action);
    return false;
  }
  const bool down = std::strcmp(field + 2, "down") == 0;
  if (!down && std::strcmp(field + 2, "up") != 0) {
    REXLOG_WARN("[fable3-interaction-list] '{}' names no direction; dropped", action);
    return false;
  }
  const uint32_t list = g_list.load();
  const uint32_t im = base != nullptr && Plausible(list) ? LoadGuestU32(base, list + kListIm) : 0u;
  if (!Plausible(im) || LoadGuestU32(base, im + kImObject) != list) {
    REXLOG_WARN("[fable3-interaction-list] '{}' with no list up; dropped", action);
    ForgetInteractionList();
    return false;
  }

  PPCContext work = ctx;
  work.r1.u32 = (ctx.r1.u32 - 0x200u) & ~0xFu;
  const uint32_t event = work.r1.u32 + 0x100u;
  for (uint32_t at = 0u; at < 32u; at += 4u) {
    StoreU32(base, event + at, 0u);
  }
  StoreU32(base, event, kEventStick);
  const float value = down ? kDown : kUp;
  uint32_t bits = 0u;
  std::memcpy(&bits, &value, sizeof(bits));
  StoreU32(base, event + 8u, bits);
  work.r3.u64 = list;
  work.r4.u64 = event;
  work.r5.u64 = static_cast<uint32_t>(field[0] - '0');
  rex_InteractionList_Input(work, base);
  return true;
}

}  // namespace refii::fable3

/// The list's hide sub_82972920(list) (disc sub_82963C90): after it the list takes no step.
REX_EXTERN(rex_InteractionList_Hide) {
  refii::fable3::ForgetInteractionList();
  __imp__rex_InteractionList_Hide(ctx, base);
}

/// The shared list input sub_82972ED8(list, action*, player) (disc sub_82964248), the vf5 of eleven
/// vtables: the Sanctuary's class answers through interaction_guiworld.cpp, every other runs as the
/// guest has it.
REX_EXTERN(rex_InteractionList_Input) {
  if (refii::fable3::GuiWorldListInput(ctx, base)) {
    return;
  }
  __imp__rex_InteractionList_Input(ctx, base);
}
