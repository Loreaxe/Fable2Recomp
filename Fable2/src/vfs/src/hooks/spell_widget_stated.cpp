// THE SPELL ASSIGNMENT WIDGET, STATED. One 420-byte class serves the quick wheel over the game
// (`*(HUD + 92)` player one, `+96` player two) and the abilities screen's blade (`screen + 200`).
// Each hook is one of the widget's own methods, called with the widget in r3; the cursor and
// lists are stated as that method left them (`@Spell.Widget`):
//
//   open   `sub_826B5048(widget, player, name, presentation)` (disc `sub_8259A2E0`): stores the
//          player at `+416` and builds the lists (`sub_826B4848`).
//   show   `sub_826B5608(widget, mode)` (disc `sub_8259A8A0`): 0 the wheel, 1 the blade. The
//          mode is stated from the call's own argument: the byte it is kept in (`+12`) also
//          flips when the wheel expands.
//   move   `sub_826B5430(widget, action)` (disc `sub_8259A6C8`): 28 / 29 move the row, 30 / 31
//          the row's cell, 36 copies the row's spell down.
//   close  `sub_826B5668(widget)` (disc `sub_8259A900`).
//
// Words after the op, widget, player and mode (empty on every op but the show): row `+176`, highest row `+412`, five counts
// `+180 + 4r`, five cells `+200 + 4r`, and five lists of eight `+220 + 32r + 4c` (entries past a
// row's count are not the widget's and are sent as 0).
#include <array>
#include <cstdint>
#include <mutex>
#include <string>
#include <unordered_map>

#include "vfs_hooks_private.h"
#include "core/gui_native_calls.h"
#include "hooks/ppc_externs.h"

namespace {

constexpr uint32_t kRows = 5u;
constexpr uint32_t kCells = 8u;

void StateWidget(uint8_t* base, uint32_t widget, const char* op, bool with_words,
                 const std::string& mode = std::string()) {
    if (widget < 0x10000u) return;
    const auto word = [&](uint32_t at) { return refii::vfs::LoadGuestU32(base, widget + at); };

    refii::vfs::GuiNativeCall call;
    call.name = "@Spell.Widget";
    const auto add = [&call](std::string text) {
        refii::vfs::GuiNativeArgument argument;
        argument.kind = refii::vfs::GuiNativeArgument::Kind::kString;
        argument.text = std::move(text);
        call.arguments.push_back(std::move(argument));
    };
    add(op);
    add(std::to_string(widget));
    add(std::to_string(word(416u)));
    add(mode);
    if (with_words) {
        const uint32_t row = word(176u);
        const int32_t max_row = static_cast<int32_t>(word(412u));
        if (row >= kRows || max_row < -1 || max_row >= static_cast<int32_t>(kRows)) return;
        add(std::to_string(row));
        add(std::to_string(max_row));
        std::array<uint32_t, kRows> counts{};
        for (uint32_t r = 0; r < kRows; ++r) {
            const uint32_t count = word(180u + 4u * r);
            counts[r] = count <= kCells ? count : 0u;
            add(std::to_string(counts[r]));
        }
        for (uint32_t r = 0; r < kRows; ++r) {
            const uint32_t cell = word(200u + 4u * r);
            add(std::to_string(cell < kCells ? cell : 0u));
        }
        for (uint32_t r = 0; r < kRows; ++r) {
            for (uint32_t c = 0; c < kCells; ++c) {
                add(std::to_string(c < counts[r] ? word(220u + 32u * r + 4u * c) : 0u));
            }
        }
    }
    // A held direction runs the action every frame; a move that changed nothing is not restated.
    static std::mutex lock;
    static std::unordered_map<uint32_t, std::string> said;
    std::string all;
    for (size_t i = 1; i < call.arguments.size(); ++i) {
        all += call.arguments[i].text;
        all += ' ';
    }
    {
        std::lock_guard<std::mutex> guard(lock);
        std::string& last = said[widget];
        if (op[0] == 'm' && last == all) return;
        last = with_words ? all : std::string();
    }
    refii::vfs::PostGuiNativeCall(std::move(call));
}

}  // namespace

REX_EXTERN(rex_lhGuiWillAssignWidget_Open) {
    REX_FUNC_PROLOGUE();
    const uint32_t widget = ctx.r3.u32;
    __imp__rex_lhGuiWillAssignWidget_Open(ctx, base);
    StateWidget(base, widget, "open", true);
}

REX_EXTERN(rex_lhGuiWillAssignWidget_Show) {
    REX_FUNC_PROLOGUE();
    const uint32_t widget = ctx.r3.u32;
    const uint32_t mode = ctx.r4.u32 & 0xFFu;
    __imp__rex_lhGuiWillAssignWidget_Show(ctx, base);
    StateWidget(base, widget, "show", true, std::to_string(mode));
}

REX_EXTERN(rex_lhGuiWillAssignWidget_OnScreenAction) {
    REX_FUNC_PROLOGUE();
    const uint32_t widget = ctx.r3.u32;
    __imp__rex_lhGuiWillAssignWidget_OnScreenAction(ctx, base);
    StateWidget(base, widget, "move", true);
}

REX_EXTERN(rex_lhGuiWillAssignWidget_Close) {
    REX_FUNC_PROLOGUE();
    const uint32_t widget = ctx.r3.u32;
    __imp__rex_lhGuiWillAssignWidget_Close(ctx, base);
    StateWidget(base, widget, "close", false);
}
