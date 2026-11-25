// refii — the pause root's minimap markers and the inline texture stores, stated for Godot.
//
// THE QUEST MARKERS. The pause root's Update (`sub_826A23F8`, disc `sub_8240E7B0`) runs the marker
// pass `sub_826A3150` (disc `sub_8240F508`) while `screen0+100` is clear (0x826a257c); OnEnter
// clears it (0x826a1b88 / 0x826a1d44 / 0x826a2338, disc 0x8240df40 / 0x8240e0fc / 0x8240e6f0) and
// the pass sets it (0x826a3178). The pass first asks `sub_826A3D98` (disc `sub_82410150`) for the
// map's rectangle, taken from the LevelExit token's and the map image's quads — on a headless
// presentation both are the inert record, whose corners are equal, so the guest's degenerate-quad
// test (`vcmpeqfp.` 0x826a3e7c / `bne` 0x826a3e8c) answered 0 and no marker was ever placed. The
// rectangle is Godot's layout: the gate is answered 1 at its read (`clrlwi r11, r3, 24`, TU1
// 0x826A3164 / disc 0x8240F51C). The pass then names each marker to `sub_826A3FD8` (disc
// `sub_82410390`; `mr r28, r4` / `mr r29, r5` at 0x826a3ff4 / 0x826a3ff8, disc 0x824103ac /
// 0x824103b0): r4 the token's path (`Foreground.ReusableTokens.Quest` / `.QuestExit` for the dog's
// trigger target, 0x826a3448; `.QuestGiver` for each giver of the primary quest, 0x826a361c —
// static inits 0x8325BA30 / 0x8325BA70 / 0x8325BAB0), r5 the world point it projects
// (`sub_826A3C00` takes it at 0x826a40f0). Each is stated here, and the placer's drawing body —
// the quad copy, the projection through the inert record and the render command — does not run:
// Godot projects the point as it projects the hero's token (Engine_UiPauseMinimap).
//
// THE INLINE TEXTURE STORES. Three writers set a quad record's texture (`+0x200`, with the dirty
// byte `+0x224`) only through the element's `+4` node, which a headless element does not have, so
// they skip. Two are stated where the element and the hash are known:
//   the pause map image (the inline store 0x826A21EC in `sub_826A19E0`, disc `sub_8240DD98`): the
//     ported OnEnter (gui_screen_stub_pause_root.cpp) hands the element and the key it hashed and
//     `sub_826A4388` refined (StatePauseMapTexture);
//   each co-op save card's portrait (`sub_8243B520` 0x8243B57C, disc `sub_8247C7D8` 0x8247C834):
//     r31 the card's screenshot element, the hash at *r29 (0x8243b5ac / 0x8247c864).
// The third, each legend row's icon, is its call's argument: SetMapKey (`sub_826A3AC8`, disc
// `sub_8240FE80`) is stated at its entry (gui_headless_presentation.cpp, `@PauseMap.Key`).

#include <cstdint>
#include <cstring>
#include <string>

#include <rex/logging.h>
#include <rex/ppc/context.h>

#include "core/guest_state.h"
#include "core/payload_bridge_fable2.h"
#include "gui/gui_told_statements.h"
#include "hooks/gui_screen_stubs.h"

extern "C" uint8_t* RefiiGuestBase(void);

namespace {

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

/// The texture a store would have put on a headless element, told as the element's texture.
void TellStoredTexture(uint8_t* base, uint32_t element, uint32_t hash) {
    uint32_t presentation = 0;
    uint32_t serial = 0;
    uint32_t id = 0;
    if (base == nullptr || !Plausible(element) || hash == 0u ||
        !refii::vfs::HeadlessElementKey(element, &presentation, &serial, &id)) {
        return;
    }
    refii_bridge_told_push(static_cast<uint32_t>(refii::vfs::ToldStatement::Kind::kTexture),
                           presentation, serial, refii::vfs::kHeadlessOrdinal | id, hash, 0u);
}

/// `Foreground.ReusableTokens.<leaf>` to the marker's token: 0 Quest, 1 QuestExit, 2 QuestGiver.
uint32_t TokenOf(const std::string& path) {
    const std::size_t dot = path.rfind('.');
    const std::string leaf = dot == std::string::npos ? path : path.substr(dot + 1u);
    if (leaf == "Quest") {
        return 0u;
    }
    if (leaf == "QuestExit") {
        return 1u;
    }
    if (leaf == "QuestGiver") {
        return 2u;
    }
    return 0xFFFFFFFFu;
}

float GuestFloat(uint8_t* base, uint32_t at) {
    const uint32_t bits = refii::vfs::LoadGuestU32(base, at);
    float value = 0.0f;
    std::memcpy(&value, &bits, sizeof value);
    return value;
}

}  // namespace

// The marker pass's rectangle test, read at TU1 0x826A3164 / disc 0x8240F51C: answered 1, and the
// pass's markers start over.
void RefiiPauseMarkerGate(PPCRegister& r3) {
    refii_bridge_pause_markers_begin();
    r3.u64 = 1u;
}

// The marker placer's entry, TU1 0x826A3FD8 / disc 0x82410390: r4 the token's path (CCharString*),
// r5 the world point. Stated, and the drawing body skipped.
bool RefiiPauseMarker(PPCRegister& r4, PPCRegister& r5) {
    uint8_t* base = RefiiGuestBase();
    if (base == nullptr) {
        return false;
    }
    const std::string path = Plausible(r4.u32) ? refii::vfs::ReadGuestLhString(base, r4.u32)
                                               : std::string();
    const uint32_t token = TokenOf(path);
    if (token != 0xFFFFFFFFu && Plausible(r5.u32)) {
        const float x = GuestFloat(base, r5.u32 + 0u);
        const float y = GuestFloat(base, r5.u32 + 4u);
        const float z = GuestFloat(base, r5.u32 + 8u);
        refii_bridge_pause_marker_note(token, x, y, z);
        REXLOG_INFO("Refii pause minimap: the guest places '{}' at ({:.1f}, {:.1f}, {:.1f})", path, x,
                    y, z);
    }
    return true;
}

// The co-op card portrait: the element, and the address of the hash.
void RefiiHeadlessInlineTexture(PPCRegister& element, PPCRegister& hash_at) {
    uint8_t* base = RefiiGuestBase();
    if (base == nullptr || !Plausible(hash_at.u32)) {
        return;
    }
    TellStoredTexture(base, element.u32, refii::vfs::LoadGuestU32(base, hash_at.u32));
}

// The pause map image: the element and the key the ported OnEnter (gui_screen_stub_pause_root.cpp) hashed and
// `sub_826A4388` refined.
namespace refii::vfs {
void StatePauseMapTexture(uint8_t* base, uint32_t element, uint32_t hash) { TellStoredTexture(base, element, hash); }
}  // namespace refii::vfs
