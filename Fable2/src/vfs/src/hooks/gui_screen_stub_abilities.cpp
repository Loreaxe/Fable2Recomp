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
PPC_EXTERN_FUNC(rex_lhGuiMenu_GetHighlightedItemData);

using namespace refii::vfs::stub;
using refii::guest::Address;
using refii::mods::LoadU32;
using refii::mods::StoreU32;

// ---- ABILITIES (14; disc 13) — vtable 0x820CF4BC / 0x820C1D30 --------------------------------
//
// h+4 the owner (copied from the pause root: 0 player one), h+24 the row list (vt 0x820EC238: +4
// populate `lhGuiAbilitiesScreen_PopulateMenu` 0x829A45A0 / 0x828F54C0), h+44 the owner's hero, h+48
// the book, pages h+92 / h+136 / h+164, h+197 "tutorial 120 standing", h+200 the will-assign widget
// (`lhGuiWillAssignWidget`, its stick latch at h+332), h+620 the blade's mode byte. The fields are at
// the same offsets on both builds. Learn and discard are the `Ability:<p>:…` lever (DispatchAbility).

namespace {

bool g_abilities_update_said = false;

/// The abilities row's discard-gate answer for Godot (payload_bridge.h key 20).
constexpr uint32_t kAbilityDiscardGate = 20u;

/// THE GUEST'S DISCARD GATE for one abilities row, run as its callers run it — the page fill `sub_826C2F98`
/// (0x826C3870 / disc 0x8259F810) and the Y arms `sub_826C2D40` / `sub_826C2E68` (0x826C2DC0 / 0x826C2EF0,
/// disc 0x8259ED70 / 0x8259EE98): the info `sub_82896868` / 0x828FAD28 filled from the row's record id by
/// `sub_82896900(info, id, hero flag)` / 0x828FADC0 (+0 the Type, +28 the level held, +32 the flag), the
/// gate `sub_826C3A30(h, info)` / 0x8259F9D0 (the hero's component 33 byte +46 and `+28 > 0`, 0x826C3BBC /
/// 0x826C3BD4; r3 unread), then the info's strings released as those callers do (+16 and +12
/// `lhCharString_Dtor` 0x82214DD8, +8 0x821C67F8, +4). `info` is 0x24 bytes of the caller's scratch.
bool RunDiscardGate(PPCContext& ctx, uint8_t* base, uint32_t frame, uint32_t info, uint32_t h, uint32_t row_id,
                    uint32_t flag, uint32_t* type) {
  Call(ctx, base, frame, 0x82896868u, {info});
  Call(ctx, base, frame, 0x82896900u, {info, row_id, flag});
  const bool open = (Call(ctx, base, frame, 0x826C3A30u, {h, info}) & 0xFFu) != 0u;
  if (type != nullptr) {
    *type = LoadU32(base, info);
  }
  Call(ctx, base, frame, 0x82214DD8u, {info + 16u});
  Call(ctx, base, frame, 0x82214DD8u, {info + 12u});
  Call(ctx, base, frame, 0x821C67F8u, {info + 8u});
  Call(ctx, base, frame, 0x82214DD8u, {info + 4u});
  return open;
}

}  // namespace

// OnEnter TU1 0x826C2118 / disc 0x8259E0C8. Kept, in the guest's order: h+197 = 1; h+4 = the pause
// root's owner (`FindScreenHandler(QM, 0)+4`, 0x826C2150 / 0x8259E100); h+44 = the owner's hero
// (`lhEntity_GetModel` / `GetRenderObject` on the roster, 0x826C2194 / 0x826C219C); the rows h+24 vf+4
// (0x826C21B8 / 0x8259E168); the three pages pushed (`sub_82B49750` / 0x8228A900), h+168 = 0, h+192 = the
// page count; the book `sub_82990610(h+48, &pages, "Foreground.Book", pres, 1)` (0x826C225C / 0x828CE060),
// which runs every page's +4 reader; the tab strip `sub_826B8348(*(G+76), 3)` (0x826C226C / 0x8240B218);
// the gold text `sub_8246B5E8(pres, 0x8349AFF8, h+4 == 0)` (0x826C2290 / 0x82837218 with 0x83498E48); with
// tutorials on (`*(tut+4)+56`), `CompleteTutorial(tut, 119)` 0x82450918 / 0x824FDAF8, `sub_8244DAD8`,
// `lhTutorials_Display(state, 120)` and h+197 = 0 when `sub_8244DE40` says none stands (0x826C22B8..
// 0x826C22E4). Dropped: the open sound, vf+4 (0x826C2134).
REX_EXTERN(rex_lhGuiAbilitiesScreen_OnEnter) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  const uint32_t pages = s.data;         // {?, begin, end, capacity}
  const uint32_t item = s.data + 0x10u;  // the page pointer handed to the push
  const uint32_t text = s.data + 0x20u;  // a CCharString handle and its characters
  const uint32_t g = Gui(base);
  const uint32_t pres = BookPresentation(base);
  g_abilities_update_said = false;

  base[h + 197u] = 1u;
  PPCContext find = ctx;
  find.r1.u32 = s.frame;
  find.r3.u32 = LoadU32(base, g + 28u);
  find.r4.u32 = 0u;
  rex_lhGuiManager_FindScreenHandler(find, base);
  const uint32_t root = find.r3.u32;
  const uint32_t owner = Plausible(root) ? LoadU32(base, root + 4u) : 0u;
  StoreU32(base, h + 4u, owner);
  const uint32_t game = Game(base);
  const uint32_t holder = Plausible(game) ? LoadU32(base, game + 88u) : 0u;
  const uint32_t party = Plausible(holder) ? LoadU32(base, holder + 4u) : 0u;
  const uint32_t roster = Plausible(party) ? LoadU32(base, party) : 0u;
  StoreU32(base, h + 44u, Call(ctx, base, s.frame, owner != 0u ? 0x828E5A78u : 0x8222C1B8u, {roster}));
  CallSlot(ctx, base, s.frame, h + 24u, 1u, {});

  for (uint32_t i = 0; i < 16u; i += 4u) {
    StoreU32(base, pages + i, 0u);
  }
  for (uint32_t page : {92u, 136u, 164u}) {
    StoreU32(base, item, h + page);
    Call(ctx, base, s.frame, 0x82B49750u, {pages, item});
  }
  base[h + 168u] = 0u;
  const uint32_t begin = LoadU32(base, pages + 4u);
  StoreU32(base, h + 192u, begin != 0u ? (LoadU32(base, pages + 8u) - begin) >> 2 : 0u);
  MakeString(ctx, base, s.frame, text, text + 0x10u, "Foreground.Book");
  Call(ctx, base, s.frame, 0x82990610u, {h + 48u, pages, text, pres, 1u});
  Call(ctx, base, s.frame, 0x826B8348u, {LoadU32(base, g + 76u), 3u});
  Call(ctx, base, s.frame, 0x8246B5E8u, {pres, Address(0x8349AFF8u), owner == 0u ? 1u : 0u});

  const uint32_t tutorials = Tutorials(base);
  const uint32_t state = Plausible(tutorials) ? LoadU32(base, tutorials + 4u) : 0u;
  if (Plausible(state) && base[state + 56u] != 0u) {
    Call(ctx, base, s.frame, 0x82450918u, {tutorials, 119u});  // Lua_TutorialManager_CompleteTutorial
    Call(ctx, base, s.frame, 0x8244DAD8u, {state});
    Call(ctx, base, s.frame, 0x821B5260u, {state, 120u});  // lhTutorials_Display
    if ((Call(ctx, base, s.frame, 0x8244DE40u, {state}) & 0xFFu) == 0u) {
      base[h + 197u] = 0u;
    }
  }
  DropString(ctx, base, s.frame, text);
  if (begin != 0u) {
    Call(ctx, base, s.frame, 0x8221BD38u, {begin});  // Render_FreeD3DObject
  }
  Said("abilities (14)", "OnEnter", h, owner, base[h + 197u]);
}

// Update TU1 0x826C23A8 / disc 0x8259E358. Kept: with the blade up (h+620), only the will-assign
// widget's stick latch reset `sub_822CFC78(h+332)` (0x826C23CC / 0x822D03F8), the input state the kept
// widget input reads. Without it, the highlighted ability's tutorial, keyed to the highlighted row
// (`lhGuiMenu_GetHighlightedItemData(*(QM+60))` 0x826C1B50 / 0x8259DB00, answered natively from the row
// Godot states): the edge `sub_826C3C78`'s game half — h+196 = the row is an ability (type not 301 / 23,
// 0x826C3CFC), on a new id with tutorials on (`*(tut+4)+56`) the 2 s timer `sub_828C36D0(h+628, 2.0)`
// (0x826C3E0C / 0x82834498), on a non-ability row the timer cleared (0x826C3E2C), h+624 = the id
// (0x826C3EB0) — then, once the timer ran out (`sub_828C3748` 0x826C23F8 / 0x82834510) and tutorial 120 no
// longer stands (h+197, `sub_8244DE40`), h+628 / h+629 = 0 and `sub_826C3FD0(h)` / 0x8259FF70, which
// shows that ability's tutorial (`sub_8219AE90`). Dropped: the child tick `sub_826D5D00`, the book tick
// `sub_82990730(h+48)` (its clock and the current page's +12 — page vtables 0x820EC6DC / 0x820ECB70 /
// 0x820ECBC8, highlight-keyed page animation), and the edge's page fills (`sub_826C2F98`, `sub_829A56F8`,
// `sub_8246D9F8`, `sub_829A5C28`, `sub_829A73D0`, `sub_82990A98`) — Godot's abilities page draws from the
// stated levels and experience.
REX_EXTERN(rex_lhGuiAbilitiesScreen_Update) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  if (base[h + 620u] != 0u) {
    Call(ctx, base, s.frame, 0x822CFC78u, {h + 332u});
  } else {
    const uint32_t row = s.data;  // {type, id, String1, String2}
    StoreU32(base, row, 0u);
    StoreU32(base, row + 4u, 0u);
    MakeString(ctx, base, s.frame, row + 8u, s.data + 0x40u, "");
    MakeString(ctx, base, s.frame, row + 12u, s.data + 0x40u, "");
    PPCContext ask = ctx;
    ask.r1.u32 = s.frame;
    ask.r3.u32 = LoadU32(base, LoadU32(base, Gui(base) + 28u) + 60u);
    ask.r4.u32 = row;
    rex_lhGuiMenu_GetHighlightedItemData(ask, base);
    const uint32_t type = LoadU32(base, row);
    const uint32_t id = LoadU32(base, row + 4u);
    DropString(ctx, base, s.frame, row + 12u);
    DropString(ctx, base, s.frame, row + 8u);
    const bool ability = type != 301u && type != 23u;
    base[h + 196u] = ability ? 1u : 0u;
    const uint32_t tutorials = Tutorials(base);
    const uint32_t state = Plausible(tutorials) ? LoadU32(base, tutorials + 4u) : 0u;
    if (id != LoadU32(base, h + 624u)) {
      if (ability && Plausible(state) && base[state + 56u] != 0u) {
        PPCContext arm = ctx;
        arm.r1.u32 = s.frame;
        arm.r3.u32 = h + 628u;
        arm.f1.f64 = 2.0;
        refii::mods::CallGuest(arm, base, Address(0x828C36D0u));
      } else if (!ability && base[h + 629u] != 0u) {
        base[h + 628u] = 0u;
        base[h + 629u] = 0u;
      }
      if (ability) {
        // The edge's page fill ran the gate for the new row (0x826C3870) and drew Discard by it; its
        // answer is stated for that row: the row id with bit 0 replaced by the gate's byte.
        const uint32_t flag = LoadU32(base, h + 4u) == 0u ? 1u : 0u;
        const bool open = RunDiscardGate(ctx, base, s.frame, s.data + 0x100u, h, id, flag, nullptr);
        refii_bridge_stated_value(kAbilityDiscardGate, (id & ~1u) | (open ? 1u : 0u));
        REXLOG_INFO("Refii screen stub: abilities (14) — the discard gate for row 0x{:08X} (hero flag {}): {}", id,
                    flag, open ? "open" : "shut");
      }
      StoreU32(base, h + 624u, id);
    }
    if (base[h + 629u] != 0u && (Call(ctx, base, s.frame, 0x828C3748u, {h + 628u}) & 0xFFu) != 0u) {
      bool run = true;
      if (base[h + 197u] != 0u) {
        run = (Call(ctx, base, s.frame, 0x8244DE40u, {state}) & 0xFFu) == 0u;
        if (run) {
          base[h + 197u] = 0u;
        }
      }
      if (run) {
        base[h + 628u] = 0u;
        base[h + 629u] = 0u;
        Call(ctx, base, s.frame, 0x826C3FD0u, {h});
        REXLOG_INFO("Refii screen stub: abilities (14) — the highlighted ability's tutorial check ran (row id "
                    "0x{:08X})", id);
      }
    }
  }
  if (!g_abilities_update_said) {
    g_abilities_update_said = true;
    Said("abilities (14)", "Update", h);
  }
}

// OnMenuEvent TU1 0x826C2660 / disc 0x8259E610 (h, event): with no box up (`QM+44 == 0`, 0x826C268C).
// Kept: type 23, the Spell Selector row → the blade `sub_826C2480(h, (h+4 != 0) + 1)` (0x826C28FC /
// 0x8259E430). Dropped: 20 / 21 / 22 (`sub_826C2D40` / `sub_826C2E68`: learn by the arm byte h+198, or
// the discard confirm whose YES resolves the ability from the guest's highlight h+624) — their
// consequences are `Ability:<p>:learn|unlearn:<n>` (DispatchAbility); the event cache h+644..h+652 that
// only that YES reads; the sounds 0x8C / 0x8D.
REX_EXTERN(rex_lhGuiAbilitiesScreen_OnMenuEvent) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const uint32_t type = LoadU32(base, ctx.r4.u32 + 4u);
  Said("abilities (14)", "OnMenuEvent", h, type, LoadU32(base, ctx.r4.u32 + 8u));
  if (type != 23u || LoadU32(base, LoadU32(base, Gui(base) + 28u) + 44u) != 0u) {
    return;
  }
  const Scratch s = Claim(ctx);
  Call(ctx, base, s.frame, 0x826C2480u, {h, LoadU32(base, h + 4u) != 0u ? 2u : 1u});
}

// OnScreenAction TU1 0x826C2958 / disc 0x8259E908 (h, code, player). Kept: with the blade up (h+620),
// 37 → its exit `sub_826C25C0(h)` (0x826C2984 / 0x8259E570), any other code → the widget's input
// `lhGuiWillAssignWidget_OnScreenAction(h+200, code, player, pres)` (0x826C29A0 / 0x8259A6C8), which
// moves the slot cursor and writes the spell slots. Dropped without the blade: 26 / 27 the guest's book
// cursor, 36 / 39 the arm byte + Lua OnPressA; B (37) and LB / RB (72 / 73, player one) are the spine
// calls Godot posts as `PauseBack` / `PauseCycle:<p>:…`.
REX_EXTERN(rex_lhGuiAbilitiesScreen_OnScreenAction) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const uint32_t code = ctx.r4.u32;
  const uint32_t player = ctx.r5.u32;
  Said("abilities (14)", "OnScreenAction", h, code, player);
  if (base[h + 620u] == 0u) {
    return;
  }
  const Scratch s = Claim(ctx);
  if (code == 37u) {
    Call(ctx, base, s.frame, 0x826C25C0u, {h});
  } else {
    Call(ctx, base, s.frame, 0x826B5430u, {h + 200u, code, player, BookPresentation(base)});
  }
}

// OnModalAnswer TU1 0x826C2B58 / disc 0x8259EB08 (h, code, player), the box at QM+44. Kept: box 1, the
// result box the learn / discard functions open (`OpenModal(QM, text, 2, 1, 0)`, 0x8246CF30 /
// 0x8246D4F4 / 0x8246D9D4), A (36) → `sub_8236FEC8(QM, 2)` (0x826C2C6C / 0x823380C0), which clears the
// latch; box 2, the guest's discard confirm: 47 → `sub_8236FE38(QM, 3)`, 46 → `sub_8236FEC8(QM, 3)`.
// Dropped: 46's discard of the HIGHLIGHTED ability h+624 (`sub_8246D510`, 0x826C2BF8) and its restate /
// ghost (`sub_829A5568`, `sub_826C2C88`) — box 2 is opened only by the dropped OnMenuEvent arm.
REX_EXTERN(rex_lhGuiAbilitiesScreen_OnModalAnswer) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const uint32_t code = ctx.r4.u32;
  const uint32_t qm = LoadU32(base, Gui(base) + 28u);
  const uint32_t box = LoadU32(base, qm + 44u);
  Said("abilities (14)", "OnModalAnswer", h, code, box);
  const Scratch s = Claim(ctx);
  if (box == 1u && code == 36u) {
    Call(ctx, base, s.frame, 0x8236FEC8u, {qm, 2u});
  } else if (box == 2u && code == 46u) {
    Call(ctx, base, s.frame, 0x8236FEC8u, {qm, 3u});
  } else if (box == 2u && code == 47u) {
    Call(ctx, base, s.frame, 0x8236FE38u, {qm, 3u});
  }
}

// OnExit TU1 0x826C2308 / disc 0x8259E2B8: kept whole — the base exit `sub_826D5C30` / 0x82583490 (the
// tab strip's hide), the blade's exit `sub_826C25C0` when h+620, `ResetTutorial(tut, 120)` 0x82450988 /
// 0x824FDB68 when h+197 and `sub_8244DE40` (0x826C2378), `sub_8244DAD8(state)`. Also run by `OpenModal`'s
// `SetScreen(QM, 3, 0)` (0x8236E68C) when a result box opens over the book, as on the 360.
REX_EXTERN(rex_lhGuiAbilitiesScreen_OnExit) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  Call(ctx, base, s.frame, 0x826D5C30u, {h});
  if (base[h + 620u] != 0u) {
    Call(ctx, base, s.frame, 0x826C25C0u, {h});
  }
  const uint32_t tutorials = Tutorials(base);
  const uint32_t state = Plausible(tutorials) ? LoadU32(base, tutorials + 4u) : 0u;
  bool reset = false;
  if (base[h + 197u] != 0u && (Call(ctx, base, s.frame, 0x8244DE40u, {state}) & 0xFFu) != 0u) {
    Call(ctx, base, s.frame, 0x82450988u, {tutorials, 120u});  // Lua_TutorialManager_ResetTutorial
    reset = true;
  }
  Call(ctx, base, s.frame, 0x8244DAD8u, {state});
  Said("abilities (14)", "OnExit", h, reset ? 1u : 0u);
}

namespace refii::vfs {

bool DispatchAbility(PPCContext& ctx, uint8_t* base, uint32_t player, bool unlearn, uint32_t index) {
  // The transaction is one guest call taking (ability 1..14, hero flag) and reading nothing of the
  // screen: Strength / Skill `lhGuiAbilitiesScreen_PurchaseStrengthOrSkillLevel` 0x8246C970 / 0x828385A0,
  // Will `sub_8246CF48` / 0x82838B78, the discard `sub_8246D510` / 0x82839138. The flag is the
  // book's owner, `h+4 == 0` (`lwz r11, 4(r31)` / `cntlzw` 0x826C2D58 / 0x826C2E80; disc 0x8259ED08): 1 →
  // `lhEntity_GetModel` (hero one, 0x8246C9B0), 0 → `GetRenderObject` (hero two). Each spends or refunds
  // and opens its own result box, `OpenModal(QM, text, 2, 1, 0)` (0x8246CF30 / 0x8246D4F4 / 0x8246D9D4),
  // answered by OnModalAnswer.
  const Scratch s = Claim(ctx);
  const uint32_t qm_now = LoadU32(base, Gui(base) + 28u);
  PPCContext find = ctx;
  find.r1.u32 = s.frame;
  find.r3.u32 = qm_now;
  find.r4.u32 = refii::guest::ScreenId(14u);
  rex_lhGuiManager_FindScreenHandler(find, base);
  const uint32_t h = Plausible(qm_now) && Plausible(find.r3.u32) ? find.r3.u32 : 0u;
  if (h == 0u) {
    REXLOG_INFO("Refii guest-tick pump: Ability:{} — no abilities handler, dropped", player);
    return true;
  }
  const uint32_t flag = LoadU32(base, h + 4u) == 0u ? 1u : 0u;
  if (unlearn) {
    // The discard confirm's YES (`sub_826C2B58` 0x826C2BB0..0x826C2BF8): the info built from the
    // highlighted row h+624 — the row Godot states, cached by the Update stub — gated by the guest's own
    // `sub_826C3A30` (the Y arms 0x826C2DC0 / 0x826C2EF0), and the unlearn given its Type.
    const uint32_t row_id = Plausible(h) ? LoadU32(base, h + 624u) : 0xFFFFFFFFu;
    uint32_t type = 0u;
    const bool open = row_id != 0xFFFFFFFFu && RunDiscardGate(ctx, base, s.frame, s.data, h, row_id, flag, &type);
    if (!open || type != index) {
      REXLOG_INFO("Refii guest-tick pump: Ability:{}:unlearn:{} — refused by the discard gate (row 0x{:08X}, "
                  "Type {}, gate {})", player, index, row_id, type, open ? "open" : "shut");
      return true;
    }
  }
  const uint32_t entry = unlearn ? 0x8246D510u : index <= 6u ? 0x8246C970u : 0x8246CF48u;
  const bool done = (Call(ctx, base, s.frame, entry, {index, flag}) & 0xFFu) != 0u;
  if (done && Plausible(h)) {
    // A level bought or given back forgets the highlight (h+624 = -1: 0x826C2DB0, 0x826C2EDC,
    // 0x826C2C1C; disc 0x8259ED60, 0x8259EE84), so the next Update's edge refills the page and
    // states the row's gate again. A Will level bought, and any level given back, also restates the
    // Spell Selector row's ghost, `sub_826C2C88(h)` / 0x8259EC38 (0x826C2EE0, 0x826C2C24):
    // `sub_823DF660(14, "SpellAssigning", !CanOpen(h+200, owner + 1), 0)`.
    StoreU32(base, h + 624u, 0xFFFFFFFFu);
    if (unlearn || entry == 0x8246CF48u) {
      Call(ctx, base, s.frame, 0x826C2C88u, {h});
    }
  }
  const uint32_t qm = LoadU32(base, Gui(base) + 28u);
  REXLOG_INFO("Refii guest-tick pump: Ability:{}:{}:{} ran 0x{:08X}(index {}, hero flag {}) — {}; box {}", player,
              unlearn ? "unlearn" : "learn", index, Address(entry), index, flag,
              done      ? (unlearn ? "one level given back" : "bought")
              : unlearn ? "refused"
                        : "refused (maxed, or not enough experience)",
              LoadU32(base, qm + 44u));
  return true;
}

}  // namespace refii::vfs
