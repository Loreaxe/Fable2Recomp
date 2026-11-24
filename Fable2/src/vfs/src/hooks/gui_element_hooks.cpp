// THE STATED ELEMENTS, Fable 2's answers (core/headless_presentation.h). Every presentation is a
// zeroed block with three vtables (gui_headless_presentation.cpp) and every element a zeroed HANDLE;
// the element service (CPresentation vf +32, vtable TU1 0x820065C0 / disc 0x820065E0, at +492), the
// animation manager (vf +36, vtable TU1 0x820030A8 / disc 0x820030C8, at +568; its vf +4 answers
// the element itself, so the guest's timeline record is element +48..+64) and the presentation's
// event push and registrations are answered here from the host's record of each handle. Each slot
// is a full replacement for a handle; any other object (none is expected — every presentation is
// stated) runs the guest's own function, and is logged.
//
// Service slots (r3 the service):
//   +12 GetAttribute   TU1 `sub_82BB52E0` / disc `sub_82BAC918`: (element, id, out) -> found, *out the value
//   +16 GetRecord      TU1 `sub_82BB5270` / disc `sub_82BAC8A8`: (element, &word, out): id = word >> 5;
//                      *out the value, *word the record word
//   +72 Store          TU1 `sub_82284798` / disc `sub_82284300`: (element, id, value) — writes only an
//                      attribute the element carries (`if (result && ...)` 0x822847dc)
//   +76 TestFlags      TU1 `sub_821D2EB0` / disc `sub_821D2C58`: `lhz r11,0x1C(r4); and r10,r11,r5` -> != 0
//   +80 Flags          TU1 `sub_82284BE8` / disc `sub_82284750`: (element, mask, on)
//   +84 ChildByName    TU1 `sub_82BB5658` / disc `sub_82BACC90`: (hash, parent or 0 = the root level)
//   +88 Parent         TU1 `sub_82284358` / disc 0x82283EC8: `lwz r3,0xC(r4)`
//   +92 Children       TU1 `sub_82BB56A8` / disc `sub_82BACCE0`: (parent, array, all) — each child the
//                      filter vf +4 (`sub_822846A0`: `lhz r11,0x1C(r4); extrwi r3,r11,1,19`, flag 0x1000)
//                      passes, or every child with `all`, pushed by the array's own push (TU1
//                      `sub_82284CA8` 0x82bb5708, disc `sub_82284810`)
// Animation slots (r3 the manager):
//   +8  GotoSlide      TU1 `sub_82BB5760` / disc `sub_82BAF540`: (element, slide, notify)
//   +32 CurrentSlide   TU1 `sub_82BB5990` / disc `sub_82BAF770`: element +43
//   +40 Advance        TU1 `sub_82284D50` / disc `sub_822848B8`: (element, clock) -> the time after the
//                      timeline's advance (`sub_82284DF8`); its end edge on an element with flag 0x4000
//                      pushes an event through pres+0x1F0 vf +28 into the queue nothing drains (S3)
//   +44 SetTime        TU1 `sub_82BB5B60` / disc `sub_82BAF940`: (element, time)
//   +60 SetPaused      TU1 `sub_82BB5CB8` / disc `sub_82BAFA98`: (element, paused)
// The direct readers the facade and SetActive call: the record lookup TU1 `sub_822848E0` / disc
// `sub_82284448` (element, id) -> the record's address; the typed read `sub_82284948` / disc
// `sub_822844B0` (element, id, out); the compounded opacity `sub_82284368` / disc `sub_82283ED0`.
// The presentation's vf +24 (TU1 `sub_82BB0648` / disc `sub_82BA9F00`) pushes a 20-byte event
// record into its queue at +0x144 and answers the record (0x82bb06a0..0x82bb06c8); nothing drains
// that queue since the per-frame update stopped, so the record is written to a host-owned one.
//
// STATEMENTS, as the guest made them: every store (kind 3), every flags write of `active` 0x2000
// (kind 4), every GotoSlide the caller notifies (kind 1; the facade's GoToSlide passes 1), every
// SetPaused (kind 2). Texts and textures are stated at their writers' entries
// (gui_headless_presentation.cpp).

#include <atomic>
#include <cstdint>
#include <cstring>
#include <vector>

#include "vfs_hooks_private.h"
#include "core/guest_build.h"
#include "core/headless_presentation.h"
#include "gui/gui_told_statements.h"
#include "hooks/frontend_stated.h"
#include "hooks/ppc_externs.h"

namespace {

using refii::headless::Answer;
using refii::vfs::LoadGuestU32;
using refii::vfs::StoreGuestU32;

constexpr uint32_t kServiceInPresentation = 492u;  // vf +32 `addi r3,r3,0x1EC`
constexpr uint16_t kFlagActive = 0x2000u;
constexpr uint16_t kFlagRegistered = 0x80u;       // vf +108's `vf+80(element, 0x80, 1)` (0x82bb0000)
constexpr uint32_t kQueueRecordBytes = 20u;

constexpr refii::guest::Addr kArrayPush{0x82284CA8u};      // (array, &element)
constexpr refii::guest::Addr kAttrOpacity{0x834BE518u};   // opacity's attribute id

bool Milestone(uint64_t n) { return n <= 16u || (n & (n - 1u)) == 0u; }

/// A slot reached with an object that is not a handle: the guest's own function runs.
void NoteForeign(const char* slot, uint32_t element) {
    static std::atomic<uint64_t> seen{0};
    const uint64_t n = ++seen;
    if (Milestone(n)) {
        REXLOG_WARN("Refii stated elements: {} reached with 0x{:08X}, not a handle — the guest's own function ran (#{})",
                    slot, element, n);
    }
}

}  // namespace

namespace refii::vfs {
const refii::headless::GamefaceTable& Fable2Gameface();
}  // namespace refii::vfs

// --- the element service -----------------------------------------------------------------------

REX_EXTERN(rex_lhGuiAttributeService_GetAttribute) {
    REX_FUNC_PROLOGUE();
    uint32_t word = 0;
    uint32_t value = 0;
    const Answer a = refii::headless::StatedRecord(refii::vfs::Fable2Gameface(), ctx, base, ctx.r4.u32, ctx.r5.u32,
                                                   &word, &value);
    if (a == Answer::kNotHandle) {
        NoteForeign("service +12", ctx.r4.u32);
        __imp__rex_lhGuiAttributeService_GetAttribute(ctx, base);
        return;
    }
    if (a == Answer::kYes) {
        StoreGuestU32(base, ctx.r6.u32, value);
    }
    ctx.r3.u32 = a == Answer::kYes ? 1u : 0u;
}

REX_EXTERN(rex_lhGuiAttributeService_GetAttributeRecord) {
    REX_FUNC_PROLOGUE();
    const uint32_t at_word = ctx.r5.u32;
    uint32_t word = 0;
    uint32_t value = 0;
    const Answer a = refii::headless::StatedRecord(refii::vfs::Fable2Gameface(), ctx, base, ctx.r4.u32,
                                                   LoadGuestU32(base, at_word) >> 5, &word, &value);
    if (a == Answer::kNotHandle) {
        NoteForeign("service +16", ctx.r4.u32);
        __imp__rex_lhGuiAttributeService_GetAttributeRecord(ctx, base);
        return;
    }
    if (a == Answer::kYes) {
        StoreGuestU32(base, ctx.r6.u32, value);
        StoreGuestU32(base, at_word, word);
    }
    ctx.r3.u32 = a == Answer::kYes ? 1u : 0u;
}

REX_EXTERN(rex_lhGuiAttributeService_SetElementAttribute) {
    REX_FUNC_PROLOGUE();
    const uint32_t element = ctx.r4.u32;
    const uint32_t id = ctx.r5.u32;
    const uint32_t value = ctx.r6.u32;
    if (refii::headless::StatedStore(element, id, value) == Answer::kNotHandle) {
        NoteForeign("service +72", element);
        __imp__rex_lhGuiAttributeService_SetElementAttribute(ctx, base);
        return;
    }
    refii::headless::TellAttribute(element, id, value);
    ctx.r3.u32 = 0u;
}

REX_EXTERN(rex_lhGuiAttributeService_TestFlags) {
    REX_FUNC_PROLOGUE();
    uint16_t flags = 0;
    if (refii::headless::StatedFlags(ctx.r4.u32, &flags) == Answer::kNotHandle) {
        NoteForeign("service +76", ctx.r4.u32);
        __imp__rex_lhGuiAttributeService_TestFlags(ctx, base);
        return;
    }
    ctx.r3.u32 = (flags & (ctx.r5.u32 & 0xFFFFu)) != 0u ? 1u : 0u;
}

REX_EXTERN(rex_lhGuiAttributeService_SetElementFlags) {
    REX_FUNC_PROLOGUE();
    const uint32_t element = ctx.r4.u32;
    const uint16_t mask = static_cast<uint16_t>(ctx.r5.u32 & 0xFFFFu);
    const bool on = (ctx.r6.u32 & 0xFFu) != 0u;
    if (refii::headless::StatedFlagsWrite(element, mask, on) == Answer::kNotHandle) {
        NoteForeign("service +80", element);
        __imp__rex_lhGuiAttributeService_SetElementFlags(ctx, base);
        return;
    }
    if (mask == kFlagActive) {
        refii::headless::TellActive(element, on);
    }
    ctx.r3.u32 = 0u;
}

REX_EXTERN(rex_lhGuiAttributeService_ChildByName) {
    REX_FUNC_PROLOGUE();
    const uint32_t presentation = ctx.r3.u32 - kServiceInPresentation;
    const uint32_t parent = ctx.r5.u32;
    if (parent != 0u && refii::headless::HandlePresentation(parent) == 0u) {
        NoteForeign("service +84", parent);
        __imp__rex_lhGuiAttributeService_ChildByName(ctx, base);
        return;
    }
    ctx.r3.u32 = refii::headless::StatedChildNamed(refii::vfs::Fable2Gameface(), ctx, base, presentation, ctx.r4.u32,
                                                   parent);
}

REX_EXTERN(rex_lhGuiAttributeService_Parent) {
    REX_FUNC_PROLOGUE();
    uint32_t parent = 0;
    if (refii::headless::StatedParent(ctx.r4.u32, &parent) == Answer::kNotHandle) {
        NoteForeign("service +88", ctx.r4.u32);
        __imp__rex_lhGuiAttributeService_Parent(ctx, base);
        return;
    }
    ctx.r3.u32 = parent;
}

REX_EXTERN(rex_lhGuiAttributeService_CollectChildren) {
    REX_FUNC_PROLOGUE();
    const uint32_t parent = ctx.r4.u32;
    const uint32_t array = ctx.r5.u32;
    const bool all = (ctx.r6.u32 & 0xFFu) != 0u;
    std::vector<uint32_t> children;
    if (refii::headless::StatedChildren(refii::vfs::Fable2Gameface(), ctx, base, parent, all, &children) ==
        Answer::kNotHandle) {
        NoteForeign("service +92", parent);
        __imp__rex_lhGuiAttributeService_CollectChildren(ctx, base);
        return;
    }
    PPCFunc* push = refii::vfs::FindGuestFunction(kArrayPush);
    const uint32_t frame = (ctx.r1.u32 - 0x80u) & ~0xFu;
    const uint32_t cell = frame + 0x50u;
    for (const uint32_t child : children) {
        if (push == nullptr) {
            break;
        }
        StoreGuestU32(base, cell, child);
        PPCContext call = ctx;
        call.r1.u32 = frame;
        call.r3.u32 = array;
        call.r4.u32 = cell;
        call.ctr.u32 = kArrayPush;
        push(call, base);
    }
    ctx.r3.u32 = 0u;
}

// --- the animation manager -----------------------------------------------------------------------

REX_EXTERN(rex_lhGuiAnimation_GotoSlide) {
    REX_FUNC_PROLOGUE();
    const uint32_t element = ctx.r4.u32;
    const int32_t slide = static_cast<int32_t>(ctx.r5.u32);
    const bool notify = (ctx.r6.u32 & 0xFFu) != 0u;
    const Answer a = refii::headless::StatedGotoSlide(element, slide);
    if (a == Answer::kNotHandle) {
        NoteForeign("animation +8", element);
        __imp__rex_lhGuiAnimation_GotoSlide(ctx, base);
        return;
    }
    if (a == Answer::kNo) {
        static std::atomic<uint64_t> outside{0};
        const uint64_t n = ++outside;
        if (Milestone(n)) {
            REXLOG_INFO("Refii stated elements: GotoSlide: Index out of range — slide {} on 0x{:08X} (#{})", slide,
                        element, n);
        }
    }
    if (notify) {
        refii::vfs::FrontEndGotoSlide(static_cast<uint32_t>(slide));
        refii::headless::TellGotoSlide(element, static_cast<uint32_t>(slide));
    }
}

REX_EXTERN(rex_lhGuiAnimation_CurrentSlide) {
    REX_FUNC_PROLOGUE();
    uint8_t slide = 0;
    if (refii::headless::StatedCurrentSlide(ctx.r4.u32, &slide) == Answer::kNotHandle) {
        NoteForeign("animation +32", ctx.r4.u32);
        __imp__rex_lhGuiAnimation_CurrentSlide(ctx, base);
        return;
    }
    ctx.r3.u32 = slide;
}

REX_EXTERN(rex_lhGuiAnimation_Advance) {
    REX_FUNC_PROLOGUE();
    const uint32_t element = ctx.r4.u32;
    int32_t time = 0;
    bool ended = false;
    if (refii::headless::StatedAdvance(element, static_cast<int32_t>(ctx.r5.u32), &time, &ended) ==
        Answer::kNotHandle) {
        NoteForeign("animation +40", element);
        __imp__rex_lhGuiAnimation_Advance(ctx, base);
        return;
    }
    uint16_t flags = 0;
    if (ended && refii::headless::StatedFlags(element, &flags) == Answer::kYes && (flags & 0x4000u) != 0u) {
        REXLOG_INFO("Refii stated elements: 0x{:08X}'s timeline ended with flag 0x4000 — its event goes to the "
                    "queue nothing drains",
                    element);
    }
    ctx.r3.u32 = static_cast<uint32_t>(time);
}

REX_EXTERN(rex_lhGuiAnimation_SetTime) {
    REX_FUNC_PROLOGUE();
    const uint32_t element = ctx.r4.u32;
    if (refii::headless::StatedSetTime(element, static_cast<int32_t>(ctx.r5.u32)) == Answer::kNotHandle) {
        NoteForeign("animation +44", element);
        __imp__rex_lhGuiAnimation_SetTime(ctx, base);
        return;
    }
    ctx.r3.u32 = element;
}

REX_EXTERN(rex_lhGfxTimeline_SetPaused) {
    REX_FUNC_PROLOGUE();
    const uint32_t element = ctx.r4.u32;
    const bool paused = (ctx.r5.u32 & 0xFFu) != 0u;
    if (refii::headless::StatedSetPaused(element, paused) == Answer::kNotHandle) {
        NoteForeign("animation +60", element);
        __imp__rex_lhGfxTimeline_SetPaused(ctx, base);
        return;
    }
    refii::headless::TellPaused(element, paused);
    ctx.r3.u32 = element;
}

// --- the direct readers --------------------------------------------------------------------------

REX_EXTERN(rex_lhGuiElement_FindAttribute) {
    REX_FUNC_PROLOGUE();
    uint32_t word = 0;
    uint32_t value = 0;
    const Answer a = refii::headless::StatedRecord(refii::vfs::Fable2Gameface(), ctx, base, ctx.r3.u32, ctx.r4.u32,
                                                   &word, &value);
    if (a == Answer::kNotHandle) {
        __imp__rex_lhGuiElement_FindAttribute(ctx, base);
        return;
    }
    ctx.r3.u32 = a == Answer::kYes ? refii::headless::RecordScratch(refii::vfs::Fable2Gameface(), ctx, base, word, value)
                                   : 0u;
}

REX_EXTERN(rex_lhGuiElement_ReadAttribute) {
    REX_FUNC_PROLOGUE();
    uint32_t word = 0;
    uint32_t value = 0;
    const Answer a = refii::headless::StatedRecord(refii::vfs::Fable2Gameface(), ctx, base, ctx.r3.u32, ctx.r4.u32,
                                                   &word, &value);
    if (a == Answer::kNotHandle) {
        __imp__rex_lhGuiElement_ReadAttribute(ctx, base);
        return;
    }
    if (a == Answer::kYes) {
        StoreGuestU32(base, ctx.r5.u32, value);
    }
    ctx.r3.u32 = a == Answer::kYes ? 1u : 0u;
}

// `sub_82284368(element, presentation)`: 1.0, times each opacity up the parent chain over 100
// (`fmuls; fmuls` by 0.0099999998, 0x822843f0), 0.0 without an element or a presentation.
REX_EXTERN(rex_lhGuiElement_CompoundedOpacity) {
    REX_FUNC_PROLOGUE();
    const uint32_t element = ctx.r3.u32;
    if (element != 0u && ctx.r4.u32 != 0u && refii::headless::HandlePresentation(element) == 0u) {
        NoteForeign("compounded opacity", element);
        __imp__rex_lhGuiElement_CompoundedOpacity(ctx, base);
        return;
    }
    if (element == 0u || ctx.r4.u32 == 0u) {
        ctx.f1.f64 = 0.0;
        return;
    }
    const uint32_t opacity = LoadGuestU32(base, kAttrOpacity);
    float compounded = 1.0f;
    uint32_t at = element;
    while (at != 0u) {
        uint32_t word = 0;
        uint32_t value = 0;
        if (refii::headless::StatedRecord(refii::vfs::Fable2Gameface(), ctx, base, at, opacity, &word, &value) ==
            Answer::kYes) {
            float v = 0.0f;
            std::memcpy(&v, &value, sizeof v);
            compounded = (v * compounded) * 0.0099999998f;
        }
        uint32_t parent = 0;
        if (refii::headless::StatedParent(at, &parent) != Answer::kYes) {
            break;
        }
        at = parent;
    }
    ctx.f1.f64 = compounded;
}

// --- the presentation's event push ---------------------------------------------------------------

// vf +24 (r3 presentation, r4 the event hash, r5 the element, r6 / r7 pointers to two argument words
// or 0, r8 / r9 two bytes): the record the original fills (0x82bb0654..0x82bb06c8) — words r5, r4,
// *r6, *r7, then r8, r9 and 0xC0 in the last word's bytes — written to a host-owned guest record,
// which is the answer.
REX_EXTERN(rex_CPresentation_QueueEvent) {
    REX_FUNC_PROLOGUE();
    static std::atomic<uint32_t> record{0u};
    uint32_t at = record.load();
    if (at == 0u) {
        const uint32_t made = refii::vfs::CallLhMalloc(ctx, base, kQueueRecordBytes);
        uint32_t none = 0u;
        if (made != 0u && !record.compare_exchange_strong(none, made)) {
            refii::vfs::CallLhFree(ctx, base, made);
        }
        at = record.load();
    }
    if (at != 0u) {
        StoreGuestU32(base, at + 0u, ctx.r5.u32);
        StoreGuestU32(base, at + 4u, ctx.r4.u32);
        StoreGuestU32(base, at + 8u, ctx.r6.u32 != 0u ? LoadGuestU32(base, ctx.r6.u32) : 0u);
        StoreGuestU32(base, at + 12u, ctx.r7.u32 != 0u ? LoadGuestU32(base, ctx.r7.u32) : 0u);
        StoreGuestU32(base, at + 16u, ((ctx.r8.u32 & 0xFFu) << 24) | ((ctx.r9.u32 & 0xFFu) << 16) | 0xC000u);
    }
    static std::atomic<uint64_t> pushed{0};
    const uint64_t n = ++pushed;
    if (Milestone(n)) {
        REXLOG_INFO("Refii stated elements: event 0x{:08X} pushed on 0x{:08X} for element 0x{:08X} — nothing drains "
                    "the queue (#{})",
                    ctx.r4.u32, ctx.r3.u32, ctx.r5.u32, n);
    }
    ctx.r3.u32 = at;
}

namespace refii::vfs {

/// vf +108's flag write on the registered element (`vf+80(element, 0x80, 1)`, TU1 0x82bb0000).
void StatedRegistrationFlag(uint32_t element) {
    refii::headless::StatedFlagsWrite(element, kFlagRegistered, true);
}

}  // namespace refii::vfs
