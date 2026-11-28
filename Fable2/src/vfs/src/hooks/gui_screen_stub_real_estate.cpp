// THE PORTED SCREENS (S7). Each handler slot below is a full replacement: it keeps the guest's
// statements of the screen's data and its game side effects, as direct calls, and drops what
// only draws or animates. Godot drives the rest through the levers at the end of each file
// (guest_tick_pump.cpp dispatches them). The handler objects stay registered.
//
// Slot layout (vtable): +8 OnEnter, +16 Update, +20 OnMenuEvent(h, event), +24
// OnScreenAction(h, code, player), +36 OnExit. TU1 / disc addresses on each function.
#include <cstdint>
#include <initializer_list>
#include <string>

#include <rex/logging.h>

#include "core/estate_owner_rule.h"
#include "core/guest_build.h"
#include "core/refii_mod.h"
#include "hooks/gui_screen_stub_support.h"
#include "hooks/gui_screen_stubs.h"
#include "hooks/page_fills_stated.h"
#include "hooks/ppc_externs.h"
#include "vfs_hooks_private.h"

using namespace refii::vfs::stub;
using refii::guest::Address;
using refii::mods::LoadU32;
using refii::mods::StoreU32;

PPC_EXTERN_FUNC(rex_lhGuiRealEstateScreen_OnMenuEvent);

// ---- REAL ESTATE (28; disc 27) — vtable 0x820CEA58 / 0x820C1654 ------------------------------
//
// h+24 the row list (vt 0x820EB114: +4 `sub_82998300` / 0x828ECC10, the offered options — each row
// `sub_826CE848(list, &textTag, icon, "Root", 0, event, pres, 0)` / 0x82408338, its id 0 and its
// identity the EVENT r8, 110..120; +8 a nullsub), h+44 the book, h+88 page 1 (vt 0x820EB80C: +4 the
// fill `sub_82998FB0`, +12 `sub_8299A728`, the rent / profit line restated), h+116 page 2, h+144..h+168
// the binding `sub_826B1A08` fills at OnEnter (hero, its comp69 and comp77, the sale sign, its comp142,
// the building, its comp67). Same offsets on both builds. No slot body is shared with another vtable.
//
// A row's name (String1) is its LOCALISED label cut to one byte a character (`sub_826CE848`:
// `sub_8230E0E8` localises the tag, `sub_82B3F898` / 0x82B398A8 keeps each unit's low byte, 0x82B3F904 /
// 0x82B3F910), so it is no identity outside Latin text (GitHub #81). The handler never reads it:
// OnMenuEvent takes the event's type alone (`lwz r4, 4(r4)` 0x826B2224 / 0x825974D4), and the row's
// lever carries that.
//
// Every slot is ported. OnEnter (TU1 0x826B18D0 / disc 0x82596B80), Update, OnMenuEvent (0x826B2210 /
// 0x825974C0) and OnModalAnswer (0x826B25E0 / 0x82597880) run under the screen's household wrappers
// (mod_events.cpp), which hold the hooks' names; OnExit (0x826B19D0 / 0x82596C80) is hooked here. The
// decision tree's boxes — the confirms 29..34, the buy-denied notice, the numeric boxes 35..37 with the
// limits `sub_826B1FC8` states — and every consequence are the guest's own helpers, reached through the
// function table and so through their hooks (Buy / Clear / MaritalHome / Sell / SetBudget / Message
// forward on a joiner, mod_events.cpp).

namespace {

constexpr uint32_t kRealEstateVtable = 0x820CEA58u;

bool g_real_estate_update_said = false;

// The frame's strings (offsets into the scratch): the box text, a replaced key, its value, the price,
// a C tag, the building's name tag, the copy the box takes; C text is staged at kChars.
constexpr uint32_t kText = 0x00u;
constexpr uint32_t kKey = 0x04u;
constexpr uint32_t kValue = 0x08u;
constexpr uint32_t kNumber = 0x0Cu;
constexpr uint32_t kTag = 0x10u;
constexpr uint32_t kName = 0x14u;
constexpr uint32_t kCopy = 0x18u;
constexpr uint32_t kChars = 0x100u;

/// A confirm with a plain localised text, as arms 112 and 119 open it: the tag localised
/// (`sub_8230E0E8(&text, &tag)`), copied (`sub_82287C28(&copy, &text)`) and handed to
/// `lhGuiManager_OpenModal(QM, &copy, 3, id, 1, 0, 0)`; the text released (`sub_821C67F8`).
void AskConfirm(PPCContext& ctx, uint8_t* base, const Scratch& s, uint32_t qm, const char* tag, uint32_t id) {
  MakeString(ctx, base, s.frame, s.data + kTag, s.data + kChars, tag);
  Call(ctx, base, s.frame, 0x8230E0E8u, {s.data + kKey, s.data + kTag});
  const uint32_t copy = Call(ctx, base, s.frame, 0x82287C28u, {s.data + kCopy, s.data + kKey});
  Call(ctx, base, s.frame, 0x8236FA68u, {qm, copy, 3u, id, 1u, 0u, 0u});
  Call(ctx, base, s.frame, 0x821C67F8u, {s.data + kKey});
}

/// `lhGuiRealEstateScreen_Message(h, &tag)` (TU1 0x826B1F48 / disc 0x825971F8), which takes the string.
void Tell(PPCContext& ctx, uint8_t* base, const Scratch& s, uint32_t h, const char* tag) {
  MakeString(ctx, base, s.frame, s.data + kKey, s.data + kChars, tag);
  Call(ctx, base, s.frame, 0x826B1F48u, {h, s.data + kKey});
}

/// THE ROWS OF A VIEWER WHO MAY NOT ACT HERE (the other hero's property, the two not partners:
/// coop_estate_owner.cpp): Back alone, as the row builder `sub_82998300` (disc `sub_828ECC10`) begins
/// and ends — `g_MenuFormatter:FadeOutBottomItemEarly()` on the book (0x82998334..0x82998340 / disc
/// 0x828ECC44..0x828ECC50), `g_Menu:CountSpawnedItems()` (0x82998EB0..0x82998ECC / 0x828ED7C0..
/// 0x828ED7DC), and while the current presentation's "Float1" is under 1.0
/// (`lhGuiPresentationSet_GetCurrent(*(G+56))` 0x82998EF4, `lhGuiPresentation_GetFloatParam` 0x82998F24,
/// 0.0 without one; `fcmpu` / `bge` 0x82998F40 / 0x82998F44; disc 0x828ED804 / 0x828ED834 / 0x828ED850 /
/// 0x828ED854) the Back row, `sub_826CE848(list, &"GUI_BACK", 0x8349D060, &"Root", 0, 120, pres, 0)`
/// (0x82998F8C; disc `sub_82408338` 0x828ED89C with 0x8349D008).
void RowsBackAlone(PPCContext& ctx, uint8_t* base, const Scratch& s, uint32_t list) {
  const uint32_t pres = BookPresentation(base);
  RunMenuLua(ctx, base, s.frame, s.data + 0x40u, "g_MenuFormatter:FadeOutBottomItemEarly()", pres);
  RunMenuLua(ctx, base, s.frame, s.data + 0x40u, "g_Menu:CountSpawnedItems()", pres);
  float float1 = 0.0f;
  const uint32_t current = GuestPresentation(ctx, base, s.frame);
  if (current != 0u) {
    MakeString(ctx, base, s.frame, s.data + kKey, s.data + kChars, "Float1");
    PPCContext work = ctx;
    work.r1.u32 = s.frame;
    work.r3.u64 = s.data + kKey;
    work.r4.u64 = current;
    if (refii::mods::CallGuest(work, base, Address(0x826B9210u))) {
      float1 = static_cast<float>(work.f1.f64);
    }
    DropString(ctx, base, s.frame, s.data + kKey);
  }
  if (float1 < 1.0f) {
    MakeString(ctx, base, s.frame, s.data + kText, s.data + kChars, "GUI_BACK");
    MakeString(ctx, base, s.frame, s.data + kTag, s.data + kChars + 0x40u, "Root");
    Call(ctx, base, s.frame, 0x826CE848u, {list, s.data + kText, Address(0x8349D060u), s.data + kTag, 0u, 120u, pres, 0u});
    DropString(ctx, base, s.frame, s.data + kText);
    DropString(ctx, base, s.frame, s.data + kTag);
  }
}

}  // namespace

namespace refii::vfs {

// OnEnter TU1 0x826B18D0 / disc 0x82596B80, run by its household wrapper (mod_events.cpp
// rex_lhGuiRealEstateScreen_OnEnter) with r3 the handler. Kept, in the guest's order: the GUI's owner
// stated — `lhGuiManager_FindScreenHandler(QM, 0)` (0x826B18F0 / 0x82596BA0) and its vf+32 (0x826B190C /
// 0x82596BBC) with `li r4, 0` (0x826B1900 / 0x82596BB0), player one: the guest's screen is hero one's
// alone (its binding's hero, 0x826B1A6C). It is player two's here when the wrapper binds hero two for
// local player two (`owner_two`): the GUI tick then serves player two's input object and the host
// routes player two's pad (StateGuiOwner); the handler's slots take any player's input (OnScreenAction
// 0x826B2A18 has no player test, its B 0x826B2AC0); the two pages pushed, h+88 and h+116
// (`sub_82B49750` 0x826B1950 / 0x826B1964; disc 0x8228A900); the book `sub_82990610(h+44, &pages, 0x8349AE38, pres, 1)` (0x826B1984 / 0x828CE060 with
// 0x83498CF4), which runs each page's vf+4 — the fills `sub_82998FB0` / `sub_8299B068`, stated by their
// own hooks (page_fills_stated.cpp); the rows, h+24 vf+4 = `sub_82998300` (0x826B19A8); the binding
// `sub_826B1A08(h)` (0x826B19B0 / 0x82596CB8), which writes h+144..h+168; the page vector's storage
// freed (0x826B19C0); and the book's slide `sub_82990A98(h+44, pres, 1)` (0x826B1994 / disc 0x828CE4E8):
// book+0x28 = 1 (0x82990AB4) and `lhGuiElement_GoToSlideAndRestart(*(book+4), pres, 1)` (0x82990AD0), the
// stated slide that brings the pages in from where the file parks them (Godot's book is built from the
// presentation's state at the open, so the slide must be stated inside OnEnter), with its cue 0x83497DF0
// "SE_GUI_BLADE_SLIDE_IN" guest-played. Dropped: the open sound, `h` vf+4 (0x826B1920 = sub_826D5BD8).
void RealEstateScreenOnEnter(PPCContext& ctx, uint8_t* base, bool owner_two) {
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  const uint32_t pages = s.data;         // {?, begin, end, capacity}
  const uint32_t item = s.data + 0x10u;  // the page pointer handed to the push
  const uint32_t pres = BookPresentation(base);
  g_real_estate_update_said = false;

  StateGuiOwner(ctx, base, s.frame, owner_two);
  for (uint32_t i = 0; i < 16u; i += 4u) {
    StoreU32(base, pages + i, 0u);
  }
  for (uint32_t page : {0x58u, 0x74u}) {
    StoreU32(base, item, h + page);
    Call(ctx, base, s.frame, 0x82B49750u, {pages, item});
  }
  Call(ctx, base, s.frame, 0x82990610u, {h + 0x2Cu, pages, Address(0x8349AE38u), pres, 1u});
  Call(ctx, base, s.frame, 0x82990A98u, {h + 0x2Cu, pres, 1u});
  const refii::vfs::EstateSession* session = refii::vfs::EstateSessionOf(h);
  if (session != nullptr && session->access == static_cast<uint8_t>(refii::coop::EstateAccess::kRefused)) {
    RowsBackAlone(ctx, base, s, h + 24u);
  } else {
    CallSlot(ctx, base, s.frame, h + 24u, 1u, {});
  }
  Call(ctx, base, s.frame, 0x826B1A08u, {h});
  const uint32_t storage = LoadU32(base, pages + 4u);
  if (storage != 0u) {
    Call(ctx, base, s.frame, 0x8221BD38u, {storage});  // Render_FreeD3DObject
  }
  Said("real estate (28)", "OnEnter", h, LoadU32(base, h + 0x90u), LoadU32(base, h + 0xA4u));
}

// OnMenuEvent TU1 0x826B2210 / disc 0x825974C0, run by its household wrapper with r3 the handler and
// r4 the event; the type alone is read (`lwz r4, 4(r4)` 0x826B2224). Jump table 0x826B2250 / 0x82597500,
// index type − 110, 32 entries, the same arms on both builds (disc is three instructions shorter: its
// `lhGuiManager_OpenModal` takes no r9). Kept, each arm call for call:
//   110 sell    `CECBuilding_SalePrice(h+168)` (0x826B22D4 / 0x8274DFD0); "GUI_SCREEN_REAL_ESTATE_CONFIRM_
//               SELL" localised (`sub_8230E0E8`); the key 0x820CE7E4 (disc 0x820C13D4) replaced by the
//               building's localised name (`sub_8282AE58(&tag, h+168)` 0x826B2314 / 0x8274D188,
//               `sub_82B3EB68(&text, &key, &value)` 0x826B2330 / 0x82B38B78) and the key 0x820CE804
//               (0x820C13F4) by the price (`sub_8230AE78(&w, price)` 0x826B235C / 0x82303F98); the copy
//               `sub_82287C28` and `lhGuiManager_OpenModal(QM, &copy, 3, 29, 1, 0, 0)` (0x826B23B4);
//   111 buy     `sub_826B2F20(h, 0)` (0x826B23CC / 0x825981C0);
//   112 rent out `sub_828D14E8(h+148, h+164)` owned (0x826B23DC / 0x828B0B38) → the confirm 33 with
//               "GUI_SCREEN_REAL_ESTATE_CONFIRM_RENT_OUT" (0x826B2440), else `sub_826B2F20(h, 2)` (0x826B2458);
//   113 / 114   the building's redecorating byte (h+168)+85 flipped and `CECBuilding_SetRedecorating(
//               building, flag)` (0x826B2470 / 0x826B2474; disc 0x827502F0), then `ExitScreen(QM, 1)` (0x826B25C0);
//   115 / 117 / 118 the numeric box `sub_826B1FC8(h, type)` (0x826B24F4 / 0x82597278);
//   116 marital home, owned → `lhGuiRealEstateScreen_Clear(h)`, `_MaritalHome(h)`, `ExitScreen(QM, 1)`,
//               `_Message(h, "GUI_SCREEN_REAL_ESTATE_PROPERTY_BOUGHT")` (0x826B24A0..0x826B24DC), else
//               `sub_826B2F20(h, 1)` (0x826B24E8);
//   119 evict   the confirm 34 with "GUI_SCREEN_REAL_ESTATE_CONFIRM_EVICT" (0x826B2550);
//   120 back    `ExitScreen(QM, 1)` (0x826B25C0).
// Dropped: 140 / 141, `sub_82371E80(&ev, G, "SE_GUI_SLIDE_MENU_UP" / "_DOWN", 0)` (0x826B25A0 / 0x826B2578;
// disc 0x82597844 / 0x8259781C). Every other type returns, as the table's default does.
void RealEstateScreenOnMenuEvent(PPCContext& ctx, uint8_t* base) {
  const uint32_t h = ctx.r3.u32;
  const uint32_t type = LoadU32(base, ctx.r4.u32 + 4u);
  const Scratch s = Claim(ctx);
  const uint32_t qm = LoadU32(base, Gui(base) + 28u);
  const uint32_t building = LoadU32(base, h + 0xA8u);
  const auto owned = [&]() {
    return (Call(ctx, base, s.frame, 0x828D14E8u, {LoadU32(base, h + 0x94u), LoadU32(base, h + 0xA4u)}) & 0xFFu) != 0u;
  };
  switch (type) {
    case 110u: {
      const uint32_t price = Call(ctx, base, s.frame, 0x8282BCA0u, {building});
      MakeString(ctx, base, s.frame, s.data + kTag, s.data + kChars, "GUI_SCREEN_REAL_ESTATE_CONFIRM_SELL");
      Call(ctx, base, s.frame, 0x8230E0E8u, {s.data + kText, s.data + kTag});
      Call(ctx, base, s.frame, 0x822D6408u, {s.data + kKey, Address(0x820CE7E4u)});
      const uint32_t name = Call(ctx, base, s.frame, 0x8282AE58u, {s.data + kName, LoadU32(base, h + 0xA8u)});
      uint32_t value = Call(ctx, base, s.frame, 0x8230E0E8u, {s.data + kValue, name});
      Call(ctx, base, s.frame, 0x82B3EB68u, {s.data + kText, s.data + kKey, value});
      Call(ctx, base, s.frame, 0x821C67F8u, {s.data + kValue});
      Call(ctx, base, s.frame, 0x821C67F8u, {s.data + kKey});
      Call(ctx, base, s.frame, 0x822D6408u, {s.data + kKey, Address(0x820CE804u)});
      value = Call(ctx, base, s.frame, 0x8230AE78u, {s.data + kNumber, price});
      Call(ctx, base, s.frame, 0x82B3EB68u, {s.data + kText, s.data + kKey, value});
      Call(ctx, base, s.frame, 0x821C67F8u, {s.data + kNumber});
      Call(ctx, base, s.frame, 0x821C67F8u, {s.data + kKey});
      const uint32_t copy = Call(ctx, base, s.frame, 0x82287C28u, {s.data + kCopy, s.data + kText});
      Call(ctx, base, s.frame, 0x8236FA68u, {qm, copy, 3u, 29u, 1u, 0u, 0u});
      Call(ctx, base, s.frame, 0x821C67F8u, {s.data + kText});
      break;
    }
    case 111u:
      Call(ctx, base, s.frame, 0x826B2F20u, {h, 0u});
      break;
    case 112u:
      if (owned()) {
        AskConfirm(ctx, base, s, qm, "GUI_SCREEN_REAL_ESTATE_CONFIRM_RENT_OUT", 33u);
      } else {
        Call(ctx, base, s.frame, 0x826B2F20u, {h, 2u});
      }
      break;
    case 113u:
    case 114u: {
      const uint32_t flag = base[building + 0x55u] == 0u ? 1u : 0u;
      base[building + 0x55u] = static_cast<uint8_t>(flag);
      Call(ctx, base, s.frame, 0x8282DFC0u, {building, flag});
      Call(ctx, base, s.frame, 0x8236ED98u, {LoadU32(base, Gui(base) + 28u), 1u});
      break;
    }
    case 115u:
    case 117u:
    case 118u:
      Call(ctx, base, s.frame, 0x826B1FC8u, {h, type});
      break;
    case 116u:
      if (owned()) {
        Call(ctx, base, s.frame, 0x826B3258u, {h});
        Call(ctx, base, s.frame, 0x826B35B8u, {h});
        Call(ctx, base, s.frame, 0x8236ED98u, {LoadU32(base, Gui(base) + 28u), 1u});
        Tell(ctx, base, s, h, "GUI_SCREEN_REAL_ESTATE_PROPERTY_BOUGHT");
      } else {
        Call(ctx, base, s.frame, 0x826B2F20u, {h, 1u});
      }
      break;
    case 119u:
      AskConfirm(ctx, base, s, qm, "GUI_SCREEN_REAL_ESTATE_CONFIRM_EVICT", 34u);
      break;
    case 120u:
      Call(ctx, base, s.frame, 0x8236ED98u, {qm, 1u});
      break;
    default:
      break;
  }
  Said("real estate (28)", "OnMenuEvent", h, type, LoadU32(base, h + 0x90u));
}

// OnModalAnswer TU1 0x826B25E0 / disc 0x82597880 (`lhGuiRealEstateScreen_Confirm`), run by its household
// wrapper with r3 the handler, r4 the code, r5 the player. Mode `*(QM+40)`, box id `*(QM+44)`, both read
// at entry (0x826B260C / 0x826B2610). Kept:
//   mode 5 (the numeric box): the widget's input `sub_826B8EB0(*(QM+64), code, player)` (0x826B2624 /
//     0x8240CD98) — taken (non-zero byte) ends the answer; else the chunk
//     `g_Menu:SetExpandableMenuDisabled(false)` on the book presentation (0x826B2658) and on as mode 3;
//   mode 3 (a confirm), codes 46 yes / 47 no only (0x826B2668..0x826B2674): by box id − 29 (jump table
//     0x826B2704 / 0x825979A4, 9 entries, the same arms on both builds), each arm first
//     `lhGuiManager_ClearModalState(QM)` behind `*(QM+40) == 3` (35..37: == 5), then on yes:
//       29 `_Sell(h)`; 30 `_Buy(h)`, `_Clear(h)` unless the building's flags (h+168)+28 have a bit of
//       0xEFE and not bit 0 (0x826B2784..0x826B27A0), `sub_826B33E8(h)`; 31 `_Buy`, `_Clear`,
//       `_MaritalHome`, `sub_826B33E8`; 32 `_Buy`, `CECPlayerProperties_SetRentedOut(h+148, h+164, 1)`,
//       `sub_826B33E8`; 33 `SetRentedOut(…, 1)`, `ExitScreen(QM, 1)`, `_Message(h, "…PROPERTY_RENTED")`;
//       34 `_Clear`, `ExitScreen(QM, 1)`, `_Message(h, "…PROPERTY_EVICTED")`; 35 the box's "open" byte
//       `**(QM+64)` cleared and `_SetBudget(h, box+44)`; 36 / 37 the byte cleared and the rent / profit
//       level written to (h+168)+92: v = box+44, a = v − min(v + (flags & 0xEFE ? 60 : 100), 0),
//       min(a, 100) (0x826B29C8..0x826B2A08);
//   mode 2, code 46: the box answered, `if (*(QM+40) == 2) ClearModalState(QM)` (0x826B26BC `sub_8236FEC8`).
// Dropped: the answer's sound, `sub_82371E80(&ev, G, 0x83497E4C "SE_GUI_MENU_BOX_CANCEL", 0)` on 47
// (0x826B2690 / disc 0x82597930) or 0x83497E50 "SE_GUI_MENU_BOX_SELECT" on 46 (0x826B26D4 / 0x82597974),
// and the select sound inside `sub_8236FEC8` (0x8236FEFC).
void RealEstateScreenOnModalAnswer(PPCContext& ctx, uint8_t* base) {
  const uint32_t h = ctx.r3.u32;
  const uint32_t code = ctx.r4.u32;
  const uint32_t player = ctx.r5.u32;
  const Scratch s = Claim(ctx);
  const uint32_t qm = LoadU32(base, Gui(base) + 28u);
  const uint32_t mode = LoadU32(base, qm + 40u);
  const uint32_t id = LoadU32(base, qm + 44u);
  const bool yes = code == 46u;
  if (mode == 5u) {
    if ((Call(ctx, base, s.frame, 0x826B8EB0u, {LoadU32(base, qm + 64u), code, player}) & 0xFFu) != 0u) {
      return;  // the widget took a step or a held stick: said by nothing, it comes every tick
    }
    RunMenuLua(ctx, base, s.frame, s.data + 0x40u, "g_Menu:SetExpandableMenuDisabled(false)", BookPresentation(base));
  } else if (mode != 3u) {
    if (mode == 2u && yes) {
      Said("real estate (28)", "OnModalAnswer", h, code, id);
      AnswerBox(ctx, base, s.frame, qm, 2u);
    }
    return;
  }
  if (static_cast<int32_t>(code) < 46 || static_cast<int32_t>(code) > 47) {
    return;
  }
  Said("real estate (28)", "OnModalAnswer", h, code, id);
  if (id < 29u || id > 37u) {
    return;
  }
  AnswerBox(ctx, base, s.frame, qm, id >= 35u ? 5u : 3u);
  if (!yes) {
    return;
  }
  const uint32_t building = LoadU32(base, h + 0xA8u);
  const auto rent_out = [&]() {
    Call(ctx, base, s.frame, 0x828D28D8u, {LoadU32(base, h + 0x94u), LoadU32(base, h + 0xA4u), 1u});
  };
  const auto leave = [&]() { Call(ctx, base, s.frame, 0x8236ED98u, {LoadU32(base, Gui(base) + 28u), 1u}); };
  switch (id) {
    case 29u:
      Call(ctx, base, s.frame, 0x826B3448u, {h});
      break;
    case 30u: {
      const uint32_t flags = LoadU32(base, building + 28u);
      Call(ctx, base, s.frame, 0x826B32B0u, {h});
      if ((flags & 0xEFEu) == 0u || (flags & 1u) != 0u) {
        Call(ctx, base, s.frame, 0x826B3258u, {h});
      }
      Call(ctx, base, s.frame, 0x826B33E8u, {h});
      break;
    }
    case 31u:
      Call(ctx, base, s.frame, 0x826B32B0u, {h});
      Call(ctx, base, s.frame, 0x826B3258u, {h});
      Call(ctx, base, s.frame, 0x826B35B8u, {h});
      Call(ctx, base, s.frame, 0x826B33E8u, {h});
      break;
    case 32u:
      Call(ctx, base, s.frame, 0x826B32B0u, {h});
      rent_out();
      Call(ctx, base, s.frame, 0x826B33E8u, {h});
      break;
    case 33u:
      rent_out();
      leave();
      Tell(ctx, base, s, h, "GUI_SCREEN_REAL_ESTATE_PROPERTY_RENTED");
      break;
    case 34u:
      Call(ctx, base, s.frame, 0x826B3258u, {h});
      leave();
      Tell(ctx, base, s, h, "GUI_SCREEN_REAL_ESTATE_PROPERTY_EVICTED");
      break;
    case 35u: {
      const uint32_t box = LoadU32(base, LoadU32(base, Gui(base) + 28u) + 64u);
      base[box] = 0u;
      Call(ctx, base, s.frame, 0x826B2B20u, {h, LoadU32(base, box + 44u)});
      break;
    }
    default: {  // 36, 37
      const uint32_t box = LoadU32(base, LoadU32(base, Gui(base) + 28u) + 64u);
      base[box] = 0u;
      const uint32_t target = LoadU32(base, h + 0xA8u);
      const int32_t v = static_cast<int32_t>(LoadU32(base, box + 44u));
      const int32_t t = v + ((LoadU32(base, target + 28u) & 0xEFEu) == 0u ? 100 : 60);
      const int32_t a = v - (t < 0 ? t : 0);
      const int32_t b = 100 - a;
      StoreU32(base, target + 92u, static_cast<uint32_t>((b < 0 ? b : 0) + a));
      break;
    }
  }
}

// Update TU1 0x826B1EF0 / disc 0x825971A0, run by its household wrapper (mod_events.cpp
// rex_lhGuiRealEstateScreen_Update) with r3 the handler. Kept: the book's tick `sub_82990730(h+44)`
// (0x826B1F0C / 0x828CE180) — the standing page's vf+12, on page 1 `sub_8299A728`, the one restatement
// of the rent / profit line after a numeric box commits — and the gold text `sub_8246B5E8(pres,
// 0x8349AE3C, 1)` (0x826B1F2C / 0x82837218 with 0x83498CF8). Dropped: the child tick `sub_826D5D00`
// (0x826B1F04; this list's vf+8 is a nullsub). The tick's page writes are stated (page_fills_stated.cpp,
// scope "tick", restated: only when they change); the pages' own fills state the rest at the open.
void RealEstateScreenUpdate(PPCContext& ctx, uint8_t* base) {
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  refii::vfs::OpenPageFill(28u, "tick");
  Call(ctx, base, s.frame, 0x82990730u, {h + 0x2Cu});
  refii::vfs::ClosePageFill(0u, true);
  Call(ctx, base, s.frame, 0x8246B5E8u, {BookPresentation(base), Address(0x8349AE3Cu), 1u});
  RestatePurseForPlayerTwo(ctx, base, s.frame, BookPresentation(base), 0x8349AE3Cu);
  if (!g_real_estate_update_said) {
    g_real_estate_update_said = true;
    Said("real estate (28)", "Update", h, LoadU32(base, h + 0x90u), LoadU32(base, h + 0xA4u));
  }
}

}  // namespace refii::vfs

// OnExit TU1 0x826B19D0 / disc 0x82596C80, kept whole: the base exit `sub_826D5C30(h)` (0x826B19E4 /
// 0x82583490) and the book's release `sub_829912B0(h+44)` (0x826B19EC / 0x828CED00: each page's vf+8,
// `sub_828C2F90`, a buffer free). It frees only and reads no household, so it runs outside the wrappers'
// scope.
//
// THE NUMERIC BOX EXITS THE SCREEN AND KEEPS IT. Budget, rent and profits (115 / 117 / 118) open
// `lhGuiManager_OpenNumericAdjuster` (TU1 0x8236FCA8 / disc 0x82337EA0): it stores the box id at QM+44 and
// the mode 5 at QM+40 (`stw r5,0x2C(r31)` / `li r11,5` / `stw r11,0x28(r31)` 0x8236FCE0..0x8236FCEC, disc
// 0x82337ED8..0x82337EE4), saves the current screen (`lwz r21,4(r10)` 0x8236FCFC / 0x82337EF4), calls
// `lhGuiManager_SetScreen(QM, 4, 0)` (0x8236FD00 / 0x82337EF8) — whose flag 0 runs the current screen's vf+36,
// this OnExit (`lwz r9,0x24(r10)` / `bctrl` 0x8236E684..0x8236E68C, disc 0x8233688C..0x82336894) — and puts the
// screen back (`stw r21,4(r5)` 0x8236FD64 / 0x82337F5C). The screen goes on: its Update and the box's answer
// (OnModalAnswer 36 / 37 writes the level) run on the same handler, so its session — a partner's lend of the
// owner's properties among it — stands. A confirm (`lhGuiManager_OpenModal`, flag 1) runs no OnExit.
REX_EXTERN(rex_lhGuiRealEstateScreen_OnExit) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  Call(ctx, base, s.frame, 0x826D5C30u, {h});
  Call(ctx, base, s.frame, 0x829912B0u, {h + 0x2Cu});
  const uint32_t qm = LoadU32(base, Gui(base) + 28u);
  const uint32_t box = qm != 0u && LoadU32(base, qm + 40u) == 5u ? LoadU32(base, qm + 44u) : 0u;
  if (box >= 35u && box <= 37u) {
    Said("real estate (28)", "OnExit under its numeric box", h, box);
    return;
  }
  refii::vfs::EstateSessionEnd(h);
  Said("real estate (28)", "OnExit", h);
}

// OnScreenAction TU1 0x826B2A18 / disc 0x82597CB8 (no player test; jump table 0x826B2A50 / 0x82597CF0,
// index code − 26, read from the image): 26 / 27 the open page's input (behind h+84, `sub_829907F0`
// 0x826B2B08), 36 a sound and the Lua OnPressA helper `sub_826D58F0` (0x826B2AA8), 37
// `ExitScreen(QM, 1)` (0x826B2AD0). Godot posts a row's A as `RealEstate:<p>:event:<110..120>`
// (DispatchRealEstateEvent) and B as `RealEstate:<p>:close` (DispatchRealEstateClose). Nothing kept.
REX_EXTERN(rex_lhGuiRealEstateScreen_OnScreenAction) {
  REX_FUNC_PROLOGUE();
  Said("real estate (28)", "OnScreenAction", ctx.r3.u32, ctx.r4.u32, ctx.r5.u32);
}

// ---- THE LEVERS -----------------------------------------------------------------------------

namespace refii::vfs {

bool DispatchRealEstateEvent(PPCContext& ctx, uint8_t* base, uint32_t player, uint32_t type) {
  // A row's commit, by the event its spawn stated (`sub_826CE848` r8): the ported OnMenuEvent, through
  // its household wrapper, on `{name, type, id}` with that type — 110 sell, 111 buy, 112 rent out, 113 / 114 redecorating on
  // / off, 115 budget, 116 marital home, 117 rent, 118 profits, 119 evict, 120 back (jump table
  // 0x826B2250 / 0x82597500, read from the image). Its name is left empty and its id 0: neither is
  // read. The confirm, the numeric box and the consequence are the guest's.
  const Scratch s = Claim(ctx);
  const uint32_t h = ScreenHandler(ctx, base, s.frame, 28u, kRealEstateVtable);
  if (h == 0u || type < 110u || type > 120u) {
    REXLOG_INFO("Refii guest-tick pump: RealEstate:{}:event:{} — {}", player, type,
                h == 0u ? "no real-estate handler, dropped" : "not a real-estate row's event (110..120), dropped");
    return true;
  }
  const uint32_t event = s.data;
  MakeString(ctx, base, s.frame, event, s.data + 0x40u, "");
  StoreU32(base, event + 4u, type);
  StoreU32(base, event + 8u, 0u);
  {
    PPCContext work = ctx;
    work.r1.u32 = s.frame;
    work.r3.u32 = h;
    work.r4.u32 = event;
    rex_lhGuiRealEstateScreen_OnMenuEvent(work, base);
  }
  DropString(ctx, base, s.frame, event);
  REXLOG_INFO("Refii guest-tick pump: RealEstate:{}:event:{} — the handler's own answer ran (buyer 0x{:08X}, "
              "building 0x{:08X})",
              player, type, LoadU32(base, h + 0x90u), LoadU32(base, h + 0xA4u));
  return true;
}

bool DispatchRealEstateClose(PPCContext& ctx, uint8_t* base, uint32_t player) {
  // The handler's B, any player's (0x826B2AC0 / disc 0x82597D60): `ExitScreen(QM, 1)`.
  const Scratch s = Claim(ctx);
  const uint32_t h = ScreenHandler(ctx, base, s.frame, 28u, kRealEstateVtable);
  if (h == 0u) {
    REXLOG_INFO("Refii guest-tick pump: RealEstate:{}:close — no real-estate handler, dropped", player);
    return true;
  }
  Call(ctx, base, s.frame, 0x8236ED98u, {LoadU32(base, Gui(base) + 28u), 1u});
  REXLOG_INFO("Refii guest-tick pump: RealEstate:{}:close — the screen left", player);
  return true;
}

}  // namespace refii::vfs
