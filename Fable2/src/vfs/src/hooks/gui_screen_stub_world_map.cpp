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
#include "hooks/page_fills_stated.h"
#include "hooks/gui_screen_stubs.h"
#include "hooks/ppc_externs.h"
#include "vfs_hooks_private.h"

PPC_EXTERN_FUNC(rex_lhGuiWorldMapScreen_AskStopTravel);

using namespace refii::vfs::stub;
using refii::guest::Address;
using refii::mods::LoadU32;
using refii::mods::StoreU32;

// ---- THE WORLD MAP (23; disc 22) — vtable 0x820CF704 / 0x820C1F78 ----------------------------
//
// h+24 the row list (`lhGuiWorldMapScreen_PopulateMenu` 0x829A2F40 / 0x828F3EC0), h+44 the book and its
// six pages (h+88 region info, h+128 region minimap, h+256 quest, h+284 job, h+312 sale, h+340 family),
// h+84 / h+85 / h+384 / h+388 the book's page state (read by Update and OnScreenAction alone), h+392 "the
// region's stop chooser is up", h+396 the last highlighted row id, h+400 the category the opener asked
// for. Same offsets on both builds.
//
// Every own slot is ported here. OnMenuEvent and OnModalAnswer are the map's decision tree with the
// guest's own boxes 3..13 — make primary, trails, fast travel and its refusals, the stop chooser — run on
// the guest's own rows Godot pushes (`MenuEvent` with the row's spawned type and id) and answered by
// `QueryAnswer` / `MenuBoxAnswer`; the tree's helpers stay guest calls.

namespace {

bool g_world_map_update_said = false;

/// A row type's book page and the scope its fill is stated under. The page objects are the handler's
/// own (OnEnter hands h+0x58, +0x80, +0x100, +0x11C, +0x138, +0x154 to the book in that order, TU1
/// 0x826C5B24..0x826C5B98 / disc 0x825A1ABC..0x825A1B30); each page's vf +12 is its fill — vtables TU1 / disc, +12: region info 0x820ED484 /
/// 0x820E820C `sub_829AB0E0` / `sub_828FD5A0`, quest 0x820ED850 / 0x820E852C `sub_829AF638` /
/// `sub_82901AA0`, job 0x820EDB2C / 0x820E8808 `sub_829AFE58` / `sub_829022C0`, sale 0x820ECE6C /
/// 0x820E7BF0 `sub_829AAB50` / `sub_828FD010`, family 0x820EDF70 / 0x820E8C48 `sub_829B04E0` /
/// `sub_82902948` (each pair identical). A region row also has the minimap (h+0x80, region mode's other
/// page; vtable 0x820ED590 / 0x820E8318, +12 `sub_829ABFC0` / `sub_828FE480`), run beside it: kMinimap.
struct RowPage {
  uint32_t type;
  uint32_t page;
  const char* scope;
};

constexpr RowPage kRowPages[] = {
    {41u, 0x58u, "row.region"}, {42u, 0x100u, "row.quest"}, {43u, 0x11Cu, "row.job"},
    {44u, 0x138u, "row.sale"},  {45u, 0x154u, "row.family"},
};

/// THE MINIMAP PAGE (h+0x80). Its fill reads the highlight itself (`lhGuiMenu_GetHighlightedItemData`
/// 0x829AC04C / 0x828FE50C), refuses all but a region row (`cmpwi cr6,r11,0x29` 0x829AC058) and runs only
/// when the region changed: `lwz r11,0x64(r16) ; cmpw ; beq` (0x829AC064..0x829AC06C), `stw r31,0x64(r16)`
/// (0x829AC074). The guest re-arms that edge with the page's vf +20, `li r11,-1 ; stw r11,0x64(r3) ; blr`
/// (0x829ABFA8 / 0x828FE468), when the book turns to the page (`sub_82990D78` tail-calls the page's +20,
/// 0x82990DD0); Godot turns the book, so the edge is re-armed here each time the cursor lands on a region
/// row, and the fill states that row afresh. It finds "the hero" as `sub_8229EE78(sub_821C7CE0(G))`
/// (0x829AC840 / 0x829AC850) — player one on the session host — so it runs under the screen's owner
/// (OwnerScope: player two's map shows player two's hero, dog, trail and quests). What it writes is
/// stated under "row.minimap" (page_fills_stated.cpp, minimap_page_stated.cpp).
constexpr RowPage kMinimap = {41u, 0x80u, "row.minimap"};

}  // namespace

// OnEnter TU1 0x826C5B00 / disc 0x825A1A98. Kept, in the guest's order: h+388 = 0 (0x826C5B28); the six
// pages pushed (`sub_82B49750` / 0x8228A900: h+88, +128, +256, +284, +312, +340, 0x826C5B44..0x826C5BA8)
// and the bytes h+260 / +288 / +316 / +344 cleared; the book `sub_82990610(h+44, &pages, 0x8349B0E0, pres,
// 1)` (0x826C5BE0 / 0x828CE060 with 0x83498F30), which runs each page's +4; hero one — the players'
// record `*(*(*(0x83496AB8)+12)+88)+4`, its first entry's weak handle at +104 (target `**(+108)`; a
// handle whose target is gone is released, `sub_82193E38(entry+104)` 0x826C5C88 / 0x821940C8) — when it
// stands (`+144 & 0x40`) and carries components (`+64` bit 26), its component 250 (the definition's
// slot byte `*(+140)+250` into the table at +72, else the table's search 0x826C5C94..0x826C5D50) handed
// to `sub_827FD9D8` (0x826C5D68 / 0x82890A80); the rows, h+24 vf+4 = PopulateMenu (0x826C5D7C); h+392 =
// 0; the tab strip `sub_826B8348(*(G+76), 2)` (0x826C5D90 / 0x8240B218); the asked category h+400 (jump
// table 0x826C5DBC / 0x825A1D54 on h+400 − 1: 1 → 0x8349D348, 2 → 0x8349D344, 3 → 0x8349D350, 4 →
// 0x8349D34C; disc 0x8349D2BC / 0x8349D2B8 / 0x8349D2C4 / 0x8349D2C0) → the row's emphasis
// `sub_823DFA98(23, name, 3, 0)` (0x826C5E04 / 0x8283FD80, `li r3, 0x16` on disc), then h+400 = 0; with
// tutorials on (`*(*(GAME+168)+4)+57`) `lhTutorials_Display(state, 147)` (0x826C5E2C / 0x821B5478); the
// gold text `sub_8246B5E8(pres, 0x8349B0E4, 1)` (0x826C5E48 / 0x82837218 with 0x83498F34); h+396 = -1; the
// page vector's storage freed (0x826C5E60). Dropped: the open sound, `h` vf+4 (0x826C5B1C). A component
// table without id 250 makes no call (the guest reads one entry past the table there).
REX_EXTERN(rex_lhGuiWorldMapScreen_OnEnter) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  const uint32_t pages = s.data;         // {?, begin, end, capacity}
  const uint32_t item = s.data + 0x10u;  // the page pointer handed to the push
  const uint32_t g = Gui(base);
  const uint32_t pres = BookPresentation(base);
  g_world_map_update_said = false;

  StoreU32(base, h + 0x184u, 0u);
  for (uint32_t i = 0; i < 16u; i += 4u) {
    StoreU32(base, pages + i, 0u);
  }
  for (uint32_t page : {0x58u, 0x80u, 0x100u, 0x11Cu, 0x138u, 0x154u}) {
    StoreU32(base, item, h + page);
    Call(ctx, base, s.frame, 0x82B49750u, {pages, item});
  }
  for (uint32_t flag : {0x104u, 0x120u, 0x13Cu, 0x158u}) {
    base[h + flag] = 0u;
  }
  Call(ctx, base, s.frame, 0x82990610u, {h + 0x2Cu, pages, Address(0x8349B0E0u), pres, 1u});

  const uint32_t root = LoadU32(base, Address(0x83496AB8u));
  const uint32_t players = LoadU32(base, LoadU32(base, LoadU32(base, root + 12u) + 0x58u) + 4u);
  const uint32_t entry = LoadU32(base, players);
  const uint32_t holder = LoadU32(base, entry + 0x6Cu);
  uint32_t hero = 0u;
  if (holder != 0u) {
    hero = LoadU32(base, holder);
    if (hero == 0u) {
      Call(ctx, base, s.frame, 0x82193E38u, {entry + 0x68u});
    }
  }
  if (hero != 0u && (base[hero + 0x90u] & 0x40u) != 0u && ((LoadU32(base, hero + 0x40u) >> 26) & 1u) != 0u) {
    const uint32_t definition = LoadU32(base, hero + 0x8Cu);
    const uint32_t begin = LoadU32(base, hero + 0x48u);
    uint32_t component = 0u;
    bool found = false;
    if (definition != 0u) {
      component = LoadU32(base, begin + 8u * base[definition + 0xFAu] + 4u);
      found = true;
    } else {
      const uint32_t end = LoadU32(base, hero + 0x4Cu);
      uint32_t at = begin;
      int32_t count = static_cast<int32_t>(end - begin) >> 3;
      while (count > 0) {
        const int32_t half = count >> 1;
        if (static_cast<int32_t>(LoadU32(base, at + 8u * static_cast<uint32_t>(half))) < 250) {
          at += 8u * static_cast<uint32_t>(half) + 8u;
          count -= half + 1;
        } else {
          count = half;
        }
      }
      if (at != end && static_cast<int32_t>(LoadU32(base, at)) <= 250) {
        component = LoadU32(base, at + 4u);
        found = true;
      }
    }
    if (found) {
      Call(ctx, base, s.frame, 0x827FD9D8u, {component});
    }
  }

  CallSlot(ctx, base, s.frame, h + 24u, 1u, {});
  StoreU32(base, h + 0x188u, 0u);
  Call(ctx, base, s.frame, 0x826B8348u, {LoadU32(base, g + 76u), 2u});
  const uint32_t asked = LoadU32(base, h + 0x190u);
  constexpr uint32_t kEmphasis[] = {0x8349D348u, 0x8349D344u, 0x8349D350u, 0x8349D34Cu};
  if (asked >= 1u && asked <= 4u) {
    Call(ctx, base, s.frame, 0x823DFA98u, {refii::guest::ScreenId(23u), Address(kEmphasis[asked - 1u]), 3u, 0u});
  }
  StoreU32(base, h + 0x190u, 0u);
  const uint32_t tutorials = Tutorials(base);
  const uint32_t state = Plausible(tutorials) ? LoadU32(base, tutorials + 4u) : 0u;
  if (Plausible(state) && base[state + 0x39u] != 0u) {
    Call(ctx, base, s.frame, 0x821B5260u, {state, 147u});
  }
  Call(ctx, base, s.frame, 0x8246B5E8u, {pres, Address(0x8349B0E4u), 1u});
  RestatePurseForPlayerTwo(ctx, base, s.frame, pres, 0x8349B0E4u);
  StoreU32(base, h + 0x18Cu, 0xFFFFFFFFu);
  const uint32_t storage = LoadU32(base, pages + 4u);
  if (storage != 0u) {
    Call(ctx, base, s.frame, 0x8221BD38u, {storage});  // Render_FreeD3DObject
  }
  Said("world map (23)", "OnEnter", h, asked, hero);
}

// Update TU1 0x826C5E70 / disc 0x825A1E08. Kept: the highlighted row (`lhGuiMenu_GetHighlightedItemData`
// 0x826C5EF0, answered natively from the row Godot states) and h+396 = its id on a change (0x826C5F78);
// on a quest row (type 42) with tutorials on (`*(*(GAME+0xA8)+4)+0x39`) tutorial 148,
// `lhTutorials_Display(state, 148)` (`sub_826C6698` case 42, 0x826C67AC..0x826C67BC / disc 0x825A2754);
// THE HIGHLIGHTED ROW'S PAGE FILL, the call the book's tick `sub_82990730` makes every Update for its
// page (`(*(*page + 12))(page)`, r3 the page alone, 0x829907D4): the page `sub_826C6698` picks by the
// row's type (jump table 0x826C6764: 42 → 2, 43 → 3, 44 → 4, 45 → 5; 41 the region pages) — kRowPages.
// Each fill reads the highlight itself (`lhGuiMenu_GetHighlightedItemData` 0x826C1B50 / 0x8259DB00,
// answered from the row Godot states), checks its type and finds its row by the id the population
// spawned: the quest and job ordinal in `lhWorldMap_BuildQuestList` / `BuildJobList` (0x829AF6FC /
// 0x829AFF1C), the sale ordinal in `BuildSalesList` (0x829AAC10), the family ordinal in the page's own
// `BuildFamilyList` vector at +32 (0x829B058C), the region `id - 999` into `sub_82518330` (0x829AB17C).
// What it writes by name is stated (page_fills_stated.cpp) under the row's scope and id, restated: posted
// when it changes. A region row's minimap page fill runs after it (kMinimap). The quest and job fills
// also mark the row seen, `quest+78 = 1` (0x829AF7D0 / 0x829B01A4), as the guest's own tick did for its
// cursor.
// THE STOP CHOOSER'S POLL while h+392 is set (0x826C6078..0x826C60F0) — the menu box controller
// `*(QM+56)`: once it is shut (+12 clear and its close timer +4 not above 0.0), h+392 = 0 and the box
// answered — the guest's `sub_8236FE38(QM, 4)` when cancelled (+13, 0x826C60DC / disc 0x82338030) else
// `sub_8236FEC8(QM, 4)` (0x826C60E4 / 0x823380C0), each without its sound (AnswerBox) — and, not
// cancelled, the chosen stop's travel `sub_826C8C98(h, box)` (0x826C60F0 / disc 0x825A4BC8). Dropped: the child tick `sub_826D5D00`
// (0x826C5E80); the pages' scroll resets `sub_828C3358` on a new row (0x826C5F54 / 0x826C5F74); the
// book's page state for a region row against a category row (0x826C5F90..0x826C5FF4), the pages' show /
// hide `sub_826C6840` / `sub_826C6698`, the book's clock and page index (0x8299076C / 0x82990774) and
// slide `sub_82990A98` (0x826C6038) — Godot turns the book's pages.
namespace {
// ONLY PLAYER ONE TRAVELS (coop_world_state.cpp): the region pages' "Travel" A-button is the document's own
// (worldmapscreen.bgf: `Page1` and `Page2` each carry `AButton` / `ButtonText` "Travel"
// GUI_SCREEN_WORLD_MAP_MISC_TRAVEL), which no guest code writes — the region fill `sub_829AB0E0` and the
// minimap fill name no A-button, and on the 360 it always stands. For a player who is not player one the
// region fill's statement says it is not shown: a mark of the fill (`shown <path> 0`, the kind and path the
// other pages' own hide states, page_fills_stated.cpp), the HOST'S statement of this rule, not a guest write.
// (JM1r3 made the guest's `sub_82426328(&name, pres, 0)` do it here; the run logged the region fill's
// write as "wrote N element(s) it did not find by name — not stated", so Godot never heard it.)
void HideRegionTravel() {
  static constexpr const char* kButtons[] = {"Foreground.Book.Pages.Page1.Contents.AButton",
                                             "Foreground.Book.Pages.Page2.Contents.AButton"};
  for (const char* name : kButtons) {
    refii::vfs::NotePageFillMark("shown", name, "0");
  }
  static bool s_said = false;
  if (!s_said) {
    s_said = true;
    REXLOG_INFO("Refii screen stub: world map (23) — the region pages' Travel A-button is stated not shown for a "
                "player who is not player one");
  }
}
}  // namespace

REX_EXTERN(rex_lhGuiWorldMapScreen_Update) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  uint32_t type = 0u;
  uint32_t id = 0u;
  HighlightedRow(ctx, base, s.frame, s.data, s.data + 0x40u, &type, &id);
  // The kept OnEnter states hero one's gold (0x826C5E38 `li r5,1` / disc 0x825A1DD0); player two's purse.
  RestatePurseForPlayerTwo(ctx, base, s.frame, BookPresentation(base), 0x8349B0E4u);
  const bool moved = id != LoadU32(base, h + 0x18Cu);
  if (moved) {
    StoreU32(base, h + 0x18Cu, id);
  }
  if (type == 42u) {
    const uint32_t tutorials = Tutorials(base);
    const uint32_t state = Plausible(tutorials) ? LoadU32(base, tutorials + 4u) : 0u;
    if (Plausible(state) && base[state + 0x39u] != 0u) {
      Call(ctx, base, s.frame, 0x821B5260u, {state, 148u});
    }
  }
  for (const RowPage& page : kRowPages) {
    if (page.type == type) {
      refii::vfs::OpenPageFill(23u, page.scope);
      CallSlot(ctx, base, s.frame, h + page.page, 3u, {});
      if (type == 41u && refii::vfs::MapTravelRefusedNow(base)) {
        HideRegionTravel();
      }
      refii::vfs::ClosePageFill(id, true);
      break;
    }
  }
  if (type == kMinimap.type) {
    OwnerScope owner(base, 23u, false);
    if (moved) {
      CallSlot(ctx, base, s.frame, h + kMinimap.page, 5u, {});
    }
    refii::vfs::OpenPageFill(23u, kMinimap.scope);
    CallSlot(ctx, base, s.frame, h + kMinimap.page, 3u, {});
    refii::vfs::ClosePageFill(id, true);
    // The minimap's statement, once per region it is made for (the fill re-arms on a new row and states
    // nothing again for the same region, 0x829AC064..0x829AC06C), so a run shows the page's source.
    const refii::vfs::PageFillClosed& closed = refii::vfs::LastPageFillClosed();
    static uint32_t s_said_row = 0xFFFFFFFFu;
    if (closed.posted && id != s_said_row) {
      s_said_row = id;
      REXLOG_INFO("Refii screen stub: world map (23) — region row {}: the minimap fill stated {}; map {}", id,
                  closed.counts,
                  closed.map.empty() ? std::string("none (no icons: the hero is not in this region, or it has no proper "
                                                   "minimap)")
                                     : closed.map);
    } else if (moved && !closed.posted && !closed.unchanged) {
      REXLOG_INFO("Refii screen stub: world map (23) — region row {}: the minimap fill stated nothing ({} write(s) "
                  "to elements it did not name)",
                  id, closed.unnamed);
    }
  }
  if (LoadU32(base, h + 0x188u) != 0u) {
    const uint32_t qm = LoadU32(base, Gui(base) + 28u);
    const uint32_t box = LoadU32(base, qm + 56u);
    const bool standing =
        !Plausible(box) || base[box + 12u] != 0u || refii::mods::LoadF32(base, box + 4u) > 0.0f;
    if (!standing) {
      StoreU32(base, h + 0x188u, 0u);
      AnswerBox(ctx, base, s.frame, qm, 4u);
      if (base[box + 13u] != 0u) {
        REXLOG_INFO("Refii screen stub: world map (23) — the stop chooser was cancelled");
      } else {
        Call(ctx, base, s.frame, 0x826C8C98u, {h, box});
        REXLOG_INFO("Refii screen stub: world map (23) — the chosen stop's travel ran (sub_826C8C98)");
      }
    }
  }
  if (!g_world_map_update_said) {
    g_world_map_update_said = true;
    Said("world map (23)", "Update", h, type, id);
  }
}

// OnScreenAction TU1 0x826C63C8 / disc 0x825A2360 (jump table index code − 26): 26 / 27 the page's
// scroll (behind h+84), 36 Lua OnPressA + sound; B (37: `sub_826C1D98` → `sub_826B8130(*(G+76))`, then
// `sub_826D5AF0(h)`, 0x826C6508) and LB / RB (72 / 73, player one, 0x826C64E0 / 0x826C64C4) are the spine
// calls Godot posts as `PauseBack` / `PauseCycle:<p>:…`. Nothing kept.
REX_EXTERN(rex_lhGuiWorldMapScreen_OnScreenAction) {
  REX_FUNC_PROLOGUE();
  Said("world map (23)", "OnScreenAction", ctx.r3.u32, ctx.r4.u32, ctx.r5.u32);
}

// OnMenuEvent TU1 0x826C6110 / disc 0x825A20A8, `(h, event)`; nothing while a box is up (`*(QM+40) != 1`,
// 0x826C6138). Kept: the event copied to QM+80 — `lhCharString_Assign(QM+80, event)` (0x826C6140 /
// 0x82265160), QM+84 = its type, QM+88 = its id (0x826C6148 / 0x826C6150) — and the row's arm (jump
// table 0x826C617C / 0x825A2114, type − 41, 101 entries read from both images): 41 the region's stop
// question `sub_826C7E90(h, event)` (0x826C6318 / 0x825A3DC0, under its hook
// rex_lhGuiWorldMapScreen_AskStopTravel), 42 the quest `sub_826C6940` (0x826C632C / 0x825A28D8), 43 the
// job `sub_826C6B10` (0x826C6340 / 0x825A2AA0), 44 the sale `sub_826C6CF0` (0x826C6354 / 0x825A2C80), 45
// the family member `sub_826C6F40` (0x826C6368 / 0x825A2E78); 46..139 return. Dropped: 140 / 141, the
// scroll sounds "SE_GUI_SLIDE_MENU_UP" / "_DOWN" (0x826C63B0 / 0x826C6388; disc 0x825A2348 / 0x825A2320).
REX_EXTERN(rex_lhGuiWorldMapScreen_OnMenuEvent) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const uint32_t event = ctx.r4.u32;
  const Scratch s = Claim(ctx);
  const uint32_t qm = LoadU32(base, Gui(base) + 28u);
  const uint32_t type = LoadU32(base, event + 4u);
  Said("world map (23)", "OnMenuEvent", h, type, LoadU32(base, event + 8u));
  if (LoadU32(base, qm + 40u) != 1u) {
    return;
  }
  Call(ctx, base, s.frame, 0x822651A0u, {qm + 80u, event});
  StoreU32(base, qm + 84u, LoadU32(base, event + 4u));
  StoreU32(base, qm + 88u, LoadU32(base, event + 8u));
  switch (type) {
    case 41u: {
      PPCContext ask = ctx;
      ask.r1.u32 = s.frame;
      ask.r3.u32 = h;
      ask.r4.u32 = event;
      rex_lhGuiWorldMapScreen_AskStopTravel(ask, base);
      break;
    }
    case 42u:
    case 43u:
    case 44u:
    case 45u:
      // ONLY PLAYER ONE SELECTS QUESTS AND TRAVELS: a quest, job, sale or family row's answer is the
      // primary-quest question (box 3..6, `lhGuiManager_OpenModal(QM, text, 3, 3, 1)` in `sub_826C6940`),
      // the trail or the travel question; for a player who is not player one the row is for reading only
      // (its A-button hidden, coop_world_state.cpp), and its A answers nothing.
      if (refii::vfs::MapTravelRefusedNow(base)) {
        REXLOG_INFO("Refii screen stub: world map (23) — row type {}'s A is player one's (primary, trail, travel): "
                    "nothing for this player",
                    type);
        break;
      }
      Call(ctx, base, s.frame, type == 42u ? 0x826C6940u : type == 43u ? 0x826C6B10u : type == 44u ? 0x826C6CF0u
                                                                                                    : 0x826C6F40u,
           {h, event});
      break;
    default: break;
  }
}

// OnModalAnswer TU1 0x826C6580 / disc 0x825A2518, `(h, code)`, by the box id `*(QM+44)` (jump table
// 0x826C65C0 / 0x825A2558, id − 3, 11 entries read from both images). Kept: 3..6 →
// `sub_826C72E0(h, code, id)` (0x826C65EC / 0x825A3210) and 8..11 → `sub_826C8100(h, code, id)`
// (0x826C6600 / 0x825A4030), r3 / r4 as the slot got them and r5 the id (`lwz r5, 0x2C(r10)` 0x826C6598);
// 12 with code 36 → the box answered, the guest's `sub_8236FEC8(QM, 2)` (0x826C6624) without its sound;
// 13 with code 37 → answered, `sub_8236FE38(QM, 3)` (0x826C6680) without its sound; 13 with code 39 →
// answered, `sub_8236FEC8(QM, 3)` (0x826C6650) without its sound, then
// `Lua_Global_LoadFailQuestSave(*(GAME+148))` (0x826C6664 / 0x824ECCB8; GAME = `*(*(0x83496AB8)+12)`,
// `lwz r3, 0x94(r10)` on both builds); 7 and every other id return.
REX_EXTERN(rex_lhGuiWorldMapScreen_OnModalAnswer) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const uint32_t code = ctx.r4.u32;
  const Scratch s = Claim(ctx);
  const uint32_t qm = LoadU32(base, Gui(base) + 28u);
  const uint32_t id = LoadU32(base, qm + 44u);
  Said("world map (23)", "OnModalAnswer", h, code, id);
  if (id >= 3u && id <= 6u) {
    Call(ctx, base, s.frame, 0x826C72E0u, {h, code, id});
  } else if (id >= 8u && id <= 11u) {
    Call(ctx, base, s.frame, 0x826C8100u, {h, code, id});
  } else if (id == 12u) {
    if (code == 36u) {
      AnswerBox(ctx, base, s.frame, qm, 2u);
    }
  } else if (id == 13u) {
    if (code == 37u) {
      AnswerBox(ctx, base, s.frame, qm, 3u);
    } else if (code == 39u) {
      AnswerBox(ctx, base, s.frame, qm, 3u);
      Call(ctx, base, s.frame, 0x82449768u, {LoadU32(base, Game(base) + 148u)});
      REXLOG_INFO("Refii screen stub: world map (23) — the failed quest's save reload ran");
    }
  }
}

// OnExit TU1 0x826C5AC8 / disc 0x825A1A60, kept whole: the book's release `sub_829912B0(h+44)`
// (0x826C5AE0 / 0x828CED00: each page's vf+8) and the base exit `sub_826D5C30(h)` (0x826C5AE8 /
// 0x82583490).
REX_EXTERN(rex_lhGuiWorldMapScreen_OnExit) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  Call(ctx, base, s.frame, 0x829912B0u, {h + 0x2Cu});
  Call(ctx, base, s.frame, 0x826D5C30u, {h});
  Said("world map (23)", "OnExit", h);
}
