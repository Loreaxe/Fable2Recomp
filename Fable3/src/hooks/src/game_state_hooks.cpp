// refii - Fable 3's game mode stacks, its GUI screen and its installed cultures, stated at their
// writers into the table both modules link (core/payload_bridge.h, src/runtime/src/core/
// stated_game_state.cpp); the host bridge (Fable3/src/hostbridge/src/level_feed.cpp,
// options_feed.cpp) reads only that table. Every hook runs the guest's body unchanged.
//
// Every address is the Fable 3 TU3 IDB ("disc" marks one TU3 lacks); each offset names its base.
//
// THE MODE STACK. A game state's modes are a list S = `*(*(state+0x30)+4)` (state =
// `*(*0x83597034+0xC)`, the pause push at 0x825F9ADC and the run at disc 0x826ED3C8..disc 0x826ED3D0):
// S+0 the sentinel node, S+4 the depth, a node {+0, +4 links, +8 the mode, +12 its count}; the TOP
// is `*(*(S+0)+4)`'s mode, the one the game loop runs (0x821CBF98..disc 0x821CBEA4). Its writers:
//   - push sub_8242BD78(S, shared_ptr<mode>*): the insert sub_82476A28 links the new node at the
//     sentinel's +4 (0x82476AC4) and bumps S+4 (0x82476AC0), so the pushed mode is the top; the
//     old top's slot 8 runs first (0x8242BDB8), the new one's slot 5 after (0x8242BDDC).
//   - pop sub_8242BDF8(S, id): the top when its slot 1 (the mode's id) answers `id` (0x8242BE38),
//     else the first mode below it that does (0x8242BEF0), erased through sub_828A0628.
//   - the run sub_8242BF30(S): it unlinks every top whose slot 2 says it is finished
//     (0x8242BFA0..0x8242C004) and runs the first that is not. It is NOT the per-update run: its one
//     caller is slot 6 of the play mode (id 2: slot 1 `li r3,2` TU3 0x82E2E918 / disc 0x82DF1AF8;
//     vtable TU3 0x820AC700 / disc 0x820AA314, slot 6 at +0x18 = TU3 sub_82716D08 / disc
//     sub_826ED3A0 — reached only through that vtable, so the disc IDB shows it no code reference),
//     and only when the mode's own byte asks for it: `lbz r11,0x18(r3); cmplwi; beq` TU3
//     0x82716D1C..0x82716D28, cleared `stb r8,0x18(r31)` 0x82716D4C; disc `lbz r11,0x14(r3)`
//     0x826ED3B0..0x826ED3BC, `stb r8,0x14(r31)` 0x826ED3E0. The game loop runs the top itself.
// Each is stated as it returns: the top's mode, its vtable and the depth. The GUI's pause mode
// (CGameModePause, ctor disc sub_824B3B68: vtable 0x82093488 at disc 0x824B3B94; id 1, slot 1 sub_82A99990
// `li r3,1`) is pushed by lhGuiManager_EnterGuiMode (0x825F9ADC) and popped by id 1 by
// lhGuiManager_ExitScreen (0x825F9C14) and the credits' end (0x825FA3A0).
//
// THE UI MODE, CGameModeUI (TU3 vtable 0x820AF42C / disc 0x820AC0E4, stored by its ctor TU3
// sub_82778758 at 0x8277877C..0x82778788 / disc sub_82743D50 at 0x82743D74..0x82743D7C; id 8: slot 1
// `li r3,8` TU3 0x82A66A60 / disc 0x82796FB8), is the mode the GUI pushes when one of its surfaces
// takes the game from the heroes:
//   - a box's pause form sub_82958388(box, 0) / disc sub_8291F0C0 (the ctor 0x829584F0 / 0x8291F228,
//     the push 0x82958534 / 0x8291F26C; skipped when the top mode is 12 or 8, `cmpwi r3,0xC` 0x8295844C /
//     0x8291F184, `cmpwi r3,8` 0x829584D8 / 0x8291F210): the Options pause form (0x825F4730 / 0x825C9C8C), the pausing message
//     box (0x82878D78 / 0x8281B17C), the quest box (0x8287A82C / 0x8281CAC4);
//   - the menu box's open (ctor 0x82622200 / 0x825FA378, push 0x82622244 / 0x825FA3BC);
//   - the save menu's open (ctor 0x829B5048 / 0x8296C800, push 0x829B5088 / 0x8296C840).
// It leaves by the pop of id 8: the box base update (`li r4,8` 0x82958228 / 0x8291EF00), the menu
// box's close (0x82622350 / 0x825FA4C8), the save menu's close (0x829B513C / 0x8296C8F4).
// THE TWO GUI MODES ARE STATED AT THE STACK'S WRITERS. Every push and pop of either mode names the
// presented game's stack the same way: `lwz r11,dword_83597034; lwz 0xC(r11); lwz 0x30(..); lwz
// r3,4(..)` (the save menu's push 0x829B5078..0x829B5088 / disc `lwz r11,dword_83556540@l(r26)` ..
// `bl sub_8240E7A0` 0x8296C830..0x8296C840; the menu box's 0x82622230..0x82622244 / disc
// 0x825FA3A8..0x825FA3BC; the pause form's 0x82958524..0x82958534 / disc 0x8291F25C..0x8291F26C; the
// pops of id 8 load the same global, `li r4,8` 0x829B513C / disc 0x8296C8F4, 0x82958228 / disc
// 0x8291EF00). So each of the three writers, when it wrote THAT stack, states as it returns:
//   - `@GameMode.Ui(on)`: its top is the UI mode;
//   - `@GameMode.Gui(on)`: its top is the GUI's pause — a GUI screen has the game (EnterGuiMode).
// Under either the heroes' pad is the GUI's: the UI mode's slot 5 switches the input's control
// contexts (TU3 CGameModeUI_vf5 0x82778808: `bl sub_8250B1B8` (1, 0) 0x82778824, `bl sub_8250B228`
// (1) 0x8277882C; disc 0x82743DF0: `li r4,0; li r3,1; bl sub_824EAD60` 0x82743E08..0x82743E10, `li
// r3,1; bl sub_824EADD0` 0x82743E14..0x82743E18) and slot 8 switches them back with 0 (TU3
// sub_82778B48 0x82778B90 / 0x82778B98, disc 0x82744118: `li r4,0; li r3,0; bl sub_824EAD60`
// 0x82744150..0x82744158); under the GUI's pause the GUI input's router (TU3 sub_826019F0 / disc
// sub_825D6A08) answers 1 — its box test `bl sub_821A38C8` 0x82601B44 / disc `bl sub_821A3D20`
// 0x825D6B5C then `li r3,1` 0x82601BB8 / 0x825D6BD0, or the screen's handler `bctrl` 0x82601C60 /
// 0x825D6C78 — and its caller skips the dispatcher (`bne` TU3 0x82601148 / disc 0x825D6164). Both are posted on every write of the presented stack (pushes and pops are rare) and
// logged when they change.
//
// THE GUI SCREEN, `*(SM+4)`, SM = `*(GUI+0x38)`, GUI = `*0x83596F84`. Its writers, every
// `lwz rN,0x38(..)` followed by `stw ..,4(rN)` in the image:
//   - the screen set sub_825F9618(G, id, quiet): `stw r28,4(r10)` at 0x825F9988, r28 =
//     the id argument;
//   - lhGuiManager_ExitScreen: 32 at 0x825F9DBC;
//   - the download message box sub_825F9F38: sets 0 through sub_825F9618 (0x825F9F88) and puts
//     the previous id back at 0x825FA0B4;
//   - the credits' end ScreenManager_EndCredits: 0x825FA3D4, 0x825FA4E4 (its hook is
//     end_screen_hooks.cpp's, which states it through StateGuiScreen below).
// The set is stated from its argument; the others as they return, the field they wrote.
//
// THE CULTURES, Fable 2's table at Fable 3's addresses: rows of 96 bytes at *0x832E1548, the
// count at 0x832E154C, the row in use at 0x832E1550. Each row leads with its code (copied by
// sub_82DE7E78(0, i, row, 85): 0x822E9008 / 0x822E8B74) and holds its UTF-16 display name at
// `*(row+88)` (0x822E9018 / 0x822E8B84; read as a `wchar_t*` by the Options page,
// sub_82299940(&s, *(row+88)) at 0x825F8A90..0x825F8A98). Its writers:
//   - the boot build sub_822E8E88: frees, counts, fills, and takes the "en-uk" row as the one in
//     use (0x822E904C);
//   - the refresh sub_822E8948 (sub_822E9E18, CGUIGameComponent's ctor): the same rebuild,
//     keeping the code in use (0x822E8BBC), else selecting the en-uk row through the switch
//     (0x822E8C20);
//   - the switch sub_822E8C30(index): `index < count` (0x822E8C58) makes it the row in use
//     (0x822E8CA8).
// The builds are stated as they return, the table they wrote; the switch from its argument.

#include <atomic>
#include <cstdint>
#include <string>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "mod_lua.h"
#include "box_answers.h"
#include "core/payload_bridge.h"
#include "gui_statements.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_GameModeStack_Push);
REX_EXTERN(__imp__rex_GameModeStack_Pop);
REX_EXTERN(__imp__rex_GameModeStack_Run);
REX_EXTERN(__imp__rex_GuiManager_SetScreen);
REX_EXTERN(__imp__rex_GuiManager_ExitScreen);
REX_EXTERN(__imp__rex_GuiManager_DownloadBox);
REX_EXTERN(__imp__rex_Cultures_Build);
REX_EXTERN(__imp__rex_Cultures_Refresh);
REX_EXTERN(__imp__rex_Culture_Select);

namespace {

using refii::fable3::AppendUtf8;
using refii::fable3::LoadGuestU16;
using refii::fable3::LoadGuestU32;
using refii::fable3::Plausible;

const refii::guest::Addr kGuiManager{0x83596F84u};
constexpr uint32_t kGuiScreens = 0x38u;
constexpr uint32_t kScreenId = 0x4u;
/// creditsscreen's id in the loader's table (ShowCreditsScreen's (loader, 8), SetScreen(SM, 8, 0)).
constexpr int32_t kCreditsScreen = 8;
const refii::guest::Addr kGuiPauseMode{0x82093488u};
const refii::guest::Addr kUiMode{0x820AF42Cu};
const refii::guest::Addr kGame{0x83597034u};

const refii::guest::Addr kCultureTable{0x832E1548u};
const refii::guest::Addr kCultureCount{0x832E154Cu};
const refii::guest::Addr kCultureCurrent{0x832E1550u};
constexpr uint32_t kCultureStride = 96u;
constexpr uint32_t kCultureCode = 85u;
constexpr uint32_t kCultureName = 88u;
constexpr uint32_t kMaxCultures = 64u;

uint32_t Word(const uint8_t* base, uint32_t at) { return Plausible(at) ? LoadGuestU32(base, at) : 0u; }

/// The top of mode stack `stack` as its writer left it; returns the top mode's vtable, 0 for none.
uint32_t StateStack(const uint8_t* base, uint32_t stack) {
  if (!Plausible(stack)) {
    return 0u;
  }
  const uint32_t depth = LoadGuestU32(base, stack + 4u);
  const uint32_t top = depth != 0u ? Word(base, Word(base, Word(base, stack) + 4u) + 8u) : 0u;
  const uint32_t vtable = Word(base, top);
  refii_bridge_mode_stack_stated(stack, top, vtable, depth);

  static std::atomic<uint64_t> last{~0ull};
  const uint64_t key = (static_cast<uint64_t>(stack) << 32) | vtable;
  if (last.exchange(key) != key) {
    REXLOG_INFO("[fable3-modes] stack {:08X}: top {:08X} (vtable {:08X}{}), depth {}", stack, top, vtable,
                vtable == kGuiPauseMode ? ", the GUI's pause" : vtable == kUiMode ? ", the UI mode" : "", depth);
  }
  return vtable;
}

/// The presented game's mode stack, named as the GUI modes' pushers and pops name it.
uint32_t PresentedStack(const uint8_t* base) {
  return Word(base, Word(base, Word(base, Word(base, kGame) + 0xCu) + 0x30u) + 4u);
}

void StateGuiMode(const char* name, const char* what, uint32_t on, std::atomic<uint32_t>& last) {
  refii::fable3::Statement(name).Add(refii::fable3::Boolean(on != 0u)).Post();
  if (last.exchange(on) != on) {
    REXLOG_INFO("[fable3-modes] {} is {} the presented game", what, on != 0u ? "on top of" : "off");
  }
}

/// `@GameMode.Ui(on)` and `@GameMode.Gui(on)` after a writer of `stack`, when it is the presented game's.
void StateGuiModes(const uint8_t* base, uint32_t stack, uint32_t top_vtable) {
  if (stack == 0u || stack != PresentedStack(base)) {
    return;
  }
  static std::atomic<uint32_t> ui{~0u};
  static std::atomic<uint32_t> gui{~0u};
  StateGuiMode("@GameMode.Ui", "the UI mode", top_vtable == kUiMode ? 1u : 0u, ui);
  StateGuiMode("@GameMode.Gui", "the GUI's pause", top_vtable == kGuiPauseMode ? 1u : 0u, gui);
}

void StateScreen(uint32_t id) {
  refii_bridge_gui_screen_stated(id);
  static std::atomic<uint32_t> last{~0u};
  if (last.exchange(id) != id) {
    REXLOG_INFO("[fable3-modes] GUI screen {}", id);
  }
}

/// The UTF-16BE string at `wide` as UTF-8, bounded.
std::string ReadWide(const uint8_t* base, uint32_t wide) {
  std::string text;
  for (uint32_t i = 0; Plausible(wide) && i < 128u; ++i) {
    uint32_t unit = LoadGuestU16(base, wide + (i * 2u));
    if (unit == 0u) {
      break;
    }
    if (unit >= 0xD800u && unit < 0xDC00u) {
      const uint32_t low = LoadGuestU16(base, wide + ((i + 1u) * 2u));
      if (low < 0xDC00u || low >= 0xE000u) {
        break;
      }
      unit = 0x10000u + ((unit - 0xD800u) << 10) + (low - 0xDC00u);
      ++i;
    } else if (unit >= 0xDC00u && unit < 0xE000u) {
      break;
    }
    AppendUtf8(text, unit);
  }
  return text;
}

/// The culture table as a build left it.
void StateCultures(const uint8_t* base, const char* writer) {
  const uint32_t table = LoadGuestU32(base, kCultureTable);
  uint32_t count = LoadGuestU32(base, kCultureCount);
  const int32_t current = static_cast<int32_t>(LoadGuestU32(base, kCultureCurrent));
  if (!Plausible(table) || count > kMaxCultures) {
    count = 0u;
  }
  std::string codes;
  std::string names;
  for (uint32_t i = 0; i < count; ++i) {
    const uint32_t row = table + (i * kCultureStride);
    for (uint32_t c = 0; c < kCultureCode && base[row + c] != 0u; ++c) {
      codes.push_back(static_cast<char>(base[row + c]));
    }
    codes.push_back('\0');
    names += ReadWide(base, LoadGuestU32(base, row + kCultureName));
    names.push_back('\0');
  }
  refii_bridge_cultures_stated(count, current, codes.data(), static_cast<uint32_t>(codes.size()), names.data(),
                               static_cast<uint32_t>(names.size()));
  char code[kCultureCode + 1u]{};
  REXLOG_INFO("[fable3-culture] {}: {} cultures, row {} ('{}')", writer, count, current,
              refii_bridge_culture_code_read(code, sizeof(code)) != 0u ? code : "");
}

}  // namespace

namespace refii::fable3 {

void StateGuiScreen(const uint8_t* base) {
  const uint32_t screens = Word(base, Word(base, kGuiManager) + kGuiScreens);
  if (Plausible(screens)) {
    StateScreen(LoadGuestU32(base, screens + kScreenId));
  }
}

}  // namespace refii::fable3

// (S, shared_ptr<mode>*).
REX_EXTERN(rex_GameModeStack_Push) {
  const uint32_t stack = ctx.r3.u32;
  __imp__rex_GameModeStack_Push(ctx, base);
  StateGuiModes(base, stack, StateStack(base, stack));
}

// (S, id).
REX_EXTERN(rex_GameModeStack_Pop) {
  const uint32_t stack = ctx.r3.u32;
  __imp__rex_GameModeStack_Pop(ctx, base);
  StateGuiModes(base, stack, StateStack(base, stack));
}

// (S): the play mode's flush of finished tops (its slot 6, when asked).
REX_EXTERN(rex_GameModeStack_Run) {
  const uint32_t stack = ctx.r3.u32;
  __imp__rex_GameModeStack_Run(ctx, base);
  StateGuiModes(base, stack, StateStack(base, stack));
}

// (G, id, quiet): the screen id is the argument (0x825F9988).
REX_EXTERN(rex_GuiManager_SetScreen) {
  const uint32_t id = ctx.r4.u32;
  const int32_t before = refii::fable3::modlua::MenuScreen(base);
  __imp__rex_GuiManager_SetScreen(ctx, base);
  StateScreen(id);
  refii::fable3::modlua::NoteMenuScreen(base, before);
}

// (G).
REX_EXTERN(rex_GuiManager_ExitScreen) {
  const int32_t before = refii::fable3::modlua::MenuScreen(base);
  if (before == kCreditsScreen) {
    REXLOG_INFO("[fable3-modes] the credits' screen 8 left by ExitScreen from {:08X}, not by the credits' end",
                static_cast<uint32_t>(ctx.lr));
  }
  __imp__rex_GuiManager_ExitScreen(ctx, base);
  refii::fable3::StateGuiScreen(base);
  refii::fable3::modlua::NoteMenuScreen(base, before);
}

// (G, text LHString, download): puts the previous screen id back as it returns. When G+36 was 1
// the box came up (+36 = 2 at 0x825F9F6C): stated with the text and the download flag, read
// before the call, which destroys the text as it returns (after 0x825FA0B4).
REX_EXTERN(rex_GuiManager_DownloadBox) {
  const uint32_t manager = ctx.r3.u32;
  const bool takes = base != nullptr && Plausible(manager) && LoadGuestU32(base, manager + 36u) == 1u;
  const std::string text = takes ? refii::fable3::ReadLhString(base, ctx.r4.u32) : std::string();
  const bool download = (ctx.r5.u32 & 0xFFu) != 0u;
  __imp__rex_GuiManager_DownloadBox(ctx, base);
  refii::fable3::StateGuiScreen(base);
  if (takes) {
    refii::fable3::StateModalMessageBox(manager, text, download);
  }
}

// The boot build of the culture table.
REX_EXTERN(rex_Cultures_Build) {
  __imp__rex_Cultures_Build(ctx, base);
  StateCultures(base, "build");
}

// (force): the rebuild.
REX_EXTERN(rex_Cultures_Refresh) {
  __imp__rex_Cultures_Refresh(ctx, base);
  StateCultures(base, "refresh");
}

// (index).
REX_EXTERN(rex_Culture_Select) {
  const uint32_t index = ctx.r3.u32;
  const uint32_t count = LoadGuestU32(base, kCultureCount);
  __imp__rex_Culture_Select(ctx, base);
  if (index < count) {
    refii_bridge_culture_selected(index);
    char code[kCultureCode + 1u]{};
    REXLOG_INFO("[fable3-culture] select row {} ('{}')", index,
                refii_bridge_culture_code_read(code, sizeof(code)) != 0u ? code : "");
  }
}
