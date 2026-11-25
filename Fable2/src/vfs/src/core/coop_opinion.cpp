// refii — A VILLAGER'S OPINION OF EACH HERO (Fable 3 keeps one record per hero, `comp+864`, and
// pushes the visitor's to them, msg 0x1E2). Fable 2's opinion component (CECOpinionReaction, 47)
// holds ONE hero's worth of state: base and current axes (+24, +44: love, fear, attractiveness,
// morality, respect), the deed-modifier lists (+176), the appearance, script and weapon terms and
// the relationship fields. Player two's copy is kept here and swapped into the component whenever
// code runs for hero two (mod_events.cpp OpinionHeroScope: their deeds, the per-frame rebuild,
// speech, topics and the OpinionReaction natives), so the villager feels about each hero
// separately; the host reads it for player two's targeted readout. Here, in refii_vfs, because
// both link it.

#include <array>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <unordered_map>

#include "core/payload_bridge_fable2.h"

namespace {

struct State {
  std::mutex mutex;
  std::unordered_map<uint32_t, std::array<uint8_t, REFII_OPINION_BLOCK_BYTES>> sets;
};

State& Tables() {
  static State state;
  return state;
}

}  // namespace

extern "C" int32_t refii_bridge_opinion_two_load(uint32_t component, uint8_t* out) {
  State& s = Tables();
  std::lock_guard lock(s.mutex);
  const auto found = s.sets.find(component);
  if (found == s.sets.end()) {
    return 0;
  }
  std::memcpy(out, found->second.data(), REFII_OPINION_BLOCK_BYTES);
  return 1;
}

extern "C" void refii_bridge_opinion_two_store(uint32_t component, const uint8_t* in) {
  State& s = Tables();
  std::lock_guard lock(s.mutex);
  std::memcpy(s.sets[component].data(), in, REFII_OPINION_BLOCK_BYTES);
}

// HERO ONE'S SLOT WHILE IT IS LENT. An opinion scope for hero two lends hero one's slot (the
// player record's +108 holder) to hero two for the length of one guest call; our own readers of
// hero one, on any thread, read through here: a read that overlapped a lend (an odd sequence, or
// the sequence moved) answers the holder the lend saved. Lends do not nest (the scope lends once).
namespace {
std::atomic<uint32_t> g_lend_sequence{0};
std::atomic<uint32_t> g_lend_holder{0};
/// The slot is lent by this thread (lends are the guest tick thread's and do not nest).
thread_local bool t_lent_here = false;
}  // namespace

extern "C" void refii_bridge_hero_slot_lend(uint32_t real_holder) {
  g_lend_holder.store(real_holder, std::memory_order_seq_cst);
  g_lend_sequence.fetch_add(1u, std::memory_order_seq_cst);
  t_lent_here = true;
}

extern "C" void refii_bridge_hero_slot_return(void) {
  t_lent_here = false;
  g_lend_sequence.fetch_add(1u, std::memory_order_seq_cst);
}

extern "C" uint32_t refii_bridge_hero_slot_lent_here(void) {
  return t_lent_here ? g_lend_holder.load(std::memory_order_seq_cst) : 0u;
}

extern "C" uint32_t refii_bridge_hero_one_holder(const uint8_t* base, uint32_t players) {
  const uint32_t before = g_lend_sequence.load(std::memory_order_seq_cst);
  uint32_t raw = 0u;
  std::memcpy(&raw, base + players + 108u, sizeof(raw));
  std::atomic_thread_fence(std::memory_order_seq_cst);
  const uint32_t after = g_lend_sequence.load(std::memory_order_seq_cst);
  if ((before & 1u) != 0u || before != after) {
    return g_lend_holder.load(std::memory_order_seq_cst);
  }
  return __builtin_bswap32(raw);
}

extern "C" void refii_bridge_opinion_two_forget(uint32_t component) {
  State& s = Tables();
  std::lock_guard lock(s.mutex);
  s.sets.erase(component);
}
