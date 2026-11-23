// THE SCREEN FADE, STATED. The fader is 48 bytes: progress `+0`, seconds `+4`, the colour it left
// `+8` and the one it goes to `+12` (four bytes each, B, G, R, A), the lock `+33`. Addresses are
// TU1 / disc.
//
//   start    `sub_823CE950(fader, bgra*, f1 seconds)` / `sub_823B3E00` — every fade's one start
//            (FadeScreenOut / OutToColour / In, the level change, the network's, 17 callers):
//            `+8..11` = the colour showing now (the lerp `sub_822C7970`), `+12..15` = the argument
//            (0x823ce9b0 .. 0x823ce9d0), then unless that changes nothing `*(a1 + 4) = seconds` and
//            progress 0, or 1 under 0.0001 s (0x823cea38 .. 0x823cea54).
//   advance  `sub_822C1FC0(fader)` / `sub_822C1FB8`, once per lhGameLoop_Tick (0x822771e8 /
//            0x822770b4), the only other writer of progress: `+= 1 / dbl_83319660 / seconds`
//            clamped to 1 (0x822c21c0), held by the lock (`!*(a1 + 33)` 0x822c1fdc), by a
//            completed lerp, and by gates of its own (the flow command on top with `+44`, a
//            pending headline `+34`) that no call states.
//
// The advance's gates are not stated, so the host runs no clock: each call states the fader as
// it left it (refii_bridge_screen_fade_started with rate 0, then the lock the advance tested).
#include <cstdint>
#include <cstring>
#include <mutex>

#include "vfs_hooks_private.h"
#include "core/guest_state.h"
#include "core/payload_bridge.h"
#include "hooks/ppc_externs.h"

namespace {

constexpr uint32_t kProgress = 0u;
constexpr uint32_t kFrom = 8u;
constexpr uint32_t kTo = 12u;
constexpr uint32_t kLock = 33u;

struct Stated {
    uint32_t fader = 0u;
    uint32_t progress = 0u;
    uint32_t from = 0u;
    uint32_t to = 0u;
    uint32_t locked = 0u;
};

std::mutex g_lock;
Stated g_stated;

void State(uint8_t* base, uint32_t fader, const char* why) {
    if (fader < 0x10000u) {
        return;
    }
    Stated now;
    now.fader = fader;
    now.progress = refii::vfs::LoadGuestU32(base, fader + kProgress);
    std::memcpy(&now.from, base + fader + kFrom, sizeof(now.from));
    std::memcpy(&now.to, base + fader + kTo, sizeof(now.to));
    now.locked = base[fader + kLock] != 0u ? 1u : 0u;
    std::lock_guard<std::mutex> lock(g_lock);
    if (std::memcmp(&now, &g_stated, sizeof(now)) == 0) {
        return;
    }
    const bool report = why != nullptr || now.fader != g_stated.fader || now.locked != g_stated.locked;
    g_stated = now;
    float progress = 0.0f;
    std::memcpy(&progress, &now.progress, sizeof(progress));
    const uint8_t* from = base + fader + kFrom;
    const uint8_t* to = base + fader + kTo;
    refii_bridge_screen_fade_started(fader, from, to, progress, 0.0f);
    refii_bridge_screen_fade_stepped(fader, progress, 0, static_cast<int32_t>(now.locked));
    if (report) {
        REXLOG_INFO("Refii screen fade: {} — fader {:08X} B,G,R,A {},{},{},{} -> {},{},{},{} t {:.2f}{}",
                    why != nullptr ? why : "advance", fader, from[0], from[1], from[2], from[3], to[0], to[1], to[2],
                    to[3], progress, now.locked != 0u ? " locked" : "");
    }
}

}  // namespace

REX_EXTERN(rex_lhScreenFade_Start) {
    REX_FUNC_PROLOGUE();
    const uint32_t fader = ctx.r3.u32;
    __imp__rex_lhScreenFade_Start(ctx, base);
    State(base, fader, "start");
}

REX_EXTERN(rex_lhScreenFade_Advance) {
    REX_FUNC_PROLOGUE();
    const uint32_t fader = ctx.r3.u32;
    __imp__rex_lhScreenFade_Advance(ctx, base);
    State(base, fader, nullptr);
}
