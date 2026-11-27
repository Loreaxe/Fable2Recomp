// refii — the targeting highlight colour the guest states, as SHARED STATE.
//
// THIS FILE MUST BE IN librefii_vfs, NOT IN THE PPC HOOK SET, for the same reason as
// stated_fx_state.cpp beside it: the PAYLOAD writes (the setter hooks in
// Fable2/src/vfs/src/hooks/target_highlight.cpp, on the guest's thread) and the HOST reads (the entity walk in
// the host bridge, on Godot's). Both link refii_vfs; the payload does not export anything the
// host may look up, and the host may not link the payload at all.
//
// The host reaches these as ordinary C++ symbols; the PAYLOAD reaches them through the C entries
// in core/payload_bridge.h. See that header for the contract.

#include <array>
#include <atomic>
#include <cstdint>
#include <mutex>

#include "core/payload_bridge_fable2.h"

namespace refii::vfs {
namespace {

struct StatedHighlight {
  std::atomic<uint32_t> component{0};
  std::atomic<uint32_t> rgba{0x000000FFu};
  std::atomic<uint32_t> sequence{0};
};

/// Sized well above the number of things that can be targeted at once; open addressed.
constexpr uint32_t kSlots = 256u;

StatedHighlight g_highlights[kSlots];
std::atomic<uint32_t> g_sequence{0};
std::atomic<uint64_t> g_colour_calls{0};
std::atomic<uint64_t> g_target_calls{0};

uint32_t SlotFor(uint32_t component) {
  const uint32_t h = component * 2654435761u;
  return (h >> 12) & (kSlots - 1u);
}

StatedHighlight* Claim(uint32_t component) {
  if (component == 0u) {
    return nullptr;
  }

  const uint32_t start = SlotFor(component);
  for (uint32_t probe = 0u; probe < 12u; ++probe) {
    StatedHighlight& slot = g_highlights[(start + probe) & (kSlots - 1u)];
    uint32_t held = slot.component.load(std::memory_order_relaxed);
    if (held == component) {
      return &slot;
    }

    if (held == 0u &&
        slot.component.compare_exchange_strong(held, component, std::memory_order_relaxed)) {
      return &slot;
    }
  }

  return nullptr;
}

}  // namespace

/// The colour the guest last stated for one renderable component, or 0 when it never stated one.
uint32_t StatedHighlightFor(uint32_t component) {
  if (component == 0u) {
    return 0u;
  }

  const uint32_t start = SlotFor(component);
  for (uint32_t probe = 0u; probe < 12u; ++probe) {
    StatedHighlight& slot = g_highlights[(start + probe) & (kSlots - 1u)];
    const uint32_t held = slot.component.load(std::memory_order_acquire);
    if (held == component) {
      return slot.rgba.load(std::memory_order_relaxed);
    }

    // `Claim` takes the first empty slot and none is ever given back: nothing lies past one.
    if (held == 0u) {
      return 0u;
    }
  }

  return 0u;
}

namespace {

/// WHO TARGETS WHAT: one row per thing some player targets, as the setter left its two bytes.
/// Rows go when the setter leaves neither set; a thing destroyed while targeted keeps its row
/// until the table needs the room, which is harmless — the highlight and the "Interact" icon are
/// raised only through the setter, so a reused address is restated before either shows.
struct TargetedRow {
  uint32_t thing = 0u;
  uint8_t mask = 0u;
  bool interact = false;
};

constexpr int32_t kTargetedRows = 64;
std::mutex g_targeted_mutex;
std::array<TargetedRow, kTargetedRows> g_targeted{};
int32_t g_targeted_count = 0;

}  // namespace
}  // namespace refii::vfs

// --- The payload's side. C only; see core/payload_bridge.h. --------------------------------

extern "C" {
void refii_bridge_note_highlight_colour(uint32_t component, uint32_t rgba) {
  refii::vfs::g_colour_calls.fetch_add(1u, std::memory_order_relaxed);

  if (refii::vfs::StatedHighlight* slot = refii::vfs::Claim(component); slot != nullptr) {
    slot->rgba.store(rgba, std::memory_order_relaxed);
    slot->sequence.store(
        refii::vfs::g_sequence.fetch_add(1u, std::memory_order_relaxed) + 1u,
        std::memory_order_release);
  }
}


void refii_bridge_note_highlight_target(void) {
  refii::vfs::g_target_calls.fetch_add(1u, std::memory_order_relaxed);
}

void refii_bridge_note_targeted_by(uint32_t thing, uint32_t mask, int32_t interact) {
  using refii::vfs::g_targeted;
  using refii::vfs::g_targeted_count;
  if (thing == 0u) {
    return;
  }
  std::lock_guard<std::mutex> lock(refii::vfs::g_targeted_mutex);
  int32_t at = 0;
  while (at < g_targeted_count && g_targeted[static_cast<std::size_t>(at)].thing != thing) {
    ++at;
  }
  if ((mask & 3u) == 0u) {
    if (at < g_targeted_count) {
      g_targeted[static_cast<std::size_t>(at)] = g_targeted[static_cast<std::size_t>(--g_targeted_count)];
    }
    return;
  }
  if (at == g_targeted_count) {
    if (g_targeted_count == refii::vfs::kTargetedRows) {
      // Full: the oldest row gives way (rows are appended, so it is the first).
      for (int32_t i = 1; i < g_targeted_count; ++i) {
        g_targeted[static_cast<std::size_t>(i - 1)] = g_targeted[static_cast<std::size_t>(i)];
      }
      at = --g_targeted_count;
    }
    ++g_targeted_count;
  }
  g_targeted[static_cast<std::size_t>(at)] = {thing, static_cast<uint8_t>(mask & 3u), interact != 0};
}

int32_t refii_bridge_targeted_by(uint32_t* out_pairs, int32_t capacity_pairs, int32_t interact_only) {
  if (out_pairs == nullptr || capacity_pairs <= 0) {
    return 0;
  }
  std::lock_guard<std::mutex> lock(refii::vfs::g_targeted_mutex);
  int32_t written = 0;
  for (int32_t i = 0; i < refii::vfs::g_targeted_count && written < capacity_pairs; ++i) {
    const refii::vfs::TargetedRow& row = refii::vfs::g_targeted[static_cast<std::size_t>(i)];
    if (interact_only != 0 && !row.interact) {
      continue;
    }
    out_pairs[written * 2] = row.thing;
    out_pairs[written * 2 + 1] = row.mask;
    ++written;
  }
  return written;
}
}  // extern "C"
