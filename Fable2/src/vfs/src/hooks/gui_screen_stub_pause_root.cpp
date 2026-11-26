// THE PORTED SCREENS (S7). Each handler slot below is a full replacement: it keeps the guest's
// statements of the screen's data and its game side effects, as direct calls, and drops what
// only draws or animates. Godot drives the rest through the levers at the end of each file
// (guest_tick_pump.cpp dispatches them). The handler objects stay registered.
//
// Slot layout (vtable): +8 OnEnter, +16 Update, +20 OnMenuEvent(h, event), +24
// OnScreenAction(h, code, player), +36 OnExit. TU1 / disc addresses on each function.
#include <algorithm>
#include <cctype>
#include <cstdint>
#include <cstring>
#include <initializer_list>
#include <string>

#include <rex/logging.h>

#include "core/guest_build.h"
#include "core/refii_mod.h"
#include "hooks/gui_screen_stub_support.h"
#include "hooks/gui_screen_stubs.h"
#include "hooks/ppc_externs.h"
#include "vfs_hooks_private.h"

PPC_EXTERN_FUNC(rex_lhGuiManager_FindScreenHandler);

using namespace refii::vfs::stub;
using refii::guest::Address;
using refii::mods::LoadU32;
using refii::mods::StoreU32;

// ---- THE PAUSE ROOT (0) — vtable 0x820CDE18 / 0x820B06E8 ------------------------------------
//
// h+4 the owner (0 player one, 1 player two), h+24 the row list (vt 0x820E9D74: +4 populate
// `sub_8298DED8` → `sub_8298DF98` hero / `sub_8298EDE0` henchman), h+84 the map's turn in degrees,
// h+88 the marker-id tree, h+100 "markers placed", h+244/+245 the map flags `sub_826A4388` writes,
// h+252 the legend's scroll region (vt 0x820C0DE4), h+280 the kick countdown (-1 idle). The byte
// TU1 h+52 / disc h+48 and the float TU1 h+56 / disc h+52 are the only fields the builds place apart
// (`stb r21, 0x34(r25)` 0x826A1A8C / `0x30` 0x8240DE44; `stfs f4, 0x38(r25)` 0x826A22AC / `0x34`).

namespace {

/// The current level's record, `*(*(*(*(*(*(GAME+0x58)+4)+8)+4)+0x24)+4)` (0x826A2110..0x826A2124):
/// +0x98 the world rectangle {x1, y1, x2, y2}, +0xAC the region name's handle.
uint32_t LevelRecord(uint8_t* base) {
  uint32_t at = Game(base);
  for (uint32_t offset : {0x58u, 4u, 8u, 4u, 0x24u, 4u}) {
    if (!Plausible(at)) {
      return 0u;
    }
    at = LoadU32(base, at + offset);
  }
  return Plausible(at) ? at : 0u;
}

/// The characters of the CCharString whose handle is `handle` (+0 the characters), "" for none.
std::string HandleText(uint8_t* base, uint32_t handle) {
  const uint32_t chars = Plausible(handle) ? LoadU32(base, handle) : 0u;
  return Plausible(chars) ? std::string(reinterpret_cast<const char*>(base + chars)) : std::string();
}

bool SameIgnoringCase(const std::string& a, const std::string& b) {
  return a.size() == b.size() && std::equal(a.begin(), a.end(), b.begin(), [](char x, char y) {
           return std::tolower(static_cast<unsigned char>(x)) == std::tolower(static_cast<unsigned char>(y));
         });
}

/// The pause root's handler out of the guest's map, checked against its vtable; 0 when absent.
uint32_t PauseRoot(PPCContext& ctx, uint8_t* base, uint32_t frame) {
  const uint32_t g = Gui(base);
  const uint32_t qm = Plausible(g) ? LoadU32(base, g + 28u) : 0u;
  if (!Plausible(qm)) {
    return 0u;
  }
  PPCContext find = ctx;
  find.r1.u32 = frame;
  find.r3.u32 = qm;
  find.r4.u32 = 0u;
  rex_lhGuiManager_FindScreenHandler(find, base);
  const uint32_t h = find.r3.u32;
  return Plausible(h) && LoadU32(base, h) == Address(0x820CDE18u) ? h : 0u;
}

bool g_pause_update_said = false;

}  // namespace

// OnEnter TU1 0x826A19E0 / disc 0x8240DD98. Kept, in the guest's order: h+248 = 0; the list-end
// fades `g_MenuFormatter:FadeOutBottomItemEarly()` / `FadeOutTopItemEarly()` (0x826A1A44 /
// 0x826A1A6C — Godot reads them as the pause list's end fades, GameUi StatedMenuEndFades); the rows,
// h+24 vf+4 (0x826A1A88); h+52 = 0; the tab strip `sub_826B8348(*(G+76), 1)` (0x826A1A9C / 0x8240B218)
// and its title `sub_826B81C0(*(G+76), "GUI_SCREEN_TITLE_MAIN_MENU[_P2]")` (0x826A1AD8 / 0x8240B090,
// _P2 when h+4 == 1); the legend scroll region's reset h+252 vf+4 (0x826A1AF8); tutorials 119 and, when
// 147 was never shown, 146 (0x826A1B48 / 0x826A1B7C); h+100 = 1; then the right page off the local
// hero (`sub_82264230(*(GAME+0x9C), net+0x34 && net+0x35 ? 1 : 2)`, 0x826A1BCC / 0x822641F0): with
// component 33 the minimap's show `0x8349ABC4` = comp33+0x35 (0x826A1D34); when shown, h+100 = 0,
// the map image's key `mm_barrowfields.tex` hashed (0x826A1D90) and refined with the map's turn and
// flags by `sub_826A4388` (0x826A1DE0 / 0x82410740); with component 102 the primary quest
// (`Lua_QuestTracker_GetPrimaryQuestName` + `sub_8251F6D0`, 0x826A1F0C / 0x825FC090): its title
// `SetTextFromTag` on 0x8349ABE0 (0x826A1F8C), its objective `SetText` on 0x8349ABE4 (0x826A2004),
// both shown — or both hidden without a quest (0x826A2180 / 0x826A21A4) — and whether its region is
// the current level's (`strcmpi` 0x826A214C); the map image 0x8349ABE8's texture (the inline store
// 0x826A21EC, stated here: the headless element has no quad) and turn `sub_824254C8` (0x826A2214 /
// 0x823F7960); h+56 = the rectangle's larger side and h+64 = {x, 0, y, 0} of the image's position
// `sub_824252E8` (0x826A22AC / 0x826A22F4); 0x8349AC04 shown as h+244 (0x826A2314); the legend fill
// `sub_826A3658` (0x826A2330 / 0x8240FA10) or h+100 = 1; the hero and dog tokens `sub_826A2F98`
// (0x826A2364 / 0x8240F350). Last, without the map the legend's scroll mask `sub_828C3250` (0x826A23BC /
// 0x82833C38), the scroll bar `sub_828C33F0(h+252, 0x8349AC08)` (0x826A23DC / 0x82833DD8), h+280 = -1.
// Dropped: the open sound (vf+4 0x826A1A24, `sub_826D5BD8`), `g_Menu:HandleEnter()` (0x826A1B18 — an
// empty Lua function, Engine_UiMenuModel) and the objective's re-position under its text
// (0x826A2018..0x826A20B0: layout, and it needs the quad a headless element lacks).
REX_EXTERN(rex_lhGuiPauseRootScreen_OnEnter) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  const uint32_t slot = s.data;               // a handle copy for an element lookup
  const uint32_t text = s.data + 0x10u;       // a CCharString handle, its characters at +0x40
  const uint32_t text2 = s.data + 0x14u;      // a second one, its characters at +0xC0
  const uint32_t hash = s.data + 0x18u;       // the map image's key
  const uint32_t quest_name = s.data + 0x1Cu; // the primary quest's name
  const uint32_t tag = s.data + 0x150u;       // the quest title's tag
  const uint32_t objective = s.data + 0x154u; // its objective (three strings)
  const uint32_t localised = s.data + 0x158u;
  const uint32_t line = s.data + 0x15Cu;
  const uint32_t empty = s.data + 0x168u;     // ""
  const uint32_t at = s.data + 0x170u;        // the image's position {x, y, z}
  const uint32_t g = Gui(base);
  const uint32_t pres = GuestPresentation(ctx, base, s.frame);
  const uint32_t strip = LoadU32(base, g + 76u);
  StoreU32(base, empty, 0u);
  g_pause_update_said = false;

  StoreU32(base, h + 248u, 0u);
  RunMenuLua(ctx, base, s.frame, text, "g_MenuFormatter:FadeOutBottomItemEarly()", pres);
  RunMenuLua(ctx, base, s.frame, text, "g_MenuFormatter:FadeOutTopItemEarly()", pres);
  CallSlot(ctx, base, s.frame, h + 24u, 1u, {});
  base[h + refii::guest::Pick(52u, 48u)] = 0u;
  Call(ctx, base, s.frame, 0x826B8348u, {strip, 1u});
  MakeString(ctx, base, s.frame, text, text + 0x30u,
             LoadU32(base, h + 4u) == 1u ? "GUI_SCREEN_TITLE_MAIN_MENU_P2" : "GUI_SCREEN_TITLE_MAIN_MENU");
  Call(ctx, base, s.frame, 0x826B81C0u, {strip, text});
  DropString(ctx, base, s.frame, text);
  CallSlot(ctx, base, s.frame, h + 252u, 1u, {});

  const uint32_t game = Game(base);
  const uint32_t tutorials = LoadU32(base, game + 0xA8u);
  const uint32_t state = LoadU32(base, tutorials + 4u);
  if (base[state + 0x38u] != 0u) {
    Call(ctx, base, s.frame, 0x821B5260u, {state, 119u});  // lhTutorials_Display
  }
  const uint32_t state2 = LoadU32(base, tutorials + 4u);
  if (base[state2 + 0x39u] != 0u && (Call(ctx, base, s.frame, 0x82450870u, {tutorials, 147u}) & 0xFFu) == 0u) {
    Call(ctx, base, s.frame, 0x821B5260u, {state2, 146u});
  }
  base[h + 100u] = 1u;

  const uint32_t net = LoadU32(base, game + 0x8Cu);
  const uint32_t key = base[net + 0x34u] != 0u && base[net + 0x35u] != 0u ? 1u : 2u;
  const uint32_t record = Call(ctx, base, s.frame, 0x82264230u, {LoadU32(base, game + 0x9Cu), key}) + 12u;
  const uint32_t ref = LoadU32(base, record + 4u);
  uint32_t hero = 0u;
  if (Plausible(ref) && LoadU32(base, ref) != 0u) {
    hero = LoadU32(base, ref);
  } else if (Plausible(ref)) {
    Call(ctx, base, s.frame, 0x82193E38u, {record});
  }
  const uint32_t comp33 = refii_mod_component(base, hero, 33u);
  const uint32_t map_shown = comp33 != 0u ? base[comp33 + 0x35u] : 0u;
  if (comp33 != 0u) {
    Toggle(ctx, base, s.frame, Element(ctx, base, s.frame, slot, 0x8349ABC4u, pres), pres, map_shown);
  }
  if (map_shown != 0u) {
    base[h + 100u] = 0u;
    MakeString(ctx, base, s.frame, text, text + 0x30u, "art\\gui\\legacy\\mm_barrowfields.tex");
    StoreU32(base, hash, Call(ctx, base, s.frame, 0x82C037F0u, {text}));  // rex_lhResourceName_ComputeHash
    DropString(ctx, base, s.frame, text);
    refii::mods::StoreF32(base, h + 84u, 0.0f);
    Call(ctx, base, s.frame, 0x826A4388u, {h, hash, h + 84u, h + 216u, h + 224u, h + 240u, h + 244u});

    uint32_t in_region = 0u;
    const uint32_t comp102 = refii_mod_component(base, hero, 102u);
    const auto primary_quest = [&]() {
      const uint32_t name = Call(ctx, base, s.frame, 0x822AD2B8u, {quest_name, comp102});
      const uint32_t handle = LoadU32(base, name);
      const uint32_t chars = Plausible(handle) ? LoadU32(base, handle) : empty;
      const uint32_t quest = Call(ctx, base, s.frame, 0x8251F6D0u, {comp102, chars});
      DropString(ctx, base, s.frame, quest_name);
      return quest;
    };
    if (comp102 != 0u) {
      const uint32_t quest = primary_quest();
      if (quest != 0u) {
        const uint32_t title = Element(ctx, base, s.frame, slot, 0x8349ABE0u, pres);
        Call(ctx, base, s.frame, 0x82426260u, {title, pres, 1u});
        const uint32_t said = Call(ctx, base, s.frame, 0x82522CB0u, {tag, quest});
        if (title != 0u) {
          Call(ctx, base, s.frame, 0x82425F40u, {title, said});  // lhGuiElement_SetTextFromTag
        }
        DropString(ctx, base, s.frame, tag);
        const uint32_t goal = Element(ctx, base, s.frame, slot, 0x8349ABE4u, pres);
        Call(ctx, base, s.frame, 0x82426260u, {goal, pres, 1u});
        const uint32_t raw = Call(ctx, base, s.frame, 0x82522E48u, {objective, quest});
        const uint32_t wide = Call(ctx, base, s.frame, 0x8230E268u, {localised, quest, raw});
        Call(ctx, base, s.frame, 0x82287C28u, {line, wide});
        Call(ctx, base, s.frame, 0x82B3EF20u, {line, Address(0x820CD4BCu)});
        Call(ctx, base, s.frame, 0x82425DD0u, {goal, pres, line});  // lhGuiElement_SetText
        Call(ctx, base, s.frame, 0x821C67F8u, {line});
        Call(ctx, base, s.frame, 0x821C67F8u, {localised});

        const uint32_t again = primary_quest();
        const std::string region = again != 0u ? HandleText(base, LoadU32(base, again + 0x84u)) : std::string();
        const uint32_t level = LevelRecord(base);
        const std::string here = level != 0u ? HandleText(base, LoadU32(base, level + 0xACu)) : std::string();
        in_region = SameIgnoringCase(region, here) ? 1u : 0u;
      } else {
        Call(ctx, base, s.frame, 0x82426260u, {Element(ctx, base, s.frame, slot, 0x8349ABE0u, pres), pres, 0u});
        Call(ctx, base, s.frame, 0x82426260u, {Element(ctx, base, s.frame, slot, 0x8349ABE4u, pres), pres, 0u});
      }
    }

    const uint32_t image = Element(ctx, base, s.frame, slot, 0x8349ABE8u, pres);
    refii::vfs::StatePauseMapTexture(base, image, LoadU32(base, hash));
    {
      PPCContext turn = ctx;
      turn.r1.u32 = s.frame;
      turn.r3.u32 = image;
      turn.r4.u32 = pres;
      turn.f1.f64 = 0.0;
      turn.f2.f64 = 0.0;
      turn.f3.f64 = refii::mods::LoadF32(base, h + 84u) * 0.017453292f;
      refii::mods::CallGuest(turn, base, Address(0x824254C8u));
    }
    const uint32_t level = LevelRecord(base);
    if (level != 0u) {
      const uint32_t rect = level + 0x98u;
      const float dx = static_cast<float>(static_cast<int32_t>(LoadU32(base, rect + 8u) - LoadU32(base, rect)));
      const float dy = static_cast<float>(static_cast<int32_t>(LoadU32(base, rect + 12u) - LoadU32(base, rect + 4u)));
      refii::mods::StoreF32(base, h + refii::guest::Pick(56u, 52u), dx - dy >= 0.0f ? dx : dy);
    }
    Call(ctx, base, s.frame, 0x824252E8u, {at, image});
    refii::mods::StoreF32(base, h + 64u, refii::mods::LoadF32(base, at));
    refii::mods::StoreF32(base, h + 68u, 0.0f);
    refii::mods::StoreF32(base, h + 72u, refii::mods::LoadF32(base, at + 4u));
    refii::mods::StoreF32(base, h + 76u, 0.0f);
    Toggle(ctx, base, s.frame, Element(ctx, base, s.frame, slot, 0x8349AC04u, pres), pres, base[h + 244u]);
    if (base[h + 244u] != 0u) {
      Call(ctx, base, s.frame, 0x826A3658u, {h, pres, in_region});
    } else {
      base[h + 100u] = 1u;
    }
    const uint32_t tokens = base[h + 244u] != 0u && base[h + 245u] == 0u ? 1u : 0u;
    Call(ctx, base, s.frame, 0x826A2F98u, {h, pres, tokens});
  }

  if (base[h + 244u] == 0u) {
    CallSlot(ctx, base, s.frame, h + 252u, 2u, {});
    MakeString(ctx, base, s.frame, text, text + 0x30u, "Scene.Layer.Foreground.Pages.Contents.MiniMap.Legend");
    MakeString(ctx, base, s.frame, text2, text2 + 0xACu, "Mask");
    Call(ctx, base, s.frame, 0x828C3250u, {h + 252u, text2, text});
    DropString(ctx, base, s.frame, text2);
    DropString(ctx, base, s.frame, text);
  }
  Call(ctx, base, s.frame, 0x828C33F0u, {h + 252u, Address(0x8349AC08u)});
  StoreU32(base, h + 280u, 0xFFFFFFFFu);
  Said("pause root (0)", "OnEnter", h, LoadU32(base, h + 4u), map_shown);
}

// Update TU1 0x826A23F8 / disc 0x8240E7B0. Kept: the row list's per-tick restate, the tail of the base
// tick `sub_826D5D00` (0x826A2408 / disc `sub_82583560`: vf+40 → h+24, then that list's vf+8, 0x826D5D70 /
// 0x826D5D88) — `sub_8298DEF0` / 0x828427B8 ghosts "Kick" unless an online game has a second hero
// (`sub_823DF660(0, "Kick", …)` 0x8298DF64) and, through `sub_8298F578(list, sub_82448900(SaveMgr))`
// (0x8298DF7C / 0x8298DF88; disc 0x824EBF30 / 0x82843D18), "Save and Continue" and "Save and Quit" while
// no save can be made; the readouts `sub_826A4D50(h, h+4 != 1)` (0x826A2558 /
// 0x82411108: gold, renown and the hero's stats, SetText each tick); the marker pass `sub_826A3150`
// (0x826A25A0 / 0x8240F508) while h+100 is clear and the input block `**(G+68)` is not positive —
// its placer states each quest marker (gui_pause_hooks.cpp); the kick countdown h+280: at 0
// `LeaveSession(*(GAME+0x8C), 0)` + `ExitScreen(QM, 1)` (0x826A26A0 / 0x826A26B0), above 0 one less.
// the third open tick's slide, h+248 0 → 1 → 2 → 3 and at 2 `lhGuiElement_GoToSlideAndRestart(root,
// pres, 3)` (0x826A264C / 0x823F9140) — the root's resting slide, which Godot's book stands on.
// Dropped: the base tick's child widgets (`sub_826D5D00` 0x826D5D34..0x826D5D5C), the highlighted quest row's string
// params (0x826A24D0 / 0x826A252C — keyed to the guest's highlight, which Godot's cursor never
// moves), the scroll region's tick h+252 vf+12 (0x826A25B4), the minimap draw `sub_826A49E0`
// (0x826A25C8), and the slide's sound (`sub_82371E80(…, 0x83497DD0)` + `sub_82C05EF8`, 0x826A2664).
REX_EXTERN(rex_lhGuiPauseRootScreen_Update) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  const uint32_t g = Gui(base);
  CallSlot(ctx, base, s.frame, h + 24u, 2u, {});
  Call(ctx, base, s.frame, 0x826A4D50u, {h, LoadU32(base, h + 4u) != 1u ? 1u : 0u});
  const uint32_t block = LoadU32(base, g + 68u);
  const bool open = !Plausible(block) || static_cast<int32_t>(LoadU32(base, block)) <= 0;
  if (base[h + 100u] == 0u && open) {
    Call(ctx, base, s.frame, 0x826A3150u, {h});
  }
  if (open) {
    const int32_t tick = static_cast<int32_t>(LoadU32(base, h + 248u));
    if (tick == 2) {
      StoreU32(base, h + 248u, 3u);
      const uint32_t pres = GuestPresentation(ctx, base, s.frame);
      Call(ctx, base, s.frame, 0x82426CA8u, {PresentationRoot(ctx, base, s.frame, pres), pres, 3u});
    } else if (tick < 2) {
      StoreU32(base, h + 248u, static_cast<uint32_t>(tick + 1));
    }
  }
  const int32_t kick = static_cast<int32_t>(LoadU32(base, h + 280u));
  if (kick == 0) {
    Call(ctx, base, s.frame, 0x8233E668u, {LoadU32(base, Game(base) + 0x8Cu), 0u});  // LeaveSession
    Call(ctx, base, s.frame, 0x8236ED98u, {LoadU32(base, g + 28u), 1u});             // ExitScreen
    StoreU32(base, h + 280u, 0xFFFFFFFFu);
    REXLOG_INFO("Refii screen stub: pause root (0) — the kick countdown ended, session left");
  } else if (kick > 0) {
    StoreU32(base, h + 280u, static_cast<uint32_t>(kick - 1));
  }
  if (!g_pause_update_said) {
    g_pause_update_said = true;
    Said("pause root (0)", "Update", h);
  }
}

// OnMenuEvent TU1 0x826A26E8 / disc 0x8240EAA0: 140/141 sounds; type 2 / 300 rows to the hero's
// `sub_826A5FC0` / henchman's `sub_826A6610` (disc 0x82412378 / 0x824129C0), whose consequences are
// levers Godot posts: the tabs `PauseTab:<n>` (`lhGuiTabStrip_Push`), the Saving rows
// `PauseSaving:<p>:<row>` (DispatchPauseSaving), player two's Quit `PauseHenchmanQuit:<p>`
// (DispatchPauseHenchmanQuit, the guest's own arm), Kick `CoopKick`; then the
// debug rows' `sub_826A5170` / `5590` / `5A30` ("CYCLE the Theme", "Shop Screen", "Tag Test" — not
// spawned in a retail list). Nothing kept.
REX_EXTERN(rex_lhGuiPauseRootScreen_OnMenuEvent) {
  REX_FUNC_PROLOGUE();
  Said("pause root (0)", "OnMenuEvent", ctx.r3.u32, LoadU32(base, ctx.r4.u32 + 4u), LoadU32(base, ctx.r4.u32 + 8u));
}

// OnScreenAction TU1 0x826A2808 / disc 0x8240EBC0: the scroll input h+252 vf+16, A (Lua OnPressA +
// sound), B `sub_826D5A60` (ExitScreen at the menu's root, 0x826D5AA4), LB / RB `sub_826B8780` /
// `sub_826B8878` for player one (jump table cases 0x48 / 0x49, `cmpwi r5, 1`). Godot posts B as
// `PauseClose:<p>` and LB / RB as `PauseCycle:<p>:prev|next`. Nothing kept.
REX_EXTERN(rex_lhGuiPauseRootScreen_OnScreenAction) {
  REX_FUNC_PROLOGUE();
  Said("pause root (0)", "OnScreenAction", ctx.r3.u32, ctx.r4.u32, ctx.r5.u32);
}

// OnModalAnswer TU1 0x826A2950 / disc 0x8240ED08 (h, code, player), the box at QM+44 (jump table
// 0x826A2994 / 0x8240ED4C, index box − 23). Kept whole, as the answer's consequences:
// 23/24/25 → `sub_826A7738(h, code, box)` (0x826A29B0 / 0x82413988), re-expressed below without its
// sounds; 26/27 → the henchman's drop-out `sub_826A6E70(h, code, box)` (0x826A2A54 / 0x824130C0);
// 28 the kick: 36 → `sub_8233AC68(net)`, `sub_822C3F50(net, 1)`, `sub_82182C30(net, 1)`, h+280 = 5,
// `sub_8236FEC8(QM, 3)`; 37 → `sub_8233AC68(net)`, `sub_8236FE38(QM, 3)` (0x826A29BC..0x826A2A44).
//
// `sub_826A7738`: 46 → `sub_8236FEC8(QM, 3)`, then box 23 `ExitScreen(QM, 1)` +
// `sub_82445398(SaveMgr, 0, 0, 0, 1)` (0x826A7890 / 0x826A78B4); 24 ExitScreen +
// `sub_82445398(SaveMgr, 0, 0, 0, 0)` + `sub_82312530(GAME, "")` (0x826A7810 / 0x826A7854 /
// 0x826A787C); 25 ExitScreen + `sub_82312530(GAME, "")` (0x826A77B0 / 0x826A77FC); 47 →
// `sub_8236FE38(QM, 3)` (0x826A7770). Dropped: the confirm sound `sub_82371E80(…, 0x83497E10)` +
// `sub_82C05EF8` on 24 / 25 (0x826A7828 / 0x826A77C8).
REX_EXTERN(rex_lhGuiPauseRootScreen_OnModalAnswer) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const uint32_t code = ctx.r4.u32;
  const Scratch s = Claim(ctx);
  const uint32_t qm = LoadU32(base, Gui(base) + 28u);
  const uint32_t box = LoadU32(base, qm + 44u);
  const uint32_t game = Game(base);
  Said("pause root (0)", "OnModalAnswer", h, code, box);
  if (box >= 23u && box <= 25u) {
    if (code == 47u) {
      Call(ctx, base, s.frame, 0x8236FE38u, {qm, 3u});
      return;
    }
    if (code != 46u) {
      return;
    }
    Call(ctx, base, s.frame, 0x8236FEC8u, {qm, 3u});
    Call(ctx, base, s.frame, 0x8236ED98u, {qm, 1u});
    if (box == 23u || box == 24u) {
      Call(ctx, base, s.frame, 0x82445398u, {LoadU32(base, game + 0x94u), 0u, 0u, 0u, box == 23u ? 1u : 0u});
    }
    if (box == 24u || box == 25u) {
      MakeString(ctx, base, s.frame, s.data, s.data + 0x10u, "");
      Call(ctx, base, s.frame, 0x82312530u, {game, s.data});  // QuitToFrontEnd; it releases the string
    }
    REXLOG_INFO("Refii screen stub: pause root (0) — box {} answered yes: {}", box,
                box == 23u ? "saved, play resumes" : box == 24u ? "saved, quit to the title" : "quit to the title");
    return;
  }
  if (box == 26u || box == 27u) {
    Call(ctx, base, s.frame, 0x826A6E70u, {h, code, box});
    return;
  }
  if (box == 28u && (code == 36u || code == 37u)) {
    const uint32_t net = LoadU32(base, game + 0x8Cu);
    Call(ctx, base, s.frame, 0x8233AC68u, {net});
    if (code == 36u) {
      const uint32_t again = LoadU32(base, game + 0x8Cu);
      Call(ctx, base, s.frame, 0x822C3F50u, {again, 1u});
      Call(ctx, base, s.frame, 0x82182C30u, {again, 1u});
      StoreU32(base, h + 280u, 5u);
      Call(ctx, base, s.frame, 0x8236FEC8u, {qm, 3u});
    } else {
      Call(ctx, base, s.frame, 0x8236FE38u, {qm, 3u});
    }
  }
}

// OnExit TU1 0x826A2A60 / disc 0x8240EE18. Kept: a pending kick's `LeaveSession(*(GAME+0x8C), 0)` and
// h+280 = -1 (0x826A2A90); the legend scroll region's teardown `sub_828C3358(h+252)` + h+252 vf+8
// (0x826A2AA4 / 0x82833D40); the base exit `sub_826D5C30` (0x826A2AC0 / 0x82583490); every marker id in
// the tree at h+88 that is not -1 returned to the free list G+144
// (`stdListDword_InsertWithLengthCheck(G+144, *(G+148), &id)` 0x826A2B48 / 0x825F7B10, the walk
// `sub_8238FE78` / 0x82413CA8), the tree emptied (`sub_8238FF20(h+88, head->parent)` 0x826A2B6C /
// 0x823958C0, head relinked to itself, size 0); `sub_8244DAD8(*(*(GAME+0xA8)+4))` (0x826A2C7C /
// 0x824FACB8); the root's slide 3 `lhGuiElement_GoToSlide(root, pres, 3)` and its time
// `GuiElement_SetTime_p(root, pres, 0.0)` (0x826A2BFC / 0x826A2C68; disc 0x823F9040 / 0x823F92B8).
REX_EXTERN(rex_lhGuiPauseRootScreen_OnExit) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  const uint32_t g = Gui(base);
  const uint32_t game = Game(base);
  if (static_cast<int32_t>(LoadU32(base, h + 280u)) > 0) {
    Call(ctx, base, s.frame, 0x8233E668u, {LoadU32(base, game + 0x8Cu), 0u});
    StoreU32(base, h + 280u, 0xFFFFFFFFu);
  }
  Call(ctx, base, s.frame, 0x828C3358u, {h + 252u});
  CallSlot(ctx, base, s.frame, h + 252u, 2u, {});
  Call(ctx, base, s.frame, 0x826D5C30u, {h});

  const uint32_t tree = h + 88u;
  const uint32_t head = LoadU32(base, tree + 4u);
  const uint32_t it = s.data;          // {container, node}
  const uint32_t id = s.data + 8u;
  uint32_t returned = 0u;
  StoreU32(base, it, tree);
  StoreU32(base, it + 4u, LoadU32(base, head));
  for (uint32_t guard = 0u; LoadU32(base, it + 4u) != head && guard < 4096u; ++guard) {
    const uint32_t marker = LoadU32(base, LoadU32(base, it + 4u) + 16u);
    if (marker != 0xFFFFFFFFu) {
      StoreU32(base, id, marker);
      Call(ctx, base, s.frame, 0x82A97368u, {g + 144u, LoadU32(base, g + 148u), id});
      ++returned;
    }
    Call(ctx, base, s.frame, 0x8238FE78u, {it});
  }
  Call(ctx, base, s.frame, 0x8238FF20u, {tree, LoadU32(base, head + 4u)});
  StoreU32(base, head + 4u, head);
  StoreU32(base, tree + 8u, 0u);
  StoreU32(base, head, head);
  StoreU32(base, head + 8u, head);

  const uint32_t pres = GuestPresentation(ctx, base, s.frame);
  Call(ctx, base, s.frame, 0x82426BA8u, {PresentationRoot(ctx, base, s.frame, pres), pres, 3u});
  {
    PPCContext time = ctx;
    time.r1.u32 = s.frame;
    time.r3.u32 = PresentationRoot(ctx, base, s.frame, pres);
    time.r4.u32 = pres;
    time.f1.f64 = 0.0;
    refii::mods::CallGuest(time, base, Address(0x82426E20u));
  }
  Call(ctx, base, s.frame, 0x8244DAD8u, {LoadU32(base, LoadU32(base, game + 0xA8u) + 4u)});
  Said("pause root (0)", "OnExit", h, returned);
}

namespace refii::vfs {

bool DispatchStart(PPCContext& ctx, uint8_t* base, uint32_t player) {
  // The guest's Start, `sub_82371B10(G, player, 0)` (disc 0x82305D28), as the GUI input's raw pass
  // calls it (`CGUIInput_vf6`, 0x82403D50) with the pad's player (vf+16): its own gates (wheel and
  // spells closed, the co-op screen, the input block `*(G+68)`, the fade `sub_823CE8D0`), the lite
  // "GAME_PAUSED" box in a cutscene or conversation (`sub_82372118`, 0x82371CA4), the close from any
  // pause screen the player owns (`ExitScreen(QM, 1)`, 0x82371D60), or the open from play through
  // the player's GUI input vf+32 (`sub_826A2C88(root, isP1)`, 0x82371CEC / 0x82371CD0).
  const Scratch s = Claim(ctx);
  const uint32_t g = Gui(base);
  if (!Plausible(g) || !Plausible(LoadU32(base, g + 28u))) {
    REXLOG_INFO("Refii guest-tick pump: Start:{} — no in-game GUI, dropped", player);
    return true;
  }
  Call(ctx, base, s.frame, 0x82371B10u, {g, player, 0u});
  REXLOG_INFO("Refii guest-tick pump: Start:{} — the guest's Start ran", player);
  return true;
}

bool DispatchPauseClose(PPCContext& ctx, uint8_t* base, uint32_t player) {
  // The root's B at the top of its list: `sub_826D5A60` (case 0x25, 0x826A2944 / 0x825832B0) with the
  // menu at its root closes, `ExitScreen(QM, 1)` (0x826D5AA4 / 0x82336F98). Only the book's owner's
  // pad reaches the root (the co-op filter in `sub_824047E8`): owner h+4 0 is player one, 1 player two.
  const Scratch s = Claim(ctx);
  const uint32_t root = PauseRoot(ctx, base, s.frame);
  if (root == 0u) {
    REXLOG_INFO("Refii guest-tick pump: PauseClose:{} — no pause root, dropped", player);
    return true;
  }
  const uint32_t owner = LoadU32(base, root + 4u) == 1u ? 2u : 1u;
  if (player != owner) {
    REXLOG_INFO("Refii guest-tick pump: PauseClose:{} — the book is player {}'s, refused", player, owner);
    return true;
  }
  Call(ctx, base, s.frame, 0x8236ED98u, {LoadU32(base, Gui(base) + 28u), 1u});
  REXLOG_INFO("Refii guest-tick pump: PauseClose:{} — the book closed", player);
  return true;
}

bool DispatchPauseSaving(PPCContext& ctx, uint8_t* base, uint32_t player, uint32_t row) {
  // The hero list's Saving rows, {type 2, id row}, as `sub_826A5FC0` answers them (0x826A6470..
  // 0x826A65C4 / disc 0x82412828..0x82412974): 0 and 1 open the guest's confirm, `OpenModal(QM,
  // localised "GUI_SAVING_OVERWRITE_WARNING", 3, 23 | 24, 1, 0, 0)`; 2 quits at once when
  // `sub_82448900(SaveMgr)` (0x826A64A4 / 0x824EBF30) says nothing is held — `ExitScreen(QM, 1)` +
  // `sub_82312530(GAME, "")` — else opens 25 with "GUI_SAVING_PROGRESS_WARNING". The text is
  // localised by `sub_8230E0E8` (0x82301D40). The answer reaches OnModalAnswer above. The rows exist
  // only in the hero list (`sub_8298DF98`, owner h+4 == 0), which only player one's pad drives. Rows 0
  // and 1 are ghosted, and cannot be picked, while `sub_82448900(SaveMgr)` is 0 (`sub_8298F578`:
  // `sub_823DF660(0, name, held == 0, 0)` 0x8298F5D8 / 0x8298F61C, restated every tick by the list's vf+8).
  const Scratch s = Claim(ctx);
  const uint32_t root = PauseRoot(ctx, base, s.frame);
  if (root == 0u || row > 2u) {
    REXLOG_INFO("Refii guest-tick pump: PauseSaving:{}:{} — no pause root or no such row, dropped", player, row);
    return true;
  }
  if (player != 1u || LoadU32(base, root + 4u) != 0u) {
    REXLOG_INFO("Refii guest-tick pump: PauseSaving:{}:{} — the Saving rows are player one's, refused", player, row);
    return true;
  }
  const uint32_t qm = LoadU32(base, Gui(base) + 28u);
  const uint32_t game = Game(base);
  const uint32_t text = s.data;
  const uint32_t wide = s.data + 8u;
  uint32_t box = 23u + row;
  const bool held = (Call(ctx, base, s.frame, 0x82448900u, {LoadU32(base, game + 0x94u)}) & 0xFFu) != 0u;
  if (row < 2u && !held) {
    REXLOG_INFO("Refii guest-tick pump: PauseSaving:{}:{} — no save can be made, the row is ghosted: refused", player,
                row);
    return true;
  }
  if (row == 2u && !held) {
    Call(ctx, base, s.frame, 0x8236ED98u, {qm, 1u});
    MakeString(ctx, base, s.frame, text, text + 0x20u, "");
    Call(ctx, base, s.frame, 0x82312530u, {game, text});
    REXLOG_INFO("Refii guest-tick pump: PauseSaving:{}:2 — nothing held, quit to the title", player);
    return true;
  }
  MakeString(ctx, base, s.frame, text, text + 0x20u,
             row == 2u ? "GUI_SAVING_PROGRESS_WARNING" : "GUI_SAVING_OVERWRITE_WARNING");
  const uint32_t said = Call(ctx, base, s.frame, 0x8230E0E8u, {wide, text});
  Call(ctx, base, s.frame, 0x8236FA68u, {qm, said, 3u, box, 1u, 0u, 0u});  // lhGuiManager_OpenModal
  REXLOG_INFO("Refii guest-tick pump: PauseSaving:{}:{} — the guest's confirm, box {} (modal now {})", player, row,
              box, LoadU32(base, qm + 44u));
  return true;
}

bool DispatchPauseHenchmanQuit(PPCContext& ctx, uint8_t* base, uint32_t player) {
  // Player two's one way out, the henchman list's last row: `sub_8298EDE0` spawns "HenchmanQuit"
  // (`GUI_SCREEN_PAUSE_HENCHMAN_QUIT`, event type 300, `li r9,0x12C` 0x8298F204 / disc 0x828439A8) for a
  // local and an online player two alike. Its A is the root's OnMenuEvent `sub_826A26E8` → the henchman
  // arm `sub_826A6610(h, event, QM)` (0x826A27FC / disc 0x8240EBB4 → 0x824129C0), run here on the
  // event as the guest builds it ({CCharString name, type, id}): with a save named at SaveMgr+4 box 27
  // `GUI_SCREEN_HENCHMAN_PAUSE_QUIT_TEXT` with player two's experience and gold, without one box 26
  // `..._QUIT_WITHOUT_SAVES_TEXT` (they go to the hero), `lhGuiManager_OpenModal(QM, text, 3, 27|26,
  // 0, 0, 0)` (0x826A67CC / 0x826A6828 → 0x826A6928; disc 0x82412CD4). Its yes reaches OnModalAnswer
  // above (26/27 → `sub_826A6E70`). Refused unless the root is player two's (h+4 == 1) and player two
  // asked.
  const Scratch s = Claim(ctx);
  const uint32_t root = PauseRoot(ctx, base, s.frame);
  if (root == 0u) {
    REXLOG_INFO("Refii guest-tick pump: PauseHenchmanQuit:{} — no pause root, dropped", player);
    return true;
  }
  if (player != 2u || LoadU32(base, root + 4u) != 1u) {
    REXLOG_INFO("Refii guest-tick pump: PauseHenchmanQuit:{} — the root is player {}'s, refused", player,
                LoadU32(base, root + 4u) == 1u ? 2u : 1u);
    return true;
  }
  const uint32_t qm = LoadU32(base, Gui(base) + 28u);
  const uint32_t event = s.data;
  MakeString(ctx, base, s.frame, event, s.data + 0x20u, "HenchmanQuit");
  StoreU32(base, event + 4u, 300u);
  StoreU32(base, event + 8u, 0u);
  Call(ctx, base, s.frame, 0x826A6610u, {root, event, qm});
  DropString(ctx, base, s.frame, event);
  REXLOG_INFO("Refii guest-tick pump: PauseHenchmanQuit:{} — the henchman's quit ran (sub_826A6610), modal now {}",
              player, LoadU32(base, qm + 44u));
  return true;
}

}  // namespace refii::vfs
