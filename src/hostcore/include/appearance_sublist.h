#pragma once

// refii — active sub-component list check on CECGraphicAppearance (component 3).
//
// In both Fable 2 and Fable 3, CECGraphicAppearance holds an MSVC `std::list` of active
// `IGraphicAppearanceSubComponent*` interfaces (`subcomp + 0x0Cu`). When an entity switches to a
// single-model override (e.g. a Hollow Man crumbling into separate bones via `sub_8276FE38`),
// the guest unlinks `CECGraphicAppearanceMorph` (`comp17 + 0x0Cu` / `comp20 + 0x0Cu`) from that
// draw list while leaving the morph component's built and source vectors populated in memory
// for potential re-assembly. Walking the list prevents stale body/clothing morph parts or
// inactive eyes from being merged onto the replacement single model.
//
// Header-only with zero host dependencies: works over `GuestReader` (host_core.h) and synthetic
// test readers (`SyntheticMemory`, entity_walk_test.cpp).

#include <cstdint>

namespace refii::hostcore {

/// Fable 2 stores a 4-byte list header/allocator at comp3 + 0x0C, placing `_Myhead` at +0x10.
inline constexpr uint32_t kFable2AppearanceSublistOffset = 0x10u;

/// Fable 3 places the circular `std::list` sentinel `_Myhead` directly at comp3 + 0x0C.
inline constexpr uint32_t kFable3AppearanceSublistOffset = 0x0Cu;

/// Bit 3 (`8u`) of EntityAppearance.Flags: entity carries a CECCreature component whose
/// alive byte (`+0x18`) is 0 (Fable 2 comp 20, Fable 3 comp 24).
inline constexpr uint32_t kAppearanceDeadCreature = 8u;

/// Bit 4 (`16u`) of EntityAppearance.Flags: the guest leaves that dead creature lying —
/// `CECCreature_FinishDeath` ran on it since its constructor. Fable 2 states it; Fable 3 does not
/// yet (its FinishDeath on component 24 is unread).
inline constexpr uint32_t kAppearanceGuestCorpse = 16u;

/// Returns true if `creature_component` is plausible and its `alive` byte at `+0x18` is 0.
template <typename Memory>
bool IsDeadCreature(const Memory& memory, uint32_t creature_component) {
    return creature_component >= 0x10000u && creature_component < 0xC0000000u &&
           memory.Byte(creature_component + 0x18u) == 0u;
}

/// Returns true if `subcomp_node` (`subcomp + 0x0Cu`) is linked in component 3's active draw list
/// at `renderable + sentinel_offset`, or if the renderable/sentinel state is absent or torn
/// (conservative fallback so synthetic/partial entities are never hidden).
template <typename Memory>
bool IsAppearanceSubcomponentActive(const Memory& memory, uint32_t renderable,
                                    uint32_t sentinel_offset, uint32_t subcomp_node) {
    constexpr auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };

    if (!plausible(renderable) || !plausible(subcomp_node)) {
        return true;
    }

    const uint32_t sentinel = memory.Word(renderable + sentinel_offset);
    if (!plausible(sentinel)) {
        return true;
    }

    uint32_t link = memory.Word(sentinel);
    for (uint32_t depth = 0u; depth < 64u && link != sentinel; ++depth) {
        if (!plausible(link)) {
            return true;
        }
        if (memory.Word(link + 8u) == subcomp_node) {
            return true;
        }
        link = memory.Word(link);
    }

    return false;
}

}  // namespace refii::hostcore
