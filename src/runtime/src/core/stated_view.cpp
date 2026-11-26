// refii — the view the shell draws, the camera a title states, and the host's world-build hold
// (core/payload_bridge.h).
//
// Both cross from a title's hooks (the payload) to its host bridge, which live in different
// modules: the hook sees the guest's call, the bridge answers the shell. Neither knows a title.
//
// THE CAMERA STATEMENTS (core/camera_statements.h) are a ring of the last kRing records, numbered
// from 1. The guest's camera calls come from its simulation thread; the shell drains them every
// frame, so the ring holds minutes of a normal camera and only a stalled reader loses any.

#include <array>
#include <atomic>
#include <cstdint>
#include <mutex>

#include "core/camera_statements.h"
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
