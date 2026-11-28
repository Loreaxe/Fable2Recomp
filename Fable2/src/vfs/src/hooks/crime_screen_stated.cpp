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
// call as `@CrimeScreen.Open <charge> <fine> <payFine> <communityService> <serviceKey> <arrested>
// <guard uid>`; the host composes both notices from its own Babel database. `arrested` is the player
// the arrest stated (gui_screen_stub_crime.cpp RefiiCrimeArrested: 1, 2, or 0 for an opener that is no
// arrest); `guard` the entity argument (r10, the screen's speaker) by its UniqueID (+0x14 / +0x18, as
// Refii.uid reads it), "" for none. An online host's arrest of player two is answered on the joiner's
// screen (Engine_UiCrimePage), which opens its own from these.
#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <utility>

#include "vfs_hooks_private.h"
#include "core/guest_state.h"
#include "core/gui_native_calls.h"
#include "hooks/gui_screen_stubs.h"
#include "hooks/ppc_externs.h"
#include "core/refii_mod.h"

PPC_EXTERN_FUNC(rex_lhGuiManager_FindScreenHandler);
PPC_EXTERN_FUNC(rex_lhCharString_CtorFromCStr);
PPC_EXTERN_FUNC(rex_lhCharString_Dtor);
PPC_EXTERN_FUNC(rex_lhWideString_Dtor);

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
    // An arrest states whose screen this is just before it opens it (gui_screen_stub_crime.cpp).
    refii::vfs::CrimeScreenOpening();
    const uint32_t arrested = refii::vfs::CrimeArrestedPlayer();
    const uint32_t guard = ctx.r10.u32;
    char guard_uid[24] = {};
    if (guard >= 0x10000u && guard < 0xC0000000u) {
        std::snprintf(guard_uid, sizeof(guard_uid), "0x%08x%08x", refii::vfs::LoadGuestU32(base, guard + 0x14u),
                      refii::vfs::LoadGuestU32(base, guard + 0x18u));
    }
    REXLOG_INFO("Refii crime screen: fine {}, pay fine {}, community service {}, service key '{}', charge {} bytes, "
                "arrested player {}, guard {}",
                fine, pay_fine, community_service, service_key, charge.size(), arrested,
                guard_uid[0] != 0 ? guard_uid : "none");

    refii::vfs::GuiNativeCall call;
    call.name = "@CrimeScreen.Open";
    Add(call, charge);
    Add(call, std::to_string(fine));
    Add(call, pay_fine ? "1" : "0");
    Add(call, community_service ? "1" : "0");
    Add(call, service_key);
    Add(call, std::to_string(arrested));
    Add(call, guard_uid);
    refii::vfs::PostGuiNativeCall(std::move(call));
    __imp__rex_lhGuiCrimeScreen_Open(ctx, base);
}

// THE JOINER OPENS THE HOST'S ARREST AS THE HOST'S GUEST BUILT IT (Refii._crime_screen, the co-op relay,
// GameUiCoopConsent). The Lua binding `GUI.DisplayCrimeScreen` (Lua_GUI_DisplayCrimeScreen TU1 0x82304F90) takes
// its charge as CRIME KEYS, one a line: it splits it at 10 (`sub_821C7408(&s, 10)`), counts each key in a map
// and composes the text from the map (`sub_823C8178(&wide, map)`) before the opener — so the host's composed text
// handed to it ("<bullet.img> Trespassing x1\n") was taken for a key and composed a second time (the doubled
// bullet, the stray suffix). Here the opener itself is called with the host's text: `sub_823C4128(*(QM+48),
// &charge, fine, payFine, communityService, &serviceKey, &key6, entity)` — the binding's own call (0x823050xx:
// `v26 = *(*(0x83496B08)+28)+48`, then the opener) — with the charge made wide losslessly (WideFromText). Before
// it the GUI's owner is stated as the arrest states it, `lhGuiManager_FindScreenHandler(QM, 0)` vf+32 with
// (criminal == players' henchman) (Lua_VillageCrimeManager_DisplayCrimeScreen TU1 0x827CFF08, the root owner at
// 0x827D0078 / disc 0x82855960): on the joiner the arrested is player two, so 1 — whose purse the page reads
// (RestatePurseForPlayerTwo's `sub_8236E400`) and whose pad it serves. 1 when the screen was opened.
extern "C" int32_t refii_crime_screen_open_here(PPCContext* ctx, uint8_t* base, const char* charge, int32_t fine,
                                                int32_t pay_fine, int32_t community_service, const char* service_key,
                                                uint32_t guard) {
    using refii::mods::LoadU32;
    const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
    const uint32_t gui = LoadU32(base, refii::guest::Address(0x83496B08u));
    const uint32_t qm = plausible(gui) ? LoadU32(base, gui + 28u) : 0u;
    const uint32_t opener = plausible(qm) ? LoadU32(base, qm + 48u) : 0u;
    if (ctx == nullptr || charge == nullptr || service_key == nullptr || !plausible(opener)) {
        return 0;
    }
    PPCContext frame = refii::mods::Frame(*ctx, 0x600u);
    const uint32_t area = (frame.r1.u32 + 0x100u) & ~0xFu;
    const uint32_t wide = area;
    const uint32_t key5 = area + 0x10u;
    const uint32_t key6 = area + 0x20u;
    const uint32_t chars = area + 0x40u;
    refii::mods::StoreU32(base, wide, 0u);
    if (!refii::vfs::WideFromText(frame, base, wide, charge)) {
        return 0;
    }
    PPCContext find = frame;
    find.r3.u64 = qm;
    find.r4.u64 = 0u;
    rex_lhGuiManager_FindScreenHandler(find, base);
    if (plausible(find.r3.u32)) {
        PPCContext owner = frame;
        owner.r4.u64 = 1u;
        refii::mods::CallVirtual(owner, base, find.r3.u32, 8u);  // vf+32
    }
    const std::string key(service_key);
    std::memcpy(base + chars, key.c_str(), std::min<size_t>(key.size(), 0x100u));
    base[chars + std::min<size_t>(key.size(), 0x100u)] = 0;
    base[chars + 0x110u] = 0;
    for (const auto& [at, text] : {std::pair<uint32_t, uint32_t>{key5, chars}, {key6, chars + 0x110u}}) {
        PPCContext make = frame;
        make.r3.u64 = at;
        make.r4.u64 = text;
        make.r5.s64 = -1;
        rex_lhCharString_CtorFromCStr(make, base);
    }
    PPCContext open = frame;
    open.r3.u64 = opener;
    open.r4.u64 = wide;
    open.r5.u64 = static_cast<uint32_t>(fine);
    open.r6.u64 = pay_fine != 0 ? 1u : 0u;
    open.r7.u64 = community_service != 0 ? 1u : 0u;
    open.r8.u64 = key5;
    open.r9.u64 = key6;
    open.r10.u64 = guard;
    rex_lhGuiCrimeScreen_Open(open, base);
    for (uint32_t at : {key5, key6}) {
        PPCContext drop = frame;
        drop.r3.u64 = at;
        rex_lhCharString_Dtor(drop, base);
    }
    PPCContext drop = frame;
    drop.r3.u64 = wide;
    rex_lhWideString_Dtor(drop, base);
    REXLOG_INFO("Refii crime screen: the host's arrest opens here as the host built it (fine {}, {} byte(s) of charge)",
                fine, std::strlen(charge));
    return 1;
}
