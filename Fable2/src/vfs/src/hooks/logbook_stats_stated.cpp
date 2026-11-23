// THE LOGBOOK'S STATS VIEW, STATED.
//
//   opened on another player  `sub_823C48D8(_, stats)` (disc `sub_824F9B08`), the orb box's View
//       Stats answer (`sub_8281E410` 0x8281e5c0, disc `sub_828404D0` 0x82840680): sets the
//       logbook's mode 2 and keeps the stats object, `v3[6] = a2` (0x823c4920).
//   filled  `sub_829BAEB8(page, stats)` (disc `sub_828D4298`), from the logbook's open
//       `sub_826D8DD8` (disc `sub_82581A68`): in mode 2 with the kept object (0x826d8e68, disc
//       0x82581af8), else with the hero's own (0x826d9050, disc 0x82581ce0). It writes every
//       counter text through the object's getters and the title from its name.
//
// The fill states `@Logbook.Stats 0` for the hero's own book, and for another player's
// `@Logbook.Stats 1 <name> <counter 0..123>`: the argument object as its own getters read it
// (vtable 0x820A9B6C, disc 0x820A8404) — slot 0 `*(this + 8 + 4*id)` (`addi r11,r4,2; slwi
// r10,r11,2; lwzx r3,r10,r3` 0x82390c18, disc 0x8238de00), slot 2 the CCharString at `this + 4`
// (`addi r4,r4,4` 0x82390c48, disc 0x8238de30).
#include <atomic>
#include <cstdint>
#include <string>

#include "vfs_hooks_private.h"
#include "core/guest_state.h"
#include "core/gui_native_calls.h"
#include "hooks/ppc_externs.h"

namespace {

constexpr uint32_t kCounters = 124u;

std::atomic<uint32_t> g_other_players_stats{0u};

void Add(refii::vfs::GuiNativeCall& call, std::string value) {
    refii::vfs::GuiNativeArgument argument;
    argument.kind = refii::vfs::GuiNativeArgument::Kind::kString;
    argument.text = std::move(value);
    call.arguments.push_back(std::move(argument));
}

}  // namespace

REX_EXTERN(rex_lhGuiLogbookScreen_OpenOnPlayer) {
    REX_FUNC_PROLOGUE();
    g_other_players_stats.store(ctx.r4.u32);
    __imp__rex_lhGuiLogbookScreen_OpenOnPlayer(ctx, base);
}

REX_EXTERN(rex_lhGuiLogbookStatsPage_Fill) {
    REX_FUNC_PROLOGUE();
    const uint32_t stats = ctx.r4.u32;
    const bool other = stats != 0u && stats == g_other_players_stats.load();
    refii::vfs::GuiNativeCall call;
    call.name = "@Logbook.Stats";
    Add(call, other ? "1" : "0");
    if (other) {
        Add(call, refii::vfs::ReadGuestLhString(base, stats + 4u));
        for (uint32_t id = 0; id < kCounters; ++id) {
            Add(call, std::to_string(static_cast<int32_t>(refii::vfs::LoadGuestU32(base, stats + 8u + 4u * id))));
        }
    }
    refii::vfs::PostGuiNativeCall(std::move(call));
    REXLOG_INFO("Refii logbook: stats page filled from {:08X} ({})", stats, other ? "another player" : "the hero");
    __imp__rex_lhGuiLogbookStatsPage_Fill(ctx, base);
}
