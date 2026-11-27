// refii - Fable 3's options: the values the running game is given, stated at the appliers that
// take them and at the player's saved settings' read and write, into the table the host bridge's
// options feed reads (core/payload_bridge.h, src/runtime/src/core/stated_options.cpp); and the
// Options screen, whose cursor, working values and query answer are Godot's — every decision and
// consequence stays the guest's, reached through the guest's own functions.
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
//
// THE OPTIONS SCREEN (COptionsMenuBox; vtable 0x820A0B10 / disc 0x8209D230, opened by sub_825F33C0 /
// disc sub_825C8630). TU3 addresses, disc after the slash; every function below is the same
// instruction for instruction on both builds unless it says otherwise (normalised compare, NOTES).
// Box fields (offsets from the box): +12 shown, +30 query up, +31 invert aim, +32 subtitles, +33
// tutorial boxes, +34 opened from the pause, +36 save allowed, +37 the front end's variant, +38
// changed since the page opened, +40 / +44 / +48 the update count at the last up / down / gated
// step, +56 the update count (`addi r9,r11,1; stw r9,0x38(r31)` 0x825F4A98 / 0x825CA004), +60 the
// language count, +64 the language, +72 the save count, +80 the page (1 Options, 2 Save
// Management, 3 Game Settings, 4 the front end's Game Settings, 5 Multiplayer), +84 / +88 / +92 /
// +96 the cursor of pages 1 and 5 / 2 / 3 / 4, +100 speaker 0..2, +104 breadcrumb 0..4, +108 the
// multiplayer role (1 host, 2 joined), +112 why Load is blocked (0 it is not, 1 no saves, 2 not
// now), +116 music, +120 sfx, +124 voice, +128 gamma (0..1), TU3 +192 Load offered.
//
// THE HANDLER sub_825F4F30 / sub_825CA410 (box, ev*, player), slot 4, IS STUBBED: the guest's pad
// never reaches it (rex_OptionsBox_HandleInput answers 1, "consumed", for every action). Its own
// body is still the guest's commit path and is run only by Godot's levers, for the player the
// lever names: its gate takes the box's own hero's player alone (`lwz r11,0x24(r11)` / `cmpw
// r11,r26` 0x825F50AC..0x825F50B8, disc 0x825CA598), after `lbz r11,0xC(r3)` (shown, 0x825F4F3C /
// 0x825CA41C). Its arms, by action:
//   - while +30 (`lbz r11,0x1E(r29)` 0x825F50CC / 0x825CA5AC): 74 = the query's YES, the answer
//     sub_825F6D90 / sub_825CBF20 (0x825F5174 / 0x825CA654) then +30 = 0 (0x825F5178 / 0x825CA658);
//     75 = NO, +30 = 0 (0x825F510C / 0x825CA5EC); nothing else;
//   - 29 / 30 the up / down steps sub_825F54A0 / sub_825F5378 (disc sub_825CA980 / sub_825CA858,
//     0x825F51DC / 0x825F5230): pages 1, 5 and 2 through the row setters below, pages 3 and 4 move
//     +92 within 1..9 / +96 within 1..7 (`stw r11,0x5C(r31)` 0x825F53F0 / 0x825CA8D0) only when +56
//     is more than four past the last step's (`lwz r11,0x2C(r3); lwz r10,0x38(r3)`
//     0x825F5388..0x825F5390 / 0x825CA868..0x825CA870): a refused step changes nothing;
//   - 31 / 32 the left / right setters sub_825F5B48 / sub_825F55B8 (disc sub_825CB028 /
//     sub_825CAA98, 0x825F524C / 0x825F5260): on the page's cursor row (`lwz r11,0x50(r3)`
//     0x825F55C4 / 0x825CAAA4), toggles to 0 / 1 (`stb r29,0x1F(r31)` invert 0x825F5644, `stb
//     r29,0x21(r31)` tutorial 0x825F568C, `stb r29,0x20(r31)` subtitles 0x825F5668), the bars by
//     0.025 clamped to 0..1 (gamma `stfs f9,0x80(r31)` 0x825F5704, music 0x74 0x825F57A8, sfx 0x78
//     0x825F5818, voice 0x7C 0x825F5888), the counts by one and only when +56 is more than four
//     past +48 (`lwz r10,0x30(r3); lwz r11,0x38(r3)` 0x825F55DC..0x825F55E4, `stw r11,0x30(r3)`
//     0x825F55F4: breadcrumb `stw r11,0x68(r31)` 0x825F5730, speaker 0x64 0x825F58B4, language 0x40
//     0x825F5944 within +60; the disc's bodies hold each at the TU3 address less 0x2AB20, e.g. invert
//     0x825CAB24, gamma 0x825CABE4, the gate 0x825CAABC..0x825CAAD4); every step sets +38,
//     then the row writer sub_825F7848 / sub_825F7AA0 applies gamma and the levels live;
//   - 74 the A arm sub_825F60A8 / sub_825CB588 (box, player) (0x825F5368): page 1/5 row 1 Save
//     Management, row 2 Game Settings (the fills below), row 3 the new-game / quit query, row 4 the
//     multiplayer query; page 2 row 1 the save query when +36 (`lbz r11,0x24(r31)` 0x825F6114 /
//     0x825CB5F4), row 2 Load when TU3 +192 (`lbz r11,0xC0(r3)` 0x825F60E8) / disc +112 == 0 (`lwz
//     r11,0x70(r3)` 0x825CB5C8): +35 = 1 (0x825F60FC / 0x825CB5DC) and the hide, Update fires it;
//     page 3 the commit sub_825F70F8 / sub_825CC1E8 (0x825F6144 / 0x825CB624: the hero's component
//     +38 / +40, sub_825F8350 / sub_825CD3F8 (box, user) — the appliers and the saved settings
//     sub_822F62B8 / sub_822F2A50 at 0x825F84CC / 0x825CD574 — and the breadcrumb) and back to page
//     1 or 5; page 4 sub_825F8350(box, user) (0x825F61AC / 0x825CB68C), the language sub_822E8C30 /
//     sub_822E8D70, `"LanguageChanged"`, the hide;
//   - 75 the B arm sub_825F66D8 / sub_825CBAA0 (0x825F5330): pages 3 / 4 with +38 ask (the query),
//     without it revert sub_825F73B0 / sub_825CC4A0 and go back; page 2 back; pages 1 / 5 the hide;
//   - 77 the defaults (0x825F529C..0x825F5300): +31 = 0, +33 = 1, +104 = 2, sub_825F7460 /
//     sub_825CC550, the row writer; 14 the cancel cue and the hide.
// THE ROW SETTERS of pages 1 / 5 and 2: sub_825F68A0 / sub_825CBC68 (box, row) stores +84
// (`stw r4,0x54(r3)` 0x825F68B8) and sub_825F69B8 / sub_825CBD80 (box, row) stores +88 for rows 1 and
// 2 alone (`cmpwi r4,1` / `cmpwi r4,2` 0x825F69CC..0x825F69D8); with the demo mode *0x83596F48
// (Global.GetDemoMode) at 12 or above only page 5 rows 2 and 4 move (0x825F5420..0x825F5430).
// THE FILLS read the player's settings into the box when a settings page opens: page 3
// sub_825F7508 / sub_825CC5F8 (the A arm, 0x825F6310 / 0x825CB7E8), page 4 sub_825F77D8 /
// sub_825CC8C8 (the A arm 0x825F6350 / 0x825CB828, and Show when +37, 0x825F4458 / 0x825C992C).
// THE QUERY sub_825F6D30 / sub_825CBEC0 (box, tag), called only by the A and B arms
// (0x825F612C .. 0x825F687C / 0x825CB60C .. 0x825CBC44), IS RE-EXPRESSED: it states `@Options.Query
// (box, tag)` and keeps its one store, `+30 = 1` (`li r9,1; stb r9,0x1E(r30)` 0x825F6D7C..0x825F6D80 /
// 0x825CBF10). Dropped: the presentation `sub_829588C8` / `sub_8291F4C8` (0x825F6D44 / 0x825CBED4,
// which LOADS a slot when it is empty and gated the store), root state 6 and the question's
// element write — Godot draws the query and owns its answer.
// THE UPDATE sub_825F4958 / sub_825C9EC0 (slot 5) and THE SHOW sub_825F4408 / sub_825C98D8 still run
// as the guest's: Update counts +56 (which the step gates read), plays the step cues, refreshes
// TU3's save fields (0x825F4C74..0x825F4CA8), runs the base update and fires Load; Show fills,
// pauses (sub_82958388) and restricts the hero (sub_82958550). Their element writes stand behind
// the presentation gate and are not read by Godot.
//
// GODOT'S LEVERS (each names the local player p, handed to the handler in r5):
//   - `options:<p>:<74|75|77|14>`: A, B, Y, START through the handler. While the query is up 74
//     and 75 answer it at once; on a settings page 74 (commit) and 75 (back) wait until every
//     working value has reached the box (below), so the guest's commit and its "changed?" read
//     Godot's values;
//   - `options-row:<p>:<row>[:accept]`: on pages 1, 5 and 2 the row setter with the row, then with
//     `:accept` the handler's 74; on pages 3 and 4 the row Godot's cursor is on, which the guest's
//     own cursor is walked to while no value waits;
//   - `options-value:<p>:<page>:<row>:<value>`: Godot's working value of a settings row (a bar 0..1,
//     a toggle 0 / 1, a count), dropped unless the page is the box's. Each update after the guest's own, the box is brought to it by its
//     own steps: the cursor (+92 / +96) one gated step toward the nearest row whose value waits,
//     then on that row the left / right setter until the value is the working one — a bar takes
//     every 0.025 step in one update, a gated count one step per five updates. Nothing is
//     written to the box but by those functions.
// STATED: `@Options.State(box, page, row, fromPause, frontEnd, canSave, multiplayer, loadBlocked,
// canLoad, saves, languages, query, pending, owner)` when it differs — the page and the gates as the
// guest computed them (row: the list pages' cursor the guest set, 0 on a settings page; owner: the
// player whose Options these are, as the handler's gate reads it — the box's hero (+0x88, a weak ref)
// -> its component 37 -> `lwz r11,0x14` the player record -> `lwz r11,0x24` the player, compared with
// the handler's r5 `cmpw r11,r26` 0x825F50A0..0x825F50B8 / disc 0x825CA580..0x825CA598; 0 when the
// hero is no player's, which the gate lets anyone through);
// `@Options.Values(box, epoch, page, invert, subtitles, tutorial, gamma, breadcrumb, music, sfx,
// voice, speaker, language)` when the guest itself filled them: the page's fill, the defaults,
// Show on the front end's page. Godot takes its working values from that statement alone.

#include <cmath>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <string>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "box_answers.h"
#include "mod_seam.h"
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
REX_EXTERN(rex_OptionsBox_SetListRow);
REX_EXTERN(rex_OptionsBox_SetSaveRow);

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

float Clamp01(double value) { return value < 0.0 ? 0.0f : value > 1.0 ? 1.0f : static_cast<float>(value); }

/// The box's fields (the header's list).
constexpr uint32_t kShown = 12u;
constexpr uint32_t kQueryUp = 30u;
constexpr uint32_t kInvert = 31u;
constexpr uint32_t kSubtitles = 32u;
constexpr uint32_t kTutorial = 33u;
constexpr uint32_t kFromPause = 34u;
constexpr uint32_t kCanSave = 36u;
constexpr uint32_t kFrontEnd = 37u;
constexpr uint32_t kLanguages = 60u;
constexpr uint32_t kLanguage = 64u;
constexpr uint32_t kSaves = 72u;
constexpr uint32_t kPage = 80u;
constexpr uint32_t kListRow = 84u;
constexpr uint32_t kSaveRow = 88u;
constexpr uint32_t kSettingsRow = 92u;
constexpr uint32_t kLiteRow = 96u;
constexpr uint32_t kSpeaker = 100u;
constexpr uint32_t kBreadcrumb = 104u;
constexpr uint32_t kMultiplayer = 108u;
constexpr uint32_t kLoadBlocked = 112u;
constexpr uint32_t kMusic = 116u;
constexpr uint32_t kSfx = 120u;
constexpr uint32_t kVoice = 124u;
constexpr uint32_t kGamma = 128u;
/// TU3 alone keeps whether Load is offered; the disc offers it while +112 is 0.
constexpr uint32_t kCanLoadTu3 = 192u;

constexpr uint32_t kPageSettings = 3u;
constexpr uint32_t kPageLite = 4u;

/// Global.GetDemoMode's value; 12 and above leave only page 5's rows 2 and 4.
const refii::guest::Addr kDemoMode{0x83596F48u};
constexpr uint32_t kDemoModeLocked = 12u;

/// The actions the screen's handler takes (see the header).
constexpr long kActionStart = 14;
constexpr long kActionUp = 29;
constexpr long kActionDown = 30;
constexpr long kActionLeft = 31;
constexpr long kActionRight = 32;
constexpr long kActionA = 74;
constexpr long kActionB = 75;
constexpr long kActionY = 77;

/// One settings row's field, as the left / right setters step it.
enum class Kind : uint8_t { None, Toggle, Bar, Count };
struct Field {
  uint32_t offset;
  Kind kind;
  int32_t last;  // a count's top (the language's is +60 - 1)
};

/// Page 3's rows 1..9 and page 4's rows 1..7, as sub_825F55B8's two switches order them.
constexpr Field kSettingsFields[10] = {
    {0u, Kind::None, 0},       {kInvert, Kind::Toggle, 1},     {kSubtitles, Kind::Toggle, 1},
    {kTutorial, Kind::Toggle, 1}, {kGamma, Kind::Bar, 1},     {kBreadcrumb, Kind::Count, 4},
    {kMusic, Kind::Bar, 1},    {kSfx, Kind::Bar, 1},           {kVoice, Kind::Bar, 1},
    {kSpeaker, Kind::Count, 2},
};
constexpr Field kLiteFields[8] = {
    {0u, Kind::None, 0},        {kLanguage, Kind::Count, -1}, {kSubtitles, Kind::Toggle, 1},
    {kGamma, Kind::Bar, 1},     {kMusic, Kind::Bar, 1},       {kSfx, Kind::Bar, 1},
    {kVoice, Kind::Bar, 1},     {kSpeaker, Kind::Count, 2},
};
constexpr long kSettingsRows = 9;
constexpr long kLiteRows = 7;

/// Half the setters' 0.025 step: a bar within it of the working value has reached it.
constexpr double kBarReach = 0.0125;
/// The most setter calls one update makes on one bar (0 to 1 is forty steps).
constexpr int kStepsPerUpdate = 41;
/// The updates a held commit or back waits for the box before it runs regardless (ten seconds).
constexpr int kDeferredUpdates = 300;

uint32_t g_screen = 0u;         // the Options screen its Show last stated
uint32_t g_screen_player = 0u;  // the player the router last handed its handler (for the log)
std::mutex g_mutex;
std::string g_last_state;
uint32_t g_values_epoch = 0u;

/// Godot's working value of each settings row not yet in the box, for page g_targets_page; the
/// row Godot's cursor is on; a commit or back held until the box has them. Guest tick thread only.
struct Target {
  bool set = false;
  double value = 0.0;
};
Target g_targets[10];
uint32_t g_targets_page = 0u;
long g_cursor_hint = 0;
long g_deferred = 0;
int g_deferred_updates = 0;
uint32_t g_player = 1u;

const Field* FieldOf(uint32_t page, long row) {
  if (page == kPageSettings && row >= 1 && row <= kSettingsRows) {
    return &kSettingsFields[row];
  }
  if (page == kPageLite && row >= 1 && row <= kLiteRows) {
    return &kLiteFields[row];
  }
  return nullptr;
}

long RowsOf(uint32_t page) { return page == kPageSettings ? kSettingsRows : page == kPageLite ? kLiteRows : 0; }

double BoxValue(const uint8_t* base, uint32_t box, const Field& field) {
  switch (field.kind) {
    case Kind::Toggle: return base[box + field.offset] != 0u ? 1.0 : 0.0;
    case Kind::Bar: return LoadGuestF32(base, box + field.offset);
    case Kind::Count: return static_cast<double>(static_cast<int32_t>(LoadGuestU32(base, box + field.offset)));
    default: return 0.0;
  }
}

bool Reached(const Field& field, double value, double target) {
  if (field.kind == Kind::Bar) {
    return std::fabs(value - target) <= kBarReach || (target >= 1.0 && value >= 1.0) || (target <= 0.0 && value <= 0.0);
  }
  return std::lround(value) == std::lround(target);
}

bool AnyTarget() {
  for (const Target& target : g_targets) {
    if (target.set) {
      return true;
    }
  }
  return false;
}

void ClearTargets() {
  for (Target& target : g_targets) {
    target.set = false;
  }
}

/// The player whose Options these are (head note): the box's hero, its component 37's player record, that
/// record's player; 0 when the hero is no player's.
uint32_t OwnerPlayer(const uint8_t* base, uint32_t box) {
  const uint32_t holder = LoadGuestU32(base, box + 0x8Cu);
  const uint32_t hero = Plausible(holder) ? LoadGuestU32(base, holder) : 0u;
  const uint32_t player = Plausible(hero) ? refii::fable3::seam::Component(base, hero, 37u) : 0u;
  const uint32_t record = player != 0u ? LoadGuestU32(base, player + 0x14u) : 0u;
  return Plausible(record) ? LoadGuestU32(base, record + 0x24u) : 0u;
}

/// `@Options.State(...)` (the header) when it differs from the last.
void StateScreen(const uint8_t* base, uint32_t box) {
  if (base == nullptr || !Plausible(box)) {
    return;
  }
  const uint32_t page = LoadGuestU32(base, box + kPage);
  const uint32_t row = page == 2u ? LoadGuestU32(base, box + kSaveRow) :
                       page == 1u || page == 5u ? LoadGuestU32(base, box + kListRow) : 0u;
  const uint32_t load_blocked = LoadGuestU32(base, box + kLoadBlocked);
  const uint32_t words[] = {
      page,
      row,
      base[box + kFromPause],
      base[box + kFrontEnd],
      base[box + kCanSave],
      LoadGuestU32(base, box + kMultiplayer),
      load_blocked,
      refii::guest::Pick(static_cast<uint32_t>(base[box + kCanLoadTu3]), load_blocked == 0u ? 1u : 0u),
      LoadGuestU32(base, box + kSaves),
      LoadGuestU32(base, box + kLanguages),
      base[box + kQueryUp],
      AnyTarget() || g_deferred != 0 ? 1u : 0u,
      OwnerPlayer(base, box),
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

/// `@Options.Values(...)`: the settings the guest itself just put in the box; Godot's working
/// values from here. Any value Godot still had waiting is the guest's to replace.
void StateValues(const uint8_t* base, uint32_t box) {
  if (base == nullptr || !Plausible(box)) {
    return;
  }
  ClearTargets();
  Statement values("@Options.Values");
  values.Add(Text(Hex(box)));
  values.Add(Number(static_cast<float>(++g_values_epoch)));
  values.Add(Number(static_cast<float>(LoadGuestU32(base, box + kPage))));
  values.Add(Number(base[box + kInvert] != 0u ? 1.0f : 0.0f));
  values.Add(Number(base[box + kSubtitles] != 0u ? 1.0f : 0.0f));
  values.Add(Number(base[box + kTutorial] != 0u ? 1.0f : 0.0f));
  values.Add(Number(LoadGuestF32(base, box + kGamma)));
  values.Add(Number(static_cast<float>(static_cast<int32_t>(LoadGuestU32(base, box + kBreadcrumb)))));
  values.Add(Number(LoadGuestF32(base, box + kMusic)));
  values.Add(Number(LoadGuestF32(base, box + kSfx)));
  values.Add(Number(LoadGuestF32(base, box + kVoice)));
  values.Add(Number(static_cast<float>(static_cast<int32_t>(LoadGuestU32(base, box + kSpeaker)))));
  values.Add(Number(static_cast<float>(static_cast<int32_t>(LoadGuestU32(base, box + kLanguage)))));
  values.Post();
}

/// The player field of a lever, `<p>:` with p 1 or 2, at `at`; the text after it, or null.
const char* TakePlayer(const char* at, uint32_t& player) {
  if ((at[0] != '1' && at[0] != '2') || at[1] != ':') {
    return nullptr;
  }
  player = static_cast<uint32_t>(at[0] - '0');
  return at + 2;
}

/// A non-negative decimal (`0.725`, `3`) without the locale; false when it is not one.
bool TakeValue(const char* at, double& value) {
  value = 0.0;
  bool digits = false;
  while (*at >= '0' && *at <= '9') {
    value = value * 10.0 + (*at++ - '0');
    digits = true;
  }
  if (*at == '.') {
    ++at;
    double scale = 0.1;
    while (*at >= '0' && *at <= '9') {
      value += (*at++ - '0') * scale;
      scale *= 0.1;
      digits = true;
    }
  }
  return digits && *at == '\0';
}

PPCContext Frame(const PPCContext& ctx, uint32_t bytes = 0x200u) {
  PPCContext work = ctx;
  work.r1.u32 = (ctx.r1.u32 - bytes) & ~0xFu;
  return work;
}

/// The event record the handler reads (`*ev` the action), on the call's own stack.
void StoreStackU32(uint8_t* base, uint32_t address, uint32_t value) {
  const uint32_t swapped = __builtin_bswap32(value);
  std::memcpy(base + address, &swapped, sizeof(swapped));
}

/// One game action into the screen's own handler body for `player`.
void RunHandler(PPCContext& ctx, uint8_t* base, uint32_t box, long id, uint32_t player) {
  PPCContext work = Frame(ctx);
  const uint32_t event = work.r1.u32 + 0x100u;
  StoreStackU32(base, event, static_cast<uint32_t>(id));
  StoreStackU32(base, event + 4u, 0u);
  StoreStackU32(base, event + 8u, 0u);
  StoreStackU32(base, event + 12u, 0u);
  work.r3.u64 = box;
  work.r4.u64 = event;
  work.r5.u64 = player;
  __imp__rex_OptionsBox_HandleInput(work, base);
}

/// A lever's action through the handler; a settings page the guest just filled or reset is stated.
void RunLever(PPCContext& ctx, uint8_t* base, uint32_t box, long id, uint32_t player) {
  const uint32_t was = LoadGuestU32(base, box + kPage);
  RunHandler(ctx, base, box, id, player);
  const uint32_t page = LoadGuestU32(base, box + kPage);
  if ((page == kPageSettings || page == kPageLite) && (page != was || id == kActionY)) {
    g_targets_page = page;
    StateValues(base, box);
  }
  StateScreen(base, box);
}

/// One update's work toward Godot's working values (the header's `options-value`), then a held
/// commit or back once nothing waits.
void SyncSettings(PPCContext& ctx, uint8_t* base, uint32_t box) {
  if (!BoxIsShowing(box) || base[box + kShown] == 0u) {
    return;
  }
  const uint32_t page = LoadGuestU32(base, box + kPage);
  if (page != g_targets_page) {
    if (AnyTarget() || g_deferred != 0) {
      REXLOG_INFO("[fable3-options] page {} left for page {}: working values not yet in the box dropped",
                  g_targets_page, page);
    }
    ClearTargets();
    g_deferred = 0;
    g_targets_page = page;
  }
  const long rows = RowsOf(page);
  if (rows == 0 || base[box + kQueryUp] != 0u) {
    return;
  }
  const uint32_t row_at = page == kPageSettings ? kSettingsRow : kLiteRow;
  const long row = static_cast<int32_t>(LoadGuestU32(base, box + row_at));
  if (const Field* field = FieldOf(page, row); field != nullptr && g_targets[row].set) {
    const double target = g_targets[row].value;
    for (int step = 0; step < kStepsPerUpdate; ++step) {
      const double value = BoxValue(base, box, *field);
      if (Reached(*field, value, target)) {
        g_targets[row].set = false;
        break;
      }
      RunHandler(ctx, base, box, target > value ? kActionRight : kActionLeft, g_player);
      if (BoxValue(base, box, *field) == value) {
        break;  // a count's gate: the next update steps it
      }
    }
  }
  long next = 0;
  for (long r = 1; r <= rows; ++r) {
    if (g_targets[r].set && (next == 0 || std::labs(r - row) < std::labs(next - row))) {
      next = r;
    }
  }
  if (next == 0) {
    next = g_cursor_hint;
  }
  if (next >= 1 && next <= rows && next != row) {
    RunHandler(ctx, base, box, next > row ? kActionDown : kActionUp, g_player);
  }
  if (g_deferred != 0) {
    const bool ready = !AnyTarget();
    if (ready || --g_deferred_updates < 0) {
      if (!ready) {
        REXLOG_WARN("[fable3-options] action {} waited {} updates for the box's values; run with them unreached",
                    g_deferred, kDeferredUpdates);
        ClearTargets();
      }
      const long action = g_deferred;
      g_deferred = 0;
      RunLever(ctx, base, box, action, g_player);
    }
  }
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
  if (id != kActionA && id != kActionB && id != kActionY && id != kActionStart) {
    REXLOG_WARN("[fable3-options] '{}' is no action Godot hands the screen; dropped", action);
    return false;
  }
  if (player != g_screen_player && g_screen_player != 0u) {
    REXLOG_INFO("[fable3-options] '{}' as player {} (the guest's router last handed this screen player {})", action,
                player, g_screen_player);
  }
  g_player = player;
  const uint32_t page = LoadGuestU32(base, box + kPage);
  const bool query = base[box + kQueryUp] != 0u;
  if (!query && (page == kPageSettings || page == kPageLite) && (id == kActionA || id == kActionB) && AnyTarget()) {
    g_deferred = id;
    g_deferred_updates = kDeferredUpdates;
    REXLOG_INFO("[fable3-options] '{}' held until the box has Godot's values", action);
    StateScreen(base, box);
    return true;
  }
  if (id == kActionY) {
    ClearTargets();
  }
  RunLever(ctx, base, box, id, player);
  return true;
}

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
  const uint32_t page = LoadGuestU32(base, box + kPage);
  if (page == kPageSettings || page == kPageLite) {
    // Godot's cursor: the guest's is walked to it while no value waits (SyncSettings).
    if (row < 1 || row > RowsOf(page)) {
      REXLOG_WARN("[fable3-options] '{}' is no row of page {}; dropped", action, page);
      return false;
    }
    g_cursor_hint = row;
    g_player = player;
    return true;
  }
  long last = 0;
  if (page == 2u) {
    last = 2;
  } else if (page == 1u || page == 5u) {
    if (LoadGuestU32(base, kDemoMode) >= kDemoModeLocked) {
      last = page == 5u && (row == 2 || row == 4) ? row : 0;
    } else {
      last = LoadGuestU32(base, box + kMultiplayer) != 0u ? 4 : 3;
    }
  }
  if (row < 1 || row > last) {
    REXLOG_WARN("[fable3-options] '{}' is no row of page {}; dropped", action, page);
    return false;
  }
  const uint32_t row_at = page == 2u ? kSaveRow : kListRow;
  if (static_cast<long>(static_cast<int32_t>(LoadGuestU32(base, box + row_at))) != row) {
    PPCContext work = Frame(ctx);
    work.r3.u64 = box;
    work.r4.u64 = static_cast<uint32_t>(row);
    if (page == 2u) {
      rex_OptionsBox_SetSaveRow(work, base);
    } else {
      rex_OptionsBox_SetListRow(work, base);
    }
  }
  g_player = player;
  if (accept) {
    RunLever(ctx, base, box, kActionA, player);
  } else {
    StateScreen(base, box);
  }
  return true;
}

bool AnswerOptionsValue(PPCContext& ctx, uint8_t* base, const char* action) {
  (void)ctx;
  const uint32_t box = g_screen;
  if (base == nullptr || box == 0u || !BoxIsShowing(box)) {
    REXLOG_WARN("[fable3-options] '{}' with no Options screen showing; dropped", action);
    return false;
  }
  uint32_t player = 0u;
  const char* rest = TakePlayer(action + sizeof(kOptionsValueAction) - 1u, player);
  char* end = nullptr;
  const long stated_page = rest == nullptr ? 0 : std::strtol(rest, &end, 10);
  long row = 0;
  if (end != nullptr && *end == ':') {
    row = std::strtol(end + 1, &end, 10);
  }
  double value = 0.0;
  const uint32_t page = LoadGuestU32(base, box + kPage);
  const Field* field = FieldOf(page, row);
  if (rest == nullptr || stated_page != static_cast<long>(page) || field == nullptr || end == nullptr || *end != ':' ||
      !TakeValue(end + 1, value)) {
    REXLOG_WARN("[fable3-options] '{}' names no value of a row of page {}; dropped", action, page);
    return false;
  }
  // The setters' own range: a toggle 0 / 1, a bar 0..1, a count 0..its top (the language's +60 - 1).
  const double top = field->kind == Kind::Count && field->last < 0
                         ? static_cast<double>(static_cast<int32_t>(LoadGuestU32(base, box + kLanguages)) - 1)
                         : static_cast<double>(field->last);
  value = value < 0.0 ? 0.0 : value > top ? top : value;
  if (page != g_targets_page) {
    ClearTargets();
    g_targets_page = page;
  }
  g_targets[row] = {true, value};
  g_player = player;
  StateScreen(base, box);
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
  const float gamma = readable ? LoadGuestF32(base, screen + kGamma) : 0.0f;
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

// (screen): the guest's Show, then its state; the front end's page was filled by it.
REX_EXTERN(rex_OptionsBox_Show) {
  const uint32_t screen = ctx.r3.u32;
  __imp__rex_OptionsBox_Show(ctx, base);
  g_screen = screen;
  g_screen_player = 0u;
  g_cursor_hint = 0;
  g_deferred = 0;
  ClearTargets();
  g_targets_page = base != nullptr && Plausible(screen) ? LoadGuestU32(base, screen + kPage) : 0u;
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    g_last_state.clear();
  }
  if (g_targets_page == kPageSettings || g_targets_page == kPageLite) {
    StateValues(base, screen);
  }
  StateScreen(base, screen);
}

/// (screen): the guest's per-frame update (slot 5), then one update's work toward Godot's values
/// and the state (TU3 rewrites the save fields there).
REX_EXTERN(rex_OptionsBox_Update) {
  const uint32_t screen = ctx.r3.u32;
  __imp__rex_OptionsBox_Update(ctx, base);
  if (screen == g_screen && base != nullptr) {
    SyncSettings(ctx, base, screen);
    StateScreen(base, screen);
  }
}

/// (screen, ev*, player): STUBBED — the guest's pad never reaches the handler; its body runs from
/// Godot's levers alone (RunHandler). Every action is answered "consumed".
REX_EXTERN(rex_OptionsBox_HandleInput) {
  g_screen_player = ctx.r5.u32;
  ctx.r3.u64 = 1u;
}

/// (box, tag): the query, re-expressed (the header): stated, and its one store.
REX_EXTERN(rex_OptionsBox_Query) {
  const uint32_t box = ctx.r3.u32;
  Statement("@Options.Query").Add(Text(Hex(box))).Add(Text(Hex(ctx.r4.u32))).Post();
  if (base != nullptr && Plausible(box)) {
    base[box + kQueryUp] = 1u;
  }
}
