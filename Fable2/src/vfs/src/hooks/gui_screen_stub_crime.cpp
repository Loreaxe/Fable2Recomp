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

#include "core/guest_build.h"
#include "core/refii_mod.h"
#include "hooks/gui_screen_stub_support.h"
#include "hooks/gui_screen_stubs.h"
#include "hooks/ppc_externs.h"
#include "vfs_hooks_private.h"

using namespace refii::vfs::stub;
using refii::guest::Address;
using refii::mods::LoadU32;
using refii::mods::StoreU32;

// ---- UPGRADE FURNITURE (26; disc 25) — vtable 0x820CECEC / 0x820C18E8 ------------------------
PPC_EXTERN_FUNC(rex_lhGuiCrimeScreen_OnMenuEvent);

extern "C" uint32_t refii_mod_player_hero(uint8_t* base, int32_t player);

// ---- THE CRIME SCREEN (21; disc 20) — vtable 0x820CE068 / 0x820C0F28 -------------------------
//
// h+24 the guard's weak handle (its entity `**(h+28)`), h+32 / h+36 the two notices, h+40 the fine,
// h+44 "pay fine allowed", h+45 "community service allowed", h+46 the first-tick latch, h+48 the row
// list (vt 0x820B2D1C: +4 and +8 nullsubs — the three rows are the shipped script's own table), h+68
// the book, h+196 the last highlighted row. All written by the opener `sub_823C4128` / 0x824F9358
// (crime_screen_stated.cpp) and OnEnter. Same offsets on both builds. No slot body is shared with
// another vtable.
//
// Every slot but two is ported here; OnExit (TU1 0x826A97C0 / disc 0x82592038) is the book's release
// (`b sub_829912B0` on h+68: each page's vf+8, a buffer free) and the dtor frees.
//
// THE SENDER. The guest posts the local player's hero (`sub_821C7CE0` → `sub_8229EE78`, 0x826A94D8 /
// 0x826A94DC; the player map's key 1 on a hosting machine, else key 2), and the guard's script acts on
// it (`StopAttackingCriminal` / `AttackCriminal(message:GetEntitySentBy())`). In the shipped game that
// hero IS the criminal: `AddCrime` files every player's crime on hero one (0x827C9F64..0x827C9F9C).
// Ours files player two's on player two (RefiiCoopCrimeIsTheirs, coop_hero_hooks.cpp), and the arrest
// states whose it is — `VillageCrimeManager.DisplayCrimeScreen` sets the pause root's owner with r4 =
// (criminal == hero two) at 0x827D0078 / disc 0x82855960, just before it opens the screen. The sender
// is that player's hero, handed to the post by the ported OnMenuEvent below.

namespace {

constexpr uint32_t kCrimeVtable = 0x820CE068u;

/// The arrested player the open arrest stated (1 or 2), waiting for the opener it precedes.
uint32_t g_crime_arrest_stated = 0u;

/// The arrested player of the screen that is up; 0 when its opener was not an arrest (the debug
/// native and the pause root's test row state none): the sender is then the guest's own.
uint32_t g_crime_arrested = 0u;

}  // namespace

// The arrest's statement of whose crime screen this is (0x827D0078 / disc 0x82855960), r4 = the
// criminal is hero two.
void RefiiCrimeArrested(PPCRegister& r4) { g_crime_arrest_stated = (r4.u32 & 0xFFu) != 0u ? 2u : 1u; }

namespace refii::vfs {

void CrimeScreenOpening() {
  g_crime_arrested = g_crime_arrest_stated;
  g_crime_arrest_stated = 0u;
}

uint32_t CrimeArrestedPlayer() { return g_crime_arrested; }

}  // namespace refii::vfs

// OnEnter TU1 0x826A8C78 / disc 0x825914F0. Kept, in the guest's order:
// `g_MenuFormatter:FadeOutBottomItemEarly()` and `g_MenuFormatter:FadeOutTopItemEarly()` on the book's
// presentation (0x826A8CC0 / 0x826A8CF0; pres `*(*(G+56)+156)`, disc +152); the two row ghosts
// `sub_823DF660(screen, "PayFine", h[44] == 0, 0)` and `(screen, "CommunityService", h[45] == 0, 0)`
// (0x826A8D28 / 0x826A8D60; disc `sub_8283F948`), `screen` the crime screen's id — `li r3,0x15` on TU1,
// `li r3,0x14` on disc (0x826A8D20 / 0x82591598); the three pages h+112, h+140, h+168 pushed
// (`sub_82B49750` 0x826A8D8C / 0x826A8DA0 / 0x826A8DB4) and the book `sub_82990610(h+68, &pages,
// 0x8349AD00, pres, 1)` (0x826A8DDC / 0x828CE060 with 0x83498C24), which runs each page's +4; h+109 = 0,
// h+46 = 1 (the first-tick latch Update reads), h+196 = 9999999 (0x826A8E04..0x826A8E14). Dropped: the
// open sound, `h` vf+4 (0x826A8C94), and the book's slide `sub_82990A98(h+68, pres, 1)` (0x826A8DF4 /
// 0x828CE4E8: `lhGuiElement_GoToSlideAndRestart`, "SE_GUI_BLADE_SLIDE_IN" and the store book+40 = h+108
// = 1, which only the book's own tick, scroll and slide read — none is called here any more).
REX_EXTERN(rex_lhGuiCrimeScreen_OnEnter) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  const uint32_t pages = s.data;         // {?, begin, end, capacity}
  const uint32_t item = s.data + 0x10u;  // the page pointer handed to the push
  const uint32_t name = s.data + 0x14u;  // a row's name
  const uint32_t pres = BookPresentation(base);

  RunMenuLua(ctx, base, s.frame, s.data + 0x100u, "g_MenuFormatter:FadeOutBottomItemEarly()", pres);
  RunMenuLua(ctx, base, s.frame, s.data + 0x100u, "g_MenuFormatter:FadeOutTopItemEarly()", pres);
  const uint32_t screen = refii::guest::ScreenId(21u);
  MakeString(ctx, base, s.frame, name, s.data + 0x40u, "PayFine");
  Call(ctx, base, s.frame, 0x823DF660u, {screen, name, base[h + 44u] == 0u ? 1u : 0u, 0u});
  DropString(ctx, base, s.frame, name);
  MakeString(ctx, base, s.frame, name, s.data + 0x40u, "CommunityService");
  Call(ctx, base, s.frame, 0x823DF660u, {screen, name, base[h + 45u] == 0u ? 1u : 0u, 0u});
  DropString(ctx, base, s.frame, name);

  for (uint32_t i = 0; i < 16u; i += 4u) {
    StoreU32(base, pages + i, 0u);
  }
  for (uint32_t page : {0x70u, 0x8Cu, 0xA8u}) {
    StoreU32(base, item, h + page);
    Call(ctx, base, s.frame, 0x82B49750u, {pages, item});
  }
  Call(ctx, base, s.frame, 0x82990610u, {h + 0x44u, pages, Address(0x8349AD00u), pres, 1u});
  base[h + 0x6Du] = 0u;
  base[h + 0x2Eu] = 1u;
  StoreU32(base, h + 0xC4u, 9999999u);
  const uint32_t storage = LoadU32(base, pages + 4u);
  if (storage != 0u) {
    Call(ctx, base, s.frame, 0x8221BD38u, {storage});  // Render_FreeD3DObject
  }
  Said("crime (21)", "OnEnter", h, base[h + 44u], base[h + 45u]);
}

// Update TU1 0x826A8E28 / disc 0x825916A0. Kept: the guard's arrest line on the first tick — h+46 set
// → cleared (0x826A8EDC); the guard `**(h+28)` alive (0x826A8F10) with its component 100
// (0x826A8F3C): `sub_825D7060(comp100, &TEXT_AI_CRIME_ARREST, 2, 0, 0, 0, 0, -1)` (0x826A907C /
// 0x82448530; the key 0x8349AD18 / 0x83498C3C), then the line's sound event `*(comp100+0x38)` vf+20
// with 0 (0x826A9080..0x826A909C) — and the gold text every tick, `sub_8246B5E8(pres, 0x8349AD04, 1)`
// (0x826A90B8 / 0x82837218 with 0x83498C28). Dropped: the child tick `sub_826D5D00` (0x826A8E38; this
// list's vf+8 is a nullsub), the highlight read and the page switch it drives `sub_826A90D8`
// (0x826A8EC0: Page2 / Page3 shown and hidden, the book's page) and the book's tick `sub_82990730`
// (0x826A8ECC).
REX_EXTERN(rex_lhGuiCrimeScreen_Update) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  if (base[h + 0x2Eu] != 0u) {
    base[h + 0x2Eu] = 0u;
    const uint32_t holder = LoadU32(base, h + 0x1Cu);
    const uint32_t guard = Plausible(holder) ? LoadU32(base, holder) : 0u;
    const uint32_t speech =
        Plausible(guard) && (base[guard + 0x90u] & 0x40u) != 0u ? refii_mod_component(base, guard, 100u) : 0u;
    if (speech != 0u) {
      Call(ctx, base, s.frame, 0x825D7060u, {speech, Address(0x8349AD18u), 2u, 0u, 0u, 0u, 0u, 0xFFFFFFFFu});
      const uint32_t line = LoadU32(base, speech + 0x38u);
      if (Plausible(line)) {
        CallSlot(ctx, base, s.frame, line, 5u, {0u});
      }
    }
    REXLOG_INFO("Refii screen stub: crime (21) Update's first tick ran on 0x{:08X} — guard 0x{:08X}, {}", h, guard,
                speech != 0u ? "the arrest line said" : "no live guard to speak");
  }
  Call(ctx, base, s.frame, 0x8246B5E8u, {BookPresentation(base), Address(0x8349AD04u), 1u});
  RestatePurseForPlayerTwo(ctx, base, s.frame, BookPresentation(base), 0x8349AD04u);
}

// OnScreenAction TU1 0x826A96B8 / disc 0x82591F30: 26 / 27 the book's page scroll (`b sub_829907F0`
// on h+68), 36 the Lua OnPressA helper (`b sub_826D58F0`). No B: the screen is left by a choice
// alone. Godot posts a row's A as `Crime:<p>:pay|resist|service` (DispatchCrime). Nothing kept.
REX_EXTERN(rex_lhGuiCrimeScreen_OnScreenAction) {
  REX_FUNC_PROLOGUE();
  Said("crime (21)", "OnScreenAction", ctx.r3.u32, ctx.r4.u32, ctx.r5.u32);
}

// OnMenuEvent TU1 0x826A92D8 / disc 0x82591B50 (r4 the event, its type at +4; jump table 0x826A9310 /
// 0x82591B88 on type − 50, 92 entries, read from both images: 50 → 0x826A9480, 51 → 0x826A9518, 52 →
// 0x826A9520, 53..139 → 0x826A9500, 140 → 0x826A968C, 141 → 0x826A9660; out of range → 0x826A9500).
// Kept to the instruction: 50 behind h+44 (`lbz r11,0x2C(r3); beq` → return, 0x826A9480) message 42; 51
// message 43; 52 behind h+45 (0x826A9520) message 44 after the safety goes on — the local hero's
// component 33 (0x826A9538..0x826A9648) and `sub_823E0788(comp33, 1)` (0x826A9658 / disc 0x823BF150);
// then the post (0x826A9490..0x826A94FC): the local hero `sub_8229EE78(sub_821C7CE0(G))`, its position
// `(*(*(hero+124))+64)(&pos, *(hero+124))` (0x826A94B8), the bus `*(*(*(GAME+88)+4)+16)`
// (0x826A94C4..0x826A94D4), and `sub_82279B80(bus, message, &pos, sender, 0, 0x65, 0)` (0x826A94FC /
// 0x82279A40) — sender the local hero (0x826A94E0 `mr r6,r3`), or player two's hero when the arrest
// stated player two (THE SENDER, above); then `lhGuiManager_ExitScreen(QM, 1)` (0x826A950C). Every
// other type but 140 / 141 is that exit alone (0x826A9500). Dropped: 140 / 141, `sub_82371E80(&ev, G,
// "SE_GUI_SLIDE_MENU_UP" 0x83497DD8 / "_DOWN" 0x83497DDC, 0)` and its release (0x826A96A4 / 0x826A9678;
// disc 0x82591F1C / 0x82591EF0).
REX_EXTERN(rex_lhGuiCrimeScreen_OnMenuEvent) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const uint32_t type = LoadU32(base, ctx.r4.u32 + 4u);
  const Scratch s = Claim(ctx);
  const uint32_t g = Gui(base);
  Said("crime (21)", "OnMenuEvent", h, type, g_crime_arrested);
  if (type == 140u || type == 141u) {
    return;
  }
  if (type == 50u || type == 51u || type == 52u) {
    if ((type == 50u && base[h + 0x2Cu] == 0u) || (type == 52u && base[h + 0x2Du] == 0u)) {
      return;
    }
    // THE ARRESTED PLAYER'S SAFETY. The guest turns safety on for the local player's hero, the one it
    // posts as. For player two's arrest that is player two's: their hero's here, in local co-op; on an online
    // host, the joiner's own answer has already set it on its hero (the safety is that machine's, the
    // stated query IsSafetyModeEnabled), so nothing here.
    const int32_t mode = refii_mod_coop_mode(base);
    if (type == 52u && !(mode == 2 && g_crime_arrested == 2u)) {
      uint32_t hero = Call(ctx, base, s.frame, 0x8229EE78u, {Call(ctx, base, s.frame, 0x821C7CE0u, {g})});
      if (g_crime_arrested == 2u && refii_mod_player_hero(base, 2) != 0u) {
        hero = refii_mod_player_hero(base, 2);
      }
      const uint32_t comp33 = refii_mod_component(base, hero, 33u);
      if (comp33 != 0u) {
        Call(ctx, base, s.frame, 0x823E0788u, {comp33, 1u});
      }
    }
    // AN ONLINE JOINER'S CRIME SCREEN is the host's arrest shown here (Engine_UiCrimePage): the guard's
    // script that reads the world message runs on the host, which posts it as hero two when this
    // machine's choice reaches it. Here the choice leaves the screen and nothing is posted.
    if (mode == 3) {
      REXLOG_INFO("Refii crime screen: this joiner's choice {} goes to the host, which posts it as player two's", type);
      Call(ctx, base, s.frame, 0x8236ED98u, {LoadU32(base, Gui(base) + 28u), 1u});
      return;
    }
    const uint32_t pos = s.data;  // the hero's position, 16 bytes
    const uint32_t hero = Call(ctx, base, s.frame, 0x8229EE78u, {Call(ctx, base, s.frame, 0x821C7CE0u, {g})});
    {
      PPCContext work = ctx;
      work.r1.u32 = s.frame;
      work.r3.u32 = pos;
      work.r4.u32 = LoadU32(base, hero + 124u);
      refii::mods::CallGuest(work, base, LoadU32(base, LoadU32(base, work.r4.u32) + 64u));
    }
    const uint32_t bus = LoadU32(base, LoadU32(base, LoadU32(base, Game(base) + 88u) + 4u) + 16u);
    uint32_t sender = Call(ctx, base, s.frame, 0x8229EE78u, {Call(ctx, base, s.frame, 0x821C7CE0u, {g})});
    if (g_crime_arrested == 2u) {
      const uint32_t two = refii_mod_player_hero(base, 2);
      if (two != 0u && two != sender) {
        REXLOG_INFO("Refii crime screen: the arrest was player two's — the message is sent by hero two 0x{:08X} "
                    "(the local player's hero is 0x{:08X})",
                    two, sender);
        sender = two;
      }
    }
    Call(ctx, base, s.frame, 0x82279B80u, {bus, type - 8u, pos, sender, 0u, 0x65u, 0u});
  }
  Call(ctx, base, s.frame, 0x8236ED98u, {LoadU32(base, Gui(base) + 28u), 1u});
}

// ---- THE LEVERS -----------------------------------------------------------------------------

namespace refii::vfs {

bool DispatchCrime(PPCContext& ctx, uint8_t* base, uint32_t player, const std::string& choice) {
  // One of the three rows, as the handler's own OnMenuEvent answers its event: 50 pay the fine, 51
  // resist arrest, 52 community service. The gates, the safety, the message and the exit are the
  // guest's; a refused gate leaves the screen up, as it does there.
  const uint32_t type = choice == "pay" ? 50u : choice == "resist" ? 51u : choice == "service" ? 52u : 0u;
  const Scratch s = Claim(ctx);
  const uint32_t h = ScreenHandler(ctx, base, s.frame, 21u, kCrimeVtable);
  if (h == 0u || type == 0u) {
    REXLOG_INFO("Refii guest-tick pump: Crime:{}:{} — {}", player, choice,
                h == 0u ? "no crime handler, dropped" : "not pay, resist or service, dropped");
    return true;
  }
  const bool allowed = type == 51u || base[h + (type == 50u ? 0x2Cu : 0x2Du)] != 0u;
  const uint32_t event = s.data;
  MakeString(ctx, base, s.frame, event, s.data + 0x40u, "");
  StoreU32(base, event + 4u, type);
  StoreU32(base, event + 8u, 0u);
  {
    PPCContext work = ctx;
    work.r1.u32 = s.frame;
    work.r3.u32 = h;
    work.r4.u32 = event;
    rex_lhGuiCrimeScreen_OnMenuEvent(work, base);
  }
  DropString(ctx, base, s.frame, event);
  REXLOG_INFO("Refii guest-tick pump: Crime:{}:{} — the handler's own answer to event {} ran: {} (arrested player {})",
              player, choice, type,
              allowed ? "message posted, the screen left" : "the guest's gate refuses it, the screen stays",
              g_crime_arrested);
  return true;
}

}  // namespace refii::vfs
