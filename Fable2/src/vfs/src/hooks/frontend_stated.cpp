// THE FRONT END, STATED. Whether the front-end GUI exists, which of its two screens is active, each
// screen's mode, the title's press-A latch and the Load Game card the cursor is on, kept here from
// the guest's own calls and published as one word (stated value 13, kFrontEnd): bit 0 up, bits 1-2
// the screen (0 none, 1 title, 2 main menu), bit 3 press-A, bits 4-5 the screen whose warning is
// raised (0 none), bits 8-15 the title's mode, 16-23 the main menu's, 24-31 the card. Nothing here reads a screen object. Addresses are TU1 / disc.
//
// UP. The front-end GUI's constructor `sub_82378230(this)` / `sub_82378CD0` (`*(_DWORD *)a1 =
// off_820A924C` 0x82378250, title into +180, menu into +184, `*(a1 + 188) = *(a1 + 180)`
// 0x8237834c) and its scalar destructor `sub_823783B8` / `sub_82378E58` (`sub_82378408(); if (a2 &
// 1) free` — the body's base destructor clears the published GUI, `dword_83496B08 = 0`
// 0x82371700). The front-end mode's exit destroys it (`sub_822F5190` from 0x82319b30).
//
// THE ACTIVE SCREEN. `sub_82379488(frontEnd, screen)` / `sub_82379F28`: `*(a1 + 188) = a2;
// (*(*a2 + 28))(a2)` (0x823794d4 / 0x82379f74). Which screen is the activate that runs inside it:
// the title's `sub_826E30F0` / `sub_826C6380` (`*(a1 + 24) = 9; *(a1 + 80) = 0` 0x826e3124, disc
// +72), or the main menu's rex_lhGui_FrontendRoot_PopulateMainMenu (`*(a1 + 24) = 0` 0x826e21b8 /
// 0x826ca688). The front end's Enter runs the title's activate directly (0x82378728).
//
// THE TITLE'S MODE, from the events the host posts (gui_menu_hooks.cpp) and its input slot:
//   END_LEGAL 9 -> 0 (0x826e2dc8); CAN_PRESS_A latch = 1 (0x826e2e38); END_STARTED 10 -> 11
//   (0x826e2ed8); dialog DIALOG_ACTIVE 12, UNFREEZE 0 (0x826e3770 / 0x826e3798);
//   rex_lhGui_FrontendTitle_OnScreenAction: the two arms that act each play one slide — in 12 on
//   36 `GoToSlideAndRestart(dialog, 2)` then 1 (0x826e2f9c), else on 36 with the latch set the
//   latch clears and `GoToSlideAndRestart(root, 3)` then 10 (0x826e308c .. 0x826e30e0).
//   The idle attract (0 -> 8 -> 0, inline in the title's tick 0x826e3248 / 0x826e3510) has no
//   call and is not carried: the title reads 0 through it.
//
// THE MAIN MENU'S MODE:
//   events — choosecard BOY_ACTIVE 3, GIRL_ACTIVE 4, UNFREEZE 0 (0x826e0d84 ..); loadgame
//   LOAD_CARD_ACTIVE 5, UNFREEZE 0 (0x826e0e80 / 0x826e0ea0); dialog DIALOG_ACTIVE 6 (0x826e1068);
//   OpenDialog (`sub_826E40E0` / `sub_826CB158`): `*(a1 + 24) = 1` (0x826e4134 / 0x826cb1a8);
//   rex_lhGui_FrontendRoot_MenuActionDispatch: "New Game" with a profile and a free slot plays
//   `GoToSlideAndRestart(choosecard, 1)` then 2 (0x826e021c .. 0x826e022c); "Load Game" with two
//   or more saves stamps the cards (StampSaveCard) then 5 (0x826e07e0), the card back at 0
//   (0x826e03b0);
//   rex_lhGui_FrontendRoot_OnScreenAction — every arm that changes the mode plays a slide first:
//   30 in 2 or 4, 31 in 2 or 3 -> 1; 36 in 3 or 4 (no warning) -> 1, in 6 -> 1 with the answer
//   accepted (`+113 = 1` 0x826df9c0); 37 in 0 at the list's root -> 7 (0x826dfbb0), in 2, 3, 4
//   -> 1, in 5 -> 0 (0x826dfb7c), in 6 with cancel allowed -> 1 with the answer refused.
//
// THE WARNING'S ANSWER IS DERIVED, NOT STATED. The dialog's UNFREEZE (`sub_826E1040` /
// `sub_826C9460`) sets the mode from three fields its openers store inline (`+116`, `+120`,
// `+124`), which no call states. The mode is taken from the calls that branch makes instead:
// back to the title when it switches the screen (0x826e10f0); accepted, by the slide it plays —
// choosecard 1 -> 2 (0x826e1170), 6 -> 3, 7 -> 4 (0x826e11e8 / 0x826e1260), loadgame 2 -> 0
// (0x826e12d8) — and with no slide the mode the warning opened over when that was the carousel
// (5), else 0; refused -> 0 (every cancellable opener stores `+124 = 0`).
//
// THE WARNING IS RAISED from OpenDialog until its UNFREEZE — the span of the owner's own `+57`:
// set straight after OpenDialog returns (`sub_826E1C50` 0x826e1c78 / 0x826ca090, the title's
// `sub_826E3580` 0x826e3704 / 0x826c6974), cleared by the dialog's UNFREEZE (0x826e1090 /
// 0x826c94b0, the title's 0x826e3794 / 0x826c6a04).
//
// THE CARD. `sub_826E0BE0(menu, index)` / `sub_826C9008`: out of range returns 0, else `a1[18] =
// a2` (0x826e0cdc, disc `a1[15]`) and returns 1.
//
// THE SAVE LIST. `sub_826E14F8(menu)` / `sub_826C9918` (one caller, the dispatch's Load Game
// branch 0x826e0448 / 0x826c8884) walks the enumerated files: per file one `sub_82447160(saveMgr,
// &header, &fileName, user)` / `sub_824EA7A0` (0x826e1640 / 0x826c9a60) and one
// `sub_8243BE88(menu + 128, &text)` / `sub_8247D1A0` (0x826e19d8 / 0x826c9df4, disc menu + 116)
// pushing the card's finished text. Stated as `@FrontEnd.SaveList` (count) after
// `@FrontEnd.SaveText` (index, file name, text) per file.
//
// THE FRONT-END MODE (stated value 18): the game-flow mode that holds the front end, entered once
// the startup movies and the bank mounter are done. Its enter `sub_82319340` / `sub_82354BB8`
// opens with `byte_83496AC4 = 1` (0x82319360 / disc `byte_83496925` 0x82354bd8) and its exit
// `sub_82319A40` / `sub_823552B8` writes 0 (0x82319b24 / 0x8235539c) — the byte's only writers.
//
// THE SUBTITLES SETTING (the profile's, which the front end's Subtitles row shows). Its reader
// `sub_82382558` / `sub_82373650` answers the profile's flag (`sub_82443458`, 0x823826c0) — or
// writes 1 through the setter first on a first boot outside en-uk (0x823826a0) — and its one
// caller is the main menu's build (0x826e27a0 / 0x826cac60). The setter `sub_82443820(profiles,
// user, on)` / `sub_824E6E18` is called by that reader and the SubtitlesOn / SubtitlesOff arms of
// the menu dispatch (0x826e0a48, 0x826e0b58 / 0x826c8e70, 0x826c8f80). Each states the setting.
#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <initializer_list>
#include <mutex>
#include <string>
#include <vector>

#include "vfs_hooks_private.h"
#include "core/guest_build.h"
#include "core/guest_state.h"
#include "core/gui_native_calls.h"
#include "core/payload_bridge_fable2.h"
#include "hooks/frontend_stated.h"
#include "hooks/ppc_externs.h"

namespace {

constexpr uint32_t kFrontEnd = 13u;
constexpr uint32_t kFrontEndMode = 18u;

constexpr uint32_t kScreenNone = 0u;
constexpr uint32_t kScreenTitle = 1u;
constexpr uint32_t kScreenMenu = 2u;

struct Model {
    uint32_t gui = 0u;
    uint32_t title_screen = 0u;   // the title's activate's r3
    uint32_t menu_screen = 0u;    // the main menu's activate's r3
    uint32_t dialog_owner = 0u;   // OpenDialog's r3, while its warning is raised
    uint32_t screen = kScreenNone;
    uint32_t title = 0u;
    uint32_t menu = 0u;
    uint32_t card = 0u;
    uint32_t over = 0u;      // the menu's mode when its warning opened
    uint32_t dialog = kScreenNone;  // the screen whose warning is raised
    bool up = false;
    bool can_press_a = false;
    bool accepted = false;   // the warning's last answer
};

std::mutex g_lock;
Model g_model;
/// Counts the screens made active (the construction's title, then each switch to the title or the
/// main menu); never reset, so a front end made again does not repeat an earlier count.
uint32_t g_activation = 0u;
uint32_t g_published = 0u;

// One guest call in flight on this thread and what it has done so far. A nested call that states
// itself (the activates, the screen switch, OpenDialog, the card move) opens its own frame, so its
// slides are not its caller's.
struct Frame {
    uint32_t slides = 0u;
    uint32_t first_slide = 0u;
    uint32_t stamps = 0u;
    bool switched = false;
    bool at_root = false;
};

thread_local std::vector<Frame> t_frames;
thread_local int t_switching = 0;
thread_local bool t_switch_named = false;

// The save list being built on this thread: the file `sub_82447160` was last asked about.
thread_local uint32_t t_save_menu = 0u;
thread_local uint32_t t_save_count = 0u;
thread_local std::string t_save_file;

void Enter() { t_frames.emplace_back(); }

Frame Leave() {
    Frame frame = t_frames.back();
    t_frames.pop_back();
    return frame;
}

// Publishes the model; the caller holds the lock.
void Publish(const char* why) {
    const Model& m = g_model;
    const uint32_t word = (m.up ? 1u : 0u) | (m.screen << 1) | (m.can_press_a ? 8u : 0u) | (m.dialog << 4) |
                          ((m.title & 0xFFu) << 8) | ((m.menu & 0xFFu) << 16) | ((m.card & 0xFFu) << 24);
    if (word == g_published) {
        return;
    }
    g_published = word;
    refii_bridge_stated_value(kFrontEnd, word);
    REXLOG_INFO("Refii front end: {} — up {}, screen {}, title {}, menu {}, press-A {}, card {}, warning {}", why,
                m.up ? 1 : 0, m.screen, m.title, m.menu, m.can_press_a ? 1 : 0, m.card, m.dialog);
}

void Post(const char* name, std::initializer_list<std::string> arguments) {
    refii::vfs::GuiNativeCall call;
    call.name = name;
    for (const std::string& value : arguments) {
        refii::vfs::GuiNativeArgument argument;
        argument.kind = refii::vfs::GuiNativeArgument::Kind::kString;
        argument.text = value;
        call.arguments.push_back(std::move(argument));
    }
    refii::vfs::PostGuiNativeCall(std::move(call));
}

}  // namespace

namespace refii::vfs {

void FrontEndGotoSlide(uint32_t slide) {
    if (t_frames.empty()) {
        return;
    }
    Frame& frame = t_frames.back();
    if (frame.slides++ == 0u) {
        frame.first_slide = slide;
    }
}

void FrontEndCardStamped() {
    if (!t_frames.empty()) {
        ++t_frames.back().stamps;
    }
}

void FrontEndRootTestAnswered(bool at_root) {
    if (!t_frames.empty()) {
        t_frames.back().at_root = at_root;
    }
}

void FrontEndDialogOpening() { Enter(); }

void FrontEndDialogOpened(uint32_t owner) {
    Leave();
    std::lock_guard<std::mutex> lock(g_lock);
    g_model.dialog_owner = owner;
    if (g_model.screen == kScreenMenu) {
        g_model.over = g_model.menu;
        g_model.menu = 1u;
    } else if (g_model.screen == kScreenTitle) {
        g_model.title = 1u;
    }
    g_model.dialog = g_model.screen;
    Publish("a warning opened");
}

void FrontEndMenuActivating() { Enter(); }

void FrontEndMenuActivated(uint32_t menu) {
    Leave();
    std::lock_guard<std::mutex> lock(g_lock);
    g_model.menu_screen = menu;
    g_model.menu = 0u;
    if (t_switching > 0) {
        g_model.screen = kScreenMenu;
        t_switch_named = true;
        ++g_activation;
    }
    Publish("the main menu activated");
}

void FrontEndEventBegin() { Enter(); }

void FrontEndEventEnd(const char* presentation_name, const char* parent_name, const char* event_name) {
    const Frame frame = Leave();
    const std::string presentation = presentation_name;
    const std::string parent = parent_name;
    const std::string event = event_name;

    std::lock_guard<std::mutex> lock(g_lock);
    Model& m = g_model;
    if (presentation == "frontendstartupscreen") {
        if (parent.empty()) {
            if (event == "END_LEGAL" && m.title == 9u) m.title = 0u;
            else if (event == "END_STARTED" && m.title == 10u) m.title = 11u;
            else if (event == "CAN_PRESS_A") m.can_press_a = true;
        } else if (parent == "dialog") {
            if (event == "DIALOG_ACTIVE") {
                m.title = 12u;
            } else if (event == "UNFREEZE") {
                m.title = 0u;
                if (m.dialog == kScreenTitle) {
                    m.dialog = kScreenNone;
                    m.dialog_owner = 0u;
                }
            }
        }
    } else if (presentation == "frontendmainmenu") {
        if (parent == "choosecard") {
            if (event == "BOY_ACTIVE") m.menu = 3u;
            else if (event == "GIRL_ACTIVE") m.menu = 4u;
            else if (event == "UNFREEZE") m.menu = 0u;
        } else if (parent == "loadgame") {
            if (event == "LOAD_CARD_ACTIVE") m.menu = 5u;
            else if (event == "UNFREEZE") m.menu = 0u;
        } else if (parent == "dialog") {
            if (event == "DIALOG_ACTIVE") {
                m.menu = 6u;
            } else if (event == "UNFREEZE") {
                if (m.dialog == kScreenMenu) {
                    m.dialog = kScreenNone;
                    m.dialog_owner = 0u;
                }
                if (frame.switched || !m.accepted) {
                    m.menu = 0u;
                } else if (frame.slides == 0u) {
                    m.menu = m.over == 5u ? 5u : 0u;
                } else {
                    switch (frame.first_slide) {
                        case 1u: m.menu = 2u; break;
                        case 6u: m.menu = 3u; break;
                        case 7u: m.menu = 4u; break;
                        default: m.menu = 0u; break;
                    }
                }
            }
        }
    } else {
        return;
    }
    Publish(event_name);
}

uint32_t FrontEndTitleScreen() {
    std::lock_guard<std::mutex> lock(g_lock);
    return g_model.title_screen;
}

uint32_t FrontEndMenuScreen() {
    std::lock_guard<std::mutex> lock(g_lock);
    return g_model.menu_screen;
}

uint32_t FrontEndActiveScreen(uint32_t* activation) {
    std::lock_guard<std::mutex> lock(g_lock);
    if (activation != nullptr) {
        *activation = g_activation;
    }
    if (!g_model.up) {
        return 0u;
    }
    return g_model.screen == kScreenTitle ? g_model.title_screen
                                          : (g_model.screen == kScreenMenu ? g_model.menu_screen : 0u);
}

uint32_t FrontEndDialogOwner(bool* title) {
    std::lock_guard<std::mutex> lock(g_lock);
    if (title != nullptr) *title = g_model.dialog == kScreenTitle;
    return g_model.dialog != kScreenNone ? g_model.dialog_owner : 0u;
}

bool FrontEndUp() {
    std::lock_guard<std::mutex> lock(g_lock);
    return g_model.up;
}

uint32_t FrontEndMenuMode() {
    std::lock_guard<std::mutex> lock(g_lock);
    return g_model.up && g_model.screen == kScreenMenu ? g_model.menu : 0u;
}

}  // namespace refii::vfs

REX_EXTERN(rex_lhFrontEndGui_Ctor) {
    REX_FUNC_PROLOGUE();
    __imp__rex_lhFrontEndGui_Ctor(ctx, base);
    std::lock_guard<std::mutex> lock(g_lock);
    g_model = Model{};
    g_model.gui = ctx.r3.u32;
    g_model.up = true;
    g_model.screen = kScreenTitle;
    ++g_activation;
    Publish("constructed");
}

REX_EXTERN(rex_lhFrontEndGui_ScalarDtor) {
    REX_FUNC_PROLOGUE();
    const uint32_t gui = ctx.r3.u32;
    __imp__rex_lhFrontEndGui_ScalarDtor(ctx, base);
    std::lock_guard<std::mutex> lock(g_lock);
    if (gui == g_model.gui) {
        g_model = Model{};
        Publish("destroyed");
    }
}

REX_EXTERN(rex_lhFrontEndGui_SetActiveScreen) {
    REX_FUNC_PROLOGUE();
    const bool outer_named = t_switch_named;
    t_switch_named = false;
    ++t_switching;
    Enter();
    __imp__rex_lhFrontEndGui_SetActiveScreen(ctx, base);
    Leave();
    --t_switching;
    if (!t_switch_named) {
        std::lock_guard<std::mutex> lock(g_lock);
        g_model.screen = kScreenNone;
        Publish("a screen that is neither the title nor the main menu activated");
    }
    t_switch_named = outer_named;
    if (!t_frames.empty()) {
        t_frames.back().switched = true;
    }
}

REX_EXTERN(rex_lhGuiFrontEndTitle_Activate) {
    REX_FUNC_PROLOGUE();
    const uint32_t title = ctx.r3.u32;
    Enter();
    __imp__rex_lhGuiFrontEndTitle_Activate(ctx, base);
    Leave();
    std::lock_guard<std::mutex> lock(g_lock);
    g_model.title_screen = title;
    g_model.title = 9u;
    g_model.can_press_a = false;
    if (t_switching > 0) {
        g_model.screen = kScreenTitle;
        t_switch_named = true;
        ++g_activation;
    }
    Publish("the title activated");
}

REX_EXTERN(rex_lhGui_FrontendTitle_OnScreenAction) {
    REX_FUNC_PROLOGUE();
    const uint32_t action = ctx.r4.u32;
    Enter();
    __imp__rex_lhGui_FrontendTitle_OnScreenAction(ctx, base);
    const Frame frame = Leave();
    if (frame.slides == 0u || action != 36u) {
        return;
    }
    std::lock_guard<std::mutex> lock(g_lock);
    if (g_model.title == 12u) {
        g_model.title = 1u;
    } else {
        g_model.can_press_a = false;
        g_model.title = 10u;
    }
    Publish("the title took A");
}

REX_EXTERN(rex_lhGui_FrontendRoot_OnScreenAction) {
    REX_FUNC_PROLOGUE();
    const uint32_t action = ctx.r4.u32;
    uint32_t mode = 0u;
    {
        std::lock_guard<std::mutex> lock(g_lock);
        mode = g_model.menu;
    }
    Enter();
    __imp__rex_lhGui_FrontendRoot_OnScreenAction(ctx, base);
    const Frame frame = Leave();
    if (frame.slides == 0u) {
        return;
    }
    std::lock_guard<std::mutex> lock(g_lock);
    Model& m = g_model;
    if (m.menu != mode) {
        return;  // a nested call stated the mode itself (a warning opened)
    }
    switch (action) {
        case 30u:
            if (mode == 2u || mode == 4u) m.menu = 1u;
            break;
        case 31u:
            if (mode == 2u || mode == 3u) m.menu = 1u;
            break;
        case 36u:
            if (mode == 3u || mode == 4u) {
                m.menu = 1u;
            } else if (mode == 6u) {
                m.menu = 1u;
                m.accepted = true;
            }
            break;
        case 37u:
            if (mode == 0u) {
                if (frame.at_root) m.menu = 7u;
            } else if (mode == 2u || mode == 3u || mode == 4u) {
                m.menu = 1u;
            } else if (mode == 5u) {
                m.menu = 0u;
            } else if (mode == 6u) {
                m.menu = 1u;
                m.accepted = false;
            }
            break;
        default:
            break;
    }
    Publish("the main menu took an action");
}

PPC_EXTERN_FUNC(rex_lhSaveRecord_ScreenshotTexture);

namespace {

using refii::vfs::LoadGuestU32;
using refii::vfs::StoreGuestU32;

// The New Game limit this build keeps — see rex_lhFrontEnd_Enter below.
constexpr uint32_t kSaveLimit = 10u;

std::string Hex(uint32_t value) {
    char hex[9];
    std::snprintf(hex, sizeof hex, "%08X", value);
    return hex;
}

// THE PORTRAITS PAST THE SIXTH SAVE. The Load Game branch stamps saves 0 .. min(count, 6) - 1
// only (TU1 0x826e0720..0x826e0748, disc 0x826c8b50); the rest get what StampSaveCard would have
// derived for them, with its own call: a loadable record (`+24`, 0x826e1448 / disc 0x826c9868) gets
// `sub_824714E8(&hash, record + 16)` / `sub_8283D088` (0x826e1474 / 0x826c9894), else — or for a
// zero hash (0x826e1480) — the blank `dword_8349B600` / disc `dword_8349B080`. Stated as
// `@FrontEnd.SaveCard` (index, slot 0: no authored card carries it, texture hash).
void StateLaterPortraits(PPCContext& ctx, uint8_t* base, uint32_t menu) {
    constexpr uint32_t kStamped = 6u;
    const uint32_t begin = LoadGuestU32(base, menu + refii::guest::FrontEndMenuField(84u));
    const uint32_t end = LoadGuestU32(base, menu + refii::guest::FrontEndMenuField(88u));
    const uint32_t records = LoadGuestU32(base, menu + refii::guest::FrontEndMenuField(148u));
    if (begin == 0u || end <= begin || records == 0u) {
        return;
    }
    const uint32_t count = std::min<uint32_t>((end - begin) / 4u, 150u);
    const uint32_t blank = LoadGuestU32(base, refii::guest::Address(0x8349B600u));
    for (uint32_t index = kStamped; index < count; ++index) {
        const uint32_t record = records + (index * refii::guest::SaveRecordStride());
        uint32_t texture = 0u;
        if (base[record + 24u] != 0u) {
            PPCContext call = ctx;
            const uint32_t frame = (ctx.r1.u32 - 0x40u) & ~0xFu;
            const uint32_t out = frame + 0x20u;
            StoreGuestU32(base, out, 0u);
            call.r1.u32 = frame;
            call.r3.u32 = out;
            call.r4.u32 = record + 16u;
            rex_lhSaveRecord_ScreenshotTexture(call, base);
            texture = LoadGuestU32(base, out);
        }
        Post("@FrontEnd.SaveCard", {std::to_string(index), "0", Hex(texture != 0u ? texture : blank)});
    }
    if (count > kStamped) {
        REXLOG_INFO("Refii front end: portraits of saves {}..{} stated past the stamped six", kStamped, count - 1u);
    }
}

}  // namespace

// THE SAVE LIMIT, RAISED TO 10. `lhFrontEnd_Enter` (TU1 `sub_82378618`, disc `sub_823790B8`) is the
// limit's only writer — `li r10, 6` / `stw r10, dword_83319620` at 0x82378D38 / 0x82378D3C (disc
// 0x823797D8 / 0x823797DC, `dword_833194D8`) — and its two readers, the New Game guard (0x826E0148)
// and the container create (0x82445104), compare against it. A deliberate rule change the user
// asked for (2026-11-24), not a guest fact: the enumeration takes 150 and names run Hero000..149.
REX_EXTERN(rex_lhFrontEnd_Enter) {
    REX_FUNC_PROLOGUE();
    __imp__rex_lhFrontEnd_Enter(ctx, base);
    StoreGuestU32(base, refii::guest::Address(0x83319620u), kSaveLimit);
}

REX_EXTERN(rex_lhGui_FrontendRoot_MenuActionDispatch) {
    REX_FUNC_PROLOGUE();
    const std::string name = refii::vfs::ReadGuestLhString(base, ctx.r4.u32);
    const uint32_t menu = ctx.r3.u32;
    Enter();
    __imp__rex_lhGui_FrontendRoot_MenuActionDispatch(ctx, base);
    const Frame frame = Leave();
    if (name == "Load Game" && frame.stamps != 0u) {
        StateLaterPortraits(ctx, base, menu);
    }
    std::lock_guard<std::mutex> lock(g_lock);
    if (name == "Load Game" && frame.stamps != 0u) {
        g_model.menu = 5u;
        g_model.card = 0u;
    } else if (name == "New Game" && frame.slides != 0u && frame.first_slide == 1u) {
        g_model.menu = 2u;
    } else {
        return;
    }
    Publish("the main menu dispatched an entry");
}

REX_EXTERN(rex_lhGuiFrontEndRoot_SelectCard) {
    REX_FUNC_PROLOGUE();
    const uint32_t index = ctx.r4.u32;
    Enter();
    __imp__rex_lhGuiFrontEndRoot_SelectCard(ctx, base);
    Leave();
    if ((ctx.r3.u32 & 0xFFu) == 0u) {
        return;
    }
    std::lock_guard<std::mutex> lock(g_lock);
    g_model.card = index;
    Publish("the card moved");
}

REX_EXTERN(rex_lhGuiFrontEndRoot_BuildSaveTexts) {
    REX_FUNC_PROLOGUE();
    const uint32_t outer_menu = t_save_menu;
    const uint32_t outer_count = t_save_count;
    t_save_menu = ctx.r3.u32;
    t_save_count = 0u;
    t_save_file.clear();
    __imp__rex_lhGuiFrontEndRoot_BuildSaveTexts(ctx, base);
    Post("@FrontEnd.SaveList", {std::to_string(t_save_count)});
    REXLOG_INFO("Refii front end: {} save text(s) stated", t_save_count);
    t_save_menu = outer_menu;
    t_save_count = outer_count;
}

REX_EXTERN(rex_lhSaveManager_ReadSaveHeader) {
    REX_FUNC_PROLOGUE();
    if (t_save_menu != 0u) {
        t_save_file = refii::vfs::ReadGuestLhString(base, ctx.r5.u32);
    }
    __imp__rex_lhSaveManager_ReadSaveHeader(ctx, base);
}

REX_EXTERN(rex_lhWideStringVector_PushBack) {
    REX_FUNC_PROLOGUE();
    if (t_save_menu != 0u && ctx.r3.u32 == t_save_menu + refii::guest::FrontEndMenuField(128u)) {
        Post("@FrontEnd.SaveText", {std::to_string(t_save_count), t_save_file,
                                    refii::vfs::ReadGuestWideString(base, ctx.r4.u32)});
        ++t_save_count;
        t_save_file.clear();
    }
    __imp__rex_lhWideStringVector_PushBack(ctx, base);
}

REX_EXTERN(rex_lhProfile_GetSubtitlesEnabled) {
    REX_FUNC_PROLOGUE();
    __imp__rex_lhProfile_GetSubtitlesEnabled(ctx, base);
    refii_bridge_front_end_note_subtitles((ctx.r3.u32 & 0xFFu) != 0u ? 1 : 0);
}

REX_EXTERN(rex_lhProfile_SetSubtitlesEnabled) {
    REX_FUNC_PROLOGUE();
    const bool on = (ctx.r5.u32 & 0xFFu) != 0u;
    __imp__rex_lhProfile_SetSubtitlesEnabled(ctx, base);
    refii_bridge_front_end_note_subtitles(on ? 1 : 0);
}

REX_EXTERN(rex_lhFrontEndMode_Enter) {
    REX_FUNC_PROLOGUE();
    refii_bridge_stated_value(kFrontEndMode, 1u);
    __imp__rex_lhFrontEndMode_Enter(ctx, base);
}

REX_EXTERN(rex_lhFrontEndMode_Exit) {
    REX_FUNC_PROLOGUE();
    __imp__rex_lhFrontEndMode_Exit(ctx, base);
    refii_bridge_stated_value(kFrontEndMode, 0u);
}
