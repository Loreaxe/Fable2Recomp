// THE OPTION LEVELS, STATED. Gamma and the audio levels as the guest's own calls carry them, kept
// in the shared stated-value registry (payload_bridge.h, keys 7..12) for the host bridge's
// RefiiHostGetOptionsSliders; nothing is read out of the options screen.
//
//   gamma    the profile's getter `sub_82443680(profiles, user)` (disc `sub_824E6C78`) returns it
//            in f1 (0x824436fc, 0.5 when the profile has none) to the front end's entry, the
//            profile loads and the Video page's load `sub_829C0730`; the setter
//            `sub_82443A08(profiles, user, f1)` (disc `sub_824E7000`) is the Video page's commit
//            `sub_829C07D0` (0x829c0860).
//   sfx      `sub_829FD678(audio, f1)` (disc `sub_829F88B0`): clamps to 0..1 and sets category 0
//            (`li r4,0` 0x829fd6d0).
//   music    `sub_829FD888(audio, f1)` (disc `sub_829F8AC0`): the same for category 1 (0x829fd8e0).
//   speech   `sub_829FD710(audio, f1)` (disc `sub_829F8948`): clamps to 0..1 into audio+88
//            (0x829fd764).
//   speaker  `sub_829FD920(audio, mode)` (disc `sub_829F8B58`): 0 TV, 1 hi-fi, 2 surround.
//
// The four audio calls are made together by the front end's entry, the profile loads and the
// Audio page's apply `sub_829C15E8` (disc `sub_828DAA78`), which hands them the page's three
// slider values and its speaker index on every left/right.
#include <algorithm>
#include <atomic>
#include <cstdint>
#include <cstring>

#include "vfs_hooks_private.h"
#include "core/payload_bridge.h"
#include "hooks/ppc_externs.h"

namespace {

// Stated-value keys (payload_bridge.h).
constexpr uint32_t kGamma = 7u;
constexpr uint32_t kSfx = 8u;
constexpr uint32_t kMusic = 9u;
constexpr uint32_t kSpeech = 10u;
constexpr uint32_t kSpeaker = 11u;
constexpr uint32_t kStated = 12u;

std::atomic<uint32_t> g_stated{0u};

void State(uint32_t key, uint32_t value) {
    refii_bridge_stated_value(key, value);
    refii_bridge_stated_value(kStated, g_stated.fetch_or(1u << (key - kGamma)) | (1u << (key - kGamma)));
}

void StateLevel(uint32_t key, double level) {
    const float value = std::clamp(static_cast<float>(level), 0.0f, 1.0f);
    uint32_t bits = 0u;
    std::memcpy(&bits, &value, sizeof(bits));
    State(key, bits);
}

}  // namespace

REX_EXTERN(rex_lhProfile_GetGamma) {
    REX_FUNC_PROLOGUE();
    __imp__rex_lhProfile_GetGamma(ctx, base);
    StateLevel(kGamma, ctx.f1.f64);
}

REX_EXTERN(rex_lhProfile_SetGamma) {
    REX_FUNC_PROLOGUE();
    const double gamma = ctx.f1.f64;
    __imp__rex_lhProfile_SetGamma(ctx, base);
    StateLevel(kGamma, gamma);
}

REX_EXTERN(rex_lhAudio_SetSfxVolume) {
    REX_FUNC_PROLOGUE();
    const double level = ctx.f1.f64;
    __imp__rex_lhAudio_SetSfxVolume(ctx, base);
    StateLevel(kSfx, level);
}

REX_EXTERN(rex_lhAudio_SetMusicVolume) {
    REX_FUNC_PROLOGUE();
    const double level = ctx.f1.f64;
    __imp__rex_lhAudio_SetMusicVolume(ctx, base);
    StateLevel(kMusic, level);
}

REX_EXTERN(rex_lhAudio_SetSpeechVolume) {
    REX_FUNC_PROLOGUE();
    const double level = ctx.f1.f64;
    __imp__rex_lhAudio_SetSpeechVolume(ctx, base);
    StateLevel(kSpeech, level);
}

REX_EXTERN(rex_lhAudio_SetSpeakerMode) {
    REX_FUNC_PROLOGUE();
    const uint32_t mode = ctx.r4.u32;
    __imp__rex_lhAudio_SetSpeakerMode(ctx, base);
    State(kSpeaker, mode);
}
