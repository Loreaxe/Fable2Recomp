// refii — the view the shell draws, the camera a title states, and the host's world-build hold
// (core/payload_bridge.h).
//
// Both cross from a title's hooks (the payload) to its host bridge, which live in different
// modules: the hook sees the guest's call, the bridge answers the shell. Neither knows a title.
//
// THE CAMERA STATEMENTS (core/camera_statements.h) are a ring of the last kRing records, numbered
// from 1. The guest's camera calls come from its simulation thread; the shell drains them every
// frame, so the ring holds minutes of a normal camera and only a stalled reader loses any.
//
// PER PLAYER (core/camera_views.h). As each record is stated: a kGuestBlock is the block the guest's
// own getter built for its manager (kept per manager); a kPush ends that manager's update and names its
// player-map key, so the block a player's readers see is the last one its manager's getter built. The
// camera events are read off the same records and taken by each title's event pump at its tick: a cut
// where the block's cut byte rose, a blend where an InterpolateFor with ticks > 0 was stated since the
// previous push, a tick per push; each belongs to the player of the push that ends the update.
//
// THE VIEWS (RefiiHostStateCameraViews) and THE DISPLAY OFFSET (camera_offset) are the shell's and a
// mod's: stated here so a title's natives answer them on the guest thread.

#include <array>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <mutex>

#include "core/camera_statements.h"
#include "core/camera_views.h"
#include "core/payload_bridge.h"

namespace {

std::mutex g_camera_mutex;
std::array<float, 11> g_camera{};
bool g_camera_stated = false;

std::atomic<int32_t> g_world_build_pending{0};

constexpr uint32_t kRing = 1u << 16;
std::mutex g_statement_mutex;
std::array<uint32_t, kRing * refii::camera::kRecordWords> g_statements{};
uint32_t g_next_sequence = 1;
thread_local uint32_t t_camera_depth = 0;

/// One manager's last block as its getter built it (kGuestBlock): position, forward, up, fov, cut.
struct ManagerBlock {
  uint32_t manager = 0;
  uint32_t key = 0;
  bool have = false;
  bool drawn = false;
  bool pending_cut = false;
  std::array<float, 11> block{};
};
std::array<ManagerBlock, 4> g_managers{};
uint32_t g_pending_blend = 0;

constexpr uint32_t kEvents = 64u;
std::array<uint32_t, kEvents * refii::camera::kEventWords> g_events{};
uint32_t g_events_head = 0;   // the oldest
uint32_t g_events_count = 0;

/// Two views drawn, as the shell last stated (word 1 of the views): a second manager's camera is seen.
std::atomic<bool> g_split_stated{false};

std::mutex g_views_mutex;
std::array<float, refii::camera::kViewsWords> g_views{};
bool g_views_stated = false;

std::mutex g_offset_mutex;
std::array<std::array<float, 7>, 2> g_offset{};
std::array<bool, 2> g_offset_on{};

uint32_t Bits(float value) {
  uint32_t bits = 0;
  std::memcpy(&bits, &value, sizeof(bits));
  return bits;
}

float Float(uint32_t bits) {
  float value = 0.0f;
  std::memcpy(&value, &bits, sizeof(value));
  return value;
}

/// The slot holding `manager`, taking a free one (or the oldest-numbered one) for a new manager.
ManagerBlock& SlotFor(uint32_t manager) {
  for (ManagerBlock& slot : g_managers) {
    if (slot.manager == manager) {
      return slot;
    }
  }
  for (ManagerBlock& slot : g_managers) {
    if (slot.manager == 0u) {
      slot = ManagerBlock{};
      slot.manager = manager;
      return slot;
    }
  }
  ManagerBlock& slot = g_managers[manager % g_managers.size()];
  slot = ManagerBlock{};
  slot.manager = manager;
  return slot;
}

void QueueEvent(uint32_t kind, uint32_t player, uint32_t ticks, const ManagerBlock& from) {
  if (g_events_count == kEvents) {
    g_events_head = (g_events_head + 1u) % kEvents;
    --g_events_count;
  }
  uint32_t* slot = &g_events[((g_events_head + g_events_count) % kEvents) * refii::camera::kEventWords];
  ++g_events_count;
  slot[0] = kind;
  slot[1] = player;
  slot[2] = ticks;
  for (uint32_t i = 0; i < 11u; ++i) {
    slot[3u + i] = Bits(from.block[i]);
  }
  slot[14] = 0u;
  slot[15] = 0u;
}

/// Reads a statement for the per-player blocks and the events (under g_statement_mutex).
void TakeStatement(const uint32_t* words) {
  namespace cam = refii::camera;
  const uint32_t call = words[1] & 0xFFu;
  if (call == cam::kDestroy) {
    // The guest destroyed the manager: its block is nobody's camera from here on.
    for (ManagerBlock& slot : g_managers) {
      if (slot.manager != 0u && slot.manager == words[2]) {
        slot = ManagerBlock{};
      }
    }
  } else if (call == cam::kGuestBlock) {
    ManagerBlock& slot = SlotFor(words[2]);
    const float cut = (words[5] & 0xFFu) != 0u ? 1.0f : 0.0f;
    if (cut != 0.0f && (!slot.have || slot.block[10] == 0.0f)) {
      slot.pending_cut = true;
    }
    // The record's values: 0..2 position, 3..5 up, 6..8 forward, 9 fov; kept as position, forward, up.
    for (uint32_t i = 0; i < 3u; ++i) {
      slot.block[i] = Float(words[6u + i]);
      slot.block[3u + i] = Float(words[6u + 6u + i]);
      slot.block[6u + i] = Float(words[6u + 3u + i]);
    }
    slot.block[9] = Float(words[6u + 9u]);
    slot.block[10] = cut;
    slot.have = true;
  } else if (call == cam::kInterpolateFor) {
    if (words[5] > g_pending_blend) {
      g_pending_blend = words[5];
    }
  } else if (call == cam::kPush) {
    ManagerBlock& slot = SlotFor(words[2]);
    slot.key = (words[1] >> 16) & 0xFFu;
    slot.drawn = ((words[1] >> 24) & cam::kFlagDrawn) != 0u;
    // A manager's camera is a player's view when this machine draws it or two views are drawn; local
    // co-op's second manager on a shared screen is nobody's view (both players see player one's).
    if ((slot.key == 1u || slot.key == 2u) && (slot.drawn || g_split_stated.load(std::memory_order_relaxed))) {
      if (slot.pending_cut) {
        QueueEvent(cam::kEventCut, slot.key, 0u, slot);
      }
      if (g_pending_blend != 0u) {
        QueueEvent(cam::kEventBlend, slot.key, g_pending_blend, slot);
      }
      if (slot.have) {
        QueueEvent(cam::kEventTick, slot.key, 0u, slot);
      }
    }
    slot.pending_cut = false;
    g_pending_blend = 0u;
  }
}

}  // namespace

extern "C" void refii_bridge_camera_stated(const float* position, const float* up,
                                           const float* forward, float horizontal_fov,
                                           int32_t snap) {
  if (position == nullptr || up == nullptr || forward == nullptr) {
    return;
  }

  std::lock_guard<std::mutex> lock(g_camera_mutex);
  for (int i = 0; i < 3; ++i) {
    g_camera[i] = position[i];
    g_camera[3 + i] = up[i];
    g_camera[6 + i] = forward[i];
  }

  g_camera[9] = horizontal_fov;
  g_camera[10] = snap != 0 ? 1.0f : 0.0f;
  g_camera_stated = true;
}

extern "C" int32_t refii_bridge_camera_read(float* out) {
  if (out == nullptr) {
    return 0;
  }

  std::lock_guard<std::mutex> lock(g_camera_mutex);
  if (!g_camera_stated) {
    return 0;
  }

  for (size_t i = 0; i < g_camera.size(); ++i) {
    out[i] = g_camera[i];
  }

  return 1;
}

extern "C" void refii_bridge_set_world_build_pending(int32_t pending) {
  g_world_build_pending.store(pending != 0 ? 1 : 0, std::memory_order_release);
}

extern "C" int32_t refii_bridge_world_build_pending(void) {
  return g_world_build_pending.load(std::memory_order_acquire);
}

extern "C" void refii_bridge_camera_statement(const uint32_t* words) {
  if (words == nullptr) {
    return;
  }

  const uint32_t frame = refii_bridge_guest_frame();
  std::lock_guard<std::mutex> lock(g_statement_mutex);
  const uint32_t sequence = g_next_sequence++;
  uint32_t* slot = &g_statements[(sequence % kRing) * refii::camera::kRecordWords];
  for (uint32_t i = 1; i < 22u; ++i) {
    slot[i] = words[i];
  }
  slot[0] = sequence;
  slot[22] = frame;
  slot[23] = 0;
  TakeStatement(slot);
}

extern "C" int32_t refii_bridge_camera_stated_block(int32_t player, float* out11) {
  if (out11 == nullptr) {
    return 0;
  }
  std::lock_guard<std::mutex> lock(g_statement_mutex);
  const ManagerBlock* own = nullptr;
  const ManagerBlock* drawn = nullptr;
  for (const ManagerBlock& slot : g_managers) {
    if (slot.manager == 0u || !slot.have) {
      continue;
    }
    if (player >= 1 && slot.key == static_cast<uint32_t>(player) &&
        (slot.drawn || g_split_stated.load(std::memory_order_relaxed))) {
      own = &slot;
    }
    if (slot.drawn) {
      drawn = &slot;
    }
  }
  const ManagerBlock* from = own != nullptr ? own : drawn;
  if (from == nullptr || (player != 1 && player != 2)) {
    return 0;
  }
  for (uint32_t i = 0; i < 11u; ++i) {
    out11[i] = from->block[i];
  }
  return 1;
}

extern "C" uint32_t refii_bridge_camera_events_take(uint32_t* out_words, uint32_t max_events) {
  if (out_words == nullptr || max_events == 0u) {
    return 0u;
  }
  std::lock_guard<std::mutex> lock(g_statement_mutex);
  uint32_t taken = 0u;
  while (g_events_count > 0u && taken < max_events) {
    const uint32_t* slot = &g_events[g_events_head * refii::camera::kEventWords];
    std::memcpy(out_words + taken * refii::camera::kEventWords, slot, refii::camera::kEventWords * sizeof(uint32_t));
    g_events_head = (g_events_head + 1u) % kEvents;
    --g_events_count;
    ++taken;
  }
  return taken;
}

extern "C" void refii_bridge_camera_views_stated(const float* words, uint32_t count) {
  if (words == nullptr || count < refii::camera::kViewsWords) {
    return;
  }
  std::lock_guard<std::mutex> lock(g_views_mutex);
  std::memcpy(g_views.data(), words, sizeof(g_views));
  g_views_stated = true;
  g_split_stated.store(words[1] != 0.0f, std::memory_order_relaxed);
}

extern "C" int32_t refii_bridge_camera_view_read(int32_t player, float* out_words) {
  if (out_words == nullptr || (player != 1 && player != 2)) {
    return 0;
  }
  std::lock_guard<std::mutex> lock(g_views_mutex);
  const float* view = g_views.data() + 2u + static_cast<uint32_t>(player - 1) * refii::camera::kViewWords;
  if (!g_views_stated || view[16] == 0.0f) {
    return 0;
  }
  std::memcpy(out_words, view, refii::camera::kViewWords * sizeof(float));
  return 1;
}

extern "C" int32_t refii_bridge_camera_screen_read(int32_t* split) {
  std::lock_guard<std::mutex> lock(g_views_mutex);
  if (split != nullptr) {
    *split = g_views_stated && g_views[1] != 0.0f ? 1 : 0;
  }
  return g_views_stated ? static_cast<int32_t>(g_views[0]) : -1;
}

extern "C" void refii_bridge_camera_offset_set(int32_t player, const float* seven, int32_t on) {
  if (player != 1 && player != 2) {
    return;
  }
  std::lock_guard<std::mutex> lock(g_offset_mutex);
  const size_t at = static_cast<size_t>(player - 1);
  g_offset_on[at] = on != 0 && seven != nullptr;
  for (size_t i = 0; i < 7u; ++i) {
    g_offset[at][i] = g_offset_on[at] ? seven[i] : 0.0f;
  }
}

extern "C" int32_t refii_bridge_camera_offset_read(int32_t player, float* out7) {
  if (out7 == nullptr || (player != 1 && player != 2)) {
    return 0;
  }
  std::lock_guard<std::mutex> lock(g_offset_mutex);
  const size_t at = static_cast<size_t>(player - 1);
  for (size_t i = 0; i < 7u; ++i) {
    out7[i] = g_offset[at][i];
  }
  return g_offset_on[at] ? 1 : 0;
}

extern "C" uint32_t refii_bridge_camera_enter(void) { return t_camera_depth++; }

extern "C" void refii_bridge_camera_leave(void) {
  if (t_camera_depth > 0) {
    --t_camera_depth;
  }
}

extern "C" uint32_t refii_bridge_camera_statements_read(uint32_t after, uint32_t* out_words,
                                                        uint32_t max_records) {
  if (out_words == nullptr || max_records == 0) {
    return 0;
  }

  std::lock_guard<std::mutex> lock(g_statement_mutex);
  const uint32_t newest = g_next_sequence - 1;
  if (after >= newest) {
    return 0;
  }

  uint32_t first = after + 1;
  if (newest - after > kRing - 1) {
    first = newest - (kRing - 1) + 1;
  }

  uint32_t written = 0;
  for (uint32_t sequence = first; sequence <= newest && written < max_records; ++sequence) {
    const uint32_t* slot = &g_statements[(sequence % kRing) * refii::camera::kRecordWords];
    for (uint32_t i = 0; i < refii::camera::kRecordWords; ++i) {
      out_words[written * refii::camera::kRecordWords + i] = slot[i];
    }
    ++written;
  }

  return written;
}
