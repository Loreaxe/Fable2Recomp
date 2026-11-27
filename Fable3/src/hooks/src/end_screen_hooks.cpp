// refii - Fable 3's epilogue (quest completion) and credits screens, stated as the screen manager
// opens and closes them, for Godot's Fable3EndScreens. Neither is a box-manager box: they are
// screen-manager screens (SM = *(GUI+0x1C), GUI = *0x83596F84), keyed in SM+64 by the .bgf id
// (6 questcompletion, 8 creditsscreen; built in sub_825F8F60 at disc 0x825CE2A0 / disc 0x825CE330), so no
// @Box.Open names them. Their texts and positions are element writes the element hooks already
// state; what only these calls state is that the screen is up, which document it is, and the
// epilogue's image. Godot owns their pad: the guest's reaches neither handler, Godot's press comes
// back as a tick action and runs the handler itself (AnswerEndScreen).
//
// EPILOGUE E = SM[6] (28 bytes; vtable TU3 off_820A0BE0, stored by the SM ctor sub_825F8F60 at
// 0x825F9318, disc off_8209D2FC, stored by sub_825CDEE0 at 0x825CE294 — 0x820A0BE0 is string data
// in the disc image). Its pad is slot 5, Input(E, action, player) (TU3 0x827A2480 / disc
// 0x8276B410, 0x50 bytes each, one vtable word each, a function in neither IDB), called for the open
// screen object by sub_826019F0 (`lwz r10,0x14(r11)` / `bctrl`, TU3 0x82601C58..0x82601C60 / disc
// 0x825D6C70..0x825D6C78), not by the modal input sub_825F9408. Its whole body: only action 13
// (INTERACT: `cmpwi cr6,r4,0xD` / `bnelr` 0x827A2480 / 0x8276B410); player one always (`cmpwi cr6,r5,
// 1` / `beq` 0x827A2488 / 0x8276B418); any other player only on a machine that is not the session's
// server (N = *(NET+0x10), NET = *0x835979F8 / disc *0x83556EF8: `lbz r10,0x1C(r11)` and `lbz r11,
// 0x1D(r11)` both set returns, 0x827A249C..0x827A24C4 / 0x8276B42C..0x8276B454; N+0x1C && N+0x1D is
// the guest's Network.IsServer, see THE SESSION'S FLAGS below); then `b Epilogue_Hide`
// (0x827A24C8 / 0x8276B458) with E still in r3. `epilogue:<p>:close` is that body with 13 and p.
// Show sub_827A24D0(E, title, description,
// renown, image, flag) — QuestTracker.ShowEpilogueScreen through sub_82511020 (0x825111C4) and the
// co-op net handler sub_82377E80 (disc 0x82373E54) — returns at once unless E+0x14 is clear
// (`lbz r11,0x14(r3)` 0x827A24DC), sets it (0x827A2508), loads the screen (sub_825F26E8(*(GUI+0x38),
// 6) at 0x827A2518) and attaches its presentation *(*(GUI+0x38)+0x20) (disc 0x8276B4B4) to the HUD
// holder (sub_8269F088 at 0x827A253C). The image is r7, a CCharString handle (`lwz r11,0(r26)` /
// `lwz r4,0(r11)` at 0x827A27B4..0x827A27C0); it reaches the `ScreenShot` texture by a raw store
// (`stw r9,0x200(r11)` 0x827A2868), never SetTexture, so this is the only place it is stated.
// Hide sub_827A2A90(E) acts only when E+0x14 is set (0x827A2A9C..0x827A2AA8) and clears it
// (0x827A2AB4).
//
// CREDITS. Start sub_825FA1B0(SM) — ShowCreditsScreen (0x8254CF88) and the net handler
// (disc 0x82374190) — runs the roll's init sub_82B74BA8 only while SM+0x23 is clear (disc 0x825CF1AC) and
// sets it (disc 0x825CF264); the roll writes `Text.Text0..23` under the presentation
// *(*(GUI+0x38)+0x9C) (`lwz r6,0x38(r11)` / `lwz r28,0x9C(r6)` 0x82B74D34..0x82B74D40). End
// sub_825FA2F8(SM) — the roll running out (0x82B75170), GUI_SELECT / GUI_CANCEL (0x82B751F8,
// 0x82B75244) and the net handler (disc 0x823741A4) — closes the screen and, with SM+0x23 set
// (0x825FA36C), pops the credits component. The credits screen's pad is its slot 5 (vtable TU3
// 0x820D6464 / disc 0x820D3AE4, sub_82B75188 / sub_82B3FF60, one vtable word each): only 72 and 73
// (`cmpwi cr6,r4,0x48` / `blt`, `cmpwi cr6,r4,0x49` / `bgt` 0x82B75198..0x82B751A4 / 0x82B3FF70..
// 0x82B3FF7C); with the shared-screen word set (`lwz r10,0x80(r31)` 0x82B751B4, DISC `lwz r10,
// 0x7C(r31)` 0x82B3FF8C; written by the local join, `stw r17,0x80(r11)` TU3 0x8245A478) EndCredits
// alone (0x82B75244 / 0x82B4001C); else on the session's SERVER (N+0x1C and N+0x1D) EndCredits and
// the end packet (0x82B751F8..0x82B75220 / 0x82B3FFD0..0x82B3FFF8); else (an online client) NOTHING.
// A solo game is its own session's server, so alone the credits DO skip, as on the 360. It reads
// neither r3 nor r5. `credits:<p>:skip` is that body with 72.
// THE SESSION'S FLAGS (N bytes, both builds), read from the guest's own Network natives: IsServer =
// +0x1C && +0x1D (TU3 0x82252FA0 / disc 0x82253D60), IsClient = +0x1C && +0x1E (0x82209428 /
// 0x82209E50), IsInLiveGame = IsClient || N+0x5C (disc N+0x58) (0x82209460 / 0x82209E88),
// IsInCouchGame = N+0x80 (disc +0x7C) == 1 (0x82244790 / 0x822455C0). The server's state is set at
// every game start by sub_82374CE8 / disc sub_8236D480 (`stb r28,0x1C` / `stb r28,0x1D` = 1, `stb
// r27,0x1E` = 0, TU3 0x82374D70..0x82374D7C / disc 0x8236D508..0x8236D514; callers include
// CGUIGameComponent's ctor, TU3 0x82712F7C / disc 0x826E9620); only joining an online host turns
// it into a client (+0x1C = +0x1E = 1, +0x1D = 0: TU3 sub_82375C78 0x82376148..0x82376164 / disc
// sub_8236E560 0x8236E7BC..0x8236E7C4).
// THE SHARED-SCREEN WORD N+0x80 (disc N+0x7C) is the local co-op seat: a second pad's START raw record
// (type 13, button 5) from a user who is no player yet joins player two while it is not 1 and the game
// is not live (CGUIInput_vf6 TU3 sub_82601318: `*(N+128) != 1` at 0x826013D0, then sub_824598D0
// 0x8260142C, whose hero setup sub_82459A80 stores it `stw r17,0x80(r11)` 0x8245A478; disc
// sub_825D6330, setup sub_8243A190 `stw r9,0x7C(r8)` 0x8243ABDC); only the shared-screen setter (TU3
// 0x82382330, stores 0x823823E4 / 0x82382408 / 0x82382610; disc 0x823799A4 / 0x823799C8 / 0x82379BDC)
// writes it otherwise. Which branch ended them is the End's own caller (its return address): TU3
// 0x82B75248 / disc 0x82B40020 the seat, 0x82B751FC / 0x82B3FFD4 the server's skip (a solo game's
// A), 0x82B75174 / 0x82B3FF4C the roll running out,
// 0x82377234 and 0x8237C414 / disc 0x823741A8 the net handlers — logged at each End.

#include <atomic>
#include <cstdint>
#include <cstring>

#include <rex/logging.h>
#include <rex/ppc.h>
#include <rex/system/kernel_state.h>
#include <rex/system/xmemory.h>

#include "box_answers.h"
#include "gui_statements.h"
#include "mod_seam.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_Epilogue_HandleInput);
REX_EXTERN(__imp__rex_CreditsScreen_HandleInput);
REX_EXTERN(rex_Epilogue_HandleInput);
REX_EXTERN(rex_CreditsScreen_HandleInput);
REX_EXTERN(__imp__rex_Epilogue_Show);
REX_EXTERN(__imp__rex_Epilogue_Hide);
REX_EXTERN(__imp__rex_ScreenManager_StartCredits);
REX_EXTERN(__imp__rex_ScreenManager_EndCredits);

namespace {

using namespace refii::fable3;

/// The documents the two screens load: screen 6 and screen 8 of the loader's table (the show's
/// sub_825F26E8(loader, 6) at 0x827A2518, ShowCreditsScreen's (loader, 8) at 0x8254CF68).
constexpr char kEpilogueDocument[] = "questcompletion";
constexpr char kCreditsDocument[] = "creditsscreen";
constexpr uint32_t kEpilogueOpen = 0x14u;
constexpr uint32_t kCreditsRunning = 0x23u;
/// The epilogue's title and description text names, FNV-1 words (0x827A2570 / 0x827A2594).
const refii::guest::Addr kEpilogueTitleName{0x8359B9A4u};
const refii::guest::Addr kEpilogueDescriptionName{0x8359B9A8u};
/// The screenshot's ImageName registry name, an FNV-1 word (0x827A2808).
const refii::guest::Addr kEpilogueImageName{0x8359B9C4u};

/// The epilogue screen the show last opened (its r3), 0 once hidden: the close lever's object.
std::atomic<uint32_t> g_epilogue{0u};
/// The credits roll, between its start and its end.
std::atomic<bool> g_credits{false};

/// Set while this file runs a handler with Godot's press, so the stub lets it through.
thread_local bool t_host_input = false;

constexpr uint32_t kActionInteract = 13u;
constexpr uint32_t kActionSelect = 72u;

/// The pad handler of a screen Godot owns: Godot's press runs the body, the guest's pad is dropped
/// (sub_826019F0 answers 1 for the open screen whatever its slot 5 did, TU3 0x82601C60).
template <typename Original>
void OwnedInput(PPCContext& ctx, uint8_t* base, Original original, std::atomic<bool>& said, const char* what) {
  if (!said.exchange(true)) {
    REXLOG_INFO("[fable3-endscreens] {}: its pad is Godot's (the guest's input no longer reaches its handler)", what);
  }
  if (t_host_input) {
    original(ctx, base);
  }
}

}  // namespace

namespace refii::fable3 {

bool AnswerEndScreen(PPCContext& ctx, uint8_t* base, const char* action) {
  if (base == nullptr || action == nullptr) {
    return false;
  }
  const bool epilogue = std::strncmp(action, kEpilogueAction, sizeof(kEpilogueAction) - 1u) == 0;
  const bool credits = !epilogue && std::strncmp(action, kCreditsAction, sizeof(kCreditsAction) - 1u) == 0;
  if (!epilogue && !credits) {
    return false;
  }
  const char* at = action + (epilogue ? sizeof(kEpilogueAction) : sizeof(kCreditsAction)) - 1u;
  if ((at[0] != '1' && at[0] != '2') || at[1] != ':') {
    REXLOG_WARN("[fable3-endscreens] '{}' names no player; dropped", action);
    return false;
  }
  const uint32_t player = static_cast<uint32_t>(at[0] - '0');
  PPCContext work = refii::fable3::seam::Frame(ctx);
  work.r5.u64 = player;
  if (epilogue) {
    const uint32_t screen = g_epilogue.load();
    if (std::strcmp(at + 2, "close") != 0 || screen == 0u) {
      REXLOG_WARN("[fable3-endscreens] '{}' with no epilogue up; dropped", action);
      return false;
    }
    REXLOG_INFO("[fable3-endscreens] '{}': the epilogue's input with INTERACT as player {}", action, player);
    work.r3.u64 = screen;
    work.r4.u64 = kActionInteract;
    t_host_input = true;
    rex_Epilogue_HandleInput(work, base);
    t_host_input = false;
    return true;
  }
  if (std::strcmp(at + 2, "skip") != 0 || !g_credits.load()) {
    REXLOG_WARN("[fable3-endscreens] '{}' with no credits rolling; dropped", action);
    return false;
  }
  REXLOG_INFO("[fable3-endscreens] '{}': the credits' input with GUI_SELECT as player {}", action, player);
  work.r3.u64 = 0u;
  work.r4.u64 = kActionSelect;
  t_host_input = true;
  rex_CreditsScreen_HandleInput(work, base);
  t_host_input = false;
  return true;
}

}  // namespace refii::fable3

/// The epilogue's slot 5 (E, action, player).
REX_EXTERN(rex_Epilogue_HandleInput) {
  static std::atomic<bool> said{false};
  OwnedInput(ctx, base, __imp__rex_Epilogue_HandleInput, said, "epilogue");
}

/// The credits screen's slot 5 (screen, action, player).
REX_EXTERN(rex_CreditsScreen_HandleInput) {
  static std::atomic<bool> said{false};
  OwnedInput(ctx, base, __imp__rex_CreditsScreen_HandleInput, said, "credits");
}

/// `@Epilogue.Open(document, image, title, description, renown, titleName, descriptionName)`
/// when the show takes: it returns at once unless E+0x14 is clear (`lbz r11,0x14(r3)` 0x827A24DC /
/// 0x8276B46C) and then sets it unconditionally (0x827A2508). The show keeps its arguments (`mr r28,r4;
/// mr r27,r5; mr r29,r6; mr r26,r7` 0x827A24E0..disc 0x8276B47C) and writes them only behind the
/// screen's presentation (`lwz r30,0x20(r10)` disc 0x8276B4B4 / beq disc 0x8276B4BC): the title into the
/// text registry under `*disc dword_8355AC24` (r5 = r28, 0x827A2568..0x827A2580), the description
/// under `*disc dword_8355AC28` (r5 = r27, 0x827A258C..0x827A25A4); the renown r6 is formatted into
/// AC30 / AC34 (0x827A2608..0x827A265C). The names' hashes are read from those words (their
/// static initialisers store the FNV-1, disc sub_831C7360 / disc sub_831C73A0).
REX_EXTERN(rex_Epilogue_Show) {
  const uint32_t epilogue = ctx.r3.u32;
  const bool closed = base != nullptr && Plausible(epilogue) && base[epilogue + kEpilogueOpen] == 0u;
  const std::string image = closed ? ReadCharString(base, ctx.r7.u32) : std::string();
  const std::string title = closed ? ReadLhString(base, ctx.r4.u32) : std::string();
  const std::string description = closed ? ReadLhString(base, ctx.r5.u32) : std::string();
  const int32_t renown = static_cast<int32_t>(ctx.r6.u32);
  __imp__rex_Epilogue_Show(ctx, base);
  if (closed) {
    g_epilogue.store(epilogue);
    Statement("@Epilogue.Open")
        .Add(Text(kEpilogueDocument))
        .Add(Text(image))
        .Add(Text(title))
        .Add(Text(description))
        .Add(Number(static_cast<float>(renown)))
        .Add(Text(Hex(LoadGuestU32(base, kEpilogueTitleName))))
        .Add(Text(Hex(LoadGuestU32(base, kEpilogueDescriptionName))))
        .Post();
  }
}

/// THE SCREENSHOT. After `bl sub_827236F0` at 0x827A27F8 (r3 = r1+0x58 out, r4 = r1+0x50 the
/// image's path from sub_8253FB80 at 0x827A27C8, r5 = `*dword_8359B9D8` "ScreenShot"), the word at
/// r1+0x58 is the texture hash the show then stores raw into +512 of every element the ImageName
/// registry (R+0x30, `lwz r3,0x30(r7)` 0x827A2814) files under `*disc dword_8355AC44` (`lwz r9,
/// 0xA0+var_48(r1)` 0x827A2834, `stw r9,0x200(r11)` 0x827A2868) — only through the element's node.
/// A [[midasm_hook]] at 0x827A27FC: `@Epilogue.Image(name hash, texture hash, path)`.
void RefiiF3EpilogueScreenshot(PPCRegister& stack) {
  rex::system::KernelState* state = rex::system::kernel_state();
  rex::memory::Memory* memory = state != nullptr ? state->memory() : nullptr;
  const uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
  if (base == nullptr) {
    return;
  }
  Statement("@Epilogue.Image")
      .Add(Text(Hex(LoadGuestU32(base, kEpilogueImageName))))
      .Add(Text(Hex(LoadGuestU32(base, stack.u32 + 0x58u))))
      .Add(Text(ReadCharString(base, stack.u32 + 0x50u, 260u)))
      .Post();
}

/// `@Epilogue.Close` when the hide had an open screen to drop.
REX_EXTERN(rex_Epilogue_Hide) {
  const uint32_t epilogue = ctx.r3.u32;
  const bool open = base != nullptr && Plausible(epilogue) && base[epilogue + kEpilogueOpen] != 0u;
  __imp__rex_Epilogue_Hide(ctx, base);
  if (open) {
    g_epilogue.store(0u);
    Statement("@Epilogue.Close").Post();
  }
}

/// `@Credits.Open(document)` when the start ran the roll's init (SM+0x23 clear before).
REX_EXTERN(rex_ScreenManager_StartCredits) {
  const uint32_t manager = ctx.r3.u32;
  const bool idle = base != nullptr && Plausible(manager) && base[manager + kCreditsRunning] == 0u;
  __imp__rex_ScreenManager_StartCredits(ctx, base);
  if (idle) {
    g_credits.store(true);
    Statement("@Credits.Open").Add(Text(kCreditsDocument)).Post();
  }
}

/// `@Credits.Close` when the end popped a running roll (SM+0x23 set before).
REX_EXTERN(rex_ScreenManager_EndCredits) {
  const uint32_t manager = ctx.r3.u32;
  const bool running = base != nullptr && Plausible(manager) && base[manager + kCreditsRunning] != 0u;
  const uint32_t from = static_cast<uint32_t>(ctx.lr);
  // Return addresses of the three `bl ScreenManager_EndCredits` (TU3 / disc rows in guest_addresses.inc).
  const char* branch = from == refii::guest::Address(0x82B75248u)   ? "the skip with a second player seated"
                       : from == refii::guest::Address(0x82B751FCu) ? "the skip on the session's server (solo or host)"
                       : from == refii::guest::Address(0x82B75174u) ? "the roll ran out"
                                                                    : "a net handler";
  REXLOG_INFO("[fable3-endscreens] the credits end from {:08X} ({}); the roll {}", from, branch,
              running ? "was running: @Credits.Close" : "was not running (SM+0x23 clear): no close stated");
  __imp__rex_ScreenManager_EndCredits(ctx, base);
  // Its stores to the GUI screen id (0x825FA3D4, 0x825FA4E4), stated as it returns.
  refii::fable3::StateGuiScreen(base);
  if (running) {
    g_credits.store(false);
    Statement("@Credits.Close").Post();
  }
}
