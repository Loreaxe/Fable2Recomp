// refii - what the HUD's levers share (emotion_choice_answer.cpp, world_icon_hold.cpp, hud_dpad_use.cpp,
// interaction_mode_input.cpp): a game action handed to one of the guest's own input handlers on the
// guest tick thread, the lever's player field, and the HUD's objects.
//
// THE HANDLERS. The guest's dispatcher sub_825FC860(D, action*, player) (disc sub_825D1710) walks its
// channel list, kept sorted by channel (the insert sub_825FC6F0 stops at the first entry whose channel
// is not below the new one, `i[2] >= a2`), calls each entry's thunk (action*, player, owner) and stops
// at the first that answers non-zero (`lwz r5,8(r11); mtctr r10; bctrl; clrlwi r9,r3,24; bne`
// 0x825FC8E4..0x825FC8F8). Each thunk is `mr r5,r4; mr r4,r3; mr r3,r11; b <handler>`: the handler
// takes (owner, action*, player). The action record's first word is the EGameAction code and its float
// at +8 a stick's value (interaction_list_step.cpp). The HUD's channels are 4 TopBox, 5 emotion choices,
// 8 interaction mode, 9 orb status, 12 / 13 the d-pad of player one / two, 14 world icons — 14 is the
// last, so its answer reaches nobody.
//
// THE OBJECTS (the same offsets on both builds; the HUD's constructor TU3 sub_824CBCA0 / disc
// sub_825C0208 replaces each: `lwz r29,0x18(r30)` 0x824CBE18 / 0x825C0380, `lwz r3,0x1C(r30)`
// 0x824CBE58 / 0x825C03C0, `lwz r26,0x30(r30)` 0x824CC024 / 0x825C0550, `lwz r26,0x34(r30)` 0x824CC0C4 /
// 0x825C05F0): H = *(GUI+0x20); the world icon registry R = *(H+0x18); the emotion choice UI
// E = *(H+0x1C); the d-pad widgets *(H+0x30) (channel 12) and *(H+0x34) (channel 13).

#pragma once

#include <cstdint>
#include <cstdlib>
#include <cstring>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "box_answers.h"
#include "gui_statements.h"
#include "../../vfs/include/fable3_guest_build.h"

namespace refii::fable3 {

constexpr uint32_t kHudIconRegistry = 0x18u;
constexpr uint32_t kHudEmotionChoices = 0x1Cu;
constexpr uint32_t kHudDpadOne = 0x30u;
constexpr uint32_t kHudDpadTwo = 0x34u;

/// H = *(GUI+0x20), or 0 before the GUI has one.
inline uint32_t HudOf(const uint8_t* base) {
  const uint32_t gui = LoadGuestU32(base, refii::guest::Address(0x83596F84u));
  const uint32_t hud = Plausible(gui) ? LoadGuestU32(base, gui + 0x20u) : 0u;
  return Plausible(hud) ? hud : 0u;
}

/// The HUD's object at `offset`, or 0.
/// The code of the action record a channel handler was handed (`*(action+0)`, the dispatcher's own
/// argument), for its once-only log; ~0 when there is none.
inline uint32_t HandedCode(const uint8_t* base, uint32_t action) {
  return base != nullptr && Plausible(action) ? LoadGuestU32(base, action) : ~0u;
}

/// THE CUT'S ONCE-ONLY LINE. Its handler is entered by the dispatcher alone (the levers call the body,
/// `__imp__`), so the line printing means the guest's own pad action came to the hook and was answered 0
/// without running: the cut holds. A run in which the pad still acts here and the line never prints is
/// the defect (the row is not reached).
inline void SayCutHolds(const char* tag, const char* surface, const char* lever, uint32_t code) {
  REXLOG_INFO("[{}] the pad cut holds: the guest's own pad action {} came to the {} and was answered 0 without "
              "running it (only the {} lever runs the body)",
              tag, static_cast<int32_t>(code), surface, lever);
}

inline uint32_t HudObject(const uint8_t* base, uint32_t offset) {
  const uint32_t hud = HudOf(base);
  const uint32_t object = hud != 0u ? LoadGuestU32(base, hud + offset) : 0u;
  return Plausible(object) ? object : 0u;
}

/// `handler(owner, action*, player)` with a zeroed 32-byte action record holding `action` and `value`,
/// on a frame below the tick's; the handler's answer.
template <typename Handler>
inline uint32_t CallWithAction(PPCContext& ctx, uint8_t* base, Handler handler, uint32_t owner, uint32_t action,
                               uint32_t player, float value = 0.0f) {
  PPCContext work = ctx;
  work.r1.u32 = (ctx.r1.u32 - 0x200u) & ~0xFu;
  const uint32_t record = work.r1.u32 + 0x100u;
  std::memset(base + record, 0, 32u);
  const uint32_t code = __builtin_bswap32(action);
  std::memcpy(base + record, &code, sizeof(code));
  uint32_t bits = 0u;
  std::memcpy(&bits, &value, sizeof(bits));
  bits = __builtin_bswap32(bits);
  std::memcpy(base + record + 8u, &bits, sizeof(bits));
  work.r3.u64 = owner;
  work.r4.u64 = record;
  work.r5.u64 = player;
  handler(work, base);
  return work.r3.u32 & 0xFFu;
}

/// A lever's `<p>:` field at `at` (p 1 or 2): the text after it, or null.
inline const char* LeverPlayer(const char* at, uint32_t& player) {
  if ((at[0] != '1' && at[0] != '2') || at[1] != ':') {
    return nullptr;
  }
  player = static_cast<uint32_t>(at[0] - '0');
  return at + 2;
}

/// A lever's decimal field at `at`, ending at ':' or the end: the text after it (past the ':'), or null.
inline const char* LeverNumber(const char* at, uint32_t& value) {
  char* end = nullptr;
  const unsigned long parsed = std::strtoul(at, &end, 10);
  if (end == at || (*end != '\0' && *end != ':') || parsed > 0xFFFFul) {
    return nullptr;
  }
  value = static_cast<uint32_t>(parsed);
  return *end == ':' ? end + 1 : end;
}

/// Whether a showing box takes `action` before any HUD channel: the box router (channel 1) answers
/// "consumed" for 29..36 and 72..85 while a box other than an intermission shows (`cmpwi r10,0x1D /
/// 0x24 / 0x48 / 0x55`, TU3 0x825F39F0..0x825F3A1C / disc 0x825C8C60..0x825C8C8C), and the menu box
/// takes A.
inline bool BoxTakesAction(const uint8_t* base, uint32_t action, uint32_t player) {
  const bool routed = (action >= 29u && action <= 36u) || (action >= 72u && action <= 85u);
  return routed && PadTakenAheadOfTopBox(base, action, player);
}

}  // namespace refii::fable3
