// refii - THE STATED ELEMENTS, Fable 3's answers (core/headless_presentation.h). Every presentation is
// a zeroed block with three vtables (headless_presentation_hooks.cpp) and every element a zeroed
// HANDLE. Each slot or reader below that the guest reaches with a handle is a full replacement
// answered from the host's record of it; any other object runs the guest's own function and is
// logged (none is expected — every presentation is stated). TU3 addresses; the disc's rows are in
// Fable3/versions/Fable3/refiii_config.toml. Both builds' bodies are instruction-identical.
//
// The element service (CPresentation vf +32 `addi r3,r3,0x1EC`, vtable 0x82006E58), r3 the service:
//   +12 GetAttribute   `sub_82C6E8E0`: (element, id, out) -> found; FindAttr then `lwz r11,4(r3)` /
//                      `stw r11,0(r6)` — a miss leaves *out as it was
//   +16 GetRecord      `sub_82C6E880`: (element, &word, out): id = word >> 5 (`srwi r4,r11,5`); *out the
//                      value, *word the record word (`lwz r9,0(r11)` / `stw r9,0(r5)`)
//   +72 Store          `sub_821C9F98`: (element, id, value) — only an attribute the element carries
//   +76 TestFlags      `sub_821D1C78`: `lhz r11,0x1C(r4); and r10,r11,r5` -> != 0
//   +80 Flags          `sub_821C9C38`: (element, mask, on)
//   +84 ChildByName    `sub_82C6EC30`: (hash, parent or 0 = the root level)
//   +88 Parent         0x82C6EC80 `lwz r3,0xC(r4)`
//   +92 Children       `sub_82C6EC88`: (parent, collector, all) — each child whose flag 0x1000 the
//                      service's vf +4 passes, or every child with `all` (0x82C6ECC4..0x82C6ECD8),
//                      appended to the collector {+0x20 data, +0x24 capacity, +0x28 count, +0x2C growth}
//                      (0x82C6ECDC..0x82C6ED38), grown by `sub_82C61E40`(collector, (int)(cap * growth) + 1)
// The slide manager (vf +36, vtable 0x82002AC0; every slot first takes the element through its own
// vf +4 `mr r3,r4`, so the guest's bodies would work on handle memory):
//   +8  GoTo           `sub_82267158` — in gui_element_hooks.cpp beside its census
//   +44 SetTime        `sub_82C6EFE8`: (element, time) — the facade SetTime 0x8287833C
//   +60 SetPaused      `sub_82195608`: (element, paused) — GoToSlideAndRestart 0x828781E8, CAcceptHoldBox
//                      vf5 0x82C3A1F0
// The direct readers the facade starts from:
//   FindAttr           `sub_821C9F40`(element, id) -> the 8-byte record or 0 (`lbz r8,0x1F(r3)` and the
//                      walk over +0x44 / +0x20): GetOpacity 0x8287728C, LocalPosition 0x828763CC, LocalScale
//                      0x82876680, LocalPivot 0x828768B8, SetOpacity 0x8287717C, NameString 0x82877364,
//                      CumOpacity 0x82877750, AttributeString 0x8287612C, AttrBool 0x82876218. Answered with
//                      the handle's record in a scratch record, every facade reader (GetOpacity, GetPosition,
//                      the cumulative scale, pivot and opacity, the attribute strings) answers the last value
//                      the guest stored, else the file's.
//   OwnerWalk          `sub_8227E390`(service, element): the element or its nearest ancestor with flag
//                      0x0001 (+76 (e, 1) 0x8227E3B4, +88 0x8227E3E8); the guest's own has no null exit
//                      (`beq loc_8227E3D4` 0x8227E428).
// THE GLINTS. CHUDParticle's per-frame update `sub_82BCEEA0`(particle, &changed) clears the caller's
// byte (`stb r11,0(r29)` 0x82BCEED8), then makes, places, fades and kills a guest NParticleSystem for
// the element's `ParticleEffect` from its active bit, its cumulative opacity and scale.x and its render
// node's screen position (`lwz r11,4(r3)` 0x82878428, 0 on a handle). The box glints' opacity and
// scale are their tracks' (querybox `fx2`: 100 -> 0 over 440..450 ms), which the guest no longer
// plays, and the position is the world origin; no game system reads the instance. The glints are
// Godot's to draw: the update only clears the byte, so no instance is made (the create / kill
// `sub_82BCF0D8` is reached only from it, the destructor and `sub_82BCF0D0`, both with 0).

#include <atomic>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <unordered_map>
#include <vector>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "core/headless_presentation.h"
#include "gui_statements.h"
#include "headless_table.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_GfService_GetAttribute);
REX_EXTERN(__imp__rex_GfService_GetAttributeRecord);
REX_EXTERN(__imp__rex_GfService_SetElementAttribute);
REX_EXTERN(__imp__rex_GfService_TestFlags);
REX_EXTERN(__imp__rex_GfService_SetElementFlags);
REX_EXTERN(__imp__rex_GfService_ChildByName);
REX_EXTERN(__imp__rex_GfService_Parent);
REX_EXTERN(__imp__rex_GfService_CollectChildren);
REX_EXTERN(__imp__rex_GuiSlides_SetTime);
REX_EXTERN(__imp__rex_GuiSlides_SetPaused);
REX_EXTERN(__imp__rex_GuiElement_FindAttribute);
REX_EXTERN(__imp__rex_GuiElement_OwnerOf);

namespace {

using refii::headless::Answer;
using namespace refii::fable3;

constexpr uint32_t kServiceInPresentation = 492u;  // vf +32 `addi r3,r3,0x1EC`
constexpr uint32_t kCollectorData = 0x20u;
constexpr uint32_t kCollectorCapacity = 0x24u;
constexpr uint32_t kCollectorCount = 0x28u;
constexpr uint32_t kCollectorGrowth = 0x2Cu;
const refii::guest::Addr kCollectorReserve{0x82C61E40u};  // (collector, capacity)

void StoreWord(uint8_t* base, uint32_t at, uint32_t value) {
  const uint32_t v = __builtin_bswap32(value);
  std::memcpy(base + at, &v, sizeof v);
}

bool Milestone(uint64_t n) { return n <= 16u || (n & (n - 1u)) == 0u; }

/// A slot reached with an object that is not a handle: the guest's own function runs.
void NoteForeign(const char* slot, uint32_t element) {
  static std::atomic<uint64_t> seen{0};
  const uint64_t n = ++seen;
  if (Milestone(n)) {
    REXLOG_WARN("Refii stated elements (Fable 3): {} reached with 0x{:08X}, not a handle — the guest's own "
                "function ran (#{})",
                slot, element, n);
  }
}

PPCFunc* Resolved(uint32_t address) {
  static std::mutex m;
  static std::unordered_map<uint32_t, PPCFunc*> cache;
  std::lock_guard<std::mutex> lock(m);
  const auto it = cache.find(address);
  if (it != cache.end()) {
    return it->second;
  }
  PPCFunc* fn = nullptr;
  for (PPCFuncMapping* mapping = PPCFuncMappings; mapping != nullptr && mapping->host != nullptr; ++mapping) {
    if (static_cast<uint32_t>(mapping->guest) == address) {
      fn = mapping->host;
      break;
    }
  }
  cache.emplace(address, fn);
  if (fn == nullptr) {
    REXLOG_ERROR("Refii stated elements (Fable 3): guest function 0x{:08X} is not a recompiled entry", address);
  }
  return fn;
}

}  // namespace

// --- the element service -----------------------------------------------------------------------

REX_EXTERN(rex_GfService_GetAttribute) {
  uint32_t word = 0;
  uint32_t value = 0;
  const Answer a = refii::headless::StatedRecord(Gameface(), ctx, base, ctx.r4.u32, ctx.r5.u32, &word, &value);
  if (a == Answer::kNotHandle) {
    NoteForeign("service +12", ctx.r4.u32);
    __imp__rex_GfService_GetAttribute(ctx, base);
    return;
  }
  if (a == Answer::kYes) {
    StoreWord(base, ctx.r6.u32, value);
  }
  ctx.r3.u32 = a == Answer::kYes ? 1u : 0u;
}

REX_EXTERN(rex_GfService_GetAttributeRecord) {
  const uint32_t at_word = ctx.r5.u32;
  uint32_t word = 0;
  uint32_t value = 0;
  const Answer a = refii::headless::StatedRecord(Gameface(), ctx, base, ctx.r4.u32, LoadGuestU32(base, at_word) >> 5,
                                                 &word, &value);
  if (a == Answer::kNotHandle) {
    NoteForeign("service +16", ctx.r4.u32);
    __imp__rex_GfService_GetAttributeRecord(ctx, base);
    return;
  }
  if (a == Answer::kYes) {
    StoreWord(base, ctx.r6.u32, value);
    StoreWord(base, at_word, word);
  }
  ctx.r3.u32 = a == Answer::kYes ? 1u : 0u;
}

REX_EXTERN(rex_GfService_SetElementAttribute) {
  if (refii::headless::StatedStore(ctx.r4.u32, ctx.r5.u32, ctx.r6.u32) == Answer::kNotHandle) {
    NoteForeign("service +72", ctx.r4.u32);
    __imp__rex_GfService_SetElementAttribute(ctx, base);
    return;
  }
  ctx.r3.u32 = 0u;
}

REX_EXTERN(rex_GfService_TestFlags) {
  uint16_t flags = 0;
  if (refii::headless::StatedFlags(ctx.r4.u32, &flags) == Answer::kNotHandle) {
    NoteForeign("service +76", ctx.r4.u32);
    __imp__rex_GfService_TestFlags(ctx, base);
    return;
  }
  ctx.r3.u32 = (flags & (ctx.r5.u32 & 0xFFFFu)) != 0u ? 1u : 0u;
}

REX_EXTERN(rex_GfService_SetElementFlags) {
  const uint16_t mask = static_cast<uint16_t>(ctx.r5.u32 & 0xFFFFu);
  const bool on = (ctx.r6.u32 & 0xFFu) != 0u;
  if (refii::headless::StatedFlagsWrite(ctx.r4.u32, mask, on) == Answer::kNotHandle) {
    NoteForeign("service +80", ctx.r4.u32);
    __imp__rex_GfService_SetElementFlags(ctx, base);
    return;
  }
  ctx.r3.u32 = 0u;
}

REX_EXTERN(rex_GfService_ChildByName) {
  const uint32_t presentation = ctx.r3.u32 - kServiceInPresentation;
  const uint32_t parent = ctx.r5.u32;
  if ((parent != 0u && refii::headless::HandlePresentation(parent) == 0u) ||
      (parent == 0u && !refii::headless::IsHeadless(presentation))) {
    NoteForeign("service +84", parent);
    __imp__rex_GfService_ChildByName(ctx, base);
    return;
  }
  ctx.r3.u32 = refii::headless::StatedChildNamed(Gameface(), ctx, base, presentation, ctx.r4.u32, parent);
}

REX_EXTERN(rex_GfService_Parent) {
  uint32_t parent = 0;
  if (refii::headless::StatedParent(ctx.r4.u32, &parent) == Answer::kNotHandle) {
    NoteForeign("service +88", ctx.r4.u32);
    __imp__rex_GfService_Parent(ctx, base);
    return;
  }
  ctx.r3.u32 = parent;
}

REX_EXTERN(rex_GfService_CollectChildren) {
  const uint32_t parent = ctx.r4.u32;
  const uint32_t collector = ctx.r5.u32;
  const bool all = (ctx.r6.u32 & 0xFFu) != 0u;
  std::vector<uint32_t> children;
  if (refii::headless::StatedChildren(Gameface(), ctx, base, parent, all, &children) == Answer::kNotHandle) {
    NoteForeign("service +92", parent);
    __imp__rex_GfService_CollectChildren(ctx, base);
    return;
  }
  PPCFunc* reserve = Resolved(kCollectorReserve);
  for (const uint32_t child : children) {
    const uint32_t count = LoadGuestU32(base, collector + kCollectorCount);
    const uint32_t capacity = LoadGuestU32(base, collector + kCollectorCapacity);
    if (count == capacity) {
      if (reserve == nullptr) {
        break;
      }
      // `extsw; fcfid; frsp; fmuls; fctiwz` then `addi r4,r11,1` (0x82C6ECEC..0x82C6ED18).
      const float grown = static_cast<float>(static_cast<int32_t>(capacity)) * LoadGuestF32(base, collector + kCollectorGrowth);
      PPCContext call = ctx;
      call.r3.u32 = collector;
      call.r4.u32 = static_cast<uint32_t>(static_cast<int32_t>(grown) + 1);
      call.ctr.u32 = kCollectorReserve;
      reserve(call, base);
    }
    const uint32_t data = LoadGuestU32(base, collector + kCollectorData);
    const uint32_t at = LoadGuestU32(base, collector + kCollectorCount);
    StoreWord(base, data + at * 4u, child);
    StoreWord(base, collector + kCollectorCount, at + 1u);
  }
}

// --- the slide manager ---------------------------------------------------------------------------

REX_EXTERN(rex_GuiSlides_SetTime) {
  const uint32_t element = ctx.r4.u32;
  if (refii::headless::StatedSetTime(element, static_cast<int32_t>(ctx.r5.u32)) == Answer::kNotHandle) {
    NoteForeign("slides +44", element);
    __imp__rex_GuiSlides_SetTime(ctx, base);
  }
}

REX_EXTERN(rex_GuiSlides_SetPaused) {
  const uint32_t element = ctx.r4.u32;
  if (refii::headless::StatedSetPaused(element, (ctx.r5.u32 & 0xFFu) != 0u) == Answer::kNotHandle) {
    NoteForeign("slides +60", element);
    __imp__rex_GuiSlides_SetPaused(ctx, base);
  }
}

// --- the direct readers --------------------------------------------------------------------------

REX_EXTERN(rex_GuiElement_FindAttribute) {
  uint32_t word = 0;
  uint32_t value = 0;
  const Answer a = refii::headless::StatedRecord(Gameface(), ctx, base, ctx.r3.u32, ctx.r4.u32, &word, &value);
  if (a == Answer::kNotHandle) {
    if (ctx.r3.u32 != 0u) {
      NoteForeign("FindAttr", ctx.r3.u32);
    }
    __imp__rex_GuiElement_FindAttribute(ctx, base);
    return;
  }
  ctx.r3.u32 = a == Answer::kYes ? refii::headless::RecordScratch(Gameface(), ctx, base, word, value) : 0u;
}

REX_EXTERN(rex_GuiElement_OwnerOf) {
  uint32_t owner = 0;
  const Answer a = refii::headless::StatedOwner(ctx.r4.u32, &owner);
  if (a == Answer::kNotHandle) {
    NoteForeign("the owner walk", ctx.r4.u32);
    __imp__rex_GuiElement_OwnerOf(ctx, base);
    return;
  }
  ctx.r3.u32 = a == Answer::kYes ? owner : 0u;
}

// --- the glints ----------------------------------------------------------------------------------

REX_EXTERN(rex_HudParticle_Update) {
  if (base != nullptr && Plausible(ctx.r4.u32)) {
    base[ctx.r4.u32] = 0u;
  }
}
