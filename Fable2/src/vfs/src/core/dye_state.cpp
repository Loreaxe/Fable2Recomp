// refii — which of the hero's garments are dyed, as SHARED STATE.
//
// In librefii_vfs for the reason entity_appearance_state.cpp gives: the PAYLOAD writes (the
// GUI-tick sampler in Fable2/src/vfs/src/hooks/guest_tick_pump.cpp, on the guest's thread) and the HOST reads
// (the host bridge, on Godot's).
//
// One entry per item INSTANCE the hero wears or carries that answered the guest's own
// `sub_826CAFA8(_, itemRef, slot)` — the test its dye screen's footer makes to choose between
// "apply" and "scrub" (`sub_829B5D68`). kDyeStateWords words each {item, record, flags}, see
// core/payload_bridge_fable2.h.

#include <cstdint>
#include <cstring>
#include <mutex>

#include "core/payload_bridge_fable2.h"

namespace refii::vfs {
namespace {

std::mutex g_lock;
uint32_t g_words[kDyeStateItems * kDyeStateWords] = {};
uint32_t g_count = 0u;

}  // namespace

/// Copies the sampled entries into `out` and returns how many there are.
uint32_t SampledDyeStates(uint32_t* out, uint32_t word_capacity) {
  std::lock_guard<std::mutex> guard(g_lock);
  if (out == nullptr) {
    return 0u;
  }
  const uint32_t fits = word_capacity / kDyeStateWords;
  const uint32_t count = g_count < fits ? g_count : fits;
  std::memcpy(out, g_words, sizeof(uint32_t) * count * kDyeStateWords);
  return count;
}

}  // namespace refii::vfs

extern "C" {

void refii_bridge_note_dye_states(const uint32_t* words, uint32_t count) {
  using namespace refii::vfs;
  if (words == nullptr || count > static_cast<uint32_t>(kDyeStateItems)) {
    count = words == nullptr ? 0u : static_cast<uint32_t>(kDyeStateItems);
  }
  bool changed = false;
  {
    std::lock_guard<std::mutex> guard(g_lock);
    const size_t bytes = sizeof(uint32_t) * count * kDyeStateWords;
    changed = count != g_count || (count != 0u && std::memcmp(g_words, words, bytes) != 0);
    g_count = count;
    if (count != 0u) {
      std::memcpy(g_words, words, bytes);
    }
  }
  // The dye lists are built from this snapshot: a changed one moves the UI data generation,
  // which is what the host's open list re-reads on.
  if (changed) {
    refii_bridge_ui_data_published();
  }
}

}  // extern "C"
