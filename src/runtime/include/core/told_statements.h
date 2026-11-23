// What a title's guest TELLS its presentations' elements, posted to the host as statements.
//
// SHARED STATE, in librefii_runtime: a title's hooks (the payload, on the guest's threads) push
// through the refii_bridge_told_* C entries (core/payload_bridge.h), and the title's host bridge
// drains them with the Take* functions below for the shell's RefiiHostTakeToldStatements /
// RefiiHostTakeToldTexts / RefiiHostToldHeadless* exports. A HEADLESS presentation's elements
// (core/headless_presentation.h, the shared headless core) are keyed by the id their creation
// stated; the registry of those presentations and ids is kept here too.
#pragma once

#include <cstdint>

#include "core/payload_bridge.h"

namespace refii::vfs {

/// One statement the guest made to an element, keyed `(presentation, root name hash, ordinal)`.
struct ToldStatement {
    enum class Kind : uint32_t {
        kGotoSlide = 1,  // a = slide
        kPaused = 2,     // a = paused
        kAttribute = 3,  // a = attribute id (27 bits), b = value bits
        kActive = 4,     // a = on
        kTexture = 5,    // a = resource hash the element's quad now shows
    };

    Kind kind = Kind::kGotoSlide;
    uint32_t presentation = 0;
    uint32_t root_hash = 0;
    uint32_t ordinal = 0;
    uint32_t a = 0;
    uint32_t b = 0;
};

/// A headless presentation's element is keyed by the id its creation stated, not by a walk of the
/// live tree: `root_hash` carries the presentation's serial and `ordinal` this flag with the id.
/// The host resolves the id's name path against the presentation's file
/// (RefiiHostToldHeadlessElement / RefiiHostToldHeadlessFile).
constexpr uint32_t kHeadlessOrdinal = 0x80000000u;

/// HOST BRIDGE side: takes the queued statements, oldest first, six words each (kind,
/// presentation, root hash, ordinal, a, b); returns how many were written.
uint32_t TakeToldStatements(uint32_t* words, uint32_t capacity);

/// HOST BRIDGE side: takes the queued texts, oldest first: four words each (presentation, root
/// name hash, ordinal, UTF-16 length) into `words`, the units back to back into `units`; returns
/// how many.
uint32_t TakeToldTexts(uint32_t* words, uint32_t word_capacity, uint16_t* units, uint32_t unit_capacity);

/// HOST BRIDGE side: a headless presentation's file, as its create named it — the length written
/// (at most `capacity - 1`, NUL-terminated), 0 when `serial` is not the one at `presentation`.
uint32_t ToldHeadlessFile(uint32_t presentation, uint32_t serial, char* out, uint32_t capacity);

/// The id a title states when no screen is up.
constexpr uint32_t kNoStatedScreen = 0xFFFFFFFFu;

/// HOST BRIDGE side: a count that moves whenever the live list or the stated screen changes — a
/// presentation made, destroyed or given a screen, a gate written with another value, another
/// screen stated.
uint32_t ToldHeadlessGeneration();

/// HOST BRIDGE side: the live headless presentations, five words each (address, serial, name
/// offset, gates: bit 0 the draw gate is parked, bit 1 the update gate; then the screen id the
/// guest loaded it for plus one, 0 for none), the files NUL-separated into `names`. Returns how
/// many there are, which may be more than fitted.
uint32_t ToldHeadlessList(uint32_t* words, uint32_t word_capacity, char* names, uint32_t name_capacity);

/// HOST BRIDGE side: element `id`'s parent id (0 the root) and name hash; false when unknown.
bool ToldHeadlessElement(uint32_t presentation, uint32_t serial, uint32_t id, uint32_t* parent_id,
                         uint32_t* name_hash);

}  // namespace refii::vfs
