// refii - Fable 3's world, as the guest states it: the level standing, the level change in
// flight, the scenario, whether the world is ready, the camera, and the hold that keeps the guest
// in its loading screen while Godot builds; and, from the table its hooks state, the pause gate,
// the culture and the loading screen.
//
// Every offset is from the Fable 3 IDB, each against its own base:
//
//   Game   = the game of the world on screen, *0x83597030 (presented_world.h)
//   World  = *(*(*(*(Game+0x1C)+4)+0x10)+4)   (state 9 of the level change, disc 0x82722D88..0x82756F08)
//   Level  = *(*(World+0x28)+4)          World+0x28 null means no level (IsAnyLevelLoaded,
//                                         `lwz r11,0x28(r3)` at 0x82351D10)
//   World+0x14  world name   Level+0xAC  level name (0x8245C454)   Level+0xB0  entrance (0x8245C4FC)
//   Level+0xB4  key, FNV-1 of the world+level path (`stw r28,0xB4(r31)` at 0x8245C518)
//   Game+0xB5   world ready: cleared as the blocking load starts (disc 0x82722848), set as the level
//               change finishes (0x8275712C)
//   Game+0xBA   the game is initialised (disc 0x82345810)
//
// Names are LHString Reps (+0 characters, +4 length), as in Fable 2.
//
// THE FRONT END IS A WORLD. Fable 3's Sanctuary is a level (fable3 / pvp_gui\gui), standing in
// the GUI-world component (vtable 0x82096D7C) as the main game's levels stand in its own
// (0x82001A84). So "in game", to the shell, is "a world is standing" in either.

#include <cmath>
#include <cstdint>
#include <cstring>

#include "core/payload_bridge.h"
#include "host_abi.h"
#include "host_core.h"
#include "presented_world.h"
#include "../../vfs/include/fable3_guest_build.h"

namespace {

using refii::hostcore::GuestByteAt;
using refii::hostcore::GuestWordAt;
using refii::hostcore::SafeKernelMemory;

const refii::guest::Addr kLevelChangeCommand{0x820AEA18u};
const refii::guest::Addr kDefaultScenario{0x83597DBCu};
const refii::guest::Addr kTickRate{0x832DF4D0u};
const refii::guest::Addr kGuiMode{0x82093488u};
constexpr uint32_t kNoScreen = 31u;

bool Plausible(uint32_t address) { return address >= 0x10000u && address < 0xC0000000u; }

const uint8_t* GuestBase() {
    rex::memory::Memory* memory = SafeKernelMemory();
    return memory == nullptr ? nullptr : memory->virtual_membase();
}

uint32_t Word(const uint8_t* base, uint32_t address) {
    return Plausible(address) ? GuestWordAt(base, address) : 0u;
}

/// The Game of the world on screen (presented_world.h), else 0.
uint32_t WorldGame(const uint8_t* base) { return refii::fable3::PresentedGame(base); }

uint32_t WorldOf(const uint8_t* base, uint32_t game) {
    const uint32_t container = Word(base, Word(base, game + 0x1Cu) + 4u);
    const uint32_t world = Word(base, Word(base, container + 0x10u) + 4u);
    return Plausible(world) ? world : 0u;
}

uint32_t LevelOf(const uint8_t* base, uint32_t world) {
    const uint32_t holder = Word(base, world + 0x28u);
    const uint32_t level = Plausible(holder) ? Word(base, holder + 4u) : 0u;
    return Plausible(level) ? level : 0u;
}

/// One LHString field (it holds a Rep pointer) into a NUL-terminated buffer.
bool ReadName(const uint8_t* base, uint32_t field, char* out, uint32_t capacity) {
    if (out == nullptr || capacity == 0u) {
        return false;
    }

    out[0] = '\0';
    const uint32_t rep = Word(base, field);
    const uint32_t chars = Word(base, rep);
    const uint32_t length = Word(base, rep + 4u);
    if (!Plausible(chars) || length == 0u || length >= capacity) {
        return false;
    }

    for (uint32_t i = 0; i < length; ++i) {
        const char c = static_cast<char>(GuestByteAt(base, chars + i));
        if (c < 0x20 || c > 0x7E) {
            out[0] = '\0';
            return false;
        }

        out[i] = c;
    }

    out[length] = '\0';
    return true;
}

/// The level-change command at the back of the gameflow list, the one the game tick runs:
/// gf = *(*(Game+0x30)+4), back = *(*(gf+0)+4), command = *(back+8). 0 when another kind of
/// command is running or none is.
uint32_t LevelChange(const uint8_t* base, uint32_t game) {
    const uint32_t flow = Word(base, Word(base, game + 0x30u) + 4u);
    const uint32_t back = Word(base, Word(base, flow) + 4u);
    const uint32_t command = Word(base, back + 8u);
    return Word(base, command) == kLevelChangeCommand ? command : 0u;
}

}  // namespace

extern "C" {

/// A world is standing: the current component is a world (the game's, or the GUI world the front
/// end runs in), its game is initialised and it holds a level.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostIsInGame(void) {
    const uint8_t* base = GuestBase();
    if (base == nullptr) {
        return 0u;
    }

    const uint32_t game = WorldGame(base);
    if (game == 0u || GuestByteAt(base, game + 0xBAu) == 0u) {
        return 0u;
    }

    const uint32_t world = WorldOf(base, game);
    return world != 0u && LevelOf(base, world) != 0u ? 1u : 0u;
}

/// Game+0xB5. Use its rising edge: it falls only once the blocking load has torn the old level
/// down, so the level change's own state is what frees a scene.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostWorldIsReady(void) {
    const uint8_t* base = GuestBase();
    const uint32_t game = base == nullptr ? 0u : WorldGame(base);
    return game != 0u && GuestByteAt(base, game + 0xB5u) != 0u ? 1u : 0u;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetCurrentLevel(char* world_name, char* level_name, char* entrance,
                                                        uint32_t capacity, uint32_t* out_key) {
    if (out_key != nullptr) *out_key = 0u;
    if (capacity == 0u) return 0u;
    if (world_name != nullptr) world_name[0] = '\0';
    if (level_name != nullptr) level_name[0] = '\0';
    if (entrance != nullptr) entrance[0] = '\0';

    const uint8_t* base = GuestBase();
    const uint32_t game = base == nullptr ? 0u : WorldGame(base);
    const uint32_t world = game == 0u ? 0u : WorldOf(base, game);
    const uint32_t level = world == 0u ? 0u : LevelOf(base, world);
    if (level == 0u) {
        return 0u;
    }

    ReadName(base, world + 0x14u, world_name, capacity);
    ReadName(base, level + 0xACu, level_name, capacity);
    ReadName(base, level + 0xB0u, entrance, capacity);
    if (out_key != nullptr) *out_key = GuestWordAt(base, level + 0xB4u);
    return 1u;
}

/// The level change in flight (command vtable 0x820AEA18, ctor sub_82755D10): world +0x14,
/// level +0x18, entrance +0x1C, travel type +0x24, state +0x2C, full load +0x3A (decided at
/// 0x82756090; 0 is a reposition inside the resident level, which tears nothing down).
///
/// THE STATE IS STATED IN THE SHELL'S NUMBERING, Fable 2's: 2 is the teardown, 3 the first state
/// with the new level built, 5 its entities placed. Fable 3's blocking load (which tears the old
/// level down, then builds the new) is its state 4 (0x82756998), so its states are reported two
/// lower: 4 -> 2, 5 -> 3, 7 -> 5.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetLevelChange(char* world_name, char* level_name, char* entrance,
                                                       uint32_t capacity, uint32_t* out_state,
                                                       uint32_t* out_travel_type, uint32_t* out_full_load) {
    if (out_state != nullptr) *out_state = 0u;
    if (out_travel_type != nullptr) *out_travel_type = 0u;
    if (out_full_load != nullptr) *out_full_load = 0u;
    if (capacity == 0u) return 0u;
    if (world_name != nullptr) world_name[0] = '\0';
    if (level_name != nullptr) level_name[0] = '\0';
    if (entrance != nullptr) entrance[0] = '\0';

    const uint8_t* base = GuestBase();
    const uint32_t game = base == nullptr ? 0u : WorldGame(base);
    const uint32_t command = game == 0u ? 0u : LevelChange(base, game);
    if (command == 0u) {
        return 0u;
    }

    ReadName(base, command + 0x14u, world_name, capacity);
    ReadName(base, command + 0x18u, level_name, capacity);
    ReadName(base, command + 0x1Cu, entrance, capacity);
    const uint32_t state = GuestWordAt(base, command + 0x2Cu);
    if (out_state != nullptr) *out_state = state > 2u ? state - 2u : 0u;
    if (out_travel_type != nullptr) *out_travel_type = GuestWordAt(base, command + 0x24u);
    if (out_full_load != nullptr) *out_full_load = GuestByteAt(base, command + 0x3Au) != 0u ? 1u : 0u;
    return 1u;
}

/// The current level's scenario: the world's {u32 key, LHString} map at World+0x7C..+0x80, keyed
/// by Level+0xB4 (sub_823529B8, 8-byte entries, the name at +4), else the guest's default
/// "DefaultScenario" (0x83597DBC). Scanned, not searched: a save restore appends unsorted.
REFII_HOST_ABI_EXPORT int RefiiHostGetActiveScenario(char* scenario, uint32_t capacity) {
    if (scenario == nullptr || capacity == 0u) return 0;
    scenario[0] = '\0';

    const uint8_t* base = GuestBase();
    const uint32_t game = base == nullptr ? 0u : WorldGame(base);
    const uint32_t world = game == 0u ? 0u : WorldOf(base, game);
    const uint32_t level = world == 0u ? 0u : LevelOf(base, world);
    if (level == 0u) {
        return 0;
    }

    const uint32_t key = GuestWordAt(base, level + 0xB4u);
    const uint32_t first = Word(base, world + 0x7Cu);
    const uint32_t last = Word(base, world + 0x80u);
    if (Plausible(first) && last >= first && last - first <= 8u * 4096u) {
        for (uint32_t entry = first; entry < last; entry += 8u) {
            if (GuestWordAt(base, entry) == key) {
                return ReadName(base, entry + 4u, scenario, capacity) ? 1 : 0;
            }
        }
    }

    return ReadName(base, kDefaultScenario, scenario, capacity) ? 1 : 0;
}

/// The scenario a new game starts in: the start globals the new-game path writes
/// (0x83597948 world, 0x8359794C level, 0x83597950 scenario, 0x83597954 start point).
REFII_HOST_ABI_EXPORT int RefiiHostGetStartScenario(char* scenario, uint32_t capacity) {
    const uint8_t* base = GuestBase();
    return base != nullptr && ReadName(base, refii::guest::Address(0x83597950u), scenario, capacity) ? 1 : 0;
}

/// The world's game tick and its clock. The tick is what GetWorldFrame states (sub_82209028):
/// `*(*(*(Game+0x1C)+4)+0x100)` while Game+0xBA is set, else 0 — incremented once per world update
/// (0x821C5390, disc 0x826ED31C; both paths of the world component's slot 6, vtable 0x820AC6DC). The
/// rate is GetTickRate's (sub_82240908: `dbl_832DF4D0`, 15.0), so the clock is tick / rate. The
/// main loop sub_821CB7B0 runs two HF steps (`dbl_832DF4D8`, 30.0) per tick, the ratio the shell's
/// lead cap assumes (GameSession: half a game tick per granted step). 0 when no world is standing,
/// outputs untouched.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetSimClock(double* out_seconds, uint32_t* out_tick) {
    const uint8_t* base = GuestBase();
    const uint32_t game = base == nullptr ? 0u : WorldGame(base);
    if (game == 0u || GuestByteAt(base, game + 0xBAu) == 0u) {
        return 0u;
    }

    const uint32_t tick = Word(base, Word(base, Word(base, game + 0x1Cu) + 4u) + 0x100u);
    if (tick == 0u) {
        return 0u;
    }

    uint64_t rate_bits = (static_cast<uint64_t>(GuestWordAt(base, kTickRate)) << 32) |
                         GuestWordAt(base, kTickRate + 4u);
    double rate = 0.0;
    std::memcpy(&rate, &rate_bits, sizeof(rate));
    if (out_tick != nullptr) {
        *out_tick = tick;
    }
    if (out_seconds != nullptr) {
        *out_seconds = rate > 0.0 ? static_cast<double>(tick) / rate : 0.0;
    }

    return 1u;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetSimTick(void) {
    uint32_t tick = 0u;
    return RefiiHostGetSimClock(nullptr, &tick) != 0u ? tick : 0u;
}

/// The presented world's container B = `*(*(Game+0x1C)+4)`: the world update sub_821CD4C8(B)
/// advances the time manager `*(B+0x20)` (0x821CD60C) and evaluates the theme manager `*(B+0x24)`
/// (`lwz r3,0x24(r27)` at 0x821CD610). One of each per world, so the two front-end worlds stay apart.
uint32_t WorldContainer(const uint8_t* base) {
    const uint32_t game = WorldGame(base);
    return game == 0u ? 0u : Word(base, Word(base, game + 0x1Cu) + 4u);
}

/// The world's time of day: the time manager (sub_821AB918 advances it) holds the day fraction at
/// +4, the day count at +8 and its stop count at +0x24 (time stands while it is non-zero). Fable 3
/// has no moon-phase field; the moon is theme parameters. Returns 1 when read.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetTimeOfDay(float* out_fraction, int32_t* out_day, int32_t* out_frozen,
                                                    int32_t* out_moon_phase) {
    if (out_moon_phase != nullptr) {
        *out_moon_phase = -1;
    }

    const uint8_t* base = GuestBase();
    const uint32_t timing = base == nullptr ? 0u : Word(base, WorldContainer(base) + 0x20u);
    if (!Plausible(timing)) {
        return 0u;
    }

    const uint32_t raw = GuestWordAt(base, timing + 4u);
    float fraction = 0.0f;
    std::memcpy(&fraction, &raw, sizeof(fraction));
    if (!(fraction >= 0.0f) || !(fraction < 1000.0f)) {
        return 0u;
    }

    fraction -= std::floor(fraction);
    if (out_fraction != nullptr) *out_fraction = fraction;
    if (out_day != nullptr) *out_day = static_cast<int32_t>(GuestWordAt(base, timing + 8u));
    if (out_frozen != nullptr) *out_frozen = GuestWordAt(base, timing + 0x24u) != 0u ? 1 : 0;
    return 1u;
}

/// The world's weather, as its theme stack evaluated it: the stack is `*(M+8)` while byte M+12 is
/// set, else `*(M+4)` (M = the theme manager); its evaluated block starts at +32, where the
/// descriptor table sub_82C757C0 places Snow Size, Snow Fall Speed, Rain Size and Rain Density at
/// +0xD4..+0xE0 (`li r5,0xD4` at 0x82C75C6C; Lua GetRainDensity reads stack+0x100). Returns 1 when read.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetWeather(float* out_snow_size, float* out_snow_fall_speed,
                                                  float* out_rain_size, float* out_rain_density) {
    const uint8_t* base = GuestBase();
    const uint32_t themes = base == nullptr ? 0u : Word(base, WorldContainer(base) + 0x24u);
    if (!Plausible(themes)) {
        return 0u;
    }

    const uint32_t stack = Word(base, themes + (GuestByteAt(base, themes + 12u) != 0u ? 8u : 4u));
    if (!Plausible(stack)) {
        return 0u;
    }

    float values[4] = {0.0f, 0.0f, 0.0f, 0.0f};
    for (uint32_t i = 0; i < 4u; ++i) {
        const uint32_t raw = GuestWordAt(base, stack + 32u + 0xD4u + (i * 4u));
        std::memcpy(&values[i], &raw, sizeof(float));
        if (!(values[i] >= 0.0f) || !(values[i] <= 100.0f)) {
            return 0u;
        }
    }

    if (out_snow_size != nullptr) *out_snow_size = values[0];
    if (out_snow_fall_speed != nullptr) *out_snow_fall_speed = values[1];
    if (out_rain_size != nullptr) *out_rain_size = values[2];
    if (out_rain_density != nullptr) *out_rain_density = values[3];
    return 1u;
}

/// The pause gate. The presented game runs only the TOP of its mode stack S = `*(*(game+0x30)+4)`
/// (the main loop, 0x821CBF78..disc 0x821CBEA4), and while the GUI's pause mode is on top (vtable
/// 0x82093488, id 1: `li r3,1` at 0x82A99990; pushed by lhGuiManager_EnterGuiMode at 0x825F9ADC)
/// its update ticks only the GUI and input — the world is frozen. Both are stated
/// (Fable3/src/hooks/src/game_state_hooks.cpp): the top by the stack's push, pop and run, the
/// screen id by each writer of the GUI's screen manager. S is the key the presented game's stack
/// was stated under. The screen id is the GUI's own while that mode is on top (31, "no screen",
/// answered as 0 there), and 31 otherwise. Fable 2's modal and tab fields have no Fable 3
/// counterpart.
REFII_HOST_ABI_EXPORT int RefiiHostGetPauseState(uint32_t* out_screen_id, uint32_t* out_modal_id,
                                                uint32_t* out_modal_mode, uint32_t* out_current_tab) {
    if (out_screen_id != nullptr) *out_screen_id = 0xFFFFFFFFu;
    if (out_modal_id != nullptr) *out_modal_id = 0u;
    if (out_modal_mode != nullptr) *out_modal_mode = 1u;
    if (out_current_tab != nullptr) *out_current_tab = 0u;

    const uint8_t* base = GuestBase();
    const uint32_t game = base == nullptr ? 0u : WorldGame(base);
    if (game == 0u) {
        return 0;
    }

    const uint32_t stack = Word(base, Word(base, game + 0x30u) + 4u);
    uint32_t vtable = 0u;
    uint32_t depth = 0u;
    if (refii_bridge_mode_stack_read(stack, nullptr, &vtable, &depth) == 0 || depth == 0u) {
        return 0;
    }

    uint32_t screen_id = kNoScreen;
    if (vtable == kGuiMode) {
        uint32_t stated = kNoScreen;
        refii_bridge_gui_screen_read(&stated);
        screen_id = stated == kNoScreen ? 0u : stated;
    }

    if (out_screen_id != nullptr) *out_screen_id = screen_id;
    return 1;
}

/// The culture the guest runs in ("en-uk", ...): the code of the culture table's row in use, as its
/// builds and its switch stated it (Fable3/src/hooks/src/game_state_hooks.cpp: the boot build
/// sub_822E8E88, the rebuild sub_822E8948, the switch sub_822E8C30). Returns the code's length, 0
/// before the table is stated or when no row is in use.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetCurrentCulture(char* out, uint32_t capacity) {
    return refii_bridge_culture_code_read(out, capacity);
}

/// Holds the guest in its own loading screen while Godot builds: its hook on the 3D Engine's
/// preload gate answers "still preloading" while this is set. Clear it when the build lands,
/// failure included.
REFII_HOST_ABI_EXPORT void RefiiHostSetWorldBuildPending(uint32_t pending) {
    refii_bridge_set_world_build_pending(pending != 0u ? 1 : 0);
}

}  // extern "C"
