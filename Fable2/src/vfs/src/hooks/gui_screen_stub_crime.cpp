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

extern "C" uint8_t* RefiiGuestBase(void);
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
// Three slots stay the guest's. OnEnter (TU1 0x826A8C78 / disc 0x825914F0) is the two row ghosts
// (`sub_823DF660(21, "PayFine" | "CommunityService", !allowed, 0)`), the book and its pages' text
// readers and the first-tick latch around one open sound, two formatter chunks and one slide. OnExit
// (0x826A97C0 / 0x82592038) is the book's release. OnMenuEvent (0x826A92D8 / 0x82591B50, one stream on
// both builds) is the screen's whole consequence and is the guest's to the instruction: type 50 behind
// h+44, 51, and 52 behind h+45 (then safety on, `sub_823E0788(comp33 of the local hero, 1)`
// 0x826A9658) post world message 42 / 43 / 44 — `sub_82279B80(bus, type, &position, sender, 0, 0x65, 0)`
// 0x826A94FC — and leave, `ExitScreen(QM, 1)` 0x826A950C; a refused gate returns with the screen up;
// 140 / 141 are two nav sounds; EVERY OTHER TYPE lands on that same exit with no message posted
// (the jump table's default, 0x826A9500 / 0x82591D78), which leaves `behaviourguard.lua` polling the
// three message types for good. That arm is the guest's and stays as it is.
//
// THE SENDER. The guest posts the local player's hero (`sub_821C7CE0` → `sub_8229EE78`, 0x826A94D8 /
// 0x826A94DC; the player map's key 1 on a hosting machine, else key 2), and the guard's script acts on
// it (`StopAttackingCriminal` / `AttackCriminal(message:GetEntitySentBy())`). In the shipped game that
// hero IS the criminal: `AddCrime` files every player's crime on hero one (0x827C9F64..0x827C9F9C).
// Ours files player two's on player two (RefiiCoopCrimeIsTheirs, coop_hero_hooks.cpp), and the arrest
// states whose it is — `VillageCrimeManager.DisplayCrimeScreen` sets the pause root's owner with r4 =
// (criminal == hero two) at 0x827D0078 / disc 0x82855960, just before it opens the screen. The sender
// is that player's hero.

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

// The post's sender (r6, the local player's hero) at 0x826A94E4 / disc 0x82591D5C: player two's hero
// when the arrest stated player two.
void RefiiCrimeSender(PPCRegister& r6) {
  uint8_t* base = RefiiGuestBase();
  if (base == nullptr || g_crime_arrested != 2u) {
    return;
  }
  const uint32_t two = refii_mod_player_hero(base, 2);
  if (two != 0u && two != r6.u32) {
    REXLOG_INFO("Refii crime screen: the arrest was player two's — the message is sent by hero two 0x{:08X} "
                "(the local player's hero is 0x{:08X})",
                two, r6.u32);
    r6.u64 = two;
  }
}

namespace refii::vfs {

void CrimeScreenOpening() {
  g_crime_arrested = g_crime_arrest_stated;
  g_crime_arrest_stated = 0u;
}

}  // namespace refii::vfs

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
