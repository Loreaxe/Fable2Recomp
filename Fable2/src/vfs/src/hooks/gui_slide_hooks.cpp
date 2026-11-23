// Every slide a presentation is TOLD to play, stated to the host.
//
// Every explicit slide change in the Gameface runtime — native screen code (`sub_82426BA8`,
// `sub_82426CA8`), the GUI registries, Lua `goToSlide`, a file's Logic commands, the page
// arrows, a completed go-next slide — reaches one call on the presentation's time interface
// (`pres + 0x238`, `sub_82173E98`; TU1 addresses, disc 0x82BAF540 / 0x82BAFA98):
//
//   sub_82BB5760(timeline, element, int slide, bool stated) — GotoSlide. `stated` is 1 for every
//       caller but the element walk's own entry when a component comes live (`sub_822849A0`
//       0x82284b38 passes 0), which the host's playback reproduces itself.
//   sub_82BB5CB8(timeline, element, bool paused) — SetPaused: `sub_82426CA8`'s play
//       (0x82426cf8), COMMAND_PLAY / COMMAND_PAUSE. The slide entry's own record-pause write
//       (`sub_82BB6408` 0x82bb64f0) happens INSIDE GotoSlide and is not posted.
//
// The other statements an element is told go through the presentation's element store
// (`pres + 0x1EC`, `sub_82284360`, its vtable +72 / +80; disc 0x82284300 / 0x82284750, tracks
// 0x82284218):
//
//   sub_82284798(store, element, attrId, value) — SetAttribute, change-only (`result[1] != a4`):
//       Lua `setAttribute` (`AKElement_setAttribute` 0x82bbb998), `COMMAND_SETPROPERTY`, native
//       writers. Also every active track every frame (`sub_822846B0`) and every slide command
//       inside GotoSlide; both are the host's own playback and are not posted.
//   sub_82284BE8(store, element, mask, on) — the flags writer. Mask 0x2000 is `active` (Lua
//       `setAttribute("active", b)` 0x82bbb774); the walk's 0x1000 / slide-entry writes are not
//       statements.
//
// GotoSlide and the two element-store writers are hooked in gui_element_hooks.cpp (the
// element change feed) and post through the Tell* functions here; SetPaused and the track
// evaluator are hooked here. Pushed after the original through the C ABI (`refii_bridge_told_push`)
// for the host to take (`RefiiHostTakeToldStatements`), the ordinal being the element's index in the
// presentation's depth-first walk (child +20, sibling +16, parent +12 — the order the host
// bridge's element sampler and every `.bgf` ordinal use). The root's name hash lets the host
// drop a statement made to a presentation that has since been rebuilt at the same address.
// Every entry is posted, a restatement included: GotoSlide has no same-slide guard
// (`sub_823FE848` calls `sub_82426CA8` unconditionally), so a restatement re-enters the slide
// at t = 0 in the guest as well.
//
// An element of a HEADLESS presentation (gui_headless_presentation.cpp) has no file tree to walk:
// it is posted under the id its creation stated, `root_hash` the presentation's serial and the
// ordinal `kHeadlessOrdinal | id`; the host resolves the id's name path in the file. Its texts are
// stated at the writers' entries there, since a headless element has no text record (+4).
#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <mutex>
#include <unordered_map>
#include <utility>
#include <vector>

#include "vfs_hooks_private.h"
#include "core/guest_build.h"
#include "core/guest_state.h"
#include "core/payload_bridge_fable2.h"
#include "gui/gui_told_statements.h"
#include "hooks/frontend_stated.h"
#include "hooks/ppc_externs.h"

namespace {

bool Plausible(uint32_t value) { return value >= 0x10000u && value < 0xC0000000u; }

constexpr uint32_t kTimelineInPresentation = 0x238u;
constexpr uint32_t kRootInPresentation = 492u + 8u;

/// Element → ordinal, open addressing over one array that is kept between rebuilds. A guest
/// building a screen sets flags on elements it has not linked yet, and each such miss rebuilds the
/// map: as a node-per-entry std::unordered_map that rebuild was mostly malloc and free (about 1.2 s
/// of a OnePlus 7 Pro's 3.3 s between leaving the front end and the region build).
class OrdinalTable {
public:
    void clear() {
        std::fill(slots_.begin(), slots_.end(), 0u);
        count_ = 0;
    }

    void put(uint32_t element, uint32_t ordinal) {
        if ((count_ + 1u) * 2u > slots_.size()) grow();
        if (Insert(slots_, element, ordinal)) ++count_;
    }

    bool find(uint32_t element, uint32_t& ordinal) const {
        if (slots_.empty()) return false;
        const size_t mask = slots_.size() - 1u;
        for (size_t at = Mix(element) & mask;; at = (at + 1u) & mask) {
            const uint64_t slot = slots_[at];
            if (slot == 0u) return false;
            if (static_cast<uint32_t>(slot >> 32) == element) {
                ordinal = static_cast<uint32_t>(slot);
                return true;
            }
        }
    }

    template <typename Visit>
    void for_each(Visit&& visit) const {
        for (uint64_t slot : slots_) {
            if (slot != 0u) visit(static_cast<uint32_t>(slot >> 32), static_cast<uint32_t>(slot));
        }
    }

private:
    // An element is never 0 (Plausible), so a zero slot is empty.
    static uint32_t Mix(uint32_t key) {
        key ^= key >> 16;
        key *= 0x7FEB352Du;
        key ^= key >> 15;
        key *= 0x846CA68Bu;
        return key ^ (key >> 16);
    }

    /// True when the element was new, false when its ordinal was replaced.
    static bool Insert(std::vector<uint64_t>& slots, uint32_t element, uint32_t ordinal) {
        const size_t mask = slots.size() - 1u;
        const uint64_t entry = (static_cast<uint64_t>(element) << 32) | ordinal;
        for (size_t at = Mix(element) & mask;; at = (at + 1u) & mask) {
            if (slots[at] == 0u) {
                slots[at] = entry;
                return true;
            }
            if (static_cast<uint32_t>(slots[at] >> 32) == element) {
                slots[at] = entry;
                return false;
            }
        }
    }

    void grow() {
        std::vector<uint64_t> wider(slots_.empty() ? 256u : slots_.size() * 2u, 0u);
        for (uint64_t slot : slots_) {
            if (slot != 0u) Insert(wider, static_cast<uint32_t>(slot >> 32), static_cast<uint32_t>(slot));
        }
        slots_.swap(wider);
    }

    std::vector<uint64_t> slots_;
    uint32_t count_ = 0;
};

/// One presentation's element → ordinal map, rebuilt when its tree is not the one it was built from.
struct Ordinals {
    uint32_t root = 0;
    uint32_t root_hash = 0;
    OrdinalTable by_element;
};

std::mutex g_lock;
std::unordered_map<uint32_t, Ordinals> g_ordinals;

thread_local int t_inside_goto = 0;
thread_local int t_inside_tracks = 0;

constexpr uint32_t kStoreInPresentation = 0x1ECu;

void Build(uint8_t* base, uint32_t root, Ordinals& map) {
    map.root = root;
    map.root_hash = refii::vfs::LoadGuestU32(base, root);
    map.by_element.clear();
    uint32_t ordinal = 0;
    uint32_t element = root;
    while (element != 0 && ordinal < 16384u && Plausible(element)) {
        map.by_element.put(element, ordinal++);
        const uint32_t child = refii::vfs::LoadGuestU32(base, element + 20u);
        if (Plausible(child)) {
            element = child;
            continue;
        }

        int climbed = 0;
        while (element != 0) {
            if (element == root || ++climbed > 16384) {
                element = 0;
                break;
            }

            const uint32_t sibling = refii::vfs::LoadGuestU32(base, element + 16u);
            if (Plausible(sibling)) {
                element = sibling;
                break;
            }

            const uint32_t parent = refii::vfs::LoadGuestU32(base, element + 12u);
            element = Plausible(parent) ? parent : 0;
        }
    }
}

/// The element's ordinal in its presentation, or false when it is not in the tree.
bool OrdinalOf(uint8_t* base, uint32_t presentation, uint32_t element, uint32_t& root_hash,
               uint32_t& ordinal) {
    const uint32_t root = refii::vfs::LoadGuestU32(base, presentation + kRootInPresentation);
    if (!Plausible(root) || !Plausible(element)) return false;

    std::lock_guard<std::mutex> lock(g_lock);
    Ordinals& map = g_ordinals[presentation];
    const uint32_t hash = refii::vfs::LoadGuestU32(base, root);
    if (map.root != root || map.root_hash != hash || !map.by_element.find(element, ordinal)) {
        Build(base, root, map);
        if (!map.by_element.find(element, ordinal)) return false;
    }

    root_hash = map.root_hash;
    return true;
}

/// Pushes one statement for the host through the C ABI (payload_bridge.h); the queue lives in
/// refii_vfs (gui/gui_told_statements.cpp), which the bridge drains.
void Queue(uint8_t* base, refii::vfs::ToldStatement::Kind kind, uint32_t presentation, uint32_t element,
           uint32_t a, uint32_t b = 0u) {
    uint32_t headless = 0;
    uint32_t serial = 0;
    uint32_t id = 0;
    if (refii::vfs::HeadlessElementKey(element, &headless, &serial, &id)) {
        if (headless == presentation) {
            refii_bridge_told_push(static_cast<uint32_t>(kind), presentation, serial,
                                   refii::vfs::kHeadlessOrdinal | id, a, b);
        }
        return;
    }
    uint32_t root_hash = 0;
    uint32_t ordinal = 0;
    if (!OrdinalOf(base, presentation, element, root_hash, ordinal)) return;
    refii_bridge_told_push(static_cast<uint32_t>(kind), presentation, root_hash, ordinal, a, b);
}

}  // namespace

namespace refii::vfs {

void TellGotoSlideBegin() { ++t_inside_goto; }

void TellGotoSlideEnd(uint8_t* base, uint32_t timeline, uint32_t element, uint32_t slide, bool stated) {
    --t_inside_goto;
    if (stated && t_inside_goto == 0) {
        FrontEndGotoSlide(slide);
    }
    if (stated && Plausible(timeline)) {
        Queue(base, ToldStatement::Kind::kGotoSlide, timeline - kTimelineInPresentation, element, slide);
    }
}

void TellAttribute(uint8_t* base, uint32_t store, uint32_t element, uint32_t attribute, uint32_t value) {
    if (t_inside_goto == 0 && t_inside_tracks == 0 && Plausible(store)) {
        Queue(base, ToldStatement::Kind::kAttribute, store - kStoreInPresentation, element, attribute, value);
    }
}

void TellFlags(uint8_t* base, uint32_t store, uint32_t element, uint32_t mask, bool on) {
    if (mask == 0x2000u && t_inside_goto == 0 && t_inside_tracks == 0 && Plausible(store)) {
        Queue(base, ToldStatement::Kind::kActive, store - kStoreInPresentation, element, on ? 1u : 0u);
    }
}

}  // namespace refii::vfs

// SetPaused: TU1 0x82BB5CB8.
REX_EXTERN(rex_lhGfxTimeline_SetPaused) {
    const uint32_t timeline = ctx.r3.u32;
    const uint32_t element = ctx.r4.u32;
    const uint32_t paused = (ctx.r5.u32 & 0xFFu) != 0u ? 1u : 0u;
    __imp__rex_lhGfxTimeline_SetPaused(ctx, base);
    if (t_inside_goto == 0 && Plausible(timeline)) {
        Queue(base, refii::vfs::ToldStatement::Kind::kPaused, timeline - kTimelineInPresentation, element, paused);
    }
}

// Track evaluation: TU1 0x822846B0 — every active track's write is the host's own playback.
REX_EXTERN(rex_lhGfxElements_EvaluateTracks) {
    ++t_inside_tracks;
    __imp__rex_lhGfxElements_EvaluateTracks(ctx, base);
    --t_inside_tracks;
}
