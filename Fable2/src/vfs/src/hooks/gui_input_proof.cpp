// THE GUI'S RAW PASS, COUNTED: the statement that a button Godot owns no longer reaches the
// guest's UI. Addresses are TU1 / disc.
//
// `sub_82403350` / `sub_823B4590` (r3 the GUI input object, r4 the input record) is the raw eat
// both players' `CGUIInput` vf6 run first: `bl sub_82403350` at 0x824030E8 (player one) and
// 0x829C1D04 (player two) / disc 0x823B4328 and 0x82845FCC, its only code callers; its only
// pointer in the image is its .pdata entry (0x82116078 / 0x82113970), so no vtable holds it. The
// record is the dispatcher's: +8 the button (byte, 1..16), +40 the pad, +44 the kind — read the
// same way by the front end's translator (`lbz r11,8(r29)`, `lwz r5,0x28(r29)`, `lwz r10,0x2C(r29)`
// 0x826e39fc / 0x826e39e8 / 0x826e39f0). Kind 13 is a press: vf6's kind switch takes `+44 - 1`
// and its case 12 is the press handler (`bl sub_82403920` 0x82403168).
//
// Every guest UI consumer of a pad press in game starts from such a record: the raw HUD and box
// slots inside `sub_82403350` itself, Start (`sub_82403920` → `bl sub_82371B10` 0x82403D50 /
// 0x823B4F8C), and every action the dispatcher translates from the record afterwards — the modal
// router (0x82404AA0 / 0x823B5CDC), the box chain, a screen's vf+24 (0x82404C1C / 0x823B5E58), the
// wheel and the blade (`sub_82405190`), Start's action 7 (0x824049B8 / 0x823B5BF4). A button Godot
// masks makes no press record at all: the pad's edge detector `sub_821894A8` makes a press only on
// a rising bit. So each press reaching here is logged with its button, its pad, the stated screen
// and modal mode, and a run total; under a Godot surface no masked button may appear.
//
// `sub_82371B10`'s two other callers are not pad presses: the game-flow tick (0x8217649C / disc
// 0x82176264, r5 = 1) and `sub_822CAD38` (0x822CB0C0 / disc `sub_822CAD30` 0x822CB0B8, player
// `dword_83496AF8`) — the four code callers on each build with the two press sites above.
#include <array>
#include <atomic>
#include <cstdint>

#include "vfs_hooks_private.h"
#include "core/payload_bridge_fable2.h"
#include "hooks/ppc_externs.h"

namespace {

constexpr uint32_t kPressKind = 13u;

/// The dispatcher's button numbers (`sub_821894A8`).
constexpr std::array<const char*, 17> kButtons = {
    "?",  "D-up", "D-down", "D-left", "D-right", "Start", "Back", "LS", "RS",
    "LB", "RB",   "A",      "B",      "X",       "Y",     "LT",   "RT",
};

std::atomic<uint64_t> g_presses{0u};

}  // namespace

REX_EXTERN(rex_lhGuiInput_RawPass) {
    REX_FUNC_PROLOGUE();
    const uint32_t record = ctx.r4.u32;
    if (record >= 0x10000u && record < 0xC0000000u &&
        refii::vfs::LoadGuestU32(base, record + 44u) == kPressKind) {
        const uint32_t button = base[record + 8u];
        const uint32_t pad = refii::vfs::LoadGuestU32(base, record + 40u);
        uint32_t mode = 0u;
        uint32_t id = 0u;
        const bool screens = refii_bridge_stated_pause(&mode, &id, nullptr) != 0u;
        const uint32_t screen = screens ? refii_bridge_stated_screen_now() : 0xFFFFFFFFu;
        const uint64_t n = g_presses.fetch_add(1u, std::memory_order_relaxed) + 1u;
        REXLOG_INFO("Refii input: a press reached the GUI's raw pass — button {} ({}), pad {}, screen {}, "
                    "modal mode {} id {} (#{})",
                    button, button < kButtons.size() ? kButtons[button] : "?", pad,
                    screen == 0xFFFFFFFFu ? -1 : static_cast<int64_t>(screen), mode, id, n);
    }
    __imp__rex_lhGuiInput_RawPass(ctx, base);
}
