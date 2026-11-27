// refii - the queued boxes' methods that draw, re-expressed without the drawing: Godot draws every
// box (Fable3Boxes), the guest keeps what its methods do to the game. Each body here REPLACES its
// guest function (no __imp__ call) and is the function's own instructions less its element work; every
// method is the only slot of one vtable (TU3 / disc, read word by word), so no other class reaches it.
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
// THE INTERMISSION'S PRELOAD ROW (TU3 sub_821A2C48 / disc sub_821A30E8; reached by two `bl`s alone,
// the row builder TU3 sub_821BD528 at 0x821BD550 / disc sub_821BD5E0 at 0x821BD608 and the keep-list
// TU3 sub_82258B98 at 0x82258CB0 / disc sub_822594E8 at 0x82259600, and by no vtable). The screen
// loader's per-frame pass TU3 sub_821B05A8(L) / disc sub_821B05B0 (L = *(GUI+0x38), a screen's slot
// at L + 4*(id+2)) walks the builder's vector of {id, state} rows; this function pushes the row of
// screen 3, IntermissionBox.bgf: state 1 (`li r31,1` 0x821A2C5C / 0x821A30FC), cleared to 0 while the
// second box queue's showing box (`lwz r11,0x3C(r29)` 0x821A2C7C / disc `lwz r11,0x34(r29)`
// 0x821A311C, r29 = BM) is of type 5 (`addi r11,r10,-5` .. `and r31,r7,r31` 0x821A2CB4..0x821A2CC0 /
// 0x821A3154..0x821A3160), then `{3, state}` through the vector's push (`li r11,3` 0x821A2CC4, `bl
// sub_826D5288` 0x821A2CD8 / `bl sub_821BD4F8` 0x821A3178). The pass loads an empty slot on state 0
// alone (`cmpwi cr6,r11,0` / `bne` 0x821B0744..0x821B0748, `bl sub_825F26E8` 0x821B0750 / disc
// 0x821B074C..0x821B0750, `bl sub_825C79A0` 0x821B0758): that load is what made the intermission's
// presentation, whatever the box's methods above no longer do. Godot draws the intermission, so
// the row's state is 1, always: the pass turns it to 2 for screen 3 (`cmpwi cr6,r11,1` / `li
// r11,2` 0x821B063C..0x821B0650 / disc 0x821B0644..0x821B0658), which only hides a loaded slot and
// does nothing to an empty one, and the keep-list copies no name for it (state 0 only, 0x82258CF0).
// Each row is walked on its own, so no other screen's row changes; the box's queue, show gate
// (sub_826230C0) and lifetime (the update above) read no presentation.
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
//
// THE KING'S SCHEDULE (CKingsScheduleBox, TU3 vtable 0x820C6658 / disc 0x820C30E0, slot 1 Show
// sub_829BBA40 / disc sub_82972FD8 at +4; the two bodies are the same instruction for instruction,
// and nothing calls the Show but the vtable). The guest's Show does EVERYTHING behind its
// presentation (`bl sub_829588C8` / `beq` 0x829BBA50..0x829BBA5C / 0x82972FE8..0x82972FF4) — `+12 = 1`
// and BoxShown included (0x829BBCF4 / 0x829BBCF8, disc 0x8297328C / 0x82973290) — and behind a live
// hero (`bl sub_823BB518` on `*(*(*(*(GAME+0xC)+0x1C)+4)+4)`, `lbz r11,0x94(r3)` bit 0x40,
// 0x829BBA60..0x829BBAA4 / disc sub_82399658 0x82972FF8..0x8297303C). What it writes, all dropped here
// and STATED instead, so the presentation is never made for this box:
//   - the title by tag: `*dword_8359D398` when the hero's component 13 has +0xC == 2 (`cmpwi r11,2`
//     0x829BBB78 / 0x82973110), else `*dword_8359D39C` (disc dword_8355C58C / dword_8355C590), set on
//     `KingsSchedule.Title` (0x829BBBA0 / 0x82973138);
//   - the day: `+0x1C` (`lwz r10,0x1C(r28)` / `cmpwi r10,-1` 0x829BBBA4..0x829BBBA8 / 0x8297313C..
//     0x82973140); for a day the text of tag `*dword_8359D3A0` (disc dword_8355C594) with its `%1`
//     (word_82079620) replaced by the number (sub_82312870, sub_82304950, sub_82DA5198,
//     0x829BBBD0..0x829BBBFC) on `KingsSchedule.SubTitle` (0x829BBC20 / 0x829731B8); for -1 three
//     elements hidden and the subtitle emptied (0x829BBC3C..0x829BBC88);
//     `@Screen.Schedule(box, title tag, day tag, day)`;
//   - rows 1..4 through sub_829BBD90(box, row, tick, tag) / disc sub_82973328 (its one caller,
//     0x829BBCD8 / 0x82973270): the list `+0x24..+0x28` of 8-byte {tag, tick} (`lwz r6,-8(r11)` /
//     `lwz r5,-4(r11)` 0x829BBCC0..0x829BBCC4), tick 4 and tag 0 past its end (0x829BBCCC..0x829BBCD0).
//     The row writer sets the tag and the tick slide behind the presentation and, for tick 2, plays
//     SE_GUI_STAMP (`cmpwi r28,2` 0x829BBE5C / 0x829733F4, `dword_8359BF8C` / disc dword_8355BB8C,
//     `bl sub_823333B0` 0x829BBE78 / 0x82973410): `@Screen.ScheduleRow(box, row, tick, tag)`, and the
//     cue is still the guest's call (kept: Godot has no Fable 3 cue player yet).
// Kept as the guest has it: the live-hero test, `+12 = 1`, BoxShown, the answer 1. Hide sub_829BBD08 /
// disc sub_829732A0 (`+12 = 0`, `+16 = 0.75`, BoxHidden, the list emptied) and the base update draw
// nothing and stay the guest's; the input is gated (display_box_hooks.cpp), and its `+12 = 0` / Hide
// make no presentation either.
//
// THE CHAT CHANNEL (COrbChatBox, TU3 vtable 0x820C64CC / disc 0x820C2F58: slot 1 Show sub_829BADE0 /
// disc sub_82972408 at +4, slot 5 Update sub_829BB200 / disc sub_82972798 at +0x14; each the one
// word of its vtable, the Show and its content writer the same instruction for instruction on both).
//   - Show: `+12 = 1` (0x829BADFC / 0x82972424), BoxShown (0x829BAE00 / 0x82972428), the working byte
//     `+0x24` = the saved mute `VOICE[40 * U + 0x20]`, VOICE = `*(*(NET+0x30)+0x10)`, NET =
//     *0x835979F8 / disc *0x83556EF8, U = dword_832DF7BC / disc dword_8329ED38 (`lbz r5,0x20(r6)` /
//     `stb r5,0x24(r30)` 0x829BAE30..0x829BAE34 / 0x82972458..0x8297245C), the HUD root's hide
//     sub_824CD7E8(*(GUI+0x20)) (0x829BAE50 / 0x82972478), `+0x25 = 1` (0x829BAE58 / 0x82972480), the
//     answer 1 — all kept. Dropped: the gamer picture sub_829BAE78 / disc sub_829724A0 (0x829BAE38: a
//     lookup, a download request and a visibility toggle, for a texture only the guest's own element
//     would show) and the content writer sub_829BB518 / disc sub_82972AB0 (0x829BAE40), whose every
//     write is behind the presentation (`bl sub_829588C8` / `beq` 0x829BB52C..0x829BB534): two slides
//     2 muted / 1 audible from `+0x24` (0x829BB55C / 0x829BB570), the SAVED setting's tag —
//     `*dword_8359D384` muted, `*dword_8359D388` audible (0x829BB59C..0x829BB5B8; disc dword_8355C578 /
//     dword_8355C57C) — and the working value's description, `*dword_8359D38C` muted /
//     `*dword_8359D390` audible (0x829BB5CC..0x829BB5E8; disc dword_8355C580 / dword_8355C584). Stated
//     instead: `@Screen.Chat(box, saved muted, saved tag, muted description tag, audible description
//     tag)`. The working value after the Show is Godot's.
//   - Update: the picture again (0x829BB214 / disc 0x829727AC), dropped; the answer `+0x25`
//     (`lbz r3,0x25(r31)` 0x829BB218), kept.
// Hide sub_829BB0A8 / disc sub_829726D0 and TU3's own force-close sub_829BB170 call no presentation
// (`+12 = 0`, `+16`, BoxHidden, the entity reference dropped, `+0x25 = 0`, the Hide's cue, the HUD
// root's show) and stay the guest's; the input is gated and its accept path (72) stores the mute,
// applies it and hides without a presentation.

#include <atomic>
#include <cstdint>
#include <cstring>
#include <initializer_list>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "gui_statements.h"
#include "mod_seam.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(rex_DisplayBox_Shown);
REX_EXTERN(rex_DisplayBox_Hidden);
REX_EXTERN(rex_DisplayBox_Update);
REX_EXTERN(rex_InfoBox_Update);
REX_EXTERN(rex_HudSlides_Toggle);
REX_EXTERN(rex_PlayerManager_Hero);
REX_EXTERN(rex_Hud_Hide);
REX_EXTERN(rex_GuiSound_Play);
REX_EXTERN(rex_SoundHandle_Release);

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

/// The king's schedule's statics its Show reads: the two title tags (FNV-1 words), the day line's
/// tag, and the stamp cue its row writer plays.
const refii::guest::Addr kScheduleTitleTagA{0x8359D398u};
const refii::guest::Addr kScheduleTitleTagB{0x8359D39Cu};
const refii::guest::Addr kScheduleDayTag{0x8359D3A0u};
const refii::guest::Addr kScheduleStampCue{0x8359BF8Cu};
constexpr uint32_t kScheduleDay = 0x1Cu;
constexpr uint32_t kScheduleListBegin = 0x24u;
constexpr uint32_t kScheduleListEnd = 0x28u;
constexpr uint32_t kScheduleTickStamped = 2u;
constexpr uint32_t kScheduleTickNone = 4u;
/// Component 13 (its +0xC == 2 picks the first title).
constexpr uint32_t kComponentGender = 13u;

/// The chat channel's statics and the globals its Show reads: the saved setting's two tags, the
/// working value's two descriptions; the network object and the active user.
const refii::guest::Addr kChatSavedMutedTag{0x8359D384u};
const refii::guest::Addr kChatSavedAudibleTag{0x8359D388u};
const refii::guest::Addr kChatMutedDescriptionTag{0x8359D38Cu};
const refii::guest::Addr kChatAudibleDescriptionTag{0x8359D390u};
const refii::guest::Addr kNetwork{0x835979F8u};
const refii::guest::Addr kActiveUser{0x832DF7BCu};
constexpr uint32_t kChatWorking = 0x24u;
constexpr uint32_t kChatOpen = 0x25u;

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

/// The preload pass's row for screen 3 (r3 = the row vector): `{3, 1}`, not needed, pushed through
/// the guest's own vector push; the guest's test of the second queue's showing box is dropped.
REX_EXTERN(rex_ScreenPreload_IntermissionRow) {
  constexpr uint32_t kIntermissionScreen = 3u;
  constexpr uint32_t kNotNeeded = 1u;
  const uint32_t rows = ctx.r3.u32;
  PPCContext work = refii::fable3::seam::Frame(ctx);
  const uint32_t row = work.r1.u32 + 0x100u;
  refii::fable3::seam::StoreU32(base, row, kIntermissionScreen);
  refii::fable3::seam::StoreU32(base, row + 4u, kNotNeeded);
  work.r3.u64 = rows;
  work.r4.u64 = row;
  const bool pushed = refii::fable3::seam::CallAt(work, base, refii::guest::Pick(0x826D5288u, 0x821BD4F8u));
  static std::atomic<bool> logged{false};
  if (!logged.exchange(true)) {
    if (pushed) {
      REXLOG_INFO("Refii display boxes: the preload pass's intermission row answered {{3, not needed}} (first)");
    } else {
      REXLOG_WARN("Refii display boxes: the preload rows' push is not a recompiled function; screen 3 has no row");
    }
  }
  ctx.r3.u64 = work.r3.u64;
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

/// CKingsScheduleBox::Show(box) without its presentation: the title, day and rows stated.
REX_EXTERN(rex_KingsSchedule_Show) {
  using refii::fable3::Hex;
  using refii::fable3::Number;
  using refii::fable3::Statement;
  using refii::fable3::Text;
  const uint32_t box = ctx.r3.u32;
  ctx.r3.u64 = 1u;
  static std::atomic<bool> said{false};
  if (!said.exchange(true)) {
    REXLOG_INFO("[fable3-boxes] king's schedule: its Show is replaced (stated, no presentation made)");
  }

  uint32_t at = LoadGuestU32(base, kGame);
  for (uint32_t offset : {0xCu, 0x1Cu, 4u, 4u}) {
    at = Plausible(at) ? LoadGuestU32(base, at + offset) : 0u;
  }
  PPCContext hero_call = refii::fable3::seam::Frame(ctx);
  hero_call.r3.u64 = at;
  rex_PlayerManager_Hero(hero_call, base);
  const uint32_t hero = hero_call.r3.u32;
  if (!refii::fable3::seam::Live(base, hero)) {
    return;
  }

  const uint32_t gender = refii::fable3::seam::Component(base, hero, kComponentGender);
  const bool first = gender != 0u && LoadGuestU32(base, gender + 0xCu) == 2u;
  Statement("@Screen.Schedule")
      .Add(Text(Hex(box)))
      .Add(Text(Hex(LoadGuestU32(base, first ? kScheduleTitleTagA : kScheduleTitleTagB))))
      .Add(Text(Hex(LoadGuestU32(base, kScheduleDayTag))))
      .Add(Number(static_cast<float>(static_cast<int32_t>(LoadGuestU32(base, box + kScheduleDay)))))
      .Post();

  const uint32_t list = LoadGuestU32(base, box + kScheduleListBegin);
  const int32_t listed = static_cast<int32_t>(LoadGuestU32(base, box + kScheduleListEnd) - list) >> 3;
  for (int32_t row = 1; row <= 4; ++row) {
    const bool has = row <= listed;
    const uint32_t tag = has ? LoadGuestU32(base, list + 8u * static_cast<uint32_t>(row - 1)) : 0u;
    const uint32_t tick = has ? LoadGuestU32(base, list + 8u * static_cast<uint32_t>(row - 1) + 4u) : kScheduleTickNone;
    Statement("@Screen.ScheduleRow")
        .Add(Text(Hex(box)))
        .Add(Number(static_cast<float>(row)))
        .Add(Number(static_cast<float>(static_cast<int32_t>(tick))))
        .Add(Text(Hex(tag)))
        .Post();
    if (tick == kScheduleTickStamped) {
      PPCContext work = refii::fable3::seam::Frame(ctx);
      const uint32_t handle = work.r1.u32 + 0x100u;
      work.r3.u64 = handle;
      work.r4.u64 = LoadGuestU32(base, kGuiManager);
      work.r5.u64 = kScheduleStampCue;
      rex_GuiSound_Play(work, base);
      work.r3.u64 = handle;
      rex_SoundHandle_Release(work, base);
    }
  }

  base[box + kBoxShown] = 1u;
  PPCContext work = refii::fable3::seam::Frame(ctx);
  work.r3.u64 = box;
  rex_DisplayBox_Shown(work, base);
}

/// COrbChatBox::Show(box) without its picture and its content writer: the saved mute stated.
REX_EXTERN(rex_OrbChatBox_Show) {
  using refii::fable3::Boolean;
  using refii::fable3::Hex;
  using refii::fable3::Statement;
  using refii::fable3::Text;
  const uint32_t box = ctx.r3.u32;
  static std::atomic<bool> said{false};
  if (!said.exchange(true)) {
    REXLOG_INFO("[fable3-boxes] chat channel: its Show and Update are replaced (stated, no presentation made)");
  }
  base[box + kBoxShown] = 1u;
  {
    PPCContext work = refii::fable3::seam::Frame(ctx);
    work.r3.u64 = box;
    rex_DisplayBox_Shown(work, base);
  }
  const uint32_t network = LoadGuestU32(base, kNetwork);
  const uint32_t voice = LoadGuestU32(base, LoadGuestU32(base, network + 0x30u) + 0x10u);
  const uint8_t saved = base[voice + 40u * LoadGuestU32(base, kActiveUser) + 0x20u];
  base[box + kChatWorking] = saved;
  Statement("@Screen.Chat")
      .Add(Text(Hex(box)))
      .Add(Boolean(saved != 0u))
      .Add(Text(Hex(LoadGuestU32(base, saved != 0u ? kChatSavedMutedTag : kChatSavedAudibleTag))))
      .Add(Text(Hex(LoadGuestU32(base, kChatMutedDescriptionTag))))
      .Add(Text(Hex(LoadGuestU32(base, kChatAudibleDescriptionTag))))
      .Post();
  {
    PPCContext work = refii::fable3::seam::Frame(ctx);
    work.r3.u64 = LoadGuestU32(base, LoadGuestU32(base, kGuiManager) + kGuiHud);
    rex_Hud_Hide(work, base);
  }
  base[box + kChatOpen] = 1u;
  ctx.r3.u64 = 1u;
}

/// COrbChatBox::Update(box) without its picture: the answer `+0x25`.
REX_EXTERN(rex_OrbChatBox_Update) {
  ctx.r3.u64 = base[ctx.r3.u32 + kChatOpen];
}
