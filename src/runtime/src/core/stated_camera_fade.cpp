// refii — player two's camera's fades (core/payload_bridge.h), between a title's Lua natives and the shell.
//
// Each camera fades what stands near it through GraphicAppearance.SetCameraAlpha, which writes one field per
// entity that every view draws. Player two's camera keeps its own book in local co-op and writes no field
// (the core camera chunks: Fable3CoreLua.cs, Fable 2's CoopHeroLua.cs); it states the book whole here at the
// end of each of its fade-ups, and the shell draws it in player two's half. Replaced whole, so an entity that
// died or faded back up leaves the book on the next statement.

#include <atomic>
#include <cstdint>
#include <mutex>
#include <vector>

#include "core/payload_bridge.h"

namespace {

/// More than any camera fades at once (a mode fades what stands within twice its radius).
constexpr uint32_t kMaxEntries = 512u;

std::mutex g_lock;
std::vector<uint32_t> g_entities;
std::vector<float> g_alphas;
std::atomic<uint32_t> g_serial{0u};

}  // namespace

extern "C" void refii_bridge_partner_camera_alphas_set(const uint32_t* entities, const float* alphas,
                                                       uint32_t count) {
    std::lock_guard<std::mutex> lock(g_lock);
    g_entities.clear();
    g_alphas.clear();
    if (entities != nullptr && alphas != nullptr) {
        const uint32_t kept = count < kMaxEntries ? count : kMaxEntries;
        g_entities.assign(entities, entities + kept);
        g_alphas.assign(alphas, alphas + kept);
    }
    g_serial.fetch_add(1u, std::memory_order_relaxed);
}

extern "C" uint32_t refii_bridge_partner_camera_alphas_read(uint32_t* entities, float* alphas, uint32_t capacity,
                                                            uint32_t* serial) {
    std::lock_guard<std::mutex> lock(g_lock);
    if (serial != nullptr) {
        *serial = g_serial.load(std::memory_order_relaxed);
    }
    const uint32_t held = static_cast<uint32_t>(g_entities.size());
    if (entities != nullptr && alphas != nullptr) {
        const uint32_t n = held < capacity ? held : capacity;
        for (uint32_t i = 0; i < n; ++i) {
            entities[i] = g_entities[i];
            alphas[i] = g_alphas[i];
        }
    }
    return held;
}
