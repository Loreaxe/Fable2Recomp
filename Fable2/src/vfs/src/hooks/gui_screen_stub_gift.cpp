// THE PORTED SCREENS (S7). Each handler slot below is a full replacement: it keeps the guest's
// statements of the screen's data and its game side effects, as direct calls, and drops what
// only draws or animates. Godot drives the rest through the levers at the end of each file
// (guest_tick_pump.cpp dispatches them). The handler objects stay registered.
//
// Slot layout (vtable): +8 OnEnter, +16 Update, +20 OnMenuEvent(h, event), +24
// OnScreenAction(h, code, player), +36 OnExit. TU1 / disc addresses on each function.
#include <algorithm>
#include <atomic>
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
#include "hooks/item_rows_stated.h"
#include "hooks/page_fills_stated.h"
#include "hooks/ppc_externs.h"
#include "vfs_hooks_private.h"

using namespace refii::vfs::stub;
using refii::guest::Address;
using refii::mods::LoadU32;
using refii::mods::StoreU32;

// ---- GIFT GIVING (25; disc 24) — vtable 0x820CF584 / 0x820C1DF8 ------------------------------
//
// h+24 the row list (`lhGuiGiftGoldScreen_PopulateMenu` 0x82430468 / 0x823A45F8), h+44 its filter (1
// gifts, 5 orb), h+48 the mode the openers write (0 gift, 1 orb), h+52 the book, h+96 the item page (its
// preview at h+128), h+352 the last highlighted row id, h+356 the orb recipient's name. Same offsets on
// both builds.
//
// Every slot is ported. OnExit (0x826B6190 / 0x8259B380) is one body shared with screens 26 and 27
// (gui_screen_stub_shared.cpp); the dtor (0x826C4260 / 0x825A0200) only frees. The rows' consequences
// arrive as the guest's own events: an item row's A as `Gift:<p>:item:<ticket>:<record>` (the pump's
// CommitItemThroughScreen pushes type 3 with the row's ticket), the gold row's as `MenuEvent` type 7;
// the boxes they open are answered by `QueryAnswer` through OnModalAnswer. Every slot runs for the screen's
// owner (OwnerScope): the hero-one slot holds the listed hero (ListedHeroScope, item_rows_stated.cpp — the
// populate's record `bl sub_82264230` 0x82430500 / disc 0x823A4690, key 2 for local player two's book and a
// joiner), and "the local player" answers player two on player two's screen — the recipient
// `sub_8230F540` (the local player's comp77 target, its inlined pick 0x8230F594 / 0x823031EC), the gold
// row's purse (`sub_821C7CE0` 0x826C48D8), the item's giver `sub_826C5408` (0x826C5434: the local
// player's hero's comp33, `lhGiftComponent_GiveGift`) and the gold's `sub_826C5258` (0x826C5294: its
// comp81; the morality it earns, `lhHeroStats_ModifyMorality`, reads the slot).

namespace {

constexpr uint32_t kGiftVtable = 0x820CF584u;

bool g_gift_update_said = false;

// The format markers the question's builder replaces, `%1` 0x820A3118 / disc 0x820A2D6C and `%2`
// 0x820A3FFC / 0x820A36C4; the empty wide text 0x82000AF8 / 0x82000AF4 and 0x82099218 / 0x82099278.
constexpr uint32_t kMarkerOne = 0x820A3118u;
constexpr uint32_t kMarkerTwo = 0x820A3FFCu;
constexpr uint32_t kEmptyWide = 0x82000AF8u;
constexpr uint32_t kEmptyWideChars = 0x82099218u;
constexpr const char* kDisableMenu = "g_Menu:SetExpandableMenuDisabled(true)";

/// `sub_8230E0E8(out, key)` / disc `sub_82301D40` on a key built from `text`: the localised wide text
/// at `out`. The key string is the callee's, as in the guest (no release at any of its sites).
uint32_t Localised(PPCContext& ctx, uint8_t* base, const Scratch& s, uint32_t out, uint32_t key, const char* text) {
  MakeString(ctx, base, s.frame, key, s.data + 0x80u, text);
  return Call(ctx, base, s.frame, 0x8230E0E8u, {out, key});
}

}  // namespace

// OnEnter TU1 0x826C4318 / disc 0x825A02B8. Kept, in the guest's order: the two formatter statements
// `lhGui_RunLuaString("g_MenuFormatter:FadeOutBottomItemEarly()", pres)` and `…FadeOutTopItemEarly()`
// (0x826C4368 / 0x826C4398; disc 0x825A0308 / 0x825A0338), pres = `*(*(G+56)+156)` (disc +152,
// 0x825A0304); for mode h+48 of 0 or 1 the filter h+44 = 1 or 5 (0x826C43C0) and the rows
// `lhGuiGiftGoldScreen_PopulateMenu(h+24)` (0x826C43C8 / 0x823A45F8) — any other mode populates nothing;
// the two pages h+96 and h+320 pushed (`sub_82B49750` 0x826C43EC / 0x826C4400; disc 0x8228A900) and the
// book `sub_82990610(h+52, &pages, "Foreground.Book", pres, 1)` (0x826C4434 / 0x828CE060), which runs
// each page's +4; the page vector's storage freed (0x826C444C). Dropped: the open sound, `h` vf+4
// (0x826C433C). ADDED, first, before the slot's hero reads: the GUI's owner stated as its opener's
// player (StateGuiOwner). Three openers: the item action states its player itself (0x8280559C / disc
// 0x827E3FE4) before its gift (0x82805C0C / 0x827E4654), and that stands — stated again here so the
// host has it before the slot's reads (gui_stated_screens.cpp StatedPausePlayerTwo); the wheel's gift
// (`bl sub_823C4868` 0x826AAC50 / disc `bl sub_824F9A98` 0x8258E390) states none — its owner is noted
// before the wheel closes (gui_screen_stub_shared.cpp RefiiWheelGiftOwner) and stated here; the menu
// box's orb gift (`sub_8281E410`, mode h+48 = 1, `lhGuiManager_SetScreen(QM, 0x19, 0)` 0x8281E554 /
// disc 0x18 at 0x82840614) states none and is stated player one's here: which player's menu box it
// answers is not read (an online orb recipient, h+356). Without a statement the screen inherited the
// last one. A DELIBERATE DEPARTURE from the
// guest: the handler takes player one's input alone (OnScreenAction `cmplwi cr6, r5, 1` / `bnelr`
// 0x826C4BC0 / disc 0x825A0B58); here the screen's owner drives it, player two included. Every slot
// runs for its owner (OwnerScope): the list, the recipient, the purse and the giving are hero two's on
// player two's screen.
REX_EXTERN(rex_lhGuiGiftGivingScreen_OnEnter) {
  REX_FUNC_PROLOGUE();
  const Scratch s = Claim(ctx);
  const uint32_t h = ctx.r3.u32;
  const uint32_t opener = TakeOpenerPlayer();
  const bool two = opener != 0u                  ? opener == 2u
                   : LoadU32(base, h + 48u) == 1u ? false
                                                  : refii::vfs::StatedPausePlayerTwo();
  StateGuiOwner(ctx, base, s.frame, two);
  OwnerScope owner(base, 25u);
  const uint32_t pages = s.data;         // {?, begin, end, capacity}
  const uint32_t item = s.data + 0x10u;  // the page pointer handed to the push
  const uint32_t name = s.data + 0x14u;  // "Foreground.Book"
  const uint32_t chunk = s.data + 0x100u;
  const uint32_t pres = BookPresentation(base);
  g_gift_update_said = false;

  RunMenuLua(ctx, base, s.frame, chunk, "g_MenuFormatter:FadeOutBottomItemEarly()", pres);
  RunMenuLua(ctx, base, s.frame, chunk, "g_MenuFormatter:FadeOutTopItemEarly()", pres);
  const uint32_t mode = LoadU32(base, h + 48u);
  if (mode <= 1u) {
    StoreU32(base, h + 44u, mode == 0u ? 1u : 5u);
    Call(ctx, base, s.frame, 0x82430468u, {h + 24u});
  }
  for (uint32_t i = 0; i < 16u; i += 4u) {
    StoreU32(base, pages + i, 0u);
  }
  for (uint32_t page : {0x60u, 0x140u}) {
    StoreU32(base, item, h + page);
    Call(ctx, base, s.frame, 0x82B49750u, {pages, item});
  }
  MakeString(ctx, base, s.frame, name, s.data + 0x80u, "Foreground.Book");
  Call(ctx, base, s.frame, 0x82990610u, {h + 52u, pages, name, pres, 1u});
  DropString(ctx, base, s.frame, name);
  const uint32_t storage = LoadU32(base, pages + 4u);
  if (storage != 0u) {
    Call(ctx, base, s.frame, 0x8221BD38u, {storage});  // Render_FreeD3DObject
  }
  Said("gift (25)", "OnEnter", h, mode, LoadU32(base, h + 44u));
}

// Update TU1 0x826C4568 / disc 0x825A0508. Kept: the highlight edge `sub_826C4468` / 0x825A0408 — the
// highlighted row (`lhGuiMenu_GetHighlightedItemData` 0x826C44E4, answered natively from the row Godot
// states); on a new id the item page's fill `sub_829AA588(h+96)` (0x826C44FC / 0x828FC2F8: name,
// description, value, the preview's Set) and h+352 = the id (0x826C4500); on a category row (type 301)
// the preview's Hide `sub_828C3FD8(h+128)` (0x826C4534 / 0x82834DA0) — and the gold text
// `sub_8246B5E8(pres, 0x8349B088, 1)` (0x826C45AC / 0x82837218 with 0x83498ED8). Dropped: the child tick
// `sub_826D5D00` (0x826C457C), the book's slide `sub_82990A98` (0x826C454C) and tick `sub_82990730`
// (0x826C458C).
REX_EXTERN(rex_lhGuiGiftGivingScreen_Update) {
  REX_FUNC_PROLOGUE();
  OwnerScope owner(base, 25u);
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  uint32_t type = 0u;
  uint32_t id = 0u;
  HighlightedRow(ctx, base, s.frame, s.data, s.data + 0x40u, &type, &id);
  if (id != LoadU32(base, h + 0x160u)) {
    // Stated as screen 25's "row" fill for the row's ticket (page_fills_stated.cpp): the name, the Gold row's
    // text and bag image, the preview.
    refii::vfs::OpenPageFill(25u, "row");
    Call(ctx, base, s.frame, 0x829AA588u, {h + 0x60u});
    refii::vfs::ClosePageFill(id);
    StoreU32(base, h + 0x160u, id);
  }
  if (type == 301u) {
    Call(ctx, base, s.frame, 0x828C3FD8u, {h + 0x80u});
  }
  Call(ctx, base, s.frame, 0x8246B5E8u, {BookPresentation(base), Address(0x8349B088u), 1u});
  RestatePurseForPlayerTwo(ctx, base, s.frame, BookPresentation(base), 0x8349B088u);
  if (!g_gift_update_said) {
    g_gift_update_said = true;
    Said("gift (25)", "Update", h, type, id);
  }
}

// OnMenuEvent TU1 0x826C45C8 / disc 0x825A0568 (h, event = {name, type +4, id +8}). Nothing while a
// query stands (`*(QM+40) == 3`, 0x826C45F0) or for a category row (type 301, 0x826C4654). Kept, in the
// guest's order, both arms whole:
//  - the question `text` empty (`lhWideString_CtorFromWStr` 0x826C4664 / disc 0x822D6B40), the partner's
//    name empty (inline at 0x826C4670..0x826C4694: the wide strings' count +1 and a null — the same
//    constructor on the empty text), the row's name copied from the event (`sub_821F0240` 0x826C46A0);
//  - the row's item: `sub_82880BC0(*(QM+60)+4, event id, &handle)` (0x826C46CC / disc `sub_827BE1E8`) and,
//    for a handle, its display name `sub_82764718(&tmp, item)` assigned over the row's (0x826C46E4 /
//    0x826C46F0; disc `sub_82741530`);
//  - by mode h+48 — 1 (orb): `text` = localised "GUI_GIFT_GIVING_SCREEN_GIVE_ORB_X_QUERY" (`sub_8230E0E8`
//    0x826C4734 / `sub_82301D40`), `%1` → the localised row name and `%2` → the recipient h+356 as wide
//    (`sub_82B3EB68` 0x826C477C / 0x826C47B8, `sub_82B3F968` 0x826C47A8); 0 (gift) with a co-op partner
//    (`sub_8230F540()` 0x826C47D0 / disc `sub_82303198`, the recipient): its name `sub_823C8510(&tmp, partner)`
//    (0x826C47E4 / `sub_828C8440`), `text` = "GUI_GIFT_GIVING_SCREEN_WOULD_YOU_LIKE" with `%1` → the row
//    name and `%2` → the partner's name (0x826C4860 / 0x826C4890); any other mode: nothing more
//    (0x826C4714 → the releases);
//  - the event filed as the box's subject, `lhCharString_Assign(QM+80, event)`, QM+84 = type, QM+88 = id
//    (0x826C48B0..0x826C48C0);
//  - any type but 7: `lhGuiManager_OpenModal(QM, copy of text, 3, 15, 0, 0, 0)` (0x826C4B80; disc
//    0x825A0B1C takes six — TU1's seventh `li r9, 0` at 0x826C4B7C is the one instruction the builds differ by);
//  - type 7, the gold row: hero one's (`sub_8229EE78(sub_821C7CE0(G))` 0x826C48D8 / 0x826C48DC) component
//    81 (the inline lookup 0x826C48E0..0x826C49BC, `refii_mod_component`) and its count `+12`
//    (0x826C49C0; a hero without the component reads as 0 held, where the guest would fault); mode 1: the step and
//    first value 10 with more than 10 held, else 1 (0x826C49EC..0x826C4A08), `text` =
//    "GUI_GIFT_GIVING_SCREEN_GIVE_ORB_QUERY" with `%1` → the recipient h+356; mode 0: both 1, `text` =
//    `sub_82B3F250(&text, localised "GUI_GIFT_GIVING_SCREEN_GOLD_QUERY", partner's name or empty)`
//    (0x826C4AD8 / disc `sub_82B39260`); then the statement
//    `lhGui_RunLuaString("g_Menu:SetExpandableMenuDisabled(true)", pres)` (0x826C4B08) and
//    `lhGuiManager_OpenNumericAdjuster(QM, copy of text, r5 = 15, r6 = min(held, 10), r7 = step,
//    r8 = held, r9 = first value, r10 = 1)` (0x826C4B28..0x826C4B48 / disc 0x82337EA0; no float argument);
//  - the releases: the handle (`lhTrackedObject_RemoveHandleRef` 0x826C4B94), the row name, the partner's
//    name, `text` (0x826C4BA0..0x826C4BB0).
// Dropped: types 141 / 140, `sub_82371E80(&ev, G, 0x83497DDC "SE_GUI_SLIDE_MENU_DOWN" / 0x83497DD8
// "SE_GUI_SLIDE_MENU_UP", 0)` (0x826C4610 / 0x826C463C; disc 0x825A05B0 / 0x825A05DC).
//
// Scratch (s.data +): 0x00 text, 0x04 the row name, 0x08 the partner's name, 0x0C / 0x10 the builder's
// two wide temporaries, 0x14 the localiser's output, 0x18 a key string, 0x1C the row name's copy, 0x20
// the box's copy of the text, 0x30 the 16-byte item handle, 0x80 literal bytes, 0x100 the chunk.
REX_EXTERN(rex_lhGuiGiftGivingScreen_OnMenuEvent) {
  REX_FUNC_PROLOGUE();
  OwnerScope owner(base, 25u);
  const uint32_t h = ctx.r3.u32;
  const uint32_t event = ctx.r4.u32;
  const Scratch s = Claim(ctx);
  const uint32_t text = s.data;
  const uint32_t name = s.data + 0x04u;
  const uint32_t partner = s.data + 0x08u;
  const uint32_t a = s.data + 0x0Cu;
  const uint32_t b = s.data + 0x10u;
  const uint32_t loc = s.data + 0x14u;
  const uint32_t key = s.data + 0x18u;
  const uint32_t copy = s.data + 0x1Cu;
  const uint32_t boxed = s.data + 0x20u;
  const uint32_t handle = s.data + 0x30u;
  const uint32_t g = Gui(base);
  const uint32_t qm = LoadU32(base, g + 28u);
  const uint32_t type = LoadU32(base, event + 4u);
  Said("gift (25)", "OnMenuEvent", h, type, LoadU32(base, event + 8u));
  if (LoadU32(base, qm + 40u) == 3u || type == 141u || type == 140u || type == 301u) {
    return;
  }

  Call(ctx, base, s.frame, 0x822D6408u, {text, Address(kEmptyWide)});
  Call(ctx, base, s.frame, 0x822D6408u, {partner, Address(kEmptyWide)});
  Call(ctx, base, s.frame, 0x821F0240u, {name, event});
  for (uint32_t i = 0; i < 16u; i += 4u) {
    StoreU32(base, handle + i, 0u);
  }
  Call(ctx, base, s.frame, 0x82880BC0u, {LoadU32(base, qm + 60u) + 4u, LoadU32(base, event + 8u), handle});
  if (LoadU32(base, handle) != 0u) {
    const uint32_t shown = Call(ctx, base, s.frame, 0x82764718u, {b, LoadU32(base, handle)});
    Call(ctx, base, s.frame, 0x822651A0u, {name, shown});
    DropString(ctx, base, s.frame, b);
  }

  const uint32_t mode = LoadU32(base, h + 48u);
  uint32_t partner_text = 0u;  // r27: the partner's name's buffer, set by the gift arm alone
  if (mode == 1u) {
    Call(ctx, base, s.frame, 0x822B88A8u,
         {text, Localised(ctx, base, s, loc, key, "GUI_GIFT_GIVING_SCREEN_GIVE_ORB_X_QUERY")});
    Call(ctx, base, s.frame, 0x821C67F8u, {loc});
    Call(ctx, base, s.frame, 0x822D6408u, {a, Address(kMarkerOne)});
    const uint32_t row = Call(ctx, base, s.frame, 0x821F0240u, {copy, name});
    const uint32_t shown = Call(ctx, base, s.frame, 0x8230E0E8u, {loc, row});
    Call(ctx, base, s.frame, 0x82B3EB68u, {text, a, shown});
    Call(ctx, base, s.frame, 0x821C67F8u, {loc});
    Call(ctx, base, s.frame, 0x821C67F8u, {a});
    Call(ctx, base, s.frame, 0x822D6408u, {b, Address(kMarkerTwo)});
    Call(ctx, base, s.frame, 0x82B3F968u, {a, h + 356u});
    Call(ctx, base, s.frame, 0x82B3EB68u, {text, b, a});
    Call(ctx, base, s.frame, 0x821C67F8u, {a});
    Call(ctx, base, s.frame, 0x821C67F8u, {b});
  } else if (mode == 0u) {
    const uint32_t other = Call(ctx, base, s.frame, 0x8230F540u, {});
    if (other != 0u) {
      Call(ctx, base, s.frame, 0x822B88A8u, {partner, Call(ctx, base, s.frame, 0x823C8510u, {loc, other})});
      Call(ctx, base, s.frame, 0x821C67F8u, {loc});
      Call(ctx, base, s.frame, 0x822B88A8u,
           {text, Localised(ctx, base, s, loc, key, "GUI_GIFT_GIVING_SCREEN_WOULD_YOU_LIKE")});
      Call(ctx, base, s.frame, 0x821C67F8u, {loc});
      Call(ctx, base, s.frame, 0x822D6408u, {b, Address(kMarkerOne)});
      const uint32_t row = Call(ctx, base, s.frame, 0x821F0240u, {copy, name});
      const uint32_t shown = Call(ctx, base, s.frame, 0x8230E0E8u, {loc, row});
      Call(ctx, base, s.frame, 0x82B3EB68u, {text, b, shown});
      Call(ctx, base, s.frame, 0x821C67F8u, {loc});
      Call(ctx, base, s.frame, 0x821C67F8u, {b});
      Call(ctx, base, s.frame, 0x822D6408u, {b, Address(kMarkerTwo)});
      Call(ctx, base, s.frame, 0x82B3EB68u, {text, b, partner});
      Call(ctx, base, s.frame, 0x821C67F8u, {b});
      partner_text = LoadU32(base, partner);
    }
  }

  if (mode <= 1u) {
    Call(ctx, base, s.frame, 0x822651A0u, {qm + 80u, event});
    StoreU32(base, qm + 84u, LoadU32(base, event + 4u));
    StoreU32(base, qm + 88u, LoadU32(base, event + 8u));
    if (type != 7u) {
      const uint32_t said = Call(ctx, base, s.frame, 0x82287C28u, {boxed, text});
      Call(ctx, base, s.frame, 0x8236FA68u, {qm, said, 3u, 15u, 0u, 0u, 0u});
      REXLOG_INFO("Refii screen stub: gift (25) — the item's question asked (query 15)");
    } else {
      const uint32_t hero = Call(ctx, base, s.frame, 0x8229EE78u, {Call(ctx, base, s.frame, 0x821C7CE0u, {g})});
      const uint32_t purse = Plausible(hero) ? refii_mod_component(base, hero, 81u) : 0u;
      const int32_t held = Plausible(purse) ? static_cast<int32_t>(LoadU32(base, purse + 12u)) : 0;
      uint32_t first = 0u;  // r30
      uint32_t step = 0u;   // r31
      if (mode == 1u) {
        first = step = held > 10 ? 10u : 1u;
        Call(ctx, base, s.frame, 0x822B88A8u,
             {text, Localised(ctx, base, s, loc, key, "GUI_GIFT_GIVING_SCREEN_GIVE_ORB_QUERY")});
        Call(ctx, base, s.frame, 0x821C67F8u, {loc});
        Call(ctx, base, s.frame, 0x822D6408u, {a, Address(kMarkerOne)});
        Call(ctx, base, s.frame, 0x82B3F968u, {b, h + 356u});
        Call(ctx, base, s.frame, 0x82B3EB68u, {text, a, b});
        Call(ctx, base, s.frame, 0x821C67F8u, {b});
        Call(ctx, base, s.frame, 0x821C67F8u, {a});
      } else {
        first = step = 1u;
        const uint32_t who = partner_text != 0u ? LoadU32(base, partner_text) : Address(kEmptyWideChars);
        const uint32_t asked = Localised(ctx, base, s, loc, key, "GUI_GIFT_GIVING_SCREEN_GOLD_QUERY");
        const uint32_t buffer = LoadU32(base, asked);
        Call(ctx, base, s.frame, 0x82B3F250u,
             {text, buffer != 0u ? LoadU32(base, buffer) : Address(kEmptyWideChars), who});
        Call(ctx, base, s.frame, 0x821C67F8u, {loc});
      }
      RunMenuLua(ctx, base, s.frame, s.data + 0x100u, kDisableMenu, BookPresentation(base));
      const uint32_t said = Call(ctx, base, s.frame, 0x82287C28u, {boxed, text});
      Call(ctx, base, s.frame, 0x8236FCA8u,
           {qm, said, 15u, static_cast<uint32_t>(std::min(held, 10)), step, static_cast<uint32_t>(held), first, 1u});
      REXLOG_INFO("Refii screen stub: gift (25) — the gold picker asked for (held {}, step {})", held, step);
    }
  }

  const uint32_t tracked = LoadU32(base, handle);
  if (tracked != 0u) {
    Call(ctx, base, s.frame, 0x82764DC0u, {tracked, handle});
  }
  StoreU32(base, handle, 0u);
  DropString(ctx, base, s.frame, name);
  Call(ctx, base, s.frame, 0x821C67F8u, {partner});
  Call(ctx, base, s.frame, 0x821C67F8u, {text});
}

// OnModalAnswer TU1 0x826C4CC0 / disc 0x825A0C58 (h, code, player), by the box mode `*(QM+40)`:
//  - 5, the numeric picker: its own input first, `sub_826B8EB0(*(QM+64), code, player)` (0x826C4CF0 /
//    disc `sub_8240CD98`) — handled, nothing more; else the statement
//    `lhGui_RunLuaString("g_Menu:SetExpandableMenuDisabled(true)", pres)` (0x826C4D24 / 0x825A0CBC, the
//    guest's own literal); code 47 the picker cancelled; code 46: the picker's first byte cleared
//    (0x826C4DC4) and the gold given, `sub_826C5258(h, *(picker+44))` (0x826C4DCC / 0x825A11F0);
//  - 3, the query: code 47 cancelled; code 46: mode h+48 1 the orb `sub_826C5578(h)` (0x826C4D94 /
//    0x825A1510), 0 the gift `sub_826C5408(h)` (0x826C4DA4 / 0x825A13A0), any other neither;
//  - after either 46: the box accepted, `lhGuiManager_ExitScreen(QM, 1)` (0x826C4DEC / 0x82336F98) and
//    the preview's release `sub_828C3FD8(h+128)` (0x826C4DF4 / 0x82834DA0).
// Dropped: the box sounds — `sub_8236FE38(QM, 5)` (0x826C4D4C), `sub_8236FE38(QM, 3)` (0x826C4D74) and
// `sub_8236FEC8(QM, mode)` (0x826C4DDC; disc 0x825A0CE4 / 0x825A0D0C / 0x825A0D74) keep their second
// half alone, `if (mode == *(QM+40)) lhGuiManager_ClearModalState(QM)` (AnswerBox).
REX_EXTERN(rex_lhGuiGiftGivingScreen_OnModalAnswer) {
  REX_FUNC_PROLOGUE();
  OwnerScope owner(base, 25u);
  const uint32_t h = ctx.r3.u32;
  const uint32_t code = ctx.r4.u32;
  const uint32_t player = ctx.r5.u32;
  const Scratch s = Claim(ctx);
  const uint32_t qm = LoadU32(base, Gui(base) + 28u);
  const uint32_t mode = LoadU32(base, qm + 40u);
  static std::atomic<bool> said{false};
  if (code == 46u || code == 47u || !said.exchange(true)) {
    Said("gift (25)", "OnModalAnswer", h, code, mode);
  }
  if (mode == 5u) {
    if ((Call(ctx, base, s.frame, 0x826B8EB0u, {LoadU32(base, qm + 64u), code, player}) & 0xFFu) != 0u) {
      return;
    }
    RunMenuLua(ctx, base, s.frame, s.data + 0x100u, kDisableMenu, BookPresentation(base));
    if (code == 47u) {
      AnswerBox(ctx, base, s.frame, qm, 5u);
      return;
    }
    if (code != 46u) {
      return;
    }
    const uint32_t picker = LoadU32(base, qm + 64u);
    base[picker] = 0u;
    Call(ctx, base, s.frame, 0x826C5258u, {h, LoadU32(base, picker + 44u)});
    REXLOG_INFO("Refii screen stub: gift (25) — the gold was given (sub_826C5258)");
  } else if (mode == 3u) {
    if (code == 47u) {
      AnswerBox(ctx, base, s.frame, qm, 3u);
      return;
    }
    if (code != 46u) {
      return;
    }
    const uint32_t kind = LoadU32(base, h + 48u);
    if (kind == 1u) {
      Call(ctx, base, s.frame, 0x826C5578u, {h});
    } else if (kind == 0u) {
      Call(ctx, base, s.frame, 0x826C5408u, {h});
    }
    REXLOG_INFO("Refii screen stub: gift (25) — the {} was given", kind == 1u ? "orb" : "item");
  } else {
    return;
  }
  AnswerBox(ctx, base, s.frame, qm, mode);
  Call(ctx, base, s.frame, 0x8236ED98u, {qm, 1u});
  Call(ctx, base, s.frame, 0x828C3FD8u, {h + 0x80u});
}

// OnScreenAction TU1 0x826C4BC0 / disc 0x825A0B58 (the guest's: player one only, `cmplwi r5, 1; bnelr`; jump table
// index code − 26): 26 / 27 the book's page turn (behind h+92), 36 Lua OnPressA + sound, 37 B
// `sub_826C4C50`. Godot posts B as `Gift:<p>:close` (DispatchGiftClose). Nothing kept.
REX_EXTERN(rex_lhGuiGiftGivingScreen_OnScreenAction) {
  REX_FUNC_PROLOGUE();
  Said("gift (25)", "OnScreenAction", ctx.r3.u32, ctx.r4.u32, ctx.r5.u32);
}

// ---- THE LEVERS -----------------------------------------------------------------------------

namespace refii::vfs {

bool DispatchGiftClose(PPCContext& ctx, uint8_t* base, uint32_t player) {
  // The handler's B — the guest's for player one alone (0x826C4BC0 / disc 0x825A0B58 `cmplwi r5, 1`),
  // the owner's here (the departure stated at OnEnter; Godot routes the owner's pad alone): the guest's
  // own helper `sub_826C4C50(h)` / 0x825A0BE8 whole — at the list's root (`sub_826C1D98` 0x826C4C78)
  // `ExitScreen(QM, 1)` and the preview's Hide `sub_828C3FD8(h+128)` (0x826C4C94 / 0x826C4C9C), then the
  // shared back `sub_826D5AF0(h)` (0x826C4CA4).
  const Scratch s = Claim(ctx);
  OwnerScope owner(base, 25u);
  const uint32_t h = ScreenHandler(ctx, base, s.frame, 25u, kGiftVtable);
  if (h == 0u) {
    REXLOG_INFO("Refii guest-tick pump: Gift:{}:close — no gift handler, dropped", player);
    return true;
  }
  Call(ctx, base, s.frame, 0x826C4C50u, {h});
  REXLOG_INFO("Refii guest-tick pump: Gift:{}:close — the guest's B ran (sub_826C4C50)", player);
  return true;
}

}  // namespace refii::vfs
