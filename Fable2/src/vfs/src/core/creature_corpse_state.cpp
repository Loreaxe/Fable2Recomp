// refii — which dead creatures the guest leaves LYING, as SHARED STATE.
//
// In librefii_vfs, not the PPC hook set, for the reason target_highlight_state.cpp gives: the
// PAYLOAD writes (the CECCreature hooks: FinishDeath in Fable2/src/vfs/src/hooks/mod_events.cpp,
// the constructor in Fable2/src/vfs/src/hooks/creature_corpse.cpp, on the guest's thread) and the
// HOST reads (the entity walk in the host bridge, on Godot's).
//
// One entry per CECCreature (component 20) on which `CECCreature_FinishDeath` (TU1 0x823A4290,
// disc 0x82432848) ran since its constructor (TU1 0x823A3148, disc 0x82431700). FinishDeath is
// what leaves the body: the death switch (TU1 `sub_823A6C58`, disc `sub_824351C0`) calls it for
// DeathType 2 and the Die action's end (`CAction_Die_vf18`) for 3; every other type ends in a
// destroy. Its other callers (knockdown, stabbed on ground, the vortex, Lua, the message switch
// `sub_82392250`) leave a body the same way.
//
// The guest keeps few corpses at once (the detritus culler caps them at 48), so the table is
// small; an entry whose creature was freed without a new one built at its address stays until
// the window it hashes to is full, and then the oldest entry in that window goes.

#include <atomic>
#include <cstdint>

#include "core/payload_bridge_fable2.h"

namespace refii::vfs {
namespace {

constexpr uint32_t kEmpty = 0u;
constexpr uint32_t kGone = 1u;  // a reconstructed creature; probes continue past it

struct LeftCorpse {
  std::atomic<uint32_t> creature{kEmpty};
  std::atomic<uint32_t> sequence{0};
};

constexpr uint32_t kSlots = 1024u;
constexpr uint32_t kProbes = 16u;

LeftCorpse g_corpses[kSlots];
std::atomic<uint32_t> g_sequence{0};

uint32_t SlotFor(uint32_t creature) {
  const uint32_t h = creature * 2654435761u;
  return (h >> 12) & (kSlots - 1u);
}

LeftCorpse* Find(uint32_t creature) {
  const uint32_t start = SlotFor(creature);
  for (uint32_t probe = 0u; probe < kProbes; ++probe) {
    LeftCorpse& slot = g_corpses[(start + probe) & (kSlots - 1u)];
    const uint32_t held = slot.creature.load(std::memory_order_acquire);
    if (held == creature) {
      return &slot;
    }

    if (held == kEmpty) {
      return nullptr;
    }
  }

  return nullptr;
}

/// A free slot in the creature's window, or the window's oldest entry.
LeftCorpse* Claim(uint32_t creature) {
  if (LeftCorpse* found = Find(creature); found != nullptr) {
    return found;
  }

  const uint32_t start = SlotFor(creature);
  LeftCorpse* oldest = nullptr;
  uint32_t oldest_age = 0u;
  const uint32_t now = g_sequence.load(std::memory_order_relaxed);
  for (uint32_t probe = 0u; probe < kProbes; ++probe) {
    LeftCorpse& slot = g_corpses[(start + probe) & (kSlots - 1u)];
    uint32_t held = slot.creature.load(std::memory_order_relaxed);
    if ((held == kEmpty || held == kGone) &&
        slot.creature.compare_exchange_strong(held, creature, std::memory_order_acq_rel)) {
      return &slot;
    }

    const uint32_t age = now - slot.sequence.load(std::memory_order_relaxed);
    if (oldest == nullptr || age > oldest_age) {
      oldest = &slot;
      oldest_age = age;
    }
  }

  // Every slot of the window holds a creature: the one noted longest ago is the likeliest to be
  // long freed. Only the guest's thread writes, so the store cannot race another claim.
  if (oldest != nullptr) {
    oldest->creature.store(creature, std::memory_order_release);
  }
  return oldest;
}
}  // namespace

/// True when FinishDeath ran on this CECCreature since it was constructed.
bool CreatureLeftCorpse(uint32_t creature) {
  return creature > kGone && Find(creature) != nullptr;
}
}  // namespace refii::vfs

// --- The payload's side. C only; see core/payload_bridge.h. --------------------------------

extern "C" {
void refii_bridge_note_creature_corpse(uint32_t creature) {
  if (creature <= refii::vfs::kGone) {
    return;
  }

  if (refii::vfs::LeftCorpse* slot = refii::vfs::Claim(creature); slot != nullptr) {
    slot->sequence.store(refii::vfs::g_sequence.fetch_add(1u, std::memory_order_relaxed) + 1u,
                         std::memory_order_release);
  }
}

void refii_bridge_forget_creature_corpse(uint32_t creature) {
  if (creature <= refii::vfs::kGone) {
    return;
  }

  if (refii::vfs::LeftCorpse* slot = refii::vfs::Find(creature); slot != nullptr) {
    slot->creature.store(refii::vfs::kGone, std::memory_order_release);
  }
}
}  // extern "C"
