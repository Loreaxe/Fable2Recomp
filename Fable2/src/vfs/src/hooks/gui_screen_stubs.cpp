// THE PORTED SCREENS (S7). Each handler slot below is a full replacement: it keeps the guest's
// statements of the screen's data and its game side effects, as direct calls, and drops what
// only draws or animates. Godot drives the rest through the levers at the end of this file
// (guest_tick_pump.cpp dispatches them). The handler objects stay registered.
//
// Slot layout (vtable): +8 OnEnter, +16 Update, +20 OnMenuEvent(h, event), +24
// OnScreenAction(h, code, player), +36 OnExit. TU1 / disc addresses on each function.
#include <cstdint>
#include <cstring>
#include <initializer_list>
#include <string>

#include <rex/logging.h>

#include "core/guest_build.h"
#include "core/refii_mod.h"
#include "hooks/gui_screen_stubs.h"
#include "hooks/ppc_externs.h"
#include "vfs_hooks_private.h"

PPC_EXTERN_FUNC(rex_lhGuiManager_FindScreenHandler);
/// Screen 13, the next-episode advert, exists in TU1 only (the disc has no screen 13): its slots and
/// its close `sub_823D05A8` bind only there, so they are weak here.
extern "C" __attribute__((weak)) REX_FUNC(__imp__rex_lhGuiNextEpisodeScreen_Close);
extern "C" __attribute__((weak)) REX_FUNC(__imp__rex_lhGuiNextEpisodeScreen_Update);
extern "C" __attribute__((weak)) REX_FUNC(__imp__rex_lhGuiNextEpisodeScreen_HandleAction);

namespace {

using refii::guest::Address;
using refii::mods::LoadU32;
using refii::mods::StoreU32;

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

uint8_t Load8(uint8_t* base, uint32_t at) { return base[at]; }

/// `G = *(0x83496B08)` (disc 0x834968A4).
uint32_t Gui(uint8_t* base) { return LoadU32(base, Address(0x83496B08u)); }

/// The current screen's presentation, `*(*(G+56)+156)` (disc +152).
uint32_t CurrentPresentation(uint8_t* base) {
  const uint32_t g = Gui(base);
  const uint32_t set = Plausible(g) ? LoadU32(base, g + 56u) : 0u;
  return Plausible(set) ? LoadU32(base, set + refii::guest::ScreenManagerField(156u)) : 0u;
}

/// One guest call on a scratch frame below `ctx`; r3 back.
uint32_t Call(PPCContext& ctx, uint8_t* base, uint32_t frame, uint32_t tu1, std::initializer_list<uint32_t> args) {
  PPCContext work = ctx;
  work.r1.u32 = frame;
  uint32_t* regs[] = {&work.r3.u32, &work.r4.u32, &work.r5.u32, &work.r6.u32, &work.r7.u32, &work.r8.u32};
  size_t i = 0;
  for (uint32_t value : args) {
    *regs[i++] = value;
  }
  refii::mods::CallGuest(work, base, Address(tu1));
  return work.r3.u32;
}

/// A virtual call, slot `slot` of `object`, on a scratch frame.
uint32_t CallSlot(PPCContext& ctx, uint8_t* base, uint32_t frame, uint32_t object, uint32_t slot,
                  std::initializer_list<uint32_t> args) {
  PPCContext work = ctx;
  work.r1.u32 = frame;
  uint32_t* regs[] = {&work.r4.u32, &work.r5.u32, &work.r6.u32, &work.r7.u32};
  size_t i = 0;
  for (uint32_t value : args) {
    *regs[i++] = value;
  }
  refii::mods::CallVirtual(work, base, object, slot);
  return work.r3.u32;
}

/// A guest CCharString at `handle` built from `text`, copied into the frame at `chars` first.
void MakeString(PPCContext& ctx, uint8_t* base, uint32_t frame, uint32_t handle, uint32_t chars, const char* text) {
  const size_t n = std::strlen(text);
  std::memcpy(base + chars, text, n + 1u);
  StoreU32(base, handle, 0u);
  Call(ctx, base, frame, 0x8222CED0u, {handle, chars, 0xFFFFFFFFu});  // lhCharString_CtorFromCStr
}

void DropString(PPCContext& ctx, uint8_t* base, uint32_t frame, uint32_t handle) {
  Call(ctx, base, frame, 0x82214DD8u, {handle});  // lhCharString_Dtor
}

/// Runs `chunk` on the current presentation, as `lhGui_RunLuaString` (TU1 0x82426470).
void RunMenuLua(PPCContext& ctx, uint8_t* base, uint32_t frame, uint32_t scratch, const char* chunk) {
  MakeString(ctx, base, frame, scratch, scratch + 0x10u, chunk);
  Call(ctx, base, frame, 0x82426470u, {scratch, CurrentPresentation(base)});
  DropString(ctx, base, frame, scratch);
}

/// Scratch below the caller: the callee frames grow down from `frame`, the scratch sits above it.
struct Scratch {
  uint32_t frame;
  uint32_t data;
};

Scratch Claim(const PPCContext& ctx) {
  const uint32_t top = (ctx.r1.u32 - 0x600u) & ~0xFu;
  return {top, top + 0x200u};
}

/// The logbook opened on another player (`sub_823C48D8`, the orb's View Stats): 0 not, 1 opened
/// elsewhere, 2 opened over the world map (its B returns there). Stated by the opener's hook.
int g_logbook_orb = 0;

bool g_logbook_update_said = false;

void Said(const char* screen, const char* slot, uint32_t handler, uint32_t a = 0u, uint32_t b = 0u) {
  REXLOG_INFO("Refii screen stub: {} {} ran on 0x{:08X} ({} {})", screen, slot, handler, a, b);
}

}  // namespace

namespace refii::vfs {

void StateLogbookOpenedOnPlayer(bool over_world_map) { g_logbook_orb = over_world_map ? 2 : 1; }

}  // namespace refii::vfs

// ---- THE LOGBOOK (17; disc 16) — vtable 0x820D0110 / 0x820C04C4 ----------------------------
//
// OnEnter TU1 0x826D8DD8 / disc 0x82581A68. Kept, in the guest's order: the change detectors
// h+96/h+100 = -1; orb mode (h+4 == 2): the `LogbookMenuOrb` repopulate (0x826D8E50) and the stats
// fill `sub_829BAEB8(h+384, h+24)` (0x826D8E68 / disc 0x828D4298); else the tab strip
// `sub_826B8348(*(G+76), 7)` (0x826D8E88 / 0x8240B218), the hero (`lhEntity_GetModel` /
// `GetRenderObject` on h+4) into h+132/+280/+316, the stats fill on hero component 57 + 12 when
// present (0x826D9050), and the eight page objects pushed into the page vector (`sub_82B49750` /
// `sub_8228A900`); then the page registration `sub_82990610(h+52, &pages, 0x8349B454, pres, 1)`
// (0x826D90D8 / 0x828CE060 with 0x83498A3C), h+93 = 0, the row population h+32 vf+4
// (`sub_829B8648` / 0x828D1A28), the "Dog" row ghost `sub_823DF660(17, "Dog", !sub_826D99C8(h), 0)`
// (0x826D9128 / 0x8283F948, id 16 on disc) and the gold text `sub_8246B5E8(pres, 0x8349B458, 1)`
// (0x826D914C / 0x82837218 with 0x83498A40). Dropped: the open sound, vf+4 (0x826D8DF4).
REX_EXTERN(rex_lhGuiLogbookScreen_OnEnter) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  const uint32_t pages = s.data;          // {?, begin, end, capacity}
  const uint32_t item = s.data + 0x10u;   // the page pointer handed to the push
  const uint32_t text = s.data + 0x20u;   // a CCharString handle and its characters
  const uint32_t g = Gui(base);
  const uint32_t pres = CurrentPresentation(base);
  g_logbook_update_said = false;

  StoreU32(base, h + 96u, 0xFFFFFFFFu);
  StoreU32(base, h + 100u, 0xFFFFFFFFu);
  for (uint32_t i = 0; i < 16u; i += 4u) {
    StoreU32(base, pages + i, 0u);
  }
  const auto push = [&](uint32_t page) {
    StoreU32(base, item, page);
    Call(ctx, base, s.frame, 0x82B49750u, {pages, item});
  };

  uint32_t last = 0u;
  if (LoadU32(base, h + 4u) == 2u) {
    RunMenuLua(ctx, base, s.frame, text, "g_Menu:RepopulateMenuFromPopulationScript( 'LogbookMenuOrb' )");
    Call(ctx, base, s.frame, 0x829BAEB8u, {h + 384u, LoadU32(base, h + 24u)});
    last = h + 384u;
  } else {
    Call(ctx, base, s.frame, 0x826B8348u, {LoadU32(base, g + 76u), 7u});
    const uint32_t root = LoadU32(base, Address(0x83496AB8u));
    const uint32_t game = Plausible(root) ? LoadU32(base, root + 12u) : 0u;
    const uint32_t holder = Plausible(game) ? LoadU32(base, game + 88u) : 0u;
    const uint32_t party = Plausible(holder) ? LoadU32(base, holder + 4u) : 0u;
    const uint32_t roster = Plausible(party) ? LoadU32(base, party) : 0u;
    const uint32_t hero =
        Call(ctx, base, s.frame, LoadU32(base, h + 4u) != 0u ? 0x828E5A78u : 0x8222C1B8u, {roster});
    StoreU32(base, h + 132u, hero);
    push(h + 104u);
    StoreU32(base, h + 280u, hero);
    push(h + 252u);
    StoreU32(base, h + 316u, hero);
    push(h + 288u);
    if (Plausible(hero) && (Load8(base, hero + 144u) & 0x40u) != 0u &&
        ((LoadU32(base, hero + 40u) >> 25) & 1u) != 0u) {
      const uint32_t stats = refii_mod_component(base, hero, 57u);
      Call(ctx, base, s.frame, 0x829BAEB8u, {h + 384u, stats != 0u ? stats + 12u : 0u});
    }
    push(h + 384u);
    push(h + 428u);
    push(h + 464u);
    push(h + 492u);
    last = h + 520u;
  }
  push(last);

  Call(ctx, base, s.frame, 0x82990610u, {h + 52u, pages, Address(0x8349B454u), pres, 1u});
  base[h + 93u] = 0u;
  CallSlot(ctx, base, s.frame, h + 32u, 1u, {});
  MakeString(ctx, base, s.frame, text, text + 0x10u, "Dog");
  const uint32_t dog = Call(ctx, base, s.frame, 0x826D99C8u, {h}) & 0xFFu;
  Call(ctx, base, s.frame, 0x823DF660u, {refii::guest::ScreenId(17u), text, dog == 0u ? 1u : 0u, 0u});
  DropString(ctx, base, s.frame, text);
  Call(ctx, base, s.frame, 0x8246B5E8u, {pres, Address(0x8349B458u), 1u});
  const uint32_t storage = LoadU32(base, pages + 4u);
  if (storage != 0u) {
    Call(ctx, base, s.frame, 0x8221BD38u, {storage});  // Render_FreeD3DObject
  }
  Said("logbook (17)", "OnEnter", h, LoadU32(base, h + 4u));
}

// Update TU1 0x826D9168 / disc 0x82581DF8: the base widget tick and the highlight-keyed page
// fills — drawing, driven by a guest highlight that Godot's cursor never moves. Nothing kept.
REX_EXTERN(rex_lhGuiLogbookScreen_Update) {
  REX_FUNC_PROLOGUE();
  if (!g_logbook_update_said) {
    g_logbook_update_said = true;
    Said("logbook (17)", "Update", ctx.r3.u32);
  }
}

// OnMenuEvent TU1 0x826D9620 / disc 0x825822B0: 140/141 sounds; 150 `XamShowAchievementsUI`
// (0x826D967C), whose row Godot drops (its achievements are a host screen). Nothing kept.
REX_EXTERN(rex_lhGuiLogbookScreen_OnMenuEvent) {
  REX_FUNC_PROLOGUE();
  Said("logbook (17)", "OnMenuEvent", ctx.r3.u32, LoadU32(base, ctx.r4.u32 + 4u), LoadU32(base, ctx.r4.u32 + 8u));
}

// OnScreenAction TU1 0x826D96F0 / disc 0x82582380: A is Lua OnPressA + sound, 26/27 the guest's
// menu cursor; B and LB/RB are spine calls Godot makes as `PauseBack` / `PauseCycle:<p>:…`
// (DispatchPauseBack / DispatchPauseCycle). Nothing kept.
REX_EXTERN(rex_lhGuiLogbookScreen_OnScreenAction) {
  REX_FUNC_PROLOGUE();
  Said("logbook (17)", "OnScreenAction", ctx.r3.u32, ctx.r4.u32, ctx.r5.u32);
}

// OnExit TU1 0x826D9980 / disc 0x82582610: kept whole — the base exit `sub_826D5C30` / 0x82583490
// (frees what OnEnter registered), h+24 = h+28 = h+4 = 0, the page teardown `sub_829912B0(h+52)` /
// 0x828CED00.
REX_EXTERN(rex_lhGuiLogbookScreen_OnExit) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  Call(ctx, base, s.frame, 0x826D5C30u, {h});
  StoreU32(base, h + 24u, 0u);
  StoreU32(base, h + 28u, 0u);
  StoreU32(base, h + 4u, 0u);
  Call(ctx, base, s.frame, 0x829912B0u, {h + 52u});
  g_logbook_orb = 0;
  Said("logbook (17)", "OnExit", h);
}

// ---- PHOTO MODE (1) — vtable 0x820CDEA4 / 0x820C0308; no opener on either build ------------
//
// Update TU1 0x826A88D8 / disc 0x825815A0: base tick + the upload dialog's update — drawing.
// OnScreenAction TU1 0x826A8918 / disc 0x825815E0: the dialog's input, and A's Live screenshot
// upload request `sub_8298FA38` (0x826A8960 / disc `sub_828460C0`) — a platform upload with no
// service here. Nothing kept.
REX_EXTERN(rex_lhGuiPhotoScreen_Update) {
  REX_FUNC_PROLOGUE();
}

REX_EXTERN(rex_lhGuiPhotoScreen_OnScreenAction) {
  REX_FUNC_PROLOGUE();
  Said("photo (1)", "OnScreenAction", ctx.r3.u32, ctx.r4.u32, ctx.r5.u32);
}

// ---- CREDITS (29; disc 28) — vtable 0x820CED6C / 0x820C1968 ---------------------------------
//
// Update TU1 0x826B7D00 / disc 0x8259CFA0: the scroll (SetText / position per line) and the close
// at the roll's end `sub_82370178(QM)` (0x826B7E3C). Godot rolls the lines and posts `CreditsEnd`
// at the same moment (DispatchCreditsEnd). Nothing kept.
REX_EXTERN(rex_lhGuiCreditsScreen_Update) {
  REX_FUNC_PROLOGUE();
}

// OnMenuEvent TU1 0x826B7E50 / disc 0x8259D0F0: type 201 named `ReturnToGame` → the quiet exit
// `sub_82370178(QM)` (0x826B7EE0 / disc 0x82338370). Kept.
REX_EXTERN(rex_lhGuiCreditsScreen_OnMenuEvent) {
  REX_FUNC_PROLOGUE();
  const uint32_t event = ctx.r4.u32;
  const uint32_t name = LoadU32(base, event);
  const uint32_t chars = Plausible(name) ? LoadU32(base, name) : 0u;
  const bool ret = LoadU32(base, event + 4u) == 201u && Plausible(chars) &&
                   std::strstr(reinterpret_cast<const char*>(base + chars), "ReturnToGame") != nullptr;
  Said("credits (29)", "OnMenuEvent", ctx.r3.u32, LoadU32(base, event + 4u), ret ? 1u : 0u);
  if (ret) {
    const Scratch s = Claim(ctx);
    Call(ctx, base, s.frame, 0x82370178u, {LoadU32(base, Gui(base) + 28u)});
  }
}

// OnScreenAction TU1 0x826B7EF8 / disc 0x8259D198: codes 36..37 only. Online co-op (game+284 == 1):
// the host closes `sub_82370178(QM)` (0x826B7F6C) and tells the peer, message {vt 0x820A6600 /
// 0x820A4A24, byte 0} through `sub_8255C0D8(net, &msg, 0)` (0x826B7F90 / disc `sub_82526B78`
// 0x8259D230); a joiner does nothing. Otherwise the close (0x826B7FB4). Kept whole.
REX_EXTERN(rex_lhGuiCreditsScreen_OnScreenAction) {
  REX_FUNC_PROLOGUE();
  const uint32_t code = ctx.r4.u32;
  Said("credits (29)", "OnScreenAction", ctx.r3.u32, code, ctx.r5.u32);
  if (code < 36u || code > 37u) {
    return;
  }
  const Scratch s = Claim(ctx);
  const uint32_t qm = LoadU32(base, Gui(base) + 28u);
  const uint32_t root = LoadU32(base, Address(0x83496AB8u));
  const uint32_t game = LoadU32(base, root + 12u);
  if (LoadU32(base, game + 0x11Cu) != 1u) {
    Call(ctx, base, s.frame, 0x82370178u, {qm});
    return;
  }
  const uint32_t net = LoadU32(base, game + 0x8Cu);
  if (Load8(base, net + 0x34u) == 0u || Load8(base, net + 0x35u) == 0u) {
    return;
  }
  Call(ctx, base, s.frame, 0x82370178u, {qm});
  StoreU32(base, s.data, Address(0x820A6600u));
  base[s.data + 4u] = 0u;
  Call(ctx, base, s.frame, 0x8255C0D8u, {net, s.data, 0u});
}

// ---- MINIGAME COMPLETED (11) — vtable 0x820AE3F0 / 0x820C09D4 -------------------------------
//
// Update TU1 0x823CDBA0 / disc 0x8258CAD8: base tick + the count-up texts — drawing (Godot counts
// up from the stated amounts). Nothing kept.
REX_EXTERN(rex_lhGuiMinigameCompletedScreen_Update) {
  REX_FUNC_PROLOGUE();
}

// HandleAction (+24) TU1 0x823CDC08 / disc 0x8258CB40: code 6 → the close `sub_823CDF10` /
// `sub_8258CE48` (0x823CDCA0): h+40 = 0, the rows' toggles off, the presentation unregistered,
// `ExitScreen(QM, 1)`. The count-up snap before it is Godot's; Godot sends 6 once its rows are
// settled. OnExit +36 is the same close (`b sub_823CDF10`) and stays the guest's.
REX_EXTERN(rex_lhGuiMinigameCompletedScreen_HandleAction) {
  REX_FUNC_PROLOGUE();
  Said("minigame (11)", "HandleAction", ctx.r3.u32, ctx.r4.u32, ctx.r5.u32);
  if (ctx.r4.u32 == 6u) {
    const Scratch s = Claim(ctx);
    Call(ctx, base, s.frame, 0x823CDF10u, {ctx.r3.u32});
  }
}

// ---- QUEST COMPLETION (12) — vtable 0x820A8DE4 / 0x820A5ABC ---------------------------------
//
// HandleAction (+24) TU1 0x823D3F98 / disc 0x824F2348: code 6; player 1 always, player 2 only on
// a joiner (host = net+0x34 && +0x35, 0x823D3FB8..0x823D3FE0) → the close `sub_823D4750` /
// `sub_824F2B58` (host: packet 0xE6 to the peer; ExitScreen; joiner: `sub_8232B2B0`). Kept whole.
// Update is the shared base and OnExit (base exit + the same close) stays the guest's.
REX_EXTERN(rex_lhGuiQuestCompletionScreen_HandleAction) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const uint32_t code = ctx.r4.u32;
  const uint32_t player = ctx.r5.u32;
  Said("quest completion (12)", "HandleAction", h, code, player);
  if (code != 6u) {
    return;
  }
  if (player != 1u) {
    const uint32_t root = LoadU32(base, Address(0x83496AB8u));
    const uint32_t net = LoadU32(base, LoadU32(base, root + 12u) + 0x8Cu);
    if (Load8(base, net + 0x34u) != 0u && Load8(base, net + 0x35u) != 0u) {
      return;
    }
  }
  const Scratch s = Claim(ctx);
  Call(ctx, base, s.frame, 0x823D4750u, {h});
}

// ---- NEXT EPISODE ADVERT (13, TU1 only — the disc has no such screen) — vtable 0x820A8E10 ----
//
// Update TU1 0x823D0268: while open (h+24), the countdown h+40 steps down; at 0 it reveals the
// prompt (`lhGuiRegistry_SetToggle`, 0x823D02D0) — drawing. The countdown is kept: it gates input.
REX_EXTERN(rex_lhGuiNextEpisodeScreen_Update) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  if (Load8(base, h + 24u) == 0u) {
    return;
  }
  const int32_t left = static_cast<int32_t>(LoadU32(base, h + 40u));
  if (left > 0) {
    StoreU32(base, h + 40u, static_cast<uint32_t>(left - 1));
  }
}

// HandleAction (+24) TU1 0x823D00E8: refused while h+40 > 0 (0x823D0100); player 2 only on a
// joiner (0x823D0104..0x823D0144); code 6 → the close `sub_823D05A8` (0x823D0150; h+24 = 0, the
// presentation unregistered, `ExitScreen(QM, 1)`). Kept. Code 38's
// `XamShowMarketplaceUI` (0x823D01B4) is the console store, absent here: dropped.
REX_EXTERN(rex_lhGuiNextEpisodeScreen_HandleAction) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  Said("next episode (13)", "HandleAction", h, ctx.r4.u32, ctx.r5.u32);
  if (static_cast<int32_t>(LoadU32(base, h + 40u)) > 0 || ctx.r4.u32 != 6u) {
    return;
  }
  if (ctx.r5.u32 != 1u) {
    const uint32_t root = LoadU32(base, Address(0x83496AB8u));
    const uint32_t net = LoadU32(base, LoadU32(base, root + 12u) + 0x8Cu);
    if (Load8(base, net + 0x34u) != 0u && Load8(base, net + 0x35u) != 0u) {
      return;
    }
  }
  if (&__imp__rex_lhGuiNextEpisodeScreen_Close != nullptr) {
    PPCContext work = ctx;
    work.r1.u32 = Claim(ctx).frame;
    work.r3.u32 = h;
    __imp__rex_lhGuiNextEpisodeScreen_Close(work, base);
  }
}

// ---- THE LEVERS -----------------------------------------------------------------------------

namespace refii::vfs {

bool DispatchPauseBack(PPCContext& ctx, uint8_t* base, uint32_t screen) {
  const uint32_t g = Gui(base);
  const uint32_t qm = Plausible(g) ? LoadU32(base, g + 28u) : 0u;
  PPCContext find = ctx;
  find.r3.u32 = qm;
  find.r4.u32 = refii::guest::ScreenId(screen);
  rex_lhGuiManager_FindScreenHandler(find, base);
  const uint32_t h = find.r3.u32;
  if (!Plausible(qm) || !Plausible(h)) {
    REXLOG_INFO("Refii guest-tick pump: PauseBack on screen {} — no handler, dropped", screen);
    return true;
  }
  const Scratch s = Claim(ctx);
  if (screen == 17u && g_logbook_orb == 2) {
    // The orb book over the world map (0x826D98C4..0x826D9960): SetScreen(QM, 23, 0), the map's
    // OnEnter, EnterGuiMode, the map's Update, `g_Menu:PopMenuState()`.
    PPCContext map = ctx;
    map.r3.u32 = qm;
    map.r4.u32 = refii::guest::ScreenId(23u);
    rex_lhGuiManager_FindScreenHandler(map, base);
    const uint32_t world = map.r3.u32;
    Call(ctx, base, s.frame, 0x8236E630u, {qm, refii::guest::ScreenId(23u), 0u});
    CallSlot(ctx, base, s.frame, world, 2u, {});
    Call(ctx, base, s.frame, 0x8236EB68u, {qm});
    CallSlot(ctx, base, s.frame, world, 4u, {});
    RunMenuLua(ctx, base, s.frame, s.data, "g_Menu:PopMenuState()");
    REXLOG_INFO("Refii guest-tick pump: PauseBack — the orb logbook back to the world map");
    return true;
  }
  if (screen == 17u && g_logbook_orb == 1) {
    Call(ctx, base, s.frame, 0x826D5A60u, {h});  // 0x826D9970: ExitScreen at the menu root
    REXLOG_INFO("Refii guest-tick pump: PauseBack — the orb logbook closed");
    return true;
  }
  // The book's back at its root (logbook 0x826D9870..0x826D9890, options 0x826DA4B0..0x826DA4BC):
  // the tab strip's hide `sub_826B8130(*(G+76))` (the logbook only when `sub_826C1D98` says the
  // menu is at its root), then `sub_826D5AF0(h)`, which reopens the pause root `sub_826A2C88`.
  const bool at_root = screen != 17u || (Call(ctx, base, s.frame, 0x826C1D98u, {LoadU32(base, qm + 60u)}) & 0xFFu) != 0u;
  if (at_root) {
    Call(ctx, base, s.frame, 0x826B8130u, {LoadU32(base, g + 76u)});
  }
  Call(ctx, base, s.frame, 0x826D5AF0u, {h});
  REXLOG_INFO("Refii guest-tick pump: PauseBack on screen {} — back to the book's root ({})", screen,
              at_root ? "at root" : "not at root");
  return true;
}

bool DispatchPauseCycle(PPCContext& ctx, uint8_t* base, uint32_t screen, uint32_t player, bool next) {
  // Player one only, and not the orb book (logbook `cmplwi r28,1` 0x826D97FC / 0x826D9828; options
  // 0x826DA300 / 0x826DA32C): 72 (LB) `sub_826B8780` (logbook 0x826D983C, options 0x826DA340), 73 (RB)
  // `sub_826B8878` (0x826D9810, 0x826DA314), on `*(G+76)`.
  if (player != 1u || (screen == 17u && g_logbook_orb != 0)) {
    REXLOG_INFO("Refii guest-tick pump: PauseCycle on screen {} for player {} — refused", screen, player);
    return true;
  }
  const uint32_t g = Gui(base);
  const Scratch s = Claim(ctx);
  Call(ctx, base, s.frame, next ? 0x826B8878u : 0x826B8780u, {LoadU32(base, g + 76u)});
  REXLOG_INFO("Refii guest-tick pump: PauseCycle on screen {} — {}", screen, next ? "next" : "previous");
  return true;
}

bool DispatchCreditsEnd(PPCContext& ctx, uint8_t* base) {
  const uint32_t g = Gui(base);
  const Scratch s = Claim(ctx);
  Call(ctx, base, s.frame, 0x82370178u, {LoadU32(base, g + 28u)});
  REXLOG_INFO("Refii guest-tick pump: CreditsEnd — the roll ended, quiet exit");
  return true;
}

}  // namespace refii::vfs
