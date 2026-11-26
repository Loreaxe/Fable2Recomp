// refii - the queued boxes' methods that draw, re-expressed without the drawing: Godot draws every
// box (Fable3Boxes), the guest keeps what its methods do to the game. Each body here REPLACES its
// guest method (no __imp__ call) and is the method's own instructions less its element work; every
// one is the only slot of one vtable (TU3 / disc, read word by word), so no other class reaches it.
// TU3 addresses; the disc bodies are the same instruction for instruction (named below).
//
// THE INTERMISSION BOX (CIntermissionBox, TU3 vtable 0x820DB1DC / disc 0x820D87D4: slot 1 Show
// sub_82B93580 / disc sub_82B5E930 at +4, slot 2 Hide sub_82B93678 / disc sub_82B5EA28 at +8, slot 5
// Update sub_82B93710 / disc sub_82B5EAC0 at +0x14). It takes no input and posts no answer. What its
// methods do, by instruction:
//   - Show: the presentation `Pres(box)` sub_829588C8 (0x82B93590; it LOADS the slot when empty),
//     `Find("Group.Text")` (0x82B935B8), the text commit (0x82B935E4), root state 1 (0x82B935F8) and
//     the element read-back `box+32 = Y of Group.Text` (`bl sub_82876390` 0x82B93604, `stfs f0,
//     0x20(r29)` 0x82B9360C) — drawing, dropped. Kept: unless the game is paused (GAME vt[6], `lwz
//     r9,0x18(r10)` / `bctrl` 0x82B9361C..0x82B93624), the HUD group the box covers slides away,
//     `sub_829759E8(*(*(*(*(GUI+0x20)+0x24)+0x18)+0x24), *dword_8359F2B4, 1)` (0x82B93634..0x82B93658,
//     rex_HudSlides_Toggle, which states it); `+12 = 1` (0x82B93664) and BoxShown sub_82958550
//     (0x82B93668); the answer 1.
//   - Hide: Pres and root state 2 (0x82B93690..0x82B936AC) dropped; `+12 = 0`, `+16 = 0.75`
//     (flt_8206C930, 0x82B936BC..0x82B936C4), BoxHidden sub_82958710 (0x82B936C8), the HUD group back
//     (`li r5,0` / `bl sub_829759E8` 0x82B936D4..0x82B936F0).
//   - Update: CInfoBox's update sub_82B93500 (0x82B93720: the lifetime `+16 -= *(GUI+0xA8)` unless
//     `+28` holds it, vt[2] at zero, then the base update, whose answer is the method's), then while
//     shown the self-close on the HUD's state — `*(*(HUD+0x40)+9)` (0x82B93740..0x82B9374C), or with
//     X = *(HUD+0x30) `X+0x18` 1 or 2 and `X+0x2B` set (0x82B93750..0x82B93784) — through vt[2]
//     (0x82B937F8..0x82B93808). Otherwise the guest re-reads Group.Text's position and writes it back
//     when it moved from +32 (0x82B93788..0x82B937E8): the second read-back, dropped.
//
// THE INFO CARD (CAcceptHoldBox, TU3 vtable 0x820D9BB0 / disc 0x820D71CC, slot 5 Update
// sub_82B8BC50 / disc sub_82B570A0 at +0x14). Update: while shown (`lbz r11,0xC(r3)` 0x82B8BC64) and
// not infinite (`lbz r10,0x20(r3)` 0x82B8BC74), `+16 -= *(GUI+0xA8)` and vt[2] at zero
// (0x82B8BC88..0x82B8BCB8); then the hold step sub_82C3A130(H = *(box+0x1C)) (0x82B8BCC8) — H+0 the
// progress moved by `*(GUI+0xB0)` up while H+8 is held, down otherwise, clamped, and written to
// the meter element (`bl GuiElement_SetTime` 0x82C3A1B0) — and PostButtonAnswer(1) when the
// progress crossed 1.0 (0x82B8BCCC..0x82B8BD04); then the base update sub_82958160 (0x82B8BD0C), its
// answer the method's. THE HOLD IS GODOT'S (display_box_hooks.cpp answers it): its fill and its
// meter are dropped with the step, the lifetime and the base update kept.

#include <cstdint>
#include <cstring>

#include <rex/ppc.h>

#include "gui_statements.h"
#include "mod_seam.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(rex_DisplayBox_Shown);
REX_EXTERN(rex_DisplayBox_Hidden);
REX_EXTERN(rex_DisplayBox_Update);
REX_EXTERN(rex_InfoBox_Update);
REX_EXTERN(rex_HudSlides_Toggle);

namespace {

using refii::fable3::LoadGuestF32;
using refii::fable3::LoadGuestU32;
using refii::fable3::Plausible;

const refii::guest::Addr kGuiManager{0x83596F84u};
const refii::guest::Addr kGame{0x83597034u};
/// The HUD group the intermission box slides away while it shows (its Show and Hide read it,
/// `lwz r4,dword_8359F2B4@l(r10)` 0x82B93644 / 0x82B936DC).
const refii::guest::Addr kIntermissionHudGroup{0x8359F2B4u};

constexpr uint32_t kGuiHud = 0x20u;
constexpr uint32_t kGuiFrameSeconds = 0xA8u;
constexpr uint32_t kBoxShown = 12u;
constexpr uint32_t kBoxTimer = 16u;
/// CAcceptHoldBox: +32 the lifetime is infinite (its ctor, `< 0` from params+24).
constexpr uint32_t kCardInfinite = 0x20u;
/// Every Hide's close tail (flt_8206C930 / disc flt_8206C974): the base update releases the
/// queue when it runs out.
constexpr float kHideSeconds = 0.75f;
/// GAME's vtable slot 6: the game is paused.
constexpr uint32_t kGamePausedSlot = 6u;
/// The box's vtable slot 2: Hide.
constexpr uint32_t kHideSlot = 2u;

void StoreF32(uint8_t* base, uint32_t at, float value) {
  uint32_t bits;
  std::memcpy(&bits, &value, sizeof(bits));
  refii::fable3::seam::StoreU32(base, at, bits);
}

/// The HUD registry the box's group slide goes through: *(*(*(HUD+0x24)+0x18)+0x24), or 0.
uint32_t HudSlideRegistry(const uint8_t* base) {
  const uint32_t gui = LoadGuestU32(base, kGuiManager);
  const uint32_t hud = Plausible(gui) ? LoadGuestU32(base, gui + kGuiHud) : 0u;
  const uint32_t registries = Plausible(hud) ? LoadGuestU32(base, hud + 0x24u) : 0u;
  const uint32_t table = Plausible(registries) ? LoadGuestU32(base, registries + 0x18u) : 0u;
  return Plausible(table) ? LoadGuestU32(base, table + 0x24u) : 0u;
}

void SlideHudGroup(PPCContext& ctx, uint8_t* base, bool away) {
  const uint32_t registry = HudSlideRegistry(base);
  if (!Plausible(registry)) {
    return;
  }
  PPCContext work = refii::fable3::seam::Frame(ctx);
  work.r3.u64 = registry;
  work.r4.u64 = LoadGuestU32(base, kIntermissionHudGroup);
  work.r5.u64 = away ? 1u : 0u;
  rex_HudSlides_Toggle(work, base);
}

void Hide(PPCContext& ctx, uint8_t* base, uint32_t box) {
  PPCContext work = refii::fable3::seam::Frame(ctx);
  refii::fable3::seam::CallSlot(work, base, box, kHideSlot);
}

/// The base update's answer for `box` (0 releases it from its queue).
uint32_t BaseUpdate(PPCContext& ctx, uint8_t* base, uint32_t box,
                    void (*update)(PPCContext&, uint8_t*)) {
  PPCContext work = refii::fable3::seam::Frame(ctx);
  work.r3.u64 = box;
  update(work, base);
  return work.r3.u32;
}

}  // namespace

/// CIntermissionBox::Show(box) without its presentation, text commit, root state and read-back.
REX_EXTERN(rex_IntermissionBox_Show) {
  const uint32_t box = ctx.r3.u32;
  const uint32_t game = LoadGuestU32(base, kGame);
  PPCContext paused = refii::fable3::seam::Frame(ctx);
  const bool is_paused = Plausible(game) && refii::fable3::seam::CallSlot(paused, base, game, kGamePausedSlot) &&
                         (paused.r3.u32 & 0xFFu) != 0u;
  if (!is_paused) {
    SlideHudGroup(ctx, base, true);
  }
  base[box + kBoxShown] = 1u;
  PPCContext work = refii::fable3::seam::Frame(ctx);
  work.r3.u64 = box;
  rex_DisplayBox_Shown(work, base);
  ctx.r3.u64 = 1u;
}

/// CIntermissionBox::Hide(box) without its presentation and root state.
REX_EXTERN(rex_IntermissionBox_Hide) {
  const uint32_t box = ctx.r3.u32;
  base[box + kBoxShown] = 0u;
  StoreF32(base, box + kBoxTimer, kHideSeconds);
  PPCContext work = refii::fable3::seam::Frame(ctx);
  work.r3.u64 = box;
  rex_DisplayBox_Hidden(work, base);
  SlideHudGroup(ctx, base, false);
}

/// CIntermissionBox::Update(box): its lifetime, base update and self-close, not its read-back.
REX_EXTERN(rex_IntermissionBox_Update) {
  const uint32_t box = ctx.r3.u32;
  const uint32_t answer = BaseUpdate(ctx, base, box, rex_InfoBox_Update);
  if (base[box + kBoxShown] != 0u) {
    const uint32_t gui = LoadGuestU32(base, kGuiManager);
    const uint32_t hud = Plausible(gui) ? LoadGuestU32(base, gui + kGuiHud) : 0u;
    const uint32_t first = Plausible(hud) ? LoadGuestU32(base, hud + 0x40u) : 0u;
    const uint32_t second = Plausible(hud) ? LoadGuestU32(base, hud + 0x30u) : 0u;
    bool close = Plausible(first) && base[first + 9u] != 0u;
    if (!close && Plausible(second)) {
      const uint32_t state = LoadGuestU32(base, second + 0x18u);
      close = (state == 1u || state == 2u) && base[second + 0x2Bu] != 0u;
    }
    if (close) {
      Hide(ctx, base, box);
    }
  }
  ctx.r3.u64 = answer;
}

/// CAcceptHoldBox::Update(box): its lifetime and base update; the hold is Godot's.
REX_EXTERN(rex_AcceptHoldBox_Update) {
  const uint32_t box = ctx.r3.u32;
  if (base[box + kBoxShown] != 0u && base[box + kCardInfinite] == 0u) {
    const uint32_t gui = LoadGuestU32(base, kGuiManager);
    const float frame = Plausible(gui) ? LoadGuestF32(base, gui + kGuiFrameSeconds) : 0.0f;
    const float left = LoadGuestF32(base, box + kBoxTimer) - frame;
    StoreF32(base, box + kBoxTimer, left);
    if (!(left > 0.0f)) {
      Hide(ctx, base, box);
    }
  }
  ctx.r3.u64 = BaseUpdate(ctx, base, box, rex_DisplayBox_Update);
}
