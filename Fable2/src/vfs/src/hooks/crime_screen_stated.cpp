// THE CRIME SCREEN, STATED. Its one opener `sub_823C4128(_, charge, fine, payFine,
// communityService, serviceKey, _, entity)` (disc `sub_824F9358`; r4..r8, r10) is called by
// `Lua_GUI_DisplayCrimeScreen` and two native arrests. It writes the screen from its arguments:
//   - `screen+32` = Babel("GUI_SCREEN_CRIME_YOU_HAVE_BEEN_CHARGED_WITH") + L"\n" + charge
//     (0x823c4198..dc; the separator is `word_820ADF4C` / disc `unk_820B4D68`,
//     the units 000A 0000);
//   - `screen+40` = fine (0x823c4220), `+44` = payFine (0x823c4240), `+45` = communityService
//     (0x823c4260);
//   - `screen+36` = Babel("GUI_SCREEN_CRIME_COMMUNITY_SERVICE") + L"\n" + Babel(serviceKey), in
//     `sub_826A96F0` (disc `sub_82591F68`).
//
// `charge` is a wide LHString handle (`sub_82B3EE50`: `sub_82B3FA78(*a1, (_WORD *)**a2)`, NUL
// terminated); `serviceKey` a CCharString handle (`sub_821F0240` copies it). Stated before the
// call as `@CrimeScreen.Open <charge> <fine> <payFine> <communityService> <serviceKey>`; the host
// composes both notices from its own Babel database.
#include <cstdint>
#include <string>

#include "vfs_hooks_private.h"
#include "core/guest_state.h"
#include "core/gui_native_calls.h"
#include "hooks/gui_screen_stubs.h"
#include "hooks/ppc_externs.h"

namespace {

void Add(refii::vfs::GuiNativeCall& call, std::string value) {
    refii::vfs::GuiNativeArgument argument;
    argument.kind = refii::vfs::GuiNativeArgument::Kind::kString;
    argument.text = std::move(value);
    call.arguments.push_back(std::move(argument));
}

// The wide string at `handle`: `*handle` the body, `*body` the NUL-terminated UTF-16 units.
std::string WideArgument(uint8_t* base, uint32_t handle) {
    constexpr uint32_t kMaxUnits = 4096u;
    const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
    uint32_t body = 0u;
    uint32_t chars = 0u;
    if (!plausible(handle) || !refii::vfs::TryLoadGuestU32(base, handle, &body) || !plausible(body) ||
        !refii::vfs::TryLoadGuestU32(base, body, &chars) || !plausible(chars)) {
        return std::string();
    }

    std::string value;
    for (uint32_t i = 0; i < kMaxUnits; ++i) {
        uint8_t raw[2];
        if (refii::vfs::TryCopyGuestBytes(base, chars + i * 2u, raw, 2u) < 2u) {
            break;
        }
        const uint32_t unit = (static_cast<uint32_t>(raw[0]) << 8) | raw[1];
        if (unit == 0u) {
            break;
        }
        if (unit < 0x80u) {
            value.push_back(static_cast<char>(unit));
        } else if (unit < 0x800u) {
            value.push_back(static_cast<char>(0xC0u | (unit >> 6)));
            value.push_back(static_cast<char>(0x80u | (unit & 0x3Fu)));
        } else {
            value.push_back(static_cast<char>(0xE0u | (unit >> 12)));
            value.push_back(static_cast<char>(0x80u | ((unit >> 6) & 0x3Fu)));
            value.push_back(static_cast<char>(0x80u | (unit & 0x3Fu)));
        }
    }
    return value;
}

}  // namespace

REX_EXTERN(rex_lhGuiCrimeScreen_Open) {
    REX_FUNC_PROLOGUE();
    const std::string charge = WideArgument(base, ctx.r4.u32);
    const int32_t fine = static_cast<int32_t>(ctx.r5.u32);
    const bool pay_fine = (ctx.r6.u32 & 0xFFu) != 0u;
    const bool community_service = (ctx.r7.u32 & 0xFFu) != 0u;
    const std::string service_key = refii::vfs::ReadGuestLhString(base, ctx.r8.u32);
    REXLOG_INFO("Refii crime screen: fine {}, pay fine {}, community service {}, service key '{}', charge {} bytes",
                fine, pay_fine, community_service, service_key, charge.size());

    refii::vfs::GuiNativeCall call;
    call.name = "@CrimeScreen.Open";
    Add(call, charge);
    Add(call, std::to_string(fine));
    Add(call, pay_fine ? "1" : "0");
    Add(call, community_service ? "1" : "0");
    Add(call, service_key);
    refii::vfs::PostGuiNativeCall(std::move(call));

    // An arrest states whose screen this is just before it opens it (gui_screen_stub_crime.cpp).
    refii::vfs::CrimeScreenOpening();
    __imp__rex_lhGuiCrimeScreen_Open(ctx, base);
}
