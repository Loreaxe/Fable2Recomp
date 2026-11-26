// refii - Fable 3's options: the values the running game is given, stated at the appliers that
// take them and at the player's saved settings' read and write, into the table the host bridge's
// options feed reads (core/payload_bridge.h, src/runtime/src/core/stated_options.cpp); and the
// Options screen, whose pad Godot owns — Godot's actions run through the screen's own handler,
// whose state is stated as it returns.
//
// Every address is the Fable 3 TU3 IDB ("disc" marks one TU3 lacks); each offset names its base.
//
// THE APPLIERS (each `r3` the audio object *0x835974DC):
//   - sfx sub_82D4F758(A, f1): clamps to 0..1 (0x82D4F7A4) and stores A+88 (0x82D4F7AC);
//   - voice sub_82D4F800(A, f1): clamps (0x82D4F838..0x82D4F850), stores A+92 (0x82D4F854);
//   - music sub_82D4F980(A, f1): clamps (0x82D4F9B8..0x82D4F9D0), stores A+100 (0x82D4F9D4);
//   - speaker sub_82D4FA28(A, r4 mode): 0 TV, 1 HIFI, 2 SURROUND, stored A+104 (0x82D4FAD4).
//   Their callers: the Options screen's live step and commit, its revert, the sign-in
//   sub_825E36A0, the boot sub_82318D30 and the SoundTools natives.
//   - gamma has no applier that takes a value: sub_825F8148(box) applies the Options screen's own
//     +128 (`lfs` at 0x825F8168; stores at 0x825F81BC / 0x825F81CC), the only field it reads; the
//     profile's is applied from sub_822F6018(user)'s answer (f1) by the display object's ctor
//     sub_824F19F8 (0x824F1A68), the sign-in sub_825E36A0 (0x825E37BC, `fmr f31,f1` at
//     0x825E37C8) and the revert sub_825F8250 (0x825F8268); the Options screen's fill
//     sub_825F8028 hands it to the screen instead (0x825F80A4, returning at 0x825F80A8).
// THE SAVED SETTINGS, title setting 0x63E83FFF, 92 bytes: read by sub_822F78C8(user, out)
// (answers 1 and copies the blob at 0x822F7940), written by sub_822F62B8(user, r4 subtitles,
// f1 gamma, r6 &{sfx, music, voice, speaker}) (`mr r30,r4` / `fmr f31,f1` / `mr r29,r6` at
// 0x822F62CC..0x822F62D8), which lays them at blob +8 (0x822F634C), +4 (0x822F6360), +12, +16,
// +20, +24 (0x822F63B4..0x822F63CC).
//
// THE OPTIONS SCREEN (COptionsMenuBox, vtable 0x820A0B10, ctor disc sub_825C95F0, opened by
// sub_825F33C0): its pad handler sub_825F4F30(box, ev*, player) (slot 4, reached only through
// the box manager's router sub_825F38F8) takes 29/30 up/down (sub_825F54A0 / sub_825F5378,
// 0x825F51DC / 0x825F5230), 31/32 left/right (sub_825F5B48 / sub_825F55B8, 0x825F524C /
// 0x825F5260), 74 A (sub_825F60A8, 0x825F5368), 75 B (sub_825F66D8, 0x825F5330), 77 Y (the
// defaults, 0x825F529C..0x825F5300), and 74/75 answer its confirm query while +30 is set
// (0x825F50CC..0x825F5184). The steps write the screen's own fields and re-run its row writer,
// which applies gamma and the volumes live (sub_825F8148, sub_825F7E18 -> the four appliers);
// A on a settings page commits through the appliers and the save sub_822F62B8 (sub_825F8350).
// The screen's state, the fields its handler and Show wrote: +80 page (1 Options, 2 Save
// Management, 3 Game Settings, 4 the front end's, 5 Multiplayer), +84/+88/+92/+96 the selected
// row of pages 1 and 5 / 2 / 3 / 4, +31 invert aim, +32 subtitles, +33 tutorial, +104
// breadcrumb 0..4, +100 speaker, +64 language index, +60 language count, +108 the multiplayer
// row (1 host, 2 joined), +112 why Load is blocked (0 it is not, 1 no saves, 2 not now: it picks
// the row's description, sub_825F69B8 `lwz r11,0x70(r31)` 0x825F69E8), +34 opened from the pause,
// +37 the front end's variant, +36 save allowed, +30 the query up, +38 changed since open.
// A ROW FROM OUTSIDE (Godot's pointer: `options-row:<row>[:accept]`). Nothing of the screen's is
// written here: the row reaches it through the guest's own functions. The handler's steps are the
// contract (down sub_825F5378, up sub_825F54A0; disc sub_825CA858 / sub_825CA980, the same
// instruction for instruction):
//   - pages 1 and 5 hand the new row to sub_825F68A0(box, row) (`addi r4,r11,1; mr r3,r31; bl`
//     0x825F5458..0x825F5460; disc sub_825CBC68 at 0x825CA940), which stores +84 (`stw r4,0x54(r3)`
//     0x825F68B8) and writes the row's description; rows 1..3, or ..4 while +108 is set
//     (`lwz r10,0x6C(r31)` .. `addi r6,r10,3; cmpw r11,r6; bge` 0x825F5438..0x825F5454). With the
//     demo mode *0x83596F48 (Lua Global.GetDemoMode, 0x822DEA24) at 12 or above only page 5 moves,
//     between rows 2 and 4 (`cmpwi r10,0xC; blt` / `cmpwi r11,5; bne` / `li r4,4` 0x825F5420..0x825F5430,
//     up `li r11,2; stw r11,0x54(r31)` 0x825F5558);
//   - page 2 hands it to sub_825F69B8(box, row) (0x825F53CC..0x825F53D0; disc sub_825CBD80), which
//     takes 1 and 2 alone (`cmpwi r4,1` / `cmpwi r4,2; bne` 0x825F69CC..0x825F69D8);
//   - pages 3 and 4 have no function that takes a row: the step moves the row by one, +92 within
//     1..9 and +96 within 1..7 (0x825F53E0..0x825F5410), and only when the screen's update count
//     +56 is more than four past the last move's (+44 down, +40 up: `lwz r11,0x2C(r3); lwz
//     r10,0x38(r3); addi r8,r11,4; cmpw r10,r8; ble` 0x825F5388..0x825F53A0; the update adds one a
//     tick, `addi r9,r11,1; stw r9,0x38(r31)` 0x825F4A88..0x825F4A98). A refused step changes
//     nothing (the branch leaves before any store).
// So a stated row is, on pages 1, 5 and 2, the guest's own setter with that row, then its row
// writer sub_825F4DE8(box) (disc sub_825CA2C8; what the update runs for a move, 0x825F4AF8) so the
// page is redrawn at once; and on pages 3 and 4 a target the screen is STEPPED to through its own
// handler (29 / 30), one try per update after the guest's own update, so each step lands on the
// first update the gate allows — five updates a row, the rate of a held stick, the fastest the
// guest moves that cursor. `:accept` runs the handler with 74 once the cursor is on the row. Any
// pad action, a page change or 150 updates drops the target.
//
// THE PLAYER. The handler takes the pad of the screen's own hero alone: box+0x88 holds the hero, its
// component 37 (`cmpwi r6,0x25` in the component walk from 0x825F5020) +0x14 its player record,
// whose +0x24 is compared with r5 (`lwz r11,0x24(r11)` / `cmpw r11,r26` / `beq` 0x825F50AC..0x825F50BC;
// disc 0x825CA598), answering 0 for any other; the A handler gets the same r5 (`mr r4,r26` 0x825F5360).
// So each lever names the player who pressed (`options:<p>:<action>`, `options-row:<p>:<row>`), the
// 1 or 2 the GUI input object answers for that pad (display_box_hooks.cpp), and hands it in r5.
//
// WHETHER LOAD IS OFFERED differs by build: TU3 keeps it in +192 (set by sub_825F6AF8 0x825F6BEC /
// 0x825F6C94, read by the ghosting sub_825F6CA8 0x825F6D10 and A 0x825F60E8), the disc tests +112
// == 0 (disc Show disc 0x825C9A40, A disc 0x825CB5C8). TU3 also recomputes +36/+112/+192 while the
// screen is up (its update, slot 5, on a save-list change: 0x825F4C80..0x825F4C9C), stated there.

#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <string>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "box_answers.h"
#include "core/payload_bridge.h"
#include "gui_statements.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_Audio_SetSfxVolume);
REX_EXTERN(__imp__rex_Audio_SetVoiceVolume);
REX_EXTERN(__imp__rex_Audio_SetMusicVolume);
REX_EXTERN(__imp__rex_Audio_SetSpeakerMode);
REX_EXTERN(__imp__rex_Options_ApplyGamma);
REX_EXTERN(__imp__rex_Profile_ReadGamma);
REX_EXTERN(__imp__rex_Profile_ReadSettings);
REX_EXTERN(__imp__rex_Profile_WriteSettings);
REX_EXTERN(__imp__rex_OptionsBox_Show);
REX_EXTERN(__imp__rex_OptionsBox_HandleInput);
REX_EXTERN(__imp__rex_OptionsBox_Update);
REX_EXTERN(rex_OptionsBox_HandleInput);
REX_EXTERN(rex_OptionsBox_SetListRow);
REX_EXTERN(rex_OptionsBox_SetSaveRow);
REX_EXTERN(rex_OptionsBox_Redraw);

namespace {

using namespace refii::fable3;

/// The Options screen's fill hands the profile's gamma to the screen, not to the game.
const refii::guest::Addr kGammaToScreenReturn{0x825F80A8u};

constexpr uint32_t kBlobGamma = 4u;
constexpr uint32_t kBlobSubtitles = 8u;
constexpr uint32_t kBlobSfx = 12u;
constexpr uint32_t kBlobMusic = 16u;
constexpr uint32_t kBlobVoice = 20u;
constexpr uint32_t kBlobSpeaker = 24u;

constexpr uint32_t kScreenGamma = 128u;

/// The screen's fields the row lever reads (see the header's "A ROW FROM OUTSIDE").
constexpr uint32_t kScreenPage = 80u;
constexpr uint32_t kScreenListRow = 84u;
constexpr uint32_t kScreenSaveRow = 88u;
constexpr uint32_t kScreenSettingsRow = 92u;
constexpr uint32_t kScreenLiteRow = 96u;
constexpr uint32_t kScreenMultiplayer = 108u;

/// Global.GetDemoMode's value; 12 and above leave only page 5's rows 2 and 4.
const refii::guest::Addr kDemoMode{0x83596F48u};
constexpr uint32_t kDemoModeLocked = 12u;

/// The actions the screen's handler takes (see the header).
constexpr long kActionUp = 29;
constexpr long kActionDown = 30;
constexpr long kActionRight = 32;
constexpr long kActionA = 74;
constexpr long kActionB = 75;
constexpr long kActionY = 77;

float Clamp01(double value) { return value < 0.0 ? 0.0f : value > 1.0 ? 1.0f : static_cast<float>(value); }

uint32_t g_screen = 0u;         // the Options screen its Show last stated
uint32_t g_screen_player = 0u;  // the player the router last handed its handler (for the log)
std::mutex g_mutex;
std::string g_last_state;

/// `@Options.State(box, page, row, invert, subtitles, tutorial, breadcrumb, speaker, language,
/// languages, flag108, flag112, fromPause, frontEnd, canSave, query, changed, canLoad)` — when it differs
/// from the last.
void StateScreen(const uint8_t* base, uint32_t box) {
  if (base == nullptr || !Plausible(box)) {
    return;
  }
  const uint32_t page = LoadGuestU32(base, box + 80u);
  const uint32_t row_at = page == 2u ? 88u : page == 3u ? 92u : page == 4u ? 96u : 84u;
  const uint32_t words[] = {
      page,
      LoadGuestU32(base, box + row_at),
      base[box + 31u],
      base[box + 32u],
      base[box + 33u],
      LoadGuestU32(base, box + 104u),
      LoadGuestU32(base, box + 100u),
      LoadGuestU32(base, box + 64u),
      LoadGuestU32(base, box + 60u),
      LoadGuestU32(base, box + 108u),
      LoadGuestU32(base, box + 112u),
      base[box + 34u],
      base[box + 37u],
      base[box + 36u],
      base[box + 30u],
      base[box + 38u],
      refii::guest::Pick(static_cast<uint32_t>(base[box + 192u]), LoadGuestU32(base, box + 112u) == 0u ? 1u : 0u),
  };
  std::string key = Hex(box);
  for (uint32_t word : words) {
    key += ':' + std::to_string(word);
  }
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    if (key == g_last_state) {
      return;
    }
    g_last_state = key;
  }
  Statement state("@Options.State");
  state.Add(Text(Hex(box)));
  for (uint32_t word : words) {
    state.Add(Number(static_cast<float>(static_cast<int32_t>(word))));
  }
  state.Post();
}

thread_local bool t_host_action = false;

/// The row the pointer stated on a stepped page (3 or 4), 0 for none; whether to accept there;
/// the updates left before it is dropped; the player who stated it. Guest tick thread only.
long g_row_target = 0;
bool g_row_accept = false;
int g_row_updates = 0;
uint32_t g_row_player = 0u;

/// The player field of a lever, `<p>:` with p 1 or 2, at `at`; the text after it, or null.
const char* TakePlayer(const char* at, uint32_t& player) {
  if ((at[0] != '1' && at[0] != '2') || at[1] != ':') {
    return nullptr;
  }
  player = static_cast<uint32_t>(at[0] - '0');
  return at + 2;
}
constexpr int kRowUpdates = 150;

PPCContext Frame(const PPCContext& ctx, uint32_t bytes = 0x200u) {
  PPCContext work = ctx;
  work.r1.u32 = (ctx.r1.u32 - bytes) & ~0xFu;
  return work;
}

void StoreGuestU32(uint8_t* base, uint32_t address, uint32_t value) {
  const uint32_t swapped = __builtin_bswap32(value);
  std::memcpy(base + address, &swapped, sizeof(swapped));
}

}  // namespace

namespace refii::fable3 {

bool AnswerOptionsAction(PPCContext& ctx, uint8_t* base, const char* action) {
  const uint32_t box = g_screen;
  if (base == nullptr || box == 0u || !BoxIsShowing(box)) {
    REXLOG_WARN("[fable3-options] '{}' with no Options screen showing; dropped", action);
    return false;
  }
  uint32_t player = 0u;
  const char* rest = TakePlayer(action + sizeof(kOptionsAction) - 1u, player);
  if (rest == nullptr) {
    REXLOG_WARN("[fable3-options] '{}' names no player; dropped", action);
    return false;
  }
  const long id = std::strtol(rest, nullptr, 10);
  g_row_target = 0;
  if (!((id >= kActionUp && id <= kActionRight) || id == kActionA || id == kActionB || id == kActionY)) {
    return false;
  }
  if (player != g_screen_player) {
    REXLOG_INFO("[fable3-options] '{}' as player {} (the guest's router last handed this screen player {})", action,
                player, g_screen_player);
  }
  PPCContext work = Frame(ctx);
  const uint32_t event = work.r1.u32 + 0x100u;
  StoreGuestU32(base, event, static_cast<uint32_t>(id));
  StoreGuestU32(base, event + 4u, 0u);
  StoreGuestU32(base, event + 8u, 0u);
  StoreGuestU32(base, event + 12u, 0u);
  work.r3.u64 = box;
  work.r4.u64 = event;
  work.r5.u64 = player;
  t_host_action = true;
  rex_OptionsBox_HandleInput(work, base);
  t_host_action = false;
  return true;
}

namespace {

/// One game action into the showing screen's handler for `player`, as Godot's own (`t_host_action`).
void RunHandler(PPCContext& ctx, uint8_t* base, uint32_t box, long id, uint32_t player) {
  PPCContext work = Frame(ctx);
  const uint32_t event = work.r1.u32 + 0x100u;
  StoreGuestU32(base, event, static_cast<uint32_t>(id));
  StoreGuestU32(base, event + 4u, 0u);
  StoreGuestU32(base, event + 8u, 0u);
  StoreGuestU32(base, event + 12u, 0u);
  work.r3.u64 = box;
  work.r4.u64 = event;
  work.r5.u64 = player;
  t_host_action = true;
  rex_OptionsBox_HandleInput(work, base);
  t_host_action = false;
}

/// One update's try at the stated row of a stepped page: the handler's own step toward it (its
/// gate decides), or, on it, the accept.
void StepTowardRow(PPCContext& ctx, uint8_t* base, uint32_t box) {
  if (g_row_target == 0) {
    return;
  }
  const uint32_t page = LoadGuestU32(base, box + kScreenPage);
  if ((page != 3u && page != 4u) || --g_row_updates < 0 || !BoxIsShowing(box)) {
    g_row_target = 0;
    return;
  }
  const long row = static_cast<int32_t>(LoadGuestU32(base, box + (page == 3u ? kScreenSettingsRow : kScreenLiteRow)));
  if (row == g_row_target) {
    const bool accept = g_row_accept;
    g_row_target = 0;
    if (accept) {
      RunHandler(ctx, base, box, kActionA, g_row_player);
    }
    return;
  }
  RunHandler(ctx, base, box, row < g_row_target ? kActionDown : kActionUp, g_row_player);
}

}  // namespace

bool AnswerOptionsRow(PPCContext& ctx, uint8_t* base, const char* action) {
  const uint32_t box = g_screen;
  if (base == nullptr || box == 0u || !BoxIsShowing(box)) {
    REXLOG_WARN("[fable3-options] '{}' with no Options screen showing; dropped", action);
    return false;
  }
  uint32_t player = 0u;
  const char* rest = TakePlayer(action + sizeof(kOptionsRowAction) - 1u, player);
  if (rest == nullptr) {
    REXLOG_WARN("[fable3-options] '{}' names no player; dropped", action);
    return false;
  }
  char* end = nullptr;
  const long row = std::strtol(rest, &end, 10);
  const bool accept = end != nullptr && std::strcmp(end, ":accept") == 0;
  const uint32_t page = LoadGuestU32(base, box + kScreenPage);
  const uint32_t row_at = page == 2u ? kScreenSaveRow : page == 3u ? kScreenSettingsRow :
                          page == 4u ? kScreenLiteRow : kScreenListRow;
  const long was = static_cast<int32_t>(LoadGuestU32(base, box + row_at));
  long last = 0;
  switch (page) {
    case 1u:
    case 5u:
      if (LoadGuestU32(base, kDemoMode) >= kDemoModeLocked) {
        last = page == 5u && (row == 2 || row == 4) ? row : 0;
      } else {
        last = LoadGuestU32(base, box + kScreenMultiplayer) != 0u ? 4 : 3;
      }
      break;
    case 2u:
      last = 2;
      break;
    case 3u:
      last = 9;
      break;
    case 4u:
      last = 7;
      break;
    default:
      break;
  }
  // A hover that restates the row a click is being stepped to keeps the click's accept.
  const bool pending_accept = g_row_target == row && g_row_accept;
  g_row_target = 0;
  if (row < 1 || row > last) {
    REXLOG_WARN("[fable3-options] '{}' is no row of page {}; dropped", action, page);
    return false;
  }
  if (page == 3u || page == 4u) {
    // Stepped: the update's wrapper tries a step each update (StepTowardRow).
    g_row_target = row;
    g_row_accept = accept || pending_accept;
    g_row_updates = kRowUpdates;
    g_row_player = player;
    return true;
  }
  if (row != was) {
    PPCContext work = Frame(ctx);
    work.r3.u64 = box;
    work.r4.u64 = static_cast<uint32_t>(row);
    if (page == 2u) {
      rex_OptionsBox_SetSaveRow(work, base);
    } else {
      rex_OptionsBox_SetListRow(work, base);
    }
    PPCContext redraw = Frame(ctx);
    redraw.r3.u64 = box;
    rex_OptionsBox_Redraw(redraw, base);
    StateScreen(base, box);
  }
  if (accept) {
    RunHandler(ctx, base, box, kActionA, player);
  }
  return true;
}

}  // namespace refii::fable3

// (A, f1).
REX_EXTERN(rex_Audio_SetSfxVolume) {
  const float value = Clamp01(ctx.f1.f64);
  __imp__rex_Audio_SetSfxVolume(ctx, base);
  refii_bridge_option_applied(REFII_OPTION_SFX, value);
}

// (A, f1).
REX_EXTERN(rex_Audio_SetVoiceVolume) {
  const float value = Clamp01(ctx.f1.f64);
  __imp__rex_Audio_SetVoiceVolume(ctx, base);
  refii_bridge_option_applied(REFII_OPTION_VOICE, value);
}

// (A, f1).
REX_EXTERN(rex_Audio_SetMusicVolume) {
  const float value = Clamp01(ctx.f1.f64);
  __imp__rex_Audio_SetMusicVolume(ctx, base);
  refii_bridge_option_applied(REFII_OPTION_MUSIC, value);
}

// (A, mode).
REX_EXTERN(rex_Audio_SetSpeakerMode) {
  const uint32_t mode = ctx.r4.u32;
  __imp__rex_Audio_SetSpeakerMode(ctx, base);
  refii_bridge_option_applied(REFII_OPTION_SPEAKER, static_cast<float>(mode));
}

// (screen): the applier's one input is the screen's +128.
REX_EXTERN(rex_Options_ApplyGamma) {
  const uint32_t screen = ctx.r3.u32;
  const bool readable = base != nullptr && Plausible(screen);
  const float gamma = readable ? LoadGuestF32(base, screen + kScreenGamma) : 0.0f;
  __imp__rex_Options_ApplyGamma(ctx, base);
  if (readable) {
    refii_bridge_option_applied(REFII_OPTION_GAMMA, gamma);
  }
}

// (user) -> f1: the profile's gamma, applied by every caller but the Options screen's fill.
REX_EXTERN(rex_Profile_ReadGamma) {
  const uint32_t caller = static_cast<uint32_t>(ctx.lr);
  __imp__rex_Profile_ReadGamma(ctx, base);
  const float gamma = static_cast<float>(ctx.f1.f64);
  refii_bridge_option_profiled(REFII_OPTION_GAMMA, gamma);
  if (caller != kGammaToScreenReturn) {
    refii_bridge_option_applied(REFII_OPTION_GAMMA, gamma);
  }
}

// (user, out) -> 1 with the blob copied.
REX_EXTERN(rex_Profile_ReadSettings) {
  const uint32_t out = ctx.r4.u32;
  __imp__rex_Profile_ReadSettings(ctx, base);
  if (base == nullptr || ctx.r3.u32 == 0u || !Plausible(out)) {
    return;
  }
  refii_bridge_option_profiled(REFII_OPTION_GAMMA, LoadGuestF32(base, out + kBlobGamma));
  refii_bridge_option_profiled(REFII_OPTION_SUBTITLES, base[out + kBlobSubtitles] != 0u ? 1.0f : 0.0f);
  refii_bridge_option_profiled(REFII_OPTION_SFX, LoadGuestF32(base, out + kBlobSfx));
  refii_bridge_option_profiled(REFII_OPTION_MUSIC, LoadGuestF32(base, out + kBlobMusic));
  refii_bridge_option_profiled(REFII_OPTION_VOICE, LoadGuestF32(base, out + kBlobVoice));
  refii_bridge_option_profiled(REFII_OPTION_SPEAKER, static_cast<float>(LoadGuestU32(base, out + kBlobSpeaker)));
}

// (user, subtitles, f1 gamma, r6 &{sfx, music, voice, speaker}).
REX_EXTERN(rex_Profile_WriteSettings) {
  const bool subtitles = (ctx.r4.u32 & 0xFFu) != 0u;
  const float gamma = static_cast<float>(ctx.f1.f64);
  const uint32_t levels = ctx.r6.u32;
  if (base != nullptr && Plausible(levels)) {
    refii_bridge_option_profiled(REFII_OPTION_SUBTITLES, subtitles ? 1.0f : 0.0f);
    refii_bridge_option_profiled(REFII_OPTION_GAMMA, gamma);
    refii_bridge_option_profiled(REFII_OPTION_SFX, LoadGuestF32(base, levels));
    refii_bridge_option_profiled(REFII_OPTION_MUSIC, LoadGuestF32(base, levels + 4u));
    refii_bridge_option_profiled(REFII_OPTION_VOICE, LoadGuestF32(base, levels + 8u));
    refii_bridge_option_profiled(REFII_OPTION_SPEAKER, static_cast<float>(LoadGuestU32(base, levels + 12u)));
  }
  __imp__rex_Profile_WriteSettings(ctx, base);
}

// (screen).
REX_EXTERN(rex_OptionsBox_Show) {
  const uint32_t screen = ctx.r3.u32;
  __imp__rex_OptionsBox_Show(ctx, base);
  g_screen = screen;
  g_screen_player = 0u;
  g_row_target = 0;
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    g_last_state.clear();
  }
  StateScreen(base, screen);
}

/// (screen): the screen's per-frame update (slot 5), stated after it: TU3 rewrites the save fields
/// there; on the disc the state repeats and nothing is posted.
REX_EXTERN(rex_OptionsBox_Update) {
  const uint32_t screen = ctx.r3.u32;
  __imp__rex_OptionsBox_Update(ctx, base);
  if (screen == g_screen) {
    if (base != nullptr) {
      StepTowardRow(ctx, base, screen);
    }
    StateScreen(base, screen);
  }
}

/// (screen, ev*, player): Godot owns the showing screen's pad; its actions run here, the
/// screen's state stated after each.
REX_EXTERN(rex_OptionsBox_HandleInput) {
  const uint32_t screen = ctx.r3.u32;
  if (!t_host_action && screen == g_screen && BoxIsShowing(screen)) {
    g_screen_player = ctx.r5.u32;
    ctx.r3.u64 = 1u;
    return;
  }
  __imp__rex_OptionsBox_HandleInput(ctx, base);
  if (screen == g_screen) {
    StateScreen(base, screen);
  }
}
