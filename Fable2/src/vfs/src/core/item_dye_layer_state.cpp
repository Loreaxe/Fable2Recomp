// refii — the texture layers the guest's own dye builder states for ONE inventory item, as
// SHARED STATE.
//
// In librefii_vfs for the reason entity_appearance_state.cpp gives: the PAYLOAD writes (the
// GUI-tick sampler in Fable2/src/vfs/src/hooks/guest_tick_pump.cpp, on the guest's thread) and the HOST reads
// (the host bridge, on Godot's).
//
// One record for the dyed inventory item INSTANCE the host's preview names (worn or carried), keyed by the item's guest
// address — the dye is the instance's type-2 record, so two copies of one record hash are dyed
// apart — holding the composites in the same words a character's texture layers use, so the
// menu's preview of a dyed garment goes through the very compositor that dresses the hero.

#include <atomic>
#include <cstdint>
#include <cstring>
#include <mutex>

#include "core/payload_bridge_fable2.h"

namespace refii::vfs {
namespace {

struct ItemLayers {
  uint32_t item = 0u;
  uint32_t generation = 0u;
  uint32_t count = 0u;
  uint32_t words[kItemDyeLayerWords] = {};
};

std::mutex g_lock;
std::atomic<uint32_t> g_wanted{0u};
ItemLayers g_items[kItemDyeLayerItems];
uint32_t g_used = 0u;

ItemLayers* Find(uint32_t item) {
  for (uint32_t i = 0u; i < g_used; ++i) {
    if (g_items[i].item == item) {
      return &g_items[i];
    }
  }
  return nullptr;
}

}  // namespace

/// Copies the item instance's composites into `out` ([original, count | kind << 16, count x
/// kTextureLayerWords] per texture) and returns the words written; 0 for an item the sampler
/// found undyed or never saw. `item` is the guest address the sampler keyed it by.
/// `out_generation` moves when the item's layers changed.
uint32_t StatedItemDyeLayers(uint32_t item_address, uint32_t* out, uint32_t capacity,
                             uint32_t* out_generation) {
  std::lock_guard<std::mutex> guard(g_lock);
  if (out_generation != nullptr) {
    *out_generation = 0u;
  }
  if (item_address == 0u || out == nullptr) {
    return 0u;
  }
  const ItemLayers* item = Find(item_address);
  if (item == nullptr) {
    return 0u;
  }
  if (out_generation != nullptr) {
    *out_generation = item->generation;
  }
  const uint32_t count = item->count < capacity ? item->count : capacity;
  std::memcpy(out, item->words, sizeof(uint32_t) * count);
  return count;
}

}  // namespace refii::vfs

extern "C" {

void refii_bridge_want_item_dye_layers(uint32_t item) { refii::vfs::g_wanted.store(item, std::memory_order_relaxed); }

uint32_t refii_bridge_wanted_item_dye_layers(void) { return refii::vfs::g_wanted.load(std::memory_order_relaxed); }

// The read's whole answer: `items` (guest addresses) with each item's words at
// `offsets[i]..offsets[i] + counts[i]`. Items not in it are dropped (no longer asked, dyed or listed).
void refii_bridge_set_item_dye_layers(const uint32_t* items, const uint32_t* offsets,
                                      const uint32_t* counts, uint32_t item_count,
                                      const uint32_t* words) {
  using namespace refii::vfs;
  std::lock_guard<std::mutex> guard(g_lock);
  if (items == nullptr || offsets == nullptr || counts == nullptr || words == nullptr) {
    item_count = 0u;
  }
  if (item_count > static_cast<uint32_t>(kItemDyeLayerItems)) {
    item_count = static_cast<uint32_t>(kItemDyeLayerItems);
  }

  ItemLayers next[kItemDyeLayerItems];
  uint32_t used = 0u;
  for (uint32_t i = 0u; i < item_count; ++i) {
    const uint32_t key = items[i];
    uint32_t count = counts[i];
    if (key == 0u || count == 0u) {
      continue;
    }
    if (count > static_cast<uint32_t>(kItemDyeLayerWords)) {
      count = static_cast<uint32_t>(kItemDyeLayerWords);
    }
    ItemLayers& item = next[used++];
    item.item = key;
    item.count = count;
    std::memcpy(item.words, words + offsets[i], sizeof(uint32_t) * count);

    // The generation carries over and moves only when the content did, so a reader that keyed
    // its composite on it rebuilds nothing for a sweep that said the same thing again.
    const ItemLayers* previous = Find(key);
    if (previous != nullptr) {
      item.generation = previous->generation;
      if (previous->count != count ||
          std::memcmp(previous->words, item.words, sizeof(uint32_t) * count) != 0) {
        ++item.generation;
      }
    } else {
      item.generation = 1u;
    }
  }

  std::memcpy(g_items, next, sizeof(ItemLayers) * used);
  g_used = used;
}

}  // extern "C"
